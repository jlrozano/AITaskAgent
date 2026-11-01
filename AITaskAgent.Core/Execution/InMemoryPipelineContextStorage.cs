using AITaskAgent.Core.Models;
using System.Collections.Concurrent;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Provides volatile, in-memory storage for pipeline contexts. Used primarily for testing and fast local executions.
/// </summary>
public class InMemoryPipelineContextStorage : IPipelineContextStorage
{
    private readonly ConcurrentDictionary<string, PipelineContext> _storage = new();

    /// <inheritdoc/>
    public Task SaveAsync(string correlationId, PipelineContext context, CancellationToken cancellationToken)
    {
        _storage[correlationId] = context;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<PipelineContext?> LoadAsync(string correlationId, CancellationToken cancellationToken)
    {
        _storage.TryGetValue(correlationId, out PipelineContext? context);
        return Task.FromResult(context);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string correlationId, CancellationToken cancellationToken)
    {
        _storage.TryRemove(correlationId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        _storage.Clear();
        return Task.CompletedTask;
    }
}
