using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Text.Json;

namespace McpIntegration.Tools;

/// <summary>
/// Wraps an MCP tool to implement ITool interface.
/// Enables MCP tools to be used seamlessly in AITaskAgent pipelines.
/// </summary>
public sealed class McpToolWrapper(
    Tool mcpTool,
    McpClient client,
    string serverName) : ITool
{
    private readonly Tool _mcpTool = mcpTool ?? throw new ArgumentNullException(nameof(mcpTool));
    private readonly McpClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly string _serverName = serverName;

    /// <inheritdoc/>
    public string Name => _mcpTool.Name;

    /// <inheritdoc/>
    public string Description => _mcpTool.Description ?? string.Empty;

    /// <inheritdoc/>
    public ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        ParametersJsonSchema = GetParametersSchema()
    };

    /// <inheritdoc/>
    public async Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "Executing MCP tool {ToolName} from server {ServerName} with arguments: {Args}",
            Name, _serverName, argumentsJson);

        var stopwatch = Stopwatch.StartNew();

        // Emit ToolStarted event. Surface the call arguments under "parameters"
        // so the orchestrator's EventMapper can forward them to the UI (it reads
        // AdditionalData["parameters"]). Empty/invalid argument payloads are omitted.
        var startedData = new Dictionary<string, object> { { "McpServer", _serverName } };
        if (TryParseJson(argumentsJson) is { } parameters)
        {
            startedData["parameters"] = parameters;
        }
        await context.SendEventAsync(new ToolStartedEvent
        {
            StepName = stepName,
            CorrelationId = context.CorrelationId,
            Timestamp = DateTimeOffset.UtcNow,
            ToolName = Name,
            AdditionalData = startedData
        }, cancellationToken);

        try
        {
            // Parse arguments from JSON
            var arguments = ParseArguments(argumentsJson);

            // Call MCP tool
            var result = await _client.CallToolAsync(
                new CallToolRequestParams
                {
                    Name = Name,
                    Arguments = arguments
                },
                cancellationToken: cancellationToken);

            if (result.IsError == true)
            {
                // The MCP server reported a tool-level failure via CallToolResult.IsError.
                // Throw so the catch block below emits ToolCompletedEvent with Success = false
                // and the error detail - mirrors LlmTool's exception-based failure contract -
                // instead of silently reporting success with the error text as the "result".
                var errorDetail = ExtractTextResult(result);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(errorDetail)
                        ? $"MCP tool '{Name}' from server '{_serverName}' reported an error " +
                          "(IsError=true) with no error detail."
                        : errorDetail);
            }

            // Extract and return text content
            var textResult = ExtractTextResult(result);

            stopwatch.Stop();

            logger.LogDebug(
                "MCP tool {ToolName} completed successfully. Result length: {Length}",
                Name, textResult.Length);

            // Emit ToolCompleted event (Success). Surface the tool's textual
            // output under "result" so the orchestrator's EventMapper (which reads
            // AdditionalData["result"]) can forward it to the UI. Parse it as JSON
            // when possible so it renders as structured data; otherwise keep the
            // raw text. Empty output is omitted.
            var completedData = new Dictionary<string, object> { { "McpServer", _serverName } };
            if (TryParseJson(textResult) is { } resultJson)
            {
                completedData["result"] = resultJson;
            }
            else if (!string.IsNullOrEmpty(textResult))
            {
                completedData["result"] = textResult;
            }
            await context.SendEventAsync(new ToolCompletedEvent
            {
                StepName = stepName,
                CorrelationId = context.CorrelationId,
                Timestamp = DateTimeOffset.UtcNow,
                ToolName = Name,
                Success = true,
                Duration = stopwatch.Elapsed,
                AdditionalData = completedData
            }, cancellationToken);

            return textResult;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            logger.LogError(ex,
                "MCP tool {ToolName} from server {ServerName} failed: {Error}",
                Name, _serverName, ex.Message);

            // Emit ToolCompleted event (Failure)
            await context.SendEventAsync(new ToolCompletedEvent
            {
                StepName = stepName,
                CorrelationId = context.CorrelationId,
                Timestamp = DateTimeOffset.UtcNow,
                ToolName = Name,
                Success = false,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message,
                AdditionalData = new() { { "McpServer", _serverName } }
            }, cancellationToken);

            throw;
        }
    }

    /// <summary>
    /// Parses a JSON string into a Newtonsoft <see cref="JToken"/> so it can be
    /// embedded into a progress event's AdditionalData and serialized as
    /// structured JSON by the SSE writer. Returns null for empty or non-JSON text.
    /// </summary>
    private static JToken? TryParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JToken.Parse(text);
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the JSON arguments string into a dictionary for MCP call.
    /// </summary>
    private static Dictionary<string, JsonElement> ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson) ?? [];
        }
        catch (JsonException)
        {
            // Fallback for simple object parsing if not direct string dictionary
            try
            {
                var element = JsonDocument.Parse(argumentsJson).RootElement;
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, JsonElement>();
                    foreach (var property in element.EnumerateObject())
                    {
                        dict[property.Name] = property.Value;
                    }
                    return dict;
                }
                return [];
            }
            catch
            {
                return [];
            }
        }
    }

    /// <summary>
    /// Extracts text content from MCP call result.
    /// </summary>
    private static string ExtractTextResult(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0)
        {
            // MCP servers are expected to mirror StructuredContent as text in Content for
            // backward compatibility, but not all of them do. Fall back to the structured
            // payload so a well-formed, structured-only result (or error) isn't silently
            // reported as empty.
            return result.StructuredContent?.ToJsonString() ?? string.Empty;
        }

        // Collect all text content blocks
        var textParts = new List<string>();

        foreach (var content in result.Content)
        {
            if (content is TextContentBlock textBlock)
            {
                textParts.Add(textBlock.Text);
            }
            else
            {
                // For non-text content, serialize to JSON
                textParts.Add(JsonSerializer.Serialize(content));
            }
        }

        return string.Join("\n", textParts);
    }

    /// <summary>
    /// Gets the JSON schema for tool parameters.
    /// </summary>
    private string GetParametersSchema()
    {
        // McpClientTool exposes InputSchema as JsonElement
        if (_mcpTool.InputSchema is { } schema)
        {
            return schema.GetRawText();
        }

        // Default empty schema
        return """{"type": "object", "properties": {}}""";
    }
}
