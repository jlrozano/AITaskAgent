namespace AITaskAgent.Core.Models;

/// <summary>
/// Exception thrown when an LLM provider returns a response with no choices
/// (e.g., empty response from OpenRouter models on free tier).
/// This exception is retriable as it often indicates a transient condition.
/// </summary>
public sealed class LlmResponseEmptyException : Exception
{
    public string Model { get; }
    public string Provider { get; }

    public LlmResponseEmptyException(string model, string provider)
        : base($"LLM provider '{provider}' returned an empty response for model '{model}' (0 choices)")
    {
        Model = model;
        Provider = provider;
    }

    public LlmResponseEmptyException(string model, string provider, Exception innerException)
        : base($"LLM provider '{provider}' returned an empty response for model '{model}' (0 choices)", innerException)
    {
        Model = model;
        Provider = provider;
    }
}