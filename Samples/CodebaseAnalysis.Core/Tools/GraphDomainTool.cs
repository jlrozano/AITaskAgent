using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.Extensions.Logging;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

public sealed class GraphDomainTool : LlmTool
{
    private readonly CbmGraphDb _graphDb;

    public GraphDomainTool(string? defaultDbPath = null)
    {
        _graphDb = new CbmGraphDb(defaultDbPath);
    }

    public override string Name => "graph_domain";
    public override string Description => "Explora elementos de dominio y framework: endpoints HTTP y contratos de seguridad ('routes'), registros del contenedor de inyección de dependencias ('di'), o atributos C# materializados ('attributes').";
    public override bool IsReadOnly => true;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "view": {
          "type": "string",
          "enum": ["routes", "di", "attributes"],
          "description": "Aspecto de dominio a inspeccionar: 'routes' (superficie HTTP con auth y contratos), 'di' (registros y lifetimes de IoC/DI), 'attributes' (atributos decoradores C#)."
        },
        "attr": {
          "type": "string",
          "description": "Filtro opcional para el nombre del atributo cuando view='attributes' (ej. 'Authorize', 'Obsolete', 'ApiController')."
        },
        "limit": {
          "type": "integer",
          "description": "Número máximo de resultados a devolver (por defecto 40)."
        }
      },
      "required": ["view"]
    }
    """);

    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson, 
        PipelineContext context, 
        ILogger logger, 
        CancellationToken cancellationToken)
    {
        var dbPath = context.Get<string>(AnalysisContextKeys.CbmDbPath);
        var view = "routes";
        var attr = "";
        var limit = 40;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("view", out var v))
            {
                view = v.GetString() ?? "routes";
            }
            if (doc.RootElement.TryGetProperty("attr", out var a))
            {
                attr = a.GetString() ?? "";
            }
            if (doc.RootElement.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lInt))
            {
                limit = lInt;
            }
        }
        catch
        {
            // Default parameters
        }

        return view.ToLowerInvariant() switch
        {
            "di" => await DomainDiAsync(dbPath, limit, cancellationToken),
            "attributes" => await DomainAttributesAsync(dbPath, attr, limit, cancellationToken),
            _ => await DomainRoutesAsync(dbPath, limit, cancellationToken)
        };
    }

    private async Task<string> DomainRoutesAsync(string? dbPath, int limit, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            "SELECT json_extract(e.properties,'$.http_method') AS verb, " +
            "json_extract(e.properties,'$.route_path') AS path, m.qualified_name AS handler, " +
            "json_extract(e.properties,'$.auth_required') AS auth_required, " +
            "json_extract(e.properties,'$.auth_schemes') AS auth_schemes, " +
            "json_extract(e.properties,'$.response_codes') AS response_codes, " +
            "json_extract(e.properties,'$.request_type_fqn') AS request_type, " +
            "json_extract(e.properties,'$.response_type_fqn') AS response_type " +
            "FROM edges e JOIN nodes m ON e.source_id = m.id " +
            "WHERE e.type = 'HANDLES' ORDER BY path, verb LIMIT @lim",
            p => p.AddWithValue("@lim", limit), dbPath, ct);

        var sb = new StringBuilder($"# Superficie de Rutas HTTP y APIs ({rows.Count})\n\n");
        if (rows.Count == 0)
        {
            sb.AppendLine("*(No se detectaron rutas HTTP registradas en el grafo)*");
            return sb.ToString();
        }

        sb.AppendLine("| Verbo | Ruta / Path | Handler | Auth | Esquemas / Roles | Request DTO | Response DTO |");
        sb.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var r in rows)
        {
            var verb = r.GetValueOrDefault("verb") ?? "GET";
            var path = r.GetValueOrDefault("path") ?? "/";
            var handler = r.GetValueOrDefault("handler") ?? "";
            var auth = r.GetValueOrDefault("auth_required")?.ToString() is "true" or "1" ? "🔒 Sí" : "🔓 No";
            var schemes = r.GetValueOrDefault("auth_schemes") ?? "-";
            var req = r.GetValueOrDefault("request_type") ?? "-";
            var resp = r.GetValueOrDefault("response_type") ?? "-";

            sb.AppendLine($"| `{verb}` | `{path}` | `{handler}` | {auth} | `{schemes}` | `{req}` | `{resp}` |");
        }

        return sb.ToString();
    }

    private async Task<string> DomainDiAsync(string? dbPath, int limit, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            "SELECT name, qualified_name AS qn, " +
            "json_extract(properties,'$.di_lifetime') AS lifetime, " +
            "json_extract(properties,'$.is_external') AS is_external " +
            "FROM nodes WHERE label = 'DiRegistration' ORDER BY name LIMIT @lim",
            p => p.AddWithValue("@lim", limit), dbPath, ct);

        var sb = new StringBuilder($"# Registros del Contenedor de Inyección de Dependencias ({rows.Count})\n\n");
        if (rows.Count == 0)
        {
            sb.AppendLine("*(No se encontraron registros DiRegistration en el grafo)*");
            return sb.ToString();
        }

        sb.AppendLine("| Servicio / Registro | Lifetime (Ciclo de Vida) | ¿Externo? | Detalle FQN |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var r in rows)
        {
            var name = r.GetValueOrDefault("name") ?? "";
            var qn = r.GetValueOrDefault("qn") ?? "";
            var lifetime = r.GetValueOrDefault("lifetime") ?? "Transient";
            var isExt = r.GetValueOrDefault("is_external")?.ToString() is "true" or "1" ? "Sí" : "No";

            sb.AppendLine($"| `{name}` | **{lifetime}** | {isExt} | `{qn}` |");
        }

        return sb.ToString();
    }

    private async Task<string> DomainAttributesAsync(string? dbPath, string attr, int limit, CancellationToken ct)
    {
        var hasFilter = !string.IsNullOrWhiteSpace(attr);
        var wanted = hasFilter && attr.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase)
            ? attr[..^"Attribute".Length] : attr;

        var sql =
            "SELECT n.label AS label, n.name AS name, n.qualified_name AS qn, " +
            "json_extract(a.value,'$.name') AS attribute " +
            // Con un solo nodo de properties inválido (los hay en grafos de cbm), json_each tumbaría la consulta entera.
            "FROM nodes n, json_each(CASE WHEN json_valid(n.properties) THEN n.properties ELSE '{}' END,'$.attributes') a " +
            (hasFilter ? "WHERE lower(json_extract(a.value,'$.name')) = lower(@attr) " : "") +
            "ORDER BY attribute, label, name LIMIT @lim";

        var rows = await _graphDb.SelectAsync(sql, p =>
        {
            if (hasFilter) p.AddWithValue("@attr", wanted);
            p.AddWithValue("@lim", limit);
        }, dbPath, ct);

        var title = hasFilter ? $"Atributos: [{wanted}] ({rows.Count})" : $"Atributos C# ({rows.Count})";
        var sb = new StringBuilder($"# {title}\n\n");

        if (rows.Count == 0)
        {
            sb.AppendLine("*(No se encontraron atributos en el grafo para el filtro especificado)*");
            return sb.ToString();
        }

        sb.AppendLine("| Atributo | Tipo Elemento | Nombre | FQN |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var r in rows)
        {
            sb.AppendLine($"| `[{r.GetValueOrDefault("attribute")}]` | {r.GetValueOrDefault("label")} | `{r.GetValueOrDefault("name")}` | `{r.GetValueOrDefault("qn")}` |");
        }

        return sb.ToString();
    }
}
