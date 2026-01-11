using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// A step that delegates its execution to a provided function.
/// Useful for simple inline transformations.
/// </summary>
/// <typeparam name="TIn">Input step result type.</typeparam>
/// <typeparam name="TOut">Output step result type.</typeparam>
public class DelegatedStep<TIn, TOut>(
    string name,
    Func<TIn, PipelineContext, int, TOut?, Task<TOut>> action)
    : TypedStep<TIn, TOut>(name) where TIn : IStepResult where TOut : IStepResult
{
    protected override async Task<TOut> ExecuteAsync(
        TIn input,
        PipelineContext context,
        int attempt,
        TOut? lastStepResult,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(input, context, attempt, lastStepResult);
        }
        catch (Exception ex)
        {
            return CreateErrorResult($"DelegatedStep {Name} failed with error: {ex.Message}", ex);
        }
    }


}
