using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodebaseAnalysis.Core.Reports;

/// <summary>Nombres de las comprobaciones de <see cref="ReportValidator"/>, como salen en <c>_validation.json</c>.</summary>
public static class ReportValidationChecks
{
    /// <summary>Cada <c>ruta:línea</c> o <c>ruta:ini-fin</c> citada existe en el repositorio y la línea está en rango.</summary>
    public const string Citations = "citations";

    /// <summary>
    /// Todo informe de auditoría (no el resumen ejecutivo) cita código con <c>ruta:línea</c>, y cada uno de sus hallazgos al menos
    /// una vez. Solo avisa: un informe sin citas no hace fallar el análisis.
    /// </summary>
    public const string CitationsRequired = "citations_required";

    /// <summary>Las citas a informes y documentos (<c>nombre › sección</c> o el fichero de un informe) apuntan a algo que existe.</summary>
    public const string SourceReferences = "source_references";

    /// <summary>Los identificadores <c>NN-CAT-nnn</c> (o <c>CAT-nnn</c>, en informes anteriores) siguen el formato y no se repiten.</summary>
    public const string FindingIds = "finding_ids";

    /// <summary>Cada hallazgo P0 o P1 cita al menos una ubicación que existe.</summary>
    public const string SevereFindings = "severe_findings";

    /// <summary>Los nombres cualificados entre comillas invertidas existen en el grafo.</summary>
    public const string GraphNames = "graph_names";

    /// <summary>La línea <c>analysis-meta</c> existe, con semáforo y procedencia.</summary>
    public const string Metadata = "metadata";

    /// <summary>Los bloques Mermaid usan solo los tipos de <c>_mermaid_guidelines.md</c>.</summary>
    public const string Mermaid = "mermaid";

    /// <summary>
    /// Un informe en modo B (que investiga por subsistema) lleva el apartado «Cobertura» y nombra cada subsistema que el plan de
    /// cobertura del disco (<c>_coverage_plan.json</c>) eligió para él. Solo avisa, y no se comprueba sin plan en disco.
    /// </summary>
    public const string Coverage = "coverage";

    /// <summary>
    /// Una ruta de recurso (<c>/WEB-INF/jsp/x.jsp</c>) que un fichero de configuración del repositorio declara pero que no existe en él. No es una
    /// cita de código rota (el informe cuenta bien lo que dice la configuración), así que no entra en <see cref="Citations"/>; solo avisa.
    /// </summary>
    public const string DeclaredPaths = "declared_paths";
}

/// <summary>Gravedad de un problema: <c>error</c> incumple una regla de las plantillas; <c>warning</c> es sospechoso.</summary>
public static class ReportValidationLevels
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>Resultado de una comprobación en un informe.</summary>
public static class ReportCheckStatuses
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";

    /// <summary>No se pudo comprobar (los nombres del grafo sin BD).</summary>
    public const string Skipped = "skipped";

    /// <summary>No hay nada que comprobar (un informe sin hallazgos P0/P1 o sin Mermaid).</summary>
    public const string NotApplicable = "not_applicable";
}

/// <summary>Estado de una cita de código.</summary>
public static class CitationStatuses
{
    public const string Ok = "ok";
    public const string Missing = "missing";

    /// <summary>La ruta incompleta coincide con varios ficheros del repositorio.</summary>
    public const string Ambiguous = "ambiguous";
    public const string OutOfRange = "out_of_range";
}

/// <summary>Estado de una cita a un informe o documento.</summary>
public static class SourceReferenceStatuses
{
    public const string Ok = "ok";
    public const string MissingSource = "missing_source";
    public const string MissingSection = "missing_section";
}

/// <summary>Un problema de un informe, con la línea (desde 1) donde está.</summary>
public sealed record ReportValidationIssue(string Check, string Level, int Line, string Message);

/// <summary>
/// Una cita de código. <c>Target</c> es la ruta relativa al repositorio del fichero al que se resolvió (null si no se
/// resolvió); <c>BySuffix</c>, que la cita traía una ruta incompleta (<c>CGAR.bas:12</c>) y casa con un único fichero.
/// </summary>
public sealed record ReportCitation(
    string Text,
    int Line,
    string Status,
    string? Target,
    int? StartLine,
    int? EndLine,
    bool BySuffix,
    int? FileLines,
    IReadOnlyList<string>? Candidates)
{
    /// <summary>Los rangos de una cita con varios (<c>F.cs:11,16-21</c>); null en las de un solo rango. <c>StartLine</c>/<c>EndLine</c> abarcan todos.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CitationRange>? Ranges { get; init; }

    [JsonIgnore]
    public bool IsValid => Status == CitationStatuses.Ok;
}

