using BRMS.Core.Constants;

namespace BRMS.Core.Extensions;

/// <summary>
/// Manejador personalizado de errores de validación de JsonSchema que traduce los mensajes
/// </summary>
public class SchemaValidationErrorHandler
{
    private readonly List<string> _errors = [];

    /// <summary>
    /// Obtiene los errores de validación traducidos
    /// </summary>
    public IEnumerable<string> Errors => _errors;

    /// <summary>
    /// Añade un error de validación traducido
    /// </summary>
    public void AddError(string error)
    {
        string translatedMessage = ResourcesManager.GetLocalizedMessage("VALIDATION_TranslateSchemaError", (object)error);
        _errors.Add(translatedMessage);
    }

    /// <summary>
    /// Añade un error de validación sin traducir
    /// </summary>
    /// <param name="error">Mensaje de error sin procesar</param>
    public void AddRawError(string error)
    {
        _errors.Add(error);
    }
}
