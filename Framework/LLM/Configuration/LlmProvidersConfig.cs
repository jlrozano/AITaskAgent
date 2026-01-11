namespace AITaskAgent.LLM.Configuration;

/// <summary>
/// LLM profiles configuration.
/// Contains all available profiles and the default profile.
/// </summary>
public sealed class LlmProvidersConfig
{
    /// <summary>
    /// Available LLM profiles.
    /// Key = profile name, Value = profile configuration.
    /// </summary>
    public Dictionary<string, LlmProviderConfig> Providers { get; init; } = [];

    /// <summary>
    /// Default profile name.
    /// Used when no profile is explicitly specified.
    /// </summary>
    public string DefaultProvider { get; init; } = "default";
}
