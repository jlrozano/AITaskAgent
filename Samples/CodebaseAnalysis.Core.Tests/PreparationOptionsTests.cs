using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Preparation;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <c>CodebaseAnalysis:Preparation</c> y <c>CodebaseAnalysis:TimeBudget</c> (T1 del plan de preparación de informes): los
/// valores por defecto aceptados (K = 8, concurrencia 4), la lectura desde configuración y el comportamiento ante un valor
/// inválido, que cae al defecto con un aviso en lugar de parar el análisis.
/// </summary>
public sealed class PreparationOptionsTests
{
    [Fact]
    public void Preparation_defaults_are_the_accepted_ones()
    {
        var options = PreparationOptions.FromConfiguration(TestConfig.FromPairs());

        Assert.Equal(PreparationMode.Subsystems, options.Mode);
        Assert.Equal(8, options.MaxSubsystemsPerReport);
        Assert.Equal(4, options.MaxParallelInvestigations);
        Assert.Equal(0.5, options.VocabularyMergeThreshold);
        Assert.Equal(5, options.MinSubsystemFiles);
        Assert.Equal(3, options.LongTailSize);
        Assert.Equal(0, options.ConsolidationMaxToolIterations);
        Assert.Equal(PreparationOptions.DefaultCountedExtensions, options.CountedExtensions);
        Assert.Equal(PreparationOptions.DefaultStopTokens, options.StopTokens);
        Assert.Equal(new SubsystemScoreWeights(), options.Weights);
        Assert.Empty(options.Warnings);
    }

    [Fact]
    public void Preparation_values_come_from_the_configuration()
    {
        var options = PreparationOptions.FromConfiguration(TestConfig.FromPairs(
            ("CodebaseAnalysis:Preparation:Mode", "off"),
            ("CodebaseAnalysis:Preparation:MaxSubsystemsPerReport", "3"),
            ("CodebaseAnalysis:Preparation:MaxParallelInvestigations", "1"),
            ("CodebaseAnalysis:Preparation:VocabularyMergeThreshold", "0.75"),
            ("CodebaseAnalysis:Preparation:MinSubsystemFiles", "2"),
            ("CodebaseAnalysis:Preparation:LongTailSize", "0"),
            ("CodebaseAnalysis:Preparation:ConsolidationMaxToolIterations", "0"),
            ("CodebaseAnalysis:Preparation:Weights:Centrality", "5"),
            ("CodebaseAnalysis:Preparation:Weights:Tables", "0"),
            ("CodebaseAnalysis:Preparation:CountedExtensions:0", "PY"),
            ("CodebaseAnalysis:Preparation:CountedExtensions:1", ".Go"),
            ("CodebaseAnalysis:Preparation:StopTokens:0", " Foo "),
            ("CodebaseAnalysis:Preparation:StopTokens:1", "bar"),
            ("CodebaseAnalysis:Preparation:StopTokens:2", "FOO")));

        Assert.Equal(PreparationMode.Off, options.Mode);
        Assert.Equal(3, options.MaxSubsystemsPerReport);
        Assert.Equal(1, options.MaxParallelInvestigations);
        Assert.Equal(0.75, options.VocabularyMergeThreshold);
        Assert.Equal(2, options.MinSubsystemFiles);
        Assert.Equal(0, options.LongTailSize);
        Assert.Equal(0, options.ConsolidationMaxToolIterations);
        Assert.Equal(5, options.Weights.Centrality);
        Assert.Equal(0, options.Weights.Tables);
        Assert.Equal(new SubsystemScoreWeights().EntryPoints, options.Weights.EntryPoints);
        Assert.Equal([".py", ".go"], options.CountedExtensions);
        Assert.Equal(["foo", "bar"], options.StopTokens);
        Assert.Empty(options.Warnings);
    }

    [Theory]
    [InlineData("Mode", "automatico")]
    [InlineData("MaxSubsystemsPerReport", "0")]
    [InlineData("MaxSubsystemsPerReport", "-2")]
    [InlineData("MaxParallelInvestigations", "muchas")]
    [InlineData("MinSubsystemFiles", "0")]
    [InlineData("VocabularyMergeThreshold", "1.5")]
    [InlineData("VocabularyMergeThreshold", "-0.1")]
    [InlineData("LongTailSize", "-1")]
    [InlineData("ConsolidationMaxToolIterations", "-1")]
    [InlineData("ConsolidationMaxToolIterations", "pocas")]
    [InlineData("Weights:Centrality", "-3")]
    public void An_invalid_preparation_value_falls_back_to_the_default_with_a_warning(string key, string value)
    {
        var options = PreparationOptions.FromConfiguration(TestConfig.FromPairs(($"CodebaseAnalysis:Preparation:{key}", value)));

        Assert.Equal(new PreparationOptions { Warnings = options.Warnings }, options);
        var warning = Assert.Single(options.Warnings);
        Assert.Contains(key.Split(':')[^1], warning);
        Assert.Contains(value, warning);
    }

