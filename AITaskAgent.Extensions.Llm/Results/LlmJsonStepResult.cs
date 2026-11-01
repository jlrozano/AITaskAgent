using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace AITaskAgent.LLM.Results;

/// <summary>
/// LLM result whose value is plain JSON validated against a JSON Schema carried by the instance:
/// the LLM counterpart of <see cref="JsonStepResult"/>. It stays an <see cref="LlmStepResult{T}"/>
/// so the step records the assistant message in the conversation and the usage metrics, which a
/// plain <see cref="JsonStepResult"/> would lose. Validation is <see cref="JsonStepResult.Validate"/>.
/// </summary>
public class LlmJsonStepResult(IStep step) : LlmStepResult<JToken>(step)
{
    /// <summary>Schema the value must satisfy. Null means "any JSON".</summary>
    public JsonSchema? Schema { get; set; }

    /// <inheritdoc/>
    public override async Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
    {
        (bool isValid, string? error) = JsonStepResult.Validate(Value, Schema);
        return isValid ? await base.ValidateAsync(context) : (false, error);
    }
}
