using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using Newtonsoft.Json;

namespace AITaskAgent.LLM.Steps;

/// <summary>
/// Specialized routing step for intention-based routing.
/// Routes based on LLM-analyzed user intention and passes the optimized prompt to the selected pipeline.
/// </summary>

public sealed class RouterStep(
    string name,
    Dictionary<string, IStep> routes,
    IStep? defaultRoute = null)
    : SwitchStep<IRouterResult>(name: name), IRouterOptions

{
    /// <summary>
    /// Gets the default route if no intention matches.
    /// </summary>
    public IStep? DefaultRoute => defaultRoute;

    IList<string> IRouterOptions.Options { get; } = [.. routes.Keys];

    /// <summary>
    /// Maps route names to steps, matched without regard to letter casing.
    /// </summary>
    /// <remarks>
    /// The route name comes from an LLM, which is precisely where casing cannot be relied on, so
    /// the comparison must be case-insensitive regardless of the dictionary the caller passed in.
    /// This is the dictionary <see cref="SelectTargetStep"/> reads, so modifying it does change
    /// the available routes.
    /// </remarks>
    public Dictionary<string, IStep> Routes { get; } = new(routes, StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// Provides intention-specific routing reason for events.
    /// </summary>
    protected override string GetRoutingReason(IRouterResult input, IStep targetStep)
    {
        return $"Selected options from router {Name}: {input.SelectedOption}";
    }

    protected override (IStep, IStepResult value) SelectTargetStep(IRouterResult input, PipelineContext context)
    {
        string selectedOption = input.SelectedOption ?? throw new InvalidOperationException("No intention value selected.");

        // Read Routes, not the constructor parameter: only Routes carries the case-insensitive
        // comparer, and the option name was produced by a model.
        IStep targetStep = (Routes.TryGetValue(selectedOption, out IStep? step)
            ? step
            : defaultRoute) ?? throw new InvalidOperationException($"No route for intention {selectedOption} and no default route");

        // Do not add targetStep to input.NextSteps here: SwitchStep.ExecuteAsync already does it
        // with the value returned below, and adding it twice made the pipeline run it twice.
        return (targetStep, input);
    }
}

