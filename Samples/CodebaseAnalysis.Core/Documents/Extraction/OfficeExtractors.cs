using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using OfficeTools.Extractors;
using System.Globalization;
using System.Text;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace CodebaseAnalysis.Core.Documents.Extraction;

/// <summary>
/// DOCX, XLSX y PPTX a Markdown (plan v2 §7.2). DOCX con <see cref="DocxReader"/> de OfficeTools; XLSX y PPTX con
/// DocumentFormat.OpenXml directamente, leyendo solo las partes que hacen falta (no dependen de maestros ni diseños).
/// </summary>
public static class OfficeExtractors
{
    public const int MaxSheetRows = 1000;

    /// <summary>Formatos de número integrados de Excel que son fechas u horas (ECMA-376, 18.8.30).</summary>
    private static readonly HashSet<uint> BuiltInDateFormats = [14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47];

    /// <summary>
    /// <see cref="DocxReader"/> no admite token: se mira antes y después, y el plazo de
    /// <see cref="DocumentExtractor.ExtractAsync(ExtractionInput, CancellationToken)"/> corta la espera si se queda dentro.
    /// </summary>
    public static ExtractionResult Docx(string path, string title, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var markdown = new DocxReader().ReadAsMarkdown(path);
        ct.ThrowIfCancellationRequested();
        return DocumentExtractor.Markdown(markdown.Replace("\r\n", "\n"), title);
    }

    public static ExtractionResult Xlsx(string path, string title, CancellationToken ct)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("El XLSX no tiene libro.");
        var shared = ReadSharedStrings(workbook.SharedStringTablePart, ct);
        var dateStyles = DateStyleIndexes(workbook);

        var sb = new StringBuilder();
        var warnings = new List<string>();
        var sheets = workbook.Workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();
            sb.Append("## Hoja: ").Append(sheet.Name?.Value ?? "(sin nombre)").Append("\n\n");
            if (sheet.Id?.Value is not { } relId || workbook.GetPartById(relId) is not WorksheetPart part)
                continue;

            var rows = new List<string[]>();
            var truncatedColumns = false;
            foreach (var row in ReadRows(part, ct))
            {
                if (rows.Count > MaxSheetRows)
                {
                    warnings.Add($"La hoja '{sheet.Name}' tiene más de {MaxSheetRows} filas: solo se incluyen las primeras.");
                    break;
                }

                var cells = new SortedDictionary<int, string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = ColumnIndex(cell.CellReference?.Value) ?? cells.Count;
                    if (column >= DocumentExtractor.MaxTableColumns)
                    {
                        truncatedColumns = true;
                        continue;
                    }
                    cells[column] = CellText(cell, shared, dateStyles);
                }

