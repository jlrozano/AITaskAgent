namespace AITaskAgent.Core.Execution;

/// <summary>
/// Marks a property on a step-result's <c>Value</c> type as one the LLM never produces - code
/// fills it in after the structured-output call returns - so <see cref="StepResultFactory"/>
/// leaves it out of the JSON schema sent to the model.
/// </summary>
/// <remarks>
/// Deliberately distinct from <c>[JsonIgnore]</c>: that attribute also hides the property from
/// general Newtonsoft serialization, which would make it invisible to prompt templates too
/// (<c>TinyTemplateRendered</c> serializes the whole model through the same JsonIgnore-respecting
/// path). This attribute affects schema generation only.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExcludeFromLlmSchemaAttribute : Attribute;
