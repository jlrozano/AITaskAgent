using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Results;

namespace AITaskAgent.LLM.Steps;

/// <summary>
/// Specialized routing step for intention-based routing.
/// Routes based on LLM-analyzed user intention and passes the optimized prompt to the selected pipeline.
/// </summary>
/// <typeparam name="TEnum">Enum type representing user intentions.</typeparam>
public sealed class IntentionRouterStep<TEnum>(
    Dictionary<TEnum, IStep> routes,
    IStep? defaultRoute = null)
    : SwitchStep<IStepResult<Intention<TEnum>>>(
        name: "Router",
        selector: CreateSelector(routes, defaultRoute))
    where TEnum : struct, Enum
{
    private readonly Dictionary<TEnum, IStep> _routes = routes;
    private readonly IStep? _defaultRoute = defaultRoute;

    /// <summary>
    /// Gets the routes dictionary mapping intentions to steps.
    /// </summary>
    public IReadOnlyDictionary<TEnum, IStep> Routes => _routes;

    /// <summary>
    /// Gets the default route if no intention matches.
    /// </summary>
    public IStep? DefaultRoute => _defaultRoute;

    /// <summary>
    /// Creates the selector function for the base SwitchStep.
    /// Returns (targetStep, RouterStepResult with Intention data preserved).
    /// </summary>
    private static Func<IStepResult<Intention<TEnum>>, PipelineContext, (IStep, IStepResult)> CreateSelector(
        Dictionary<TEnum, IStep> routes,
        IStep? defaultRoute)
    {
        return (input, context) =>
        {
            var intentionInfo = input.Value ?? throw new InvalidOperationException("No intention value selected.");

            // Try to get route for the intention
            var targetStep = (routes.TryGetValue(intentionInfo.Option, out var step)
                ? step
                : defaultRoute) ?? throw new InvalidOperationException($"No route for intention {intentionInfo.Option} and no default route");

            input.NextSteps.Add(targetStep);
            return ((IStep, IStepResult))(targetStep, input);
        };
    }



    /// <summary>
    /// Provides intention-specific routing reason for events.
    /// </summary>
    protected override string GetRoutingReason(IStepResult<Intention<TEnum>> input, IStep targetStep)
    {
        var intentionInfo = input.Value;
        return intentionInfo == null
            ? "No intention information available"
            : $"Intention: {intentionInfo.Option} - {intentionInfo.Reasoning}";
    }


}