/// <summary>Un rango de líneas (inclusive) de una cita.</summary>
public sealed record CitationRange(int Start, int End);

/// <summary>Una cita a un informe o documento: <c>nombre › sección</c> o el nombre de fichero de un informe.</summary>
public sealed record ReportSourceReference(string Text, int Line, string Status, string? Target);

/// <summary>
/// Una ruta de recurso citada sin línea que no existe en el repositorio pero que <paramref name="DeclaredIn"/> (un fichero de configuración
/// XML, ruta relativa al repositorio) declara: una «ruta declarada no existente», aparte de las citas de código rotas.
/// </summary>
public sealed record ReportDeclaredPath(string Text, int Line, string DeclaredIn);

/// <summary>Un nombre del grafo citado y si existe.</summary>
public sealed record ReportGraphName(string Text, int Line, bool Found);

/// <summary>
/// Un hallazgo del informe: su identificador, la línea donde se define (su encabezado o, si no tiene, su primera fila de
/// tabla), la prioridad (0 a 3) si la declara, y cuántas citas de código hay en su sección y en sus filas.
/// </summary>
public sealed record ReportFinding(string Id, int Line, int? Priority, int Citations, int ValidCitations)
{
    /// <summary>Texto del encabezado, la sección y las filas: lo que busca el comparador de hallazgos esperados.</summary>
    [JsonIgnore]
    public string Text { get; init; } = string.Empty;

    /// <summary>Las citas de código de la sección y de las filas.</summary>
    [JsonIgnore]
    public IReadOnlyList<ReportCitation> CitationList { get; init; } = [];
}

/// <summary>Resultado de una comprobación: cuántos elementos pasan y cuántos fallan, y un resumen.</summary>
public sealed record ReportCheckResult(string Check, string Status, int Passed, int Failed, string? Detail);

/// <summary>Las cifras de un informe.</summary>
public sealed class ReportScores
{
    public int CitationsTotal { get; set; }
    public int CitationsValid { get; set; }

    /// <summary>Porcentaje de citas válidas, con un decimal; null si no hay citas.</summary>
    public double? CitationsValidPercent { get; set; }

    public int SourceReferencesTotal { get; set; }
    public int SourceReferencesValid { get; set; }

    /// <summary>Rutas de recurso citadas que una configuración declara y el repositorio no tiene (<see cref="ReportValidationChecks.DeclaredPaths"/>); no cuentan como citas.</summary>
    public int DeclaredPathsMissing { get; set; }
    public int FindingsDefined { get; set; }

    /// <summary>Hallazgos que no citan ninguna ubicación de código (<see cref="ReportValidationChecks.CitationsRequired"/>).</summary>
    public int FindingsUncited { get; set; }
    public int DuplicateIds { get; set; }
    public int MalformedIds { get; set; }
    public int UndefinedIdReferences { get; set; }

    /// <summary>Hallazgos P0 o P1.</summary>
    public int SevereFindings { get; set; }
    public int SevereFindingsWithoutLocation { get; set; }

    /// <summary>Null sin BD del grafo.</summary>
    public int? GraphNamesChecked { get; set; }
    public int? GraphNamesFound { get; set; }

    /// <summary>Nombres con punto cuyo primer tramo no es ningún nodo del grafo (librerías, variables locales): no se comprueban.</summary>
    public int? GraphNamesSkipped { get; set; }

    public int MermaidBlocks { get; set; }
    public int MermaidInvalid { get; set; }
    public bool MetaPresent { get; set; }
    public List<string> MetaMissingKeys { get; set; } = [];
}

/// <summary>La validación de un informe.</summary>
public sealed class ReportFileValidation
{
    public required string FileName { get; init; }
    public ReportScores Scores { get; } = new();
    public List<ReportCheckResult> Checks { get; } = [];
    public List<ReportValidationIssue> Issues { get; } = [];
    public List<ReportCitation> Citations { get; } = [];
    public List<ReportSourceReference> SourceReferences { get; } = [];

