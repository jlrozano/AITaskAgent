namespace AITaskAgent.Designer.Models;

using Newtonsoft.Json.Linq;

/// <summary>
/// Reference to a step with its configuration.
/// Equivalent to BRMS RuleReference.
/// </summary>
public record StepReference
{
/// <summary>
/// ID of the registered step type.
/// </summary>
public required string StepId { get; init; }

/// <summary>
/// Instance name for this step in the pipeline.
/// </summary>
public string? Name { get; init; }

/// <summary>
/// Configuration parameters for this step instance.
/// </summary>
public JObject? Configuration { get; init; }
}
