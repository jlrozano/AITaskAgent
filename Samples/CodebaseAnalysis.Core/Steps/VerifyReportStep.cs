using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Results;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;
using System.Globalization;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// Verifica un informe ya escrito en disco, en tres tiempos dentro del mismo paso: prepara (valida el informe y arma el
/// texto del verificador), llama al modelo del rol <c>ReportVerifier</c> con sus tools y cierra (valida lo devuelto y lo
/// escribe con <see cref="ReportWriter.Update"/>, que guarda el original como <c>baseline</c> para «Deshacer»). El sub-step
/// LLM se invoca sin middleware, como el de la síntesis, y el límite lo aplica este paso. Si el verificador falla, no
/// responde a tiempo o devuelve algo inservible, el fichero no se toca. Va dentro de
/// <see cref="AnalysisPipelineFactory.CreateVerificationPipeline"/>.
/// </summary>
public sealed class VerifyReportStep : IStep<GraphStatusResult, VerificationResult>
{
    private readonly ReportType _reportType;
    private readonly CustomStatelessTemplateLlmStep<VerificationInput, LlmStringStepResult> _llmStep;
    private readonly Lazy<ReportValidator.ReportContentValidator> _validator;
    private readonly TimeSpan _llmTimeout;

    public VerifyReportStep(
        ReportType reportType,
        CustomStatelessTemplateLlmStep<VerificationInput, LlmStringStepResult> llmStep,
        Lazy<ReportValidator.ReportContentValidator> validator,
        TimeSpan llmTimeout)
    {
        _reportType = reportType;
        _llmStep = llmStep;
        _validator = validator;
        _llmTimeout = llmTimeout;
    }

    public string Name => AnalysisPipelineFactory.VerifyStepName(_reportType);
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 500;

    /// <summary><c>CodebaseAnalysis:Timeouts:VerifierMinutes</c> más un margen para validar y escribir; lo fija la factoría.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(12);
    public Type InputType => typeof(GraphStatusResult);
    public Type OutputType => typeof(VerificationResult);

    public async Task<VerificationResult> ExecuteAsync(
        GraphStatusResult input,
        PipelineContext context,
        int attempt,
        VerificationResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        var fileName = AnalysisPipelineInput.GetReportFileName(_reportType);
        var config = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput) ?? new AnalysisPipelineInput();
        var path = Path.Combine(config.OutputReportsPath, fileName);
        if (!File.Exists(path))
            return Fail(context, fileName, $"No existe el informe '{fileName}': no hay nada que verificar.");

        // 1. Preparación: el informe tal como está y lo que la validación determinista encuentra en él.
        var original = (await File.ReadAllTextAsync(path, cancellationToken)).Replace("\r\n", "\n");
        var validator = _validator.Value;
        var before = validator.Validate(fileName, original);
        var failures = before.Issues.Count(i => i.Check is not (ReportValidationChecks.Metadata or ReportValidationChecks.Coverage));
        context.Report(Name, AnalysisLogLevel.Info,
            $"🔎 Verificando '{fileName}': {before.Citations.Count} cita(s) de código y {failures} fallo(s) de validación.");
        var text = ReportVerification.BuildInput(fileName, original, before, validator.Root);

        // 2. El verificador, en una conversación nueva y con su propio límite de tiempo.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_llmTimeout != System.Threading.Timeout.InfiniteTimeSpan)
            limit.CancelAfter(_llmTimeout);

