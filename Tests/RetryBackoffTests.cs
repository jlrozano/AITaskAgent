using System.Diagnostics;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// IStep.MiillisecondsBetweenRetries (typo kept on purpose, see its remarks) was declared and
/// documented but never read: RetryMiddleware retried immediately. It now waits before each retry,
/// never before the first attempt, never after a fatal exception, and the wait honours cancellation.
/// </summary>
public class RetryBackoffTests
{
    /// <summary>
    /// Implements IStep directly, as the steps that declare the delay do: StepBase does not expose it.
    /// Fails without an exception (a recoverable error) for the first <c>failures</c> attempts.
    /// </summary>
    private sealed class FlakyStep(int delayMs, int failures, bool throwInstead = false) : IStep
    {
        public int Executions { get; private set; }
        public string Name => "Flaky";
        public IResume? Resume => null;
        public int MaxRetries => 3;
        public int MiillisecondsBetweenRetries => delayMs;
        public Type InputType => typeof(IStepResult);
        public Type OutputType => typeof(StepResult<string>);

        public Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            Executions++;
            if (throwInstead)
            {
                throw new InvalidOperationException("fatal");
            }

            return Task.FromResult<IStepResult>(attempt <= failures
                ? ErrorStepResult.FromMessage(this, "recoverable")
                : new StepResult<string>(this, "ok"));
        }
    }

    private static StepResult<string> Seed() => new(new EmptyStep("Seed"), "seed");

    [Fact]
    public async Task Waits_the_declared_delay_before_a_retry()
    {
        var step = new FlakyStep(delayMs: 300, failures: 1);

        var stopwatch = Stopwatch.StartNew();
        var result = await new Pipeline().ExecuteAsync("Test", [step], Seed(), new PipelineContext());
        stopwatch.Stop();

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, step.Executions);
        // Timer resolution on Windows is ~15 ms; Task.Delay never returns early by more than that.
        Assert.True(stopwatch.ElapsedMilliseconds >= 280, $"Elapsed {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Does_not_wait_before_the_first_attempt()
    {
        var step = new FlakyStep(delayMs: 5_000, failures: 0);

        var stopwatch = Stopwatch.StartNew();
        var result = await new Pipeline().ExecuteAsync("Test", [step], Seed(), new PipelineContext());
        stopwatch.Stop();

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(1, step.Executions);
        Assert.True(stopwatch.ElapsedMilliseconds < 2_500, $"Elapsed {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Does_not_wait_after_a_fatal_exception()
    {
        var step = new FlakyStep(delayMs: 10_000, failures: 0, throwInstead: true);

        var stopwatch = Stopwatch.StartNew();
        var result = await new Pipeline().ExecuteAsync("Test", [step], Seed(), new PipelineContext());
        stopwatch.Stop();

        Assert.True(result.HasError);
        Assert.Equal(1, step.Executions);
        Assert.True(stopwatch.ElapsedMilliseconds < 5_000, $"Elapsed {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Cancellation_interrupts_the_wait()
    {
        var step = new FlakyStep(delayMs: 10_000, failures: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await new Pipeline().ExecuteAsync(
                "Test", [step], Seed(), new PipelineContext(), cancellationToken: cts.Token);
            // The pipeline may surface the cancellation as an error result instead of throwing.
            Assert.True(result.HasError);
        }
        catch (OperationCanceledException)
        {
            // Equally acceptable: the cancellation propagated.
        }
        stopwatch.Stop();

        // The retry never ran: the wait was cut short instead of running its 10 s.
        Assert.Equal(1, step.Executions);
        Assert.True(stopwatch.ElapsedMilliseconds < 5_000, $"Elapsed {stopwatch.ElapsedMilliseconds} ms");
    }
}
