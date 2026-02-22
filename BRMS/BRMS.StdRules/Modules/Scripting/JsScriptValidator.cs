// BRMS.StdRules, Version=1.0.1.0, Culture=neutral, PublicKeyToken=null
// BRMS.StdRules.JsScript.JsScriptValidator
using System.ComponentModel;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;

namespace BRMS.StdRules.Modules.Scripting;

public class ScriptRuleResult(IValidator rule,
        BRMSExecutionContext context,
        IEnumerable<ConsoleMessage> console,
        string? errorMessage = null
        ) :
    RuleResult(rule, context, errorMessage)
{
    public IEnumerable<ConsoleMessage> Console => console;
}

[Description(ResourcesKeys.Desc_JsScriptValidator_Description)]
[RuleName("JSValidator")]
public class JsScriptValidator : JsScriptRule, IValidator
{

    private IRuleResult InternalInvoke(BRMSExecutionContext context)
    {
        ScriptRuleResult? res = null;
        ScriptExecutionResult scriptResult = ExecuteScript(context, true);
        if (!scriptResult.Success)
        {
            res = new ScriptRuleResult(this, context, scriptResult.Console, scriptResult.ErrorMessage);
        }

        if (scriptResult.Result is not bool and not null)
        {
            res = new ScriptRuleResult(this, context, scriptResult.Console, "La expresión debe devolver un valor boolean.");
        }

        res ??= new ScriptRuleResult(this, context, scriptResult.Console);

        return res;
    }
    public Task<object> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken = default)
    {

        return Task.FromResult(InternalInvoke(context) as object);
    }
    protected override ILogger Logger => base.Logger;
    Task<IRuleResult> IRule<IRuleResult>.Invoke(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(InternalInvoke(context));
    }
}
