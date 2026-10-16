using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Notes;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Runtime.CompilerServices;

namespace CodebaseAnalysis.Core.Steps;

public class CustomStatelessTemplateLlmStep<TIn, TOut> : StatelessTemplateLlmStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : ILlmStepResult
{
    public ReportType? AssociatedReportType { get; init; }
    public string? OutputFileName { get; init; }

    /// <summary>
    /// Mensaje de usuario del intento que sigue a agotar <c>MaxToolIterations</c>: sobre la misma conversación, con
    /// los resultados de tools ya reunidos y sin tools, el modelo redacta el informe con lo que tiene. Sin valor, el
    /// tope agotado falla el step como antes.
    /// </summary>
    public string? ToolLimitFinalAnswerPrompt { get; init; }

    /// <summary>
    /// Lo que una invocación recuerda entre intentos: la conversación, que el intento con respuesta final reutiliza en vez
    /// de pedir una nueva (cuando el fallback se decide, BaseLlmStep aún no ha restaurado el bookmark), y si ya está en ese
    /// intento. Vive por <see cref="PipelineContext"/> y no en campos del paso, como el estado de <see cref="BaseLlmStep"/>:
    /// dos ejecuciones del mismo paso, o dos ramas de un <c>ParallelStep</c>, tienen cada una su contexto.
    /// </summary>
    private sealed class InvocationState
    {
        public ConversationContext? Conversation;
        public bool FinalAnswerMode;
    }

    // Por identidad del contexto (PipelineContext es un record, y dos contextos iguales no son la misma ejecución) y sin
    // retenerlo: una invocación que no llega a FinalizeAsync no deja nada vivo cuando su contexto se recoge. El estado de
    // BaseLlmStep usa PipelineContext.StepState, que es interno del framework.
    private readonly ConditionalWeakTable<PipelineContext, InvocationState> _invocations = new();

    private readonly LlmRole _role;
    private readonly ITemplateProvider _templateProvider;
    private string? _systemSha;
    private string? _promptSha;
    private bool _shasComputed;

    private InvocationState State(PipelineContext context) => _invocations.GetOrCreateValue(context);

    private bool IsFinalAnswerMode(PipelineContext context) =>
        _invocations.TryGetValue(context, out var state) && state.FinalAnswerMode;

