using AITaskAgent.Core.Abstractions;
using System.Reflection;

namespace AITaskAgent.Core.StepResults;

/// <summary>
/// Base class for step results with automatic parameter extraction.
/// </summary>
public abstract class StepResult(IStep step, object? value = null) : IStepResult
{

    internal object? _value = value;
    internal List<IStep> _nextSteps = [];
    /// <summary>Gets the result value (implementation for IStepResult).</summary>
    public object? Value { get => _value; internal protected set => _value = value; }
    public IStep Step => step;


    /// <summary>Gets whether this is an error result.</summary>
    public bool HasError => Error != null;

    /// <summary>Gets error information if IsError is true.</summary>
    public IStepError? Error { get; set; }


    /// <summary>Validates the result content. Override for custom validation.</summary>
    public virtual Task<(bool IsValid, string? Error)> ValidateAsync()
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
        var properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            // Skip Value and IsError
            if (prop.Name is nameof(IStepResult.Value) or nameof(HasError))
            {
                continue;
            }

            var value = prop.GetValue(this);

            // Add PascalCase
            parameters[prop.Name] = value;

            // Add camelCase
            var camelCase = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
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
    new public T? Value { get => (T?)_value; internal protected set => _value = value; }


}


