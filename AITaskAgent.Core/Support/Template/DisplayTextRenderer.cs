using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace AITaskAgent.Support.Template;

/// <summary>
/// Renders the <c>Display</c> text of the start and end events of a step or a tool: the template that
/// the step or tool declares (<see cref="IDisplayable"/>) or that the host's
/// <see cref="IDisplayTextProvider"/> supplies by name, over the same template engine and the same
/// <c>input</c>/<c>context</c> model as prompts and <c>when</c> expressions.
/// </summary>
/// <remarks>
/// The renderer never throws and never blocks an execution: with no template it returns null at no cost
/// (no model is built), and on any failure (a provider that throws, a model that cannot be serialized,
/// an include directive) it logs a warning and returns null. The warning names the step or tool and the
/// kind of failure, never the values. A text that renders empty is null; one longer than
/// <see cref="MaxLength"/> is cut and ends with an ellipsis. File includes (<c>{{ include }}</c>,
/// <c>@[path]</c>) are refused: a display text must not read files.
/// <para>
/// Model at start of a step: <c>step</c> {name, path, attempt}, <c>input</c>, <c>context</c>, <c>data</c>.
/// At the end it has no <c>step.attempt</c> (the event is emitted once, after the retries) and adds <c>output</c>, <c>success</c>, <c>error</c> and <c>durationMs</c>. For a tool:
/// <c>tool</c> (the name), <c>args</c>, <c>context</c>, <c>data</c>; at the end also <c>result</c>,
/// <c>success</c>, <c>error</c> and <c>durationMs</c>. <c>args</c> and <c>result</c> are parsed to JSON when
/// the text is a JSON object or array, otherwise they stay text. Lookups ignore case, as everywhere in the
/// engine. A piece of the model is only built when the template mentions its name.
/// </para>
/// </remarks>
public static partial class DisplayTextRenderer
{
    /// <summary>Maximum length of a display text; a longer one is cut and ends with an ellipsis.</summary>
    public const int MaxLength = 500;

    /// <summary>Renders the start text of a step; null when it has no template or the template fails.</summary>
    public static string? StepStart(
        IStep step,
        IStepResult? input,
        PipelineContext context,
        int attempt,
        string? path,
        IReadOnlyDictionary<string, object?>? data)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(context);

        string? template = ResolveTemplate(step as IDisplayable, DisplayKind.Step, step.Name, DisplayPhase.Start, context);
        if (template is null)
        {
            return null;
        }

