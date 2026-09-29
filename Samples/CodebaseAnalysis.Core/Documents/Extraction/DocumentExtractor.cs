using AngleSharp.Html.Parser;
using Microsoft.VisualBasic.FileIO;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CodebaseAnalysis.Core.Documents.Extraction;

/// <summary>Entrada de un extractor: el fichero temporal (OpenXml y PdfPig abren por ruta), su texto si es de texto y su familia.</summary>
public sealed record ExtractionInput(string FilePath, string? Text, DocumentTypeInfo Type, string Extension, string Title);

/// <summary>
/// Normaliza cualquier documento admitido a un único <c>content.md</c> con secciones (plan v2 §7.2, V2-D6). Un fallo no
/// lanza: devuelve <c>failed</c> con el motivo, porque el visor nativo del original puede servir igual.
/// </summary>
public static class DocumentExtractor
{
    /// <summary>Tope de <c>content.md</c>; lo que pase se trunca con aviso.</summary>
    public const int MaxContentChars = 2_000_000;

    /// <summary>Plazo de una extracción.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public const int MaxCsvRows = 5000;
    public const int MaxTableColumns = 50;

    public static Task<ExtractionResult> ExtractAsync(ExtractionInput input, CancellationToken cancellationToken) =>
        ExtractAsync(input, ExtractCore, Timeout, cancellationToken);

