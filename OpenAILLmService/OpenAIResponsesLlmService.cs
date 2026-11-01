using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Support;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenAILLmService;

/// <summary>
/// OpenAI <b>Responses API</b> (<c>POST {base}/responses</c>) as an <see cref="AITaskAgent.LLM.Abstractions.ILlmService"/>.
/// Reasoning models (gpt-5.x, o-series) accept tools together with any <c>reasoning.effort</c> through this API, which
/// chat completions refuses for some of them. It inherits the transport of <see cref="OpenAILlmService"/> (client cached
/// per base URL and key, retry policy, call gate, timeouts, key-safe logging) and replaces only the wire format: the body
/// (<see cref="BuildResponsesRequestBody"/>), the parsing of the answer and of the SSE event stream.
/// </summary>
public class OpenAIResponsesLlmService : OpenAILlmService
{
    public OpenAIResponsesLlmService(ILogger<OpenAIResponsesLlmService> logger, RetryPolicy? retryPolicy = null)
        : base((ILogger)logger, retryPolicy)
    {
    }

    /// <summary>For a subclass that forwards its own <c>ILogger&lt;T&gt;</c>.</summary>
    protected OpenAIResponsesLlmService(ILogger logger, RetryPolicy? retryPolicy = null)
        : base(logger, retryPolicy)
    {
    }

    public override string Name => "openai-responses";

    protected internal override string GetEndpoint(LlmRequest request)
    {
        var baseUrl = request.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        return $"{baseUrl}/responses";
    }

