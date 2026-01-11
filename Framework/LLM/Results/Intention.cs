using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using System.ComponentModel;

namespace AITaskAgent.LLM.Results;

/// <summary>
/// Data payload for intention analysis - this is what the LLM returns.
/// </summary>
[Description("Result of analyzing user intention and classifying it into predefined categories.")]
public sealed class Intention<TEnum>
    where TEnum : struct, Enum
{
    [Description("Select from 'Available Options' listed in the prompt. Each option has a description there - read them carefully to choose correctly. Use the exact option name.")]
    public required TEnum Option { get; init; }

    [Description("Detailed explanation of why this option was chosen based on the user's message.")]
    public required string Reasoning { get; init; }

    [Description("CRITICAL: The next step has ZERO access to conversation history. You MUST include ALL content inline. " +
        "For 'ReviewStory': COPY THE ENTIRE CURRENT STORY TEXT here, then add the modification instruction. " +
        "For 'Questions': COPY all relevant story text/versions/data here, then add the question. " +
        "For 'NewStory': describe what story to create. " +
        "WARNING: If you do not copy the story text here, the next step will fail because it cannot see the conversation.")]
    public required string OptimizedPrompt { get; init; }

    [Description("Confidence score between 0.0 and 1.0 indicating how certain the classification is.")]
    public float? Confidence { get; init; }

    [Description("Optional search keywords extracted from the user message for RAG (Retrieval-Augmented Generation).")]
    public List<string>? RagKeys { get; init; }

    [Description("Optional tags for filtering or categorizing the intention.")]
    public List<string>? RagTags { get; init; }

    public string GetDescription() => GetEnumDescription(Option);

    private static string GetEnumDescription(TEnum value)
    {
        var field = typeof(TEnum).GetField(value.ToString());
        var attribute = field?.GetCustomAttributes(typeof(DescriptionAttribute), false)
            .FirstOrDefault() as DescriptionAttribute;
        return attribute?.Description ?? value.ToString();
    }
}
//public interface IIntentionResult
//{
//    Enum Options { get; }
//}
public sealed class IntentionResult<TEnum>(IStep step, Intention<TEnum>? value) : StepResult<Intention<TEnum>>(step, value)//, IIntentionResult
    where TEnum : struct, Enum
{
    public Enum Options => throw new NotImplementedException();

    public override Task<(bool IsValid, string? Error)> ValidateAsync()
    {
        if (Value == null)
        {
            return Task.FromResult((false, (string?)"Intention data is null"));
        }

        if (!Enum.IsDefined(Value.Option))
        {
            return Task.FromResult((false, (string?)$"Option '{Value.Option}' is not a valid {typeof(TEnum).Name} value"));
        }

        if (string.IsNullOrWhiteSpace(Value.Reasoning))
        {
            return Task.FromResult((false, (string?)"Reasoning cannot be empty"));
        }

        if (string.IsNullOrWhiteSpace(Value.OptimizedPrompt))
        {
            return Task.FromResult((false, (string?)"OptimizedPrompt cannot be empty"));
        }

        return Task.FromResult((true, (string?)null));
    }
}

