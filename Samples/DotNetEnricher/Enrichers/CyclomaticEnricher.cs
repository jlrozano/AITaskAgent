using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Calcula la complejidad ciclomática REAL de McCabe sobre el AST de Roslyn y la parchea como
/// propiedad <c>cyclomatic</c> (int) en cada nodo Method del grafo base.
///
/// ⚠️ <c>cyclomatic</c> (esta métrica) NO es lo mismo que <c>complexity</c> (cbm):
///   - <c>cyclomatic</c> = McCabe real: 1 + nº de puntos de decisión (if/while/for/foreach/case
///     con etiqueta/catch/&amp;&amp;/||/??/?:), con base +1 → una función lineal vale 1.
///   - <c>complexity</c> (cbm) = conteo crudo de nodos AST de bifurcación, SIN base +1, sin
///     &amp;&amp;/||/ternarios, e infla switch (switch + cada case) y try (try + cada catch).
/// Son dos señales distintas; este enricher NO toca <c>complexity</c>.
///
/// Mismo universo de métodos que <see cref="SemanticMethodEnricher"/>: los que cbm nodea (<see cref="GraphMethods"/>).
/// </summary>
public class CyclomaticEnricher : IGraphEnricher
{
    public string Name  => "CyclomaticEnricher";
    public int    Order => 13;

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        int processed = 0;

        foreach (var compilation in ctx.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees
                .Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                var model = compilation.GetSemanticModel(tree);

                var typeDecls = tree.GetRoot()
                    .DescendantNodes()
                    .OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDecls)
                {
                    if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSymbol)
                        continue;

                    foreach (var method in typeSymbol.GetMembers().OfType<IMethodSymbol>())
                    {
                        if (!GraphMethods.HasNode(method)
                            || GraphMethods.IsShadowedExplicitImpl(method, ctx.Project, ctx.RepoRoot))
                            continue;

                        // Deduplicación: cada símbolo de método se procesa una sola vez, en el
                        // árbol de su declaración primaria (DeclaringSyntaxReferences[0]). Sin esto,
                        // una clase parcial declarada en N ficheros procesaría sus métodos N veces.
                        var primary = method.DeclaringSyntaxReferences.FirstOrDefault();
                        if (primary is null || primary.SyntaxTree != tree)
                            continue;

                        var body = GetBody(primary.GetSyntax());
                        if (body is null) continue;   // abstract/partial/extern: sin cuerpo

                        var qn = SymbolQn.ForMethod(method, ctx.Project, ctx.RepoRoot);
                        if (qn is null) continue;

                        var cyclomatic = 1 + CountDecisionPoints(body);

                        var patched = await ctx.Repository.PatchNodePropertiesAsync(
                            ctx.Project, qn, new { cyclomatic });
                        if (patched == 0) ctx.RecordPatchMiss(Name, qn);
                        else            { ctx.RecordPatchHit(Name); processed++; }
                    }
                }
            }
        }

        progress?.Report($"Cyclomatic: {processed} methods with McCabe complexity.");
    }

    /// <summary>Cuerpo del método/constructor: bloque <c>{ }</c> o cuerpo de expresión <c>=&gt;</c>.</summary>
    private static SyntaxNode? GetBody(SyntaxNode declaration) => declaration switch
    {
        MethodDeclarationSyntax m      => (SyntaxNode?)m.Body ?? m.ExpressionBody,
        ConstructorDeclarationSyntax c => (SyntaxNode?)c.Body ?? c.ExpressionBody,
        _                              => null
    };

    /// <summary>
    /// McCabe: cuenta puntos de decisión dentro del cuerpo. La base (+1) la añade el llamante.
    /// </summary>
    private static int CountDecisionPoints(SyntaxNode body)
    {
        int count = 0;

        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case CaseSwitchLabelSyntax:            // `case X:` (DefaultSwitchLabelSyntax queda fuera)
                case CasePatternSwitchLabelSyntax:     // `case X when ...:`
                case CatchClauseSyntax:
                case ConditionalExpressionSyntax:      // ?:
                    count++;
                    break;

                case SwitchExpressionArmSyntax arm:    // brazo de switch-expression (excluye `_ =>`)
                    if (arm.Pattern is not DiscardPatternSyntax)
                        count++;
                    break;

                case BinaryExpressionSyntax be
                    when be.IsKind(SyntaxKind.LogicalAndExpression)   // &&
                      || be.IsKind(SyntaxKind.LogicalOrExpression)    // ||
                      || be.IsKind(SyntaxKind.CoalesceExpression):    // ??
                    count++;
                    break;
            }
        }

        return count;
    }
}
