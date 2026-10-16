using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Tools.Implementation;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CodebaseAnalysis.Core.Chat;

/// <summary>
/// Servicios opcionales de V2 que el chat pasa a sus tools por el contexto
/// (<see cref="AnalysisContextKeys.ChatKnowledgeServices"/>): describir imágenes y búsqueda semántica.
/// </summary>
public sealed class ChatKnowledgeServices
{
    /// <summary>Describe imágenes (<c>describe_image</c>); null si no hay rol con visión.</summary>
    public Documents.ImageDescriber? ImageDescriber { get; init; }

    /// <summary>Embeddings (<c>semantic_search</c>); null o desactivado si no hay proveedor con clave.</summary>
    public Knowledge.Embeddings.EmbeddingsRuntime? Embeddings { get; init; }
}

/// <summary>Límites del chat (<c>CodebaseAnalysis:Chat</c>).</summary>
public sealed record ChatOptions
{
    public const string SectionName = "CodebaseAnalysis:Chat";

    public int MaxToolIterations { get; init; } = 12;
    public int MaxHistoryTurns { get; init; } = 20;

    /// <summary>
    /// Rutas de solo lectura que el chat puede recorrer en todos los repositorios (<c>AllowedReadRoots</c>), además de
    /// las que añade el usuario por repositorio. Se validan igual que las suyas; las que no valen se descartan con un aviso.
    /// </summary>
    public IReadOnlyList<string> AllowedReadRoots { get; init; } = [];

    /// <summary>Admite rutas de red UNC como rutas de lectura (<c>AllowUncPaths</c>); por defecto no.</summary>
    public bool AllowUncPaths { get; init; }

    /// <summary>
    /// Límite de un turno (<c>TurnTimeoutMinutes</c>, 5 por defecto; 0 o negativo lo quita y queda
    /// <see cref="Timeout.InfiniteTimeSpan"/>). Lo aplica <see cref="ChatAgent"/> al turno entero, que es un solo paso.
    /// </summary>
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public static ChatOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new ChatOptions();
        return new ChatOptions
        {
            MaxToolIterations = section.GetValue<int?>("MaxToolIterations") ?? defaults.MaxToolIterations,
            MaxHistoryTurns = section.GetValue<int?>("MaxHistoryTurns") ?? defaults.MaxHistoryTurns,
            TurnTimeout = section.GetValue<double?>("TurnTimeoutMinutes") is double minutes
                ? minutes > 0 && minutes <= Hosting.AnalysisTimeouts.MaxMinutes ? TimeSpan.FromMinutes(minutes) : Timeout.InfiniteTimeSpan
                : defaults.TurnTimeout,
            AllowedReadRoots = [.. section.GetSection(nameof(AllowedReadRoots)).GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v))!],
            AllowUncPaths = section.GetValue<bool?>(nameof(AllowUncPaths)) ?? defaults.AllowUncPaths
        };
    }
}

