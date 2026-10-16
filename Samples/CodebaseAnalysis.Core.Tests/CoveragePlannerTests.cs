using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="CoveragePlanner"/> (T13, criterio A3): puntuación determinista con los pesos configurables, exclusión de lo que
/// no es producción, los K primeros por informe en modo B más una cola larga de orden estable, motivos de cada exclusión,
/// preguntas leídas de las plantillas y evidencia mínima. Sin LLM y sin disco salvo las plantillas reales.
/// </summary>
public sealed class CoveragePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static Subsystem Sub(
        string id, long loc = 1000, double centrality = 0, int entries = 0, int kinds = 0, int tables = 0, int states = 0,
        int integrations = 0, int dynamic = 0, string sourceSet = SourceSets.Production) =>
        new()
        {
            Id = id,
            Name = char.ToUpperInvariant(id[0]) + id[1..],
            SourceSet = sourceSet,
            Metrics = new SubsystemMetrics
            {
                Files = 10, Loc = loc, Centrality = centrality, EntryPoints = entries, EntryPointKinds = kinds, Tables = tables,
                States = states, Integrations = integrations, DynamicUseSites = dynamic
            }
        };

    private static DomainMap MapOf(params Subsystem[] subsystems) =>
        new() { GeneratedAt = Now, GraphFingerprint = "fp-1", Subsystems = [.. subsystems.OrderBy(s => s.Id, StringComparer.Ordinal)] };

    private static CoveragePlan Plan(DomainMap map, PreparationOptions? options = null, ITemplateProvider? templates = null) =>
        CoveragePlanner.Plan(map, options ?? new PreparationOptions(), templates, Now);

    private sealed class FakeTemplates(Dictionary<string, string> templates) : ITemplateProvider
    {
        public string? GetTemplate(string name) => templates.GetValueOrDefault(name);

        public string? Render(string name, object model) => throw new NotSupportedException();
    }

    [Fact]
    public void It_scores_each_subsystem_with_the_configured_weights_over_signals_normalised_between_subsystems()
    {
        // Mayor de cada señal: centralidad 0,6; entradas 10 y 2 tipos; tablas 8; estados 2; integraciones 2; dinámico 1.
        var a = Sub("alpha", centrality: 0.6, entries: 10, kinds: 2, tables: 4, states: 2);
        var b = Sub("beta", centrality: 0.3, entries: 5, kinds: 1, tables: 8, integrations: 2, dynamic: 1);

        var plan = Plan(MapOf(a, b));

        var alpha = plan.Scores.Single(s => s.SubsystemId == "alpha");
        var beta = plan.Scores.Single(s => s.SubsystemId == "beta");
        // Pesos por defecto 3, 2, 2, 1, 1, 1 (suma 10).
        Assert.Equal(0.7, alpha.Score, 6);     // (3·1 + 2·1 + 2·0,5 + 1·1 + 0 + 0) / 10
        Assert.Equal(0.65, beta.Score, 6);     // (3·0,5 + 2·0,5 + 2·1 + 0 + 1·1 + 1·1) / 10
        Assert.Equal(["centrality", "entry_points", "tables", "states", "integrations", "dynamic_use_risk"], alpha.Components.Select(c => c.Name));
        Assert.Equal([1.0, 1.0, 0.5, 1.0, 0.0, 0.0], alpha.Components.Select(c => c.Value));
        Assert.Equal([3.0, 2.0, 2.0, 1.0, 1.0, 1.0], alpha.Components.Select(c => c.Weight));
        Assert.Equal([3.0, 2.0, 1.0, 1.0, 0.0, 0.0], alpha.Components.Select(c => c.Weighted));
        Assert.Equal(["alpha", "beta"], plan.Scores.Select(s => s.SubsystemId));
    }

    [Fact]
    public void The_weights_of_the_options_change_the_ranking_and_a_zero_weight_cancels_a_signal()
    {
        var a = Sub("alpha", centrality: 0.6, entries: 10, kinds: 2, tables: 4, states: 2);
        var b = Sub("beta", centrality: 0.3, entries: 5, kinds: 1, tables: 8, integrations: 2, dynamic: 1);
        var onlyTables = new PreparationOptions
        {
            Weights = new SubsystemScoreWeights { Centrality = 0, EntryPoints = 0, Tables = 1, States = 0, Integrations = 0, DynamicUseRisk = 0 }
        };

        var plan = Plan(MapOf(a, b), onlyTables);

        Assert.Equal(["beta", "alpha"], plan.Scores.Select(s => s.SubsystemId));
        Assert.Equal(1.0, plan.Scores[0].Score, 6);
        Assert.Equal(0.5, plan.Scores[1].Score, 6);
    }

    [Fact]
    public void With_every_weight_at_zero_the_scores_are_zero_and_the_ranking_falls_back_to_the_id()
    {
        var zero = new PreparationOptions { Weights = new SubsystemScoreWeights { Centrality = 0, EntryPoints = 0, Tables = 0, States = 0, Integrations = 0, DynamicUseRisk = 0 } };

        var plan = Plan(MapOf(Sub("beta", tables: 5), Sub("alpha", tables: 1)), zero);

        Assert.All(plan.Scores, s => Assert.Equal(0, s.Score));
        Assert.Equal(["alpha", "beta"], plan.Reports[0].Chosen.Select(c => c.SubsystemId));
    }

    [Fact]
    public void A_tie_in_the_score_is_broken_by_the_id_and_a_signal_that_nobody_has_is_zero()
    {
        var plan = Plan(MapOf(Sub("delta"), Sub("alpha"), Sub("charlie"), Sub("bravo")));

        Assert.All(plan.Scores, s => Assert.Equal(0, s.Score));
        Assert.Equal(["alpha", "bravo", "charlie", "delta"], plan.Scores.Select(s => s.SubsystemId));
        Assert.Equal([1, 2, 3, 4], plan.Reports[0].Chosen.Select(c => c.Rank));
    }

    [Theory]
    [InlineData(SourceSets.Test, ExclusionReasons.TestSource)]
    [InlineData(SourceSets.Qa, ExclusionReasons.QaSource)]
    [InlineData(SourceSets.Debug, ExclusionReasons.DebugSource)]
    [InlineData(SourceSets.Generated, ExclusionReasons.GeneratedSource)]
    [InlineData(SourceSets.Samples, ExclusionReasons.SamplesSource)]
    public void A_subsystem_that_is_not_production_is_excluded_with_its_reason_and_is_neither_scored_nor_chosen(string sourceSet, string reason)
    {
        var plan = Plan(MapOf(Sub("billing", tables: 3), Sub("noise", tables: 99, sourceSet: sourceSet)));

        Assert.Equal(["billing"], plan.Scores.Select(s => s.SubsystemId));
        foreach (var report in plan.Reports)
        {
            Assert.Equal(["billing"], report.Chosen.Select(c => c.SubsystemId));
            var excluded = Assert.Single(report.Excluded);
            Assert.Equal(("noise", reason), (excluded.SubsystemId, excluded.Reason));
        }
    }

    [Fact]
    public void An_unknown_source_set_is_not_guessed_and_the_subsystem_stays_eligible()
    {
        var plan = Plan(MapOf(Sub("billing"), Sub("mystery", sourceSet: SourceSets.Unknown)));

        Assert.Equal(["billing", "mystery"], plan.Reports[0].Chosen.Select(c => c.SubsystemId).Order(StringComparer.Ordinal));
        Assert.Empty(plan.Reports[0].Excluded);
    }

    [Fact]
    public void A_non_production_subsystem_does_not_distort_the_normalisation_of_the_others()
    {
        var withNoise = Plan(MapOf(Sub("a", tables: 2), Sub("b", tables: 4), Sub("noise", tables: 400, sourceSet: SourceSets.Test)));
        var without = Plan(MapOf(Sub("a", tables: 2), Sub("b", tables: 4)));

        Assert.Equal(without.Scores.Select(s => (s.SubsystemId, s.Score)), withNoise.Scores.Select(s => (s.SubsystemId, s.Score)));
    }

    [Fact]
    public void There_is_one_entry_per_mode_b_report_in_catalog_order()
    {
        var plan = Plan(MapOf(Sub("a")));

        Assert.Equal(
            [ReportType.AnalisisFuncional, ReportType.FlujosEndToEnd, ReportType.ErroresYBugs, ReportType.ArquitecturaAcoplamiento],
            plan.Reports.Select(r => r.Report));
        Assert.Equal(plan.Reports.Select(r => r.Report), CoveragePlanner.ModeBReports);
    }

    [Fact]
    public void At_most_k_subsystems_are_chosen_per_report_by_descending_score_even_with_hundreds()
    {
        var subsystems = Enumerable.Range(0, 300).Select(i => Sub($"s{i:000}", tables: i % 37, entries: i % 11, kinds: i % 3)).ToArray();

        var plan = Plan(MapOf(subsystems), new PreparationOptions { MaxSubsystemsPerReport = 8 });

        Assert.Equal(8, plan.MaxSubsystemsPerReport);
        foreach (var report in plan.Reports)
        {
            Assert.Equal(8, report.Chosen.Count);
            Assert.Equal(Enumerable.Range(1, 8), report.Chosen.Select(c => c.Rank));
            Assert.Equal(report.Chosen.Select(c => c.Score).OrderByDescending(x => x), report.Chosen.Select(c => c.Score));
            // Los elegidos son los de más puntuación de todos, con la puntuación de su propio informe.
            Assert.Equal(report.Scores.Take(8).Select(s => s.SubsystemId), report.Chosen.Select(c => c.SubsystemId));
            Assert.True(report.Chosen.Min(c => c.Score) >= report.Scores.Skip(8).Max(s => s.Score));
        }
    }

    [Fact]
    public void Every_subsystem_that_is_not_chosen_is_excluded_with_a_reason_ordered_by_id()
    {
        var subsystems = Enumerable.Range(0, 20).Select(i => Sub($"s{i:00}", tables: i)).Append(Sub("t-test", sourceSet: SourceSets.Test)).ToArray();

        var plan = Plan(MapOf(subsystems), new PreparationOptions { MaxSubsystemsPerReport = 5 });

        foreach (var report in plan.Reports)
        {
            var chosen = report.Chosen.Select(c => c.SubsystemId).ToHashSet();
            var excluded = report.Excluded.Select(e => e.SubsystemId).ToList();
            Assert.Equal(21, chosen.Count + excluded.Count);
            Assert.Empty(chosen.Intersect(excluded));
            Assert.Equal(excluded.Order(StringComparer.Ordinal), excluded);
            Assert.All(report.Excluded, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
            Assert.Equal(ExclusionReasons.TestSource, report.Excluded.Single(e => e.SubsystemId == "t-test").Reason);
            Assert.Equal(15, report.Excluded.Count(e => e.Reason == ExclusionReasons.BelowTopK));
        }
    }

    [Fact]
    public void The_long_tail_is_a_stable_sample_of_the_ones_below_the_top_k_spread_over_the_ranking()
    {
        var subsystems = Enumerable.Range(0, 30).Select(i => Sub($"s{i:00}", tables: 30 - i)).ToArray();
        var options = new PreparationOptions { MaxSubsystemsPerReport = 6, LongTailSize = 3 };

        var first = Plan(MapOf(subsystems), options);
        var second = Plan(MapOf([.. subsystems.Reverse()]), options);

        var tail = first.Reports[0].LongTail;
        Assert.Equal(3, tail.Count);
        var below = first.Reports[0].Excluded.Where(e => e.Reason == ExclusionReasons.BelowTopK).Select(e => e.SubsystemId).ToHashSet();
        Assert.All(tail, t => Assert.Contains(t.SubsystemId, below));
        Assert.DoesNotContain(tail.Select(t => t.SubsystemId), id => first.Reports[0].Chosen.Any(c => c.SubsystemId == id));
        // Repartida por el ranking (no las tres siguientes al top K): puestos crecientes, del principio al final de la cola.
        Assert.Equal(tail.Select(t => t.Rank).Order(), tail.Select(t => t.Rank));
        Assert.True(tail[0].Rank < 14 && tail[^1].Rank > 22, string.Join(",", tail.Select(t => t.Rank)));
        Assert.Equal(tail.Select(t => t.SubsystemId), second.Reports[0].LongTail.Select(t => t.SubsystemId));
    }

    [Fact]
    public void The_long_tail_takes_everything_that_is_left_when_it_is_shorter_than_its_size_and_none_when_the_size_is_zero()
    {
        var subsystems = Enumerable.Range(0, 7).Select(i => Sub($"s{i}", tables: 7 - i)).ToArray();

        var small = Plan(MapOf(subsystems), new PreparationOptions { MaxSubsystemsPerReport = 5, LongTailSize = 3 });
        var none = Plan(MapOf(subsystems), new PreparationOptions { MaxSubsystemsPerReport = 5, LongTailSize = 0 });

        Assert.Equal(["s5", "s6"], small.Reports[0].LongTail.Select(t => t.SubsystemId));
        Assert.Equal([6, 7], small.Reports[0].LongTail.Select(t => t.Rank));
        Assert.Empty(none.Reports[0].LongTail);
    }

    [Fact]
    public void With_fewer_subsystems_than_k_all_are_chosen_and_nothing_is_excluded()
    {
        var plan = Plan(MapOf(Sub("a"), Sub("b")), new PreparationOptions { MaxSubsystemsPerReport = 8 });

        Assert.All(plan.Reports, r =>
        {
            Assert.Equal(2, r.Chosen.Count);
            Assert.Empty(r.Excluded);
            Assert.Empty(r.LongTail);
        });
    }

    [Fact]
    public void The_selection_carries_the_name_score_rank_and_loc_of_the_subsystem()
    {
        var plan = Plan(MapOf(Sub("alpha", loc: 4200, tables: 3), Sub("beta", loc: 100, tables: 1)));

        var first = plan.Reports[0].Chosen[0];
        Assert.Equal(("alpha", "Alpha", 1, 4200L), (first.SubsystemId, first.Name, first.Rank, first.Loc));
        Assert.Equal(plan.Reports[0].Scores[0].Score, first.Score);
    }

    [Fact]
    public void The_plan_is_identical_between_runs_and_does_not_depend_on_the_order_of_the_subsystems()
    {
        var subsystems = Enumerable.Range(0, 40).Select(i => Sub($"s{i:00}", tables: i % 9, entries: i % 5, kinds: i % 2, states: i % 4)).ToArray();
        var options = new PreparationOptions { MaxSubsystemsPerReport = 6 };

        var one = PreparationArtifacts.Serialize(Plan(MapOf(subsystems), options));
        var two = PreparationArtifacts.Serialize(Plan(MapOf(subsystems), options));
        var shuffled = PreparationArtifacts.Serialize(Plan(
            new DomainMap { GeneratedAt = Now, GraphFingerprint = "fp-1", Subsystems = [.. subsystems.OrderByDescending(s => s.Id, StringComparer.Ordinal)] }, options));

        Assert.Equal(one, two);
        Assert.Equal(one, shuffled);
    }

    [Fact]
    public void The_plan_records_the_fingerprint_of_the_graph_the_map_was_computed_on()
    {
        var plan = Plan(MapOf(Sub("a")));

        Assert.Equal("fp-1", plan.GraphFingerprint);
        Assert.Equal(Now, plan.GeneratedAt);
    }

    [Fact]
    public void The_questions_come_from_the_template_of_each_report_and_not_from_the_code()
    {
        var templates = new FakeTemplates(new Dictionary<string, string>
        {
            ["_investigation_questions_funcional"] = "- ¿Primera funcional?\n- ¿Segunda funcional?\n\nuna nota que no es pregunta\n",
            ["_investigation_questions_flujos"] = "- ¿Flujos?\r\n",
            ["_investigation_questions_errores"] = "  - ¿Errores con sangría?\n",
            ["_investigation_questions_arquitectura"] = "- ¿Arquitectura?\n- ¿Otra?\n- ¿Tercera?\n"
        });

        var plan = Plan(MapOf(Sub("a")), templates: templates);

        Assert.Equal(["¿Primera funcional?", "¿Segunda funcional?"], plan.Reports.Single(r => r.Report == ReportType.AnalisisFuncional).Questions);
        Assert.Equal(["¿Flujos?"], plan.Reports.Single(r => r.Report == ReportType.FlujosEndToEnd).Questions);
        Assert.Equal(["¿Errores con sangría?"], plan.Reports.Single(r => r.Report == ReportType.ErroresYBugs).Questions);
        Assert.Equal(3, plan.Reports.Single(r => r.Report == ReportType.ArquitecturaAcoplamiento).Questions.Count);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void The_shipped_question_templates_give_questions_for_the_four_reports()
    {
        var provider = AnalysisPipelineFactory.CreateDefaultTemplateProvider();

        var plan = Plan(MapOf(Sub("a")), templates: provider);

        Assert.All(plan.Reports, r =>
        {
            Assert.True(r.Questions.Count >= 5, $"{r.Report}: {r.Questions.Count} preguntas");
            Assert.All(r.Questions, q => Assert.Contains("¿", q));
        });
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_missing_question_template_is_a_warning_naming_it_and_the_report_gets_no_questions()
    {
        var templates = new FakeTemplates(new Dictionary<string, string> { ["_investigation_questions_funcional"] = "- ¿Una?\n" });

        var plan = Plan(MapOf(Sub("a")), templates: templates);

        Assert.Equal(["¿Una?"], plan.Reports[0].Questions);
        Assert.Empty(plan.Reports[1].Questions);
        Assert.Contains(plan.Warnings, w => w.Contains("_investigation_questions_flujos", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("_investigation_questions_errores", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("_investigation_questions_arquitectura", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("_investigation_questions_funcional", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_template_provider_there_are_no_questions_and_one_warning_says_so()
    {
        var plan = Plan(MapOf(Sub("a")), templates: null);

        Assert.All(plan.Reports, r => Assert.Empty(r.Questions));
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void The_minimum_evidence_asks_for_entry_points_complete_flows_alternative_paths_and_at_least_one_counterexample()
    {
        var plan = Plan(MapOf(Sub("a")));

        Assert.All(plan.Reports, r =>
        {
            Assert.Equal(4, r.MinimumEvidence.Count);
            var text = string.Join('\n', r.MinimumEvidence);
            Assert.Contains("entrada", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("flujo", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("alternativo", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("contraejemplo", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void An_empty_map_gives_reports_with_nothing_chosen_and_a_warning()
    {
        var plan = Plan(MapOf());

        Assert.Equal(4, plan.Reports.Count);
        Assert.All(plan.Reports, r =>
        {
            Assert.Empty(r.Chosen);
            Assert.Empty(r.Excluded);
        });
        Assert.Empty(plan.Scores);
        Assert.Single(plan.Warnings, w => w.Contains("subsistemas", StringComparison.Ordinal));
    }

    [Fact]
    public void A_map_with_only_non_production_subsystems_chooses_nothing_and_says_why()
    {
        var plan = Plan(MapOf(Sub("t1", sourceSet: SourceSets.Test), Sub("g1", sourceSet: SourceSets.Generated)));

        Assert.All(plan.Reports, r =>
        {
            Assert.Empty(r.Chosen);
            Assert.Equal(2, r.Excluded.Count);
        });
        Assert.Contains(plan.Warnings, w => w.Contains("producción", StringComparison.Ordinal));
    }

    [Fact]
    public void The_empty_plan_has_no_reports_and_carries_the_warning_it_is_given()
    {
        var plan = CoveragePlanner.Empty(new PreparationOptions { MaxSubsystemsPerReport = 5 }, "sin mapa", Now);

        Assert.Empty(plan.Reports);
        Assert.Empty(plan.Scores);
        Assert.Equal(5, plan.MaxSubsystemsPerReport);
        Assert.Equal(["sin mapa"], plan.Warnings);
        Assert.Null(plan.GraphFingerprint);
    }

    // ───────────────────────────── Perfiles de puntuación por informe ─────────────────────────────

    // Tres subsistemas con señales opuestas (nombres genéricos): uno de muchas entradas, estados y tablas con poca centralidad;
    // uno de infraestructura compartida (muy llamado, casi sin entradas); uno de integraciones y uso dinámico.
    private static DomainMap ThreeProfilesMap() => MapOf(
        Sub("forms", centrality: 0.05, entries: 30, kinds: 3, tables: 8, states: 6),
        Sub("kernel", centrality: 0.7, entries: 1, kinds: 1, tables: 1, integrations: 1),
        Sub("gateways", centrality: 0.2, entries: 2, kinds: 1, tables: 1, integrations: 9, dynamic: 7));

    private static string Text(IReadOnlyList<SubsystemScore> scores) => System.Text.Json.JsonSerializer.Serialize(scores);

    private static ReportCoverage Of(CoveragePlan plan, ReportType report) => plan.Reports.Single(r => r.Report == report);

    [Fact]
    public void Each_mode_b_report_chooses_the_subsystems_its_scoring_profile_favours()
    {
        var plan = Plan(ThreeProfilesMap(), new PreparationOptions { MaxSubsystemsPerReport = 1 });

        // Con los pesos base manda la mezcla de siempre: el de más entradas, estados y tablas.
        Assert.Equal("forms", plan.Scores[0].SubsystemId);
        Assert.Equal(["forms"], Of(plan, ReportType.AnalisisFuncional).Chosen.Select(c => c.SubsystemId));
        Assert.Equal(["forms"], Of(plan, ReportType.FlujosEndToEnd).Chosen.Select(c => c.SubsystemId));
        // Errores pesa integraciones, uso dinámico y centralidad; arquitectura, centralidad.
        Assert.Equal(["gateways"], Of(plan, ReportType.ErroresYBugs).Chosen.Select(c => c.SubsystemId));
        Assert.Equal(["kernel"], Of(plan, ReportType.ArquitecturaAcoplamiento).Chosen.Select(c => c.SubsystemId));
    }

    [Fact]
    public void The_default_profiles_weigh_what_each_report_is_about()
    {
        var profiles = new PreparationOptions().ReportProfiles;
        var baseWeights = new SubsystemScoreWeights();

        double Effective(ReportType report, Func<SubsystemScoreWeights, double> weight, Func<SubsystemScoreProfile, double> factor) =>
            weight(baseWeights) * factor(profiles.Of(report));

        // Funcional y flujos: entradas, estados y tablas por encima de la centralidad y de las integraciones.
        foreach (var report in new[] { ReportType.AnalisisFuncional, ReportType.FlujosEndToEnd })
        {
            var centrality = Effective(report, w => w.Centrality, p => p.Centrality);
            Assert.True(Effective(report, w => w.EntryPoints, p => p.EntryPoints) > centrality, $"{report}: entradas");
            Assert.True(Effective(report, w => w.States, p => p.States) > centrality, $"{report}: estados");
            Assert.True(Effective(report, w => w.Tables, p => p.Tables) > centrality, $"{report}: tablas");
            Assert.True(Effective(report, w => w.EntryPoints, p => p.EntryPoints) > Effective(report, w => w.Integrations, p => p.Integrations), $"{report}: integraciones");
        }

        // Errores: integraciones, uso dinámico y centralidad por encima de entradas, estados y tablas.
        var errors = ReportType.ErroresYBugs;
        var errorsEntries = Effective(errors, w => w.EntryPoints, p => p.EntryPoints);
        Assert.True(Effective(errors, w => w.Integrations, p => p.Integrations) > errorsEntries);
        Assert.True(Effective(errors, w => w.DynamicUseRisk, p => p.DynamicUseRisk) > errorsEntries);
        Assert.True(Effective(errors, w => w.Centrality, p => p.Centrality) >= errorsEntries);
        Assert.True(Effective(errors, w => w.Integrations, p => p.Integrations) > Effective(errors, w => w.States, p => p.States));

        // Arquitectura: la centralidad pesa más que ninguna otra señal y más que en los informes funcionales.
        var architecture = ReportType.ArquitecturaAcoplamiento;
        var archCentrality = Effective(architecture, w => w.Centrality, p => p.Centrality);
        Assert.True(archCentrality > Effective(architecture, w => w.EntryPoints, p => p.EntryPoints));
        Assert.True(archCentrality > Effective(architecture, w => w.Tables, p => p.Tables));
        Assert.True(archCentrality > Effective(architecture, w => w.States, p => p.States));
        Assert.True(archCentrality > Effective(architecture, w => w.Integrations, p => p.Integrations));
        Assert.True(archCentrality > Effective(ReportType.AnalisisFuncional, w => w.Centrality, p => p.Centrality));
    }

    [Fact]
    public void Each_report_carries_its_own_scores_and_the_global_scores_stay_those_of_the_base_weights()
    {
        var map = ThreeProfilesMap();

        var profiled = Plan(map);
        var flat = Plan(map, new PreparationOptions { ReportProfiles = ReportScoreProfiles.Neutral });

        Assert.Equal(Text(flat.Scores), Text(profiled.Scores));
        foreach (var report in profiled.Reports)
        {
            Assert.Equal(3, report.Scores.Count);
            Assert.Equal(report.Scores.OrderByDescending(s => s.Score).ThenBy(s => s.SubsystemId, StringComparer.Ordinal).Select(s => s.SubsystemId),
                report.Scores.Select(s => s.SubsystemId));
            Assert.Equal(report.Scores.Select(s => (s.SubsystemId, s.Score)), report.Chosen.Select(c => (c.SubsystemId, c.Score)));
        }

        // Los componentes de un informe llevan el peso efectivo: el base por el factor de su perfil.
        var errors = Of(profiled, ReportType.ErroresYBugs).Scores[0];
        var profile = new PreparationOptions().ReportProfiles.Of(ReportType.ErroresYBugs);
        Assert.Equal(new SubsystemScoreWeights().Integrations * profile.Integrations, errors.Components.Single(c => c.Name == "integrations").Weight, 6);
    }

    [Fact]
    public void Without_profiles_every_report_is_scored_with_the_base_weights_and_chooses_the_same()
    {
        var plan = Plan(ThreeProfilesMap(), new PreparationOptions { MaxSubsystemsPerReport = 2, ReportProfiles = ReportScoreProfiles.Neutral });

        Assert.All(plan.Reports, r => Assert.Equal(plan.Scores.Take(2).Select(s => s.SubsystemId), r.Chosen.Select(c => c.SubsystemId)));
        Assert.All(plan.Reports, r => Assert.Equal(Text(plan.Scores), Text(r.Scores)));
    }

    [Fact]
    public void A_zero_factor_cancels_that_signal_for_that_report_only()
    {
        var onlyTables = new SubsystemScoreProfile { Centrality = 0, EntryPoints = 0, Tables = 1, States = 0, Integrations = 0, DynamicUseRisk = 0 };
        var options = new PreparationOptions
        {
            MaxSubsystemsPerReport = 1,
            ReportProfiles = ReportScoreProfiles.Neutral with { ErroresYBugs = onlyTables }
        };
        var map = MapOf(Sub("alpha", centrality: 0.9, entries: 20, kinds: 2, tables: 1), Sub("beta", tables: 6));

        var plan = Plan(map, options);

        var errors = Of(plan, ReportType.ErroresYBugs);
        Assert.Equal(["beta"], errors.Chosen.Select(c => c.SubsystemId));
        Assert.Equal(1.0, errors.Scores[0].Score, 6);
        Assert.Equal(1.0 / 6, errors.Scores[1].Score, 6);
        Assert.Equal(["alpha"], Of(plan, ReportType.AnalisisFuncional).Chosen.Select(c => c.SubsystemId)); // sin perfil: pesos base
        Assert.Equal("alpha", plan.Scores[0].SubsystemId);
    }

    [Fact]
    public void The_plan_with_profiles_is_identical_between_runs_and_does_not_depend_on_the_order_of_the_subsystems()
    {
        var subsystems = Enumerable.Range(0, 60).Select(i => Sub(
            $"s{i:00}", centrality: (i % 7) / 10.0, tables: i % 9, entries: i % 5, kinds: i % 2, states: i % 4, integrations: i % 3, dynamic: i % 2)).ToArray();
        var options = new PreparationOptions { MaxSubsystemsPerReport = 6 };

        var one = PreparationArtifacts.Serialize(Plan(MapOf(subsystems), options));
        var two = PreparationArtifacts.Serialize(Plan(MapOf(subsystems), options));
        var shuffled = PreparationArtifacts.Serialize(Plan(
            new DomainMap { GeneratedAt = Now, GraphFingerprint = "fp-1", Subsystems = [.. subsystems.OrderByDescending(s => s.Id, StringComparer.Ordinal)] }, options));

        Assert.Equal(one, two);
        Assert.Equal(one, shuffled);
    }

    [Fact]
    public void The_excluded_below_k_and_the_long_tail_are_those_of_each_reports_own_ranking()
    {
        var plan = Plan(ThreeProfilesMap(), new PreparationOptions { MaxSubsystemsPerReport = 1, LongTailSize = 1 });

        foreach (var report in plan.Reports)
        {
            var chosen = report.Chosen.Single().SubsystemId;
            Assert.DoesNotContain(report.Excluded, e => e.SubsystemId == chosen);
            Assert.Equal(2, report.Excluded.Count(e => e.Reason == ExclusionReasons.BelowTopK));
            Assert.All(report.LongTail, t => Assert.NotEqual(chosen, t.SubsystemId));
            Assert.All(report.LongTail, t => Assert.Equal(report.Scores.Single(s => s.SubsystemId == t.SubsystemId).Score, t.Score));
        }
    }

    [Fact]
    public void Only_the_requested_reports_are_planned_and_none_means_no_mode_b_report()
    {
        var map = MapOf(Sub("a", entries: 3), Sub("b", tables: 2), Sub("c", states: 1));

        var all = CoveragePlanner.Plan(map, new PreparationOptions(), null, Now);
        var some = CoveragePlanner.Plan(map, new PreparationOptions(), null, Now, [ReportType.ErroresYBugs, ReportType.CodeSmells]);
        var none = CoveragePlanner.Plan(map, new PreparationOptions(), null, Now, [ReportType.CodigoMuerto]);

        Assert.Equal(CoveragePlanner.ModeBReports, all.Reports.Select(r => r.Report));
        // Un informe que no es de modo B no entra aunque se pida, y el orden es el del catálogo y no el de la petición.
        Assert.Equal([ReportType.ErroresYBugs], some.Reports.Select(r => r.Report));
        Assert.Empty(none.Reports);
        // Lo que se planifica es igual con o sin el recorte de informes: el de uno es el mismo que el de ese informe en el plan completo.
        Assert.Equal(PreparationArtifacts.Serialize(all with { Reports = [all.Reports.Single(r => r.Report == ReportType.ErroresYBugs)] }),
            PreparationArtifacts.Serialize(some));
        Assert.Equal(Json(all with { Reports = [] }), Json(none));
    }

    /// <summary>Los registros llevan listas, que se comparan por referencia: se comparan serializados.</summary>
    private static string Json(CoveragePlan plan) => PreparationArtifacts.Serialize(plan);

    private static string Json(ReportCoverage report) => Json(new CoveragePlan { Reports = [report] });

    [Fact]
    public void The_plan_of_some_reports_takes_the_others_from_a_previous_plan_of_the_same_graph_and_never_from_another_graph()
    {
        var map = MapOf(Sub("a", entries: 3), Sub("b", tables: 2));
        var previous = CoveragePlanner.Plan(map, new PreparationOptions(), null, Now);
        var partial = CoveragePlanner.Plan(map, new PreparationOptions { MaxSubsystemsPerReport = 1 }, null, Now, [ReportType.FlujosEndToEnd]);

        var merged = CoveragePlanner.WithCarriedOverReports(partial, previous);

        // El del informe pedido es el nuevo y los demás vienen del plan anterior, todos en el orden del catálogo.
        Assert.Equal(CoveragePlanner.ModeBReports, merged.Reports.Select(r => r.Report));
        Assert.Single(merged.Reports.Single(r => r.Report == ReportType.FlujosEndToEnd).Chosen);
        Assert.Equal(Json(previous.Reports.Single(r => r.Report == ReportType.AnalisisFuncional)), Json(merged.Reports.Single(r => r.Report == ReportType.AnalisisFuncional)));
        Assert.Equal(Json(partial with { Reports = [] }), Json(merged with { Reports = [] }));

        var otherGraph = previous with { GraphFingerprint = "fp-otro" };
        Assert.Same(partial, CoveragePlanner.WithCarriedOverReports(partial, otherGraph));
        Assert.Same(partial, CoveragePlanner.WithCarriedOverReports(partial, null));
        var complete = CoveragePlanner.Plan(map, new PreparationOptions(), null, Now);
        Assert.Same(complete, CoveragePlanner.WithCarriedOverReports(complete, previous));
        var withoutFingerprint = partial with { GraphFingerprint = null };
        Assert.Same(withoutFingerprint, CoveragePlanner.WithCarriedOverReports(withoutFingerprint, previous with { GraphFingerprint = null }));
    }
}
