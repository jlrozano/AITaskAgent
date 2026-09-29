using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Support;
using LlmFinishReason = AITaskAgent.LLM.Constants.FinishReason;
using AITaskAgent.Resilience;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using NJsonSchema;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GeminiLlmService;

/// <summary>
/// Gemini implementation of ILlmService using the official Google.GenAI SDK.
/// Supports Context Caching, Files API, Google Search Grounding, and File Search (RAG).
/// </summary>
public sealed class GeminiLlmService : ILlmService
{
    private readonly ConcurrentDictionary<string, Client> _clients = new();
    private readonly ILogger<GeminiLlmService> _logger;
    private readonly RateLimiter? _rateLimiter;
    private readonly RetryPolicy _retryPolicy;

    // Keyed the same way as _clients ("BaseUrl|ApiKey"), so two different Gemini API keys each get
    // their own manager instead of every credential silently sharing whichever client was created
    // first.
    private readonly ConcurrentDictionary<string, GeminiCacheManager> _cacheManagers = new();
    private readonly ConcurrentDictionary<string, GeminiFileManager> _fileManagers = new();
    private string? _lastCacheKey;

    /// <summary>
    /// Manager for Context Caching operations, for the credentials used by the most recent request.
    /// </summary>
    /// <remarks>
    /// Both managers need a <c>Client</c>, and the client is built from the credentials carried by
    /// each request - so neither exists until the service has been invoked at least once. Accessing
    /// this before that used to throw a bare NullReferenceException; it now says what is wrong.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The service has not been invoked yet.</exception>
    public GeminiCacheManager CacheManager =>
        _lastCacheKey != null && _cacheManagers.TryGetValue(_lastCacheKey, out var manager)
            ? manager
            : throw NotReady(nameof(CacheManager));

    /// <summary>
    /// Manager for Files API operations. Same lifecycle as <see cref="CacheManager"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The service has not been invoked yet.</exception>
    public GeminiFileManager FileManager =>
        _lastCacheKey != null && _fileManagers.TryGetValue(_lastCacheKey, out var manager)
            ? manager
            : throw NotReady(nameof(FileManager));

    private static InvalidOperationException NotReady(string member) => new(
        $"{member} is not available yet. It is created from the credentials of the first request, " +
        "so invoke the service once before using it.");

    /// <summary>
    /// Creates a new Gemini LLM service instance.
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="rateLimiter">Optional rate limiter for request throttling</param>
    /// <param name="retryPolicy">Optional retry policy for transient errors</param>
    public GeminiLlmService(
        ILogger<GeminiLlmService> logger,
        RateLimiter? rateLimiter = null,
        RetryPolicy? retryPolicy = null)
    {
        _logger = logger;
        _rateLimiter = rateLimiter;
        _retryPolicy = retryPolicy ?? new RetryPolicy
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(60),
            BackoffMultiplier = 2.0,
            ShouldRetry = IsTransientError
        };

