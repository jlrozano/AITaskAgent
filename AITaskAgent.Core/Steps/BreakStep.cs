using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Support.Template;

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
/// With <see cref="When"/> it breaks only when that condition holds and otherwise passes its input
/// through unchanged, which is how a declarative (YAML) loop expresses its exit condition.
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

    /// <summary>
    /// Optional condition, a <see cref="TemplateExpression"/> over <c>input</c> and <c>context</c>
    /// (for instance <c>input.done</c> or <c>{{ context.status }}</c>). When set, the step breaks only
    /// if it renders a truthy value (see <see cref="TemplateExpression.IsTruthy"/>); otherwise it
    /// returns its input unchanged and the loop goes on. Null breaks unconditionally. An empty or
    /// blank condition is rejected when it is set (in YAML, <c>when: ""</c> fails when the pipeline is
    /// built): it has no lookup to evaluate and used to break on the first pass by accident.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty or whitespace.</exception>
    public string? When
    {
        get => _when;
        set
        {
            if (value != null && string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "'when' is empty: write a condition such as 'input.done', or remove 'when' to break unconditionally.",
                    nameof(When));
            }

            _when = value;
        }
    }

    private string? _when;

    protected override Task<IStepResult> ExecuteAsync(IStepResult input, Models.PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        if (When != null && !TemplateExpression.IsTruthy(TemplateExpression.Evaluate(When, input, context)))
        {
            return Task.FromResult(input);
        }

        // Pass the input's value through the break result so data isn't lost
        return Task.FromResult<IStepResult>(new BreakStepResult(this, input.Value));
    }
}
