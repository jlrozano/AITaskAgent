namespace AITaskAgent.LLM.Models;

/// <summary>
/// A failure of the LLM provider itself (HTTP error, empty response, broken stream, open circuit...), as opposed
/// to a failure of the step's own logic. It is the only error that <c>BaseLlmStep.SwitchToNextFallback</c> answers
/// by moving to the next provider: switching engines cannot fix a template bug or a tool loop.
/// </summary>
public sealed class LlmProviderException : Exception
{
    /// <summary>Whether the provider reported the failure as transient (<see cref="LlmResult.IsRetriable"/>).</summary>
    public bool IsRetriable { get; }

    public LlmProviderException(string message, Exception? innerException, bool isRetriable)
        : base(message, innerException)
    {
        IsRetriable = isRetriable;
    }
}
