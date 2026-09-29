using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysisWeb.Hosting;
using CodebaseAnalysisWeb.Knowledge;
using CodebaseAnalysisWeb.Workspace;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace CodebaseAnalysisWeb.Api;

/// <summary>Documentos aportados (plan v2 §5.1, §7): subida, listado, detalle, original, texto, cambios, borrado y descripción.</summary>
public static class DocumentsEndpoints
{
    /// <summary>Cabecera con el nombre del fichero, codificado con <c>encodeURIComponent</c> (V2-D15).</summary>
    public const string FileNameHeader = "X-File-Name";

    /// <summary>Límite de cuerpo del resto de rutas nuevas con cuerpo (plan v2 §5.6).</summary>
    public const long SmallBodyLimit = 1_048_576;

    public static void MapDocumentsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/documents", async (WorkspaceState workspace, KnowledgeService knowledge, CancellationToken ct) =>
        {
            var active = workspace.Current;
            if (active == null)
                return Results.Ok(Array.Empty<DocumentSummaryDto>());

            var records = new DocumentStore(active.Knowledge).List();
            var states = await knowledge.GetStatesAsync(active, records.Select(r => SourceRef.Document(r.Id)).ToList(), ct);
            return Results.Ok(records.Select((r, i) => DocumentDtos.Summary(r, states[i])).ToList());
        });

        // Un fichero por petición, en crudo: application/octet-stream y X-File-Name fuerzan un preflight CORS que no se
        // concede (V2-D15). multipart/form-data no se acepta.
        app.MapPost("/api/documents", async (HttpContext http, WorkspaceState workspace, KnowledgeService knowledge, IConfiguration configuration, CancellationToken ct) =>
        {
            if (!IsOctetStream(http.Request.ContentType))
            {
                return new ApiError(ApiErrorCodes.InvalidRequest, "La subida tiene que ser el fichero en crudo con Content-Type: application/octet-stream.")
                    .ToResult(StatusCodes.Status415UnsupportedMediaType);
            }

            var active = workspace.Current;
            if (active == null)
                return ApiErrorsFactory.NoWorkspace().ToResult(StatusCodes.Status409Conflict);

            // Repliegue de RV8: el límite del cuerpo también en la propia petición, por si el metadato no se aplica.
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = DocumentTypeRegistry.MaxFileBytes;
            if (http.Request.ContentLength > DocumentTypeRegistry.MaxFileBytes)
            {
                return new ApiError(ApiErrorCodes.FileTooLarge, $"El fichero supera el máximo de {DocumentTypeRegistry.MaxFileBytes / (1024 * 1024)} MB.")
                    .ToResult(StatusCodes.Status413PayloadTooLarge);
            }

            var (lease, error) = knowledge.Begin(KnowledgeOperation.Upload);
            if (lease == null)
                return error!;

            using (lease)
            {
                try
                {
                    var store = new DocumentStore(active.Knowledge, DocumentOptions.FromConfiguration(configuration));
                    var record = await store.UploadAsync(http.Request.Body, http.Request.Headers[FileNameHeader].ToString(), ct);
                    return Results.Created($"/api/documents/{record.Id}",
                        DocumentDtos.Summary(record, await knowledge.GetStateAsync(active, SourceRef.Document(record.Id), ct)));
                }
                catch (KnowledgeOperationException ex)
                {
                    return ApiErrorsFactory.FromKnowledge(ex);
                }
            }
        }).WithMetadata(new RequestSizeLimitAttribute(DocumentTypeRegistry.MaxFileBytes));

        app.MapGet("/api/documents/{id}", async (string id, WorkspaceState workspace, KnowledgeService knowledge, CancellationToken ct) =>
        {
            if (!TryGet(workspace, id, out var active, out var record, out var error))
                return error;
            return Results.Ok(DocumentDtos.Detail(record, await knowledge.GetStateAsync(active, SourceRef.Document(record.Id), ct)));
        });

        app.MapGet("/api/documents/{id}/raw", (string id, HttpContext http, WorkspaceState workspace) =>
        {
            if (!TryGet(workspace, id, out var active, out var record, out var error))
                return error;

            var path = new DocumentStore(active.Knowledge).OriginalPath(record.Id);
            if (!File.Exists(path))
                return NotFound(id);

            var contentType = RawContentHeaders.Apply(http.Response, record);
            return Results.File(path, contentType);
        });

        app.MapGet("/api/documents/{id}/text", (string id, WorkspaceState workspace) =>
        {
            if (!TryGet(workspace, id, out var active, out var record, out var error))
                return error;
            return Results.Text(new DocumentStore(active.Knowledge).ReadContent(record.Id), "text/markdown; charset=utf-8");
        });

        app.MapPatch("/api/documents/{id}", async (string id, PatchDocumentRequest body, WorkspaceState workspace, KnowledgeService knowledge, CancellationToken ct) =>
        {
            if (!TryGet(workspace, id, out var active, out _, out var error))
                return error;
            try
            {
                var updated = new DocumentStore(active.Knowledge).Patch(id, body.Title, body.UseInAnalysis);
                return Results.Ok(DocumentDtos.Summary(updated, await knowledge.GetStateAsync(active, SourceRef.Document(id), ct)));
            }
            catch (KnowledgeOperationException ex)
            {
                return ApiErrorsFactory.FromKnowledge(ex);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(SmallBodyLimit));

        // Borrar un documento que está en el grafo toca el grafo: misma fila que «quitar del grafo» (§5.5). Si no está,
        // se permite con todo (fila 1), pero las dos ramas cuentan como operación de conocimiento mientras duran. La
        // carpeta se borra antes de quitar grafo y embeddings: quien indexe a la vez vuelve a comprobar dentro de
        // WriteGate que la fuente existe, así que o escribe antes de la baja (y la baja lo quita) o ya no la encuentra.
        app.MapDelete("/api/documents/{id}", async (string id, WorkspaceState workspace, KnowledgeService knowledge, CancellationToken ct) =>
        {
            if (!TryGet(workspace, id, out var active, out var record, out var error))
                return error;

            var source = SourceRef.Document(record.Id);
            var inGraph = await knowledge.IsInGraphAsync(active, source, ct);
            var (lease, busy) = knowledge.Begin(inGraph ? KnowledgeOperation.GraphWrite : KnowledgeOperation.DeleteDocument);
            if (lease == null)
                return busy!;
            using (lease)
            {
                new DocumentStore(active.Knowledge).Delete(record.Id);
                if (inGraph)
                    await knowledge.ForgetSourceAsync(active, source, ct);
                else
                    await knowledge.Embeddings.For(active.Knowledge).RemoveAsync(source, ct);
            }
            return Results.NoContent();
        });

        app.MapPost("/api/documents/{id}/describe", async (string id, EmptyBody _, WorkspaceState workspace, KnowledgeService knowledge,
            AnalysisHost host, CancellationToken ct) =>
        {
            if (!TryGet(workspace, id, out var active, out var record, out var error))
                return error;
            if (record.Family != DocumentFamilies.Image)
            {
                return new ApiError(ApiErrorCodes.NotAnImage, $"'{record.Title}' no es una imagen ({record.Family}): no se puede describir.")
                    .ToResult(StatusCodes.Status400BadRequest);
            }
            if (host.ImageDescriber is not { } describer)
            {
                return new ApiError(ApiErrorCodes.ImageDescriptionUnavailable, host.ImageDescriptionDisabledReason ?? "No se pueden describir imágenes.")
                    .ToResult(StatusCodes.Status409Conflict);
            }

            var (lease, busy) = knowledge.Begin(KnowledgeOperation.Describe);
            if (lease == null)
                return busy!;
            using (lease)
            {
                try
                {
                    var store = new DocumentStore(active.Knowledge);
                    var description = await describer.DescribeAsync(await File.ReadAllBytesAsync(store.OriginalPath(record.Id), ct), record.MediaType, null, ct);
                    var updated = store.SaveDescription(record.Id, description, describer.Model);
                    return Results.Ok(DocumentDtos.Detail(updated, await knowledge.GetStateAsync(active, SourceRef.Document(record.Id), ct)));
                }
                catch (KnowledgeOperationException ex)
                {
                    return ApiErrorsFactory.FromKnowledge(ex);
                }
            }
        }).WithMetadata(new RequestSizeLimitAttribute(SmallBodyLimit));
    }

    private static bool IsOctetStream(string? contentType) =>
        contentType != null && MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && string.Equals(parsed.MediaType.Value, "application/octet-stream", StringComparison.OrdinalIgnoreCase);

    private static bool TryGet(WorkspaceState workspace, string id, out ActiveWorkspace active, out DocumentRecord record, out IResult error)
    {
        active = workspace.Current!;
        record = null!;
        error = null!;
        if (active == null)
        {
            error = ApiErrorsFactory.NoWorkspace().ToResult(StatusCodes.Status409Conflict);
            return false;
        }

        var found = new DocumentStore(active.Knowledge).TryGet(id);
        if (found == null)
        {
            error = NotFound(id);
            return false;
        }

        record = found;
        return true;
    }

    private static IResult NotFound(string id) =>
        ApiErrorsFactory.NotFound($"No existe el documento '{id}'.").ToResult(StatusCodes.Status404NotFound);
}

