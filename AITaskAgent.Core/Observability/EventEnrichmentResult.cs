namespace AITaskAgent.Observability;

/// <summary>
/// Result of event enrichment, containing the modified event and suppression decision.
/// </summary>
/// <typeparam name="TEvent">Type of the event being enriched.</typeparam>
public readonly record struct EventEnrichmentResult<TEvent>(
    TEvent Event,
    bool Suppress
) where TEvent : IProgressEvent;
