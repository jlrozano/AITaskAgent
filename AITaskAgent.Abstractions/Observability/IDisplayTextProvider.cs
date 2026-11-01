namespace AITaskAgent.Observability;

/// <summary>What a display text is about: a pipeline step or an LLM tool.</summary>
public enum DisplayKind
{
    /// <summary>A pipeline step; the name is <c>IStep.Name</c>.</summary>
    Step,

    /// <summary>An LLM tool; the name is <c>ITool.Name</c>.</summary>
    Tool
}

/// <summary>Moment of a step or tool a display text is for.</summary>
public enum DisplayPhase
{
    /// <summary>The start event (<c>StepStartedEvent</c>, <c>ToolStartedEvent</c>).</summary>
    Start,

    /// <summary>The completion event (<c>StepCompletedEvent</c>, <c>ToolCompletedEvent</c>).</summary>
    End
}

/// <summary>
/// Optional host service that supplies the <c>Display</c> template of a step or a tool by name, for
/// the ones that do not declare their own (<see cref="IDisplayable"/>) or that the host cannot
/// subclass (for example, tools whose catalog lives in a JSON file). Register it in DI; the
/// framework asks <c>PipelineContext.ServiceProvider</c> for it.
/// </summary>
/// <remarks>
/// A template declared by the step or tool itself wins over the provider's. It is called once per
/// start and end event, so it should be cheap (a dictionary lookup). An exception thrown here is
/// logged as a warning and the event goes without <c>Display</c>.
/// </remarks>
public interface IDisplayTextProvider
{
    /// <summary>The template for <paramref name="name"/> in <paramref name="phase"/>, or null when there is none.</summary>
    string? GetTemplate(DisplayKind kind, string name, DisplayPhase phase);
}

/// <summary>
/// A step or tool that can declare the text it wants shown in the start and end events. Both are
/// templates rendered with <c>DisplayTextRenderer</c> (see the Display section of the event guide).
/// </summary>
public interface IDisplayable
{
    /// <summary>Template for the start event, or null/empty for none.</summary>
    string? DisplayStart { get; }

    /// <summary>Template for the completion event, or null/empty for none.</summary>
    string? DisplayEnd { get; }
}
