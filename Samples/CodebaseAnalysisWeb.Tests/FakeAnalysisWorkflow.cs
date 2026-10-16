using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysisWeb.Analysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Key = CodebaseAnalysis.Core.Progress.AnalysisEventTypes.MetadataKeys;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>
/// Ejecución falsa: emite por el canal los eventos del framework y de dominio de una ejecución con 2 auditores
/// (uno con reintento y tools), en el mismo orden de tipos que <c>successfulRun</c> de
/// <c>ClientApp/src/test/fixtures.ts</c>, y escribe los tres informes. Con <see cref="HoldMidway"/> se detiene
/// tras arrancar los auditores hasta <see cref="Release"/> o la cancelación. Con <see cref="FailingReports"/>, esos
/// auditores fallan sin escribir su informe. <see cref="RegenerateAsync"/> emite la regeneración reducida (preparación,
/// facetas, los auditores pedidos y, si se pide, la síntesis), con la misma pausa.
/// </summary>
public sealed class FakeAnalysisWorkflow : IAnalysisWorkflow
{
    /// <summary>Tipos de evento que produce una ejecución completa, incluidos los del runner (<c>run.*</c>).</summary>
    public static readonly string[] ExpectedTypes =
    [
        "run.started", "pipeline.started",
        "step.started", "log", "log", "step.completed",
        "step.started", "log", "step.completed",
        "step.started", "log", "step.completed",
        "step.started", "step.completed",
        "step.started", "branch.started", "branch.started", "step.started", "step.started",
        "llm.response", "tool.started", "tool.completed", "tool.started", "tool.completed",
        "step.completed", "step.started",
        "report.written", "step.completed", "branch.completed",
        "report.written", "step.completed", "branch.completed",
        "step.completed",
        "step.started", "report.written", "step.completed",
        "pipeline.completed",
        // El resumen de la validación de informes (ReportValidationSummary), que añade el runner antes de run.completed.
        "log", "run.completed"
    ];

    /// <summary>Eventos emitidos hasta la pausa (incluido <c>run.started</c>).</summary>
    public const int EventsBeforeHold = 19;

    private TaskCompletionSource _release = NewTcs();
    private TaskCompletionSource _held = NewTcs();

    public ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;

    public bool HoldMidway { get; set; }

    /// <summary>Si se fija, la ejecución lanza esta excepción tras arrancar los auditores (y tras la pausa, si la hay).</summary>
    public Exception? ThrowMidway { get; set; }

    /// <summary>Auditores que fallan (HTTP 500, sin <c>report.written</c>), en la ejecución y en la regeneración.</summary>
    public HashSet<ReportType> FailingReports { get; } = [];

    /// <summary>Las llamadas a <see cref="RegenerateAsync"/>, en orden.</summary>
    public List<(IReadOnlyList<ReportType> Reports, bool Synthesis)> Regenerations { get; } = [];

    /// <summary>Las llamadas a <see cref="VerifyAsync"/>, en orden.</summary>
    public List<(IReadOnlyList<ReportType> Reports, bool Synthesis)> Verifications { get; } = [];

    /// <summary>Informes cuya verificación falla (el verificador no responde): el informe queda como estaba.</summary>
    public HashSet<ReportType> FailingVerifications { get; } = [];

    /// <summary>Se completa con el token de la ejecución cuando este se cancela.</summary>
    public TaskCompletionSource CancellationObserved { get; } = NewTcs();

    /// <summary>Se completa cuando la ejecución llega a la pausa.</summary>
    public Task Held => _held.Task;

    public void Release() => _release.TrySetResult();

