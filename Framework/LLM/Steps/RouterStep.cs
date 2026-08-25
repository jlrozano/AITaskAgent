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
    /// Provides a mapping of route names to their corresponding step implementations, using case-insensitive string
    /// comparison for keys.
    /// </summary>
    /// <remarks>This dictionary enables lookup of steps by route name without regard to letter casing.
    /// Modifying the dictionary affects the available routes for step resolution.</remarks>
    public Dictionary<string, IStep> Routes = new(routes, StringComparer.OrdinalIgnoreCase);


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

        // Try to get route for the intention. 
        IStep targetStep = (routes.TryGetValue(selectedOption, out IStep? step)
            ? step
            : defaultRoute) ?? throw new InvalidOperationException($"No route for intention {selectedOption} and no default route");

        input.NextSteps.Add(targetStep);

        return (targetStep, input);
    }
}

