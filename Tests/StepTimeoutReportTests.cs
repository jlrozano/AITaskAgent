using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// A step that exceeds its limit is reported as a timeout, whichever way it reacts to the cancellation: by letting the
/// exception out (RetryMiddleware turns it into an error result carrying an OperationCanceledException, which used to be
/// the whole report: «The operation was canceled.»), or by catching it and returning its own error.
/// </summary>
public class StepTimeoutReportTests
{
    public enum Reaction { Throw, ReturnError }

    private sealed class SlowStep(Reaction reaction, TimeSpan limit) : IStep
    {
        public string Name => "Slow";
        public IResume? Resume => null;
        public int MaxRetries => 3;
        public int MiillisecondsBetweenRetries => 0;
        public TimeSpan? Timeout { get; } = limit;
        public Type InputType => typeof(IStepResult);
        public Type OutputType => typeof(StepResult<string>);

        public async Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException) when (reaction == Reaction.ReturnError)
            {
                return ErrorStepResult.FromMessage(this, "the execution was cancelled.");
            }

            return new StepResult<string>(this, "late");
        }
    }

    private static StepResult<string> Seed() => new(new EmptyStep("Seed"), "seed");

    [Theory]
    [InlineData(Reaction.Throw)]
    [InlineData(Reaction.ReturnError)]
    public async Task An_expired_step_reports_a_timeout_with_its_name_and_limit(Reaction reaction)
    {
        var result = await new Pipeline().ExecuteAsync("Test", [new SlowStep(reaction, TimeSpan.FromMilliseconds(300))], Seed(), new PipelineContext());

        Assert.True(result.HasError);
        Assert.Equal("Step Slow exceeded its limit of 0 s", result.Error!.Message);
        Assert.IsType<TimeoutException>(result.Error.OriginalException);
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_is_not_reported_as_a_timeout()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await new Pipeline().ExecuteAsync(
            "Test", [new SlowStep(Reaction.Throw, TimeSpan.FromMinutes(5))], Seed(), new PipelineContext(), cancellationToken: cancel.Token);

        Assert.True(result.HasError);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error!.OriginalException);
        Assert.DoesNotContain("exceeded its limit", result.Error.Message, StringComparison.Ordinal);
    }
}
