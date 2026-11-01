using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using AITaskAgent.YAML.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace AITaskAgent.Tests;

/// <summary>Event channel that just records what it is sent.</summary>
internal sealed class CapturingChannel : IEventChannel
{
    public ConcurrentQueue<IProgressEvent> Events { get; } = new();

    public Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default) where TEvent : IProgressEvent
    {
        Events.Enqueue(progressEvent);
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(Func<IProgressEvent, CancellationToken, Task> handler, string? correlationId = null) => throw new NotSupportedException();

    public IEventSubscription CreateSubscription(int capacity = 100, string? correlationId = null) => throw new NotSupportedException();

    public IReadOnlyList<StepValidationEvent> Validations => [.. Events.OfType<StepValidationEvent>()];
}

/// <summary>
/// Plain step for hook tests: fails with a recoverable error for the first <see cref="FailFirst"/> attempts,
/// then returns <see cref="Output"/> as JSON. Counts its executions in the instance (single-run tests only).
/// </summary>
[StepTag("HookProbe")]
public class HookProbeStep : StepBase
{
    public HookProbeStep() : base("HookProbe", typeof(IStepResult), typeof(JsonStepResult))
    {
        MaxRetries = 3;
    }

    public string Output { get; set; } = "{\"v\":1}";

    public int FailFirst { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public int Executions;

    protected override Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Executions);
        return Task.FromResult<IStepResult>(attempt <= FailFirst
            ? ErrorStepResult.FromMessage(this, "recoverable")
            : new JsonStepResult(this, JToken.Parse(Output)));
    }
}

/// <summary>A <see cref="TypedStep{TIn,TOut}"/> to check the result-validator stamping reaches that base class too.</summary>
public sealed class TypedHookProbe() : TypedStep<JsonStepResult, JsonStepResult>("TypedHookProbe")
{
    protected override Task<JsonStepResult> ExecuteAsync(JsonStepResult input, PipelineContext context, int Attempt, JsonStepResult? LastStepResult, CancellationToken cancellationToken)
        => Task.FromResult(new JsonStepResult(this, JToken.Parse("{\"typed\":true}")));
}

/// <summary>Kit with a hook registry, optional extra services and a scripted LLM, over the core steps and the YAML LLM step.</summary>
internal sealed class HookKit
{
    public HookKit(ILlmService? llm = null, Action<IServiceCollection>? configure = null)
    {
        Llm = llm ?? new ScriptedLlmService(_ => "ok");

        LlmEnvironment environment = new() { DefaultRole = "default" };
        environment.Roles["default"] = new LlmRole { Provider = new LlmProvider { Service = Llm }, Model = "test-model" };

        Registry = new StepRegistry();
        Registry.AddFromAssembly(typeof(StepRegistry).Assembly);
        Registry.Add(typeof(YamlLlmStep));
        Registry.Add(typeof(ProbeStep));
        Registry.Add(typeof(HookProbeStep));

        ServiceCollection services = new();
        services.AddSingleton<IOptions<LlmEnvironment>>(Options.Create(environment));
        services.AddSingleton(Hooks);
        services.AddSingleton<IHookRegistry>(Hooks);
        services.AddSingleton<ILoggerFactory>(LoggerFactory);
        configure?.Invoke(services);
        Services = services.BuildServiceProvider();

        Factory = new YamlPipelineFactory(Services, Registry, Schemas, new Pipeline(loggerFactory: NullLoggerFactory.Instance));
    }

    public ILlmService Llm { get; }

    public HookRegistry Hooks { get; } = new();

    public WarningLoggerFactory LoggerFactory { get; } = new();

    public StepRegistry Registry { get; }

    public SchemaRegistry Schemas { get; } = new();

    public IServiceProvider Services { get; }

    public YamlPipelineFactory Factory { get; }

    public Task<IStepResult> RunAsync(string yaml, JToken? input = null, PipelineContext? context = null)
        => Factory.CreateFromYaml(yaml).ExecuteAsync(input ?? new JObject(), context ?? new LlmPipelineContext());

    public InvalidOperationException BuildError(string yaml)
        => Assert.Throws<InvalidOperationException>(() => Factory.CreateFromYaml(yaml));
}

/// <summary>Logger factory whose loggers keep the warnings.</summary>
internal sealed class WarningLoggerFactory : ILoggerFactory
{
    public ConcurrentQueue<string> Warnings { get; } = new();

    public void AddProvider(ILoggerProvider provider) { }

    public ILogger CreateLogger(string categoryName) => new Capture(Warnings);

    public void Dispose() { }

    private sealed class Capture(ConcurrentQueue<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}

/// <summary>
/// LLM double that answers from a script and records, at call time, the last user message and the
/// <c>tool_choice</c> option of every request.
/// </summary>
internal sealed class ToolScriptLlm(Func<int, LlmResponse> respond) : ILlmService
{
    private int _calls;

    public string Name => "tool-script";

    public int Calls => _calls;

    public ConcurrentQueue<(string User, string? ToolChoice, int Tools)> Requests { get; } = new();

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _calls);
        string user = request.Conversation.History.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        Requests.Enqueue((user, request.GetOption<string>("tool_choice"), request.Tools?.Count ?? 0));
        return Task.FromResult(LlmResult.Success([respond(call)]));
    }

    public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 8192;
}

internal sealed class EchoTool : ITool
{
    public string Name => "list_dir";

    public string Description => "test tool";

    public bool IsReadOnly => true;

    public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };

    public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken)
        => Task.FromResult("listing");
}
