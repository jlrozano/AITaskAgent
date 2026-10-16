using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.Extensions.Logging;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

/// <summary>
/// Vecinos de un nodo del grafo por sus aristas: llamantes y llamados (<c>CALLS</c>), jerarquía (<c>INHERITS</c>,
/// <c>IMPLEMENTS</c>), métodos de un tipo (<c>DEFINES_METHOD</c>), rutas (<c>HANDLES</c>), las aristas Struts/JSP y los usos
/// de tipos y campos del analizador Java (<c>USES_TYPE</c>, <c>READS</c>, <c>WRITES</c>), y las del análisis (tablas, subsistemas y
/// notas de investigación: <c>QUERIES</c>, <c>MODIFIES</c>, <c>CONTAINS</c>, <c>DEPENDS_ON</c>, <c>ABOUT</c>, <c>CITES</c>), con un filtro por línea del fichero
/// de origen. Es de solo lectura y no lee el código: lo que devuelve es del grafo del último análisis. Cada arista dice su
/// procedencia: resuelta por un enriquecedor (<c>enriched_by</c>), estimada por el fallback del LLM, de la ingesta de Office (sin marca,
/// con su nodo de origen escrito por OfficeTools), heurística de cbm (sin marca; las <c>CALLS</c> de cbm traen
/// <c>strategy</c> y <c>confidence</c>) o de estructura de cbm (pertenencia), y la cabecera avisa si hay aristas sin resolver.
/// </summary>
public sealed class GraphNeighborsTool : LlmTool
{
    public const string ToolName = "graph_neighbors";

    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>Candidatos que se listan cuando el nombre casa con varios nodos.</summary>
    public const int MaxCandidates = 20;

    /// <summary>Líneas de una arista que se enseñan; el resto se resume.</summary>
    public const int MaxEdgeLines = 10;

    /// <summary>Tipos de arista por defecto: los que sirven para seguir el código y las pantallas.</summary>
    public static IReadOnlyList<string> DefaultEdgeTypes { get; } =
    [
        "CALLS", "INHERITS", "IMPLEMENTS", "DEFINES_METHOD", "HANDLES",
        "FORWARDS_TO", "SUBMITS_TO", "INCLUDES", "RENDERS", "EXTENDS_DEFINITION",
        "USES_TYPE", "READS", "WRITES",
        Enrichment.AnalysisGraph.AnalysisGraphLabels.Queries, Enrichment.AnalysisGraph.AnalysisGraphLabels.Modifies,
        Enrichment.AnalysisGraph.AnalysisGraphLabels.Cites, Enrichment.AnalysisGraph.AnalysisGraphLabels.Contains,
        Enrichment.AnalysisGraph.AnalysisGraphLabels.DependsOn, Enrichment.AnalysisGraph.AnalysisGraphLabels.About
    ];

    /// <summary>
    /// Marcas de aristas cuyo contenido decidió un LLM: el enriquecimiento semántico (estimadas) y las notas de investigación (la
    /// arista <c>CITES</c> ancla una cita de forma determinista, pero lo que dice la nota es del modelo). No cuentan como resueltas.
    /// </summary>
    private static readonly string[] LlmMarks =
    [
        Steps.SemanticEnrichmentStep.EnricherName, Enrichment.AnalysisGraph.AnalysisGraphLabels.NoteEnricher,
        Enrichment.AnalysisGraph.AnalysisGraphLabels.LlmRegistryEnricher
    ];

