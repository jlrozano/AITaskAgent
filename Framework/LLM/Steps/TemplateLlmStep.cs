using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Support;
using AITaskAgent.LLM.Tools.Implementation;
using AITaskAgent.Support.Template;

namespace AITaskAgent.LLM.Steps;

/// <summary>
/// Stateful LLM step that uses templates for prompts.
/// Templates are loaded via ITemplateProvider and merged with input data using ITemplateEngine.
/// Uses context.Conversation for stateful conversation history.
/// </summary>
/// <typeparam name="TIn">Input step result type</typeparam>
/// <typeparam name="TOut">Output LLM result type</typeparam>
public class TemplateLlmStep<TIn, TOut> : BaseLlmStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : ILlmStepResult
{
    private readonly ITemplateProvider? _templateProvider;
    private readonly Dictionary<string, object?> _variables = [];

    public TemplateLlmStep(
        string name,
        LlmRole role,
        ITemplateProvider? templateProvider = null,
        IList<string>? tools = null,
        string? promtTemplateString = null,
        string? systemTemplateString = null

    ) : base(
            name,
            role,
            string.IsNullOrWhiteSpace(promtTemplateString) ? $"{name}_promt.md" : promtTemplateString,
            string.IsNullOrEmpty(systemTemplateString) ? (role.SystemPrompt ?? $"{name}_system.md") : systemTemplateString)
    {
        Tools = tools == null ? [] : AvailableTools.Instance.GetTools([.. tools]).ToList();
        _templateProvider = templateProvider;
    }

    public object? GetVariable(string key)
    {
        return _variables.TryGetValue(key.ToLowerInvariant(), out var value) ? value : null;
    }

    public void SetVariable(string key, object? value)
    {
        _variables[key.ToLowerInvariant()] = value;
    }

    public void RemoveVariable(string key)
    {
        _variables.Remove(key.ToLowerInvariant());
    }

    public void ClearVariables()
    {
        _variables.Clear();
    }

    protected override async Task<string?> BuildUserMessageAsync(IStepResult input, PipelineContext context)
    {
        var rawPrompt = await base.BuildUserMessageAsync(input, context);
        return PromptTemplateResolver.Resolve(rawPrompt, input, context, _templateProvider, _variables);
    }


    protected override async Task<string?> BuildSystemMessageAsync(IStepResult input, PipelineContext context)
    {

        var rawSystemPrompt = await base.BuildSystemMessageAsync(input, context);
        return PromptTemplateResolver.Resolve(rawSystemPrompt, input, context, _templateProvider, _variables);
    }

}
