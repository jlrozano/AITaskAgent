using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BRMS.Core.Models;

/// <summary>
/// Severidad de la validación: advertencia o error.
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum ErrorSeverityLevelEnum
{
    /// <summary>
    /// La validación genera una incidencia.
    /// </summary>
    Issue,
    /// <summary>
    /// La validación genera un error.
    /// </summary>
    Error
}
