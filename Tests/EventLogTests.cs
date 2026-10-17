using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Tests;

/// <summary>
/// The channel no longer knows <see cref="LlmResponseEvent"/>: each event writes its own log line through
/// <see cref="IProgressEvent.WriteLog"/>, so a common event logs the common fields and an LLM response adds its own.
/// </summary>
public class EventLogTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    [Fact]
    public void A_common_event_logs_only_the_common_fields()
    {
        var logger = new CapturingLogger();

        new StepStartedEvent { StepName = "Writer", CorrelationId = "c1" }.WriteLog(logger, LogLevel.Debug);

        Assert.Equal("step.started from Writer | CorrelationId: c1", Assert.Single(logger.Lines));
    }

    [Fact]
    public void An_event_outside_ProgressEventBase_uses_the_interface_default()
    {
        var logger = new CapturingLogger();
        IProgressEvent evt = new StepProgressEvent { StepName = "Writer", CorrelationId = "c1", EventType = "step.progress", Message = "m" };

        evt.WriteLog(logger, LogLevel.Debug);

        Assert.StartsWith("step.progress from Writer", Assert.Single(logger.Lines));
    }

    [Fact]
    public void An_LLM_response_logs_its_own_fields()
    {
        var logger = new CapturingLogger();

        new LlmResponseEvent { StepName = "Writer", CorrelationId = "c1", Content = "hola", TokensUsed = 7 }
            .WriteLog(logger, LogLevel.Debug);

        string line = Assert.Single(logger.Lines);
        Assert.Contains("Content: hola", line);
        Assert.Contains("Tokens: 7", line);
    }
}
