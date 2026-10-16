using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="TimeBudgetCalculator"/> (T16, criterio A4): las fórmulas del §4.4 con las curvas de <see cref="TimeBudgetOptions"/>
/// y la conciliación con el techo global del pipeline (suma de presupuestos entre la concurrencia efectiva; si lo supera,
/// recorte de los subsistemas de menor puntuación, nunca el último de un informe, con avisos).
/// </summary>
public sealed class TimeBudgetCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly ReportType[] ModeB = [.. CoveragePlanner.ModeBReports];

    private static readonly ReportType[] ModeA =
        [ReportType.CodeSmells, ReportType.CodigoMuerto, ReportType.InventarioApisSeguridad, ReportType.DeudaTecnicaHotspots, ReportType.InyeccionDependenciasExtensiones];

    private static CoverageSelection Sel(string id, int rank, double score, long loc) =>
        new() { SubsystemId = id, Name = id.ToUpperInvariant(), Rank = rank, Score = score, Loc = loc };

    /// <summary>Un plan con los mismos subsistemas elegidos en los cuatro informes en modo B (como lo deja el planificador).</summary>
    private static CoveragePlan PlanOf(params CoverageSelection[] chosen) =>
        new()
        {
            GeneratedAt = Now,
            GraphFingerprint = "fp",
            MaxSubsystemsPerReport = 8,
            Reports = [.. ModeB.Select(r => new ReportCoverage { Report = r, Chosen = chosen })]
        };

    private static TimeBudgetInput Input(
        CoveragePlan plan, long totalLoc = 100_000, double ceiling = 0, int concurrency = 4, TimeBudgetOptions? coefficients = null,
        IReadOnlyList<ReportType>? modeB = null, IReadOnlyList<ReportType>? modeA = null) =>
        new()
        {
            Plan = plan,
            TotalProductionLoc = totalLoc,
            ModeBReports = modeB ?? ModeB,
            ModeAReports = modeA ?? ModeA,
            Coefficients = coefficients ?? new TimeBudgetOptions(),
            PipelineCeilingMinutes = ceiling,
            EffectiveConcurrency = concurrency,
            MaxParallelInvestigations = 4,
            GeneratedAt = Now
        };

    private static TimeBudgetEntry Single(TimeBudgetResult result, TimeBudgetKind kind, ReportType report, string? subsystem = null) =>
        result.Budget.Entries.Single(e => e.Kind == kind && e.Report == report && e.SubsystemId == subsystem);

    [Fact]
    public void It_applies_the_formulas_of_the_spec_with_the_default_coefficients()
    {
        // Investigación: 4 + 0,1 por millar de líneas, entre 4 y 20. Consolidación: 5 + 1 por subsistema, entre 5 y 30.
        // Auditor en modo A: 10 + 0,03 por millar de líneas del repositorio, entre 10 y 40.
        var plan = PlanOf(Sel("big", 1, 0.9, 100_000), Sel("small", 2, 0.5, 10_000));

        var result = TimeBudgetCalculator.Calculate(Input(plan, totalLoc: 500_000));

        Assert.Equal(14, Single(result, TimeBudgetKind.Investigation, ReportType.AnalisisFuncional, "big").Minutes);
        Assert.Equal(100, Single(result, TimeBudgetKind.Investigation, ReportType.AnalisisFuncional, "big").Units);
        Assert.Equal(5, Single(result, TimeBudgetKind.Investigation, ReportType.AnalisisFuncional, "small").Minutes);
        Assert.Equal(7, Single(result, TimeBudgetKind.Consolidation, ReportType.AnalisisFuncional).Minutes);
        Assert.Equal(2, Single(result, TimeBudgetKind.Consolidation, ReportType.AnalisisFuncional).Units);
        Assert.Equal(25, Single(result, TimeBudgetKind.Auditor, ReportType.CodeSmells).Minutes);
        Assert.Equal(500, Single(result, TimeBudgetKind.Auditor, ReportType.CodeSmells).Units);
    }

    [Fact]
    public void Each_curve_is_clamped_between_its_floor_and_its_ceiling()
    {
        var plan = PlanOf(Sel("tiny", 1, 0.9, 10), Sel("huge", 2, 0.5, 10_000_000));
        var many = PlanOf([.. Enumerable.Range(1, 40).Select(i => Sel($"s{i:00}", i, 1.0 / i, 1000))]);

        // Con los de por defecto la base ya es el suelo; un suelo por encima de la base sí se aplica.
        var floor = new TimeBudgetOptions { Investigation = new TimeBudgetCurve(1, 0.1, 3, 20) };
        var result = TimeBudgetCalculator.Calculate(Input(plan, totalLoc: 0, coefficients: floor));
        var big = TimeBudgetCalculator.Calculate(Input(many, totalLoc: 50_000_000));

        Assert.Equal(3, Single(result, TimeBudgetKind.Investigation, ReportType.ErroresYBugs, "tiny").Minutes);   // suelo
        Assert.Equal(20, Single(result, TimeBudgetKind.Investigation, ReportType.ErroresYBugs, "huge").Minutes);  // techo
        Assert.Equal(10.0, Single(result, TimeBudgetKind.Auditor, ReportType.CodigoMuerto).Minutes);                  // suelo
        Assert.Equal(30, Single(big, TimeBudgetKind.Consolidation, ReportType.ErroresYBugs).Minutes);              // techo: 5 + 40 > 30
        Assert.Equal(40, Single(big, TimeBudgetKind.Auditor, ReportType.CodigoMuerto).Minutes);                       // techo
    }

    [Fact]
    public void The_coefficients_come_from_the_options_and_are_recorded_in_the_budget()
    {
        var coefficients = new TimeBudgetOptions { Investigation = new TimeBudgetCurve(2, 0.5, 1, 100) };
        var plan = PlanOf(Sel("a", 1, 0.9, 20_000));

        var result = TimeBudgetCalculator.Calculate(Input(plan, coefficients: coefficients));

        Assert.Equal(12, Single(result, TimeBudgetKind.Investigation, ReportType.FlujosEndToEnd, "a").Minutes); // 2 + 0,5 · 20
        Assert.Equal(coefficients, result.Budget.Coefficients);
    }

    [Fact]
    public void Every_registered_report_is_budgeted_once_in_catalog_order_and_the_budget_keeps_its_inputs()
    {
        var plan = PlanOf(Sel("a", 1, 0.9, 5_000));

        var result = TimeBudgetCalculator.Calculate(Input(plan, totalLoc: 1_234_567, ceiling: 240, concurrency: 4));

        Assert.Equal(4 * 2 + 5, result.Budget.Entries.Count); // por informe en modo B: una investigación y la consolidación; en modo A, el auditor
        Assert.Equal(1_234_567, result.Budget.TotalProductionLoc);
        Assert.Equal(240, result.Budget.PipelineCeilingMinutes);
        Assert.Equal(4, result.Budget.EffectiveConcurrency);
        Assert.Equal("fp", result.Budget.GraphFingerprint);
        Assert.Equal(Now, result.Budget.GeneratedAt);
        Assert.Equal(result.Budget.Entries.Select(e => e.Report), result.Budget.Entries.Select(e => e.Report).OrderBy(r => r));
        Assert.Equal(result.Budget.Entries.Sum(e => e.Minutes), result.Budget.TotalMinutes, 3);
    }

    [Fact]
    public void Without_a_ceiling_nothing_is_trimmed_however_large_the_sum_is()
    {
        var plan = PlanOf([.. Enumerable.Range(1, 8).Select(i => Sel($"s{i}", i, 1.0 / i, 200_000))]);

        var result = TimeBudgetCalculator.Calculate(Input(plan, ceiling: 0, concurrency: 1));

        Assert.Empty(result.Budget.Trimmed);
        Assert.Empty(result.Budget.Warnings);
        Assert.All(result.EffectivePlan.Reports, r => Assert.Equal(8, r.Chosen.Count));
    }

    [Fact]
    public void A_sum_that_fits_the_ceiling_is_not_trimmed()
    {
        var plan = PlanOf(Sel("a", 1, 0.9, 10_000), Sel("b", 2, 0.5, 10_000));

        var result = TimeBudgetCalculator.Calculate(Input(plan, ceiling: 240, concurrency: 1));

        Assert.True(result.Budget.TotalMinutes < 240);
        Assert.Empty(result.Budget.Trimmed);
        Assert.Empty(result.Budget.Warnings);
    }

    [Fact]
    public void The_sum_is_divided_by_the_effective_concurrency_before_it_is_compared_with_the_ceiling()
    {
        var plan = PlanOf([.. Enumerable.Range(1, 8).Select(i => Sel($"s{i}", i, 1.0 / i, 200_000))]);

        var serial = TimeBudgetCalculator.Calculate(Input(plan, ceiling: 240, concurrency: 1));
        var parallel = TimeBudgetCalculator.Calculate(Input(plan, ceiling: 240, concurrency: 16));

        Assert.NotEmpty(serial.Budget.Trimmed);
        Assert.Empty(parallel.Budget.Trimmed);
        Assert.Equal(4 * 8, serial.Budget.Entries.Count(e => e.Kind == TimeBudgetKind.Investigation) + serial.Budget.Trimmed.Count);
    }

    [Fact]
    public void Over_the_ceiling_it_trims_the_lowest_scored_subsystems_first_one_report_at_a_time_and_says_so()
    {
        // Cuatro subsistemas de 100 000 líneas en cada informe: 4 · 14 + 9 = 65 min por informe, 260 en total; techo 200 con concurrencia 1.
        var plan = PlanOf(Sel("s1", 1, 0.9, 100_000), Sel("s2", 2, 0.8, 100_000), Sel("s3", 3, 0.7, 100_000), Sel("s4", 4, 0.6, 100_000));

        var result = TimeBudgetCalculator.Calculate(Input(plan, ceiling: 200, concurrency: 1, modeA: []));

        // Cada recorte quita 14 de la investigación y 1 de la consolidación: cuatro recortes dejan 200 y 200 no supera 200.
        Assert.Equal(
            [("AnalisisFuncional", "s4"), ("FlujosEndToEnd", "s4"), ("ErroresYBugs", "s4"), ("ArquitecturaAcoplamiento", "s4")],
            result.Budget.Trimmed.Select(t => (t.Report.ToString(), t.SubsystemId)));
        Assert.All(result.Budget.Trimmed, t => Assert.Equal("S4", t.Name));
        Assert.Equal(200, result.Budget.TotalMinutes, 3);
        Assert.All(result.EffectivePlan.Reports, r =>
        {
            Assert.Equal(["s1", "s2", "s3"], r.Chosen.Select(c => c.SubsystemId));
            var trimmed = Assert.Single(r.Excluded);
            Assert.Equal(("s4", ExclusionReasons.TrimmedByTimeBudget), (trimmed.SubsystemId, trimmed.Reason));
        });
        var warning = Assert.Single(result.Budget.Warnings);
        Assert.Contains("4", warning, StringComparison.Ordinal);
        Assert.Contains("S4", warning, StringComparison.Ordinal);
        Assert.Contains("PipelineMinutes", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void The_trimming_walks_up_the_ranking_and_leaves_the_best_subsystems_for_last()
    {
        var plan = PlanOf(Sel("s1", 1, 0.9, 100_000), Sel("s2", 2, 0.8, 100_000), Sel("s3", 3, 0.7, 100_000), Sel("s4", 4, 0.6, 100_000));

        var result = TimeBudgetCalculator.Calculate(Input(plan, ceiling: 130, concurrency: 1, modeA: []));

        var order = result.Budget.Trimmed.Select(t => t.SubsystemId).ToList();
        // Primero el cuarto de los cuatro informes, luego el tercero: nunca un subsistema de más puntuación antes que uno de menos.
        Assert.Equal(["s4", "s4", "s4", "s4", "s3", "s3", "s3", "s3"], order.Take(8));
        Assert.DoesNotContain("s1", order);
        Assert.All(result.EffectivePlan.Reports, r => Assert.Contains("s1", r.Chosen.Select(c => c.SubsystemId)));
    }

    [Fact]
    public void A_report_never_loses_its_last_subsystem_even_if_the_ceiling_cannot_be_met_and_the_warning_says_so()
    {
        var plan = PlanOf(Sel("s1", 1, 0.9, 100_000), Sel("s2", 2, 0.8, 100_000), Sel("s3", 3, 0.7, 100_000));

        var result = TimeBudgetCalculator.Calculate(Input(plan, ceiling: 1, concurrency: 1));

        Assert.All(result.EffectivePlan.Reports, r => Assert.Equal(["s1"], r.Chosen.Select(c => c.SubsystemId)));
        Assert.Equal(8, result.Budget.Trimmed.Count);
        Assert.Contains(result.Budget.Warnings, w => w.Contains("aun con", StringComparison.OrdinalIgnoreCase) || w.Contains("sigue superando", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Budget.TotalMinutes / 1 > 1);
    }

    [Fact]
    public void A_mode_b_report_with_nothing_chosen_or_missing_from_the_plan_is_budgeted_as_a_plain_auditor()
    {
        var plan = new CoveragePlan { GeneratedAt = Now, Reports = [new ReportCoverage { Report = ReportType.AnalisisFuncional }] };

        var result = TimeBudgetCalculator.Calculate(Input(plan, totalLoc: 300_000));

        // 10 + 0,03 · 300 = 19 min, como cualquier auditor; sin investigaciones ni consolidación.
        foreach (var report in ModeB)
            Assert.Equal(19, Single(result, TimeBudgetKind.Auditor, report).Minutes);
        Assert.DoesNotContain(result.Budget.Entries, e => e.Kind is TimeBudgetKind.Investigation or TimeBudgetKind.Consolidation);
    }

    [Fact]
    public void The_timeout_of_a_mode_b_wrapper_covers_its_waves_of_investigations_plus_the_consolidation()
    {
        // Seis subsistemas de 100 000 líneas (14 min) con 4 en paralelo: dos tandas de 14 min y una consolidación de 5 + 6 = 11.
        var plan = PlanOf([.. Enumerable.Range(1, 6).Select(i => Sel($"s{i}", i, 1.0 / i, 100_000))]);

        var result = TimeBudgetCalculator.Calculate(Input(plan, totalLoc: 100_000));

        Assert.Equal(2 * 14 + 11, result.Budget.ReportTimeouts.Single(t => t.Report == ReportType.FlujosEndToEnd).Minutes);
        // Un informe en modo A usa el límite de su auditor.
        Assert.Equal(13, result.Budget.ReportTimeouts.Single(t => t.Report == ReportType.CodeSmells).Minutes); // 10 + 0,03 · 100
        Assert.Equal(9, result.Budget.ReportTimeouts.Count);
    }

    [Fact]
    public void The_investigation_limits_of_a_report_are_a_table_by_subsystem_id_in_minutes_as_time_spans()
    {
        var plan = PlanOf(Sel("big", 1, 0.9, 100_000), Sel("small", 2, 0.5, 10_000));

        var result = TimeBudgetCalculator.Calculate(Input(plan));
        var table = TimeBudgetCalculator.InvestigationTimeouts(result.Budget, ReportType.ArquitecturaAcoplamiento);

        Assert.Equal(TimeSpan.FromMinutes(14), table["big"]);
        Assert.Equal(TimeSpan.FromMinutes(5), table["small"]);
        Assert.Equal(2, table.Count);
        Assert.Empty(TimeBudgetCalculator.InvestigationTimeouts(result.Budget, ReportType.CodeSmells));
    }

    [Fact]
    public void Zero_minutes_is_no_limit_and_anything_else_is_that_many_minutes()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, TimeBudgetCalculator.ToTimeSpan(0));
        Assert.Equal(TimeSpan.FromMinutes(7.5), TimeBudgetCalculator.ToTimeSpan(7.5));
    }

    [Fact]
    public void The_result_is_identical_between_runs()
    {
        var plan = PlanOf([.. Enumerable.Range(1, 8).Select(i => Sel($"s{i}", i, 1.0 / i, 37_000 * i))]);

        var first = PreparationArtifacts.Serialize(TimeBudgetCalculator.Calculate(Input(plan, ceiling: 100, concurrency: 2)).Budget);
        var second = PreparationArtifacts.Serialize(TimeBudgetCalculator.Calculate(Input(plan, ceiling: 100, concurrency: 2)).Budget);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_queue_factor_raises_every_limit_and_never_lowers_one()
    {
        var plan = PlanOf(Sel("a", 1, 0.9, 50_000), Sel("b", 2, 0.5, 10_000));
        var plain = TimeBudgetCalculator.Calculate(Input(plan, totalLoc: 100_000, concurrency: 9));

        var withFactor = TimeBudgetCalculator.Calculate(new TimeBudgetInput
        {
            Plan = plan, TotalProductionLoc = 100_000, ModeBReports = ModeB, ModeAReports = ModeA, EffectiveConcurrency = 9,
            QueueFactor = 16.0 / 9, MaxParallelInvestigations = 4, GeneratedAt = Now
        });

        Assert.Equal(1, plain.Budget.QueueFactor);
        Assert.Equal(16.0 / 9, withFactor.Budget.QueueFactor, 3);
        Assert.Equal(plain.Budget.Entries.Count, withFactor.Budget.Entries.Count);
        foreach (var (before, after) in plain.Budget.Entries.Zip(withFactor.Budget.Entries))
            Assert.Equal(Math.Round(before.Minutes * 16 / 9, 3), after.Minutes, 2);
        foreach (var (before, after) in plain.Budget.ReportTimeouts.Zip(withFactor.Budget.ReportTimeouts))
            Assert.True(after.Minutes > before.Minutes, $"{after.Report}: {before.Minutes} -> {after.Minutes}");
    }

    [Fact]
    public void The_queue_factor_does_not_change_what_is_trimmed_against_the_ceiling()
    {
        // La espera en cola ya está en la concurrencia efectiva (el mínimo de la deseada y la del limitador): la comparación con el
        // techo es la de siempre, y el recorte sigue funcionando con los mismos subsistemas.
        var plan = PlanOf([.. Enumerable.Range(1, 8).Select(i => Sel($"s{i}", i, 1.0 / i, 200_000))]);
        TimeBudgetInput WithFactor(double factor) => new()
        {
            Plan = plan, TotalProductionLoc = 100_000, ModeBReports = ModeB, ModeAReports = ModeA, EffectiveConcurrency = 9,
            QueueFactor = factor, PipelineCeilingMinutes = 60, MaxParallelInvestigations = 4, GeneratedAt = Now
        };

        var none = TimeBudgetCalculator.Calculate(WithFactor(1));
        var queued = TimeBudgetCalculator.Calculate(WithFactor(16.0 / 9));

        Assert.NotEmpty(none.Budget.Trimmed);
        Assert.Equal(none.Budget.Trimmed.Select(t => (t.Report, t.SubsystemId)), queued.Budget.Trimmed.Select(t => (t.Report, t.SubsystemId)));
    }
}
