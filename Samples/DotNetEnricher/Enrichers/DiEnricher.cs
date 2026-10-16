using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Extrae registros de Dependency Injection desde llamadas a IServiceCollection.
/// Detecta las variantes: AddScoped/AddSingleton/AddTransient con tipos genéricos.
/// </summary>
public class DiEnricher : IGraphEnricher
{
    public string Name  => "DiEnricher";
    public int    Order => 3;

    private static readonly HashSet<string> DiMethods =
    [
        "AddScoped", "AddSingleton", "AddTransient",
        "AddHostedService", "TryAddScoped",
        "TryAddSingleton", "TryAddTransient"
    ];

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        foreach (var compilation in ctx.Compilations)
            await EnrichCompilationAsync(compilation, ctx, filter, progress);
    }

    private static async Task EnrichCompilationAsync(
        Compilation compilation, EnrichmentContext ctx, CbmExclusionFilter filter, IProgress<string>? progress)
    {
        int total = 0, enriched = 0;

        foreach (var tree in compilation.SyntaxTrees
            .Where(t => !filter.ShouldExclude(t.FilePath)))
        {
            var model      = compilation.GetSemanticModel(tree);
            var invocations = tree.GetRoot()
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>();

            foreach (var inv in invocations)
            {
                total++;
                var result = await TryProcessRegistrationAsync(inv, model, ctx);
                if (result) enriched++;
            }
        }

        progress?.Report($"DI: {enriched}/{total} registrations enriched.");
    }

    private static async Task<bool> TryProcessRegistrationAsync(
        InvocationExpressionSyntax inv, SemanticModel model, EnrichmentContext ctx)
    {
        if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol method)
            return false;

        var methodName = method.Name;
        if (!DiMethods.Contains(methodName))
            return false;

        if (method.ReceiverType?.Name is not ("IServiceCollection" or "ServiceCollectionServiceExtensions"))
            if (!method.IsExtensionMethod || !method.Parameters.FirstOrDefault()?.Type.Name.Contains("IServiceCollection") == true)
                return false;

        if (method.TypeArguments.Length == 2)
        {
            var serviceType = method.TypeArguments[0];
            var implType    = method.TypeArguments[1];
            return await WriteRegistrationAsync(serviceType, implType, methodName, inv, ctx);
        }

        if (method.TypeArguments.Length == 1)
        {
            var implType = method.TypeArguments[0];
            return await WriteRegistrationAsync(implType, implType, methodName, inv, ctx);
        }

        return false;
    }

    private static async Task<bool> WriteRegistrationAsync(
        ITypeSymbol serviceType, ITypeSymbol implType,
        string methodName, InvocationExpressionSyntax inv, EnrichmentContext ctx)
    {
        var graph    = ctx.Repository;
        var project  = ctx.Project;
        var repoRoot = ctx.RepoRoot;
        var lifetime = GetLifetime(methodName);
        var serviceFqn = serviceType.ToDisplayString();
        var implFqn    = implType.ToDisplayString();

        // QN de LOCALIZACIÓN del nodo base (file-path). El QN propio de la registración
        // (_di_.{ServiceFQN}->{ImplFQN}) NO cambia — se construye más abajo.
        var serviceNodeId = await LookupBaseNodeAsync(graph, serviceType, project, repoRoot);
        var implNodeId    = await LookupBaseNodeAsync(graph, implType, project, repoRoot);

        var isExternalService = !serviceNodeId.HasValue;
        var isExternalImpl    = !implNodeId.HasValue;
        var isExternal        = isExternalService || isExternalImpl;

        var enrichedAt = DateTime.UtcNow.ToString("O");

        if (implNodeId.HasValue)
        {
            await graph.PatchNodePropertiesByIdAsync(implNodeId.Value, new
            {
                di_lifetime       = lifetime,
                di_registered_as  = serviceFqn,
                enriched_by       = new[] { "DiEnricher" },
                enriched_at       = enrichedAt
            });
        }

        var svcDisplay  = serviceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var implDisplay = implType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // Relativa al repositorio, como el file_path de cbm (el original guardaba la absoluta).
        var filePath = SymbolQn.Relativize(inv.SyntaxTree.FilePath, repoRoot);
        var line     = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var regQn    = $"_di_.{serviceFqn}->{implFqn}";

        // Quién consume el servicio, exacto con Roslyn: los miembros que nombran el tipo sin contar las llamadas de registro ni
        // las listas de bases. Nulo si el servicio no es del repositorio (no se sabe quién lo resuelve). El host consume
        // él solo lo que registra AddHostedService.
        var consumedByFramework = methodName == "AddHostedService";
        var consumers = consumedByFramework ? null : ctx.References.CountServiceConsumers(serviceType);
        ctx.DiRegistrations.Add(new DiRegistrationFact(serviceType, implType, lifetime, filePath, line));

        var regProps = new Dictionary<string, object?>
        {
            ["lifetime"]            = lifetime,
            ["service_type"]        = serviceFqn,
            ["implementation_type"] = implFqn,
            ["strategy"]            = "generic_two_types",
            ["is_external"]         = isExternal,
            ["is_external_service"] = isExternalService,
            ["is_external_impl"]    = isExternalImpl
        };
        if (consumers is int count) regProps["consumer_count"] = count;
        if (consumedByFramework)    regProps["consumed_by_framework"] = true;
        regProps["origin"]         = "enricher";
        regProps["enriched_by"]    = new[] { "DiEnricher" };
        regProps["enriched_at"]    = enrichedAt;
        regProps["presentational"] = true;

        var regNodeId = await graph.UpsertNodeAsync(
            project, "DiRegistration",
            $"{DiMethodName(lifetime)}<{svcDisplay},{implDisplay}>",
            regQn, filePath, line, regProps);

        if (serviceNodeId.HasValue)
            await graph.UpsertEdgeAsync(project, regNodeId, serviceNodeId.Value, "DI_RESOLVES", new
            {
                lifetime,
                origin         = "enricher",
                enriched_by    = new[] { "DiEnricher" },
                enriched_at    = enrichedAt,
                presentational = true
            });

        if (implNodeId.HasValue && serviceNodeId != implNodeId)
            await graph.UpsertEdgeAsync(project, regNodeId, implNodeId.Value, "DI_RESOLVES", new
            {
                lifetime,
                role           = "implementation",
                origin         = "enricher",
                enriched_by    = new[] { "DiEnricher" },
                enriched_at    = enrichedAt,
                presentational = true
            });

        return true;
    }

    private static string GetLifetime(string methodName) => methodName switch
    {
        var m when m.Contains("Singleton") => "Singleton",
        var m when m.Contains("Scoped")    => "Scoped",
        _                                  => "Transient"
    };

    private static string DiMethodName(string lifetime) => lifetime switch
    {
        "Singleton" => "AddSingleton",
        "Scoped"    => "AddScoped",
        _           => "AddTransient"
    };

    /// <summary>
    /// Localiza el nodo del grafo base para un tipo usando el QN file-path (<see cref="SymbolQn.ForType"/>).
    /// Devuelve null si el tipo es externo (sin syntaxRef en el repo) → se marca como is_external.
    /// </summary>
    private static async Task<long?> LookupBaseNodeAsync(
        GraphRepository graph, ITypeSymbol type, string project, string repoRoot)
    {
        if (type is not INamedTypeSymbol named) return null;
        var qn = SymbolQn.ForType(named, project, repoRoot);
        return qn is null ? null : await graph.GetNodeIdAsync(project, qn);
    }
}
