namespace AITaskAgent.LLM.Configuration;

/// <summary>
/// Implementation of the LLM provider resolver.
/// </summary>
public sealed class LlmProviderResolver(LlmProvidersConfig config) : ILlmProviderResolver
{
    private readonly LlmProvidersConfig _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <inheritdoc />
    public LlmProviderConfig GetProvider(string? providerName = null)
    {
        // Validate that at least one provider exists
        if (_config.Providers.Count == 0)
        {
            throw new InvalidOperationException(
                "No LLM providers configured. Please add at least one provider in appsettings.json under AITaskAgent:LlmProviders");
        }

        // Validate that the default provider exists
        if (!_config.Providers.ContainsKey(_config.DefaultProvider))
        {
            throw new InvalidOperationException(
                $"Default LLM provider '{_config.DefaultProvider}' not found in configuration. " +
                $"Available providers: {string.Join(", ", _config.Providers.Keys)}");
        }

        var name = providerName ?? _config.DefaultProvider;

        if (!_config.Providers.TryGetValue(name, out var provider))
        {
            throw new InvalidOperationException(
                $"LLM provider '{name}' not found. Available providers: {string.Join(", ", _config.Providers.Keys)}");
        }

        // Validate provider integrity
        return string.IsNullOrWhiteSpace(provider.BaseUrl)
            ? throw new InvalidOperationException($"LlmProvider '{name}' has empty BaseUrl. Please check configuration.")
            : string.IsNullOrWhiteSpace(provider.ApiKey)
            ? throw new InvalidOperationException($"LlmProvider '{name}' has empty ApiKey.")
            : provider;
    }

    /// <inheritdoc />
    public bool ProviderExists(string providerName)
        => _config.Providers.ContainsKey(providerName);

    /// <inheritdoc />
    public IEnumerable<string> GetAvailableProviders()
        => _config.Providers.Keys;
}
