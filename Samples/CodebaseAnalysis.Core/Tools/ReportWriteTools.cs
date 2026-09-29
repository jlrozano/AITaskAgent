using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

/// <summary>
/// <c>create_report { title, content, note? }</c>: crea un informe propio en <c>reports/custom/</c> con su primera
/// versión (plan v2 §6.2). Solo en el chat: necesita el <see cref="ChatTurnScope"/> del turno.
/// </summary>
public sealed class CreateReportTool : LlmTool
{
    public const string ToolName = "create_report";

    public override string Name => ToolName;
    public override string Description =>
        "Guarda un informe o estudio nuevo en Markdown (reports/custom/). Úsalo SOLO cuando el usuario pida explícitamente guardar o crear un informe o un estudio. Devuelve el fichero y la versión; el usuario puede deshacerlo desde la interfaz.";
    public override bool IsReadOnly => false;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "title": { "type": "string", "description": "Título del informe; si el contenido empieza por '# Título', manda ese." },
        "content": { "type": "string", "description": "Contenido completo en Markdown." },
        "note": { "type": "string", "description": "Opcional: nota breve para el historial de versiones." }
      },
      "required": ["title", "content"]
    }
    """);

    protected override async Task<string> InternalExecuteAsync(string argumentsJson, PipelineContext context, ILogger logger, CancellationToken cancellationToken)
    {
        var args = ReportWriteSupport.Parse(argumentsJson);
        var scope = ReportWriteSupport.RequireWriteBudget(context);
        var result = ReportWriteSupport.Run(() => new ReportWriter(scope.Paths).Create(
            args.Get("title"), args.Get("content"), args.Get("note"), scope.Origin));
        return await ReportWriteSupport.CompleteAsync(context, scope, result, logger, cancellationToken);
    }
}

/// <summary>
/// <c>update_report { file_name, mode, heading?, content, note }</c>: modifica un informe del análisis o propio,
/// guardando antes una versión <c>baseline</c> si el contenido actual no está en el historial (plan v2 §6.2).
/// </summary>
public sealed class UpdateReportTool : LlmTool
{
    public const string ToolName = "update_report";

    public override string Name => ToolName;
    public override string Description =>
        "Modifica un informe existente (del análisis o propio). Úsalo SOLO cuando el usuario pida explícitamente modificar un informe. mode: 'replace' (contenido completo), 'replace_section' (sustituye la sección de 'heading') o 'append_section' (añade una sección al final). Cada cambio queda versionado y se puede deshacer.";
    public override bool IsReadOnly => false;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "file_name": { "type": "string", "description": "Fichero del informe tal como lo da list_reports: '09_...md' o 'custom/<fichero>.md'." },
        "mode": { "type": "string", "enum": ["replace", "replace_section", "append_section"] },
        "heading": { "type": "string", "description": "Encabezado de la sección (sin '#'): obligatorio con replace_section; con append_section, título de la sección nueva si el contenido no empieza por un encabezado." },
        "content": { "type": "string", "description": "Markdown nuevo: el informe entero, la sección o la sección que se añade." },
        "note": { "type": "string", "description": "Qué se cambia, en una frase, para el historial." }
      },
      "required": ["file_name", "mode", "content", "note"]
    }
    """);

    protected override async Task<string> InternalExecuteAsync(string argumentsJson, PipelineContext context, ILogger logger, CancellationToken cancellationToken)
    {
        var args = ReportWriteSupport.Parse(argumentsJson);
        var fileName = args.Get("file_name");
        if (!ReportLibrary.TryParseId(fileName, out var source))
            throw new ArgumentException($"'{fileName}' no es un informe válido: usa el nombre que da list_reports.");

        var scope = ReportWriteSupport.RequireWriteBudget(context);
        var result = ReportWriteSupport.Run(() => new ReportWriter(scope.Paths).Update(
            source, args.Get("mode"), args.Get("heading"), args.Get("content"), args.Get("note"), scope.Origin));
        return await ReportWriteSupport.CompleteAsync(context, scope, result, logger, cancellationToken);
    }
}

internal static class ReportWriteSupport
{
    public sealed class Args(Dictionary<string, string?> values)
    {
        public string? Get(string name) => values.TryGetValue(name, out var v) ? v : null;
    }

    public static Args Parse(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
            }
            return new Args(values);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Argumentos JSON no válidos: {ex.Message}");
        }
    }

    public static ChatTurnScope RequireWriteBudget(PipelineContext context)
    {
        var scope = ChatTurnScope.From(context)
            ?? throw new InvalidOperationException("Esta tool solo está disponible en un turno de chat.");
        if (!scope.TryConsumeWrite())
        {
            throw new InvalidOperationException(
                $"Límite de {scope.MaxWrites} escrituras de informes por turno alcanzado: no se ha guardado nada más. Termina la respuesta y di al usuario qué queda pendiente.");
        }
        return scope;
    }

    public static ReportWriteResult Run(Func<ReportWriteResult> write)
    {
        try
        {
            return write();
        }
        catch (KnowledgeOperationException ex)
        {
            throw new InvalidOperationException($"[{ex.Code}] {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reingesta en el grafo si procede, emite <c>chat.report_changed</c> y devuelve la línea para el modelo. La escritura
    /// ya está hecha y versionada: si la reingesta falla (o el turno se corta en ella), el evento y el artefacto salen
    /// igual, porque sin ellos el chip de «Deshacer» no aparecería, y el fallo queda como aviso para el modelo y el log.
    /// </summary>
    public static async Task<string> CompleteAsync(PipelineContext context, ChatTurnScope scope, ReportWriteResult result, ILogger logger, CancellationToken ct)
    {
        var dbPath = context.Metadata.TryGetValue(AnalysisContextKeys.CbmDbPath, out var db) ? db as string : null;
        bool graphRefreshed = false, embeddingsOutdated = false;
        string? warning = null;
        try
        {
            (graphRefreshed, embeddingsOutdated) = await ReportKnowledgeHooks.AfterWriteAsync(scope.Paths, dbPath, result.Source, ct);
        }
        catch (Exception ex)
        {
            warning = $" Aviso: el informe se guardó, pero no se pudo actualizar en el grafo ({ex.Message}); se puede volver a añadir desde la interfaz.";
            logger.LogWarning(ex, "Informe {Id} escrito ({VersionId}) sin reingestar en el grafo", result.Id, result.VersionId);
        }
        var artifact = ChatTurnArtifact.From(result, graphRefreshed, embeddingsOutdated);

        await context.SendEventAsync(new StepProgressEvent
        {
            StepName = ChatAgent.StepName,
            EventType = AnalysisEventTypes.ChatReportChanged,
            Message = $"{artifact.Id} {artifact.Action} {artifact.VersionId}",
            CorrelationId = context.CorrelationId,
            Metadata = artifact.ToEventFields()
        }, CancellationToken.None);

        var undo = result.PreviousVersionId == null
            ? "el usuario puede borrarlo desde la interfaz"
            : $"el usuario puede deshacerlo desde la interfaz (vuelve a {result.PreviousVersionId})";
        return $"Informe {(result.Action == ReportVersionActionNames.Created ? "creado" : "modificado")}: {artifact.Id} («{artifact.Title}»), versión {artifact.VersionId}; {undo}."
               + (graphRefreshed ? " Actualizado en el grafo." : string.Empty)
               + (embeddingsOutdated ? " Sus embeddings han quedado desactualizados." : string.Empty)
               + warning;
    }
}
