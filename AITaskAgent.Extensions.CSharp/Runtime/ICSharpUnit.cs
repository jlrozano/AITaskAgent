using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;

namespace AITaskAgent.CSharp;

/// <summary>The method a unit compiled for a <c>CSharpStep</c>.</summary>
public delegate Task<IStepResult> CSharpStepMethod(StepCall call);

/// <summary>
/// The compiled code of a package as the <c>CSharpStep</c>s see it: one method per step id. Registered in the engine's
/// services by the host that compiled it (see <see cref="CSharpServiceCollectionExtensions.AddCSharpUnit"/>).
/// </summary>
public interface ICSharpUnit
{
    /// <summary>The method of step <paramref name="stepId"/>.</summary>
    bool TryGetStep(string stepId, out CSharpStepMethod method);
}

/// <summary>A hook, validator or result validator a unit declares.</summary>
/// <param name="Name"><c>Class.Method</c>.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Delegate">The method as a delegate.</param>
/// <param name="Description">The attribute's description.</param>
public sealed record UnitHook(string Name, HookKind Kind, Delegate Delegate, string? Description);

/// <summary>A class of the unit marked <see cref="ResponseTypeAttribute"/>.</summary>
/// <param name="Name">Schema name.</param>
/// <param name="Type">The class.</param>
/// <param name="Schema">Its schema, or null when it cannot be described.</param>
public sealed record UnitResponseType(string Name, Type Type, NJsonSchema.JsonSchema? Schema);

/// <summary>
/// Decides whether a run may execute C# code. The designer registers one after the host's own services (as it does for
/// <c>ExecStep</c>); without one the code runs (framework use).
/// </summary>
public interface ICSharpStepPolicy
{
    /// <summary>Null to allow the step; otherwise why it is denied.</summary>
    string? Deny(string stepName);
}
