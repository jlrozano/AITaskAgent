using System.Text.RegularExpressions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Streaming;
using CodebaseAnalysis.Core.Progress;

namespace CodebaseAnalysis.Core.Notes;

/// <summary>
/// Etiqueta <c>&lt;nota r="Rn"&gt;…&lt;/nota&gt;</c> del protocolo de notas. Guarda la nota en la <see cref="NoteSession"/> del contexto
/// y sustituye en la conversación el resultado <c>[Rn]</c> por ella, con <c>ReplaceMessage</c>: el prompt de las vueltas
/// siguientes queda acotado y su prefijo, salvo ese mensaje, no cambia (la caché del proveedor se conserva).
/// </summary>
/// <remarks>
/// No falla nunca: una nota sin sesión, sin atributo, de un <c>Rn</c> desconocido o ya sustituido se guarda igual (si hay sesión)
/// y se avisa. Sin estado propio: una instancia sirve a todos los pasos.
/// </remarks>
public sealed partial class NoteTagHandler : ITagHandler
{
    public const string Tag = "nota";
    public const string ReferenceAttribute = "r";

    /// <summary>Una instancia compartida.</summary>
    public static NoteTagHandler Instance { get; } = new();

    public string TagName => Tag;

    /// <summary>Texto que sustituye a la etiqueta en la respuesta y en el historial.</summary>
    public static string Placeholder(string id) => $"[nota {id} guardada]";

    /// <summary>Contenido del mensaje <c>tool</c> una vez sustituido por su nota.</summary>
    public static string ReplacedContent(string id, string note) => $"[{id} leído; tu nota:] {note}";

    public string GetInstructions() =>
        """
        <nota r="Rn">…</nota> — protocolo de notas sobre los resultados de herramientas.
        Cada resultado de herramienta llega marcado con [Rn] (R1, R2...). En cada respuesta en la que recibas resultados nuevos,
        antes de pedir más herramientas, escribe una etiqueta <nota r="Rn">…</nota> por CADA resultado recibido, con todo lo que
        necesites conservar de él: hechos, reglas, valores y sus citas ruta:línea. Al escribir la nota, el contenido original del
        resultado se borra del historial y solo queda tu nota: si después te falta algo, vuelve a pedirlo. Si un resultado no
        aporta nada, escribe <nota r="Rn">sin interés</nota>. Antes de ampliar a otros ficheros, lee completo el punto de entrada
        principal de lo que investigas. Las notas deben ser densas, sin repetir lo ya anotado. Puedes escribir las notas y pedir
        herramientas en la misma respuesta; no hace falta comentar las notas después.
        Ejemplo: <nota r="R3">OrderService.cancel (src/orders/OrderService.java:120-148): solo cancela en estado PENDING; si no, lanza IllegalStateException (:131).</nota>
        """;

    public Task<string?> OnCompleteTagAsync(Dictionary<string, string> attributes, string content, PipelineContext context, CancellationToken cancellationToken)
    {
        var id = (attributes.TryGetValue(ReferenceAttribute, out var raw) ? raw : null)?.Trim() ?? "";
        var note = content.Trim();
        var placeholder = Placeholder(id.Length > 0 ? id : "sin referencia");

        if (!NoteSessions.TryGet(context, out var session))
        {
            // Un paso con la etiqueta pero sin sesión (no debería pasar): la nota se pierde, el resultado sigue entero.
            context.Report("notas", AnalysisLogLevel.Warning, $"⚠️ Nota {(id.Length > 0 ? id : "sin referencia")} recibida sin sesión de notas: no se guarda.");
            return Task.FromResult<string?>(placeholder);
        }

        if (id.Length == 0)
        {
            context.Report(session.StepName, AnalysisLogLevel.Warning, "⚠️ Nota sin atributo r: se guarda, pero no sustituye ningún resultado.");
            session.AddNote("?", note);
            return Task.FromResult<string?>(placeholder);
        }

        session.AddNote(id, note);

        if (!session.WasEmitted(id))
        {
            context.Report(session.StepName, AnalysisLogLevel.Warning, $"⚠️ Nota para {id}, que no es ningún resultado de esta investigación: se guarda sin sustituir nada.");
            return Task.FromResult<string?>(placeholder);
        }

        if (session.IsReplaced(id))
        {
            context.Report(session.StepName, AnalysisLogLevel.Warning, $"⚠️ Nota repetida para {id}: se añade a la anterior; el resultado ya estaba sustituido.");
            return Task.FromResult<string?>(placeholder);
        }

        var prefix = $"[{id}]";
        var messages = session.Conversation.History.Messages;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role == LlmConstants.MessageRoles.Tool && message.Content?.StartsWith(prefix, StringComparison.Ordinal) == true)
            {
                session.Conversation.ReplaceMessage(i, message with { Content = ReplacedContent(id, note) });
                session.MarkReplaced(id);
                return Task.FromResult<string?>(placeholder);
            }
        }

        // Emitido pero ausente de la conversación (un reintento con otra conversación, por ejemplo): se guarda igual.
        context.Report(session.StepName, AnalysisLogLevel.Warning, $"⚠️ Nota para {id}: su resultado no está en la conversación; se guarda sin sustituir nada.");
        return Task.FromResult<string?>(placeholder);
    }

    /// <summary>Quita de un texto (un informe, unas notas) los marcadores <c>[nota Rn guardada]</c> que dejó la etiqueta.</summary>
    public static string RemovePlaceholders(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("[nota ", StringComparison.Ordinal))
            return text;

        var stripped = PlaceholderPattern().Replace(text, "");
        return stripped == text ? text : BlankLinesPattern().Replace(stripped, "\n\n").Trim();
    }

    /// <summary>Si la nota no aporta nada (<c>sin interés</c>).</summary>
    public static bool IsEmptyNote(string note) =>
        string.Equals(note.Trim().TrimEnd('.').Trim(), "sin interés", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[ \t]*\[nota [^\]\r\n]{1,40} guardada\][ \t]*")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\n[ \t]*(?:\r?\n[ \t]*){2,}")]
    private static partial Regex BlankLinesPattern();
}
