using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.Extensions.Logging;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

public sealed class GraphSearchTool : LlmTool
{
    private readonly CbmGraphDb _graphDb;

    public GraphSearchTool(string? defaultDbPath = null)
    {
        _graphDb = new CbmGraphDb(defaultDbPath);
    }

    public override string Name => "graph_search";
    public override string Description => "Busca nodos en el grafo de código por etiqueta ('label'), patrón de nombre ('name_pattern'), o nombre cualificado ('qn_pattern'). Devuelve etiqueta, nombre, Qualified Name, fichero y línea, y las propiedades útiles de cada nodo (firma, jerarquía, ruta de framework, indicadores de JSP, docstring).";
    public override bool IsReadOnly => true;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "label": {
          "type": "string",
          "description": "Etiqueta del nodo: 'Class', 'Interface', 'Method', 'CsProject', 'DiRegistration', 'Route', 'Flow', 'Enum', 'Function'; 'Jsp' y 'TilesDefinition' en aplicaciones Struts/JSP; y 'UserDocument' o 'UserDocSection' para la documentación que el usuario ha añadido al grafo (sus secciones enlazan con DOC_DESCRIBES al código que mencionan)."
        },
        "name_pattern": {
          "type": "string",
          "description": "Texto o fragmento a buscar en el nombre del nodo."
        },
        "qn_pattern": {
          "type": "string",
          "description": "Texto o fragmento a buscar en el Qualified Name."
        },
        "limit": {
          "type": "integer",
          "description": "Número máximo de resultados (por defecto 30)."
        },
        "include_code": {
          "type": "boolean",
          "description": "Si es true, añade el código de cada nodo leído del fichero actual (tramo de líneas del nodo, hasta 150 líneas y 5 nodos). Úsalo para ver una función o clase por su nombre."
        },
        "include_properties": {
          "type": "boolean",
          "description": "Si es true (por defecto), añade tras la tabla las propiedades útiles de cada nodo que las tenga: firma, tipos, visibilidad, complejidad, sobrecarga, jerarquía (fqn, super_fqn, interfaces_fqn), ruta y acción de framework, indicadores de JSP (suspicious, scriptlets, imports, SQL, sesión) y docstring. Pon false si solo quieres la tabla."
        }
      }
    }
    """);

    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson, 
        PipelineContext context, 
        ILogger logger, 
        CancellationToken cancellationToken)
    {
        var dbPath = context.Get<string>(AnalysisContextKeys.CbmDbPath);
        string? label = null;
        string? namePat = null;
        string? qnPat = null;
        int limit = 30;
        var includeCode = false;
        var includeProperties = true;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("label", out var l) && !string.IsNullOrWhiteSpace(l.GetString()))
            {
                label = l.GetString();
            }
            if (doc.RootElement.TryGetProperty("name_pattern", out var np) && !string.IsNullOrWhiteSpace(np.GetString()))
            {
                namePat = np.GetString();
            }
            if (doc.RootElement.TryGetProperty("qn_pattern", out var qp) && !string.IsNullOrWhiteSpace(qp.GetString()))
            {
                qnPat = qp.GetString();
            }
            if (doc.RootElement.TryGetProperty("limit", out var lim) && lim.TryGetInt32(out var limInt))
            {
                limit = limInt;
            }
            if (doc.RootElement.TryGetProperty("include_code", out var ic) && ic.ValueKind == JsonValueKind.True)
            {
                includeCode = true;
            }
            if (doc.RootElement.TryGetProperty("include_properties", out var ip) && ip.ValueKind == JsonValueKind.False)
            {
                includeProperties = false;
            }
        }
        catch
        {
            // Defaults
        }

        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(label)) conditions.Add("label = @lbl");
        if (!string.IsNullOrEmpty(namePat)) conditions.Add("name LIKE @npat");
        if (!string.IsNullOrEmpty(qnPat)) conditions.Add("qualified_name LIKE @qpat");

        void bind(Microsoft.Data.Sqlite.SqliteParameterCollection p)
        {
            if (!string.IsNullOrEmpty(label)) p.AddWithValue("@lbl", label);
            if (!string.IsNullOrEmpty(namePat)) p.AddWithValue("@npat", "%" + namePat + "%");
            if (!string.IsNullOrEmpty(qnPat)) p.AddWithValue("@qpat", "%" + qnPat + "%");
            p.AddWithValue("@lim", limit);
        }

        // Los nodos que escribió el LLM (hallazgos, resúmenes…) solo se buscan pidiendo su etiqueta; las vistas findings y llm_contracts de graph_domain los leen.
        if (label == null || !Enrichment.AnalysisGraph.AnalysisGraphLabels.RegistryLabels.Contains(label, StringComparer.Ordinal))
            conditions.Add("NOT " + Enrichment.AnalysisGraph.AnalysisGraphLabels.LlmNodePredicate("nodes"));

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var sql = $"SELECT label, name, qualified_name AS qn, coalesce(nullif(file_path, ''), file) AS file_path, " +
                  "coalesce(nullif(line_start, 0), start_line) AS line_start, coalesce(nullif(line_end, 0), end_line) AS line_end, " +
                  // Hay nodos de cbm con properties que no es JSON válido: sin json_valid, json_extract tumba la consulta entera.
                  "CASE WHEN json_valid(properties) THEN json_extract(properties, '$.code') END AS code, " +
                  "CASE WHEN json_valid(properties) THEN json_remove(properties, '$.code') END AS props, " +
                  "coalesce(json_valid(properties), 0) AS props_valid " +
                  $"FROM nodes {whereClause} ORDER BY label, name LIMIT @lim";

        var rows = await _graphDb.SelectAsync(sql, bind, dbPath, cancellationToken);

        var sb = new StringBuilder($"# Búsqueda en Grafo ({rows.Count} resultados)\n\n");
        if (rows.Count == 0)
        {
            sb.AppendLine("*(No se encontraron nodos coincidentes con los criterios indicados)*");
            return sb.ToString();
        }

        sb.AppendLine("| Etiqueta | Nombre | FQN / Qualified Name | Archivo | Línea |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var r in rows)
        {
            sb.AppendLine($"| `{r.GetValueOrDefault("label")}` | **{r.GetValueOrDefault("name")}** | `{r.GetValueOrDefault("qn")}` | `{r.GetValueOrDefault("file_path")}` | {r.GetValueOrDefault("line_start")} |");
        }

        if (includeProperties)
            AppendProperties(sb, rows);

        if (includeCode)
            await AppendCodeAsync(sb, rows, cancellationToken);

        return sb.ToString();
    }

    /// <summary>
    /// Propiedades de nodo que se enseñan, en este orden y solo si existen: las del analizador nativo (método y tipo), las
    /// de rutas y acciones de framework y las de JSP. El resto (código, líneas de scriptlets, taglibs…) no aporta a la tabla.
    /// </summary>
    public static IReadOnlyList<string> ShownProperties { get; } =
    [
        "signature", "return_type_fqn", "param_types", "accessibility", "is_static", "is_abstract", "cyclomatic", "overload_of",
        "fqn", "super_fqn", "interfaces_fqn",
        "route_path", "http_method", "framework", "action_type", "form_bean",
        "suspicious", "suspicious_reasons", "scriptlet_count", "page_imports", "has_sql", "session_access",
        "docstring"
    ];

    public const int MaxDocstringChars = 300;
    public const int MaxPropertyChars = 200;
    public const int MaxPropertyItems = 10;

    /// <summary>
    /// Sección de propiedades tras la tabla, una línea por nodo que tenga alguna. Un nodo cuyo <c>properties</c> no es JSON
    /// válido lo dice, para que no se lea como «sin propiedades». Las líneas nunca empiezan por <c>####</c>: esa cabecera es
    /// la del código leído, que la auditoría del chat cuenta como lectura.
    /// </summary>
    private static void AppendProperties(StringBuilder sb, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var lines = new List<string>();
        foreach (var r in rows)
        {
            var head = $"- **{r.GetValueOrDefault("name")}** `{r.GetValueOrDefault("qn")}`";
            if (r.GetValueOrDefault("props_valid")?.ToString() != "1")
            {
                lines.Add($"{head}: *(properties no es JSON válido en el grafo; no se pueden leer)*");
                continue;
            }
            if (r.GetValueOrDefault("props")?.ToString() is not { Length: > 0 } json)
                continue;

            List<string> parts;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;
                parts = [.. ShownProperties.Select(key => FormatProperty(doc.RootElement, key)).OfType<string>()];
            }
            catch (JsonException)
            {
                continue;
            }
            if (parts.Count > 0)
                lines.Add($"{head}: {string.Join(" · ", parts)}");
        }

        if (lines.Count == 0)
            return;
        sb.AppendLine();
        sb.AppendLine("### Propiedades");
        sb.AppendLine();
        foreach (var line in lines)
            sb.AppendLine(line);
    }

    private static string? FormatProperty(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value))
            return null;

        if (key == "docstring")
        {
            var text = value.ValueKind == JsonValueKind.String ? CleanDocstring(value.GetString()) : "";
            return text.Length == 0 ? null : $"docstring: «{Truncate(text, MaxDocstringChars)}»";
        }

        string? shown = value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : $"`{Code(Truncate(value.GetString()!, MaxPropertyChars))}`",
            JsonValueKind.Number => $"`{value.GetRawText()}`",
            JsonValueKind.True => "`true`",
            JsonValueKind.False => "`false`",
            JsonValueKind.Array => FormatArray(value),
            _ => null
        };
        return shown == null ? null : $"{key}: {shown}";
    }

    private static string? FormatArray(JsonElement array)
    {
        var items = array.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()
                : x.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? x.GetRawText() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (items.Count == 0)
            return null;
        var shown = string.Join(", ", items.Take(MaxPropertyItems).Select(x => $"`{Code(Truncate(x!, MaxPropertyChars))}`"));
        return items.Count > MaxPropertyItems ? $"{shown} (+{items.Count - MaxPropertyItems})" : shown;
    }

    /// <summary>
    /// Docstring en una línea: sin los delimitadores de comentario que cbm deja en bruto (<c>/**</c>, <c>*/</c>, <c>*</c>
    /// al principio de línea, <c>///</c>) y con los espacios y saltos colapsados.
    /// </summary>
    private static string CleanDocstring(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var lines = raw.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Trim())
            .Select(line => line.StartsWith("/**", StringComparison.Ordinal) ? line[3..]
                : line.StartsWith("///", StringComparison.Ordinal) ? line[3..]
                : line.StartsWith("/*", StringComparison.Ordinal) ? line[2..]
                : line)
            .Select(line => line.EndsWith("*/", StringComparison.Ordinal) ? line[..^2] : line)
            .Select(line => line.TrimStart())
            .Select(line => line.StartsWith('*') ? line[1..] : line);
        return string.Join(' ', string.Join(' ', lines).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static string Code(string text) => text.Replace('`', '\'').ReplaceLineEndings(" ");

    public const int MaxCodeNodes = 5;
    public const int MaxCodeLines = 150;

    /// <summary>
    /// Código de los primeros nodos con fichero y líneas, leído del fichero actual: el grafo es del último análisis y el
    /// fichero puede haber cambiado. La cabecera <c>#### Código · `ruta:ini-fin`</c> es la que reconoce la auditoría del
    /// chat como lectura; la copia de <c>properties.code</c> solo sale si el fichero no se puede leer, y avisa.
    /// </summary>
    private static async Task AppendCodeAsync(StringBuilder sb, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken ct)
    {
        var root = Path.GetFullPath(AITaskAgent.FileTools.FileToolsConfiguration.RootDirectory);
        var shown = 0;
        foreach (var r in rows)
        {
            if (shown >= MaxCodeNodes)
                break;
            var file = r.GetValueOrDefault("file_path")?.ToString();
            if (string.IsNullOrWhiteSpace(file) || !int.TryParse(r.GetValueOrDefault("line_start")?.ToString(), out var start) || start < 1)
                continue;
            _ = int.TryParse(r.GetValueOrDefault("line_end")?.ToString(), out var end);
            if (end < start)
                end = start;
            end = Math.Min(end, start + MaxCodeLines - 1);

            var full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(root, file));
            var inside = full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var relative = inside ? Path.GetRelativePath(root, full).Replace('\\', '/') : file;
            var name = r.GetValueOrDefault("name");

            string[]? lines = null;
            if (inside && File.Exists(full))
            {
                // Con la regla de codificación de las tools (H-8), no como UTF-8 a secas.
                try { lines = await AITaskAgent.FileTools.TextFileEncoding.ReadAllLinesAsync(full, ct); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            sb.AppendLine();
            if (lines != null && start <= lines.Length)
            {
                end = Math.Min(end, lines.Length);
                sb.AppendLine($"#### Código · `{relative}:{start}-{end}` ({name})");
                sb.AppendLine("```");
                for (var i = start; i <= end; i++)
                    sb.AppendLine($"{i}: {lines[i - 1]}");
                sb.AppendLine("```");
            }
            else if (r.GetValueOrDefault("code")?.ToString() is { Length: > 0 } snapshot)
            {
                sb.AppendLine($"#### Copia del grafo de `{relative}` ({name}), puede estar desfasada: el fichero no se pudo leer");
                sb.AppendLine("```");
                sb.AppendLine(snapshot.TrimEnd());
                sb.AppendLine("```");
            }
            else
            {
                continue;
            }
            shown++;
        }
    }
}
