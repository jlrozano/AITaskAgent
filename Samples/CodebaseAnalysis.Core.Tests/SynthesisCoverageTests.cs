using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// La síntesis ejecutiva con cobertura (T25): <see cref="SynthesisCoverage"/> arma, del plan del disco y de los apartados «Cobertura»
/// de los informes, un resumen de lo que cada informe recorrió; <see cref="ExecutiveSynthesizerStep"/> se lo pasa a su prompt
/// (<c>{{ coverage_summary }}</c>) para que no presente como completo un análisis con subsistemas sin cubrir, y tolera su ausencia.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SynthesisCoverageTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string Reports => Path.Combine(_temp.Path, "repo", "reports");

    public void Dispose() => _temp.Dispose();

    private static CoveragePlan PlanOf(int scored, int maxPerReport, params (ReportType Type, string[] Chosen, int BelowTopK)[] reports) => new()
    {
        GeneratedAt = DateTimeOffset.UtcNow,
        GraphFingerprint = "fp",
        MaxSubsystemsPerReport = maxPerReport,
        Scores = [.. Enumerable.Range(0, scored).Select(i => new SubsystemScore { SubsystemId = $"s{i}", Name = $"S{i}", Score = 1.0 - i * 0.01 })],
        Reports =
        [
            .. reports.Select(r => new ReportCoverage
            {
                Report = r.Type,
                Chosen = [.. r.Chosen.Select((name, i) => new CoverageSelection { SubsystemId = name.ToLowerInvariant(), Name = name, Rank = i + 1, Score = 1, Loc = 10 })],
                Excluded = [.. Enumerable.Range(0, r.BelowTopK).Select(i => new CoverageExclusion { SubsystemId = $"x{i}", Name = $"X{i}", Reason = ExclusionReasons.BelowTopK })]
            })
        ]
    };

    private const string WithSection =
        "# Informe\n\n## Resumen\n\nTexto.\n\n## Cobertura\n\n- Facturación: cubierto.\n- Envíos: **no cubierto** (la investigación superó su límite).\n\n<!-- analysis-meta: health=Green -->\n";

    private static (ReportType, string, string) Report(ReportType type, string markdown) =>
        (type, AnalysisPipelineInput.GetReportFileName(type), markdown);

    // ───────────────────────────── El resumen ─────────────────────────────

    [Fact]
    public void The_summary_says_what_the_plan_chose_and_left_out_and_copies_the_coverage_section_of_the_report()
    {
        var plan = PlanOf(scored: 12, maxPerReport: 8, (ReportType.AnalisisFuncional, ["Facturación", "Envíos"], 10));

        var text = SynthesisCoverage.Build(plan, [Report(ReportType.AnalisisFuncional, WithSection)]);

        Assert.Contains("12 subsistemas de producción puntuados", text);
        Assert.Contains("como máximo 8", text);
        Assert.Contains("01_analisis_funcional.md (AnalisisFuncional): el plan eligió 2 de 12 subsistemas: Facturación, Envíos.", text);
        Assert.Contains("No investiga 10: 10 — Fuera de los de mayor puntuación", text);
        // Lo que el plan no sabe (el subsistema cuya investigación falló) viene del apartado del informe.
        Assert.Contains("| - Envíos: **no cubierto** (la investigación superó su límite).", text);
        Assert.DoesNotContain("analysis-meta", text);
    }

    [Fact]
    public void A_mode_b_report_without_the_section_is_flagged_as_not_declaring_its_coverage()
    {
        var plan = PlanOf(5, 8, (ReportType.FlujosEndToEnd, ["Facturación"], 4));

        var text = SynthesisCoverage.Build(plan, [Report(ReportType.FlujosEndToEnd, "# Flujos\n\nSin apartado.\n\n<!-- analysis-meta: health=Green -->\n")]);

        Assert.Contains("04_flujos_end_to_end.md", text);
        Assert.Contains("no lleva apartado «Cobertura»", text);
    }

    [Fact]
    public void A_report_with_a_section_but_no_plan_entry_still_contributes_its_own_declaration_and_one_with_neither_does_not_appear()
    {
        var plan = PlanOf(5, 8, (ReportType.AnalisisFuncional, ["Facturación"], 4));

        var text = SynthesisCoverage.Build(plan,
        [
            Report(ReportType.CodeSmells, "# Smells\n\n## Cobertura\n\nSe recorrió Facturación; Envíos quedó sin recorrer.\n"),
            Report(ReportType.CodigoMuerto, "# Muerto\n\nNada.\n")
        ]);

        Assert.Contains("02_code_smells.md (CodeSmells):", text);
        Assert.Contains("Envíos quedó sin recorrer", text);
        Assert.DoesNotContain("03_codigo_muerto.md", text);
    }

    [Fact]
    public void Without_a_plan_and_without_sections_the_summary_is_empty()
    {
        Assert.Equal(string.Empty, SynthesisCoverage.Build(null, [Report(ReportType.AnalisisFuncional, "# A\n\nTexto.\n")]));
        Assert.Equal(string.Empty, SynthesisCoverage.Build(PlanOf(3, 8), []));
    }

    [Fact]
    public void Without_a_plan_the_sections_of_the_reports_are_still_used()
    {
        var text = SynthesisCoverage.Build(null, [Report(ReportType.AnalisisFuncional, WithSection)]);

        Assert.Contains("01_analisis_funcional.md", text);
        Assert.Contains("Envíos: **no cubierto**", text);
        Assert.DoesNotContain("puntuados", text);
    }

    [Fact]
    public void The_summary_is_bounded_and_deterministic()
    {
        var big = "# I\n\n## Cobertura\n\n" + string.Join('\n', Enumerable.Range(0, 400).Select(i => $"- Subsistema número {i} cubierto")) + "\n";
        var plan = PlanOf(40, 40, (ReportType.AnalisisFuncional, [.. Enumerable.Range(0, 40).Select(i => $"Sub{i}")], 0));

        var first = SynthesisCoverage.Build(plan, [Report(ReportType.AnalisisFuncional, big)]);
        var second = SynthesisCoverage.Build(plan, [Report(ReportType.AnalisisFuncional, big)]);

        Assert.Equal(first, second);
        Assert.Contains($"(+{40 - SynthesisCoverage.MaxListedNames})", first);
        Assert.Contains("apartado recortado por tamaño", first);
        Assert.True(first.Length < 4000, $"mide {first.Length}");
    }

    // ───────────────────────────── La plantilla ─────────────────────────────

    [Fact]
    public void The_synthesis_prompt_carries_the_coverage_summary_and_tolerates_it_empty()
    {
        var templates = AnalysisPipelineFactory.CreateDefaultTemplateProvider();
        object Model(SynthesisInput input) => new { Input = input, Context = new Dictionary<string, object?>() };

        var withCoverage = templates.Render("report_sintesis_prompt", Model(new SynthesisInput { CoverageSummary = "RESUMEN-DE-COBERTURA-DE-PRUEBA" }))!;
        var empty = templates.Render("report_sintesis_prompt", Model(new SynthesisInput()))!;

        Assert.Contains("RESUMEN-DE-COBERTURA-DE-PRUEBA", withCoverage);
        Assert.DoesNotContain("{{", withCoverage);
        Assert.DoesNotContain("{{", empty);
        Assert.Contains("no presenta el análisis como completo", empty);
        Assert.Contains("Llega vacío cuando no hubo preparación", empty);
    }

    // ───────────────────────────── El paso ─────────────────────────────

    private sealed class CapturingLlm(string answer) : ILlmService
    {
        private readonly Lock _lock = new();
        private readonly List<string> _users = [];

        public string Name => "capture";

        public IReadOnlyList<string> Users
        {
            get
            {
                lock (_lock)
                {
                    return [.. _users];
                }
            }
        }

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _users.Add(request.Conversation.History.Messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content ?? "");
            }
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = answer, FinishReason = FinishReason.Stop }], tokensUsed: 10, promptTokens: 5, completionTokens: 5, model: "fake-model"));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private async Task<(ExecutiveSynthesizerStep Step, PipelineContext Context, CapturingReporter Reporter)> NewStepAsync(CapturingLlm llm, params (string, string?)[] config)
    {
        await Task.CompletedTask;
        var role = new LlmRole
        {
            Provider = new LlmProvider { Service = llm, BaseUrl = "http://fake.invalid/" },
            Model = "fake-model",
            Inference = new LlmInferenceSettings { Temperature = 0.1f, MaxTokens = 1000 }
        };
        var llmStep = new CustomStatelessTemplateLlmStep<SynthesisInput, AITaskAgent.LLM.Results.LlmStringStepResult>(
            AnalysisPipelineFactory.SynthesizerStepName, role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), "report_sintesis_prompt", "report_sintesis_system");
        var reporter = new CapturingReporter();
        var context = EnrichmentStepContext.Create(TestConfig.FromPairs(config), reporter, Path.Combine(_temp.Path, "repo"), Path.Combine(_temp.Path, "none.db"));
        // Sin base de datos ni repositorio de la entrada: la síntesis no reincorpora informes al grafo.
        context.Set(AnalysisContextKeys.PipelineInput, new AnalysisPipelineInput { OutputReportsPath = Reports });
        return (new ExecutiveSynthesizerStep(llmStep), context, reporter);
    }

    private void WriteReport(ReportType type, string markdown)
    {
        Directory.CreateDirectory(Reports);
        File.WriteAllText(Path.Combine(Reports, AnalysisPipelineInput.GetReportFileName(type)), markdown);
    }

    private static Task<AnalysisPipelineFinalResult> RunAsync(ExecutiveSynthesizerStep step, PipelineContext context) =>
        step.ExecuteAsync(new AITaskAgent.Core.StepResults.ParallelResult(step), context, 1, null, CancellationToken.None);

    [Fact]
    public async Task The_synthesis_input_mentions_the_subsystems_a_report_left_uncovered()
    {
        var llm = new CapturingLlm("# Resumen ejecutivo\n\n<!-- analysis-meta: health=Green -->");
        var (step, context, _) = await NewStepAsync(llm);
        WriteReport(ReportType.AnalisisFuncional, WithSection);
        Assert.Null(PreparationArtifacts.TryWrite(Reports, PlanOf(12, 8, (ReportType.AnalisisFuncional, ["Facturación", "Envíos"], 10))));

        var result = await RunAsync(step, context);

        Assert.False(result.HasError, result.Error?.Message);
        var prompt = Assert.Single(llm.Users);
        Assert.DoesNotContain("{{", prompt);
        Assert.Contains("Cobertura de los Informes", prompt);
        Assert.Contains("el plan eligió 2 de 12 subsistemas: Facturación, Envíos", prompt);
        Assert.Contains("Envíos: **no cubierto** (la investigación superó su límite).", prompt);
    }

    [Fact]
    public async Task Without_a_plan_or_sections_the_synthesis_is_written_as_before()
    {
        var llm = new CapturingLlm("# Resumen ejecutivo\n\n<!-- analysis-meta: health=Green -->");
        var (step, context, reporter) = await NewStepAsync(llm);
        WriteReport(ReportType.CodeSmells, "# Smells\n\nTexto.\n\n<!-- analysis-meta: health=Green -->\n");

        var result = await RunAsync(step, context);

        Assert.False(result.HasError, result.Error?.Message);
        var prompt = Assert.Single(llm.Users);
        Assert.DoesNotContain("{{", prompt);
        Assert.DoesNotContain("el plan eligió", prompt);
        Assert.DoesNotContain(reporter.Entries, e => e.Level is AnalysisLogLevel.Warning or AnalysisLogLevel.Error);
    }

    [Fact]
    public async Task With_the_preparation_off_a_plan_left_on_disk_by_an_earlier_run_is_not_used()
    {
        var llm = new CapturingLlm("# Resumen ejecutivo\n\n<!-- analysis-meta: health=Green -->");
        var (step, context, _) = await NewStepAsync(llm, ("CodebaseAnalysis:Preparation:Mode", "Off"));
        WriteReport(ReportType.AnalisisFuncional, WithSection);
        Assert.Null(PreparationArtifacts.TryWrite(Reports, PlanOf(12, 8, (ReportType.AnalisisFuncional, ["Facturación", "Envíos"], 10))));

        await RunAsync(step, context);

        Assert.DoesNotContain("el plan eligió", Assert.Single(llm.Users));
    }

    [Fact]
    public async Task The_summary_written_by_code_when_the_model_does_not_answer_keeps_the_coverage()
    {
        var llm = new CapturingLlm("");
        var (step, context, _) = await NewStepAsync(llm);
        WriteReport(ReportType.AnalisisFuncional, WithSection);
        Assert.Null(PreparationArtifacts.TryWrite(Reports, PlanOf(12, 8, (ReportType.AnalisisFuncional, ["Facturación", "Envíos"], 10))));

        var result = await RunAsync(step, context);

        var written = await File.ReadAllTextAsync(result.ExecutiveReportPath);
        Assert.Contains("fallback=true", written);
        Assert.Contains("## Cobertura de los Informes", written);
        Assert.Contains("Envíos: **no cubierto**", written);
    }
}
