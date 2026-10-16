using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

public class FlowEnricher : IGraphEnricher
{
    private readonly IReadOnlyList<IFlowEntryProvider> _entryProviders;
    private readonly IReadOnlyList<IStepResolver>      _stepResolvers;

    public FlowEnricher(
        IEnumerable<IFlowEntryProvider> entryProviders,
        IEnumerable<IStepResolver>      stepResolvers)
    {
        _entryProviders = entryProviders.ToList();
        _stepResolvers  = stepResolvers.ToList();
    }

    public string Name  => "FlowEnricher";
    public int    Order => 11;

    private record FlowPosition(IMethodSymbol Method, string? Condition);
    private record FlowEdge(
        IMethodSymbol From, IMethodSymbol To, string? Condition, int Order, string PathType,
        string[]? ExceptionTypes = null, bool Presentational = true);

    // Maximum recursion depth for private method expansion
    private const int MaxExpansionDepth = 5;

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var filter     = new CbmExclusionFilter(ctx.RepoRoot);
        var enrichedAt = DateTime.UtcNow.ToString("O");

        foreach (var compilation in ctx.Compilations)
            await EnrichCompilationAsync(compilation, ctx.Repository, ctx.Project, ctx.RepoRoot, filter, enrichedAt, progress);
    }

    private async Task EnrichCompilationAsync(
        Compilation compilation, GraphRepository graph, string project,
        string repoRoot, CbmExclusionFilter filter, string enrichedAt,
        IProgress<string>? progress)
    {
        int flowsCreated = 0, edgesCreated = 0;

        // Collect entry points from all registered providers
        var entryPoints = new List<(IMethodSymbol Method, string FlowKind)>();
        foreach (var provider in _entryProviders)
            entryPoints.AddRange(provider.GetEntryPoints(compilation, project, repoRoot, graph));

        // Deduplicate by QN (same method may be returned by multiple providers)
        var seen     = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<(IMethodSymbol Method, string FlowKind)>();
        foreach (var ep in entryPoints)
        {
            var qn = BuildMethodQn(ep.Method, project, repoRoot);
            if (qn is null) continue;
            if (seen.Add(qn))
                distinct.Add(ep);
        }

        foreach (var (method, kind) in distinct)
        {
            var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef is null) continue;

            var syntax = syntaxRef.GetSyntax() as MethodDeclarationSyntax;
            if (syntax is null) continue;

            if (filter.ShouldExclude(syntax.SyntaxTree.FilePath)) continue;

            var model    = compilation.GetSemanticModel(syntax.SyntaxTree);
            var cycleGuard = new HashSet<string>(StringComparer.Ordinal);
            var allEdges = BuildFlowEdges(syntax, model, method, compilation, filter, cycleGuard);
            if (allEdges.Count == 0) continue;

            var (f, e) = await PersistFlowAsync(
                method, syntax, kind, allEdges, syntax.SyntaxTree.FilePath,
                graph, project, repoRoot, enrichedAt);
            flowsCreated += f;
            edgesCreated += e;
            progress?.Report(
                $"Flow [{method.ContainingType?.Name}.{method.Name}]: " +
                $"{allEdges.Count(x => x.PathType == "happy")} happy steps, {e} edges persisted");
        }

        progress?.Report($"FlowEnricher: {flowsCreated} flows, {edgesCreated} edges total.");
    }

    // ─── Flow graph construction ───────────────────────────────────────────────

    private List<FlowEdge> BuildFlowEdges(
        MethodDeclarationSyntax method, SemanticModel model,
        IMethodSymbol orchestrator, Compilation compilation, CbmExclusionFilter filter,
        HashSet<string> cycleGuard)
    {
        var edges = new List<FlowEdge>();
        if (method.Body is null) return edges;

        var frontier = new List<FlowPosition> { new(orchestrator, null) };
        int order    = 0;

        var tryStmt = method.Body.Statements.OfType<TryStatementSyntax>().FirstOrDefault();

        if (tryStmt != null)
        {
            WalkBlock(tryStmt.Block, model, orchestrator, compilation, filter, cycleGuard, frontier, edges, ref order, "happy", 0);

            var throwSites = edges
                .Where(e => e.PathType == "happy")
                .Select(e => e.To)
                .Distinct(SymbolEqualityComparer.Default)
                .Cast<IMethodSymbol>()
                .ToList();

            foreach (var catchClause in tryStmt.Catches)
            {
                var catchType = catchClause.Declaration?.Type.ToString() ?? "Exception";
                const string pathType = "error";

                var firstAwait = catchClause.Block.DescendantNodes()
                    .OfType<AwaitExpressionSyntax>().FirstOrDefault();
                if (firstAwait is null) continue;

                var handler = ResolveDirectMethod(firstAwait, model);
                if (handler is null) continue;

                edges.Add(new FlowEdge(orchestrator, handler, catchType, 0, pathType));

                foreach (var step in throwSites)
                {
                    if (SymbolEqualityComparer.Default.Equals(step, handler)) continue;

                    var thrown = DetectThrownExceptions(step).ToArray();
                    string[]? detail = thrown.Length > 0 ? thrown : null;

                    edges.Add(new FlowEdge(step, handler, catchType, 0, pathType,
                        ExceptionTypes: detail, Presentational: false));
                }
            }
        }
        else
        {
            WalkBlock(method.Body, model, orchestrator, compilation, filter, cycleGuard, frontier, edges, ref order, "happy", 0);
        }

        return edges;
    }

    private List<FlowPosition> WalkBlock(
        BlockSyntax block, SemanticModel model, IMethodSymbol orchestrator, Compilation compilation,
        CbmExclusionFilter filter, HashSet<string> cycleGuard,
        List<FlowPosition> frontier, List<FlowEdge> edges, ref int order, string pathType, int depth)
    {
        foreach (var stmt in block.Statements)
            frontier = WalkStatement(stmt, model, orchestrator, compilation, filter, cycleGuard, frontier, edges, ref order, pathType, depth);
        return frontier;
    }

    private List<FlowPosition> WalkStatement(
        StatementSyntax stmt, SemanticModel model, IMethodSymbol orchestrator, Compilation compilation,
        CbmExclusionFilter filter, HashSet<string> cycleGuard,
        List<FlowPosition> frontier, List<FlowEdge> edges, ref int order, string pathType, int depth)
    {
        switch (stmt)
        {
            case ExpressionStatementSyntax { Expression: AwaitExpressionSyntax awaitExpr }:
                return HandleAwait(awaitExpr, model, orchestrator, compilation, filter, cycleGuard, frontier, edges, ref order, pathType, depth);

            case LocalDeclarationStatementSyntax localDecl:
                foreach (var v in localDecl.Declaration.Variables)
                {
                    if (v.Initializer?.Value is AwaitExpressionSyntax awaitInit)
                        frontier = HandleAwait(awaitInit, model, orchestrator, compilation, filter, cycleGuard, frontier, edges, ref order, pathType, depth);
                }
                return frontier;

            case IfStatementSyntax ifStmt:
            {
                var cond      = NormalizeCondition(ifStmt.Condition.ToString());
                var ifFrontier = ApplyCondition(frontier, cond);
                var endIf     = WalkBranch(ifStmt.Statement, model, orchestrator, compilation, filter, cycleGuard, ifFrontier, edges, ref order, pathType, depth);

                if (ifStmt.Else != null)
                {
                    var elseFrontier = ApplyCondition(frontier, NegateCondition(cond));
                    var endElse      = WalkBranch(ifStmt.Else.Statement, model, orchestrator, compilation, filter, cycleGuard, elseFrontier, edges, ref order, pathType, depth);
                    return endIf.Concat(endElse).ToList();
                }
                else
                {
                    var bypass = ApplyCondition(frontier, NegateCondition(cond));
                    return endIf.Concat(bypass).ToList();
                }
            }

            case TryStatementSyntax nested:
                return WalkBlock(nested.Block, model, orchestrator, compilation, filter, cycleGuard, frontier, edges, ref order, pathType, depth);

            default:
                return frontier;
        }
    }

    private List<FlowPosition> WalkBranch(
        StatementSyntax stmt, SemanticModel model, IMethodSymbol orchestrator, Compilation compilation,
        CbmExclusionFilter filter, HashSet<string> cycleGuard,
        List<FlowPosition> frontier, List<FlowEdge> edges, ref int order, string pathType, int depth) =>
        stmt is BlockSyntax block
            ? WalkBlock(block, model, orchestrator, compilation, filter, cycleGuard, frontier, edges, ref order, pathType, depth)
            : WalkStatement(stmt, model, orchestrator, compilation, filter, cycleGuard, frontier, edges, ref order, pathType, depth);

    private List<FlowPosition> HandleAwait(
        AwaitExpressionSyntax awaitExpr, SemanticModel model, IMethodSymbol orchestrator, Compilation compilation,
        CbmExclusionFilter filter, HashSet<string> cycleGuard,
        List<FlowPosition> frontier, List<FlowEdge> edges, ref int order, string pathType, int depth)
    {
        var target = ExtractStepMethod(awaitExpr, model, compilation);
        if (target is null) return frontier;

        int stepOrder = ++order;
        foreach (var pos in frontier)
            edges.Add(new FlowEdge(pos.Method, target, pos.Condition, stepOrder, pathType));

        if (depth < MaxExpansionDepth && IsExpandable(target, orchestrator, compilation, filter))
        {
            var expanded = ExpandMethod(target, orchestrator, compilation, filter, cycleGuard, edges, ref order, pathType, depth + 1);
            if (expanded != null) return expanded;
        }

        return [new FlowPosition(target, null)];
    }

    // ─── Private method expansion ──────────────────────────────────────────────

    private static bool IsExpandable(
        IMethodSymbol target, IMethodSymbol orchestrator, Compilation compilation, CbmExclusionFilter filter)
    {
        if (!IsSamePrivateClass(target, orchestrator)) return false;

        var syntaxRef = target.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef is null) return false;

        var syntax = syntaxRef.GetSyntax() as MethodDeclarationSyntax;
        if (syntax?.Body is null) return false;

        if (filter.ShouldExclude(syntax.SyntaxTree.FilePath)) return false;

        var targetModel = compilation.GetSemanticModel(syntax.SyntaxTree);

        return syntax.Body.DescendantNodes()
            .OfType<AwaitExpressionSyntax>()
            .Any(a =>
            {
                var m = ExtractStepMethodDirect(a, targetModel);
                return m != null && IsSamePrivateClass(m, orchestrator);
            });
    }

    private List<FlowPosition>? ExpandMethod(
        IMethodSymbol target, IMethodSymbol orchestrator, Compilation compilation,
        CbmExclusionFilter filter, HashSet<string> cycleGuard,
        List<FlowEdge> edges, ref int order, string pathType, int depth)
    {
        // Cycle guard: skip if we've already expanded this method
        var targetQnKey = $"{target.ContainingType?.ToDisplayString()}.{target.Name}";
        if (!cycleGuard.Add(targetQnKey)) return null;

        if (edges.Any(e => SymbolEqualityComparer.Default.Equals(e.From, target)))
            return null;

        var syntaxRef = target.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef is null) return null;

        var syntax = syntaxRef.GetSyntax() as MethodDeclarationSyntax;
        if (syntax?.Body is null) return null;

        if (filter.ShouldExclude(syntax.SyntaxTree.FilePath)) return null;

        var targetModel  = compilation.GetSemanticModel(syntax.SyntaxTree);
        var subFrontier  = new List<FlowPosition> { new(target, null) };
        return WalkBlock(syntax.Body, targetModel, orchestrator, compilation, filter, cycleGuard, subFrontier, edges, ref order, pathType, depth);
    }

    // ─── Method resolution ─────────────────────────────────────────────────────

    private IMethodSymbol? ExtractStepMethod(AwaitExpressionSyntax awaitExpr, SemanticModel model, Compilation compilation)
    {
        if (awaitExpr.Expression is not InvocationExpressionSyntax invocation) return null;

        // Try each registered step resolver first (e.g. a delegate-based dispatch resolver)
        foreach (var resolver in _stepResolvers)
        {
            var resolved = resolver.TryResolve(invocation, model, compilation);
            if (resolved != null) return resolved;
        }

        // Fall back to direct Roslyn resolution
        return ResolveDirectMethod(awaitExpr, model);
    }

    private static IMethodSymbol? ExtractStepMethodDirect(AwaitExpressionSyntax awaitExpr, SemanticModel model)
    {
        if (awaitExpr.Expression is not InvocationExpressionSyntax invocation) return null;
        return ResolveDirectInvocation(invocation, model);
    }

    private static IMethodSymbol? ResolveDirectMethod(AwaitExpressionSyntax awaitExpr, SemanticModel model)
    {
        if (awaitExpr.Expression is not InvocationExpressionSyntax invocation) return null;
        return ResolveDirectInvocation(invocation, model);
    }

    private static IMethodSymbol? ResolveDirectInvocation(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var info = model.GetSymbolInfo(invocation);
        return info.Symbol as IMethodSymbol
            ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    private static HashSet<string> DetectThrownExceptions(IMethodSymbol method)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        if (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                is not MethodDeclarationSyntax syntax || syntax.Body is null)
            return result;

        foreach (var node in syntax.Body.DescendantNodes())
        {
            var thrown = node switch
            {
                ThrowStatementSyntax ts => ts.Expression,
                ThrowExpressionSyntax te => te.Expression,
                _ => null
            };
            if (thrown is ObjectCreationExpressionSyntax oce)
                result.Add(oce.Type.ToString());
        }

        return result;
    }

    private static string GetCallName(InvocationExpressionSyntax inv) =>
        inv.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            _ => ""
        };

    private static bool IsSamePrivateClass(IMethodSymbol method, IMethodSymbol orchestrator) =>
        SymbolEqualityComparer.Default.Equals(method.ContainingType, orchestrator.ContainingType)
        && method.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected;

    // ─── Condition helpers ─────────────────────────────────────────────────────

    private static string NormalizeCondition(string raw)
    {
        raw = Regex.Replace(raw, @"\bctx\.(?:Result\.)?", "");
        raw = Regex.Replace(raw, @"\b(?:context|_ctx|_context)\.", "");
        return raw.Trim();
    }

    private static string NegateCondition(string c) =>
        c.StartsWith('!') ? c[1..] : $"!{c}";

    private static string? MergeConditions(string? a, string? b) =>
        (a, b) switch { (null, _) => b, (_, null) => a, _ => $"{a} && {b}" };

    private static List<FlowPosition> ApplyCondition(List<FlowPosition> frontier, string cond) =>
        frontier.Select(p => new FlowPosition(p.Method, MergeConditions(p.Condition, cond))).ToList();

    // ─── Persistence ───────────────────────────────────────────────────────────

    private static async Task<(int flows, int edges)> PersistFlowAsync(
        IMethodSymbol orchestrator, MethodDeclarationSyntax syntax, string kind,
        List<FlowEdge> allEdges, string filePath,
        GraphRepository graph, string project, string repoRoot, string enrichedAt)
    {
        var className      = orchestrator.ContainingType?.Name ?? "Unknown";
        var flowQn         = $"_flow_.{className}.{orchestrator.Name}";
        var orchestratorQn = BuildMethodQn(orchestrator, project, repoRoot);
        if (orchestratorQn is null) return (0, 0);

        var loc     = syntax.GetLocation().GetLineSpan();
        var relPath = SymbolQn.Relativize(filePath, repoRoot);

        var flowId = await graph.UpsertNodeAsync(
            project, "Flow", $"{className}.{orchestrator.Name}", flowQn,
            relPath, loc.StartLinePosition.Line + 1,
            new
            {
                origin           = "enricher",
                enriched_by      = new[] { "FlowEnricher" },
                enriched_at      = enrichedAt,
                presentational   = true,
                flow_kind        = kind,
                entry_method_qn  = orchestratorQn,
                step_count       = allEdges.Count(e => e.PathType == "happy"),
                path_count       = allEdges.Select(e => e.PathType).Distinct().Count()
            },
            endLine: loc.EndLinePosition.Line + 1);

        var orchestratorId = await graph.GetNodeIdAsync(project, orchestratorQn);
        if (orchestratorId.HasValue)
            await graph.UpsertEdgeAsync(project, flowId, orchestratorId.Value, "FLOW_ENTRY", new
            {
                origin         = "enricher",
                enriched_by    = new[] { "FlowEnricher" },
                enriched_at    = enrichedAt,
                presentational = false
            });

        int edgesCreated = 0;
        foreach (var edge in allEdges)
        {
            var fromQn = BuildMethodQn(edge.From, project, repoRoot);
            var toQn   = BuildMethodQn(edge.To,   project, repoRoot);
            if (fromQn is null || toQn is null) continue;

            var fromId = await graph.GetNodeIdAsync(project, fromQn);
            var toId   = await graph.GetNodeIdAsync(project, toQn);
            if (!fromId.HasValue || !toId.HasValue) continue;

            await graph.UpsertEdgeAsync(project, fromId.Value, toId.Value, "FLOW_NEXT", new
            {
                origin          = "enricher",
                enriched_by     = new[] { "FlowEnricher" },
                enriched_at     = enrichedAt,
                presentational  = edge.Presentational,
                flow_qn         = flowQn,
                order           = edge.Order,
                path_type       = edge.PathType,
                condition       = edge.Condition,
                exception_types = edge.ExceptionTypes
            });
            edgesCreated++;
        }

        return (1, edgesCreated);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    // Delegación en SymbolQn (file-path, crítico para clases parciales). Sin fallback namespace:
    // si no es resoluble → null y el paso se trata como no-localizable.
    private static string? BuildMethodQn(IMethodSymbol method, string project, string repoRoot) =>
        SymbolQn.ForMethod(method, project, repoRoot);
}
