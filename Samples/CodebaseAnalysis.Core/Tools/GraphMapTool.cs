using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.Extensions.Logging;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

public sealed class GraphMapTool : LlmTool
{
    private readonly CbmGraphDb _graphDb;

    public GraphMapTool(string? defaultDbPath = null)
    {
        _graphDb = new CbmGraphDb(defaultDbPath);
    }

    public override string Name => "graph_map";
    public override string Description => "Muestra el mapa arquitectural del proyecto: dependencias entre proyectos C# ('projects'), acoplamiento Ca/Ce/inestabilidad por clase ('coupling'), o comunidades modulares Louvain ('communities').";
    public override bool IsReadOnly => true;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "view": {
          "type": "string",
          "enum": ["projects", "coupling", "communities"],
          "description": "Vista deseada: 'projects' para proyectos y dependencias, 'coupling' para métricas Ca/Ce/Instabilidad de Robert C. Martin, 'communities' para modularidad Louvain."
        },
        "limit": {
          "type": "integer",
          "description": "Número máximo de elementos a devolver (por defecto 30)."
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
        var view = "projects";
        var limit = 30;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("view", out var v))
            {
                view = v.GetString() ?? "projects";
            }
            if (doc.RootElement.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lInt))
            {
                limit = lInt;
            }
        }
        catch
        {
            // Parámetros por defecto
        }

        return view.ToLowerInvariant() switch
        {
            "coupling" => await MapCouplingAsync(dbPath, limit, cancellationToken),
            "communities" => await MapCommunitiesAsync(dbPath, limit, cancellationToken),
            _ => await MapProjectsAsync(dbPath, limit, cancellationToken)
        };
    }

    private async Task<string> MapProjectsAsync(string? dbPath, int limit, CancellationToken ct)
    {
        var csProjects = await _graphDb.GetNodesByLabelAsync("CsProject", dbPath, ct);
        var depEdges = await _graphDb.SelectAsync(
            "SELECT a.name AS from_proj, b.name AS to_proj, json_extract(e.properties, '$.layer_violation') AS violation " +
            "FROM edges e " +
            "JOIN nodes a ON e.source_id = a.id " +
            "JOIN nodes b ON e.target_id = b.id " +
            "WHERE e.type = 'PROJECT_DEPENDS_ON' ORDER BY a.name", null, dbPath, ct);

        var sb = new StringBuilder("# Mapa de Proyectos y Componentes C#\n\n");
        sb.AppendLine($"Proyectos detectados: {csProjects.Count}\n");

        foreach (var (name, qn, propsJson) in csProjects)
        {
            sb.AppendLine($"### 📦 {name}");
            sb.AppendLine($"- **Qualified Name**: `{qn}`");

            try
            {
                using var pDoc = JsonDocument.Parse(propsJson);
                var root = pDoc.RootElement;
                if (root.TryGetProperty("afferent_coupling", out var ca)) sb.AppendLine($"- **Ca (Afferent Coupling)**: {ca}");
                if (root.TryGetProperty("efferent_coupling", out var ce)) sb.AppendLine($"- **Ce (Efferent Coupling)**: {ce}");
                if (root.TryGetProperty("instability", out var inst)) sb.AppendLine($"- **Instabilidad (I)**: {inst}");
                if (root.TryGetProperty("role", out var role)) sb.AppendLine($"- **Rol**: {role.GetString()}");
            }
            catch { }

            var deps = depEdges
                .Where(e => string.Equals(e.GetValueOrDefault("from_proj")?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.GetValueOrDefault("to_proj")?.ToString())
                .Where(d => !string.IsNullOrEmpty(d))
                .ToList();

            if (deps.Count > 0)
            {
                sb.AppendLine($"- **Depende de**: {string.Join(", ", deps)}");
            }
            sb.AppendLine();
        }

        var violations = depEdges
            .Where(e => e.GetValueOrDefault("violation")?.ToString() is "true" or "1")
            .ToList();

        if (violations.Count > 0)
        {
            sb.AppendLine("## ⚠️ Violaciones de Capas Detectadas");
            foreach (var v in violations)
            {
                sb.AppendLine($"- **{v.GetValueOrDefault("from_proj")}** ➔ **{v.GetValueOrDefault("to_proj")}** (Violación arquitectónica)");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<string> MapCouplingAsync(string? dbPath, int limit, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            "SELECT name, qualified_name AS qn, " +
            "CAST(json_extract(properties,'$.afferent_coupling') AS INTEGER) AS ca, " +
            "CAST(json_extract(properties,'$.efferent_coupling') AS INTEGER) AS ce, " +
            "json_extract(properties,'$.instability') AS instability " +
            "FROM nodes WHERE label IN ('Class','Interface') " +
            "AND json_extract(properties,'$.afferent_coupling') IS NOT NULL " +
            "ORDER BY ca DESC, ce DESC LIMIT @lim",
            p => p.AddWithValue("@lim", limit), dbPath, ct);

        var sb = new StringBuilder($"# Métricas de Acoplamiento (Top {rows.Count} por Afferent Coupling)\n\n");
        sb.AppendLine("| Componente / Clase | Ca (Eferente Entrante) | Ce (Aferente Saliente) | Instabilidad (I) |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var r in rows)
        {
            var name = r.GetValueOrDefault("name") ?? "";
            var ca = r.GetValueOrDefault("ca") ?? 0;
            var ce = r.GetValueOrDefault("ce") ?? 0;
            var inst = r.GetValueOrDefault("instability") ?? "0.0";
            sb.AppendLine($"| `{name}` | {ca} | {ce} | {inst} |");
        }

        return sb.ToString();
    }

    private async Task<string> MapCommunitiesAsync(string? dbPath, int limit, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            "SELECT json_extract(properties,'$.community_id') AS community_id, " +
            "json_extract(properties,'$.community_name') AS community_name, count(*) AS members " +
            "FROM nodes WHERE label IN ('Class','Interface') " +
            "AND json_extract(properties,'$.community_id') IS NOT NULL " +
            "GROUP BY community_id ORDER BY members DESC LIMIT @lim",
            p => p.AddWithValue("@lim", limit), dbPath, ct);

        var sb = new StringBuilder($"# Comunidades Modulares (Louvain Clustering, Top {rows.Count})\n\n");
        sb.AppendLine("| ID Comunidad | Nombre / Etiqueta | Miembros (Clases/Interfaces) |");
        sb.AppendLine("|---|---|---|");

        foreach (var r in rows)
        {
            var id = r.GetValueOrDefault("community_id") ?? "";
            var name = r.GetValueOrDefault("community_name") ?? $"Comunidad_{id}";
            var members = r.GetValueOrDefault("members") ?? 0;
            sb.AppendLine($"| {id} | {name} | {members} |");
        }

        return sb.ToString();
    }
}
