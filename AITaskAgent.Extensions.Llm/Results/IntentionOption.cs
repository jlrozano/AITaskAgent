namespace AITaskAgent.LLM.Results;

/// <summary>
/// Represents a classification option for dynamic intention analysis.
/// </summary>
public sealed class IntentionOption
{
    /// <summary>
    /// Unique identifier for the option (e.g., "CreateTicket", "SearchDocs").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Description of when this option should be selected.
    /// </summary>
    public required string Description { get; init; }
}
