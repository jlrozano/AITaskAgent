using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Tests;

/// <summary>
/// At Trace each iteration of the tool loop logs what the model wrote in that turn (text, reasoning,
/// requested calls) labelled with the step and the iteration; the Information line about the requested
/// calls gains the turn's reasoning tokens only when the provider reported them.
/// </summary>
public sealed class ToolLoopTurnTraceTests
{
    private sealed class ListDirTool : ITool
    {
        public string Name => "list_dir";
        public string Description => "test tool";
        public bool IsReadOnly => true;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken) =>
            Task.FromResult("ok");
    }

    /// <summary>First turn: text + reasoning + a tool call; second turn: the final answer.</summary>
    private sealed class TwoTurnService(int? reasoningTokens) : ILlmService
    {
        private int _calls;

        public string Name => "two-turn-stub";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(++_calls == 1
                ? LlmResult.Success(
                    [new LlmResponse
                    {
                        Content = "Voy a listar el directorio.",
                        Reasoning = "necesito ver los ficheros",
                        FinishReason = FinishReason.ToolCalls,
                        ToolCalls = [new ToolCall { Id = "c1", Name = "list_dir", Arguments = "{\"path\":\".\"}" }]
                    }],
                    reasoningTokens: reasoningTokens)
                : LlmResult.Success([new LlmResponse { Content = "Listo.", FinishReason = FinishReason.Stop }]));

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("not exercised by this test");

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private sealed class CaptureLoggerFactory(LogLevel minimum) : ILoggerFactory, IServiceProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public object? GetService(Type serviceType) => serviceType == typeof(ILoggerFactory) ? this : null;

        private sealed class CaptureLogger(CaptureLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.Minimum;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) owner.Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private LogLevel Minimum { get; } = minimum;
    }

    private static async Task<CaptureLoggerFactory> RunAsync(LogLevel minimum, int? reasoningTokens)
    {
        var logs = new CaptureLoggerFactory(minimum);
        IStep step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Chat",
            new LlmRole
            {
                Provider = new LlmProvider { Service = new TwoTurnService(reasoningTokens) },
                Model = "test-model",
                Inference = new LlmInferenceSettings { UseStreaming = false }
            },
            templateProvider: null,
            promtTemplateString: "{{Input}}")
        {
            Tools = [new ListDirTool()]
        };

        await step.ExecuteAsync(
            new LlmStringStepResult(new EmptyStep("Seed")) { Content = "hazlo" },
            new PipelineContext(null, logs), attempt: 1, lastStepResult: null, CancellationToken.None);
        return logs;
    }

    [Fact]
    public async Task Trace_logs_the_text_reasoning_and_calls_of_each_turn_labelled_with_step_and_iteration()
    {
        var logs = await RunAsync(LogLevel.Trace, reasoningTokens: 12);

        var turn = Assert.Single(logs.Entries, e => e.Level == LogLevel.Trace && e.Message.Contains("model turn"));
        Assert.Contains("Step Chat iteration 1", turn.Message);
        Assert.Contains("reasoning tokens: 12", turn.Message);
        Assert.Contains("[Content]: Voy a listar el directorio.", turn.Message);
        Assert.Contains("[Reasoning]: necesito ver los ficheros", turn.Message);
        Assert.Contains("list_dir({\"path\":\".\"})", turn.Message);
    }

    [Fact]
    public async Task Information_line_adds_reasoning_tokens_only_when_reported()
    {
        var with = await RunAsync(LogLevel.Information, reasoningTokens: 12);
        var without = await RunAsync(LogLevel.Information, reasoningTokens: null);

        Assert.Contains(with.Entries, e => e.Message == "LLM requested 1 canonical tool calls (iteration 1, reasoning tokens: 12)");
        Assert.Contains(without.Entries, e => e.Message == "LLM requested 1 canonical tool calls (iteration 1)");
    }

    [Fact]
    public async Task Below_trace_the_turn_text_is_not_logged()
    {
        var logs = await RunAsync(LogLevel.Information, reasoningTokens: 12);

        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Trace);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("Voy a listar"));
    }
}
