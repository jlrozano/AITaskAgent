using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Support.Template;

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
    /// <param name="rawPrompt">Template text, or a template name when a provider is supplied.</param>
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
            Input = input.Value,     // {{ input.someproperty }}
            Context = mergedContext  // {{ context.mykey }}
        };

        return templateProvider != null
            ? templateProvider.Render(rawPrompt, model) ?? rawPrompt
            : TinyTemplateRendered.Render(rawPrompt, model, strictMode: false, baseDirectory: context.CurrentStepPath) ?? rawPrompt;
    }
}
