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
    private const string DefaultSystemTemplate = "Analyze and clarify the user's intention based on these options:\n{{ context.options }}";
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
               (role.SystemPrompt ?? "") + "\n\n" + (systemPromptTemplateName ?? DefaultSystemTemplate))
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