using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Non-generic base for parallel execution. Directly serializable from YAML via <c>!ParallelStep</c>.
/// Executes all branches concurrently; each branch gets an isolated conversation context.
/// </summary>
[StepTag("ParallelStep")]
public class ParallelStep : StepBase
{
    private readonly List<IStep> _branches = [];

    /// <summary>Parameterless ctor for YAML deserialization.</summary>
    public ParallelStep() : base("ParallelStep", typeof(IStepResult), typeof(ParallelResult))
    {
        MaxRetries = 1;
    }

    /// <summary>Programmatic ctor.</summary>
    public ParallelStep(string name, params IStep[] branches)
        : base(name, typeof(IStepResult), typeof(ParallelResult))
    {
        _branches.AddRange(branches);
        MaxRetries = 1;
    }

    /// <summary>
    /// Branch steps to execute in parallel. Set via YAML deserialization or programmatic ctor.
    /// </summary>
    public List<IStep> Branches
    {
        get => _branches;
        init => _branches = value ?? [];
    }

    /// <inheritdoc/>
    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt,
        IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        ParallelResult result = new(this);
        ConcurrentBag<string> errorMessages = [];

        await Parallel.ForEachAsync(_branches, cancellationToken, async (step, ct) =>
        {
            PipelineContext branchContext = context.CloneForBranch();
            var branchId = Guid.NewGuid().ToString();
            try
            {
                branchContext.AddPathPart(step.Name);
                await branchContext.SendEventAsync(new BranchStartedEvent
                {
                    StepName = Name,
                    StepPath = branchContext.CurrentStepPath,
                    CorrelationId = branchContext.CorrelationId,
                    Timestamp = DateTimeOffset.UtcNow,
                    SchemaVersion = "1.0",
                    BranchName = step.Name,
                    BranchId = branchId
                }, ct);

                Stopwatch branchStopwatch = Stopwatch.StartNew();
                IStepResult stepResult = await context.ExecuteStepsWithNextStepsAsync(
                    [step], input, Name, ct, isBranchScope: true);
                branchStopwatch.Stop();
                result.AddResult(step.Name, stepResult);

                if (stepResult.HasError)
                {
                    errorMessages.Add($"Step {step.Name} error: {stepResult.Error?.Message}");
                    Logger.LogWarning("Parallel branch {StepName} completed with error: {Error}",
                        step.Name, stepResult.Error?.Message);
                }

                await branchContext.SendEventAsync(new BranchCompletedEvent
                {
                    StepName = Name,
                    StepPath = branchContext.CurrentStepPath,
                    CorrelationId = branchContext.CorrelationId,
                    Timestamp = DateTimeOffset.UtcNow,
                    SchemaVersion = "1.0",
                    BranchName = step.Name,
                    BranchId = branchId,
                    Success = !stepResult.HasError && stepResult.SuspendInfo == null,
                    Duration = branchStopwatch.Elapsed,
                    ErrorMessage = stepResult.HasError ? stepResult.Error?.Message : null
                }, ct);

                branchContext.RemovePathPart();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Parallel branch {StepName} threw exception", step.Name);
                result.AddResult(step.Name, ErrorStepResult.FromMessage(this, ex.Message));
                errorMessages.Add($"Step {step.Name} exception: {ex.Message}");

                await branchContext.SendEventAsync(new BranchCompletedEvent
                {
                    StepName = Name,
                    StepPath = branchContext.CurrentStepPath,
                    CorrelationId = branchContext.CorrelationId,
                    Timestamp = DateTimeOffset.UtcNow,
                    SchemaVersion = "1.0",
                    BranchName = step.Name,
                    BranchId = branchId,
                    Success = false,
                    Duration = TimeSpan.Zero,
                    ErrorMessage = ex.Message
                }, ct);

                branchContext.RemovePathPart();
            }
        });

        if (!errorMessages.IsEmpty)
        {
            result.Error = new StepError { Message = string.Join(Environment.NewLine, errorMessages) };
        }

        return result;
    }
}

/// <summary>
/// Typed variant for programmatic use. Constrains input to <typeparamref name="TIn"/>.
/// </summary>
/// <typeparam name="TIn">The input type for all branches.</typeparam>
public sealed class ParallelStep<TIn> : ParallelStep
    where TIn : StepResult
{
    public ParallelStep(string name, params IStep[] branches)
        : base(name, branches)
    {
        InputType = typeof(TIn);
    }
}
