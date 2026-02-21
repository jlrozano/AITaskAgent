namespace AITaskAgent.YAML.Attributes;

/// <summary>
/// Marks a class as discoverable by the YAML pipeline factory via a YAML tag (e.g., !LlmStep).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class YamlStepTagAttribute(string tagName) : Attribute
{
    public string TagName { get; } = tagName;
}
