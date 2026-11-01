using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Defines the contract for persisting and retrieving the state of a suspended pipeline.
/// The storage mechanism is agnostic (Memory, Redis, DB, etc.) and should implement the actual serialization logic.
/// </summary>
public interface IPipelineContextStorage
{
    /// <summary>
    /// Saves the pipeline context associated with a given correlation ID.
    /// </summary>
    Task SaveAsync(string correlationId, PipelineContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a previously suspended pipeline context by its correlation ID.
    /// </summary>
    Task<PipelineContext?> LoadAsync(string correlationId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the context after it has been fully completed or is no longer needed.
    /// </summary>
    Task DeleteAsync(string correlationId, CancellationToken cancellationToken);

    /// <summary>
    /// Clears all stored contexts. Primarily used for testing and cleanup.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken);
}
