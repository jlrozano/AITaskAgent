using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Chat;

/// <summary>
/// Lo que las tools de escritura necesitan del turno en curso (plan v2 §6.2): las rutas del repositorio, el origen de
/// las versiones (sesión y turno) y el presupuesto de escrituras. Va en <see cref="AnalysisContextKeys.ChatTurn"/>.
/// </summary>
public sealed class ChatTurnScope(KnowledgePaths paths, string sessionId, string turnId, int maxWrites = ChatTurnScope.DefaultMaxWrites)
{
    /// <summary>Escrituras de informes por turno; la siguiente devuelve un error al modelo.</summary>
    public const int DefaultMaxWrites = 5;

    private int _writes;

    public KnowledgePaths Paths { get; } = paths;
    public string SessionId { get; } = sessionId;
    public string TurnId { get; } = turnId;
    public int MaxWrites { get; } = maxWrites;

    public ReportVersionOrigin Origin => ReportVersionOrigin.Chat(SessionId, TurnId);

    /// <summary>Apunta una escritura; false si ya se agotó el presupuesto del turno.</summary>
    public bool TryConsumeWrite() => Interlocked.Increment(ref _writes) <= MaxWrites;

    public static ChatTurnScope? From(PipelineContext context) =>
        context.Metadata.TryGetValue(AnalysisContextKeys.ChatTurn, out var value) ? value as ChatTurnScope : null;
}

/// <summary>
/// Un informe escrito en el turno (plan v2 §5.3, decisión 0-9): el mismo dato que el evento <c>report.changed</c>,
/// persistido en <see cref="ChatTurn.Artifacts"/> para que el chip siga visible al recargar la sesión.
/// </summary>
public sealed record ChatTurnArtifact
{
    public const string ReportType = "report";

    public string Type { get; init; } = ReportType;
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string FileName { get; init; }
    public required string Title { get; init; }
    public required string Action { get; init; }
    public required string VersionId { get; init; }
    public string? PreviousVersionId { get; init; }
    public bool GraphRefreshed { get; init; }
    public bool EmbeddingsOutdated { get; init; }

    /// <summary>
    /// Versión que deshizo este cambio: la <c>restored</c> que devolvió el informe a <see cref="PreviousVersionId"/>, o la
    /// <c>deleted</c> si se borró el informe propio. Null (y omitido en el JSON) mientras no se deshaga. Lo pone la web al
    /// restaurar o borrar, para que el chip no vuelva a ofrecer «Deshacer» al recargar.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UndoneByVersionId { get; init; }

    /// <summary>
    /// Cómo se deshizo: <c>restored</c> (se restauró <see cref="PreviousVersionId"/>; el informe sigue existiendo) o
    /// <c>deleted</c> (se borró el informe propio; abrirlo daría 404). Va con <see cref="UndoneByVersionId"/> y, como él,
    /// se omite mientras no se deshaga. Los turnos guardados antes de este campo no lo traen.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UndoneAction { get; init; }

    /// <summary>Campos del evento <c>report.changed</c>, sin <c>type</c> (lo pone el evento).</summary>
    public Dictionary<string, object?> ToEventFields() => new()
    {
        ["id"] = Id,
        ["kind"] = Kind,
        ["fileName"] = FileName,
        ["title"] = Title,
        ["action"] = Action,
        ["versionId"] = VersionId,
        ["previousVersionId"] = PreviousVersionId,
        ["graphRefreshed"] = GraphRefreshed,
        ["embeddingsOutdated"] = EmbeddingsOutdated
    };

    public static ChatTurnArtifact FromEventFields(IReadOnlyDictionary<string, object?> fields) => new()
    {
        Id = (string)fields["id"]!,
        Kind = (string)fields["kind"]!,
        FileName = (string)fields["fileName"]!,
        Title = (string)fields["title"]!,
        Action = (string)fields["action"]!,
        VersionId = (string)fields["versionId"]!,
        PreviousVersionId = fields["previousVersionId"] as string,
        GraphRefreshed = fields["graphRefreshed"] is true,
        EmbeddingsOutdated = fields["embeddingsOutdated"] is true
    };

    public static ChatTurnArtifact From(ReportWriteResult result, bool graphRefreshed, bool embeddingsOutdated) => new()
    {
        Id = result.Id,
        Kind = result.Kind,
        FileName = result.FileName,
        Title = result.Title,
        Action = result.Action,
        VersionId = result.VersionId,
        PreviousVersionId = result.PreviousVersionId,
        GraphRefreshed = graphRefreshed,
        EmbeddingsOutdated = embeddingsOutdated
    };
}
