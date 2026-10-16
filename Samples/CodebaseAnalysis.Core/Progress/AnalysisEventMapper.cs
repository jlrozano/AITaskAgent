using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Steps;
using Key = CodebaseAnalysis.Core.Progress.AnalysisEventTypes.MetadataKeys;

namespace CodebaseAnalysis.Core.Progress;

/// <summary>
/// Traduce los eventos del framework y los de dominio del pipeline a <see cref="AnalysisEventDto"/>
/// (plan §4.3). Una instancia por ejecución y un solo hilo: el suscriptor del canal la llama en orden.
/// </summary>
/// <remarks>
/// La fase sale del nombre del step, nunca de su ruta. Los componentes que no son un step de primer
/// nivel ni un auditor (helpers, sub-steps sin middleware) heredan la fase del último step de primer
/// nivel que empezó: los seis se ejecutan en secuencia, así que esa fase es la que está en curso.
/// </remarks>
public sealed class AnalysisEventMapper(string runId)
{
    /// <summary>Tope de caracteres de los argumentos de una tool call en <c>llm.response</c>.</summary>
    public const int MaxToolArgumentsChars = 500;

    private string? _currentPhase;

    /// <summary>
    /// Fase de un nombre de step conocido (los de primer nivel, incluidos los cuatro de preparación, el primero de la
    /// regeneración, los auditores y sus investigaciones, y los sub-steps LLM del enriquecimiento, el nombrado de dominios y la
    /// síntesis); null si no es ninguno.
    /// </summary>
    public static string? PhaseOfStep(string? stepName)
    {
        if (string.IsNullOrEmpty(stepName))
            return null;

        return stepName switch
        {
            DetectAndIndexGraphStep.StepName => AnalysisPhases.Detect,
            LanguageEnricherStep.StepName => AnalysisPhases.Enrich,
            DotNetEnricherStep.StepName => AnalysisPhases.Enrich,
            AnalysisPipelineFactory.SemanticEnricherStepName => AnalysisPhases.Enrich,
            PrepareRegenerationStep.StepName => AnalysisPhases.Facets,
            SqlFacetExtractorStep.StepName => AnalysisPhases.Facets,
            RepositoryInventoryStep.StepName => AnalysisPhases.Facets,
            DomainDiscoveryStep.StepName => AnalysisPhases.Facets,
            DomainDiscoveryStep.NamingStepName => AnalysisPhases.Facets,
            CoveragePlanningStep.StepName => AnalysisPhases.Facets,
            TimeBudgetStep.StepName => AnalysisPhases.Facets,
            SharedPassStep.StepName => AnalysisPhases.Facets,
            SharedPass.BatchReader.StepName => AnalysisPhases.Facets,
            SharedPass.FindingJudge.StepName => AnalysisPhases.Facets,
            AnalysisPipelineFactory.ParallelReportsStepName=> AnalysisPhases.Reports,
            ExecutiveSynthesizerStep.StepName => AnalysisPhases.Synthesis,
            AnalysisPipelineFactory.SynthesizerStepName => AnalysisPhases.Synthesis,
            _ when AnalysisPipelineFactory.TryParseAuditorStepName(stepName, out _) => AnalysisPhases.Reports,
            _ when AnalysisPipelineFactory.TryParseInvestigationStepName(stepName, out _, out _) => AnalysisPhases.Reports,
            _ => null
        };
    }

    private static bool IsTopLevelStep(string stepName) =>
        stepName is DetectAndIndexGraphStep.StepName
            or LanguageEnricherStep.StepName
            or DotNetEnricherStep.StepName
            or PrepareRegenerationStep.StepName
            or SqlFacetExtractorStep.StepName
            or RepositoryInventoryStep.StepName
            or DomainDiscoveryStep.StepName
            or CoveragePlanningStep.StepName
            or TimeBudgetStep.StepName
            or SharedPassStep.StepName
            or AnalysisPipelineFactory.ParallelReportsStepName
            or ExecutiveSynthesizerStep.StepName;