    /// <summary>
    /// Aristas de cbm que salen de la estructura del código (pertenencia) y no de decidir un destino por nombre: sin marca de
    /// enriquecedor siguen siendo fiables. El resto de aristas de cbm sin marca son heurísticas.
    /// </summary>
    public static IReadOnlySet<string> StructuralEdgeTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "DEFINES_METHOD", "DEFINES", "CONTAINS_FILE", "CONTAINS_FOLDER", "CONTAINS_PACKAGE", "MEMBER_OF"
    };

    // Marca de enriquecedor en la arista; json_each falla con JSON inválido, de ahí el CASE.
    private static string MarkPredicate(string condition = "") =>
        $"EXISTS (SELECT 1 FROM json_each(CASE WHEN json_valid(properties) THEN properties ELSE '{{}}' END, '$.enriched_by') m{condition})";

    private static readonly string ResolvedPredicate = MarkPredicate($" WHERE m.value NOT IN ({string.Join(", ", LlmMarks.Select(m => $"'{m}'"))})");

    // Las aristas de la ingesta de Office no llevan marca: se reconocen por el origin de su nodo de origen, que OfficeTools
    // escribe en sus nodos de código (DetectAndIndexGraphStep.OfficeOrigins).
    private static readonly string OfficeOriginList =
        string.Join(", ", Steps.DetectAndIndexGraphStep.OfficeOrigins.Select(o => $"'{o}'"));

    private static string OriginExpr(string alias) =>
        $"json_extract(CASE WHEN json_valid({alias}.properties) THEN {alias}.properties END, '$.origin')";

    private static readonly string OfficeSourcePredicate =
        $"EXISTS (SELECT 1 FROM nodes s WHERE s.id = edges.source_id AND {OriginExpr("s")} IN ({OfficeOriginList}))";

    // Nodos de estructura (carpetas y ficheros): solo se eligen si ningún otro candidato casa igual de bien.
    private const string StructuralLabels = "'File', 'Folder', 'Project'";

    private const string FileExpr = "coalesce(nullif(n.file_path, ''), n.file)";
    private const string LineExpr = "coalesce(nullif(n.line_start, 0), n.start_line)";

    // Tipo interfaz que declara el método n (por DEFINES_METHOD); null si no lo es. Va en la misma consulta.
    private const string InterfaceExpr =
        "(SELECT p.qualified_name FROM edges d JOIN nodes p ON p.id = d.source_id " +
        "WHERE d.target_id = n.id AND d.type = 'DEFINES_METHOD' AND p.label = 'Interface' LIMIT 1)";

    private readonly CbmGraphDb _graphDb;

    public GraphNeighborsTool(string? defaultDbPath = null)
    {
        _graphDb = new CbmGraphDb(defaultDbPath);
    }

    public override string Name => ToolName;
    public override string Description =>
        "Vecinos de un nodo del grafo de código por sus aristas: quién llama a X y a quién llama X (CALLS, con las líneas de cada llamada), " +
        "qué hereda o implementa y quién lo hereda o implementa (INHERITS, IMPLEMENTS), qué métodos define un tipo (DEFINES_METHOD), " +
        "qué clase atiende una ruta (HANDLES) y, en Struts/JSP, qué JSP o definición Tiles muestra una acción (FORWARDS_TO), " +
        "a qué acción envía una JSP (SUBMITS_TO), qué incluye (INCLUDES) y qué JSP compone una definición (RENDERS, EXTENDS_DEFINITION). " +
        "En Java, además, qué tipos usa un método, un campo o un tipo (USES_TYPE, con el contexto: parámetro, retorno, local, cast, new…) " +
        "y qué campos lee o escribe (READS, WRITES), con sus líneas. " +
        "Dónde se usa una clase o un campo: direction 'in' sobre la clase (USES_TYPE y CALLS entrantes) o sobre el campo (READS y WRITES entrantes). " +
        "Qué referencia hay en la línea N de un fichero: 'line' con el método que la contiene y direction 'out'. " +
        "Del análisis: qué tablas lee o escribe un método o una clase (QUERIES, MODIFIES, con sus líneas), a qué subsistema pertenece un tipo " +
        "(CONTAINS entrante), de qué subsistemas depende uno (DEPENDS_ON) y qué notas de investigación citan un nodo (CITES entrante, con la cita " +
        "y la frase de la nota; la nota la escribió el LLM). " +
        "Cada arista dice su procedencia: resuelta por un enriquecedor (fiable), heurística de cbm (una pista, con su confianza si la trae) o de estructura. " +
        "Devuelve datos del grafo, no el código: para confirmar un hecho, abre la línea que indica con view_file.";
    public override bool IsReadOnly => true;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "qualified_name": {
          "type": "string",
          "description": "Qualified Name del nodo, exacto o un fragmento (p. ej. 'Clase.metodo'). Si casa con varios nodos, se listan los candidatos para que repitas la llamada con el exacto."
        },
        "direction": {
          "type": "string",
          "enum": ["out", "in", "both"],
          "description": "'out': aristas que salen del nodo (a quién llama, de quién hereda, a qué JSP reenvía); 'in': las que llegan (quién lo llama, quién lo hereda o implementa); 'both' (por defecto)."
        },
        "edge_types": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Tipos de arista a incluir. Por defecto: CALLS, INHERITS, IMPLEMENTS, DEFINES_METHOD, HANDLES, FORWARDS_TO, SUBMITS_TO, INCLUDES, RENDERS, EXTENDS_DEFINITION, USES_TYPE, READS, WRITES, QUERIES, MODIFIES, CITES, CONTAINS, DEPENDS_ON, ABOUT."
        },
        "line": {
          "type": "integer",
          "description": "Solo las aristas que tienen esa línea entre sus líneas. Es una línea del fichero de origen de la arista: con direction 'out', la del propio nodo (las llamadas, los tipos y los campos que usa un método en esa línea)."
        },
        "limit": {
          "type": "integer",
          "description": "Número máximo de vecinos (por defecto 50, máximo 200)."
        }
      },
      "required": ["qualified_name"]
    }
    """);

    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var dbPath = context.Get<string>(AnalysisContextKeys.CbmDbPath);
        string? qualifiedName = null;
        var direction = "both";
        var edgeTypes = new List<string>();
        var limit = DefaultLimit;
        int? line = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("qualified_name", out var q) && q.ValueKind == JsonValueKind.String)
                qualifiedName = q.GetString()?.Trim();
            if (root.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String
                && d.GetString()?.Trim().ToLowerInvariant() is "out" or "in" or "both")
                direction = d.GetString()!.Trim().ToLowerInvariant();
            if (root.TryGetProperty("edge_types", out var t))
            {
                // Lista o, si el modelo la manda como texto, separada por comas.
                IEnumerable<string?> raw = t.ValueKind switch
                {
                    JsonValueKind.Array => t.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()),
                    JsonValueKind.String => (t.GetString() ?? "").Split(',', ';'),
                    _ => []
                };
                edgeTypes.AddRange(raw.Select(s => s?.Trim().ToUpperInvariant()).Where(s => !string.IsNullOrEmpty(s)).Distinct()!);
            }
            if (root.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lInt))
                limit = lInt;
            // Número o, si el modelo lo manda como texto, un entero en texto.
            if (root.TryGetProperty("line", out var n))
            {
                var parsed = n.ValueKind switch
                {
                    JsonValueKind.Number when n.TryGetInt32(out var number) => number,
                    JsonValueKind.String when int.TryParse(n.GetString(), out var text) => text,
                    _ => 0
                };
                if (parsed > 0)
                    line = parsed;
            }
        }
        catch
        {
            // Defaults
        }

        if (string.IsNullOrWhiteSpace(qualifiedName))
            return "# Vecinos en el grafo\n\n*(Falta `qualified_name`: indica el nodo, p. ej. el que devuelve `graph_search`)*\n";

        if (edgeTypes.Count == 0)
            edgeTypes.AddRange(DefaultEdgeTypes);
        limit = Math.Clamp(limit, 1, MaxLimit);

        var candidates = await ResolveAsync(qualifiedName, dbPath, cancellationToken);
        if (candidates.Count == 0)
            return $"# Vecinos en el grafo de `{qualifiedName}`\n\n*(Ningún nodo tiene un Qualified Name que coincida; búscalo con `graph_search`)*\n";
        if (candidates.Count > 1)
            return Ambiguous(qualifiedName, candidates);

        var node = candidates[0];
        var nodeId = Convert.ToInt64(node["id"]);
        var (sql, bind) = NeighborsQuery(nodeId, direction, edgeTypes, limit, line);
        var rows = await _graphDb.SelectAsync(sql, bind, dbPath, cancellationToken);
        var (countSql, countBind) = CountQuery(nodeId, direction, edgeTypes, line);
        var counts = (await _graphDb.SelectAsync(countSql, countBind, dbPath, cancellationToken))
            .ToDictionary(r => ($"{r["dir"]}", $"{r["type"]}"),
                r => new EdgeCount(Convert.ToInt32(r["n"]), Convert.ToInt32(r["resolved"] ?? 0), Convert.ToInt32(r["llm"] ?? 0),
                    Convert.ToInt32(r["office"] ?? 0)));

        return Render(node, direction, edgeTypes, limit, line, rows, counts);
    }

    /// <summary>
    /// Nodos que casan, del mejor nivel: el QN exacto; si no, los que acaban en <c>.fragmento</c> (o
    /// <c>.fragmento(…)</c>, una sobrecarga) con el nombre del último tramo; después los que solo acaban así; y por
    /// último los que lo contienen. Dentro del nivel, ficheros y carpetas solo si no hay otra cosa.
    /// </summary>
    private async Task<List<IReadOnlyDictionary<string, object?>>> ResolveAsync(string input, string? dbPath, CancellationToken ct)
    {
        var escaped = EscapeLike(input);
        var bare = input.Contains('(') ? input[..input.IndexOf('(')] : input;
        var last = bare[(bare.LastIndexOf('.') + 1)..];

        var sql =
            $"SELECT n.id, n.label, n.name, n.qualified_name AS qn, {FileExpr} AS file_path, {LineExpr} AS line_start, " +
            "CASE WHEN n.qualified_name = @qn THEN 1 " +
            "WHEN (n.qualified_name LIKE @suf ESCAPE '\\' OR n.qualified_name LIKE @ovl ESCAPE '\\') AND n.name = @last THEN 2 " +
            "WHEN n.qualified_name LIKE @suf ESCAPE '\\' OR n.qualified_name LIKE @ovl ESCAPE '\\' THEN 3 ELSE 4 END AS tier, " +
            $"CASE WHEN n.label IN ({StructuralLabels}) THEN 1 ELSE 0 END AS structural, " +
            $"{InterfaceExpr} AS iface " +
            $"FROM nodes n WHERE n.qualified_name LIKE @any ESCAPE '\\' AND NOT {Enrichment.AnalysisGraph.AnalysisGraphLabels.LlmNodePredicate("n")} " +
            "ORDER BY tier, structural, n.qualified_name LIMIT @cap";

        var rows = await _graphDb.SelectAsync(sql, p =>
        {
            p.AddWithValue("@qn", input);
            p.AddWithValue("@suf", "%." + escaped);
            p.AddWithValue("@ovl", "%." + escaped + "(%");
            p.AddWithValue("@last", last);
            p.AddWithValue("@any", "%" + escaped + "%");
            p.AddWithValue("@cap", MaxCandidates + 1);
        }, dbPath, ct);

        if (rows.Count == 0)
            return [];
        var best = (Tier: rows[0]["tier"]?.ToString(), Structural: rows[0]["structural"]?.ToString());
        return [.. rows.Where(r => r["tier"]?.ToString() == best.Tier && r["structural"]?.ToString() == best.Structural)];
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // Filtro de línea: la arista lleva @line en su lista lines, que son líneas del fichero de su origen.
    private static string LinePredicate(string alias) =>
        $" AND EXISTS (SELECT 1 FROM json_each(CASE WHEN json_valid({alias}properties) THEN {alias}properties ELSE '{{}}' END, '$.lines') ln WHERE ln.value = @line)";

    private static (string Sql, Action<Microsoft.Data.Sqlite.SqliteParameterCollection> Bind) NeighborsQuery(
        long nodeId, string direction, List<string> edgeTypes, int limit, int? line)
    {
        var types = string.Join(", ", edgeTypes.Select((_, i) => $"@t{i}"));
        var lineFilter = line.HasValue ? LinePredicate("e.") : "";
        string Part(string dir, string self, string other) =>
            $"SELECT '{dir}' AS dir, e.type AS type, n.id AS id, n.label AS label, n.name AS name, n.qualified_name AS qn, " +
            $"{FileExpr} AS file_path, {LineExpr} AS line_start, " +
            "CASE WHEN json_valid(e.properties) THEN e.properties END AS props, " +
            $"CASE WHEN e.type = 'CALLS' THEN {InterfaceExpr} END AS iface, " +
            $"(SELECT {OriginExpr("s")} FROM nodes s WHERE s.id = e.source_id) AS source_origin " +
            $"FROM edges e JOIN nodes n ON n.id = e.{other} WHERE e.{self} = @id AND e.type IN ({types}){lineFilter} " +
            // Los nodos que escribió el LLM en la pasada compartida (hallazgos, anclas, resúmenes…) solo los ven las vistas findings y llm_contracts.
            $"AND NOT {Enrichment.AnalysisGraph.AnalysisGraphLabels.LlmNodePredicate("n")}";

        var parts = new List<string>();
        if (direction is "out" or "both")
            parts.Add(Part("out", "source_id", "target_id"));
        if (direction is "in" or "both")
            parts.Add(Part("in", "target_id", "source_id"));

        // Salientes antes que entrantes. Si hay más de las que caben, lo dice el recuento de CountQuery.
        var sql = string.Join(" UNION ALL ", parts) + " ORDER BY dir DESC, type, qn LIMIT @lim";
        return (sql, p =>
        {
            p.AddWithValue("@id", nodeId);
            for (var i = 0; i < edgeTypes.Count; i++)
                p.AddWithValue($"@t{i}", edgeTypes[i]);
            p.AddWithValue("@lim", limit);
            if (line.HasValue)
                p.AddWithValue("@line", line.Value);
        });
    }

    // Por la misma razón que en NeighborsQuery, los recuentos no cuentan las aristas hacia nodos escritos por el LLM.
    private static string NotLlmEnd(string column) =>
        $"NOT EXISTS (SELECT 1 FROM nodes o WHERE o.id = edges.{column} AND {Enrichment.AnalysisGraph.AnalysisGraphLabels.LlmNodePredicate("o")})";

    private static (string Sql, Action<Microsoft.Data.Sqlite.SqliteParameterCollection> Bind) CountQuery(
        long nodeId, string direction, List<string> edgeTypes, int? line)
    {
        var types = string.Join(", ", edgeTypes.Select((_, i) => $"@t{i}"));
        var lineFilter = line.HasValue ? LinePredicate("") : "";
        // Por procedencia: resueltas por un enriquecedor, estimadas por el LLM (solo su marca), de la ingesta de Office (sin
        // marca, con el nodo de origen de OfficeTools) y, el resto, de cbm.
        var provenance = $"SUM(CASE WHEN {ResolvedPredicate} THEN 1 ELSE 0 END) AS resolved, " +
                         $"SUM(CASE WHEN {MarkPredicate()} AND NOT {ResolvedPredicate} THEN 1 ELSE 0 END) AS llm, " +
                         $"SUM(CASE WHEN NOT {MarkPredicate()} AND {OfficeSourcePredicate} THEN 1 ELSE 0 END) AS office";
        var parts = new List<string>();
        if (direction is "out" or "both")
            parts.Add($"SELECT 'out' AS dir, type, COUNT(1) AS n, {provenance} FROM edges WHERE source_id = @id AND type IN ({types}){lineFilter} AND {NotLlmEnd("target_id")} GROUP BY type");
        if (direction is "in" or "both")
            parts.Add($"SELECT 'in' AS dir, type, COUNT(1) AS n, {provenance} FROM edges WHERE target_id = @id AND type IN ({types}){lineFilter} AND {NotLlmEnd("source_id")} GROUP BY type");
        return (string.Join(" UNION ALL ", parts), p =>
        {
            p.AddWithValue("@id", nodeId);
            for (var i = 0; i < edgeTypes.Count; i++)
                p.AddWithValue($"@t{i}", edgeTypes[i]);
            if (line.HasValue)
                p.AddWithValue("@line", line.Value);
        });
    }

    private static string Ambiguous(string input, List<IReadOnlyDictionary<string, object?>> candidates)
    {
        var sb = new StringBuilder($"# Vecinos en el grafo de `{input}`\n\n");
        var more = candidates.Count > MaxCandidates;
        sb.AppendLine($"`{input}` casa con {(more ? $"más de {MaxCandidates}" : candidates.Count.ToString())} nodos. " +
                      "No se muestran vecinos: repite la llamada con el `qualified_name` exacto de uno de ellos.");
        sb.AppendLine();
        sb.AppendLine("| Etiqueta | Nombre | Qualified Name | Archivo:línea |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in candidates.Take(MaxCandidates))
            sb.AppendLine($"| `{c["label"]}` | **{Cell(c["name"])}** | `{Cell(c["qn"])}` | {Location(c)} |");
        if (more)
            sb.AppendLine($"\n*(Hay más candidatos: concreta el fragmento)*");
        return sb.ToString();
    }

    private static string Render(
        IReadOnlyDictionary<string, object?> node,
        string direction,
        List<string> edgeTypes,
        int limit,
        int? line,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Dictionary<(string Dir, string Type), EdgeCount> counts)
    {
        var qn = $"{node["qn"]}";
        var nodeFile = node["file_path"]?.ToString();
        var total = counts.Values.Sum(c => c.Total);

        var sb = new StringBuilder($"# Vecinos en el grafo de `{qn}`\n\n");
        sb.AppendLine($"- Nodo: `{node["label"]}` **{Cell(node["name"])}** · {Location(node)}");
        sb.AppendLine($"- Dirección: `{direction}` · Tipos: {string.Join(", ", edgeTypes.Select(t => $"`{t}`"))} · {(total == 1 ? "1 arista" : $"{total} aristas")}.");
        if (line.HasValue)
            sb.AppendLine($"- Línea: {line} del fichero de origen de cada arista ({(direction == "out" ? $"`{nodeFile}`" : "el del nodo si sale, el del vecino si llega")}).");
        AppendProvenanceHeader(sb, rows, counts);
        if (node["iface"] is { } ownIface && direction is "in" or "both")
            sb.AppendLine($"\n> Es un método de la interfaz `{ownIface}`: quien llama a la interfaz llega aquí; las implementaciones se ven con `{ToolName}` sobre `{ownIface}`, `direction: in`, `edge_types: [\"IMPLEMENTS\"]`.");
        sb.AppendLine();

        if (rows.Count == 0)
        {
            sb.AppendLine(line.HasValue
                ? $"*(El nodo no tiene aristas de esos tipos en esa dirección con la línea {line})*"
                : "*(El nodo no tiene aristas de esos tipos en esa dirección)*");
            return sb.ToString();
        }

        foreach (var group in rows.GroupBy(r => ($"{r["dir"]}", $"{r["type"]}")))
        {
            var (dir, type) = group.Key;
            var shown = group.Count();
            var all = counts.TryGetValue(group.Key, out var count) ? count.Total : shown;
            var arrow = dir == "out" ? $"`{Short(qn)}` → vecino" : $"vecino → `{Short(qn)}`";
            sb.AppendLine($"## `{type}` {(dir == "out" ? "salientes" : "entrantes")} ({arrow}): {(all > shown ? $"{shown} de {all}" : $"{shown}")}");
            sb.AppendLine();
            sb.AppendLine("| Etiqueta | Nombre | Qualified Name | Archivo:línea | Procedencia | Detalle |");
            sb.AppendLine("|---|---|---|---|---|---|");

            var interfaces = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var r in group)
            {
                // Las líneas de una arista son del fichero de su origen: el nodo si sale, el vecino si llega.
                var sourceFile = dir == "out" ? nodeFile : r["file_path"]?.ToString();
                var detail = Detail(type, r["props"]?.ToString(), sourceFile);
                if (r["iface"] is { } iface)
                {
                    interfaces.Add($"{iface}");
                    detail = string.IsNullOrEmpty(detail) ? "método de interfaz" : detail + " · método de interfaz";
                }
                var origin = Provenance.Of(type, r["props"]?.ToString(), r["source_origin"]?.ToString());
                sb.AppendLine($"| `{r["label"]}` | **{Cell(r["name"])}** | `{Cell(r["qn"])}` | {Location(r)} | {Cell(origin.Text)} | {Cell(detail)} |");
            }
            foreach (var iface in interfaces)
                sb.AppendLine($"\n> Llamada a un método de la interfaz `{iface}`: las implementaciones se ven con `{ToolName}` sobre `{iface}`, `direction: in`, `edge_types: [\"IMPLEMENTS\"]`.");
            sb.AppendLine();
        }

        var shownTotal = rows.Count;
        if (total > shownTotal)
            sb.AppendLine($"> Salida truncada: se muestran {shownTotal} de {total} aristas (límite {limit}). Filtra con `direction` o `edge_types`, o sube `limit` (máximo {MaxLimit}).");

        return sb.ToString();
    }

    /// <summary>
    /// Aristas de un tipo y dirección: todas, las resueltas por un enriquecedor, las estimadas por el LLM y las de la ingesta
    /// de Office.
    /// </summary>
    private sealed record EdgeCount(int Total, int Resolved, int Llm, int Office);

    private enum ProvenanceKind { Resolved, Llm, Office, CbmHeuristic, CbmStructural }

    /// <summary>Procedencia de una arista, leída de sus propiedades, y el texto de su columna.</summary>
    private sealed record Provenance(ProvenanceKind Kind, string Text, IReadOnlyList<string> Enrichers)
    {
        public static Provenance Of(string type, string? props, string? sourceOrigin = null)
        {
            List<string> marks = [];
            string? strategy = null, confidence = null;
            if (!string.IsNullOrWhiteSpace(props))
            {
                try
                {
                    using var doc = JsonDocument.Parse(props);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("enriched_by", out var by))
                        {
                            if (by.ValueKind == JsonValueKind.Array)
                                marks.AddRange(by.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
                            else if (by.ValueKind == JsonValueKind.String)
                                marks.Add(by.GetString()!);
                        }
                        strategy = GraphNeighborsTool.Text(root, "strategy");
                        if (root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                            confidence = c.GetDouble().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                catch (JsonException)
                {
                }
            }

            var withConfidence = confidence != null ? $" · confianza {confidence}" : "";
            var resolvedBy = marks.Where(m => !LlmMarks.Contains(m)).ToList();
            if (resolvedBy.Count > 0)
                return new(ProvenanceKind.Resolved, $"resuelta · {string.Join(", ", resolvedBy)}{withConfidence}", resolvedBy);
            if (marks.Contains(Enrichment.AnalysisGraph.AnalysisGraphLabels.NoteEnricher))
                return new(ProvenanceKind.Llm, "nota de investigación (LLM) · cita anclada", marks);
            if (marks.Count > 0)
                return new(ProvenanceKind.Llm, $"estimada por LLM · {marks[0]}", marks);
            // OfficeTools decide el destino de una llamada VBA por el nombre del procedimiento: no es de cbm, y tampoco está resuelta.
            if (sourceOrigin != null && Steps.DetectAndIndexGraphStep.OfficeOrigins.Contains(sourceOrigin))
                return new(ProvenanceKind.Office, $"ingesta Office · `{sourceOrigin}`", []);
            if (StructuralEdgeTypes.Contains(type))
                return new(ProvenanceKind.CbmStructural, "estructura cbm", []);
            return new(ProvenanceKind.CbmHeuristic, $"heurística cbm{(strategy != null ? $" · `{strategy}`" : "")}{withConfidence}", []);
        }
    }

    /// <summary>
    /// Recuento por procedencia de todas las aristas pedidas (no solo las mostradas) y, si hay heurísticas de cbm o
    /// estimadas por el LLM, el aviso de que son pistas. Los nombres de los enriquecedores salen de las filas mostradas.
    /// </summary>
    private static void AppendProvenanceHeader(
        StringBuilder sb,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Dictionary<(string Dir, string Type), EdgeCount> counts)
    {
        if (counts.Count == 0)
            return;

        var resolved = counts.Values.Sum(c => c.Resolved);
        var llm = counts.Values.Sum(c => c.Llm);
        var office = counts.Values.Sum(c => c.Office);
        var cbmStructural = counts.Where(c => StructuralEdgeTypes.Contains(c.Key.Type))
            .Sum(c => c.Value.Total - c.Value.Resolved - c.Value.Llm - c.Value.Office);
        var cbmHeuristic = counts.Values.Sum(c => c.Total - c.Resolved - c.Llm - c.Office) - cbmStructural;
        var enrichers = rows
            .SelectMany(r => Provenance.Of($"{r["type"]}", r["props"]?.ToString(), r["source_origin"]?.ToString()) is { Kind: ProvenanceKind.Resolved } p ? p.Enrichers : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var parts = new List<string>();
        if (resolved > 0)
            parts.Add($"{resolved} {(resolved == 1 ? "resuelta" : "resueltas")} por enriquecedor{(enrichers.Count > 0 ? $" ({string.Join(", ", enrichers)})" : "")}");
        if (cbmHeuristic > 0)
            parts.Add($"{cbmHeuristic} {(cbmHeuristic == 1 ? "heurística" : "heurísticas")} de cbm");
        if (llm > 0)
            parts.Add($"{llm} {(llm == 1 ? "estimada" : "estimadas")} por el LLM");
        if (office > 0)
            parts.Add($"{office} de la ingesta de Office");
        if (cbmStructural > 0)
            parts.Add($"{cbmStructural} de estructura de cbm");
        sb.AppendLine($"- Procedencia: {string.Join(" · ", parts)}.");

        var unresolved = cbmHeuristic + llm + office;
        if (unresolved > 0)
            sb.AppendLine($"\n> Aviso: {(unresolved == 1 ? "1 arista no está resuelta" : $"{unresolved} aristas no están resueltas")} " +
                          "(heurística de cbm, que decide el destino por nombre, " +
                          (office > 0 ? "ingesta de Office, que también lo decide por el nombre del procedimiento, " : "") +
                          "o estimación del LLM). Son pistas: confírmalas en el código antes de afirmarlas. Las resueltas por un enriquecedor son fiables.");
    }

    /// <summary>
    /// Lo que aporta la arista: líneas y número de llamadas, los contextos de un uso de tipo, el acceso a un campo, el forward,
    /// el rol en la definición o la ruta.
    /// </summary>
    private static string Detail(string type, string? props, string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(props))
            return "";

        JsonDocument doc;
        try { doc = JsonDocument.Parse(props); }
        catch (JsonException) { return ""; }
        using var owned = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return "";

        var parts = new List<string>();
        switch (type)
        {
            case "CALLS":
                if (root.TryGetProperty("count", out var count) && count.TryGetInt32(out var n))
                    parts.Add(n == 1 ? "1 llamada" : $"{n} llamadas");
                if (root.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.String && ctx.GetString() == "initializer")
                    parts.Add("desde un inicializador");
                break;
            case "USES_TYPE":
                if (Strings(root, "contexts") is { Count: > 0 } contexts)
                    parts.Add("como " + string.Join(", ", contexts.Select(x => $"`{x}`")));
                if (root.TryGetProperty("count", out var uses) && uses.TryGetInt32(out var u))
                    parts.Add(u == 1 ? "1 referencia" : $"{u} referencias");
                break;
            case "READS":
            case "WRITES":
                parts.Add(type == "READS" ? "lectura" : "escritura");
                if (root.TryGetProperty("count", out var accesses) && accesses.TryGetInt32(out var a))
                    parts.Add(a == 1 ? "1 acceso" : $"{a} accesos");
                break;
            case "FORWARDS_TO":
                if (Strings(root, "forward_names") is { Count: > 0 } names)
                    parts.Add("forward " + string.Join(", ", names.Select(x => $"`{x}`")));
                else if (Text(root, "forward_name") is { } forward)
                    parts.Add($"forward `{forward}`");
                if (root.TryGetProperty("redirect", out var redirect) && redirect.ValueKind == JsonValueKind.True)
                    parts.Add("redirect");
                break;
            case "RENDERS":
                if (Strings(root, "roles") is { Count: > 0 } roles)
                    parts.Add("rol " + string.Join(", ", roles.Select(x => $"`{x}`")));
                else if (Text(root, "role") is { } role)
                    parts.Add($"rol `{role}`");
                break;
            case "HANDLES":
                if (Text(root, "http_method") is { } verb)
                    parts.Add($"`{verb}`");
                if (Text(root, "route_path") is { } path)
                    parts.Add($"`{path}`");
                break;
            case "QUERIES":
            case "MODIFIES":
                parts.Add(type == "QUERIES" ? "lee la tabla" : "escribe en la tabla");
                if (Strings(root, "statements") is { Count: > 0 } statements)
                    parts.Add(string.Join(", ", statements));
                break;
            case "CITES":
                if (Text(root, "report") is { } report)
                    parts.Add($"informe `{report}`");
                if (Strings(root, "refs") is { Count: > 0 } refs)
                    parts.Add("cita " + string.Join(", ", refs.Take(MaxEdgeLines).Select(x => $"`{x}`")));
                if (Strings(root, "excerpts") is { Count: > 0 } excerpts)
                    parts.Add($"«{excerpts[0]}»");
                break;
            case "DEPENDS_ON":
                if (root.TryGetProperty("weight", out var weight) && weight.TryGetInt32(out var w))
                    parts.Add(w == 1 ? "1 arista de código" : $"{w} aristas de código");
                break;
            case "CONTAINS":
                if (Text(root, "member") is { } member)
                    parts.Add(member switch { "route" => "ruta que atiende", "file" => "fichero", _ => "miembro" });
                break;
        }

        if (root.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            var numbers = lines.EnumerateArray().Where(x => x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToList();
            if (numbers.Count > 0)
            {
                var listed = string.Join(", ", numbers.Take(MaxEdgeLines));
                if (numbers.Count > MaxEdgeLines)
                    listed += $" (+{numbers.Count - MaxEdgeLines})";
                var where = string.IsNullOrWhiteSpace(sourceFile) ? "del origen" : $"de `{sourceFile}`";
                parts.Add($"{(numbers.Count == 1 ? "línea" : "líneas")} {listed} {where}");
            }
        }

        return string.Join(" · ", parts);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    private static List<string>? Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : null;

    private static string Location(IReadOnlyDictionary<string, object?> row)
    {
        var file = row.GetValueOrDefault("file_path")?.ToString();
        if (string.IsNullOrWhiteSpace(file))
            return "—";
        var line = row.GetValueOrDefault("line_start")?.ToString();
        return string.IsNullOrEmpty(line) || line == "0" ? $"`{file}`" : $"`{file}:{line}`";
    }

    /// <summary>El QN sin el prefijo de proyecto y ruta, para los encabezados: los dos últimos tramos.</summary>
    private static string Short(string qn)
    {
        var bare = qn.Contains('(') ? qn[..qn.IndexOf('(')] : qn;
        var dot = bare.LastIndexOf('.');
        var prev = dot > 0 ? bare.LastIndexOf('.', dot - 1) : -1;
        return prev >= 0 ? qn[(prev + 1)..] : qn;
    }

    private static string Cell(object? value) => (value?.ToString() ?? "").Replace("|", "\\|").ReplaceLineEndings(" ");
}
