namespace AITaskAgent.LLM.Configuration;

/// <summary>
/// Root LLM configuration section.
/// Contains connection targets and behavior presets.
/// </summary>
public sealed class LlmEnvironment
{
    /// <summary>
    /// Available connection provider.
    /// Key = target name, Value = connection credentials and provider-specific defaults.
    /// </summary>
    public Dictionary<string, LlmProvider> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-Step behavior configurations.
    /// Key = preset name (or Step name), Value = model + inference parameter overrides.
    /// </summary>
    public Dictionary<string, LlmRole> Roles { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default preset name used when a Step has no matching entry in <see cref="Roles"/>.
    /// </summary>
    public string DefaultRole { get; init; } = "default";

    public List<FallbackProviderSettings> FallbackProviders { get; init; } = [];
}

public class FallbackProviderSettings
{
    /// <summary>
    /// Name of the fallback provider (must match a key in <see cref="LlmEnvironment.Providers"/>).
    /// </summary>
    public string Provider { get; init; } = string.Empty;
    /// <summary>
    /// Model to use for the fallback provider.
    /// Example: "gemini-2.5-flash", "openai/gpt-4o", "qwen/qwen3-32b"
    /// </summary>
    public string Model { get; init; } = string.Empty;
    /// <summary>
    /// Optional inference settings for this fallback.
    /// </summary>
    public LlmInferenceSettings? Inference { get; init; }
}
