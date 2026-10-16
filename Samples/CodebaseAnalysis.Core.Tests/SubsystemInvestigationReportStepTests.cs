using System.Text.RegularExpressions;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Notes;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using Microsoft.Extensions.Logging;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="SubsystemInvestigationReportStep"/> (T21, criterio A5) con un LLM de mentira que distingue investigaciones de
/// consolidación por el texto del prompt: N subsistemas son N investigaciones y una consolidación, un subsistema que falla o supera su
/// límite queda «no cubierto» y el informe se escribe igual, si fallan todos hay error, y sin subsistemas elegidos el paso es un auditor
/// normal. Las plantillas son propias del test (en una carpeta temporal) y se pintan con el motor real, así que también prueban que los
/// siete placeholders de la entrada de la investigación se resuelven.
/// </summary>
public sealed class SubsystemInvestigationReportStepTests : IDisposable
{
    private const ReportType Report = ReportType.FlujosEndToEnd;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string ReportsPath => Path.Combine(_temp.Path, "repo", "reports");
    private string ReportFile => Path.Combine(ReportsPath, AnalysisPipelineInput.GetReportFileName(Report));

    // ───────────────────────────── LLM de mentira ─────────────────────────────

    private sealed record Call(bool IsInvestigation, string? Subsystem, string User, string System, bool WithTools);

    /// <summary>
    /// La consolidación empieza por <c>CONSOLIDACION</c> (así es su plantilla en el test) y todo lo demás es de una investigación, también
    /// el mensaje de su intento final sin tools. El manejador decide qué contesta; el servicio cuenta las llamadas y el máximo de investigaciones a la vez.
    /// </summary>
    private sealed class FakeLlm(Func<Call, CancellationToken, Task<LlmResult>> handler) : ILlmService
    {
        private readonly Lock _lock = new();
        private readonly List<Call> _calls = [];
        private int _inFlight;
        private int _maxInFlight;

