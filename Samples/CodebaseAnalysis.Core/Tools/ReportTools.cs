using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

/// <summary>
/// <c>list_reports</c>: los informes del catálogo que existen en la carpeta de informes del contexto
/// (<see cref="AnalysisContextKeys.ReportsPath"/>), con semáforo, críticos y fecha.
/// </summary>
public sealed class ListReportsTool : LlmTool
{
    public const string ToolName = "list_reports";

    public override string Name => ToolName;
    public override string Description =>
        "Lista los informes de este repositorio: los del análisis (kind 'catalog') y los propios guardados desde el chat (kind 'custom', fichero 'custom/<nombre>.md'), con título, semáforo (Green/Amber/Red/Unknown), hallazgos críticos y fecha. Sin argumentos.";
    public override bool IsReadOnly => true;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    { "type": "object", "properties": {} }
    """);

    protected override Task<string> InternalExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var reportsPath = ReportToolSupport.GetReportsPath(context);
        var reports = ReportFiles.List(reportsPath);
        var custom = ReportToolSupport.GetKnowledgePaths(context) is { } paths ? ReportLibrary.ListCustom(paths) : [];
        if (reports.Count == 0 && custom.Count == 0)
        {
            return Task.FromResult("No hay informes generados todavía: hay que ejecutar el análisis.");
        }

        var rows = reports.Select(r => new
        {
            file_name = r.FileName,
            kind = "catalog",
            title = r.Title,
            report_type = r.ReportType?.ToString(),
            health = r.Health,
            critical_findings = r.CriticalFindings,
            size_bytes = r.SizeBytes,
            last_modified = r.LastModified.ToString("o")
        }).Concat(custom.Select(r => new
        {
            file_name = ReportLibrary.CustomIdPrefix + r.FileName,
            kind = "custom",
            title = r.Title,
            report_type = (string?)null,
            health = r.Health,
            critical_findings = r.CriticalFindings,
            size_bytes = r.SizeBytes,
            last_modified = r.LastModified.ToString("o")
        }));

        return Task.FromResult(JsonSerializer.Serialize(rows, ReportToolSupport.JsonOptions));
    }
}

/// <summary>
/// <c>read_report { file_name, heading? }</c>: el informe entero o, con <c>heading</c>, solo esa sección
/// hasta el siguiente encabezado de igual o mayor nivel. Rechaza nombres fuera del catálogo y trunca a
/// <see cref="MaxChars"/> caracteres avisando.
/// </summary>
public sealed class ReadReportTool : LlmTool
{
    public const string ToolName = "read_report";

    /// <summary>Tope de caracteres devueltos al modelo.</summary>
    public const int MaxChars = 60_000;

    public override string Name => ToolName;
    public override string Description =>
        "Lee un informe por su nombre de fichero (de list_reports): los del análisis ('05_errores_y_bugs.md') y los propios ('custom/<fichero>.md'). Con 'heading' devuelve solo esa sección. Úsalo para citar o contrastar lo que dice un informe.";
    public override bool IsReadOnly => true;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "file_name": {
          "type": "string",
          "description": "Nombre del fichero del informe, p. ej. '05_errores_y_bugs.md', '00_resumen_ejecutivo.md' o 'custom/estudio-de-colas.md'."
        },
        "heading": {
          "type": "string",
          "description": "Opcional: texto del encabezado de la sección a leer (sin los '#')."
        }
      },
      "required": ["file_name"]
    }
    """);

    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? fileName = null;
        string? heading = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (doc.RootElement.TryGetProperty("file_name", out var f) && f.ValueKind == JsonValueKind.String)
                fileName = f.GetString();
            if (doc.RootElement.TryGetProperty("heading", out var h) && h.ValueKind == JsonValueKind.String)
                heading = h.GetString();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Argumentos JSON no válidos: {ex.Message}");
        }

        if (!ReportLibrary.TryParseId(fileName, out var source))
        {
            throw new ArgumentException(
                $"'{fileName}' no es un informe del catálogo ni un propio. Del catálogo: {string.Join(", ", ReportFiles.CatalogFileNames)}; propios: 'custom/<fichero>.md' (de list_reports).");
        }

        var reportsPath = ReportToolSupport.GetReportsPath(context);
        var paths = ReportToolSupport.GetKnowledgePaths(context) ?? new KnowledgePaths(Path.GetDirectoryName(reportsPath) ?? reportsPath, reportsPath);
        var fullPath = ReportLibrary.GetFilePath(paths, source);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(source.Kind == SourceKind.Custom
                ? $"El informe propio '{fileName}' no existe."
                : $"El informe '{fileName}' no existe todavía: hay que ejecutar el análisis.");
        }

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);

        if (!string.IsNullOrWhiteSpace(heading))
        {
            var section = ExtractSection(content, heading);
            if (section == null)
            {
                var headings = ListHeadings(content);
                return $"No se encontró la sección '{heading}' en {fileName}. Encabezados disponibles:\n{string.Join("\n", headings)}";
            }

            content = section;
        }

        return Truncate(content);
    }

    /// <summary>
    /// Sección cuyo encabezado coincide con <paramref name="heading"/> (sin '#', sin distinguir mayúsculas;
    /// primero coincidencia exacta y, si no hay, la primera que lo contenga), hasta el siguiente encabezado
    /// de igual o mayor nivel. Null si no hay ninguna.
    /// </summary>
    internal static string? ExtractSection(string markdown, string heading)
    {
        var lines = SplitLines(markdown);
        return MarkdownHeadings.FindSection(lines, heading) is { } range
            ? string.Join(NL, lines[range.Start..range.End]).TrimEnd()
            : null;
    }

    private static IEnumerable<string> ListHeadings(string markdown) => MarkdownHeadings.List(SplitLines(markdown));

    private const string NL = "\n";

    private static string[] SplitLines(string markdown) => markdown.Replace("\r\n", NL).Split('\n');

    internal static string Truncate(string content)
    {
        if (content.Length <= MaxChars)
            return content;

        var sb = new StringBuilder(MaxChars + 200);
        sb.Append(content, 0, MaxChars);
        sb.Append($"\n\n[… informe truncado: se muestran {MaxChars} de {content.Length} caracteres. Pide una sección concreta con 'heading'.]");
        return sb.ToString();
    }
}

internal static class ReportToolSupport
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Rutas de V2 del turno (<see cref="AnalysisContextKeys.KnowledgePaths"/>); null fuera del chat.</summary>
    public static KnowledgePaths? GetKnowledgePaths(PipelineContext context) =>
        context.Metadata.TryGetValue(AnalysisContextKeys.KnowledgePaths, out var value) ? value as KnowledgePaths : null;

    public static string GetReportsPath(PipelineContext context) =>
        context.Metadata.TryGetValue(AnalysisContextKeys.ReportsPath, out var value) && value is string path && !string.IsNullOrWhiteSpace(path)
            ? path
            : throw new InvalidOperationException($"El contexto no tiene '{AnalysisContextKeys.ReportsPath}'.");
}
