using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.StepResults;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// A special result indicating that an enclosing loop should break immediately.
/// </summary>
public class BreakStepResult : StepResult<object>
{
    public BreakStepResult(IStep step, object? value = null) : base(step, value)
    {
    }
}

/// <summary>
/// A control-flow step that instructs an enclosing loop to stop iterating.
/// By passing the result of a previous step as input, it can tunnel data out of the loop.
/// </summary>
[StepTag("BreakStep")]
public class BreakStep : StepBase
{
    public BreakStep() : base("Break", typeof(IStepResult), typeof(BreakStepResult))
    {
    }

    public BreakStep(string name = "Break") : base(name, typeof(IStepResult), typeof(BreakStepResult))
    {
    }

    protected override Task<IStepResult> ExecuteAsync(IStepResult input, Models.PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        // Pass the input's value through the break result so data isn't lost
        return Task.FromResult<IStepResult>(new BreakStepResult(this, input.Value));
    }
}
