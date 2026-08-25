using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Support;
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
/// (OpenAI, OpenRouter, NVIDIA, Ollama, LM Studio, etc.).
/// </summary>
public sealed class OpenAILlmService : ILlmService, IDisposable
{
    public string Name => "openai";

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();
    private readonly ILogger<OpenAILlmService> _logger;

    private static readonly HashSet<int> TransientStatusCodes = [429, 500, 502, 503, 504];

    public OpenAILlmService(ILogger<OpenAILlmService> logger)
    {
        _logger = logger;
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

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", request.ApiKey ?? "");

        // Headers de identificación para OpenRouter y similares.
        // Sin pipeline policy, sin AsyncLocal, sin re-serialización del body.
        client.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/AITaskFramework");
        client.DefaultRequestHeaders.Add("X-Title", "AITaskFramework");

        _clients.TryAdd(cacheKey, client);
        return client;
    }

    private static string GetEndpoint(LlmRequest request)
    {
        var baseUrl = request.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        return $"{baseUrl}/chat/completions";
    }

    private static JObject BuildRequestBody(LlmRequest request, bool stream = false)
    {
        var messages = new JArray();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new JObject { ["role"] = "system", ["content"] = request.SystemPrompt });

        foreach (var msg in request.Conversation.History.Messages)
            messages.Add(msg.ToJObject());

        var body = new JObject
        {
            ["model"]    = request.Model,
            ["messages"] = messages
        };

        if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
        if (request.MaxTokens.HasValue)   body["max_tokens"]  = request.MaxTokens.Value;
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
        if (request.Options != null)
            foreach (var prop in request.Options.Properties())
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

    public async Task<LlmResult> InvokeAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
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
            _logger.LogError(ex, "Failed to parse API response JSON");
            return LlmResult.Failure(ex, isRetriable: false);
        }

        var choicesRaw = root["choices"] as JArray ?? [];
        var usageRaw   = root["usage"];
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
    private LlmResponse ProcessChoice(JToken choiceToken, NormalizationContext normCtx)
    {
        var index     = choiceToken["index"]?.Value<int>() ?? 0;
        var message   = choiceToken["message"];
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
        var usageTokens = chunk["usage"]?["total_tokens"]?.Value<int?>();
        if (usageTokens.HasValue) totalTokens = usageTokens.Value;
        var usagePrompt = chunk["usage"]?["prompt_tokens"]?.Value<int?>();
        if (usagePrompt.HasValue) promptTokens = usagePrompt.Value;
        var usageCompletion = chunk["usage"]?["completion_tokens"]?.Value<int?>();
        if (usageCompletion.HasValue) completionTokens = usageCompletion.Value;

        if (chunk["choices"] is not JArray choices || choices.Count == 0)
            continue;

        foreach (var choice in choices)
        {
            var choiceIndex = choice["index"]?.Value<int>() ?? 0;
            var delta       = choice["delta"];
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
                    TokensUsed       = totalTokens ?? chunk["usage"]?["total_tokens"]?.Value<int?>(),
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