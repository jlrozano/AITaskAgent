using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Support;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAILLmService.Normalization;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace OpenAILLmService;

/// <summary>
/// OpenAI-compatible implementation of ILlmService using direct HTTP calls.
/// Supports any provider that implements the OpenAI chat completions protocol
/// (OpenAI, OpenRouter, NVIDIA, Ollama, LM Studio, etc.) directly, and is the base for provider
/// subclasses whose protocol diverges just enough to need it - see
/// <see cref="AzureOpenAILlmService"/> (different endpoint shape and auth header) and
/// <see cref="OpenRouterLlmService"/> (attribution headers). A provider only earns a subclass when
/// its particularities are its own concept, never meaningful for another vendor served by this same
/// class (Azure's deployment-based URL, OpenRouter's ranking headers) - particularities tied to the
/// *model* rather than the vendor (a reasoning model's `&lt;think&gt;` tag, an XML tool-call dialect)
/// belong in <see cref="OpenAiPluginRegistry"/> instead, since the same model can sit behind any of
/// these vendors and the two kinds of quirk must be free to combine.
/// </summary>
public class OpenAILlmService : ILlmService, IDisposable
{
    public virtual string Name => "openai";

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();
    private readonly ILogger _logger;

    private static readonly HashSet<int> TransientStatusCodes = [429, 500, 502, 503, 504];

    private readonly int _retryMaxAttempts;
    private readonly TimeSpan _retryInitialDelay;
    private readonly TimeSpan _retryMaxDelay;

    /// <param name="logger">Logger instance.</param>
    /// <param name="retryPolicy">
    /// Retry settings for transient failures. Only the attempt count and delays are used: whether
    /// a given failure is transient is decided by <see cref="LlmResult.IsRetriable"/>.
    /// </param>
    public OpenAILlmService(ILogger<OpenAILlmService> logger, RetryPolicy? retryPolicy = null)
        : this((ILogger)logger, retryPolicy)
    {
    }

    /// <summary>
    /// For a subclass whose own constructor requests <c>ILogger&lt;TSubclass&gt;</c> from DI and
    /// forwards it here - the plain <see cref="ILogger"/> parameter (rather than
    /// <c>ILogger&lt;OpenAILlmService&gt;</c>) is what lets that subclass's log lines carry its own
    /// category instead of this base type's, without any generics on this class.
    /// </summary>
    protected OpenAILlmService(ILogger logger, RetryPolicy? retryPolicy = null)
    {
        _logger = logger;

        var policy = retryPolicy ?? new RetryPolicy
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(60)
        };

