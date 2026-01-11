using AITaskAgent.Core.Abstractions;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.Observability;
using System.Collections.Concurrent;

namespace AITaskAgent.Core.Models;

/// <summary>
/// Context passed through the pipeline execution containing shared state and services.
/// Uses ConcurrentDictionary for thread-safety in parallel execution scenarios.
/// </summary>
public sealed record PipelineContext(
    IEventChannel? EventChannel = null)
{
    /// <summary>Active conversation context.</summary>
    private string _currentPath = "";
    private readonly Stack<string> _lastPath = [];
    public ConversationContext Conversation { get; init; } = new();

    /// <summary>
    /// Unique identifier for this pipeline execution.
    /// Used for correlating logs and events across the entire flow.
    /// </summary>
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Metadata dictionary for custom data. Thread-safe for parallel access.
    /// </summary>
    public ConcurrentDictionary<string, object?> Metadata { get; init; } = [];

    /// <summary>
    /// Storage for step results. Thread-safe for parallel access.
    /// Key: step path (e.g., "pipeline/step1"). Value: step result.
    /// </summary>
    public ConcurrentDictionary<string, IStepResult> StepResults { get; init; } = [];

    /// <summary>
    /// Current path in the pipeline execution tree.
    /// Used for building step result keys. Internal setter for Pipeline use.
    /// </summary>
    public string CurrentPath { get => _currentPath; }

    internal void AddPathPart(string part)
    {
        _lastPath.Push(_currentPath);
        _currentPath = $"{(_currentPath == "" ? "" : $"{_currentPath}/")}{part}";
    }
    internal void RemovePathPart()
    {
        if (_lastPath.Count > 0)
        {
            _currentPath = _lastPath.Pop();
        }
        else
        {
            _currentPath = "";
        }
    }

    /// <summary>
    /// Creates a clone for parallel branch execution.
    /// Clones only the Conversation (each branch needs isolated history).
    /// Shares StepResults, Metadata, EventChannel (thread-safe).
    /// </summary>
    public PipelineContext CloneForBranch() => this with
    {
        Conversation = Conversation.Clone()
    };

    /// <summary>
    /// Sends an event to the EventChannel if available.
    /// Returns true if event was sent, false if channel is null.
    /// Swallows exceptions to prevent pipeline failures from event delivery issues.
    /// </summary>
    public async Task<bool> SendEventAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent
    {
        if (EventChannel == null)
        {
            return false;
        }

        try
        {
            await EventChannel.SendAsync(eventData, cancellationToken);
            return true;
        }
        catch
        {
            // Swallow event errors to prevent pipeline failure
            return false;
        }
    }
}
