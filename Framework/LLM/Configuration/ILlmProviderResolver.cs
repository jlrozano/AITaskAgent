namespace AITaskAgent.LLM.Configuration;

/// <summary>
/// Service for resolving LLM configurations by provider name.
/// </summary>
public interface ILlmProviderResolver
{
    /// <summary>
    /// Gets an LLM provider by name.
    /// </summary>
    /// <param name="providerName">Provider name. If null, uses the default provider.</param>
    /// <returns>Provider configuration with environment variables expanded.</returns>
    /// <exception cref="InvalidOperationException">If the provider does not exist.</exception>
    LlmProviderConfig GetProvider(string? providerName = null);

    /// <summary>
    /// Checks if a provider with the specified name exists.
    /// </summary>
    /// <param name="providerName">Provider name.</param>
    /// <returns>True if the provider exists, false otherwise.</returns>
    bool ProviderExists(string providerName);

    /// <summary>
    /// Gets all available provider names.
    /// </summary>
    IEnumerable<string> GetAvailableProviders();
}
