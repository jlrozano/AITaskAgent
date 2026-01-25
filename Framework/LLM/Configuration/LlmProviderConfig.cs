using AITaskAgent.Support.JSON;

namespace AITaskAgent.LLM.Configuration;

/// <summary>
/// Configuration for a specific LLM provider.
/// Defines all parameters needed to connect to an LLM provider.
/// </summary>
public sealed class LlmProviderConfig
{
    /// <summary>
    /// Provider name (OpenRouter, Ollama, AzureOpenAI, etc.).
    /// Informational only, does not affect functionality.
    /// </summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Base URL of the LLM service.
    /// Example: "https://openrouter.ai/api/v1", "http://localhost:11434/v1"
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// API Key for authentication.
    /// Supports environment variables with ${VAR_NAME} syntax.
    /// Example: "${OPENROUTER_API_KEY}"
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Model to use.
    /// Example: "openai/gpt-4o", "deepseek-ai/DeepSeek-V3"
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Default temperature (0.0 - 2.0).</summary>
    public float? Temperature { get; init; }

    /// <summary>Maximum tokens in the response.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Top P for nucleus sampling.</summary>
    public float? TopP { get; init; }

    /// <summary>Top K for sampling.</summary>
    public int? TopK { get; init; }

    /// <summary>Frequency penalty (-2.0 - 2.0).</summary>
    public float? FrequencyPenalty { get; init; }

    /// <summary>Presence penalty (-2.0 - 2.0).</summary>
    public float? PresencePenalty { get; init; }

    /// <summary>Enable reasoning/thinking output (o1, Gemini Thinking, DeepSeek R1). Overrides Profile.</summary>
    public bool? EnableThinking { get; init; }

    /// <summary>Budget for thinking tokens. Overrides Profile.</summary>
    public int? ThinkingBudget { get; init; }

    /// <summary>Number of chat completion choices to generate for each input message.</summary>
    public int? ChoiceCount { get; init; }

    // Pricing configuration

    /// <summary>
    /// Price per million input tokens in USD.
    /// Used to calculate the cost of LLM calls.
    /// If not specified, cost calculation will return 0.
    /// </summary>
    public decimal? InputTokenPricePerMillion { get; init; }

    /// <summary>
    /// Price per million output tokens in USD.
    /// Used to calculate the cost of LLM calls.
    /// If not specified, cost calculation will return 0.
    /// </summary>
    public decimal? OutputTokenPricePerMillion { get; init; }

    // Azure OpenAI specific properties

    /// <summary>
    /// Deployment name (Azure OpenAI specific).
    /// </summary>
    public string? DeploymentName { get; init; }

    /// <summary>
    /// API version (Azure OpenAI specific).
    /// Example: "2024-02-01"
    /// </summary>
    public string? ApiVersion { get; init; }

    /// <summary>
    /// Structured JSON response capability of the model.
    /// Determines whether to use json_object, json_schema, or prompt injection.
    /// </summary>
    public JsonResponseCapability JsonCapability { get; init; } = JsonResponseCapability.None;

}
