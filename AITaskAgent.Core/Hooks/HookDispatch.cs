namespace AITaskAgent.Core.Hooks;

/// <summary>
/// Calls hooks with one convention: cancellation passes through, any other exception becomes a
/// <see cref="StepHookException"/> that names the hook and the step.
/// </summary>
public static class HookDispatch
{
    /// <summary>Calls an around hook with <paramref name="next"/> as the built-in behaviour.</summary>
    public static async Task<T> AroundAsync<TArgs, T>(string hookName, string stepName, HookAround<TArgs, T> hook, TArgs args, Func<Task<T>> next)
    {
        try
        {
            return await hook(args, next);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StepHookException)
        {
            throw new StepHookException(hookName, stepName, ex);
        }
    }

    /// <summary>Calls a decision hook that returns a value.</summary>
    public static async Task<T> InvokeAsync<TArgs, T>(string hookName, string stepName, Func<TArgs, Task<T>> hook, TArgs args)
    {
        try
        {
            return await hook(args);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StepHookException)
        {
            throw new StepHookException(hookName, stepName, ex);
        }
    }

    /// <summary>Calls a notification hook.</summary>
    public static async Task NotifyAsync(string hookName, string stepName, Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StepHookException)
        {
            throw new StepHookException(hookName, stepName, ex);
        }
    }

    /// <summary>Calls a synchronous transform hook (it returns the value to use).</summary>
    public static T Transform<T>(string hookName, string stepName, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StepHookException)
        {
            throw new StepHookException(hookName, stepName, ex);
        }
    }
}
