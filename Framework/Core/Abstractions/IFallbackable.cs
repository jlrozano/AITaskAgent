namespace AITaskAgent.Core.Abstractions;

public interface IFallbackable
{
    bool SwitchToNextFallback(IStepResult error, int attempt);
}