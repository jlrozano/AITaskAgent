namespace AITaskAgent.Designer.Models;

using NJsonSchema;

/// <summary>
/// Describes a registered step type with its configuration schema.
/// Equivalent to BRMS RuleDescription.
/// </summary>
public record StepDescription
{
/// <summary>
/// Unique identifier for the step type.
/// </summary>
public required string StepId { get; init; }

/// <summary>
/// Display name for UI.
/// </summary>
public string? DisplayName { get; init; }

/// <summary>
/// Description of what this step does.
/// </summary>
public string? Description { get; init; }

/// <summary>
/// The CLR type that implements this step.
/// </summary>
public required Type StepType { get; init; }

/// <summary>
/// JSON Schema for the step's configuration parameters.
/// </summary>
public JsonSchema? ConfigurationSchema { get; init; }

/// <summary>
/// Category for grouping in UI (e.g., "LLM", "Flow", "Action").
/// </summary>
public string Category { get; init; } = "General";
}
