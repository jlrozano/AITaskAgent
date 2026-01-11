using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Generic base class for type-safe steps.
/// </summary>
public abstract class TypedStep<TIn, TOut>(string name) : StepBase(name, typeof(TIn), typeof(TOut)), IStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : IStepResult
{
    private readonly Lazy<StepResultFactory.StepActivatorInfo> _activatorInfo =
        new(() => StepResultFactory.GetStepActivatorInfo(typeof(TOut)));

    new protected TOut CreateResult(object? value = null, IStepError? error = null)
    {
        return (TOut)_activatorInfo.Value.CreateInstance(this, value, error);
    }

    new protected TOut CreateErrorResult(string message, Exception? exception = null)
    {
        return CreateResult(error: new StepError()
        {
            Message = message,
            OriginalException = exception
        });
    }

    protected abstract Task<TOut> ExecuteAsync(TIn input, PipelineContext context, int Attempt, TOut? LastStepResult, CancellationToken cancellationToken);

    Task<TOut> IStep<TIn, TOut>.ExecuteAsync(TIn input, PipelineContext context, int Attempt, TOut? lastStepResult, CancellationToken cancellationToken) =>
        ExecuteAsync(input, context, Attempt, lastStepResult, cancellationToken);

    protected async override Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {

        if (input.GetType().IsAssignableTo(typeof(TIn))) //&& (lastStepResult?.GetType().IsAssignableTo(typeof(TOut)) ?? true)))
        {
            return await ExecuteAsync((TIn)input, context, attempt, (TOut?)lastStepResult, cancellationToken);
        }
        else
        {
            try
            {
                var newInput = StepResultFactory.CreateStepResult<TIn>(this, input.Value);
                return await ExecuteAsync(newInput, context, attempt, (TOut?)lastStepResult, cancellationToken);
            }
            catch
            {
                throw new InvalidOperationException(
                $"Step '{Name}' expected input type '{typeof(TIn).Name}' but received '{input.GetType().Name}'.");
            }
        }
    }
}
