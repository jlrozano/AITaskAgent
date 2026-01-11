using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.LLM.Tools.Abstractions;

/// <summary>
/// Represents a tool that can be invoked by an LLM.
/// </summary>
public interface ITool
{
    /// <summary>Gets the unique name of the tool.</summary>
    string Name { get; }

    /// <summary>Gets the description of what the tool does.</summary>
    string Description { get; }

    /// <summary>Gets the provider-agnostic tool definition.</summary>
    ToolDefinition GetDefinition();

    /// <summary>
    /// Executes the tool with the given arguments as JSON string.
    /// This overload includes observability context.
    /// </summary>
    /// <param name="argumentsJson">JSON string containing the tool arguments.</param>
    /// <param name="context">Pipeline context for observability (events, metrics, correlation).</param>
    /// <param name="stepName">Name of the step invoking this tool.</param>
    /// <param name="logger">Logger for tracing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the tool execution as a string.</returns>
    Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger logger,
        CancellationToken cancellationToken = default);
}
