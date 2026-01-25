using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Tool to list directory contents.
/// </summary>
public sealed class ListDirTool : BaseFileTool
{
    public override string Name => "list_dir";
    public override string Description => "List the contents of a directory, i.e. all files and subdirectories that are children of the directory. Directory path must be an absolute path to a directory that exists. For each child in the directory, output will have: relative path to the directory, whether it is a directory or file, size in bytes if file, and number of children (recursive) if directory. Number of children may be missing if the workspace is too large, since we are not able to track the entire workspace.";
    public override string? UsageGuidelines => "Use for exploring a specific directory's immediate contents. For recursive/deep searches, use find_by_name instead.";

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
                    "DirectoryPath": {
                        "type": "STRING",
                        "description": "Path to list contents of, should be absolute path to a directory"
                    }
                },
                "required": []
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
            expectedSchemaHint: """{"DirectoryPath": "string (absolute path)"}""",
            logger: logger);

        if (!success)
        {
            return parseError!;
        }

        logger.LogDebug("[ListDirTool] Parsed args - DirectoryPath: '{Path}'", args?.DirectoryPath ?? "(null)");

        // Use ResolvePath to handle relative/absolute/empty paths consistently
        var searchPath = ResolvePath(args?.DirectoryPath);

        logger.LogDebug("[ListDirTool] Resolved path: '{SearchPath}'", searchPath);

        await NotifyProgressAsync($"📂 Listing directory '{searchPath}'", context, cancellationToken);

        ValidatePath(searchPath);

        if (!Directory.Exists(searchPath))
        {
            return $"""
                TOOL_ERROR: Directory '{searchPath}' does not exist.
                Guidance: This path may be incorrect. Try using a valid directory path or check for typos.
                """;
        }

        var sb = new System.Text.StringBuilder();
        var dirInfo = new DirectoryInfo(searchPath);

        try
        {
            foreach (var fsInfo in dirInfo.EnumerateFileSystemInfos())
            {
                if (fsInfo is DirectoryInfo subDir)
                {
                    // Basic recursion check or skip count for speed
                    int childCount = 0;
                    try { childCount = subDir.EnumerateFileSystemInfos().Count(); } catch { }
                    sb.AppendLine($"{{\"name\":\"{subDir.Name}\", \"isDir\":true, \"numChildren\":{childCount}}}");
                }
                else if (fsInfo is FileInfo file)
                {
                    sb.AppendLine($"{{\"name\":\"{file.Name}\", \"sizeBytes\":\"{file.Length}\"}}");
                }
            }

            // Add summary
            sb.AppendLine();
            sb.AppendLine($"Summary: This directory contains {dirInfo.GetDirectories().Length} subdirectories and {dirInfo.GetFiles().Length} files.");
        }
        catch (Exception ex)
        {
            return $"Error listing directory: {ex.Message}";
        }

        return sb.ToString();
    }

    private record Arguments(string? DirectoryPath);
}
