using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace AITaskAgent.Support.Template;

/// <summary>
/// Evaluates the small expressions declarative steps use (a switch's <c>on</c>, a break's
/// <c>when</c>) with <see cref="TinyTemplateRendered"/>, the same engine and model as prompts:
/// <c>input</c> is the step input value and <c>context</c> the pipeline metadata (keys lowercased).
/// There is no JavaScript and no operator syntax: an expression is a value lookup, optionally with
/// filters and a <c>??</c> default.
/// </summary>
public static partial class TemplateExpression
{
    /// <summary>
    /// Renders <paramref name="expression"/> and returns the trimmed text. Text without <c>{{</c> is a
    /// bare path (<c>input.category</c>) and is wrapped as <c>{{ input.category }}</c>.
    /// </summary>
    public static string Evaluate(string expression, IStepResult input, PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        string template = expression.Contains("{{", StringComparison.Ordinal)
            ? expression
            : "{{ " + expression.Trim() + " }}";

        return (TinyTemplateRendered.Render(template, BuildModel(input, context)) ?? string.Empty).Trim();
    }

    /// <summary>
    /// Renders <paramref name="template"/> as text over the same model as <see cref="Evaluate"/>.
    /// Unlike <see cref="Evaluate"/>, text without <c>{{</c> is returned as it is (it is a message,
    /// not a lookup), and the result is not trimmed.
    /// </summary>
    public static string Render(string template, IStepResult input, PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        return template.Contains("{{", StringComparison.Ordinal)
            ? TinyTemplateRendered.Render(template, BuildModel(input, context)) ?? string.Empty
            : template;
    }

    /// <summary>
    /// Looks up the value of <paramref name="expression"/> as JSON, keeping its structure. A bare path
    /// (<c>input.order</c>, <c>context.items[0]</c>, optionally written as <c>{{ input.order }}</c>)
    /// returns the value it reaches, or null when there is none; anything else (text with several
    /// placeholders, filters, defaults) is rendered with <see cref="Render"/> and returned as a string.
    /// </summary>
    public static JToken? Resolve(string expression, IStepResult input, PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        Match path = BarePath().Match(expression);
        if (!path.Success)
        {
            return new JValue(Render(expression, input, context));
        }

        JToken model = TinyTemplateRendered.ToModelToken(BuildModel(input, context));
        return model.SelectToken(path.Groups["path"].Value, errorWhenNoMatch: false)?.DeepClone();
    }

    /// <summary>
    /// The <c>{ input, context }</c> model of prompts, <c>when</c> and <c>on</c> expressions: <c>input</c> is the
    /// step input value and <c>context</c> the pipeline metadata with keys lowercased.
    /// </summary>
    public static object BuildModel(IStepResult input, PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        return new
        {
            Input = input.Value ?? input,
            Context = BuildContextModel(context)
        };
    }

    /// <summary>The <c>context</c> part of the model: the pipeline metadata with keys lowercased (the last one wins).</summary>
    public static Dictionary<string, object?> BuildContextModel(PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Metadata
            .GroupBy(kvp => kvp.Key.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Last().Value);
    }

    [GeneratedRegex(@"^\s*(?:\{\{\s*)?(?<path>(?:input|context)(?:\.[A-Za-z_][\w-]*|\[\d+\])*)(?:\s*\}\})?\s*$")]
    private static partial Regex BarePath();

    /// <summary>
    /// False for an empty value and for <c>false</c>, <c>0</c>, <c>no</c> and <c>null</c> (any case);
    /// true for anything else.
    /// </summary>
    public static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string v = value.Trim();
        return !(v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v == "0"
            || v.Equals("no", StringComparison.OrdinalIgnoreCase)
            || v.Equals("null", StringComparison.OrdinalIgnoreCase));
    }
}
