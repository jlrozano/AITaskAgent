using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Support;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace AITaskAgent.Ollama;

/// <summary>
/// Native Ollama implementation of <see cref="ILlmService"/>: <c>POST {host}/api/chat</c>, not the OpenAI-compatible
/// <c>/v1</c> endpoint. The reason it exists is <c>options.num_ctx</c>: <c>/v1</c> cannot set the context window per
/// request, so a model whose Modelfile says <c>num_ctx 8192</c> silently truncates a 22,000-token prompt and answers
/// nothing. Here the value travels in every request (see <see cref="NumCtxKey"/>).
/// </summary>
/// <remarks>
/// Request mapping: system prompt and conversation become <c>messages</c> (tool call arguments as a JSON object,
/// <c>tool_name</c> on tool messages); <c>MaxTokens</c> becomes <c>options.num_predict</c>; <c>think</c>,
/// <c>keep_alive</c> and <c>format</c> from the settings' Options go to the body root and every other free option goes
/// to <c>options</c>. Ollama numbers no tool calls, so ids are synthesized by the client.
/// </remarks>
public class OllamaLlmService : ILlmService, IDisposable
{
    public virtual string Name => "ollama";

    /// <summary>Option (provider's or role's <c>Inference.Options</c>) with the context window sent as <c>options.num_ctx</c>.</summary>
    public const string NumCtxKey = "num_ctx";

    /// <summary>Alias of <see cref="NumCtxKey"/>; <see cref="NumCtxKey"/> wins when both are present.</summary>
    public const string ContextTokensKey = "ContextTokens";

    /// <summary>Host used when the request carries no BaseUrl.</summary>
    public const string DefaultHost = "http://localhost:11434";

    /// <summary>
    /// <see cref="LlmStreamChunk.RawFinishReason"/> of a stream that ended before Ollama's final <c>done</c> chunk; it
    /// comes with <see cref="FinishReason.Other"/> so the answer is not taken for a normal stop.
    /// </summary>
    public const string StreamClosedWithoutDone = "stream_closed_without_done";

    /// <summary>Per-attempt limit of a non-streaming call when no setting overrides it.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Longest silence of a stream when no setting overrides it. Longer than the OpenAI service's: a local model can
    /// take minutes to load and to evaluate a 20,000-token prompt before the first byte.
    /// </summary>
    public static readonly TimeSpan DefaultStreamingInactivityTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Largest limit <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts, in seconds.</summary>
    private const int MaxTimeoutSeconds = (int)((uint.MaxValue - 1L) / 1000);

    private static readonly HashSet<int> TransientStatusCodes = [429, 500, 502, 503, 504];

    /// <summary>Options that live at the body root instead of in <c>options</c>.</summary>
    private static readonly string[] RootOptionKeys = ["think", "keep_alive", "format"];

