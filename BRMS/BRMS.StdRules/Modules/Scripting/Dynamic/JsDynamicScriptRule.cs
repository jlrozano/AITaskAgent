using System.Dynamic;
using BRMS.Core.Attributes;
using BRMS.Core.Models;
using Newtonsoft.Json.Linq;


namespace BRMS.StdRules.Modules.Scripting.Dynamic;

public abstract class JsDynamicScriptRule : JsScriptRule
{

    public virtual void Configure(DynamicRuleConfiguration ruleConfig, JObject? values)
    {
        RuleId = ruleConfig.RuleId;
        Expression = ruleConfig.Expression;
        ErrorMessage = ruleConfig.ErrorMessage;
        ErrorSeverityLevel = ruleConfig.ErrorSeverityLevel;
        InputTypes = ruleConfig.InputTypes;

        var parameters = new ExpandoObject() as IDictionary<string, object?>;

        foreach (ParameterDescription parameter in ruleConfig.AdditionalParameters)
        {
            parameters.Add(parameter.Name, values?[parameter.Name]?.Value<dynamic>());
        }

        parameters.Add("errorMessage", ErrorMessage!); // Fixed nullability
        parameters.Add("errorSeverityLevel", ruleConfig.ErrorSeverityLevel.ToString());
        AddCustomParameters(parameters!);
        AddHostObject("parameters", (parameters as ExpandoObject)!);

    }

    protected virtual void AddCustomParameters(IDictionary<string, object?> customParameters)
    {

    }

    [StaticRuleDescription]
    public static RuleDescription? GetStaticRuleDescription(Type type)
    {
        return DynamicRuleManager.RuleDescriptionFromJsType(type);
    }
}