    // Chat-completions-only keys that must not reach the Responses body (they are rejected with 400 there).
    private static readonly HashSet<string> ChatOnlyOptionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "stream_options", "max_tokens", "max_completion_tokens", "response_format", "reasoning_effort",
        "reasoning_summary", "verbosity", "n", "stop"
    };

    private static readonly Regex ReasoningModelName =
        new(@"(^|[/:])(o\d|gpt-5|gpt-6)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? EffortOf(LlmRequest request)
    {
        var effort = request.Options.GetValue("reasoning_effort", StringComparison.OrdinalIgnoreCase)?.Type == JTokenType.String
            ? request.Options.GetValue("reasoning_effort", StringComparison.OrdinalIgnoreCase)!.Value<string>()
            : null;
        if (!string.IsNullOrWhiteSpace(effort)) return effort;

        return (request.Options.GetValue("reasoning", StringComparison.OrdinalIgnoreCase) as JObject)
            ?["effort"]?.Value<string>();
    }

    /// <summary>
    /// Whether the model takes <c>temperature</c> / <c>top_p</c>: a reasoning model does not, unless its effort is
    /// <c>none</c>. The effort configured on the request wins over the guess from the model name.
    /// </summary>
    internal static bool AcceptsSampling(LlmRequest request)
    {
        var effort = EffortOf(request);
        if (effort != null) return effort.Equals("none", StringComparison.OrdinalIgnoreCase);
        if (request.Options.GetValue("reasoning", StringComparison.OrdinalIgnoreCase) is JObject) return false;
        return !ReasoningModelName.IsMatch(request.Model ?? string.Empty);
    }

    internal static JObject BuildResponsesRequestBody(LlmRequest request, bool stream = false)
    {
        var input = new JArray();
        var conversation = request.Conversation.GetMessagesForRequest(
            maxTokens: request.SlidingWindowMaxTokens,
            useSlidingWindow: request.UseSlidingWindow);

        foreach (var msg in conversation)
        {
            switch (msg.Role)
            {
                case "system":
                    input.Add(TextMessage("system", "input_text", msg.Content));
                    break;
                case "user":
                    input.Add(new JObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = AttachmentWire.ResponsesUserContent(msg)
                    });
                    break;
                case "assistant":
                    if (!string.IsNullOrEmpty(msg.Content))
                        input.Add(TextMessage("assistant", "output_text", msg.Content));
                    // Only call_id travels: the item id (fc_...) would make the API demand its reasoning item back.
                    foreach (var tc in msg.ToolCalls ?? [])
                        input.Add(new JObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = tc.Id,
                            ["name"] = tc.Name,
                            ["arguments"] = string.IsNullOrEmpty(tc.Arguments) ? "{}" : tc.Arguments
                        });
                    break;
                case "tool":
                    input.Add(new JObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = msg.ToolCallId,
                        ["output"] = msg.Content ?? string.Empty
                    });
                    break;
                default:
                    throw new ArgumentException($"Unknown role: {msg.Role}");
            }
        }

        var body = new JObject { ["model"] = request.Model };
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt)) body["instructions"] = request.SystemPrompt;
        body["input"] = input;
        body["store"] = false;

        if (request.MaxTokens.HasValue) body["max_output_tokens"] = request.MaxTokens.Value;
        if (AcceptsSampling(request))
        {
            if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
            if (request.TopP.HasValue) body["top_p"] = request.TopP.Value;
        }
        if (request.User != null) body["user"] = request.User;

        if (request.Tools?.Count > 0)
            body["tools"] = new JArray(request.Tools.Select(t => new JObject
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JObject.Parse(t.ParametersJsonSchema)
            }));

        var text = new JObject();
        if (request.ResponseFormat?.Type == ResponseFormatType.JsonObject)
            text["format"] = request.ResponseFormat.JsonSchema == null
                ? new JObject { ["type"] = "json_object" }
                : new JObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "custom_schema",
                    ["strict"] = true,
                    ["schema"] = JObject.Parse(request.ResponseFormat.JsonSchema.ToJson())
                };
        if (request.Options.GetValue("verbosity", StringComparison.OrdinalIgnoreCase) is { Type: JTokenType.String } verbosity)
            text["verbosity"] = verbosity.DeepClone();
        if (text.HasValues) body["text"] = text;

        // Free options go straight through, except the facade keys and the chat-only ones handled above or below.
        foreach (var prop in request.Options.Properties())
            if (!FrameworkFacadeOptionKeys.Contains(prop.Name) && !ChatOnlyOptionKeys.Contains(prop.Name))
                body[prop.Name] = prop.Value.DeepClone();

        // reasoning_effort (chat-completions spelling) becomes reasoning.effort; a given `reasoning` object is kept.
        var effort = request.Options.GetValue("reasoning_effort", StringComparison.OrdinalIgnoreCase);
        var summary = request.Options.GetValue("reasoning_summary", StringComparison.OrdinalIgnoreCase);
        if (effort is { Type: not JTokenType.Null } || summary is { Type: not JTokenType.Null })
        {
            var reasoning = body["reasoning"] as JObject ?? new JObject();
            if (effort is { Type: not JTokenType.Null } && reasoning["effort"] == null) reasoning["effort"] = effort.DeepClone();
            if (summary is { Type: not JTokenType.Null } && reasoning["summary"] == null) reasoning["summary"] = summary.DeepClone();
            body["reasoning"] = reasoning;
        }

        if (stream) body["stream"] = true;
        return body;
    }

    private static JObject TextMessage(string role, string partType, string? content) => new()
    {
        ["type"] = "message",
        ["role"] = role,
        ["content"] = new JArray(new JObject { ["type"] = partType, ["text"] = content ?? string.Empty })
    };

    // -------------------------------------------------------------------------
    // Non streaming
    // -------------------------------------------------------------------------

    internal override async Task<LlmResult> InvokeOnceAsync(
        LlmRequest request, AttemptInfo info, CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient(request);
        var endpoint = GetEndpoint(request);
        JObject body;
        try
        {
            body = BuildResponsesRequestBody(request);
        }
        catch (NotSupportedException ex)
        {
            Logger.LogError(ex, "Request not supported by {Provider}", Name);
            return LlmResult.Failure(ex, isRetriable: false);
        }

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Invoking {Model} ({Provider}) to {Endpoint} with body: {Body}",
                request.Model, Name, endpoint, AttachmentWire.LogSafe(body).ToString(Formatting.Indented));
        else if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation("Invoking {Model} ({Provider}) to {Endpoint}", request.Model, Name, endpoint);

        string rawJson;
        try
        {
            using var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var httpResponse = await client.PostAsync(endpoint, content, cancellationToken);
            rawJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var statusCode = (int)httpResponse.StatusCode;
                info.StatusCode = statusCode;
                info.RetryAfter = RetryAfterOf(httpResponse, DateTimeOffset.UtcNow);
                var ex = new HttpRequestException($"HTTP {statusCode}: {rawJson}", null, httpResponse.StatusCode);
                Logger.LogError(ex, "API error. Retriable: {Retriable}", IsRetriable(statusCode));
                return LlmResult.Failure(ex, IsRetriable(statusCode));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "HTTP request failed");
            return LlmResult.Failure(ex, isRetriable: true);
        }

        JObject root;
        try
        {
            root = JObject.Parse(rawJson);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse Responses API JSON for model {Model}. Raw body: {Body}",
                request.Model, Truncate(rawJson, RawBodyLogLimit));
            return LlmResult.Failure(ex, isRetriable: false);
        }

        return ParseResponse(root, request, rawJson);
    }

    internal LlmResult ParseResponse(JObject root, LlmRequest request, string rawJson)
    {
        var status = root["status"]?.Value<string>();
        if (status is "failed" || root["error"] is JObject)
        {
            var error = ErrorOf(root["error"] as JObject);
            var ex = new HttpRequestException($"Response failed: {error.Message}. Body: {Truncate(rawJson, RawBodyLogLimit)}");
            Logger.LogError("Responses API reported a failure for model {Model}: {Error}", request.Model, error.Message);
            return LlmResult.Failure(ex, error.Retriable);
        }

        var contentBuilder = new StringBuilder();
        var reasoningParts = new List<string>();
        var toolCalls = new List<ToolCall>();

        foreach (var item in ((root["output"] as JArray) ?? []))
        {
            switch (item["type"]?.Value<string>())
            {
                case "message":
                    foreach (var part in ((item["content"] as JArray) ?? []))
                    {
                        var partType = part["type"]?.Value<string>();
                        if (partType == "output_text") contentBuilder.Append(part["text"]?.Value<string>());
                        else if (partType == "refusal") contentBuilder.Append(part["refusal"]?.Value<string>());
                    }
                    break;
                case "reasoning":
                    foreach (var part in ((item["summary"] as JArray) ?? []))
                        if (part["text"]?.Value<string>() is { Length: > 0 } s) reasoningParts.Add(s);
                    foreach (var part in ((item["content"] as JArray) ?? []))
                        if (part["text"]?.Value<string>() is { Length: > 0 } s) reasoningParts.Add(s);
                    break;
                case "function_call":
                    toolCalls.Add(ToolCallOf(item));
                    break;
            }
        }

        var (finish, raw) = FinishOf(root, toolCalls.Count > 0);
        var usage = root["usage"] as JObject;
        var model = root["model"]?.Value<string>() ?? request.Model;

        var candidate = new LlmResponse
        {
            Index = 0,
            Content = contentBuilder.ToString(),
            Reasoning = reasoningParts.Count > 0 ? string.Join("\n\n", reasoningParts) : null,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            FinishReason = finish,
            RawFinishReason = raw
        };

        var (input, output, total, reasoningTokens, cached) = UsageOf(usage);
        return LlmResult.Success(
            [candidate],
            tokensUsed: total,
            promptTokens: input,
            completionTokens: output,
            model: model,
            nativeResponse: root,
            reasoningTokens: reasoningTokens,
            cachedPromptTokens: cached);
    }

    private static ToolCall ToolCallOf(JToken item) => new()
    {
        Id = item["call_id"]?.Value<string>() ?? item["id"]?.Value<string>() ?? string.Empty,
        Name = item["name"]?.Value<string>() ?? string.Empty,
        Arguments = item["arguments"]?.Value<string>() ?? string.Empty
    };

    private static (FinishReason Reason, string? Raw) FinishOf(JObject response, bool hasToolCalls)
    {
        if (response["status"]?.Value<string>() == "incomplete")
        {
            var reason = (response["incomplete_details"] as JObject)?["reason"]?.Value<string>();
            return reason switch
            {
                "max_output_tokens" => (FinishReason.Length, null),
                "content_filter" => (FinishReason.ContentFilter, null),
                _ => (FinishReason.Other, reason ?? "incomplete")
            };
        }
        return (hasToolCalls ? FinishReason.ToolCalls : FinishReason.Stop, null);
    }

    private static (int? Input, int? Output, int? Total, int? Reasoning, int? Cached) UsageOf(JObject? usage) => (
        usage?["input_tokens"]?.Value<int?>(),
        usage?["output_tokens"]?.Value<int?>(),
        usage?["total_tokens"]?.Value<int?>(),
        (usage?["output_tokens_details"] as JObject)?["reasoning_tokens"]?.Value<int?>(),
        (usage?["input_tokens_details"] as JObject)?["cached_tokens"]?.Value<int?>());

    /// <summary>A failed response or an <c>error</c> event: its message and whether retrying can help.</summary>
    private static (string Message, bool Retriable) ErrorOf(JObject? error)
    {
        var code = error?["code"]?.Value<string>();
        var message = error?["message"]?.Value<string>() ?? "unknown error";
        var retriable = code is "server_error" or "rate_limit_exceeded" or "overloaded" or "timeout"
            or "service_unavailable";
        return (code == null ? message : $"{code}: {message}", retriable);
    }

    // -------------------------------------------------------------------------
    // Streaming
    // -------------------------------------------------------------------------

    public override async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Held for the whole stream and released by the iterator's disposal (see OpenAILlmService).
        var gate = GateOf(request);
        using var slot = await gate.EnterAsync(cancellationToken);

        var client = GetOrCreateClient(request);
        var endpoint = GetEndpoint(request);
        var body = BuildResponsesRequestBody(request, stream: true);

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Invoking {Model} ({Provider}) to {Endpoint} (streaming) with body: {Body}",
                request.Model, Name, endpoint, AttachmentWire.LogSafe(body).ToString(Formatting.Indented));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };

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
            Logger.LogError(ex, "Streaming call stalled for model {Model}", request.Model);
            return ex;
        }

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
            Logger.LogError(ex, "Streaming HTTP request failed");
            throw;
        }
        finally
        {
            Disarm();
        }

        using var response = httpResponse;
        if (!response.IsSuccessStatusCode)
        {
            ReportThrottle(gate, (int)response.StatusCode, RetryAfterOf(response, DateTimeOffset.UtcNow));
            var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            Logger.LogError("Streaming API error {Status}: {Body}", (int)response.StatusCode, errBody);
            throw new HttpRequestException(
                $"Provider returned HTTP {(int)response.StatusCode}: {errBody}", null, response.StatusCode);
        }

        // Function calls by item id: the tool-call index handed to the consumer, and whether arguments already streamed.
        var calls = new Dictionary<string, (int Index, StringBuilder Args)>();
        var reasoningSeen = false;
        var textSeen = false;
        var finished = false;

        string KeyOf(JObject ev, bool fromItem) =>
            (fromItem ? ev["item"]?["id"] : ev["item_id"])?.Value<string>()
            ?? $"output_{ev["output_index"]?.Value<int>() ?? 0}";

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

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
                Logger.LogError(ex, "Error reading SSE stream");
                break;
            }
            finally
            {
                Disarm();
            }

            if (string.IsNullOrEmpty(line) || !line.StartsWith("data:")) continue;
            var data = line["data:".Length..].TrimStart();
            if (data == "[DONE]") break;

            JObject ev;
            try { ev = JObject.Parse(data); }
            catch { continue; }

            switch (ev["type"]?.Value<string>())
            {
                case "response.output_text.delta":
                case "response.refusal.delta":
                    if (ev["delta"]?.Value<string>() is { Length: > 0 } text)
                    {
                        textSeen = true;
                        yield return new LlmStreamChunk { Delta = text, IsThinking = false };
                    }
                    break;

                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    if (ev["delta"]?.Value<string>() is { Length: > 0 } thought)
                    {
                        reasoningSeen = true;
                        yield return new LlmStreamChunk { Delta = thought, IsThinking = true };
                    }
                    break;

                case "response.reasoning_summary_part.added" when reasoningSeen:
                    // A new summary part: keep the parts apart in the accumulated reasoning.
                    yield return new LlmStreamChunk { Delta = "\n\n", IsThinking = true };
                    break;

                case "response.output_item.added" when (ev["item"]?["type"]?.Value<string>() == "function_call"):
                {
                    var key = KeyOf(ev, fromItem: true);
                    var args = new StringBuilder(ev["item"]?["arguments"]?.Value<string>() ?? string.Empty);
                    var index = calls.Count;
                    calls[key] = (index, args);
                    yield return ToolUpdate(index, ev["item"]!["call_id"]?.Value<string>() ?? key,
                        ev["item"]!["name"]?.Value<string>(), args.ToString());
                    break;
                }

                case "response.function_call_arguments.delta":
                {
                    var key = KeyOf(ev, fromItem: false);
                    var delta = ev["delta"]?.Value<string>();
                    if (string.IsNullOrEmpty(delta)) break;
                    if (!calls.TryGetValue(key, out var call))
                        calls[key] = call = (calls.Count, new StringBuilder());
                    call.Args.Append(delta);
                    yield return ToolUpdate(call.Index, null, null, delta);
                    break;
                }

                case "response.output_item.done" when (ev["item"]?["type"]?.Value<string>() == "function_call"):
                {
                    var key = KeyOf(ev, fromItem: true);
                    var item = ev["item"]!;
                    var fullArgs = item["arguments"]?.Value<string>() ?? string.Empty;
                    if (!calls.TryGetValue(key, out var call))
                    {
                        // Never announced: the whole call arrives at once.
                        calls[key] = (calls.Count, new StringBuilder(fullArgs));
                        yield return ToolUpdate(calls[key].Index, item["call_id"]?.Value<string>() ?? key,
                            item["name"]?.Value<string>(), fullArgs);
                    }
                    else if (call.Args.Length == 0 && fullArgs.Length > 0)
                    {
                        // Announced but with no argument deltas: complete it with the final value.
                        call.Args.Append(fullArgs);
                        yield return ToolUpdate(call.Index, null, null, fullArgs);
                    }
                    break;
                }

                case "response.completed":
                case "response.incomplete":
                case "response.failed":
                {
                    var final = ev["response"] as JObject ?? new JObject();
                    if (ev["type"]!.Value<string>() == "response.failed" || final["status"]?.Value<string>() == "failed")
                    {
                        var error = ErrorOf(final["error"] as JObject);
                        Logger.LogError("Responses stream failed for model {Model}: {Error}", request.Model, error.Message);
                        throw new HttpRequestException($"Response failed: {error.Message}");
                    }

                    // Calls the item events never showed (a provider that only sends the final object).
                    foreach (var item in ((final["output"] as JArray) ?? []))
                    {
                        if (item["type"]?.Value<string>() != "function_call") continue;
                        var key = item["id"]?.Value<string>() ?? item["call_id"]?.Value<string>() ?? $"final_{calls.Count}";
                        if (calls.ContainsKey(key)) continue;
                        var tc = ToolCallOf(item);
                        calls[key] = (calls.Count, new StringBuilder(tc.Arguments));
                        yield return ToolUpdate(calls[key].Index, tc.Id, tc.Name, tc.Arguments);
                    }

                    var (finish, raw) = FinishOf(final, calls.Count > 0);
                    var (input, output, total, reasoningTokens, cached) = UsageOf(final["usage"] as JObject);
                    finished = true;
                    yield return new LlmStreamChunk
                    {
                        Delta = string.Empty,
                        IsComplete = true,
                        FinishReason = finish,
                        RawFinishReason = raw,
                        TokensUsed = total,
                        PromptTokens = input,
                        CompletionTokens = output,
                        ReasoningTokens = reasoningTokens,
                        CachedPromptTokens = cached
                    };
                    break;
                }

                case "error":
                {
                    var error = ErrorOf(ev["error"] as JObject ?? ev);
                    Logger.LogError("Responses stream error for model {Model}: {Error}", request.Model, error.Message);
                    throw new HttpRequestException($"Response failed: {error.Message}");
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!finished)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning(
                    "The stream from model {Model} closed without a terminal response event: the answer ends with " +
                    "FinishReason.Other ('{RawFinishReason}').", request.Model, StreamClosedWithoutFinishReason);
            yield return new LlmStreamChunk
            {
                Delta = string.Empty,
                IsComplete = true,
                FinishReason = FinishReason.Other,
                RawFinishReason = StreamClosedWithoutFinishReason
            };
        }
        else if (!textSeen && calls.Count == 0 && Logger.IsEnabled(LogLevel.Warning))
        {
            Logger.LogWarning("Streaming completed without yielding any text or tool call for model {Model}", request.Model);
        }
    }

    private static LlmStreamChunk ToolUpdate(int index, string? id, string? name, string? args) => new()
    {
        Delta = string.Empty,
        ToolCallUpdates =
        [
            new ToolCallUpdate
            {
                Index = index,
                ToolCallId = id,
                FunctionName = name,
                FunctionArgumentsUpdate = args
            }
        ]
    };
}