    private static string? ReportOf(string? stepName)
    {
        if (stepName == null)
            return null;

        // El auditor de un informe, su verificador (el paso y su sub-step LLM) o una de sus investigaciones por subsistema: el carril es el mismo.
        if (AnalysisPipelineFactory.TryParseAuditorStepName(stepName, out var type)
            || AnalysisPipelineFactory.TryParseVerificationStepName(stepName, out type)
            || AnalysisPipelineFactory.TryParseInvestigationStepName(stepName, out type, out _))
            return type.ToString();

        return null;
    }

    /// <summary>
    /// El DTO del evento, o null si no se registra: los <see cref="LlmResponseEvent"/> de streaming (un
    /// rol con <c>UseStreaming</c> inundaría el log) y el nombre del pipeline como paso.
    /// </summary>
    public AnalysisEventDto? Map(IProgressEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (evt is StepStartedEvent started && IsTopLevelStep(started.StepName))
        {
            _currentPhase = PhaseOfStep(started.StepName);
        }

        var source = string.IsNullOrEmpty(evt.StepName) ? null : evt.StepName;
        var phase = PhaseOfStep(source) ?? _currentPhase;

        AnalysisEventDto Dto(string type, string level, string message, Dictionary<string, object?>? data, string? report = null) => new()
        {
            RunId = runId,
            Ts = evt.Timestamp,
            Type = type,
            Source = source,
            Report = report ?? ReportOf(source),
            Phase = phase,
            Level = level,
            Message = message,
            Data = data
        };

        switch (evt)
        {
            case PipelineStartedEvent ps:
                return Dto(AnalysisEventDtoTypes.PipelineStarted, AnalysisEventLevels.Info,
                    $"Pipeline {ps.PipelineName} iniciado ({ps.TotalSteps} pasos)",
                    new() { ["totalSteps"] = ps.TotalSteps }) with { Phase = null };

            case PipelineCompletedEvent pc:
                return Dto(AnalysisEventDtoTypes.PipelineCompleted,
                    pc.Success ? AnalysisEventLevels.Success : AnalysisEventLevels.Error,
                    pc.Success
                        ? $"Pipeline {pc.PipelineName} completado en {FormatDuration(pc.Duration)}"
                        : $"Pipeline {pc.PipelineName} terminado con error: {pc.ErrorMessage}",
                    new() { ["success"] = pc.Success, ["durationMs"] = Ms(pc.Duration), ["error"] = pc.ErrorMessage }) with { Phase = null };

            case StepStartedEvent ss:
                return Dto(AnalysisEventDtoTypes.StepStarted,
                    ss.AttemptNumber > 1 ? AnalysisEventLevels.Warning : AnalysisEventLevels.Info,
                    ss.AttemptNumber > 1 ? $"{ss.StepName}: reintento {ss.AttemptNumber}" : $"{ss.StepName} iniciado",
                    new() { ["stepPath"] = ss.StepPath, ["attempt"] = ss.AttemptNumber });

            case StepCompletedEvent sc:
                return MapStepCompleted(sc, Dto);

            case StepValidationEvent sv:
                return Dto(AnalysisEventDtoTypes.StepValidation,
                    sv.IsValid ? AnalysisEventLevels.Info : AnalysisEventLevels.Warning,
                    sv.IsValid ? $"{sv.StepName}: validación correcta" : $"{sv.StepName}: validación fallida ({sv.ValidationError})",
                    new() { ["isValid"] = sv.IsValid, ["validationType"] = sv.ValidationType, ["error"] = sv.ValidationError, ["attempt"] = sv.AttemptNumber });

            case StepTimeoutEvent st:
                return Dto(AnalysisEventDtoTypes.StepTimeout, AnalysisEventLevels.Error,
                    $"{st.StepName}: tiempo agotado ({FormatDuration(st.ConfiguredTimeout)})",
                    new() { ["configuredTimeoutMs"] = Ms(st.ConfiguredTimeout) });

            case BranchStartedEvent bs:
                return Dto(AnalysisEventDtoTypes.BranchStarted, AnalysisEventLevels.Info,
                    $"{bs.BranchName} iniciado",
                    new() { ["branch"] = bs.BranchName },
                    report: ReportOf(bs.BranchName));

            case BranchCompletedEvent bc:
                return Dto(AnalysisEventDtoTypes.BranchCompleted,
                    bc.Success ? AnalysisEventLevels.Success : AnalysisEventLevels.Error,
                    bc.Success
                        ? $"{bc.BranchName} completado en {FormatDuration(bc.Duration)}"
                        : $"{bc.BranchName} falló: {bc.ErrorMessage}",
                    new() { ["branch"] = bc.BranchName, ["success"] = bc.Success, ["durationMs"] = Ms(bc.Duration), ["error"] = bc.ErrorMessage },
                    report: ReportOf(bc.BranchName));

            case LlmResponseEvent lr:
                if (lr.FinishReason == FinishReason.Streaming)
                    return null;
                return MapLlmResponse(lr, Dto);

            case ToolStartedEvent ts:
            {
                var data = new Dictionary<string, object?> { ["tool"] = ts.ToolName };
                ToolEventPayload.AddTo(data, ts.AdditionalData, includeOutput: false);
                return Dto(AnalysisEventDtoTypes.ToolStarted, AnalysisEventLevels.Info, $"{ts.StepName} → {ts.ToolName}", data);
            }

            case ToolCompletedEvent tc:
            {
                // Los argumentos van también aquí para que el detalle de la llamada salga de un solo evento; la salida, truncada.
                var data = new Dictionary<string, object?>
                {
                    ["tool"] = tc.ToolName, ["success"] = tc.Success, ["durationMs"] = Ms(tc.Duration), ["error"] = tc.ErrorMessage
                };
                ToolEventPayload.AddTo(data, tc.AdditionalData, includeOutput: true);
                return Dto(AnalysisEventDtoTypes.ToolCompleted,
                    tc.Success ? AnalysisEventLevels.Info : AnalysisEventLevels.Warning,
                    tc.Success
                        ? $"{tc.ToolName} ({Ms(tc.Duration)} ms)"
                        : $"{tc.ToolName} falló ({Ms(tc.Duration)} ms): {tc.ErrorMessage}",
                    data);
            }

            case StepProgressEvent sp when sp.EventType == AnalysisEventTypes.SharedPassProgress:
                return Dto(AnalysisEventDtoTypes.SharedPassProgress,
                    MetaString(sp.Metadata, Key.Level) ?? AnalysisEventLevels.Info,
                    sp.Message,
                    sp.Metadata?.Where(kv => kv.Key != Key.Level).ToDictionary(kv => kv.Key, kv => kv.Value));

            case StepProgressEvent sp when sp.EventType == AnalysisEventTypes.ReportWritten:
                return MapReportWritten(sp, Dto);

            case StepProgressEvent sp when sp.EventType == AnalysisEventTypes.ReportVerified:
                return MapReportVerified(sp, Dto);

            case StepProgressEvent sp:
                return Dto(AnalysisEventDtoTypes.Log,
                    MetaString(sp.Metadata, Key.Level) ?? AnalysisEventLevels.Info,
                    sp.Message,
                    new() { ["channel"] = MetaString(sp.Metadata, Key.Channel) ?? AnalysisEventTypes.Channels.Pipeline });

            default:
                // Tipos del framework que el plan no enumera (routing, caché, tags...): se conservan con su
                // tipo original para no perder información, sin datos.
                return Dto(evt.EventType, AnalysisEventLevels.Info, evt.EventType, null);
        }
    }

