using BRMS.Core.Models;


namespace BRMS.Core.Abstractions;

/// <summary>
/// Contrato base para cualquier plugin del sistema.
/// </summary>


public interface IRule
{
    /// <summary>
    /// Nombre único de la regla
    /// </summary>
    string RuleId { get; }

    /// <summary>
    /// Ruta de la propiedad JSON sobre la que actúa esta regla
    /// </summary>
    string PropertyPath { get; }

    /// <summary>
    /// Indica si la regla produce un error,  si debe notificar como error o warning
    /// </summary>
    ErrorSeverityLevelEnum ErrorSeverityLevel { get; }
    /// <summary>
    /// Lista de tipos de entrada soportados por esta regla
    /// </summary>
    IReadOnlyList<RuleInputType> InputTypes { get; }
    /// <summary>
    /// Mensaje de error que se mostrará cuando la validación falle
    /// </summary>
    string? ErrorMessage { get; }
    /// <summary>
    /// Ejecuta la regla sobre el contexto proporcionado
    /// </summary>
    /// <param name="context">Contexto de ejecución que contiene los datos a procesar</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Resultado de la ejecución de la regla</returns>
    Task<object> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Contrato genérico para reglas que devuelven un tipo específico de resultado
/// </summary>
/// <typeparam name="T">Tipo de resultado que devuelve la regla</typeparam>
public interface IRule<T> : IRule where T : IRuleResult
{
    /// <summary>
    /// Ejecuta la regla sobre el contexto proporcionado
    /// </summary>
    /// <param name="context">Contexto de ejecución que contiene los datos a procesar</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Resultado tipado de la ejecución de la regla</returns>
    new Task<T> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken = default);
}
