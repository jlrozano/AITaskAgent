using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Preparation;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>Un nodo del grafo con fichero. <see cref="Properties"/> solo se carga para tipos y métodos (vacío en el resto).</summary>
public sealed record GraphNodeFact(
    long Id, string Label, string Name, string QualifiedName, string File, int StartLine, int EndLine, JsonObject Properties)
{
    public bool IsMethod => Label == "Method";
}

/// <summary>Una arista de dependencia; <see cref="Exact"/> es la propiedad <c>exact</c> (resuelta por el compilador).</summary>
public readonly record struct GraphEdgeFact(long Source, long Target, string Type, bool Exact);

/// <summary>
/// Lo que los paquetes de la pasada compartida leen del grafo de cbm (en solo lectura): los nodos con fichero, las propiedades de
/// tipos y métodos (las del indexador y las de los enriquecedores, si las hay) y las aristas de dependencia de
/// <see cref="DependencyEdgeTypes"/>. Un grafo sin las propiedades nuevas o con JSON inválido se lee igual; una BD que no existe o
/// no se puede leer da un grafo vacío con aviso.
/// </summary>
public sealed class SharedPassGraph
{
    /// <summary>Tipos de arista que cuentan como relación entre lotes (los del experimento de 2026-10-16).</summary>
    public static readonly IReadOnlyList<string> DependencyEdgeTypes = ["CALLS", "USAGE", "INHERITS", "IMPLEMENTS", "OVERRIDES", "READS", "WRITES", "THROWS"];

    public static SharedPassGraph Empty { get; } = new("", [], [], []);

    private readonly Dictionary<long, GraphNodeFact> _nodes;
    private readonly Dictionary<string, List<GraphNodeFact>> _factsByFile;

    private SharedPassGraph(string project, IEnumerable<GraphNodeFact> nodes, IReadOnlyList<GraphEdgeFact> edges, IReadOnlyList<string> warnings)
    {
        Project = project;
        _nodes = nodes.ToDictionary(n => n.Id);
        Edges = edges;
        Warnings = warnings;
        _factsByFile = new Dictionary<string, List<GraphNodeFact>>(StringComparer.OrdinalIgnoreCase);
        var factLabels = new HashSet<string>(TypeGraphBuilder.TypeLabels.Append("Method"), StringComparer.Ordinal);
        foreach (var node in _nodes.Values.Where(n => factLabels.Contains(n.Label)))
        {
            if (!_factsByFile.TryGetValue(node.File, out var list))
                _factsByFile[node.File] = list = [];
            list.Add(node);
        }
        foreach (var list in _factsByFile.Values)
            list.Sort((a, b) => a.StartLine != b.StartLine ? a.StartLine.CompareTo(b.StartLine)
                : string.CompareOrdinal(a.QualifiedName, b.QualifiedName) is var c && c != 0 ? c : a.Id.CompareTo(b.Id));
    }

    /// <summary>Proyecto de cbm de la mayoría de los nodos: el prefijo que <see cref="ShortName"/> quita.</summary>
    public string Project { get; }

    /// <summary>Aristas de <see cref="DependencyEdgeTypes"/> entre nodos con fichero, ordenadas por (origen, destino, tipo).</summary>
    public IReadOnlyList<GraphEdgeFact> Edges { get; }

    public IReadOnlyList<string> Warnings { get; }

    public GraphNodeFact? Node(long id) => _nodes.GetValueOrDefault(id);

    /// <summary>Los ficheros con algún tipo o método.</summary>
    public IEnumerable<string> FactFiles => _factsByFile.Keys;

    /// <summary>Tipos y métodos de un fichero, por línea de inicio, nombre cualificado e id.</summary>
    public IReadOnlyList<GraphNodeFact> FactsOf(string file) => _factsByFile.GetValueOrDefault(file) ?? [];

    /// <summary>El nombre cualificado sin el prefijo del proyecto.</summary>
    public string ShortName(string qualifiedName) =>
        Project.Length > 0 && qualifiedName.StartsWith(Project + ".", StringComparison.Ordinal) ? qualifiedName[(Project.Length + 1)..] : qualifiedName;

    public static async Task<SharedPassGraph> LoadAsync(string databasePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return new SharedPassGraph("", [], [], [$"⚠️ No existe la base de datos del grafo ('{databasePath}'): los paquetes van sin hechos del grafo."]);

        try
        {
            using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await conn.OpenAsync(ct);
            return await LoadAsync(conn, ct);
        }
        catch (SqliteException ex)
        {
            return new SharedPassGraph("", [], [], [$"⚠️ No se pudo leer el grafo para los paquetes: {ex.Message}. Van sin hechos del grafo."]);
        }
    }

    internal static async Task<SharedPassGraph> LoadAsync(SqliteConnection conn, CancellationToken ct)
    {
        var factLabels = new HashSet<string>(TypeGraphBuilder.TypeLabels.Append("Method"), StringComparer.Ordinal);
        var project = "";
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT project FROM nodes GROUP BY project ORDER BY count(*) DESC, project LIMIT 1";
            project = await cmd.ExecuteScalarAsync(ct) as string ?? "";
        }

        var nodes = new List<GraphNodeFact>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, label, name, qualified_name, coalesce(file_path, ''), coalesce(start_line, 0), coalesce(end_line, 0), properties
                FROM nodes WHERE coalesce(file_path, '') <> '' ORDER BY id
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var label = reader.GetString(1);
                var properties = factLabels.Contains(label) && !reader.IsDBNull(7) ? ParseObject(reader.GetString(7)) : [];
                nodes.Add(new GraphNodeFact(reader.GetInt64(0), label, reader.GetString(2), reader.GetString(3),
                    reader.GetString(4).Replace('\\', '/'), reader.GetInt32(5), reader.GetInt32(6), properties));
            }
        }

        var ids = nodes.Select(n => n.Id).ToHashSet();
        var edges = new List<GraphEdgeFact>();
        await using (var cmd = conn.CreateCommand())
        {
            var types = string.Join(", ", DependencyEdgeTypes.Select(t => $"'{t}'"));
            cmd.CommandText = $"SELECT source_id, target_id, type, properties FROM edges WHERE type IN ({types}) ORDER BY source_id, target_id, type, id";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var (source, target) = (reader.GetInt64(0), reader.GetInt64(1));
                if (!ids.Contains(source) || !ids.Contains(target))
                    continue;
                var exact = !reader.IsDBNull(3) && ParseObject(reader.GetString(3))["exact"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                edges.Add(new GraphEdgeFact(source, target, reader.GetString(2), exact));
            }
        }

        return new SharedPassGraph(project, nodes, edges, []);
    }

    private static JsonObject ParseObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        try
        {
            if (JsonNode.Parse(text) is not JsonObject obj)
                return [];
            _ = obj.Count; // JsonObject es perezoso: una clave repetida falla aquí y no al leerla después
            return obj;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }
}
