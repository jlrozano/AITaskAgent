using System.Globalization;
using System.Reflection;
using System.Resources;

namespace BRMS.Core.Constants;

public static class ResourcesManager
{
    private static readonly List<ResourceManager> _resources = [
            new ResourceManager("BRMS.Abstractions.Resources.ConsolidatedResources", Assembly.GetExecutingAssembly())
        ];

    public static void AddResourceManager(ResourceManager resourceManager)
    {
        // Comparar por BaseName para evitar duplicados
        string baseName = resourceManager.BaseName;

        bool exists = _resources.Any(rm => rm.BaseName == baseName);

        if (!exists)
        {
            _resources.Add(resourceManager);
        }

    }

    /// <summary>
    /// Gets a localized message from ALL registered resource managers.
    /// </summary>
    /// <param name="key">The message key with prefix (CONFIG_, ERROR_, LOG_, VALIDATION_, UI_)</param>
    /// <param name="culture">The culture for localization (e.g., "es" for Spanish)</param>
    /// <returns>The localized message template in Markdown format</returns>
    public static string GetLocalizedMessage(string key, string? culture = null)
    {
        CultureInfo cultureInfo = string.IsNullOrEmpty(culture) ?
            System.Globalization.CultureInfo.CurrentCulture :
            new System.Globalization.CultureInfo(culture);

        foreach (ResourceManager resourceManager in _resources)
        {
            try
            {
                string? text = resourceManager.GetString(key, cultureInfo);
                if (text != null)
                {
                    return text;
                }
            }
            catch (Exception)
            {
                continue;
            }
        }

        return key;
    }

    /// <summary>
    /// Gets a formatted message from the consolidated resource manager.
    /// </summary>
    /// <param name="key">The message key with prefix (CONFIG_, ERROR_, LOG_, VALIDATION_, UI_)</param>
    /// <param name="args">Arguments to format into the message template</param>
    /// <returns>The formatted message in Markdown format</returns>
    public static string GetLocalizedMessage(string key, params object[] args)
    {
        string message = GetLocalizedMessage(key.ToString());
        return args?.Length > 0 ? string.Format(message, args) : message;
    }

    /// <summary>
    /// Gets a formatted localized message from ALL registered resource managers.
    /// </summary>
    /// <param name="key">The message key with prefix (CONFIG_, ERROR_, LOG_, VALIDATION_, UI_)</param>
    /// <param name="culture">The culture for localization (e.g., "es" for Spanish)</param>
    /// <param name="args">Arguments to format into the message template</param>
    /// <returns>The formatted localized message in Markdown format</returns>
    public static string GetLocalizedMessage(string key, string? culture, params object[] args)
    {
        string message = GetLocalizedMessage(key, culture);
        return args?.Length > 0 ? string.Format(message, args) : message;
    }

}
