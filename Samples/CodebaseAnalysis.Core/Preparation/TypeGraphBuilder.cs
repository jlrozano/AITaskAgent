using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Steps;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// Lee del grafo de cbm (en solo lectura) el grafo agregado a nivel de tipo (T7): los nodos son las clases, interfaces y
/// enumerados de los ficheros de producción, y las aristas suman las dependencias entre ellos. Una llamada de un método a otro
/// cuenta como una dependencia entre los tipos que los definen (vía <c>DEFINES_METHOD</c>; sin ella, el tipo principal del
/// fichero), y se suman también las referencias de tipo (<c>USAGE</c>, <c>USES_TYPE</c>), los accesos (<c>READS</c>,
/// <c>WRITES</c>), la herencia (<c>INHERITS</c>, <c>IMPLEMENTS</c> y la propiedad <c>base_classes</c> de los nodos, que es lo
/// único que cbm deja en Java), las excepciones lanzadas y los pares acción-formulario de las rutas de Struts.
/// </summary>
/// <remarks>
/// Las tablas (nodos <c>Table</c> o <c>AccessTable</c>) entran como nodos virtuales: dos tipos que tocan la misma tabla quedan
/// unidos por ella en la detección de comunidades (la señal «los mismos DAO tocan las mismas tablas» de la spec §4.2).
/// <c>SEMANTICALLY_RELATED</c> y <c>SIMILAR_TO</c> no se usan: dicen que dos fragmentos se parecen, no que dependan uno de
/// otro, y unirían capacidades distintas por tener el mismo estilo de código. Las consultas sobre <c>properties</c> pasan por
/// <see cref="SqlFacetExtractorStep.SafeJson"/>: cbm deja nodos con JSON inválido. Lo que no se puede leer es un aviso y esa
/// parte queda vacía.
/// </remarks>
public static class TypeGraphBuilder
{
    /// <summary>Etiquetas de cbm que son un tipo.</summary>
    public static readonly IReadOnlyList<string> TypeLabels = ["Class", "Interface", "Enum", "Struct", "Record", "Trait", "Type", "Annotation"];

    /// <summary>Peso de cada tipo de arista; la herencia pesa más porque dos tipos emparentados casi siempre son del mismo asunto.</summary>
    private static readonly (string Type, double Weight)[] EdgeWeights =
    [
        ("CALLS", 1), ("USAGE", 1), ("USES_TYPE", 1), ("READS", 1), ("WRITES", 1), ("CONFIGURES", 1),
        ("INHERITS", 3), ("IMPLEMENTS", 3), ("THROWS", 0.5)
    ];

    private const double StrutsPairWeight = 2;
    private const double TableWeight = 1;
    private const double BaseClassWeight = 3;

