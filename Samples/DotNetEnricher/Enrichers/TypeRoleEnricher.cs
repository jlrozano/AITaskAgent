using Microsoft.CodeAnalysis;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Clasifica cada tipo del grafo por el papel que juega, para responder si un proyecto depende de contratos o de detalles
/// internos de otro (vista <c>graph_domain(view: contracts)</c>). Con Roslyn, sobre los tipos que tienen nodo:
///   - <c>type_role</c>: <c>contract</c>, <c>implementation</c>, <c>infrastructure</c> o <c>model</c> (reglas en <see cref="Classify"/>).
///   - <c>is_public</c>: visible desde fuera del ensamblado (el tipo y todos los que lo contienen son públicos).
///   - <c>cs_project</c>: el proyecto C# al que pertenece el fichero.
/// Además escribe en cada <c>CsProject</c> la abstracción de Martin: <c>abstractness</c> (tipos abstractos —interfaces y
/// clases abstractas— entre el total), <c>distance</c> a la secuencia principal (|A + I - 1|, con la inestabilidad que
/// calculó <see cref="ProjectEnricher"/>) y <c>type_roles</c> (cuántos tipos de cada papel). La heurística es estructural y
/// no usa nombres del dominio analizado. Order 8: tras <see cref="ProjectEnricher"/>, que crea los nodos <c>CsProject</c>.
/// </summary>
public class TypeRoleEnricher : IGraphEnricher
{
    public const string Contract       = "contract";
    public const string Implementation = "implementation";
    public const string Infrastructure = "infrastructure";
    public const string Model          = "model";

    public string Name  => "TypeRoleEnricher";
    public int    Order => 8;

    private sealed class Tally
    {
        public int Total;
        public int Abstractions;
        public Dictionary<string, int> Roles { get; } = new(StringComparer.Ordinal);
    }

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var symbols    = await ctx.GetGraphSymbolsAsync();
        var enrichedAt = DateTime.UtcNow.ToString("O");
        var tallies    = new Dictionary<string, Tally>(StringComparer.Ordinal);
        int typed = 0;

        foreach (var (nodeId, type) in symbols.TypesByNode)
        {
            var role      = Classify(type);
            var isPublic  = IsExternallyVisible(type);
            var file      = type.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
            var csProject = ctx.CsProjectOf(file is null ? null : SymbolQn.Relativize(file, ctx.RepoRoot))?.Name;

            var patch = new Dictionary<string, object?>
            {
                ["type_role"]      = role,
                ["is_public"]      = isPublic,
                ["enriched_by"]    = new[] { Name },
                ["enriched_at"]    = enrichedAt
            };
            if (csProject is not null)
                patch["cs_project"] = csProject;

            await ctx.Repository.PatchNodePropertiesByIdAsync(nodeId, patch);
            ctx.RecordPatchHit(Name);
            typed++;

            if (csProject is null) continue;
            if (!tallies.TryGetValue(csProject, out var tally))
                tallies[csProject] = tally = new Tally();
            tally.Total++;
            if (IsAbstraction(type)) tally.Abstractions++;
            tally.Roles[role] = tally.Roles.GetValueOrDefault(role) + 1;
        }

        foreach (var (csProject, tally) in tallies)
            await PatchProjectAsync(ctx, csProject, tally, enrichedAt);