        IStepResult llmResult;
        try
        {
            llmResult = await _llmStep.ExecuteStepAsync(new VerificationInput(text), context, attempt, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(context, fileName,
                $"El verificador superó el límite de {_llmTimeout.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)} min " +
                $"({AnalysisTimeouts.SectionName}:{nameof(AnalysisTimeouts.VerifierMinutes)}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(context, fileName, $"El verificador falló: {ex.Message}");
        }

        if (llmResult.HasError)
            return Fail(context, fileName, $"El verificador falló: {llmResult.Error?.Message}");
        if (llmResult is ILlmStepResult { FinishReason: FinishReason.Length })
            return Fail(context, fileName, "La respuesta del verificador se cortó al alcanzar MaxOutputTokens: se descarta.");

        var output = ReportVerification.ParseOutput(
            (llmResult as LlmStringStepResult)?.Content ?? llmResult.Value?.ToString(), original);
        if (output.Kind == VerificationOutputKind.Invalid)
            return Fail(context, fileName, $"Respuesta del verificador no utilizable: {output.Problem}.");

        // 3. Cierre: lo que se escribe, validado, con el resultado en la línea analysis-meta.
        var corrected = output.Kind == VerificationOutputKind.Corrected;
        if (corrected && ReportVerification.SameBody(original, output.Report!))
        {
            context.Report(Name, AnalysisLogLevel.Warning,
                $"⚠️ El verificador de '{fileName}' declaró {output.Changes} cambio(s) pero el texto del informe no cambia: se toma como sin cambios. Declaró: {ReportVerification.ChangesSummary(output.Report!)}");
            corrected = false;
        }

        var changes = corrected ? output.Changes : 0;
        var content = corrected ? ReportVerification.Compose(original, output.Report!) : original;
        var warnings = ReportVerification.CountWarnings(
            corrected ? validator.Validate(fileName, ReportVerification.WithoutChangesSection(content)) : before);
        content = ReportVerification.WithVerificationMeta(content, changes, warnings);

        var paths = context.Get<KnowledgePaths>(AnalysisContextKeys.KnowledgePaths)
            ?? new KnowledgePaths(config.RepositoryPath, config.OutputReportsPath);
        string? previousVersionId = null, versionId = null;
        try
        {
            var written = new ReportWriter(paths).Update(SourceRef.Report(fileName), ReportUpdateModes.Replace, null, content,
                $"Verificación: {ReportVerification.MetaValue(changes, warnings)}", ReportVersionOrigin.Pipeline);
            previousVersionId = written.PreviousVersionId;
            versionId = written.VersionId;
        }
        catch (KnowledgeOperationException ex) when (ex.Code == KnowledgeErrorCodes.InvalidRequest)
        {
            // Idéntico al actual: ya estaba verificado con el mismo resultado; no hay versión nueva.
            context.Report(Name, AnalysisLogLevel.Info, $"'{fileName}' no cambia: {ex.Message}");
        }
        catch (KnowledgeOperationException ex)
        {
            return Fail(context, fileName, $"No se pudo guardar el informe verificado: {ex.Message}");
        }

        var outcome = warnings > 0
            ? VerificationOutcomes.Warnings
            : corrected ? VerificationOutcomes.Corrected : VerificationOutcomes.Verified;
        var level = warnings > 0 ? AnalysisLogLevel.Warning : AnalysisLogLevel.Success;
        var summary = outcome switch
        {
            VerificationOutcomes.Verified => $"{fileName} verificado: sin correcciones.",
            VerificationOutcomes.Corrected => $"{fileName} verificado: {changes} corrección(es).",
            _ => $"{fileName} verificado con {warnings} aviso(s) de validación pendientes" + (changes > 0 ? $" y {changes} corrección(es)." : ".")
        };
        context.Report(Name, level, (warnings > 0 ? "⚠️ " : "✅ ") + summary);
        context.ReportVerified(Name, _reportType, fileName, outcome, changes, summary, previousVersionId, versionId);

        return new VerificationResult(this)
        {
            FileName = fileName,
            Outcome = outcome,
            Changes = changes,
            Warnings = warnings,
            PreviousVersionId = previousVersionId,
            VersionId = versionId
        };
    }

    private VerificationResult Fail(PipelineContext context, string fileName, string reason)
    {
        context.Report(Name, AnalysisLogLevel.Warning, $"⚠️ No se pudo verificar '{fileName}': {reason} El informe queda como estaba.");
        context.ReportVerified(Name, _reportType, fileName, VerificationOutcomes.Failed, 0,
            $"{fileName}: no se pudo verificar. {reason}", error: reason);
        return new VerificationResult(this) { FileName = fileName, Outcome = VerificationOutcomes.Failed, FailureReason = reason };
    }

    public Task FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult((result is VerificationResult, (string?)null));
}
