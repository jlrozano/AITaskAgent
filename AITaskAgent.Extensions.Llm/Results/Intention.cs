using System.ComponentModel;

namespace AITaskAgent.LLM.Results;

/// <summary>
/// Detailed result of analyzing user intention using dynamic options.
/// </summary>
[Description("Result of analyzing user intention and classifying it into dynamically provided categories.")]
public sealed class Intention
{
    [Description("Select EXACTLY ONE from the 'Available Options' listed in the prompt. Use the exact option name. Do not invent new options.")]
    public string SelectedOption { get; init; } = string.Empty;

    [Description("Detailed explanation of why this option was chosen based on the user's message.")]
    public string SelectionReason { get; init; } = string.Empty;

    [Description("User-facing feedback. For AMBIGUOUS / NeedMoreInfo=true, include a single closed clarification question and a list of chip-like options the UI can render.")]
    public UserFeedbackInfo UserFeedback { get; init; } = new();

    [Description("An optimized and detailed prompt for the next step, expanding on what the user is asking for.")]
    public string DetailedPrompt { get; init; } = string.Empty;

    [Description("Additional information extracted from the user message as specified in the system prompt.")]
    public Dictionary<string, object> ExtraInformation { get; init; } = [];
}

[Description("User-facing feedback payload. Used to render a friendly message and optional choice chips.")]
public sealed class UserFeedbackInfo
{
    [Description("Friendly message to show the user acknowledging their request and/or explaining what happens next.")]
    public string Message { get; init; } = string.Empty;
    [Description("True if more information is needed to fulfill the request.")]
    public bool NeedMoreInfo { get; init; }
    [Description("If NeedMoreInfo=true, provide a single closed clarification question and optional choice chips for the user to select from.")]
    public List<Question>? Questions { get; init; }
    [Description("If true, the user may also answer with free text instead of selecting a chip.")]
    public bool AllowFreeText { get; init; } = true;
}

[Description("A single selectable option that can be rendered as a chip/button.")]
public sealed class UserChoiceChip
{
    [Description("Stable id for the chip (e.g., QUERY, DISCOVERY, PLANNING, ORCHESTRATION).")]
    public string Id { get; init; } = string.Empty;

    [Description("Short label to display to the user.")]
    public string Label { get; init; } = string.Empty;

    [Description("Optional short description to help the user choose.")]
    public string? Description { get; init; }
}

public sealed class Question
{
    [Description("Ask ONE closed clarification question here. Keep it short and action-oriented.")]
    public string? ClarificationQuestion { get; init; }
    [Description("True if multiples choices can be selected. False otherwise.")]
    public bool Multiple { get; init; } = false;
    [Description("If NeedMoreInfo=true, provide choices the UI can render as chips. The user's selection should map to a valid next intent (e.g., QUERY, DISCOVERY, PLANNING, ORCHESTRATION).")]
    public List<UserChoiceChip>? ChoiceChips { get; init; }
}
