namespace AITaskAgent.FileTools.Streaming;

using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Streaming;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Text;

/// <summary>
/// Tag handler for writing files (supports both streaming and non-streaming modes).
/// Inherits from StreamingTagHandlerBase for automatic event emission.
/// </summary>
public sealed class WriteFileTagHandler(string baseDirectory, ILogger<WriteFileTagHandler> logger) : StreamingTagHandlerBase
{
    public override string TagName => "write_file";

    protected override ILogger Logger => logger;

    public override string GetInstructions()
    {
        return @"### Tag: write_file
Use this tag to create or overwrite files with streaming content.
This uses DIRECT XML COMMANDS within your text response. DO NOT invoke this as a tool.

**Usage:**
```xml
<write_file path=""path/to/file"" summary=""Action summary"">
Content...
</write_file>
```

**Parameters:**
- `path`: Relative path to the file.
- `summary`: Short description of the action (shown to user as placeholder).

**IMPORTANT:** The file content inside the tag is NOT visible to the user.
After using this tag, ALWAYS explain what you created, e.g.:
'He creado el archivo `script.py` que contiene [breve descripción].'

**Example:**
<write_file path=""script.py"" summary=""Creating python script"">
print('hello')
</write_file>
He creado el archivo `script.py` con un simple script que imprime 'hello'.";
    }

    // ============ STREAMING MODE ============

    protected override Task<ITagContext?> InternalOnTagStartAsync(
        Dictionary<string, string> attributes,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        if (!attributes.TryGetValue("path", out var relativePath))
        {
            throw new InvalidOperationException("write_file tag requires 'path' attribute");
        }

        var fullPath = Path.Combine(baseDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        logger.LogInformation("[STREAMING] Starting file write: {Path}", fullPath);

        var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        var writer = new StreamWriter(fileStream, Encoding.UTF8);

        return Task.FromResult<ITagContext?>(new WriteFileContext(TagName, attributes, fullPath, writer, logger, context));
    }

    protected override async Task InternalOnContentAsync(
        ITagContext tagContext,
        string content,
        CancellationToken cancellationToken)
    {
        if (tagContext is WriteFileContext writeContext)
        {
            await writeContext.Writer.WriteAsync(content);
            writeContext.BytesWritten += Encoding.UTF8.GetByteCount(content);
        }
    }

    protected override async Task<string?> InternalOnTagEndAsync(
        ITagContext tagContext,
        CancellationToken cancellationToken)
    {
        if (tagContext is WriteFileContext writeContext)
        {
            await writeContext.Writer.FlushAsync(cancellationToken);

            logger.LogInformation(
                "[STREAMING] Completed file write: {Path} ({Bytes} bytes)",
                writeContext.FilePath,
                writeContext.BytesWritten);

            return GeneratePlaceholder(writeContext.Attributes, writeContext.BytesWritten);
        }

        return null;
    }

    // ============ NON-STREAMING MODE ============

    protected override async Task<string?> InternalOnCompleteTagAsync(
        Dictionary<string, string> attributes,
        string content,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        if (!attributes.TryGetValue("path", out var relativePath))
        {
            throw new InvalidOperationException("write_file tag requires 'path' attribute");
        }

        var fullPath = Path.Combine(baseDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        logger.LogInformation("[BATCH] Writing file: {Path}", fullPath);

        // Write entire content at once (more efficient for non-streaming)
        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, cancellationToken);

        var bytesWritten = Encoding.UTF8.GetByteCount(content);

        logger.LogInformation(
            "[BATCH] Completed file write: {Path} ({Bytes} bytes)",
            fullPath,
            bytesWritten);

        return GeneratePlaceholder(attributes, bytesWritten);
    }

    // ============ ENRICHMENT HOOKS ============

    protected override TagStartedEvent EnrichStartedEvent(
        TagStartedEvent baseEvent,
        Dictionary<string, string> attributes,
        PipelineContext context)
    {
        attributes.TryGetValue("path", out var path);
        return baseEvent with
        {
            AdditionalData = new Dictionary<string, object>
            {
                ["path"] = path ?? "unknown"
            }
        };
    }

    protected override TagCompletedEvent EnrichCompletedEvent(
        TagCompletedEvent baseEvent,
        string? placeholder,
        ITagContext? tagContext)
    {
        if (tagContext is WriteFileContext writeContext)
        {
            return baseEvent with
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["path"] = writeContext.FilePath,
                    ["bytesWritten"] = writeContext.BytesWritten
                }
            };
        }
        return baseEvent;
    }

    // ============ HELPERS ============

    private static string GeneratePlaceholder(Dictionary<string, string> attributes, long bytesWritten)
    {
        // Use LLM-provided summary if available, otherwise default placeholder
        if (attributes.TryGetValue("summary", out var summary) && !string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        return $"[File: {attributes["path"]} ({bytesWritten} bytes written)]";
    }
}

/// <summary>
/// Context for an active file write operation in streaming mode.
/// </summary>
internal sealed class WriteFileContext(
    string tagName,
    Dictionary<string, string> attributes,
    string filePath,
    StreamWriter writer,
    ILogger logger,
    PipelineContext context) : StreamingTagContextBase
{
    public override string TagName { get; } = tagName;
    public override Dictionary<string, string> Attributes { get; } = attributes;
    public string FilePath { get; } = filePath;
    public StreamWriter Writer { get; } = writer;
    public long BytesWritten { get; set; }

    public new PipelineContext PipelineContext { get; } = context;

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await Writer.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error disposing file writer for {Path}", FilePath);
        }
    }
}
