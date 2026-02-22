using BRMS.StdRules.Constants;

namespace BRMS.StdRules.Resources;

/// <summary>
/// Helper para acceder a las descripciones desde ResourcesManager
/// </summary>
public static class DescriptionHelper
{
    /// <summary>
    /// Obtiene una descripción del ResourcesManager usando la clave especificada
    /// </summary>
    /// <param name="key">Clave de la descripción</param>
    /// <returns>Descripción localizada o la clave si no se encuentra</returns>
    public static string GetDescription(string key)
    {
        try
        {
            return ResourcesKeys.GetString(key) ?? key;
        }
        catch
        {
            return key;
        }
    }
}
