namespace AITaskAgent.Core.Abstractions
{
    public interface IStepError
    {
        string Message { get; init; }
        Exception? OriginalException { get; init; }

    }
}

