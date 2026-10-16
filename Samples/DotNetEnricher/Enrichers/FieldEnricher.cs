using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;
using Microsoft.CodeAnalysis;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Campos con sus lecturas y escrituras exactas (Roslyn), con la misma forma que el analizador Java de la app
/// (<c>JavaGraphWriter</c>) para que la faceta <c>unread_fields</c> funcione sin cambios:
///
///   - Nodo <c>Field</c> en <c>{QN del tipo}.{campo}</c> (cbm deja el campo como un <c>Variable</c> suelto en
///     <c>{prefijo del fichero}.{campo}</c>, sin propiedades de campo) con <c>field_name</c>, <c>type_fqn</c>,
///     <c>accessibility</c>, <c>is_static</c>, <c>is_final</c> (<c>readonly</c> o <c>const</c>) y <c>docstring</c>; además
///     <c>is_readonly</c>, <c>is_const</c> y <c>parent_class</c>. Arista <c>DEFINES</c> del tipo al campo.
///   - Aristas <c>READS</c> y <c>WRITES</c> del método (o del tipo, en un inicializador de campo) al campo, una por
///     (origen, campo, tipo) con <c>lines</c> (líneas distintas) y <c>count</c> (referencias). Un <c>+=</c>, un <c>++</c> o un
///     <c>ref</c> dan las dos aristas; un <c>out</c> o un destino de asignación, solo <c>WRITES</c>; <c>nameof</c> no da ninguna.
///
/// Quedan fuera los miembros de un <c>enum</c> y los campos de respaldo del compilador. Los accesos desde código de nivel
/// superior no tienen origen en el grafo y no dan arista.
/// </summary>
public class FieldEnricher : IGraphEnricher
{
    public string Name  => "FieldEnricher";
    public int    Order => 5;

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph = ctx.Repository;
        var project = ctx.Project;
        var symbols = await ctx.GetGraphSymbolsAsync();
        var index = ctx.References;

        int fields = 0, reads = 0, writes = 0, unmatched = 0;
        var enrichedAt = DateTime.UtcNow.ToString("O");

        foreach (var (typeId, type) in symbols.TypesByNode.OrderBy(t => t.Key))
        {
            if (type.TypeKind == TypeKind.Enum || SymbolQn.ForType(type, project, ctx.RepoRoot) is not { } typeQn) continue;

            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsImplicitlyDeclared || field.AssociatedSymbol is not null || field.DeclaringSyntaxReferences.Length == 0) continue;

                var declaration = field.DeclaringSyntaxReferences[0];
                var location = declaration.SyntaxTree.GetLineSpan(declaration.Span);
                var props = new Dictionary<string, object?>
                {
                    ["field_name"]    = field.Name,
                    ["type_fqn"]      = field.Type.ToDisplayString(),
                    ["accessibility"] = field.DeclaredAccessibility.ToString().ToLowerInvariant(),
                    ["is_static"]     = field.IsStatic || field.IsConst,
                    ["is_final"]      = field.IsReadOnly || field.IsConst,
                    ["is_readonly"]   = field.IsReadOnly,
                    ["is_const"]      = field.IsConst,
                    ["docstring"]     = DocText.For(field).Text,
                    ["parent_class"]  = typeQn,
                    ["origin"]        = "enricher",
                    ["enriched_by"]   = new[] { Name },
                    ["enriched_at"]   = enrichedAt
                };

                var fieldId = await graph.UpsertNodeAsync(
                    project, "Field", field.Name, $"{typeQn}.{field.Name}",
                    SymbolQn.Relativize(declaration.SyntaxTree.FilePath, ctx.RepoRoot),
                    location.StartLinePosition.Line + 1, props, endLine: location.EndLinePosition.Line + 1);
                await graph.UpsertEdgeAsync(project, typeId, fieldId, "DEFINES", EnrichmentMarks.EdgeProperties(Name));
                fields++;

                // Por (origen, tipo de arista): líneas distintas y número de referencias.
                var edges = new Dictionary<(long Source, string Type), (SortedSet<int> Lines, int Count)>();
                foreach (var reference in index.To(field))
                {
                    if (symbols.NodeOfContainer(reference.Container) is not { } source)
                    {
                        unmatched++;
                        continue;
                    }
                    foreach (var edgeType in EdgeTypes(reference.Flags))
                    {
                        if (!edges.TryGetValue((source, edgeType), out var agg)) agg = (new SortedSet<int>(), 0);
                        agg.Lines.Add(reference.Line);
                        edges[(source, edgeType)] = (agg.Lines, agg.Count + 1);
                    }
                }

                foreach (var ((source, edgeType), (lines, count)) in edges.OrderBy(e => e.Key.Source).ThenBy(e => e.Key.Type, StringComparer.Ordinal))
                {
                    await graph.UpsertEdgeAsync(project, source, fieldId, edgeType, EnrichmentMarks.EdgeProperties(Name, new JsonObject
                    {
                        ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
                        ["count"] = count
                    }));
                    if (edgeType == "READS") reads++; else writes++;
                }
            }
        }

        progress?.Report($"Fields: {fields} field node(s), {reads} READS and {writes} WRITES edge(s); {unmatched} access(es) without a graph node to originate from.");
    }

    private static IEnumerable<string> EdgeTypes(ReferenceFlags flags)
    {
        if ((flags & ReferenceFlags.Read) != 0) yield return "READS";
        if ((flags & ReferenceFlags.Write) != 0) yield return "WRITES";
    }
}
