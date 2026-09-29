using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Documents;

/// <summary>Sección <c>CodebaseAnalysis:Documents</c> (plan v2 §7.6). Los máximos por familia no se configuran: <see cref="DocumentTypeRegistry"/>.</summary>
public sealed record DocumentOptions
{
    public const string SectionName = "CodebaseAnalysis:Documents";

    /// <summary>Documentos por repositorio; el siguiente es <c>document_limit_reached</c>.</summary>
    public int MaxDocuments { get; init; } = 300;

    /// <summary>Suma de los originales por repositorio, en MiB; superarla es <c>document_limit_reached</c>.</summary>
    public int MaxTotalMegabytes { get; init; } = 500;

    /// <summary>Rol del LLM que describe imágenes (§7.4).</summary>
    public string VisionRole { get; init; } = "ChatAssistant";

    /// <summary>Valor inicial de <c>useInAnalysis</c> de un documento recién subido.</summary>
    public bool IncludeInAnalysisByDefault { get; init; } = true;

    /// <summary>Caracteres totales del índice de documentación que reciben los auditores (§7.8).</summary>
    public int AnalysisIndexMaxChars { get; init; } = 8000;

    public long MaxTotalBytes => MaxTotalMegabytes * 1024L * 1024L;

    public static DocumentOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new DocumentOptions();
        return new DocumentOptions
        {
            MaxDocuments = section.GetValue<int?>(nameof(MaxDocuments)) ?? defaults.MaxDocuments,
            MaxTotalMegabytes = section.GetValue<int?>(nameof(MaxTotalMegabytes)) ?? defaults.MaxTotalMegabytes,
            VisionRole = section[nameof(VisionRole)] is { Length: > 0 } role ? role : defaults.VisionRole,
            IncludeInAnalysisByDefault = section.GetValue<bool?>(nameof(IncludeInAnalysisByDefault)) ?? defaults.IncludeInAnalysisByDefault,
            AnalysisIndexMaxChars = section.GetValue<int?>(nameof(AnalysisIndexMaxChars)) ?? defaults.AnalysisIndexMaxChars
        };
    }
}
