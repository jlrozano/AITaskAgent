using System.Reflection;
using System.Resources;
using BRMS.Core.Abstractions;
using BRMS.Core.Constants;
using BRMS.Core.Core;
using BRMS.StdRules.Modules.Scripting;
using BRMS.StdRules.Modules.Scripting.Dynamic;
using Microsoft.Extensions.Logging;

namespace BRMS.StdRules;

/// <summary>
/// Plugin que registra los normalizadores y validadores estándar con soporte para configuración dinámica.
/// </summary>

public class StdRulesPlugin(IBRMSConfigurationProvider configProvider, ILogger<StdRulesPlugin> logger) : Plugin<StdRulesConfiguration>(configProvider, "StdRules")
{
    protected override Task Register(StdRulesConfiguration? config)
    {
        // Registrar managers de recursos para descripciones y mensajes de log de StdRules
        // Resources.resx (esp/eng) contiene las descripciones y los mensajes de log de los normalizadores/validadores
        ResourcesManager.AddResourceManager(new ResourceManager("BRMS.StdRules.Resources.Resources", Assembly.GetExecutingAssembly()));

        if (config != null && config.JsRules != null && config.JsRules.Count > 0)
        {
            _ = new DynamicRuleManager(config).LoadRulesAsync(logger);
        }

        return Task.CompletedTask;
    }

    protected override Task UnRegister(StdRulesConfiguration? config)
    {
        return Task.CompletedTask;
    }
}
