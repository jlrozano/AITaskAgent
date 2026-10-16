using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// La versión compacta del plan de cobertura que va a las plantillas (<c>ReportFacetInput.CoveragePlan</c>): por informe en modo B,
/// los subsistemas elegidos, la muestra de la cola larga y los excluidos con su motivo (recortados), en JSON sin sangría. Sin las
/// preguntas ni la evidencia mínima, que son de la investigación y no del contexto de un auditor; sin las componentes de la
/// puntuación, que siguen en <c>_coverage_plan.json</c>.
/// </summary>
public static class CoveragePlanSummary
{
    /// <summary>Excluidos que se listan por informe (el total va aparte).</summary>
    public const int DefaultExcluded = 40;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string ToJson(CoveragePlan plan, int maxExcluded = DefaultExcluded) =>
        JsonSerializer.Serialize(new
        {
            max_subsystems_per_report = plan.MaxSubsystemsPerReport,
            reports = plan.Reports.Select(r => new
            {
                report = r.Report.ToString(),
                chosen = r.Chosen.Select(Selection),
                long_tail = r.LongTail.Select(Selection),
                excluded_total = r.Excluded.Count,
                excluded = r.Excluded.Take(maxExcluded).Select(e => new { id = e.SubsystemId, name = e.Name, reason = e.Reason }),
                failed = r.Failed.Select(f => new { id = f.SubsystemId, name = f.Name, reason = f.Reason })
            }),
            warnings = plan.Warnings
        }, Json);

    private static object Selection(CoverageSelection s) => new { id = s.SubsystemId, name = s.Name, rank = s.Rank, score = s.Score, loc = s.Loc };
}
