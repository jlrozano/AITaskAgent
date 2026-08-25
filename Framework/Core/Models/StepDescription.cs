using NJsonSchema;

namespace AITaskAgent.Core.Models;

/// <summary>
/// Metadata descriptor for a registered step type.
/// Equivalent to RuleDescription in BRMS.
/// </summary>
public sealed class StepDescription
{
    public string TagName { get; init; } = "";
    public string Description { get; init; } = "";
    public Type StepType { get; init; } = typeof(object);
    public JsonSchema? Parameters { get; init; }
}
