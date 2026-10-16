using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DotNetEnricher;

/// <summary>Una declaración de método o constructor con cuerpo, con el nodo del grafo donde se escribe lo que se saca de ella.</summary>
internal sealed record MethodBody(
    long NodeId, IMethodSymbol Method, BaseMethodDeclarationSyntax Declaration, SemanticModel Model, SourceText Text, string RelativePath)
{
    public int LineOf(SyntaxNode node) => Text.Lines.GetLinePosition(node.SpanStart).Line + 1;
}

/// <summary>
/// Los cuerpos que analizan los enriquecedores por método (<c>SensitiveApiEnricher</c>, <c>ConcurrencyEnricher</c>): métodos
/// ordinarios y constructores con nodo (<see cref="GraphMethods"/>) y con cuerpo, de los ficheros que no excluye el filtro
/// de cbm, en un orden estable (fichero relativo y posición). Un fichero enlazado en dos proyectos sale una sola vez. Las
/// sobrecargas comparten nodo, así que puede haber varios cuerpos por nodo.
/// </summary>
internal static class MethodBodies
{
    public static async Task<List<MethodBody>> LoadAsync(EnrichmentContext ctx, string enricher)
    {
        var symbols = await ctx.GetGraphSymbolsAsync();
        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        var seen = new HashSet<(string File, int Start)>();
        var bodies = new List<MethodBody>();

        foreach (var compilation in ctx.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees.Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                var relative = SymbolQn.Relativize(tree.FilePath, ctx.RepoRoot);
                var model = compilation.GetSemanticModel(tree);
                var text = await tree.GetTextAsync();
                foreach (var declaration in (await tree.GetRootAsync()).DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    if (declaration is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax)
                        || (declaration.Body is null && declaration.ExpressionBody is null)
                        || model.GetDeclaredSymbol(declaration) is not IMethodSymbol method || !GraphMethods.HasNode(method)
                        || !seen.Add((relative, declaration.SpanStart)))
                        continue;
                    if (symbols.NodeOfMethod(method) is not { } nodeId)
                    {
                        if (SymbolQn.ForMethod(method, ctx.Project, ctx.RepoRoot) is { } qn) ctx.RecordPatchMiss(enricher, qn);
                        continue;
                    }
                    bodies.Add(new MethodBody(nodeId, method, declaration, model, text, relative));
                }
            }
        }
        return [.. bodies.OrderBy(b => b.RelativePath, StringComparer.Ordinal).ThenBy(b => b.Declaration.SpanStart)];
    }

    /// <summary>Nombre completo de un tipo sin argumentos genéricos (<c>System.Threading.Tasks.Task</c>).</summary>
    public static string FullName(ITypeSymbol type) => type.OriginalDefinition.ToDisplayString(FullNameFormat);

    private static readonly SymbolDisplayFormat FullNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None);

    /// <summary>
    /// El símbolo de una llamada o de una creación; si no resuelve del todo (un argumento de un paquete sin restaurar), el
    /// candidato cuando todos son del mismo tipo y con el mismo nombre, que es lo que miran las reglas.
    /// </summary>
    public static IMethodSymbol? MethodOf(SyntaxNode node, SemanticModel model)
    {
        var info = model.GetSymbolInfo(node);
        if (info.Symbol is IMethodSymbol method) return method;
        var candidates = info.CandidateSymbols.OfType<IMethodSymbol>().ToList();
        return candidates.Count > 0
               && candidates.All(c => c.Name == candidates[0].Name && SymbolEqualityComparer.Default.Equals(c.ContainingType?.OriginalDefinition, candidates[0].ContainingType?.OriginalDefinition))
            ? candidates[0]
            : null;
    }

    /// <summary>El tipo, sus bases y sus interfaces, para las reglas que se aplican también a las derivadas.</summary>
    public static IEnumerable<INamedTypeSymbol> SelfBasesAndInterfaces(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            yield return t;
        foreach (var i in type.AllInterfaces)
            yield return i;
    }
}