        _logger.LogInformation("GeminiLlmService initialized. LLM configuration comes from request profiles.");
    }

    private (Client Client, string Model) GetClientAndModelForRequest(LlmRequest request)
    {
        var model = request.Model;
        var cacheKey = $"{request.BaseUrl}|{request.ApiKey}";

        _logger.LogDebug("Getting client for model: {Model}, baseUrl: {BaseUrl}",
            model, request.BaseUrl);

        if (_clients.TryGetValue(cacheKey, out var cachedClient))
        {
            _logger.LogDebug("Using cached client for {Model}", model);
            _lastCacheKey = cacheKey;
            return (cachedClient, model);
        }

        _logger.LogInformation("Creating new Gemini client for model: {Model}", model);

        // Map BaseUrl and ApiVersion if provided
        HttpOptions? httpOptions = null;
        if (!string.IsNullOrEmpty(request.BaseUrl) || !string.IsNullOrEmpty(request.ApiVersion))
        {
            httpOptions = new HttpOptions
            {
                BaseUrl = string.IsNullOrEmpty(request.BaseUrl) ? null : request.BaseUrl,
                ApiVersion = string.IsNullOrEmpty(request.ApiVersion) ? null : request.ApiVersion
            };
        }

        var newClient = new Client(apiKey: request.ApiKey, httpOptions: httpOptions);

        _clients.TryAdd(cacheKey, newClient);

        // Initialize managers for this client's own credentials, not a single global instance -
        // otherwise a second API key would silently operate the cache/file manager against the
        // first key's account.
        _cacheManagers.GetOrAdd(cacheKey, _ => new GeminiCacheManager(newClient, _logger));
        _fileManagers.GetOrAdd(cacheKey, _ => new GeminiFileManager(newClient, _logger));

        _lastCacheKey = cacheKey;

        return (newClient, model);
    }

    /// <inheritdoc/>
    public string Name => "gemini";

    /// <inheritdoc/>
    /// <remarks>
    /// Provider-level failures (a bad API response, a transport error, a timeout) are always
    /// reported as <see cref="LlmResult.Failure(Exception, bool)"/>, never thrown - matching
    /// <c>OpenAILlmService</c>. Only genuine programming errors (null arguments, etc.) still throw.
    /// Retrying is done here with a manual loop rather than <see cref="_retryPolicy"/>'s
    /// exception-driven <c>ExecuteAsync</c>, for the same reason <c>OpenAILlmService</c> does its
    /// own retry loop: an API error no longer throws, so an exception-catching retry would never
    /// see it.
    /// </remarks>
    public async Task<LlmResult> InvokeAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_rateLimiter != null)
        {
            await _rateLimiter.WaitAsync(cancellationToken);
        }

        (var client, var model) = GetClientAndModelForRequest(request);

        _logger.LogDebug("Invoking Gemini model {Model} with conversation of {MessageCount} total messages",
            model, request.Conversation.History.Messages.Count);

        var contents = BuildContents(request);
        var config = BuildGenerateContentConfig(request);

        var delay = _retryPolicy.InitialDelay;
        LlmResult result;

        for (var attempt = 1; ; attempt++)
        {
            result = await InvokeOnceAsync(client, model, contents, config, cancellationToken);

            if (result.IsSuccess || !result.IsRetriable || attempt >= _retryPolicy.MaxAttempts)
            {
                if (!result.IsSuccess && result.IsRetriable && attempt >= _retryPolicy.MaxAttempts)
                {
                    _logger.LogError(
                        "Giving up after {Attempts} attempts for Gemini model {Model}.",
                        attempt, model);
                }

                return result;
            }

            _logger.LogWarning(
                "Gemini attempt {Attempt}/{MaxAttempts} failed for model {Model}, retrying in {Delay}ms. Reason: {Reason}",
                attempt, _retryPolicy.MaxAttempts, model, delay.TotalMilliseconds,
                result.Error?.Message);

            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromMilliseconds(
                Math.Min(delay.TotalMilliseconds * _retryPolicy.BackoffMultiplier, _retryPolicy.MaxDelay.TotalMilliseconds));
        }
    }

    private async Task<LlmResult> InvokeOnceAsync(
        Client client,
        string model,
        List<Content> contents,
        GenerateContentConfig config,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await client.Models.GenerateContentAsync(
                model: model,
                contents: contents,
                config: config);

            var usageMetadata = response.UsageMetadata;

            // Every Gemini candidate becomes an LlmResponse; LlmResult wraps the set along
            // with the usage that applies to the call as a whole.
            var candidates = response.Candidates?.Select(c => new LlmResponse
            {
                Index = c.Index ?? 0,
                Content = ExtractTextContent(c),
                Reasoning = ExtractReasoningContent(c),
                ToolCalls = ExtractToolCalls(c),
                FinishReason = MapFinishReason(c.FinishReason),
                RawFinishReason = c.FinishReason?.ToString()
            }).ToList() ?? [];

            var llmResult = LlmResult.Success(
                candidates,
                tokensUsed: usageMetadata?.TotalTokenCount,
                promptTokens: usageMetadata?.PromptTokenCount,
                completionTokens: usageMetadata?.CandidatesTokenCount,
                model: model,
                nativeResponse: response);

            _logger.LogInformation(
                "Gemini response: {Tokens} tokens, candidates: {CandidateCount}, finish: {Reason}",
                llmResult.TokensUsed, candidates.Count, llmResult.FinishReason);

            return llmResult;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or Google.GenAI.ClientError or Google.GenAI.ServerError)
        {
            var isRetriable = IsTransientError(ex);
            _logger.LogError(ex, "Gemini API error. Retriable: {Retriable}", isRetriable);
            return LlmResult.Failure(
                new InvalidOperationException($"Failed to call Gemini API: {ex.Message}", ex),
                isRetriable);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_rateLimiter != null)
        {
            await _rateLimiter.WaitAsync(cancellationToken);
        }

        (var client, var model) = GetClientAndModelForRequest(request);

        var contents = BuildContents(request);
        var config = BuildGenerateContentConfig(request);

        IAsyncEnumerator<GenerateContentResponse>? enumerator = null;
        GenerateContentResponse? firstChunk = null;

        // Retry logic for stream initialization
        (enumerator, firstChunk) = await _retryPolicy.ExecuteAsync(async _ =>
        {
            IAsyncEnumerator<GenerateContentResponse>? enumeratorResult = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stream = client.Models.GenerateContentStreamAsync(
                    model: model,
                    contents: contents,
                    config: config);

                enumeratorResult = stream.GetAsyncEnumerator(cancellationToken);

                if (await enumeratorResult.MoveNextAsync())
                {
                    return (enumeratorResult, enumeratorResult.Current);
                }

                return (enumeratorResult, (GenerateContentResponse?)null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // MoveNextAsync can fail after the enumerator (and its underlying HTTP stream)
                // was already created - without disposing it here, each failed attempt (up to
                // MaxAttempts) leaked a stream/connection instead of just the one from the last try.
                if (enumeratorResult != null)
                {
                    await enumeratorResult.DisposeAsync();
                }

                _logger.LogError(ex, "Gemini Streaming API error: {Message}", ex.Message);
                throw new InvalidOperationException($"Failed to start Gemini stream: {ex.Message}", ex);
            }
        }, _logger, cancellationToken);

        if (enumerator == null)
        {
            yield break;
        }

        await using (enumerator)
        {
            // Process first chunk if exists
            if (firstChunk != null)
            {
                foreach (var chunk in ProcessStreamChunk(firstChunk))
                {
                    yield return chunk;
                }
            }
            else
            {
                yield break;
            }

            // Continue with remaining chunks
            while (await enumerator.MoveNextAsync())
            {
                var response = enumerator.Current;
                foreach (var chunk in ProcessStreamChunk(response))
                {
                    yield return chunk;
                }
            }
        }
    }

    /// <inheritdoc/>
    public int EstimateTokenCount(string text)
    {
        // Rough estimation: ~4 characters per token
        // For exact count, use CountTokensAsync via CacheManager
        return (int)Math.Ceiling(text.Length / 4.0);
    }

    /// <inheritdoc/>
    public int GetMaxContextTokens(string? model = null)
    {
        // Return conservative default - actual limits should be configured per profile
        return 8192;
    }

    /// <summary>
    /// Debug helper: Lists available models for the API key.
    /// </summary>
    public async Task ListModelsAsync(string? apiKey = null, string? apiVersion = null)
    {
        Google.GenAI.Client? client = _clients.Values.FirstOrDefault();

        // If no initialized client, try to create one from provided key
        if (client == null && !string.IsNullOrEmpty(apiKey))
        {
            HttpOptions? httpOptions = null;
            if (!string.IsNullOrEmpty(apiVersion))
            {
                httpOptions = new HttpOptions { ApiVersion = apiVersion };
            }
            try
            {
                client = new Google.GenAI.Client(apiKey: apiKey, httpOptions: httpOptions);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating temporary client: {ex.Message}");
                return;
            }
        }

        if (client == null)
        {
            Console.WriteLine("⚠️ No client initialized and no API key provided. Please run a demo (option 1 or 2) first or provide config.");
            return;
        }

        Console.WriteLine($"\nListing models using client...");
        try
        {
            // The SDK typically uses ListAsync for resources. 
            // We pass null for config/params if optional.
            // Note: ListAsync returns a Task<Pager<...>>, so we must await the task first.
            // The Pager object itself is usually IAsyncEnumerable.
            var response = await client.Models.ListAsync();

            await foreach (var model in response)
            {
                // Print basic info to identify the model ID
                Console.WriteLine($"- ID: {model.Name}");
                Console.WriteLine($"  Display: {model.DisplayName}");
                Console.WriteLine($"  Desc: {model.Description}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error querying models: {ex.Message}");
            if (ex.Message.Contains("Not Found"))
            {
                Console.WriteLine("Hint: Check if ApiVersion is set correctly (e.g. 'v1alpha' for experimental models).");
            }
        }
    }

    /// <summary>
    /// Counts tokens exactly using Gemini's CountTokens API.
    /// </summary>
    public async Task<int> CountTokensExactAsync(
        string model,
        string content,
        CancellationToken cancellationToken = default)
    {
        var client = _clients.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("No client available. Make an LLM request first.");

        cancellationToken.ThrowIfCancellationRequested();

        var response = await client.Models.CountTokensAsync(
            model: model,
            contents: content);

        return response.TotalTokens ?? 0;
    }

    private static bool IsTransientError(Exception ex)
    {
        // Handle Gemini SDK specific ClientError
        if (ex is Google.GenAI.ClientError clientError)
        {
            // 429 (Too Many Requests) is typically transient, BUT in the context of "Free Tier limit=0", 
            // it is a hard configuration error that should NOT be retried.
            // We assume 429 is non-transient to avoid spamming "Quota exceeded" logs.
            if (clientError.StatusCode == 429)
            {
                return false;
            }
        }

        // Also check inner exception for ClientError
        if (ex.InnerException is Google.GenAI.ClientError innerClientError && innerClientError.StatusCode == 429)
        {
            return false;
        }

        if (ex is InvalidOperationException { InnerException: HttpRequestException })
        {
            return true;
        }

        return ex is HttpRequestException or TimeoutException;
    }

    private List<Content> BuildContents(LlmRequest request)
    {
        List<Content> contents = [];

        var conversationMessages = request.Conversation.GetMessagesForRequest(
            maxTokens: request.SlidingWindowMaxTokens,
            useSlidingWindow: request.UseSlidingWindow);

        contents.AddRange(conversationMessages.Select(m => m.ToGeminiContent()));

        _logger.LogDebug(
            "Built {TotalCount} contents for Gemini: system={HasSystem}, conversation={ConvCount}",
            contents.Count,
            !string.IsNullOrWhiteSpace(request.SystemPrompt),
            conversationMessages.Count);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Full Gemini Request Content:\n{ContentJson}", Newtonsoft.Json.JsonConvert.SerializeObject(contents, Newtonsoft.Json.Formatting.Indented));
        }

        return contents;
    }

    /// <summary>
    /// Option names this service understands, spelled as the Gemini API spells them so a role's
    /// Inference.Options can be copied straight from Google's docs.
    /// </summary>
    private static class GeminiOptionKeys
    {
        public const string TopK = "TopK";
        public const string ThinkingConfig = "ThinkingConfig";
        public const string IncludeThoughts = "IncludeThoughts";
        public const string ThinkingBudget = "ThinkingBudget";
        public const string EnableThinking = "EnableThinking";

        /// <summary>Top-level keys consumed either by this service or by the framework facade.</summary>
        public static readonly HashSet<string> Recognized = new(StringComparer.OrdinalIgnoreCase)
        {
            // Gemini-specific
            TopK, ThinkingConfig, EnableThinking, ThinkingBudget,
            // Framework facade properties, which persist into the same Options bag
            "Temperature", "TopP", "MaxTokens", "UseStreaming", "JsonSupport", "ToolSupport", "Stop"
        };

        /// <summary>Keys accepted inside a nested ThinkingConfig object.</summary>
        public static readonly HashSet<string> RecognizedThinking = new(StringComparer.OrdinalIgnoreCase)
        {
            IncludeThoughts, ThinkingBudget
        };
    }

    /// <summary>
    /// Fallback when a role says nothing about thinking: infer it from the model family.
    /// </summary>
    private static bool ModelImpliesThinking(string model) =>
        model.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("gemini-2.0-flash", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("gemini-2.5-flash", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Logs a warning for every Inference.Options key this service will silently ignore.
    /// Misspelled or misplaced options are otherwise invisible: the call succeeds and the setting
    /// simply never takes effect.
    /// </summary>
    private void WarnOnUnrecognizedOptions(LlmRequest request)
    {
        foreach (var property in request.Options.Properties())
        {
            if (!GeminiOptionKeys.Recognized.Contains(property.Name))
            {
                _logger.LogWarning(
                    "Gemini: ignoring unrecognized inference option '{Option}'. Recognized options: {Recognized}",
                    property.Name, string.Join(", ", GeminiOptionKeys.Recognized));
                continue;
            }

            if (!string.Equals(property.Name, GeminiOptionKeys.ThinkingConfig, StringComparison.OrdinalIgnoreCase)
                || property.Value is not JObject nested)
            {
                continue;
            }

            foreach (var thinkingProperty in nested.Properties())
            {
                if (!GeminiOptionKeys.RecognizedThinking.Contains(thinkingProperty.Name))
                {
                    _logger.LogWarning(
                        "Gemini: ignoring unrecognized ThinkingConfig option '{Option}'. Recognized options: {Recognized}",
                        thinkingProperty.Name, string.Join(", ", GeminiOptionKeys.RecognizedThinking));
                }
            }
        }
    }

    private GenerateContentConfig BuildGenerateContentConfig(LlmRequest request)
    {
        var config = new GenerateContentConfig();
        WarnOnUnrecognizedOptions(request);

        // System instruction
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            config.SystemInstruction = new Content
            {
                Parts = [new Part { Text = request.SystemPrompt }]
            };
        }

        // Generation parameters
        if (request.Temperature.HasValue)
        {
            config.Temperature = request.Temperature.Value;
        }

        if (request.MaxTokens.HasValue)
        {
            config.MaxOutputTokens = request.MaxTokens.Value;
        }

        if (request.TopP.HasValue)
        {
            config.TopP = request.TopP.Value;
        }

        // TopK is Gemini-specific, so it travels in Options rather than as a typed property.
        var topK = request.GetOption<int?>(GeminiOptionKeys.TopK);
        if (topK.HasValue)
        {
            config.TopK = topK.Value;
        }

        if (request.Stop != null && request.Stop.Count > 0)
        {
            config.StopSequences = [.. request.Stop];
        }

        // Response format
        if (request.ResponseFormat != null && request.ResponseFormat.Type == ResponseFormatType.JsonObject)
        {
            config.ResponseMimeType = "application/json";

            if (request.ResponseFormat.JsonSchema != null)
            {
                config.ResponseSchema = BuildSchema(request.ResponseFormat.JsonSchema);
            }
        }

        // Tools (function declarations)
        if (request.Tools != null && request.Tools.Count > 0)
        {
            config.Tools = request.Tools.Select(t => t.ToGeminiTool()).ToList();
        }

        // Context Caching
        // 1. Check if provided in metadata (preferred for conversation-level persistence)
        if (request.Conversation.Metadata.TryGetValue("Gemini.CachedContentName", out var cacheNameObj) &&
            cacheNameObj is string cacheName && !string.IsNullOrEmpty(cacheName))
        {
            config.CachedContent = cacheName;
        }

        // Thinking Configuration
        // Determine if thinking should be enabled based on hierarchy:
        // 1. Explicit request/profile configuration (Standard Framework way)
        // 2. Metadata (Legacy/Provider-specific way)
        // 3. Heuristic based on model name (Fallback for ease of use)

        // Canonical form mirrors the Gemini API itself: Options.ThinkingConfig.IncludeThoughts.
        // EnableThinking stays supported as a flat alias, but the nested form wins.
        JObject? thinkingOptions = request.GetOption<JObject>(GeminiOptionKeys.ThinkingConfig);
        bool? includeThoughtsOption =
            thinkingOptions?.GetValue(GeminiOptionKeys.IncludeThoughts, StringComparison.OrdinalIgnoreCase)?.ToObject<bool?>()
            ?? request.GetOption<bool?>(GeminiOptionKeys.EnableThinking);

        bool enableThinking = includeThoughtsOption ?? ModelImpliesThinking(request.Model);

        // Metadata override
        if (request.Conversation.Metadata.TryGetValue("Gemini.IncludeThoughts", out var includeThoughtsObj) &&
            includeThoughtsObj is bool includeThoughtsOverride)
        {
            enableThinking = includeThoughtsOverride;
        }

        if (enableThinking)
        {
            var thinkingConfig = new ThinkingConfig
            {
                IncludeThoughts = true
            };

            // Budget hierarchy: nested ThinkingConfig -> flat alias -> Metadata
            int? budget =
                thinkingOptions?.GetValue(GeminiOptionKeys.ThinkingBudget, StringComparison.OrdinalIgnoreCase)?.ToObject<int?>()
                ?? request.GetOption<int?>(GeminiOptionKeys.ThinkingBudget);

            if (request.Conversation.Metadata.TryGetValue("Gemini.ThinkingBudget", out var budgetObj) &&
                budgetObj is int budgetOverride)
            {
                budget = budgetOverride;
            }

            if (budget.HasValue)
            {
                thinkingConfig.ThinkingBudget = budget.Value;
            }

            config.ThinkingConfig = thinkingConfig;
        }

        _logger.LogDebug(
            "Gemini thinking resolved: enabled={Enabled}, budget={Budget}, source={Source}, options=[{Options}]",
            enableThinking,
            config.ThinkingConfig?.ThinkingBudget,
            includeThoughtsOption.HasValue ? "explicit" : "model-heuristic",
            string.Join(", ", request.Options.Properties().Select(x => x.Name)));

        // Google Search Grounding (via Metadata)
        if (request.Conversation.Metadata.TryGetValue("Gemini.EnableGoogleSearch", out var searchObj) &&
            searchObj is bool enableSearch && enableSearch)
        {
            config.Tools ??= [];
            // Check if GoogleSearch tool already exists to avoid duplicates (unlikely but safe)
            if (!config.Tools.Any(t => t.GoogleSearch != null))
            {
                config.Tools.Add(new Tool { GoogleSearch = new GoogleSearch() });
            }
        }

        // Choice Count
        config.CandidateCount = request.GetOption<int?>("ChoiceCount");

        return config;
    }

    private static Schema? BuildSchema(JsonSchema jsonSchema)
    {
        // Reuses the same JSON-Schema -> Gemini Schema walk as tool parameter conversion
        // (GeminiTypeConverters.ConvertJsonSchemaToGeminiSchema), instead of emitting a bare
        // {Type: OBJECT} with no properties/required - which left the model unconstrained and
        // free to omit required fields (e.g. an empty SelectedOption).
        var schemaObj = JObject.Parse(jsonSchema.ToJson());
        return GeminiTypeConverters.ConvertJsonSchemaToGeminiSchema(schemaObj);
    }

    private static string ExtractTextContent(Candidate? candidate)
    {
        if (candidate?.Content?.Parts == null)
        {
            return string.Empty;
        }

        // Thinking parts (Part.Thought == true) go through ExtractReasoningContent instead;
        // mixing them in here corrupted structured-output parsing, since the model's thinking
        // prose would land ahead of (or instead of) the actual JSON answer.
        return string.Concat(
            candidate.Content.Parts
                .Where(p => p.Text != null && p.Thought != true)
                .Select(p => p.Text));
    }

    private static string? ExtractReasoningContent(Candidate? candidate)
    {
        if (candidate?.Content?.Parts == null)
        {
            return null;
        }

        var reasoning = string.Concat(
            candidate.Content.Parts
                .Where(p => p.Text != null && p.Thought == true)
                .Select(p => p.Text));

        return string.IsNullOrEmpty(reasoning) ? null : reasoning;
    }

    private List<ToolCall>? ExtractToolCalls(Candidate? candidate)
    {
        if (candidate?.Content?.Parts == null)
        {
            return null;
        }

        var functionCalls = candidate.Content.Parts
            .Where(p => p.FunctionCall != null)
            .Select(p => p.FunctionCall!)
            .ToList();

        if (functionCalls.Count == 0)
        {
            return null;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var fc in functionCalls)
            {
                // Log argument structure to help debug parsing issues
                // Sanitize args first to avoid "ValueKind" artifacts from JsonElement
                var sanitizedArgs = GeminiTypeConverters.SanitizeArgsDictionary(fc.Args!);
                _logger.LogDebug("Gemini Tool Call: {Name}, Args: {Args}", fc.Name, JsonConvert.SerializeObject(sanitizedArgs));
            }
        }

        return functionCalls.Select(fc => fc.ToFrameworkToolCall()).ToList();
    }

    private IEnumerable<LlmStreamChunk> ProcessStreamChunk(GenerateContentResponse response)
    {
        if (response.Candidates == null) yield break;

        var usageMetadata = response.UsageMetadata;

        foreach (var candidate in response.Candidates)
        {
            if (candidate.Content?.Parts == null) continue;

            var isComplete = candidate.FinishReason.HasValue;
            var choiceIndex = candidate.Index ?? 0;
            var emittedAnyPart = false;

            // Built once per candidate, in the same Part order iterated below, so each entry can
            // be matched to the single Part it describes instead of being attached to every text
            // chunk of the candidate (which used to make the caller's argument accumulator
            // concatenate the same tool-call JSON once per text chunk).
            var toolCallUpdates = ExtractToolCallUpdates(candidate);
            var toolCallUpdateIndex = 0;

            // Process parts individually to separate Thinking from Text
            foreach (var part in candidate.Content.Parts)
            {
                // Thinking status, useful when diagnosing a candidate that mixes thinking, text,
                // and function calls.
                _logger.LogDebug("Gemini Part: IsThinking={IsThinking}, HasThoughtProp={HasProperty}, Snippet={Snippet}...",
                   part.Thought ?? false,
                   part.Thought.HasValue,
                   part.Text?.Substring(0, Math.Min(part.Text?.Length ?? 0, 40)).Replace("\n", "\\n"));

                if (!string.IsNullOrEmpty(part.Text))
                {
                    yield return new LlmStreamChunk
                    {
                        Delta = part.Text,
                        IsThinking = part.Thought ?? false,
                        IsComplete = isComplete,
                        FinishReason = MapFinishReason(candidate.FinishReason),
                        RawFinishReason = candidate.FinishReason?.ToString(),
                        TokensUsed = usageMetadata?.TotalTokenCount,
                        PromptTokens = usageMetadata?.PromptTokenCount,
                        CompletionTokens = usageMetadata?.CandidatesTokenCount,
                        ChoiceIndex = choiceIndex,
                        NativeChunk = response
                    };
                    emittedAnyPart = true;
                }
                else if (part.FunctionCall != null && toolCallUpdates != null && toolCallUpdateIndex < toolCallUpdates.Count)
                {
                    yield return new LlmStreamChunk
                    {
                        Delta = string.Empty,
                        IsThinking = false,
                        IsComplete = isComplete,
                        FinishReason = MapFinishReason(candidate.FinishReason),
                        RawFinishReason = candidate.FinishReason?.ToString(),
                        TokensUsed = usageMetadata?.TotalTokenCount,
                        PromptTokens = usageMetadata?.PromptTokenCount,
                        CompletionTokens = usageMetadata?.CandidatesTokenCount,
                        ToolCallUpdates = [toolCallUpdates[toolCallUpdateIndex]],
                        ChoiceIndex = choiceIndex,
                        NativeChunk = response
                    };
                    toolCallUpdateIndex++;
                    emittedAnyPart = true;
                }
            }

            // Defensive fallback: should not trigger in practice, since every FunctionCall part is
            // now handled above, but avoids silently dropping tool calls if a future SDK shape ever
            // has Parts that ExtractToolCallUpdates counts but the loop above didn't emit.
            if (!emittedAnyPart && toolCallUpdates is { Count: > 0 })
            {
                yield return new LlmStreamChunk
                {
                    Delta = string.Empty,
                    IsThinking = false,
                    IsComplete = isComplete,
                    FinishReason = MapFinishReason(candidate.FinishReason),
                    RawFinishReason = candidate.FinishReason?.ToString(),
                    TokensUsed = usageMetadata?.TotalTokenCount,
                    PromptTokens = usageMetadata?.PromptTokenCount,
                    CompletionTokens = usageMetadata?.CandidatesTokenCount,
                    ToolCallUpdates = toolCallUpdates,
                    ChoiceIndex = choiceIndex,
                    NativeChunk = response
                };
            }
        }
    }

    private static LlmFinishReason? MapFinishReason(Google.GenAI.Types.FinishReason? reason)
    {
        if (reason == null) return null;

        return reason switch
        {
            Google.GenAI.Types.FinishReason.STOP => LlmFinishReason.Stop,
            Google.GenAI.Types.FinishReason.MAX_TOKENS => LlmFinishReason.Length,
            Google.GenAI.Types.FinishReason.SAFETY => LlmFinishReason.ContentFilter,
            Google.GenAI.Types.FinishReason.RECITATION => LlmFinishReason.ContentFilter,
            Google.GenAI.Types.FinishReason.MALFORMED_FUNCTION_CALL => LlmFinishReason.Other,
            Google.GenAI.Types.FinishReason.UNEXPECTED_TOOL_CALL => LlmFinishReason.Other,
            _ => LlmFinishReason.Other
        };
    }

    private static List<ToolCallUpdate>? ExtractToolCallUpdates(Candidate candidate)
    {
        if (candidate.Content?.Parts == null)
        {
            return null;
        }

        var functionCalls = candidate.Content.Parts
            .Where(p => p.FunctionCall != null)
            .Select((p, index) => new ToolCallUpdate
            {
                Index = index,
                ToolCallId = $"call_{index}",
                FunctionName = p.FunctionCall!.Name,
                // Args is a Dictionary: ToString() yields the CLR type name, not JSON. Reuse the
                // same conversion as the non-streaming path so the args round-trip as JSON.
                FunctionArgumentsUpdate = p.FunctionCall.ToFrameworkToolCall().Arguments,
                // Keep the whole Part, not just the FunctionCall: Gemini 3 requires the
                // thoughtSignature to be echoed back and it lives on the Part, not the call.
                Native = p.ToNativeToolCallObject()
            })
            .ToList();

        return functionCalls.Count > 0 ? functionCalls : null;
    }

    // Cost used to be computed here from the request profile. Pricing now lives on LlmProvider and
    // is applied by BaseLlmStep once the call returns, so every provider is priced the same way.
}
