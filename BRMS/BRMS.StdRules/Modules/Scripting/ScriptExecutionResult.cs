// BRMS.StdRules, Version=1.0.1.0, Culture=neutral, PublicKeyToken=null
// BRMS.StdRules.JsScript.ScriptExecutionResult
namespace BRMS.StdRules.Modules.Scripting;

public record ScriptExecutionResult(bool Success, object? Result, string? ErrorMessage, IEnumerable<ConsoleMessage> Console, Exception? ex)
{
    public static ScriptExecutionResult SuccessResult(object? result, IEnumerable<ConsoleMessage> console)
    {
        return new ScriptExecutionResult(Success: true, result, null, console, null);
    }

    public static ScriptExecutionResult ErrorResult(string errorMessage, IEnumerable<ConsoleMessage> console, Exception? ex)
    {
        return new ScriptExecutionResult(false, null, errorMessage, console, ex);
    }
}
