using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using NJsonSchema.Validation;
using System.Text;

namespace AITaskAgent.Core.StepResults;

/// <summary>
/// A step result whose value is plain JSON (<see cref="JToken"/>) described by a JSON Schema
/// carried by the instance rather than by a C# type. It replaces the types the YAML engine used to
/// compile from each schema: the schema is checked at run time, in <see cref="ValidateAsync"/>,
/// which <c>RetryMiddleware</c> calls as the structural validation of every result.
/// </summary>
public class JsonStepResult : StepResult<JToken>
{
    /// <summary>Constructor the step result factory uses (value and schema set afterwards).</summary>
    public JsonStepResult(IStep step) : base(step)
    {
    }

    /// <summary>Creates a result holding <paramref name="value"/>, optionally bound to <paramref name="schema"/>.</summary>
    public JsonStepResult(IStep step, JToken? value, JsonSchema? schema = null) : base(step, value)
    {
        Schema = schema;
    }

    /// <summary>Schema the value must satisfy. Null means "any JSON".</summary>
    public JsonSchema? Schema { get; set; }

    /// <summary>Validates <see cref="StepResult{T}.Value"/> against <see cref="Schema"/>, then the attached validators.</summary>
    public override async Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
    {
        (bool isValid, string? error) = Validate(Value, Schema);
        return isValid ? await base.ValidateAsync(context) : (false, error);
    }

    /// <summary>
    /// Validates <paramref name="value"/> against <paramref name="schema"/> with NJsonSchema. Shared by
    /// every result that carries a run-time schema (the LLM extension's JSON result uses it too).
    /// </summary>
    /// <returns>Valid when there is no schema; otherwise the list of violations as the error.</returns>
    public static (bool IsValid, string? Error) Validate(JToken? value, JsonSchema? schema)
    {
        if (schema == null)
        {
            return (true, null);
        }

        if (value == null || value.Type == JTokenType.Null)
        {
            return (false, $"The value is empty but must match the JSON schema{Describe(schema)}.");
        }

        ICollection<ValidationError> errors = new JsonSchemaValidator().Validate(value, schema);
        if (errors.Count == 0)
        {
            return (true, null);
        }

        StringBuilder sb = new();
        sb.Append("The value does not match the JSON schema").Append(Describe(schema)).Append(':');
        AppendErrors(sb, errors, indent: 1);
        return (false, sb.ToString());
    }

    private static string Describe(JsonSchema schema)
        => string.IsNullOrWhiteSpace(schema.Title) ? "" : $" '{schema.Title}'";

    private static void AppendErrors(StringBuilder sb, IEnumerable<ValidationError> errors, int indent)
    {
        foreach (ValidationError error in errors)
        {
            string path = string.IsNullOrEmpty(error.Path) ? "#" : error.Path;
            sb.AppendLine().Append(new string(' ', indent * 2)).Append("- ").Append(path).Append(": ").Append(error.Kind);
            if (!string.IsNullOrEmpty(error.Property) && !path.EndsWith(error.Property, StringComparison.Ordinal))
            {
                sb.Append(" (").Append(error.Property).Append(')');
            }

            if (error is ChildSchemaValidationError child)
            {
                foreach (ICollection<ValidationError> nested in child.Errors.Values)
                {
                    AppendErrors(sb, nested, indent + 1);
                }
            }
        }
    }
}
