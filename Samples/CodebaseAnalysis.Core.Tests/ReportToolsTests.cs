using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tests;

public sealed class ReportToolsTests
{
    private const string ErrorsReport = """
        # Errores y bugs

        ## Resumen

        Texto del resumen.

        ## Hallazgos

        ### P0 credencial

        Detalle P0.

        ```text
        ## esto no es un encabezado
        ```

        ### P1 validación

        Detalle P1.

        ## Recomendaciones

        Cambiar cosas.

        <!-- analysis-meta: health=Red; critical=6 -->
        """;

    private static PipelineContext ContextFor(string reportsPath)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.ReportsPath, reportsPath);
        return context;
    }

    private static Task<string> RunAsync(AITaskAgent.LLM.Tools.Abstractions.ITool tool, string args, PipelineContext context) =>
        tool.ExecuteAsync(args, context, "ChatAssistant", NullLogger.Instance);

    [Fact]
    public void Catalog_listing_reads_health_and_ignores_foreign_markdown()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "05_errores_y_bugs.md"), ErrorsReport);
        File.WriteAllText(Path.Combine(dir.Path, "00_resumen_ejecutivo.md"), "# Resumen\n<!-- analysis-meta: health=Amber -->");
        File.WriteAllText(Path.Combine(dir.Path, "02_code_smells.md"), "# Sin meta");
        File.WriteAllText(Path.Combine(dir.Path, "NOTAS.md"), "ajeno");

        var reports = ReportFiles.List(dir.Path);

        Assert.Equal(["00_resumen_ejecutivo.md", "02_code_smells.md", "05_errores_y_bugs.md"], reports.Select(r => r.FileName));
        Assert.Null(reports[0].ReportType);
        Assert.Equal("Resumen ejecutivo", reports[0].Title);
        Assert.Equal("Amber", reports[0].Health);
        Assert.Equal("Unknown", reports[1].Health);
        Assert.Equal(Models.ReportType.ErroresYBugs, reports[2].ReportType);
        Assert.Equal("Red", reports[2].Health);
        Assert.Equal(6, reports[2].CriticalFindings);
        Assert.True(reports[2].SizeBytes > 0);
    }

    [Theory]
    [InlineData("05_ERRORES_Y_BUGS.md", true)]
    [InlineData("../05_errores_y_bugs.md", false)]
    [InlineData("..%2F05_errores_y_bugs.md", false)]
    [InlineData("NOTAS.md", false)]
    [InlineData("", false)]
    public void Only_catalog_names_resolve(string name, bool expected)
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "05_errores_y_bugs.md"), ErrorsReport);
        File.WriteAllText(Path.Combine(dir.Path, "NOTAS.md"), "ajeno");

        Assert.Equal(expected, ReportFiles.TryResolve(dir.Path, name, out _));
    }

    [Fact]
    public async Task List_reports_returns_the_catalog_as_json()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "05_errores_y_bugs.md"), ErrorsReport);

        var json = await RunAsync(new ListReportsTool(), "{}", ContextFor(dir.Path));

        using var doc = JsonDocument.Parse(json);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("05_errores_y_bugs.md", row.GetProperty("file_name").GetString());
        Assert.Equal("Red", row.GetProperty("health").GetString());
        Assert.Equal(6, row.GetProperty("critical_findings").GetInt32());
    }

    [Fact]
    public async Task List_reports_without_reports_says_so()
    {
        using var dir = new TempDirectory();

        var text = await RunAsync(new ListReportsTool(), "{}", ContextFor(dir.Path));

        Assert.Contains("No hay informes", text);
    }

    [Fact]
    public async Task Read_report_returns_the_whole_file_or_one_section()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "05_errores_y_bugs.md"), ErrorsReport);
        var context = ContextFor(dir.Path);

        var whole = await RunAsync(new ReadReportTool(), """{"file_name":"05_errores_y_bugs.md"}""", context);
        var section = await RunAsync(new ReadReportTool(), """{"file_name":"05_errores_y_bugs.md","heading":"## hallazgos"}""", context);
        var subsection = await RunAsync(new ReadReportTool(), """{"file_name":"05_errores_y_bugs.md","heading":"P0 credencial"}""", context);

        Assert.Equal(ErrorsReport, whole);
        Assert.StartsWith("## Hallazgos", section);
        Assert.Contains("### P1 validación", section);
        Assert.DoesNotContain("## Recomendaciones", section);
        Assert.StartsWith("### P0 credencial", subsection);
        Assert.Contains("## esto no es un encabezado", subsection);
        Assert.DoesNotContain("P1 validación", subsection);
    }

    [Fact]
    public async Task Read_report_with_unknown_heading_lists_the_headings()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "05_errores_y_bugs.md"), ErrorsReport);

        var text = await RunAsync(new ReadReportTool(), """{"file_name":"05_errores_y_bugs.md","heading":"Inexistente"}""", ContextFor(dir.Path));

        Assert.Contains("No se encontró la sección", text);
        Assert.Contains("## Recomendaciones", text);
    }

    [Theory]
    [InlineData("""{"file_name":"../appsettings.json"}""")]
    [InlineData("""{"file_name":"NOTAS.md"}""")]
    [InlineData("{}")]
    public async Task Read_report_rejects_names_outside_the_catalog(string args)
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "NOTAS.md"), "ajeno");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(new ReadReportTool(), args, ContextFor(dir.Path)));

        Assert.Contains("no es un informe del catálogo", ex.Message);
    }

    [Fact]
    public async Task Read_report_of_a_missing_catalog_file_fails()
    {
        using var dir = new TempDirectory();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            RunAsync(new ReadReportTool(), """{"file_name":"00_resumen_ejecutivo.md"}""", ContextFor(dir.Path)));
    }

    [Fact]
    public async Task Read_report_truncates_long_reports_with_a_notice()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "08_deuda_tecnica_hotspots.md"), new string('a', ReadReportTool.MaxChars + 500));

        var text = await RunAsync(new ReadReportTool(), """{"file_name":"08_deuda_tecnica_hotspots.md"}""", ContextFor(dir.Path));

        Assert.StartsWith(new string('a', ReadReportTool.MaxChars), text);
        Assert.Contains("informe truncado", text);
        Assert.True(text.Length < ReadReportTool.MaxChars + 400);
    }

    [Fact]
    public void Both_tools_are_read_only()
    {
        Assert.True(new ListReportsTool().IsReadOnly);
        Assert.True(new ReadReportTool().IsReadOnly);
    }
}
