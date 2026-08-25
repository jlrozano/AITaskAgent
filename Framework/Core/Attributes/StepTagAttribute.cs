namespace AITaskAgent.Core.Attributes;

/// <summary>
/// Marks a step class as discoverable by the YAML pipeline factory and StepManager
/// via a YAML tag (e.g., !JsStep). Equivalent to [RuleName] in BRMS.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class StepTagAttribute(string tagName) : Attribute
{
    public string TagName { get; } = tagName;
}
