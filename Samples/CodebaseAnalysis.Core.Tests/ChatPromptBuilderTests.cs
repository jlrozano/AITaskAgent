using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Tests;

public sealed class ChatPromptBuilderTests
{
    private static ChatWorkspace Workspace(string root) =>
        new(root, Path.Combine(root, ".codebase", "codebase.db"), Path.Combine(root, "reports"));

    [Fact]
    public async Task Renders_the_template_and_appends_the_context_blocks()
    {
        using var dir = new TempDirectory();
        var ws = Workspace(dir.Path);
        Directory.CreateDirectory(ws.ReportsPath);
        File.WriteAllText(Path.Combine(ws.ReportsPath, "05_errores_y_bugs.md"), "# E\n<!-- analysis-meta: health=Red; critical=2 -->");
        Directory.CreateDirectory(Path.GetDirectoryName(ws.DatabasePath)!);
        await GraphSchema.EnsureAsync(ws.DatabasePath, CancellationToken.None);
        using (var conn = new SqliteConnection($"Data Source={ws.DatabasePath};Pooling=False"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties) VALUES ('p','Class','A','A','a.cs',1,2,'{}');";
            await cmd.ExecuteNonQueryAsync();
        }

        var prompt = await ChatPromptBuilder.BuildAsync(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), "chat_system", ws);

        Assert.Contains($"El repositorio está en `{dir.Path}`", prompt);
        Assert.DoesNotContain("{{", prompt);
        // El include de la plantilla sí se resuelve; el de las reglas comunes no se incluye.
        Assert.Contains("Directrices de Diagramación Mermaid", prompt);
        Assert.DoesNotContain("Directrices Obligatorias de Entrega", prompt);
        Assert.Contains("| 05_errores_y_bugs.md | Errores y bugs | Red | 2 |", prompt);
        Assert.Contains("El grafo tiene 1 nodos y 0 aristas.", prompt);
    }

    [Fact]
    public async Task An_include_inside_the_executive_report_is_not_resolved()
    {
        using var dir = new TempDirectory();
        var ws = Workspace(dir.Path);
        Directory.CreateDirectory(ws.ReportsPath);
        File.WriteAllText(Path.Combine(ws.ReportsPath, "00_resumen_ejecutivo.md"),
            "# Resumen\n\nEl informe menciona @[_common_rules.md] y {{ repository_path }} literalmente.\n");

        var prompt = await ChatPromptBuilder.BuildAsync(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), null, ws);

        Assert.Contains("El informe menciona @[_common_rules.md] y {{ repository_path }} literalmente.", prompt);
        Assert.DoesNotContain("Directrices Obligatorias de Entrega", prompt);
        Assert.Contains("El grafo no existe o está vacío", prompt);
    }

    [Fact]
    public async Task The_executive_report_is_truncated()
    {
        using var dir = new TempDirectory();
        var ws = Workspace(dir.Path);
        Directory.CreateDirectory(ws.ReportsPath);
        File.WriteAllText(Path.Combine(ws.ReportsPath, "00_resumen_ejecutivo.md"), new string('z', ChatPromptBuilder.MaxExecutiveSummaryChars + 1000));

        var prompt = await ChatPromptBuilder.BuildAsync(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), null, ws);

        Assert.Contains(new string('z', ChatPromptBuilder.MaxExecutiveSummaryChars), prompt);
        Assert.DoesNotContain(new string('z', ChatPromptBuilder.MaxExecutiveSummaryChars + 1), prompt);
        Assert.Contains("resumen truncado", prompt);
    }

    [Fact]
    public async Task Without_reports_the_catalog_block_says_so()
    {
        using var dir = new TempDirectory();

        var prompt = await ChatPromptBuilder.BuildAsync(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), null, Workspace(dir.Path));

        Assert.Contains("No hay informes generados", prompt);
    }
}
