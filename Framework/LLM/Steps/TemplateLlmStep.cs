using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Results;
using AITaskAgent.Support.Template;

namespace AITaskAgent.LLM.Steps;

/// <summary>
/// Stateful LLM step that uses templates for prompts.
/// Templates are loaded via ITemplateProvider and merged with input data using ITemplateEngine.
/// Uses context.Conversation for stateful conversation history.
/// </summary>
/// <typeparam name="TIn">Input step result type</typeparam>
/// <typeparam name="TOut">Output LLM result type</typeparam>
public class TemplateLlmStep<TIn, TOut>(
    ILlmService llmService,
    ITemplateProvider templateProvider,
    string name,
    LlmProviderConfig profile,
    string promptTemplateName,
    string? systemPromptTemplateName = null,
    Func<TOut, Task<(bool IsValid, string? Error)>>? resultValidator = null)
    : BaseLlmStep<TIn, TOut>(
        llmService,
        name,
        profile,
        BuildPromptBuilder(templateProvider, promptTemplateName),
        systemPromptTemplateName != null
            ? BuildPromptBuilder(templateProvider, systemPromptTemplateName)
            : null,
        null, // tools
        resultValidator)
    where TIn : IStepResult
    where TOut : ILlmStepResult
{

    private static Func<TIn, PipelineContext, Task<string>> BuildPromptBuilder(
        ITemplateProvider templateProvider,
        string templateName)
    {
        return (input, context) =>
        {
            var rendered = templateProvider.Render(templateName, input ?? new object()) ?? "";
            return Task.FromResult(rendered);
        };
    }

}
