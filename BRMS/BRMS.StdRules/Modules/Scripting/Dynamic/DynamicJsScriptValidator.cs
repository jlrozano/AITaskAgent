using BRMS.Core.Abstractions;
using BRMS.Core.Models;

namespace BRMS.StdRules.Modules.Scripting.Dynamic;


public abstract class DynamicJsScriptValidator : JsDynamicScriptRule, IValidator
{
    /// <summary>
    /// Constructor interno para crear instancias dinámicas
    /// </summary>
    public DynamicJsScriptValidator() { }


    private ScriptRuleResult InternalInvoke(BRMSExecutionContext context)
    {
        ScriptRuleResult? res = null;
        ScriptExecutionResult scriptResult = ExecuteScript(context, true);
        if (!scriptResult.Success)
        {
            res = new ScriptRuleResult(this, context, scriptResult.Console, scriptResult.ErrorMessage);
        }

        if (scriptResult.Result is not bool or null)
        {
            res = new ScriptRuleResult(this, context, scriptResult.Console, "La expresión debe devolver un valor boolean.");
        }

        res ??= new ScriptRuleResult(this, context, scriptResult.Console, (bool)scriptResult.Result! ? null : ErrorMessage);

        return res;
    }
    public Task<object> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken = default)
    {

        return Task.FromResult(InternalInvoke(context) as object);
    }

    Task<IRuleResult> IRule<IRuleResult>.Invoke(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult((IRuleResult)InternalInvoke(context));
    }
}
