using CodebaseAnalysis.Core.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Reports;

/// <summary>
/// Un hallazgo que un repositorio de evaluación debería producir. <c>Report</c> es el fichero del informe
/// (<c>05_errores_y_bugs.md</c>) o su alias (<c>errores</c>). Casa con un hallazgo del informe si se cumplen todas las
/// condiciones que tenga: <c>File</c>, una cita válida del hallazgo a ese fichero (ruta relativa al repositorio o un sufijo
/// suyo); <c>StartLine</c>/<c>EndLine</c>, que esa cita se solape con el rango; <c>Keywords</c>, que el texto del hallazgo
/// (encabezado, sección y filas) contenga todas, sin distinguir mayúsculas ni tildes. Hace falta al menos <c>File</c> o
/// <c>Keywords</c>.
/// </summary>
public sealed record ExpectedFinding
{
    public string? Id { get; init; }
    public string? Report { get; init; }
    public string? File { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public List<string> Keywords { get; init; } = [];

    /// <summary>Texto libre para quien mantiene la lista; el comparador no lo usa.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Algo que los informes NO deberían decir (una trampa sembrada): un hallazgo que case con él es un falso positivo. Mismas
/// condiciones que <see cref="ExpectedFinding"/>, con <c>Files</c> para varios ficheros (basta que el hallazgo cite uno) y sin
/// obligación de <c>File</c> o <c>Keywords</c> más allá de tener alguno.
/// </summary>
public sealed record NotExpectedFinding
{
    public string? Id { get; init; }
    public string? Report { get; init; }
    public string? File { get; init; }
    public List<string> Files { get; init; } = [];
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public List<string> Keywords { get; init; } = [];
    public string? Note { get; init; }
}

/// <summary>El contenido de <c>expected-findings.json</c>: la lista de hallazgos esperados de un repositorio de evaluación.</summary>
public sealed record ExpectedFindingsDocument
{
    public string? Repository { get; init; }
    public string? Description { get; init; }
    public List<ExpectedFinding> Findings { get; init; } = [];

    /// <summary>Las trampas: los hallazgos que casen con alguna son falsos positivos.</summary>
    public List<NotExpectedFinding> NotExpected { get; init; } = [];

    /// <summary>
    /// Lee el fichero. Admite comentarios <c>//</c> y comas finales, para que la plantilla pueda ir documentada.
    /// </summary>
    /// <exception cref="InvalidDataException">Si el JSON no es válido.</exception>
    public static ExpectedFindingsDocument Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ExpectedFindingsDocument>(File.ReadAllText(path), ReadOptions)
                ?? throw new InvalidDataException($"'{path}' está vacío.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{path}' no es un expected-findings.json válido: {ex.Message}", ex);
        }
    }

    public static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

/// <summary>Resultado de un hallazgo esperado: si se encontró y con qué hallazgos del informe casa.</summary>
public sealed record ExpectedFindingResult(string Id, string? Report, bool Found, IReadOnlyList<string> MatchedFindings, string? Problem);

/// <summary>Una trampa de <c>notExpected</c> y los hallazgos del informe que casan con ella (falsos positivos si hay alguno).</summary>
public sealed record FalsePositiveResult(string Id, string? Report, IReadOnlyList<string> MatchedFindings, string? Problem)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Triggered => MatchedFindings.Count > 0;
}

/// <summary>Los hallazgos de un informe cubierto por la lista que no casan con ningún esperado: candidatos a falso positivo.</summary>
public sealed record UnexpectedFindings(string Report, IReadOnlyList<string> Findings);

/// <summary>Cobertura de los esperados y recuento de los no esperados, para medir los falsos positivos a mano.</summary>
public sealed class ExpectedFindingsComparison
{
    public string? File { get; init; }
    public int ExpectedCount { get; set; }
    public int Found { get; set; }

    /// <summary>Porcentaje de esperados encontrados, con un decimal; null si la lista está vacía.</summary>
    public double? CoveragePercent { get; set; }

    public int UnexpectedCount { get; set; }
    public List<ExpectedFindingResult> Items { get; } = [];
    public List<UnexpectedFindings> Unexpected { get; } = [];

    /// <summary>Número de trampas (<c>notExpected</c>) evaluadas.</summary>
    public int NotExpectedCount { get; set; }

    /// <summary>Cuántas trampas se activaron: hallazgos que dicen lo que no deberían.</summary>
    public int FalsePositiveCount { get; set; }

    public List<FalsePositiveResult> NotExpected { get; } = [];
}

/// <summary>Compara los hallazgos de una validación con un <see cref="ExpectedFindingsDocument"/>.</summary>
public static class ExpectedFindingsComparer
{
    public static ExpectedFindingsComparison Compare(ReportValidationResult validation, ExpectedFindingsDocument expected, string? file = null)
    {
        var comparison = new ExpectedFindingsComparison { File = file };
        var matchedByReport = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < expected.Findings.Count; i++)
        {
            var item = expected.Findings[i];
            var id = string.IsNullOrWhiteSpace(item.Id) ? $"#{i + 1}" : item.Id.Trim();
            comparison.ExpectedCount++;

            var reportName = ResolveReportName(item.Report);
            string? problem = reportName == null
                ? $"Informe '{item.Report}' desconocido: usa el fichero (05_errores_y_bugs.md) o el alias (errores)."
                : string.IsNullOrWhiteSpace(item.File) && item.Keywords.Count == 0
                    ? "Sin 'file' ni 'keywords': no hay nada con qué casar."
                    : null;

            var report = reportName == null ? null : validation.Reports.FirstOrDefault(r => r.FileName.Equals(reportName, StringComparison.OrdinalIgnoreCase));
            if (problem == null && report == null)
                problem = $"No hay informe {reportName} en la carpeta validada.";
            if (reportName != null)
                matchedByReport.TryAdd(reportName, new HashSet<string>(StringComparer.Ordinal));

            var matched = problem == null ? report!.Findings.Where(f => Matches(item, f)).Select(f => f.Id).ToList() : [];
            if (matched.Count > 0)
            {
                comparison.Found++;
                matchedByReport[reportName!].UnionWith(matched);
            }
            comparison.Items.Add(new ExpectedFindingResult(id, reportName ?? item.Report, matched.Count > 0, matched, problem));
        }

