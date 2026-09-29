using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.Extensions.Logging;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

public sealed class GraphFlowsTool : LlmTool
{
    private readonly CbmGraphDb _graphDb;

    public GraphFlowsTool(string? defaultDbPath = null)
    {
        _graphDb = new CbmGraphDb(defaultDbPath);
    }

    public override string Name => "graph_flows";
    public override string Description => "Inspecciona los flujos end-to-end materializados en el grafo: lista de flujos, pasos ordenados de un flujo específico ('name'), o flujos que tocan una clase o método ('touching').";
    public override bool IsReadOnly => true;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "name": {
          "type": "string",
          "description": "Nombre o QualifiedName del flujo a inspeccionar detalladamente con sus pasos ordenados."
        },
        "touching": {
          "type": "string",
          "description": "Nombre de una clase o método para averiguar qué flujos lo atraviesan."
        },
        "detail": {
          "type": "string",
          "enum": ["steps", "errors"],
          "description": "'steps' para todos los pasos o 'errors' para aislar rutas de error y excepciones."
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
        string? name = null;
        string? touching = null;
        string detail = "steps";

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
            {
                name = n.GetString();
            }
            if (doc.RootElement.TryGetProperty("touching", out var t) && !string.IsNullOrWhiteSpace(t.GetString()))
            {
                touching = t.GetString();
            }
            if (doc.RootElement.TryGetProperty("detail", out var d) && !string.IsNullOrWhiteSpace(d.GetString()))
            {
                detail = d.GetString()!;
            }
        }
        catch
        {
            // Defaults
        }

        if (!string.IsNullOrWhiteSpace(touching))
        {
            return await FlowsTouchingAsync(dbPath, touching, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return await FlowStepsOrderedAsync(dbPath, name, detail, cancellationToken);
        }

        return await FlowsListAsync(dbPath, cancellationToken);
    }

    private async Task<string> FlowsListAsync(string? dbPath, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            "SELECT name, qualified_name AS qn, " +
            $"json_extract({CbmGraphDb.SafeProperties},'$.flow_kind') AS kind, " +
            $"json_extract({CbmGraphDb.SafeProperties},'$.step_count') AS steps, " +
            $"json_extract({CbmGraphDb.SafeProperties},'$.path_count') AS paths " +
            "FROM nodes WHERE label='Flow' ORDER BY kind, name", null, dbPath, ct);

        var sb = new StringBuilder($"# Flujos End-to-End Registrados ({rows.Count})\n\n");
        if (rows.Count == 0)
        {
            sb.AppendLine("*(No se encontraron nodos Flow en el grafo)*");
            return sb.ToString();
        }

        sb.AppendLine("| Flujo | Tipo / Kind | Pasos | Rutas | Qualified Name |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var r in rows)
        {
            sb.AppendLine($"| **{r.GetValueOrDefault("name")}** | `{r.GetValueOrDefault("kind")}` | {r.GetValueOrDefault("steps")} | {r.GetValueOrDefault("paths")} | `{r.GetValueOrDefault("qn")}` |");
        }

        return sb.ToString();
    }

