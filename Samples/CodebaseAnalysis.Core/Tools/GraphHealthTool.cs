using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.Extensions.Logging;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

public sealed class GraphHealthTool : LlmTool
{
    private readonly CbmGraphDb _graphDb;

    public GraphHealthTool(string? defaultDbPath = null)
    {
        _graphDb = new CbmGraphDb(defaultDbPath);
    }

    public override string Name => "graph_health";
    public override string Description => "Analiza la salud del código y deuda técnica: ciclos circulares ('cycles'), candidatos a código muerto ('dead_code'), complejidad ciclomática ('complexity'), hotspots de mantenimiento ('hotspots'), rutas huérfanas ('orphans'), o cobertura de documentación ('doc_coverage').";
    public override bool IsReadOnly => true;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "view": {
          "type": "string",
          "enum": ["cycles", "dead_code", "complexity", "hotspots", "orphans", "doc_coverage"],
          "description": "Aspecto de salud a inspeccionar: 'cycles' (ciclos Tarjan), 'dead_code' (código muerto BFS), 'complexity' (McCabe), 'hotspots' (complejidad x fan-in), 'orphans' (rutas sin handler), 'doc_coverage' (documentación XML)."
        },
        "limit": {
          "type": "integer",
          "description": "Número máximo de resultados a devolver (por defecto 30)."
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
        var view = "cycles";
        var limit = 30;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("view", out var v))
            {
                view = v.GetString() ?? "cycles";
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

        string sql = view.ToLowerInvariant() switch
        {
            "dead_code" =>
                "SELECT name, qualified_name AS qn, label, " +
                $"json_extract({CbmGraphDb.SafeProperties},'$.dead_code_confidence') AS confidence, " +
                $"json_extract({CbmGraphDb.SafeProperties},'$.dead_code_reason') AS reason " +
                $"FROM nodes WHERE json_extract({CbmGraphDb.SafeProperties},'$.dead_code_candidate') IN (1,'true') " +
                $"ORDER BY CASE json_extract({CbmGraphDb.SafeProperties},'$.dead_code_confidence') WHEN 'high' THEN 0 WHEN 'medium' THEN 1 ELSE 2 END, name LIMIT @lim",

            "complexity" =>
                $"SELECT name, qualified_name AS qn, CAST(json_extract({CbmGraphDb.SafeProperties},'$.cyclomatic') AS INTEGER) AS cyclomatic " +
                $"FROM nodes WHERE json_extract({CbmGraphDb.SafeProperties},'$.cyclomatic') IS NOT NULL " +
                "ORDER BY cyclomatic DESC LIMIT @lim",

            "hotspots" =>
                $"SELECT name, qualified_name AS qn, CAST(json_extract({CbmGraphDb.SafeProperties},'$.hotspot') AS REAL) AS hotspot, " +
                $"CAST(json_extract({CbmGraphDb.SafeProperties},'$.cyclomatic') AS INTEGER) AS cyclomatic, " +
                $"CAST(json_extract({CbmGraphDb.SafeProperties},'$.fan_in') AS INTEGER) AS fan_in " +
                $"FROM nodes WHERE json_extract({CbmGraphDb.SafeProperties},'$.hotspot') IS NOT NULL " +
                "ORDER BY hotspot DESC LIMIT @lim",

            "cycles" =>
                "SELECT name, qualified_name AS qn, label, " +
                $"json_extract({CbmGraphDb.SafeProperties},'$.cycle_group') AS cycle_group, " +
                $"CAST(json_extract({CbmGraphDb.SafeProperties},'$.cycle_size') AS INTEGER) AS cycle_size " +
                $"FROM nodes WHERE json_extract({CbmGraphDb.SafeProperties},'$.in_cycle') IN (1,'true') " +
                "ORDER BY cycle_size DESC, label, name LIMIT @lim",

            "orphans" =>
                "SELECT n.name AS path FROM nodes n WHERE n.label='Route' " +
                "AND NOT EXISTS (SELECT 1 FROM edges e WHERE e.type='HANDLES' AND e.target_id=n.id) " +
                "ORDER BY n.name LIMIT @lim",

            "doc_coverage" =>
                $"SELECT name, CAST(json_extract({CbmGraphDb.SafeProperties},'$.doc_coverage') AS REAL) AS doc_coverage, " +
                $"json_extract({CbmGraphDb.SafeProperties},'$.documented_defs') AS documented, json_extract({CbmGraphDb.SafeProperties},'$.total_defs') AS total " +
                $"FROM nodes WHERE label='CsProject' AND json_extract({CbmGraphDb.SafeProperties},'$.doc_coverage') IS NOT NULL " +
                "ORDER BY doc_coverage ASC LIMIT @lim",

            _ => ""
        };

        if (string.IsNullOrEmpty(sql))
        {
            return $"# graph_health\n\nVista inválida: '{view}'. Vistas válidas: cycles, dead_code, complexity, hotspots, orphans, doc_coverage.";
        }

        var rows = await _graphDb.SelectAsync(sql, p => p.AddWithValue("@lim", limit), dbPath, cancellationToken);
        var sb = new StringBuilder($"# Salud del Código: {view} (Top {rows.Count})\n\n");

        if (rows.Count == 0)
        {
            sb.AppendLine("*(No se encontraron incidencias para esta categoría)*");
            return sb.ToString();
        }

        switch (view.ToLowerInvariant())
        {
            case "cycles":
                sb.AppendLine("| Elemento en Ciclo | Tipo | Grupo de Ciclo | Tamaño del Ciclo |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var r in rows)
                {
                    sb.AppendLine($"| `{r.GetValueOrDefault("name")}` | {r.GetValueOrDefault("label")} | {r.GetValueOrDefault("cycle_group")} | {r.GetValueOrDefault("cycle_size")} |");
                }
                break;

            case "dead_code":
                sb.AppendLine("| Elemento | Tipo | Confianza | Causa / Detalle |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var r in rows)
                {
                    sb.AppendLine($"| `{r.GetValueOrDefault("name")}` | {r.GetValueOrDefault("label")} | {r.GetValueOrDefault("confidence")} | {r.GetValueOrDefault("reason")} |");
                }
                break;

            case "complexity":
                sb.AppendLine("| Método / Función | Complejidad Ciclomática (McCabe) |");
                sb.AppendLine("|---|---|");
                foreach (var r in rows)
                {
                    sb.AppendLine($"| `{r.GetValueOrDefault("name")}` | {r.GetValueOrDefault("cyclomatic")} |");
                }
                break;

            case "hotspots":
                sb.AppendLine("| Elemento Hotspot | Score Hotspot | Complejidad Ciclomática | Fan-In |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var r in rows)
                {
                    sb.AppendLine($"| `{r.GetValueOrDefault("name")}` | {r.GetValueOrDefault("hotspot")} | {r.GetValueOrDefault("cyclomatic")} | {r.GetValueOrDefault("fan_in")} |");
                }
                break;

            case "orphans":
                sb.AppendLine("| Ruta HTTP Huérfana (sin método/handler asociado) |");
                sb.AppendLine("|---|");
                foreach (var r in rows)
                {
                    sb.AppendLine($"| `{r.GetValueOrDefault("path")}` |");
                }
                break;

            case "doc_coverage":
                sb.AppendLine("| Proyecto | Cobertura Documental (%) | Definiciones Documentadas | Total Definiciones |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var r in rows)
                {
                    sb.AppendLine($"| `{r.GetValueOrDefault("name")}` | {r.GetValueOrDefault("doc_coverage")}% | {r.GetValueOrDefault("documented")} | {r.GetValueOrDefault("total")} |");
                }
                break;
        }

        return sb.ToString();
    }
}
