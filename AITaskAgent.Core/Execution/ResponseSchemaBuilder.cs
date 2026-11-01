using AITaskAgent.Core.Abstractions;
using NJsonSchema;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Public face of the JSON schema builder typed steps already use for their response format, so a schema
/// registered for a C# class is the one the model sees and the result is validated against: strict
/// (<c>additionalProperties: false</c>), with <c>[Description]</c>, validation attributes, defaults,
/// <c>required</c> from <c>[Required]</c> or non-null <c>init</c> properties and <c>[ExcludeFromLlmSchema]</c>.
/// </summary>
/// <remarks>
/// Limits (inherited from the builder): the root is a class; types are inlined, with no <c>$ref</c>, and a
/// recursive type is cut silently where it recurses; System.Text.Json attributes are ignored (Newtonsoft only).
/// </remarks>
public static class ResponseSchemaBuilder
{
    /// <summary>Builds the schema of <paramref name="type"/>; null for primitives, <c>string</c>, <c>object</c> and when the type cannot be described.</summary>
    public static JsonSchema? Build(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return StepResultFactory.BuildJsonSchema(type);
    }

    /// <summary>Builds the schema of <typeparamref name="T"/>.</summary>
    public static JsonSchema? Build<T>() => Build(typeof(T));
}
