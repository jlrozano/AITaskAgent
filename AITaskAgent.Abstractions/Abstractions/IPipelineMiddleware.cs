using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// Middleware for pipeline step execution.
/// Follows the same pattern as ASP.NET Core middleware.
/// </summary>
public interface IPipelineMiddleware
{
    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    /// <param name="step">The step being executed.</param>
    /// <param name="input">The input result from the previous step.</param>
    /// <param name="context">The pipeline context.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <param name="next">Delegate to invoke the next middleware. Accepts CancellationToken to allow token modification (e.g., timeout).</param>
    /// <returns>The step result.</returns>
    Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken);
}
