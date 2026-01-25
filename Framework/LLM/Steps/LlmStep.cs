using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Tools.Abstractions;

using AITaskAgent.LLM.Streaming;

namespace AITaskAgent.LLM.Steps;

/// <summary>
/// Stateful LLM step for string-to-string interactions.
/// Alias for LlmStep&lt;LlmStringStepResult&gt;.
/// </summary>
public sealed class LlmStep(
    ILlmService llmService,
    string name,
    LlmProviderConfig profile,
    Func<StepResult, PipelineContext, Task<string>> promptBuilder,
    string? systemPrompt = null,
    List<ITool>? tools = null,
    int maxToolIterations = 5,
    List<IStreamingTagHandler>? streamingHandlers = null)
    : LlmStep<LlmStringStepResult>(
        llmService,
        name,
        profile,
        promptBuilder,
        systemPrompt,
        tools,
        maxToolIterations: maxToolIterations,
        streamingHandlers: streamingHandlers)
{
}

/// <summary>
/// Stateful LLM step with typed output.
/// Uses context.Conversation for stateful conversation history.
/// </summary>
/// <typeparam name="TOut">Output LLM result type</typeparam>
public class LlmStep<TOut>(
    ILlmService llmService,
    string name,
    LlmProviderConfig profile,
    Func<StepResult, PipelineContext, Task<string>> promptBuilder,
    string? systemPrompt = null,
    List<ITool>? tools = null,
    Func<TOut, Task<(bool IsValid, string? Error)>>? resultValidator = null,
    int maxToolIterations = 5,
    List<IStreamingTagHandler>? streamingHandlers = null)
    : BaseLlmStep<StepResult, TOut>(
        llmService,
        name,
        profile,
        promptBuilder,
        systemPrompt != null ? (_, _) => Task.FromResult(systemPrompt) : null,
        tools,
        resultValidator,
        maxToolIterations,
        streamingHandlers)
    where TOut : LlmStepResult
{
    protected override ConversationContext GetConversationContext(PipelineContext context)
        => context.Conversation;
}

/// <summary>
/// Stateful LLM step for typed inputs and outputs.
/// Uses context.Conversation for stateful conversation history.
/// </summary>
/// <typeparam name="TIn">Input step result type</typeparam>
/// <typeparam name="TOut">Output LLM result type</typeparam>
public sealed class LlmStep<TIn, TOut>(
    ILlmService llmService,
    string name,
    LlmProviderConfig profile,
    Func<TIn, PipelineContext, Task<string>> promptBuilder,
    string? systemPrompt = null,
    List<ITool>? tools = null,
    Func<TOut, Task<(bool IsValid, string? Error)>>? resultValidator = null,
    int maxToolIterations = 5,
    List<IStreamingTagHandler>? streamingHandlers = null)
    : BaseLlmStep<TIn, TOut>(
        llmService,
        name,
        profile,
        promptBuilder,
        systemPrompt != null ? (_, _) => Task.FromResult(systemPrompt) : null,
        tools,
        resultValidator,
        maxToolIterations,
        streamingHandlers)
    where TIn : StepResult
    where TOut : LlmStepResult
{
    protected override ConversationContext GetConversationContext(PipelineContext context)
        => context.Conversation;
}
