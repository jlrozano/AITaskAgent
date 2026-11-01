using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Abstractions;

public interface IFallbackable
{
    bool SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt);
}