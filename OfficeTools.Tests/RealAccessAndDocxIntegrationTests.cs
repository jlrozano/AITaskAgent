using OfficeTools.Extractors;
using OfficeTools.Tools;
using Xunit.Abstractions;

namespace OfficeTools.Tests;

public class RealAccessAndDocxIntegrationTests
{
    private readonly ITestOutputHelper _output;
    // Carpeta con un .accdb y documentos .docx reales, fuera del repositorio. Sin la variable de
    // entorno las pruebas se omiten: no hay rutas de una máquina concreta en el código.
    private static readonly string TargetDir = Environment.GetEnvironmentVariable("OFFICETOOLS_SAMPLE_DIR") ?? string.Empty;
    private static readonly string AccdbPath = Directory.Exists(TargetDir)
        ? Directory.GetFiles(TargetDir, "*.accdb").FirstOrDefault() ?? string.Empty
        : string.Empty;

    public RealAccessAndDocxIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ExtractSchema_FromRealAccdb_ExtractsTablesColumnsAndQueries()
    {
        if (!File.Exists(AccdbPath))
        {
            _output.WriteLine($"El archivo {AccdbPath} no existe. Omitiendo prueba real.");
            return;
        }

        var extractor = new AccessSchemaExtractor();
        
        var canConnect = extractor.CanConnect(AccdbPath, null, out var error);
        _output.WriteLine($"CanConnect: {canConnect}, Error: {error ?? "ninguno"}");
        Assert.True(canConnect, $"No se pudo conectar a la base Access: {error}");

        var schema = await extractor.ExtractSchemaAsync(AccdbPath);

        _output.WriteLine($"=== Esquema extraído de {schema.DatabaseName} ===");
        _output.WriteLine($"Total tablas: {schema.Tables.Count}");
        _output.WriteLine($"Total consultas: {schema.Queries.Count}");
        _output.WriteLine($"Total claves foráneas: {schema.ForeignKeys.Count}");

        foreach (var table in schema.Tables.Take(15))
        {
            _output.WriteLine($"  - Tabla: {table.Name} ({table.Columns.Count} columnas, PKs: {string.Join(", ", table.PrimaryKeys)})");
        }

        foreach (var query in schema.Queries.Take(10))
        {
            _output.WriteLine($"  - Consulta: {query.Name} (SQL len: {query.Sql?.Length ?? 0})");
        }

        Assert.NotEmpty(schema.Tables);
    }

    [Fact]
    public async Task AccessSchemaTool_WithRealAccdb_ReturnsFormattedMarkdown()
    {
        if (!File.Exists(AccdbPath))
        {
            return;
        }

        var extractor = new AccessSchemaExtractor();
        var tool = new AccessSchemaTool(extractor, AccdbPath);
        
        // 1. Resumen general de tablas
        var summaryArgs = $"{{\"database_path\": \"{AccdbPath.Replace('\\', '/')}\"}}";
        var summaryResult = await tool.ExecuteAsync(summaryArgs, new AITaskAgent.Core.Models.PipelineContext(), "test", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        _output.WriteLine("=== Resultado de AccessSchemaTool (Resumen) ===");
        _output.WriteLine(summaryResult.Substring(0, Math.Min(summaryResult.Length, 1000)));

        Assert.Contains("# Esquema de Base de Datos Access", summaryResult);

        // 2. Extraer el nombre de la primera tabla y pedir detalle
        var schema = await extractor.ExtractSchemaAsync(AccdbPath);
        var firstTable = schema.Tables.FirstOrDefault();

        if (firstTable != null)
        {
            var detailArgs = $"{{\"database_path\": \"{AccdbPath.Replace('\\', '/')}\", \"table_name\": \"{firstTable.Name}\"}}";
            var detailResult = await tool.ExecuteAsync(detailArgs, new AITaskAgent.Core.Models.PipelineContext(), "test", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            _output.WriteLine($"=== Detalle de tabla '{firstTable.Name}' ===");
            _output.WriteLine(detailResult.Substring(0, Math.Min(detailResult.Length, 1000)));
            Assert.Contains(firstTable.Name, detailResult);
        }
    }

    [Fact]
    public async Task DocxReader_WithRealDocxFiles_ExtractsMarkdown()
    {
        if (!Directory.Exists(TargetDir)) return;

        var docxFiles = Directory.GetFiles(TargetDir, "*.docx");
        _output.WriteLine($"Archivos .docx encontrados: {docxFiles.Length}");

        var reader = new DocxReader();

        foreach (var docxPath in docxFiles)
        {
            var docInfo = await reader.ReadDocumentAsync(docxPath);
            _output.WriteLine($"=== Leído: {Path.GetFileName(docxPath)} ===");
            _output.WriteLine($"Secciones: {docInfo.Sections.Count}, Tablas: {docInfo.Tables.Count}, Chars: {docInfo.MarkdownText.Length}");

            Assert.False(string.IsNullOrWhiteSpace(docInfo.MarkdownText));
        }
    }
}
