using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.YAML.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace AITaskAgent.Tests;

/// <summary>
/// Non-LLM step that counts its executions in a process-wide table under <see cref="Key"/> (so the
/// count survives a "restart": a new service provider) and returns its input, or
/// <see cref="Output"/> parsed as JSON.
/// </summary>
[StepTag("Counter")]
public sealed class CountingStep : StepBase
{
    private static readonly ConcurrentDictionary<string, int> Counts = new();

    public CountingStep() : base("Counter", typeof(IStepResult), typeof(IStepResult))
    {
        MaxRetries = 1;
    }

    public string Key { get; set; } = "";

    public string? Output { get; set; }

    public static int Count(string key) => Counts.TryGetValue(key, out int count) ? count : 0;

    protected override Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        Counts.AddOrUpdate(Key, 1, (_, n) => n + 1);
        return Task.FromResult<IStepResult>(Output == null ? input : new JsonStepResult(this, JToken.Parse(Output)));
    }
}

/// <summary>LLM double that records, per call, the whole conversation it was sent.</summary>
internal sealed class RecordingLlmService(Func<int, string>? respond = null) : ILlmService
{
    private int _calls;

    public ConcurrentQueue<List<string>> Conversations { get; } = new();

    public int Calls => _calls;

    public string Name => "recording";

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _calls);
        Conversations.Enqueue([.. request.Conversation.History.Messages.Select(m => $"{m.Role}:{m.Content}")]);
        string content = respond?.Invoke(call) ?? $"answer {call}";
        return Task.FromResult(LlmResult.Success([new LlmResponse { Content = content, FinishReason = FinishReason.Stop }]));
    }

    public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 8192;
}

/// <summary>
/// A whole application container as a host builds it (<c>AddAITaskAgent()</c> +
/// <c>AddAITaskAgentYaml()</c> over an in-memory configuration), with <see cref="CountingStep"/>
/// and a <see cref="RecordingLlmService"/> as the only LLM provider. Building two of them over the
/// same storage folder simulates a process restart.
/// </summary>
internal static class HostKit
{
    public static ServiceProvider Build(
        RecordingLlmService llm,
        Dictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configure = null)
    {
        Dictionary<string, string?> values = new()
        {
            ["AITaskAgent:LlmProviders:Providers:P1:BaseUrl"] = "https://example.test",
            ["AITaskAgent:LlmProviders:Providers:P1:ServiceType"] = "recording",
            ["AITaskAgent:LlmProviders:Roles:default:Provider"] = "P1",
            ["AITaskAgent:LlmProviders:Roles:default:Model"] = "test-model"
        };
        foreach ((string key, string? value) in settings ?? [])
        {
            values[key] = value;
        }

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILlmService>(llm);
        services.AddAITaskAgent();
        services.AddAITaskAgentYaml();
        services.GetOrAddStepRegistry().Add(typeof(CountingStep));
        configure?.Invoke(services);

        ServiceProvider provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        return provider;
    }
}
