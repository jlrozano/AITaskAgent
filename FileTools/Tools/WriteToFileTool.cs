using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Tool to write content to a new file.
/// </summary>
public sealed class WriteToFileTool : BaseFileTool
{
    public override string Name => "write_to_file";
    public override string Description => "Use this tool to create new files.\n" +
        "IMPORTANT: You must provide the content as a list of strings in 'CodeLines'.\n" +
        "Example:\n" +
        "{\n" +
        "  \"TargetFile\": \"path/to/file.md\",\n" +
        "  \"Overwrite\": true,\n" +
        "  \"CodeLines\": [\n" +
        "    \"# Title\",\n" +
        "    \"\",\n" +
        "    \"Refer to [link](file.cs)\"\n" +
        "  ]\n" +
        "}";
    public override string? UsageGuidelines => "Use to create new files. Provide content as lines in CodeLines array.";

    public override ToolDefinition GetDefinition()
    {
        return new ToolDefinition
        {
            Name = Name,
            Description = Description,
            ParametersJsonSchema = """
            {
                "type": "object",
                "properties": {
                    "TargetFile": {
                        "type": "string",
                        "description": "The target file to create. Specify this FIRST."
                    },
                    "Overwrite": {
                        "type": "boolean",
                        "description": "Set to true to overwrite existing files."
                    },
                    "CodeLines": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "The content of the file as an array of strings. Each item is a line. Use for small files."
                    },
                    "CodeContent": {
                        "type": "string",
                        "description": "The full content of the file as a single string. RECOMMENDED for large files to avoid JSON errors."
                    }
                },
                "required": [
                    "TargetFile",
                    "Overwrite"
                ]
            }
            """
        };
    }

    protected override async Task<string> ExecuteInternalAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var (success, args, parseError) = TryParseArguments<Arguments>(
            argumentsJson,
            expectedSchemaHint: """{"TargetFile": "string", "Overwrite": true, "CodeContent": "string"}""",
            logger: logger);

        if (!success) return parseError!;

        if (args is null || string.IsNullOrWhiteSpace(args.TargetFile))
        {
            return "Error: TargetFile is required.";
        }

        // Resolve path in case it's relative
        var resolvedTargetFile = ResolvePath(args.TargetFile);

        await NotifyProgressAsync($"💾 Writing to file '{resolvedTargetFile}'", context, cancellationToken);

        ValidatePath(resolvedTargetFile);

        if (File.Exists(resolvedTargetFile) && !args.Overwrite)
        {
            return $"Error: File {resolvedTargetFile} already exists and Overwrite is false.";
        }

        var directory = Path.GetDirectoryName(resolvedTargetFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Support both CodeContent (preferred) and CodeLines (legacy/fallback)
        string content = args.CodeContent ??
                        (args.CodeLines != null ? string.Join("\n", args.CodeLines) : string.Empty);

        await File.WriteAllTextAsync(resolvedTargetFile, content, cancellationToken);

        logger.LogInformation("Written file {File}", resolvedTargetFile);

        return $"Created file {resolvedTargetFile} with requested content.";
    }

    private record Arguments(
        string TargetFile,
        bool Overwrite,
        string? CodeContent,
        List<string>? CodeLines);
}
