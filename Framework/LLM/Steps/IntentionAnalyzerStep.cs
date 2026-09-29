using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Results;
using AITaskAgent.Observability.Events;
using AITaskAgent.Support.Template;
using Newtonsoft.Json;

namespace AITaskAgent.LLM.Steps;


public class IntentionAnalyzerStep<TIn> : TemplateLlmStep<TIn, LlmIntentionResult>, IRouterOptions
    where TIn : IStepResult
{
    private const string DefaultSystemTemplate = "Analyze and clarify the user's intention.";

    /// <summary>
    /// The option catalogue, appended to whatever system prompt is in force.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="DefaultSystemTemplate"/> because the two used to be one
    /// string: supplying a custom system prompt replaced the default and took the catalogue with
    /// it, leaving the model to guess options it had never been shown while the step went on
    /// validating its answer against them.
    /// </remarks>
    private const string OptionsTemplate = "\n\nChoose one of these options:\n{{ context.options }}";
    private const string DefaultUserTemplate = "{{ input }}";

    private readonly IEnumerable<IntentionOption> _options;

    public IntentionAnalyzerStep(
        string name,
        LlmRole role,
        IList<IntentionOption> options,
        string? promptTemplateName = null,
        string? systemPromptTemplateName = null,
        ITemplateProvider? templateProvider = null,
        IList<string>? tools = null)
        : base(name, role, templateProvider, tools,
               promptTemplateName ?? DefaultUserTemplate,
               (role.SystemPrompt ?? "") + "\n\n" + (systemPromptTemplateName ?? DefaultSystemTemplate) + OptionsTemplate)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (!options.Any())
        {
            throw new ArgumentException("Options for intention analysis cannot be empty.", nameof(options));
        }

        // Definir la variable con las opciones y sus descripciones para el LLM

        var optionsText = string.Join("\n", options.Select(o => $"- **{o.Id}**: {o.Description}"));
        SetVariable("options", optionsText);
    }

    IList<string> IRouterOptions.Options => [.. _options.Select(o => o.Id)];

    /// <summary>
    /// The system prompt built in the constructor is always literal composed text -
    /// <c>role.SystemPrompt</c> + the caller's text (or <see cref="DefaultSystemTemplate"/>) +
    /// <see cref="OptionsTemplate"/> - never a bare file name, even when a <c>templateProvider</c>
    /// is supplied for the user prompt. Resolving it through the provider would look up a file that
    /// can't exist (its "name" is a multi-paragraph string) and, on a miss, either throw
    /// (<c>validateTemplates: true</c>) or return null and fall back to the raw, unrendered text -
    /// leaving the model with a literal <c>{{ context.options }}</c> placeholder instead of the
    /// interpolated option catalogue. Forcing this to null makes <c>TemplateLlmStep</c> render the
    /// composed text directly via the template engine instead.
    /// </summary>
    protected override ITemplateProvider? SystemTemplateProvider => null;


    protected override async Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        await base.FinalizeAsync(result, context, cancellationToken);


        if (result?.Value is Intention intention)
        {
            await context.SendEventAsync(new LlmResponseEvent
            {
                StepName = Name,
                Content = intention.UserFeedback?.Message ?? "",
                FinishReason = FinishReason.Stop,
                IsThinking = false,
                CorrelationId = context.CorrelationId
            }, cancellationToken);

            context.Conversation?.History?.RemoveMessage(context.Conversation.History.Messages.Count - 1);

            if (intention.UserFeedback?.NeedMoreInfo ?? false)
            {
                context.Conversation?.AddAssistantMessage(JsonConvert.SerializeObject(intention.UserFeedback));
            }

        }

    }
}