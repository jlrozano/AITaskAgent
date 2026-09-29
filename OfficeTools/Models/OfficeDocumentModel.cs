namespace OfficeTools.Models;

/// <summary>
/// Represents structured information extracted from an office document (.docx).
/// </summary>
public class DocumentInfo
{
    /// <summary>
    /// Document file name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the document file.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Document contents converted to structured Markdown text.
    /// </summary>
    public string MarkdownText { get; set; } = string.Empty;

    /// <summary>
    /// Document sections organized by headings.
    /// </summary>
    public List<DocumentSection> Sections { get; set; } = new();

    /// <summary>
    /// Extracted tables from the document.
    /// </summary>
    public List<DocumentTable> Tables { get; set; } = new();
}

/// <summary>
/// Represents a section within the document identified by a heading.
/// </summary>
public class DocumentSection
{
    /// <summary>
    /// Section title / heading text.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Heading level (1 for Heading 1, 2 for Heading 2, etc.).
    /// </summary>
    public int Level { get; set; } = 1;

    /// <summary>
    /// Markdown content belonging to this section.
    /// </summary>
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Represents a table extracted from the document.
/// </summary>
public class DocumentTable
{
    /// <summary>
    /// Sequential index of the table within the document.
    /// </summary>
    public int TableIndex { get; set; }

    /// <summary>
    /// Table header column names.
    /// </summary>
    public List<string> Headers { get; set; } = new();

    /// <summary>
    /// Rows of cell values.
    /// </summary>
    public List<List<string>> Rows { get; set; } = new();

    /// <summary>
    /// Converts this table into Markdown table representation.
    /// </summary>
    public string ToMarkdownTable()
    {
        var sb = new System.Text.StringBuilder();

        if (Headers.Count > 0)
        {
            sb.Append("| ").Append(string.Join(" | ", Headers.Select(h => h.Replace("|", "\\|")))).AppendLine(" |");
            sb.Append("| ").Append(string.Join(" | ", Headers.Select(_ => "---"))).AppendLine(" |");
        }
        else if (Rows.Count > 0 && Rows[0].Count > 0)
        {
            int colCount = Rows[0].Count;
            sb.Append("| ").Append(string.Join(" | ", Enumerable.Range(1, colCount).Select(i => $"Col {i}"))).AppendLine(" |");
            sb.Append("| ").Append(string.Join(" | ", Enumerable.Range(1, colCount).Select(_ => "---"))).AppendLine(" |");
        }

        foreach (var row in Rows)
        {
            sb.Append("| ").Append(string.Join(" | ", row.Select(c => c.Replace("|", "\\|")))).AppendLine(" |");
        }

        return sb.ToString();
    }
}
