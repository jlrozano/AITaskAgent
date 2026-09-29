using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OfficeTools.Abstractions;
using OfficeTools.Models;

namespace OfficeTools.Extractors;

/// <summary>
/// Reader for .docx files that parses document structure and converts it to Markdown.
/// </summary>
public class DocxReader : IDocxReader
{
    private static readonly Regex HeadingRegex = new(@"^(?:Heading|Encabezado|Titre)\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    public Task<DocumentInfo> ReadDocumentAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadDocument(filePath));
    }

    /// <inheritdoc />
    public DocumentInfo ReadDocument(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must not be null or empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Word document file not found at: '{filePath}'.", filePath);

        var docInfo = new DocumentInfo
        {
            Name = Path.GetFileName(filePath),
            FilePath = Path.GetFullPath(filePath)
        };

        var markdownBuilder = new StringBuilder();
        var styleNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using (var wordDoc = WordprocessingDocument.Open(filePath, false))
        {
            var mainPart = wordDoc.MainDocumentPart;
            if (mainPart == null || mainPart.Document?.Body == null)
            {
                docInfo.MarkdownText = string.Empty;
                return docInfo;
            }

            // Map style IDs to style names if style definitions are present
            if (mainPart.StyleDefinitionsPart?.Styles != null)
            {
                foreach (var style in mainPart.StyleDefinitionsPart.Styles.Elements<Style>())
                {
                    var styleId = style.StyleId?.Value;
                    var styleName = style.StyleName?.Val?.Value;
                    if (!string.IsNullOrEmpty(styleId) && !string.IsNullOrEmpty(styleName))
                    {
                        styleNameMap[styleId] = styleName;
                    }
                }
            }

            var body = mainPart.Document.Body;
            var sections = new List<DocumentSection>();
            DocumentSection? currentSection = null;
            var currentSectionBuilder = new StringBuilder();

            int tableIndex = 0;

            foreach (var element in body.ChildElements)
            {
                if (element is Paragraph paragraph)
                {
                    string paragraphText = ExtractParagraphMarkdown(paragraph, styleNameMap, out int headingLevel, out bool isBullet);

                    if (string.IsNullOrWhiteSpace(paragraphText))
                        continue;

                    if (headingLevel > 0)
                    {
                        // Commit previous section if exists
                        if (currentSection != null)
                        {
                            currentSection.Content = currentSectionBuilder.ToString().Trim();
                            sections.Add(currentSection);
                            currentSectionBuilder.Clear();
                        }

                        string cleanTitle = paragraphText.TrimStart('#').Trim();
                        currentSection = new DocumentSection
                        {
                            Title = cleanTitle,
                            Level = headingLevel
                        };
                    }

                    markdownBuilder.AppendLine(paragraphText);
                    currentSectionBuilder.AppendLine(paragraphText);

                    // If it's not a bullet list, add spacing
                    if (!isBullet && headingLevel == 0)
                    {
                        markdownBuilder.AppendLine();
                        currentSectionBuilder.AppendLine();
                    }
                    else if (headingLevel > 0)
                    {
                        markdownBuilder.AppendLine();
                        currentSectionBuilder.AppendLine();
                    }
                }
                else if (element is Table table)
                {
                    tableIndex++;
                    var extractedTable = ExtractTable(table, tableIndex);
                    docInfo.Tables.Add(extractedTable);

                    string tableMarkdown = extractedTable.ToMarkdownTable();
                    markdownBuilder.AppendLine(tableMarkdown);
                    markdownBuilder.AppendLine();

                    currentSectionBuilder.AppendLine(tableMarkdown);
                    currentSectionBuilder.AppendLine();
                }
            }

            if (currentSection != null)
            {
                currentSection.Content = currentSectionBuilder.ToString().Trim();
                sections.Add(currentSection);
            }
            else if (currentSectionBuilder.Length > 0)
            {
                sections.Add(new DocumentSection
                {
                    Title = "Document",
                    Level = 0,
                    Content = currentSectionBuilder.ToString().Trim()
                });
            }

            docInfo.Sections = sections;
            docInfo.MarkdownText = markdownBuilder.ToString().Trim();
        }

        return docInfo;
    }

