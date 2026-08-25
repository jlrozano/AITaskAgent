using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Base;
using AITaskAgent.Core.Execution.Middlewares;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Pipeline executor with middleware composition.
/// Execution order: User middlewares → Observability → Timeout → Retry → Step.
/// Timeout wraps Retry, so it is GLOBAL across all retry attempts (not per-attempt).
/// Internal middlewares (Observability, Timeout, Retry) are ALWAYS present.
/// Registered as singleton in DI for testability and to remove static state.
/// </summary>
public sealed class Pipeline : PipelineRuntimeBase, IPipeline
{
    private readonly ILogger<Pipeline> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PipelineOptions _options;

    /// <summary>
    /// Default timeout for the entire pipeline execution.
    /// </summary>
    public TimeSpan? DefaultPipelineTimeout { get; }

    /// <summary>
    /// Default timeout for individual steps.
    /// Individual steps can override this via IStep.Timeout.
    /// </summary>
    public TimeSpan? DefaultStepTimeout { get; }

    /// <summary>
    /// Constructor with simplified parameters, independent of DI.
    /// </summary>
    public Pipeline(
        TimeSpan? defaultPipelineTimeout = null,
        TimeSpan? defaultStepTimeout = null,
        ILoggerFactory? loggerFactory = null)
        : base(loggerFactory)
    {
        _loggerFactory = LoggerFactory;
        _logger = _loggerFactory.CreateLogger<Pipeline>();
        _options = new PipelineOptions();

        DefaultPipelineTimeout = defaultPipelineTimeout ?? _options.DefaultPipelineTimeout;
        DefaultStepTimeout = defaultStepTimeout ?? _options.DefaultStepTimeout;
    }

