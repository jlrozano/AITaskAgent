using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.Logging;

namespace CodebaseAnalysis.Core.Notes;

/// <summary>
/// Envuelve una tool (composición) para el protocolo de notas: si el contexto tiene una <see cref="NoteSession"/> activa, antepone
/// <c>[Rn] </c> a su resultado, también al de error, y lo registra en la sesión. Sin sesión pasa el resultado tal cual: el chat y
/// cualquier paso sin <see cref="NoteTagHandler"/> usan la misma tool registrada.
/// </summary>
public sealed class NoteProtocolTool(ITool inner) : ITool
{
    /// <summary>La tool envuelta.</summary>
    public ITool Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public string Name => Inner.Name;

    public string Description => Inner.Description;

    public string? UsageGuidelines => Inner.UsageGuidelines;

    public bool IsReadOnly => Inner.IsReadOnly;

    public ToolDefinition GetDefinition() => Inner.GetDefinition();

    public async Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!NoteSessions.TryGet(context, out var session) || !session.IsActive)
            return await Inner.ExecuteAsync(argumentsJson, context, stepName, logger, cancellationToken);

        // Pendientes de turnos anteriores: el modelo pide más sin anotar lo que ya recibió. Con muchos, el dato nuevo se retiene
        // (la tool ni se ejecuta) hasta que anote; con alguno, el resultado lleva un recordatorio. El texto pide solo anotar, no
        // repetir: la misma llamada retenida cuenta en el detector de repeticiones del framework y a la tercera cortaría el paso.
        var options = session.Options;
        var pending = session.PendingFromPreviousTurns();
        if (options.WithholdFromPending > 0 && pending.Count >= options.WithholdFromPending)
        {
            var withheld = $"[Retenido] No se ejecuta esta consulta: tienes {pending.Count} resultados de turnos anteriores sin anotar ({NoteSession.FormatRanges(pending)}). " +
                           "Escribe ahora una <nota r=\"Rn\">…</nota> por cada uno (\"sin interés\" si no aporta nada); hasta entonces no se ejecutan consultas nuevas.";
            var turns = session.RegisterWithheld();
            logger.LogWarning("Tool {ToolName} withheld in {StepName}: {Count} results pending a note ({Turns} turn(s) in a row)", Inner.Name, stepName, pending.Count, turns);

            // Un modelo que nunca anota y varía los argumentos no lo corta el detector: tras tantos turnos seguidos, el paso termina
            // por su respuesta final sin herramientas.
            if (options.StopAfterWithheldTurns > 0 && turns >= options.StopAfterWithheldTurns)
                throw new ToolLoopStopRequestedException(
                    $"{withheld} Llevas {turns} turnos seguidos pidiendo datos sin anotar los pendientes: se cierran las consultas.");

            return withheld;
        }

        session.ResetWithheld();

        string result;
        try
        {
            result = await Inner.ExecuteAsync(argumentsJson, context, stepName, logger, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // El mismo texto que pondría el framework, pero marcado: si no, el error no tendría Rn al que anotar y quedaría
            // como resultado sin nota.
            logger.LogError(ex, "Tool {ToolName} execution failed", Inner.Name);
            result = $"Error executing tool: {ex.Message}";
        }

        var tagged = $"[{session.NextResultId()}] {result}";
        return options.ReminderFromPending > 0 && pending.Count >= options.ReminderFromPending
            ? $"{tagged}\n\nPendientes de nota de turnos anteriores: {NoteSession.FormatRanges(pending)} ({pending.Count}). Anótalos con <nota r=\"Rn\">…</nota> antes de seguir."
            : tagged;
    }

    /// <summary>Envuelve <paramref name="tool"/> si no lo está ya.</summary>
    public static ITool Wrap(ITool tool) => tool as NoteProtocolTool ?? new NoteProtocolTool(tool);
}