        return Render(template, DisplayKind.Step, step.Name, context,
            () => StepModel(template, step.Name, path, attempt, input, context, data));
    }

    /// <summary>Renders the completion text of a step; null when it has no template or the template fails.</summary>
    public static string? StepEnd(
        IStep step,
        IStepResult? input,
        IStepResult? output,
        PipelineContext context,
        string? path,
        bool success,
        string? error,
        TimeSpan duration,
        IReadOnlyDictionary<string, object?>? data)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(context);

        string? template = ResolveTemplate(step as IDisplayable, DisplayKind.Step, step.Name, DisplayPhase.End, context);
        if (template is null)
        {
            return null;
        }

        return Render(template, DisplayKind.Step, step.Name, context, () =>
        {
            JObject model = StepModel(template, step.Name, path, null, input, context, data);
            if (Uses(template, "output") && output is not null)
            {
                model["output"] = ToToken(output.Value ?? output);
            }

            AddOutcome(model, success, error, duration);
            return model;
        });
    }

    /// <summary>Renders the start text of a tool call; null when it has no template or the template fails.</summary>
    public static string? ToolStart(
        IDisplayable? tool,
        string toolName,
        string? argumentsJson,
        PipelineContext context,
        IReadOnlyDictionary<string, object?>? data)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(context);

        string? template = ResolveTemplate(tool, DisplayKind.Tool, toolName, DisplayPhase.Start, context);
        if (template is null)
        {
            return null;
        }

        return Render(template, DisplayKind.Tool, toolName, context,
            () => ToolModel(template, toolName, argumentsJson, context, data));
    }

    /// <summary>Renders the completion text of a tool call; null when it has no template or the template fails.</summary>
    public static string? ToolEnd(
        IDisplayable? tool,
        string toolName,
        string? argumentsJson,
        string? result,
        PipelineContext context,
        bool success,
        string? error,
        TimeSpan duration,
        IReadOnlyDictionary<string, object?>? data)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(context);

        string? template = ResolveTemplate(tool, DisplayKind.Tool, toolName, DisplayPhase.End, context);
        if (template is null)
        {
            return null;
        }

        return Render(template, DisplayKind.Tool, toolName, context, () =>
        {
            JObject model = ToolModel(template, toolName, argumentsJson, context, data);
            if (Uses(template, "result") && result is not null)
            {
                model["result"] = ParseJsonOrText(result);
            }

            AddOutcome(model, success, error, duration);
            return model;
        });
    }

    // ── Template resolution ──────────────────────────────────────────────────

    private static string? ResolveTemplate(IDisplayable? owner, DisplayKind kind, string name, DisplayPhase phase, PipelineContext context)
    {
        string? declared = phase == DisplayPhase.Start ? owner?.DisplayStart : owner?.DisplayEnd;
        if (!string.IsNullOrWhiteSpace(declared))
        {
            return declared;
        }

        try
        {
            string? fromProvider = context.ServiceProvider.GetService<IDisplayTextProvider>()?.GetTemplate(kind, name, phase);
            return string.IsNullOrWhiteSpace(fromProvider) ? null : fromProvider;
        }
        catch (Exception ex)
        {
            GetLogger(context)?.LogWarning(
                "The IDisplayTextProvider failed for {Kind} '{Name}' ({ErrorType}); the event goes without text.",
                kind, name, ex.GetType().Name);
            return null;
        }
    }

    private static string? Render(string template, DisplayKind kind, string name, PipelineContext context, Func<JObject> buildModel)
    {
        try
        {
            if (IncludeDirective().IsMatch(template))
            {
                GetLogger(context)?.LogWarning(
                    "The Display template of {Kind} '{Name}' uses a file include, which is not allowed; the event goes without text.",
                    kind, name);
                return null;
            }

            string text = TinyTemplateRendered.Render(template, buildModel());
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return text.Length <= MaxLength ? text : string.Concat(text.AsSpan(0, MaxLength - 1), "…");
        }
        catch (Exception ex)
        {
            GetLogger(context)?.LogWarning(
                "The Display template of {Kind} '{Name}' failed ({ErrorType}); the event goes without text.",
                kind, name, ex.GetType().Name);
            return null;
        }
    }

    // ── Models ───────────────────────────────────────────────────────────────

    private static JObject StepModel(
        string template, string name, string? path, int? attempt, IStepResult? input, PipelineContext context,
        IReadOnlyDictionary<string, object?>? data)
    {
        JObject model = [];
        JObject stepInfo = new() { ["name"] = name, ["path"] = path };
        if (attempt is { } n)
        {
            stepInfo["attempt"] = n;
        }

        model["step"] = stepInfo;

        if (Uses(template, "input") && input is not null)
        {
            model["input"] = ToToken(input.Value ?? input);
        }

        AddCommon(model, template, context, data);
        return model;
    }

    private static JObject ToolModel(
        string template, string toolName, string? argumentsJson, PipelineContext context,
        IReadOnlyDictionary<string, object?>? data)
    {
        JObject model = [];
        model["tool"] = toolName;
        if (Uses(template, "args") && argumentsJson is not null)
        {
            model["args"] = ParseJsonOrText(argumentsJson);
        }

        AddCommon(model, template, context, data);
        return model;
    }

    private static void AddCommon(JObject model, string template, PipelineContext context, IReadOnlyDictionary<string, object?>? data)
    {
        if (Uses(template, "context"))
        {
            model["context"] = ToToken(TemplateExpression.BuildContextModel(context));
        }

        if (Uses(template, "data") && data is not null)
        {
            model["data"] = ToToken(data);
        }
    }

    private static void AddOutcome(JObject model, bool success, string? error, TimeSpan duration)
    {
        model["success"] = success;
        model["error"] = error;
        model["durationMs"] = (long)duration.TotalMilliseconds;
    }

    private static JToken ToToken(object value) => TinyTemplateRendered.ToModelToken(value);

    private static JToken ParseJsonOrText(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length > 1 && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            try
            {
                return JToken.Parse(trimmed);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // Not JSON after all: it stays text.
            }
        }

        return new JValue(text);
    }

    private static bool Uses(string template, string name) =>
        template.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static ILogger? GetLogger(PipelineContext context)
    {
        try
        {
            return context.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("AITaskAgent.Display");
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\{\{\s*include\b|@\[", RegexOptions.IgnoreCase)]
    private static partial Regex IncludeDirective();
}
