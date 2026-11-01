using NJsonSchema;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// A step whose input and output can be described with JSON Schemas loaded at run time instead of
/// C# types. The YAML pipeline factory assigns them from a step's <c>inputSchema</c> and
/// <c>outputSchema</c>; a step that does not implement this interface rejects those keys.
/// </summary>
/// <remarks>
/// Implementations mark both properties <c>[JsonIgnore]</c>: they are not configuration a step
/// receives as JSON, the factory resolves them by name from its schema registry.
/// </remarks>
public interface IJsonSchemaStep
{
    /// <summary>Schema the step's input value is expected to satisfy, or null when undeclared.</summary>
    JsonSchema? InputJsonSchema { get; set; }

    /// <summary>
    /// Schema the step's output value must satisfy, or null when undeclared. When set, the step
    /// produces a JSON value validated against it.
    /// </summary>
    JsonSchema? OutputJsonSchema { get; set; }
}