        progress?.Report($"TypeRole: {typed} types classified in {tallies.Count} projects.");
    }

    private async Task PatchProjectAsync(EnrichmentContext ctx, string csProject, Tally tally, string enrichedAt)
    {
        var qn = $"_proj_.{csProject}";
        var instability = await ctx.Repository.ExecuteScalarAsync<double?>(
            "SELECT json_extract(properties, '$.instability') FROM nodes WHERE project = @project AND qualified_name = @qn",
            new { project = ctx.Project, qn });

        // Sin nodo CsProject (la solución no lo declaró) no hay dónde escribirlo.
        if (instability is null) return;

        double abstractness = tally.Total == 0 ? 0 : (double)tally.Abstractions / tally.Total;
        double distance     = Math.Abs(abstractness + instability.Value - 1);
        await ctx.Repository.PatchNodePropertiesAsync(ctx.Project, qn, new
        {
            abstractness = Math.Round(abstractness, 2),
            distance     = Math.Round(distance, 2),
            type_roles   = tally.Roles,
            enriched_by  = new[] { Name },
            enriched_at  = enrichedAt
        });
    }

    /// <summary>
    /// Papel del tipo, por la primera regla que cumpla:
    /// <list type="number">
    ///   <item>enum o delegado: <c>model</c>;</item>
    ///   <item>clase estática (utilidades, métodos de extensión, registro de DI): <c>infrastructure</c>;</item>
    ///   <item>interfaz o clase abstracta: <c>contract</c> si es pública; si no, <c>infrastructure</c> (abstracción interna);</item>
    ///   <item>excepción o atributo (derivan de <c>System.Exception</c> / <c>System.Attribute</c>): <c>model</c>;</item>
    ///   <item>con punto de entrada <c>Main</c> o con una clase base de <c>Microsoft.*</c> (hosting, controladores,
    ///         middleware…): <c>infrastructure</c>;</item>
    ///   <item>implementa alguna interfaz declarada en el código fuente: <c>implementation</c>;</item>
    ///   <item>solo datos (propiedades o campos, sin métodos de instancia propios): <c>model</c>;</item>
    ///   <item>el resto: <c>implementation</c>.</item>
    /// </list>
    /// </summary>
    internal static string Classify(INamedTypeSymbol type)
    {
        if (type.TypeKind is TypeKind.Enum or TypeKind.Delegate)
            return Model;
        if (type.IsStatic)
            return Infrastructure;
        if (IsAbstraction(type))
            return IsExternallyVisible(type) ? Contract : Infrastructure;
        if (DerivesFrom(type, "System.Exception") || DerivesFrom(type, "System.Attribute"))
            return Model;
        if (HasEntryPoint(type) || HasFrameworkBase(type))
            return Infrastructure;
        if (type.AllInterfaces.Any(i => i.DeclaringSyntaxReferences.Length > 0))
            return Implementation;
        return IsDataOnly(type) ? Model : Implementation;
    }

    /// <summary>Interfaz o clase abstracta (las estáticas son abstractas para Roslyn, pero no son una abstracción).</summary>
    private static bool IsAbstraction(INamedTypeSymbol type) =>
        !type.IsStatic && (type.TypeKind == TypeKind.Interface || (type.TypeKind == TypeKind.Class && type.IsAbstract));

    /// <summary>Visible desde otro ensamblado: el tipo y los que lo contienen son públicos.</summary>
    internal static bool IsExternallyVisible(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
            if (t.DeclaredAccessibility != Accessibility.Public)
                return false;
        return true;
    }

    private static bool DerivesFrom(INamedTypeSymbol type, string fullName)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (b.ToDisplayString() == fullName)
                return true;
        return false;
    }

    private static bool HasEntryPoint(INamedTypeSymbol type) =>
        type.GetMembers("Main").OfType<IMethodSymbol>().Any(m => m.IsStatic);

    private static bool HasFrameworkBase(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (b.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.", StringComparison.Ordinal) == true)
                return true;
        return false;
    }

    /// <summary>Tiene propiedades o campos y ningún método de instancia propio (los override de object no cuentan).</summary>
    private static bool IsDataOnly(INamedTypeSymbol type)
    {
        var members = type.GetMembers().Where(m => !m.IsImplicitlyDeclared).ToList();
        var behaviour = members.OfType<IMethodSymbol>()
            .Any(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic && !m.IsOverride);
        return !behaviour && members.Any(m => m is IPropertySymbol or IFieldSymbol);
    }
}
