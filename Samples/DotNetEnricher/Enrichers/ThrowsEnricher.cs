using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Excepciones por método, con el modelo semántico. En cada nodo de método (o constructor) analizado escribe:
///
///   - <c>throws</c>: tipos (nombre completo, sin repetir) que lanza su cuerpo: <c>throw new X(..)</c>, <c>throw e</c> (el
///     tipo estático de la expresión), las expresiones <c>?? throw ..</c> y <c>throw;</c> (el tipo del <c>catch</c> que lo
///     contiene, o <c>System.Exception</c> si no declara ninguno). No cuentan los de lambdas y métodos anónimos, que no se
///     ejecutan al llamar al método; sí los de funciones locales. Si no lanza nada no se escribe (nunca <c>[]</c>).
///   - <c>rethrows</c>: algún <c>catch</c> relanza (<c>throw;</c> o <c>throw e</c> con su variable); y
///     <c>rethrow_loses_stack</c> si lo hace con <c>throw e</c>, que reinicia la traza.
///   - <c>swallows_exceptions</c>: algún <c>catch</c> se traga la excepción, y <c>swallowed_catches</c>
///     (<c>[{line, type, kind}]</c>) dice cuáles: <c>empty</c> y <c>inert</c> son la regla <c>empty-catch</c> de
///     <see cref="IssueEnricher"/> (catch vacío, o genérico que solo devuelve o continúa; el hallazgo sigue en
///     <c>detected_issues</c> y aquí no se repite); <c>log-only</c> es un catch genérico que solo llama a algo (registra) o
///     devuelve, y <c>generic</c>, uno genérico que hace otra cosa, todos sin relanzar. Uno de una cancelación no cuenta.
///
/// Y aristas <c>THROWS</c> exactas (<c>exact</c>, <c>origin</c> = <c>compiler</c>, <c>lines</c>, <c>kinds</c>: <c>new</c>,
/// <c>expression</c>, <c>rethrow</c>) del método al tipo de excepción cuando el tipo tiene nodo (las del código fuente); las de
/// cbm que salen de los ficheros analizados se sustituyen (<c>cbm_edges_replaced</c>).
/// </summary>
public sealed class ThrowsEnricher : IGraphEnricher
{
    public string Name  => "ThrowsEnricher";
    public int    Order => 7;

    private const string RootException = "System.Exception";

    private sealed class MethodFacts
    {
        public SortedSet<string> Throws { get; } = new(StringComparer.Ordinal);
        public bool Rethrows { get; set; }
        public bool RethrowLosesStack { get; set; }
        public List<(int Line, string Type, string Kind)> Swallowed { get; } = [];
        public Dictionary<INamedTypeSymbol, (SortedSet<int> Lines, SortedSet<string> Kinds)> ThrownTypes { get; } =
            new(SymbolEqualityComparer.Default);
    }

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph = ctx.Repository;
        await graph.ExecuteAsync($"""
            UPDATE nodes SET properties = json_remove(properties, '$.throws', '$.rethrows', '$.rethrow_loses_stack',
                                                      '$.swallows_exceptions', '$.swallowed_catches')
            WHERE project = @project AND label IN ('Method','Function')
              AND (json_type(properties, '$.throws') IS NOT NULL OR json_type(properties, '$.rethrows') IS NOT NULL)
              AND {LanguageScope.NotJava()}
            """, new { project = ctx.Project });

        var symbols = await ctx.GetGraphSymbolsAsync();
        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        var facts = new Dictionary<long, MethodFacts>();

