namespace AITaskAgent.Core.Attributes;

/// <summary>
/// Marks an <see cref="AITaskAgent.Core.Abstractions.IStepValidator"/> class as discoverable
/// by the YAML pipeline factory via a YAML tag (e.g. <c>!JsValidator</c>).
/// Mirrors the <see cref="StepTagAttribute"/> pattern for validators.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ValidatorTagAttribute(string tagName) : Attribute
{
    /// <summary>The YAML tag name used to identify this validator type.</summary>
    public string TagName { get; } = tagName;
}
