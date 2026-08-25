using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Support;
using AITaskAgent.Support.Template;
using AITaskAgent.YAML.Abstractions;
using AITaskAgent.YAML.Attributes;
using Newtonsoft.Json;

namespace AITaskAgent.YAML.Steps;

/// <summary>
/// YAML-deserializable LLM step.
/// Inherits all execution logic (retry, tools, telemetry, streaming, provider fallback) from
/// BaseLlmStep. Only overrides the hooks needed to wire YAML-declared prompts and role.
/// InputType and OutputType are fixed to IStepResult / LlmStringStepResult;
/// the SchemaCompiler injects JSON validation via OutputType on BaseLlmStep.
/// </summary>
/// <remarks>
/// YamlDotNet constructs this step before the pipeline factory knows which role it should use,
/// so it is built against <see cref="UnresolvedRole"/> - a placeholder backed by NullLlmService
/// that throws loudly if it is ever invoked. <c>YamlPipelineFactory</c> calls
/// <see cref="ApplyRole"/> during dependency injection to supply the real one.
/// </remarks>
[YamlStepTag("LlmStep")]
public class YamlLlmStep(ITemplateProvider? templateProvider = null)
    : BaseLlmStep(
        $"YamlLlmStep-{Guid.NewGuid()}",
        UnresolvedRole,
        typeof(IStepResult),
        typeof(LlmStringStepResult),
        prompt: string.Empty),
      IYamlStep
{
    /// <summary>
    /// Placeholder role used between deserialization and role resolution. Its service throws on
    /// any call, so a pipeline that reaches execution without a resolved role fails with a clear
    /// message instead of silently talking to the wrong provider.
    /// </summary>
    private static LlmRole UnresolvedRole => new()
    {
        Provider = new LlmProvider { Service = new NullLlmService() },
        Model = string.Empty
    };

    /// <summary>Unique identifier for this step within the pipeline.</summary>
    public required string StepId { get => Name; init { Name = value; } }

    // InputSchema and OutputSchema are inherited from StepBase, which declares them for exactly
    // this purpose. Redeclaring them here would shadow the base members the SchemaCompiler reads.

    /// <summary>Step IDs this step depends on (DAG edges).</summary>
    public string[]? DependsOn { get; init; }

    /// <summary>Name of the LLM role to resolve at factory time. Optional; falls back to the default role.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Prompt sent as user message. Supports @templatename.</summary>
    public string? UserPrompt { get; init; }

    /// <summary>
    /// When true (default), the step uses the shared pipeline conversation history.
    /// When false, the step starts with a clean conversation context — useful for steps
    /// whose output depends only on their input and system prompt, not on prior turns.
    /// </summary>
    public bool UseConversationHistory { get; init; } = true;

    /// <summary>
    /// Supplies the role resolved from <see cref="ProfileName"/>. Called by YamlPipelineFactory
    /// after deserialization, before the pipeline runs.
    /// </summary>
    public void ApplyRole(LlmRole role) => SetRole(role);

    // ── BaseLlmStep hooks ────────────────────────────────────────────────────

    protected override Task<string?> BuildUserMessageAsync(IStepResult input, PipelineContext context)
        => Task.FromResult<string?>(
            UserPrompt != null
                ? PromptTemplateResolver.Resolve(UserPrompt, input, context, templateProvider)
                : JsonConvert.SerializeObject(input.Value ?? input));

    protected override Task<string?> BuildSystemMessageAsync(IStepResult input, PipelineContext context)
        => Task.FromResult<string?>(
            SystemPrompt != null
                ? PromptTemplateResolver.Resolve(SystemPrompt, input, context, templateProvider)
                : null);

    protected override ConversationContext GetConversationContext(PipelineContext context)
        => UseConversationHistory
            ? context.Conversation
            : new ConversationContext(context.Conversation.History.MaxTokens, context.Conversation.History.TokenCounter);
}
