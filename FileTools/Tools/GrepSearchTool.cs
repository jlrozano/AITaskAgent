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
/// <remarks>
/// Each file is decoded with <see cref="TextFileEncoding"/> before matching, so the pattern runs on the same text
/// view_file shows: an accented query also matches in an ISO-8859-1 file.
/// On a large repository a directory search walks every file under the path: files are visited in ordinal path order,
/// so a capped result is the same on every run; version-control, build-output and dependency folders
/// (<see cref="ExcludedDirectoryNames"/>) and the host's <see cref="FileToolsConfiguration.SearchExcludedDirectories"/>
/// are skipped; files over 1 MB and binary files are skipped; and each line is cut to <see cref="MaxLineChars"/>
/// characters. When the cap is reached the last element of the array says so and carries no <c>Filename</c>.
/// </remarks>
public sealed class GrepSearchTool : BaseFileTool
{
    public override string Name => "grep_search";

    /// <inheritdoc/>
    public override bool IsReadOnly => true;
    public override string Description => "Use this tool to find exact pattern matches within files or directories. Results returned in JSON format including Filename, LineNumber, and LineContent. Total results capped at 50; a last element with Truncated=true says the cap was reached.";
    public override string? UsageGuidelines => "Use to find text patterns or code references across files. Supports regex. Narrow large searches with SearchPath or Includes globs (e.g. '**/*.java', '**/*.xml').";

    /// <summary>Upper bound on a single regex match, to defend against catastrophic backtracking.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Maximum matches returned by one search.</summary>
    public const int MaxMatches = 50;

    /// <summary>Characters kept of each matching line: a minified file has lines of hundreds of kilobytes.</summary>
    public const int MaxLineChars = 300;

    /// <summary>Files larger than this are not searched.</summary>
    private const long MaxFileBytes = 1024 * 1024;

    /// <summary>
    /// Folders never searched inside a directory search, wherever they sit: version control, .NET build output and
    /// npm dependencies. The same ones <see cref="FindByNameTool"/> leaves out, plus <c>node_modules</c>. A file path
    /// given as <c>SearchPath</c> is always searched.
    /// </summary>
    public static IReadOnlyCollection<string> ExcludedDirectoryNames { get; } = [".git", "bin", "obj", "node_modules"];

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

        Regex regex;
        try
        {
            var options = RegexOptions.Compiled;
            if (args.CaseInsensitive) options |= RegexOptions.IgnoreCase;

            string pattern = args.IsRegex ? args.Query : Regex.Escape(args.Query);

            // The pattern can come straight from the LLM, so it is untrusted input: a match
            // timeout bounds the cost of a catastrophic-backtracking pattern (ReDoS) instead of
            // hanging the request indefinitely and uncancellably.
            regex = new Regex(pattern, options, MatchTimeout);
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
                    files.AddRange(matchResult.Files
                        .Select(f => Path.GetFullPath(Path.Combine(resolvedSearchPath, f.Path)))
                        .Where(f => !IsInExcludedDirectory(resolvedSearchPath, f))
                        .Where(FileToolsConfiguration.IsAllowed));
                }
                else
                {
                    // EnumerateFiles(..., AllDirectories) walks through symlinks/junctions with no
                    // control of its own, so a link anywhere under the search path could lead
                    // outside the sandbox. FileToolsConfiguration.IsAllowed resolves reparse
                    // points before checking containment - reuse it here instead of trusting the
                    // enumerated path's text.
                    files.AddRange(Directory
                        .EnumerateFiles(resolvedSearchPath, "*", SearchOption.AllDirectories)
                        .Where(f => !IsInExcludedDirectory(resolvedSearchPath, f))
                        .Where(FileToolsConfiguration.IsAllowed));
                }
            }
            catch { }

            // Same order on every run, so a search that hits the cap returns the same matches.
            files.Sort(StringComparer.Ordinal);
        }
        else
        {
            return $"Error: Path {resolvedSearchPath} not found.";
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matchCount >= MaxMatches) break;

            // Skip huge files; binaries are recognised below by a NUL character.
            if (new FileInfo(file).Length > MaxFileBytes) continue;

            try
            {
                // Same decoding rule as view_file, so an accented query also matches in a Latin-1 file.
                var lines = await TextFileEncoding.ReadAllLinesAsync(file, cancellationToken);
                if (lines.Any(l => l.Contains('\0'))) continue;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (matchCount >= MaxMatches) break;

                    if (regex.IsMatch(lines[i]))
                    {
                        // Use resolvedSearchPath so relative→absolute comparison works correctly
                        var relativePath = File.Exists(resolvedSearchPath)
                            ? Path.GetFileName(file)
                            : Path.GetRelativePath(resolvedSearchPath, file);

                        var content = lines[i].Trim();
                        results.Add(new
                        {
                            Filename = relativePath,
                            LineNumber = i + 1,
                            LineContent = content.Length > MaxLineChars ? content[..MaxLineChars] + "…" : content
                        });
                        matchCount++;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Surface as a normal tool error instead of an unhandled exception: the pattern is
                // untrusted LLM input, and a timeout on one file means it will time out on every
                // other one too, so abort the search rather than silently skip results.
                return $"Error: The regex pattern timed out while matching '{args.Query}' against " +
                       $"'{file}'. Simplify the pattern (it may be triggering catastrophic backtracking).";
            }
            catch { }
        }

        if (results.Count == 0) return "No results found";
        if (matchCount >= MaxMatches)
        {
            // An element without Filename: readers that take the matches (ClaimAuditor) skip it.
            results.Add(new
            {
                Truncated = true,
                Note = $"Results capped at {MaxMatches}; there may be more matches. Narrow SearchPath or Includes to see the rest."
            });
        }
        return JsonConvert.SerializeObject(results, Formatting.Indented);
    }

    /// <summary>
    /// True when <paramref name="file"/> sits, below <paramref name="searchRoot"/>, in one of the
    /// <see cref="ExcludedDirectoryNames"/> or inside one of <see cref="FileToolsConfiguration.SearchExcludedDirectories"/>.
    /// Only the part below the search root counts: searching inside a folder called <c>bin</c> on purpose still works.
    /// </summary>
    private static bool IsInExcludedDirectory(string searchRoot, string file)
    {
        var relative = Path.GetRelativePath(searchRoot, file);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (ExcludedDirectoryNames.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
                return true;
        }

        foreach (var excluded in FileToolsConfiguration.SearchExcludedDirectories)
        {
            var prefix = Path.TrimEndingDirectorySeparator(excluded) + Path.DirectorySeparatorChar;
            if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private record Arguments(
        string SearchPath,
        string Query,
        bool CaseInsensitive,
        bool IsRegex,
        List<string>? Includes);
}