    private static AnalysisEventDto MapStepCompleted(
        StepCompletedEvent sc,
        Func<string, string, string, Dictionary<string, object?>?, string?, AnalysisEventDto> dto)
    {
        var data = new Dictionary<string, object?>
        {
            ["stepPath"] = sc.StepPath,
            ["success"] = sc.Success,
            ["durationMs"] = Ms(sc.Duration),
            ["error"] = sc.ErrorMessage
        };

        // Del resultado solo se copian cifras: el objeto (el informe entero, en un auditor) no se serializa.
        if (sc.StepResult is ILlmStepResult llm)
        {
            data["tokens"] = llm.TokensUsed;
            data["costUsd"] = llm.CostUsd;
            data["finishReason"] = llm.FinishReason?.ToString();
        }

        return dto(AnalysisEventDtoTypes.StepCompleted,
            sc.Success ? AnalysisEventLevels.Success : AnalysisEventLevels.Error,
            sc.Success
                ? $"{sc.StepName} completado en {FormatDuration(sc.Duration)}"
                : $"{sc.StepName} falló: {sc.ErrorMessage}",
            data, null);
    }

    private static AnalysisEventDto MapLlmResponse(
        LlmResponseEvent lr,
        Func<string, string, string, Dictionary<string, object?>?, string?, AnalysisEventDto> dto)
    {
        var calls = (lr.ToolCalls ?? []).Select(c => (object?)new Dictionary<string, object?>
        {
            ["id"] = c.Id,
            ["name"] = c.Name,
            ["arguments"] = Truncate(c.Arguments, MaxToolArgumentsChars)
        }).ToList();

        var message = calls.Count > 0
            ? $"{lr.StepName}: el modelo pide {calls.Count} tool(s): {string.Join(", ", (lr.ToolCalls ?? []).Select(c => c.Name))}"
            : $"{lr.StepName}: respuesta del modelo ({lr.FinishReason}, {lr.TokensUsed} tokens)";

        return dto(AnalysisEventDtoTypes.LlmResponse,
            lr.FinishReason == FinishReason.Length ? AnalysisEventLevels.Warning : AnalysisEventLevels.Info,
            message,
            new()
            {
                ["finishReason"] = lr.FinishReason.ToString(),
                ["tokens"] = lr.TokensUsed,
                // Los de esta llamada, solo en la opción principal: la SPA los suma para el consumo en vivo (LlmUsageTracker).
                ["promptTokens"] = lr.PromptTokens,
                ["completionTokens"] = lr.CompletionTokens,
                // Parte de promptTokens servida desde la caché de prefijo del proveedor (null si no lo informa).
                ["cachedPromptTokens"] = lr.CachedPromptTokens,
                ["costUsd"] = lr.CostUsd,
                ["model"] = lr.Model,
                ["contentLength"] = lr.Content?.Length ?? 0,
                ["toolCalls"] = calls
            }, null);
    }

