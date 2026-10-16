using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher;

/// <summary>
/// El nodo del grafo al que pertenece un trozo de código: el método o constructor que lo contiene, el campo si es su
/// declaración, o el tipo. Los enriquecedores que escriben un hallazgo por posición (diagnósticos, comentarios) lo usan
/// para saber dónde colgarlo.
/// </summary>
internal static class OwnerNodes
{
    public static async Task<long?> ForAsync(SyntaxNode? start, SemanticModel model, EnrichmentContext ctx)
    {
        for (var node = start; node is not null; node = node.Parent)
        {
            string? qn = null;
            switch (node)
            {
                case BaseMethodDeclarationSyntax when model.GetDeclaredSymbol(node) is IMethodSymbol method && GraphMethods.HasNode(method):
                    qn = SymbolQn.ForMethod(method, ctx.Project, ctx.RepoRoot);
                    break;
                case VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } when model.GetDeclaredSymbol(node) is IFieldSymbol field:
                    qn = SymbolQn.ForType(field.ContainingType, ctx.Project, ctx.RepoRoot) is { } owner ? $"{owner}.{field.Name}" : null;
                    break;
                case BaseTypeDeclarationSyntax when model.GetDeclaredSymbol(node) is INamedTypeSymbol type:
                    qn = SymbolQn.ForType(type, ctx.Project, ctx.RepoRoot);
                    break;
            }
            if (qn is not null && await ctx.Repository.GetNodeIdAsync(ctx.Project, qn) is { } id)
                return id;
        }
        return null;
    }
}
