using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using System.Reflection;

namespace AITaskAgent.Core.StepResults;

/// <summary>
/// Base class for step results with automatic parameter extraction.
/// </summary>
public abstract class StepResult : IStepResult
{

    private object? _value;
    private List<IStep> _nextSteps = [];
    private IReadOnlyList<IResultValidator> _resultValidators = [];

    public StepResult(IStep step, object? value = null)
    {
        ArgumentNullException.ThrowIfNull(step, nameof(step));
        _value = value;
        StepName = step.Name;
        StepType = step.GetType().Name;
    }
    /// <summary>Gets the result value (implementation for IStepResult).</summary>
    public object? Value { get => _value; internal protected set => _value = value; }

    public string StepName { get; private set; }

    public string StepType { get; private set; }

    /// <summary>
    /// Gives a result rebuilt from storage the identity of the step that produced it. A stored result
    /// is re-created around a stand-in step, so without this its <see cref="StepType"/> would name the
    /// stand-in instead of the original step type.
    /// </summary>
    internal void RestoreIdentity(string stepName, string stepType)
    {
        StepName = stepName;
        StepType = stepType;
    }
    /// <summary>Gets whether this is an error result.</summary>
    public bool HasError => Error != null;

    /// <summary>Gets error information if IsError is true.</summary>
    public IStepError? Error { get; set; }

    /// <summary>Gets or sets information related to suspension.</summary>
    public object? SuspendInfo { get; set; }


    /// <summary>
    /// Validators the producing step attached to this result (YAML <c>resultValidators</c>). Never serialized:
    /// a delegate cannot be stored, the step stamps them again every time it produces a result.
    /// </summary>
    public IReadOnlyList<IResultValidator> ResultValidators => _resultValidators;

    /// <summary>
    /// Replaces the validators <see cref="ValidateAsync"/> runs after the result's own check. The step
    /// stamps its list on every result it returns, so a result passed through another step carries the
    /// latest owner's validators, not an accumulation.
    /// </summary>
    public void AttachResultValidators(IEnumerable<IResultValidator> validators)
    {
        ArgumentNullException.ThrowIfNull(validators);
        _resultValidators = [.. validators];
    }

    /// <summary>
    /// Validates the result content. The default runs the attached <see cref="ResultValidators"/> in
    /// order (first failure wins). Overrides must call <c>base.ValidateAsync</c> once their own check
    /// passes, or the attached validators are skipped for that result type.
    /// </summary>
    public virtual async Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
    {
        foreach (IResultValidator validator in _resultValidators)
        {
            (bool isValid, string? error) = await validator.ValidateAsync(this, context, CancellationToken.None);
            if (!isValid)
            {
                return (false, error);
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Returns additional steps to execute after this result.
    /// Override in routing steps to return dynamically determined steps.
    /// </summary>
    public virtual List<IStep> NextSteps { get => _nextSteps; internal protected set => _nextSteps = value?.ToList() ?? []; }

    /// <summary>
    /// Shallow copy with its own, empty <see cref="NextSteps"/>: same concrete type, same value
    /// reference. <c>ParallelStep</c> hands one to each branch, because a step that passes its
    /// input through and schedules children on it (<c>GroupStep</c>, a switch) would otherwise write
    /// into the <see cref="NextSteps"/> of the one input object every branch shares, and another
    /// branch could run those children.
    /// </summary>
    internal StepResult CopyForBranch()
    {
        StepResult copy = (StepResult)MemberwiseClone();
        copy._nextSteps = [];
        return copy;
    }

    /// <summary>
    /// Gets parameters for template rendering using reflection.
    /// Extracts all public properties in both PascalCase and camelCase.
    /// </summary>
    public virtual Dictionary<string, object?> GetParameters()
    {
        Dictionary<string, object?> parameters = [];
        PropertyInfo[] properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (PropertyInfo prop in properties)
        {
            // Skip Value and IsError
            if (prop.Name is nameof(IStepResult.Value) or nameof(HasError))
            {
                continue;
            }

            // An indexer (ParallelResult's this[stepName]) has no value without an argument; templates reach it directly.
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? value = prop.GetValue(this);

            // Add PascalCase
            parameters[prop.Name] = value;

            // Add camelCase
            string camelCase = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            parameters[camelCase] = value;
        }

        return parameters;
    }
}
/// <summary>
/// Base class for strongly-typed step results with automatic parameter extraction.
/// </summary>
/// <typeparam name="T">The type of the result value.</typeparam>
public class StepResult<T>(IStep step, T? value = default) : StepResult(step, value), IStepResult<T>
{
    /// <summary>Gets the strongly-typed result value.</summary>
    new public T? Value { get => (T?)base.Value; internal protected set => base.Value = value; }


}


