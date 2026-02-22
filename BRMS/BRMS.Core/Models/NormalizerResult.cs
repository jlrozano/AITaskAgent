using BRMS.Core.Abstractions;
using BRMS.Core.Extensions;

namespace BRMS.Core.Models;

/// <summary>
/// Resultado de la ejecución de una regla de normalización.
/// </summary>
public class NormalizerResult(INormalizer rule, BRMSExecutionContext context, string? errorMessage = null, bool? hasChanges = null) : RuleResult(rule, context,
         errorMessage), INormalizerResult
{
    private object? _oldValue;
    private object? _newValue;

    /// <summary>
    /// Valor anterior de la propiedad.
    /// </summary>
    public object? OldValue
    {
        get
        {
            _oldValue ??= Context.OldValue?.GetValueAs<object>(Rule.PropertyPath);
            return _oldValue;
        }
        set => _oldValue = value;
    }

    /// <summary>
    /// Valor nuevo de la propiedad.
    /// </summary>
    public object? NewValue
    {
        get
        {
            _newValue ??= Context.NewValue?.GetValueAs<object>(Rule.PropertyPath);
            return _newValue;
        }
        set => _newValue = value;
    }

    /// <summary>
    /// Indica si el valor ha cambiado tras la normalización.
    /// </summary>
    public bool Changed => hasChanges == null ? (!Equals(OldValue, NewValue)) : hasChanges.Value;

    public static NormalizerResult Fail(INormalizer rule, BRMSExecutionContext context, string errorMessage) =>
        new(rule, context, string.IsNullOrWhiteSpace(errorMessage) ? "Error" : errorMessage);

    public static NormalizerResult Ok(INormalizer rule, BRMSExecutionContext context, bool? hasChanges = null) =>
        new(rule, context, null, hasChanges);

}
