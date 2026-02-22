using BRMS.Core.Abstractions;
using Newtonsoft.Json;

namespace BRMS.Core.Models;

/// <summary>
/// Representa el resultado de la ejecución de una regla, incluyendo información relevante para auditoría y notificación de cambios.
/// </summary>
public class RuleResult(IRule rule, BRMSExecutionContext context, string? message = null) : IRuleResult
{
    /// <summary>
    /// Contexto de ejecución asociado a la regla.
    /// </summary>
    [JsonIgnore]
    public BRMSExecutionContext Context { get; } = context;
    /// <summary>
    /// Mensaje adicional o de error asociado al resultado.
    /// </summary>
    public string? Message { get; } = message;
    /// <summary>
    /// Nivel de resultado (Ok, Warning, Error).
    /// </summary>
    public ResultLevelEnum ResultLevel { get; } = string.IsNullOrWhiteSpace(message) ? ResultLevelEnum.Ok :
                                                    (ResultLevelEnum)(int)rule.ErrorSeverityLevel;
    /// <summary>
    /// Regla aplicada a la propiedad.
    /// </summary>
    public string RuleId => Rule.RuleId;
    public string PropertyPath => Rule.PropertyPath;
    /// <summary>
    /// Propiedad setteable para compatibilidad con código legacy
    /// </summary>
    public bool Success => ResultLevel == ResultLevelEnum.Ok;
    /// <summary>
    /// Excepción asociada al resultado (si la hay)
    /// </summary>
    [JsonIgnore]
    public Exception? Exception { get; set; }

    /// <summary>
    /// Instancia de la regla que generó este resultado
    /// </summary>
    [JsonIgnore]
    public IRule Rule { get; } = rule;

    public static RuleResult Ok(IRule rule, BRMSExecutionContext context) => new(rule, context, null);
    public static RuleResult Fail(IRule rule, BRMSExecutionContext context, string errorMessage) => new(rule, context,
        string.IsNullOrWhiteSpace(errorMessage) ? "Error" : errorMessage);
}