    /// <summary>
    /// Ejecuta <paramref name="extractor"/> con plazo. El plazo corta la espera aunque el extractor no mire el token
    /// (PdfPig u OpenXml dentro de una parte grande): la tarea sigue en segundo plano y su resultado se descarta, pero el
    /// llamante recibe <c>failed</c> y libera lo que tenga tomado. Los tests inyectan un extractor lento.
    /// </summary>
    internal static async Task<ExtractionResult> ExtractAsync(
        ExtractionInput input, Func<ExtractionInput, CancellationToken, ExtractionResult> extractor, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var result = await Task.Run(() => extractor(input, deadline.Token), deadline.Token).WaitAsync(deadline.Token);
            return Truncate(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ExtractionResult.Failed($"La extracción superó el plazo de {timeout.TotalSeconds:0.#} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ExtractionResult.Failed($"No se pudo extraer el texto: {ex.Message}");
        }
    }

    private static ExtractionResult ExtractCore(ExtractionInput input, CancellationToken ct)
    {
        var text = input.Text ?? string.Empty;
        return input.Type.Family switch
        {
            DocumentFamilies.Markdown => Markdown(Normalize(text), input.Title),
            DocumentFamilies.Text => Plain(Normalize(text)),
            DocumentFamilies.Code => Fenced(Normalize(text), LanguageOf(input.Extension), input.Title + input.Extension, []),
            DocumentFamilies.Structured => Structured(Normalize(text), input),
            DocumentFamilies.Csv => Csv(Normalize(text), input),
            DocumentFamilies.Html => Html(text, input.Title, ct),
            DocumentFamilies.Svg => Svg(text, input.Title),
            DocumentFamilies.Pdf => PdfExtractor.Extract(input.FilePath, input.Title, ct),
            DocumentFamilies.Docx => OfficeExtractors.Docx(input.FilePath, input.Title, ct),
            DocumentFamilies.Xlsx => OfficeExtractors.Xlsx(input.FilePath, input.Title, ct),
            DocumentFamilies.Pptx => OfficeExtractors.Pptx(input.FilePath, input.Title, ct),
            DocumentFamilies.Image => new ExtractionResult(string.Empty, [], ExtractionStatuses.PendingDescription, null, false, []),
            _ => ExtractionResult.Failed($"Familia '{input.Type.Family}' sin extractor.")
        };
    }

    /// <summary>Un Markdown ya normalizado: secciones por encabezados; sin texto, <c>empty</c>.</summary>
    public static ExtractionResult Markdown(string content, string title, IReadOnlyList<string>? warnings = null, int? pageCount = null)
    {
        content = content.Trim('\n');
        if (content.Trim().Length == 0)
            return new ExtractionResult(string.Empty, [], ExtractionStatuses.Empty, pageCount, false, warnings ?? []);
        content += "\n";
        return new ExtractionResult(content, MarkdownSections.Parse(content, title), ExtractionStatuses.Ok, pageCount, false, warnings ?? []);
    }

    private static ExtractionResult Plain(string content)
    {
        if (content.Trim().Length == 0)
            return new ExtractionResult(string.Empty, [], ExtractionStatuses.Empty, null, false, []);
        return new ExtractionResult(content, MarkdownSections.GroupParagraphs(content), ExtractionStatuses.Ok, null, false, []);
    }

    private static ExtractionResult Fenced(string content, string language, string heading, IReadOnlyList<string> warnings)
    {
        if (content.Trim().Length == 0)
            return new ExtractionResult(string.Empty, [], ExtractionStatuses.Empty, null, false, warnings);

        var fence = new string('`', Math.Max(3, LongestBacktickRun(content) + 1));
        var md = $"{fence}{language}\n{content.TrimEnd('\n')}\n{fence}\n";
        return new ExtractionResult(md, MarkdownSections.Whole(md, heading), ExtractionStatuses.Ok, null, false, warnings);
    }

    private static ExtractionResult Structured(string content, ExtractionInput input)
    {
        var name = input.Title + input.Extension;
        switch (input.Extension)
        {
            case ".json":
                try
                {
                    using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    var indented = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                    return Fenced(indented.Replace("\r\n", "\n"), "json", name, []);
                }
                catch (JsonException ex)
                {
                    return Fenced(content, "json", name, [$"JSON no válido ({ex.Message}): se muestra como texto."]);
                }

            case ".xml":
                try
                {
                    using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    while (reader.Read())
                    {
                    }
                    return Fenced(content, "xml", name, []);
                }
                catch (XmlException ex)
                {
                    return Fenced(content, "xml", name, [$"XML no válido ({ex.Message}): se muestra como texto."]);
                }

            default:
                return Fenced(content, "yaml", name, []);
        }
    }

    private static ExtractionResult Csv(string content, ExtractionInput input)
    {
        var firstLine = content.Split('\n', 2)[0];
        var delimiter = input.Extension == ".tsv"
            ? "\t"
            : new[] { ",", ";", "\t" }.OrderByDescending(d => firstLine.Count(c => c == d[0])).First();

        using var parser = new TextFieldParser(new StringReader(content))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(delimiter);

        var rows = new List<string[]>();
        var warnings = new List<string>();
        var truncatedColumns = false;
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? [];
            if (rows.Count > MaxCsvRows)
            {
                warnings.Add($"La tabla tiene más de {MaxCsvRows} filas: solo se incluyen las primeras.");
                break;
            }
            if (fields.Length > MaxTableColumns)
            {
                truncatedColumns = true;
                fields = fields[..MaxTableColumns];
            }
            rows.Add(fields);
        }
        if (truncatedColumns)
            warnings.Add($"La tabla tiene más de {MaxTableColumns} columnas: solo se incluyen las primeras.");
        if (rows.Count == 0)
            return new ExtractionResult(string.Empty, [], ExtractionStatuses.Empty, null, false, warnings);

        var md = MarkdownTable(rows[0], rows.Skip(1));
        return new ExtractionResult(md, MarkdownSections.Whole(md, input.Title), ExtractionStatuses.Ok, null, false, warnings);
    }

    /// <summary>Tabla GFM con cabecera; celdas con <c>|</c> escapado y sin saltos de línea.</summary>
    public static string MarkdownTable(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var width = Math.Max(1, header.Count);
        var list = rows.ToList();
        width = Math.Max(width, list.Count == 0 ? 0 : list.Max(r => r.Count));
        var sb = new StringBuilder();
        AppendRow(sb, header, width, headerRow: true);
        sb.Append('|').Append(string.Concat(Enumerable.Repeat("---|", width))).Append('\n');
        foreach (var row in list)
            AppendRow(sb, row, width, headerRow: false);
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells, int width, bool headerRow)
    {
        sb.Append('|');
        for (var i = 0; i < width; i++)
        {
            var cell = i < cells.Count ? cells[i] : string.Empty;
            if (headerRow && cell.Trim().Length == 0)
                cell = $"Columna {i + 1}";
            sb.Append(' ').Append(Cell(cell)).Append(" |");
        }
        sb.Append('\n');
    }

