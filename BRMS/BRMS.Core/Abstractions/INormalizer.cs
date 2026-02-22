using BRMS.Core.Models;

namespace BRMS.Core.Abstractions;

/// <summary>
/// Contrato para normalizadores de datos.
/// </summary>
public interface INormalizer : IRule
{
    /// <summary>
    /// Indica si el normalizador debe notificar cuando realiza cambios en los datos
    /// </summary>
    bool MustNotifyChange { get; }

    /// <summary>
    /// Ejecuta la normalización sobre el contexto proporcionado
    /// </summary>
    /// <param name="context">Contexto de ejecución con los datos a normalizar</param>
    /// <param name="cancellationToken">Token de cancelación</param>
    /// <returns>Resultado de la normalización</returns>
    new Task<INormalizerResult> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken = default);
}
