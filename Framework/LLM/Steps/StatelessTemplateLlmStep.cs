using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
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
    string name,
    LlmRole role,
    ITemplateProvider? templateProvider = null,
    IList<string>? tools = null,
    string? promtTemplateString = null,
    string? systemTemplateString = null
) : TemplateLlmStep<TIn, TOut>(name, role, templateProvider, tools, promtTemplateString, systemTemplateString)

    where TIn : IStepResult
    where TOut : ILlmStepResult
{
    private ConversationContext? _restoredConversation;
    private ConversationContext? _currentConversation;
    private readonly int? _roleMaxTokens = role.Inference?.MaxTokens;

    protected override Task<object?> SaveStateAsync()
    {
        // En un paso "Stateless", la conversación vive de forma aislada en `_conversation`.
        // Guardando el contexto entero garantizamos que las herramientas o historial previo (ej: el AskUser Tool) 
        // no se pierdan al suspender el proceso. El framework (PipelineContext) ya lo serializará automáticamente.
        return Task.FromResult<object?>(_conversation);
    }

    protected override Task RestoreStateAsync(object? state, object? hitlResponse)
    {
        if (state is ConversationContext savedContext)
        {
            _restoredConversation = savedContext;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a clean conversation context (stateless) unless it is resuming from a saved state.
    /// </summary>
    protected override ConversationContext GetConversationContext(PipelineContext context)
    {
        if (_restoredConversation != null)
        {
            _currentConversation = _restoredConversation;
            _restoredConversation = null; // Consumir y limpiar

        }
        else
        {
            _currentConversation = new(_roleMaxTokens ?? context.Conversation.History.MaxTokens, context.Conversation.History.TokenCounter);

        }
        return _currentConversation;
    }

}