/// <summary>DTOs de documentos a partir de <see cref="DocumentRecord"/>.</summary>
internal static class DocumentDtos
{
    public static DocumentSummaryDto Summary(DocumentRecord r, KnowledgeState knowledge) => new()
    {
        Id = r.Id,
        Title = r.Title,
        OriginalFileName = r.OriginalFileName,
        Extension = r.Extension,
        Family = r.Family,
        MediaType = r.MediaType,
        Viewer = r.Viewer,
        SizeBytes = r.SizeBytes,
        Sha256 = r.Sha256,
        UploadedAt = r.UploadedAt,
        UseInAnalysis = r.UseInAnalysis,
        Extraction = Extraction(r.Extraction),
        Knowledge = knowledge
    };

    public static DocumentDetailDto Detail(DocumentRecord r, KnowledgeState knowledge) => new()
    {
        Id = r.Id,
        Title = r.Title,
        OriginalFileName = r.OriginalFileName,
        Extension = r.Extension,
        Family = r.Family,
        MediaType = r.MediaType,
        Viewer = r.Viewer,
        SizeBytes = r.SizeBytes,
        Sha256 = r.Sha256,
        UploadedAt = r.UploadedAt,
        UseInAnalysis = r.UseInAnalysis,
        Extraction = Extraction(r.Extraction),
        Knowledge = knowledge,
        Sections = r.Sections.Select(s => new DocumentSectionDto(s.Ordinal, s.Heading, s.Level, s.StartLine, s.EndLine)).ToList(),
        Description = r.Description is { } d ? new DocumentDescriptionDto(d.Model, d.GeneratedAt) : null
    };

    private static DocumentExtractionDto Extraction(DocumentExtractionInfo e) =>
        new(e.Status, e.CharCount, e.SectionCount, e.PageCount, e.Truncated, e.Warnings ?? []);
}
