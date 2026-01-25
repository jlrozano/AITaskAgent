using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Tool to search for text patterns within files (grep-like).
/// Replaces external 'ripgrep' with .NET Regex.
/// </summary>
public sealed class GrepSearchTool : BaseFileTool
{
    public override string Name => "grep_search";
    public override string Description => "Use this tool to find exact pattern matches within files or directories. Results returned in JSON format including Filename, LineNumber, and LineContent. Total results capped at 50.";
    public override string? UsageGuidelines => "Use to find text patterns or code references across files. Supports regex.";

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
                    "CaseInsensitive": {
                        "type": "BOOLEAN",
                        "description": "If true, performs a case-insensitive search."
                    },
                    "IsRegex": {
                        "type": "BOOLEAN",
                        "description": "If true, treats Query as a regular expression."
                    },
                    "Query": {
                        "type": "STRING",
                        "description": "The search term or pattern to look for."
                    },
                    "SearchPath": {
                        "type": "STRING",
                        "description": "The path to search. This can be a directory or a file."
                    },
                    "Includes": {
                        "type": "ARRAY",
                        "items": { "type": "STRING" },
                        "description": "Glob patterns to filter files."
                    }
                },
                "required": [
                    "SearchPath",
                    "Query"
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
        if (args is null || string.IsNullOrWhiteSpace(args.SearchPath) || string.IsNullOrWhiteSpace(args.Query))
        {
            return "Error: SearchPath and Query are required.";
        }

        var results = new List<object>();
        int matchCount = 0;
        const int MaxMatches = 50;

        Regex regex;
        try
        {
            var options = RegexOptions.Compiled;
            if (args.CaseInsensitive) options |= RegexOptions.IgnoreCase;

            string pattern = args.IsRegex ? args.Query : Regex.Escape(args.Query);
            regex = new Regex(pattern, options);
        }
        catch (Exception ex)
        {
            return $"Error: Invalid regex. {ex.Message}";
        }

        // Resolve path in case it's relative
        var resolvedSearchPath = ResolvePath(args.SearchPath);

        await NotifyProgressAsync($"🔎 Grep searching for '{args.Query}' in '{resolvedSearchPath}'", context, cancellationToken);

        // Identify files to search
        var files = new List<string>();
        if (File.Exists(resolvedSearchPath))
        {
            files.Add(resolvedSearchPath);
        }
        else if (Directory.Exists(resolvedSearchPath))
        {
            // Simple recursive search, filters logic omitted for brevity/speed in this MVP 
            // Ideally would use Globbing from FindByNameTool logic here too.
            try
            {
                files.AddRange(Directory.EnumerateFiles(resolvedSearchPath, "*", SearchOption.AllDirectories));
            }
            catch { }
        }
        else
        {
            return $"Error: Path {resolvedSearchPath} not found.";
        }

        foreach (var file in files)
        {
            if (matchCount >= MaxMatches) break;

            // Skip binaries/huge files simplistic check
            if (new FileInfo(file).Length > 1024 * 1024) continue;

            try
            {
                var lines = await File.ReadAllLinesAsync(file, cancellationToken);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (matchCount >= MaxMatches) break;

                    if (regex.IsMatch(lines[i]))
                    {
                        var relativePath = Path.GetRelativePath(args.SearchPath, file); // if dir
                        if (File.Exists(args.SearchPath)) relativePath = Path.GetFileName(file);

                        results.Add(new
                        {
                            Filename = relativePath,
                            LineNumber = i + 1,
                            LineContent = lines[i].Trim()
                        });
                        matchCount++;
                    }
                }
            }
            catch { }
        }

        if (results.Count == 0) return "No results found";
        return JsonConvert.SerializeObject(results, Formatting.Indented);
    }

    private record Arguments(
        string SearchPath,
        string Query,
        bool CaseInsensitive,
        bool IsRegex,
        List<string>? Includes);
}
