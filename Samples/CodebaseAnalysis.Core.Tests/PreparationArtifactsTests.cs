using System.Text.Json;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// E/S de los artefactos de la preparación (T2): ida y vuelta, serialización estable (misma entrada = mismos bytes salvo
/// <c>generated_at</c>), orden de las listas respetado y lectura de un fichero ausente, corrupto o de otro formato como null con
/// aviso. Los registros llevan listas, así que se compara el texto serializado y no la igualdad de los registros.
/// </summary>
public sealed class PreparationArtifactsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _temp.Dispose();

    private static RepositoryInventory Inventory(DateTimeOffset at) => new()
    {
        GeneratedAt = at,
        GraphFingerprint = "demo|2026-10-04T10:00:00Z",
        Modules = [new InventoryModule { Path = "", Kind = "root" }, new InventoryModule { Path = "web", Kind = "maven", BuildFile = "pom.xml" }],
        SourceSets = [new SourceSetSummary { SourceSet = "production", Files = 3, Lines = 120 }, new SourceSetSummary { SourceSet = "test", Files = 1, Lines = 10 }],
        EntryPoints = [new InventoryEntryPoint { Kind = EntryPointKinds.Main, Name = "demo.App.main", File = "src/App.java", Line = 4 }],
        EntryPointsTotal = 1,
        Persistence = new InventoryPersistence { Tables = [new InventoryTable { Name = "ORDERS", References = 2 }], TablesTotal = 1, ConfigurationFiles = ["a.xml"], ConfigurationFilesTotal = 1 },
        Languages = [new LanguageStatus { Language = "java", Files = 3, Status = LanguageStatuses.Enriched, Enricher = "JavaAnalyzer", RunId = 7, Detail = "ok" }],
        GraphCoverage = new GraphCoverage
        {
            FilesOnDisk = 3, FilesInGraph = 2, FilesNotInGraph = 1, NotInGraphSample = ["src/C.java"],
            CbmMatch = new CbmMatchSummary { UnindexedFiles = 1, TypesUnmatchedRatio = 0.25, DriftSuspected = true }
        },
        ProductionLoc = new ProductionLines
        {
            Total = 120,
            ByLanguage = [new LanguageLines { Language = "java", Files = 3, Lines = 120 }],
            Files = [new FileLines { Path = "src/A.java", Language = "java", Lines = 50 }]
        },
        Warnings = ["aviso ñandú"]
    };

    private static DomainMap Map(DateTimeOffset at) => new()
    {
        GeneratedAt = at,
        GraphFingerprint = "demo|x",
        Basis = DomainMapBases.StructureOnly,
        Subsystems =
        [
            new Subsystem
            {
                Id = "s-a", Name = "Alfa", Description = "primero", Priority = SubsystemPriority.High, NameOrigin = SubsystemNameOrigins.Llm,
                Roots = ["a", "z/a"], DominantTerm = "alfa", Files = ["a/A.java", "z/a/B.java"], KeyTypes = ["demo.A"],
                EntryPoints = [new InventoryEntryPoint { Kind = EntryPointKinds.StrutsAction, Name = "/a" }], Tables = ["T1"],
                Metrics = new SubsystemMetrics { Files = 2, Loc = 80, Centrality = 0.4, EntryPoints = 1, EntryPointKinds = 1, Tables = 1 }
            },
            new Subsystem { Id = "s-b", Name = "Beta" }
        ],
        UnassignedFiles = ["u/U.java"]
    };

    private static CoveragePlan Plan(DateTimeOffset at) => new()
    {
        GeneratedAt = at,
        GraphFingerprint = "demo|x",
        MaxSubsystemsPerReport = 8,
        Scores = [new SubsystemScore { SubsystemId = "s-a", Name = "Alfa", Score = 2.5, Components = [new ScoreComponent { Name = "centrality", Value = 0.5, Weight = 3, Weighted = 1.5 }] }],
        Reports =
        [
            new ReportCoverage
            {
                Report = ReportType.AnalisisFuncional,
                Chosen = [new CoverageSelection { SubsystemId = "s-a", Name = "Alfa", Rank = 1, Score = 2.5, Loc = 80 }],
                LongTail = [new CoverageSelection { SubsystemId = "s-b", Name = "Beta", Rank = 2 }],
                Excluded = [new CoverageExclusion { SubsystemId = "s-b", Name = "Beta", Reason = ExclusionReasons.BelowTopK }],
                Questions = ["¿Qué entradas tiene?"],
                MinimumEvidence = ["un contraejemplo"],
                Failed = [new CoverageFailure { SubsystemId = "s-a", Name = "Alfa", Reason = "tiempo agotado" }]
            }
        ]
    };

    private static TimeBudgetPlan Budget(DateTimeOffset at) => new()
    {
        GeneratedAt = at,
        GraphFingerprint = "demo|x",
        TotalProductionLoc = 120_000,
        Coefficients = new TimeBudgetOptions { Warnings = ["no se serializa"] },
        PipelineCeilingMinutes = 240,
        EffectiveConcurrency = 4,
        TotalMinutes = 31.5,
        Entries =
        [
            new TimeBudgetEntry { Kind = TimeBudgetKind.Investigation, Report = ReportType.ErroresYBugs, SubsystemId = "s-a", Units = 80, Minutes = 12 },
            new TimeBudgetEntry { Kind = TimeBudgetKind.Auditor, Report = ReportType.CodeSmells, Units = 120, Minutes = 13.6 }
        ],
        Trimmed = [new TimeBudgetTrim { Report = ReportType.ErroresYBugs, SubsystemId = "s-z", Name = "Zeta" }]
    };

    [Fact]
    public void Every_artifact_round_trips_to_the_same_bytes()
    {
        RoundTrip(Inventory(T0));
        RoundTrip(Map(T0));
        RoundTrip(Plan(T0));
        RoundTrip(Budget(T0));
    }

    private void RoundTrip<T>(T artifact) where T : class, IPreparationArtifact
    {
        var folder = Path.Combine(_temp.Path, typeof(T).Name);
        Assert.Null(PreparationArtifacts.TryWrite(folder, artifact));
        Assert.True(File.Exists(Path.Combine(folder, PreparationArtifacts.FileNameOf<T>())));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));

        var read = PreparationArtifacts.TryRead<T>(folder, out var warning);
        Assert.Null(warning);
        Assert.NotNull(read);
        Assert.Equal(PreparationArtifacts.Serialize(artifact), PreparationArtifacts.Serialize(read));
        Assert.Equal(artifact.GeneratedAt, read.GeneratedAt);
        Assert.Equal(artifact.GraphFingerprint, read.GraphFingerprint);
    }

    [Fact]
    public void Two_runs_differ_only_in_generated_at()
    {
        var first = PreparationArtifacts.Serialize(Plan(T0));
        var second = PreparationArtifacts.Serialize(Plan(T0.AddHours(3)));

        Assert.NotEqual(first, second);
        Assert.Equal(StripGeneratedAt(first), StripGeneratedAt(second));
        Assert.Equal(first, PreparationArtifacts.Serialize(Plan(T0)));
    }

    private static string StripGeneratedAt(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"generated_at\": \"[^\"]*\"", "\"generated_at\": \"\"");

    [Fact]
    public void Serialization_is_snake_case_with_enums_as_text_and_keeps_the_order_of_the_lists()
    {
        var json = PreparationArtifacts.Serialize(Map(T0));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("structure_only", root.GetProperty("basis").GetString());
        Assert.Equal(["s-a", "s-b"], root.GetProperty("subsystems").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        var first = root.GetProperty("subsystems")[0];
        Assert.Equal("high", first.GetProperty("priority").GetString());
        Assert.Equal(["a", "z/a"], first.GetProperty("roots").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("struts_action", first.GetProperty("entry_points")[0].GetProperty("kind").GetString());
        Assert.Contains("aviso", PreparationArtifacts.Serialize(Inventory(T0)));
        Assert.Contains("ñandú", PreparationArtifacts.Serialize(Inventory(T0)));
        Assert.EndsWith("\n", json);

        using var budget = JsonDocument.Parse(PreparationArtifacts.Serialize(Budget(T0)));
        Assert.Equal("investigation", budget.RootElement.GetProperty("entries")[0].GetProperty("kind").GetString());
        Assert.Equal("ErroresYBugs", budget.RootElement.GetProperty("entries")[0].GetProperty("report").GetString());
        Assert.False(budget.RootElement.GetProperty("coefficients").TryGetProperty("warnings", out _));
    }

    [Fact]
    public void The_report_type_survives_the_round_trip_as_text()
    {
        var folder = Path.Combine(_temp.Path, "plan");
        PreparationArtifacts.TryWrite(folder, Plan(T0));

        var read = PreparationArtifacts.TryRead<CoveragePlan>(folder, out _)!;

        Assert.Equal(ReportType.AnalisisFuncional, read.Reports[0].Report);
        Assert.Equal("Alfa", read.Reports[0].Failed[0].Name);
        Assert.Equal(TimeBudgetKind.Auditor, PreparationArtifacts.TryRead<TimeBudgetPlan>(WriteBudget(), out _)!.Entries[1].Kind);
    }

    private string WriteBudget()
    {
        var folder = Path.Combine(_temp.Path, "budget");
        PreparationArtifacts.TryWrite(folder, Budget(T0));
        return folder;
    }

    [Fact]
    public void A_missing_file_gives_null_with_a_warning()
    {
        var read = PreparationArtifacts.TryRead<DomainMap>(_temp.Path, out var warning);

        Assert.Null(read);
        Assert.Contains(DomainMap.FileName, warning);
        Assert.Null(PreparationArtifacts.TryRead<DomainMap>("", out var noFolder));
        Assert.NotNull(noFolder);
    }

    [Theory]
    [InlineData("{ esto no es json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"schema_version\": 99}")]
    [InlineData("[1, 2]")]
    public void A_corrupt_or_foreign_file_gives_null_with_a_warning(string content)
    {
        File.WriteAllText(Path.Combine(_temp.Path, RepositoryInventory.FileName), content);

        var read = PreparationArtifacts.TryRead<RepositoryInventory>(_temp.Path, out var warning);

        Assert.Null(read);
        Assert.False(string.IsNullOrEmpty(warning));
    }

    [Fact]
    public void Writing_to_an_impossible_folder_is_a_warning_and_not_an_exception()
    {
        var blocker = Path.Combine(_temp.Path, "file");
        File.WriteAllText(blocker, "x");

        var warning = PreparationArtifacts.TryWrite(Path.Combine(blocker, "reports"), Plan(T0));

        Assert.False(string.IsNullOrEmpty(warning));
        Assert.NotNull(PreparationArtifacts.TryWrite("", Plan(T0)));
    }

    [Fact]
    public void Rewriting_replaces_the_previous_file()
    {
        PreparationArtifacts.TryWrite(_temp.Path, Plan(T0));
        PreparationArtifacts.TryWrite(_temp.Path, Plan(T0.AddHours(1)));

        Assert.Equal(T0.AddHours(1), PreparationArtifacts.TryRead<CoveragePlan>(_temp.Path, out _)!.GeneratedAt);
        Assert.Single(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void An_artifact_matches_the_graph_only_with_two_equal_non_empty_fingerprints()
    {
        var plan = Plan(T0);

        Assert.True(PreparationArtifacts.MatchesGraph(plan, "demo|x"));
        Assert.False(PreparationArtifacts.MatchesGraph(plan, "demo|y"));
        Assert.False(PreparationArtifacts.MatchesGraph(plan, null));
        Assert.False(PreparationArtifacts.MatchesGraph(plan, ""));
        Assert.False(PreparationArtifacts.MatchesGraph(plan with { GraphFingerprint = null }, null));
        Assert.False(PreparationArtifacts.MatchesGraph(null, "demo|x"));
    }

    [Fact]
    public void The_new_context_keys_are_distinct_from_each_other_and_from_the_existing_ones()
    {
        var keys = typeof(AnalysisContextKeys).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(AnalysisContextKeys.RepositoryInventory, keys);
        Assert.Contains(AnalysisContextKeys.DomainMap, keys);
        Assert.Contains(AnalysisContextKeys.CoveragePlan, keys);
        Assert.Contains(AnalysisContextKeys.TimeBudgetPlan, keys);
        Assert.Contains(AnalysisContextKeys.AuditorStepRegistry, keys);
    }

    [Fact]
    public void Only_the_four_artifacts_have_a_file_name()
    {
        Assert.Equal("_repository_inventory.json", PreparationArtifacts.FileNameOf<RepositoryInventory>());
        Assert.Equal("_domain_map.json", PreparationArtifacts.FileNameOf<DomainMap>());
        Assert.Equal("_coverage_plan.json", PreparationArtifacts.FileNameOf<CoveragePlan>());
        Assert.Equal("_time_budget.json", PreparationArtifacts.FileNameOf<TimeBudgetPlan>());
    }
}