                if (cells.Count == 0 || cells.Values.All(v => v.Length == 0))
                    continue;
                var dense = new string[cells.Keys.Max() + 1];
                for (var i = 0; i < dense.Length; i++)
                    dense[i] = cells.TryGetValue(i, out var v) ? v : string.Empty;
                rows.Add(dense);
            }

            if (truncatedColumns)
                warnings.Add($"La hoja '{sheet.Name}' tiene más de {DocumentExtractor.MaxTableColumns} columnas: solo se incluyen las primeras.");
            if (rows.Count == 0)
            {
                sb.Append("(hoja vacía)\n\n");
                continue;
            }

            sb.Append(DocumentExtractor.MarkdownTable(rows[0], rows.Skip(1))).Append('\n');
        }

        return DocumentExtractor.Markdown(sb.ToString(), title, warnings, sheets.Count);
    }

    /// <summary>
    /// Cadenas compartidas en SAX: <c>OpenXmlReader</c> carga cada <c>&lt;si&gt;</c> por separado y no el DOM de la
    /// tabla entera.
    /// </summary>
    private static List<string> ReadSharedStrings(SharedStringTablePart? part, CancellationToken ct)
    {
        var result = new List<string>();
        if (part == null)
            return result;

        using var reader = DocumentFormat.OpenXml.OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(SharedStringItem) || !reader.IsStartElement)
                continue;
            if (result.Count % 1000 == 0)
                ct.ThrowIfCancellationRequested();
            result.Add(reader.LoadCurrentElement()?.InnerText ?? string.Empty);
        }
        return result;
    }

    /// <summary>
    /// Filas de la hoja en SAX: cada <c>&lt;row&gt;</c> se materializa cuando se pide, así que cortar en
    /// <see cref="MaxSheetRows"/> deja sin leer ni cargar el resto de la hoja.
    /// </summary>
    private static IEnumerable<Row> ReadRows(WorksheetPart part, CancellationToken ct)
    {
        using var reader = DocumentFormat.OpenXml.OpenXmlReader.Create(part);
        var count = 0;
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                continue;
            if (++count % 200 == 0)
                ct.ThrowIfCancellationRequested();
            if (reader.LoadCurrentElement() is Row row)
                yield return row;
        }
    }

    public static ExtractionResult Pptx(string path, string title, CancellationToken ct)
    {
        using var document = PresentationDocument.Open(path, false);
        var presentation = document.PresentationPart ?? throw new InvalidDataException("El PPTX no tiene presentación.");
        var slideIds = presentation.Presentation.SlideIdList?.Elements<P.SlideId>().ToList() ?? [];

        var sb = new StringBuilder();
        var number = 0;
        foreach (var slideId in slideIds)
        {
            ct.ThrowIfCancellationRequested();
            number++;
            if (slideId.RelationshipId?.Value is not { } relId || presentation.GetPartById(relId) is not SlidePart slide)
                continue;

            var shapes = slide.Slide.Descendants<P.Shape>().ToList();
            var titleShape = shapes.FirstOrDefault(s => PlaceholderType(s) is { } t && (t == P.PlaceholderValues.Title || t == P.PlaceholderValues.CenteredTitle));
            var slideTitle = titleShape == null ? null : ShapeText(titleShape).FirstOrDefault();
            sb.Append("## Diapositiva ").Append(number);
            if (!string.IsNullOrWhiteSpace(slideTitle))
                sb.Append(" — ").Append(slideTitle);
            sb.Append("\n\n");

            foreach (var shape in shapes.Where(s => !ReferenceEquals(s, titleShape)))
            {
                foreach (var paragraph in ShapeText(shape))
                    sb.Append(paragraph).Append("\n\n");
            }

            var notes = slide.NotesSlidePart?.NotesSlide.Descendants<P.Shape>()
                .Where(s => PlaceholderType(s) is not { } t || t == P.PlaceholderValues.Body)
                .SelectMany(ShapeText)
                .ToList() ?? [];
            if (notes.Count > 0)
                sb.Append("> Notas: ").Append(string.Join(" ", notes)).Append("\n\n");
        }

        return DocumentExtractor.Markdown(sb.ToString(), title, null, slideIds.Count);
    }

    private static P.PlaceholderValues? PlaceholderType(P.Shape shape) =>
        shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;

    private static IEnumerable<string> ShapeText(P.Shape shape) =>
        shape.TextBody?.Elements<A.Paragraph>()
            .Select(p => string.Concat(p.Descendants<A.Text>().Select(t => t.Text)).Trim())
            .Where(t => t.Length > 0)
        ?? [];

    private static HashSet<uint> DateStyleIndexes(WorkbookPart workbook)
    {
        var result = new HashSet<uint>();
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        var formats = stylesheet?.CellFormats?.Elements<CellFormat>().ToList();
        if (formats == null)
            return result;

        var customDates = stylesheet!.NumberingFormats?.Elements<NumberingFormat>()
            .Where(f => f.NumberFormatId?.Value is not null && IsDateFormatCode(f.FormatCode?.Value))
            .Select(f => f.NumberFormatId!.Value)
            .ToHashSet() ?? [];

        for (var i = 0; i < formats.Count; i++)
        {
            var id = formats[i].NumberFormatId?.Value ?? 0;
            if (BuiltInDateFormats.Contains(id) || customDates.Contains(id))
                result.Add((uint)i);
        }
        return result;
    }

    private static bool IsDateFormatCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return false;
        var withoutLiterals = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]", string.Empty);
        return withoutLiterals.IndexOfAny(['d', 'm', 'y', 'h', 's', 'D', 'M', 'Y', 'H', 'S']) >= 0;
    }

    private static string CellText(Cell cell, List<string> shared, HashSet<uint> dateStyles)
    {
        var raw = cell.CellValue?.Text ?? cell.InnerText;
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
            return int.TryParse(raw, out var index) && index >= 0 && index < shared.Count ? shared[index] : raw;
        if (type == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? raw;
        if (type == CellValues.Boolean)
            return raw == "1" ? "VERDADERO" : "FALSO";
        if (type == CellValues.String || type == CellValues.Error)
            return cell.CellValue?.Text ?? string.Empty;

        var value = cell.CellValue?.Text ?? string.Empty;
        if (cell.StyleIndex?.Value is { } style && dateStyles.Contains(style)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            try
            {
                var date = DateTime.FromOADate(serial);
                return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
            }
        }
        return value;
    }

    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return null;
        var index = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z')
            {
                index = index * 26 + (c - 'A' + 1);
                letters++;
            }
            else
            {
                break;
            }
        }
        return letters == 0 ? null : index - 1;
    }
}