    /// <summary>
    /// Lee el grafo de <paramref name="databasePath"/>. <paramref name="isProductionFile"/> decide con la ruta relativa
    /// (con <c>/</c>) si un fichero es de producción: los tipos de los demás no entran, y las aristas de o hacia ellos tampoco.
    /// </summary>
    public static async Task<TypeGraph> ReadAsync(string databasePath, Func<string, bool> isProductionFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            var builder = new TypeGraph.Builder();
            builder.Warn($"⚠️ No existe la base de datos del grafo ('{databasePath}'): el mapa de dominios se calcula solo con la estructura.");
            return builder.Build();
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await conn.OpenAsync(ct);
            return await ReadAsync(conn, isProductionFile, ct);
        }
        catch (SqliteException ex)
        {
            var builder = new TypeGraph.Builder();
            builder.Warn($"⚠️ No se pudo leer el grafo de tipos: {ex.Message}. El mapa de dominios se calcula solo con la estructura.");
            return builder.Build();
        }
    }

    internal static async Task<TypeGraph> ReadAsync(SqliteConnection conn, Func<string, bool> isProductionFile, CancellationToken ct)
    {
        var graph = new TypeGraph.Builder();

        // 1. Tipos de producción. owner[id de nodo] = posición provisional del tipo al que pertenece ese nodo.
        var owner = new Dictionary<long, int>();
        var typesByFile = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var typeNames = new List<string>();
        var typeFiles = new List<string>();
        var baseClasses = new List<(int Type, string Raw)>();

        await using (var cmd = conn.CreateCommand())
        {
            var labels = string.Join(", ", TypeLabels.Select(l => $"'{l}'"));
            cmd.CommandText = $"""
                SELECT n.id, n.label, n.name, n.qualified_name, coalesce(n.file_path, ''),
                       json_extract({SqlFacetExtractorStep.SafeJson("n.properties")}, '$.base_classes')
                FROM nodes n WHERE n.label IN ({labels})
                ORDER BY n.qualified_name, n.id
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var file = reader.GetString(4).Replace('\\', '/');
                if (file.Length == 0 || !isProductionFile(file))
                    continue;

                var index = graph.AddNode(reader.GetString(3), reader.GetString(2), reader.GetString(1), file);
                if (index != typeNames.Count)
                    continue; // mismo nombre cualificado que otro nodo ya leído: es el mismo tipo
                owner[reader.GetInt64(0)] = index;
                typeNames.Add(reader.GetString(2));
                typeFiles.Add(file);
                if (!typesByFile.TryGetValue(file, out var inFile))
                    typesByFile[file] = inFile = [];
                inFile.Add(index);
                if (!reader.IsDBNull(5))
                    baseClasses.Add((index, reader.GetString(5)));
            }
        }

        if (typeNames.Count == 0)
            return graph.Build();

        // El tipo principal de cada fichero: el que se llama como él; si no, el primero por clave.
        var primaryOfFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, inFile) in typesByFile)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var named = inFile.FirstOrDefault(i => string.Equals(typeNames[i], stem, StringComparison.OrdinalIgnoreCase), -1);
            primaryOfFile[file] = named >= 0 ? named : inFile[0];
        }

        // 2. Métodos: su tipo es el que los define; los demás nodos (campos, módulos de fichero...) caen en el principal de su fichero.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT source_id, target_id FROM edges WHERE type = 'DEFINES_METHOD'";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (owner.TryGetValue(reader.GetInt64(0), out var type))
                    owner[reader.GetInt64(1)] = type;
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT n.id, coalesce(n.file_path, '') FROM nodes n WHERE n.label NOT IN ('Folder', 'Project')";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                if (owner.ContainsKey(id))
                    continue;
                var file = reader.GetString(1).Replace('\\', '/');
                if (file.Length > 0 && primaryOfFile.TryGetValue(file, out var primary))
                    owner[id] = primary;
            }
        }

        // 3. Dependencias de tipo a tipo.
        await using (var cmd = conn.CreateCommand())
        {
            var weights = string.Join(" ", EdgeWeights.Select(w => $"WHEN '{w.Type}' THEN {w.Weight.ToString(CultureInfo.InvariantCulture)}"));
            var types = string.Join(", ", EdgeWeights.Select(w => $"'{w.Type}'"));
            cmd.CommandText = $"SELECT source_id, target_id, CASE type {weights} END FROM edges WHERE type IN ({types})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (owner.TryGetValue(reader.GetInt64(0), out var source) && owner.TryGetValue(reader.GetInt64(1), out var target))
                    graph.AddEdge(source, target, reader.GetDouble(2));
            }
        }

        // 4. Herencia que cbm solo deja como propiedad (Java): «implements X», «extends Y», «: Base, IFoo».
        AddBaseClassEdges(graph, baseClasses, typeNames, typeFiles);

        // 5. Pares acción-formulario de las rutas de Struts.
        await TryAsync(graph, "las rutas de Struts", () => AddStrutsPairsAsync(conn, graph, typeNames, typeFiles, ct));

        // 6. Tablas como nodos virtuales unidos a los tipos que las tocan.
        await TryAsync(graph, "las tablas", () => AddTablesAsync(conn, graph, owner, ct));

        return graph.Build();
    }

    private static async Task TryAsync(TypeGraph.Builder graph, string what, Func<Task> read)
    {
        try
        {
            await read();
        }
        catch (SqliteException ex)
        {
            graph.Warn($"⚠️ No se pudo leer {what} del grafo para el mapa de dominios: {ex.Message}");
        }
    }

    private static void AddBaseClassEdges(
        TypeGraph.Builder graph, List<(int Type, string Raw)> baseClasses, List<string> typeNames, List<string> typeFiles)
    {
        var bySimpleName = IndexBySimpleName(typeNames);
        foreach (var (type, raw) in baseClasses)
        {
            foreach (var name in BaseClassNames(raw))
            {
                var target = ResolveSimpleName(name, bySimpleName, typeFiles, typeFiles[type]);
                if (target >= 0)
                    graph.AddEdge(type, target, BaseClassWeight);
            }
        }
    }

    /// <summary>Nombres simples de lo que dice una propiedad <c>base_classes</c> (un array JSON de textos como <c>implements Foo, Bar</c>).</summary>
    internal static IEnumerable<string> BaseClassNames(string raw)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            yield break;
        }

        var texts = node is JsonArray array
            ? array.Select(item => item is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            : [node is JsonValue single && single.TryGetValue<string>(out var one) ? one : null];

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            foreach (var part in StripGenerics(text).Split([',', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = part;
                foreach (var keyword in new[] { "extends ", "implements ", "inherits " })
                {
                    if (name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                        name = name[keyword.Length..].Trim();
                }

                var dot = name.LastIndexOfAny(['.', '$']);
                if (dot >= 0)
                    name = name[(dot + 1)..];
                if (name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_'))
                    yield return name;
            }
        }
    }

    private static string StripGenerics(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '<')
                depth++;
            else if (c == '>')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0)
                result.Append(c);
        }
        return result.ToString();
    }

    private static async Task AddStrutsPairsAsync(
        SqliteConnection conn, TypeGraph.Builder graph, List<string> typeNames, List<string> typeFiles, CancellationToken ct)
    {
        var bySimpleName = IndexBySimpleName(typeNames);
        await using var cmd = conn.CreateCommand();
        var props = SqlFacetExtractorStep.SafeJson("r.properties");
        cmd.CommandText = $"""
            SELECT json_extract({props}, '$.action_type'), json_extract({props}, '$.form_type')
            FROM nodes r
            WHERE r.label = 'Route' AND json_extract({props}, '$.framework') = 'struts'
            ORDER BY r.qualified_name, r.id
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;
            var action = ResolveSimpleName(LastSegment(reader.GetString(0)), bySimpleName, typeFiles, "");
            var form = ResolveSimpleName(LastSegment(reader.GetString(1)), bySimpleName, typeFiles, "");
            if (action >= 0 && form >= 0)
                graph.AddEdge(action, form, StrutsPairWeight);
        }
    }

    private static async Task AddTablesAsync(SqliteConnection conn, TypeGraph.Builder graph, Dictionary<long, int> owner, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        // Sin las tablas de E2: el mapa de dominios no debe cambiar según haya quedado o no en la copia la pasada de un análisis anterior.
        cmd.CommandText = $"""
            SELECT e.source_id, t.name
            FROM edges e JOIN nodes t ON t.id = e.target_id
            WHERE t.label IN ('Table', 'AccessTable') AND {Enrichment.AnalysisGraph.AnalysisGraphLabels.NotDerivedTablePredicate("t")}
              AND {Enrichment.AnalysisGraph.AnalysisGraphLabels.NotDerivedEdgePredicate("e")}
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!owner.TryGetValue(reader.GetInt64(0), out var type))
                continue;
            var name = reader.GetString(1).Trim();
            if (name.Length == 0)
                continue;
            var table = graph.AddNode("table:" + name.ToUpperInvariant(), name, "", "", isVirtual: true);
            graph.AddEdge(type, table, TableWeight);
            graph.AddEdge(table, type, TableWeight);
        }
    }

    private static string LastSegment(string qualified)
    {
        var dot = qualified.LastIndexOfAny(['.', '$']);
        return dot >= 0 ? qualified[(dot + 1)..] : qualified;
    }

    private static Dictionary<string, List<int>> IndexBySimpleName(List<string> typeNames)
    {
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < typeNames.Count; i++)
        {
            if (!index.TryGetValue(typeNames[i], out var list))
                index[typeNames[i]] = list = [];
            list.Add(i);
        }
        return index;
    }

    /// <summary>
    /// El tipo de ese nombre simple: el único que lo lleva o, si hay varios, el de la misma carpeta que <paramref name="fromFile"/>.
    /// Con varios y ninguno en su carpeta no se adivina (-1).
    /// </summary>
    private static int ResolveSimpleName(string name, Dictionary<string, List<int>> bySimpleName, List<string> typeFiles, string fromFile)
    {
        if (!bySimpleName.TryGetValue(name, out var candidates))
            return -1;
        if (candidates.Count == 1)
            return candidates[0];

        var directory = TypeGraph.DirectoryOf(fromFile);
        var local = candidates.Where(i => TypeGraph.DirectoryOf(typeFiles[i]) == directory).ToList();
        return local.Count == 1 ? local[0] : -1;
    }
}
