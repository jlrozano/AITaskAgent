using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Tool to generate an outline of a file.
/// Uses Roslyn for C# files and Regex for others.
/// </summary>
public sealed class ViewFileOutlineTool : BaseFileTool
{
    public override string Name => "view_file_outline";
    public override string Description => "View the outline of the input file. Contains breakdown of functions and classes. Preferable first step for exploring files.";
    public override string? UsageGuidelines => "Start here when exploring code files. Shows classes, methods, properties without full content.";

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
        var (success, args, parseError) = TryParseArguments<Arguments>(
            argumentsJson,
            expectedSchemaHint: """{"AbsolutePath": "string (absolute path to file)"}""",
            logger: logger);

        if (!success)
        {
            return parseError!;
        }

        logger.LogDebug("[ViewFileOutlineTool] Parsed args - AbsolutePath: '{Path}'", args?.AbsolutePath ?? "(null)");

        if (args is null || string.IsNullOrWhiteSpace(args.AbsolutePath))
        {
            return """
                TOOL_ERROR: AbsolutePath is required.
                Guidance: Provide an absolute path to the file you want to outline.
                """;
        }

        // Resolve path in case it's relative
        var resolvedPath = ResolvePath(args.AbsolutePath);
        logger.LogDebug("[ViewFileOutlineTool] Resolved path: '{Path}'", resolvedPath);

        await NotifyProgressAsync($"📑 Generating outline for file '{resolvedPath}'", context, cancellationToken);

        ValidatePath(resolvedPath);

        if (!File.Exists(resolvedPath))
        {
            return $"""
                TOOL_ERROR: File '{resolvedPath}' not found.
                Guidance: Verify the file path exists. Use find_by_name or list_dir to discover files first.
                """;
        }

        string source = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        var ext = Path.GetExtension(resolvedPath).ToLowerInvariant();

        if (ext == ".cs")
        {
            return GenerateRoslynOutline(source, resolvedPath);
        }
        else
        {
            return GenerateRegexOutline(source, ext);
        }
    }
    private string GenerateRoslynOutline(string source, string filePath)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var root = syntaxTree.GetRoot();
        var sb = new StringBuilder();

        sb.AppendLine($"File Outline: {Path.GetFileName(filePath)}");
        sb.AppendLine($"Total Lines: {source.Split('\n').Length}");
        sb.AppendLine();

        // Walk the tree
        var members = root.DescendantNodes().OfType<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            if (member is BaseTypeDeclarationSyntax typeDecl) // Class, Interface, Struct, Record
            {
                var startLine = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var endLine = typeDecl.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
                sb.AppendLine($"[TYPE] {typeDecl.Identifier.Text} (Lines {startLine}-{endLine})");
            }
            else if (member is MethodDeclarationSyntax method)
            {
                var startLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var endLine = method.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
                sb.AppendLine($"  [METHOD] {method.Identifier.Text}{method.ParameterList} (Lines {startLine}-{endLine})");
            }
            else if (member is ConstructorDeclarationSyntax ctor)
            {
                var startLine = ctor.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sb.AppendLine($"  [CTOR] {ctor.Identifier.Text} (Line {startLine})");
            }
            else if (member is PropertyDeclarationSyntax prop)
            {
                var startLine = prop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sb.AppendLine($"  [PROP] {prop.Identifier.Text} (Line {startLine})");
            }
        }

        return sb.ToString();
    }

    private string GenerateRegexOutline(string source, string extension)
    {
        // Simple fallback
        var sb = new StringBuilder();
        sb.AppendLine($"File Outline (Regex Fallback): {extension}");

        // Very basic Python/JS patterns
        string? pattern = extension switch
        {
            ".py" => @"^\s*(def|class)\s+(\w+)",
            ".js" or ".ts" or ".jsx" or ".tsx" => @"(function\s+\w+|class\s+\w+|const\s+\w+\s*=\s*(\(|async))",
            _ => null
        };

        if (pattern == null) return "Outline not supported for this file type.";

        var regex = new Regex(pattern, RegexOptions.Multiline);
        var matches = regex.Matches(source);

        foreach (Match match in matches)
        {
            sb.AppendLine($"[MATCH] {match.Value.Trim()}");
        }

        if (matches.Count == 0) sb.AppendLine("No items found.");

        return sb.ToString();
    }

    private record Arguments(string AbsolutePath);
}
