using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Support;
using System.Text;

namespace CodebaseAnalysis.Core.Reports;

/// <summary>
/// Error de una operación sobre informes o documentos con su código de la API (plan v2 §5.4): la web lo traduce a su
/// estado HTTP y la tool del chat lo devuelve al modelo como error.
/// </summary>
public sealed class KnowledgeOperationException(string code, string message, string? documentId = null) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>Solo con <c>duplicate_document</c>: el documento que ya existe.</summary>
    public string? DocumentId { get; } = documentId;
}

/// <summary>Códigos de error de Core, los mismos que <c>ApiErrorCodes</c> de la web.</summary>
public static class KnowledgeErrorCodes
{
    public const string NotFound = "not_found";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidPath = "invalid_path";
    public const string ReportTooLarge = "report_too_large";
    public const string ReportLimitReached = "report_limit_reached";
    public const string VersionNotFound = "version_not_found";
    public const string InvalidFileName = "invalid_file_name";
    public const string UnsupportedType = "unsupported_type";
    public const string ContentMismatch = "content_mismatch";
    public const string EmptyFile = "empty_file";
    public const string FileTooLarge = "file_too_large";
    public const string DuplicateDocument = "duplicate_document";
    public const string DocumentLimitReached = "document_limit_reached";
    public const string NotAnImage = "not_an_image";
    public const string ImageDescriptionUnavailable = "image_description_unavailable";
    public const string LlmError = "llm_error";
    public const string NoGraph = "no_graph";
    public const string EmbeddingsDisabled = "embeddings_disabled";
    public const string EmbeddingProviderError = "embedding_provider_error";
}

/// <summary>Modos de <c>update_report</c> (plan v2 §6.2).</summary>
public static class ReportUpdateModes
{
    public const string Replace = "replace";
    public const string ReplaceSection = "replace_section";
    public const string AppendSection = "append_section";
}

/// <summary>Resultado de una escritura: lo que llevan el evento <c>report.changed</c> y el artefacto del turno.</summary>
public sealed record ReportWriteResult(
    SourceRef Source,
    string FileName,
    string Title,
    string Action,
    string VersionId,
    string? PreviousVersionId)
{
    /// <summary>Id de la API (<c>05_x.md</c> o <c>custom/x.md</c>).</summary>
    public string Id => ReportLibrary.ToId(Source);

    /// <summary><c>catalog</c> o <c>custom</c>, como <c>ReportSummaryDto.kind</c>.</summary>
    public string Kind => Source.Kind == SourceKind.Custom ? "custom" : "catalog";
}

/// <summary>
/// Escritura versionada de informes (plan v2 §6.2, §6.4): crear propios, modificar cualquiera, restaurar y borrar.
/// Antes de escribir sobre un contenido que no está en el historial (lo regeneró el pipeline o es la primera vez) lo
/// guarda como <c>baseline</c>, así que toda escritura se puede deshacer. En los del catálogo conserva la línea
/// <c>analysis-meta</c> y recalcula <c>critical</c>.
/// </summary>
public sealed class ReportWriter(KnowledgePaths paths, TimeProvider? clock = null)
{
    private static readonly Lock WriteLock = new();

    public KnowledgePaths Paths { get; } = paths;

    public ReportHistoryStore History { get; } = new(paths, clock);

