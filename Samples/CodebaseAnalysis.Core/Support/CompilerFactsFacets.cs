using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodebaseAnalysis.Core.Steps;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Las facetas que consumen lo que los enriquecedores de Roslyn y javac escriben en el grafo (tanda 5 de la auditoría del
/// 2026-09-30): <c>detected_issues</c>, código muerto, campos, DI, autenticación de rutas, TODO y código comentado. Todas
/// resumen: en un repositorio grande hay miles de hallazgos y de candidatos, y el prompt recibe los totales y los más
/// relevantes con su cita <c>ruta:línea</c>, no la lista entera. Lo que sale son candidatos que el auditor verifica contra el
/// código.
/// </summary>
internal static partial class CompilerFactsFacets
{
    // ------------------------------------------------------------------ reglas de detected_issues

    /// <summary>Peso de cada regla: 3 las que suelen ser un defecto, 2 las dudosas, 1 las de higiene. Una regla desconocida pesa 2.</summary>
    private static readonly Dictionary<string, int> RuleWeights = new(StringComparer.Ordinal)
    {
        ["null-deref"] = 3, ["null-return-deref"] = 3, ["unawaited-task"] = 3, ["undisposed-resource"] = 3, ["empty-catch"] = 3,
        ["blocking-async"] = 3, ["async-void"] = 3, ["captive-dependency"] = 3,
        ["string-identity"] = 2, ["unreachable-code"] = 2,
        ["obsolete-api"] = 1, ["unused-field"] = 1
    };

    private const int DefaultWeight = 2;

    /// <summary>Hallazgos que salen por regla según su peso: las graves tienen más cupo.</summary>
    private static int Quota(int weight) => weight switch { >= 3 => 8, 2 => 5, _ => 3 };

    /// <summary>Hallazgos de una misma regla en un mismo fichero que salen, para que un fichero no ocupe todo el cupo.</summary>
    private const int PerFileCap = 3;

    /// <summary>Tope de hallazgos en <c>suspect_patterns</c>.</summary>
    private const int MaxShownFindings = 60;

    private const int MaxCaptive = 30;

    private static int WeightOf(string rule) => RuleWeights.GetValueOrDefault(rule, DefaultWeight);

    /// <summary>Suma de pesos de las reglas de un nodo (SQL), con <paramref name="propertiesExpr"/> como JSON válido.</summary>
    private static string IssueScoreSql(string propertiesExpr) =>
        $"(SELECT coalesce(SUM(CASE r.value {string.Join(" ", RuleWeights.Select(w => $"WHEN '{w.Key}' THEN {w.Value}"))} ELSE {DefaultWeight} END), 0) " +
        $"FROM json_each({propertiesExpr}, '$.issue_rules') r)";

    [GeneratedRegex(@"^(?<rule>[a-z0-9-]+): (?<text>.*?)(?: \(línea (?<line>\d+)\))?$", RegexOptions.Singleline)]
    private static partial Regex FindingPattern();

    private sealed record Finding(string Rule, string Text, int Line, string QualifiedName, string Name, string File, double Hotspot);

    private static string Normalize(string? path) => (path ?? "").Replace('\\', '/');

    private static async Task<List<Dictionary<string, object?>>> RowsAsync(SqliteConnection conn, string sql, CancellationToken ct) =>
        await SqlFacetExtractorStep.QueryRowsAsync(conn, sql, ct);

    private static string? Str(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value != null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static double Num(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value != null && double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string Serialize(JsonNode node) => node.ToJsonString(SqlFacetExtractorStep.FacetJsonOptions);

    private static string Cite(string file, int line) => line > 0 ? $"{file}:{line}" : file;

    // ------------------------------------------------------------------ detected_issues

    /// <summary>Resultado de <see cref="IssuesAsync"/>: el resumen, los hallazgos que salen y las dependencias cautivas.</summary>
    public sealed record IssueFacets(string Summary, string Shown, string Captive, int CaptiveCount);

    /// <summary>
    /// <c>detected_issues</c> (<c>"regla: texto (línea N)"</c>) de todos los nodos, resumidos: totales por regla (las graves
    /// primero) y, por regla, los hallazgos de los nodos de mayor hotspot dentro del cupo de su peso. Más las
    /// <c>captive-dependency</c> aparte, que son del informe de DI.
    /// </summary>
    public static async Task<IssueFacets> IssuesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var props = SqlFacetExtractorStep.SafeJson("n.properties");
        var rows = await RowsAsync(conn, $@"
            SELECT n.qualified_name, n.name, n.file, {SqlFacetExtractorStep.StartLineExpr} AS start_line,
                   CAST(json_extract({props}, '$.hotspot') AS REAL) AS hotspot, i.value AS issue
            FROM nodes n, json_each({props}, '$.detected_issues') i
            WHERE json_type({props}, '$.detected_issues') = 'array'
            ORDER BY n.qualified_name, i.key;", ct);

        var findings = new List<Finding>();
        foreach (var row in rows)
        {
            var text = Str(row, "issue") ?? "";
            var match = FindingPattern().Match(text);
            var nodeLine = (int)Num(row, "start_line");
            var rule = match.Success ? match.Groups["rule"].Value : "sin-regla";
            var body = match.Success ? match.Groups["text"].Value : text;
            var line = match.Success && match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) : nodeLine;
            findings.Add(new Finding(rule, body, line, Str(row, "qualified_name") ?? "", Str(row, "name") ?? "", Normalize(Str(row, "file")), Num(row, "hotspot")));
        }

        var byRule = findings.GroupBy(f => f.Rule, StringComparer.Ordinal)
            .Select(g => (Rule: g.Key, Findings: g.Count(), Nodes: g.Select(f => f.QualifiedName).Distinct(StringComparer.Ordinal).Count(), Weight: WeightOf(g.Key)))
            .OrderByDescending(r => r.Weight).ThenByDescending(r => r.Findings).ThenBy(r => r.Rule, StringComparer.Ordinal)
            .ToList();

        var shown = new List<Finding>();
        foreach (var rule in byRule)
        {
            var perFile = new Dictionary<string, int>(StringComparer.Ordinal);
            var taken = 0;
            foreach (var f in findings.Where(f => f.Rule == rule.Rule)
                         .OrderByDescending(f => f.Hotspot).ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line))
            {
                if (taken >= Quota(rule.Weight) || shown.Count >= MaxShownFindings) break;
                perFile.TryGetValue(f.File, out var inFile);
                if (inFile >= PerFileCap) continue;
                perFile[f.File] = inFile + 1;
                shown.Add(f);
                taken++;
            }
        }

        var summary = new JsonObject
        {
            ["total_findings"] = findings.Count,
            ["nodes_with_findings"] = findings.Select(f => f.QualifiedName).Distinct(StringComparer.Ordinal).Count(),
            ["by_rule"] = new JsonArray(byRule.Select(r => (JsonNode?)new JsonObject { ["rule"] = r.Rule, ["findings"] = r.Findings, ["nodes"] = r.Nodes }).ToArray()),
            ["shown_in_suspect_patterns"] = shown.Count,
            ["note"] = "Son candidatos de las reglas del compilador (Roslyn, javac), no pruebas: confirma cada uno leyendo la línea citada. suspect_patterns lista los de los nodos de mayor hotspot dentro de un cupo por regla."
        };

        var shownRows = shown.Select(FindingJson).ToList();
        // Marcas heredadas de otros indexadores (anti_pattern, smell), si algún grafo las trae.
        var legacy = await RowsAsync(conn, $@"
            SELECT n.name, n.qualified_name, n.file, {SqlFacetExtractorStep.StartLineExpr} AS start_line,
                   coalesce(json_extract({props}, '$.anti_pattern'), json_extract({props}, '$.smell')) AS marker
            FROM nodes n
            WHERE json_extract({props}, '$.anti_pattern') IS NOT NULL OR json_extract({props}, '$.smell') IS NOT NULL
            ORDER BY n.file, start_line, n.qualified_name LIMIT 10;", ct);
        foreach (var row in legacy)
        {
            var file = Normalize(Str(row, "file"));
            var line = (int)Num(row, "start_line");
            shownRows.Add(new JsonObject
            {
                ["rule"] = Str(row, "marker"), ["text"] = Str(row, "marker"), ["cite"] = Cite(file, line), ["file"] = file, ["line"] = line,
                ["qualified_name"] = Str(row, "qualified_name"), ["name"] = Str(row, "name")
            });
        }

        var captive = findings.Where(f => f.Rule == "captive-dependency")
            .OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).Take(MaxCaptive)
            .Select(f => new JsonObject
            {
                ["cite"] = Cite(f.File, f.Line), ["file"] = f.File, ["line"] = f.Line, ["qualified_name"] = f.QualifiedName, ["name"] = f.Name, ["text"] = f.Text
            }).ToList();

        return new IssueFacets(
            Serialize(summary),
            shownRows.Count > 0 ? Serialize(new JsonArray(shownRows.ToArray())) : "[]",
            captive.Count > 0 ? Serialize(new JsonArray(captive.Select(c => (JsonNode?)c).ToArray())) : "[]",
            findings.Count(f => f.Rule == "captive-dependency"));
    }

    private static JsonNode FindingJson(Finding f) => new JsonObject
    {
        ["rule"] = f.Rule, ["text"] = f.Text, ["cite"] = Cite(f.File, f.Line), ["file"] = f.File, ["line"] = f.Line,
        ["qualified_name"] = f.QualifiedName, ["name"] = f.Name, ["node_hotspot"] = f.Hotspot > 0 ? f.Hotspot : null
    };

    // ------------------------------------------------------------------ puntos críticos

    private const int CriticalLimit = 30;
    private const int CriticalWithIssuesLimit = 20;

    /// <summary>
    /// Los nodos con hallazgos graves primero (por la suma de pesos de sus reglas y después por hotspot), hasta 20, y los de
    /// mayor hotspot o con <c>throws</c> hasta completar 30. Cada uno con el total de hallazgos y los 8 primeros.
    /// </summary>
    public static async Task<string> CriticalHotspotsAsync(SqliteConnection conn, string repoPath, CancellationToken ct)
    {
        var props = SqlFacetExtractorStep.SafeJson("n.properties");
        string Select(string where, string order, int limit) => $@"
            SELECT * FROM (
                SELECT n.name, n.qualified_name, n.file,
                       {SqlFacetExtractorStep.StartLineExpr} AS start_line,
                       {SqlFacetExtractorStep.EndLineExpr} AS end_line,
                       CAST(json_extract({props}, '$.hotspot') AS REAL) AS hotspot,
                       json_extract({props}, '$.throws') AS throws,
                       json_extract({props}, '$.cyclomatic') AS cyclomatic,
                       (SELECT COUNT(1) FROM json_each({props}, '$.detected_issues')) AS issues_total,
                       (SELECT json_group_array(value) FROM (SELECT value FROM json_each({props}, '$.issue_rules') ORDER BY value)) AS issue_rules,
                       (SELECT json_group_array(value) FROM (SELECT value FROM json_each({props}, '$.detected_issues') LIMIT 8)) AS detected_issues,
                       {IssueScoreSql(props)} AS issue_score
                FROM nodes n) q
            WHERE {where}
            ORDER BY {order}, qualified_name LIMIT {limit};";

        var withIssues = await SqlFacetExtractorStep.QueryRowsAsync(conn,
            Select("issue_score > 0", "issue_score DESC, coalesce(hotspot, 0) DESC", CriticalWithIssuesLimit), ct,
            repositoryPath: repoPath, attachSnippets: true, maxSnippetLines: 70, maxSnippetItems: 6);
        var seen = withIssues.Select(r => Str(r, "qualified_name")).ToHashSet(StringComparer.Ordinal);
        var plain = (await SqlFacetExtractorStep.QueryRowsAsync(conn,
            // throws cuenta si es una lista no vacía: las pasadas anteriores escribían [] en todo método con cuerpo, y
            // json_extract devuelve '[]', no NULL. json_valid porque un valor escalar (texto suelto) no es un array.
            Select("(hotspot > 5 OR (CASE WHEN json_valid(throws) THEN json_array_length(throws) ELSE 0 END) > 0)",
                "coalesce(hotspot, 0) DESC", CriticalLimit * 2), ct,
            repositoryPath: repoPath, attachSnippets: true, maxSnippetLines: 70, maxSnippetItems: 4))
            .Where(r => !seen.Contains(Str(r, "qualified_name")))
            .Take(CriticalLimit - withIssues.Count);

        var all = withIssues.Concat(plain).ToList();
        if (all.Count == 0)
            return "[]";
        foreach (var row in all)
        {
            row.Remove("issue_score");
            foreach (var key in new[] { "issue_rules", "detected_issues" })
                if (Str(row, key) is { } json && JsonNode.Parse(json) is JsonArray array && array.Count > 0)
                    row[key] = array;
                else
                    row.Remove(key);
        }
        return System.Text.Json.JsonSerializer.Serialize(all, SqlFacetExtractorStep.FacetJsonOptions);
    }

    // ------------------------------------------------------------------ código muerto

    private const int DeadCandidatesLimit = 40;
    private const int DeadPrivateLimit = 40;

    private const string DeadReasonPrivate = "método privado sin ninguna referencia";

    private static string DeadBucketSql(string props) => $@"
        CASE
          WHEN EXISTS (SELECT 1 FROM edges d JOIN nodes t ON t.id = d.source_id
                       WHERE d.target_id = n.id AND d.type = 'DEFINES_METHOD'
                         AND json_extract({SqlFacetExtractorStep.SafeJson("t.properties")}, '$.dead_code_candidate') = 1) THEN 'owner'
          WHEN n.label IN ('Method', 'Function') AND json_extract({props}, '$.accessibility') = 'private'
               AND json_extract({props}, '$.dead_code_reason') LIKE '{DeadReasonPrivate}%' THEN 'private'
          WHEN EXISTS (SELECT 1 FROM edges o WHERE o.source_id = n.id AND o.type = 'OVERRIDES') THEN 'override'
          ELSE 'listed' END";

    /// <summary>Resultado de <see cref="DeadCodeAsync"/>.</summary>
    public sealed record DeadCodeFacets(string Candidates, string Summary, string PrivateMethods);

    /// <summary>
    /// Candidatos de <c>dead_code_candidate</c> sin duplicados: los miembros de un tipo candidato no se listan (el tipo lleva
    /// <c>dead_members</c>), los métodos privados sin uso van aparte (el compilador resolvió todas sus referencias) y los que
    /// sobrescriben o implementan un contrato (<c>OVERRIDES</c>) no son candidatos. Por confianza, tipos antes que miembros y
    /// tamaño; con los totales en el resumen.
    /// </summary>
    public static async Task<DeadCodeFacets> DeadCodeAsync(SqliteConnection conn, CancellationToken ct)
    {
        var props = SqlFacetExtractorStep.SafeJson("n.properties");
        var bucket = DeadBucketSql(props);
        var rank = $"CASE json_extract({props}, '$.dead_code_confidence') WHEN 'high' THEN 0 WHEN 'medium' THEN 1 ELSE 2 END";
        var members = $@"(SELECT COUNT(1) FROM edges d JOIN nodes m ON m.id = d.target_id
                          WHERE d.source_id = n.id AND d.type = 'DEFINES_METHOD'
                            AND json_extract({SqlFacetExtractorStep.SafeJson("m.properties")}, '$.dead_code_candidate') = 1)";
        string Inner(string columns) => $@"
            SELECT n.name, n.qualified_name, n.label, n.file, {SqlFacetExtractorStep.StartLineExpr} AS start_line,
                   {SqlFacetExtractorStep.EndLineExpr} AS end_line,
                   json_extract({props}, '$.dead_code_confidence') AS confidence,
                   json_extract({props}, '$.dead_code_reason') AS reason,
                   json_extract({props}, '$.accessibility') AS accessibility,
                   {rank} AS conf_rank, {bucket} AS bucket{columns}
            FROM nodes n WHERE json_extract({props}, '$.dead_code_candidate') = 1";

        var counts = await RowsAsync(conn, $@"
            SELECT bucket, confidence, COUNT(1) AS n FROM ({Inner("")}) GROUP BY bucket, confidence;", ct);
        int Sum(Func<Dictionary<string, object?>, bool> filter) => counts.Where(filter).Sum(r => (int)Num(r, "n"));
        var total = Sum(_ => true);

        var candidates = await RowsAsync(conn, $@"
            SELECT * FROM ({Inner($", {members} AS dead_members")}) WHERE bucket = 'listed'
            ORDER BY conf_rank, CASE label WHEN 'Class' THEN 0 ELSE 1 END, (end_line - start_line) DESC, qualified_name LIMIT {DeadCandidatesLimit};", ct);
        foreach (var row in candidates)
        {
            row.Remove("bucket");
            row.Remove("conf_rank");
            if (Num(row, "dead_members") == 0)
                row.Remove("dead_members");
        }

        var privateMethods = await RowsAsync(conn, $@"
            SELECT * FROM ({Inner("")}) WHERE bucket = 'private' ORDER BY file, start_line, qualified_name LIMIT {DeadPrivateLimit};", ct);
        foreach (var row in privateMethods)
        {
            row.Remove("bucket");
            row.Remove("conf_rank");
        }

        var listed = Sum(r => Str(r, "bucket") == "listed");
        var summary = new JsonObject
        {
            ["total_candidates"] = total,
            ["by_confidence"] = new JsonObject
            {
                ["high"] = Sum(r => Str(r, "confidence") == "high"),
                ["medium"] = Sum(r => Str(r, "confidence") == "medium"),
                ["low"] = Sum(r => Str(r, "confidence") is not ("high" or "medium"))
            },
            ["members_of_candidate_types"] = Sum(r => Str(r, "bucket") == "owner"),
            ["private_unused_methods"] = Sum(r => Str(r, "bucket") == "private"),
            ["overriding_members_excluded"] = Sum(r => Str(r, "bucket") == "override"),
            ["listed_candidates"] = listed,
            ["shown_candidates"] = candidates.Count,
            ["note"] = "total_candidates cuenta todo lo marcado; en dead_code_candidates no se repiten los miembros de un tipo candidato (dead_members), ni los métodos privados sin uso (dead_private_methods), ni los que sobrescriben un contrato. Son candidatos: verifícalos contra el código."
        };

        return new DeadCodeFacets(
            candidates.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(candidates, SqlFacetExtractorStep.FacetJsonOptions) : "[]",
            Serialize(summary),
            privateMethods.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(privateMethods, SqlFacetExtractorStep.FacetJsonOptions) : "[]");
    }

    // ------------------------------------------------------------------ campos

    /// <summary>
    /// Campos privados que nada lee, de los que el analizador casó o creó (llevan <c>field_name</c>; pueden ser <c>Field</c> o,
    /// en las constantes de enum de Java, <c>Variable</c>). Quedan fuera las <c>static final</c> (y las <c>const</c> de C#): una
    /// constante sin leer no guarda estado. Alias <c>n</c> para la tabla de nodos.
    /// </summary>
    public static string UnreadFieldWhere => $@"n.label IN ('Field', 'Variable') AND json_extract({SqlFacetExtractorStep.SafeJson("n.properties")}, '$.field_name') IS NOT NULL
              AND json_extract(n.properties, '$.accessibility') = 'private'
              AND NOT (coalesce(json_extract(n.properties, '$.is_static'), 0) = 1 AND coalesce(json_extract(n.properties, '$.is_final'), 0) = 1)
              AND NOT EXISTS (SELECT 1 FROM edges r WHERE r.target_id = n.id AND r.type = 'READS')";

    private const int WriteOnlyLimit = 30;

    private const string WritesSql = "(SELECT COUNT(1) FROM edges w WHERE w.target_id = n.id AND w.type = 'WRITES')";

    /// <summary>El recuento de campos sin leer y la lista de los que solo se escriben.</summary>
    public static async Task<(string Summary, string WriteOnly)> UnreadFieldsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var counts = await RowsAsync(conn, $@"
            SELECT COUNT(1) AS total, coalesce(SUM(CASE WHEN writes > 0 THEN 1 ELSE 0 END), 0) AS write_only
            FROM (SELECT {WritesSql} AS writes FROM nodes n WHERE {UnreadFieldWhere});", ct);
        var total = (int)Num(counts[0], "total");
        var writeOnly = (int)Num(counts[0], "write_only");

        var rows = await RowsAsync(conn, $@"
            SELECT * FROM (
                SELECT json_extract(n.properties, '$.field_name') AS field_name, n.qualified_name, n.label, n.file,
                       {SqlFacetExtractorStep.StartLineExpr} AS start_line, {SqlFacetExtractorStep.EndLineExpr} AS end_line,
                       json_extract(n.properties, '$.type_fqn') AS type_fqn, {WritesSql} AS writes
                FROM nodes n WHERE {UnreadFieldWhere}) q
            WHERE writes > 0 ORDER BY writes DESC, file, start_line, qualified_name LIMIT {WriteOnlyLimit};", ct);

        var summary = new JsonObject
        {
            ["total_unread"] = total,
            ["never_touched"] = total - writeOnly,
            ["write_only"] = writeOnly,
            ["note"] = "never_touched: ningún método los lee ni los escribe; write_only: alguien los escribe y nadie los lee (estado inútil). Punto ciego: la reflexión, la serialización y los frameworks que usan campos por nombre no dejan READS."
        };
        return (Serialize(summary), rows.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(rows, SqlFacetExtractorStep.FacetJsonOptions) : "[]");
    }

    // ------------------------------------------------------------------ duplicación

    private const int DuplicateLimit = 25;
    private const int DuplicateMinLines = 5;
    private const double DuplicateMinSimilarity = 0.8;

    private static string SpanSql(string alias) =>
        $"(coalesce(nullif({alias}.line_end, 0), {alias}.end_line, 0) - coalesce(nullif({alias}.line_start, 0), {alias}.start_line, 1) + 1)";

    /// <summary>
    /// Pares de métodos casi idénticos que el indexador ya calculó (aristas <c>SIMILAR_TO</c> con su similitud de Jaccard), sin
    /// los cuerpos triviales (menos de <see cref="DuplicateMinLines"/> líneas) ni los de similitud baja: candidatos de duplicación
    /// con las dos ubicaciones, los mayores primero. No es un detector de clones por AST: lo que el indexador no empareja no sale.
    /// </summary>
    public static async Task<(string Summary, string Pairs)> DuplicatesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var from = $@"FROM edges e
            JOIN nodes a ON a.id = e.source_id JOIN nodes b ON b.id = e.target_id
            WHERE e.type = 'SIMILAR_TO' AND a.id <> b.id
              AND CAST(json_extract({SqlFacetExtractorStep.SafeJson("e.properties")}, '$.jaccard') AS REAL) >= {DuplicateMinSimilarity.ToString(System.Globalization.CultureInfo.InvariantCulture)}
              AND {SpanSql("a")} >= {DuplicateMinLines} AND {SpanSql("b")} >= {DuplicateMinLines}";
        var counts = await RowsAsync(conn, $"SELECT COUNT(1) AS total {from};", ct);
        var rows = await RowsAsync(conn, $@"
            SELECT a.name AS name_a, a.qualified_name AS qualified_name_a, a.file AS file_a,
                   coalesce(nullif(a.line_start, 0), a.start_line, 1) AS start_line_a, coalesce(nullif(a.line_end, 0), a.end_line, 0) AS end_line_a,
                   b.name AS name_b, b.qualified_name AS qualified_name_b, b.file AS file_b,
                   coalesce(nullif(b.line_start, 0), b.start_line, 1) AS start_line_b, coalesce(nullif(b.line_end, 0), b.end_line, 0) AS end_line_b,
                   CAST(json_extract({SqlFacetExtractorStep.SafeJson("e.properties")}, '$.jaccard') AS REAL) AS similarity,
                   {SpanSql("a")} AS lines_a, {SpanSql("b")} AS lines_b,
                   coalesce(json_extract({SqlFacetExtractorStep.SafeJson("e.properties")}, '$.same_file'), 0) AS same_file
            {from}
            ORDER BY similarity DESC, lines_a DESC, a.file, a.qualified_name LIMIT {DuplicateLimit};", ct);

        var total = (int)Num(counts[0], "total");
        var summary = new JsonObject
        {
            ["total_pairs"] = total,
            ["shown"] = rows.Count,
            ["min_lines"] = DuplicateMinLines,
            ["min_similarity"] = DuplicateMinSimilarity,
            ["note"] = "Pares de métodos que el indexador marca como casi idénticos (similitud de Jaccard de su huella, 1 = idénticos); candidatos a duplicación que hay que confirmar leyendo el código: una diferencia mínima (un sufijo, un símbolo) no los hace distintos. Solo los detecta el indexador: que un par no figure no prueba que no haya copias."
        };
        return (Serialize(summary), rows.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(rows, SqlFacetExtractorStep.FacetJsonOptions) : "[]");
    }

    // ------------------------------------------------------------------ DI

    /// <summary>
    /// Registros de DI sin consumidor conocido: los que el enriquecedor marca <c>unused_registration</c> (<c>consumer_count = 0</c>
    /// con Roslyn) y, solo si no hay <c>consumer_count</c> ni los consume el framework, los de <c>fan_in = 0</c> de cbm.
    /// </summary>
    public const string UnusedDiWhere = @"label = 'DiRegistration' AND (
                json_extract(properties, '$.unused_registration') = 1
                OR (json_extract(properties, '$.consumer_count') IS NULL
                    AND coalesce(json_extract(properties, '$.consumed_by_framework'), 0) <> 1
                    AND json_extract(properties, '$.fan_in') = 0))";

    /// <summary>Resumen de los registros de DI con las dependencias cautivas que ya contó <see cref="IssuesAsync"/>.</summary>
    public static async Task<string> DiSummaryAsync(SqliteConnection conn, int captiveCount, CancellationToken ct)
    {
        var props = SqlFacetExtractorStep.SafeJson("properties");
        var rows = await RowsAsync(conn, $@"
            SELECT coalesce(json_extract({props}, '$.lifetime'), json_extract({props}, '$.di_lifetime'), 'desconocido') AS lifetime,
                   coalesce(json_extract({props}, '$.unused_registration'), 0) AS unused,
                   coalesce(json_extract({props}, '$.consumed_by_framework'), 0) AS framework,
                   json_extract({props}, '$.consumer_count') AS consumers
            FROM nodes WHERE label = 'DiRegistration';", ct);
        var lifetimes = new JsonObject();
        foreach (var group in rows.GroupBy(r => Str(r, "lifetime") ?? "desconocido").OrderBy(g => g.Key, StringComparer.Ordinal))
            lifetimes[group.Key] = group.Count();
        var summary = new JsonObject
        {
            ["registrations"] = rows.Count,
            ["by_lifetime"] = lifetimes,
            ["unused_registrations"] = rows.Count(r => Num(r, "unused") == 1),
            ["consumed_by_framework"] = rows.Count(r => Num(r, "framework") == 1),
            ["consumer_count_unknown"] = rows.Count(r => Str(r, "consumers") == null),
            ["captive_dependencies"] = captiveCount,
            ["note"] = "consumer_count sale de las referencias exactas de Roslyn al tipo del servicio (sin contar el registro ni las listas de bases); sin él el servicio es externo o lo resuelve el framework. unused_registration es un candidato: una resolución por reflexión o por IServiceProvider no la ve."
        };
        return Serialize(summary);
    }

    // ------------------------------------------------------------------ rutas

    /// <summary>Recuento de rutas por autenticación: <c>auth_inferred</c> distingue «no declara nada» de un <c>[AllowAnonymous]</c> explícito.</summary>
    public static async Task<string> RouteAuthSummaryAsync(SqliteConnection conn, CancellationToken ct)
    {
        var props = SqlFacetExtractorStep.SafeJson("properties");
        var rows = await RowsAsync(conn, $@"
            SELECT COUNT(1) AS total,
                   coalesce(SUM(CASE WHEN auth = 1 THEN 1 ELSE 0 END), 0) AS authenticated,
                   coalesce(SUM(CASE WHEN auth = 0 AND inferred = 1 THEN 1 ELSE 0 END), 0) AS without_declared_auth,
                   coalesce(SUM(CASE WHEN auth = 0 AND coalesce(inferred, 0) <> 1 THEN 1 ELSE 0 END), 0) AS explicit_allow_anonymous,
                   coalesce(SUM(CASE WHEN auth IS NULL THEN 1 ELSE 0 END), 0) AS unknown
            FROM (SELECT json_extract({props}, '$.auth_required') AS auth, json_extract({props}, '$.auth_inferred') AS inferred
                  FROM nodes WHERE label = 'Route');", ct);
        var row = rows[0];
        var summary = new JsonObject
        {
            ["total_routes"] = (int)Num(row, "total"),
            ["authenticated"] = (int)Num(row, "authenticated"),
            ["without_declared_auth"] = (int)Num(row, "without_declared_auth"),
            ["explicit_allow_anonymous"] = (int)Num(row, "explicit_allow_anonymous"),
            ["unknown"] = (int)Num(row, "unknown"),
            ["note"] = "without_declared_auth: la ruta no declara autenticación ni hay política global que la exija (auth_required=false con auth_inferred=true): es un hallazgo a confirmar. explicit_allow_anonymous: [AllowAnonymous] explícito, una decisión del código. unknown: el grafo no tiene el dato."
        };
        return Serialize(summary);
    }

    // ------------------------------------------------------------------ TODO y código comentado

    private const int TopFiles = 15;
    private const int TopTodos = 25;
    private const int TopCommentedBlocks = 20;

    private static string Truncate(string? text, int max)
    {
        var flat = (text ?? "").ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static int KindRank(string kind) => kind switch { "FIXME" => 0, "HACK" => 1, "XXX" => 2, "BUG" => 2, _ => 3 };

    /// <summary>Marcas TODO/FIXME/HACK: totales, por tipo, los ficheros con más y las más graves con su cita.</summary>
    public static async Task<string> TodoMarkersAsync(SqliteConnection conn, CancellationToken ct)
    {
        var props = SqlFacetExtractorStep.SafeJson("n.properties");
        var rows = await RowsAsync(conn, $@"
            SELECT n.file, json_extract(t.value, '$.kind') AS kind, json_extract(t.value, '$.line') AS line, json_extract(t.value, '$.text') AS text
            FROM nodes n, json_each({props}, '$.todo_markers') t
            WHERE json_type({props}, '$.todo_markers') = 'array';", ct);
        var markers = rows.Select(r => (File: Normalize(Str(r, "file")), Kind: (Str(r, "kind") ?? "TODO").ToUpperInvariant(), Line: (int)Num(r, "line"), Text: Str(r, "text") ?? "")).ToList();

        var byKind = new JsonObject();
        foreach (var group in markers.GroupBy(m => m.Kind).OrderBy(g => KindRank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal))
            byKind[group.Key] = group.Count();
        var summary = new JsonObject
        {
            ["total_markers"] = markers.Count,
            ["files_with_markers"] = markers.Select(m => m.File).Distinct(StringComparer.Ordinal).Count(),
            ["by_kind"] = byKind,
            ["top_files"] = new JsonArray(markers.GroupBy(m => m.File, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(TopFiles)
                .Select(g => (JsonNode?)new JsonObject { ["file"] = g.Key, ["markers"] = g.Count() }).ToArray()),
            ["top"] = new JsonArray(markers.OrderBy(m => KindRank(m.Kind)).ThenBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Line).Take(TopTodos)
                .Select(m => (JsonNode?)new JsonObject { ["kind"] = m.Kind, ["cite"] = Cite(m.File, m.Line), ["text"] = Truncate(m.Text, 160) }).ToArray()),
            ["note"] = "Marcas escritas en comentarios (TODO, FIXME, HACK) con su línea; top muestra primero FIXME y HACK. Deuda declarada por el propio equipo: confirma la línea antes de citarla."
        };
        return Serialize(summary);
    }

    /// <summary>Código comentado: bloques de comentarios que parsean como código, con los ficheros con más y los bloques mayores.</summary>
    public static async Task<string> CommentedCodeAsync(SqliteConnection conn, CancellationToken ct)
    {
        var props = SqlFacetExtractorStep.SafeJson("n.properties");
        var rows = await RowsAsync(conn, $@"
            SELECT n.file, json_extract(t.value, '$.start_line') AS start_line, json_extract(t.value, '$.lines') AS lines,
                   json_extract(t.value, '$.preview') AS preview
            FROM nodes n, json_each({props}, '$.commented_code') t
            WHERE json_type({props}, '$.commented_code') = 'array';", ct);
        var blocks = rows.Select(r => (File: Normalize(Str(r, "file")), Start: (int)Num(r, "start_line"), Lines: (int)Num(r, "lines"), Preview: Str(r, "preview") ?? "")).ToList();

        var summary = new JsonObject
        {
            ["blocks"] = blocks.Count,
            ["lines"] = blocks.Sum(b => b.Lines),
            ["files_with_blocks"] = blocks.Select(b => b.File).Distinct(StringComparer.Ordinal).Count(),
            ["top_files"] = new JsonArray(blocks.GroupBy(b => b.File, StringComparer.Ordinal)
                .OrderByDescending(g => g.Sum(b => b.Lines)).ThenBy(g => g.Key, StringComparer.Ordinal).Take(TopFiles)
                .Select(g => (JsonNode?)new JsonObject { ["file"] = g.Key, ["blocks"] = g.Count(), ["lines"] = g.Sum(b => b.Lines) }).ToArray()),
            ["top"] = new JsonArray(blocks.OrderByDescending(b => b.Lines).ThenBy(b => b.File, StringComparer.Ordinal).ThenBy(b => b.Start).Take(TopCommentedBlocks)
                .Select(b => (JsonNode?)new JsonObject { ["cite"] = Cite(b.File, b.Start), ["lines"] = b.Lines, ["preview"] = Truncate(b.Preview, 120) }).ToArray()),
            ["note"] = "Comentarios que el compilador parsea como código sin errores y no son prosa: candidatos a código comentado; la lectura del bloque lo confirma."
        };
        return Serialize(summary);
    }

    // ------------------------------------------------------------------ constantes resueltas

    private const int TopSecrets = 25;
    private const int TopReflectionTargets = 25;
    private const int TopKeptByReflection = 25;

    private static int SecretKindRank(string kind) => kind switch { "connection-string" => 0, "identifier" => 1, _ => 2 };

    /// <summary>
    /// Credenciales escritas en claro que el compilador resolvió como constantes (<c>hardcoded_secrets</c>): un valor constante
    /// asignado a un identificador con nombre de credencial, pasado como contraseña o dentro de una cadena de conexión, también
    /// cuando está en un <c>static final</c>/<c>const</c> o en una concatenación de constantes. Totales por tipo y las primeras con su
    /// cita <c>ruta:línea</c>. El valor sale enmascarado (<c>Or*** (11 caracteres)</c>): ni el grafo ni la faceta guardan el
    /// secreto completo, y los informes no deben reproducirlo.
    /// </summary>
    public static async Task<string> ResolvedSecretsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var props = SqlFacetExtractorStep.SafeJson("n.properties");
        var rows = await RowsAsync(conn, $@"
            SELECT n.file, n.qualified_name, json_extract(s.value, '$.kind') AS kind, json_extract(s.value, '$.name') AS name,
                   json_extract(s.value, '$.line') AS line, json_extract(s.value, '$.preview_masked') AS preview
            FROM nodes n, json_each({props}, '$.hardcoded_secrets') s
            WHERE json_type({props}, '$.hardcoded_secrets') = 'array';", ct);
        // Una misma línea y tipo es un solo hallazgo aunque lo recojan el campo y el tipo que lo contiene.
        var secrets = rows.Select(r => (File: Normalize(Str(r, "file")), Line: (int)Num(r, "line"), Kind: Str(r, "kind") ?? "identifier",
                Name: Str(r, "name"), Preview: Str(r, "preview") ?? "", Qn: Str(r, "qualified_name") ?? ""))
            .DistinctBy(s => (s.File, s.Line, s.Kind, s.Preview))
            .ToList();

        var byKind = new JsonObject();
        foreach (var group in secrets.GroupBy(s => s.Kind).OrderBy(g => SecretKindRank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal))
            byKind[group.Key] = group.Count();
        var summary = new JsonObject
        {
            ["total_secrets"] = secrets.Count,
            ["files_with_secrets"] = secrets.Select(s => s.File).Distinct(StringComparer.Ordinal).Count(),
            ["by_kind"] = byKind,
            ["shown"] = Math.Min(secrets.Count, TopSecrets),
            ["top"] = new JsonArray(secrets.OrderBy(s => SecretKindRank(s.Kind)).ThenBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line)
                .Take(TopSecrets)
                .Select(s => (JsonNode?)new JsonObject
                {
                    ["cite"] = Cite(s.File, s.Line), ["kind"] = s.Kind, ["name"] = s.Name, ["preview_masked"] = s.Preview, ["qualified_name"] = s.Qn
                }).ToArray()),
            ["note"] = "Valores constantes que el compilador resolvió (constantes con nombre y concatenaciones incluidas) y que se asignan a un identificador de credencial, se pasan como contraseña o forman una cadena de conexión con contraseña. identifier: asignación a un nombre de credencial; call-argument: argumento de una llamada (getConnection, setPassword, SetPassword…); connection-string: «Password=…» o «Pwd=…» dentro de una cadena. Son candidatos: lee la línea citada. Nunca reproduzcas el valor, ni el enmascarado. Pueden repetir lo de hardcoded_secrets (regex sobre el texto): cuenta cada línea una vez."
        };
        return Serialize(summary);
    }

    /// <summary>
    /// Nombres de clase que el compilador resolvió en <c>Class.forName</c>, <c>loadClass</c>, <c>Type.GetType</c> o
    /// <c>Activator.CreateInstance</c> (<c>reflection_targets</c>): el nombre fijo, o con «*» por cada tramo que no es constante
    /// (<c>com.acme.export.*Impl</c>). Más las clases que por eso se tratan como vivas y no salen como código muerto.
    /// </summary>
    public static async Task<string> ReflectionTargetsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var props = SqlFacetExtractorStep.SafeJson("n.properties");
        var rows = await RowsAsync(conn, $@"
            SELECT n.file, n.qualified_name, {SqlFacetExtractorStep.StartLineExpr} AS start_line, t.value AS target
            FROM nodes n, json_each({props}, '$.reflection_targets') t
            WHERE json_type({props}, '$.reflection_targets') = 'array';", ct);
        var targets = rows.Select(r => (File: Normalize(Str(r, "file")), Line: (int)Num(r, "start_line"), Target: Str(r, "target") ?? "", Qn: Str(r, "qualified_name") ?? ""))
            .DistinctBy(t => (t.File, t.Target))
            .ToList();

        var kept = await RowsAsync(conn, $@"
            SELECT n.name, n.qualified_name, n.file, {SqlFacetExtractorStep.StartLineExpr} AS start_line,
                   json_extract({props}, '$.dead_code_suppressed_by') AS suppressed_by
            FROM nodes n
            WHERE json_extract({props}, '$.dead_code_suppressed_by') IN ('reflection-target', 'ReflectionTargetSuppressor')
            ORDER BY n.file, start_line, n.qualified_name;", ct);

        var summary = new JsonObject
        {
            ["total_targets"] = targets.Select(t => t.Target).Distinct(StringComparer.Ordinal).Count(),
            ["exact"] = targets.Select(t => t.Target).Distinct(StringComparer.Ordinal).Count(t => !t.Contains('*')),
            ["patterns"] = targets.Select(t => t.Target).Distinct(StringComparer.Ordinal).Count(t => t.Contains('*')),
            ["top"] = new JsonArray(targets.OrderBy(t => t.Target.Contains('*') ? 0 : 1).ThenBy(t => t.File, StringComparer.Ordinal).ThenBy(t => t.Line)
                .Take(TopReflectionTargets)
                .Select(t => (JsonNode?)new JsonObject
                {
                    ["target"] = t.Target, ["kind"] = t.Target.Contains('*') ? "pattern" : "exact", ["cite"] = Cite(t.File, t.Line), ["qualified_name"] = t.Qn
                }).ToArray()),
            ["classes_kept_alive"] = kept.Count,
            ["kept_alive"] = new JsonArray(kept.Take(TopKeptByReflection)
                .Select(k => (JsonNode?)new JsonObject
                {
                    ["name"] = Str(k, "name"), ["cite"] = Cite(Normalize(Str(k, "file")), (int)Num(k, "start_line")), ["qualified_name"] = Str(k, "qualified_name")
                }).ToArray()),
            ["note"] = "Nombres de clase que el compilador resolvió como constantes en la carga por nombre. «*» marca cada tramo que no es constante: com.example.export.*Impl lo forman un paquete y un sufijo fijos con un nombre que llega de fuera. Las clases que ese patrón puede nombrar se instancian por reflexión y por eso no figuran como código muerto (classes_kept_alive); el cite de un objetivo es el método que hace la llamada. Una carga con un nombre del todo dinámico no sale aquí: está en dynamic_instantiation_sites."
        };
        return Serialize(summary);
    }

    // ------------------------------------------------------------------ documentación

    /// <summary>
    /// Cobertura de documentación por etiqueta. En los ficheros de C# y de Java solo cuenta la documentación que el enriquecedor
    /// reconoció (<c>docstring_source</c> <c>xml</c> o <c>javadoc</c>): un <c>docstring</c> sin fuente es el comentario <c>//</c>
    /// que cbm guardó (y en Java, con la codificación estropeada), y un <c>comment</c> es un comentario anterior a la declaración,
    /// no documentación. En el resto de lenguajes, sin enriquecedor propio, vale el <c>docstring</c> de cbm.
    /// </summary>
    public const string DocCoverageSql = @"
        SELECT label, COUNT(1) AS total,
               SUM(CASE WHEN json_extract(p, '$.docstring_source') IN ('xml', 'javadoc') OR json_extract(p, '$.xml_doc') IS NOT NULL THEN 1
                        WHEN json_extract(p, '$.docstring_source') IS NULL AND json_extract(p, '$.docstring') IS NOT NULL AND NOT enriched_language THEN 1
                        ELSE 0 END) AS documented,
               SUM(CASE WHEN json_extract(p, '$.docstring_source') = 'comment' THEN 1 ELSE 0 END) AS documented_as_comment,
               SUM(CASE WHEN json_extract(p, '$.docstring_source') IS NULL AND json_extract(p, '$.docstring') IS NOT NULL AND enriched_language THEN 1 ELSE 0 END) AS cbm_docstring_unverified
        FROM (SELECT label, " + "(CASE WHEN json_valid(properties) THEN properties ELSE '{}' END)" + @" AS p,
                     (coalesce(file, '') LIKE '%.cs' OR coalesce(file, '') LIKE '%.java') AS enriched_language
              FROM nodes WHERE label NOT IN ('UserDocument', 'UserDocSection', 'Finding', 'Anchor', 'Summary', 'Batch', 'Run'))
        GROUP BY label;";
}
