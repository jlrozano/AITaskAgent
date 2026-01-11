using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Results;
using AITaskAgent.Support.Template;

namespace AITaskAgent.LLM.Steps;

/// <summary>
/// Stateless LLM step that uses templates for prompts.
/// Templates are loaded via ITemplateProvider and merged with input data using ITemplateEngine.
/// Creates a clean conversation context for each execution (does not persist history).
/// </summary>
/// <typeparam name="TIn">Input step result type</typeparam>
/// <typeparam name="TOut">Output LLM result type</typeparam>
public class StatelessTemplateLlmStep<TIn, TOut>(
    ILlmService llmService,
    ITemplateProvider templateProvider,
    string name,
    LlmProviderConfig profile,
    string promptTemplateName,
    string? systemPromptTemplateName = null,
    Func<TOut, Task<(bool IsValid, string? Error)>>? resultValidator = null)
    : TemplateLlmStep<TIn, TOut>(
        llmService,
        templateProvider,
        name,
        profile,
        promptTemplateName,
        systemPromptTemplateName,
        resultValidator)
    where TIn : IStepResult
    where TOut : ILlmStepResult
{
    /// <summary>
    /// Creates a clean conversation context (stateless).
    /// </summary>
    protected override ConversationContext GetConversationContext(PipelineContext context)
        => new(context.Conversation.History.MaxTokens, context.Conversation.History.TokenCounter);

}
