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
    /// Fase de un nombre de step conocido (los seis de primer nivel, el primero de la regeneración, los auditores y los
    /// sub-steps LLM del enriquecimiento y la síntesis); null si no es ninguno.
    /// </summary>
    public static string? PhaseOfStep(string? stepName)
    {
        if (string.IsNullOrEmpty(stepName))
            return null;

        return stepName switch
        {
            DetectAndIndexGraphStep.StepName => AnalysisPhases.Detect,
            LanguageEnricherStep.StepName => AnalysisPhases.Enrich,
            BdpEnricherStep.StepName => AnalysisPhases.Enrich,
            AnalysisPipelineFactory.SemanticEnricherStepName => AnalysisPhases.Enrich,
            PrepareRegenerationStep.StepName => AnalysisPhases.Facets,
            SqlFacetExtractorStep.StepName => AnalysisPhases.Facets,
            AnalysisPipelineFactory.ParallelReportsStepName=> AnalysisPhases.Reports,
            ExecutiveSynthesizerStep.StepName => AnalysisPhases.Synthesis,
            AnalysisPipelineFactory.SynthesizerStepName => AnalysisPhases.Synthesis,
            _ when AnalysisPipelineFactory.TryParseAuditorStepName(stepName, out _) => AnalysisPhases.Reports,
            _ => null
        };
    }

    private static bool IsTopLevelStep(string stepName) =>
        stepName is DetectAndIndexGraphStep.StepName
            or LanguageEnricherStep.StepName
            or BdpEnricherStep.StepName
            or PrepareRegenerationStep.StepName
            or SqlFacetExtractorStep.StepName
            or AnalysisPipelineFactory.ParallelReportsStepName
            or ExecutiveSynthesizerStep.StepName;

    private static string? ReportOf(string? stepName) =>
        stepName != null && AnalysisPipelineFactory.TryParseAuditorStepName(stepName, out var type) ? type.ToString() : null;

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
                return Dto(AnalysisEventDtoTypes.ToolStarted, AnalysisEventLevels.Info,
                    $"{ts.StepName} → {ts.ToolName}",
                    new() { ["tool"] = ts.ToolName });

            case ToolCompletedEvent tc:
                return Dto(AnalysisEventDtoTypes.ToolCompleted,
                    tc.Success ? AnalysisEventLevels.Info : AnalysisEventLevels.Warning,
                    tc.Success
                        ? $"{tc.ToolName} ({Ms(tc.Duration)} ms)"
                        : $"{tc.ToolName} falló ({Ms(tc.Duration)} ms): {tc.ErrorMessage}",
                    new() { ["tool"] = tc.ToolName, ["success"] = tc.Success, ["durationMs"] = Ms(tc.Duration), ["error"] = tc.ErrorMessage });

            case StepProgressEvent sp when sp.EventType == AnalysisEventTypes.ReportWritten:
                return MapReportWritten(sp, Dto);

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

    private static string? MetaString(Dictionary<string, object?>? metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static long Ms(TimeSpan duration) => (long)Math.Round(duration.TotalMilliseconds);

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:F1} s" : $"{Ms(duration)} ms";

    internal static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text ?? string.Empty : text[..max] + "…";
}
