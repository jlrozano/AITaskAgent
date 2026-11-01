using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
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

    /// <summary>
    /// Fails before the run starts when a step, or a child of a composite step whose children are
    /// known up front (Group, Parallel, Loop, and every case and the default of the declarative
    /// <see cref="DeclarativeSwitchStep"/>), needs a context type the pipeline was not given.
    /// </summary>
    /// <remarks>
    /// Children decided at run time (the target returned by a programmatic <c>SwitchStep&lt;TIn&gt;</c>
    /// selector, any step's <c>NextSteps</c>) cannot be seen here; the pipeline checks each step again
    /// right before running it.
    /// </remarks>
    public static void ValidateRequiredContext(IReadOnlyList<IStep> steps, PipelineContext context, string pipelineName)
    {
        foreach (IStep step in steps)
        {
            var error = GetRequiredContextError(step, context, pipelineName);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            if (step is GroupStep groupStep)
            {
                ValidateRequiredContext(groupStep.Steps, context, pipelineName);
            }
            else if (step is ParallelStep parallelStep)
            {
                ValidateRequiredContext(parallelStep.Branches, context, pipelineName);
            }
            else if (step is LoopStep loopStep)
            {
                ValidateRequiredContext(loopStep.Steps, context, pipelineName);
            }
            else if (step is DeclarativeSwitchStep switchStep)
            {
                // Every case and the default, whichever the selector would pick at run time.
                foreach (List<IStep> caseSteps in switchStep.Cases.Values)
                {
                    ValidateRequiredContext(caseSteps, context, pipelineName);
                }

                if (switchStep.Default is not null)
                {
                    ValidateRequiredContext(switchStep.Default, context, pipelineName);
                }
            }
        }
    }

    /// <summary>
    /// The message for a step whose <see cref="IStep.RequiredContext"/> the context does not
    /// satisfy, or null when it does.
    /// </summary>
    public static string? GetRequiredContextError(IStep step, PipelineContext context, string pipelineName)
    {
        var required = step.RequiredContext;
        return required is null || required.IsInstanceOfType(context)
            ? null
            : $"Pipeline '{pipelineName}': step '{step.Name}' ({step.GetType().Name}) requires a " +
              $"{required.Name}, but the pipeline context is a {context.GetType().Name}. " +
              $"Start the pipeline with a {required.Name}.";
    }
}