    public CustomStatelessTemplateLlmStep(
        string name,
        LlmRole role,
        ITemplateProvider templateProvider,
        string promptTemplateName,
        string? systemPromptTemplateName = null,
        ReportType? reportType = null,
        string? outputFileName = null,
        IList<string>? tools = null)
        : base(name, role, templateProvider, tools)
    {
        _role = role;
        _templateProvider = templateProvider;
        Prompt = promptTemplateName;
        SystemPrompt = systemPromptTemplateName;
        AssociatedReportType = reportType;
        OutputFileName = outputFileName;
    }

    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        var targetInfo = !string.IsNullOrEmpty(OutputFileName) ? $" '{OutputFileName}'" : $" ({Name})";
        context.Report(Name, AnalysisLogLevel.Info, IsFinalAnswerMode(context)
            ? $"🤖 [LLM] Redactando{targetInfo} sin más herramientas..."
            : $"🤖 [LLM] Iniciando auditoría{targetInfo}...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await base.ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);

        sw.Stop();

        // Bucle de herramientas cortado (el detector de repeticiones, o la retención reiterada del protocolo de notas): lo que
        // queda es el turno cortado, no un informe (con notas, solo sus marcadores). Con respuesta final configurada se lleva al
        // mismo camino que el tope de iteraciones; sin ella, el corte se queda como respuesta, como en el resto del framework.
        if (result is LlmStepResult { ToolLoopStopMessage: { } stopMessage } && ToolLimitFinalAnswerPrompt != null && !IsFinalAnswerMode(context))
            result = CreateErrorResult(stopMessage, new ToolLoopCutException(Name, stopMessage));

        if (CanAnswerWithoutTools(result, context))
        {
            // No es un fallo del informe: SwitchToNextFallback lo convierte en un último intento sin tools.
            var why = result.Error?.OriginalException is ToolLoopCutException ? "cortó su bucle de herramientas" : "agotó su tope de herramientas";
            context.Report(Name, AnalysisLogLevel.Warning,
                $"⚠️ [LLM]{targetInfo} {why} tras {sw.Elapsed.TotalSeconds:F1}s: se redacta con lo consultado.");
            return result;
        }

        // Un resultado con error no es un informe: antes se escribía el texto del HTTP 400 como
        // si fuera 06_arquitectura_acoplamiento.md.
        if (result.HasError)
        {
            context.Report(Name, AnalysisLogLevel.Error, $"❌ [LLM] Falló{targetInfo} tras {sw.Elapsed.TotalSeconds:F1}s: {result.Error?.Message}");
            return result;
        }

        context.Report(Name, AnalysisLogLevel.Success, $"✅ [LLM] Completado{targetInfo} en {sw.Elapsed.TotalSeconds:F1}s.");

        var truncated = result is ILlmStepResult { FinishReason: FinishReason.Length };
        if (truncated)
        {
            context.Report(Name, AnalysisLogLevel.Warning, $"⚠️ [LLM]{targetInfo} se cortó al alcanzar MaxOutputTokens: el contenido está incompleto.");
        }

        if (result is SingleReportResult singleReport)
        {
            if (AssociatedReportType.HasValue)
            {
                singleReport.ReportType = AssociatedReportType.Value;
            }
            if (!string.IsNullOrEmpty(OutputFileName))
            {
                singleReport.ReportFileName = OutputFileName;
            }

            // Los marcadores que dejan las notas del protocolo (<nota r="Rn">) no son parte del informe.
            singleReport.MarkdownContent = ReportMarkdown.RemoveFileNameHeading(ReportMarkdown.RemoveTextBeforeTitle(NoteTagHandler.RemovePlaceholders(singleReport.MarkdownContent)));
            singleReport.PopulateDerivedMetadata();
            ReconcileCriticalFindings(singleReport, context, targetInfo);
            if (!singleReport.HasAnalysisMeta)
            {
                context.Report(Name, AnalysisLogLevel.Warning, $"⚠️ [LLM]{targetInfo} no incluye la línea 'analysis-meta': su semáforo queda como {SingleReportResult.UnknownHealth}.");
            }

            // Procedencia (recomendación 12): después del aviso, porque sin línea del modelo la crea sin health.
            singleReport.MarkdownContent = ReportMarkdown.SetMetaValues(singleReport.MarkdownContent, Provenance(context, result));
            singleReport.PopulateDerivedMetadata();

            // Único punto de escritura de los informes individuales: se guardan al terminar cada
            // auditor, así que sobreviven aunque la síntesis ejecutiva falle después.
            var pipelineInput = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput);
            if (pipelineInput != null &&
                !string.IsNullOrWhiteSpace(pipelineInput.OutputReportsPath) &&
                !string.IsNullOrWhiteSpace(singleReport.ReportFileName))
            {
                var filePath = Path.Combine(pipelineInput.OutputReportsPath, singleReport.ReportFileName);
                try
                {
                    Directory.CreateDirectory(pipelineInput.OutputReportsPath);
                    await File.WriteAllTextAsync(filePath, singleReport.MarkdownContent, cancellationToken);
                    context.ReportWritten(Name, filePath, AssociatedReportType, singleReport.QualityHealth, singleReport.CriticalFindings, truncated);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    context.Report(Name, AnalysisLogLevel.Warning, $"⚠️ [LLM] No se pudo guardar '{filePath}': {ex.Message}");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Pares de procedencia de <see cref="ReportProvenance"/> para el informe que escribe este paso (o la síntesis con este
    /// paso): el modelo y el servicio del rol, el modelo que respondió según <paramref name="result"/>, los SHA de sus dos
    /// plantillas y el grafo de <see cref="AnalysisContextKeys.GraphProvenance"/>.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Provenance(PipelineContext context, IStepResult? result)
    {
        if (!_shasComputed)
        {
            // Las plantillas no cambian durante la vida del paso: el proveedor las cachea.
            _systemSha = ReportProvenance.TemplateSha(_templateProvider, SystemPrompt);
            _promptSha = ReportProvenance.TemplateSha(_templateProvider, Prompt);
            _shasComputed = true;
        }

        return ReportProvenance.Build(
            DateTimeOffset.UtcNow,
            _role.Model,
            _role.Provider.Service?.Name,
            (result as ILlmStepResult)?.Model,
            _systemSha,
            _promptSha,
            context.Get<GraphProvenance>(AnalysisContextKeys.GraphProvenance));
    }

    /// <summary>
    /// Tope de tools agotado, o bucle de tools cortado, por primera vez en esta invocación, con un mensaje de respuesta final
    /// configurado.
    /// </summary>
    private bool CanAnswerWithoutTools(IStepResult result, PipelineContext context) =>
        ToolLimitFinalAnswerPrompt != null
        && _invocations.TryGetValue(context, out var state)
        && !state.FinalAnswerMode
        && state.Conversation != null
        && result.Error?.OriginalException is ToolIterationLimitExceededException or ToolLoopCutException;

    protected override bool SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt)
    {
        if (CanAnswerWithoutTools(error, context))
        {
            // Sin llamar a la base: no se restaura el bookmark y la conversación conserva la evidencia.
            State(context).FinalAnswerMode = true;
            return true;
        }

        // Ya en el intento sin tools, cambiar de proveedor le pasaría el tráfico de tools de otro motor:
        // el step falla como fallaría sin esta estrategia.
        return !IsFinalAnswerMode(context) && base.SwitchToNextFallback(error, context, attempt);
    }

    protected override ConversationContext GetConversationContext(PipelineContext context)
    {
        var state = State(context);
        var conversation = state.FinalAnswerMode && state.Conversation != null
            ? state.Conversation
            : state.Conversation = base.GetConversationContext(context);

        // Con el protocolo de notas, la sesión de este contexto apunta a esta conversación: las tools envueltas en
        // NoteProtocolTool marcan sus resultados y NoteTagHandler los sustituye por sus notas. En el intento de respuesta
        // final la conversación es la misma y la sesión se conserva.
        if (UsesNoteProtocol)
            NoteSessions.Begin(context, conversation, Name, NoteOptions);

        return conversation;
    }

    /// <summary>Umbrales de pendientes del protocolo de notas (<c>CodebaseAnalysis:NoteProtocol</c>); null = los de por defecto.</summary>
    public NoteProtocolOptions? NoteOptions { get; init; }

    /// <summary>Si el paso lleva el protocolo de notas (<see cref="NoteTagHandler"/> entre sus <c>TagHandlers</c>).</summary>
    public bool UsesNoteProtocol => TagHandlers.Any(h => h is NoteTagHandler);

    protected override Task<string?> BuildUserMessageAsync(IStepResult input, PipelineContext context) =>
        IsFinalAnswerMode(context)
            ? Task.FromResult(ToolLimitFinalAnswerPrompt)
            : base.BuildUserMessageAsync(input, context);

    protected override LlmRequest ConfigureLlmRequest(LlmRequest request, PipelineContext context)
    {
        var configured = base.ConfigureLlmRequest(request, context);
        if (!IsFinalAnswerMode(context) || configured.Tools is not { Count: > 0 })
            return configured;

        // Las tools se quedan y se prohíbe usarlas: sus definiciones son parte del prefijo cacheado, y quitarlas hacía que la
        // llamada final pagara todo el historial sin caché. El with copia Options (copia profunda) antes de añadir la clave,
        // así que el JObject de la petición original no cambia.
        return configured with { Options = new JObject { [FinalAnswerRequests.ToolChoiceOption] = FinalAnswerRequests.ToolChoiceNone } };
    }

    protected override async Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        await base.FinalizeAsync(result, context, cancellationToken);
        _invocations.Remove(context);
        CloseNoteSession(context);
    }

    /// <summary>
    /// Guardarraíl del protocolo de notas: avisa (nunca falla) de los resultados que se quedaron sin nota, que siguen enteros
    /// en la conversación, y deja de marcar resultados en este contexto. Las notas siguen legibles para quien las recoja.
    /// </summary>
    private void CloseNoteSession(PipelineContext context)
    {
        if (!UsesNoteProtocol || !NoteSessions.TryGet(context, out var session) || !session.IsActive)
            return;

        session.Deactivate();
        var unannotated = session.Unannotated;
        if (unannotated.Count == 0)
            return;

        var targetInfo = !string.IsNullOrEmpty(OutputFileName) ? $" '{OutputFileName}'" : $" ({Name})";
        Logger.LogWarning("Step {StepName}: {Count} of {Total} tool results got no note ({Ids})",
            Name, unannotated.Count, session.EmittedIds.Count, string.Join(", ", unannotated));
        context.Report(Name, AnalysisLogLevel.Warning,
            $"⚠️ [LLM]{targetInfo} dejó {unannotated.Count} de {session.EmittedIds.Count} resultado(s) de herramientas sin nota ({string.Join(", ", unannotated)}): se quedaron enteros en la conversación.");
    }

    /// <summary>
    /// El recuento de P0/P1 de la línea <c>analysis-meta</c> lo escribe el modelo y no siempre cuadra con su
    /// propia matriz. Si el informe tiene tabla de severidad, manda la tabla: se corrige la línea y se avisa.
    /// Solo en los informes que declaran <c>critical</c> o en el de errores, que es el que debe declararlo.
    /// </summary>
    private void ReconcileCriticalFindings(SingleReportResult report, PipelineContext context, string targetInfo)
    {
        var declared = report.CriticalFindings;
        if (declared == null && report.ReportType != ReportType.ErroresYBugs)
            return;

        if (ReportMarkdown.CountCriticalFindings(report.MarkdownContent) is not int computed || computed == declared)
            return;

        context.Report(Name, AnalysisLogLevel.Warning,
            $"⚠️ [LLM]{targetInfo} declara critical={declared?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(sin declarar)"}, pero su matriz de severidad tiene {computed} hallazgo(s) P0/P1: se guarda critical={computed}.");
        report.MarkdownContent = ReportMarkdown.SetCriticalInMeta(report.MarkdownContent, computed);
        report.PopulateDerivedMetadata();
    }

    public Task<IStepResult> ExecuteStepAsync(
        TIn input,
        PipelineContext context,
        int attempt,
        CancellationToken cancellationToken)
    {
        return ((IStep)this).ExecuteAsync(input, context, attempt, null, cancellationToken);
    }
}

/// <summary>
/// El framework cortó el bucle de herramientas de un paso con respuesta final configurada (ver
/// <see cref="LlmStepResult.ToolLoopStopMessage"/>): como <see cref="ToolIterationLimitExceededException"/>, lleva al último
/// intento sin herramientas en lugar de guardar el turno cortado como resultado.
/// </summary>
public sealed class ToolLoopCutException(string stepName, string reason)
    : InvalidOperationException($"Tool loop of '{stepName}' cut: {reason}")
{
    public string StepName { get; } = stepName;
}

/// <summary>Cómo pide <see cref="CustomStatelessTemplateLlmStep{TIn, TOut}"/> la respuesta final sin herramientas.</summary>
public static class FinalAnswerRequests
{
    /// <summary>Opción de la petición (va tal cual al cuerpo en los servicios OpenAI-compatibles).</summary>
    public const string ToolChoiceOption = "tool_choice";

    public const string ToolChoiceNone = "none";

    /// <summary>Si la petición prohíbe las tools (<c>tool_choice: "none"</c>): el intento de respuesta final.</summary>
    public static bool ForbidsTools(LlmRequest request) =>
        request.Options.GetValue(ToolChoiceOption, StringComparison.OrdinalIgnoreCase) is JValue { Type: JTokenType.String } value
        && string.Equals((string?)value, ToolChoiceNone, StringComparison.OrdinalIgnoreCase);
}