    /// <summary>
    /// Delegate for step execution through middleware chain.
    /// Now includes CancellationToken as parameter to allow timeout middleware to modify it.
    /// </summary>
    private delegate Task<IStepResult> StepExecutor(IStep step, IStepResult input, PipelineContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Unique metadata key for the pending IResumeResponse in a given pipeline context.
    /// CorrelationId prevents collisions between nested pipelines sharing the same context
    /// and between parallel branches running concurrently.
    /// </summary>
    private static string PendingResumeKey(PipelineContext context) =>
        $"__pipeline_pending_resume_{context.CorrelationId}__";

    /// <summary>
    /// Unique metadata key for a step's persisted resume state.
    /// CorrelationId scopes the key to the specific pipeline context instance.
    /// </summary>
    private static string ResumeStateKey(PipelineContext context) =>
        $"__resume_state_{context.CorrelationId}__";

    // A second, shorter ExecuteAsync overload used to sit here. Because every parameter past the
    // third had a default, the two were ambiguous for any ordinary call, making the short one
    // unreachable - and the long one, which is the IPipeline member everyone actually resolves,
    // never applied DefaultPipelineTimeout. Merged into one: the default is resolved below.

    /// <inheritdoc/>
    public async Task<IStepResult> ExecuteAsync<T>(
        string name,
        IReadOnlyList<IStep> steps,
        T input,
        PipelineContext? context = null,
        TimeSpan? pipelineTimeout = null,
        CancellationToken cancellationToken = default,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(input);
        if (steps.Count == 0)
        {
            throw new ArgumentException("Pipeline must contain at least one step.", nameof(steps));
        }

        StepValidation.ValidateUniqueStepNames(steps, name);

        var isResume = context != null && context.Metadata.ContainsKey(ResumeStateKey(context));

        var currentResult = isResume
            ? (IStepResult)input
            : input is IStepResult stepResult && steps[0].InputType.IsAssignableFrom(input.GetType())
                ? stepResult
                : StepResultFactory.CreateStepResult(steps[0].InputType, new EmptyStep(), input);

        context ??= PipelineContextFactory.Create(subscribers: subscribers);

        // Attach the engine to the context it is about to run. Composite steps and step loggers
        // reach it from here instead of resolving it from the container.
        context.Runtime = this;

        var logger = _logger;

        if (isResume)
        {
            context.Metadata[PendingResumeKey(context)] = input;
        }

        List<IPipelineMiddleware> middlewares = new([.. PipelineMiddlewareRegistry.GetMiddlewares(), .. CreateInternalMiddlewares()]);
        var middlewareChain = BuildMiddlewareChain(middlewares);

        CancellationToken pipelineCancellationToken;
        CancellationTokenSource? linkedCts = null;

        // Falling back to the configured default here is what makes DefaultPipelineTimeout mean
        // anything: callers reach this through IPipeline and rarely pass a timeout per run.
        var effectiveTimeout = pipelineTimeout ?? DefaultPipelineTimeout;

        if (effectiveTimeout.HasValue)
        {
            CancellationTokenSource timeoutCts = new(effectiveTimeout.Value);
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            pipelineCancellationToken = linkedCts.Token;
            logger.LogDebug("Pipeline {Name} timeout set to {Timeout}", name, effectiveTimeout.Value);
        }
        else if (cancellationToken != CancellationToken.None)
        {
            pipelineCancellationToken = cancellationToken;
        }
        else
        {
            pipelineCancellationToken = CancellationToken.None;
        }

        using var pipelineActivity = Telemetry.Source.StartActivity(
            $"Pipeline.{name}",
            ActivityKind.Internal);
        pipelineActivity?.SetTag(AITaskAgentConstants.TelemetryTags.PipelineName, name);
        pipelineActivity?.SetTag(AITaskAgentConstants.TelemetryTags.CorrelationId, context.CorrelationId);
        pipelineActivity?.SetTag(AITaskAgentConstants.TelemetryTags.PipelineStepCount, steps.Count);
        pipelineActivity?.SetTag(AITaskAgentConstants.TelemetryTags.PipelineTimeoutMs, pipelineTimeout?.TotalMilliseconds ?? DefaultPipelineTimeout?.TotalMilliseconds);

        logger.LogInformation("Pipeline {Name} starting execution with {StepCount} steps, CorrelationId: {CorrelationId}",
            name, steps.Count, context.CorrelationId);

        try
        {
            await context.SendEventAsync(new PipelineStartedEvent
            {
                StepName = name,
                PipelineName = name,
                TotalSteps = steps.Count,
                CorrelationId = context.CorrelationId,
                SchemaVersion = "1.0"
            }, pipelineCancellationToken);

            Metrics.PipelineExecutions.Add(1,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.PipelineName, name));

            Stopwatch pipelineStopwatch = Stopwatch.StartNew();

            currentResult = await ExecuteStepsWithNextStepsAsync(
                steps, currentResult, context, middlewareChain, name, logger, pipelineActivity, pipelineCancellationToken, isBranchScope: false);

            pipelineStopwatch.Stop();
            pipelineActivity?.SetStatus(currentResult.HasError ? ActivityStatusCode.Error : ActivityStatusCode.Ok);

            var isSuspended = currentResult.SuspendInfo != null;
            var success = !currentResult.HasError && !isSuspended;

            Metrics.PipelineDuration.Record(pipelineStopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.PipelineName, name),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepSuccess, success));

            logger.LogInformation("Pipeline {Name} completed with status: {Status}, duration: {Duration}ms",
                name, currentResult.HasError ? "Error" : isSuspended ? "Suspended" : "Success", pipelineStopwatch.ElapsedMilliseconds);

            await context.SendEventAsync(new PipelineCompletedEvent
            {
                StepName = name,
                PipelineName = name,
                Success = success,
                Duration = pipelineStopwatch.Elapsed,
                ErrorMessage = currentResult.HasError
                    ? currentResult.Error?.Message
                    : isSuspended
                        ? "Suspended awaiting input"
                        : null,
                CorrelationId = context.CorrelationId,
                SchemaVersion = "1.0"
            }, pipelineCancellationToken);