        foreach (var (reportName, matched) in matchedByReport.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var report = validation.Reports.FirstOrDefault(r => r.FileName.Equals(reportName, StringComparison.OrdinalIgnoreCase));
            if (report == null)
                continue;
            var unexpected = report.Findings.Select(f => f.Id).Where(fid => !matched.Contains(fid)).ToList();
            comparison.UnexpectedCount += unexpected.Count;
            comparison.Unexpected.Add(new UnexpectedFindings(report.FileName, unexpected));
        }

        for (var i = 0; i < expected.NotExpected.Count; i++)
        {
            var trap = expected.NotExpected[i];
            var id = string.IsNullOrWhiteSpace(trap.Id) ? $"#{i + 1}" : trap.Id.Trim();
            var reportName = ResolveReportName(trap.Report);
            var files = trap.Files.Concat(string.IsNullOrWhiteSpace(trap.File) ? [] : [trap.File]).ToList();
            string? problem = reportName == null
                ? $"Informe '{trap.Report}' desconocido: usa el fichero (03_codigo_muerto.md) o el alias (deadcode)."
                : files.Count == 0 && trap.Keywords.Count == 0
                    ? "Sin 'file', 'files' ni 'keywords': no hay nada con qué casar."
                    : null;
            var report = reportName == null ? null : validation.Reports.FirstOrDefault(r => r.FileName.Equals(reportName, StringComparison.OrdinalIgnoreCase));
            if (problem == null && report == null)
                problem = $"No hay informe {reportName} en la carpeta validada.";

            var matched = problem == null
                ? report!.Findings.Where(f => MatchesTrap(trap, files, f)).Select(f => f.Id).ToList()
                : [];
            comparison.NotExpectedCount++;
            if (matched.Count > 0)
                comparison.FalsePositiveCount++;
            comparison.NotExpected.Add(new FalsePositiveResult(id, reportName ?? trap.Report, matched, problem));
        }

        comparison.CoveragePercent = comparison.ExpectedCount == 0 ? null : Math.Round(100.0 * comparison.Found / comparison.ExpectedCount, 1);
        return comparison;
    }

    /// <summary>Fichero del catálogo (también el <c>00</c>) a partir del nombre, del alias o del tipo; null si no es ninguno.</summary>
    public static string? ResolveReportName(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
            return null;
        var name = report.Trim();
        if (Support.ReportFiles.TryGetCatalogName(name, out var canonical))
            return canonical;
        var descriptor = ReportCatalog.All.FirstOrDefault(d =>
            d.Alias.Equals(name, StringComparison.OrdinalIgnoreCase) || d.Type.ToString().Equals(name, StringComparison.OrdinalIgnoreCase));
        return descriptor?.FileName;
    }

    private static bool Matches(ExpectedFinding expected, ReportFinding finding) =>
        Matches(string.IsNullOrWhiteSpace(expected.File) ? [] : [expected.File], expected.StartLine, expected.EndLine, expected.Keywords, finding);

    private static bool MatchesTrap(NotExpectedFinding trap, List<string> files, ReportFinding finding) =>
        Matches(files, trap.StartLine, trap.EndLine, trap.Keywords, finding);

    /// <summary>Con varios ficheros basta que el hallazgo cite uno (con el rango, si lo hay); las palabras clave, todas.</summary>
    private static bool Matches(List<string> files, int? startLine, int? endLine, List<string> keywords, ReportFinding finding)
    {
        if (files.Count > 0)
        {
            var hit = files.Any(file =>
            {
                var wanted = file.Trim().Replace('\\', '/').TrimStart('.', '/');
                return finding.CitationList.Any(c => c.IsValid && c.Target != null
                    && (c.Target.Equals(wanted, StringComparison.OrdinalIgnoreCase) || c.Target.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase))
                    && Overlaps(startLine, endLine, c));
            });
            if (!hit)
                return false;
        }

        if (keywords.Count > 0)
        {
            var text = Fold(finding.Text);
            if (!keywords.Where(k => !string.IsNullOrWhiteSpace(k)).All(k => text.Contains(Fold(k.Trim()), StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    /// <summary>Sin rango esperado vale cualquier cita al fichero; con rango, la cita tiene que llevar líneas y solaparse.</summary>
    private static bool Overlaps(int? startLine, int? endLine, ReportCitation citation)
    {
        if (startLine is not { } start)
            return true;
        if (citation.StartLine is not { } s)
            return false;
        var end = endLine ?? start;
        // Una cita con varios rangos (`F.cs:11,16-21`) casa si alguno de ellos se solapa, no el hueco entre ellos.
        if (citation.Ranges is { Count: > 0 } ranges)
            return ranges.Any(r => r.Start <= end && r.End >= start);
        var e = citation.EndLine ?? s;
        return s <= end && e >= start;
    }

    /// <summary>Minúsculas y sin tildes: «Inyección» y «inyeccion» son la misma palabra clave.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
