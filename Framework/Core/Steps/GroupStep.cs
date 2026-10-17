using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Non-generic base for sequential step grouping. Not declarable from YAML: the YAML factory only maps
/// <c>IYamlStep</c> types marked with <c>[YamlStepTag]</c>, and this type is only tagged with <c>[StepTag]</c>.
/// Unlike <see cref="ParallelStep"/>, executes steps sequentially in the SAME pipeline context by
/// appending them to <c>NextSteps</c> — the parent Pipeline runs them in order.
/// </summary>
[StepTag("GroupStep")]
public class GroupStep : StepBase
{
    private readonly List<IStep> _steps = [];

    /// <summary>Parameterless ctor for YAML deserialization.</summary>
    public GroupStep() : base("GroupStep", typeof(IStepResult), typeof(IStepResult))
    {
        MaxRetries = 1;
    }

    /// <summary>Programmatic ctor.</summary>
    public GroupStep(string name, IReadOnlyList<IStep> steps)
        : base(name, steps.Count > 0 ? steps[0].InputType : typeof(IStepResult),
               steps.Count > 0 ? steps[^1].OutputType : typeof(IStepResult))
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps.AddRange(steps);
        MaxRetries = 1;
    }

    /// <summary>
    /// Steps to execute sequentially. Set via YAML deserialization or programmatic ctor.
    /// </summary>
    public List<IStep> Steps
    {
        get => _steps;
        init => _steps = value ?? [];
    }

    /// <inheritdoc/>
    protected override Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt,
        IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        input.NextSteps.Clear();
        input.NextSteps.AddRange(_steps);
        return Task.FromResult(input);
    }
}

/// <summary>
/// Typed variant for programmatic use. Constrains input to <typeparamref name="TIn"/>.
/// </summary>
/// <typeparam name="TIn">Input type (also output type — pass-through).</typeparam>
public sealed class GroupStep<TIn> : GroupStep
    where TIn : IStepResult
{
    public GroupStep(string name, IReadOnlyList<IStep> steps) : base(name, steps)
    {
        InputType = typeof(TIn);
        if (_steps.Count > 0 && !_steps[0].InputType.IsAssignableFrom(typeof(TIn)))
        {
            throw new ArgumentException(
                $"First step '{_steps[0].Name}' expects input type '{_steps[0].InputType.Name}' " +
                $"but GroupStep input is '{typeof(TIn).Name}'.");
        }
    }

    // Access to _steps from parent — hold a local alias via Steps property
    private List<IStep> _steps => base.Steps;
}
