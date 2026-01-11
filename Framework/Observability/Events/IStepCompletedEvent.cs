
namespace AITaskAgent.Observability.Events
{
    public interface IStepCompletedEvent : IProgressEvent
    {
        Dictionary<string, object>? AdditionalData => [];
        TimeSpan Duration { get; init; }
        string? ErrorMessage { get; init; }

    }
}
