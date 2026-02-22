using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BRMS.Core.Models;

/// <summary>
/// Niveles posibles de resultado de una regla.
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum ResultLevelEnum
{

    /// <summary>
    /// La regla generó una incidencia.
    /// </summary>
    Issue,
    /// <summary>
    /// La regla generó un error.
    /// </summary>
    Error,
    /// <summary>
    /// La regla se ejecutó correctamente.
    /// </summary>
    Ok,
}
