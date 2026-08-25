using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Executes a collection of steps in sequence repeatedly up to a maximum number of iterations.
/// Evaluates <see cref="BreakStepResult"/> to exit early.
/// </summary>
[StepTag("LoopStep")]
public class LoopStep : StepBase
{
    private readonly List<IStep> _steps = [];

    public LoopStep() : base("LoopStep", typeof(IStepResult), typeof(IStepResult))
    {
        MaxRetries = 1;
    }

    public LoopStep(string name, IReadOnlyList<IStep> steps, int maxIterations = 50)
        : base(name, typeof(IStepResult), typeof(IStepResult))
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps.AddRange(steps);
        MaxIterations = maxIterations;
        MaxRetries = 1;
    }

    /// <summary>
    /// Steps to execute in each iteration.
    /// </summary>
    public List<IStep> Steps
    {
        get => _steps;
        init => _steps = value ?? [];
    }

    /// <summary>
    /// Maximum number of iterations before ending the loop naturally. Default is 50.
    /// </summary>
    public int MaxIterations { get; init; } = 50;

    /// <summary>
    /// Optional key to store the current 1-based iteration number in PipelineContext.Metadata.
    /// Useful for injecting the loop counter into LLM prompts via templates.
    /// </summary>
    public string? OutputIterationToMetadata { get; init; }

    protected override async Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        IStepResult currentResult = input;

        for (int i = 1; i <= MaxIterations; i++)
        {
            if (!string.IsNullOrWhiteSpace(OutputIterationToMetadata))
            {
                context.Metadata[OutputIterationToMetadata] = i;
            }

            // Path uniqueness for caching per iteration: "LoopName/Iter_1", "LoopName/Iter_2"
            context.AddPathPart($"{Name}/Iter_{i}");

            // Execute the inner steps recursively using the Pipeline's core iterator
            currentResult = await context.ExecuteStepsWithNextStepsAsync(_steps, currentResult, Name, cancellationToken);

            context.RemovePathPart();

            // CRITICAL: If a child returned an error or HITL (IAwaitingInput),
            // bubble it up IMMEDIATELY without caching the loop. This enables "fast-forward" rehydration later.
            if (currentResult.HasError || currentResult.SuspendInfo != null)
            {
                return currentResult;
            }

            // We unwrap the BreakStepResult locally returning a standard result holding its value
            // so we don't accidentally break higher-level nested loops.
            if (currentResult is BreakStepResult)
            {
                return new StepResult<object>(this, currentResult.Value);
            }
        }

        // Loop exhausted without breaking
        _ = context.SendEventAsync(new AITaskAgent.Observability.Events.LoopExhaustedEvent
        {
            StepName = Name,
            LoopName = Name,
            IterationsCompleted = MaxIterations,
            MaxIterations = MaxIterations,
            CorrelationId = context.CorrelationId
        }, cancellationToken);

        return currentResult; // Max iterations reached
    }
}
