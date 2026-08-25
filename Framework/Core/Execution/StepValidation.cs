using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Steps;

namespace AITaskAgent.Core.Execution;

public static class StepValidation
{
    public static void ValidateUniqueStepNames(IReadOnlyList<IStep> steps, string pipelineName, Dictionary<string, string>? existingNames = null)
    {
        existingNames ??= [];
        foreach (IStep step in steps)
        {
            if (existingNames.ContainsKey(step.Name))
            {
                throw new InvalidOperationException(
                    $"Pipeline '{pipelineName}' has duplicate step name: '{step.Name}' (conflict with {existingNames[step.Name]})");
            }
            existingNames[step.Name] = step.GetType().Name;

            if (step is GroupStep groupStep)
            {
                ValidateUniqueStepNames(groupStep.Steps, pipelineName, new Dictionary<string, string>(existingNames));
            }
            else if (step is ParallelStep parallelStep)
            {
                ValidateUniqueStepNames(parallelStep.Branches, pipelineName, new Dictionary<string, string>(existingNames));
            }
            else if (step is LoopStep loopStep)
            {
                ValidateUniqueStepNames(loopStep.Steps, pipelineName, new Dictionary<string, string>(existingNames));
            }
        }
    }
}