    /// <summary>
    /// Crea <c>custom/&lt;slug&gt;.md</c> con la versión <c>created</c>. El título es el primer <c>#</c> del contenido o,
    /// si no tiene, <paramref name="title"/>, que entonces se pone como <c># </c> al principio.
    /// </summary>
    public ReportWriteResult Create(string? title, string? content, string? note, ReportVersionOrigin origin)
    {
        content = Normalize(content);
        if (content.Trim().Length == 0)
            throw new KnowledgeOperationException(KnowledgeErrorCodes.InvalidRequest, "El contenido del informe está vacío.");

        var effectiveTitle = ReportLibrary.TitleOf(content) ?? title?.Trim();
        if (string.IsNullOrWhiteSpace(effectiveTitle))
            throw new KnowledgeOperationException(KnowledgeErrorCodes.InvalidRequest, "Falta el título del informe.");
        if (ReportLibrary.TitleOf(content) == null)
            content = $"# {effectiveTitle}\n\n{content.TrimStart()}";

        EnsureSize(content);
        lock (WriteLock)
        {
            if (Paths.CountCustomReports() >= ReportLibrary.MaxCustomReports)
            {
                throw new KnowledgeOperationException(KnowledgeErrorCodes.ReportLimitReached,
                    $"Se ha alcanzado el máximo de {ReportLibrary.MaxCustomReports} informes propios en este repositorio.");
            }

            var fileName = ReportLibrary.NewCustomFileName(Paths, effectiveTitle);
            var source = SourceRef.Custom(fileName);
            ReportHistoryStore.WriteAtomic(Paths.CustomReportFile(source), Encoding.UTF8.GetBytes(content));
            var version = History.Append(source, content, ReportVersionActionNames.Created, origin, note);
            return new ReportWriteResult(source, fileName, effectiveTitle, ReportVersionActionNames.Created, version.VersionId, null);
        }
    }

    /// <summary>Modifica un informe existente del catálogo o propio (plan v2 §6.2).</summary>
    public ReportWriteResult Update(SourceRef report, string? mode, string? heading, string? content, string? note, ReportVersionOrigin origin)
    {
        content = Normalize(content);
        mode = string.IsNullOrWhiteSpace(mode) ? ReportUpdateModes.Replace : mode.Trim();
        lock (WriteLock)
        {
            var path = RequireExisting(report);
            var current = File.ReadAllText(path);
            string updated = mode switch
            {
                ReportUpdateModes.Replace => content,
                ReportUpdateModes.ReplaceSection => ReplaceSection(current, heading, content),
                ReportUpdateModes.AppendSection => AppendSection(current, heading, content),
                _ => throw new KnowledgeOperationException(KnowledgeErrorCodes.InvalidRequest,
                    $"Modo '{mode}' no válido: usa replace, replace_section o append_section.")
            };

            if (updated.Trim().Length == 0)
                throw new KnowledgeOperationException(KnowledgeErrorCodes.InvalidRequest, "El contenido nuevo está vacío.");

            if (report.Kind == SourceKind.Report)
                updated = KeepAnalysisMeta(current, updated);

            EnsureSize(updated);
            if (updated == Normalize(current))
                throw new KnowledgeOperationException(KnowledgeErrorCodes.InvalidRequest, "El contenido nuevo es idéntico al actual: no hay nada que guardar.");

            var previous = EnsureCurrentInHistory(report, current);
            ReportHistoryStore.WriteAtomic(path, Encoding.UTF8.GetBytes(updated));
            var version = History.Append(report, updated, ReportVersionActionNames.Updated, origin, note);
            return new ReportWriteResult(report, report.Id, TitleOf(report, updated), ReportVersionActionNames.Updated, version.VersionId, previous.VersionId);
        }
    }

    /// <summary>Restaura una versión (también es «Deshacer»): escribe su contenido y guarda una versión <c>restored</c>.</summary>
    public ReportVersion Restore(SourceRef report, string? versionId, ReportVersionOrigin origin)
    {
        lock (WriteLock)
        {
            var path = RequireExisting(report);
            var content = History.ReadContent(report, versionId)
                ?? throw new KnowledgeOperationException(KnowledgeErrorCodes.VersionNotFound, $"No existe la versión '{versionId}' de '{report.Id}'.");

            var current = File.ReadAllText(path);
            EnsureCurrentInHistory(report, current);
            ReportHistoryStore.WriteAtomic(path, Encoding.UTF8.GetBytes(content));
            return History.Append(report, content, ReportVersionActionNames.Restored, origin, $"Restaurada {versionId}");
        }
    }

