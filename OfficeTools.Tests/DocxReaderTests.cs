using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OfficeTools.Extractors;
using OfficeTools.Models;

namespace OfficeTools.Tests;

public class DocxReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _testDocxPath;

    public DocxReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DocxReaderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _testDocxPath = Path.Combine(_tempDir, "sample_document.docx");
        CreateSampleDocx(_testDocxPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static void CreateSampleDocx(string filePath)
    {
        using var wordDoc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
        var mainPart = wordDoc.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = mainPart.Document.AppendChild(new Body());

        // 1. Title / Heading 1
        var headingP = body.AppendChild(new Paragraph());
        headingP.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" });
        headingP.AppendChild(new Run(new Text("Titulo")));

        // 2. Normal paragraph
        var p = body.AppendChild(new Paragraph());
        p.AppendChild(new Run(new Text("Este es un párrafo de prueba.")));

        // 3. Bullet list item
        var bulletP = body.AppendChild(new Paragraph());
        bulletP.ParagraphProperties = new ParagraphProperties(
            new NumberingProperties(
                new NumberingLevelReference { Val = 0 },
                new NumberingId { Val = 1 }
            )
        );
        bulletP.AppendChild(new Run(new Text("Elemento de lista")));

        // 4. Table 2x2
        var table = body.AppendChild(new Table());

        // Header row
        var row1 = table.AppendChild(new TableRow());
        row1.AppendChild(new TableCell(new Paragraph(new Run(new Text("Columna 1")))));
        row1.AppendChild(new TableCell(new Paragraph(new Run(new Text("Columna 2")))));

        // Data row
        var row2 = table.AppendChild(new TableRow());
        row2.AppendChild(new TableCell(new Paragraph(new Run(new Text("Dato A")))));
        row2.AppendChild(new TableCell(new Paragraph(new Run(new Text("Dato B")))));

        mainPart.Document.Save();
    }

    [Fact]
    public async Task ReadDocumentAsync_ParsesStructureCorrectly()
    {
        var reader = new DocxReader();

        var docInfo = await reader.ReadDocumentAsync(_testDocxPath);

        Assert.NotNull(docInfo);
        Assert.Equal("sample_document.docx", docInfo.Name);
        Assert.Equal(Path.GetFullPath(_testDocxPath), docInfo.FilePath);

        // Sections
        Assert.NotEmpty(docInfo.Sections);
        var headingSection = docInfo.Sections.FirstOrDefault(s => s.Title == "Titulo");
        Assert.NotNull(headingSection);
        Assert.Equal(1, headingSection.Level);

        // Tables
        Assert.Single(docInfo.Tables);
        var table = docInfo.Tables[0];
        Assert.Equal(2, table.Headers.Count);
        Assert.Equal("Columna 1", table.Headers[0]);
        Assert.Equal("Columna 2", table.Headers[1]);
        Assert.Single(table.Rows);
        Assert.Equal("Dato A", table.Rows[0][0]);
        Assert.Equal("Dato B", table.Rows[0][1]);

        // Markdown text
        Assert.Contains("# Titulo", docInfo.MarkdownText);
        Assert.Contains("Este es un párrafo de prueba.", docInfo.MarkdownText);
        Assert.Contains("- Elemento de lista", docInfo.MarkdownText);
        Assert.Contains("| Columna 1 | Columna 2 |", docInfo.MarkdownText);
        Assert.Contains("| Dato A | Dato B |", docInfo.MarkdownText);
    }

    [Fact]
    public async Task ReadAsMarkdownAsync_ReturnsExpectedMarkdownString()
    {
        var reader = new DocxReader();

        string markdown = await reader.ReadAsMarkdownAsync(_testDocxPath);

        Assert.NotNull(markdown);
        Assert.Contains("# Titulo", markdown);
        Assert.Contains("Este es un párrafo de prueba.", markdown);
        Assert.Contains("- Elemento de lista", markdown);
        Assert.Contains("| Columna 1 | Columna 2 |", markdown);
        Assert.Contains("| --- | --- |", markdown);
        Assert.Contains("| Dato A | Dato B |", markdown);
    }

    [Fact]
    public void ReadDocument_NonExistentFile_ThrowsFileNotFoundException()
    {
        var reader = new DocxReader();
        var nonExistentPath = Path.Combine(_tempDir, "does_not_exist.docx");

        Assert.Throws<FileNotFoundException>(() => reader.ReadDocument(nonExistentPath));
    }

    [Fact]
    public void ReadDocument_NullOrWhitespace_ThrowsArgumentException()
    {
        var reader = new DocxReader();

        Assert.Throws<ArgumentException>(() => reader.ReadDocument(string.Empty));
        Assert.Throws<ArgumentException>(() => reader.ReadDocument("   "));
    }
}
