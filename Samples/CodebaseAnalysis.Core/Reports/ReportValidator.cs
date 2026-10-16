using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Reports;

/// <summary>Qué validar: el repositorio (para resolver las citas), la carpeta de informes y, opcionalmente, la BD del grafo.</summary>
public sealed record ReportValidationRequest(string RepositoryPath, string ReportsPath, string? DatabasePath = null);

/// <summary>
/// Validación determinista de los informes del catálogo (<c>00</c> a <c>09</c>) contra el formato que piden las plantillas
/// (<c>_common_rules.md</c>, <c>_mermaid_guidelines.md</c> y <c>report_errores_prompt.md</c>), sin LLM: es el arnés mínimo de la
/// recomendación 13 de la auditoría para saber si un cambio de plantilla mejora o empeora los informes.
/// </summary>
/// <remarks>
/// <para>
/// Lee los spans de código en línea (<c>`...`</c>) fuera de los bloques cercados, que es donde las plantillas y el chat ponen
/// las referencias, y los clasifica así: con <c> › </c>, cita a un informe o documento; el nombre de fichero de un informe del
/// catálogo, cita a ese informe; <c>ruta:línea</c>, <c>ruta:ini-fin</c> o una ruta con separador y extensión, cita de código
/// (misma sintaxis que <see cref="ClaimAuditor"/> y <c>claims.ts</c>); un nombre con puntos (<c>Clase.metodo</c>), nombre del
/// grafo. Una cita de código se resuelve como el visor web (relativa a la raíz del repositorio) y, si no existe, por sufijo
/// contra los ficheros del repositorio, porque los informes citan <c>CGAR.bas:12</c> por un fichero exportado a
/// <c>.codebase/access_export/modules/</c>. Del índice quedan fuera <c>.git</c>, <c>bin</c>, <c>obj</c>, <c>node_modules</c>,
/// la carpeta de informes y todo <c>.codebase</c> salvo <c>access_export</c> (las copias de <c>backups/</c> harían ambigua
/// cualquier cita).
/// </para>
/// <para>
/// Los hallazgos son los encabezados que empiezan por un identificador <c>PREFIJO-NNN</c> (su sección llega hasta el siguiente
/// encabezado de igual o mayor nivel) y las filas de las tablas con columna de identificador. La prioridad sale de un
/// <c>P0</c>-<c>P3</c> en el encabezado o en la columna de severidad; solo el informe de errores la define en su plantilla.
/// </para>
/// </remarks>
public static partial class ReportValidator
{
    /// <summary>Tipos que permite <c>_mermaid_guidelines.md</c> (punto 1). Un test comprueba que coinciden con la plantilla.</summary>
    public static readonly IReadOnlyList<string> AllowedMermaidTypes =
        ["flowchart TD", "flowchart LR", "sequenceDiagram", "classDiagram", "erDiagram", "stateDiagram-v2"];

    /// <summary>Tipos que <c>_mermaid_guidelines.md</c> prohíbe expresamente.</summary>
    public static readonly IReadOnlyList<string> ForbiddenMermaidTypes = ["mindmap", "quadrantChart", "pie"];

    /// <summary>Claves de procedencia que la tanda B añade a <c>analysis-meta</c> y que hacen comparables dos ejecuciones.</summary>
    public static readonly IReadOnlyList<string> RequiredProvenanceKeys =
        [ReportProvenance.ModelKey, ReportProvenance.SystemShaKey, ReportProvenance.PromptShaKey, ReportProvenance.GraphKey];

    /// <summary>Semáforos válidos de <c>analysis-meta</c> (<c>_common_rules.md</c>, punto 6).</summary>
    public static readonly IReadOnlyList<string> HealthValues = ["Green", "Amber", "Red"];

    private const long MaxLineCountBytes = 20 * 1024 * 1024;
    private const int MaxCandidates = 5;
    private const string SourceSeparator = " › ";

    [GeneratedRegex(@"(?<!`)`(?<code>[^`\n]+)`(?!`)")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^(?<fence>`{3,}|~{3,})\s*(?<lang>[\w-]*)")]
    private static partial Regex FenceRegex();

    /// <summary>
    /// Identificador de hallazgo de <c>_common_rules.md</c>: <c>NN-CAT-nnn</c> (número del informe, 2 a 4 mayúsculas, guion y tres
    /// dígitos: <c>05-SEC-002</c>). El número de informe es opcional para reconocer los informes anteriores a V7 (<c>SEC-002</c>).
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])(?<id>(?:\d{2}-)?[A-Z]{2,4}-\d{3})(?![0-9])")]
    private static partial Regex FindingIdRegex();

    /// <summary>Un identificador casi bien formado: prefijo en mayúsculas y un número que no tiene tres dígitos, o con <c>_</c>.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])(?<id>(?:\d{2}-)?(?<p>[A-Z]{2,4})(?:-(?<n>\d{1,2}|\d{4,6})|_(?<n>\d{1,6})))(?![0-9A-Za-z])")]
    private static partial Regex LooseIdRegex();

    [GeneratedRegex(@"^(?<id>(?:\d{2}-)?[A-Z]{2,4}-\d{3})(?![0-9])")]
    private static partial Regex LeadingIdRegex();

    [GeneratedRegex(@"^(?<id>(?:\d{2}-)?[A-Z]{2,4}(?:-\d{1,2}|-\d{4,6}|_\d{1,6}))(?![0-9])")]
    private static partial Regex LeadingLooseIdRegex();

    /// <summary>
    /// Un hallazgo definido en un párrafo o elemento de lista que empieza por su identificador en negrita
    /// (<c>**COH-001, título.**</c>, <c>**GOD-001 · título.**</c>): el formato de 02 y 08 y de cualquier informe sin encabezado
    /// por hallazgo. Una referencia a mitad de frase no lo es.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:[-*+]\s+|\d+[.)]\s+)?\*\*\s*\[?(?<id>(?:\d{2}-)?[A-Z]{2,4}-\d{3})(?![0-9])(?<rest>[^*]*)\*\*")]
    private static partial Regex BoldDefinitionRegex();

    [GeneratedRegex("<action\\b[^>]*?\\bpath\\s*=\\s*[\"'](?<path>[^\"']+)[\"']", RegexOptions.Singleline)]
    private static partial Regex StrutsActionPathRegex();

    /// <summary>Los valores de un fichero XML de configuración: atributos entrecomillados y texto de elementos (<c>path="/WEB-INF/x.jsp"</c>, <c>&lt;location&gt;x.jsp&lt;/location&gt;</c>).</summary>
    [GeneratedRegex(@"=\s*""(?<a>[^""]*)""|=\s*'(?<b>[^']*)'|>(?<c>[^<>]*)<")]
    private static partial Regex XmlValueRegex();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlCommentRegex();

    /// <summary>Un valor que parece la ruta de un recurso: sin espacios, con extensión (<c>/WEB-INF/jsp/x.jsp</c>, <c>x.jsp</c>).</summary>
    [GeneratedRegex(@"^[\w$@{}\-./\\]+\.[A-Za-z0-9]{1,6}$")]
    private static partial Regex ResourcePathRegex();

    /// <summary><c>ruta:11,16-21</c>: varias líneas o rangos separados por comas (la sintaxis del chat solo admite uno).</summary>
    [GeneratedRegex(@"^(?<path>(?:[A-Za-z]:[\\/])?[^\s:*?""<>|`]+?):(?<ranges>\d+(?:\s*[-–]\s*\d+)?(?:\s*,\s*\d+(?:\s*[-–]\s*\d+)?)+)$")]
    private static partial Regex MultiRangeCitationRegex();

    [GeneratedRegex(@"^[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)+(?:\([^()]*\))?$")]
    private static partial Regex QualifiedNameRegex();

