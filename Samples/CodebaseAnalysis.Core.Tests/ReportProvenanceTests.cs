using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Procedencia en la línea <c>analysis-meta</c> (recomendación 12 de la auditoría): el pipeline añade modelo, servicio, SHA
/// de las plantillas, huella del grafo, filas de <c>enrichment_runs</c>, fecha y versión, y los lectores de la línea siguen
/// sacando <c>health</c> y <c>critical</c> igual.
/// </summary>
public sealed class ReportProvenanceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly IReadOnlyList<KeyValuePair<string, string>> Pairs =
    [
        new("model", "gpt-5.4"),
        new("graph", "demo|2026-09-28T10:00:00Z|cbm=0.6.1"),
        new("enrichment", "JavaAnalyzer#3;StrutsJsp#4 -->")
    ];

    [Fact]
    public void The_values_replace_the_same_keys_keep_the_rest_and_cannot_break_the_line()
    {
        const string report = "# Errores\n\ntexto\n\n<!-- analysis-meta: health=Red; model=inventado; critical=2 -->\n";

        var result = ReportMarkdown.SetMetaValues(report, Pairs);

        Assert.EndsWith(
            "<!-- analysis-meta: health=Red; critical=2; model=gpt-5.4; graph=demo|2026-09-28T10:00:00Z|cbm=0.6.1; enrichment=JavaAnalyzer#3,StrutsJsp#4 -> -->\n",
            result);
        var parsed = new SingleReportResult { MarkdownContent = result };
        parsed.PopulateDerivedMetadata();
        Assert.Equal(("Red", 2), (parsed.QualityHealth, parsed.CriticalFindings));
    }

    [Fact]
    public void Without_a_meta_line_one_is_added_and_the_health_stays_unknown()
    {
        var result = ReportMarkdown.SetMetaValues("# Informe\n\ntexto", Pairs.Take(1).ToList());

        Assert.EndsWith("\n\n<!-- analysis-meta: model=gpt-5.4 -->\n", result);
        var parsed = new SingleReportResult { MarkdownContent = result };
        parsed.PopulateDerivedMetadata();
        Assert.Equal(SingleReportResult.UnknownHealth, parsed.QualityHealth);
    }

    /// <summary>Los otros dos lectores que reescriben la línea conservan la procedencia.</summary>
    [Fact]
    public void Fixing_critical_and_keeping_the_meta_on_a_chat_edit_preserve_the_provenance()
    {
        var report = ReportMarkdown.SetMetaValues("# E\n\n<!-- analysis-meta: health=Amber; critical=9 -->\n", Pairs.Take(2).ToList());

        var fixedCritical = ReportMarkdown.SetCriticalInMeta(report, 1);
        Assert.Contains("health=Amber; model=gpt-5.4; graph=demo|2026-09-28T10:00:00Z|cbm=0.6.1; critical=1", fixedCritical);

        var edited = ReportWriter.KeepAnalysisMeta(report, "# E\n\nOtro texto sin línea\n");
        Assert.EndsWith(ReportMarkdown.GetLastMetaLine(report) + "\n", edited);
    }

    [Fact]
    public void The_template_sha_follows_the_includes_and_not_the_data()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "sys.md"), "Eres auditor.\n@[_rules.md]\nLenguajes: {{ detected_languages }}");
        File.WriteAllText(Path.Combine(dir.Path, "_rules.md"), "Regla 1");

        var first = ReportProvenance.TemplateSha(new AnalysisTemplateProvider(dir.Path), "sys");
        var same = ReportProvenance.TemplateSha(new AnalysisTemplateProvider(dir.Path), "sys");
        File.WriteAllText(Path.Combine(dir.Path, "_rules.md"), "Regla 1 cambiada");
        var changedInclude = ReportProvenance.TemplateSha(new AnalysisTemplateProvider(dir.Path), "sys");

        Assert.Matches("^[0-9a-f]{12}$", first);
        Assert.Equal(first, same);
        Assert.NotEqual(first, changedInclude);
        Assert.Null(ReportProvenance.TemplateSha(new AnalysisTemplateProvider(dir.Path), "no_existe"));
        Assert.Null(ReportProvenance.TemplateSha(new AnalysisTemplateProvider(dir.Path), null));
    }

    [Fact]
    public async Task The_graph_provenance_is_the_fingerprint_and_the_last_ok_row_of_each_enricher_with_it()
    {
        using var temp = new TempDirectory();
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(temp.Path, "codebase.db"), "demo");
        var fingerprint = (await EnrichmentRuns.IndexFingerprintAsync(g.Connection, CancellationToken.None))!;
        Task<long> Insert(string enricher, string status, string index) => EnrichmentRuns.InsertAsync(g.Connection, new EnrichmentRun
        {
            Enricher = enricher,
            EnricherVersion = "1",
            IndexFingerprint = index,
            ConfigFingerprint = "cfg",
            StartedAt = DateTimeOffset.UnixEpoch,
            Status = status
        }, CancellationToken.None);

        await Insert("JavaAnalyzer", EnrichmentRuns.StatusOk, "otra|indexación");
        var java = await Insert("JavaAnalyzer", EnrichmentRuns.StatusOk, fingerprint);
        await Insert("JavaAnalyzer", EnrichmentRuns.StatusFailed, fingerprint);
        var struts = await Insert("StrutsJsp", EnrichmentRuns.StatusOk, fingerprint);

        var provenance = await GraphProvenance.ReadAsync(g.Connection, CancellationToken.None);

        Assert.Equal("demo|2026-09-28T10:00:00Z", provenance.Fingerprint);
        Assert.Equal($"JavaAnalyzer#{java},StrutsJsp#{struts}", provenance.EnrichmentRunsText);
    }

    [Fact]
    public async Task The_facet_step_leaves_the_graph_provenance_in_the_context()
    {
        using var temp = new TempDirectory();
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(temp.Path, "codebase.db"), "demo");
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.PipelineInput, new AnalysisPipelineInput { RepositoryPath = temp.Path, DatabasePath = g.DatabasePath });
        context.Set(AnalysisContextKeys.Reporter, new CapturingReporter());

        await new SqlFacetExtractorStep().ExecuteAsync(new GraphStatusResult { DatabasePath = g.DatabasePath }, context, 1, null, CancellationToken.None);

        Assert.Equal("demo|2026-09-28T10:00:00Z", context.Get<GraphProvenance>(AnalysisContextKeys.GraphProvenance)?.Fingerprint);
    }

    /// <summary>Las plantillas reales de los nueve auditores y de la síntesis se renderizan sin datos: todas tienen SHA.</summary>
    [Fact]
    public void Every_real_report_template_has_a_sha()
    {
        var provider = AnalysisPipelineFactory.CreateDefaultTemplateProvider();
        var prefixes = ReportCatalog.All.Select(d => AnalysisPipelineInput.GetTemplatePrefix(d.Type)).Append("report_sintesis").ToList();

        var shas = prefixes.SelectMany(p => new[] { $"{p}_system", $"{p}_prompt" })
            .ToDictionary(name => name, name => ReportProvenance.TemplateSha(provider, name));

        Assert.All(shas, s => Assert.Matches("^[0-9a-f]{12}$", s.Value));
        Assert.Equal(shas.Count, shas.Values.Distinct().Count());
        var example = ReportMarkdown.SetMetaValues("# 02\n\n<!-- analysis-meta: health=Amber -->\n", ReportProvenance.Build(
            DateTimeOffset.UtcNow, "gpt-5.4", "azure-openai", "gpt-5.4-2026-03-05", shas["report_codesmells_system"], shas["report_codesmells_prompt"],
            new GraphProvenance("Online|2026-09-29T08:12:44Z|cbm=0.6.1", ["JavaAnalyzer#41", "StrutsJsp#42"])));
        output.WriteLine(ReportMarkdown.GetLastMetaLine(example));
    }

    [Fact]
    public void Build_omits_what_is_unknown_and_the_response_model_when_it_is_the_same()
    {
        var at = new DateTimeOffset(2026, 9, 29, 18, 5, 7, TimeSpan.FromHours(2));

        var pairs = ReportProvenance.Build(at, "gpt-5.4", "azure-openai", "GPT-5.4", "aaaaaaaaaaaa", null, null);

        Assert.Equal(["generated_at", "model", "provider", "system_sha", "app"], pairs.Select(p => p.Key));
        Assert.Equal("2026-09-29T16:05:07Z", pairs[0].Value);
        Assert.Contains(ReportProvenance.Build(at, "gpt-5.4", null, "gpt-5.4-2026-03-05", null, null, null),
            p => p.Key == "response_model" && p.Value == "gpt-5.4-2026-03-05");
    }
}
