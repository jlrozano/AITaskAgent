namespace AITaskAgent.LLM.Streaming;

using AITaskAgent.Core.Models;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

/// <summary>
/// Base class for tag handlers with built-in observability.
///
/// Template Method: <see cref="OnCompleteTagAsync"/> times the action and emits the
/// <see cref="TagCompletedEvent"/> (success or failure); derived classes implement only
/// <see cref="InternalOnCompleteTagAsync"/>. The matching <see cref="TagStartedEvent"/> is emitted
/// by <see cref="StreamingTagParser"/> the moment the opening tag is recognised - before the body
/// has arrived - so a UI can show that an action is in progress while the model is still writing it.
/// </summary>
public abstract class TagHandlerBase : ITagHandler
{
    /// <summary>Gets the tag name without angle brackets (e.g., "write_file").</summary>
    public abstract string TagName { get; }

    /// <summary>Gets instructions for the LLM on how to use this tag.</summary>
    public abstract string GetInstructions();

    /// <summary>Gets the logger for this handler.</summary>
    protected abstract ILogger Logger { get; }

    /// <summary>
    /// Runs the action and emits <see cref="TagCompletedEvent"/>. A failure is rethrown after the
    /// event: the scanner turns it into an error placeholder so the turn survives.
    /// </summary>
    public async Task<string?> OnCompleteTagAsync(
        Dictionary<string, string> attributes,
        string content,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        string? placeholder = null;
        string? errorMessage = null;
        var success = true;

        try
        {
            placeholder = await InternalOnCompleteTagAsync(attributes, content, context, cancellationToken);
            stopwatch.Stop();

            Logger.LogDebug("Tag {TagName} completed successfully. Duration: {Duration}ms",
                TagName, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            success = false;
            errorMessage = ex.Message;
            Logger.LogError(ex, "Tag {TagName} failed: {Error}", TagName, errorMessage);
            throw;
        }
        finally
        {
            TagCompletedEvent completedEvent = new()
            {
                StepName = context.CurrentStepPath,
                TagName = TagName,
                Success = success,
                Duration = stopwatch.Elapsed,
                ErrorMessage = errorMessage,
                CorrelationId = context.CorrelationId
            };
            await context.SendEventAsync(EnrichCompletedEvent(completedEvent, attributes, content, placeholder), cancellationToken);
        }

        return placeholder;
    }

    /// <summary>The action itself. Throw to report failure.</summary>
    protected abstract Task<string?> InternalOnCompleteTagAsync(
        Dictionary<string, string> attributes,
        string content,
        PipelineContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hook to enrich the completed event with handler-specific data (e.g. the path and size of a
    /// written file). <paramref name="placeholder"/> is null when the action failed.
    /// </summary>
    protected virtual TagCompletedEvent EnrichCompletedEvent(
        TagCompletedEvent baseEvent,
        Dictionary<string, string> attributes,
        string content,
        string? placeholder) => baseEvent;
}