    /// <inheritdoc />
    public Task<string> ReadAsMarkdownAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadAsMarkdown(filePath));
    }

    /// <inheritdoc />
    public string ReadAsMarkdown(string filePath)
    {
        var doc = ReadDocument(filePath);
        return doc.MarkdownText;
    }

    private static string ExtractParagraphMarkdown(
        Paragraph paragraph,
        Dictionary<string, string> styleMap,
        out int headingLevel,
        out bool isBullet)
    {
        headingLevel = 0;
        isBullet = false;

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        string? resolvedStyleName = null;
        if (!string.IsNullOrEmpty(styleId))
        {
            styleMap.TryGetValue(styleId, out resolvedStyleName);
        }

        headingLevel = DetectHeadingLevel(styleId, resolvedStyleName);

        // Check for bullet or numbering
        int listLevel = 0;
        var numProps = paragraph.ParagraphProperties?.NumberingProperties;
        if (numProps != null)
        {
            isBullet = true;
            listLevel = numProps.NumberingLevelReference?.Val?.Value ?? 0;
        }
        else if (IsBulletStyle(styleId, resolvedStyleName))
        {
            isBullet = true;
        }

        var textBuilder = new StringBuilder();
        foreach (var run in paragraph.Elements<Run>())
        {
            string runText = ExtractRunFormattedText(run);
            textBuilder.Append(runText);
        }

        string rawText = textBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(rawText))
        {
            return string.Empty;
        }

        if (headingLevel > 0)
        {
            string prefix = new('#', headingLevel);
            return $"{prefix} {rawText}";
        }

        if (isBullet)
        {
            string indent = new(' ', Math.Max(0, listLevel) * 2);
            return $"{indent}- {rawText}";
        }

        return rawText;
    }

    private static string ExtractRunFormattedText(Run run)
    {
        var runTextBuilder = new StringBuilder();
        foreach (var text in run.Elements<Text>())
        {
            runTextBuilder.Append(text.Text);
        }

        string content = runTextBuilder.ToString();
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var runProps = run.RunProperties;
        if (runProps == null)
            return content;

        bool isBold = runProps.Bold != null && (runProps.Bold.Val == null || runProps.Bold.Val.Value);
        bool isItalic = runProps.Italic != null && (runProps.Italic.Val == null || runProps.Italic.Val.Value);

        if (!isBold && !isItalic)
            return content;

        // Preserve leading and trailing whitespaces outside markdown formatting tags
        string leadingSpaces = content[.. (content.Length - content.TrimStart().Length)];
        string trailingSpaces = content[(content.Length - (content.Length - content.TrimEnd().Length))..];
        string trimmed = content.Trim();

        if (string.IsNullOrEmpty(trimmed))
            return content;

        if (isBold && isItalic)
            return $"{leadingSpaces}***{trimmed}***{trailingSpaces}";
        if (isBold)
            return $"{leadingSpaces}**{trimmed}**{trailingSpaces}";
        if (isItalic)
            return $"{leadingSpaces}*{trimmed}*{trailingSpaces}";

        return content;
    }

    private static int DetectHeadingLevel(string? styleId, string? styleName)
    {
        if (string.Equals(styleId, "Title", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(styleName, "Title", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (!string.IsNullOrEmpty(styleId))
        {
            var match = HeadingRegex.Match(styleId);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int level))
            {
                return Math.Clamp(level, 1, 6);
            }
        }

        if (!string.IsNullOrEmpty(styleName))
        {
            var match = HeadingRegex.Match(styleName);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int level))
            {
                return Math.Clamp(level, 1, 6);
            }
        }

        return 0;
    }

    private static bool IsBulletStyle(string? styleId, string? styleName)
    {
        if (!string.IsNullOrEmpty(styleId) &&
            (styleId.Contains("List", StringComparison.OrdinalIgnoreCase) ||
             styleId.Contains("Bullet", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(styleName) &&
            (styleName.Contains("List", StringComparison.OrdinalIgnoreCase) ||
             styleName.Contains("Bullet", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static DocumentTable ExtractTable(Table table, int tableIndex)
    {
        var extractedTable = new DocumentTable
        {
            TableIndex = tableIndex
        };

        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0)
            return extractedTable;

        bool isFirstRow = true;

        foreach (var row in rows)
        {
            var rowCells = new List<string>();
            foreach (var cell in row.Elements<TableCell>())
            {
                var cellTexts = cell.Descendants<Text>().Select(t => t.Text);
                string cellText = string.Join(" ", cellTexts).Trim();
                cellText = cellText.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
                rowCells.Add(cellText);
            }

            if (isFirstRow)
            {
                extractedTable.Headers = rowCells;
                isFirstRow = false;
            }
            else
            {
                extractedTable.Rows.Add(rowCells);
            }
        }

        return extractedTable;
    }
}
