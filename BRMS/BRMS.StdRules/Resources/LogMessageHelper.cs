using BRMS.StdRules.Constants;

namespace BRMS.StdRules.Resources;

/// <summary>
/// Helper centralizado para acceder a los mensajes de logging desde el archivo de recursos consolidado
/// </summary>
public static class LogMessageHelper
{
    /// <summary>
    /// Obtiene un mensaje de logging desde el archivo de recursos usando la clave especificada
    /// </summary>
    /// <param name="key">La clave del mensaje en el archivo de recursos</param>
    /// <returns>El mensaje localizado o la clave si no se encuentra el mensaje</returns>
    public static string GetMessage(string key)
    {
        try
        {
            string message = ResourcesKeys.GetString(key);
            // Si no encontramos la clave sin prefijo, intentamos con el prefijo "Log_"
            if (message == null || message == key)
            {
                string alt = ResourcesKeys.GetString($"Log_{key}");
                return alt ?? key;
            }
            return message;
        }
        catch
        {
            return key;
        }
    }

    /// <summary>
    /// Obtiene un mensaje de logging formateado desde el archivo de recursos
    /// </summary>
    /// <param name="key">La clave del mensaje en el archivo de recursos</param>
    /// <param name="args">Argumentos para formatear el mensaje</param>
    /// <returns>El mensaje localizado y formateado</returns>
    public static string GetFormattedMessage(string key, params object[] args)
    {
        try
        {
            string message = ResourcesKeys.GetString(key);
            if (message == null || message == key)
            {
                string alt = ResourcesKeys.GetString($"Log_{key}");
                message = alt ?? key;
            }
            return args?.Length > 0 ? string.Format(message, args) : message;
        }
        catch
        {
            return key;
        }
    }
}