    [Fact]
    public void The_report_profiles_default_to_the_shipped_ones_and_are_overridden_signal_by_signal()
    {
        var defaults = new ReportScoreProfiles();
        Assert.NotEqual(defaults.ErroresYBugs, ReportScoreProfiles.Neutral.ErroresYBugs);

        var options = PreparationOptions.FromConfiguration(TestConfig.FromPairs(
            ("CodebaseAnalysis:Preparation:ReportProfiles:ErroresYBugs:Integrations", "5"),
            ("CodebaseAnalysis:Preparation:ReportProfiles:AnalisisFuncional:Tables", "0")));

        Assert.Empty(options.Warnings);
        Assert.Equal(defaults.ErroresYBugs with { Integrations = 5 }, options.ReportProfiles.ErroresYBugs);
        Assert.Equal(defaults.AnalisisFuncional with { Tables = 0 }, options.ReportProfiles.AnalisisFuncional);
        Assert.Equal(defaults.FlujosEndToEnd, options.ReportProfiles.FlujosEndToEnd);
        Assert.Equal(defaults.ArquitecturaAcoplamiento, options.ReportProfiles.ArquitecturaAcoplamiento);
    }

    [Fact]
    public void A_report_without_a_profile_is_scored_with_the_base_weights()
    {
        var profiles = new ReportScoreProfiles();

        Assert.Equal(new SubsystemScoreProfile(), profiles.Of(ReportType.CodeSmells));
        Assert.Equal(new SubsystemScoreProfile(), ReportScoreProfiles.Neutral.Of(ReportType.ErroresYBugs));
        Assert.Equal(profiles.ArquitecturaAcoplamiento, profiles.Of(ReportType.ArquitecturaAcoplamiento));
    }

    [Theory]
    [InlineData("ErroresYBugs:Integrations", "-1")]
    [InlineData("AnalisisFuncional:EntryPoints", "mucho")]
    public void An_invalid_profile_factor_falls_back_to_the_default_with_a_warning(string key, string value)
    {
        var options = PreparationOptions.FromConfiguration(TestConfig.FromPairs(($"CodebaseAnalysis:Preparation:ReportProfiles:{key}", value)));

        Assert.Equal(new PreparationOptions { Warnings = options.Warnings }, options);
        var warning = Assert.Single(options.Warnings);
        Assert.Contains(key.Split(':')[^1], warning);
        Assert.Contains(value, warning);
    }

