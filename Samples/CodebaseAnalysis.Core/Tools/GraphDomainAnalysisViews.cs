using System.Globalization;
using System.Text;
using System.Text.Json;
using CodebaseAnalysis.Core.Enrichment.AnalysisGraph;

namespace CodebaseAnalysis.Core.Tools;

/// <summary>
/// Las vistas de <c>graph_domain</c> sobre lo que el análisis deja en el grafo (plan 2026-10-06, E1-E3): <c>subsystems</c> (los
/// subsistemas del mapa de dominios, con sus miembros, entradas, tablas, dependencias y las notas de sus investigaciones) y
/// <c>tables</c> (las tablas del SQL escrito en el código, con quién las lee y quién las escribe y dónde).
/// </summary>
public sealed partial class GraphDomainTool
{
    /// <summary>Caracteres de cada nota de investigación que se enseñan en el detalle de un subsistema.</summary>
    public const int MaxNoteChars = 6000;

    private const string NodeFileExpr = "replace(coalesce(nullif({0}.file_path, ''), {0}.file, ''), '\\', '/')";

    private static string FileOf(string alias) => string.Format(CultureInfo.InvariantCulture, NodeFileExpr, alias);

    // ───────────────────────────── subsystems ─────────────────────────────

    private async Task<string> DomainSubsystemsAsync(string? dbPath, string name, int limit, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return await SubsystemDetailAsync(dbPath, name.Trim(), limit, ct);

        var rows = await _graphDb.SelectAsync(
            $"SELECT s.id AS id, s.name AS name, {SafeProps("s")} AS props, " +
            $"(SELECT COUNT(1) FROM edges d WHERE d.source_id = s.id AND d.type = '{AnalysisGraphLabels.DependsOn}') AS deps_out, " +
            $"(SELECT COUNT(1) FROM edges d WHERE d.target_id = s.id AND d.type = '{AnalysisGraphLabels.DependsOn}') AS deps_in, " +
            $"(SELECT COUNT(1) FROM edges a WHERE a.target_id = s.id AND a.type = '{AnalysisGraphLabels.About}') AS notes " +
            $"FROM nodes s WHERE s.label = '{AnalysisGraphLabels.Subsystem}'", null, dbPath, ct);

        var subsystems = rows.Select(r =>
        {
            JsonElement root = default;
            var hasProps = r.GetValueOrDefault("props") is string json && TryParse(json, out root);
            return (Row: r, Props: hasProps ? root : (JsonElement?)null, Loc: hasProps ? Number(root, "loc") ?? 0 : 0);
        }).OrderByDescending(s => s.Loc).ThenBy(s => $"{s.Row["name"]}", StringComparer.Ordinal).ToList();

        var sb = new StringBuilder($"# Subsistemas del análisis ({subsystems.Count})\n\n");
        if (subsystems.Count == 0)
        {
            sb.AppendLine("*(El grafo no tiene subsistemas: se escriben al descubrir el mapa de dominios durante la preparación de un análisis con `Preparation:Mode` activo)*");
            return sb.ToString();
        }

        sb.AppendLine("| Subsistema | Id | Clase | Ficheros | LOC | Tablas | Depende de / usado por | Notas | Descripción |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var (row, props, loc) in subsystems.Take(Math.Max(limit, 1)))
        {
            var p = props ?? default;
            var has = props.HasValue;
            sb.AppendLine($"| **{Cell(row["name"])}** | `{Cell(has ? Text(p, "subsystem_id") : "")}` | {Cell(has ? Text(p, "kind") : "-")} | " +
                          $"{Fmt(has ? Int(p, "files") : null)} | {loc.ToString("0", CultureInfo.InvariantCulture)} | {(has ? Strings(p, "tables").Count : 0)} | " +
                          $"{row["deps_out"]} / {row["deps_in"]} | {row["notes"]} | {Cell(has ? Text(p, "description") : "")} |");
        }
        if (subsystems.Count > limit)
            sb.AppendLine($"\n*(Se muestran {limit} de {subsystems.Count}: sube `limit` para ver más)*");
        sb.AppendLine("\n> Detalle de uno (miembros, entradas, tablas, dependencias y notas): `graph_domain` con `view: subsystems` y `name` = su nombre o id. " +
                      "Las dependencias salen de las aristas de código entre sus ficheros: son acoplamiento, no una prueba de uso.");
        return sb.ToString();
    }

    private async Task<string> SubsystemDetailAsync(string? dbPath, string name, int limit, CancellationToken ct)
    {
        var matches = await _graphDb.SelectAsync(
            $"SELECT s.id AS id, s.name AS name, s.qualified_name AS qn, {SafeProps("s")} AS props FROM nodes s " +
            $"WHERE s.label = '{AnalysisGraphLabels.Subsystem}' AND (lower(s.name) = lower(@n) OR s.qualified_name = @qn " +
            $"OR json_extract({SafeProps("s")}, '$.subsystem_id') = @n) ORDER BY s.name",
            p =>
            {
                p.AddWithValue("@n", name);
                p.AddWithValue("@qn", AnalysisGraphLabels.SubsystemQualifiedName(name));
            }, dbPath, ct);
        if (matches.Count == 0)
        {
            matches = await _graphDb.SelectAsync(
                $"SELECT s.id AS id, s.name AS name, s.qualified_name AS qn, {SafeProps("s")} AS props FROM nodes s " +
                $"WHERE s.label = '{AnalysisGraphLabels.Subsystem}' AND lower(s.name) LIKE '%' || lower(@n) || '%' ORDER BY s.name LIMIT 20",
                p => p.AddWithValue("@n", name), dbPath, ct);
        }

        if (matches.Count == 0)
            return $"# Subsistema `{name}`\n\n*(Ningún subsistema se llama así ni tiene ese id: lista los que hay con `view: subsystems` sin `name`)*\n";
        if (matches.Count > 1)
        {
            var list = new StringBuilder($"# Subsistema `{name}`\n\nCasa con {matches.Count} subsistemas; repite con el nombre exacto o el id:\n\n");
            foreach (var m in matches)
                list.AppendLine($"- **{Cell(m["name"])}** (`{Cell(m["qn"])}`)");
            return list.ToString();
        }

        var node = matches[0];
        var id = Convert.ToInt64(node["id"], CultureInfo.InvariantCulture);
        JsonElement props = default;
        var hasProps = node["props"] is string json && TryParse(json, out props);

        var sb = new StringBuilder($"# Subsistema **{Cell(node["name"])}**\n\n");
        if (hasProps)
        {
            sb.AppendLine($"- Id: `{Text(props, "subsystem_id")}` · clase: {Text(props, "kind") ?? "-"} · nombre {(Text(props, "name_origin") == "llm" ? "dado por el LLM" : "derivado del código")}");
            if (Text(props, "description") is { Length: > 0 } description)
                sb.AppendLine($"- Descripción: {description}");
            sb.AppendLine($"- Tamaño: {Fmt(Int(props, "files"))} fichero(s), {Fmt(Number(props, "loc"))} líneas de producción");
            if (Strings(props, "roots") is { Count: > 0 } roots)
                sb.AppendLine($"- Raíces: {string.Join(", ", roots.Select(r => $"`{r}`"))}");
            if (Text(props, "dominant_term") is { Length: > 0 } term)
                sb.AppendLine($"- Término dominante: `{term}`");
            if (Strings(props, "key_types") is { Count: > 0 } keyTypes)
                sb.AppendLine($"- Tipos clave: {string.Join(", ", keyTypes.Take(limit).Select(t => $"`{t}`"))}");
        }
        sb.AppendLine();

        await AppendEntryPointsAsync(sb, dbPath, id, hasProps ? props : null, limit, ct);
        await AppendMembersAsync(sb, dbPath, id, limit, ct);
        await AppendSubsystemTablesAsync(sb, dbPath, id, hasProps ? props : null, ct);
        await AppendDependenciesAsync(sb, dbPath, id, limit, ct);
        await AppendNotesAsync(sb, dbPath, id, ct);

        sb.AppendLine("> Son datos del grafo del último análisis. Para afirmar un hecho, abre la línea con `view_file`; las notas de investigación las escribió el LLM y llevan sus citas.");
        return sb.ToString();
    }

    private async Task AppendEntryPointsAsync(StringBuilder sb, string? dbPath, long id, JsonElement? props, int limit, CancellationToken ct)
    {
        var routes = await _graphDb.SelectAsync(
            "SELECT r.name AS name, r.qualified_name AS qn FROM edges c JOIN nodes r ON r.id = c.target_id " +
            $"WHERE c.source_id = @id AND c.type = '{AnalysisGraphLabels.Contains}' AND r.label = 'Route' ORDER BY r.name LIMIT @lim",
            p =>
            {
                p.AddWithValue("@id", id);
                p.AddWithValue("@lim", limit);
            }, dbPath, ct);
        var entries = props is { } p1 ? Strings(p1, "entry_points") : [];
        var total = props is { } p2 ? Int(p2, "entry_points_total") ?? entries.Count : entries.Count;

        sb.AppendLine($"## Puntos de entrada ({total})\n");
        if (entries.Count == 0 && routes.Count == 0)
        {
            sb.AppendLine("*(Sin puntos de entrada registrados)*\n");
            return;
        }
        foreach (var entry in entries.Take(limit))
            sb.AppendLine($"- {Cell(entry)}");
        if (routes.Count > 0)
            sb.AppendLine($"- Rutas que atienden sus clases: {string.Join(", ", routes.Select(r => $"`{Cell(r["name"])}`"))}");
        sb.AppendLine();
    }

    private async Task AppendMembersAsync(StringBuilder sb, string? dbPath, long id, int limit, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            $"SELECT m.label AS label, m.name AS name, m.qualified_name AS qn, {FileOf("m")} AS file, " +
            "coalesce(nullif(m.line_start, 0), m.start_line, 0) AS line, " +
            $"CAST(json_extract({SafeProps("m")}, '$.fan_in') AS INTEGER) AS fan_in, " +
            $"(SELECT COUNT(1) FROM edges c2 WHERE c2.source_id = @id AND c2.type = '{AnalysisGraphLabels.Contains}') AS total " +
            $"FROM edges c JOIN nodes m ON m.id = c.target_id WHERE c.source_id = @id AND c.type = '{AnalysisGraphLabels.Contains}' AND m.label <> 'Route' " +
            "ORDER BY coalesce(fan_in, -1) DESC, m.label, m.name LIMIT @lim",
            p =>
            {
                p.AddWithValue("@id", id);
                p.AddWithValue("@lim", limit);
            }, dbPath, ct);

        var total = rows.Count > 0 ? Convert.ToInt32(rows[0]["total"], CultureInfo.InvariantCulture) : 0;
        sb.AppendLine($"## Miembros principales ({(total > rows.Count ? $"{rows.Count} de {total}" : rows.Count.ToString(CultureInfo.InvariantCulture))}, por fan-in)\n");
        if (rows.Count == 0)
        {
            sb.AppendLine("*(Ningún nodo del grafo en sus ficheros)*\n");
            return;
        }
        sb.AppendLine("| Etiqueta | Nombre | Qualified Name | Archivo:línea | Fan-in |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| `{r["label"]}` | **{Cell(r["name"])}** | `{Cell(r["qn"])}` | {Location(r["file"], r["line"])} | {r["fan_in"] ?? "-"} |");
        sb.AppendLine();
    }

