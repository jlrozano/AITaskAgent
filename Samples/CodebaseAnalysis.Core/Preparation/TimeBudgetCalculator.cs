using System.Globalization;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>Lo que necesita <see cref="TimeBudgetCalculator"/>: el plan de cobertura, el tamaño del repositorio, las curvas y el techo.</summary>
public sealed record TimeBudgetInput
{
    /// <summary>El plan de cobertura (<see cref="CoveragePlanner"/>) del que salen los subsistemas elegidos por informe.</summary>
    public required CoveragePlan Plan { get; init; }

    /// <summary>Líneas de producción de todo el repositorio: el tamaño de entrada del auditor en modo A.</summary>
    public long TotalProductionLoc { get; init; }

    /// <summary>Informes registrados como modo B. Uno sin subsistemas elegidos se presupuesta como un auditor normal.</summary>
    public IReadOnlyList<ReportType> ModeBReports { get; init; } = [];

    /// <summary>Informes registrados como modo A.</summary>
    public IReadOnlyList<ReportType> ModeAReports { get; init; } = [];

    public TimeBudgetOptions Coefficients { get; init; } = new();

    /// <summary>Techo del pipeline en minutos (<c>CodebaseAnalysis:Timeouts:PipelineMinutes</c>); 0 (o negativo) = sin techo.</summary>
    public double PipelineCeilingMinutes { get; init; }

    /// <summary>Concurrencia con que se divide la suma de presupuestos para compararla con el techo; mínimo 1.</summary>
    public int EffectiveConcurrency { get; init; } = 1;

    /// <summary>
    /// Cuánto más tarda una pieza de lo que su curva dice por esperar turno en el limitador de llamadas LLM
    /// (<c>MaxConcurrentRequests</c>): concurrencia que el pipeline querría entre concurrencia que el proveedor deja (mínimo 1). Los
    /// límites de los pasos cuentan tiempo de reloj, cola incluida, así que suben por este factor; la comparación con el techo no
    /// cambia, porque la espera ya está en <see cref="EffectiveConcurrency"/> (que es el mínimo de las dos).
    /// </summary>
    public double QueueFactor { get; init; } = 1;

    /// <summary>Investigaciones que corren a la vez dentro de un informe en modo B (<c>MaxParallelInvestigations</c>): sirve para el <c>Timeout</c> del envoltorio.</summary>
    public int MaxParallelInvestigations { get; init; } = 1;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>El presupuesto de tiempo y el plan de cobertura que queda tras el recorte (el mismo si no hubo).</summary>
public sealed record TimeBudgetResult
{
    public required TimeBudgetPlan Budget { get; init; }

    /// <summary>
    /// El plan con los subsistemas recortados fuera de <see cref="ReportCoverage.Chosen"/> y dentro de <see cref="ReportCoverage.Excluded"/>
    /// con <see cref="ExclusionReasons.TrimmedByTimeBudget"/>. Es el que tienen que usar los informes.
    /// </summary>
    public required CoveragePlan EffectivePlan { get; init; }
}

/// <summary>
/// El presupuesto de tiempo dinámico (T16, spec §4.4, D6): calcula el tiempo de cada pieza con las curvas
/// <c>clamp(base + k · unidades, mín, máx)</c> de <see cref="TimeBudgetOptions"/> y lo concilia con el techo global del pipeline.
/// Determinista y sin efectos: devuelve el presupuesto y el plan resultante, y <c>TimeBudgetStep</c> los aplica.
/// </summary>
/// <remarks>
/// <para>Piezas: la investigación de cada subsistema elegido de cada informe en modo B (unidad = miles de líneas del subsistema), la
/// consolidación de cada informe en modo B (unidad = subsistemas elegidos) y el auditor de cada informe en modo A o de un informe en
/// modo B sin subsistemas elegidos (unidad = miles de líneas de producción del repositorio). Un resultado de 0 minutos es «sin
/// límite», como en el resto de <see cref="AnalysisTimeouts"/>.</para>
/// <para>Conciliación: si la suma de todos los límites, dividida por la concurrencia efectiva, supera el techo del pipeline, se
/// quita un subsistema cada vez de los informes en modo B, siempre el de menor puntuación entre los últimos de cada informe (a
/// igualdad, por el orden del catálogo), hasta que quepa. Un informe nunca se queda sin su último subsistema: si ni así cabe, se
/// dice en un aviso y se deja pasar, porque el techo lo aplica el pipeline y no este cálculo.</para>
/// </remarks>
public static class TimeBudgetCalculator
{
    private const int Decimals = 3;

