using AITaskAgent.Core.Models;

namespace AITaskAgent.LLM.Streaming;

/// <summary>
/// Handler for an in-text action tag (e.g. <c>&lt;write_file&gt;</c>) found in an LLM response.
///
/// The framework scans every response with <see cref="StreamingTagParser"/> - streamed or not,
/// reasoning channel or content - and calls <see cref="OnCompleteTagAsync"/> exactly once per
/// closed tag, with its complete body. A handler never sees a partial tag: an opening tag the
/// model never closes is handed back to the user as plain text and no handler runs for it.
/// </summary>
public interface ITagHandler
{
    /// <summary>Tag name without angle brackets (e.g., "write_file").</summary>
    string TagName { get; }

    /// <summary>
    /// Returns instructions for the LLM on how to use this tag. Injected into the system prompt,
    /// like <c>ITool.GetDefinition()</c> is for tools.
    /// </summary>
    string GetInstructions();

    /// <summary>
    /// Runs the action for one complete tag.
    /// </summary>
    /// <param name="attributes">Attributes parsed from the opening tag.</param>
    /// <param name="content">The tag body, without the single line break that follows the opening tag or precedes the closing one.</param>
    /// <param name="context">Pipeline context of the step that received the response.</param>
    /// <param name="cancellationToken">Token cancelling the action.</param>
    /// <returns>
    /// Placeholder text that replaces the tag both on screen and in the conversation history. It is
    /// the only record left of the action once the body is stripped, so it should say what was done
    /// (e.g. the path and size of a written file), not just echo a summary.
    /// </returns>
    Task<string?> OnCompleteTagAsync(
        Dictionary<string, string> attributes,
        string content,
        PipelineContext context,
        CancellationToken cancellationToken);
}
