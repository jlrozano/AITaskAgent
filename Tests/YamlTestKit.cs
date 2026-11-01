using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using AITaskAgent.YAML.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace AITaskAgent.Tests;

/// <summary>
/// Test double for an LLM: answers every call with <see cref="Respond"/> and records what each call
/// received (system prompt and last user message, captured at call time).
/// </summary>
internal sealed class ScriptedLlmService(Func<int, string> respond) : ILlmService
{
    private int _calls;

    public Func<int, string> Respond { get; } = respond;

    public ConcurrentQueue<(string? System, string User)> Requests { get; } = new();

    public int Calls => _calls;

    public string Name => "scripted";

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _calls);
        string user = request.Conversation.History.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        Requests.Enqueue((request.SystemPrompt, user));
        return Task.FromResult(LlmResult.Success([new LlmResponse { Content = Respond(call), FinishReason = FinishReason.Stop }]));
    }

    public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 8192;
}

/// <summary>
/// Non-LLM step for YAML tests: appends <see cref="Label"/> to the <c>trace</c> list in the context
/// metadata and returns either its input (pass-through) or <see cref="Output"/> parsed as JSON.
/// </summary>
[StepTag("Probe")]
public sealed class ProbeStep : StepBase
{
    public ProbeStep() : base("Probe", typeof(IStepResult), typeof(IStepResult))
    {
        MaxRetries = 1;
    }

    public string Label { get; set; } = "";

    public string? Output { get; set; }

    public int Weight { get; set; }

    protected override Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        var trace = (ConcurrentQueue<string>)context.Metadata.GetOrAdd("trace", _ => new ConcurrentQueue<string>())!;
        trace.Enqueue(Label);
        return Task.FromResult<IStepResult>(Output == null ? input : new JsonStepResult(this, JToken.Parse(Output)));
    }

    public static string[] Trace(PipelineContext context)
        => context.Metadata.TryGetValue("trace", out object? value) && value is ConcurrentQueue<string> queue ? [.. queue] : [];
}

/// <summary>Builds a YAML factory over the core steps, <see cref="YamlLlmStep"/> and <see cref="ProbeStep"/>.</summary>
internal sealed class YamlTestKit
{
    public YamlTestKit(Func<int, string>? respond = null)
    {
        Llm = new ScriptedLlmService(respond ?? (_ => "ok"));

        LlmEnvironment environment = new() { DefaultRole = "default" };
        environment.Roles["default"] = new LlmRole { Provider = new LlmProvider { Service = Llm }, Model = "test-model" };

        Registry = new StepRegistry();
        Registry.AddFromAssembly(typeof(StepRegistry).Assembly);
        Registry.Add(typeof(YamlLlmStep));
        Registry.Add(typeof(ProbeStep));

        Services = new ServiceCollection()
            .AddSingleton<IOptions<LlmEnvironment>>(Options.Create(environment))
            .BuildServiceProvider();

        Factory = new YamlPipelineFactory(Services, Registry, Schemas, new Pipeline(loggerFactory: NullLoggerFactory.Instance));
    }

    public ScriptedLlmService Llm { get; }

    public StepRegistry Registry { get; }

    public SchemaRegistry Schemas { get; } = new();

    public IServiceProvider Services { get; }

    public YamlPipelineFactory Factory { get; }

    public static LlmPipelineContext NewContext() => new();
}
