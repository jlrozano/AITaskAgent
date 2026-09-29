using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AITaskAgent.FileTools.CodeItems;

/// <summary>
/// The built-in locator of <c>view_code_item</c>: parses the file as C# with Roslyn and takes, for each part of the node
/// path, the first type, method, property or field with that name. It answers for every file, as the tool always did,
/// so it goes last; overloads are not told apart (the first one wins).
/// </summary>
public sealed class CSharpCodeItemLocator : ICodeItemLocator
{
    public static CSharpCodeItemLocator Instance { get; } = new();

    public ValueTask<IReadOnlyList<CodeItemLookup>?> LocateAsync(CodeItemRequest request, CancellationToken cancellationToken)
    {
        var root = CSharpSyntaxTree.ParseText(request.SourceText, cancellationToken: cancellationToken).GetRoot(cancellationToken);
        List<string>? available = null;

        var lookups = new List<CodeItemLookup>(request.NodePaths.Count);
        foreach (var nodePath in request.NodePaths)
        {
            var node = FindNode(root, nodePath);
            if (node == null)
            {
                lookups.Add(CodeItemLookup.NotFound(available ??= DiscoverAvailableNodes(root)));
                continue;
            }

            var lineSpan = node.GetLocation().GetLineSpan();
            lookups.Add(CodeItemLookup.Found(new CodeItemMatch
            {
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Code = node.ToFullString().Trim()
            }));
        }

        return ValueTask.FromResult<IReadOnlyList<CodeItemLookup>?>(lookups);
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
}
