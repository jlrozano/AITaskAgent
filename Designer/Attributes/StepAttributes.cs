namespace AITaskAgent.Designer.Attributes;

/// <summary>
/// Specifies a custom step ID for registration.
/// Equivalent to BRMS RuleNameAttribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StepIdAttribute(string stepId) : Attribute
{
public string StepId { get; } = stepId;
}

/// <summary>
/// Specifies the category for a step in the visual designer.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StepCategoryAttribute(string category) : Attribute
{
public string Category { get; } = category;
}