    public static string Cell(string value) => value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|").Trim();

    private static ExtractionResult Html(string text, string title, CancellationToken ct)
    {
        // AngleSharp y ReverseMarkdown no miran el token: se comprueba entre etapas.
        var document = new HtmlParser().ParseDocument(text);
        ct.ThrowIfCancellationRequested();
        foreach (var element in document.QuerySelectorAll("script, style, noscript, iframe, object, embed, template").ToList())
            element.Remove();
        ct.ThrowIfCancellationRequested();

        var html = document.Body?.InnerHtml ?? document.DocumentElement.OuterHtml;
        var config = new ReverseMarkdown.Config { GithubFlavored = true };
        config.Tags.Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass;
        config.Formatting.RemoveComments = true;
        config.Links.SmartHref = true;
        var converter = new ReverseMarkdown.Converter(config);
        var md = Normalize(converter.Convert(html));
        ct.ThrowIfCancellationRequested();
        var docTitle = string.IsNullOrWhiteSpace(document.Title) ? title : document.Title.Trim();
        return Markdown(md, docTitle);
    }

    private static ExtractionResult Svg(string text, string title)
    {
        XDocument doc;
        using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc = XDocument.Load(reader);

        var root = doc.Root!;
        string? First(string local) => root.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var svgTitle = First("title");
        var desc = First("desc");
        var texts = root.Descendants()
            .Where(e => e.Name.LocalName == "text")
            .Select(e => string.Join(" ", e.DescendantNodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0)))
            .Where(t => t.Length > 0)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("# ").Append(svgTitle ?? title).Append("\n\n");
        if (desc != null)
            sb.Append(desc).Append("\n\n");
        if (texts.Count > 0)
        {
            sb.Append("## Textos del dibujo\n\n");
            foreach (var t in texts)
                sb.Append("- ").Append(t).Append('\n');
        }

        return svgTitle == null && desc == null && texts.Count == 0
            ? new ExtractionResult(string.Empty, [], ExtractionStatuses.Empty, null, false, ["El SVG no tiene título, descripción ni textos."])
            : Markdown(sb.ToString(), title);
    }

    private static ExtractionResult Truncate(ExtractionResult result)
    {
        if (result.Content.Length <= MaxContentChars)
            return result;

        var content = result.Content[..MaxContentChars];
        var lines = content.Split('\n').Length;
        var sections = result.Sections
            .Where(s => s.StartLine <= lines)
            .Select(s => s with { EndLine = Math.Min(s.EndLine, lines) })
            .ToList();
        return result with
        {
            Content = content,
            Sections = sections,
            Truncated = true,
            Warnings = [.. result.Warnings, $"El texto extraído se ha truncado a {MaxContentChars} caracteres."]
        };
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static int LongestBacktickRun(string text)
    {
        int best = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return best;
    }

    /// <summary>Lenguaje del bloque cercado para el resaltado (nombres de highlight.js).</summary>
    public static string LanguageOf(string extension) => extension switch
    {
        ".cs" => "csharp",
        ".vb" or ".bas" or ".cls" or ".frm" => "vbnet",
        ".ts" => "typescript",
        ".tsx" => "tsx",
        ".js" or ".jsx" or ".mjs" => "javascript",
        ".py" => "python",
        ".java" => "java",
        ".kt" => "kotlin",
        ".go" => "go",
        ".rs" => "rust",
        ".c" or ".h" => "c",
        ".cpp" or ".hpp" => "cpp",
        ".sql" => "sql",
        ".ps1" or ".psm1" => "powershell",
        ".sh" => "bash",
        ".bat" => "dos",
        ".rb" => "ruby",
        ".php" => "php",
        ".swift" => "swift",
        ".scala" => "scala",
        ".css" => "css",
        ".scss" => "scss",
        ".razor" or ".cshtml" => "cshtml",
        ".xaml" or ".csproj" or ".props" or ".targets" => "xml",
        ".gradle" => "groovy",
        ".toml" => "toml",
        ".ini" or ".cfg" => "ini",
        _ => "text"
    };
}
