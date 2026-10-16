using CodebaseAnalysis.Core.Reports;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Support;

/// <summary>Qué devolvió el verificador, ya interpretado (<see cref="ReportVerification.ParseOutput"/>).</summary>
public enum VerificationOutputKind
{
    /// <summary>El modelo no encontró nada que corregir.</summary>
    NoChanges,

    /// <summary>Informe corregido con su apartado «Cambios de la verificación».</summary>
    Corrected,

    /// <summary>La respuesta no se puede usar (vacía, sin el apartado, mucho más corta que el original...).</summary>
    Invalid
}

/// <summary><c>Report</c> y <c>Changes</c> solo con <see cref="VerificationOutputKind.Corrected"/>; <c>Problem</c> solo con <see cref="VerificationOutputKind.Invalid"/>.</summary>
public sealed record VerificationOutput(VerificationOutputKind Kind, string? Report = null, int Changes = 0, string? Problem = null);

/// <summary>
/// Lo determinista del verificador de informes: armar el texto que recibe el modelo (borrador, fallos de la validación y
/// evidencias de las citas), interpretar su respuesta y componer el informe que se escribe. El modelo y las tools quedan en
/// <see cref="Steps.VerifyReportStep"/>.
/// </summary>
public static partial class ReportVerification
{
    /// <summary>Apartado final del informe corregido (decisión del 2026-09-30).</summary>
    public const string ChangesHeading = "Cambios de la verificación";

    /// <summary>Respuesta corta del modelo cuando no corrige nada: ahorra devolver el informe entero.</summary>
    public const string NoChangesMarker = "SIN CAMBIOS";

    /// <summary>Clave de <c>analysis-meta</c> con el resultado: <c>ok</c>, <c>corrected(n)</c> o <c>warnings(n)</c>.</summary>
    public const string MetaKey = "verification";

    /// <summary>
    /// Sustituto de <c>@[</c> en el texto que ve la plantilla. El motor de plantillas expande <c>@[ruta]</c> en el texto ya
    /// interpolado, así que un borrador (o un fragmento de código) con esa secuencia se leería como una inclusión de fichero.
    /// </summary>
    public const string AtBracketMarker = "@(at)[";

    /// <summary>Líneas de contexto antes y después de cada cita.</summary>
    public const int EvidenceContextLines = 6;

    public const int MaxEvidenceSnippets = 60;
    public const int MaxSnippetLines = 80;
    public const int MaxEvidenceChars = 120_000;
    public const int MaxListedFailures = 60;
    public const int MaxListedCounts = 40;

