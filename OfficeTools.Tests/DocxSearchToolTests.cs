using System.Reflection;
using AITaskAgent.Core.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeTools.Extractors;
using OfficeTools.Tools;

namespace OfficeTools.Tests;

public class DocxSearchToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _testDocxPath;

    public DocxSearchToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DocxSearchToolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _testDocxPath = Path.Combine(_tempDir, "search_sample.docx");
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

        // Heading 1
        var heading = body.AppendChild(new Paragraph());
        heading.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" });
        heading.AppendChild(new Run(new Text("Especificación del Proyecto")));

        // Paragraph
        var p = body.AppendChild(new Paragraph());
        p.AppendChild(new Run(new Text("Este documento describe la arquitectura modular del sistema.")));

        // Table 2x2
        var table = body.AppendChild(new Table());
        var row1 = table.AppendChild(new TableRow());
        row1.AppendChild(new TableCell(new Paragraph(new Run(new Text("Componente")))));
        row1.AppendChild(new TableCell(new Paragraph(new Run(new Text("Estado")))));

        var row2 = table.AppendChild(new TableRow());
        row2.AppendChild(new TableCell(new Paragraph(new Run(new Text("OfficeTools")))));
        row2.AppendChild(new TableCell(new Paragraph(new Run(new Text("Activo")))));

        mainPart.Document.Save();
    }

    private static async Task<string> CallInternalExecuteAsync(
        DocxSearchTool tool,
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var method = typeof(DocxSearchTool).GetMethod(
            "InternalExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("InternalExecuteAsync method not found on DocxSearchTool.");

        var task = (Task<string>)method.Invoke(tool, new object[] { argumentsJson, context, logger, cancellationToken })!;
        return await task;
    }

    [Fact]
    public async Task InternalExecuteAsync_WhenMatchExists_ReturnsMatchingContent()
    {
        var tool = new DocxSearchTool(new DocxReader());
        var context = new PipelineContext();
        var escapedPath = _testDocxPath.Replace("\\", "\\\\");
        var args = $$"""{"file_path": "{{escapedPath}}", "query": "arquitectura"}""";

        string result = await CallInternalExecuteAsync(tool, args, context, NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Contains("Resultados de Búsqueda Docx para: \"arquitectura\"", result);
        Assert.Contains("search_sample.docx", result);
        Assert.Contains("Especificación del Proyecto", result);
        Assert.Contains("arquitectura modular", result);
    }

    [Fact]
    public async Task InternalExecuteAsync_WhenTableMatches_ReturnsTableData()
    {
        var tool = new DocxSearchTool(new DocxReader());
        var context = new PipelineContext();
        var escapedPath = _testDocxPath.Replace("\\", "\\\\");
        var args = $$"""{"file_path": "{{escapedPath}}", "query": "OfficeTools"}""";

        string result = await CallInternalExecuteAsync(tool, args, context, NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Contains("Tablas Coincidentes", result);
        Assert.Contains("| Componente | Estado |", result);
        Assert.Contains("| OfficeTools | Activo |", result);
    }

    [Fact]
    public async Task InternalExecuteAsync_WhenNoMatch_ReturnsNoMatchesMessage()
    {
        var tool = new DocxSearchTool(new DocxReader());
        var context = new PipelineContext();
        var escapedPath = _testDocxPath.Replace("\\", "\\\\");
        var args = $$"""{"file_path": "{{escapedPath}}", "query": "termino_inexistente_xyz_123"}""";

        string result = await CallInternalExecuteAsync(tool, args, context, NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Contains("No se encontraron coincidencias para el término 'termino_inexistente_xyz_123'", result);
    }

    [Fact]
    public async Task InternalExecuteAsync_WithHeadingFilter_FiltersProperly()
    {
        var tool = new DocxSearchTool(new DocxReader());
        var context = new PipelineContext();
        var escapedPath = _testDocxPath.Replace("\\", "\\\\");

        // Matching heading filter
        var matchingArgs = $$"""{"file_path": "{{escapedPath}}", "query": "modular", "heading": "Especificación"}""";
        string matchingResult = await CallInternalExecuteAsync(tool, matchingArgs, context, NullLogger.Instance);
        Assert.Contains("Especificación del Proyecto", matchingResult);
        Assert.Contains("modular", matchingResult);

        // Non-matching heading filter
        var nonMatchingArgs = $$"""{"file_path": "{{escapedPath}}", "query": "modular", "heading": "Sección Inexistente"}""";
        string nonMatchingResult = await CallInternalExecuteAsync(tool, nonMatchingArgs, context, NullLogger.Instance);
        Assert.Contains("No se encontraron coincidencias", nonMatchingResult);
        Assert.Contains("Sección Inexistente", nonMatchingResult);
    }

    [Fact]
    public async Task ExecuteAsync_ViaPublicPipeline_ExecutesSuccessfully()
    {
        var tool = new DocxSearchTool(new DocxReader());
        var context = new PipelineContext();
        var escapedPath = _testDocxPath.Replace("\\", "\\\\");
        var args = $$"""{"file_path": "{{escapedPath}}", "query": "sistema"}""";

        string result = await tool.ExecuteAsync(args, context, "test_step", NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Contains("search_sample.docx", result);
        Assert.Contains("sistema", result);
    }

    [Fact]
    public async Task InternalExecuteAsync_MissingQuery_ReturnsToolError()
    {
        var tool = new DocxSearchTool(new DocxReader());
        var context = new PipelineContext();
        var args = """{"file_path": "some_file.docx"}""";

        string result = await CallInternalExecuteAsync(tool, args, context, NullLogger.Instance);

        Assert.Contains("TOOL_ERROR", result);
        Assert.Contains("query", result);
    }
}