    public static TimeBudgetResult Calculate(TimeBudgetInput input)
    {
        var concurrency = Math.Max(input.EffectiveConcurrency, 1);
        var queueFactor = Math.Max(input.QueueFactor, 1);
        // Los límites ya llevan la cola (× queueFactor), y la suma se reparte entre la concurrencia efectiva, que la tiene descontada:
        // el tiempo de reloj que se compara con el techo es, en la práctica, el de las curvas sobre esa concurrencia.
        double Wall(IEnumerable<TimeBudgetEntry> e) => Sum(e) / (concurrency * queueFactor);
        var ceiling = Math.Max(input.PipelineCeilingMinutes, 0);
        var warnings = new List<string>();

        var modeB = input.ModeBReports.Distinct().Order().ToList();
        var modeA = input.ModeAReports.Where(r => !modeB.Contains(r)).Distinct().Order().ToList();

        // Los elegidos de cada informe en modo B, por ranking: el último es el de menor puntuación.
        var chosen = modeB.ToDictionary(
            report => report,
            report => (input.Plan.Reports.FirstOrDefault(r => r.Report == report)?.Chosen ?? []).OrderBy(c => c.Rank).ToList());

        var trimmed = new List<(ReportType Report, CoverageSelection Selection)>();
        var (entries, timeouts) = Build(input, modeA, chosen);
        var initialTotal = Sum(entries);

        while (ceiling > 0 && Wall(entries) > ceiling)
        {
            // Candidatos: el último de cada informe que aún tiene más de uno. Sale el de menor puntuación y, a igualdad, el del primer informe.
            var candidates = chosen.Where(c => c.Value.Count > 1).Select(c => (Report: c.Key, Selection: c.Value[^1]))
                .OrderBy(c => c.Selection.Score).ThenByDescending(c => c.Selection.Rank).ThenBy(c => c.Report).ToList();
            if (candidates.Count == 0)
            {
                warnings.Add(
                    $"⚠️ Presupuesto de tiempo: aun con un solo subsistema por informe en modo B la suma de límites ({Format(Sum(entries))} min) entre la " +
                    $"concurrencia efectiva ({concurrency}{QueueNote(queueFactor)}) son {Format(Wall(entries))} min y sigue superando el techo del pipeline " +
                    $"({Format(ceiling)} min, {AnalysisTimeouts.SectionName}:{nameof(AnalysisTimeouts.PipelineMinutes)}). Súbelo o baja los coeficientes de {TimeBudgetOptions.SectionName}.");
                break;
            }

            var (report, selection) = candidates[0];
            chosen[report].RemoveAt(chosen[report].Count - 1);
            trimmed.Add((report, selection));
            (entries, timeouts) = Build(input, modeA, chosen);
        }

        if (trimmed.Count > 0)
        {
            var before = initialTotal;
            warnings.Insert(0,
                $"⚠️ Presupuesto de tiempo: la suma de límites ({Format(before)} min) entre la concurrencia efectiva ({concurrency}{QueueNote(queueFactor)}) son " +
                $"{Format(before / (concurrency * queueFactor))} min y supera el techo del pipeline ({Format(ceiling)} min, " +
                $"{AnalysisTimeouts.SectionName}:{nameof(AnalysisTimeouts.PipelineMinutes)}). Se quitan {trimmed.Count} subsistema(s) de los informes en modo B, " +
                $"empezando por los de menor puntuación: {string.Join(", ", trimmed.Select(t => $"{t.Selection.Name} de {t.Report}"))}. " +
                $"Quedan {Format(Sum(entries))} min ({Format(Wall(entries))} entre la concurrencia).");
        }

        if (entries.Any(e => e.Minutes == 0))
            warnings.Add("⚠️ Presupuesto de tiempo: hay piezas sin límite (0 min); la suma no las cuenta y no se puede comparar con el techo.");

        var budget = new TimeBudgetPlan
        {
            GeneratedAt = input.GeneratedAt,
            GraphFingerprint = input.Plan.GraphFingerprint,
            TotalProductionLoc = input.TotalProductionLoc,
            Coefficients = input.Coefficients,
            PipelineCeilingMinutes = ceiling,
            EffectiveConcurrency = concurrency,
            QueueFactor = Math.Round(queueFactor, Decimals),
            TotalMinutes = Math.Round(Sum(entries), Decimals),
            Entries = entries,
            ReportTimeouts = timeouts,
            Trimmed = [.. trimmed.Select(t => new TimeBudgetTrim { Report = t.Report, SubsystemId = t.Selection.SubsystemId, Name = t.Selection.Name })],
            Warnings = warnings
        };

        return new TimeBudgetResult { Budget = budget, EffectivePlan = trimmed.Count == 0 ? input.Plan : Trim(input.Plan, trimmed) };
    }

    /// <summary>Minutos a <see cref="TimeSpan"/>: 0 es sin límite (<see cref="Timeout.InfiniteTimeSpan"/>), como en <see cref="AnalysisTimeouts.ToTimeSpan"/>.</summary>
    public static TimeSpan ToTimeSpan(double minutes) =>
        minutes > 0 && minutes < AnalysisTimeouts.MaxMinutes ? TimeSpan.FromMinutes(minutes) : Timeout.InfiniteTimeSpan;

