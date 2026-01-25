using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Tool to view specific code definitions (classes, methods, properties) from a file
/// without loading the entire file. More efficient than view_file for targeted code exploration.
/// </summary>
public sealed class ViewCodeItemTool : BaseFileTool
{
    public override string Name => "view_code_item";

    public override string Description => "View specific code definitions (classes, methods, properties) from a C# file without loading the entire file.";
    public override string? UsageGuidelines => "Use after view_file_outline to see specific method/class implementations without loading full file.";

    public override ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        ParametersJsonSchema = """
        {
            "type": "OBJECT",
            "properties": {
                "File": {
                    "type": "STRING",
                    "description": "Absolute path to the C# source file"
                },
                "NodePaths": {
                    "type": "ARRAY",
                    "items": { "type": "STRING" },
                    "description": "List of code item paths to view, e.g., ['ClassName', 'ClassName.MethodName', 'ClassName.PropertyName']. Use view_file_outline first to discover available paths."
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

        await NotifyProgressAsync($"🧩 Reading code item '{string.Join(", ", args.NodePaths)}' in file '{resolvedFile}'", context, cancellationToken);

        if (!File.Exists(resolvedFile))
        {
            return $"""
                TOOL_ERROR: File '{resolvedFile}' does not exist.
                Guidance: This path may be incorrect. Try using find_by_name or list_dir to find the correct file path.
                """;
        }

        // Read and parse the file
        var sourceCode = File.ReadAllText(resolvedFile);
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = syntaxTree.GetRoot();

        var results = new List<string>();
        var notFound = new List<string>();

        foreach (var nodePath in args.NodePaths)
        {
            var node = FindNode(root, nodePath);
            if (node != null)
            {
                var lineSpan = node.GetLocation().GetLineSpan();
                var startLine = lineSpan.StartLinePosition.Line + 1;
                var endLine = lineSpan.EndLinePosition.Line + 1;

                results.Add($"""
                    === {nodePath} ===
                    Location: {args.File}:{startLine}-{endLine}
                    
                    {node.ToFullString().Trim()}
                    """);
            }
            else
            {
                notFound.Add(nodePath);
            }
        }

        if (results.Count == 0)
        {
            var availableNodes = DiscoverAvailableNodes(root);
            return $"""
                TOOL_ERROR: None of the requested node paths were found.
                Not found: {string.Join(", ", notFound)}
                
                Available nodes in this file:
                {string.Join("\n", availableNodes)}
                
                Guidance: Use view_file_outline to see the complete structure, or try one of the available nodes listed above.
                """;
        }

        var output = string.Join("\n\n", results);

        if (notFound.Count > 0)
        {
            output += $"\n\n⚠️ Not found: {string.Join(", ", notFound)}";
        }

        return output;
    }

    private static SyntaxNode? FindNode(SyntaxNode root, string nodePath)
    {
        var parts = nodePath.Split('.');
        SyntaxNode? current = root;

        foreach (var part in parts)
        {
            if (current == null) return null;

            // Try to find a type declaration (class, struct, interface, record)
            var typeDecl = current.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => t.Identifier.Text == part);

            if (typeDecl != null)
            {
                current = typeDecl;
                continue;
            }

            // Try to find a method
            var method = current.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == part);

            if (method != null)
            {
                return method;
            }

            // Try to find a property
            var property = current.DescendantNodes()
                .OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault(p => p.Identifier.Text == part);

            if (property != null)
            {
                return property;
            }

            // Try to find a field
            var field = current.DescendantNodes()
                .OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == part));

            if (field != null)
            {
                return field;
            }

            // Not found
            return null;
        }

        return current;
    }

    private static List<string> DiscoverAvailableNodes(SyntaxNode root)
    {
        var nodes = new List<string>();

        // Find all type declarations
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            nodes.Add(typeDecl.Identifier.Text);

            // Find members within this type
            foreach (var member in typeDecl.Members)
            {
                var memberName = member switch
                {
                    MethodDeclarationSyntax method => method.Identifier.Text,
                    PropertyDeclarationSyntax property => property.Identifier.Text,
                    FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
                    _ => null
                };

                if (memberName != null)
                {
                    nodes.Add($"{typeDecl.Identifier.Text}.{memberName}");
                }
            }
        }

        return nodes.Take(20).ToList(); // Limit to first 20 to avoid overwhelming output
    }

    private record Arguments(
        string File,
        List<string> NodePaths);
}
