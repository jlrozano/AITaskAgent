using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Support.Template;
using AITaskAgent.YAML.Abstractions;
using AITaskAgent.YAML.Attributes;
using Newtonsoft.Json;

namespace AITaskAgent.YAML.Steps;

/// <summary>
/// YAML-deserializable LLM step.
/// InputType and OutputType are injected by YamlPipelineFactory after deserialization.
/// Supports @templatename syntax for SystemPrompt and UserPrompt.
/// </summary>
[YamlStepTag("LlmStep")]
public class YamlLlmStep(ILlmService llmService, ITemplateProvider? templateProvider = null)
    : BaseLlmStep(
        llmService,
        "YamlLlmStep",
        typeof(IStepResult),
        typeof(LlmStringStepResult),
        new LlmProviderConfig(),
        templateProvider),
      IYamlStep
{
    /// <summary>Unique identifier for this step within the pipeline.</summary>
    public required string StepId { get; init; }

    /// <summary>Name of the input JSON schema (resolved by SchemaCompiler).</summary>
    public required string InputSchema { get; init; }

    /// <summary>Name of the output JSON schema (resolved by SchemaCompiler).</summary>
    public required string OutputSchema { get; init; }

    /// <summary>Step IDs this step depends on (DAG edges).</summary>
    public string[]? DependsOn { get; init; }

    /// <summary>LLM profile name to resolve at factory time. Optional.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Prompt sent as system message. Supports @templatename.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Prompt sent as user message. Supports @templatename.</summary>
    public string? UserPrompt { get; init; }

    /// <summary>
    /// Resolved LlmProviderConfig — set by YamlPipelineFactory after deserialization.
    /// </summary>
    internal LlmProviderConfig? ResolvedProfile { get; set; }

    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        var userMessage = UserPrompt != null
            ? await ResolvePromptAsync(UserPrompt, context)
            : JsonConvert.SerializeObject(input.Value ?? input);

        var systemMessage = SystemPrompt != null
            ? await ResolvePromptAsync(SystemPrompt, context)
            : null;

        var conversation = context.Conversation ?? new ConversationContext();
        conversation.AddUserMessage(userMessage);

        var request = new LlmRequest
        {
            Conversation = conversation,
            SystemPrompt = systemMessage,
            Profile = ResolvedProfile ?? Profile
        };

        var response = await LlmService.InvokeAsync(request, cancellationToken);

        conversation.AddAssistantMessage(response.Content ?? string.Empty);

        return StepResultFactory.CreateStepResult(OutputType, this, response.Content);
    }
}
