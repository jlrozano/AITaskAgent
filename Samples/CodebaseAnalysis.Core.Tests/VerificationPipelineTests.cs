using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// La verificación de informes a demanda (<see cref="AnalysisPipelineFactory.CreateVerificationPipeline"/>) de extremo a
/// extremo con un verificador enlatado: lo que escribe, el historial, los eventos y lo que hace cuando el verificador falla.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class VerificationPipelineTests
{
    private const string FileName = "06_arquitectura_acoplamiento.md";

    private const string Meta = "<!-- analysis-meta: health=Amber; generated_at=2026-10-01T10:00:00Z; model=m; provider=fake; "
        + "system_sha=aaaaaaaaaaaa; prompt_sha=bbbbbbbbbbbb; graph=cbm:1 -->";

    private static readonly string Original = "# Arquitectura\n\n## Resumen\n\nEl `src/Dao.java:5` abre una conexión y la cierra en finally. Hay tres acciones y el atributo `@[Authorize]`.\n\n"
        + "## ARC-001 Capas\n\nLa capa de datos cita `src/Nope.java:9` y `src/Dao.java:7`. " + string.Join(' ', Enumerable.Repeat("Texto de relleno del informe original.", 12)) + $"\n\n{Meta}\n";

    private static readonly string Corrected = "# Arquitectura\n\n## Resumen\n\nEl `src/Dao.java:5` abre una conexión y no la cierra. Hay cuatro acciones y el atributo `@(at)[Authorize]`.\n\n"
        + "## ARC-001 Capas\n\nLa capa de datos cita `src/Dao.java:9` y `src/Dao.java:7`. " + string.Join(' ', Enumerable.Repeat("Texto de relleno del informe original.", 12))
        + "\n\n## Cambios de la verificación\n\n- **Resumen:** decía que cerraba en finally. Motivo: `src/Dao.java:5-6` no tiene finally.\n"
        + "- **ARC-001:** la cita `src/Nope.java:9` no existe; pasa a `src/Dao.java:9`. Motivo: el fichero es Dao.java.\n\n<!-- analysis-meta: health=Green -->\n";

    private static readonly (string, string?)[] FakeProvider =
        FakeLlmConfig.Pairs("verifier-fake");

    [Fact]
    public async Task A_corrected_report_is_written_with_the_original_as_baseline_and_the_outcome_in_its_meta()
    {
        using var repo = NewRepository();
        var llm = new VerifierLlmService(_ => Corrected);
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, Original);

        var (result, events) = await VerifyAsync(host, input, synthesis: false);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(1, llm.Calls);
        var written = await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, FileName));
        Assert.Contains("abre una conexión y no la cierra", written);
        Assert.Contains("## Cambios de la verificación", written);
        Assert.Contains("`@[Authorize]`", written);
        Assert.DoesNotContain("@(at)[", written);
        // La procedencia y el semáforo son los del auditor, no los que escribió el verificador; el resultado, aparte.
        Assert.EndsWith(Meta.Replace(" -->", "; verification=corrected(2) -->") + "\n", written.Replace("\r\n", "\n"));
        Assert.DoesNotContain("health=Green", written);

        var history = new ReportHistoryStore(new KnowledgePaths(input.RepositoryPath, input.OutputReportsPath));
        var versions = history.List(SourceRef.Report(FileName));
        Assert.Equal([ReportVersionActionNames.Baseline, ReportVersionActionNames.Updated], versions.Select(v => v.Action));
        Assert.Equal(Original.Replace("\r\n", "\n"), history.ReadContent(SourceRef.Report(FileName), versions[0].VersionId));
        Assert.Equal("Verificación: corrected(2)", versions[1].Note);

        var verified = Assert.Single(events, e => e.EventType == AnalysisEventTypes.ReportVerified);
        Assert.Equal(VerificationOutcomes.Corrected, verified.Metadata![AnalysisEventTypes.MetadataKeys.Outcome]);
        Assert.Equal(2, verified.Metadata[AnalysisEventTypes.MetadataKeys.Changes]);
        Assert.Equal(versions[0].VersionId, verified.Metadata[AnalysisEventTypes.MetadataKeys.PreviousVersionId]);
        Assert.Equal(versions[1].VersionId, verified.Metadata[AnalysisEventTypes.MetadataKeys.VersionId]);
        Assert.Equal(nameof(ReportType.ArquitecturaAcoplamiento), verified.Metadata[AnalysisEventTypes.MetadataKeys.ReportType]);
        // Verificar no es regenerar: no hay report.written ni auditores.
        Assert.DoesNotContain(events, e => e.EventType == AnalysisEventTypes.ReportWritten);
    }

    [Fact]
    public async Task The_model_receives_one_text_with_the_three_sections_and_no_file_include_sequence()
    {
        using var repo = NewRepository();
        var llm = new VerifierLlmService(_ => "SIN CAMBIOS");
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, Original);

        await VerifyAsync(host, input, synthesis: false);

        var user = Assert.Single(llm.UserMessages);
        Assert.Contains("<borrador>", user);
        Assert.Contains("<fallos_validacion>", user);
        Assert.Contains("<evidencias>", user);
        Assert.Contains("`src/Nope.java:9`", user[user.IndexOf("<fallos_validacion>", StringComparison.Ordinal)..]);
        Assert.Contains("    5 | code 5", user);
        Assert.Contains("`@(at)[Authorize]`", user);
        Assert.DoesNotContain("@[", user);
        Assert.Equal(new[] { "graph_neighbors", "grep_search", "view_code_item", "view_file" }, llm.ToolNames.Order());
        Assert.Contains("Verificador de Informes", llm.SystemPrompts.Single());
    }

    [Fact]
    public async Task No_changes_only_stamps_the_meta_and_keeps_the_body_byte_for_byte()
    {
        using var repo = NewRepository();
        var clean = Original.Replace("`src/Nope.java:9` y ", string.Empty);
        var llm = new VerifierLlmService(_ => "SIN CAMBIOS\nCotejadas las citas con el código.");
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, clean);

        var (_, events) = await VerifyAsync(host, input, synthesis: false);

        var written = (await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, FileName))).Replace("\r\n", "\n");
        Assert.Equal(clean.Replace(" -->", "; verification=ok -->").Replace("\r\n", "\n"), written);
        var verified = Assert.Single(events, e => e.EventType == AnalysisEventTypes.ReportVerified);
        Assert.Equal(VerificationOutcomes.Verified, verified.Metadata![AnalysisEventTypes.MetadataKeys.Outcome]);
        Assert.Equal(0, verified.Metadata[AnalysisEventTypes.MetadataKeys.Changes]);
    }

    [Fact]
    public async Task A_change_declared_but_not_written_is_taken_as_no_changes_and_the_section_is_not_added()
    {
        using var repo = NewRepository();
        var clean = Original.Replace("`src/Nope.java:9` y ", string.Empty);
        var lying = clean.Replace(Meta, string.Empty).TrimEnd() + "\n\n## Cambios de la verificación\n\n- **ARC-001:** la cita queda igual. Motivo: `src/Dao.java:7`.\n";
        var llm = new VerifierLlmService(_ => lying);
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, clean);

        var (_, events) = await VerifyAsync(host, input, synthesis: false);

        var written = (await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, FileName))).Replace("\r\n", "\n");
        Assert.Equal(clean.Replace(" -->", "; verification=ok -->").Replace("\r\n", "\n"), written);
        Assert.DoesNotContain("Cambios de la verificación", written);
        var verified = Assert.Single(events, e => e.EventType == AnalysisEventTypes.ReportVerified);
        Assert.Equal(VerificationOutcomes.Verified, verified.Metadata![AnalysisEventTypes.MetadataKeys.Outcome]);
        Assert.Equal(0, verified.Metadata[AnalysisEventTypes.MetadataKeys.Changes]);
    }

    [Fact]
    public async Task Verifying_twice_with_the_same_result_does_not_add_a_version()
    {
        using var repo = NewRepository();
        var clean = Original.Replace("`src/Nope.java:9` y ", string.Empty);
        var llm = new VerifierLlmService(_ => "SIN CAMBIOS");
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, clean);

        await VerifyAsync(host, input, synthesis: false);
        var (_, events) = await VerifyAsync(host, input, synthesis: false);

        var history = new ReportHistoryStore(new KnowledgePaths(input.RepositoryPath, input.OutputReportsPath));
        Assert.Equal(2, history.List(SourceRef.Report(FileName)).Count);
        var verified = Assert.Single(events, e => e.EventType == AnalysisEventTypes.ReportVerified);
        Assert.Equal(VerificationOutcomes.Verified, verified.Metadata![AnalysisEventTypes.MetadataKeys.Outcome]);
        Assert.Null(verified.Metadata[AnalysisEventTypes.MetadataKeys.VersionId]);
    }

    [Fact]
    public async Task Citations_that_stay_broken_leave_a_warning_outcome_but_the_report_is_written()
    {
        using var repo = NewRepository();
        var llm = new VerifierLlmService(_ => "SIN CAMBIOS");
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, Original);

        var (_, events) = await VerifyAsync(host, input, synthesis: false);

        var written = await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, FileName));
        Assert.Contains("verification=warnings(1)", written);
        var verified = Assert.Single(events, e => e.EventType == AnalysisEventTypes.ReportVerified);
        Assert.Equal(VerificationOutcomes.Warnings, verified.Metadata![AnalysisEventTypes.MetadataKeys.Outcome]);
    }

    public static TheoryData<string> UnusableAnswers => new()
    {
        { "Claro, aquí tienes el informe corregido." },
        { "# Arquitectura\n\nCorto.\n\n## Cambios de la verificación\n\n- **A:** x.\n" },
        { "" }
    };

    [Theory]
    [MemberData(nameof(UnusableAnswers))]
    public async Task An_unusable_answer_leaves_the_file_untouched_and_reports_a_failure(string answer)
    {
        using var repo = NewRepository();
        var llm = new VerifierLlmService(_ => answer);
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, Original);
        var path = Path.Combine(input.OutputReportsPath, FileName);
        var bytes = await File.ReadAllBytesAsync(path);

        var (result, events) = await VerifyAsync(host, input, synthesis: false);

        Assert.False(result.HasError);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        var verified = Assert.Single(events, e => e.EventType == AnalysisEventTypes.ReportVerified);
        Assert.Equal(VerificationOutcomes.Failed, verified.Metadata![AnalysisEventTypes.MetadataKeys.Outcome]);
        Assert.NotNull(verified.Metadata[AnalysisEventTypes.MetadataKeys.Error]);
        Assert.False(Directory.Exists(Path.Combine(repo.Path, ".codebase", "report-history")), "sin escritura no hay historial");
    }

    [Fact]
    public async Task A_verifier_that_throws_leaves_the_file_untouched()
    {
        using var repo = NewRepository();
        var llm = new VerifierLlmService(_ => throw new InvalidOperationException("sin red"));
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, Original);
        var path = Path.Combine(input.OutputReportsPath, FileName);
        var bytes = await File.ReadAllBytesAsync(path);

        var (result, events) = await VerifyAsync(host, input, synthesis: false);

        Assert.False(result.HasError);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        var verified = Assert.Single(events, e => e.EventType == AnalysisEventTypes.ReportVerified);
        Assert.Equal(VerificationOutcomes.Failed, verified.Metadata![AnalysisEventTypes.MetadataKeys.Outcome]);
    }

    [Fact]
    public async Task A_verifier_that_exceeds_its_limit_fails_that_report_and_names_the_setting()
    {
        using var repo = NewRepository();
        var llm = new VerifierLlmService(_ => Corrected) { Delay = TimeSpan.FromSeconds(30) };
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, Original);
        var path = Path.Combine(input.OutputReportsPath, FileName);
        var bytes = await File.ReadAllBytesAsync(path);

        // El límite mínimo que expresa la configuración es de un minuto: se prueba el paso con uno de 300 ms construido a mano.
        var verifier = new VerifyReportStep(
            ReportType.ArquitecturaAcoplamiento,
            new CustomStatelessTemplateLlmStep<VerificationInput, AITaskAgent.LLM.Results.LlmStringStepResult>(
                AnalysisPipelineFactory.VerifierLlmStepName(ReportType.ArquitecturaAcoplamiento), host.Roles["ReportVerifier"], host.Templates,
                "report_verifier_prompt", "report_verifier_system"),
            new Lazy<ReportValidator.ReportContentValidator>(() =>
                ReportValidator.CreateContentValidator(new ReportValidationRequest(input.RepositoryPath, input.OutputReportsPath))),
            TimeSpan.FromMilliseconds(300));
        var context = AnalysisRunner.CreateContext(input, host.Services, null, new CapturingReporter());

        var result = await verifier.ExecuteAsync(new GraphStatusResult(), context, 1, null, CancellationToken.None);

        Assert.Equal(VerificationOutcomes.Failed, result.Outcome);
        Assert.Contains("CodebaseAnalysis:Timeouts:VerifierMinutes", result.FailureReason);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_missing_report_fails_that_report_only()
    {
        using var repo = NewRepository();
        var llm = new VerifierLlmService(_ => "SIN CAMBIOS");
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, Original);
        input.RequestedReports = [ReportType.ArquitecturaAcoplamiento, ReportType.CodeSmells];

        var (result, events) = await VerifyAsync(host, input, synthesis: false);

        Assert.IsType<ParallelResult>(result);
        var outcomes = events.Where(e => e.EventType == AnalysisEventTypes.ReportVerified)
            .ToDictionary(e => (string)e.Metadata![AnalysisEventTypes.MetadataKeys.ReportType]!, e => (string)e.Metadata![AnalysisEventTypes.MetadataKeys.Outcome]!);
        Assert.Equal(VerificationOutcomes.Failed, outcomes[nameof(ReportType.CodeSmells)]);
        Assert.Equal(1, llm.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_pipeline_has_the_preparation_the_verifiers_and_optionally_the_synthesis(bool synthesis)
    {
        await using var host = await TestHost.CreateAsync(new VerifierLlmService(_ => "SIN CAMBIOS"), ("CodebaseAnalysis:Timeouts:VerifierMinutes", "7"));
        var input = new AnalysisPipelineInput { RequestedReports = [ReportType.CodeSmells, ReportType.ErroresYBugs] };

        var pipeline = AnalysisPipelineFactory.CreateVerificationPipeline(host.Templates, host.Roles, input, synthesis, host.Services);

        string[] expected = [PrepareRegenerationStep.StepName, AnalysisPipelineFactory.ParallelVerificationStepName];
        if (synthesis)
            expected = [.. expected, ExecutiveSynthesizerStep.StepName];
        Assert.Equal(expected, pipeline.Steps.Select(s => s.Name));
        var branches = Assert.IsType<TolerantReportsParallelStep>(pipeline.Steps[1]).Branches;
        Assert.Equal(["Verify_CodeSmells", "Verify_ErroresYBugs"], branches.Select(b => b.Name));
        Assert.All(branches, b => Assert.Equal(TimeSpan.FromMinutes(9), b.Timeout));
        Assert.Equal(AnalysisPipelineFactory.VerificationPipelineName, pipeline.Name);
        Assert.Equal(TimeSpan.FromMinutes(240), pipeline.PipelineTimeout);
        Assert.Throws<ArgumentException>(() =>
            AnalysisPipelineFactory.CreateVerificationPipeline(host.Templates, host.Roles, new AnalysisPipelineInput { RequestedReports = [] }, true, host.Services));
    }

    [Fact]
    public async Task The_verification_steps_are_recognized_as_their_report_and_never_as_auditors()
    {
        Assert.True(AnalysisPipelineFactory.TryParseVerificationStepName("Verify_CodeSmells", out var a));
        Assert.True(AnalysisPipelineFactory.TryParseVerificationStepName("Verifier_ErroresYBugs", out var b));
        Assert.False(AnalysisPipelineFactory.TryParseVerificationStepName("Auditor_CodeSmells", out _));
        Assert.False(AnalysisPipelineFactory.TryParseVerificationStepName("Verify_99", out _));
        Assert.False(AnalysisPipelineFactory.TryParseAuditorStepName("Verify_CodeSmells", out _));
        Assert.Equal((ReportType.CodeSmells, ReportType.ErroresYBugs), (a, b));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_mapper_gives_the_verifier_events_the_report_of_their_lane_and_translates_report_verified()
    {
        var mapper = new AnalysisEventMapper("run1");

        var started = mapper.Map(new StepStartedEvent { StepName = "Verify_CodeSmells", StepPath = "ParallelVerificationExecution/Verify_CodeSmells" });
        var verified = mapper.Map(new AITaskAgent.Observability.Events.StepProgressEvent
        {
            StepName = "Verify_CodeSmells",
            EventType = AnalysisEventTypes.ReportVerified,
            Message = "02 verificado",
            CorrelationId = "c",
            Metadata = new Dictionary<string, object?>
            {
                [AnalysisEventTypes.MetadataKeys.FileName] = "02_code_smells.md",
                [AnalysisEventTypes.MetadataKeys.ReportType] = "CodeSmells",
                [AnalysisEventTypes.MetadataKeys.Outcome] = "corrected",
                [AnalysisEventTypes.MetadataKeys.Changes] = 3,
                [AnalysisEventTypes.MetadataKeys.PreviousVersionId] = "v0001",
                [AnalysisEventTypes.MetadataKeys.VersionId] = "v0002"
            }
        });

        Assert.Equal("CodeSmells", started!.Report);
        Assert.Equal(AnalysisEventDtoTypes.ReportVerified, verified!.Type);
        Assert.Equal("CodeSmells", verified.Report);
        Assert.Equal(AnalysisEventLevels.Success, verified.Level);
        Assert.Equal("corrected", verified.Data!["outcome"]);
        Assert.Equal(3, verified.Data["changes"]);
        Assert.Equal("v0001", verified.Data["previousVersionId"]);
        Assert.Equal("v0002", verified.Data["versionId"]);
        await Task.CompletedTask;
    }

    [Fact]
    public void The_verifier_limit_defaults_to_ten_minutes_and_comes_from_the_configuration()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), AnalysisTimeouts.FromConfiguration(TestConfig.FromPairs()).Verifier);
        Assert.Equal(TimeSpan.FromMinutes(3), AnalysisTimeouts.FromConfiguration(TestConfig.FromPairs(("CodebaseAnalysis:Timeouts:VerifierMinutes", "3"))).Verifier);
        Assert.Equal(Timeout.InfiniteTimeSpan, AnalysisTimeouts.FromConfiguration(TestConfig.FromPairs(("CodebaseAnalysis:Timeouts:VerifierMinutes", "0"))).Verifier);
    }

    [Fact]
    public async Task The_verifier_role_is_required_and_a_configuration_without_it_names_it()
    {
        Assert.Contains("ReportVerifier", LlmRoleFactory.RequiredRoles);
        var configuration = TestConfig.FromPairs(FakeLlmConfig.Pairs("verifier-fake", [.. LlmRoleFactory.RequiredRoles.Where(r => r != "ReportVerifier")]));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<ILlmService>(new VerifierLlmService(_ => string.Empty));
        services.AddAITaskAgent();
        await using var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();

        var error = Assert.Throws<InvalidOperationException>(() => LlmRoleFactory.BuildRoles(provider, _ => null, _ => { }));

        Assert.Contains("ReportVerifier", error.Message);
        Assert.Contains(LlmRoleFactory.SectionName, error.Message);
    }

    // ── CLI: --verify <informes> ─────────────────────────────────────────

    [Fact]
    public void Verify_is_not_requested_without_the_switch_and_its_reports_use_the_names_of_reports_and_only_those_on_disk()
    {
        using var repo = new TempDirectory();
        var reports = Path.Combine(repo.Path, "reports");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "01_analisis_funcional.md"), "x");
        File.WriteAllText(Path.Combine(reports, "03_codigo_muerto.md"), "x");
        File.WriteAllText(Path.Combine(reports, FileName), "x");
        var warnings = new List<string>();

        Assert.Null(VerificationCommand.ResolveReports(TestConfig.FromArgs(), reports, warnings.Add));

        var some = VerificationCommand.ResolveReports(TestConfig.FromArgs("--verify", "deadcode, funcional,codigomuerto,inventado,flujos"), reports, warnings.Add);
        Assert.Equal([ReportType.CodigoMuerto, ReportType.AnalisisFuncional], some);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("inventado", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("04_flujos_end_to_end.md", StringComparison.Ordinal));

        warnings.Clear();
        var all = VerificationCommand.ResolveReports(TestConfig.FromArgs("--verify", "all"), reports, warnings.Add);
        Assert.Equal([ReportType.AnalisisFuncional, ReportType.CodigoMuerto, ReportType.ArquitecturaAcoplamiento], all);
        Assert.Empty(warnings);
        Assert.Empty(VerificationCommand.ResolveReports(TestConfig.FromArgs("--verify", "errores"), reports, _ => { })!);
    }

    [Fact]
    public void The_synthesis_switch_is_taken_out_of_the_arguments_so_it_does_not_eat_the_next_one()
    {
        var args = AnalysisRunOptions.ExtractSwitch(["--verify", "flujos", VerificationCommand.SynthesisSwitch, "--repo", "X"], VerificationCommand.SynthesisSwitch, out var present);

        Assert.True(present);
        Assert.Equal(["--verify", "flujos", "--repo", "X"], args);
        Assert.Equal("flujos", TestConfig.FromArgs(args)[VerificationCommand.VerifyKey]);
        Assert.Equal("X", TestConfig.FromArgs(args)["repo"]);
        AnalysisRunOptions.ExtractSwitch(["--repo", "X"], VerificationCommand.SynthesisSwitch, out var absent);
        Assert.False(absent);
    }

    [Fact]
    public async Task The_verifier_model_is_overridden_from_the_command_line_like_any_other_role()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(FakeProvider.Select(p => new KeyValuePair<string, string?>(p.Item1, p.Item2)))
            .AddCommandLine([$"--{LlmRoleFactory.SectionName}:Roles:ReportVerifier:Model=otro-modelo"])
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<ILlmService>(new VerifierLlmService(_ => string.Empty));
        services.AddAITaskAgent();
        await using var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();

        var roles = LlmRoleFactory.BuildRoles(provider, _ => null, _ => { });

        Assert.Equal("otro-modelo", roles["ReportVerifier"].Model);
        Assert.Equal("fake-model", roles["FunctionalAuditor"].Model);
    }

    [Fact]
    public async Task The_verify_command_runs_the_verification_and_leaves_the_run_meta_and_the_validation_like_the_web()
    {
        using var repo = NewRepository();
        var llm = new VerifierLlmService(_ => Corrected);
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, Original);
        AnalysisToolRegistration.Register(input);
        var reporter = new CapturingReporter();

        var outcome = await VerificationCommand.RunAsync(input, [ReportType.ArquitecturaAcoplamiento], synthesis: false, host.Roles, host.Services, reporter, CancellationToken.None);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(0, outcome.Unverified);
        Assert.False(outcome.Cancelled);
        Assert.Equal(1, llm.Calls);
        Assert.Contains("abre una conexión y no la cierra", await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, FileName)));
        // El consumo del verificador se cuenta y queda con el estado en _run_meta.json.
        Assert.Equal(1, outcome.Usage.Total.Calls);
        using var meta = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, RunMetaFile.FileName)));
        Assert.Equal(RunMetaFile.KindVerification, meta.RootElement.GetProperty("kind").GetString());
        Assert.Equal("succeeded", meta.RootElement.GetProperty("status").GetString());
        Assert.True(File.Exists(Path.Combine(input.OutputReportsPath, ReportValidationFile.FileName)));
        Assert.Contains(reporter.Entries, e => e.Message.Contains("Validación de", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_report_the_verifier_cannot_verify_is_counted_leaves_the_report_as_it_was_and_makes_the_exit_code_one()
    {
        using var repo = NewRepository();
        var llm = new VerifierLlmService(_ => throw new InvalidOperationException("el proveedor se cae"));
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, Original);
        AnalysisToolRegistration.Register(input);

        var outcome = await VerificationCommand.RunAsync(input, [ReportType.ArquitecturaAcoplamiento], synthesis: false, host.Roles, host.Services, new CapturingReporter(), CancellationToken.None);

        Assert.Equal(1, outcome.Unverified);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(Original.Replace("\r\n", "\n"), (await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, FileName))).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task A_cancelled_verification_exits_with_130_and_validates_nothing()
    {
        using var repo = NewRepository();
        var llm = new VerifierLlmService(_ => Corrected) { Delay = TimeSpan.FromSeconds(30) };
        await using var host = await TestHost.CreateAsync(llm);
        var input = Input(host, repo, Original);
        AnalysisToolRegistration.Register(input);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var outcome = await VerificationCommand.RunAsync(input, [ReportType.ArquitecturaAcoplamiento], synthesis: false, host.Roles, host.Services, new CapturingReporter(), cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.Equal(130, outcome.ExitCode);
        Assert.False(File.Exists(Path.Combine(input.OutputReportsPath, ReportValidationFile.FileName)));
        using var meta = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, RunMetaFile.FileName)));
        Assert.Equal("cancelled", meta.RootElement.GetProperty("status").GetString());
    }

    // ── Soporte ──────────────────────────────────────────────────────────

    private static TempDirectory NewRepository()
    {
        var repo = new TempDirectory();
        var dao = Path.Combine(repo.Path, "src", "Dao.java");
        Directory.CreateDirectory(Path.GetDirectoryName(dao)!);
        File.WriteAllText(dao, string.Concat(Enumerable.Range(1, 30).Select(i => $"code {i}\n")));
        return repo;
    }

    private static AnalysisPipelineInput Input(TestHost host, TempDirectory repo, string report)
    {
        var input = AnalysisRunOptions.BuildInput(host.Configuration, repo.Path, _ => { });
        input.RequestedReports = [ReportType.ArquitecturaAcoplamiento];
        Directory.CreateDirectory(input.OutputReportsPath);
        File.WriteAllText(Path.Combine(input.OutputReportsPath, FileName), report);
        CreateGraph(input.DatabasePath);
        return input;
    }

    private static void CreateGraph(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        GraphSchema.EnsureAsync(dbPath, CancellationToken.None).GetAwaiter().GetResult();
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
            VALUES ('Codebase', 'Class', 'Dao', 'Demo.Dao', 'src/Dao.java', 1, 30, '{"cyclomatic":3}');
            """;
        cmd.ExecuteNonQuery();
    }

    private static async Task<(IStepResult Result, List<StepProgressEventLike> Events)> VerifyAsync(
        TestHost host, AnalysisPipelineInput input, bool synthesis)
    {
        var events = new List<StepProgressEventLike>();
        var channel = new EventChannel();
        channel.Subscribe((evt, _) =>
        {
            if (evt is AITaskAgent.Observability.Events.StepProgressEvent progress)
            {
                lock (events)
                {
                    events.Add(new StepProgressEventLike(progress.EventType, progress.Metadata));
                }
            }
            return Task.CompletedTask;
        });

        AnalysisToolRegistration.Register(input);
        var result = await AnalysisRunner.VerifyAsync(
            input, input.RequestedReports, synthesis, host.Roles, host.Services, channel, new CapturingReporter(), CancellationToken.None);

        lock (events)
        {
            return (result, [.. events]);
        }
    }

    private sealed record StepProgressEventLike(string EventType, Dictionary<string, object?>? Metadata);

    /// <summary>
    /// Verificador sin red: responde lo que devuelva <c>answer</c> a cada llamada (que recibe el mensaje de usuario), guarda lo
    /// que ha visto y puede tardar <see cref="Delay"/>.
    /// </summary>
    private sealed class VerifierLlmService(Func<string, string> answer) : ILlmService
    {
        private readonly Lock _lock = new();
        private readonly List<string> _users = [];
        private readonly List<string> _systems = [];
        private readonly List<string> _tools = [];
        private int _calls;

        public string Name => "verifier-fake";
        public TimeSpan Delay { get; init; }
        public int Calls => Volatile.Read(ref _calls);
        public IReadOnlyList<string> UserMessages { get { lock (_lock) { return [.. _users]; } } }
        public IReadOnlyList<string> SystemPrompts { get { lock (_lock) { return [.. _systems]; } } }
        public IReadOnlyList<string> ToolNames { get { lock (_lock) { return [.. _tools]; } } }

        public async Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var user = request.Conversation.History.Messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content ?? string.Empty;
            lock (_lock)
            {
                _users.Add(user);
                _systems.Add(request.SystemPrompt ?? request.Conversation.History.Messages.FirstOrDefault(m => m.Role == LlmConstants.MessageRoles.System)?.Content ?? string.Empty);
                _tools.Clear();
                _tools.AddRange((request.Tools ?? []).Select(t => t.Name));
            }

            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            try
            {
                return LlmResult.Success([new LlmResponse { Content = answer(user), FinishReason = FinishReason.Stop }],
                    tokensUsed: 10, promptTokens: 5, completionTokens: 5, model: "fake-model");
            }
            catch (Exception ex)
            {
                return LlmResult.Failure(ex, isRetriable: false);
            }
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private TestHost(IConfiguration configuration, ServiceProvider services)
        {
            Configuration = configuration;
            Services = services;
            Roles = LlmRoleFactory.BuildRoles(services, _ => null, _ => { });
        }

        public IConfiguration Configuration { get; }
        public ServiceProvider Services { get; }
        public Dictionary<string, LlmRole> Roles { get; }
        public AITaskAgent.Support.Template.ITemplateProvider Templates { get; } = AnalysisPipelineFactory.CreateDefaultTemplateProvider();

        public static Task<TestHost> CreateAsync(VerifierLlmService llm, params (string, string?)[] pairs)
        {
            var configuration = TestConfig.FromPairs([.. FakeProvider, .. pairs]);
            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            services.AddLogging();
            services.AddSingleton<ILlmService>(llm);
            services.AddAITaskAgent();
            var provider = services.BuildServiceProvider();
            provider.UseAITaskAgent();
            return Task.FromResult(new TestHost(configuration, provider));
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }
}