    public void Reset()
    {
        _release = NewTcs();
        _held = NewTcs();
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const string FailureMessage = "HTTP 500";

    private static string PathOf(string step) => $"CodebaseAnalysisPipeline/{step}";

    private static StepStartedEvent Started(string step, int attempt = 1) => new() { StepName = step, StepPath = PathOf(step), AttemptNumber = attempt };

    private static StepCompletedEvent Completed(string step, bool ok = true, string? error = null, ILlmStepResultStub? llm = null) => new()
    {
        StepName = step,
        StepPath = PathOf(step),
        Success = ok,
        ErrorMessage = error,
        Duration = TimeSpan.FromMilliseconds(100),
        StepResult = llm
    };

    /// <summary>Escribe el informe y emite su <c>report.written</c>, como el auditor real.</summary>
    private static async Task WrittenAsync(IEventChannel channel, AnalysisPipelineInput input, string step, ReportType? type, string fileName,
        string health, int? critical, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(input.OutputReportsPath);
        await File.WriteAllTextAsync(Path.Combine(input.OutputReportsPath, fileName),
            $"# {fileName}\n\n## Resumen\n\nInforme de prueba.\n\n<!-- analysis-meta: health={health}{(critical is int c ? $"; critical={c}" : "")} -->\n",
            cancellationToken);
        await channel.SendAsync(new StepProgressEvent
        {
            StepName = step,
            EventType = AnalysisEventTypes.ReportWritten,
            Message = $"{fileName} [{health}]",
            Metadata = new()
            {
                [Key.FileName] = fileName,
                [Key.FilePath] = Path.Combine(input.OutputReportsPath, fileName),
                [Key.ReportType] = type?.ToString(),
                [Key.Health] = health,
                [Key.CriticalFindings] = critical,
                [Key.Truncated] = false
            }
        }, cancellationToken);
    }

    /// <summary>Ficheros de <see cref="FailingReports"/> entre <paramref name="reports"/>, en el orden del catálogo.</summary>
    private List<string> FailedFiles(IEnumerable<ReportType> reports) =>
        reports.Where(FailingReports.Contains).Order().Select(AnalysisPipelineInput.GetReportFileName).ToList();

    public async Task<IStepResult> RunAsync(
        AnalysisPipelineInput input,
        IEventChannel channel,
        IAnalysisReporter reporter,
        CancellationToken cancellationToken)
    {
        var logger = LoggerFactory.CreateLogger("FakeAnalysisWorkflow");
        const string pipeline = "CodebaseAnalysisPipeline";
        var smells = AnalysisPipelineFactory.AuditorStepName(ReportType.CodeSmells);
        var di = AnalysisPipelineFactory.AuditorStepName(ReportType.InyeccionDependenciasExtensiones);
        var parallel = AnalysisPipelineFactory.ParallelReportsStepName;

        Task Send(IProgressEvent e) => channel.SendAsync(e, cancellationToken);
        Task Started(string step, int attempt = 1) => Send(FakeAnalysisWorkflow.Started(step, attempt));
        Task Completed(string step, bool ok = true, string? error = null, ILlmStepResultStub? llm = null) =>
            Send(FakeAnalysisWorkflow.Completed(step, ok, error, llm));
        Task Log(string step, string message, string channelName = AnalysisEventTypes.Channels.Pipeline) => Send(new StepProgressEvent
        {
            StepName = step,
            EventType = AnalysisEventTypes.Log,
            Message = message,
            Metadata = new() { [Key.Level] = "info", [Key.Channel] = channelName }
        });
        Task Written(string step, ReportType? type, string fileName, string health, int? critical) =>
            WrittenAsync(channel, input, step, type, fileName, health, critical, cancellationToken);

        await Send(new PipelineStartedEvent { StepName = pipeline, PipelineName = pipeline, TotalSteps = 6 });

        await Started(DetectAndIndexGraphStep.StepName);
        await Log(DetectAndIndexGraphStep.StepName, "   [indexador] indexing", AnalysisEventTypes.Channels.Indexer);
        await Log(DetectAndIndexGraphStep.StepName, "   [indexador] done", AnalysisEventTypes.Channels.Indexer);
        await Completed(DetectAndIndexGraphStep.StepName);

        // La fase enrich tiene dos pasos: el enriquecimiento nativo por lenguaje y DotNetEnricherStep.
        await Started(LanguageEnricherStep.StepName);
        await Log(LanguageEnricherStep.StepName, "🔎 Lenguajes detectados en disco: C# (12 ficheros .cs).");
        await Completed(LanguageEnricherStep.StepName);

        await Started(DotNetEnricherStep.StepName);
        await Log(DotNetEnricherStep.StepName, "ℹ️ El grafo ya contiene enriquecimiento estructural y semántico previo.");
        await Completed(DotNetEnricherStep.StepName);

        await Started(SqlFacetExtractorStep.StepName);
        await Completed(SqlFacetExtractorStep.StepName);

        await Started(parallel);
        await Send(new BranchStartedEvent { StepName = parallel, BranchName = smells });
        await Send(new BranchStartedEvent { StepName = parallel, BranchName = di });
        await Started(smells);
        await Started(di);

        // Un logger usado dentro de Parallel.ForEachAsync: su línea debe acabar en <repo>/logs (plan §5.5).
        await Parallel.ForEachAsync(new[] { 1, 2 }, cancellationToken, (branch, _) =>
        {
            logger.LogInformation("Rama paralela {Branch} del workflow falso", branch);
            return ValueTask.CompletedTask;
        });

        // Sin using: la continuación del WaitAsync puede correr dentro de Cancel() y, al salir del método,
        // anularía este registro antes de que se ejecute.
        cancellationToken.Register(() => CancellationObserved.TrySetResult());
        if (HoldMidway)
        {
            _held.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        if (ThrowMidway != null)
            throw ThrowMidway;

        await Send(new LlmResponseEvent
        {
            StepName = smells,
            Content = string.Empty,
            FinishReason = FinishReason.ToolCalls,
            TokensUsed = 900,
            PromptTokens = 800,
            CompletionTokens = 100,
            Model = "fake-model",
            ToolCalls =
            [
                new ToolCall { Id = "c1", Name = "view_file", Arguments = "{\"path\":\"src/A.cs\"}" },
                new ToolCall { Id = "c2", Name = "graph_search", Arguments = "{\"name_pattern\":\"A\"}" }
            ]
        });
        // Un chunk de streaming: el log no debe registrarlo.
        await Send(new LlmResponseEvent { StepName = smells, Content = "chunk", FinishReason = FinishReason.Streaming });
        await Send(new ToolStartedEvent { StepName = smells, ToolName = "view_file" });
        await Send(new ToolCompletedEvent { StepName = smells, ToolName = "view_file", Success = true, Duration = TimeSpan.FromMilliseconds(120) });
        await Send(new ToolStartedEvent { StepName = smells, ToolName = "graph_search" });
        await Send(new ToolCompletedEvent { StepName = smells, ToolName = "graph_search", Success = false, ErrorMessage = "timeout" });

        await Completed(di, ok: false, error: "HTTP 429");
        await Started(di, attempt: 2);

        if (FailingReports.Contains(ReportType.CodeSmells))
        {
            await Completed(smells, ok: false, error: FailureMessage);
            await Send(new BranchCompletedEvent { StepName = parallel, BranchName = smells, Success = false, ErrorMessage = FailureMessage });
        }
        else
        {
            await Written(smells, ReportType.CodeSmells, "02_code_smells.md", "Amber", 3);
            await Completed(smells, llm: new ILlmStepResultStub { TokensUsed = 15000, FinishReason = FinishReason.Stop });
            await Send(new BranchCompletedEvent { StepName = parallel, BranchName = smells, Success = true });
        }

        if (FailingReports.Contains(ReportType.InyeccionDependenciasExtensiones))
        {
            await Completed(di, ok: false, error: FailureMessage);
            await Send(new BranchCompletedEvent { StepName = parallel, BranchName = di, Success = false, ErrorMessage = FailureMessage });
        }
        else
        {
            await Written(di, ReportType.InyeccionDependenciasExtensiones, "09_inyeccion_dependencias.md", "Red", 1);
            await Completed(di, llm: new ILlmStepResultStub { TokensUsed = 12000, FinishReason = FinishReason.Length });
            await Send(new BranchCompletedEvent { StepName = parallel, BranchName = di, Success = true });
        }
        await Completed(parallel);

        await Started(ExecutiveSynthesizerStep.StepName);
        await Written(ExecutiveSynthesizerStep.StepName, null, ReportCatalog.ExecutiveReportFileName, "Amber", 4);
        await Completed(ExecutiveSynthesizerStep.StepName);

        await Send(new PipelineCompletedEvent { StepName = pipeline, PipelineName = pipeline, Success = true, Duration = TimeSpan.FromSeconds(1) });

        return new AnalysisPipelineFinalResult
        {
            ExecutiveReportPath = Path.Combine(input.OutputReportsPath, ReportCatalog.ExecutiveReportFileName),
            FailedReports = FailedFiles([ReportType.CodeSmells, ReportType.InyeccionDependenciasExtensiones])
        };
    }

    /// <summary>
    /// Verificación falsa con el orden de pasos del pipeline de verificación: preparación, un <c>Verify_*</c> por informe y, si
    /// se pide, la síntesis. Cada informe emite su <c>report.verified</c> (<c>corrected</c> con 2 cambios y versiones
    /// <c>v0001</c>/<c>v0002</c>, o <c>failed</c> si está en <see cref="FailingVerifications"/>) y no toca el fichero. Con
    /// <see cref="HoldMidway"/> se detiene tras arrancar los verificadores.
    /// </summary>
    public async Task<IStepResult> VerifyAsync(
        AnalysisPipelineInput input,
        IReadOnlyList<ReportType> reports,
        bool synthesis,
        IEventChannel channel,
        IAnalysisReporter reporter,
        CancellationToken cancellationToken)
    {
        Verifications.Add((reports, synthesis));
        var parallel = AnalysisPipelineFactory.ParallelVerificationStepName;
        Task Send(IProgressEvent e) => channel.SendAsync(e, cancellationToken);

        await Send(new PipelineStartedEvent
        {
            StepName = AnalysisPipelineFactory.VerificationPipelineName,
            PipelineName = AnalysisPipelineFactory.VerificationPipelineName,
            TotalSteps = synthesis ? 3 : 2
        });
        await Send(Started(PrepareRegenerationStep.StepName));
        await Send(Completed(PrepareRegenerationStep.StepName));

        await Send(Started(parallel));
        foreach (var report in reports)
        {
            var step = AnalysisPipelineFactory.VerifyStepName(report);
            await Send(new BranchStartedEvent { StepName = parallel, BranchName = step });
            await Send(Started(step));
        }

        cancellationToken.Register(() => CancellationObserved.TrySetResult());
        if (HoldMidway)
        {
            _held.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        foreach (var report in reports)
        {
            var step = AnalysisPipelineFactory.VerifyStepName(report);
            var failed = FailingVerifications.Contains(report);
            await Send(new StepProgressEvent
            {
                StepName = step,
                EventType = AnalysisEventTypes.ReportVerified,
                Message = failed ? $"{AnalysisPipelineInput.GetReportFileName(report)}: no se pudo verificar." : $"{AnalysisPipelineInput.GetReportFileName(report)} verificado: 2 corrección(es).",
                Metadata = new()
                {
                    [Key.FileName] = AnalysisPipelineInput.GetReportFileName(report),
                    [Key.ReportType] = report.ToString(),
                    [Key.Outcome] = failed ? "failed" : "corrected",
                    [Key.Changes] = failed ? 0 : 2,
                    [Key.PreviousVersionId] = failed ? null : "v0001",
                    [Key.VersionId] = failed ? null : "v0002",
                    [Key.Error] = failed ? "El verificador falló: HTTP 500" : null
                }
            });
            await Send(Completed(step));
            await Send(new BranchCompletedEvent { StepName = parallel, BranchName = step, Success = true });
        }

        await Send(Completed(parallel));
        if (synthesis)
        {
            await Send(Started(ExecutiveSynthesizerStep.StepName));
            await WrittenAsync(channel, input, ExecutiveSynthesizerStep.StepName, null, ReportCatalog.ExecutiveReportFileName, "Green", 0, cancellationToken);
            await Send(Completed(ExecutiveSynthesizerStep.StepName));
        }

        await Send(new PipelineCompletedEvent
        {
            StepName = AnalysisPipelineFactory.VerificationPipelineName,
            PipelineName = AnalysisPipelineFactory.VerificationPipelineName,
            Success = true,
            Duration = TimeSpan.FromSeconds(1)
        });

        return synthesis
            ? new AnalysisPipelineFinalResult { ExecutiveReportPath = Path.Combine(input.OutputReportsPath, ReportCatalog.ExecutiveReportFileName) }
            : new ParallelResult(new EmptyStep(parallel));
    }

    /// <summary>
    /// Regeneración falsa con el orden de pasos del pipeline reducido. Cada auditor pedido escribe su informe salvo que
    /// esté en <see cref="FailingReports"/>; con <see cref="HoldMidway"/> se detiene tras arrancarlos.
    /// </summary>
    public async Task<IStepResult> RegenerateAsync(
        AnalysisPipelineInput input,
        IReadOnlyList<ReportType> reports,
        bool synthesis,
        IEventChannel channel,
        IAnalysisReporter reporter,
        CancellationToken cancellationToken)
    {
        Regenerations.Add((reports, synthesis));
        var parallel = AnalysisPipelineFactory.ParallelReportsStepName;
        Task Send(IProgressEvent e) => channel.SendAsync(e, cancellationToken);

        await Send(new PipelineStartedEvent
        {
            StepName = AnalysisPipelineFactory.RegenerationPipelineName,
            PipelineName = AnalysisPipelineFactory.RegenerationPipelineName,
            TotalSteps = synthesis ? 4 : 3
        });
        await Send(Started(PrepareRegenerationStep.StepName));
        await Send(Completed(PrepareRegenerationStep.StepName));
        await Send(Started(SqlFacetExtractorStep.StepName));
        await Send(Completed(SqlFacetExtractorStep.StepName));

        await Send(Started(parallel));
        foreach (var report in reports)
        {
            var step = AnalysisPipelineFactory.AuditorStepName(report);
            await Send(new BranchStartedEvent { StepName = parallel, BranchName = step });
            await Send(Started(step));
        }

        cancellationToken.Register(() => CancellationObserved.TrySetResult());
        if (HoldMidway)
        {
            _held.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        foreach (var report in reports)
        {
            var step = AnalysisPipelineFactory.AuditorStepName(report);
            if (FailingReports.Contains(report))
            {
                await Send(Completed(step, ok: false, error: FailureMessage));
                await Send(new BranchCompletedEvent { StepName = parallel, BranchName = step, Success = false, ErrorMessage = FailureMessage });
                continue;
            }

            await WrittenAsync(channel, input, step, report, AnalysisPipelineInput.GetReportFileName(report), "Green", 0, cancellationToken);
            await Send(Completed(step, llm: new ILlmStepResultStub { TokensUsed = 1000, FinishReason = FinishReason.Stop }));
            await Send(new BranchCompletedEvent { StepName = parallel, BranchName = step, Success = true });
        }

        // Como TolerantReportsParallelStep: el paralelo solo conserva el error si fallan todos sus auditores.
        var failed = FailedFiles(reports);
        var allFailed = failed.Count == reports.Count;
        await Send(Completed(parallel, ok: !allFailed, error: allFailed ? FailureMessage : null));
        if (!synthesis || allFailed)
        {
            await Send(new PipelineCompletedEvent
            {
                StepName = AnalysisPipelineFactory.RegenerationPipelineName,
                PipelineName = AnalysisPipelineFactory.RegenerationPipelineName,
                Success = !allFailed,
                Duration = TimeSpan.FromSeconds(1)
            });
            return new ParallelResult(new EmptyStep(parallel)) { Error = allFailed ? new StepError { Message = FailureMessage } : null };
        }

        await Send(Started(ExecutiveSynthesizerStep.StepName));
        await WrittenAsync(channel, input, ExecutiveSynthesizerStep.StepName, null, ReportCatalog.ExecutiveReportFileName, "Green", 0, cancellationToken);
        await Send(Completed(ExecutiveSynthesizerStep.StepName));
        await Send(new PipelineCompletedEvent
        {
            StepName = AnalysisPipelineFactory.RegenerationPipelineName,
            PipelineName = AnalysisPipelineFactory.RegenerationPipelineName,
            Success = true,
            Duration = TimeSpan.FromSeconds(1)
        });

        return new AnalysisPipelineFinalResult
        {
            ExecutiveReportPath = Path.Combine(input.OutputReportsPath, ReportCatalog.ExecutiveReportFileName),
            FailedReports = failed
        };
    }
}

/// <summary>Resultado LLM mínimo para <c>step.completed</c>.</summary>
public sealed class ILlmStepResultStub() : AITaskAgent.Core.StepResults.StepResult(new AITaskAgent.Core.Steps.EmptyStep("Stub")), AITaskAgent.LLM.Abstractions.ILlmStepResult
{
    public string AssistantMessage { get; set; } = "no se serializa";
    public decimal? CostUsd { get; init; }
    public FinishReason? FinishReason { get; init; }
    public string? Model { get; init; }
    public int? TokensUsed { get; init; }
    public string GetAssistantMessageOrDefault(string? defaultMessage = null) => AssistantMessage;
}