    public static ReportValidationResult Validate(ReportValidationRequest request, TimeProvider? clock = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RepositoryPath));
        var reportsPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ReportsPath));
        var dbPath = string.IsNullOrWhiteSpace(request.DatabasePath) ? null : Path.GetFullPath(request.DatabasePath);
        var graph = GraphNameIndex.TryLoad(dbPath, out var graphNote);
        var result = new ReportValidationResult
        {
            GeneratedAt = (clock ?? TimeProvider.System).GetUtcNow().ToString("o", CultureInfo.InvariantCulture),
            RepositoryPath = root,
            ReportsPath = reportsPath,
            DatabasePath = dbPath,
            GraphChecked = graph != null,
            App = AppVersion.Current
        };
        if (graphNote != null)
            result.Notes.Add(graphNote);

        var context = new ValidationContext(root, reportsPath, new RepositoryFileIndex(root, reportsPath), graph);
        var parsed = new List<ParsedReport>();
        foreach (var name in ReportFiles.CatalogFileNames)
        {
            var path = Path.Combine(reportsPath, name);
            if (File.Exists(path))
                parsed.Add(ParsedReport.Parse(name, ReportVerification.WithoutChangesSection(File.ReadAllText(path))));
        }
        if (parsed.Count == 0)
            result.Notes.Add($"No hay informes del catálogo en '{reportsPath}'.");

        // El 00 cita los hallazgos de los demás: sus referencias se comprueban contra lo que definen todos.
        var definedByReport = parsed.ToDictionary(p => p.FileName, p => p.DefinedIds(), StringComparer.OrdinalIgnoreCase);
        var allDefined = definedByReport.Values.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal);

        foreach (var report in parsed)
        {
            var isExecutive = string.Equals(report.FileName, ReportCatalog.ExecutiveReportFileName, StringComparison.OrdinalIgnoreCase);
            result.Reports.Add(ValidateReport(report, context, isExecutive ? allDefined : null));
        }

        foreach (var collision in definedByReport
                     .Where(r => !string.Equals(r.Key, ReportCatalog.ExecutiveReportFileName, StringComparison.OrdinalIgnoreCase))
                     .SelectMany(r => r.Value.Select(id => (Id: id, Report: r.Key)))
                     .GroupBy(x => x.Id, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            result.CrossReportIdCollisions.Add(new ReportIdCollision(collision.Key, [.. collision.Select(x => x.Report)]));
        }

        Summarize(result);
        return result;
    }

    /// <summary>
    /// Validador de un informe a partir de su texto, sin leer ni escribir informes en disco: es lo que usa el verificador para
    /// comprobar el borrador antes de mandarlo al modelo y el resultado antes de guardarlo. Construir uno indexa los ficheros
    /// del repositorio (para resolver las citas) y carga el grafo si hay BD; se reutiliza para varios informes.
    /// </summary>
    public static ReportContentValidator CreateContentValidator(ReportValidationRequest request) => new(request);

    /// <summary>
    /// Los hallazgos que define un informe, con su severidad y el código que citan, sin tocar el disco ni el grafo. Es la entrada de
    /// <see cref="SeverityContrast"/>.
    /// </summary>
    public static IReadOnlyList<FindingSummary> SummarizeFindings(string fileName, string content)
    {
        var report = ParsedReport.Parse(fileName, content);
        return [.. report.DefinedIds()
            .OrderBy(id => report.FirstDefinitionLine(id))
            .Select(id =>
            {
                var text = report.TextOf(report.FindingLines(id));
                // Sin P0-P3 en el encabezado ni en la tabla, el informe puede decirlo en prosa («Prioridad P3.»).
                var priority = report.PriorityOf(id) ?? (PriorityProseRegex().Match(text) is { Success: true } m ? int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture) : (int?)null);
                return new FindingSummary(id, priority, SeverityContrast.SpansOf(text));
            })];
    }

    [GeneratedRegex(@"\bPrioridad\s+P(?<p>[0-3])\b")]
    private static partial Regex PriorityProseRegex();

    /// <summary>Ver <see cref="CreateContentValidator"/>. Las comprobaciones son las de <see cref="Validate"/>, salvo las que cruzan informes.</summary>
    public sealed class ReportContentValidator
    {
        private readonly ValidationContext _context;

        internal ReportContentValidator(ReportValidationRequest request)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RepositoryPath));
            var reportsPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ReportsPath));
            var dbPath = string.IsNullOrWhiteSpace(request.DatabasePath) ? null : Path.GetFullPath(request.DatabasePath);
            Root = root;
            _context = new ValidationContext(root, reportsPath, new RepositoryFileIndex(root, reportsPath), GraphNameIndex.TryLoad(dbPath, out _));
        }

        /// <summary>Raíz del repositorio contra la que se resuelven las citas.</summary>
        public string Root { get; }

        /// <summary>
        /// Valida <paramref name="content"/> como el informe <paramref name="fileName"/> (los IDs que cita no se cruzan con otros
        /// informes). Sin el apartado «Cambios de la verificación», que no es contenido del auditor (<see cref="ReportVerification.WithoutChangesSection"/>).
        /// </summary>
        public ReportFileValidation Validate(string fileName, string content) =>
            ValidateReport(ParsedReport.Parse(fileName, ReportVerification.WithoutChangesSection(content)), _context, crossReportIds: null);
    }

    private static void Summarize(ReportValidationResult result)
    {
        var t = result.Totals;
        t.Reports = result.Reports.Count;
        foreach (var r in result.Reports)
        {
            var s = r.Scores;
            t.CitationsTotal += s.CitationsTotal;
            t.CitationsValid += s.CitationsValid;
            t.SourceReferencesTotal += s.SourceReferencesTotal;
            t.SourceReferencesBroken += s.SourceReferencesTotal - s.SourceReferencesValid;
            t.DeclaredPathsMissing += s.DeclaredPathsMissing;
            t.SevereFindings += s.SevereFindings;
            t.SevereFindingsWithoutLocation += s.SevereFindingsWithoutLocation;
            t.DuplicateIds += s.DuplicateIds;
            t.MalformedIds += s.MalformedIds;
            t.FindingsUncited += s.FindingsUncited;
            if (r.Checks.Any(c => c.Check == ReportValidationChecks.CitationsRequired) && s.CitationsTotal == 0)
                t.ReportsWithoutCitations++;
            t.MermaidInvalid += s.MermaidInvalid;
            if (!s.MetaPresent || s.MetaMissingKeys.Count > 0)
                t.ReportsWithoutProvenance++;
            if (s.GraphNamesChecked is { } checkedNames)
            {
                t.GraphNamesChecked = (t.GraphNamesChecked ?? 0) + checkedNames;
                t.GraphNamesNotFound = (t.GraphNamesNotFound ?? 0) + checkedNames - (s.GraphNamesFound ?? 0);
            }
            t.Errors += r.Errors;
            t.Warnings += r.Warnings;
        }
        t.BrokenCitations = t.CitationsTotal - t.CitationsValid;
        t.CitationsValidPercent = PercentOf(t.CitationsValid, t.CitationsTotal);
    }

    private static double? PercentOf(int part, int total) => total == 0 ? null : Math.Round(100.0 * part / total, 1);

    private sealed record ValidationContext(string Root, string ReportsPath, RepositoryFileIndex Files, GraphNameIndex? Graph)
    {
        private IReadOnlyList<(string Id, string Title, string Path)>? _sources;
        private bool _planLoaded;
        private CoveragePlan? _plan;

        /// <summary>
        /// El plan de cobertura del disco (<c>_coverage_plan.json</c> junto a los informes); null si no hay, no se puede leer o no es de
        /// este formato: entonces no se comprueba la cobertura. Se lee una vez por validación. Solo lee: no crea la carpeta.
        /// </summary>
        public CoveragePlan? CoveragePlan
        {
            get
            {
                if (!_planLoaded)
                {
                    _plan = PreparationArtifacts.TryRead<CoveragePlan>(ReportsPath, out _);
                    _planLoaded = true;
                }
                return _plan;
            }
        }

        /// <summary>Informes del catálogo y propios, con su título, y documentos: los destinos posibles de <c>nombre › sección</c>.</summary>
        public IReadOnlyList<(string Id, string Title, string Path)> Sources => _sources ??= LoadSources();

        private List<(string Id, string Title, string Path)> LoadSources()
        {
            var sources = new List<(string, string, string)>();
            foreach (var report in ReportFiles.List(ReportsPath))
                sources.Add((report.FileName, report.Title, Path.Combine(ReportsPath, report.FileName)));

            var knowledge = new KnowledgePaths(Root, ReportsPath);
            try
            {
                foreach (var custom in ReportLibrary.ListCustom(knowledge))
                    sources.Add((ReportLibrary.CustomIdPrefix + custom.FileName, custom.Title, Path.Combine(knowledge.CustomReportsPath, custom.FileName)));
                var documents = new DocumentStore(knowledge);
                foreach (var document in documents.List())
                    sources.Add((document.Id, document.Title, documents.ContentPath(document.Id)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Sin informes propios ni documentos legibles, las citas a ellos quedan como no encontradas.
            }
            return sources;
        }
    }

    private static ReportFileValidation ValidateReport(ParsedReport report, ValidationContext context, HashSet<string>? crossReportIds)
    {
        var validation = new ReportFileValidation { FileName = report.FileName };
        var citationsByLine = new List<ReportCitation>();

        foreach (var (line, text) in report.Spans)
        {
            if (text.Contains(SourceSeparator, StringComparison.Ordinal))
            {
                validation.SourceReferences.Add(ResolveSource(text, line, context));
                continue;
            }

            if (ReportFiles.TryGetCatalogName(text, out var catalogName) || IsCustomReportName(text))
            {
                var exists = catalogName != null
                    ? File.Exists(Path.Combine(context.ReportsPath, catalogName))
                    : File.Exists(Path.Combine(context.ReportsPath, text.Replace('\\', '/')));
                validation.SourceReferences.Add(new ReportSourceReference(text, line,
                    exists ? SourceReferenceStatuses.Ok : SourceReferenceStatuses.MissingSource, catalogName ?? text));
                continue;
            }

            if (AsCodeCitation(text, context.Files) is { } code)
            {
                var citation = ResolveCitation(text, line, code, context.Files);

                // Una ruta de recurso sin línea que ningún fichero del repositorio es, pero que una configuración declara (el `forward` de un
                // struts-config a un JSP que el repositorio no trae), no es una cita de código rota: se cuenta aparte, con su aviso.
                if (citation.Status == CitationStatuses.Missing && code.Start == null && context.Files.DeclaredResource(code.Path) is { } declaredIn)
                {
                    validation.DeclaredPaths.Add(new ReportDeclaredPath(text, line, declaredIn));
                    continue;
                }

                validation.Citations.Add(citation);
                citationsByLine.Add(citation);
                continue;
            }

            // Un nombre de recurso suelto (`products.jsp`) parece un `Clase.método` y saldría como «no está en el grafo»: si una configuración
            // lo declara, es una ruta declarada. Solo con extensión de recurso: `Clase.método` no puede confundirse con ellas.
            if (HasResourceExtension(text) && !context.Files.HasFileNamed(text) && context.Files.DeclaredResource(text) is { } declaredResource)
            {
                validation.DeclaredPaths.Add(new ReportDeclaredPath(text, line, declaredResource));
                continue;
            }

            if (context.Graph != null && QualifiedNameRegex().IsMatch(text) && !context.Files.HasFileNamed(text))
            {
                var name = StripArguments(text);
                if (context.Graph.Contains(name) || context.Graph.IsKnownOwner(name))
                    validation.GraphNames.Add(new ReportGraphName(text, line, context.Graph.Contains(name)));
                else
                    validation.Scores.GraphNamesSkipped = (validation.Scores.GraphNamesSkipped ?? 0) + 1;
            }
        }

        CheckCitations(validation);
        CheckSourceReferences(validation);
        CheckFindings(report, validation, citationsByLine, crossReportIds);
        CheckRequiredCitations(report, validation);
        CheckGraphNames(validation, context.Graph != null);
        CheckMetadata(report, validation);
        CheckMermaid(report, validation);
        CheckCoverage(report, validation, context);
        return validation;
    }

    /// <summary>Extensiones de los recursos que una configuración declara (vistas, plantillas, ficheros de propiedades...): lo que no es código.</summary>
    private static readonly string[] ResourceExtensions =
        [".jsp", ".jspf", ".jspx", ".html", ".htm", ".xhtml", ".tld", ".xml", ".properties", ".css", ".js", ".ftl", ".vm", ".tag", ".tagx"];

    private static bool HasResourceExtension(string text) =>
        text.IndexOfAny([' ', ':', '(', ')']) < 0 && ResourceExtensions.Any(e => text.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static bool IsCustomReportName(string text) =>
        text.StartsWith(ReportLibrary.CustomIdPrefix, StringComparison.OrdinalIgnoreCase)
        && text.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && text.IndexOfAny([' ', ':']) < 0;

    private static string StripArguments(string text)
    {
        var paren = text.IndexOf('(');
        return paren < 0 ? text : text[..paren];
    }

    /// <summary>
    /// La cita de código de un span, o null si no lo es. Además de la sintaxis del chat, sin línea hace falta una extensión en
    /// el último tramo (<c>P0/P1</c> y <c>Green/Amber</c> no son rutas), y con línea la ruta tiene que parecer un fichero
    /// (punto o separador) o llamarse como alguno del repositorio (<c>1:1</c> no es una cita).
    /// </summary>
    private static (string Path, int? Start, int? End, IReadOnlyList<CitationRange>? Ranges)? AsCodeCitation(string text, RepositoryFileIndex files)
    {
        var multi = MultiRangeCitationRegex().Match(text.Trim());
        if (multi.Success)
        {
            var ranges = multi.Groups["ranges"].Value.Split(',')
                .Select(r => r.Split('-', '–').Select(n => int.Parse(n.Trim(), CultureInfo.InvariantCulture)).ToArray())
                .Select(n => new CitationRange(n[0], n.Length > 1 ? n[1] : n[0]))
                .ToList();
            var multiPath = multi.Groups["path"].Value;
            return multiPath.IndexOfAny(['.', '/', '\\']) >= 0 || files.HasFileNamed(multiPath)
                ? (multiPath, ranges.Min(r => r.Start), ranges.Max(r => r.End), ranges)
                : null;
        }

        if (ClaimAuditor.ParseCodeReference(text) is not { } single)
            return null;
        var code = (single.Path, single.Start, single.End, Ranges: (IReadOnlyList<CitationRange>?)null);

        var lastSegment = code.Path.Replace('\\', '/').TrimEnd('/').Split('/')[^1];
        if (code.Start == null)
            return lastSegment.Contains('.') ? code : null;
        return code.Path.IndexOfAny(['.', '/', '\\']) >= 0 || files.HasFileNamed(lastSegment) ? code : null;
    }

    private static ReportCitation ResolveCitation(string text, int line, (string Path, int? Start, int? End, IReadOnlyList<CitationRange>? Ranges) code, RepositoryFileIndex files)
    {
        var resolved = files.Resolve(code.Path);
        if (resolved.Status != CitationStatuses.Ok)
            return new ReportCitation(text, line, resolved.Status, null, code.Start, code.End, false, null,
                resolved.Candidates.Count > 0 ? resolved.Candidates : null) { Ranges = code.Ranges };

        // Sin fichero (una ruta declarada) o con un directorio, no hay líneas que comprobar.
        int? total = code.Start == null || resolved.Full == null || !File.Exists(resolved.Full) ? null : files.LineCount(resolved.Full);
        IReadOnlyList<CitationRange> checkedRanges = code.Ranges ?? (code.Start is { } a && code.End is { } b ? [new CitationRange(a, b)] : []);
        var inRange = total is not { } lines || checkedRanges.All(r => r.Start >= 1 && r.End >= r.Start && r.End <= lines);
        return new ReportCitation(text, line, inRange ? CitationStatuses.Ok : CitationStatuses.OutOfRange, resolved.Relative,
            code.Start, code.End, resolved.BySuffix, total, null) { Ranges = code.Ranges };
    }

    private static ReportSourceReference ResolveSource(string text, int line, ValidationContext context)
    {
        var separator = text.IndexOf(SourceSeparator, StringComparison.Ordinal);
        var name = text[..separator].Trim().Trim('`');
        var section = text[(separator + SourceSeparator.Length)..].Trim();

        var source = context.Sources.FirstOrDefault(s => Same(s.Id, name) || Same(s.Title, name) || Same(Path.GetFileNameWithoutExtension(s.Id), name));
        if (source.Id == null)
            return new ReportSourceReference(text, line, SourceReferenceStatuses.MissingSource, null);

        string[] lines;
        try
        {
            lines = File.Exists(source.Path) ? File.ReadAllText(source.Path).ReplaceLineEndings("\n").Split('\n') : [];
        }
        catch (IOException)
        {
            lines = [];
        }

        // «informe › sección › subsección»: vale la sección completa o, si no, el último tramo.
        var last = section.Split(SourceSeparator)[^1].Trim();
        var found = section.Length == 0
            || MarkdownHeadings.FindSection(lines, section) != null
            || MarkdownHeadings.FindSection(lines, last) != null;
        return new ReportSourceReference(text, line, found ? SourceReferenceStatuses.Ok : SourceReferenceStatuses.MissingSection, source.Id);
    }

    private static bool Same(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);

    private static void CheckCitations(ReportFileValidation validation)
    {
        var s = validation.Scores;
        s.CitationsTotal = validation.Citations.Count;
        s.CitationsValid = validation.Citations.Count(c => c.IsValid);
        s.CitationsValidPercent = PercentOf(s.CitationsValid, s.CitationsTotal);
        foreach (var citation in validation.Citations.Where(c => !c.IsValid))
        {
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.Citations,
                citation.Status == CitationStatuses.Ambiguous ? ReportValidationLevels.Warning : ReportValidationLevels.Error,
                citation.Line, $"`{citation.Text}`: {ReportValidationSummary.DescribeCitation(citation)}."));
        }

        AddCheck(validation, ReportValidationChecks.Citations, s.CitationsValid, s.CitationsTotal - s.CitationsValid,
            s.CitationsTotal == 0 ? "El informe no cita ninguna ubicación de código." : $"{s.CitationsValid} de {s.CitationsTotal} válidas ({ReportValidationSummary.Percent(s.CitationsValidPercent)}).");

        // Las rutas declaradas en una configuración que el repositorio no tiene: un aviso por ruta, sin tocar el porcentaje de citas.
        s.DeclaredPathsMissing = validation.DeclaredPaths.Count;
        if (validation.DeclaredPaths.Count > 0)
        {
            foreach (var declared in validation.DeclaredPaths)
            {
                validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.DeclaredPaths, ReportValidationLevels.Warning, declared.Line,
                    $"`{declared.Text}`: ruta declarada en `{declared.DeclaredIn}` que no existe en el repositorio (no es una cita de código)."));
            }
            AddCheck(validation, ReportValidationChecks.DeclaredPaths, 0, validation.DeclaredPaths.Count,
                $"{validation.DeclaredPaths.Count} ruta(s) declarada(s) en la configuración que no existen en el repositorio.");
        }
    }

    /// <summary>
    /// V6: todo informe de auditoría cita de dónde sale cada afirmación (<c>ruta:línea</c>). Solo avisa (no falla): sin ninguna cita,
    /// o con hallazgos que no citan ninguna. El resumen ejecutivo cita informes, no código, y queda fuera.
    /// </summary>
    private static void CheckRequiredCitations(ParsedReport report, ReportFileValidation validation)
    {
        if (string.Equals(report.FileName, ReportCatalog.ExecutiveReportFileName, StringComparison.OrdinalIgnoreCase))
            return;

        var s = validation.Scores;
        var uncited = validation.Findings.Where(f => f.Citations == 0).ToList();
        s.FindingsUncited = uncited.Count;
        string? detail;
        if (validation.Citations.Count == 0)
        {
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.CitationsRequired, ReportValidationLevels.Warning, 1,
                "El informe no cita ninguna ubicación de código (`ruta:línea`): cada afirmación debe poder comprobarse."));
            detail = "Sin ninguna cita de código.";
        }
        else if (uncited.Count > 0)
        {
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.CitationsRequired, ReportValidationLevels.Warning, uncited[0].Line,
                $"{uncited.Count} hallazgo(s) sin ninguna cita de código (`ruta:línea`): {string.Join(", ", uncited.Take(8).Select(f => $"`{f.Id}`"))}"
                + (uncited.Count > 8 ? ", ..." : string.Empty) + "."));
            detail = $"{validation.Findings.Count - uncited.Count} de {validation.Findings.Count} hallazgos citan código.";
        }
        else
        {
            detail = $"{validation.Citations.Count} citas de código.";
        }
        AddCheck(validation, ReportValidationChecks.CitationsRequired, validation.Findings.Count - uncited.Count, uncited.Count, detail);
    }

    private static void CheckSourceReferences(ReportFileValidation validation)
    {
        var s = validation.Scores;
        s.SourceReferencesTotal = validation.SourceReferences.Count;
        s.SourceReferencesValid = validation.SourceReferences.Count(r => r.Status == SourceReferenceStatuses.Ok);
        foreach (var reference in validation.SourceReferences.Where(r => r.Status != SourceReferenceStatuses.Ok))
        {
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.SourceReferences, ReportValidationLevels.Error, reference.Line,
                reference.Status == SourceReferenceStatuses.MissingSection
                    ? $"`{reference.Text}`: {reference.Target} no tiene esa sección."
                    : $"`{reference.Text}`: no existe ese informe ni documento."));
        }

        AddCheck(validation, ReportValidationChecks.SourceReferences, s.SourceReferencesValid, s.SourceReferencesTotal - s.SourceReferencesValid,
            s.SourceReferencesTotal == 0 ? null : $"{s.SourceReferencesValid} de {s.SourceReferencesTotal} válidas.",
            notApplicable: s.SourceReferencesTotal == 0);
    }

    private static void CheckFindings(ParsedReport report, ReportFileValidation validation, List<ReportCitation> citations, HashSet<string>? crossReportIds)
    {
        var s = validation.Scores;
        var errorsBefore = validation.Issues.Count;

        // Formato: un identificador casi bien formado con el prefijo de un hallazgo definido (en el 00, de cualquier informe), o
        // que encabeza un hallazgo. Con cualquier prefijo, `SHA-1` o `UTF-8` serían identificadores mal formados.
        var defined = report.DefinedIds();
        var prefixes = defined.Concat(crossReportIds ?? []).Select(PrefixOf).ToHashSet(StringComparer.Ordinal);
        var malformed = report.LooseIdMentions
            .Where(m => m.InHeadingStart || prefixes.Contains(m.Prefix))
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var group in malformed)
        {
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.FindingIds, ReportValidationLevels.Error, group.First().Line,
                $"`{group.Key}` no sigue el formato PREFIJO-NNN de `_common_rules.md` (guion y tres dígitos, como `{Canonical(group.Key)}`)."));
        }
        s.MalformedIds = malformed.Count;

        // Unicidad: dos encabezados con el mismo identificador son dos hallazgos distintos con el mismo nombre.
        var duplicates = 0;
        foreach (var group in report.HeadingDefinitions.GroupBy(d => d.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            duplicates++;
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.FindingIds, ReportValidationLevels.Error, group.First().Line,
                $"`{group.Key}` encabeza {group.Count()} hallazgos (líneas {string.Join(", ", group.Select(d => d.Line))})."));
        }
        foreach (var table in report.Tables.Where(t => t.IdColumn >= 0))
        {
            foreach (var group in table.Rows.Select((row, i) => (Id: CellId(row, table.IdColumn), Line: table.RowLines[i] + 1))
                         .Where(x => x.Id != null)
                         .GroupBy(x => x.Id!, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
            {
                duplicates++;
                validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.FindingIds, ReportValidationLevels.Warning, group.First().Line,
                    $"`{group.Key}` aparece en {group.Count()} filas de la misma tabla (líneas {string.Join(", ", group.Select(x => x.Line))})."));
            }
        }
        s.DuplicateIds = duplicates;

        // V7: un identificador con prefijo de informe lleva el número de su propio fichero, y un informe no mezcla ambos formatos.
        // Un informe anterior a V7, entero sin prefijo, no se avisa.
        var isExecutive = string.Equals(report.FileName, ReportCatalog.ExecutiveReportFileName, StringComparison.OrdinalIgnoreCase);
        var fileNumber = ReportNumberOfFile(report.FileName);
        var withNumber = defined.Where(id => ReportNumberOf(id) != null).OrderBy(id => report.FirstDefinitionLine(id)).ToList();
        if (withNumber.Count > 0 && !isExecutive)
        {
            foreach (var id in withNumber.Where(id => fileNumber != null && ReportNumberOf(id) != fileNumber))
            {
                validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.FindingIds, ReportValidationLevels.Warning, report.FirstDefinitionLine(id),
                    $"`{id}` no lleva el número de informe {fileNumber} de este fichero (`{fileNumber}-{id[3..]}`)."));
            }
            foreach (var id in defined.Where(id => ReportNumberOf(id) == null).OrderBy(id => report.FirstDefinitionLine(id)))
            {
                validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.FindingIds, ReportValidationLevels.Warning, report.FirstDefinitionLine(id),
                    $"`{id}` va sin prefijo de informe en un informe que ya usa el formato NN-CAT-nnn."));
            }
        }

        // Los hallazgos, con su prioridad y sus citas.
        foreach (var id in defined.OrderBy(i => report.FirstDefinitionLine(i)))
        {
            var lines = report.FindingLines(id);
            var own = citations.Where(c => lines.Contains(c.Line)).ToList();
            validation.Findings.Add(new ReportFinding(id, report.FirstDefinitionLine(id), report.PriorityOf(id), own.Count, own.Count(c => c.IsValid))
            {
                Text = report.TextOf(lines),
                CitationList = own
            });
        }
        s.FindingsDefined = validation.Findings.Count;

        // Referencias sin definición, solo de prefijos que el informe define (en el 00, los de todos los informes).
        var known = crossReportIds ?? defined;
        var knownPrefixes = known.Select(PrefixOf).ToHashSet(StringComparer.Ordinal);
        var undefined = report.IdMentions
            .Where(m => !known.Contains(m.Id) && !defined.Contains(m.Id) && knownPrefixes.Contains(PrefixOf(m.Id)))
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var group in undefined)
        {
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.FindingIds, ReportValidationLevels.Warning, group.First().Line,
                crossReportIds != null
                    ? $"`{group.Key}` se cita pero ningún informe lo define."
                    : $"`{group.Key}` se cita pero ningún hallazgo del informe lo define."));
        }
        s.UndefinedIdReferences = undefined.Count;

        var failed = validation.Issues.Skip(errorsBefore).Count(i => i.Level == ReportValidationLevels.Error);
        AddCheck(validation, ReportValidationChecks.FindingIds, s.FindingsDefined, failed,
            s.FindingsDefined == 0 ? "El informe no define hallazgos con identificador PREFIJO-NNN." : $"{s.FindingsDefined} hallazgos definidos.",
            issuesFrom: errorsBefore);

        // P0/P1: al menos una ubicación de código que exista.
        var severe = validation.Findings.Where(f => f.Priority is <= 1).ToList();
        s.SevereFindings = severe.Count;
        foreach (var finding in severe.Where(f => f.ValidCitations == 0))
        {
            s.SevereFindingsWithoutLocation++;
            var broken = finding.CitationList.Select(c => $"`{c.Text}`").ToList();
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.SevereFindings, ReportValidationLevels.Error, finding.Line,
                broken.Count == 0
                    ? $"`{finding.Id}` (P{finding.Priority}) no cita ninguna ubicación de código."
                    : $"`{finding.Id}` (P{finding.Priority}) solo cita ubicaciones que no existen: {string.Join(", ", broken)}."));
        }
        AddCheck(validation, ReportValidationChecks.SevereFindings, s.SevereFindings - s.SevereFindingsWithoutLocation, s.SevereFindingsWithoutLocation,
            severe.Count == 0 ? "Sin hallazgos P0/P1." : $"{s.SevereFindings - s.SevereFindingsWithoutLocation} de {s.SevereFindings} con ubicación.",
            notApplicable: severe.Count == 0);
    }

    /// <summary>La categoría del identificador (<c>SEC</c> en <c>SEC-001</c> y en <c>05-SEC-001</c>).</summary>
    private static string PrefixOf(string id) => ReportNumberOf(id) is null ? id[..id.IndexOf('-')] : id.Split('-')[1];

    /// <summary>El número de informe de un identificador <c>NN-CAT-nnn</c>; null en el formato anterior (<c>CAT-nnn</c>).</summary>
    private static string? ReportNumberOf(string id) =>
        id.Length > 3 && char.IsAsciiDigit(id[0]) && char.IsAsciiDigit(id[1]) && id[2] == '-' ? id[..2] : null;

    /// <summary>El número del fichero de un informe (<c>05</c> en <c>05_errores_y_bugs.md</c>); null si no empieza por dos dígitos y guion bajo.</summary>
    private static string? ReportNumberOfFile(string fileName) =>
        fileName.Length > 3 && char.IsAsciiDigit(fileName[0]) && char.IsAsciiDigit(fileName[1]) && fileName[2] == '_' ? fileName[..2] : null;

    /// <summary><c>SEC-1</c> o <c>SEC_001</c> escrito como <c>SEC-001</c>; <c>05-SEC-1</c>, como <c>05-SEC-001</c>.</summary>
    private static string Canonical(string id)
    {
        var separator = id.LastIndexOfAny(['-', '_']);
        return int.TryParse(id[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number < 1000
            ? $"{id[..separator]}-{number:D3}"
            : id;
    }

    private static string? CellId(List<string> row, int column)
    {
        var match = LeadingIdRegex().Match(ReportMarkdown.CleanCell(ReportMarkdown.Cell(row, column)).TrimStart('[').Trim());
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static void CheckGraphNames(ReportFileValidation validation, bool hasGraph)
    {
        var s = validation.Scores;
        if (!hasGraph)
        {
            AddCheck(validation, ReportValidationChecks.GraphNames, 0, 0, "Sin BD del grafo: no se comprueba.", skipped: true);
            return;
        }

        s.GraphNamesSkipped ??= 0;
        s.GraphNamesChecked = validation.GraphNames.Count;
        s.GraphNamesFound = validation.GraphNames.Count(n => n.Found);
        foreach (var name in validation.GraphNames.Where(n => !n.Found).DistinctBy(n => n.Text))
        {
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.GraphNames, ReportValidationLevels.Warning, name.Line,
                $"`{name.Text}` no está en el grafo."));
        }
        AddCheck(validation, ReportValidationChecks.GraphNames, s.GraphNamesFound.Value, s.GraphNamesChecked.Value - s.GraphNamesFound.Value,
            $"{s.GraphNamesFound} de {s.GraphNamesChecked} en el grafo; {s.GraphNamesSkipped} sin comprobar porque su primer tramo no es un nodo.",
            notApplicable: s.GraphNamesChecked == 0);
    }

    /// <summary>
    /// Un informe en modo B (el plan del disco le eligió subsistemas) lleva un apartado «Cobertura» y nombra en él cada subsistema
    /// elegido, comparado sin distinguir mayúsculas. Solo avisa (el informe se escribió igual), y no hay comprobación, ni entrada en
    /// <see cref="ReportFileValidation.Checks"/>, si no es un informe del catálogo, no hay plan legible en la carpeta de informes (la
    /// preparación estaba apagada, o es un informe anterior) o el plan no eligió ninguno para él (degradó a un auditor normal). El plan
    /// del disco es el efectivo: <c>TimeBudgetStep</c> lo reescribe sin los subsistemas que recorta.
    /// </summary>
    private static void CheckCoverage(ParsedReport report, ReportFileValidation validation, ValidationContext context)
    {
        if (!ReportCatalog.TryGetByFileName(report.FileName, out var descriptor))
            return;
        if (context.CoveragePlan?.Reports.FirstOrDefault(r => r.Report == descriptor.Type) is not { Chosen.Count: > 0 } coverage)
            return;

        var before = validation.Issues.Count;
        var chosen = coverage.Chosen;
        if (CoverageSection.Find(report.Lines) is not { } section)
        {
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.Coverage, ReportValidationLevels.Warning, 1,
                $"El informe investigó {chosen.Count} subsistema(s) y no lleva el apartado «Cobertura» con lo cubierto, lo excluido y lo no cubierto."));
            AddCheck(validation, ReportValidationChecks.Coverage, 0, chosen.Count, "Sin apartado «Cobertura».", issuesFrom: before);
            return;
        }

        var text = string.Join('\n', report.Lines[(section.Heading + 1)..section.End]);
        var missing = chosen.Where(c => !text.Contains(c.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var subsystem in missing)
        {
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.Coverage, ReportValidationLevels.Warning, section.Heading + 1,
                $"El apartado «Cobertura» no nombra el subsistema «{subsystem.Name}», que el plan de cobertura eligió para este informe."));
        }
        AddCheck(validation, ReportValidationChecks.Coverage, chosen.Count - missing.Count, missing.Count,
            $"{chosen.Count - missing.Count} de {chosen.Count} subsistemas elegidos aparecen en el apartado «Cobertura».", issuesFrom: before);
    }

    private static void CheckMetadata(ParsedReport report, ReportFileValidation validation)
    {
        var s = validation.Scores;
        var before = validation.Issues.Count;
        var meta = ReportMarkdown.GetLastMetaLine(report.Content);
        s.MetaPresent = meta != null;
        if (meta == null)
        {
            s.MetaMissingKeys.AddRange(RequiredProvenanceKeys);
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.Metadata, ReportValidationLevels.Error, report.Lines.Length,
                "Falta la línea final `<!-- analysis-meta: ... -->`."));
        }
        else
        {
            var metaLine = report.LineOf(meta);
            var pairs = ParseMeta(meta);
            if (!pairs.TryGetValue("health", out var health) || !HealthValues.Contains(health, StringComparer.Ordinal))
            {
                validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.Metadata, ReportValidationLevels.Error, metaLine,
                    $"`health` tiene que ser Green, Amber o Red{(health == null ? " y no está" : $", y es `{health}`")}."));
            }

            s.MetaMissingKeys.AddRange(RequiredProvenanceKeys.Where(k => !pairs.ContainsKey(k)));
            if (s.MetaMissingKeys.Count > 0)
            {
                validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.Metadata, ReportValidationLevels.Error, metaLine,
                    $"Sin procedencia completa: faltan {string.Join(", ", s.MetaMissingKeys)} (el informe es anterior a la tanda B o no pasó por el pipeline)."));
            }

            if (report.LastNonEmptyLine() != metaLine)
            {
                validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.Metadata, ReportValidationLevels.Warning, metaLine,
                    "La línea `analysis-meta` no es la última del informe."));
            }
        }

        // Texto suelto antes del título (frases de las iteraciones con herramientas): el pipeline lo quita al escribir, pero un informe
        // anterior o editado a mano puede traerlo.
        if (ReportMarkdown.RemoveTextBeforeTitle(report.Content) is var withoutPreface && withoutPreface.Length != report.Content.Length)
        {
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.Metadata, ReportValidationLevels.Warning, 1,
                "El informe empieza con texto suelto antes del título de nivel 1."));
        }

        var failed = validation.Issues.Skip(before).Count(i => i.Level == ReportValidationLevels.Error);
        AddCheck(validation, ReportValidationChecks.Metadata, failed == 0 ? 1 : 0, failed,
            meta == null ? "Sin línea analysis-meta." : s.MetaMissingKeys.Count == 0 ? "Semáforo y procedencia presentes." : $"Faltan {string.Join(", ", s.MetaMissingKeys)}.",
            issuesFrom: before);
    }

    private static Dictionary<string, string> ParseMeta(string meta)
    {
        var body = meta[(meta.IndexOf(':') + 1)..];
        body = body[..body.LastIndexOf("-->", StringComparison.Ordinal)];
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0)
                pairs[part[..separator].Trim()] = part[(separator + 1)..].Trim();
        }
        return pairs;
    }

    private static void CheckMermaid(ParsedReport report, ReportFileValidation validation)
    {
        var s = validation.Scores;
        var before = validation.Issues.Count;
        s.MermaidBlocks = report.MermaidBlocks.Count;
        foreach (var (line, header) in report.MermaidBlocks)
        {
            if (MermaidProblem(header) is not { } problem)
                continue;
            if (problem.Level == ReportValidationLevels.Error)
                s.MermaidInvalid++;
            validation.Issues.Add(new ReportValidationIssue(ReportValidationChecks.Mermaid, problem.Level, line, problem.Message));
        }

        var failed = validation.Issues.Skip(before).Count(i => i.Level == ReportValidationLevels.Error);
        AddCheck(validation, ReportValidationChecks.Mermaid, s.MermaidBlocks - failed, failed,
            s.MermaidBlocks == 0 ? "Sin bloques Mermaid." : $"{s.MermaidBlocks - failed} de {s.MermaidBlocks} con un tipo permitido.",
            notApplicable: s.MermaidBlocks == 0, issuesFrom: before);
    }

    /// <summary>
    /// El problema del tipo de un diagrama, o null si es uno de <see cref="AllowedMermaidTypes"/>. Un tipo prohibido o
    /// desconocido es un error; <c>flowchart TB</c> o <c>graph TD</c>, que Mermaid acepta pero la guía no lista, es un aviso.
    /// </summary>
    internal static (string Level, string Message)? MermaidProblem(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return (ReportValidationLevels.Error, "Bloque Mermaid vacío.");

        var tokens = header.Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
        var type = tokens[0];
        if (type is "flowchart" or "graph")
        {
            var direction = tokens.Length > 1 ? tokens[1] : string.Empty;
            if (type == "flowchart" && direction is "TD" or "LR")
                return null;
            return (ReportValidationLevels.Warning,
                $"`{header.Trim()}` no está en la lista de `_mermaid_guidelines.md` (`flowchart TD` o `flowchart LR`).");
        }

        if (AllowedMermaidTypes.Contains(type, StringComparer.Ordinal))
            return null;
        if (ForbiddenMermaidTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            return (ReportValidationLevels.Error, $"`{type}` está prohibido en `_mermaid_guidelines.md`.");
        return (ReportValidationLevels.Error, $"`{type}` no es un tipo permitido por `_mermaid_guidelines.md`.");
    }

    private static void AddCheck(ReportFileValidation validation, string check, int passed, int failed, string? detail,
        bool notApplicable = false, bool skipped = false, int? issuesFrom = null)
    {
        var issues = issuesFrom is { } from ? validation.Issues.Skip(from) : validation.Issues.Where(i => i.Check == check);
        var levels = issues.Where(i => i.Check == check).Select(i => i.Level).ToList();
        var status = skipped ? ReportCheckStatuses.Skipped
            : levels.Contains(ReportValidationLevels.Error) ? ReportCheckStatuses.Fail
            : levels.Contains(ReportValidationLevels.Warning) ? ReportCheckStatuses.Warn
            : notApplicable ? ReportCheckStatuses.NotApplicable
            : ReportCheckStatuses.Pass;
        validation.Checks.Add(new ReportCheckResult(check, status, passed, failed, detail));
    }

    /// <summary>El Markdown de un informe, troceado una vez para todas las comprobaciones.</summary>
    private sealed class ParsedReport
    {
        public required string FileName { get; init; }
        public required string Content { get; init; }
        public required string[] Lines { get; init; }

        /// <summary>Spans de código en línea fuera de bloques cercados, con su línea (desde 1).</summary>
        public List<(int Line, string Text)> Spans { get; } = [];

        /// <summary>Primera línea de cada bloque Mermaid (desde 1) y su cabecera (la primera línea con contenido).</summary>
        public List<(int Line, string? Header)> MermaidBlocks { get; } = [];

        public List<(string Id, int Line, int? Priority, int Start, int End)> HeadingDefinitions { get; } = [];
        public List<(string Id, int Line)> IdMentions { get; } = [];
        public List<(string Id, string Prefix, int Line, bool InHeadingStart)> LooseIdMentions { get; } = [];
        public List<ReportMarkdown.MarkdownTable> Tables { get; private init; } = [];

        public static ParsedReport Parse(string fileName, string content)
        {
            var normalized = content.ReplaceLineEndings("\n");
            var report = new ParsedReport
            {
                FileName = fileName,
                Content = normalized,
                Lines = normalized.Split('\n'),
                Tables = ReportMarkdown.ParseTables(normalized)
            };
            report.Scan();
            return report;
        }

        private void Scan()
        {
            string? fence = null;
            List<string>? mermaid = null;
            var mermaidStart = 0;
            var boldCandidates = new List<(int Index, string Id, string Remainder)>();
            for (var i = 0; i < Lines.Length; i++)
            {
                var trimmed = Lines[i].TrimStart();
                var opener = FenceRegex().Match(trimmed);
                if (fence != null)
                {
                    if (opener.Success && opener.Groups["lang"].Length == 0 && trimmed.StartsWith(fence, StringComparison.Ordinal))
                    {
                        if (mermaid != null)
                            MermaidBlocks.Add((mermaidStart, MermaidHeader(mermaid)));
                        fence = null;
                        mermaid = null;
                    }
                    else
                    {
                        mermaid?.Add(Lines[i]);
                    }
                    continue;
                }

                if (opener.Success)
                {
                    fence = opener.Groups["fence"].Value;
                    if (opener.Groups["lang"].Value.Equals("mermaid", StringComparison.OrdinalIgnoreCase))
                    {
                        mermaid = [];
                        mermaidStart = i + 1;
                    }
                    continue;
                }

                foreach (Match m in InlineCodeRegex().Matches(Lines[i]))
                    Spans.Add((i + 1, m.Groups["code"].Value.Trim()));

                if (BoldDefinitionRegex().Match(Lines[i]) is { Success: true } bold)
                    boldCandidates.Add((i, bold.Groups["id"].Value, bold.Groups["rest"].Value));

                // Identificadores del texto, también dentro de spans de código.
                foreach (Match m in FindingIdRegex().Matches(Lines[i]))
                    IdMentions.Add((m.Groups["id"].Value, i + 1));
                var headingStart = MarkdownHeadings.TryParse(Lines[i], out _, out var headingText)
                    ? LeadingLooseIdRegex().Match(CleanHeading(headingText)).Groups["id"].Value
                    : string.Empty;
                foreach (Match m in LooseIdRegex().Matches(Lines[i]))
                    LooseIdMentions.Add((m.Groups["id"].Value, m.Groups["p"].Value, i + 1, m.Groups["id"].Value == headingStart));
            }

            // Un bloque sin cerrar llega hasta el final.
            if (mermaid != null)
                MermaidBlocks.Add((mermaidStart, MermaidHeader(mermaid)));

            var headings = MarkdownHeadings.Scan(Lines);
            for (var h = 0; h < headings.Count; h++)
            {
                var (index, level, text) = headings[h];
                var match = LeadingIdRegex().Match(CleanHeading(text));
                if (!match.Success)
                    continue;
                var end = headings.Skip(h + 1).FirstOrDefault(x => x.Level <= level).Text == null
                    ? Lines.Length
                    : headings.Skip(h + 1).First(x => x.Level <= level).Index;
                var rest = CleanHeading(text)[match.Length..];
                HeadingDefinitions.Add((match.Groups["id"].Value, index + 1, ReportMarkdown.MinSeverity(rest), index, end));
            }

            // Definiciones en negrita: el párrafo llega hasta la línea en blanco, el siguiente encabezado o la siguiente definición.
            var headingLines = headings.Select(h => h.Index).ToHashSet();
            var headingRanges = HeadingDefinitions.ToList();
            // Los identificadores que algo define con título: un encabezado o una negrita que lleva algo más que el identificador.
            var titledIds = headingRanges.Select(d => d.Id)
                .Concat(boldCandidates.Where(c => !IsBareReference(c.Remainder)).Select(c => c.Id))
                .ToHashSet(StringComparer.Ordinal);
            for (var b = 0; b < boldCandidates.Count; b++)
            {
                var (index, id, remainder) = boldCandidates[b];
                // Un elemento en negrita que repite un identificador ya definido por un encabezado (el plan de acción: «1. **03-CDM-001:**
                // retirar…») lo cita, no lo define otra vez.
                if (headingRanges.Any(d => d.Id == id))
                    continue;
                // Igual si la negrita es solo el identificador (con «:» o enlazado a otros: «**04-PRO-003 y 04-PRO-004:**», las viñetas de
                // la prioridad de ejecución) y otra negrita con título ya lo define, esté antes o después. Sin ninguna otra definición,
                // esa negrita sí es la definición.
                if (titledIds.Contains(id) && IsBareReference(remainder))
                    continue;
                var nextBold = b + 1 < boldCandidates.Count ? boldCandidates[b + 1].Index : Lines.Length;
                var end = index + 1;
                while (end < nextBold && end < Lines.Length && Lines[end].Trim().Length > 0 && !headingLines.Contains(end))
                    end++;
                HeadingDefinitions.Add((id, index + 1, ReportMarkdown.MinSeverity(remainder), index, end));
            }
        }

        /// <summary>
        /// La negrita de un elemento de lista que empieza por un identificador no lleva título: tras él solo hay puntuación y, a lo sumo,
        /// otros identificadores unidos por «y», «e», «o» (<c>**04-PRO-001:**</c>, <c>**04-PRO-003 y 04-PRO-004:**</c>). Una definición
        /// lleva su título en la negrita (<c>**04-PRO-001, alto, factura sin líneas:**</c>).
        /// </summary>
        private static bool IsBareReference(string remainder)
        {
            var words = FindingIdRegex().Replace(remainder, " ")
                .Split([' ', ',', ';', ':', '.', '·', '–', '—', '-', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return words.All(w => w.Equals("y", StringComparison.OrdinalIgnoreCase) || w.Equals("e", StringComparison.OrdinalIgnoreCase)
                || w.Equals("o", StringComparison.OrdinalIgnoreCase) || w.Equals("and", StringComparison.OrdinalIgnoreCase));
        }

        private static string CleanHeading(string text) => ReportMarkdown.CleanCell(text).TrimStart('[', ' ');

        /// <summary>La primera línea con contenido del bloque, sin comentarios <c>%%</c> ni cabecera YAML <c>---</c>.</summary>
        private static string? MermaidHeader(List<string> block)
        {
            var inFrontMatter = false;
            for (var i = 0; i < block.Count; i++)
            {
                var line = block[i].Trim();
                if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal))
                    continue;
                if (line == "---")
                {
                    inFrontMatter = !inFrontMatter;
                    continue;
                }
                if (!inFrontMatter)
                    return line;
            }
            return null;
        }

        /// <summary>Los identificadores que el informe define: por encabezado o en una tabla con columna de identificador.</summary>
        public HashSet<string> DefinedIds()
        {
            var ids = HeadingDefinitions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var table in Tables.Where(t => t.IdColumn >= 0))
            {
                foreach (var row in table.Rows)
                {
                    if (CellId(row, table.IdColumn) is { } id)
                        ids.Add(id);
                }
            }
            return ids;
        }

        private IEnumerable<(int Line, int? Priority)> Rows(string id)
        {
            foreach (var table in Tables.Where(t => t.IdColumn >= 0))
            {
                var severity = table.SeverityColumn;
                for (var r = 0; r < table.Rows.Count; r++)
                {
                    if (CellId(table.Rows[r], table.IdColumn) == id)
                        yield return (table.RowLines[r] + 1, severity >= 0 ? ReportMarkdown.MinSeverity(ReportMarkdown.Cell(table.Rows[r], severity)) : null);
                }
            }
        }

        public int FirstDefinitionLine(string id) =>
            HeadingDefinitions.Where(d => d.Id == id).Select(d => d.Line)
                .Concat(Rows(id).Select(r => r.Line))
                .DefaultIfEmpty(0).First();

        /// <summary>El nivel más grave que le dan su encabezado y sus filas con columna de severidad.</summary>
        public int? PriorityOf(string id) =>
            HeadingDefinitions.Where(d => d.Id == id).Select(d => d.Priority)
                .Concat(Rows(id).Select(r => r.Priority))
                .Where(p => p != null).Min();

        /// <summary>Líneas (desde 1) de las secciones del hallazgo y de sus filas.</summary>
        public HashSet<int> FindingLines(string id)
        {
            var lines = new HashSet<int>();
            foreach (var definition in HeadingDefinitions.Where(d => d.Id == id))
            {
                for (var i = definition.Start; i < definition.End; i++)
                    lines.Add(i + 1);
            }
            foreach (var (line, _) in Rows(id))
                lines.Add(line);
            return lines;
        }

        public string TextOf(HashSet<int> lines) => string.Join("\n", lines.Order().Where(l => l >= 1 && l <= Lines.Length).Select(l => Lines[l - 1]));

        public int LineOf(string fragment)
        {
            var index = Content.LastIndexOf(fragment, StringComparison.Ordinal);
            return index < 0 ? Lines.Length : Content.AsSpan(0, index).Count('\n') + 1;
        }

        public int LastNonEmptyLine()
        {
            for (var i = Lines.Length - 1; i >= 0; i--)
            {
                if (Lines[i].Trim().Length > 0)
                    return i + 1;
            }
            return 0;
        }
    }

    /// <summary>
    /// Los ficheros del repositorio que pueden citarse, por nombre, para resolver rutas incompletas por sufijo. Se recorre
    /// una vez por validación, sin seguir enlaces.
    /// </summary>
    private sealed class RepositoryFileIndex
    {
        private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules" };

        private readonly string _root;
        private readonly string _reportsPath;
        private readonly Dictionary<string, int?> _lineCounts = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>>? _byName;
        private Dictionary<string, string>? _routes;
        private List<(string Key, string File)>? _declaredResources;

        public RepositoryFileIndex(string root, string reportsPath)
        {
            _root = root;
            _reportsPath = reportsPath;
        }

        /// <summary>Rutas relativas (con <c>/</c>) por nombre de fichero, sin distinguir mayúsculas.</summary>
        private Dictionary<string, List<string>> ByName => _byName ??= Build();

        public bool HasFileNamed(string name) => ByName.ContainsKey(name);

        public (string Status, string? Relative, string? Full, bool BySuffix, IReadOnlyList<string> Candidates) Resolve(string raw)
        {
            var full = TurnEvidence.ToFullPath(_root, raw);
            if (full != null && File.Exists(full))
                return (CitationStatuses.Ok, Relative(full), full, false, []);

            // Un directorio existente no es una cita rota (`src/Orders.Core/`), aunque no se pueda abrir en una línea.
            if (full != null && TurnEvidence.IsInside(_root, full) && Directory.Exists(full))
                return (CitationStatuses.Ok, Relative(full), full, false, []);

            // Una URL declarada (`/listOrders.do` en un struts-config) es una ruta del sistema, no un fichero que falte.
            if (DeclaredRoute(raw) is { } declaredIn)
                return (CitationStatuses.Ok, declaredIn, null, false, []);

            var wanted = raw.Trim().Replace('\\', '/');
            if (full != null && TurnEvidence.IsInside(_root, full))
                wanted = Relative(full);
            wanted = wanted.TrimStart('.', '/');
            var name = wanted.Split('/')[^1];
            if (name.Length == 0 || !ByName.TryGetValue(name, out var files))
                return (CitationStatuses.Missing, null, null, false, []);

            var matches = files.Where(f => f.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count switch
            {
                0 => (CitationStatuses.Missing, null, null, false, []),
                1 => (CitationStatuses.Ok, matches[0], Path.Combine(_root, matches[0]), true, []),
                _ => (CitationStatuses.Ambiguous, null, null, false, [.. matches.Order(StringComparer.Ordinal).Take(MaxCandidates)])
            };
        }

        /// <summary>
        /// El fichero de configuración que declara la ruta <c>/x/y.do</c> (un <c>&lt;action path="/x/y"&gt;</c> de un
        /// <c>struts-config*.xml</c> del repositorio), o null si la ruta no es <c>.do</c> o nadie la declara.
        /// </summary>
        public string? DeclaredRoute(string raw)
        {
            var route = raw.Trim().Replace('\\', '/');
            if (!route.EndsWith(".do", StringComparison.OrdinalIgnoreCase))
                return null;
            route = "/" + route.TrimStart('.', '/');
            var routes = _routes ??= LoadRoutes();
            if (routes.TryGetValue(route, out var exact))
                return exact;
            // Con prefijo de módulo o de contexto (`/app/listOrders.do`), vale el sufijo.
            return routes.FirstOrDefault(r => route.EndsWith(r.Key, StringComparison.OrdinalIgnoreCase)).Value;
        }

        /// <summary>XML de configuración que se leen como mucho, y su tamaño máximo: el índice no debe tardar en un repositorio enorme.</summary>
        private const int MaxConfigFiles = 400;
        private const long MaxConfigBytes = 2 * 1024 * 1024;

        /// <summary>
        /// El fichero XML de configuración que declara el recurso <paramref name="raw"/> (<c>/WEB-INF/jsp/x.jsp</c>: un <c>path="..."</c> de un
        /// <c>forward</c>, un <c>&lt;location&gt;</c>...), o null si nadie lo declara. Casa la ruta entera o una cola de ella por tramos
        /// (la citada sin el prefijo de contexto, o con más). Se lee una vez, sin los comentarios XML, con tope de ficheros y de tamaño.
        /// </summary>
        public string? DeclaredResource(string raw)
        {
            var wanted = NormalizeResource(raw);
            if (wanted.Length == 0)
                return null;
            foreach (var (key, file) in _declaredResources ??= LoadDeclaredResources())
            {
                if (key.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                    || key.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase)
                    || wanted.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }

        private static string NormalizeResource(string raw) => raw.Trim().Replace('\\', '/').TrimStart('.', '/');

        private List<(string Key, string File)> LoadDeclaredResources()
        {
            var declared = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var read = 0;
            // En orden estable: que la configuración que se nombra no dependa de cómo enumere el disco.
            foreach (var relative in ByName.Where(e => e.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                         .SelectMany(e => e.Value).Order(StringComparer.Ordinal))
            {
                if (++read > MaxConfigFiles)
                    break;
                try
                {
                    var full = Path.Combine(_root, relative);
                    if (new FileInfo(full).Length > MaxConfigBytes)
                        continue;
                    var xml = XmlCommentRegex().Replace(File.ReadAllText(full), string.Empty);
                    foreach (Match m in XmlValueRegex().Matches(xml))
                    {
                        var value = (m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["c"].Value).Trim();
                        if (value.Length > 0 && !value.Contains("://", StringComparison.Ordinal) && ResourcePathRegex().IsMatch(value)
                            && NormalizeResource(value) is { Length: > 0 } key && seen.Add(key))
                            declared.Add((key, relative));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Una configuración ilegible no declara nada.
                }
            }
            return declared;
        }

        private Dictionary<string, string> LoadRoutes()
        {
            var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, paths) in ByName)
            {
                if (!name.StartsWith("struts-config", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var relative in paths)
                {
                    try
                    {
                        foreach (Match m in StrutsActionPathRegex().Matches(File.ReadAllText(Path.Combine(_root, relative))))
                        {
                            var path = m.Groups["path"].Value.Trim();
                            routes.TryAdd((path.StartsWith('/') ? path : "/" + path) + ".do", relative);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Un fichero ilegible no declara rutas.
                    }
                }
            }
            return routes;
        }

        public int? LineCount(string full)
        {
            if (_lineCounts.TryGetValue(full, out var cached))
                return cached;
            int? count = null;
            try
            {
                if (new FileInfo(full).Length <= MaxLineCountBytes)
                    count = File.ReadLines(full).Count();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _lineCounts[full] = count;
            return count;
        }

        private string Relative(string full) => Path.GetRelativePath(_root, full).Replace('\\', '/');

        private Dictionary<string, List<string>> Build()
        {
            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
            var pending = new Stack<string>();
            pending.Push(_root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                var isCodebase = Path.GetFileName(dir).Equals(KnowledgePaths.CodebaseFolderName, StringComparison.OrdinalIgnoreCase);
                try
                {
                    // De .codebase solo interesa access_export, adonde apuntan los nodos de Office: el resto son la BD, las
                    // copias de backups/ (que harían ambigua toda cita), el historial y el chat.
                    if (isCodebase)
                    {
                        var export = Path.Combine(dir, "access_export");
                        if (Directory.Exists(export))
                            pending.Push(export);
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(dir, "*", options))
                    {
                        var name = Path.GetFileName(file);
                        if (!byName.TryGetValue(name, out var list))
                            byName[name] = list = [];
                        list.Add(Relative(file));
                    }
                    foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                    {
                        if (!ExcludedDirectories.Contains(Path.GetFileName(sub))
                            && !Path.TrimEndingDirectorySeparator(sub).Equals(_reportsPath, StringComparison.OrdinalIgnoreCase))
                            pending.Push(sub);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Una carpeta ilegible se salta: sus ficheros quedan como no encontrados.
                }
            }
            return byName;
        }
    }

    /// <summary>
    /// Los nombres del grafo, para saber si un <c>Clase.metodo</c> citado existe: los nombres cualificados y sus sufijos de
    /// dos o más tramos (<c>Module.CGAR.MontarFiltro</c> da <c>CGAR.MontarFiltro</c>), y los nombres de nodo, que deciden si
    /// un nombre con puntos es del repositorio (<c>CGAR.x</c>) o de fuera (<c>ADODB.Connection</c>, <c>rs.Close</c>) y no se
    /// comprueba. Sin los nodos de proyecto, carpetas, ficheros ni conocimiento.
    /// </summary>
    private sealed class GraphNameIndex
    {
        /// <summary>Tramos finales que se indexan como sufijo; los nombres más largos solo casan con el cualificado completo.</summary>
        private const int MaxSuffixSegments = 4;

        private static readonly string[] ExcludedLabels = ["Project", "Folder", "File", "DocSection", "PromptTemplate", "UserDocument", "UserDocSection"];

        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _qualified = new(StringComparer.OrdinalIgnoreCase);

        public bool IsKnownOwner(string candidate) => _names.Contains(candidate.Split('.')[0]);

        public bool Contains(string candidate) => _qualified.Contains(candidate);

        public static GraphNameIndex? TryLoad(string? dbPath, out string? note)
        {
            note = null;
            if (dbPath == null)
                return null;
            if (!File.Exists(dbPath))
            {
                note = $"No existe la BD del grafo '{dbPath}': los nombres del grafo no se comprueban.";
                return null;
            }

            try
            {
                var index = new GraphNameIndex();
                var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
                using var conn = new SqliteConnection(connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT name, qualified_name FROM nodes WHERE label NOT IN ({string.Join(", ", ExcludedLabels.Select((_, i) => "@l" + i))})";
                for (var i = 0; i < ExcludedLabels.Length; i++)
                    cmd.Parameters.AddWithValue("@l" + i, ExcludedLabels[i]);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                        index._names.Add(reader.GetString(0));
                    if (reader.IsDBNull(1))
                        continue;
                    var qn = reader.GetString(1);
                    if (qn.StartsWith(Knowledge.Graph.KnowledgeGraphLabels.QualifiedNamePrefix, StringComparison.Ordinal))
                        continue;
                    index._qualified.Add(qn);
                    var segments = qn.Replace(':', '.').Split('.', StringSplitOptions.RemoveEmptyEntries);
                    for (var take = 2; take <= Math.Min(MaxSuffixSegments, segments.Length); take++)
                        index._qualified.Add(string.Join('.', segments[^take..]));
                }
                return index;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                note = $"No se pudo leer el grafo de '{dbPath}' ({ex.Message}): los nombres del grafo no se comprueban.";
                return null;
            }
        }
    }
}
