using System.ComponentModel;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;

namespace BRMS.StdRules.Modules.Scripting;

/// <summary>
/// Normalizador que ejecuta scripts JavaScript para transformar datos JSON.
/// Los normalizadores son mutables por defecto (pueden modificar datos).
/// </summary>
[Description(ResourcesKeys.Desc_JsScriptNormalizer_Description)]
[RuleName("JSNormalizer")]
public class JsScriptNormalizer : JsScriptRule, INormalizer

{

    internal JsScriptNormalizer()
    {
    }
    [DefaultValue(true)]
    [Description(ResourcesKeys.Desc_JsScriptNormalizer_NotifyChanges)]
    public bool MustNotifyChange { get; init; } = true;
    protected override ILogger Logger => base.Logger;
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
