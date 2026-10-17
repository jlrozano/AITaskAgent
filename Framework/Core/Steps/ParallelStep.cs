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
/// Non-generic base for parallel execution. Not declarable from YAML: the YAML factory only maps
/// <c>IYamlStep</c> types marked with <c>[YamlStepTag]</c>, and this type is only tagged with <c>[StepTag]</c>.
/// Executes all branches concurrently; each branch gets an isolated conversation context.
/// </summary>
/// <remarks>
/// Branch step paths keep the parent prefix: a branch <c>B</c> of parallel <c>Fan</c> runs under
/// <c>"{parent}/Fan/B"</c>, so its step is cached as <c>"{parent}/Fan/B/B"</c>.
/// <para>
/// Known limitation: a branch suspension (a branch result with <c>SuspendInfo</c>) is not
/// propagated. The <see cref="ParallelResult"/> never carries <c>SuspendInfo</c>, so the pipeline
/// continues after this step and caches it as completed, and on resume the suspended branch is
/// never restored. Do not suspend inside a branch. See docs/guides/human-in-the-loop.md.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Most branches running at once. 0 or less (the default) sets no limit of its own and keeps what
    /// <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, CancellationToken, Func{TSource, CancellationToken, ValueTask})"/>
    /// has always done here: <see cref="Environment.ProcessorCount"/> branches at a time. A lower value caps, for
    /// instance, how many LLM calls a fan-out sends to the same provider at once.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>
    /// The options of the <c>CancellationToken</c> overload the step used before <see cref="MaxDegreeOfParallelism"/>
    /// existed (default scheduler, one branch per processor), with the limit applied when there is one.
    /// </summary>
    private ParallelOptions CreateParallelOptions(CancellationToken cancellationToken) => new()
    {
        CancellationToken = cancellationToken,
        TaskScheduler = TaskScheduler.Default,
        MaxDegreeOfParallelism = MaxDegreeOfParallelism > 0 ? MaxDegreeOfParallelism : Environment.ProcessorCount
    };

    /// <inheritdoc/>
    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt,
        IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        ParallelResult result = new(this);
        ConcurrentBag<string> errorMessages = [];

        await Parallel.ForEachAsync(_branches, CreateParallelOptions(cancellationToken), async (step, ct) =>
        {
            PipelineContext branchContext = context.CloneForBranch();
            branchContext.BranchName = step.Name;
            var branchId = Guid.NewGuid().ToString();
            try
            {
                // "{parent}/{ParallelName}/{Branch}": the clone keeps the parent's path, and the
                // parallel's own name keeps two ParallelSteps with same-named branches apart. The
                // branch step itself is then stored under ".../{Branch}/{Branch}".
                branchContext.AddPathPart($"{Name}/{step.Name}");
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
                // Execute on branchContext, not the shared one: CloneForBranch exists precisely to
                // give each branch its own conversation and step path. Running on `context` while
                // only the events used `branchContext` left every branch sharing state.
                IStepResult stepResult = await branchContext.ExecuteStepsWithNextStepsAsync(
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
