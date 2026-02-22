using BRMS.Core.Abstractions;
using BRMS.Core.Models;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Modules.Scripting.Dynamic;

public abstract class DynamicJsScriptNormalizer : JsDynamicScriptRule, INormalizer
{
    public bool MustNotifyChange { get; private set; }

    /// <summary>
    /// Constructor interno para crear instancias dinámicas
    /// </summary>
    public DynamicJsScriptNormalizer()
    {
    }

    public override void Configure(DynamicRuleConfiguration ruleConfig, JObject? values)
    {
        base.Configure(ruleConfig, values);
        MustNotifyChange = values?.Value<bool>("mustNotifyChange") ?? false;
    }

    protected override void AddCustomParameters(IDictionary<string, object?> customParameters)
    {
        base.AddCustomParameters(customParameters);
        customParameters.Add("mustNotifyChange", MustNotifyChange);
    }

    private Task<INormalizerResult> InternalInvoke(BRMSExecutionContext context)
    {
        INormalizerResult res;
        ScriptExecutionResult scriptResult = ExecuteScript(context, false);

        res = !scriptResult.Success
            ? new ScriptNormalizerResult(this, context, scriptResult.Console, scriptResult.ErrorMessage)
            : new ScriptNormalizerResult(this, context, scriptResult.Console, null, (bool?)scriptResult.Result);

        return Task.FromResult(res);
    }
    Task<INormalizerResult> INormalizer.Invoke(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        return InternalInvoke(context);
    }

    async Task<object> IRule.Invoke(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        return await InternalInvoke(context);
    }
}
