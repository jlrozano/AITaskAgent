using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Tool to search for files using glob patterns.
/// Replaces external 'fd' tool with .NET FileSystemGlobbing.
/// </summary>
public sealed class FindByNameTool : BaseFileTool
{
    public override string Name => "find_by_name";
    public override string Description => "Search for ALL files and subdirectories RECURSIVELY within a directory using glob patterns. Use this when user asks for 'all files' or 'every file'. If no Pattern is specified, defaults to '**/*' which finds ALL files in ALL subdirectories. Results are paginated (default 50). Use Skip/Take arguments to paginate through large result sets.";
    public override string? UsageGuidelines => "When user asks for 'all files' or 'list everything', use Pattern='**/*' with the root directory. Results are paginated (default 50). If you need more, use the Skip argument to fetch subsequent pages. Do NOT repeat the same query without changing Skip.";

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
                    "Excludes": {
                        "type": "ARRAY",
                        "items": { "type": "STRING" },
                        "description": "Optional, exclude files/directories that match the given glob patterns"
                    },
                    "Extensions": {
                        "type": "ARRAY",
                        "items": { "type": "STRING" },
                        "description": "Optional, file extensions to include (without leading .)"
                    },
                    "Pattern": {
                        "type": "STRING",
                        "description": "Optional glob pattern. Defaults to '**/*' (all files recursively). Examples: '**/*.cs' for C# files"
                    },
                    "Directory": {
                        "type": "STRING",
                        "description": "The directory to search."
                    },
                    "Skip": {
                        "type": "INTEGER",
                        "description": "Optional number of results to skip (pagination). Default: 0"
                    },
                    "Take": {
                        "type": "INTEGER",
                        "description": "Optional number of results to take (pagination). Default: 50. Max: 100"
                    }
                },
                "required": [
                    "Directory"
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
            expectedSchemaHint: "...");

        if (!success) return parseError!;

        if (args is null || string.IsNullOrWhiteSpace(args.Directory))
        {
            return "TOOL_CALL_ERROR: Directory is required.";
        }

        var resolvedDirectory = ResolvePath(args.Directory);

        // Paginación
        var skip = args.Skip ?? 0;
        var take = args.Take ?? 50;
        if (take > 100) take = 100; // Hard limit

        await NotifyProgressAsync($"🔍 Searching for '{args.Pattern ?? "**/*"}' in '{resolvedDirectory}' (Page: {skip}-{skip + take})", context, cancellationToken);

        if (!Directory.Exists(resolvedDirectory)) return $"TOOL_ERROR: Directory '{resolvedDirectory}' does not exist.";

        var matcher = new Matcher();

        // Includes
        if (!string.IsNullOrWhiteSpace(args.Pattern))
            matcher.AddInclude(args.Pattern);
        else if (args.Extensions != null && args.Extensions.Count > 0)
            foreach (var ext in args.Extensions) matcher.AddInclude($"**/*.{ext}");
        else
            matcher.AddInclude("**/*");

        // Excludes
        if (args.Excludes != null)
            foreach (var exc in args.Excludes) matcher.AddExclude(exc);

        matcher.AddExclude("**/.git/**");
        matcher.AddExclude("**/obj/**");
        matcher.AddExclude("**/bin/**");

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(resolvedDirectory)));

        var allFiles = result.Files.OrderBy(f => f.Path).ToList(); // Stable sort
        var totalCount = allFiles.Count;

        var pagedFiles = allFiles.Skip(skip).Take(take).ToList();

        if (pagedFiles.Count == 0)
        {
            if (totalCount > 0)
                return $"No results in this page (Skip={skip}). Total matches: {totalCount}. Try reducing Skip.";
            return "No results found matching the pattern.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {totalCount} total matches. Showing results {skip + 1} to {skip + pagedFiles.Count}:");

        foreach (var file in pagedFiles)
        {
            sb.AppendLine(file.Path);
        }

        if (skip + pagedFiles.Count < totalCount)
        {
            sb.AppendLine();
            sb.AppendLine($"WARNING: {totalCount - (skip + pagedFiles.Count)} more results available.");
            sb.AppendLine($"To view the next page, call find_by_name with Skip={skip + take} and the SAME arguments.");
        }

        return sb.ToString();
    }

    private record Arguments(
        string Directory,
        string Pattern,
        List<string>? Excludes,
        List<string>? Extensions,
        int? Skip,
        int? Take);
}
