using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Base;
using AITaskAgent.Core.Execution.Middlewares;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Static pipeline executor with middleware composition.
/// Middlewares are applied in order: User middlewares → Observability → Retry → Timeout → Step.
/// Internal middlewares (Observability, Retry, Timeout) are ALWAYS present.
/// </summary>
public static class Pipeline
{
    internal static ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    /// <summary>
    /// Default timeout for the entire pipeline execution.
    /// </summary>
    public static TimeSpan DefaultPipelineTimeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Default timeout for individual steps.
    /// Individual steps can override this via IStep.Timeout.
    /// </summary>
    public static TimeSpan DefaultStepTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public static ILoggerFactory LoggerFactory { get => _loggerFactory; set => _loggerFactory = value ?? NullLoggerFactory.Instance; }

    /// <summary>
    /// Delegate for step execution through middleware chain.
    /// Now includes CancellationToken as parameter to allow timeout middleware to modify it.
    /// </summary>
    private delegate Task<IStepResult> StepExecutor(IStep step, IStepResult input, PipelineContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a pipeline of steps sequentially with middleware composition.
    /// </summary>
    public static async Task<IStepResult> ExecuteAsync<T>(
        string name,
        IReadOnlyList<IStep> steps,
        T input,
        PipelineContext? context = null,
        TimeSpan? pipelineTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(input);
        if (steps.Count == 0)
        {
            throw new ArgumentException("Pipeline must contain at least one step.", nameof(steps));
        }

        if (!steps[0].InputType.IsAssignableFrom(typeof(T)) && !steps[0].InputType.IsAssignableFrom(input.GetType()))
        {
            throw new ArgumentException($"First step input type ({steps[0].InputType.Name}) must match pipeline input type ({input.GetType().Name}).", nameof(steps));
        }

        var currentResult = input is IStepResult stepResult && steps[0].InputType.IsAssignableFrom(input.GetType())
            ? stepResult
            : StepResultFactory.CreateStepResult(steps[0].InputType, new EmptyStep(), input);

        context ??= PipelineContextFactory.Create();

        var logger = LoggerFactory.CreateLogger(typeof(Pipeline));

        var middlewares = new List<IPipelineMiddleware>([.. PipelineMiddlewareRegistry.GetMiddlewares(), .. CreateInternalMiddlewares(LoggerFactory)]);

        // Build middleware chain ONCE - the terminal executes the step
        var middlewareChain = BuildMiddlewareChain(middlewares);

        // Create cancellation token with pipeline timeout
        using var pipelineCts = pipelineTimeout.HasValue
            ? new CancellationTokenSource(pipelineTimeout.Value)
            : new CancellationTokenSource();

        var pipelineCancellationToken = pipelineCts.Token;

        if (pipelineTimeout.HasValue)
        {
            logger.LogDebug("Pipeline {Name} timeout set to {Timeout}", name, pipelineTimeout.Value);
        }

        using var pipelineActivity = Telemetry.Source.StartActivity(
            $"Pipeline.{name}",
            ActivityKind.Internal);
        pipelineActivity?.SetTag(AITaskAgentConstants.TelemetryTags.PipelineName, name);
        pipelineActivity?.SetTag(AITaskAgentConstants.TelemetryTags.CorrelationId, context.CorrelationId);
        pipelineActivity?.SetTag(AITaskAgentConstants.TelemetryTags.PipelineStepCount, steps.Count);
        pipelineActivity?.SetTag(AITaskAgentConstants.TelemetryTags.PipelineTimeoutMs, pipelineTimeout?.TotalMilliseconds ?? DefaultPipelineTimeout.TotalMilliseconds);

        logger.LogInformation("Pipeline {Name} starting execution with {StepCount} steps, CorrelationId: {CorrelationId}",
            name, steps.Count, context.CorrelationId);

        // Send pipeline started event
        await context.SendEventAsync(new PipelineStartedEvent
        {
            StepName = name,
            PipelineName = name,
            TotalSteps = steps.Count,
            CorrelationId = context.CorrelationId
        }, pipelineCancellationToken);

        // Record pipeline execution metric
        Metrics.PipelineExecutions.Add(1,
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.PipelineName, name));

        var pipelineStopwatch = Stopwatch.StartNew();

        // Execute main steps and handle NextSteps recursively 
        currentResult = await ExecuteStepsWithNextStepsAsync(
            steps, currentResult, context, middlewareChain, name, logger, pipelineActivity, pipelineCancellationToken);

        pipelineStopwatch.Stop();
        pipelineActivity?.SetStatus(currentResult.HasError ? ActivityStatusCode.Error : ActivityStatusCode.Ok);

        // Record pipeline duration metric
        Metrics.PipelineDuration.Record(pipelineStopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.PipelineName, name),
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepSuccess, !currentResult.HasError));

        logger.LogInformation("Pipeline {Name} completed with status: {Status}, duration: {Duration}ms",
            name, currentResult.HasError ? "Error" : "Success", pipelineStopwatch.ElapsedMilliseconds);

        // Send pipeline completed event
        await context.SendEventAsync(new PipelineCompletedEvent
        {
            StepName = name,
            PipelineName = name,
            Success = !currentResult.HasError,
            Duration = pipelineStopwatch.Elapsed,
            ErrorMessage = currentResult.Error?.Message,
            CorrelationId = context.CorrelationId
        }, pipelineCancellationToken);

        return currentResult;
    }

    /// <summary>
    /// Executes steps with NextSteps support. Used by ParallelStep.
    /// Creates middleware chain internally (middlewares are stateless).
    /// </summary>
    internal static Task<IStepResult> ExecuteStepsWithNextStepsAsync(
        IReadOnlyList<IStep> steps,
        IStepResult currentResult,
        PipelineContext context,
        string pipelineName,
        CancellationToken cancellationToken)
    {
        var middlewares = new List<IPipelineMiddleware>([.. PipelineMiddlewareRegistry.GetMiddlewares(), .. CreateInternalMiddlewares(LoggerFactory)]);
        var chain = BuildMiddlewareChain(middlewares);
        var logger = LoggerFactory.CreateLogger(typeof(Pipeline));
        return ExecuteStepsWithNextStepsAsync(steps, currentResult, context, chain, pipelineName, logger, null, cancellationToken);
    }

    private static async Task<IStepResult> ExecuteStepsWithNextStepsAsync(
        IReadOnlyList<IStep> steps,
        IStepResult currentResult,
        PipelineContext context,
        StepExecutor middlewareChain,
        string pipelineName,
        ILogger logger,
        Activity? pipelineActivity,
        CancellationToken cancellationToken)
    {

        foreach (var step in steps)
        {


            // Create logging scope for this step
            using (logger.BeginScope(new { Path = context.CurrentPath, Step = step.Name, context.CorrelationId }))
            {
                // Execute the current step
                currentResult = await ExecuteStepAsync(
                    step,
                    currentResult,
                    context,
                    middlewareChain,
                    logger,
                    cancellationToken);

                if (currentResult.HasError)
                {
                    pipelineActivity?.SetStatus(ActivityStatusCode.Error, currentResult.Error?.Message);
                    logger.LogError(currentResult.Error?.OriginalException,
                        "Pipeline {Name} stopped at step {StepName}: {Error}",
                        pipelineName, step.Name, currentResult.Error?.Message);
                    return currentResult;
                }

                if (currentResult.NextSteps.Count > 0)
                {
                    context.AddPathPart(step.Name);

                    // Copy NextSteps and CLEAR them immediately to prevent re-execution
                    var stepsToExecute = currentResult.NextSteps.ToList();
                    currentResult.NextSteps.Clear();

                    currentResult = await ExecuteStepsWithNextStepsAsync(
                        stepsToExecute.AsReadOnly(), // Use the copy
                        currentResult,
                        context,
                        middlewareChain,
                        pipelineName,
                        logger,
                        pipelineActivity,
                        cancellationToken);

                    context.RemovePathPart();
                }
            }
        }

        return currentResult;
    }

    /// <summary>
    /// Executes a single step through the pre-built middleware chain.
    /// </summary>
    private static async Task<IStepResult> ExecuteStepAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        StepExecutor middlewareChain,
        ILogger logger,
        CancellationToken cancellationToken
        )
    {

        try
        {
            var key = $"{(string.IsNullOrWhiteSpace(context.CurrentPath) ? "" : $"{context.CurrentPath}/")}{step.Name}";

            if (context.StepResults.TryGetValue(key, out var value))
            {
                logger.LogDebug("Using stored result for step {StepName} with key {Key}", step.Name, key);
                // Clear NextSteps from cached result - they were already executed
                value.NextSteps.Clear();
                return value;
            }
            var result = await middlewareChain(step, input, context, cancellationToken);

            context.StepResults[key] = result;
            logger.LogDebug("Stored result for step {StepName} with key {Key}", step.Name, key);

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
    private static List<IPipelineMiddleware> CreateInternalMiddlewares(ILoggerFactory loggerFactory) =>
    [
        new ObservabilityMiddleware(),
        new TimeoutMiddleware(loggerFactory.CreateLogger<TimeoutMiddleware>(), DefaultStepTimeout),
        new RetryMiddleware(loggerFactory.CreateLogger<RetryMiddleware>())
    ];
}
