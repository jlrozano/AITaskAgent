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
                "type": "OBJECT",
                "properties": {
                    "TargetFile": {
                        "type": "STRING",
                        "description": "The target file to create. Specify this FIRST."
                    },
                    "Overwrite": {
                        "type": "BOOLEAN",
                        "description": "Set to true to overwrite existing files."
                    },
                    "CodeLines": {
                        "type": "ARRAY",
                        "items": { "type": "STRING" },
                        "description": "The content of the file as an array of strings. Each item is a line."
                    }
                },
                "required": [
                    "TargetFile",
                    "Overwrite",
                    "CodeLines"
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
        // Console.WriteLine($"[DEBUG] WriteToFileTool JSON: {argumentsJson}");
        var args = ParseArguments<Arguments>(argumentsJson);
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

        string content = args.CodeLines != null ? string.Join("\n", args.CodeLines) : string.Empty;
        await File.WriteAllTextAsync(resolvedTargetFile, content, cancellationToken);

        logger.LogInformation("Written file {File}", resolvedTargetFile);

        return $"Created file {resolvedTargetFile} with requested content.";
    }

    private record Arguments(
        string TargetFile,
        bool Overwrite,
        List<string>? CodeLines);
}
