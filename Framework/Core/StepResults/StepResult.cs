using AITaskAgent.Core.Abstractions;
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

    public StepResult(IStep step, object? value = null)
    {
        ArgumentNullException.ThrowIfNull(step, nameof(step));
        _value = value;
        StepName = step.Name;
        StepType = step.GetType().Name;
    }
    /// <summary>Gets the result value (implementation for IStepResult).</summary>
    public object? Value { get => _value; internal protected set => _value = value; }

    public string StepName { get; }

    public string StepType { get; }
    /// <summary>Gets whether this is an error result.</summary>
    public bool HasError => Error != null;

    /// <summary>Gets error information if IsError is true.</summary>
    public IStepError? Error { get; set; }

    /// <summary>Gets or sets information related to suspension.</summary>
    public object? SuspendInfo { get; set; }


    /// <summary>Validates the result content. Override for custom validation.</summary>
    public virtual Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
    {
        return Task.FromResult((true, (string?)null));
    }

    /// <summary>
    /// Returns additional steps to execute after this result.
    /// Override in routing steps to return dynamically determined steps.
    /// </summary>
    public virtual List<IStep> NextSteps { get => _nextSteps; internal protected set => _nextSteps = value?.ToList() ?? []; }

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


