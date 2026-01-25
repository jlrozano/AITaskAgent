namespace AITaskAgent.LLM.Streaming;

using AITaskAgent.Core;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

/// <summary>
/// Abstract base class for streaming tag handlers with built-in observability.
/// Uses Template Method pattern - public methods control flow, internal methods are your logic.
/// </summary>
public abstract class StreamingTagHandlerBase : IStreamingTagHandler
{
    /// <summary>Gets the tag name without angle brackets (e.g., "write_file").</summary>
    public abstract string TagName { get; }

    /// <summary>Gets instructions for the LLM on how to use this tag.</summary>
    public abstract string GetInstructions();

    /// <summary>Gets the logger for this handler.</summary>
    protected abstract ILogger Logger { get; }

    // ═══════════════════════════════════════════════════════════════════════════
    // TEMPLATE METHOD PATTERN - Controls flow and emits events
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called when tag opening is detected. Emits TagStartedEvent and calls internal logic.
    /// </summary>
    public async Task<ITagContext?> OnTagStartAsync(
        Dictionary<string, string> attributes,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Emit started event
        var startedEvent = new TagStartedEvent
        {
            StepName = "StreamingTag",
            TagName = TagName,
            Attributes = attributes,
            CorrelationId = context.CorrelationId
        };
        var enrichedStartedEvent = EnrichStartedEvent(startedEvent, attributes, context);
        await context.SendEventAsync(enrichedStartedEvent, cancellationToken);

        Logger.LogDebug("Tag {TagName} starting with {AttributeCount} attributes", TagName, attributes.Count);

        // Call internal logic
        var tagContext = await InternalOnTagStartAsync(attributes, context, cancellationToken);

        // Store stopwatch in context for duration calculation
        if (tagContext is StreamingTagContextBase baseContext)
        {
            baseContext.Stopwatch = stopwatch;
        }

        return tagContext;
    }

    /// <summary>
    /// Called for each content chunk inside the tag.
    /// </summary>
    public Task OnContentAsync(
        ITagContext tagContext,
        string content,
        CancellationToken cancellationToken)
    {
        return InternalOnContentAsync(tagContext, content, cancellationToken);
    }

    /// <summary>
    /// Called when tag closing is detected. Calls internal logic and emits TagCompletedEvent.
    /// </summary>
    public async Task<string?> OnTagEndAsync(
        ITagContext tagContext,
        CancellationToken cancellationToken)
    {
        var stopwatch = (tagContext as StreamingTagContextBase)?.Stopwatch ?? Stopwatch.StartNew();
        string? placeholder = null;
        string? errorMessage = null;
        var success = true;

        try
        {
            placeholder = await InternalOnTagEndAsync(tagContext, cancellationToken);
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
            // Emit completed event
            var completedEvent = new TagCompletedEvent
            {
                StepName = "StreamingTag",
                TagName = TagName,
                Success = success,
                Duration = stopwatch.Elapsed,
                ErrorMessage = errorMessage,
                CorrelationId = (tagContext as StreamingTagContextBase)?.CorrelationId
            };
            var enrichedCompletedEvent = EnrichCompletedEvent(completedEvent, placeholder, tagContext);
            var pipelineContext = (tagContext as StreamingTagContextBase)?.PipelineContext;
            if (pipelineContext != null)
            {
                await pipelineContext.SendEventAsync(enrichedCompletedEvent, cancellationToken);
            }
        }

        return placeholder;
    }

    /// <summary>
    /// Called with complete tag content when LLM is not streaming.
    /// </summary>
    public async Task<string?> OnCompleteTagAsync(
        Dictionary<string, string> attributes,
        string content,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Emit started event
        var startedEvent = new TagStartedEvent
        {
            StepName = "BatchTag",
            TagName = TagName,
            Attributes = attributes,
            CorrelationId = context.CorrelationId
        };
        await context.SendEventAsync(EnrichStartedEvent(startedEvent, attributes, context), cancellationToken);

        string? placeholder = null;
        string? errorMessage = null;
        var success = true;

        try
        {
            placeholder = await InternalOnCompleteTagAsync(attributes, content, context, cancellationToken);
            stopwatch.Stop();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            success = false;
            errorMessage = ex.Message;
            Logger.LogError(ex, "Tag {TagName} (batch) failed: {Error}", TagName, errorMessage);
            throw;
        }
        finally
        {
            // Emit completed event
            var completedEvent = new TagCompletedEvent
            {
                StepName = "BatchTag",
                TagName = TagName,
                Success = success,
                Duration = stopwatch.Elapsed,
                ErrorMessage = errorMessage,
                CorrelationId = context.CorrelationId
            };
            await context.SendEventAsync(EnrichCompletedEvent(completedEvent, placeholder, null), cancellationToken);
        }

        return placeholder;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ABSTRACT METHODS - Implement in derived classes
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Internal logic for tag start (streaming mode).</summary>
    protected abstract Task<ITagContext?> InternalOnTagStartAsync(
        Dictionary<string, string> attributes,
        PipelineContext context,
        CancellationToken cancellationToken);

    /// <summary>Internal logic for content processing.</summary>
    protected abstract Task InternalOnContentAsync(
        ITagContext tagContext,
        string content,
        CancellationToken cancellationToken);

    /// <summary>Internal logic for tag end (streaming mode).</summary>
    protected abstract Task<string?> InternalOnTagEndAsync(
        ITagContext tagContext,
        CancellationToken cancellationToken);

    /// <summary>Internal logic for batch mode processing.</summary>
    protected abstract Task<string?> InternalOnCompleteTagAsync(
        Dictionary<string, string> attributes,
        string content,
        PipelineContext context,
        CancellationToken cancellationToken);

    // ═══════════════════════════════════════════════════════════════════════════
    // VIRTUAL HOOKS - Override to enrich events
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Hook to enrich the started event with handler-specific data.</summary>
    protected virtual TagStartedEvent EnrichStartedEvent(
        TagStartedEvent baseEvent,
        Dictionary<string, string> attributes,
        PipelineContext context) => baseEvent;

    /// <summary>Hook to enrich the completed event with handler-specific data.</summary>
    protected virtual TagCompletedEvent EnrichCompletedEvent(
        TagCompletedEvent baseEvent,
        string? placeholder,
        ITagContext? tagContext) => baseEvent;
}

/// <summary>
/// Base class for tag contexts with common observability fields.
/// </summary>
public abstract class StreamingTagContextBase : ITagContext
{
    public abstract string TagName { get; }
    public abstract Dictionary<string, string> Attributes { get; }

    /// <summary>Stopwatch for duration tracking (set by base class).</summary>
    public Stopwatch? Stopwatch { get; set; }

    /// <summary>Pipeline context for event emission.</summary>
    public PipelineContext? PipelineContext { get; init; }

    /// <summary>Correlation ID for tracing.</summary>
    public string? CorrelationId { get; init; }

    public abstract ValueTask DisposeAsync();
}