    /// <summary>Rutas declaradas en una configuración que no existen en el repositorio, aparte de <see cref="Citations"/>.</summary>
    public List<ReportDeclaredPath> DeclaredPaths { get; } = [];
    public List<ReportGraphName> GraphNames { get; } = [];
    public List<ReportFinding> Findings { get; } = [];

    public int Errors => Issues.Count(i => i.Level == ReportValidationLevels.Error);
    public int Warnings => Issues.Count(i => i.Level == ReportValidationLevels.Warning);
}

/// <summary>Un identificador que definen varios informes (cada uno por su lado): el <c>00</c> no puede citarlo sin ambigüedad.</summary>
public sealed record ReportIdCollision(string Id, IReadOnlyList<string> Reports);

/// <summary>Las cifras de todos los informes validados.</summary>
public sealed class ReportValidationTotals
{
    public int Reports { get; set; }
    public int CitationsTotal { get; set; }
    public int CitationsValid { get; set; }
    public double? CitationsValidPercent { get; set; }
    public int BrokenCitations { get; set; }
    public int SourceReferencesTotal { get; set; }
    public int SourceReferencesBroken { get; set; }

    /// <summary>Rutas declaradas en una configuración que no existen en el repositorio, de todos los informes: no son citas rotas.</summary>
    public int DeclaredPathsMissing { get; set; }
    public int SevereFindings { get; set; }
    public int SevereFindingsWithoutLocation { get; set; }
    public int DuplicateIds { get; set; }
    public int MalformedIds { get; set; }

    /// <summary>Informes de auditoría sin ninguna cita de código.</summary>
    public int ReportsWithoutCitations { get; set; }

    /// <summary>Hallazgos de todos los informes que no citan código.</summary>
    public int FindingsUncited { get; set; }
    public int? GraphNamesChecked { get; set; }
    public int? GraphNamesNotFound { get; set; }
    public int MermaidInvalid { get; set; }

    /// <summary>Informes sin línea <c>analysis-meta</c> o sin alguna clave de procedencia.</summary>
    public int ReportsWithoutProvenance { get; set; }

    public int Errors { get; set; }
    public int Warnings { get; set; }
}

/// <summary>Lo que escribe <see cref="ReportValidationFile"/>: la validación de la carpeta de informes.</summary>
public sealed class ReportValidationResult
{
    public required string GeneratedAt { get; init; }
    public required string RepositoryPath { get; init; }
    public required string ReportsPath { get; init; }
    public string? DatabasePath { get; init; }

    /// <summary>False si no se pudo leer el grafo: los nombres del grafo quedan sin comprobar.</summary>
    public bool GraphChecked { get; init; }

    public string? App { get; init; }
    public ReportValidationTotals Totals { get; } = new();
    public List<ReportFileValidation> Reports { get; } = [];
    public List<ReportIdCollision> CrossReportIdCollisions { get; } = [];

    /// <summary>Avisos de la propia validación (BD ilegible, carpeta sin informes).</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Comparación con un <c>expected-findings.json</c>, si se pidió.</summary>
    public ExpectedFindingsComparison? Expected { get; set; }
}

/// <summary>
/// <c>_validation.json</c>: el resultado de <see cref="ReportValidator"/>. Se escribe en la carpeta de informes (o en la que se
/// pida) al terminar un análisis o una regeneración, y con <c>--validate-reports</c>. No es un informe del catálogo, así que ni
/// se lista ni entra en el grafo.
/// </summary>
public static class ReportValidationFile
{
    public const string FileName = "_validation.json";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Escribe el fichero en <paramref name="folder"/>. Devuelve el aviso si no se pudo, sin lanzar.</summary>
    public static string? TryWrite(string folder, ReportValidationResult result)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, JsonSerializer.Serialize(result, Json) + "\n");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"⚠️ No se pudo escribir '{path}': {ex.Message}";
        }
    }
}

/// <summary>El resumen legible de una validación, para la consola, el log de la web y <c>run.completed</c>.</summary>
public static class ReportValidationSummary
{
    public const string SourceName = "ReportValidator";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Ejemplos de citas rotas en el aviso.</summary>
    public const int BrokenExamples = 3;

    public static string Percent(double? value) => value is { } v ? v.ToString("0.0", Spanish) + " %" : "sin citas";

