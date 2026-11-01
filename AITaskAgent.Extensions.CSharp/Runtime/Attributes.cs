namespace AITaskAgent.CSharp;

// [Hook], [Validator], [ResultValidator] and [ResponseType] live in AITaskAgent.Abstractions (namespace AITaskAgent.Core.Hooks), shared
// with the plugin loader; the generated usings of a unit include that namespace.

/// <summary>Written on every generated step method: the id of the <c>CSharpStep</c> it runs.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StepMethodAttribute(string stepId) : Attribute
{
    /// <summary>The step's id.</summary>
    public string StepId { get; } = stepId;
}
