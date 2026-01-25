using AITaskAgent.Core.Models;

namespace AITaskAgent.LLM.Streaming;

/// <summary>
/// Handler for processing tags in LLM responses (streaming and non-streaming modes).
/// </summary>
public interface IStreamingTagHandler
{
    /// <summary>Tag name without angle brackets (e.g., "write_file")</summary>
    string TagName { get; }

    /// <summary>
    /// Returns instructions for the LLM on how to use this tag.
    /// Similar to ITool.GetDefinition() for standardized system prompt injection.
    /// </summary>
    string GetInstructions();

    /// <summary>
    /// [STREAMING MODE] Called when tag opening is detected with parsed attributes.
    /// Return null to skip streaming mode (will use OnCompleteTagAsync instead).
    /// </summary>
    Task<ITagContext?> OnTagStartAsync(
        Dictionary<string, string> attributes,
        PipelineContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// [STREAMING MODE] Called for each content chunk inside the tag.
    /// Only called if OnTagStartAsync returned a non-null context.
    /// </summary>
    Task OnContentAsync(
        ITagContext tagContext,
        string content,
        CancellationToken cancellationToken);

    /// <summary>
    /// [STREAMING MODE] Called when tag closing is detected.
    /// Only called if OnTagStartAsync returned a non-null context.
    /// </summary>
    /// <returns>Placeholder text to replace the tag in conversation. Uses 'summary' attribute if provided by LLM.</returns>
    Task<string?> OnTagEndAsync(
        ITagContext tagContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// [NON-STREAMING MODE] Called with complete tag content when LLM is not streaming.
    /// More efficient for batch processing - receives all content at once.
    /// </summary>
    /// <returns>Placeholder text to replace the tag in conversation.</returns>
    Task<string?> OnCompleteTagAsync(
        Dictionary<string, string> attributes,
        string content,
        PipelineContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Context for a specific tag instance being processed in streaming mode.
/// </summary>
public interface ITagContext : IAsyncDisposable
{
    string TagName { get; }
    Dictionary<string, string> Attributes { get; }
}