    private async Task<string> FlowStepsOrderedAsync(string? dbPath, string name, string detail, CancellationToken ct)
    {
        var flowRows = await _graphDb.SelectAsync(
            "SELECT name, qualified_name AS qn FROM nodes WHERE label='Flow' " +
            "AND (qualified_name LIKE @like OR name=@name) LIMIT 1",
            p => { p.AddWithValue("@like", "%" + name); p.AddWithValue("@name", name); }, dbPath, ct);

        if (flowRows.Count == 0)
        {
            return $"# Flujo: {name}\n\nNo se encontró ningún nodo Flow coincidente. Llama a `graph_flows` sin argumentos para listar todos los flujos.";
        }

        var flowQn = flowRows[0].GetValueOrDefault("qn")?.ToString() ?? "";
        var flowName = flowRows[0].GetValueOrDefault("name")?.ToString() ?? name;

        var steps = await _graphDb.SelectAsync(
            $"SELECT CAST(json_extract({CbmGraphDb.SafeEdgeProperties},'$.order') AS INTEGER) AS ord, a.name AS from_name, " +
            $"b.name AS to_name, b.label AS to_label, json_extract({CbmGraphDb.SafeEdgeProperties},'$.path_type') AS path_type, " +
            $"json_extract({CbmGraphDb.SafeEdgeProperties},'$.condition') AS condition, json_extract({CbmGraphDb.SafeEdgeProperties},'$.exception_types') AS exception_types " +
            "FROM edges e JOIN nodes a ON e.source_id=a.id JOIN nodes b ON e.target_id=b.id " +
            $"WHERE e.type='FLOW_NEXT' AND json_extract({CbmGraphDb.SafeEdgeProperties},'$.flow_qn')=@flowqn ORDER BY ord",
            p => p.AddWithValue("@flowqn", flowQn), dbPath, ct);

        var sb = new StringBuilder($"# Flujo: {flowName}\n*(Entry: `{flowQn}`)*\n\n");
        if (steps.Count == 0)
        {
            sb.AppendLine("*(Sin pasos FLOW_NEXT registrados)*");
            return sb.ToString();
        }

        bool isErrorsOnly = detail.Equals("errors", StringComparison.OrdinalIgnoreCase);
        var filtered = isErrorsOnly
            ? steps.Where(r => (r.GetValueOrDefault("path_type")?.ToString() ?? "") is "error" or "short_circuit").ToList()
            : steps;

        sb.AppendLine(isErrorsOnly ? "## Rutas de Error y Excepciones\n" : "## Secuencia de Pasos Ordenados\n");

        foreach (var row in filtered)
        {
            var ord = row.GetValueOrDefault("ord") ?? 0;
            var from = row.GetValueOrDefault("from_name") ?? "";
            var to = row.GetValueOrDefault("to_name") ?? "";
            var toL = row.GetValueOrDefault("to_label") ?? "";
            var pt = row.GetValueOrDefault("path_type")?.ToString() ?? "";
            var cond = row.GetValueOrDefault("condition")?.ToString();
            var ex = row.GetValueOrDefault("exception_types")?.ToString();

            var errorMarker = (pt is "error" or "short_circuit") ? " ⚠️" : "";
            sb.Append($"{ord}. **{from}** ➔ **{to}** ({toL}){errorMarker}");
            if (!string.IsNullOrEmpty(pt) && pt != "happy") sb.Append($" — `path_type={pt}`");
            if (!string.IsNullOrEmpty(cond)) sb.Append($" — *condición:* `{cond}`");
            if (!string.IsNullOrEmpty(ex)) sb.Append($" — *lanza:* `{ex}`");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<string> FlowsTouchingAsync(string? dbPath, string touching, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            $"SELECT DISTINCT json_extract({CbmGraphDb.SafeEdgeProperties},'$.flow_qn') AS flow_qn FROM edges e " +
            "JOIN nodes a ON e.source_id=a.id JOIN nodes b ON e.target_id=b.id " +
            "WHERE e.type='FLOW_NEXT' AND (a.qualified_name LIKE @like OR b.qualified_name LIKE @like) " +
            "ORDER BY flow_qn",
            p => p.AddWithValue("@like", "%" + touching + "%"), dbPath, ct);

        var sb = new StringBuilder($"# Flujos que Atraviesan '{touching}' ({rows.Count})\n\n");
        if (rows.Count == 0)
        {
            sb.AppendLine("*(Ningún flujo pasa por el componente especificado)*");
            return sb.ToString();
        }

        foreach (var r in rows)
        {
            sb.AppendLine($"- `{r.GetValueOrDefault("flow_qn")}`");
        }

        return sb.ToString();
    }
}