    [Fact]
    public void A_profile_for_a_report_that_does_not_investigate_by_subsystem_is_ignored_with_a_warning()
    {
        var options = PreparationOptions.FromConfiguration(TestConfig.FromPairs(
            ("CodebaseAnalysis:Preparation:ReportProfiles:CodeSmells:Tables", "9"),
            ("CodebaseAnalysis:Preparation:ReportProfiles:Inventado:Tables", "9")));

        Assert.Equal(new PreparationOptions { Warnings = options.Warnings }, options);
        Assert.Equal(2, options.Warnings.Count);
        Assert.Contains(options.Warnings, w => w.Contains("CodeSmells", StringComparison.Ordinal));
        Assert.Contains(options.Warnings, w => w.Contains("Inventado", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_mode_falls_back_to_Subsystems()
    {
        var options = PreparationOptions.FromConfiguration(TestConfig.FromPairs(("CodebaseAnalysis:Preparation:Mode", "Maybe")));

        Assert.Equal(PreparationMode.Subsystems, options.Mode);
        Assert.Single(options.Warnings);
    }

    [Fact]
    public void Without_a_configuration_the_services_give_the_defaults()
    {
        Assert.Equal(8, PreparationOptions.FromServices(null).MaxSubsystemsPerReport);
        Assert.Equal(new TimeBudgetOptions().Investigation, TimeBudgetOptions.FromServices(null).Investigation);
    }

    [Fact]
    public void TimeBudget_defaults_are_consistent_curves()
    {
        var options = TimeBudgetOptions.FromConfiguration(TestConfig.FromPairs());

        Assert.Empty(options.Warnings);
        foreach (var curve in new[] { options.Investigation, options.Consolidation, options.Auditor })
        {
            Assert.True(curve.MinMinutes > 0);
            Assert.True(curve.MaxMinutes >= curve.MinMinutes);
            Assert.True(curve.BaseMinutes >= 0 && curve.MinutesPerUnit >= 0);
        }
    }

    [Fact]
    public void TimeBudget_values_come_from_the_configuration()
    {
        var options = TimeBudgetOptions.FromConfiguration(TestConfig.FromPairs(
            ("CodebaseAnalysis:TimeBudget:Investigation:BaseMinutes", "2"),
            ("CodebaseAnalysis:TimeBudget:Investigation:MinutesPerUnit", "0.5"),
            ("CodebaseAnalysis:TimeBudget:Investigation:MinMinutes", "1"),
            ("CodebaseAnalysis:TimeBudget:Investigation:MaxMinutes", "10"),
            ("CodebaseAnalysis:TimeBudget:Auditor:MaxMinutes", "0")));

        Assert.Equal(new TimeBudgetCurve(2, 0.5, 1, 10), options.Investigation);
        // Una clave que falta conserva su defecto; el techo 0 quita el techo.
        Assert.Equal(new TimeBudgetOptions().Auditor with { MaxMinutes = 0 }, options.Auditor);
        Assert.Equal(new TimeBudgetOptions().Consolidation, options.Consolidation);
        Assert.Empty(options.Warnings);
    }

    [Fact]
    public void An_invalid_time_budget_curve_falls_back_as_a_whole_with_a_warning()
    {
        var options = TimeBudgetOptions.FromConfiguration(TestConfig.FromPairs(
            ("CodebaseAnalysis:TimeBudget:Investigation:BaseMinutes", "-4"),
            ("CodebaseAnalysis:TimeBudget:Investigation:MaxMinutes", "30"),
            ("CodebaseAnalysis:TimeBudget:Consolidation:MinMinutes", "20"),
            ("CodebaseAnalysis:TimeBudget:Consolidation:MaxMinutes", "10")));

        var defaults = new TimeBudgetOptions();
        Assert.Equal(defaults.Investigation, options.Investigation);
        Assert.Equal(defaults.Consolidation, options.Consolidation);
        Assert.Equal(2, options.Warnings.Count);
    }

    [Fact]
    public void A_curve_clamps_between_the_floor_and_the_ceiling()
    {
        var curve = new TimeBudgetCurve(BaseMinutes: 4, MinutesPerUnit: 0.1, MinMinutes: 5, MaxMinutes: 20);

        Assert.Equal(TimeSpan.FromMinutes(5), curve.Evaluate(0));
        Assert.Equal(TimeSpan.FromMinutes(5), curve.Evaluate(-50));
        Assert.Equal(TimeSpan.FromMinutes(9), curve.Evaluate(50));
        Assert.Equal(TimeSpan.FromMinutes(20), curve.Evaluate(1_000_000));
        Assert.Equal(TimeSpan.FromMinutes(1_004), (curve with { MaxMinutes = 0 }).Evaluate(10_000));
        Assert.Equal(Timeout.InfiniteTimeSpan, (curve with { MaxMinutes = 0 }).Evaluate(AnalysisTimeouts.MaxMinutes * 10.0));
    }

    /// <summary>El JSON que viaja con la librería declara los mismos valores que los defectos del código.</summary>
    [Fact]
    public void The_shipped_appsettings_declare_the_defaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.analysis.json"))
            .Build();

        var preparation = PreparationOptions.FromConfiguration(configuration);
        Assert.Empty(preparation.Warnings);
        var defaults = new PreparationOptions();
        Assert.Equal(defaults.VocabularyMergeThreshold, preparation.VocabularyMergeThreshold);
        Assert.Equal(defaults.MinSubsystemFiles, preparation.MinSubsystemFiles);
        Assert.Equal(defaults.MaxSubsystemShare, preparation.MaxSubsystemShare);
        Assert.Equal(defaults.PathTermWeightFactor, preparation.PathTermWeightFactor);
        Assert.Equal(defaults.SharedMinConsumers, preparation.SharedMinConsumers);
        Assert.Equal(defaults.SharedMinConsumerShare, preparation.SharedMinConsumerShare);
        Assert.Equal(defaults.LongTailSize, preparation.LongTailSize);
        Assert.Equal(defaults.ConsolidationMaxToolIterations, preparation.ConsolidationMaxToolIterations);
        Assert.Equal(0, preparation.ConsolidationMaxToolIterations);
        Assert.Equal(defaults.Weights, preparation.Weights);
        Assert.Equal(defaults.ReportProfiles, preparation.ReportProfiles);
        Assert.Equal(defaults.CountedExtensions, preparation.CountedExtensions);
        Assert.Equal(PreparationMode.Subsystems, preparation.Mode);
        // Sin tope de subsistemas por informe en la configuración que se entrega (decisión del 2026-10-15: no limitar herramientas, tiempo ni precio).
        Assert.Equal(50, preparation.MaxSubsystemsPerReport);
        Assert.Equal(4, preparation.MaxParallelInvestigations);
        Assert.Equal(PreparationOptions.DefaultStopTokens, preparation.StopTokens);

        var budget = TimeBudgetOptions.FromConfiguration(configuration);
        Assert.Empty(budget.Warnings);
        // Curvas a cero: cada pieza sale con 0 minutos, que es «sin límite» (decisión del 2026-10-15). Solo un techo (MaxMinutes) a 0
        // no basta: la curva base + k · unidades seguía dando unos 7 min por investigación, y todas las investigaciones se cortaban.
        var noLimit = new TimeBudgetCurve(0, 0, 0, 0);
        Assert.Equal(new TimeBudgetOptions { Investigation = noLimit, Consolidation = noLimit, Auditor = noLimit, Warnings = budget.Warnings }, budget);
        Assert.Equal(TimeSpan.Zero, budget.Investigation.Evaluate(50));  // 0 minutos = sin límite para TimeBudgetCalculator.ToTimeSpan
    }
}
