using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// A deterministic step that executes a side effect.
/// </summary>
/// <typeparam name="TIn">The type of the input and output.</typeparam>
public sealed class ActionStep<TIn>(string name,
    Func<TIn, PipelineContext, int, ActionResult?, Task> action,
    bool fireAndForget = false)
    : TypedStep<TIn, ActionResult>(name) where TIn : IStepResult
{
    protected override async Task<ActionResult> ExecuteAsync(TIn input, PipelineContext context, int Attempt, ActionResult? LastStepResult, CancellationToken cancellationToken)
    {
        if (fireAndForget)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await action(input, context, 1, null);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error executing Fire-and-Forget ActionStep {StepName}", Name);
                }
            }, cancellationToken);
            return ActionResult.FromSuccessMessage(this, $"Fire-and-Forget ActionStep {Name} success.");
        }
        else
        {
            try
            {
                await action(input, context, Attempt, LastStepResult);
                return ActionResult.FromSuccessMessage(this, $"The action {Name} was executed successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error executing ActionStep {StepName}", Name);
                return ActionResult.FromExceptionAction(this, ex, $"The action {Name} failed with error: {ex.Message}");
            }
        }
    }


}