/// <summary>
/// Un turno del chat sobre el repositorio analizado: un <see cref="BaseLlmStep{TIn,TOut}"/> con prompt
/// literal (el texto del usuario no pasa por el motor de plantillas), las tools del grafo, del código y
/// de los informes, y la conversación reconstruida con los turnos completados.
/// </summary>
public sealed class ChatAgent(
    IServiceProvider serviceProvider,
    LlmRole role,
    ITemplateProvider templateProvider,
    ChatOptions options,
    ILogger? logger = null,
    TimeProvider? clock = null,
    ChatKnowledgeServices? knowledgeServices = null,
    string? modelId = null)
{
    /// <summary>Rol LLM del chat en <c>AITaskAgent:LlmProviders:Roles</c>.</summary>
    public const string RoleName = "ChatAssistant";

    /// <summary>Nombre del step del turno: es el <c>StepName</c> de sus eventos.</summary>
    public const string StepName = "ChatAssistant";

    public const string PipelineName = "CodebaseChat";

    /// <summary>Error del turno que supera <paramref name="limit"/>: dice el límite y la clave que lo controla.</summary>
    public static string TurnTimeoutMessage(TimeSpan limit) =>
        $"El turno superó el límite de {limit.TotalMinutes.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} min " +
        $"({ChatOptions.SectionName}:TurnTimeoutMinutes); súbelo o pon 0 para quitarlo.";

    public static IReadOnlyList<string> ToolNames { get; } =
    [
        "graph_search", GraphNeighborsTool.ToolName, "graph_domain", "view_file", "view_file_outline", "view_code_item", "list_dir", "find_by_name", "grep_search",
        ListReportsTool.ToolName, ReadReportTool.ToolName,
        CreateReportTool.ToolName, UpdateReportTool.ToolName,
        ListDocumentsTool.ToolName, ReadDocumentTool.ToolName, DescribeImageTool.ToolName, SemanticSearchTool.ToolName
    ];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// El rol del chat de <paramref name="roles"/>, tal como lo declara la configuración (el del sample hace streaming). Hay
    /// que pedirlo en <c>requiredRoles</c> a <see cref="Hosting.LlmRoleFactory.BuildRoles"/>, que falla si no está declarado.
    /// </summary>
    public static LlmRole EnsureRole(Dictionary<string, LlmRole> roles) =>
        roles.TryGetValue(RoleName, out var role)
            ? role
            : throw new InvalidOperationException($"No hay rol '{RoleName}': pídelo en requiredRoles al construir los roles.");

    /// <summary>
    /// Ejecuta un turno, lo añade a la sesión y la guarda. Cada evento sale por <paramref name="onEvent"/>
    /// (síncrono y rápido: se llama desde el suscriptor del canal): <c>turn.started</c>, los del modelo y
    /// las tools, y al final <c>turn.completed</c> o <c>turn.failed</c>.
    /// </summary>
    /// <remarks>
    /// El turno se persiste también si falla o se cancela, con el texto parcial como respuesta, pero entonces no
    /// entra en el contexto de los siguientes.
    /// </remarks>
    public async Task<ChatTurn> RunTurnAsync(
        ChatSessionStore store,
        ChatSession session,
        string question,
        ChatWorkspace workspace,
        Action<ChatStreamEvent> onEvent,
        CancellationToken cancellationToken)
    {
        var turn = new ChatTurn
        {
            Id = $"t-{session.Turns.Count + 1:D4}",
            AskedAt = _clock.GetUtcNow(),
            Question = question,
            ModelId = modelId
        };

        onEvent(new ChatStreamEvent(ChatStreamEventTypes.TurnStarted, new Dictionary<string, object?>
        {
            ["turnId"] = turn.Id,
            ["askedAt"] = turn.AskedAt
        }));

        var stopwatch = Stopwatch.StartNew();
        var tracker = new ToolCallTracker();

        // Texto visible de la respuesta en curso: los deltas desde el último tool.calls, igual que el
        // segmento abierto que muestra el cliente. Es la respuesta que se guarda si el turno no termina.
        var partialAnswer = new System.Text.StringBuilder();

        var conversation = new ConversationContext(maxTokens: 1_000_000);
        foreach (var (q, a) in ChatSessionStore.BuildHistory(session, options.MaxHistoryTurns))
        {
            conversation.AddUserMessage(q);
            conversation.AddAssistantMessage(a);
        }
        // Lo que se añada a partir de aquí es de este turno: de sus resultados de tools sale la auditoría.
        var turnStart = conversation.History.Messages.Count;
        AuditedChatStep? step = null;

        // Canal propio del turno: el pipeline lo cierra al terminar y espera a que el suscriptor vacíe su
        // buffer, así que turn.completed sale siempre detrás del último delta.
        // report.changed lo emite la tool mientras se ejecuta, pero el contrato lo pone detrás de su tool.completed
        // (plan v2, decisión 0-9): se retiene hasta entonces. Cada uno queda también como artefacto del turno.
        var artifacts = new List<ChatTurnArtifact>();
        var pendingReportChanges = new List<ChatStreamEvent>();

        var channel = new EventChannel(LogLevel.None);
        using var subscription = channel.Subscribe((evt, _) =>
        {
            tracker.Observe(evt);
            foreach (var mapped in ChatEventMapper.Map(evt))
            {
                if (mapped.Type == ChatStreamEventTypes.Delta && mapped.Fields.TryGetValue("text", out var text))
                    partialAnswer.Append(text as string);
                else if (mapped.Type == ChatStreamEventTypes.ToolCalls)
                    partialAnswer.Clear();

                if (mapped.Type == ChatStreamEventTypes.ReportChanged)
                {
                    artifacts.Add(ChatTurnArtifact.FromEventFields(mapped.Fields));
                    pendingReportChanges.Add(mapped);
                    continue;
                }

                onEvent(mapped);

                if (mapped.Type == ChatStreamEventTypes.ToolCompleted)
                {
                    pendingReportChanges.ForEach(onEvent);
                    pendingReportChanges.Clear();
                }
            }
            return Task.CompletedTask;
        });

        // El límite del turno lo aplica el agente con su propio token, como CodebaseAnalysisPipeline con el del análisis (C6):
        // así el timeout se distingue de la cancelación del llamante por el token que ha saltado y el error dice qué límite
        // se ha superado. Al motor y al paso les llega sin límite y nunca null, que les haría aplicar sus valores por defecto
        // (DefaultStepTimeout, 1 min, cortaba el chat a los 60 s: auditoría, H-4).
        var limit = options.TurnTimeout;
        using var timeoutCts = limit != Timeout.InfiniteTimeSpan ? new CancellationTokenSource(limit) : null;
        using var linkedCts = timeoutCts != null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token) : null;
        var turnToken = linkedCts?.Token ?? cancellationToken;
        bool TimedOut() => timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested;

        IStepResult? result = null;
        Exception? failure = null;
        try
        {
            var context = new PipelineContext(channel, serviceProvider) { Conversation = conversation };
            context.Set(AnalysisContextKeys.CbmDbPath, workspace.DatabasePath);
            context.Set(AnalysisContextKeys.ReportsPath, workspace.ReportsPath);
            context.Set(AnalysisContextKeys.KnowledgePaths, workspace.Knowledge);
            context.Set(AnalysisContextKeys.ChatTurn, new ChatTurnScope(workspace.Knowledge, session.Id, turn.Id));
            if (knowledgeServices != null)
                context.Set(AnalysisContextKeys.ChatKnowledgeServices, knowledgeServices);

            var systemPrompt = await ChatPromptBuilder.BuildAsync(templateProvider, role.SystemPrompt, workspace, turnToken,
                knowledgeServices?.Embeddings?.Options);

            step = new AuditedChatStep(StepName, role, question, systemPrompt, turnStart)
            {
                Tools = [.. ReadRootsAwareTool.Annotate(AvailableTools.Instance.GetTools([.. ToolNames]), workspace.RepositoryPath, workspace.ReadRoots)],
                MaxToolIterations = options.MaxToolIterations,
                Timeout = Timeout.InfiniteTimeSpan
            };

            var pipeline = (serviceProvider.GetService(typeof(IPipeline)) as IPipeline) ?? new Pipeline();
            result = await pipeline.ExecuteAsync(PipelineName, [step], question, context, Timeout.InfiniteTimeSpan, turnToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (TimedOut())
        {
        }
        catch (Exception ex)
        {
            failure = ex;
            logger?.LogError(ex, "El turno {TurnId} de la sesión {SessionId} falló", turn.Id, session.Id);
        }
        finally
        {
            await channel.DisposeAsync();
        }

        // Un report.changed sin su tool.completed (turno cortado a mitad de la tool) sale igual antes del final.
        pendingReportChanges.ForEach(onEvent);

        stopwatch.Stop();
        turn.DurationMs = stopwatch.ElapsedMilliseconds;
        turn.ToolCalls = tracker.Calls;
        turn.Artifacts = artifacts.Count > 0 ? artifacts : null;

        if (result is ILlmStepResult llm)
        {
            turn.Model = llm.Model;
            turn.Tokens = llm.TokensUsed;
            turn.CostUsd = llm.CostUsd;
            turn.FinishReason = llm.FinishReason?.ToString();
        }

        // Un turno que no termina conserva el texto que el usuario ya vio: sin esto, al recargar la sesión
        // desaparecía. No entra en el contexto de los turnos siguientes (BuildHistory solo usa los completed).
        if (cancellationToken.IsCancellationRequested)
        {
            turn.Status = ChatTurnStatus.Cancelled;
            turn.Error = "Turno cancelado.";
            turn.Answer = PartialOrNull(partialAnswer);
        }
        else if (TimedOut() && (result == null || result.HasError))
        {
            turn.Status = ChatTurnStatus.Failed;
            turn.Error = TurnTimeoutMessage(limit);
            turn.Answer = PartialOrNull(partialAnswer);
            logger?.LogWarning("El turno {TurnId} de la sesión {SessionId} superó el límite de {Limit}", turn.Id, session.Id, limit);
        }
        else if (failure != null || result == null || result.HasError)
        {
            turn.Status = ChatTurnStatus.Failed;
            turn.Error = failure?.Message ?? result?.Error?.Message ?? "El turno no produjo resultado.";
            turn.Answer = PartialOrNull(partialAnswer);
        }
        else
        {
            turn.Status = ChatTurnStatus.Completed;
            turn.Answer = result is ILlmStepResult final ? final.AssistantMessage : result.Value?.ToString();
            turn.Audit = AuditOrNull(turn.Answer, step?.TurnMessages ?? [], workspace);
        }

        session.Turns.Add(turn);
        session.UpdatedAt = _clock.GetUtcNow();
        session.Title ??= ChatSessionStore.DefaultTitle(question);
        store.Save(session);

        onEvent(turn.Status == ChatTurnStatus.Completed
            ? new ChatStreamEvent(ChatStreamEventTypes.TurnCompleted, new Dictionary<string, object?> { ["turn"] = turn })
            : new ChatStreamEvent(ChatStreamEventTypes.TurnFailed, new Dictionary<string, object?>
            {
                ["turnId"] = turn.Id,
                ["status"] = turn.Status,
                ["error"] = turn.Error
            }));

        return turn;
    }

    /// <summary>La auditoría nunca tumba un turno: si falla, el turno se guarda sin ella y queda en el log.</summary>
    private ChatTurnAudit? AuditOrNull(string? answer, IReadOnlyList<AITaskAgent.LLM.Models.Message> turnMessages, ChatWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;
        try
        {
            var evidence = TurnEvidence.FromMessages(turnMessages, workspace.RepositoryPath);
            return ClaimAuditor.Audit(answer, evidence, workspace);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "No se pudo auditar la respuesta del turno");
            return null;
        }
    }

    /// <summary>
    /// El step del turno. Al terminar, <see cref="BaseLlmStep"/> compacta la conversación al par pregunta/respuesta y
    /// borra el tráfico de tools; antes de eso se guardan los mensajes del turno para la auditoría.
    /// </summary>
    private sealed class AuditedChatStep(string name, LlmRole role, string prompt, string? systemPrompt, int turnStart)
        : BaseLlmStep<LlmStringStepResult, LlmStringStepResult>(name, role, prompt, systemPrompt)
    {
        public IReadOnlyList<AITaskAgent.LLM.Models.Message> TurnMessages { get; private set; } = [];

        protected override Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        {
            TurnMessages = [.. context.Conversation.History.Messages.Skip(turnStart)];
            return base.FinalizeAsync(result, context, cancellationToken);
        }
    }

    private static string? PartialOrNull(System.Text.StringBuilder text) =>
        string.IsNullOrWhiteSpace(text.ToString()) ? null : text.ToString();

    /// <summary>
    /// Empareja cada tool call pedida por el modelo con su ejecución. Las tools de una iteración se
    /// ejecutan en secuencia, así que basta con emparejar <c>tool.started</c> por nombre y orden.
    /// </summary>
    private sealed class ToolCallTracker
    {
        private readonly List<ChatToolCall> _calls = [];
        private readonly List<ChatToolCall> _pending = [];
        private ChatToolCall? _running;

        public List<ChatToolCall> Calls => [.. _calls];

        public void Observe(IProgressEvent evt)
        {
            switch (evt)
            {
                case LlmResponseEvent { ChoiceIndex: 0, ToolCalls.Count: > 0 } lr
                    when lr.FinishReason != AITaskAgent.LLM.Constants.FinishReason.Streaming:
                    foreach (var call in lr.ToolCalls!)
                    {
                        var entry = new ChatToolCall
                        {
                            Name = call.Name,
                            Arguments = AnalysisEventMapper.Truncate(call.Arguments, ChatSessionStore.MaxStoredArgumentsChars)
                        };
                        _calls.Add(entry);
                        _pending.Add(entry);
                    }
                    break;

                case ToolStartedEvent ts:
                    _running = _pending.FirstOrDefault(c => c.Name == ts.ToolName);
                    if (_running != null)
                    {
                        _pending.Remove(_running);
                    }
                    else
                    {
                        _running = new ChatToolCall { Name = ts.ToolName };
                        _calls.Add(_running);
                    }
                    break;

                case ToolCompletedEvent tc when _running != null && _running.Name == tc.ToolName:
                    _running.Success = tc.Success;
                    _running.DurationMs = (long)Math.Round(tc.Duration.TotalMilliseconds);
                    _running = null;
                    break;
            }
        }
    }
}
