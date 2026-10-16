using System.Globalization;
using System.Runtime.CompilerServices;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Conversation.Context;

namespace CodebaseAnalysis.Core.Notes;

/// <summary>
/// El protocolo de notas de una invocación de un paso LLM: su conversación, los resultados de herramienta marcados con
/// <c>[Rn]</c> y las notas que el modelo ha escrito sobre ellos. La crea el paso (<see cref="NoteSessions.Begin"/>), la
/// marcan las tools envueltas en <see cref="NoteProtocolTool"/> y la rellena <see cref="NoteTagHandler"/>.
/// </summary>
/// <remarks>
/// Una invocación ejecuta sus tools y sus etiquetas en secuencia, pero el candado es barato y deja la clase a salvo de un
/// uso concurrente que no está previsto.
/// </remarks>
public sealed partial class NoteSession(ConversationContext conversation, string stepName)
{
    private readonly Lock _lock = new();
    private readonly List<string> _emitted = [];
    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);
    private readonly List<string> _noteOrder = [];
    private readonly HashSet<string> _replaced = new(StringComparer.Ordinal);
    private int _next;
    private int _withheldTurns;
    private int _lastWithheldTurn = -1;

    /// <summary>La conversación de la invocación: donde están los resultados que se sustituyen por sus notas.</summary>
    public ConversationContext Conversation { get; } = conversation;

    /// <summary>Umbrales de recordatorio y retención de pendientes; los de por defecto salvo que el paso fije otros.</summary>
    public NoteProtocolOptions Options { get; internal set; } = new();

    /// <summary>El paso que la abrió, para los avisos.</summary>
    public string StepName { get; } = stepName;

    /// <summary>
    /// Mientras el paso está en curso. Al finalizar deja de marcar resultados (otro paso en el mismo contexto no debe heredar
    /// el protocolo), pero sus notas se siguen pudiendo leer.
    /// </summary>
    public bool IsActive { get; private set; } = true;

    internal void Reactivate()
    {
        lock (_lock) IsActive = true;
    }

    internal void Deactivate()
    {
        lock (_lock) IsActive = false;
    }

    /// <summary>Reserva el siguiente identificador (<c>R1</c>, <c>R2</c>...) para un resultado de herramienta.</summary>
    public string NextResultId()
    {
        lock (_lock)
        {
            var id = "R" + (++_next).ToString(CultureInfo.InvariantCulture);
            _emitted.Add(id);
            return id;
        }
    }

    /// <summary>Los identificadores emitidos, en orden.</summary>
    public IReadOnlyList<string> EmittedIds
    {
        get
        {
            lock (_lock) return [.. _emitted];
        }
    }

    /// <summary>Guarda la nota de <paramref name="id"/>; una segunda nota del mismo resultado se añade a la primera.</summary>
    public void AddNote(string id, string text)
    {
        lock (_lock)
        {
            if (_notes.TryGetValue(id, out var existing))
            {
                _notes[id] = existing + "\n" + text;
            }
            else
            {
                _notes[id] = text;
                _noteOrder.Add(id);
            }
        }
    }

    /// <summary>
    /// Las notas en orden de <c>Rn</c>; las de un identificador que no es <c>R</c> + número van al final, en el orden en que llegaron.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Notes
    {
        get
        {
            lock (_lock)
            {
                return
                [
                    .. _noteOrder
                        .Select((id, index) => (id, index, number: Number(id)))
                        .OrderBy(n => n.number ?? int.MaxValue)
                        .ThenBy(n => n.index)
                        .Select(n => new KeyValuePair<string, string>(n.id, _notes[n.id]))
                ];
            }
        }
    }

    /// <summary>Los resultados emitidos que no tienen nota: siguen enteros en la conversación.</summary>
    public IReadOnlyList<string> Unannotated
    {
        get
        {
            lock (_lock) return [.. _emitted.Where(id => !_notes.ContainsKey(id))];
        }
    }

    /// <summary>
    /// Los resultados <c>[Rn]</c> todavía sin anotar de turnos anteriores: mensajes <c>tool</c> crudos situados antes del último
    /// mensaje <c>assistant</c>. Cuando una tool ejecuta, el mensaje del turno en curso ya está en el historial, de modo que los
    /// resultados de las llamadas hermanas de este turno (posteriores a él) no cuentan. Un resultado anotado empieza por
    /// <c>[Rn leído; tu nota:]</c> y no casa. En orden de aparición.
    /// </summary>
    public IReadOnlyList<int> PendingFromPreviousTurns()
    {
        var messages = Conversation.History.Messages;
        var lastAssistant = LastAssistantIndex();

        List<int> pending = [];
        for (var i = 0; i < lastAssistant; i++)
        {
            var message = messages[i];
            if (message.Role == AITaskAgent.LLM.Constants.LlmConstants.MessageRoles.Tool
                && RawResultPattern().Match(message.Content ?? "") is { Success: true } match
                && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                pending.Add(number);
            }
        }

        return pending;
    }

    /// <summary>Posición del último mensaje <c>assistant</c> (el del turno en curso cuando ejecuta una tool); -1 si no hay.</summary>
    private int LastAssistantIndex()
    {
        var messages = Conversation.History.Messages;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == AITaskAgent.LLM.Constants.LlmConstants.MessageRoles.Assistant)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Registra una consulta retenida en el turno en curso y devuelve cuántos turnos seguidos llevan retención: las llamadas
    /// hermanas de un mismo turno cuentan una vez.
    /// </summary>
    internal int RegisterWithheld()
    {
        var turn = LastAssistantIndex();
        lock (_lock)
        {
            if (turn != _lastWithheldTurn)
            {
                _withheldTurns++;
                _lastWithheldTurn = turn;
            }

            return _withheldTurns;
        }
    }

    /// <summary>Una consulta se ha ejecutado: la racha de turnos con retención vuelve a cero.</summary>
    internal void ResetWithheld()
    {
        lock (_lock)
        {
            _withheldTurns = 0;
            _lastWithheldTurn = -1;
        }
    }

    /// <summary>Los números como rangos: <c>R12-R14, R17</c>.</summary>
    public static string FormatRanges(IReadOnlyList<int> numbers)
    {
        var sorted = numbers.Distinct().Order().ToList();
        List<string> parts = [];
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j == i ? $"R{sorted[i]}" : $"R{sorted[i]}-R{sorted[j]}");
            i = j + 1;
        }

        return string.Join(", ", parts);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\[R(\d+)\]")]
    private static partial System.Text.RegularExpressions.Regex RawResultPattern();

    /// <summary>Si <paramref name="id"/> se emitió en esta sesión.</summary>
    public bool WasEmitted(string id)
    {
        lock (_lock) return _emitted.Contains(id, StringComparer.Ordinal);
    }

    /// <summary>Marca <paramref name="id"/> como sustituido; false si ya lo estaba.</summary>
    internal bool MarkReplaced(string id)
    {
        lock (_lock) return _replaced.Add(id);
    }

    internal bool IsReplaced(string id)
    {
        lock (_lock) return _replaced.Contains(id);
    }

    private static int? Number(string id) =>
        id.Length > 1 && id[0] == 'R' && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}

