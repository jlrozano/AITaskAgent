using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;

namespace AITaskAgent.LLM.Results;

/// <summary>
/// StepResult wrapper for Intention to make it compatible with SwitchStep.
/// </summary>
/// <typeparam name="TEnum">Enum type representing user intentions.</typeparam>
public sealed class IntentionStepResult<TEnum>(IStep step, Intention<TEnum>? value)
    : StepResult<Intention<TEnum>>(step, value)
    where TEnum : struct, Enum
{
}
