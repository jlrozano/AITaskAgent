using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Implementation;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Tools;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>V2-B (plan v2 §6): informes propios, versionado, restauración y las tools de escritura del chat.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class ReportWriteTests
{
    private const string ErrorsReport = """
        # Errores y bugs

        ## Resumen

        Texto del análisis.

        ## Hallazgos

        | ID | Severidad | Descripción |
        |---|---|---|
        | BUG-01 | P0 | Uno |
        | BUG-02 | P2 | Dos |

        <!-- analysis-meta: health=Red; critical=1 -->
        """;

    private static KnowledgePaths Paths(TempDirectory dir)
    {
        var paths = new KnowledgePaths(dir.Path, Path.Combine(dir.Path, "reports"));
        Directory.CreateDirectory(paths.ReportsPath);
        return paths;
    }

    private static string WriteCatalog(KnowledgePaths paths, string content = ErrorsReport)
    {
        var path = Path.Combine(paths.ReportsPath, "05_errores_y_bugs.md");
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return path;
    }

    [Theory]
    [InlineData("Estudio de colas", "estudio-de-colas")]
    [InlineData("  Análisis — Índice ÑANDÚ ", "analisis-indice-nandu")]
    [InlineData("¿¿??", "informe")]
    public void Slug_is_lowercase_ascii_without_accents(string title, string slug) =>
        Assert.Equal(slug, ReportLibrary.Slugify(title));

    [Fact]
    public void Create_writes_the_custom_report_with_v0001_and_disambiguates_names()
    {
        using var dir = new TempDirectory();
        var paths = Paths(dir);
        var writer = new ReportWriter(paths);

        var first = writer.Create("Estudio de colas", "Cuerpo sin título.", "primera", ReportVersionOrigin.Chat("s1", "t-0001"));
        var second = writer.Create("ignorado", "# Estudio de colas\n\nOtro.", null, ReportVersionOrigin.Chat("s1", "t-0001"));

        Assert.Equal("estudio-de-colas.md", first.FileName);
        Assert.Equal("custom/estudio-de-colas.md", first.Id);
        Assert.Equal(("v0001", (string?)null, "created"), (first.VersionId, first.PreviousVersionId, first.Action));
        Assert.StartsWith("# Estudio de colas\n\nCuerpo sin título.", File.ReadAllText(paths.CustomReportFile(first.Source)));
        Assert.Equal("estudio-de-colas-2.md", second.FileName);
        Assert.Equal("Estudio de colas", second.Title);

        var listed = ReportLibrary.ListCustom(paths);
        Assert.Equal(["estudio-de-colas-2.md", "estudio-de-colas.md"], listed.Select(r => r.FileName));
        Assert.Equal(paths.CountCustomReports(), listed.Count);
        var version = Assert.Single(writer.History.List(first.Source));
        Assert.Equal(("chat", "s1", "t-0001", "primera"), (version.Origin.Type, version.Origin.SessionId, version.Origin.TurnId, version.Note));
        Assert.True(writer.IsModifiedByChat(first.Source));
    }

    [Fact]
    public void Update_of_a_catalog_report_keeps_analysis_meta_saves_a_baseline_and_restore_returns_the_original_sha()
    {
        using var dir = new TempDirectory();
        var paths = Paths(dir);
        var path = WriteCatalog(paths);
        var originalSha = ReportHistoryStore.FileSha256(path);
        var writer = new ReportWriter(paths);
        var source = SourceRef.Report("05_errores_y_bugs.md");

        var result = writer.Update(source, "replace", null, "# Errores y bugs\n\nReescrito sin tablas.", "reescritura", ReportVersionOrigin.Chat("s", "t"));

        var content = File.ReadAllText(path);
        Assert.EndsWith("<!-- analysis-meta: health=Red; critical=1 -->\n", content);
        Assert.Equal("Errores y bugs", result.Title);
        var versions = writer.History.List(source);
        Assert.Equal(["baseline", "updated"], versions.Select(v => v.Action));
        Assert.Equal("pipeline", versions[0].Origin.Type);
        Assert.Equal(originalSha, versions[0].Sha256);
        Assert.Equal(versions[0].VersionId, result.PreviousVersionId);
        Assert.True(writer.IsModifiedByChat(source));

        var meta = new SingleReportResult { MarkdownContent = content };
        meta.PopulateDerivedMetadata();
        Assert.Equal("Red", meta.QualityHealth);

        writer.Restore(source, result.PreviousVersionId, ReportVersionOrigin.Ui);
        Assert.Equal(originalSha, ReportHistoryStore.FileSha256(path));
        Assert.Equal(["baseline", "updated", "restored"], writer.History.List(source).Select(v => v.Action));
        Assert.False(writer.IsModifiedByChat(source));
    }

    [Fact]
    public void Update_with_new_meta_recomputes_critical_and_sections_are_edited_in_place()
    {
        using var dir = new TempDirectory();
        var paths = Paths(dir);
        var path = WriteCatalog(paths);
        var writer = new ReportWriter(paths);
        var source = SourceRef.Report("05_errores_y_bugs.md");
        var origin = ReportVersionOrigin.Chat("s", "t");

        writer.Update(source, "replace_section", "Resumen", "Resumen nuevo.", "resumen", origin);
        var afterSection = File.ReadAllText(path);
        Assert.Contains("## Resumen\n\nResumen nuevo.\n\n## Hallazgos", afterSection);
        Assert.DoesNotContain("Texto del análisis.", afterSection);

        writer.Update(source, "append_section", "Colas", "Detalle de colas.", "colas", origin);
        var afterAppend = File.ReadAllText(path);
        Assert.True(afterAppend.IndexOf("## Colas", StringComparison.Ordinal) < afterAppend.IndexOf("analysis-meta", StringComparison.Ordinal));

        var withMeta = ErrorsReport.Replace("| BUG-02 | P2 |", "| BUG-02 | P1 |").Replace("\r\n", "\n");
        writer.Update(source, "replace", null, withMeta, "dos críticos", origin);
        Assert.Contains("critical=2", File.ReadAllText(path));

        // La versión baseline se guardó solo una vez: las siguientes partían de la última versión.
        Assert.Equal(["baseline", "updated", "updated", "updated"], writer.History.List(source).Select(v => v.Action));
    }

    [Fact]
    public void Content_regenerated_by_the_pipeline_becomes_a_new_baseline_before_the_next_write()
    {
        using var dir = new TempDirectory();
        var paths = Paths(dir);
        var path = WriteCatalog(paths);
        var writer = new ReportWriter(paths);
        var source = SourceRef.Report("05_errores_y_bugs.md");
        writer.Update(source, "append_section", "A", "a", "a", ReportVersionOrigin.Chat("s", "t"));

        File.WriteAllText(path, "# Regenerado\n\n<!-- analysis-meta: health=Green -->\n");
        Assert.False(writer.IsModifiedByChat(source));

        writer.Update(source, "append_section", "B", "b", "b", ReportVersionOrigin.Chat("s", "t"));
        Assert.Equal(["baseline", "updated", "baseline", "updated"], writer.History.List(source).Select(v => v.Action));
    }

    [Fact]
    public void Limits_and_errors_have_their_codes()
    {
        using var dir = new TempDirectory();
        var paths = Paths(dir);
        WriteCatalog(paths);
        var writer = new ReportWriter(paths);
        var origin = ReportVersionOrigin.Chat("s", "t");

        Assert.Equal("report_too_large", Assert.Throws<KnowledgeOperationException>(() =>
            writer.Create("Grande", new string('x', ReportLibrary.MaxReportChars + 1), null, origin)).Code);
        Assert.Equal("not_found", Assert.Throws<KnowledgeOperationException>(() =>
            writer.Update(SourceRef.Custom("no-existe.md"), "replace", null, "x", "n", origin)).Code);
        Assert.Equal("invalid_request", Assert.Throws<KnowledgeOperationException>(() =>
            writer.Update(SourceRef.Report("05_errores_y_bugs.md"), "replace_section", "No hay tal", "x", "n", origin)).Code);
        Assert.Equal("version_not_found", Assert.Throws<KnowledgeOperationException>(() =>
            writer.Restore(SourceRef.Report("05_errores_y_bugs.md"), "v0099", ReportVersionOrigin.Ui)).Code);

        Directory.CreateDirectory(paths.CustomReportsPath);
        for (var i = 0; i < ReportLibrary.MaxCustomReports; i++)
            File.WriteAllText(Path.Combine(paths.CustomReportsPath, $"r{i}.md"), "# r");
        Assert.Equal("report_limit_reached", Assert.Throws<KnowledgeOperationException>(() =>
            writer.Create("Uno más", "texto", null, origin)).Code);
    }

    [Fact]
    public void History_keeps_fifty_versions_and_never_prunes_the_oldest_baseline()
    {
        using var dir = new TempDirectory();
        var paths = Paths(dir);
        WriteCatalog(paths);
        var writer = new ReportWriter(paths);
        var source = SourceRef.Report("05_errores_y_bugs.md");
        for (var i = 0; i < 60; i++)
            writer.Update(source, "append_section", $"S{i}", $"texto {i}", $"n{i}", ReportVersionOrigin.Chat("s", "t"));

        var versions = writer.History.List(source);
        Assert.Equal(ReportHistoryStore.MaxVersions, versions.Count);
        Assert.Equal("baseline", versions[0].Action);
        Assert.Equal("v0001", versions[0].VersionId);
        Assert.Equal("v0061", versions[^1].VersionId);
        Assert.Equal(ReportHistoryStore.MaxVersions + 1,
            Directory.GetFiles(paths.ReportHistoryFolder(source)).Length);
    }

    [Fact]
    public void Deleting_a_custom_report_keeps_a_deleted_version()
    {
        using var dir = new TempDirectory();
        var paths = Paths(dir);
        var writer = new ReportWriter(paths);
        var created = writer.Create("Borrable", "texto", null, ReportVersionOrigin.Chat("s", "t"));

        writer.DeleteCustom(created.Source, ReportVersionOrigin.Ui);

        Assert.False(File.Exists(paths.CustomReportFile(created.Source)));
        Assert.Equal(["created", "deleted"], writer.History.List(created.Source).Select(v => v.Action));
    }

    [Fact]
    public void Force_reindex_backup_leaves_custom_reports_and_history_in_place()
    {
        using var dir = new TempDirectory();
        var paths = Paths(dir);
        WriteCatalog(paths);
        var writer = new ReportWriter(paths);
        var created = writer.Create("Estudio", "texto", null, ReportVersionOrigin.Chat("s", "t"));
        writer.Update(SourceRef.Report("05_errores_y_bugs.md"), "append_section", "X", "x", "x", ReportVersionOrigin.Chat("s", "t"));
        var db = Path.Combine(dir.Path, ".codebase", "codebase.db");
        File.WriteAllText(db, "no es una BD");

        var input = new AnalysisPipelineInput { RepositoryPath = dir.Path, DatabasePath = db, OutputReportsPath = paths.ReportsPath, ForceReindex = true };
        DetectAndIndexGraphStep.BackupPreviousRun(input, new PipelineContext());

        Assert.False(File.Exists(db));
        Assert.False(File.Exists(Path.Combine(paths.ReportsPath, "05_errores_y_bugs.md")));
        Assert.True(File.Exists(paths.CustomReportFile(created.Source)));
        Assert.Single(writer.History.List(created.Source));
        Assert.Equal(2, writer.History.List(SourceRef.Report("05_errores_y_bugs.md")).Count);
    }

    [Fact]
    public async Task A_chat_turn_that_creates_a_report_emits_report_changed_after_the_tool_and_persists_the_artifact()
    {
        using var dir = new TempDirectory();
        var ws = new ChatWorkspace(dir.Path, Path.Combine(dir.Path, ".codebase", "codebase.db"), Path.Combine(dir.Path, "reports"));
        Directory.CreateDirectory(ws.ReportsPath);
        AvailableTools.Instance.Register(new CreateReportTool());
        AvailableTools.Instance.Register(new UpdateReportTool());
        AvailableTools.Instance.Register(new ListReportsTool());
        var store = new ChatSessionStore(Path.Combine(dir.Path, ".codebase", "chat"));
        var session = store.Create(ws.RepositoryPath);
        await using var services = ChatTestHost.BuildServices();
        var llm = new ToolScriptLlmService(
            ("create_report", JsonSerializer.Serialize(new { title = "Estudio de colas", content = "# Estudio de colas\n\nTexto." })),
            ("list_reports", "{}"));
        var agent = new ChatAgent(services, ChatTestHost.StreamingRole(llm), AnalysisPipelineFactory.CreateDefaultTemplateProvider(), new ChatOptions());

        var events = new List<ChatStreamEvent>();
        var turn = await agent.RunTurnAsync(store, session, "Guarda un estudio sobre las colas", ws, events.Add, CancellationToken.None);

        Assert.Equal(ChatTurnStatus.Completed, turn.Status);
        var types = events.Select(e => e.Type).ToList();
        var changed = types.IndexOf("report.changed");
        Assert.True(changed > 0);
        Assert.Equal("tool.completed", types[changed - 1]);
        Assert.True(changed < types.IndexOf("turn.completed"));
        var fields = events[changed].Fields;
        Assert.Equal("custom/estudio-de-colas.md", fields["id"]);
        Assert.Equal("custom", fields["kind"]);
        Assert.Equal("v0001", fields["versionId"]);
        Assert.Null(fields["previousVersionId"]);
        Assert.True(File.Exists(Path.Combine(ws.ReportsPath, "custom", "estudio-de-colas.md")));

        // El modelo recibe la línea con el id y la versión, y list_reports ya incluye el propio.
        Assert.Contains("custom/estudio-de-colas.md", llm.ToolResults[0]);
        Assert.Contains("v0001", llm.ToolResults[0]);
        Assert.Contains("\"kind\": \"custom\"", llm.ToolResults[1]);

        var artifact = Assert.Single(store.TryGet(session.Id)!.Turns[0].Artifacts!);
        Assert.Equal(("report", "custom/estudio-de-colas.md", "created", "v0001"), (artifact.Type, artifact.Id, artifact.Action, artifact.VersionId));
        var json = File.ReadAllText(Path.Combine(store.Directory, session.Id + ".json"));
        Assert.Contains("\"artifacts\"", json);
        Assert.Contains("\"previousVersionId\": null", json);

        var version = new ReportHistoryStore(ws.Knowledge).List(SourceRef.Custom("estudio-de-colas.md")).Single();
        Assert.Equal((session.Id, "t-0001"), (version.Origin.SessionId, version.Origin.TurnId));
    }

    [Fact]
    public async Task The_sixth_write_of_a_turn_fails_and_a_turn_without_writes_has_no_artifacts()
    {
        using var dir = new TempDirectory();
        var ws = new ChatWorkspace(dir.Path, Path.Combine(dir.Path, ".codebase", "codebase.db"), Path.Combine(dir.Path, "reports"));
        Directory.CreateDirectory(ws.ReportsPath);
        AvailableTools.Instance.Register(new CreateReportTool());
        AvailableTools.Instance.Register(new ListReportsTool());
        var store = new ChatSessionStore(Path.Combine(dir.Path, ".codebase", "chat"));
        var session = store.Create(ws.RepositoryPath);
        await using var services = ChatTestHost.BuildServices();
        var script = Enumerable.Range(1, 6)
            .Select(i => ("create_report", JsonSerializer.Serialize(new { title = $"Estudio {i}", content = $"texto {i}" })))
            .ToArray();
        var agent = new ChatAgent(services, ChatTestHost.StreamingRole(new ToolScriptLlmService(script)),
            AnalysisPipelineFactory.CreateDefaultTemplateProvider(), new ChatOptions());

        var turn = await agent.RunTurnAsync(store, session, "Guarda seis estudios", ws, _ => { }, CancellationToken.None);

        Assert.Equal([true, true, true, true, true, false], turn.ToolCalls.Select(c => c.Success == true));
        Assert.Equal(5, turn.Artifacts!.Count);
        Assert.Equal(5, ws.Knowledge.CountCustomReports());

        var plain = new ChatAgent(services, ChatTestHost.StreamingRole(new ToolScriptLlmService(("list_reports", "{}"))),
            AnalysisPipelineFactory.CreateDefaultTemplateProvider(), new ChatOptions());
        var second = await plain.RunTurnAsync(store, store.TryGet(session.Id)!, "¿Qué hay?", ws, _ => { }, CancellationToken.None);
        Assert.Null(second.Artifacts);
        Assert.DoesNotContain("\"artifacts\"", File.ReadAllText(Path.Combine(store.Directory, session.Id + ".json")).Split("t-0002")[1]);
    }
}
