using System.Reflection;
using System.Resources;

namespace BRMS.Core.Models;

/// <summary>
/// Enumeration of validation message keys
/// </summary>
public enum ValidationMessageKeys
{
    ValidationError_RuleNotFound,
    ValidationError_MissingName,
    ValidationError_InvalidConfiguration,
    Schema_LessThanMinimum,
    Schema_GreaterThanMaximum,
    Schema_Required,
    Schema_InvalidType,
    Schema_NonUniqueArray,
    Range_MinGreaterThanMax,
    TextLength_MinGreaterThanMax,
    List_EmptyLists,
    List_CommonElements,
    Count_MinGreaterThanMax,
    Count_NegativeValue,
    Regex_InvalidPattern,
    Regex_EmptyPattern,
    TransformationError_NullResult,
    TransformationError_InvalidSourceValue,
    TransformationError_InvalidTargetValue,
    TransformationError_NullSourceValue
}

/// <summary>
/// Proporciona acceso centralizado a los mensajes de validación
/// </summary>
public static class ValidationMessages
{
    private static readonly ResourceManager ResourceManager = new(
        "BRMS.Abstractions.Resources.ValidationMessages",
        Assembly.GetExecutingAssembly());

    /// <summary>
    /// Obtiene un mensaje de validación usando el enum
    /// </summary>
    /// <param name="key">Clave del mensaje</param>
    /// <param name="args">Argumentos para formatear el mensaje</param>
    /// <returns>Mensaje formateado</returns>
    public static string GetMessage(ValidationMessageKeys key, params object[] args)
    {
        string message = ResourceManager.GetString(key.ToString()) ?? key.ToString();
        return args.Length > 0 ? string.Format(message, args) : message;
    }

    /// <summary>
    /// Obtiene un mensaje de validación usando string (para compatibilidad)
    /// </summary>
    /// <param name="key">Clave del mensaje</param>
    /// <param name="args">Argumentos para formatear el mensaje</param>
    /// <returns>Mensaje formateado</returns>
    public static string GetMessage(string key, params object[] args)
    {
        string message = ResourceManager.GetString(key) ?? key;
        return args.Length > 0 ? string.Format(message, args) : message;
    }

    /// <summary>
    /// Formatea un mensaje de error de validación con el índice y nombre de la regla
    /// </summary>
    public static string FormatValidationError(int index, string ruleName, string error)
        => GetMessage(ValidationMessageKeys.ValidationError_InvalidConfiguration, index, ruleName, error);

    /// <summary>
    /// Traduce un mensaje de error de JsonSchema
    /// </summary>
    public static string TranslateSchemaError(string error)
    {
        // Traducciones comunes de errores de JsonSchema
        if (error.Contains("is less than minimum value"))
        {
            string[] parts = error.Split(' ');
            string value = parts[1];
            string minimum = parts[^1].TrimEnd('.');
            return GetMessage(ValidationMessageKeys.Schema_LessThanMinimum, value, minimum);
        }

        if (error.Contains("is greater than maximum value"))
        {
            string[] parts = error.Split(' ');
            string value = parts[1];
            string maximum = parts[^1].TrimEnd('.');
            return GetMessage(ValidationMessageKeys.Schema_GreaterThanMaximum, value, maximum);
        }

        if (error.Contains("Non-unique array item at index"))
        {
            string index = error.Split(' ')[^3];
            return GetMessage(ValidationMessageKeys.Schema_NonUniqueArray, index);
        }

        if (error.Contains("Required properties are missing"))
        {
            string property = error.Split('\'')[1];
            return GetMessage(ValidationMessageKeys.Schema_Required, property);
        }

        // Si no hay traducción específica, devolver el error original
        return error;
    }
}