            return currentResult;
        }
        finally
        {
            linkedCts?.Dispose();

            // Complete the local channel if it was created specifically for this execution (different from the DI singleton)
            if (context?.EventChannel is IAsyncDisposable asyncDisposable)
            {
                var globalChannel = (IEventChannel?)context.ServiceProvider.GetService(typeof(IEventChannel));
                if (context.EventChannel != globalChannel)
                {
                    await asyncDisposable.DisposeAsync();
                }
            }
        }
    }


    /// <summary>
    /// Executes steps with NextSteps support. Used by ParallelStep.
    /// Creates middleware chain internally (middlewares are stateless).
    /// </summary>
    /// <inheritdoc/>
    public override Task<IStepResult> ExecuteStepsWithNextStepsAsync(
        IReadOnlyList<IStep> steps,
        IStepResult currentResult,
        PipelineContext context,
        string pipelineName,
        CancellationToken cancellationToken,
        bool isBranchScope = false)
    {
        List<IPipelineMiddleware> middlewares = new([.. PipelineMiddlewareRegistry.GetMiddlewares(), .. CreateInternalMiddlewares()]);
        var chain = BuildMiddlewareChain(middlewares);
        return ExecuteStepsWithNextStepsAsync(steps, currentResult, context, chain, pipelineName, _logger, null, cancellationToken, isBranchScope);
    }

    private static async Task<IStepResult> ExecuteStepsWithNextStepsAsync(
        IReadOnlyList<IStep> steps,
        IStepResult currentResult,
        PipelineContext context,
        StepExecutor middlewareChain,
        string pipelineName,
        ILogger logger,
        Activity? pipelineActivity,
        CancellationToken cancellationToken,
        bool isBranchScope)
    {
        foreach (var step in steps)
        {
            // Create logging scope for this step
            using (logger.BeginScope(new { Path = context.CurrentStepPath, Step = step.Name, context.CorrelationId }))
            {
                var stepKey = $"{(string.IsNullOrWhiteSpace(context.CurrentStepPath) ? "" : $"{context.CurrentStepPath}/")}{step.Name}";

                // 1. Execute the current step (or fetch from Cache/RAW)
                currentResult = await ExecuteStepAsync(
                    step,
                    currentResult,
                    context,
                    middlewareChain,
                    logger,
                    cancellationToken);

                // 2. If it errored or suspended natively, PAUSE execution & BUBBLE up
                //    Notice we DO NOT cache it yet!
                if (currentResult.HasError)
                {
                    pipelineActivity?.SetStatus(ActivityStatusCode.Error, currentResult.Error?.Message);
                    logger.LogError(currentResult.Error?.OriginalException,
                        "Pipeline {Name} stopped at step {StepName}: {Error}",
                        pipelineName, step.Name, currentResult.Error?.Message);
                    return currentResult;
                }

                if (currentResult.SuspendInfo != null)
                {
                    if (step.Resume == null)
                    {
                        throw new InvalidOperationException(
                            $"Step '{step.Name}' returned SuspendInfo but does not implement IResume. " +
                            $"Either implement IResume on the step or remove the SuspendInfo from its result.");
                    }

                    // The state is stored separately in metadata and NOT written to baseResult.Value,
                    // which preserves whatever partial value the step computed.
                    var state = await step.Resume.SaveStateAsync(step);
                    context.Metadata[ResumeStateKey(context)] = state;

                    logger.LogInformation("Pipeline {Name} suspended at step {StepName} awaiting human input.", pipelineName, step.Name);
                    await context.SendEventAsync(new PipelineSuspendedEvent
                    {
                        StepName = step.Name,
                        CorrelationId = context.CorrelationId,
                        Timestamp = DateTimeOffset.UtcNow,
                        TargetStepPath = stepKey,
                        IsBranchSuspension = isBranchScope,
                        BranchName = isBranchScope
                            ? (string.IsNullOrWhiteSpace(context.CurrentStepPath) ? null : context.CurrentStepPath.Split('/')[0])
                            : null,
                        SchemaVersion = "1.0"
                    }, cancellationToken);
                    context.StepResults[stepKey] = currentResult;
                    return currentResult;
                }

                // 3. Process dynamic children (NextSteps)
                if (currentResult.NextSteps.Count > 0)
                {
                    context.AddPathPart(step.Name);

                    // Copy NextSteps and CLEAR them immediately to prevent re-execution on next run cache hits
                    List<IStep> stepsToExecute = currentResult.NextSteps.ToList();
                    currentResult.NextSteps.Clear();

                    var childrenResult = await ExecuteStepsWithNextStepsAsync(
                        stepsToExecute.AsReadOnly(),
                        currentResult,
                        context,
                        middlewareChain,
                        pipelineName,
                        logger,
                        pipelineActivity,
                        cancellationToken,
                        isBranchScope);

                    context.RemovePathPart();

                    if (childrenResult.HasError || childrenResult.SuspendInfo != null)
                    {
                        return childrenResult;
                    }

                    // Otherwise, adopt the last child's result to continue standard linear flow
                    currentResult = childrenResult;
                }

                // 4. CACHEO DIFERIDO (DEFERRED CACHE):
                // We reached here ONLY because the step and all its recursive NextSteps finished successfully.
                // We are now safe to commit the step to the DB cache.
                context.StepResults[stepKey] = currentResult;
            }
        }

        return currentResult;
    }

    /// <summary>
    /// Executes a single step through the pre-built middleware chain.
    /// Acts as a volatile dispatcher: does NOT cache the execution result itself.
    /// </summary>
    private static async Task<IStepResult> ExecuteStepAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        StepExecutor middlewareChain,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var key = $"{(string.IsNullOrWhiteSpace(context.CurrentStepPath) ? "" : $"{context.CurrentStepPath}/")}{step.Name}";

            // Check if the step completed (or was suspended) in a prior run
            if (context.StepResults.TryGetValue(key, out var value))
            {
                logger.LogDebug("Using stored result for step {StepName} with key {Key}", step.Name, key);

                if (value.SuspendInfo != null)
                {
                    if (context.Metadata.TryRemove(PendingResumeKey(context), out var pending)
                        && (pending is IStepResult pendingResume)
                        && context.Metadata.TryRemove(ResumeStateKey(context), out var state))
                    {
                        logger.LogDebug("Resuming suspended step {StepName} with IResumeResponse.", step.Name);
                        if (step.Resume != null)
                        {
                            await step.Resume.RestoreStateAsync(step, state, pendingResume.Value);
                        }

                    }
                    else
                    {
                        return value;
                    }
                }
                else
                {
                    // Completed in a prior run. Results are cached only once the step AND its
                    // NextSteps have all succeeded, so the children already ran - returning them
                    // still attached would run them a second time on resume.
                    value.NextSteps.Clear();
                    return value;
                }
            }

            // Execute the step (either fresh or resumed)
            var result = await middlewareChain(step, input, context, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Step {Step} failed with exception", step.Name);
            return ErrorStepResult.FromException(new EmptyStep(), ex);
        }
    }

    /// <summary>
    /// Builds the middleware chain ONCE. The terminal delegate executes the step.
    /// </summary>
    private static StepExecutor BuildMiddlewareChain(List<IPipelineMiddleware> middlewares)
    {
        // Build chain in reverse order
        StepExecutor chain = (step, input, ctx, ct) => step.ExecuteAsync(input, ctx, 1, null, ct);
        foreach (var middleware in middlewares.AsEnumerable().Reverse())
        {
            var next = chain;
            chain = (step, input, ctx, ct) => middleware.InvokeAsync(
                step,
                input,
                ctx,
                (tokenFromMiddleware) => next(step, input, ctx, tokenFromMiddleware), ct);
        }

        return chain;
    }

    /// <summary>
    /// Creates internal middlewares that are ALWAYS present.
    /// Order: Observability → Timeout → Retry
    /// - Observability: traces/metrics for the entire operation including retries
    /// - Timeout: global timeout for ALL retry attempts
    /// - Retry: terminal middleware, executes step.ExecuteAsync directly (doesn't call next)
    /// </summary>
    private List<IPipelineMiddleware> CreateInternalMiddlewares() =>
    [
        new ObservabilityMiddleware(),
        new TimeoutMiddleware(_loggerFactory.CreateLogger<TimeoutMiddleware>(), DefaultStepTimeout),
        new RetryMiddleware(_loggerFactory.CreateLogger<RetryMiddleware>())
    ];
}