using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Thread-safe registry for global pipeline middlewares.
/// Middlewares registered here are applied to ALL pipeline executions in registration order.
/// </summary>
public static class PipelineMiddlewareRegistry
{
    private static readonly Lock _lock = new();
    private static readonly List<IPipelineMiddleware> _middlewares = [];

    /// <summary>
    /// Registers a middleware to be applied to all pipelines.
    /// Duplicates are ignored (same instance won't be registered twice).
    /// </summary>
    /// <param name="middleware">The middleware to register.</param>
    public static void Register(IPipelineMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        lock (_lock)
        {
            if (!_middlewares.Contains(middleware))
            {
                _middlewares.Add(middleware);
            }
        }
    }

    /// <summary>
    /// Registers multiple middlewares to be applied to all pipelines.
    /// Duplicates are ignored. Order is preserved.
    /// </summary>
    /// <param name="middlewares">The middlewares to register.</param>
    public static void RegisterRange(IEnumerable<IPipelineMiddleware> middlewares)
    {
        ArgumentNullException.ThrowIfNull(middlewares);
        lock (_lock)
        {
            foreach (var m in middlewares)
            {
                if (!_middlewares.Contains(m))
                {
                    _middlewares.Add(m);
                }
            }
        }
    }

    /// <summary>
    /// Gets all registered middlewares in registration order.
    /// No lock needed: list is built at startup and never modified after.
    /// Concurrent reads of an immutable list are safe.
    /// </summary>
    /// <returns>Read-only list of registered middlewares.</returns>
    public static IReadOnlyList<IPipelineMiddleware> GetMiddlewares()
    {
        // No lock: read-many pattern, list is immutable after startup
        return _middlewares;
    }

    /// <summary>
    /// Clears all registered middlewares. Use with caution.
    /// Intended for testing scenarios only.
    /// </summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _middlewares.Clear();
        }
    }
}
