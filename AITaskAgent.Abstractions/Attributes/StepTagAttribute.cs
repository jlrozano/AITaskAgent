namespace AITaskAgent.Core.Attributes;

/// <summary>
/// Gives a step class the tag it is registered under in <c>IStepRegistry</c>, which is also the
/// value of <c>type:</c> that declares it in a YAML pipeline (e.g. <c>type: LlmStep</c>).
/// Equivalent to [RuleName] in BRMS.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class StepTagAttribute(string tagName) : Attribute
{
    public string TagName { get; } = tagName;
}
