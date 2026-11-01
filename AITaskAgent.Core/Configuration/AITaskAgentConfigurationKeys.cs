namespace AITaskAgent.Configuration;

/// <summary>
/// Constants for framework configuration keys.
/// </summary>
public static class AITaskAgentConfigurationKeys
{
    /// <summary>Root configuration section of the framework.</summary>
    public const string RootSection = "AITaskAgent";

    /// <summary>LLM providers configuration section.</summary>
    public const string LlmProviders = $"{RootSection}:LlmProviders";

    /// <summary>Observability configuration section.</summary>
    public const string Observability = $"{RootSection}:Observability";

    /// <summary>Timeouts configuration section.</summary>
    public const string Timeouts = $"{RootSection}:Timeouts";

    /// <summary>Resilience configuration section.</summary>
    [Obsolete("No configuration section is bound under 'AITaskAgent:Resilience'; the real sections are 'AITaskAgent:CircuitBreaker' and 'AITaskAgent:RateLimit'.")]
    public const string Resilience = $"{RootSection}:Resilience";

    /// <summary>Conversation configuration section.</summary>
    public const string Conversation = $"{RootSection}:Conversation";

    /// <summary>Storage configuration section (<see cref="StorageOptions"/>).</summary>
    public const string Storage = $"{RootSection}:Storage";

    /// <summary>Storage of suspended pipeline contexts.</summary>
    public const string ContextStorage = $"{Storage}:Context";

    /// <summary>Storage of conversations.</summary>
    public const string ConversationStorage = $"{Storage}:Conversation";
}
