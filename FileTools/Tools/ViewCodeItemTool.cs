using AITaskAgent.Core.Models;
using AITaskAgent.FileTools.CodeItems;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Tool to view specific code definitions (classes, methods, properties) from a file
/// without loading the entire file. More efficient than view_file for targeted code exploration.
/// </summary>
/// <remarks>
/// Declarations are found by <see cref="ICodeItemLocator"/>s: the ones given to the constructor, in order, and then the
/// built-in <see cref="CSharpCodeItemLocator"/> (Roslyn). Every declaration shown comes as <c>=== path ===</c> followed by
/// <c>Location: file:start-end</c>; several declarations for one path (overloads) are listed by their own path and lines,
/// without code and without that header, so nothing unread is presented as read.
/// </remarks>
public sealed class ViewCodeItemTool : BaseFileTool
{
    private readonly IReadOnlyList<ICodeItemLocator> _locators;

    /// <summary>Only the built-in C# locator.</summary>
    public ViewCodeItemTool()
        : this([])
    {
    }

    /// <param name="locators">
    /// Locators asked before the built-in C# one, in order; the first that handles the file answers. The service
    /// collection passes the registered <see cref="ICodeItemLocator"/>s.
    /// </param>
    public ViewCodeItemTool(IEnumerable<ICodeItemLocator> locators)
    {
        _locators = [.. locators, CSharpCodeItemLocator.Instance];
    }

    public override string Name => "view_code_item";

    /// <inheritdoc/>
    public override bool IsReadOnly => true;

    public override string Description => "View specific code definitions (classes, methods, properties) from a source file without loading the entire file. C# is always supported; other languages when the host provides a locator for them.";
    public override string? UsageGuidelines => "Use after view_file_outline to see specific method/class implementations without loading full file. If a name matches several overloads, the tool lists them: repeat with the NodePath it gives, e.g. 'Class.method(int,String)'.";

    public override ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        ParametersJsonSchema = """
        {
            "type": "object",
            "properties": {
                "File": {
                    "type": "string",
                    "description": "Path to the source file. Can be absolute or relative to project root. The '/workspace/' prefix is handled automatically."
                },
                "NodePaths": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "List of code item paths to view, e.g., ['ClassName', 'ClassName.MethodName', 'Outer.Inner.MethodName', 'ClassName.MethodName(int,String)' to pick an overload]. Use view_file_outline first to discover available paths."
                }
            },
            "required": ["File", "NodePaths"]
        }
        """
    };

    protected override async Task<string> ExecuteInternalAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var (success, args, parseError) = TryParseArguments<Arguments>(
            argumentsJson,
            expectedSchemaHint: """{"File": "string (absolute path)", "NodePaths": ["string"]}""");

        if (!success)
        {
            return parseError!;
        }

        if (args is null || string.IsNullOrWhiteSpace(args.File))
        {
            return """
                TOOL_CALL_ERROR: File is required but was not provided.
                Guidance: You must retry this tool call with a valid File path. Do not apologize - just call the tool again with the required argument.
                """;
        }

        if (args.NodePaths is null || args.NodePaths.Count == 0)
        {
            return """
                TOOL_CALL_ERROR: NodePaths is required but was not provided.
                Guidance: You must retry this tool call with at least one NodePath. Use view_file_outline first to discover available paths.
                """;
        }

        // Resolve path in case it's relative
        var resolvedFile = ResolvePath(args.File);
        ValidatePath(resolvedFile);

        await NotifyProgressAsync($"🧩 Reading code item '{string.Join(", ", args.NodePaths)}' in file '{resolvedFile}'", context, cancellationToken);

        if (!File.Exists(resolvedFile))
        {
            return $"""
                TOOL_ERROR: File '{resolvedFile}' does not exist.
                Guidance: This path may be incorrect. Try using find_by_name or list_dir to find the correct file path.
                """;
        }

        var (sourceCode, _) = await TextFileEncoding.ReadAllTextAsync(resolvedFile, cancellationToken);
        var request = new CodeItemRequest(resolvedFile, sourceCode, args.NodePaths);

        IReadOnlyList<CodeItemLookup>? lookups = null;
        foreach (var locator in _locators)
        {
            lookups = await locator.LocateAsync(request, cancellationToken);
            if (lookups != null)
                break;
        }
        lookups ??= [];

        string[]? lines = null;
        var results = new List<string>();
        var ambiguous = new List<string>();
        var notFound = new List<string>();
        IReadOnlyList<string> availableNodes = [];

        for (var i = 0; i < args.NodePaths.Count; i++)
        {
            var nodePath = args.NodePaths[i];
            var lookup = i < lookups.Count ? lookups[i] : CodeItemLookup.NotFound([]);
            switch (lookup.Matches.Count)
            {
                case 0:
                    notFound.Add(nodePath);
                    if (availableNodes.Count == 0)
                        availableNodes = lookup.AvailableNodes;
                    break;

                case 1:
                    var match = lookup.Matches[0];
                    var code = match.Code ?? Slice(lines ??= SplitLines(sourceCode), match.StartLine, match.EndLine);
                    results.Add($"""
                        === {nodePath} ===
                        Location: {args.File}:{match.StartLine}-{match.EndLine}

                        {code}
                        """);
                    break;

                default:
                    ambiguous.Add($"⚠️ '{nodePath}' matches {lookup.Matches.Count} declarations. Repeat with one of these NodePaths:\n"
                        + string.Join("\n", lookup.Matches.Select(m =>
                            $"- `{m.NodePath ?? nodePath}` (lines {m.StartLine}-{m.EndLine}){(string.IsNullOrWhiteSpace(m.Detail) ? "" : ": " + m.Detail)}")));
                    break;
            }
        }

        if (results.Count == 0 && ambiguous.Count == 0)
        {
            return $"""
                TOOL_ERROR: None of the requested node paths were found.
                Not found: {string.Join(", ", notFound)}
                
                Available nodes in this file:
                {string.Join("\n", availableNodes)}
                
                Guidance: Use view_file_outline to see the complete structure, or try one of the available nodes listed above.
                """;
        }

        var output = string.Join("\n\n", results.Concat(ambiguous));

        if (notFound.Count > 0)
        {
            output += $"\n\n⚠️ Not found: {string.Join(", ", notFound)}";
        }

        return output;
    }

    private static string[] SplitLines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    /// <summary>Lines <paramref name="start"/> to <paramref name="end"/> (1-based, inclusive), clamped to the file.</summary>
    private static string Slice(string[] lines, int start, int end)
    {
        var from = Math.Clamp(start, 1, Math.Max(1, lines.Length));
        var to = Math.Clamp(end, from, lines.Length);
        return string.Join("\n", lines[(from - 1)..to]).TrimEnd();
    }

    private record Arguments(
        string File,
        List<string> NodePaths);
}