    private async Task AppendSubsystemTablesAsync(StringBuilder sb, string? dbPath, long id, JsonElement? props, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            $"SELECT t.name AS name, SUM(CASE WHEN e.type = '{AnalysisGraphLabels.Queries}' THEN 1 ELSE 0 END) AS reads, " +
            $"SUM(CASE WHEN e.type = '{AnalysisGraphLabels.Modifies}' THEN 1 ELSE 0 END) AS writes " +
            "FROM edges e JOIN nodes src ON src.id = e.source_id JOIN nodes t ON t.id = e.target_id " +
            $"WHERE e.type IN ('{AnalysisGraphLabels.Queries}', '{AnalysisGraphLabels.Modifies}') " +
            $"AND {FileOf("src")} IN (SELECT {FileOf("m")} FROM edges c JOIN nodes m ON m.id = c.target_id " +
            $"WHERE c.source_id = @id AND c.type = '{AnalysisGraphLabels.Contains}') " +
            "GROUP BY t.name ORDER BY writes DESC, reads DESC, t.name",
            p => p.AddWithValue("@id", id), dbPath, ct);

        sb.AppendLine($"## Tablas ({rows.Count})\n");
        if (rows.Count == 0)
        {
            var fromMap = props is { } p ? Strings(p, "tables") : [];
            sb.AppendLine(fromMap.Count > 0
                ? $"*(Sin aristas QUERIES/MODIFIES desde sus ficheros; el mapa de dominios le asigna: {string.Join(", ", fromMap.Select(t => $"`{t}`"))})*\n"
                : "*(Sin tablas en el SQL de sus ficheros)*\n");
            return;
        }
        sb.AppendLine("| Tabla | Métodos/clases que leen | Que escriben |");
        sb.AppendLine("|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| `{Cell(r["name"])}` | {r["reads"]} | {r["writes"]} |");
        sb.AppendLine("\n> Quién lee y quién escribe cada una, con ruta:línea: `graph_domain` con `view: tables` y `name` = la tabla.\n");
    }

    private async Task AppendDependenciesAsync(StringBuilder sb, string? dbPath, long id, int limit, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            $"SELECT 'out' AS dir, o.name AS name, {CbmGraphDb.SafeEdgeProperties} AS props, json_extract({CbmGraphDb.SafeEdgeProperties}, '$.weight') AS weight " +
            $"FROM edges e JOIN nodes o ON o.id = e.target_id WHERE e.source_id = @id AND e.type = '{AnalysisGraphLabels.DependsOn}' " +
            "UNION ALL " +
            $"SELECT 'in' AS dir, o.name AS name, {CbmGraphDb.SafeEdgeProperties} AS props, json_extract({CbmGraphDb.SafeEdgeProperties}, '$.weight') AS weight " +
            $"FROM edges e JOIN nodes o ON o.id = e.source_id WHERE e.target_id = @id AND e.type = '{AnalysisGraphLabels.DependsOn}' " +
            "ORDER BY dir DESC, weight DESC, name",
            p => p.AddWithValue("@id", id), dbPath, ct);

        foreach (var (dir, title) in new[] { ("out", "Depende de"), ("in", "Usado por") })
        {
            var part = rows.Where(r => $"{r["dir"]}" == dir).ToList();
            sb.AppendLine($"## {title} ({part.Count})\n");
            if (part.Count == 0)
            {
                sb.AppendLine("*(Ninguno)*\n");
                continue;
            }
            sb.AppendLine("| Subsistema | Aristas de código | Por tipo |");
            sb.AppendLine("|---|---|---|");
            foreach (var r in part.Take(limit))
            {
                var via = r["props"] is string json && TryParse(json, out var root)
                    ? string.Join(", ", Counts(root, "via").OrderByDescending(v => v.Value).Select(v => $"{v.Key} {v.Value}"))
                    : "";
                sb.AppendLine($"| **{Cell(r["name"])}** | {r["weight"]} | {Cell(via)} |");
            }
            sb.AppendLine();
        }
    }

    private async Task AppendNotesAsync(StringBuilder sb, string? dbPath, long id, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            $"SELECT n.name AS name, {SafeProps("n")} AS props FROM edges a JOIN nodes n ON n.id = a.source_id " +
            $"WHERE a.target_id = @id AND a.type = '{AnalysisGraphLabels.About}' AND n.label = '{AnalysisGraphLabels.InvestigationNote}' ORDER BY n.name",
            p => p.AddWithValue("@id", id), dbPath, ct);

        sb.AppendLine($"## Notas de investigación ({rows.Count})\n");
        if (rows.Count == 0)
        {
            sb.AppendLine("*(Ningún informe ha investigado este subsistema en un análisis con notas guardadas)*\n");
            return;
        }
        foreach (var r in rows)
        {
            if (r["props"] is not string json || !TryParse(json, out var p))
                continue;
            var text = Text(p, "text") ?? "";
            var truncated = p.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
            sb.AppendLine($"### {Cell(r["name"])}\n");
            sb.AppendLine($"Informe `{Text(p, "report")}` · escritas {Text(p, "written_at")} · {Fmt(Int(p, "citations_resolved"))} cita(s) ancladas en el código, " +
                          $"{Fmt(Int(p, "citations_unresolved"))} sin nodo{(truncated ? " · **la investigación se cortó: notas incompletas**" : "")}\n");
            sb.AppendLine(text.Length > MaxNoteChars ? text[..MaxNoteChars] + $"\n\n*(Nota recortada: {text.Length} caracteres en total)*" : text);
            sb.AppendLine();
        }
    }

    // ───────────────────────────── tables ─────────────────────────────

    private async Task<string> DomainTablesAsync(string? dbPath, string name, int limit, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return await TableDetailAsync(dbPath, name.Trim(), limit, ct);

        var rows = await _graphDb.SelectAsync(
            $"SELECT t.name AS name, t.label AS label, {SafeProps("t")} AS props, " +
            $"SUM(CASE WHEN e.type = '{AnalysisGraphLabels.Queries}' THEN 1 ELSE 0 END) AS readers, " +
            $"SUM(CASE WHEN e.type = '{AnalysisGraphLabels.Modifies}' THEN 1 ELSE 0 END) AS writers, " +
            "(SELECT COUNT(1) FROM nodes x WHERE x.label IN ('Table', 'AccessTable')) AS total " +
            $"FROM nodes t LEFT JOIN edges e ON e.target_id = t.id AND e.type IN ('{AnalysisGraphLabels.Queries}', '{AnalysisGraphLabels.Modifies}') " +
            "WHERE t.label IN ('Table', 'AccessTable') GROUP BY t.id ORDER BY (readers + writers) DESC, t.name LIMIT @lim",
            p => p.AddWithValue("@lim", Math.Max(limit, 1)), dbPath, ct);

        var total = rows.Count > 0 ? Convert.ToInt32(rows[0]["total"], CultureInfo.InvariantCulture) : 0;
        var sb = new StringBuilder($"# Tablas ({(total > rows.Count ? $"{rows.Count} de {total}" : rows.Count.ToString(CultureInfo.InvariantCulture))})\n\n");
        if (rows.Count == 0)
        {
            sb.AppendLine("*(El grafo no tiene tablas: ni el indexador las trae ni el SQL escrito en las cadenas del código nombra ninguna)*");
            return sb.ToString();
        }

        sb.AppendLine("| Tabla | Esquema | Métodos/clases que leen | Que escriben | Origen |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in rows)
        {
            var schemas = r["props"] is string json && TryParse(json, out var p) ? string.Join(", ", Strings(p, "schemas")) : "";
            var origin = r["props"] is string j2 && TryParse(j2, out var p2) && Text(p2, "source") == "sql_literal" ? "SQL en el código" : $"grafo ({r["label"]})";
            sb.AppendLine($"| `{Cell(r["name"])}` | {Cell(schemas.Length > 0 ? schemas : "-")} | {r["readers"]} | {r["writers"]} | {origin} |");
        }
        sb.AppendLine("\n> Quién lee y quién escribe una, con ruta:línea: `graph_domain` con `view: tables` y `name` = la tabla.");
        return sb.ToString();
    }

    private async Task<string> TableDetailAsync(string? dbPath, string name, int limit, CancellationToken ct)
    {
        // «esquema.tabla» o entre comillas: se busca por el último tramo.
        var bare = name.Trim('`', '"', '[', ']');
        bare = bare[(bare.LastIndexOf('.') + 1)..].Trim('"', '[', ']', '`');

        var tables = await _graphDb.SelectAsync(
            "SELECT t.id AS id, t.name AS name FROM nodes t WHERE t.label IN ('Table', 'AccessTable') AND upper(t.name) = upper(@n) ORDER BY t.id",
            p => p.AddWithValue("@n", bare), dbPath, ct);
        if (tables.Count == 0)
            return $"# Tabla `{bare}`\n\n*(No hay ninguna tabla con ese nombre en el grafo: lista las que hay con `view: tables` sin `name`, o búscala en el código con `grep_search`)*\n";

        var ids = string.Join(", ", tables.Select(t => Convert.ToInt64(t["id"], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
        var rows = await _graphDb.SelectAsync(
            $"SELECT e.type AS type, src.label AS label, src.name AS name, src.qualified_name AS qn, {CbmGraphDb.SafeEdgeProperties} AS props, " +
            $"(SELECT s.name FROM edges c JOIN nodes s ON s.id = c.source_id JOIN nodes m ON m.id = c.target_id " +
            $"WHERE c.type = '{AnalysisGraphLabels.Contains}' AND s.label = '{AnalysisGraphLabels.Subsystem}' AND {FileOf("m")} = {FileOf("src")} LIMIT 1) AS subsystem " +
            $"FROM edges e JOIN nodes src ON src.id = e.source_id WHERE e.target_id IN ({ids}) " +
            $"AND e.type IN ('{AnalysisGraphLabels.Queries}', '{AnalysisGraphLabels.Modifies}') ORDER BY e.type DESC, src.qualified_name",
            null, dbPath, ct);

        var sb = new StringBuilder($"# Tabla `{Cell(tables[0]["name"])}`\n\n");
        foreach (var (type, title) in new[] { (AnalysisGraphLabels.Modifies, "Escriben (MODIFIES)"), (AnalysisGraphLabels.Queries, "Leen (QUERIES)") })
        {
            var part = rows.Where(r => $"{r["type"]}" == type).ToList();
            sb.AppendLine($"## {title}: {(part.Count > limit ? $"{limit} de {part.Count}" : part.Count.ToString(CultureInfo.InvariantCulture))}\n");
            if (part.Count == 0)
            {
                sb.AppendLine("*(Ninguno)*\n");
                continue;
            }
            sb.AppendLine("| Etiqueta | Nombre | Qualified Name | Ruta:línea | Sentencias | Subsistema |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var r in part.Take(limit))
            {
                string where = "—", statements = "";
                if (r["props"] is string json && TryParse(json, out var p))
                {
                    var lines = p.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array
                        ? l.EnumerateArray().Where(x => x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToList()
                        : [];
                    var shown = string.Join(", ", lines.Take(10)) + (lines.Count > 10 ? $" (+{lines.Count - 10})" : "");
                    where = Text(p, "file") is { Length: > 0 } file ? $"`{file}:{shown}`" : "—";
                    statements = string.Join(", ", Strings(p, "statements"));
                    if (Text(p, "anchored_to") == "file")
                        statements += " · fuera de un método o clase del grafo";
                }
                sb.AppendLine($"| `{r["label"]}` | **{Cell(r["name"])}** | `{Cell(r["qn"])}` | {where} | {Cell(statements)} | {Cell(r["subsystem"] ?? "-")} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("> Sale del escaneo determinista del SQL escrito en literales del código (no ve SQL de ficheros .xml o .sql ni el que se compone en tiempo de ejecución). " +
                      "La línea es la del literal donde empieza la sentencia: ábrela con `view_file` antes de afirmarlo.");
        return sb.ToString();
    }

    // ───────────────────────────── findings ─────────────────────────────

    /// <summary>Hallazgos de detalle que se desarrollan cuando el filtro deja pocos.</summary>
    public const int MaxFindingDetails = 5;

    private static readonly string[] SeverityOrder = ["P0", "P1", "P2", "P3"];

    // El símbolo del hallazgo es «Tipo.Miembro»; un filtro por el tipo (o por el miembro exacto) lo casa sin comodines de LIKE.
    private static string SymbolFilter(string propsExpr) =>
        $"(json_extract({propsExpr}, '$.symbol') = @sym OR substr(json_extract({propsExpr}, '$.symbol'), 1, length(@sym) + 1) = @sym || '.')";

    private async Task<string> DomainFindingsAsync(string? dbPath, string name, string severity, string kind, int limit, CancellationToken ct)
    {
        var props = SafeProps("f");
        var conditions = new List<string> { $"f.label = '{AnalysisGraphLabels.Finding}'", $"coalesce(json_extract({props}, '$.stale'), 0) = 0" };
        if (!string.IsNullOrWhiteSpace(severity))
            conditions.Add($"json_extract({props}, '$.severity') = @sev");
        if (!string.IsNullOrWhiteSpace(kind))
            conditions.Add($"json_extract({props}, '$.kind') = @kind");
        if (!string.IsNullOrWhiteSpace(name))
            conditions.Add(SymbolFilter(props));
        var where = string.Join(" AND ", conditions);

        void Bind(Microsoft.Data.Sqlite.SqliteParameterCollection p)
        {
            if (!string.IsNullOrWhiteSpace(severity))
                p.AddWithValue("@sev", severity.Trim().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(kind))
                p.AddWithValue("@kind", kind.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(name))
                p.AddWithValue("@sym", name.Trim());
        }

        var rows = await _graphDb.SelectAsync($"SELECT f.id AS id, f.name AS name, {props} AS props FROM nodes f WHERE {where}", Bind, dbPath, ct);
        var stale = Convert.ToInt32((await _graphDb.SelectAsync(
            $"SELECT COUNT(1) AS n FROM nodes f WHERE f.label = '{AnalysisGraphLabels.Finding}' AND coalesce(json_extract({props}, '$.stale'), 0) <> 0",
            null, dbPath, ct)).FirstOrDefault()?["n"] ?? 0, CultureInfo.InvariantCulture);

        var findings = new List<(long Id, JsonElement Props)>();
        foreach (var r in rows)
        {
            if (r["props"] is string json && TryParse(json, out var root))
                findings.Add((Convert.ToInt64(r["id"], CultureInfo.InvariantCulture), root));
        }
        findings = [.. findings
            .OrderBy(f => SeverityRank(Text(f.Props, "severity")))
            .ThenBy(f => Text(f.Props, "finding_id"), StringComparer.Ordinal)];

        var filters = string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(severity) ? null : $"severidad {severity.Trim().ToUpperInvariant()}",
            string.IsNullOrWhiteSpace(kind) ? null : $"clase {kind.Trim().ToLowerInvariant()}",
            string.IsNullOrWhiteSpace(name) ? null : $"símbolo `{name.Trim()}`"
        }.OfType<string>());
        var sb = new StringBuilder($"# Hallazgos del registro ({findings.Count}{(filters.Length > 0 ? " · " + filters : "")})\n\n");
        if (findings.Count == 0)
        {
            sb.AppendLine("*(Ningún hallazgo del registro con ese filtro: el registro lo escribe la pasada compartida, que está desactivada por defecto)*");
            return sb.ToString();
        }

        sb.AppendLine("| Id | Sev. | Clase | Categoría | Título | Símbolo | Ancla | Estado | Conf. |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var (_, p) in findings.Take(Math.Max(limit, 1)))
        {
            var anchor = Text(p, "file") is { Length: > 0 } file ? $"`{file}{(Int(p, "start_line") is { } l ? ":" + l.ToString(CultureInfo.InvariantCulture) : "")}`" : "—";
            var reverified = p.TryGetProperty("reverified", out var rv) && rv.ValueKind == JsonValueKind.True ? " · reverificado" : "";
            sb.AppendLine($"| {Cell(Text(p, "finding_id"))} | {Cell(Text(p, "severity") ?? "—")} | {Cell(Text(p, "kind"))} | {Cell(Text(p, "category"))} | " +
                          $"{Cell(Text(p, "title"))} | {Cell(Text(p, "symbol") ?? "—")} | {anchor} | {Cell(Text(p, "status"))}{reverified} | {Fmt(Number(p, "confidence"))} |");
        }
        if (findings.Count > limit)
            sb.AppendLine($"\n*(Se muestran {limit} de {findings.Count}: sube `limit` o filtra por `severity`, `kind` o `name`)*");

        if (findings.Count <= MaxFindingDetails)
        {
            foreach (var (id, p) in findings)
                await AppendFindingDetailAsync(sb, dbPath, id, p, ct);
        }

        if (stale > 0)
            sb.AppendLine($"\n*({stale} hallazgo(s) de ejecuciones anteriores marcados obsoletos (`stale`) no se muestran)*");
        sb.AppendLine("\n> Los escribió el LLM en la pasada compartida: la severidad sale de una rúbrica y el ancla (`ruta:línea`) es lo que hay que abrir con `view_file` antes de afirmarlos. " +
                      "No son aristas del código.");
        return sb.ToString();
    }

    private static int SeverityRank(string? severity)
    {
        var i = Array.IndexOf(SeverityOrder, severity ?? "");
        return i >= 0 ? i : SeverityOrder.Length;
    }

    private async Task AppendFindingDetailAsync(StringBuilder sb, string? dbPath, long id, JsonElement p, CancellationToken ct)
    {
        sb.AppendLine($"\n### {Cell(Text(p, "finding_id"))} · {Cell(Text(p, "title"))}\n");
        sb.AppendLine($"- Clase `{Text(p, "kind")}` · categoría `{Text(p, "category")}` · severidad **{Text(p, "severity") ?? "—"}** · estado {Text(p, "status")}" +
                      (Text(p, "reject_reason") is { Length: > 0 } reject ? $" ({reject})" : ""));
        if (Text(p, "severity_basis") is { Length: > 0 } basis)
            sb.AppendLine($"- Base de la severidad: {basis}");
        if (Text(p, "rationale") is { Length: > 0 } rationale)
            sb.AppendLine($"- Mecanismo: {rationale}");

        var anchors = await _graphDb.SelectAsync(
            $"SELECT {SafeProps("a")} AS props FROM edges e JOIN nodes a ON a.id = e.target_id WHERE e.source_id = @id AND e.type = '{AnalysisGraphLabels.At}' ORDER BY a.qualified_name",
            q => q.AddWithValue("@id", id), dbPath, ct);
        foreach (var row in anchors)
        {
            if (row["props"] is not string json || !TryParse(json, out var a))
                continue;
            var range = Int(a, "start_line") is { } s ? $":{s}{(Int(a, "end_line") is { } e && e != s ? "-" + e : "")}" : "";
            var validated = a.TryGetProperty("validated", out var v) && v.ValueKind == JsonValueKind.True ? "cita validada" : "cita sin validar";
            sb.AppendLine($"- Ancla ({Text(a, "role")}, {validated}): `{Text(a, "path")}{range}` `{Cell(Text(a, "quote"))}`");
        }
    }

    // ───────────────────────────── llm_contracts ─────────────────────────────

    private async Task<string> DomainLlmContractsAsync(string? dbPath, string name, int limit, CancellationToken ct)
    {
        var props = SafeProps("m");
        var hasName = !string.IsNullOrWhiteSpace(name);
        var filter = hasName
            ? "AND (m.qualified_name = @n OR substr(m.qualified_name, -length(@n) - 1) = '.' || @n " +
              "OR instr(m.qualified_name, '.' || @n || '.') > 0 OR instr(m.qualified_name, '.' || @n || '(') > 0)"
            : "";
        void Bind(Microsoft.Data.Sqlite.SqliteParameterCollection p)
        {
            if (hasName)
                p.AddWithValue("@n", name.Trim());
        }

        var rows = await _graphDb.SelectAsync(
            $"SELECT m.label AS label, m.name AS name, m.qualified_name AS qn, {FileOf("m")} AS file, coalesce(nullif(m.line_start, 0), m.start_line, 0) AS line, {props} AS props " +
            $"FROM nodes m WHERE json_extract({props}, '$.llm_does') IS NOT NULL {filter} ORDER BY m.qualified_name LIMIT @lim",
            p =>
            {
                Bind(p);
                p.AddWithValue("@lim", Math.Max(limit, 1));
            }, dbPath, ct);
        var total = Convert.ToInt32((await _graphDb.SelectAsync(
            $"SELECT COUNT(1) AS n FROM nodes m WHERE json_extract({props}, '$.llm_does') IS NOT NULL {filter}", Bind, dbPath, ct)).FirstOrDefault()?["n"] ?? 0,
            CultureInfo.InvariantCulture);

        var shown = total > rows.Count ? $"{rows.Count} de {total}" : total.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder($"# Contratos de métodos escritos por el LLM ({shown}{(hasName ? $" · `{name.Trim()}`" : "")})\n\n");
        if (rows.Count == 0)
        {
            sb.AppendLine("*(Ningún método tiene contrato del LLM con ese filtro: los escribe la pasada compartida, desactivada por defecto)*");
            return sb.ToString();
        }

        if (!hasName)
        {
            sb.AppendLine("| Etiqueta | Qualified Name | Archivo:línea | Qué hace |");
            sb.AppendLine("|---|---|---|---|");
        }
        foreach (var r in rows)
        {
            if (r["props"] is not string json || !TryParse(json, out var p))
                continue;
            var stale = p.TryGetProperty("llm_stale", out var s) && s.ValueKind == JsonValueKind.True ? " · **obsoleto (stale)**" : "";
            var does = Text(p, "llm_does") ?? "";
            if (!hasName)
            {
                sb.AppendLine($"| `{r["label"]}` | `{Cell(r["qn"])}` | {Location(r["file"], r["line"])} | {Cell(does.Length > 160 ? does[..160] + "…" : does)}{stale} |");
                continue;
            }
            sb.AppendLine($"### `{Cell(r["qn"])}` · {Location(r["file"], r["line"])}{stale}\n");
            sb.AppendLine($"- Qué hace: {does}");
            sb.AppendLine($"- Recibe: {Text(p, "llm_inputs")}");
            sb.AppendLine($"- Devuelve: {Text(p, "llm_outputs")}");
            sb.AppendLine($"- Efectos: {Text(p, "llm_side_effects")}");
            sb.AppendLine($"- Lanza: {Text(p, "llm_throws")}");
            sb.AppendLine($"- Procedencia: ejecución `{Text(p, "llm_run")}`{(Text(p, "llm_batch") is { Length: > 0 } b ? $", lote `{b}`" : "")}\n");
        }
        sb.AppendLine("\n> Lo escribió el LLM leyendo el código del lote, no lo calculó un analizador: confirma con `view_file` antes de afirmarlo.");
        return sb.ToString();
    }

    private static string Location(object? file, object? line)
    {
        var f = file?.ToString();
        if (string.IsNullOrWhiteSpace(f))
            return "—";
        var l = line?.ToString();
        return string.IsNullOrEmpty(l) || l == "0" ? $"`{f}`" : $"`{f}:{l}`";
    }

    private static string Cell(object? value) => (value?.ToString() ?? "").Replace("|", "\\|").ReplaceLineEndings(" ");
}
