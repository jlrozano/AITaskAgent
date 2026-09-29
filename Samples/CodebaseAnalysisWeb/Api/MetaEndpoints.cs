using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysisWeb.Api;

public sealed record MetaReportDto(string Type, string Alias, string FileName, string Title);

/// <summary>
/// Límites de la subida (plan v2 §5.2, §7.2). <c>maxFileBytes</c> es el mayor de los máximos por familia (el límite
/// del cuerpo); cada familia tiene el suyo en <c>types[].maxBytes</c>. <c>types</c> sigue el orden de §7.2.
/// </summary>
public sealed record MetaDocumentsDto(long MaxFileBytes, long MaxTotalBytes, int MaxDocuments, IReadOnlyList<DocumentTypeInfo> Types);

/// <summary>
/// Disponibilidad de los embeddings. Con <c>enabled = false</c>, <c>model</c> y <c>dimensions</c> son null y
/// <c>disabledReason</c> explica por qué, en español, para mostrarlo tal cual; con <c>true</c>, <c>disabledReason</c> es null.
/// </summary>
public sealed record EmbeddingsFeatureDto(bool Enabled, string? Model, int? Dimensions, string? DisabledReason);

/// <summary>Disponibilidad de «Describir» y de <c>describe_image</c>; mismas reglas que <see cref="EmbeddingsFeatureDto"/>.</summary>
public sealed record ImageDescriptionFeatureDto(bool Enabled, string? Model, string? DisabledReason);

public sealed record MetaFeaturesDto(EmbeddingsFeatureDto Embeddings, ImageDescriptionFeatureDto ImageDescription);

/// <summary>Plan §4.1 más los campos de V2 (plan v2 §5.2), que salen siempre.</summary>
public sealed record MetaDto(
    IReadOnlyList<MetaReportDto> Reports,
    string ExecutiveReportFileName,
    IReadOnlyList<string> DefaultReports,
    string CustomReportsFolder,
    MetaDocumentsDto Documents,
    MetaFeaturesDto Features);

public static class MetaEndpoints
{
    /// <summary>Motivo mientras la función no está implementada (V2-C describe imágenes; V2-D, embeddings).</summary>
    public const string NotAvailableYet = "Aún no disponible en esta versión.";

    public static void MapMetaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/meta", (IConfiguration configuration, ILoggerFactory loggerFactory, Hosting.AnalysisHost host,
            CodebaseAnalysis.Core.Knowledge.Embeddings.EmbeddingsRuntime embeddings) =>
        {
            var logger = loggerFactory.CreateLogger("CodebaseAnalysisWeb.Meta");
            var defaults = AnalysisRunOptions.ResolveRequestedReports(configuration, w => logger.LogWarning("{Warning}", w));
            var documents = DocumentOptions.FromConfiguration(configuration);

            return Results.Ok(new MetaDto(
                ReportCatalog.All.Select(d => new MetaReportDto(d.Type.ToString(), d.Alias, d.FileName, d.Title)).ToList(),
                ReportCatalog.ExecutiveReportFileName,
                defaults.Select(t => t.ToString()).ToList(),
                KnowledgePaths.CustomReportsFolderName,
                new MetaDocumentsDto(DocumentTypeRegistry.MaxFileBytes, documents.MaxTotalBytes, documents.MaxDocuments, DocumentTypeRegistry.All),
                new MetaFeaturesDto(
                    embeddings.Enabled
                        ? new EmbeddingsFeatureDto(true, embeddings.Options.Model, embeddings.Options.EffectiveDimensions, null)
                        : new EmbeddingsFeatureDto(false, null, null, embeddings.DisabledReason),
                    host.ImageDescriber is { } describer
                        ? new ImageDescriptionFeatureDto(true, describer.Model, null)
                        : new ImageDescriptionFeatureDto(false, null, host.ImageDescriptionDisabledReason))));
        });
    }
}
