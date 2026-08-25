using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
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

    /// <inheritdoc/>
    public override bool IsReadOnly => true;
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
                "type": "object",
                "properties": {
                    "CaseInsensitive": {
                        "type": "boolean",
                        "description": "If true, performs a case-insensitive search."
                    },
                    "IsRegex": {
                        "type": "boolean",
                        "description": "If true, treats Query as a regular expression."
                    },
                    "Query": {
                        "type": "string",
                        "description": "The search term or pattern to look for."
                    },
                    "SearchPath": {
                        "type": "string",
                        "description": "The path to search. Can be a directory or a file. Can be absolute or relative to project root. The '/workspace/' prefix is handled automatically."
                    },
                    "Includes": {
                        "type": "array",
                        "items": { "type": "string" },
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
        var (success, args, parseError) = TryParseArguments<Arguments>(
            argumentsJson,
            expectedSchemaHint: """{"SearchPath": "string", "Query": "string"}""",
            logger: logger);

        if (!success) return parseError!;

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

        // Resolve path — must use resolved path for all subsequent operations
        var resolvedSearchPath = ResolvePath(args.SearchPath);
        ValidatePath(resolvedSearchPath);

        await NotifyProgressAsync($"🔎 Grep searching for '{args.Query}' in '{resolvedSearchPath}'", context, cancellationToken);

        // Identify files to search
        var files = new List<string>();
        if (File.Exists(resolvedSearchPath))
        {
            files.Add(resolvedSearchPath);
        }
        else if (Directory.Exists(resolvedSearchPath))
        {
            try
            {
                if (args.Includes != null && args.Includes.Count > 0)
                {
                    // Apply glob include patterns using FileSystemGlobbing
                    var matcher = new Matcher();
                    foreach (var include in args.Includes)
                        matcher.AddInclude(include);

                    var matchResult = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(resolvedSearchPath)));
                    files.AddRange(matchResult.Files.Select(f => Path.GetFullPath(Path.Combine(resolvedSearchPath, f.Path))));
                }
                else
                {
                    files.AddRange(Directory.EnumerateFiles(resolvedSearchPath, "*", SearchOption.AllDirectories));
                }
            }
            catch { }
        }
        else
        {
            return $"Error: Path {resolvedSearchPath} not found.";
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                        // Use resolvedSearchPath so relative→absolute comparison works correctly
                        var relativePath = File.Exists(resolvedSearchPath)
                            ? Path.GetFileName(file)
                            : Path.GetRelativePath(resolvedSearchPath, file);

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