    /// <summary>Borra un informe propio guardando antes su contenido como versión <c>deleted</c>.</summary>
    public ReportVersion DeleteCustom(SourceRef report, ReportVersionOrigin origin)
    {
        if (report.Kind != SourceKind.Custom)
            throw new ArgumentException($"'{report}' no es un informe propio.", nameof(report));

        lock (WriteLock)
        {
            var path = RequireExisting(report);
            var version = History.Append(report, File.ReadAllText(path), ReportVersionActionNames.Deleted, origin, null);
            File.Delete(path);
            return version;
        }
    }

    /// <summary>
    /// True si el contenido actual es la última versión del historial y esa versión es de origen chat (plan v2 §6.4). Si
    /// el pipeline regenera el informe, deja de coincidir y la marca desaparece sola.
    /// </summary>
    public bool IsModifiedByChat(SourceRef report)
    {
        var latest = History.Latest(report);
        return latest is { Origin.Type: ReportVersionOrigin.ChatType }
               && ReportHistoryStore.FileSha256(ReportLibrary.GetFilePath(Paths, report)) == latest.Sha256;
    }

    /// <summary>Guarda el contenido actual como <c>baseline</c> si no coincide con la última versión; devuelve la que coincide.</summary>
    private ReportVersion EnsureCurrentInHistory(SourceRef report, string current)
    {
        var latest = History.Latest(report);
        return latest != null && latest.Sha256 == ReportHistoryStore.Sha256(current)
            ? latest
            : History.Append(report, current, ReportVersionActionNames.Baseline, ReportVersionOrigin.Pipeline,
                "Contenido anterior a la primera escritura desde el chat o regenerado por el análisis");
    }

    private string RequireExisting(SourceRef report)
    {
        var path = ReportLibrary.GetFilePath(Paths, report);
        if (!File.Exists(path))
            throw new KnowledgeOperationException(KnowledgeErrorCodes.NotFound, $"No existe el informe '{ReportLibrary.ToId(report)}'.");
        return path;
    }

    private string TitleOf(SourceRef report, string content) => report.Kind == SourceKind.Custom
        ? ReportLibrary.TitleOf(content) ?? Path.GetFileNameWithoutExtension(report.Id)
        : ReportLibrary.GetTitle(Paths, report);

    private static string Normalize(string? content) => (content ?? string.Empty).Replace("\r\n", "\n");

    private static void EnsureSize(string content)
    {
        if (content.Length > ReportLibrary.MaxReportChars)
        {
            throw new KnowledgeOperationException(KnowledgeErrorCodes.ReportTooLarge,
                $"El informe tendría {content.Length} caracteres; el máximo es {ReportLibrary.MaxReportChars}.");
        }
    }

    /// <summary>
    /// Sin línea <c>analysis-meta</c> en el contenido nuevo, conserva la anterior al final (si no, el semáforo pasaría a
    /// Unknown); con ella, recalcula <c>critical</c> desde las tablas, igual que tras un auditor.
    /// </summary>
    internal static string KeepAnalysisMeta(string current, string updated)
    {
        if (ReportMarkdown.GetLastMetaLine(updated) == null)
        {
            var previous = ReportMarkdown.GetLastMetaLine(current);
            return previous == null ? updated : updated.TrimEnd() + "\n\n" + previous + "\n";
        }

        return ReportMarkdown.CountCriticalFindings(updated) is int critical
            ? ReportMarkdown.SetCriticalInMeta(updated, critical)
            : updated;
    }

