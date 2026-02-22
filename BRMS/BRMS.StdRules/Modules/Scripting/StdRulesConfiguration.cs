// EngineConfig.cs

// EngineConfig.cs
using BRMS.StdRules.Modules.Scripting.Dynamic;

namespace BRMS.StdRules.Modules.Scripting;

public class StdRulesConfiguration
{

    public IList<DynamicRuleConfiguration> JsRules { get; set; } = [];
    public string? LibPath { get; set; }
}
