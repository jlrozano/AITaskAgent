using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// A step that executes multiple branches in parallel and aggregates their results.
/// All branches receive the same input and execute concurrently.
/// Each branch passes through the full middleware chain (Observability, Timeout, Retry).
/// Each branch gets its own cloned Conversation context to prevent conflicts.
/// NextSteps from each branch are also executed.
/// </summary>
/// <typeparam name="TIn">The input type for all branches.</typeparam>
public sealed class ParallelStep<TIn> : TypedStep<TIn, ParallelResult>
    where TIn : StepResult
{
    private readonly List<IStep> _steps;

    public ParallelStep(string name, params IStep[] steps) : base(name)
    {
        _steps = [.. steps];
        MaxRetries = 1; // ParallelStep itself does not retry; 
    }

    /// <summary>
    /// Gets the steps that execute in parallel.
    /// </summary>
    public IReadOnlyList<IStep> Steps => _steps;

    protected override async Task<ParallelResult> ExecuteAsync(
        TIn input,
        PipelineContext context,
        int attempt,
        ParallelResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        var result = new ParallelResult(this);
        var errorMessages = new StringBuilder();

        await Parallel.ForEachAsync(_steps, cancellationToken, async (step, ct) =>
        {
            // Clone context for this branch (isolated Conversation, shared StepResults/Metadata)
            var branchContext = context.CloneForBranch();

            try
            {
                // Build branch path: parentPath/ParallelStepName
                context.AddPathPart(step.Name);

                // Execute through full middleware chain with NextSteps support
                var stepResult = await Pipeline.ExecuteStepsWithNextStepsAsync(
                    [step],        // Single step list
                    input,         // Input
                    branchContext, // Cloned context
                    Name,          // Pipeline name (for logging)
                    ct);           // Cancellation token

                context.RemovePathPart();
                result.AddResult(step.Name, stepResult);

                if (stepResult.HasError)
                {
                    errorMessages.AppendLine($"Step {step.Name} error: {stepResult.Error?.Message}");
                    Logger.LogWarning(
                        "Parallel branch {StepName} completed with error: {Error}",
                        step.Name, stepResult.Error?.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Parallel branch {StepName} threw exception", step.Name);
                result.AddResult(step.Name, ErrorStepResult.FromMessage(this, ex.Message));
                errorMessages.AppendLine($"Step {step.Name} exception: {ex.Message}");
            }
        });

        if (errorMessages.Length > 0)
        {
            result.Error = new StepError
            {
                Message = errorMessages.ToString()
            };
        }

        return result;
    }
}