    /// <summary>
    /// Sustituye la sección de <paramref name="heading"/> (misma búsqueda que <c>read_report</c>). Si el contenido nuevo
    /// empieza por un encabezado, reemplaza también el encabezado; si no, se conserva el original.
    /// </summary>
    internal static string ReplaceSection(string current, string? heading, string content)
    {
        if (string.IsNullOrWhiteSpace(heading))
            throw new KnowledgeOperationException(KnowledgeErrorCodes.InvalidRequest, "replace_section necesita 'heading'.");

        var lines = Normalize(current).Split('\n');
        var range = MarkdownHeadings.FindSection(lines, heading)
            ?? throw new KnowledgeOperationException(KnowledgeErrorCodes.InvalidRequest,
                $"No se encontró la sección '{heading}'. Encabezados: {string.Join(" | ", MarkdownHeadings.List(lines))}");

        var replacement = content.Trim('\n');
        if (!MarkdownHeadings.TryParse(replacement.Split('\n')[0], out _, out _))
            replacement = lines[range.Start] + "\n\n" + replacement;

        var before = string.Join("\n", lines[..range.Start]);
        var after = string.Join("\n", lines[range.End..]);
        return (before.Length > 0 ? before + "\n" : string.Empty) + replacement + "\n" + (after.Length > 0 ? "\n" + after : string.Empty);
    }

    /// <summary>Añade una sección al final, antes de la última línea <c>analysis-meta</c> si la hay.</summary>
    internal static string AppendSection(string current, string? heading, string content)
    {
        var section = content.Trim('\n');
        if (!string.IsNullOrWhiteSpace(heading) && !MarkdownHeadings.TryParse(section.Split('\n')[0], out _, out _))
            section = $"## {heading.Trim().TrimStart('#').Trim()}\n\n{section}";

        current = Normalize(current);
        var metaIndex = ReportMarkdown.LastMetaIndex(current);
        if (metaIndex < 0)
            return current.TrimEnd() + "\n\n" + section + "\n";

        return current[..metaIndex].TrimEnd() + "\n\n" + section + "\n\n" + current[metaIndex..];
    }
}

/// <summary>Encabezados ATX fuera de bloques de código: la búsqueda de sección que comparten <c>read_report</c> y <c>update_report</c>.</summary>
public static class MarkdownHeadings
{
    /// <summary>
    /// Rango <c>[Start, End)</c> de líneas de la sección cuyo encabezado coincide con <paramref name="heading"/> (sin
    /// '#', sin distinguir mayúsculas; primero exacta y si no la primera que lo contenga), hasta el siguiente encabezado
    /// de igual o mayor nivel. Null si no hay.
    /// </summary>
    public static (int Start, int End)? FindSection(string[] lines, string heading)
    {
        var headings = Scan(lines);
        var wanted = NormalizeHeading(heading);
        var match = headings.FirstOrDefault(h => NormalizeHeading(h.Text) == wanted);
        if (match.Text == null)
            match = headings.FirstOrDefault(h => NormalizeHeading(h.Text).Contains(wanted, StringComparison.Ordinal));
        if (match.Text == null)
            return null;

        var end = headings.FirstOrDefault(h => h.Index > match.Index && h.Level <= match.Level);
        return (match.Index, end.Text == null ? lines.Length : end.Index);
    }

    public static IEnumerable<string> List(string[] lines) => Scan(lines).Select(h => new string('#', h.Level) + " " + h.Text);

    public static List<(int Index, int Level, string Text)> Scan(string[] lines)
    {
        var result = new List<(int Index, int Level, string Text)>();
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && TryParse(lines[i], out var level, out var text))
                result.Add((i, level, text));
        }

        return result;
    }

    public static bool TryParse(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        var trimmed = line.TrimStart();
        while (level < trimmed.Length && trimmed[level] == '#') level++;
        if (level is 0 or > 6 || level >= trimmed.Length || trimmed[level] != ' ')
            return false;

        text = trimmed[level..].Trim().TrimEnd('#').Trim();
        return true;
    }

    public static string NormalizeHeading(string text) => text.Trim().TrimStart('#').Trim().ToLowerInvariant();
}