        public string Name => "scripted";

        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (_lock)
                {
                    return [.. _calls];
                }
            }
        }

        public IReadOnlyList<Call> Investigations => [.. Calls.Where(c => c.IsInvestigation)];
        public IReadOnlyList<Call> Consolidations => [.. Calls.Where(c => !c.IsInvestigation)];
        public int MaxInvestigationsInFlight => Volatile.Read(ref _maxInFlight);

        public async Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var messages = request.Conversation.History.Messages;
            var user = messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content ?? "";
            var system = request.SystemPrompt ?? "";
            var isInvestigation = !user.StartsWith("CONSOLIDACION", StringComparison.Ordinal);
            var name = Regex.Match(user, @"^nombre=(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
            var call = new Call(isInvestigation, isInvestigation ? name : null, user, system, request.Tools is { Count: > 0 } && !FinalAnswerRequests.ForbidsTools(request));
            lock (_lock)
            {
                _calls.Add(call);
            }

            if (!isInvestigation)
                return await handler(call, cancellationToken);

            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }
            try
            {
                return await handler(call, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private static LlmResult Text(string content) =>
        LlmResult.Success([new LlmResponse { Content = content, FinishReason = FinishReason.Stop }], tokensUsed: 10, promptTokens: 5, completionTokens: 5, model: "fake-model");

    private static LlmResult Failing() => LlmResult.Failure(new InvalidOperationException("el proveedor se cae"), false);

    private const string ReportText = "# Flujos\n\n## Resumen\n\nInforme consolidado.\n\n<!-- analysis-meta: health=Green -->";

    /// <summary>Contesta como un modelo sano: notas con una cita por subsistema y un informe en la consolidación.</summary>
    private static Task<LlmResult> Healthy(Call call, CancellationToken ct) =>
        Task.FromResult(call.IsInvestigation ? Text($"Notas de {call.Subsystem}: flujo completo en `src/{call.Subsystem}/A.java:10`.") : Text(ReportText));

    // ───────────────────────────── Montaje ─────────────────────────────

    private ITemplateProvider Templates()
    {
        var dir = Path.Combine(_temp.Path, "templates");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "rep_system.md"), "Eres un auditor de prueba.");
        File.WriteAllText(Path.Combine(dir, "rep_investigacion_prompt.md"),
            "INVESTIGACION\nnombre={{ subsystem_name }}\nresumen={{ subsystem_summary }}\nmiembros={{ subsystem_members }}\n" +
            "entradas={{ subsystem_entry_points }}\npreguntas={{ investigation_questions }}\nevidencia={{ minimum_evidence }}\n" +
            "contexto={{ repository_context }}\n");
        File.WriteAllText(Path.Combine(dir, "rep_prompt.md"),
            "CONSOLIDACION\n[notas]\n{{ investigation_notes }}\n[cobertura]\n{{ coverage_summary }}\n[fin]\n");
        File.WriteAllText(Path.Combine(dir, "rep_auditor_prompt.md"),
            "CONSOLIDACION\nAUDITOR SIN ACOTAR\n[notas]\n{{ investigation_notes }}\n[cobertura]\n{{ coverage_summary }}\n[fin]\n");
        return new AnalysisTemplateProvider(dir);
    }

    private static LlmRole RoleOf(ILlmService llm) => new()
    {
        Provider = new LlmProvider { Service = llm, BaseUrl = "http://fake.invalid/" },
        Model = "fake-model",
        Inference = new LlmInferenceSettings { Temperature = 0.1f, MaxTokens = 1000 }
    };

    private SubsystemInvestigationReportStep NewStep(
        FakeLlm llm,
        PreparationOptions? options = null,
        TimeSpan? defaultTimeout = null,
        int maxToolIterations = 5,
        bool withTool = false,
        bool withDegradedAuditor = false,
        bool noteProtocol = false)
    {
        var templates = Templates();
        var role = RoleOf(llm);
        var consolidation = new CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult>(
            AnalysisPipelineFactory.AuditorStepName(Report), role, templates, "rep_prompt", "rep_system",
            reportType: Report, outputFileName: AnalysisPipelineInput.GetReportFileName(Report));
        return new SubsystemInvestigationReportStep(
            Report,
            consolidation,
            id =>
            {
                var step = SubsystemInvestigationReportStep.CreateInvestigationStep(
                    templates, role, "rep", Report, id, maxToolIterations, TimeSpan.FromMinutes(15), noteProtocol);
                if (withTool)
                    step.Tools = noteProtocol ? [new NoteProtocolTool(new ReadOnlyTool())] : [new ReadOnlyTool()];
                return step;
            },
            defaultTimeout ?? Timeout.InfiniteTimeSpan,
            options ?? new PreparationOptions(),
            withDegradedAuditor
                ? new CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult>(
                    AnalysisPipelineFactory.AuditorStepName(Report), role, templates, "rep_auditor_prompt", "rep_system",
                    reportType: Report, outputFileName: AnalysisPipelineInput.GetReportFileName(Report))
                : null);
    }

    private sealed class ReadOnlyTool : ITool
    {
        public string Name => "graph_search";
        public string Description => "tool de prueba";
        public bool IsReadOnly => true;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken) =>
            Task.FromResult("resultado");
    }

    private static string NameOf(string id) => char.ToUpperInvariant(id[0]) + id[1..];

    private static Subsystem Sub(string id) => new()
    {
        Id = id,
        Name = NameOf(id),
        Description = $"Capacidad {id}.",
        Roots = [$"src/{id}"],
        Files = [$"src/{id}/A.java", $"src/{id}/B.java"],
        KeyTypes = [$"com.acme.{id}.Service"],
        EntryPoints = [new InventoryEntryPoint { Kind = EntryPointKinds.Main, Name = $"{id}.Main", File = $"src/{id}/Main.java", Line = 12 }],
        Tables = [$"T_{id.ToUpperInvariant()}"],
        Metrics = new SubsystemMetrics { Files = 2, Loc = 400, EntryPoints = 1, Tables = 1 }
    };

    private static CoveragePlan PlanOf(params string[] chosen) => new()
    {
        GeneratedAt = DateTimeOffset.UtcNow,
        MaxSubsystemsPerReport = 8,
        Reports =
        [
            new ReportCoverage
            {
                Report = Report,
                Chosen = [.. chosen.Select((id, i) => new CoverageSelection { SubsystemId = id, Name = NameOf(id), Rank = i + 1, Score = 1.0 - i * 0.1, Loc = 400 })],
                Excluded =
                [
                    new CoverageExclusion { SubsystemId = "tests", Name = "Tests", Reason = ExclusionReasons.TestSource },
                    new CoverageExclusion { SubsystemId = "tail", Name = "Tail", Reason = ExclusionReasons.BelowTopK }
                ],
                Questions = ["¿Qué entradas tiene?", "¿Dónde termina el flujo?"],
                MinimumEvidence = ["Todas las entradas con ruta:línea."]
            }
        ]
    };

    private PipelineContext NewContext(CoveragePlan? plan, string[]? mapped = null, params (string, string?)[] config)
    {
        var context = EnrichmentStepContext.Create(TestConfig.FromPairs(config), new CapturingReporter(), Path.Combine(_temp.Path, "repo"), Path.Combine(_temp.Path, "none.db"));
        if (plan != null)
            context.Set(AnalysisContextKeys.CoveragePlan, plan);
        if (mapped != null)
        {
            context.Set(AnalysisContextKeys.DomainMap, new DomainMap { Subsystems = [.. mapped.Select(Sub)], UnassignedFiles = ["src/loose/X.java", "src/loose/Y.java"] });
            context.Set(AnalysisContextKeys.RepositoryInventory, new RepositoryInventory
            {
                Modules = [new InventoryModule { Path = "", Kind = "maven" }],
                SourceSets = [new SourceSetSummary { SourceSet = "production", Files = 6, Lines = 1200 }, new SourceSetSummary { SourceSet = "test", Files = 30, Lines = 3000 }],
                ProductionLoc = new ProductionLines { Total = 1200 }
            });
        }
        return context;
    }

    private ReportFacetInput NewInput() => new() { OutputReportsPath = ReportsPath };

    private static Task<SingleReportResult> RunAsync(SubsystemInvestigationReportStep step, ReportFacetInput input, PipelineContext context, CancellationToken ct = default) =>
        step.ExecuteAsync(input, context, 1, null, ct).WaitAsync(Guard, ct);

    // ───────────────────────────── Tests ─────────────────────────────

    [Fact]
    public async Task N_subsystems_are_N_investigations_and_one_consolidation_that_receives_every_note_and_writes_the_report()
    {
        var llm = new FakeLlm(Healthy);
        var step = NewStep(llm);
        var context = NewContext(PlanOf("billing", "orders", "catalog"), ["billing", "orders", "catalog"]);
        var input = NewInput();

        var result = await RunAsync(step, input, context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(3, llm.Investigations.Count);
        var consolidation = Assert.Single(llm.Consolidations);
        Assert.Equal(["Billing", "Catalog", "Orders"], llm.Investigations.Select(c => c.Subsystem).Order());
        // Cada nota llega a la consolidación bajo su cabecera, en el orden del plan; y el resumen de cobertura dice qué se investigó y qué no.
        foreach (var name in new[] { "Billing", "Orders", "Catalog" })
        {
            Assert.Contains($"=== SUBSISTEMA: {name} (id: {name.ToLowerInvariant()}", consolidation.User);
            Assert.Contains($"Notas de {name}: flujo completo en `src/{name}/A.java:10`.", consolidation.User);
        }
        Assert.True(consolidation.User.IndexOf("SUBSISTEMA: Billing", StringComparison.Ordinal) < consolidation.User.IndexOf("SUBSISTEMA: Orders", StringComparison.Ordinal));
        Assert.Contains("Subsistemas investigados (3 de 3 elegidos", consolidation.User);
        Assert.Contains("Código de test (1): Tests", consolidation.User);
        Assert.Contains("test: 30 ficheros, 3000 líneas", consolidation.User);
        Assert.Contains("Ficheros de producción que no pertenecen a ningún subsistema: 2.", consolidation.User);
        Assert.DoesNotContain("no se cubrieron", consolidation.User, StringComparison.OrdinalIgnoreCase);
        // El informe lo escribe la consolidación con el nombre de siempre.
        Assert.Equal(AnalysisPipelineFactory.AuditorStepName(Report), step.Name);
        Assert.True(File.Exists(ReportFile));
        Assert.Contains("Informe consolidado.", await File.ReadAllTextAsync(ReportFile));
        Assert.Equal(Report, result.ReportType);
        // El input original no se toca: las ramas del paralelo lo comparten.
        Assert.Equal(string.Empty, input.InvestigationNotes);
        Assert.Equal(string.Empty, input.CoverageSummary);
    }

    [Fact]
    public async Task Each_investigation_gets_its_own_brief_with_every_placeholder_resolved()
    {
        var llm = new FakeLlm(Healthy);
        var context = NewContext(PlanOf("billing", "orders"), ["billing", "orders"]);

        await RunAsync(NewStep(llm), NewInput(), context);

        var billing = llm.Investigations.Single(c => c.Subsystem == "Billing").User;
        Assert.DoesNotContain("{{", billing);
        Assert.Contains("(id: billing)", billing);
        Assert.Contains("Descripción: Capacidad billing.", billing);
        Assert.Contains("T_BILLING", billing);
        Assert.Contains("com.acme.billing.Service", billing);
        Assert.Contains("src/billing/ (2): A.java, B.java", billing);
        Assert.Contains("[main] billing.Main — src/billing/Main.java:12", billing);
        Assert.Contains("1. ¿Qué entradas tiene?", billing);
        Assert.Contains("- Todas las entradas con ruta:línea.", billing);
        // Sabe de los demás subsistemas del informe, pero no se cita a sí mismo entre ellos.
        Assert.Contains("Otros subsistemas que se investigan en paralelo", billing);
        Assert.Contains(" Orders.", billing);
        Assert.DoesNotContain("paralelo para este informe (no los repitas; si el tuyo depende de uno, señálalo y sigue): Billing", billing);
        Assert.Contains("Eres un auditor de prueba.", llm.Investigations[0].System);
    }

    [Fact]
    public async Task An_investigation_that_fails_leaves_its_subsystem_uncovered_and_the_report_is_written_anyway()
    {
        var llm = new FakeLlm((call, ct) => Task.FromResult(call.IsInvestigation && call.Subsystem == "Orders" ? Failing() : Text(call.IsInvestigation ? $"Notas de {call.Subsystem}." : ReportText)));
        var context = NewContext(PlanOf("billing", "orders", "catalog"), ["billing", "orders", "catalog"]);

        var step = NewStep(llm);
        var result = await RunAsync(step, NewInput(), context);

        Assert.False(result.HasError, result.Error?.Message);
        var consolidation = Assert.Single(llm.Consolidations);
        Assert.Contains("Subsistemas investigados (2 de 3 elegidos", consolidation.User);
        Assert.Contains("Subsistemas elegidos que NO se cubrieron (1):", consolidation.User);
        Assert.Contains("- Orders (id: orders): la investigación falló: el proveedor se cae", consolidation.User);
        Assert.DoesNotContain("=== SUBSISTEMA: Orders", consolidation.User);
        Assert.Contains("=== SUBSISTEMA: Billing", consolidation.User);
        Assert.True(File.Exists(ReportFile));
        var reporter = (CapturingReporter)context.Metadata[AnalysisContextKeys.Reporter]!;
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("'Orders'") && e.Message.Contains("no cubierto"));
        // El error del propio sub-step (su «❌ [LLM] Falló») no es del paso: el paso solo avisa.
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Error && e.Source == step.Name);
    }

    [Fact]
    public async Task When_every_investigation_fails_the_step_fails_and_nothing_is_consolidated()
    {
        var llm = new FakeLlm((call, ct) => Task.FromResult(Failing()));
        var context = NewContext(PlanOf("billing", "orders"), ["billing", "orders"]);

        var result = await RunAsync(NewStep(llm), NewInput(), context);

        Assert.True(result.HasError);
        Assert.Contains("Fallaron las 2 investigaciones", result.Error!.Message);
        Assert.Empty(llm.Consolidations);
        Assert.False(File.Exists(ReportFile));
        var reporter = (CapturingReporter)context.Metadata[AnalysisContextKeys.Reporter]!;
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Error);
    }

    [Fact]
    public async Task An_investigation_over_its_limit_is_cancelled_alone_and_the_others_and_the_report_go_on()
    {
        var llm = new FakeLlm(async (call, ct) =>
        {
            if (call.IsInvestigation && call.Subsystem == "Orders")
                await Task.Delay(Timeout.Infinite, ct);
            return call.IsInvestigation ? Text($"Notas de {call.Subsystem}.") : Text(ReportText);
        });
        var step = NewStep(llm);
        step.SetInvestigationTimeouts(new Dictionary<string, TimeSpan> { ["orders"] = TimeSpan.FromMilliseconds(300) });
        var context = NewContext(PlanOf("billing", "orders", "catalog"), ["billing", "orders", "catalog"]);
        using var caller = new CancellationTokenSource();

        var started = DateTime.UtcNow;
        var result = await RunAsync(step, NewInput(), context, caller.Token);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.False(caller.IsCancellationRequested);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        var consolidation = Assert.Single(llm.Consolidations);
        Assert.Contains("- Orders (id: orders): la investigación superó su límite de 0.005 min", consolidation.User);
        Assert.Contains("=== SUBSISTEMA: Billing", consolidation.User);
        Assert.Contains("=== SUBSISTEMA: Catalog", consolidation.User);
        Assert.True(File.Exists(ReportFile));
    }

    [Fact]
    public async Task The_default_limit_applies_to_a_subsystem_the_table_does_not_mention()
    {
        var llm = new FakeLlm(async (call, ct) =>
        {
            if (call.IsInvestigation && call.Subsystem == "Billing")
                await Task.Delay(Timeout.Infinite, ct);
            return call.IsInvestigation ? Text($"Notas de {call.Subsystem}.") : Text(ReportText);
        });
        var step = NewStep(llm, defaultTimeout: TimeSpan.FromMilliseconds(300));
        step.SetInvestigationTimeouts(new Dictionary<string, TimeSpan> { ["orders"] = Timeout.InfiniteTimeSpan });
        var context = NewContext(PlanOf("billing", "orders"), ["billing", "orders"]);

        var result = await RunAsync(step, NewInput(), context);

        Assert.False(result.HasError, result.Error?.Message);
        var consolidation = Assert.Single(llm.Consolidations);
        Assert.Contains("- Billing (id: billing): la investigación superó su límite", consolidation.User);
        Assert.Contains("=== SUBSISTEMA: Orders", consolidation.User);
    }

    [Fact]
    public async Task The_cancellation_of_the_caller_is_not_swallowed_as_a_failed_investigation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new FakeLlm(async (call, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Text("nunca");
        });
        var step = NewStep(llm);
        var context = NewContext(PlanOf("billing", "orders"), ["billing", "orders"]);
        using var caller = new CancellationTokenSource();

        var run = step.ExecuteAsync(NewInput(), context, 1, null, caller.Token);
        await started.Task.WaitAsync(Guard);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
        Assert.Empty(llm.Consolidations);
        Assert.False(File.Exists(ReportFile));
    }

    [Fact]
    public async Task Without_chosen_subsystems_it_is_a_normal_auditor_with_no_notes()
    {
        var llm = new FakeLlm(Healthy);
        var context = NewContext(PlanOf(), []);
        var input = NewInput();

        var result = await RunAsync(NewStep(llm), input, context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Empty(llm.Investigations);
        var consolidation = Assert.Single(llm.Consolidations);
        Assert.DoesNotContain("SUBSISTEMA", consolidation.User);
        Assert.Contains("[notas]\n\n[cobertura]\n\n[fin]", consolidation.User.Replace("\r\n", "\n"));
        Assert.True(File.Exists(ReportFile));
    }

    [Fact]
    public async Task The_consolidation_is_the_capped_step_and_the_degradation_uses_the_auditor_without_that_cap()
    {
        // Con subsistemas elegidos escribe la consolidación (la acotada); sin ninguno, el auditor sin acotar: el cambio de tope no toca la degradación.
        var withSubsystems = new FakeLlm(Healthy);
        await RunAsync(NewStep(withSubsystems, withDegradedAuditor: true), NewInput(), NewContext(PlanOf("billing"), ["billing"]));

        var degraded = new FakeLlm(Healthy);
        await RunAsync(NewStep(degraded, withDegradedAuditor: true), NewInput(), NewContext(PlanOf(), []));

        Assert.DoesNotContain("AUDITOR SIN ACOTAR", Assert.Single(withSubsystems.Consolidations).User);
        Assert.Contains("AUDITOR SIN ACOTAR", Assert.Single(degraded.Consolidations).User);
        Assert.Empty(degraded.Investigations);
    }

    [Fact]
    public async Task With_the_mode_off_or_without_a_plan_the_degraded_auditor_is_used_too()
    {
        var off = new FakeLlm(Healthy);
        await RunAsync(NewStep(off, options: new PreparationOptions { Mode = PreparationMode.Off }, withDegradedAuditor: true), NewInput(),
            NewContext(PlanOf("billing"), ["billing"]));
        var withoutPlan = new FakeLlm(Healthy);
        await RunAsync(NewStep(withoutPlan, withDegradedAuditor: true), NewInput(), NewContext(plan: null));

        Assert.Contains("AUDITOR SIN ACOTAR", Assert.Single(off.Consolidations).User);
        Assert.Contains("AUDITOR SIN ACOTAR", Assert.Single(withoutPlan.Consolidations).User);
    }

    [Fact]
    public async Task Without_a_plan_or_with_the_mode_off_it_calls_the_auditor_directly()
    {
        var withoutPlan = new FakeLlm(Healthy);
        await RunAsync(NewStep(withoutPlan), NewInput(), NewContext(plan: null));

        var off = new FakeLlm(Healthy);
        await RunAsync(NewStep(off, options: new PreparationOptions { Mode = PreparationMode.Off }), NewInput(),
            NewContext(PlanOf("billing", "orders"), ["billing", "orders"]));

        Assert.Empty(withoutPlan.Investigations);
        Assert.Single(withoutPlan.Consolidations);
        Assert.Empty(off.Investigations);
        Assert.Single(off.Consolidations);
    }

    [Fact]
    public async Task A_plan_without_this_report_degrades_to_a_normal_auditor()
    {
        var llm = new FakeLlm(Healthy);
        var otherReport = PlanOf("billing") with { Reports = [PlanOf("billing").Reports[0] with { Report = ReportType.ErroresYBugs }] };

        await RunAsync(NewStep(llm), NewInput(), NewContext(otherReport, ["billing"]));

        Assert.Empty(llm.Investigations);
        Assert.Single(llm.Consolidations);
    }

    [Fact]
    public async Task It_never_runs_more_investigations_at_once_than_the_configured_parallelism()
    {
        var llm = new FakeLlm(async (call, ct) =>
        {
            if (!call.IsInvestigation)
                return Text(ReportText);
            await Task.Delay(150, ct);
            return Text($"Notas de {call.Subsystem}.");
        });
        var ids = new[] { "a", "b", "c", "d", "e", "f" };
        var context = NewContext(PlanOf(ids), ids);

        var result = await RunAsync(NewStep(llm, options: new PreparationOptions { MaxParallelInvestigations = 2 }), NewInput(), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(6, llm.Investigations.Count);
        Assert.Equal(2, llm.MaxInvestigationsInFlight);
    }

    [Fact]
    public async Task The_parallelism_comes_from_the_configuration_when_no_options_are_given()
    {
        var llm = new FakeLlm(async (call, ct) =>
        {
            if (!call.IsInvestigation)
                return Text(ReportText);
            await Task.Delay(150, ct);
            return Text($"Notas de {call.Subsystem}.");
        });
        var ids = new[] { "a", "b", "c", "d" };
        var templates = Templates();
        var role = RoleOf(llm);
        var step = new SubsystemInvestigationReportStep(
            Report,
            new CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult>(
                AnalysisPipelineFactory.AuditorStepName(Report), role, templates, "rep_prompt", "rep_system",
                reportType: Report, outputFileName: AnalysisPipelineInput.GetReportFileName(Report)),
            id => SubsystemInvestigationReportStep.CreateInvestigationStep(templates, role, "rep", Report, id, 5, TimeSpan.FromMinutes(1)),
            Timeout.InfiniteTimeSpan);
        var context = NewContext(PlanOf(ids), ids, ("CodebaseAnalysis:Preparation:MaxParallelInvestigations", "1"));

        await RunAsync(step, NewInput(), context);

        Assert.Equal(1, llm.MaxInvestigationsInFlight);
    }

    [Fact]
    public async Task An_investigation_that_exhausts_its_tool_budget_still_delivers_notes_in_the_final_answer_attempt()
    {
        var llm = new FakeLlm((call, ct) => Task.FromResult(
            !call.IsInvestigation ? Text(ReportText)
            : call.WithTools
                ? LlmResult.Success([new LlmResponse
                {
                    Content = string.Empty,
                    FinishReason = FinishReason.ToolCalls,
                    ToolCalls = [new ToolCall { Id = $"call_{Guid.NewGuid():N}", Name = "graph_search", Arguments = "{}" }]
                }], model: "fake-model")
                : Text("Notas con lo consultado.")));
        var context = NewContext(PlanOf("billing"), ["billing"]);

        var result = await RunAsync(NewStep(llm, maxToolIterations: 1, withTool: true), NewInput(), context);

        var log = string.Join(Environment.NewLine, ((CapturingReporter)context.Metadata[AnalysisContextKeys.Reporter]!).Entries.Select(e => $"{e.Level} {e.Source}: {e.Message}"));
        Assert.False(result.HasError, result.Error?.Message);
        Assert.True(llm.Investigations.Any(c => !c.WithTools), log);
        var consolidation = Assert.Single(llm.Consolidations);
        Assert.Contains("Notas con lo consultado.", consolidation.User);
        Assert.Contains("Subsistemas investigados (1 de 1 elegidos", consolidation.User);
    }

    [Fact]
    public async Task With_the_note_protocol_the_investigation_notes_include_the_session_notes_before_the_final_answer()
    {
        var turns = 0;
        var llm = new FakeLlm((call, ct) => Task.FromResult(
            !call.IsInvestigation ? Text(ReportText)
            : Interlocked.Increment(ref turns) == 1
                ? LlmResult.Success([new LlmResponse
                {
                    Content = string.Empty,
                    FinishReason = FinishReason.ToolCalls,
                    ToolCalls = [new ToolCall { Id = "call_1", Name = "graph_search", Arguments = "{}" }]
                }], model: "fake-model")
                : Text("<nota r=\"R1\">El flujo arranca en `src/billing/A.java:10`.</nota>\nConclusión: flujo cubierto.")));
        var context = NewContext(PlanOf("billing"), ["billing"]);

        var result = await RunAsync(NewStep(llm, withTool: true, noteProtocol: true), NewInput(), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Contains("<nota r=\"Rn\">", llm.Investigations[0].System);
        var consolidation = Assert.Single(llm.Consolidations);
        Assert.Contains("[R1]\nEl flujo arranca en `src/billing/A.java:10`.\n\nConclusión: flujo cubierto.", consolidation.User);
        Assert.DoesNotContain("[nota R1 guardada]", consolidation.User);
    }

    [Fact]
    public async Task A_truncated_investigation_keeps_its_notes_and_says_they_are_incomplete()
    {
        var llm = new FakeLlm((call, ct) => Task.FromResult(call.IsInvestigation
            ? LlmResult.Success([new LlmResponse { Content = "Notas a medias.", FinishReason = FinishReason.Length }], model: "fake-model")
            : Text(ReportText)));
        var context = NewContext(PlanOf("billing"), ["billing"]);

        var result = await RunAsync(NewStep(llm), NewInput(), context);

        Assert.False(result.HasError, result.Error?.Message);
        var consolidation = Assert.Single(llm.Consolidations);
        Assert.Contains("[Nota incompleta", consolidation.User);
        Assert.Contains("Notas a medias.", consolidation.User);
        Assert.Contains("nota incompleta (la respuesta se cortó)", consolidation.User);
    }

    [Fact]
    public async Task The_timeout_the_budget_writes_into_the_step_is_the_one_the_real_engine_enforces()
    {
        var llm = new FakeLlm(async (call, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Text("nunca");
        });
        var step = NewStep(llm);
        var registry = new AuditorStepRegistry();
        registry.Register(Report, step);
        Assert.True(registry.IsModeB(Report));
        Assert.True(registry.TrySetTimeout(Report, TimeSpan.FromMilliseconds(400)));
        Assert.Equal(TimeSpan.FromMilliseconds(400), step.Timeout);
        var context = NewContext(PlanOf("billing", "orders"), ["billing", "orders"]);

        var started = DateTime.UtcNow;
        var result = await new Pipeline().ExecuteAsync("run", [step], NewInput(), context).WaitAsync(Guard);

        Assert.True(result.HasError);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        Assert.False(File.Exists(ReportFile));
    }

    [Fact]
    public async Task Inside_the_real_engine_the_step_writes_the_report_with_the_investigations_in_its_lane()
    {
        var llm = new FakeLlm(Healthy);
        var step = NewStep(llm);
        var context = NewContext(PlanOf("billing", "orders"), ["billing", "orders"]);

        var result = await new Pipeline().ExecuteAsync("run", [step], NewInput(), context).WaitAsync(Guard);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.IsType<SingleReportResult>(result);
        Assert.Equal(2, llm.Investigations.Count);
        Assert.Single(llm.Consolidations);
        Assert.True(File.Exists(ReportFile));
    }

    [Fact]
    public void The_investigation_step_has_the_report_role_tools_and_templates_and_the_investigation_name()
    {
        var llm = new FakeLlm(Healthy);
        var role = RoleOf(llm);

        var step = SubsystemInvestigationReportStep.CreateInvestigationStep(Templates(), role, "report_funcional", ReportType.AnalisisFuncional, "billing-3fa9c1", 7, TimeSpan.FromMinutes(3));

        Assert.Equal("Investigation_AnalisisFuncional_billing-3fa9c1", step.Name);
        Assert.Equal("report_funcional_investigacion_prompt", step.Prompt);
        Assert.Equal("report_funcional_system", step.SystemPrompt);
        Assert.Equal(7, step.MaxToolIterations);
        Assert.Equal(SubsystemInvestigationReportStep.InvestigationToolLimitPrompt, step.ToolLimitFinalAnswerPrompt);
        Assert.Equal(TimeSpan.FromMinutes(3), step.Timeout);
    }

    [Fact]
    public void The_step_declares_the_contracts_the_time_budget_writes_into()
    {
        var step = NewStep(new FakeLlm(Healthy));

        Assert.IsAssignableFrom<IWritableTimeout>(step);
        Assert.IsAssignableFrom<IInvestigationTimeouts>(step);
        Assert.IsAssignableFrom<IStep<ReportFacetInput, SingleReportResult>>(step);
        Assert.Equal("Auditor_FlujosEndToEnd", step.Name);
        Assert.Equal(1, step.MaxRetries);
        Assert.Null(step.Timeout);
    }
}
