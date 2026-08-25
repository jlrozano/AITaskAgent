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
        Options = (JObject)other.Options.DeepClone();
    }

    /// <summary>
    /// Raw provider-specific options. This is the single source of truth.
    /// </summary>
    public JObject Options { get; init; } = [];

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

    // --- Helper Methods ---

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