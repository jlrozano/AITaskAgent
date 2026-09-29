using System.Text.Json.Serialization;

namespace CodebaseAnalysisWeb.Api;

/// <summary>
/// Cuerpo de error de la API (plan §4): <c>{ "error": "&lt;código&gt;", "message": "&lt;texto&gt;" }</c>. <c>runId</c> lo
/// llevan <c>analysis_running</c> y <c>run_from_other_workspace</c>, y <c>documentId</c>, <c>duplicate_document</c> (plan v2 §5.1); ninguno sale si es null.
/// </summary>
public sealed record ApiError(
    string Error,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DocumentId = null)
{
    public IResult ToResult(int statusCode) => Results.Json(this, statusCode: statusCode);
}

/// <summary>Códigos de error de §4.1.</summary>
public static class ApiErrorCodes
{
    public const string InvalidPath = "invalid_path";
    public const string NoWorkspace = "no_workspace";
    public const string AnalysisRunning = "analysis_running";
    public const string ChatTurnRunning = "chat_turn_running";
    public const string UnknownReport = "unknown_report";
    public const string NotRunning = "not_running";
    public const string NotFound = "not_found";
    public const string EmptyMessage = "empty_message";
    public const string MessageTooLong = "message_too_long";
    public const string WorkspaceNotWritable = "workspace_not_writable";
    public const string ReportUnavailable = "report_unavailable";
    public const string InvalidRequest = "invalid_request";

    // Plan v2 §5.4. 400 salvo que se indique.
    public const string InvalidFileName = "invalid_file_name";
    public const string UnsupportedType = "unsupported_type";
    public const string ContentMismatch = "content_mismatch";
    public const string EmptyFile = "empty_file";
    public const string NotAnImage = "not_an_image";
    public const string ReportTooLarge = "report_too_large";

    /// <summary>404.</summary>
    public const string VersionNotFound = "version_not_found";

    /// <summary>413.</summary>
    public const string FileTooLarge = "file_too_large";

    // 409.
    public const string DuplicateDocument = "duplicate_document";
    public const string DocumentLimitReached = "document_limit_reached";
    public const string ReportLimitReached = "report_limit_reached";
    public const string NoGraph = "no_graph";
    public const string EmbeddingsDisabled = "embeddings_disabled";
    public const string ImageDescriptionUnavailable = "image_description_unavailable";
    public const string KnowledgeOperationRunning = "knowledge_operation_running";

    /// <summary>409 de <c>POST /api/analysis/{runId}/regenerate</c>: la ejecución es de otro repositorio que el abierto.</summary>
    public const string RunFromOtherWorkspace = "run_from_other_workspace";

    // 502.
    public const string EmbeddingProviderError = "embedding_provider_error";
    public const string LlmError = "llm_error";

    /// <summary>403, de <c>CrossSiteGuard</c>.</summary>
    public const string CrossSiteRequest = "cross_site_request";
}

public static class ApiErrorsFactory
{
    public static ApiError NoWorkspace() =>
        new(ApiErrorCodes.NoWorkspace, "No hay repositorio abierto: ábrelo con PUT /api/workspace.");

    public static ApiError AnalysisRunning(string? runId) =>
        new(ApiErrorCodes.AnalysisRunning, "Hay un análisis en curso.", runId);

    public static ApiError ChatTurnRunning() =>
        new(ApiErrorCodes.ChatTurnRunning, "Hay un turno de chat en curso.");

    public static ApiError NotFound(string what) =>
        new(ApiErrorCodes.NotFound, what);

    public static ApiError KnowledgeOperationRunning() =>
        new(ApiErrorCodes.KnowledgeOperationRunning,
            "Hay una operación de conocimiento en curso (grafo, embeddings o documentos); vuelve a intentarlo en unos segundos.");

    public static ApiError RunFromOtherWorkspace(string runId, string runRepositoryPath) =>
        new(ApiErrorCodes.RunFromOtherWorkspace,
            $"La ejecución es del repositorio '{runRepositoryPath}', no del abierto: vuelve a abrirlo o lanza un análisis de este.",
            runId);

    public static ApiError DuplicateDocument(string existingDocumentId) =>
        new(ApiErrorCodes.DuplicateDocument, "Ese fichero ya estaba subido.", DocumentId: existingDocumentId);

    /// <summary>
    /// Un error de Core (<c>KnowledgeOperationException</c>) con el estado HTTP de su código (plan v2 §5.1, §5.4).
    /// </summary>
    public static IResult FromKnowledge(CodebaseAnalysis.Core.Reports.KnowledgeOperationException ex) =>
        new ApiError(ex.Code, ex.Message, DocumentId: ex.DocumentId).ToResult(StatusFor(ex.Code));

    public static int StatusFor(string code) => code switch
    {
        ApiErrorCodes.NotFound or ApiErrorCodes.VersionNotFound => StatusCodes.Status404NotFound,
        ApiErrorCodes.FileTooLarge => StatusCodes.Status413PayloadTooLarge,
        ApiErrorCodes.DuplicateDocument or ApiErrorCodes.DocumentLimitReached or ApiErrorCodes.ReportLimitReached
            or ApiErrorCodes.NoGraph or ApiErrorCodes.EmbeddingsDisabled or ApiErrorCodes.ImageDescriptionUnavailable
            or ApiErrorCodes.KnowledgeOperationRunning or ApiErrorCodes.NoWorkspace => StatusCodes.Status409Conflict,
        ApiErrorCodes.EmbeddingProviderError or ApiErrorCodes.LlmError => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status400BadRequest
    };

    public static ApiError CrossSiteRequest() =>
        new(ApiErrorCodes.CrossSiteRequest, "Petición rechazada: llega desde otro sitio web (Sec-Fetch-Site).");
}

/// <summary>Cuerpo JSON vacío (<c>{}</c>): obliga a <c>Content-Type: application/json</c> en los POST sin datos.</summary>
public sealed record EmptyBody;
