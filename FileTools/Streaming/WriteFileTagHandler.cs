namespace AITaskAgent.FileTools.Streaming;

using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Streaming;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
/// Tag handler that creates or overwrites a file with the tag body. The framework hands it the
/// complete tag whichever transport (streaming or not) and channel (content or reasoning) carried it.
/// </summary>
/// <remarks>
/// Sandboxing is delegated entirely to <see cref="FileToolsConfiguration"/> - the same source of
/// truth the tools use - instead of keeping a private base directory captured at construction
/// time, which used to go stale after a runtime sandbox change (<c>SetRootDirectory</c>,
/// <c>AddAllowedDirectory</c>) and let the tag write where the tools would no longer allow, or
/// reject a path a runtime grant had just authorized.
/// </remarks>
public sealed class WriteFileTagHandler(ILogger<WriteFileTagHandler> logger) : TagHandlerBase
{
    public override string TagName => "write_file";

    protected override ILogger Logger => logger;

    // Concrete on purpose: the model reads this once in the system prompt and again as a tool
    // result whenever it calls the tag as a tool. Abstract wording ("use this tag to write files")
    // did not stop reasoning models from reaching for tool_call; an exact right/wrong pair and a
    // full worked example is what it has to imitate.
    public override string GetInstructions()
    {
        return """
            ### Tag: write_file
            Creates or overwrites ONE file with the exact text you put between the opening and closing tag.

            It is NOT a function/tool. There is no `write_file` entry in your tools list, so a tool_call
            named `write_file` always fails. You use it by TYPING the tag literally in your reply text:

            WRONG (fails):   tool_call { "name": "write_file", "arguments": { "path": "notes.md", ... } }
            RIGHT (works):   <write_file path="notes.md" summary="Meeting notes">...file text...</write_file>

            **Syntax** (attributes in double quotes, body on its own lines):
            <write_file path="relative/path/to/file.ext" summary="what this file is, in a few words">
            the complete file content, verbatim
            </write_file>

            **Rules:**
            - `path` (required): relative to the working directory. Use forward slashes.
            - `summary` (optional): shown to the user in place of the file content. Do not put double quotes inside it.
            - The body is written byte for byte, so do NOT wrap it in ``` fences and do not escape anything.
            - The body must not contain the literal text `</write_file>`.
            - One tag per file. To write two files, write two tags.
            - Write the tag in your final answer, not while thinking, and close it always.

            **Complete example** - user asked for a Python script that prints the primes below 20:
            <write_file path="primes.py" summary="Prints the primes below 20">
            for n in range(2, 20):
                if all(n % d for d in range(2, int(n ** 0.5) + 1)):
                    print(n)
            </write_file>
            He creado `primes.py`, que recorre los números del 2 al 19 e imprime los que no tienen divisores.

            The user never sees the file content, only a short reference like
            `[write_file: primes.py (98 bytes) — Prints the primes below 20]`, so after the tag ALWAYS
            add one or two sentences saying what you created, as in the example.
            """;
    }

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

        var fullPath = Path.GetFullPath(Path.Combine(FileToolsConfiguration.RootDirectory, relativePath));

        if (!FileToolsConfiguration.IsAllowed(fullPath))
        {
            throw new UnauthorizedAccessException($"Invalid path traversal attempt: {relativePath}");
        }

        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        logger.LogInformation("Writing file: {Path}", fullPath);

        // No BOM: a plain Encoding.UTF8 emits one, which breaks shebangs and strict JSON/YAML
        // parsers, and the byte count below would then disagree with what got written.
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllTextAsync(fullPath, content, utf8NoBom, cancellationToken);

        var bytesWritten = utf8NoBom.GetByteCount(content);

        logger.LogInformation("Completed file write: {Path} ({Bytes} bytes)", fullPath, bytesWritten);

        return GeneratePlaceholder(attributes, bytesWritten);
    }

    protected override TagCompletedEvent EnrichCompletedEvent(
        TagCompletedEvent baseEvent,
        Dictionary<string, string> attributes,
        string content,
        string? placeholder)
    {
        attributes.TryGetValue("path", out var path);

        Dictionary<string, object> data = new()
        {
            ["path"] = path ?? "unknown"
        };

        if (placeholder != null)
        {
            data["bytesWritten"] = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetByteCount(content);
        }

        return baseEvent with { AdditionalData = data };
    }

    // ============ HELPERS ============

    /// <summary>
    /// Builds the text that replaces the tag - both on screen and in the conversation history.
    ///
    /// The path and size are always stated, even when the model supplied a summary: this text is
    /// the only record left of the write once the tag body is stripped, and a bare summary
    /// ("Analysis of the project") does not say that a file was written, so a model reading its own
    /// history cannot tell the action completed and calls it again.
    /// </summary>
    private static string GeneratePlaceholder(Dictionary<string, string> attributes, long bytesWritten)
    {
        attributes.TryGetValue("path", out var path);
        var reference = $"{TagNameConstant}: {path ?? "unknown"} ({FormatBytes(bytesWritten)})";

        if (attributes.TryGetValue("summary", out var summary) && !string.IsNullOrWhiteSpace(summary))
        {
            return $"[{reference} — {summary.Trim()}]";
        }

        return $"[{reference}]";
    }

    private const string TagNameConstant = "write_file";

    // Invariant on purpose: this string is part of the text the model reads back from its own
    // conversation history, so it must not change shape with the machine's locale ("10.1 KB"
    // becoming "10,1 KB" depending on who runs it).
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024        => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _             => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB")
    };
}