    /// <summary>Las líneas del resumen con su gravedad: el total, una por informe y, si hay citas rotas, un aviso con ejemplos.</summary>
    public static IReadOnlyList<(AnalysisLogLevel Level, string Message)> Describe(ReportValidationResult result, string? writtenTo = null)
    {
        var t = result.Totals;
        var lines = new List<(AnalysisLogLevel, string)>();
        if (t.Reports == 0)
        {
            lines.Add((AnalysisLogLevel.Warning, $"🧪 Validación de informes: no hay informes del catálogo en '{result.ReportsPath}'."));
            return lines;
        }

        var graph = t.GraphNamesChecked is { } checkedNames
            ? $" Nombres del grafo no encontrados: {t.GraphNamesNotFound} de {checkedNames}."
            : " Nombres del grafo sin comprobar (sin BD).";
        lines.Add((t.Errors > 0 ? AnalysisLogLevel.Warning : AnalysisLogLevel.Success,
            $"🧪 Validación de {t.Reports} informe(s): {t.CitationsValid} de {t.CitationsTotal} citas de código válidas ({Percent(t.CitationsValidPercent)}); "
            + $"hallazgos P0/P1 sin ubicación: {t.SevereFindingsWithoutLocation} de {t.SevereFindings}; identificadores duplicados: {t.DuplicateIds}, "
            + $"mal formados: {t.MalformedIds}; Mermaid no permitido: {t.MermaidInvalid}; sin procedencia completa: {t.ReportsWithoutProvenance}.{graph} "
            + $"{t.Errors} error(es) y {t.Warnings} aviso(s)."
            // V6: cada afirmación se cita con ruta:línea; solo avisa, en la misma línea para no añadir otra al log de la ejecución.
            + (t.ReportsWithoutCitations > 0 || t.FindingsUncited > 0
                ? $" Sin citas de código: {t.ReportsWithoutCitations} informe(s) sin ninguna y {t.FindingsUncited} hallazgo(s) sin cita."
                : string.Empty)));

        foreach (var report in result.Reports)
        {
            var s = report.Scores;
            lines.Add((AnalysisLogLevel.Info,
                $"   • {report.FileName}: citas {s.CitationsValid}/{s.CitationsTotal} ({Percent(s.CitationsValidPercent)}), "
                + $"P0/P1 sin ubicación {s.SevereFindingsWithoutLocation}/{s.SevereFindings}, {report.Errors} error(es), {report.Warnings} aviso(s)"));
        }

        if (t.BrokenCitations > 0)
        {
            var examples = result.Reports
                .SelectMany(r => r.Citations.Where(c => !c.IsValid).Select(c => $"`{c.Text}` ({r.FileName}:{c.Line}, {DescribeCitation(c)})"))
                .Take(BrokenExamples);
            lines.Add((AnalysisLogLevel.Warning, $"⚠️ {t.BrokenCitations} cita(s) de código rota(s); por ejemplo: {string.Join("; ", examples)}."));
        }

        if (t.DeclaredPathsMissing > 0)
        {
            var examples = result.Reports
                .SelectMany(r => r.DeclaredPaths.Select(p => $"`{p.Text}` ({r.FileName}:{p.Line}, en `{p.DeclaredIn}`)"))
                .Take(BrokenExamples);
            lines.Add((AnalysisLogLevel.Info,
                $"ℹ️ {t.DeclaredPathsMissing} ruta(s) declarada(s) en la configuración que no existen en el repositorio (no cuentan como citas rotas); por ejemplo: {string.Join("; ", examples)}."));
        }

        if (result.CrossReportIdCollisions.Count > 0)
        {
            var examples = result.CrossReportIdCollisions.Take(BrokenExamples).Select(c => $"`{c.Id}` ({string.Join(", ", c.Reports)})");
            lines.Add((AnalysisLogLevel.Warning,
                $"⚠️ {result.CrossReportIdCollisions.Count} identificador(es) de hallazgo repetidos entre informes; por ejemplo: {string.Join("; ", examples)}. "
                + "El formato NN-CAT-nnn (`05-SEC-002`) los hace únicos."));
        }

        foreach (var note in result.Notes)
            lines.Add((AnalysisLogLevel.Warning, $"⚠️ {note}"));

        if (result.Expected is { } expected)
        {
            lines.Add((AnalysisLogLevel.Info,
                $"🎯 Hallazgos esperados: {expected.Found} de {expected.ExpectedCount} encontrados ({Percent(expected.CoveragePercent)}); "
                + $"{expected.UnexpectedCount} hallazgo(s) no esperado(s) en los informes cubiertos."));
            if (expected.NotExpectedCount > 0)
            {
                var triggered = expected.NotExpected.Where(t => t.Triggered)
                    .Select(t => $"{t.Id} ({string.Join(", ", t.MatchedFindings)})").ToList();
                lines.Add((triggered.Count > 0 ? AnalysisLogLevel.Warning : AnalysisLogLevel.Info,
                    $"🪤 Falsos positivos (notExpected): {expected.FalsePositiveCount} de {expected.NotExpectedCount} trampas activadas"
                    + (triggered.Count > 0 ? $": {string.Join("; ", triggered)}." : ".")));
            }
        }

        if (writtenTo != null)
            lines.Add((AnalysisLogLevel.Info, $"📄 Detalle en {writtenTo}"));
        return lines;
    }

