using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.LLM.Support;

/// <summary>
/// Renders a prompt template against the step input and the pipeline context.
///
/// Shared by the code-first path (<c>TemplateLlmStep</c>) and the declarative one
/// (<c>YamlLlmStep</c>) so both expose the same template surface: a developer writing a step in
/// C# and one declaring it in YAML get identical behaviour.
/// </summary>
public static class PromptTemplateResolver
{
    /// <summary>
    /// Resolves <paramref name="rawPrompt"/> into its final text.
    ///
    /// The model exposed to the template is <c>Input</c> (the step input value, reachable as
    /// <c>{{ input.someproperty }}</c>) and <c>Context</c> (pipeline metadata merged with
    /// <paramref name="variables"/>, reachable as <c>{{ context.mykey }}</c>). Keys are lowercased
    /// and step variables win over pipeline metadata on collision.
    /// </summary>
    /// <param name="rawPrompt">
    /// Template name or inline template text. With a provider it is first looked up as a name; when
    /// the provider has no such template, a single-line value ending in ".md" throws
    /// <see cref="InvalidOperationException"/> (a template file name is never sent as the prompt),
    /// and anything else is rendered as inline text. Without a provider it is always inline text.
    /// </param>
    /// <param name="input">Step input; its <c>Value</c> becomes <c>Input</c> in the model.</param>
    /// <param name="context">Pipeline context; supplies metadata and the base directory for includes.</param>
    /// <param name="templateProvider">Optional provider. When null, the raw text is rendered directly.</param>
    /// <param name="variables">Optional step-scoped variables merged into <c>Context</c>.</param>
    /// <returns>The rendered prompt, or an empty string when <paramref name="rawPrompt"/> is blank.</returns>
    public static string Resolve(
        string? rawPrompt,
        IStepResult input,
        PipelineContext context,
        ITemplateProvider? templateProvider = null,
        IEnumerable<KeyValuePair<string, object?>>? variables = null)
    {
        if (string.IsNullOrWhiteSpace(rawPrompt))
        {
            return "";
        }

        var mergedContext = context.Metadata
            .Concat(variables ?? [])
            .GroupBy(kvp => kvp.Key.ToLowerInvariant())
            .ToDictionary(
                g => g.Key,
                g => g.Last().Value);

        var model = new
        {
            Input = input.Value ?? input,     // {{ input.someproperty }}
            Context = mergedContext  // {{ context.mykey }}
        };

        if (templateProvider != null)
        {
            var rendered = templateProvider.Render(rawPrompt, model);
            if (rendered != null)
            {
                return rendered;
            }

            // The provider has no template by that name. Something that looks like a template file
            // ("X_prompt.md") is a missing template, not a prompt: sending its name to the model
            // is exactly the silent failure this guards against.
            if (LooksLikeTemplateFileName(rawPrompt))
            {
                throw new InvalidOperationException(
                    $"Prompt template '{rawPrompt.Trim()}' was not found by {templateProvider.GetType().Name}. " +
                    "A template file name is never sent to the model as the prompt; add the template or " +
                    "pass the prompt text inline.");
            }

            // Anything else is inline text and is rendered like the provider-less path below.
            var logger = context.GetLogger(typeof(PromptTemplateResolver));
            if (!rawPrompt.Any(char.IsWhiteSpace))
            {
                // A single token could be a misspelled template name; inline prompts rarely are one word.
                logger.LogWarning(
                    "No template named '{TemplateName}' was found by {Provider}; rendering it as inline text.",
                    rawPrompt, templateProvider.GetType().Name);
            }

            return RenderInline(rawPrompt, model, logger);
        }

        return RenderInline(rawPrompt, model, context.GetLogger(typeof(PromptTemplateResolver)));
    }

    /// <summary>
    /// Renders <paramref name="rawPrompt"/> as inline template text.
    /// </summary>
    private static string RenderInline(string rawPrompt, object model, ILogger logger) =>
        // baseDirectory null: un @[ruta] relativo se resuelve contra el directorio de trabajo.
        // CurrentStepPath es la ruta del paso en el árbol del pipeline, no un directorio.
        TinyTemplateRendered.Render(rawPrompt, model, strictMode: false, baseDirectory: null,
            logger: logger) ?? rawPrompt;

    /// <summary>
    /// True when <paramref name="rawPrompt"/> is a single line ending in ".md", i.e. a template
    /// file name such as the <c>{name}_prompt.md</c> default of <c>TemplateLlmStep</c>.
    /// </summary>
    private static bool LooksLikeTemplateFileName(string rawPrompt)
    {
        var trimmed = rawPrompt.Trim();
        return trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Contains('\n')
            && !trimmed.Contains('\r');
    }
}
