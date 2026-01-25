using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Tool to view the contents of a file with line range support.
/// </summary>
public sealed class ViewFileTool : BaseFileTool
{
    public override string Name => "view_file";
    public override string Description => "View the contents of a file from the local filesystem. This tool supports some binary files such as images and videos.\nText file usage:\n- The lines of the file are 1-indexed\n- The first time you read a new file the tool will enforce reading 800 lines to understand as much about the file as possible\n- The output of this tool call will be the file contents from StartLine to EndLine (inclusive)\n- You can view at most 800 lines at a time\n- To view the whole file do not pass StartLine or EndLine arguments\nBinary file usage:\n- Do not provide StartLine or EndLine arguments, this tool always returns the entire file";
    public override string? UsageGuidelines => "Use to read file contents. For file structure overview, use view_file_outline first.";

    private const int MaxLines = 800;

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
                    "AbsolutePath": {
                        "type": "STRING",
                        "description": "Path to file to view. Must be an absolute path."
                    },
                    "EndLine": {
                        "type": "INTEGER",
                        "description": "Optional. Endline to view, 1-indexed as usual, inclusive. This value must be greater than or equal to StartLine."
                    },
                    "StartLine": {
                        "type": "INTEGER",
                        "description": "Optional. Startline to view, 1-indexed as usual, inclusive. This value must be less than or equal to EndLine."
                    }
                },
                "required": [
                    "AbsolutePath"
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
        var args = ParseArguments<Arguments>(argumentsJson);
        if (args is null || string.IsNullOrWhiteSpace(args.AbsolutePath))
        {
            return "Error: AbsolutePath is required.";
        }

        // Resolve path in case it's relative (matches ViewFileOutlineTool behavior)
        var resolvedPath = ResolvePath(args.AbsolutePath);
        logger.LogDebug("[ViewFileTool] Resolved path: '{Path}'", resolvedPath);

        await NotifyProgressAsync($"📄 Reading file '{resolvedPath}'", context, cancellationToken);

        ValidatePath(resolvedPath);

        if (!File.Exists(resolvedPath))
        {
            return $"Error: File not found at {resolvedPath}";
        }

        // Check for binary files (simplistic check)
        if (IsBinaryFile(resolvedPath))
        {
            return "Binary file content not displayed.";
        }

        var lines = await File.ReadAllLinesAsync(resolvedPath, cancellationToken);
        int totalLines = lines.Length;

        // Default or specific range
        int start = Math.Max(1, args.StartLine ?? 1);
        int end = args.EndLine ?? totalLines;

        if (start > end) return "Error: StartLine cannot be greater than EndLine.";

        // Enforce max lines
        int count = end - start + 1;
        if (count > MaxLines)
        {
            // Truncate to MaxLines
            end = start + MaxLines - 1;
            count = MaxLines;
        }

        // 0-based index for array
        int arrayStart = start - 1;
        int arrayEnd = Math.Min(end - 1, totalLines - 1);

        if (arrayStart >= totalLines) return "End of file reached.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"File Path: `file:///{resolvedPath.Replace('\\', '/')}`");
        sb.AppendLine($"Total Lines: {totalLines}");
        sb.AppendLine($"Showing lines {start} to {arrayEnd + 1}");

        for (int i = arrayStart; i <= arrayEnd; i++)
        {
            sb.AppendLine($"{i + 1}: {lines[i]}");
        }

        return sb.ToString();
    }

    private static bool IsBinaryFile(string path)
    {
        // Simple extension check for demo
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".mp4" or ".avi" or ".exe" or ".dll";
    }

    private record Arguments(string AbsolutePath, int? StartLine, int? EndLine);
}
