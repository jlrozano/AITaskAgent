using AITaskAgent.Core.Abstractions;
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
/// Inherits all execution logic (retry, tools, telemetry, streaming) from BaseLlmStep.
/// Only overrides the three virtual hooks to wire YAML-declared prompts and profile.
/// InputType and OutputType are fixed to IStepResult / LlmStringStepResult;
/// the SchemaCompiler injects JSON validation via OutputType on BaseLlmStep.
/// </summary>
[YamlStepTag("LlmStep")]
public class YamlLlmStep(ILlmService llmService, ITemplateProvider? templateProvider = null)
    : BaseLlmStep(
        llmService,
        $"YamlLlmStep-{Guid.NewGuid()}",
        typeof(IStepResult),
        typeof(LlmStringStepResult),
        new LlmProviderConfig(),
        templateProvider: templateProvider),
      IYamlStep
{
    /// <summary>Unique identifier for this step within the pipeline.</summary>
    public required string StepId { get => Name; init { Name = value; } }

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
    /// When true (default), the step uses the shared pipeline conversation history.
    /// When false, the step starts with a clean conversation context — useful for steps
    /// whose output depends only on their input and system prompt, not on prior turns.
    /// </summary>
    public bool UseConversationHistory { get; init; } = true;

    /// <summary>
    /// Resolved LlmProviderConfig — set by YamlPipelineFactory after deserialization.
    /// </summary>
    internal LlmProviderConfig? ResolvedProfile { get; set; }

    // ── BaseLlmStep hooks ────────────────────────────────────────────────────

    protected override Task<string> BuildUserMessageAsync(IStepResult input, PipelineContext context)
        => UserPrompt != null
           ? ResolvePromptAsync(UserPrompt, input, context)
           : Task.FromResult(JsonConvert.SerializeObject(input.Value ?? input));

    protected override async Task<string?> BuildSystemMessageAsync(IStepResult input, PipelineContext context)
        => SystemPrompt != null ? await ResolvePromptAsync(SystemPrompt, input, context) : null;

    protected override LlmRequest ConfigureLlmRequest(LlmRequest request, PipelineContext context)
        => ResolvedProfile != null ? request with { Profile = ResolvedProfile } : request;

    protected override ConversationContext GetConversationContext(PipelineContext context)
        => UseConversationHistory
            ? context.Conversation
            : new ConversationContext(context.Conversation.History.MaxTokens, context.Conversation.History.TokenCounter);
}
