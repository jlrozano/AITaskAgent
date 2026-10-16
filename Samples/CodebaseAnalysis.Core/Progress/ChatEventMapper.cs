using AITaskAgent.LLM.Constants;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;

namespace CodebaseAnalysis.Core.Progress;

/// <summary>
/// Un evento del SSE del chat (plan §4.4): <c>type</c> y sus campos, que viajan en la raíz del JSON
/// (<c>{"type":"delta","text":"…"}</c>).
/// </summary>
public sealed record ChatStreamEvent(string Type, IReadOnlyDictionary<string, object?> Fields)
{
    /// <summary>El objeto que se serializa: <c>type</c> primero y después los campos.</summary>
    public Dictionary<string, object?> ToPayload()
    {
        var payload = new Dictionary<string, object?>(Fields.Count + 1) { ["type"] = Type };
        foreach (var (key, value) in Fields)
            payload[key] = value;
        return payload;
    }
}

/// <summary>Valores de <see cref="ChatStreamEvent.Type"/>.</summary>
public static class ChatStreamEventTypes
{
    public const string TurnStarted = "turn.started";
    public const string Delta = "delta";
    public const string Thinking = "thinking";
    public const string ToolCalls = "tool.calls";
    public const string ToolStarted = "tool.started";
    public const string ToolCompleted = "tool.completed";
    public const string ReportChanged = "report.changed";
    public const string TurnCompleted = "turn.completed";
    public const string TurnFailed = "turn.failed";
}

/// <summary>
/// Traduce los eventos del framework de un turno de chat a <see cref="ChatStreamEvent"/>. Sin estado.
/// </summary>
/// <remarks>
/// En streaming, cada delta sale como <c>delta</c> o <c>thinking</c> y el evento final de la iteración
/// llega sin contenido. Sin streaming, el evento final trae el contenido completo y sale como un único
/// <c>delta</c>. Así la UI es la misma en los dos modos. Solo cuenta la elección principal (índice 0).
/// </remarks>
public static class ChatEventMapper
{
    public static IReadOnlyList<ChatStreamEvent> Map(IProgressEvent evt)
    {
        switch (evt)
        {
            case LlmResponseEvent lr when lr.ChoiceIndex != 0:
                return [];

            case LlmResponseEvent lr when lr.FinishReason == FinishReason.Streaming:
                return string.IsNullOrEmpty(lr.Content)
                    ? []
                    : [new ChatStreamEvent(lr.IsThinking ? ChatStreamEventTypes.Thinking : ChatStreamEventTypes.Delta,
                        new Dictionary<string, object?> { ["text"] = lr.Content })];

            case LlmResponseEvent lr:
            {
                var events = new List<ChatStreamEvent>(2);
                if (!string.IsNullOrEmpty(lr.Content))
                {
                    events.Add(new ChatStreamEvent(ChatStreamEventTypes.Delta,
                        new Dictionary<string, object?> { ["text"] = lr.Content }));
                }

                if (lr.ToolCalls is { Count: > 0 } calls)
                {
                    events.Add(new ChatStreamEvent(ChatStreamEventTypes.ToolCalls, new Dictionary<string, object?>
                    {
                        ["calls"] = calls.Select(c => new Dictionary<string, object?>
                        {
                            ["id"] = c.Id,
                            ["name"] = c.Name,
                            ["arguments"] = c.Arguments
                        }).ToList()
                    }));
                }

                return events;
            }

            case ToolStartedEvent ts:
                return [new ChatStreamEvent(ChatStreamEventTypes.ToolStarted,
                    new Dictionary<string, object?> { ["name"] = ts.ToolName })];

            case ToolCompletedEvent tc:
            {
                var fields = new Dictionary<string, object?>
                {
                    ["name"] = tc.ToolName,
                    ["success"] = tc.Success,
                    ["durationMs"] = (long)Math.Round(tc.Duration.TotalMilliseconds),
                    ["error"] = tc.ErrorMessage
                };
                // La salida, truncada, para el detalle de la llamada en vivo; los argumentos ya llegaron enteros en tool.calls.
                ToolEventPayload.AddTo(fields, tc.AdditionalData, includeOutput: true);
                fields.Remove("arguments");
                fields.Remove("argumentsTruncated");
                return [new ChatStreamEvent(ChatStreamEventTypes.ToolCompleted, fields)];
            }

            case StepProgressEvent { EventType: AnalysisEventTypes.ChatReportChanged } sp when sp.Metadata != null:
                return [new ChatStreamEvent(ChatStreamEventTypes.ReportChanged, new Dictionary<string, object?>(sp.Metadata))];

            default:
                return [];
        }
    }
}
