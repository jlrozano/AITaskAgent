using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Results;
using System.ComponentModel;
using System.Text;

namespace AITaskAgent.LLM.Steps;

/// <summary>
/// Step that analyzes user intention using LLM and classifies it into predefined options.
/// Uses BaseLlmStep for automatic retry, bookmark, validation, and JSON parsing.
/// 
/// TIn should be a StepResult containing a string value (the user's message to analyze).
/// TEnum is the enum with available intention options, using [Description] for each option.
/// 
/// The LLM response is automatically parsed to IntentionInfo&lt;TEnum&gt; by BaseLlmStep.
/// Newtonsoft.Json handles string-to-enum conversion automatically.
/// </summary>
public sealed class IntentionAnalyzerStep<TIn, TEnum>(
    ILlmService llmService,
    LlmProviderConfig profile,
    string? name = null,
    Func<TIn, PipelineContext, Task<string>>? promptBuilder = null,
    string? systemPrompt = null
    ) : BaseLlmStep<TIn, LlmStepResult<Intention<TEnum>>>(
        llmService,
        name ?? $"IntentionAnalyzer<{typeof(TEnum).Name}>",
        profile,
        promptBuilder ?? DefaultPromptBuilder,
        (_, _) => Task.FromResult(systemPrompt ?? "You are an expert at understanding user intentions and classifying them accurately."),
        tools: null,
        resultValidator: null)
    where TIn : IStepResult
    where TEnum : struct, Enum
{
    /// <summary>
    /// Default prompt builder that extracts the user message from the input and builds the classification prompt.
    /// Uses typed access to get the value properly.
    /// </summary>
    private static Task<string> DefaultPromptBuilder(TIn input, PipelineContext context)
    {
        // Extract user message from input based on its value type
        var userMessage = ExtractUserMessage(input);

        var sb = new StringBuilder();

        sb.AppendLine("Analyze the following user message and classify their intention:");
        sb.AppendLine();
        sb.AppendLine($"User Message: \"{userMessage}\"");
        sb.AppendLine();

        sb.AppendLine("Available Options:");
        var enumValues = Enum.GetValues<TEnum>();
        foreach (var value in enumValues)
        {
            var description = GetEnumDescription(value);
            sb.AppendLine($"- {value}: {description}");
        }

        // JSON Schema with field descriptions is injected automatically by BaseLlmStep

        return Task.FromResult(sb.ToString());
    }

    /// <summary>
    /// Extracts the user message from the input result, handling different input types.
    /// </summary>
    private static string ExtractUserMessage(TIn input)
    {
        // Try to get string value from IStepResult<string>
        if (input is IStepResult<string> stringResult && stringResult.Value != null)
        {
            return stringResult.Value;
        }

        // Try to get Value property if it's a string
        if (input.Value is string strValue)
        {
            return strValue;
        }

        // If Value is another IStepResult, try to extract its string content
        if (input.Value is IStepResult nestedResult && nestedResult.Value is string nestedStr)
        {
            return nestedStr;
        }

        // Last resort: if Value exists and is not null, try to get a meaningful string representation
        if (input.Value != null)
        {
            // Check if it has an OptimizedPrompt or Content property
            var valueType = input.Value.GetType();
            var optimizedPromptProp = valueType.GetProperty("OptimizedPrompt");
            if (optimizedPromptProp?.GetValue(input.Value) is string optimized)
            {
                return optimized;
            }

            var contentProp = valueType.GetProperty("Content");
            if (contentProp?.GetValue(input.Value) is string content)
            {
                return content;
            }
        }

        // If we can't extract a string, return empty
        // This should ideally not happen if the pipeline is correctly typed
        return string.Empty;
    }

    private static string GetEnumDescription(TEnum value)
    {
        var field = typeof(TEnum).GetField(value.ToString());
        var attribute = field?.GetCustomAttributes(typeof(DescriptionAttribute), false)
            .FirstOrDefault() as DescriptionAttribute;
        return attribute?.Description ?? value.ToString();
    }

}