    private static AnalysisEventDto MapReportWritten(
        StepProgressEvent sp,
        Func<string, string, string, Dictionary<string, object?>?, string?, AnalysisEventDto> dto)
    {
        var reportType = MetaString(sp.Metadata, Key.ReportType);
        var health = MetaString(sp.Metadata, Key.Health) ?? SingleReportResult.UnknownHealth;
        var truncated = sp.Metadata?.GetValueOrDefault(Key.Truncated) is true;

        return dto(AnalysisEventDtoTypes.ReportWritten,
            truncated ? AnalysisEventLevels.Warning : AnalysisEventLevels.Success,
            sp.Message,
            new()
            {
                ["fileName"] = MetaString(sp.Metadata, Key.FileName),
                ["reportType"] = reportType,
                ["health"] = health,
                ["criticalFindings"] = sp.Metadata?.GetValueOrDefault(Key.CriticalFindings) as int?,
                ["truncated"] = truncated
            },
            reportType);
    }

    private static AnalysisEventDto MapReportVerified(
        StepProgressEvent sp,
        Func<string, string, string, Dictionary<string, object?>?, string?, AnalysisEventDto> dto)
    {
        var outcome = MetaString(sp.Metadata, Key.Outcome);
        return dto(AnalysisEventDtoTypes.ReportVerified,
            outcome switch
            {
                "verified" or "corrected" => AnalysisEventLevels.Success,
                "warnings" => AnalysisEventLevels.Warning,
                _ => AnalysisEventLevels.Error
            },
            sp.Message,
            new()
            {
                ["fileName"] = MetaString(sp.Metadata, Key.FileName),
                ["reportType"] = MetaString(sp.Metadata, Key.ReportType),
                ["outcome"] = outcome,
                ["changes"] = sp.Metadata?.GetValueOrDefault(Key.Changes) as int? ?? 0,
                ["previousVersionId"] = MetaString(sp.Metadata, Key.PreviousVersionId),
                ["versionId"] = MetaString(sp.Metadata, Key.VersionId),
                ["error"] = MetaString(sp.Metadata, Key.Error)
            },
            MetaString(sp.Metadata, Key.ReportType));
    }

    private static string? MetaString(Dictionary<string, object?>? metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static long Ms(TimeSpan duration) => (long)Math.Round(duration.TotalMilliseconds);

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:F1} s" : $"{Ms(duration)} ms";

    internal static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text ?? string.Empty : text[..max] + "…";
}
