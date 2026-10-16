namespace AITaskAgent.LLM.Models;

/// <summary>
/// Thrown by a tool to end the step's tool loop: the model keeps asking for something the tool will not give, and
/// another round-trip cannot change that. The message goes to the conversation as the tool's result, the rest of the
/// batch still runs, and the step ends the loop as the repeat detector does - a final candidate carrying the message in
/// <see cref="LlmResponse.ToolLoopStopMessage"/>, with no further LLM call.
/// </summary>
public sealed class ToolLoopStopRequestedException(string message) : Exception(message);