        _retryMaxAttempts = Math.Max(1, policy.MaxAttempts);
        _retryInitialDelay = policy.InitialDelay;
        _retryMaxDelay = policy.MaxDelay;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("OpenAILlmService initialized.");
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }

    // -------------------------------------------------------------------------
    // Infrastructure
    // -------------------------------------------------------------------------

    private static bool IsRetriable(int statusCode) =>
        TransientStatusCodes.Contains(statusCode);

    /// <summary>How much of an unexpected response body reaches the log.</summary>
    private const int RawBodyLogLimit = 4000;

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength] + $"... [truncated, {value.Length} chars total]";

    private HttpClient GetOrCreateClient(LlmRequest request)
    {
        var cacheKey = $"{request.BaseUrl}|{request.ApiKey}";
        if (_clients.TryGetValue(cacheKey, out var cached))
            return cached;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Creating new HTTP client for provider: {Provider}, baseUrl: {BaseUrl}",
                Name, request.BaseUrl);
            _logger.LogInformation(
                "ApiKey prefix: {Prefix}",
                request.ApiKey?[..Math.Min(4, request.ApiKey?.Length ?? 0)]);
        }

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        ConfigureAuthentication(client, request);
        ConfigureExtraHeaders(client, request);

        _clients.TryAdd(cacheKey, client);
        return client;
    }

    /// <summary>
    /// Sets the client's auth header. The OpenAI chat-completions protocol authenticates with a
    /// bearer token; a vendor whose auth scheme differs (Azure OpenAI uses an <c>api-key</c> header
    /// instead) overrides this rather than the whole client-creation method.
    /// </summary>
    protected internal virtual void ConfigureAuthentication(HttpClient client, LlmRequest request)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", request.ApiKey ?? "");
    }

    /// <summary>
    /// Hook for headers specific to one vendor's own concept (e.g. OpenRouter's attribution
    /// headers) rather than the shared chat-completions protocol. No-op by default: sending a
    /// vendor's headers to every other vendor - which is what this used to do unconditionally - is
    /// harmless to OpenRouter itself but leaks an unrelated vendor's identity into every request.
    /// </summary>
    protected internal virtual void ConfigureExtraHeaders(HttpClient client, LlmRequest request)
    {
    }

    /// <summary>
    /// Framework facade properties (see LlmInferenceSettings) that share the Options JObject with
    /// vendor-specific passthrough keys. They are already handled explicitly above, or don't map
    /// to anything in the OpenAI protocol, so they must not be forwarded as raw body fields.
    /// </summary>
    private static readonly HashSet<string> FrameworkFacadeOptionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temperature", "TopP", "MaxTokens", "UseStreaming", "JsonSupport", "ToolSupport", "Stop",
        "UseMaxCompletionTokens"
    };

    /// <summary>
    /// Builds the chat-completions URL. Azure OpenAI's shape diverges enough (deployment in the
    /// path, mandatory <c>api-version</c> query string) that <see cref="AzureOpenAILlmService"/>
    /// overrides this rather than branching on vendor here.
    /// </summary>
    protected internal virtual string GetEndpoint(LlmRequest request)
    {
        var baseUrl = request.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        return $"{baseUrl}/chat/completions";
    }

    internal static JObject BuildRequestBody(LlmRequest request, bool stream = false)
    {
        var messages = new JArray();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new JObject { ["role"] = "system", ["content"] = request.SystemPrompt });

        // Same window the Gemini service applies and the step's trace log shows: sending the raw
        // history ignored UseSlidingWindow entirely, so long tool loops grew the prompt without bound.
        var conversationMessages = request.Conversation.GetMessagesForRequest(
            maxTokens: request.SlidingWindowMaxTokens,
            useSlidingWindow: request.UseSlidingWindow);

        foreach (var msg in conversationMessages)
            messages.Add(msg.ToJObject());

        var body = new JObject
        {
            ["model"]    = request.Model,
            ["messages"] = messages
        };

        if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
        if (request.MaxTokens.HasValue)
            body[request.UseMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = request.MaxTokens.Value;
        if (request.TopP.HasValue)        body["top_p"]       = request.TopP.Value;
        if (request.User != null)         body["user"]        = request.User;

        if (request.Stop?.Count > 0)
            body["stop"] = new JArray(request.Stop);

        if (request.ResponseFormat?.Type == ResponseFormatType.JsonObject)
        {
            body["response_format"] = request.ResponseFormat.JsonSchema == null
                ? new JObject { ["type"] = "json_object" }
                : new JObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JObject
                    {
                        ["name"]   = "custom_schema",
                        ["strict"] = true,
                        ["schema"] = JObject.Parse(request.ResponseFormat.JsonSchema.ToJson())
                    }
                };
        }

        if (request.Tools?.Count > 0)
            body["tools"] = new JArray(request.Tools.Select(t => t.ToJObject()));

        // Custom parameters: sin AsyncLocal, sin pipeline policy, sin re-serialización.
        // Cualquier campo vendor-specific (thinking, provider, etc.) va aquí directamente.
        // Las claves de fachada del framework (Temperature, JsonSupport, ...) viven en el mismo
        // JObject que las opciones de proveedor (LlmInferenceSettings) pero ya se traducen arriba
        // a sus campos OpenAI correspondientes (o no aplican al protocolo), así que se excluyen
        // aquí para no reenviarlas en crudo - algunos endpoints (OpenRouter, Gemini compat) las
        // rechazan con 400 al no reconocerlas.
        if (request.Options != null)
            foreach (var prop in request.Options.Properties())
                if (!FrameworkFacadeOptionKeys.Contains(prop.Name))
                    body[prop.Name] = prop.Value.DeepClone();

        if (stream)
        {
            body["stream"] = true;
            // Los endpoints OpenAI-compat (incl. Ollama) solo incluyen el bloque `usage`
            // en streaming si se solicita explícitamente. Sin esto, total_tokens nunca
            // llega y TokensUsed queda a 0.
            body["stream_options"] = new JObject { ["include_usage"] = true };
        }

        return body;
    }

    // -------------------------------------------------------------------------
    // InvokeAsync
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends the request, retrying transient failures with exponential backoff.
    /// </summary>
    /// <remarks>
    /// The retry lives here because this service converts transport failures into a failed
    /// <see cref="LlmResult"/> instead of letting them surface as exceptions, so an
    /// exception-driven policy would never see them.
    /// </remarks>
    public async Task<LlmResult> InvokeAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var delay = _retryInitialDelay;
        LlmResult result;

        for (var attempt = 1; ; attempt++)
        {
            result = await InvokeOnceAsync(request, cancellationToken);

            if (result.IsSuccess || !result.IsRetriable || attempt >= _retryMaxAttempts)
            {
                if (!result.IsSuccess && result.IsRetriable && attempt >= _retryMaxAttempts)
                {
                    _logger.LogError(
                        "Giving up after {Attempts} attempts for model {Model}.",
                        attempt, request.Model);
                }

                return result;
            }

            _logger.LogWarning(
                "Attempt {Attempt}/{MaxAttempts} failed for model {Model}, retrying in {Delay}ms. Reason: {Reason}",
                attempt, _retryMaxAttempts, request.Model, delay.TotalMilliseconds,
                result.Error?.Message);

            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromMilliseconds(
                Math.Min(delay.TotalMilliseconds * 2, _retryMaxDelay.TotalMilliseconds));
        }
    }

    private async Task<LlmResult> InvokeOnceAsync(
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        var client   = GetOrCreateClient(request);
        var endpoint = GetEndpoint(request);
        var body     = BuildRequestBody(request);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Invoking {Model} ({Provider}) to {Endpoint} with body: {Body}",
                request.Model, Name, endpoint, body.ToString(Formatting.Indented));

        string rawJson;
        try
        {
            var httpContent = new StringContent(
                body.ToString(Formatting.None), Encoding.UTF8, "application/json");

            var httpResponse = await client.PostAsync(endpoint, httpContent, cancellationToken);
            rawJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var statusCode = (int)httpResponse.StatusCode;
                var ex = new HttpRequestException($"HTTP {statusCode}: {rawJson}");
                _logger.LogError(ex, "API error. Retriable: {Retriable}", IsRetriable(statusCode));
                return LlmResult.Failure(ex, IsRetriable(statusCode));
            }
        }
        catch (Exception ex) when (ex is not TaskCanceledException)
        {
            _logger.LogError(ex, "HTTP request failed");
            return LlmResult.Failure(ex, isRetriable: true);
        }

        JObject root;
        try
        {
            root = JObject.Parse(rawJson);
        }
        catch (Exception ex)
        {
            // A 2xx status does not guarantee a JSON body: rate-limited free-tier models and
            // gateway hiccups can return an HTML page here, and without the body there is no way
            // to tell those apart from a genuine malformed response after the fact.
            _logger.LogError(
                ex,
                "Failed to parse API response JSON for model {Model}. Raw body: {Body}",
                request.Model, Truncate(rawJson, RawBodyLogLimit));
            return LlmResult.Failure(ex, isRetriable: false);
        }

        // A 200 with no usable choices is not a success: providers signal upstream failures and
        // free-tier limits this way, with the reason only present in the body. Returning success
        // here would discard the one piece of evidence about what went wrong.
        if (root["choices"] is not JArray { Count: > 0 })
        {
            var ex = new HttpRequestException(
                $"Response contained no choices. Body: {Truncate(rawJson, RawBodyLogLimit)}");
            _logger.LogError(
                "Response contained no choices for model {Model}. Raw body: {Body}",
                request.Model, Truncate(rawJson, RawBodyLogLimit));
            return LlmResult.Failure(ex, isRetriable: true);
        }

        var choicesRaw = (JArray)root["choices"]!;
        // "as JObject", not a plain indexer: a provider that sends an explicit `"usage": null`
        // (common - some only populate usage on the last streaming chunk, or omit it entirely
        // in error/content-filtered responses) hands back a non-null JValue wrapping JSON null,
        // not a C# null. Indexing into that JValue with usageRaw?["total_tokens"] would then throw
        // "Cannot access child value on Newtonsoft.Json.Linq.JValue" instead of the `?.` short-
        // circuiting - the cast turns anything that isn't really an object into a real C# null.
        var usageRaw   = root["usage"] as JObject;
        var model      = root["model"]?.Value<string>() ?? request.Model;

        var normCtx    = new NormalizationContext(Name, request.Tools?.Select(t => t.Name).ToList());
        var candidates = choicesRaw
            .Select(choice => ProcessChoice(choice, normCtx))
            .ToList();

        return LlmResult.Success(
            candidates,
            tokensUsed:       usageRaw?["total_tokens"]?.Value<int?>(),
            promptTokens:     usageRaw?["prompt_tokens"]?.Value<int?>(),
            completionTokens: usageRaw?["completion_tokens"]?.Value<int?>(),
            model:            model,
            nativeResponse:   root);
    }

    /// <summary>
    /// Procesa una choice del array choices del JSON de respuesta.
    /// Una sola fuente de datos: el raw JSON. Sin mezcla con tipos del SDK.
    /// </summary>
    internal LlmResponse ProcessChoice(JToken choiceToken, NormalizationContext normCtx)
    {
        var index     = choiceToken["index"]?.Value<int>() ?? 0;
        // "as JObject": see the comment on usageRaw above - a choice with an explicit
        // `"message": null` (a content-filtered choice, some error responses) must not crash
        // when message?["content"] indexes into it.
        var message   = choiceToken["message"] as JObject;
        var finishStr = choiceToken["finish_reason"]?.Value<string>() ?? string.Empty;

        var content      = message?["content"]?.Value<string>() ?? string.Empty;
        var reasoning    = (message?["reasoning_content"] ?? message?["reasoning"])?.Value<string>();
        var toolCallsRaw = message?["tool_calls"] as JArray;

        (var finishReason, var rawFinishReason) = FinishReasonExtensions.Parse(finishStr);

        var response = new LlmResponse
        {
            Index     = index,
            Content   = content,
            Reasoning = reasoning,
            ToolCalls = toolCallsRaw?.Select(t => t.ToFrameworkToolCall()).ToList()
        };

        // Phase 1: extracción de reasoning (<think> tags, etc.)
        response = OpenAiPluginRegistry.ApplyExtractors(response);

        // Phase 2: normalización (XML tool calls, formatos, etc.)
        response = OpenAiPluginRegistry.ApplyNormalizers(response, normCtx);

        // FinishReason solo se asigna una vez, aquí, sin copias intermedias
        return new LlmResponse
        {
            Index           = response.Index,
            Content         = response.Content,
            Reasoning       = response.Reasoning,
            ToolCalls       = response.ToolCalls,
            FinishReason    = finishReason,
            RawFinishReason = rawFinishReason
        };
    }

    // -------------------------------------------------------------------------
    // InvokeStreamingAsync
    // -------------------------------------------------------------------------

   public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
    LlmRequest request,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    var client   = GetOrCreateClient(request);
    var endpoint = GetEndpoint(request);
    var body     = BuildRequestBody(request, stream: true);

    var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
    {
        Content = new StringContent(
            body.ToString(Formatting.None), Encoding.UTF8, "application/json")
    };

    HttpResponseMessage httpResponse;
    try
    {
        httpResponse = await client.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
    catch (Exception ex) when (ex is not TaskCanceledException)
    {
        _logger.LogError(ex, "Streaming HTTP request failed");
        throw;
    }

    if (!httpResponse.IsSuccessStatusCode)
    {
        var errBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("Streaming API error {Status}: {Body}",
            (int)httpResponse.StatusCode, errBody);
        throw new HttpRequestException(
            $"Provider returned HTTP {(int)httpResponse.StatusCode}: {errBody}",
            null,
            httpResponse.StatusCode);
    }

    // Un contexto de extracción por choiceIndex — fix bug de contexto compartido
    var extractorContexts = new Dictionary<int, TagExtractionContext>();

    TagExtractionContext GetExtractorCtx(int idx)
    {
        if (!extractorContexts.TryGetValue(idx, out var ctx))
            extractorContexts[idx] = ctx = new TagExtractionContext();
        return ctx;
    }

    // Acumulador de reasoning nativo por choice (para el total si se necesita)
    var nativeReasoningBuffers = new Dictionary<int, StringBuilder>();

    StringBuilder GetNativeBuffer(int idx)
    {
        if (!nativeReasoningBuffers.TryGetValue(idx, out var sb))
            nativeReasoningBuffers[idx] = sb = new StringBuilder();
        return sb;
    }

    // Buffer para tool calls embebidas en content (JSON-in-content, p.ej. Nemotron)
    var embeddedToolCallBuffers = new Dictionary<int, StringBuilder>();

    bool hasYieldedText      = false;
    bool hasYieldedToolCalls = false;

    // Acumulador de tokens del stream. El bloque `usage` puede llegar en el chunk del
    // finish_reason o, en proveedores como Ollama, en un chunk separado y final con
    // `choices: []` (que se descartaría más abajo). Por eso se lee antes del `continue`.
    int? totalTokens = null;
    // Reparto prompt/completion del bloque `usage`. Imprescindible para detectar truncado
    // de contexto: si el prompt llega al tope de ventana (p. ej. 32768 en Ollama),
    // prompt_tokens se clava en el cap y completion_tokens queda en ~0.
    int? promptTokens = null;
    int? completionTokens = null;
    // True una vez que se emite un chunk con el contador de tokens, para no re-emitirlo
    // si el bloque `usage` llega en el mismo chunk que el finish_reason.
    bool tokensReported = false;

    using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
    using var reader = new StreamReader(stream, Encoding.UTF8);

    while (!cancellationToken.IsCancellationRequested)
    {
        string? line;
        try
        {
            line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
        }
        catch (Exception ex) when (ex is not TaskCanceledException)
        {
            _logger.LogError(ex, "Error reading SSE stream");
            break;
        }

        // SSE parsing robusto: acepta "data:" con o sin espacio tras los dos puntos
        if (string.IsNullOrEmpty(line) || !line.StartsWith("data:"))
            continue;

        var data = line["data:".Length..].TrimStart();
        if (data == "[DONE]") break;

        JObject chunk;
        try { chunk = JObject.Parse(data); }
        catch { continue; }

        // El bloque `usage` puede venir en cualquier chunk (incluido el final con
        // choices vacío). Leerlo aquí, antes de descartar chunks sin choices.
        // "as JObject": with stream_options.include_usage (set below in BuildRequestBody), OpenAI
        // sends an explicit `"usage": null` on every chunk except the last - indexing into that
        // JValue directly throws "Cannot access child value on Newtonsoft.Json.Linq.JValue".
        var usage = chunk["usage"] as JObject;
        var usageTokens = usage?["total_tokens"]?.Value<int?>();
        if (usageTokens.HasValue) totalTokens = usageTokens.Value;
        var usagePrompt = usage?["prompt_tokens"]?.Value<int?>();
        if (usagePrompt.HasValue) promptTokens = usagePrompt.Value;
        var usageCompletion = usage?["completion_tokens"]?.Value<int?>();
        if (usageCompletion.HasValue) completionTokens = usageCompletion.Value;

        if (chunk["choices"] is not JArray choices || choices.Count == 0)
            continue;

        foreach (var choice in choices)
        {
            var choiceIndex = choice["index"]?.Value<int>() ?? 0;
            // "as JObject": the final delta of a choice (once finish_reason is set) commonly
            // arrives as `"delta": {}` but some providers send `"delta": null` instead - same
            // JValue-indexing crash as usage/message above.
            var delta       = choice["delta"] as JObject;
            var extractCtx  = GetExtractorCtx(choiceIndex);

            // ── Reasoning nativo (DeepSeek, etc.) ────────────────────────────
            // Se emite directamente. Si hay reasoning nativo, el extractor
            // de tags se bypasea en el content de este mismo chunk para
            // evitar duplicados.
            var reasoningDelta  = (delta?["reasoning_content"] ?? delta?["reasoning"])
                ?.Value<string>();
            var hasNativeReasoning = !string.IsNullOrEmpty(reasoningDelta);

            if (hasNativeReasoning)
            {
                GetNativeBuffer(choiceIndex).Append(reasoningDelta);
                yield return new LlmStreamChunk
                {
                    ChoiceIndex = choiceIndex,
                    Delta       = reasoningDelta!,
                    IsThinking  = true
                };
            }

            // ── Content con extracción de <think> ────────────────────────────
            var textDelta = delta?["content"]?.Value<string>();
            if (!string.IsNullOrEmpty(textDelta))
            {
                if (hasNativeReasoning)
                {
                    // El proveedor ya separó el reasoning — emitir content limpio
                    hasYieldedText    = true;
                    yield return new LlmStreamChunk
                    {
                        ChoiceIndex = choiceIndex,
                        Delta       = textDelta,
                        IsThinking  = false
                    };
                }
                else
                {
                    // Extraer <think> del content
                    var result = OpenAiPluginRegistry.ProcessStreamChunk(textDelta, extractCtx);

                    if (result.HasReasoning)
                    {
                        yield return new LlmStreamChunk
                        {
                            ChoiceIndex = choiceIndex,
                            Delta       = result.Reasoning,
                            IsThinking  = true
                        };
                    }

                    if (result.HasContent)
                    {
                        // Si aún no hemos emitido texto y el contenido parece un JSON de
                        // tool call (Nemotron, Llama, etc.), lo bufferizamos en lugar de
                        // emitirlo — se resolverá al recibir finish_reason (RF-111).
                        if (!hasYieldedText && LooksLikeEmbeddedToolCall(result.Content))
                        {
                            if (!embeddedToolCallBuffers.TryGetValue(choiceIndex, out var tcBuf))
                                embeddedToolCallBuffers[choiceIndex] = tcBuf = new StringBuilder();
                            tcBuf.Append(result.Content);
                        }
                        else if (embeddedToolCallBuffers.ContainsKey(choiceIndex))
                        {
                            // Ya estamos en modo buffer para este choice — seguimos acumulando
                            embeddedToolCallBuffers[choiceIndex].Append(result.Content);
                        }
                        else
                        {
                            hasYieldedText = true;
                            yield return new LlmStreamChunk
                            {
                                ChoiceIndex = choiceIndex,
                                Delta       = result.Content,
                                IsThinking  = false
                            };
                        }
                    }
                }
            }

            // ── Tool call deltas ──────────────────────────────────────────────
            if (delta?["tool_calls"] is JArray toolUpdates && toolUpdates.Count > 0)
            {
                hasYieldedToolCalls = true;
                yield return new LlmStreamChunk
                {
                    ChoiceIndex     = choiceIndex,
                    Delta           = string.Empty,
                    ToolCallUpdates = [.. toolUpdates.Select(t => t.ToFrameworkToolCallUpdate())]
                };
            }

            // ── Finish ────────────────────────────────────────────────────────
            var finishStr = choice["finish_reason"]?.Value<string>();
            if (!string.IsNullOrEmpty(finishStr))
            {
                // Flush del PendingBuffer antes de cerrar — fix pérdida de contenido final
                var flushed = OpenAiPluginRegistry.FlushStream(extractCtx);

                if (flushed.HasReasoning)
                {
                    yield return new LlmStreamChunk
                    {
                        ChoiceIndex = choiceIndex,
                        Delta       = flushed.Reasoning,
                        IsThinking  = true
                    };
                }

                if (flushed.HasContent)
                {
                    if (embeddedToolCallBuffers.ContainsKey(choiceIndex))
                        embeddedToolCallBuffers[choiceIndex].Append(flushed.Content);
                    else
                    {
                        hasYieldedText = true;
                        yield return new LlmStreamChunk
                        {
                            ChoiceIndex = choiceIndex,
                            Delta       = flushed.Content,
                            IsThinking  = false
                        };
                    }
                }

                // ── Drain buffer de tool calls embebidas en content (RF-111) ─────────
                if (embeddedToolCallBuffers.TryGetValue(choiceIndex, out var embeddedBuf)
                    && embeddedBuf.Length > 0)
                {
                    var bufferedContent = embeddedBuf.ToString();
                    embeddedToolCallBuffers.Remove(choiceIndex);

                    var availableNames  = request.Tools?.Select(t => t.Name).ToHashSet() ?? [];
                    var embeddedCalls   = ToolCallXmlParser.ParseToolCalls(bufferedContent, availableNames);

                    if (embeddedCalls.Count > 0)
                    {
                        hasYieldedToolCalls = true;
                        yield return new LlmStreamChunk
                        {
                            ChoiceIndex     = choiceIndex,
                            Delta           = string.Empty,
                            ToolCallUpdates = [.. embeddedCalls.Select((tc, i) => new ToolCallUpdate
                            {
                                Index                   = i,
                                ToolCallId              = tc.Id,
                                FunctionName            = tc.Name,
                                FunctionArgumentsUpdate = tc.Arguments
                            })]
                        };
                    }
                    else
                    {
                        // No son tool calls — emitir como texto (fallback)
                        hasYieldedText = true;
                        yield return new LlmStreamChunk
                        {
                            ChoiceIndex = choiceIndex,
                            Delta       = bufferedContent,
                            IsThinking  = false
                        };
                    }
                }

                (var finishReason, var rawFinishReason) = ParseFinishReasonSafe(finishStr);
                yield return new LlmStreamChunk
                {
                    ChoiceIndex     = choiceIndex,
                    Delta           = string.Empty,
                    IsComplete      = true,
                    FinishReason    = finishReason,
                    RawFinishReason = rawFinishReason,
                    // Usa el acumulado del stream. Puede venir en este mismo chunk
                    // (algunos proveedores) o haberse leído en un chunk previo.
                    TokensUsed       = totalTokens ?? usage?["total_tokens"]?.Value<int?>(),
                    PromptTokens     = promptTokens,
                    CompletionTokens = completionTokens
                };
                if (totalTokens.HasValue) tokensReported = true;
            }
        }
    }

    // Ollama (y otros) envían el bloque `usage` en un chunk final con `choices: []`,
    // posterior al chunk del finish_reason. En ese caso los tokens no se reportaron
    // todavía: se emite un chunk de cierre que solo transporta el contador.
    if (totalTokens.HasValue && !tokensReported)
    {
        yield return new LlmStreamChunk
        {
            ChoiceIndex      = 0,
            Delta            = string.Empty,
            IsComplete       = true,
            TokensUsed       = totalTokens.Value,
            PromptTokens     = promptTokens,
            CompletionTokens = completionTokens
        };
    }

    // Detector de truncado de contexto: si el modelo no generó texto ni tool calls pero sí
    // consumió prompt y la salida fue ~0, lo más probable es que el prompt llenara la ventana
    // de contexto (p. ej. el cap de 32768 de Ollama por /v1) sin dejar hueco para generar.
    // Front-truncation de Ollama, además, descarta el system prompt y los mensajes antiguos.
    if (!hasYieldedText && !hasYieldedToolCalls)
    {
        if ((completionTokens ?? 0) <= 1 && (promptTokens ?? 0) > 0 && _logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning(
                "Empty completion for model {Model}: prompt_tokens={Prompt}, completion_tokens={Completion}, total={Total}. " +
                "Posible TRUNCADO DE CONTEXTO: el prompt llenó la ventana (cap del proveedor) sin dejar espacio de generación; " +
                "el system prompt y los mensajes antiguos pueden haberse descartado.",
                request.Model, promptTokens, completionTokens, totalTokens);
        else if (_logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning(
                "Streaming completed without yielding any text content for model {Model} (prompt_tokens={Prompt}, completion_tokens={Completion})",
                request.Model, promptTokens, completionTokens);
    }
}    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Heurística: el contenido comienza con un patrón que sugiere un JSON de tool call
    /// embebido en content (no en tool_calls). Se usa para decidir si bufferiazar (RF-111).
    /// </summary>
    private static bool LooksLikeEmbeddedToolCall(string content)
    {
        var t = content.TrimStart();
        return t.Length > 0 && (
            t[0] == '{' ||
            t[0] == '[' ||
            t.StartsWith("<tool_call", StringComparison.OrdinalIgnoreCase));
    }

    private (FinishReason Reason, string? Raw) ParseFinishReasonSafe(string rawFinish)
    {
        try
        {
            return FinishReasonExtensions.Parse(rawFinish);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "Unknown finish_reason: '{Reason}'. Exception: {Message}",
                    rawFinish, ex.Message);

            return (FinishReason.Other, rawFinish);
        }
    }

    public int EstimateTokenCount(string text) => (int)Math.Ceiling(text.Length / 4.0);

    public int GetMaxContextTokens(string? model = null) => 8_192;
}