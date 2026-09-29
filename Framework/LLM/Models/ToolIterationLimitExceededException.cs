namespace AITaskAgent.LLM.Models;

/// <summary>
/// The step reached <c>BaseLlmStep.MaxToolIterations</c> while the model was still asking for tools. It is not a
/// provider failure, so the default fallback does not switch engines on it; a derived step can recognise it by type
/// in its own <c>SwitchToNextFallback</c> and choose another strategy. The conversation still holds every tool result
/// gathered so far when this error reaches the fallback.
/// </summary>
public sealed class ToolIterationLimitExceededException : InvalidOperationException
{
    public string StepName { get; }
    public int MaxToolIterations { get; }

    public ToolIterationLimitExceededException(string stepName, int maxToolIterations)
        : base($"Maximum tool iterations ({maxToolIterations}) exceeded - possible infinite loop")
    {
        StepName = stepName;
        MaxToolIterations = maxToolIterations;
    }
}