    /// <summary>
    /// Framework facade keys: they share the Options object with the provider passthrough keys but are translated
    /// explicitly (or mean nothing to Ollama), so they are never forwarded raw.
    /// </summary>
    internal static readonly HashSet<string> FrameworkFacadeOptionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temperature", "TopP", "MaxTokens", "UseStreaming", "JsonSupport", "ToolSupport", "Stop",
        "UseMaxCompletionTokens", "RequestTimeoutSeconds", "StreamingInactivityTimeoutSeconds",
        "MaxConcurrentRequests", NumCtxKey, ContextTokensKey
    };

    private const int RawBodyLogLimit = 4000;

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();
    private readonly ConcurrentDictionary<string, int> _contextByModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;
    private readonly int _retryMaxAttempts;
    private readonly TimeSpan _retryInitialDelay;
    private readonly TimeSpan _retryMaxDelay;

    public OllamaLlmService(ILogger<OllamaLlmService> logger, RetryPolicy? retryPolicy = null)
        : this((ILogger)logger, retryPolicy)
    {
    }

    protected OllamaLlmService(ILogger logger, RetryPolicy? retryPolicy = null)
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
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }

    // -------------------------------------------------------------------------
    // Addressing, transport
    // -------------------------------------------------------------------------

    /// <summary>
    /// The server of a base URL: accepts <c>http://host:11434</c> and the OpenAI-compatible <c>http://host:11434/v1</c>
    /// (also <c>/api</c>) and returns it without trailing slash, <c>/v1</c> or <c>/api</c>.
    /// </summary>
    public static string NormalizeHost(string? baseUrl)
    {
        var e = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultHost : baseUrl).Trim().TrimEnd('/');
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suffix in new[] { "/v1", "/api" })
            {
                if (e.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    e = e[..^suffix.Length].TrimEnd('/');
                    changed = true;
                }
            }
        }

        return e;
    }

    internal static string GetEndpoint(LlmRequest request) => NormalizeHost(request.BaseUrl) + "/api/chat";

    /// <summary>The handler under each cached HttpClient; a test or a proxy can replace it.</summary>
    protected internal virtual HttpMessageHandler CreateHttpMessageHandler() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };

    private HttpClient GetOrCreateClient(LlmRequest request)
    {
        var cacheKey = $"{NormalizeHost(request.BaseUrl)}|{request.ApiKey}";
        if (_clients.TryGetValue(cacheKey, out var cached))
            return cached;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Creating new HTTP client for provider: {Provider}, baseUrl: {BaseUrl}", Name, request.BaseUrl);
            // Only whether a key is set and its length: no part of it belongs in a log.
            _logger.LogInformation("ApiKey: {State}",
                string.IsNullOrEmpty(request.ApiKey) ? "not set" : $"set (length {request.ApiKey.Length})");
        }

        // Limits are per request, not client-wide: they depend on the role's settings.
        var client = new HttpClient(CreateHttpMessageHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        if (_clients.TryAdd(cacheKey, client))
            return client;
        client.Dispose(); // another call created it first
        return _clients[cacheKey];
    }

    private HttpRequestMessage CreateHttpRequest(LlmRequest request, JObject body, bool stream)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, GetEndpoint(request))
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "application/x-ndjson" : "application/json"));
        if (!string.IsNullOrEmpty(request.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        return message;
    }

    internal static bool IsRetriable(int statusCode) => TransientStatusCodes.Contains(statusCode);

    internal static TimeSpan? RequestTimeoutOf(LlmRequest request) => ToLimit(request.RequestTimeoutSeconds, DefaultRequestTimeout);

    internal static TimeSpan? StreamingInactivityTimeoutOf(LlmRequest request) =>
        ToLimit(request.StreamingInactivityTimeoutSeconds, DefaultStreamingInactivityTimeout);

    private static TimeSpan? ToLimit(int? seconds, TimeSpan fallback) => seconds switch
    {
        null => fallback,
        <= 0 => null,
        > MaxTimeoutSeconds => null,
        { } s => TimeSpan.FromSeconds(s)
    };

    private static string FormatSeconds(TimeSpan limit) =>
        limit.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + $"... [truncated, {value.Length} chars total]";

    // -------------------------------------------------------------------------
    // Request body
    // -------------------------------------------------------------------------

    /// <summary>The context window configured for the request (<see cref="NumCtxKey"/>, else <see cref="ContextTokensKey"/>).</summary>
    internal static int? NumCtxOf(LlmRequest request)
    {
        var value = request.GetOption<int?>(NumCtxKey) ?? request.GetOption<int?>(ContextTokensKey);
        return value is > 0 ? value : null;
    }

    internal static JObject BuildRequestBody(LlmRequest request, bool stream)
    {
        var messages = new JArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new JObject { ["role"] = "system", ["content"] = request.SystemPrompt });

        var history = request.Conversation.GetMessagesForRequest(
            maxTokens: request.SlidingWindowMaxTokens,
            useSlidingWindow: request.UseSlidingWindow);

        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var msg in history)
            messages.Add(ToMessage(msg, toolNames));

        var options = new JObject();
        if (request.MaxTokens.HasValue) options["num_predict"] = request.MaxTokens.Value;
        if (NumCtxOf(request) is { } numCtx) options["num_ctx"] = numCtx;
        if (request.Temperature.HasValue) options["temperature"] = request.Temperature.Value;
        if (request.TopP.HasValue) options["top_p"] = request.TopP.Value;
        if (request.Stop?.Count > 0) options["stop"] = new JArray(request.Stop);

        var body = new JObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = stream
        };

        if (request.Tools?.Count > 0)
            body["tools"] = new JArray(request.Tools.Select(ToTool));

        if (request.ResponseFormat?.Type == ResponseFormatType.JsonObject)
            body["format"] = request.ResponseFormat.JsonSchema == null
                ? "json"
                : JObject.Parse(request.ResponseFormat.JsonSchema.ToJson());

        // Free options: think / keep_alive / format at the root (they win over the derived ones), the rest in options.
        foreach (var prop in request.Options.Properties())
        {
            if (FrameworkFacadeOptionKeys.Contains(prop.Name))
                continue;
            var root = RootOptionKeys.FirstOrDefault(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase));
            if (root != null)
                body[root] = prop.Value.DeepClone();
            else
                options[prop.Name] = prop.Value.DeepClone();
        }

        if (options.HasValues)
            body["options"] = options;

        return body;
    }

    private static JObject ToMessage(Message message, Dictionary<string, string> toolNames)
    {
        switch (message.Role)
        {
            case "system":
                return new JObject { ["role"] = message.Role, ["content"] = message.Content ?? "" };

            case "user":
                var user = new JObject { ["role"] = message.Role, ["content"] = message.Content ?? "" };
                if (message.Attachments is { Count: > 0 })
                {
                    var images = new JArray();
                    foreach (var a in message.Attachments)
                    {
                        if (a.Kind == AttachmentKind.Pdf)
                            throw new NotSupportedException(
                                "El cliente nativo de Ollama no admite PDF: adjunte el contenido como texto o como imagen.");
                        images.Add(a.DataBase64);
                    }
                    user["images"] = images;
                }
                return user;

            case "assistant":
                var assistant = new JObject { ["role"] = "assistant", ["content"] = message.Content ?? "" };
                if (message.ToolCalls?.Count > 0)
                {
                    var calls = new JArray();
                    foreach (var tc in message.ToolCalls)
                    {
                        toolNames[tc.Id] = tc.Name;
                        calls.Add(new JObject
                        {
                            ["type"] = "function",
                            ["function"] = new JObject
                            {
                                ["index"] = calls.Count,
                                ["name"] = tc.Name,
                                ["arguments"] = ArgumentsObject(tc.Arguments)
                            }
                        });
                    }
                    assistant["tool_calls"] = calls;
                }
                return assistant;

            case "tool":
                var name = message.Name
                    ?? (message.ToolCallId != null && toolNames.TryGetValue(message.ToolCallId, out var n) ? n : "");
                return new JObject { ["role"] = "tool", ["tool_name"] = name, ["content"] = message.Content ?? "" };

            default:
                throw new ArgumentException($"Unknown role: {message.Role}");
        }
    }

    private static JObject ToTool(ToolDefinition tool) => new()
    {
        ["type"] = "function",
        ["function"] = new JObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = JObject.Parse(tool.ParametersJsonSchema)
        }
    };

    /// <summary>History arguments are a JSON string; Ollama expects an object.</summary>
    private static JObject ArgumentsObject(string? arguments)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(arguments) && JToken.Parse(arguments) is JObject o)
                return o;
        }
        catch (JsonException)
        {
            // invalid arguments from the model: an empty object is forwarded
        }
        return new JObject();
    }

    // -------------------------------------------------------------------------
    // Response parsing
    // -------------------------------------------------------------------------

    /// <summary>Ollama does not number tool calls: the id is created here and is unique across the conversation.</summary>
    private static string NewCallId() => "ollama_" + Guid.NewGuid().ToString("N")[..12];

    private static ToolCall ToToolCall(JToken tc)
    {
        var fn = tc["function"];
        var args = fn?["arguments"];
        return new ToolCall
        {
            Id = tc["id"]?.Type == JTokenType.String && tc["id"]!.Value<string>() is { Length: > 0 } id ? id : NewCallId(),
            Name = fn?["name"]?.Value<string>() ?? "",
            Arguments = args switch
            {
                JObject o => o.ToString(Formatting.None),
                { Type: JTokenType.String } s when s.Value<string>() is { Length: > 0 } text => text,
                _ => "{}"
            },
            Native = new NativeObject { Provider = "Ollama", Value = tc, TypeName = typeof(JToken).AssemblyQualifiedName! }
        };
    }

    private static (FinishReason Reason, string? Raw) FinishOf(string? doneReason, bool hasToolCalls)
    {
        if (hasToolCalls)
            return (FinishReason.ToolCalls, null);
        return doneReason switch
        {
            null or "" or "stop" or "load" or "unload" => (FinishReason.Stop, null),
            "length" => (FinishReason.Length, null),
            _ => (FinishReason.Other, doneReason)
        };
    }

    /// <summary>
    /// The prompt did not fit: an empty answer (no text, calls or thinking) that ended by length without exhausting
    /// <c>num_predict</c>. A reasoning model that burned its output budget thinking is not this case.
    /// </summary>
    internal static bool ContextTooSmall(bool empty, string? doneReason, int? evalCount, int? numPredict) =>
        empty && doneReason == "length" && !(numPredict is > 0 && evalCount >= numPredict * 0.9);

    internal static string ContextTooSmallMessage(LlmRequest request, int? promptTokens, int? evalCount)
    {
        var ctx = NumCtxOf(request);
        return $"Ollama returned an empty answer for model '{request.Model}' with done_reason 'length': the prompt does not fit in the model's context window " +
               $"(num_ctx {(ctx is { } c ? c.ToString() : "not set: the Modelfile or the server default applies")}; " +
               $"prompt_eval_count {(promptTokens?.ToString() ?? "unknown")}, eval_count {(evalCount?.ToString() ?? "unknown")}). " +
               $"Raise '{NumCtxKey}' in the provider's or role's Inference options.";
    }

    private void WarnIfPromptFilledContext(LlmRequest request, int? promptTokens)
    {
        if (NumCtxOf(request) is { } ctx && promptTokens is { } p && p >= ctx && _logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning(
                "The prompt evaluated by Ollama for model {Model} ({Prompt} tokens) reached num_ctx ({Context}): it was probably truncated. Raise '{Key}'.",
                request.Model, p, ctx, NumCtxKey);
    }

    private void RememberContext(LlmRequest request)
    {
        if (!string.IsNullOrEmpty(request.Model) && NumCtxOf(request) is { } ctx)
            _contextByModel[request.Model] = ctx;
    }

    private static int? Int(JToken? token) => token is { Type: JTokenType.Integer or JTokenType.Float } ? token.Value<int>() : null;

    // -------------------------------------------------------------------------
    // InvokeAsync
    // -------------------------------------------------------------------------

    internal sealed class AttemptInfo
    {
        public TimeSpan? RetryAfter { get; set; }
    }

    public async Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        RememberContext(request);
        var delay = _retryInitialDelay;
        var requestTimeout = RequestTimeoutOf(request);

        for (var attempt = 1; ; attempt++)
        {
            var info = new AttemptInfo();
            var result = await InvokeAttemptAsync(request, info, requestTimeout, cancellationToken);

            if (result.IsSuccess || !result.IsRetriable || attempt >= _retryMaxAttempts)
            {
                if (!result.IsSuccess && result.IsRetriable)
                    _logger.LogError("Giving up after {Attempts} attempts for model {Model}.", attempt, request.Model);
                return result;
            }

            var wait = info.RetryAfter is { } hint && hint > TimeSpan.Zero
                ? (hint < _retryMaxDelay ? hint : _retryMaxDelay)
                : delay;
            _logger.LogWarning("Attempt {Attempt}/{MaxAttempts} failed for model {Model}, retrying in {Delay}ms. Reason: {Reason}",
                attempt, _retryMaxAttempts, request.Model, wait.TotalMilliseconds, result.Error?.Message);

            await Task.Delay(wait, cancellationToken);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _retryMaxDelay.TotalMilliseconds));
        }
    }

    private async Task<LlmResult> InvokeAttemptAsync(LlmRequest request, AttemptInfo info, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (timeout is not { } limit)
            return await InvokeOnceAsync(request, info, cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        bool Expired() => deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

        LlmResult? result;
        try
        {
            result = await InvokeOnceAsync(request, info, deadline.Token);
        }
        catch (Exception) when (Expired())
        {
            result = null;
        }

        if (result is { IsSuccess: true } || !Expired())
            return result!;

        var ex = new TimeoutException(
            $"The request to model '{request.Model}' got no response within the per-attempt limit of {FormatSeconds(limit)} s " +
            "(RequestTimeoutSeconds); raise it or set 0 to remove it.");
        _logger.LogError(ex, "LLM attempt timed out for model {Model}", request.Model);
        return LlmResult.Failure(ex, isRetriable: true);
    }

    private async Task<LlmResult> InvokeOnceAsync(LlmRequest request, AttemptInfo info, CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient(request);
        JObject body;
        try
        {
            body = BuildRequestBody(request, stream: false);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogError(ex, "Request not supported by {Provider}", Name);
            return LlmResult.Failure(ex, isRetriable: false);
        }

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Invoking {Model} ({Provider}) to {Endpoint} with body: {Body}", request.Model, Name, GetEndpoint(request), AttachmentLog.Safe(body).ToString(Formatting.Indented));
        else if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Invoking {Model} ({Provider}) to {Endpoint}", request.Model, Name, GetEndpoint(request));

        string rawJson;
        try
        {
            using var httpRequest = CreateHttpRequest(request, body, stream: false);
            using var httpResponse = await client.SendAsync(httpRequest, cancellationToken);
            rawJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var status = (int)httpResponse.StatusCode;
                info.RetryAfter = httpResponse.Headers.RetryAfter?.Delta;
                var ex = new HttpRequestException($"HTTP {status}: {Truncate(rawJson, RawBodyLogLimit)}", null, httpResponse.StatusCode);
                _logger.LogError(ex, "API error. Retriable: {Retriable}", IsRetriable(status));
                return LlmResult.Failure(ex, IsRetriable(status));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
            _logger.LogError(ex, "Failed to parse API response JSON for model {Model}. Raw body: {Body}", request.Model, Truncate(rawJson, RawBodyLogLimit));
            return LlmResult.Failure(ex, isRetriable: false);
        }

        if (root["error"] is { Type: JTokenType.String } error)
        {
            var ex = new HttpRequestException($"Ollama error: {error.Value<string>()}");
            _logger.LogError(ex, "Ollama reported an error for model {Model}", request.Model);
            return LlmResult.Failure(ex, isRetriable: false);
        }

        var message = root["message"] as JObject;
        var content = message?["content"]?.Value<string>() ?? "";
        var thinking = message?["thinking"]?.Value<string>();
        var toolCalls = (message?["tool_calls"] as JArray)?.Select(ToToolCall).ToList();
        if (toolCalls is { Count: 0 }) toolCalls = null;
        var doneReason = root["done_reason"]?.Value<string>();
        var promptTokens = Int(root["prompt_eval_count"]);
        var evalCount = Int(root["eval_count"]);

        var empty = content.Length == 0 && toolCalls == null && string.IsNullOrEmpty(thinking);
        if (ContextTooSmall(empty, doneReason, evalCount, request.MaxTokens))
        {
            var ex = new OllamaContextTooSmallException(ContextTooSmallMessage(request, promptTokens, evalCount), NumCtxOf(request), promptTokens);
            _logger.LogError(ex, "Ollama answered nothing: insufficient context for model {Model}", request.Model);
            return LlmResult.Failure(ex, isRetriable: false);
        }

        WarnIfPromptFilledContext(request, promptTokens);

        var (reason, raw) = FinishOf(doneReason, toolCalls != null);
        var candidate = new LlmResponse
        {
            Index = 0,
            Content = content,
            Reasoning = string.IsNullOrEmpty(thinking) ? null : thinking,
            ToolCalls = toolCalls,
            FinishReason = reason,
            RawFinishReason = raw
        };

        return LlmResult.Success(
            [candidate],
            tokensUsed: promptTokens.HasValue || evalCount.HasValue ? (promptTokens ?? 0) + (evalCount ?? 0) : null,
            promptTokens: promptTokens,
            completionTokens: evalCount,
            model: root["model"]?.Value<string>() ?? request.Model,
            nativeResponse: root);
    }

    // -------------------------------------------------------------------------
    // InvokeStreamingAsync (NDJSON)
    // -------------------------------------------------------------------------

    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RememberContext(request);
        var client = GetOrCreateClient(request);
        var body = BuildRequestBody(request, stream: true);

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Invoking {Model} ({Provider}) to {Endpoint} (streaming) with body: {Body}", request.Model, Name, GetEndpoint(request), AttachmentLog.Safe(body).ToString(Formatting.Indented));

        // Inactivity limit: armed while waiting for the response and for each line, disarmed while the caller processes
        // what was yielded, so only the provider's silence counts.
        var inactivityLimit = StreamingInactivityTimeoutOf(request);
        using var activity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void Arm() { if (inactivityLimit is { } limit) activity.CancelAfter(limit); }
        void Disarm() { if (inactivityLimit != null) activity.CancelAfter(Timeout.InfiniteTimeSpan); }
        bool Stalled() => activity.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
        TimeoutException StalledError(string phase)
        {
            var ex = new TimeoutException(
                $"The stream from model '{request.Model}' sent nothing for {FormatSeconds(inactivityLimit!.Value)} s {phase} " +
                "(StreamingInactivityTimeoutSeconds); raise it or set 0 to remove it.");
            _logger.LogError(ex, "Streaming call stalled for model {Model}", request.Model);
            return ex;
        }

        using var httpRequest = CreateHttpRequest(request, body, stream: true);
        HttpResponseMessage httpResponse;
        Arm();
        try
        {
            httpResponse = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, activity.Token);
        }
        catch (Exception) when (Stalled())
        {
            throw StalledError("before the response headers arrived");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Streaming HTTP request failed");
            throw;
        }
        finally
        {
            Disarm();
        }

        using (httpResponse)
        {
            if (!httpResponse.IsSuccessStatusCode)
            {
                var errBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Streaming API error {Status}: {Body}", (int)httpResponse.StatusCode, Truncate(errBody, RawBodyLogLimit));
                throw new HttpRequestException(
                    $"Provider returned HTTP {(int)httpResponse.StatusCode}: {Truncate(errBody, RawBodyLogLimit)}", null, httpResponse.StatusCode);
            }

            using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));

            var toolCallCount = 0;
            var anyOutput = false; // text, thinking or tool calls
            LlmStreamChunk? finalChunk = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                Arm();
                try
                {
                    line = await reader.ReadLineAsync(activity.Token);
                    if (line == null) break;
                }
                catch (Exception) when (Stalled())
                {
                    throw StalledError("while waiting for the next chunk");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error reading NDJSON stream");
                    break;
                }
                finally
                {
                    Disarm();
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JObject chunk;
                try
                {
                    chunk = JObject.Parse(line);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Skipping a stream line that is not JSON for model {Model}", request.Model);
                    continue;
                }

                if (chunk["error"] is { Type: JTokenType.String } error)
                    throw new HttpRequestException($"Ollama error: {error.Value<string>()}");

                var message = chunk["message"] as JObject;
                if (message?["thinking"]?.Value<string>() is { Length: > 0 } thinking)
                {
                    anyOutput = true;
                    yield return new LlmStreamChunk { Delta = thinking, IsThinking = true };
                }

                if (message?["content"]?.Value<string>() is { Length: > 0 } text)
                {
                    anyOutput = true;
                    yield return new LlmStreamChunk { Delta = text };
                }

                if (message?["tool_calls"] is JArray { Count: > 0 } calls)
                {
                    anyOutput = true;
                    var updates = new List<ToolCallUpdate>();
                    foreach (var tc in calls)
                    {
                        var call = ToToolCall(tc);
                        updates.Add(new ToolCallUpdate
                        {
                            Index = toolCallCount++,
                            ToolCallId = call.Id,
                            FunctionName = call.Name,
                            FunctionArgumentsUpdate = call.Arguments,
                            Native = call.Native
                        });
                    }
                    yield return new LlmStreamChunk { Delta = string.Empty, ToolCallUpdates = updates };
                }

                if (chunk["done"] is { Type: JTokenType.Boolean } done && done.Value<bool>())
                {
                    var doneReason = chunk["done_reason"]?.Value<string>();
                    var promptTokens = Int(chunk["prompt_eval_count"]);
                    var evalCount = Int(chunk["eval_count"]);

                    if (ContextTooSmall(!anyOutput, doneReason, evalCount, request.MaxTokens))
                    {
                        var ex = new OllamaContextTooSmallException(ContextTooSmallMessage(request, promptTokens, evalCount), NumCtxOf(request), promptTokens);
                        _logger.LogError(ex, "Ollama answered nothing: insufficient context for model {Model}", request.Model);
                        throw ex;
                    }

                    WarnIfPromptFilledContext(request, promptTokens);

                    var (reason, raw) = FinishOf(doneReason, toolCallCount > 0);
                    finalChunk = new LlmStreamChunk
                    {
                        Delta = string.Empty,
                        IsComplete = true,
                        FinishReason = reason,
                        RawFinishReason = raw,
                        TokensUsed = promptTokens.HasValue || evalCount.HasValue ? (promptTokens ?? 0) + (evalCount ?? 0) : null,
                        PromptTokens = promptTokens,
                        CompletionTokens = evalCount
                    };
                    yield return finalChunk;
                    break;
                }
            }

            if (finalChunk == null && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("The stream from model {Model} ended without Ollama's final done chunk.", request.Model);
                yield return new LlmStreamChunk
                {
                    Delta = string.Empty,
                    IsComplete = true,
                    FinishReason = FinishReason.Other,
                    RawFinishReason = StreamClosedWithoutDone
                };
            }
        }
    }

    // -------------------------------------------------------------------------
    // Token budget
    // -------------------------------------------------------------------------

    public int EstimateTokenCount(string text) => (int)Math.Ceiling(text.Length / 4.0);

    /// <summary>
    /// The <c>num_ctx</c> configured for the model in the latest request that carried one, else 8,192. The service is
    /// shared by every role and the interface only receives the model name, so the value is known after the first call.
    /// </summary>
    public int GetMaxContextTokens(string? model = null) =>
        model != null && _contextByModel.TryGetValue(model, out var ctx) ? ctx : 8_192;
}

/// <summary>
/// Ollama answered nothing because the prompt did not fit in the context window (empty answer, <c>done_reason</c>
/// <c>length</c>, <c>num_predict</c> not exhausted). It is a visible failure, not a silent empty success.
/// </summary>
public sealed class OllamaContextTooSmallException(string message, int? configuredContext, int? promptTokens)
    : InvalidOperationException(message)
{
    /// <summary>The <c>num_ctx</c> sent in the request, or null when none was configured.</summary>
    public int? ConfiguredContext { get; } = configuredContext;

    /// <summary>The <c>prompt_eval_count</c> Ollama reported, when it did.</summary>
    public int? PromptTokens { get; } = promptTokens;
}