    /// <summary>Por qué no vale una cita: no existe, es ambigua o se sale del fichero.</summary>
    public static string DescribeCitation(ReportCitation citation) => citation.Status switch
    {
        CitationStatuses.Missing => "no existe en el repositorio",
        CitationStatuses.Ambiguous => $"ambigua entre {string.Join(", ", citation.Candidates ?? [])}",
        CitationStatuses.OutOfRange => citation.FileLines is { } lines
            ? $"líneas fuera de rango: {citation.Target} tiene {lines}"
            : "líneas fuera de rango",
        _ => "válida"
    };

    /// <summary>Los totales para <c>run.completed</c> (<c>data.validation</c>), con las claves en camelCase.</summary>
    public static Dictionary<string, object?> ToData(ReportValidationResult result) => new()
    {
        ["reports"] = result.Totals.Reports,
        ["citationsTotal"] = result.Totals.CitationsTotal,
        ["citationsValid"] = result.Totals.CitationsValid,
        ["citationsValidPercent"] = result.Totals.CitationsValidPercent,
        ["brokenCitations"] = result.Totals.BrokenCitations,
        ["severeFindings"] = result.Totals.SevereFindings,
        ["severeFindingsWithoutLocation"] = result.Totals.SevereFindingsWithoutLocation,
        ["duplicateIds"] = result.Totals.DuplicateIds,
        ["malformedIds"] = result.Totals.MalformedIds,
        ["mermaidInvalid"] = result.Totals.MermaidInvalid,
        ["reportsWithoutProvenance"] = result.Totals.ReportsWithoutProvenance,
        ["errors"] = result.Totals.Errors,
        ["warnings"] = result.Totals.Warnings
    };

    /// <summary>
    /// Validación no bloqueante al final de un análisis o una regeneración: valida la carpeta de informes de
    /// <paramref name="input"/>, escribe <c>_validation.json</c> en ella y manda el resumen a <paramref name="reporter"/>.
    /// Nunca lanza: un fallo se informa como aviso y devuelve null.
    /// </summary>
    public static ReportValidationResult? RunAfterPipeline(AnalysisPipelineInput input, IAnalysisReporter reporter)
    {
        try
        {
            // Sin BD (el pipeline falló antes de indexar) los nombres del grafo se omiten sin aviso: no es un fallo de los informes.
            var db = !string.IsNullOrWhiteSpace(input.DatabasePath) && File.Exists(input.DatabasePath) ? input.DatabasePath : null;
            var result = ReportValidator.Validate(new ReportValidationRequest(input.RepositoryPath, input.OutputReportsPath, db));
            var warning = ReportValidationFile.TryWrite(input.OutputReportsPath, result);
            foreach (var (level, message) in Describe(result, warning == null ? Path.Combine(input.OutputReportsPath, ReportValidationFile.FileName) : null))
                reporter.Report(level, SourceName, message);
            if (warning != null)
                reporter.Report(AnalysisLogLevel.Warning, SourceName, warning);
            return result;
        }
        catch (Exception ex)
        {
            reporter.Report(AnalysisLogLevel.Warning, SourceName, $"⚠️ No se pudieron validar los informes: {ex.Message}");
            return null;
        }
    }
}