/// <summary>
/// La sesión de notas de cada <see cref="PipelineContext"/>. Por identidad del contexto, y no en <c>Metadata</c>: las ramas de
/// <see cref="PipelineContext.CloneForBranch"/> comparten <c>Metadata</c>, y cada investigación en paralelo tiene la suya. No
/// retiene el contexto: la sesión desaparece con él.
/// </summary>
public static class NoteSessions
{
    private static readonly ConditionalWeakTable<PipelineContext, NoteSession> Sessions = new();

    /// <summary>
    /// Abre la sesión del contexto para <paramref name="conversation"/>. Si ya hay una sobre la misma conversación (el intento de
    /// respuesta final reutiliza la conversación) la conserva y la reactiva; si es de otra conversación, la sustituye.
    /// </summary>
    public static NoteSession Begin(PipelineContext context, ConversationContext conversation, string stepName, NoteProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(conversation);
        if (Sessions.TryGetValue(context, out var existing) && ReferenceEquals(existing.Conversation, conversation))
        {
            existing.Reactivate();
            if (options is not null) existing.Options = options;
            return existing;
        }

        var session = new NoteSession(conversation, stepName);
        if (options is not null) session.Options = options;
        Sessions.AddOrUpdate(context, session);
        return session;
    }

    /// <summary>La sesión del contexto, activa o ya finalizada.</summary>
    public static bool TryGet(PipelineContext context, out NoteSession session)
    {
        if (context != null && Sessions.TryGetValue(context, out var found))
        {
            session = found;
            return true;
        }

        session = null!;
        return false;
    }
}
