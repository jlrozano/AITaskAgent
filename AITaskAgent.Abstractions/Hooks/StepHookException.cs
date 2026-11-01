namespace AITaskAgent.Core.Hooks;

/// <summary>
/// A hook or validator bound from outside (YAML, a registry) threw. Wraps the original exception so the
/// retry middleware can tell it from a failure of the step and finish the step as a fatal error
/// (no retry) instead of letting the exception escape.
/// </summary>
public sealed class StepHookException(string hook, string stepName, Exception inner)
    : Exception($"Hook '{hook}' of step '{stepName}' failed: {inner.Message}", inner)
{
    public string Hook { get; } = hook;

    public string StepName { get; } = stepName;
}
