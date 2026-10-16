using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.Extensions.Logging;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

public sealed partial class GraphDomainTool : LlmTool
{
    private readonly CbmGraphDb _graphDb;

    public GraphDomainTool(string? defaultDbPath = null)
    {
        _graphDb = new CbmGraphDb(defaultDbPath);
    }

    public override string Name => "graph_domain";
    public override string Description => "Explora elementos de dominio, framework y estructura: endpoints HTTP y contratos de seguridad ('routes'), registros del contenedor de inyección de dependencias con su lifetime ('di'), atributos C# materializados ('attributes'), proyectos C# con su acoplamiento, rol y dependencias contrastadas con los ProjectReference ('projects'), y contratos frente a implementaciones ('contracts': las implementaciones y consumidores de una interfaz o clase, o de qué tipos de otros proyectos depende un proyecto y si son contratos o implementaciones concretas), subsistemas del mapa de dominios del análisis ('subsystems': sin 'name', la lista con su descripción y tamaño; con el nombre o id de uno, sus miembros principales, puntos de entrada, tablas, dependencias y las notas de sus investigaciones) y tablas del SQL escrito en el código ('tables': sin 'name', la lista con lecturas y escrituras; con el nombre de una tabla, qué métodos o clases la leen y cuáles la escriben, con ruta:línea), hallazgos del registro del LLM ('findings': bugs, deuda y seguridad con severidad P0-P3, ancla en el código y estado; filtra por 'severity', 'kind' y 'name' = un símbolo o tipo) y contratos de métodos escritos por el LLM ('llm_contracts': qué hace, qué recibe, qué devuelve, efectos y excepciones de un método o de los de un tipo, con 'name').";
    public override bool IsReadOnly => true;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "view": {
          "type": "string",
          "enum": ["routes", "di", "attributes", "projects", "contracts", "subsystems", "tables", "findings", "llm_contracts"],
          "description": "Aspecto a inspeccionar: 'routes' (superficie HTTP con auth y contratos), 'di' (registros y lifetimes de IoC/DI), 'attributes' (atributos decoradores C#), 'projects' (proyectos C#: Ca/Ce/inestabilidad, abstracción, rol, dependencias observadas frente a ProjectReference y paquetes), 'contracts' (contratos frente a implementaciones; sin 'name' es un resumen por proyecto), 'subsystems' (subsistemas del mapa de dominios; sin 'name' la lista), 'tables' (tablas del SQL del código con quién las lee y quién las escribe; sin 'name' la lista), 'findings' (hallazgos del registro del LLM; sin 'name' la lista ordenada por severidad, con 'name' los de un símbolo o tipo y su detalle), 'llm_contracts' (contratos de métodos que escribió el LLM; con 'name' = un método o un tipo)."
        },
        "name": {
          "type": "string",
          "description": "Para view='projects', 'contracts', 'subsystems' y 'tables'. En 'projects', filtra por el nombre de un proyecto. En 'contracts', el nombre de una interfaz o clase (sus implementaciones y consumidores; el Qualified Name exacto si el nombre es ambiguo) o de un proyecto (qué tipos de otros proyectos usa y si son contratos o implementaciones concretas). En 'subsystems', el nombre o el id de un subsistema (su detalle). En 'tables', el nombre de una tabla (quién la lee y quién la escribe). En 'findings' y 'llm_contracts', un símbolo (Tipo.Miembro) o un tipo."
        },
        "attr": {
          "type": "string",
          "description": "Filtro opcional para el nombre del atributo cuando view='attributes' (ej. 'Authorize', 'Obsolete', 'ApiController')."
        },
        "severity": {
          "type": "string",
          "enum": ["P0", "P1", "P2", "P3"],
          "description": "Solo para view='findings': filtra por severidad."
        },
        "kind": {
          "type": "string",
          "enum": ["bug", "debt", "security"],
          "description": "Solo para view='findings': filtra por clase de hallazgo."
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
        var name = "";
        var limit = 40;
        var severity = "";
        var kind = "";

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
            if (doc.RootElement.TryGetProperty("name", out var n))
            {
                name = n.GetString() ?? "";
            }
            if (doc.RootElement.TryGetProperty("severity", out var sev))
            {
                severity = sev.GetString() ?? "";
            }
            if (doc.RootElement.TryGetProperty("kind", out var kd))
            {
                kind = kd.GetString() ?? "";
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
            "projects" => await DomainProjectsAsync(dbPath, name, limit, cancellationToken),
            "contracts" => await DomainContractsAsync(dbPath, name, limit, cancellationToken),
            "subsystems" => await DomainSubsystemsAsync(dbPath, name, limit, cancellationToken),
            "tables" => await DomainTablesAsync(dbPath, name, limit, cancellationToken),
            "findings" => await DomainFindingsAsync(dbPath, name, severity, kind, limit, cancellationToken),
            "llm_contracts" => await DomainLlmContractsAsync(dbPath, name, limit, cancellationToken),
            _ => await DomainRoutesAsync(dbPath, limit, cancellationToken)
        };
    }

    private async Task<string> DomainRoutesAsync(string? dbPath, int limit, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            $"SELECT json_extract({CbmGraphDb.SafeEdgeProperties},'$.http_method') AS verb, " +
            $"json_extract({CbmGraphDb.SafeEdgeProperties},'$.route_path') AS path, m.qualified_name AS handler, " +
            $"json_extract({CbmGraphDb.SafeEdgeProperties},'$.auth_required') AS auth_required, " +
            $"json_extract({CbmGraphDb.SafeEdgeProperties},'$.auth_schemes') AS auth_schemes, " +
            $"json_extract({CbmGraphDb.SafeEdgeProperties},'$.response_codes') AS response_codes, " +
            $"json_extract({CbmGraphDb.SafeEdgeProperties},'$.request_type_fqn') AS request_type, " +
            $"json_extract({CbmGraphDb.SafeEdgeProperties},'$.response_type_fqn') AS response_type " +
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
            // Sin auth_required (p. ej. las acciones Struts, cuyo plugin no lo escribe) la autenticación es desconocida, no «No».
            var auth = r.GetValueOrDefault("auth_required")?.ToString()?.ToLowerInvariant() switch
            {
                "true" or "1" => "🔒 Sí",
                "false" or "0" => "🔓 No",
                _ => "❔ Desconocida"
            };
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
            // DiEnricher escribe `lifetime` en el registro y `di_lifetime` en la implementación: se leen los dos.
            $"coalesce(json_extract({CbmGraphDb.SafeProperties},'$.lifetime'), json_extract({CbmGraphDb.SafeProperties},'$.di_lifetime')) AS lifetime, " +
            $"json_extract({CbmGraphDb.SafeProperties},'$.is_external') AS is_external " +
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
            // Sin el dato no se sabe: «Transient» por defecto presentaba como transitorio un registro cuyo lifetime no se leyó.
            var lifetime = r.GetValueOrDefault("lifetime") ?? "desconocido";
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
