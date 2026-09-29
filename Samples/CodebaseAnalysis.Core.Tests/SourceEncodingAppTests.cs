using AITaskAgent.Core.Models;
using AITaskAgent.FileTools;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Auditoría H-8: las tools, el código de <c>graph_search</c> y el visor leen con la regla del analizador (UTF-8 si es
/// válido; si no, la codificación del repositorio). El registro de tools fija esa codificación en FileTools desde
/// <c>JavaAnalyzer:DefaultEncoding</c> o el <c>pom.xml</c>, y con ninguna de las dos usa Latin-1. En la colección de la
/// consola porque <see cref="FileToolsConfiguration"/> es estático, como el registro de tools.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SourceEncodingAppTests : IDisposable
{
    private readonly TempDirectory _repo = new();
    private readonly string _originalRoot = FileToolsConfiguration.RootDirectory;

    public SourceEncodingAppTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public void Dispose()
    {
        FileToolsConfiguration.RootDirectory = _originalRoot;
        FileToolsConfiguration.DefaultEncoding = null!;
        _repo.Dispose();
    }

    private void WritePom(string encoding) => File.WriteAllText(Path.Combine(_repo.Path, "pom.xml"),
        $"<project><properties><project.build.sourceEncoding>{encoding}</project.build.sourceEncoding></properties></project>");

    [Theory]
    [InlineData(null, null, "iso-8859-1")]            // nada declarado: Latin-1, como el visor
    [InlineData(null, "ISO-8859-1", "iso-8859-1")]    // el pom.xml
    [InlineData("Cp1252", "ISO-8859-1", "windows-1252")] // la configuración manda sobre el pom.xml
    [InlineData(null, "UTF-8", "iso-8859-1")]         // UTF-8 no decodifica lo que no es UTF-8
    [InlineData("no-existe", null, "iso-8859-1")]     // el error de configuración lo avisa el analizador Java
    public void The_read_fallback_is_the_repository_encoding_or_latin1(string? configured, string? pom, string expected)
    {
        if (pom != null)
            WritePom(pom);

        Assert.Equal(expected, SourceEncoding.ResolveReadFallback(_repo.Path, configured).WebName);
    }

    [Fact]
    public void Registering_the_tools_sets_the_file_tools_encoding_from_the_configuration()
    {
        var configured = AnalysisRunOptions.BuildInput(TestConfig.FromPairs(("JavaAnalyzer:DefaultEncoding", "Cp1252")), _repo.Path, _ => { });
        var unconfigured = AnalysisRunOptions.BuildInput(TestConfig.FromPairs(), _repo.Path, _ => { });

        Assert.Equal("Cp1252", configured.SourceDefaultEncoding);
        AnalysisToolRegistration.Register(configured);
        Assert.Equal("windows-1252", FileToolsConfiguration.DefaultEncoding.WebName);

        Assert.Equal("", unconfigured.SourceDefaultEncoding);
        AnalysisToolRegistration.Register(unconfigured);
        Assert.Equal("iso-8859-1", FileToolsConfiguration.DefaultEncoding.WebName);
    }

    [Fact]
    public async Task Graph_search_with_include_code_shows_the_accents_of_a_latin1_file()
    {
        await File.WriteAllBytesAsync(Path.Combine(_repo.Path, "Pedido.bas"),
            Encoding.Latin1.GetBytes("Attribute VB_Name = \"Pedido\"\nSub Validación()\n    ' registra la acción\nEnd Sub\n"));
        var db = Path.Combine(_repo.Path, "codebase.db");
        await GraphSchema.EnsureAsync(db, CancellationToken.None);
        await using (var conn = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
                VALUES ('Codebase', 'Sub', 'Validacion', 'Pedido.Validacion', 'Pedido.bas', 2, 4, '{}');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        FileToolsConfiguration.RootDirectory = _repo.Path;
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.CbmDbPath, db);

        var result = await new GraphSearchTool(db).ExecuteAsync("""{"name_pattern":"Validacion","include_code":true}""", context, "ChatAssistant", NullLogger.Instance);

        Assert.Contains("2: Sub Validación()", result);
        Assert.Contains("3:     ' registra la acción", result);
        Assert.DoesNotContain('�', result);
    }
}