    /// <summary>La tabla <c>subsistema → límite</c> de las investigaciones de un informe, tal como la aplica su envoltorio.</summary>
    public static IReadOnlyDictionary<string, TimeSpan> InvestigationTimeouts(TimeBudgetPlan budget, ReportType report) =>
        budget.Entries.Where(e => e.Kind == TimeBudgetKind.Investigation && e.Report == report && e.SubsystemId != null)
            .ToDictionary(e => e.SubsystemId!, e => ToTimeSpan(e.Minutes), StringComparer.Ordinal);

    private static (List<TimeBudgetEntry> Entries, List<ReportStepTimeout> Timeouts) Build(
        TimeBudgetInput input, List<ReportType> modeA, Dictionary<ReportType, List<CoverageSelection>> chosen)
    {
        var coefficients = input.Coefficients;
        var queueFactor = Math.Max(input.QueueFactor, 1);
        var parallel = Math.Max(input.MaxParallelInvestigations, 1);
        double MinutesOf(TimeBudgetCurve curve, double units) => Math.Round(MinutesOfCurve(curve, units) * queueFactor, Decimals);
        var auditorUnits = Math.Round(input.TotalProductionLoc / 1000.0, Decimals);
        var entries = new List<TimeBudgetEntry>();
        var timeouts = new List<ReportStepTimeout>();

        TimeBudgetEntry Auditor(ReportType report) => new()
        {
            Kind = TimeBudgetKind.Auditor, Report = report, Units = auditorUnits, Minutes = MinutesOf(coefficients.Auditor, auditorUnits)
        };

        foreach (var report in chosen.Keys.Concat(modeA).Order())
        {
            if (!chosen.TryGetValue(report, out var selections) || selections.Count == 0)
            {
                // Modo A, o modo B que degradó a un auditor normal por no tener subsistemas elegidos.
                var auditor = Auditor(report);
                entries.Add(auditor);
                timeouts.Add(new ReportStepTimeout { Report = report, Minutes = auditor.Minutes });
                continue;
            }

            var investigations = selections.Select(s =>
            {
                var units = Math.Round(s.Loc / 1000.0, Decimals);
                return new TimeBudgetEntry
                {
                    Kind = TimeBudgetKind.Investigation, Report = report, SubsystemId = s.SubsystemId, Units = units,
                    Minutes = MinutesOf(coefficients.Investigation, units)
                };
            }).ToList();
            var consolidation = new TimeBudgetEntry
            {
                Kind = TimeBudgetKind.Consolidation, Report = report, Units = selections.Count,
                Minutes = MinutesOf(coefficients.Consolidation, selections.Count)
            };
            entries.AddRange(investigations);
            entries.Add(consolidation);

            // El envoltorio cubre las tandas de investigaciones (cada una, como mucho, lo que dura la más larga) y la consolidación.
            var waves = (selections.Count + parallel - 1) / parallel;
            var longest = investigations.Max(e => e.Minutes);
            var unlimited = longest == 0 || consolidation.Minutes == 0 || investigations.Any(e => e.Minutes == 0);
            timeouts.Add(new ReportStepTimeout { Report = report, Minutes = unlimited ? 0 : Math.Round(waves * longest + consolidation.Minutes, Decimals) });
        }

        return (entries, timeouts);
    }

    /// <summary>El tiempo de la curva en minutos; 0 si es sin límite (o si la curva da 0).</summary>
    private static double MinutesOfCurve(TimeBudgetCurve curve, double units)
    {
        var span = curve.Evaluate(units);
        return span == Timeout.InfiniteTimeSpan ? 0 : Math.Round(span.TotalMinutes, Decimals);
    }

    private static string QueueNote(double queueFactor) =>
        queueFactor > 1 ? $", con ×{Format(queueFactor)} por esperar turno en el limitador de llamadas LLM" : "";

    private static double Sum(IEnumerable<TimeBudgetEntry> entries) => entries.Sum(e => e.Minutes);

    private static CoveragePlan Trim(CoveragePlan plan, List<(ReportType Report, CoverageSelection Selection)> trimmed) =>
        plan with
        {
            Reports =
            [
                .. plan.Reports.Select(report =>
                {
                    var removed = trimmed.Where(t => t.Report == report.Report).Select(t => t.Selection).ToList();
                    if (removed.Count == 0)
                        return report;
                    var removedIds = removed.Select(r => r.SubsystemId).ToHashSet(StringComparer.Ordinal);
                    return report with
                    {
                        Chosen = [.. report.Chosen.Where(c => !removedIds.Contains(c.SubsystemId))],
                        Excluded =
                        [
                            .. report.Excluded
                                .Concat(removed.Select(r => new CoverageExclusion { SubsystemId = r.SubsystemId, Name = r.Name, Reason = ExclusionReasons.TrimmedByTimeBudget }))
                                .OrderBy(e => e.SubsystemId, StringComparer.Ordinal)
                        ]
                    };
                })
            ]
        };

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
