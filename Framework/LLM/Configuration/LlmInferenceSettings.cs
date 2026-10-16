using Newtonsoft.Json.Linq;

namespace AITaskAgent.LLM.Configuration;

/// <summary>
/// Configuration for LLM inference.
/// This class acts as a strongly-typed facade over ProviderOptions.
/// Properties here are those actively used by the framework logic.
/// </summary>
public record LlmInferenceSettings
{


    public LlmInferenceSettings() { }

    protected LlmInferenceSettings(LlmInferenceSettings other)
    {
        _options = (JObject)other._options.DeepClone();
    }

    private readonly JObject _options = [];

    /// <summary>
    /// Raw provider-specific options. This is the single source of truth.
    /// </summary>
    /// <remarks>
    /// Assigning it merges the given keys into the current ones (a given key wins, arrays are replaced) instead of
    /// replacing the object: the facade properties below live here too, so a replacement would wipe whichever of them
    /// were set before it - in an object initializer, or when a configuration binder happens to hand over
    /// <c>MaxTokens</c> before <c>Options</c>.
    /// </remarks>
    public JObject Options
    {
        get => _options;
        init
        {
            if (value != null)
            {
                _options.Merge(value, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            }
        }
    }

    // --- Framework Standard Properties (Facades to ProviderOptions) ---

    public float? Temperature
    {
        get => Get<float?>(nameof(Temperature));
        init => Set(nameof(Temperature), value);
    }

    public float? TopP
    {
        get => Get<float?>(nameof(TopP));
        init => Set(nameof(TopP), value);
    }

    public int? MaxTokens
    {
        get => Get<int?>(nameof(MaxTokens));
        init => Set(nameof(MaxTokens), value);
    }

    public bool UseStreaming
    {
        get => Get<bool?>(nameof(UseStreaming)) ?? false;
        init => Set(nameof(UseStreaming), value);
    }

    public bool JsonSupport
    {
        get => Get<bool?>(nameof(JsonSupport)) ?? false;
        init => Set(nameof(JsonSupport), value);
    }

    public bool ToolSupport
    {
        get => Get<bool?>(nameof(ToolSupport)) ?? true;
        init => Set(nameof(ToolSupport), value);
    }

    public IList<string>? Stop
    {
        get => Get<List<string>>(nameof(Stop));
        init => Set(nameof(Stop), value);
    }

    /// <summary>
    /// Some newer reasoning-model families (OpenAI's o1/o3 series, GPT-5) reject the chat-
    /// completions body's traditional <c>max_tokens</c> field with HTTP 400 and require
    /// <c>max_completion_tokens</c> instead. This is tied to which *model* is being called, not
    /// which vendor serves it - the same model can sit behind OpenAI directly, Azure OpenAI, or an
    /// OpenRouter proxy - so it is a per-role setting rather than something an <c>ILlmService</c>
    /// could infer from its own vendor.
    /// </summary>
    public bool UseMaxCompletionTokens
    {
        get => Get<bool?>(nameof(UseMaxCompletionTokens)) ?? false;
        init => Set(nameof(UseMaxCompletionTokens), value);
    }

    /// <summary>
    /// Per-attempt limit, in seconds, of a non-streaming call: an attempt with no response by then is abandoned and
    /// counts as a retriable failure, so a hung provider costs one attempt instead of the whole step. Null leaves the
    /// <c>ILlmService</c>'s own default; 0 or less disables the limit. A service that cannot enforce it ignores it.
    /// </summary>
    /// <remarks>
    /// Timeouts depend on the model (a reasoning model thinks for minutes) rather than on the pipeline, so they travel
    /// with the provider's or the role's inference settings like the rest of the call's parameters.
    /// </remarks>
    public int? RequestTimeoutSeconds
    {
        get => Get<int?>(nameof(RequestTimeoutSeconds));
        init => Set(nameof(RequestTimeoutSeconds), value);
    }

    /// <summary>
    /// Streaming inactivity limit, in seconds: the longest the service waits for the next line of the stream (the first
    /// one included) before abandoning the call as a retriable failure. It resets with every line, so a long answer that
    /// keeps arriving is never cut. Null leaves the <c>ILlmService</c>'s own default; 0 or less disables the limit.
    /// </summary>
    public int? StreamingInactivityTimeoutSeconds
    {
        get => Get<int?>(nameof(StreamingInactivityTimeoutSeconds));
        init => Set(nameof(StreamingInactivityTimeoutSeconds), value);
    }

    // --- Helper Methods ---

    /// <summary>
    /// Reads a provider-specific option by name, case-insensitively.
    /// </summary>
    /// <remarks>
    /// The typed properties above cover what every provider understands. Anything vendor-specific
    /// - Gemini's TopK or ThinkingConfig, a gateway's routing hints - travels in
    /// <see cref="Options"/>, and this is how an ILlmService implementation reads it without the
    /// framework needing a property per vendor.
    /// </remarks>
    /// <returns>The value, or default when the key is absent.</returns>
    public T? GetOption<T>(string key) => Get<T>(key);

    protected T? Get<T>(string key)
    {
        JToken? token = Options.GetValue(key, StringComparison.OrdinalIgnoreCase);
        return token != null ? token.ToObject<T>() : default;
    }

    protected void Set(string key, object? value)
    {
        // Normalizar: buscar si ya existe la clave ignorando mayúsculas/minúsculas
        JProperty? existingProp = Options.Property(key, StringComparison.OrdinalIgnoreCase);

        if (value == null)
        {
            existingProp?.Remove();
        }
        else
        {
            JToken token = JToken.FromObject(value);
            if (existingProp != null)
            {
                // Si existe, actualizar el valor de la propiedad existente preservando su nombre original
                existingProp.Value = token;
            }
            else
            {
                // Si no existe, añadirla con la clave tal cual se ha pasado
                Options[key] = token;
            }
        }
    }
}