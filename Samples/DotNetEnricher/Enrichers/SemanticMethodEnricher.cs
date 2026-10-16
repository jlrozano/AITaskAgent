using Microsoft.CodeAnalysis;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Enriquece nodos Method con metadatos semánticos de Roslyn:
/// is_async, is_abstract, is_virtual, is_override, is_static, accessibility (también en constructores, con
/// <c>is_constructor</c>, y en implementaciones explícitas de interfaz, con <c>is_explicit_interface_impl</c>).
/// También crea nodos Interface para interfaces C# no indexadas por codebase-memory-mcp,
/// añade aristas IMPLEMENTS (todas las interfaces del tipo, directas y heredadas; las heredadas llevan
/// <c>is_inherited</c>) y aristas OVERRIDES entre un método y el que sobrescribe. El <c>docstring</c> lo escribe
/// <see cref="CommentEnricher"/>, con la documentación XML completa o el comentario anterior.
/// </summary>
public class SemanticMethodEnricher : IGraphEnricher
{
    public string Name  => "SemanticMethodEnricher";
    public int    Order => 2;

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        foreach (var compilation in ctx.Compilations)
            await EnrichCompilationAsync(compilation, ctx, filter, progress);
    }

    private async Task EnrichCompilationAsync(
        Compilation compilation, EnrichmentContext ctx,
        CbmExclusionFilter filter, IProgress<string>? progress)
    {
        int processed = 0, ifacesCreated = 0;

        foreach (var tree in compilation.SyntaxTrees
            .Where(t => !filter.ShouldExclude(t.FilePath)))
        {
            var model = compilation.GetSemanticModel(tree);
            var allTypes = tree.GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>();

            foreach (var typeDecl in allTypes)
            {
                if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSymbol)
                    continue;

                if (typeSymbol.TypeKind == TypeKind.Interface)
                    ifacesCreated += await EnsureInterfaceNodeAsync(typeSymbol, typeDecl, ctx) ? 1 : 0;

                await EnrichImplementsAsync(typeSymbol, ctx);

                foreach (var member in typeSymbol.GetMembers().OfType<IMethodSymbol>())
                {
                    // Solo métodos que son nodos reales del grafo base (cbm): ordinarios, constructores declarados e
                    // implementaciones explícitas (GraphMethods). Se saltan accessors de propiedades/eventos
                    // (get_/set_/add_/remove_) y miembros generados por el compilador: cbm no los nodea, y el patch
                    // produciría misses falsos. Antes se saltaban también los constructores y las explícitas, que se
                    // quedaban sin accessibility.
                    if (!GraphMethods.HasNode(member)
                        || GraphMethods.IsShadowedExplicitImpl(member, ctx.Project, ctx.RepoRoot))
                        continue;

                    await EnrichMethodAsync(member, ctx);
                    processed++;
                }
            }
        }

        progress?.Report($"SemanticMethod: {processed} methods enriched, {ifacesCreated} interface nodes created.");
    }

    private async Task<bool> EnsureInterfaceNodeAsync(
        INamedTypeSymbol iface,
        Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax typeDecl,
        EnrichmentContext ctx)
    {
        var graph   = ctx.Repository;
        var project = ctx.Project;

        var qn = SymbolQn.ForType(iface, project, ctx.RepoRoot);
        if (qn is null) return false;

        var existingId = await graph.GetNodeIdAsync(project, qn);
        if (existingId.HasValue) return false;

        var absolutePath = typeDecl.SyntaxTree.FilePath;
        var filePath = SymbolQn.Relativize(absolutePath, ctx.RepoRoot);
        var startLine = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine   = typeDecl.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var summary = DocText.For(iface).Text;
        var enrichedAt = DateTime.UtcNow.ToString("O");

        await graph.UpsertNodeAsync(
            project, "Interface", iface.Name, qn,
            filePath, startLine,
            new
            {
                docstring    = summary,
                origin       = "enricher",
                enriched_by  = new[] { "SemanticMethodEnricher" },
                enriched_at  = enrichedAt,
                presentational = true
            },
            endLine: endLine);

        return true;
    }

    private async Task EnrichMethodAsync(IMethodSymbol method, EnrichmentContext ctx)
    {
        var fqn = SymbolQn.ForMethod(method, ctx.Project, ctx.RepoRoot);
        if (fqn is null) return;

        var patch = new Dictionary<string, object?>
        {
            ["is_async"]      = method.IsAsync,
            ["is_abstract"]   = method.IsAbstract,
            ["is_virtual"]    = method.IsVirtual,
            ["is_override"]   = method.IsOverride,
            ["is_static"]     = method.IsStatic,
            ["accessibility"] = method.DeclaredAccessibility.ToString().ToLowerInvariant()
        };
        // Un constructor no devuelve nada: sin return_type_fqn (Roslyn da void) y con is_constructor.
        if (method.MethodKind == MethodKind.Constructor) patch["is_constructor"] = true;
        else patch["return_type_fqn"] = method.ReturnType.ToDisplayString();
        // Una implementación explícita es privada para C#, pero se invoca por la interfaz.
        if (method.MethodKind == MethodKind.ExplicitInterfaceImplementation) patch["is_explicit_interface_impl"] = true;

        var patched = await ctx.Repository.PatchNodePropertiesAsync(ctx.Project, fqn, patch);
        if (patched == 0) ctx.RecordPatchMiss(Name, fqn);
        else              ctx.RecordPatchHit(Name);

        await WriteOverridesAsync(method, fqn, ctx);
    }

    /// <summary>OVERRIDES del método a la versión de la clase base que sobrescribe, si las dos tienen nodo.</summary>
    private static async Task WriteOverridesAsync(IMethodSymbol method, string fqn, EnrichmentContext ctx)
    {
        if (method.OverriddenMethod is not { } baseMethod) return;
        var baseQn = SymbolQn.ForMethod(baseMethod.OriginalDefinition, ctx.Project, ctx.RepoRoot);
        if (baseQn is null) return;

        var sourceId = await ctx.Repository.GetNodeIdAsync(ctx.Project, fqn);
        var targetId = await ctx.Repository.GetNodeIdAsync(ctx.Project, baseQn);
        if (!sourceId.HasValue || !targetId.HasValue || sourceId == targetId) return;

        await ctx.Repository.UpsertEdgeAsync(ctx.Project, sourceId.Value, targetId.Value, "OVERRIDES", new
        {
            origin         = "enricher",
            enriched_by    = new[] { "SemanticMethodEnricher" },
            enriched_at    = DateTime.UtcNow.ToString("O"),
            presentational = true
        });
    }

    private static async Task EnrichImplementsAsync(INamedTypeSymbol typeSymbol, EnrichmentContext ctx)
    {
        var graph   = ctx.Repository;
        var project = ctx.Project;

        var classQn = SymbolQn.ForType(typeSymbol, project, ctx.RepoRoot);
        if (classQn is null) return;

        var classNodeId = await graph.GetNodeIdAsync(project, classQn);
        if (!classNodeId.HasValue) return;

        var enrichedAt = DateTime.UtcNow.ToString("O");

        // AllInterfaces: también las que llegan por una interfaz base o por la clase base. Antes solo salían las declaradas
        // en el propio tipo, y una llamada a la interfaz base no alcanzaba a las implementaciones.
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var ifaceQn = SymbolQn.ForType(iface, project, ctx.RepoRoot);
            if (ifaceQn is null) continue;

            var ifaceNodeId = await graph.GetNodeIdAsync(project, ifaceQn);
            if (!ifaceNodeId.HasValue) continue;

            var edgeProps = new Dictionary<string, object?> { ["is_explicit"] = false };
            if (!typeSymbol.Interfaces.Contains(iface, SymbolEqualityComparer.Default))
                edgeProps["is_inherited"] = true;
            edgeProps["origin"]         = "enricher";
            edgeProps["enriched_by"]    = new[] { "SemanticMethodEnricher" };
            edgeProps["enriched_at"]    = enrichedAt;
            edgeProps["presentational"] = true;
            await graph.UpsertEdgeAsync(project, classNodeId.Value, ifaceNodeId.Value, "IMPLEMENTS", edgeProps);
        }
    }

}
