using AITaskAgent.Configuration;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AITaskAgent.Tests;

/// <summary>
/// ConfigureLlmEnvironmentOptions read "DefaultRole" into the private LlmEnvironmentRaw model but
/// never copied it onto the public LlmEnvironment it builds, so a configured DefaultRole other than
/// the hardcoded "default" was silently ignored - every sample happened to name a role "default"
/// too, which is the only reason nobody had noticed.
/// </summary>
public sealed class LlmEnvironmentDefaultRoleTests
{
    private sealed class StubService : ILlmService
    {
        public string Name => "stub";
        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(LlmResult.Success([]));
        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<LlmStreamChunk>();
        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static LlmEnvironment Build(string defaultRoleValue)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AITaskAgent:LlmProviders:Providers:P1:BaseUrl"] = "https://example.test",
                ["AITaskAgent:LlmProviders:Providers:P1:ServiceType"] = "stub",
                ["AITaskAgent:LlmProviders:Roles:r1:Provider"] = "P1",
                ["AITaskAgent:LlmProviders:Roles:r1:Model"] = "test-model",
                ["AITaskAgent:LlmProviders:DefaultRole"] = defaultRoleValue
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ILlmService, StubService>();
        services.AddAITaskAgent();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<LlmEnvironment>>().Value;
    }

    [Fact]
    public void A_configured_DefaultRole_other_than_the_hardcoded_name_is_honored()
    {
        Assert.Equal("r1", Build(defaultRoleValue: "r1").DefaultRole);
    }

    [Fact]
    public void DefaultRole_falls_back_to_default_when_config_omits_it()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AITaskAgent:LlmProviders:Providers:P1:BaseUrl"] = "https://example.test",
                ["AITaskAgent:LlmProviders:Providers:P1:ServiceType"] = "stub",
                ["AITaskAgent:LlmProviders:Roles:default:Provider"] = "P1",
                ["AITaskAgent:LlmProviders:Roles:default:Model"] = "test-model"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ILlmService, StubService>();
        services.AddAITaskAgent();

        using var provider = services.BuildServiceProvider();
        var env = provider.GetRequiredService<IOptions<LlmEnvironment>>().Value;

        Assert.Equal("default", env.DefaultRole);
    }
}
