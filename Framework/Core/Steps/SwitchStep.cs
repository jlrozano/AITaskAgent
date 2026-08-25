using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Generic routing step that selects the next step based on custom logic.
/// The selector function evaluates the input and returns the target step.
/// </summary>
/// <typeparam name="TIn">Input step result type.</typeparam>
/// <param name="name">Name of the step.</param>
/// <param name="selector">Function to select the next step based on input and context.</param>
public class SwitchStep<TIn>(
    string name,
    Func<TIn, PipelineContext, (IStep, IStepResult value)> selector)
    : IStep
    where TIn : IStepResult

{
    private readonly Func<TIn, PipelineContext, (IStep, IStepResult value)> _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    private Type _outType = typeof(IStepResult);
    protected SwitchStep(string name) : this(name, (input, context) => (null!, null!))
    {
        _selector = SelectTargetStep;
    }

    protected virtual (IStep, IStepResult value) SelectTargetStep(TIn input, PipelineContext context)
    {
        throw new NotImplementedException();
    }

    public string Name { get; } = name;

    Type IStep.InputType => typeof(TIn);

    Type IStep.OutputType => _outType;

    public IResume? Resume => null;

    async Task<IStepResult> IStep.ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        try
        {
            ILogger<SwitchStep<TIn>> logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SwitchStep<TIn>>();

            // Execute selector to determine target step
            (IStep? targetStep, IStepResult? value) = _selector((TIn)input, context);

            // exceptions break de pipeline. ErrorStepResult is used to continue the pipeline.
            if (targetStep == null || value == null)
            {
                throw new InvalidOperationException("Selector returned null - no route selected");
            }

            _outType = value.GetType();
            value.NextSteps.Add(targetStep);

            // Send routing event
            await context.SendEventAsync(
                new StepRoutingEvent
                {
                    StepName = Name,
                    SelectedRoute = targetStep.Name,
                    RoutingReason = GetRoutingReason((TIn)input, targetStep),
                    CorrelationId = context.CorrelationId
                },
                cancellationToken);

            logger.LogInformation(
                "Routing to {StepName}",
                targetStep.Name);

            return value;
        }
        catch (Exception ex)
        {
            ILogger<SwitchStep<TIn>> logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SwitchStep<TIn>>();
            logger.LogError(ex, "Selector failed in {StepName}", Name);
            throw;
        }
    }

    /// <summary>
    /// Gets the routing reason for the event.
    /// Override to provide custom reasoning.
    /// </summary>
    protected virtual string GetRoutingReason(TIn input, IStep targetStep)
    {
        return $"Selected {targetStep.Name} based on input evaluation";
    }
}