    /// <summary>Nombres que, tras un número, no cuentan elementos del código: medidas de tamaño, tiempo y similares.</summary>
    private static readonly HashSet<string> NonCodeUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "líneas", "caracteres", "minutos", "segundos", "horas", "días", "meses", "años", "semanas", "veces", "puntos", "bytes",
        "tokens", "hallazgos", "informes", "niveles", "pasos", "fases", "secciones"
    };

    /// <summary>Un informe corregido que mide menos que esta fracción del original se toma por truncado o reescrito.</summary>
    public const double MinLengthRatio = 0.5;

    [GeneratedRegex(@"^#{2,3}[ \t]+Cambios de la verificaci[oó]n[ \t]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ChangesHeadingRegex();

    [GeneratedRegex(@"^[ \t]{0,3}(?:[-*+]|\d+[.)])[ \t]+\S", RegexOptions.Multiline)]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"<!--\s*analysis-meta\s*:.*?-->[ \t]*\n?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaLineRegex();

    [GeneratedRegex(@"^```[\w-]*[ \t]*\n(?<body>.*)\n```[ \t]*$", RegexOptions.Singleline)]
    private static partial Regex OuterFenceRegex();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCodeSpanRegex();

    [GeneratedRegex(@"\b(?:dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez|once|doce|trece|catorce|quince|diecis[eé]is|diecisiete|dieciocho|diecinueve|veinte|\d{1,3})\s+(?<noun>[a-záéíóúñ]{4,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CountRegex();

    public static string Neutralize(string text) => text.Replace("@[", AtBracketMarker, StringComparison.Ordinal);

    public static string Restore(string text) => text.Replace(AtBracketMarker, "@[", StringComparison.Ordinal);

    /// <summary>
    /// El texto de <c>{{ input }}</c>: <c>&lt;borrador&gt;</c> con el informe, <c>&lt;fallos_validacion&gt;</c> con lo que la
    /// validación determinista encontró (sin la línea de metadatos, que el verificador no toca) y <c>&lt;evidencias&gt;</c> con
    /// el código que rodea a cada cita que resuelve. Con las secuencias <c>@[</c> neutralizadas (<see cref="Neutralize"/>).
    /// </summary>
    public static string BuildInput(string fileName, string draft, ReportFileValidation validation, string repositoryRoot)
    {
        var sb = new StringBuilder();
        sb.Append("Informe a verificar: `").Append(fileName).Append("`\n\n");
        sb.Append("<borrador>\n").Append(draft.TrimEnd()).Append("\n</borrador>\n\n");

        sb.Append("<fallos_validacion>\n");
        // La cobertura (el apartado «Cobertura» de un informe en modo B) es de quien lo escribió con las notas de sus investigaciones:
        // el verificador, que comprueba afirmaciones contra el código, no puede arreglarla y no se le pide.
        var failures = validation.Issues.Where(i => i.Check is not (ReportValidationChecks.Metadata or ReportValidationChecks.CitationsRequired or ReportValidationChecks.Coverage)).ToList();
        if (failures.Count == 0)
            sb.Append("Ninguno: todas las citas del borrador resuelven a una ubicación que existe.\n");
        foreach (var issue in failures.Take(MaxListedFailures))
            sb.Append(CultureInfo.InvariantCulture, $"- línea {issue.Line} [{issue.Check}, {issue.Level}]: {issue.Message}\n");
        if (failures.Count > MaxListedFailures)
            sb.Append(CultureInfo.InvariantCulture, $"- (otros {failures.Count - MaxListedFailures} fallos omitidos)\n");
        sb.Append("</fallos_validacion>\n\n");

        sb.Append("<recuentos>\n");
        AppendCounts(sb, draft);
        sb.Append("</recuentos>\n\n");

        sb.Append("<evidencias>\n");
        AppendEvidence(sb, validation, repositoryRoot);
        sb.Append("</evidencias>\n");
        return Neutralize(sb.ToString());
    }

    /// <summary>
    /// Los recuentos del borrador («las tres acciones», «12 métodos»), cada uno con su línea y su frase: son lo que más se
    /// puede refutar contando en el código, y un modelo sin razonamiento extendido no los comprueba si nadie se los señala.
    /// Se ignoran los bloques de código, los fragmentos en código en línea (citas, nombres) y las cantidades que no cuentan
    /// elementos del código (líneas, tiempos, porcentajes).
    /// </summary>
    private static void AppendCounts(StringBuilder sb, string draft)
    {
        var lines = draft.ReplaceLineEndings("\n").Split('\n');
        var inFence = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listed = 0;
        for (var i = 0; i < lines.Length && listed < MaxListedCounts; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
                continue;

            var text = InlineCodeSpanRegex().Replace(lines[i], " ");
            foreach (Match match in CountRegex().Matches(text))
            {
                if (NonCodeUnits.Contains(match.Groups["noun"].Value) || text.AsSpan(match.Index + match.Length).TrimStart().StartsWith("%", StringComparison.Ordinal))
                    continue;

                var from = Math.Max(0, match.Index - 40);
                var to = Math.Min(text.Length, match.Index + match.Length + 50);
                var snippet = Regex.Replace(text[from..to], @"\s+", " ").Trim();
                if (!seen.Add(snippet))
                    continue;

                sb.Append(CultureInfo.InvariantCulture, $"- línea {i + 1}: «{snippet}»\n");
                if (++listed >= MaxListedCounts)
                    break;
            }
        }

        if (listed == 0)
            sb.Append("Ninguno.\n");
    }

    private static void AppendEvidence(StringBuilder sb, ReportFileValidation validation, string repositoryRoot)
    {
        var files = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string Target, int From, int To)>();
        var snippets = 0;
        var omitted = 0;
        var budget = MaxEvidenceChars;

        foreach (var citation in validation.Citations)
        {
            if (citation.Status != CitationStatuses.Ok || citation.Target == null)
                continue;

            IEnumerable<(int Start, int End)> ranges = citation.Ranges is { Count: > 0 }
                ? citation.Ranges.Select(r => (r.Start, r.End))
                : citation.StartLine is { } first ? [(first, citation.EndLine ?? first)] : [];

            foreach (var (start, end) in ranges)
            {
                if (!files.TryGetValue(citation.Target, out var lines))
                    files[citation.Target] = lines = ReadLines(Path.Combine(repositoryRoot, citation.Target));
                if (lines == null)
                    continue;

                var from = Math.Max(1, start - EvidenceContextLines);
                var to = Math.Min(lines.Length, Math.Max(end, start) + EvidenceContextLines);
                if (to - from + 1 > MaxSnippetLines)
                    to = from + MaxSnippetLines - 1;
                if (to < from || !seen.Add((citation.Target, from, to)))
                    continue;

                var snippet = new StringBuilder();
                snippet.Append(CultureInfo.InvariantCulture, $"### `{citation.Target}:{from}-{to}` (cita del informe: `{citation.Text}`)\n~~~\n");
                for (var n = from; n <= to; n++)
                    snippet.Append(CultureInfo.InvariantCulture, $"{n,5} | {lines[n - 1]}\n");
                snippet.Append("~~~\n");

                if (snippets >= MaxEvidenceSnippets || snippet.Length > budget)
                {
                    omitted++;
                    continue;
                }

                budget -= snippet.Length;
                snippets++;
                sb.Append(snippet);
            }
        }

        if (snippets == 0)
            sb.Append("Ninguna: el borrador no cita ubicaciones de código que resuelvan.\n");
        if (omitted > 0)
            sb.Append(CultureInfo.InvariantCulture, $"(otras {omitted} evidencias omitidas por tamaño: consúltalas con las herramientas)\n");
    }

    private static string[]? ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Interpreta la respuesta del modelo: <c>SIN CAMBIOS</c> en la primera línea, o el informe completo (empieza por su
    /// título) con un apartado «Cambios de la verificación» que enumera cada cambio. Un informe mucho más corto que
    /// <paramref name="original"/> no se acepta: el modelo lo ha truncado o reescrito.
    /// </summary>
    public static VerificationOutput ParseOutput(string? modelOutput, string original)
    {
        var text = StripOuterFence((modelOutput ?? string.Empty).Replace("\r\n", "\n").Trim());
        if (text.Length == 0)
            return Invalid("el verificador devolvió una respuesta vacía");

        var firstLine = text.Split('\n')[0].Trim().Trim('*', '#', '`', ' ');
        if (firstLine.StartsWith(NoChangesMarker, StringComparison.OrdinalIgnoreCase))
            return new VerificationOutput(VerificationOutputKind.NoChanges);

        if (!text.StartsWith("# ", StringComparison.Ordinal))
            return Invalid("la respuesta no empieza por el título del informe ni dice SIN CAMBIOS");

        var headings = ChangesHeadingRegex().Matches(text);
        if (headings.Count == 0)
            return Invalid($"falta el apartado «{ChangesHeading}»");

        var heading = headings[^1];
        var section = MetaLineRegex().Replace(text[heading.Index..], string.Empty);
        var changes = BulletRegex().Matches(section).Count;
        if (changes == 0)
        {
            return section.Contains("sin cambios", StringComparison.OrdinalIgnoreCase)
                ? new VerificationOutput(VerificationOutputKind.NoChanges)
                : Invalid($"el apartado «{ChangesHeading}» no enumera ningún cambio");
        }

        var body = MetaLineRegex().Replace(text[..heading.Index], string.Empty);
        if (body.Length < original.Length * MinLengthRatio)
        {
            return Invalid(string.Create(CultureInfo.InvariantCulture,
                $"el informe devuelto ({body.Length} caracteres) es menos de la mitad del original ({original.Length}): se descarta por si está truncado"));
        }

        return new VerificationOutput(VerificationOutputKind.Corrected, text, changes);
    }

    private static VerificationOutput Invalid(string problem) => new(VerificationOutputKind.Invalid, Problem: problem);

    private static string StripOuterFence(string text)
    {
        var match = OuterFenceRegex().Match(text);
        return match.Success ? match.Groups["body"].Value.Trim() : text;
    }

    /// <summary>
    /// El informe que se escribe: el corregido con las secuencias <c>@[</c> restauradas y la línea <c>analysis-meta</c> del
    /// original al final (el semáforo y la procedencia son de quien generó el informe, no del verificador).
    /// </summary>
    public static string Compose(string original, string correctedReport)
    {
        var body = MetaLineRegex().Replace(Restore(correctedReport), string.Empty).TrimEnd();
        var meta = ReportMarkdown.GetLastMetaLine(original);
        return meta == null ? body + "\n" : body + "\n\n" + meta + "\n";
    }

    /// <summary>Valor de la clave <see cref="MetaKey"/>: <c>ok</c>, <c>corrected(2)</c>, <c>warnings(3)</c> o <c>corrected(2),warnings(3)</c>.</summary>
    public static string MetaValue(int changes, int warnings)
    {
        var corrected = changes > 0 ? string.Create(CultureInfo.InvariantCulture, $"corrected({changes})") : null;
        var remaining = warnings > 0 ? string.Create(CultureInfo.InvariantCulture, $"warnings({warnings})") : null;
        return corrected != null && remaining != null ? $"{corrected},{remaining}" : corrected ?? remaining ?? "ok";
    }

    public static string WithVerificationMeta(string markdown, int changes, int warnings) =>
        ReportMarkdown.SetMetaValues(markdown, [new KeyValuePair<string, string>(MetaKey, MetaValue(changes, warnings))]);

    /// <summary>
    /// True si el informe devuelto por el modelo, sin su apartado de cambios ni su línea de metadatos, es el mismo texto que el
    /// original salvo espacios en blanco: el modelo declaró correcciones que no escribió, y reescribir el informe solo añadiría
    /// un apartado que miente.
    /// </summary>
    public static bool SameBody(string original, string correctedReport)
    {
        static string Normalize(string text) =>
            Regex.Replace(MetaLineRegex().Replace(WithoutChangesSection(text), string.Empty), @"\s+", " ").Trim();

        return Normalize(original) == Normalize(Restore(correctedReport));
    }

    /// <summary>El apartado «Cambios de la verificación» de <paramref name="report"/> en una línea y recortado; para los mensajes de aviso.</summary>
    public static string ChangesSummary(string report, int maxChars = 300)
    {
        var heading = ChangesHeadingRegex().Match(report);
        if (!heading.Success)
            return string.Empty;

        var flat = Regex.Replace(MetaLineRegex().Replace(report[heading.Index..], string.Empty), @"\s+", " ").Trim();
        return flat.Length <= maxChars ? flat : flat[..(maxChars - 1)] + "…";
    }

    /// <summary>Errores de validación que cuentan para el verificador: todos menos los de la línea de metadatos (procedencia).</summary>
    public static int CountWarnings(ReportFileValidation validation) =>
        validation.Issues.Count(i => i.Level == ReportValidationLevels.Error && i.Check != ReportValidationChecks.Metadata);

    /// <summary>
    /// El informe sin el apartado «Cambios de la verificación»: lo que se valida tras verificar. En ese apartado el verificador
    /// nombra a propósito las citas erróneas que ha corregido y los identificadores de los hallazgos que toca, y la
    /// validación los tomaría por citas rotas y por hallazgos repetidos. Los cambios los comprueba quien los lee.
    /// </summary>
    public static string WithoutChangesSection(string report)
    {
        var heading = ChangesHeadingRegex().Match(report);
        if (!heading.Success)
            return report;

        var meta = ReportMarkdown.LastMetaIndex(report);
        var end = meta > heading.Index ? meta : report.Length;
        return report[..heading.Index] + report[end..];
    }
}