        foreach (var compilation in ctx.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees.Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                var model = compilation.GetSemanticModel(tree);
                var text = await tree.GetTextAsync();
                foreach (var declaration in (await tree.GetRootAsync()).DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    // Sin cuerpo (abstracto, de interfaz, extern, parcial sin implementar) no hay nada que decir: se deja sin
                    // las propiedades en lugar de afirmar que no lanza.
                    if (declaration is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax)
                        || (declaration.Body is null && declaration.ExpressionBody is null)
                        || model.GetDeclaredSymbol(declaration) is not IMethodSymbol method || !GraphMethods.HasNode(method))
                        continue;
                    if (symbols.NodeOfMethod(method) is not { } nodeId)
                    {
                        if (SymbolQn.ForMethod(method, ctx.Project, ctx.RepoRoot) is { } qn) ctx.RecordPatchMiss(Name, qn);
                        continue;
                    }
                    if (!facts.TryGetValue(nodeId, out var methodFacts)) facts[nodeId] = methodFacts = new MethodFacts();
                    Analyze(declaration, model, text, methodFacts);
                }
            }
        }

        var replaced = await graph.DeleteCbmEdgesFromFilesAsync(ctx.Project, "THROWS", ctx.AnalyzedFiles);
        var enrichedAt = DateTime.UtcNow.ToString("O");
        int edges = 0;
        foreach (var (nodeId, f) in facts)
        {
            // Sin excepciones no se escribe throws: un [] no es NULL para json_extract y las consultas que buscan
            // «métodos que lanzan» por presencia lo contarían. rethrows y swallows_exceptions dicen que se analizó.
            var patch = new Dictionary<string, object?>
            {
                ["rethrows"]            = f.Rethrows,
                ["swallows_exceptions"] = f.Swallowed.Count > 0
            };
            if (f.Throws.Count > 0) patch["throws"] = f.Throws.ToArray();
            if (f.RethrowLosesStack) patch["rethrow_loses_stack"] = true;
            if (f.Swallowed.Count > 0)
                patch["swallowed_catches"] = f.Swallowed.OrderBy(s => s.Line)
                    .Select(s => new Dictionary<string, object?> { ["line"] = s.Line, ["type"] = s.Type, ["kind"] = s.Kind })
                    .ToList();
            await graph.PatchNodePropertiesByIdAsync(nodeId, patch);
            ctx.RecordPatchHit(Name);

            foreach (var (type, (lines, kinds)) in f.ThrownTypes)
            {
                if (symbols.NodeOfTarget(type) is not { } typeId || typeId == nodeId) continue;
                await graph.UpsertEdgeAsync(ctx.Project, nodeId, typeId, "THROWS", new Dictionary<string, object?>
                {
                    ["exact"]       = true,
                    ["origin"]      = "compiler",
                    ["lines"]       = lines.ToArray(),
                    ["kinds"]       = kinds.ToArray(),
                    ["enriched_by"] = new[] { Name },
                    ["enriched_at"] = enrichedAt
                });
                edges++;
            }
        }

        ctx.CbmEdgesReplaced["THROWS"] = ctx.CbmEdgesReplaced.GetValueOrDefault("THROWS") + replaced.Count;
        ctx.EnricherDetails["throws"] = new
        {
            methods = facts.Count,
            throwing = facts.Values.Count(f => f.Throws.Count > 0),
            swallowing = facts.Values.Count(f => f.Swallowed.Count > 0),
            rethrowing = facts.Values.Count(f => f.Rethrows),
            exact_edges = edges,
            cbm_replaced = replaced.Count
        };
        progress?.Report($"Throws: {facts.Count} methods, {edges} exact THROWS edges, {replaced.Count} cbm THROWS replaced.");
    }

    private static void Analyze(SyntaxNode declaration, SemanticModel model, Microsoft.CodeAnalysis.Text.SourceText text, MethodFacts facts)
    {
        int LineOf(SyntaxNode n) => text.Lines.GetLinePosition(n.SpanStart).Line + 1;

        foreach (var node in declaration.DescendantNodes())
        {
            if (InsideLambda(node, declaration)) continue;
            switch (node)
            {
                case ThrowStatementSyntax { Expression: null } rethrow:
                {
                    var @catch = rethrow.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
                    var type = @catch?.Declaration is { } decl ? model.GetTypeInfo(decl.Type).Type as INamedTypeSymbol : null;
                    Record(facts, type, type?.ToDisplayString() ?? RootException, LineOf(rethrow), "rethrow");
                    facts.Rethrows = true;
                    break;
                }
                case ThrowStatementSyntax { Expression: { } expression } statement:
                    Thrown(expression, LineOf(statement));
                    break;
                case ThrowExpressionSyntax throwExpression:
                    Thrown(throwExpression.Expression, LineOf(throwExpression));
                    break;
                case CatchClauseSyntax @catch when Swallowed(@catch, model, declaration) is { } kind:
                    facts.Swallowed.Add((LineOf(@catch), @catch.Declaration?.Type.ToString() ?? "*", kind));
                    break;
            }
        }

        void Thrown(ExpressionSyntax expression, int line)
        {
            var type = model.GetTypeInfo(expression).Type as INamedTypeSymbol;
            var isCatchVariable = model.GetSymbolInfo(expression).Symbol is ILocalSymbol local
                && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is CatchDeclarationSyntax;
            var kind = isCatchVariable ? "rethrow" : expression is BaseObjectCreationExpressionSyntax ? "new" : "expression";
            if (isCatchVariable)
            {
                facts.Rethrows = true;
                facts.RethrowLosesStack = true;
            }
            if (type is null || type.TypeKind == TypeKind.Error) return;
            Record(facts, type, type.ToDisplayString(), line, kind);
        }
    }

    private static void Record(MethodFacts facts, INamedTypeSymbol? type, string name, int line, string kind)
    {
        facts.Throws.Add(name);
        if (type is null) return;
        if (!facts.ThrownTypes.TryGetValue(type, out var entry))
            facts.ThrownTypes[type] = entry = (new SortedSet<int>(), new SortedSet<string>(StringComparer.Ordinal));
        entry.Lines.Add(line);
        entry.Kinds.Add(kind);
    }

    /// <summary>El nodo está dentro de una lambda o un método anónimo del método (las funciones locales sí cuentan).</summary>
    private static bool InsideLambda(SyntaxNode node, SyntaxNode declaration)
    {
        for (var p = node.Parent; p is not null && p != declaration; p = p.Parent)
            if (p is AnonymousFunctionExpressionSyntax) return true;
        return false;
    }

    /// <summary>
    /// Cómo se traga la excepción un catch, o null si no se la traga: la relanza o lanza otra, la trata (catch de un tipo
    /// concreto con cuerpo) o es de una cancelación. Las dos primeras clases salen de la regla de <see cref="IssueEnricher"/>.
    /// </summary>
    private static string? Swallowed(CatchClauseSyntax clause, SemanticModel model, SyntaxNode declaration)
    {
        if (IssueEnricher.SwallowsException(clause, model) is not null)
            return clause.Block.Statements.Count == 0 ? "empty" : "inert";

        var declared = clause.Declaration is null ? null : model.GetTypeInfo(clause.Declaration.Type).Type;
        if (declared is not null && IssueEnricher.InheritsFrom(declared, "OperationCanceledException")) return null;
        var general = declared is null || declared.SpecialType == SpecialType.System_Object || declared.Name is "Exception" or "SystemException";
        if (!general) return null;
        if (clause.Block.DescendantNodes().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax && !InsideLambda(n, declaration)))
            return null;

        return clause.Block.Statements.All(s => s is EmptyStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax or ReturnStatementSyntax
                || s is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax or AwaitExpressionSyntax { Expression: InvocationExpressionSyntax } })
            ? "log-only"
            : "generic";
    }
}
