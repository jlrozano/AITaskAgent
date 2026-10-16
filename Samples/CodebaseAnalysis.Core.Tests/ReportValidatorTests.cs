using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="ReportValidator"/> sobre un repositorio y unos informes sintéticos: cada comprobación por separado, el comparador
/// de hallazgos esperados y el modo <c>--validate-reports</c>.
/// </summary>
public sealed class ReportValidatorTests : IDisposable
{
    private const string FullMeta = "<!-- analysis-meta: health=Red; critical=2; generated_at=2026-09-30T10:00:00Z; model=m; provider=fake; "
        + "system_sha=aaaaaaaaaaaa; prompt_sha=bbbbbbbbbbbb; graph=cbm:1 -->";

    private readonly TempDirectory _temp = new();

    private string Repo => _temp.Path;
    private string Reports => Path.Combine(Repo, "reports");

    public ReportValidatorTests()
    {
        WriteLines("src/Orders/OrderRepository.cs", 30);
        WriteLines(".codebase/access_export/modules/CGAR.bas", 20);
        // Las copias de backups/ y la BD de .codebase no son citables: si lo fueran, CGAR.bas sería ambiguo.
        WriteLines(".codebase/backups/20260101/.codebase/access_export/modules/CGAR.bas", 20);
        WriteLines("lib/a/Util.cs", 5);
        WriteLines("lib/b/Util.cs", 5);
        WriteLines("bin/Debug/Generated.cs", 5);
        Directory.CreateDirectory(Reports);
    }

    public void Dispose() => _temp.Dispose();

    private void WriteLines(string relative, int count)
    {
        var path = Path.Combine(Repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Concat(Enumerable.Range(1, count).Select(i => $"line {i}\n")));
    }

    private void WriteReport(string fileName, string content) => File.WriteAllText(Path.Combine(Reports, fileName), content);

    private ReportValidationResult Validate(string? db = null) =>
        ReportValidator.Validate(new ReportValidationRequest(Repo, Reports, db));

    private static ReportFileValidation Report(ReportValidationResult result, string fileName) =>
        result.Reports.Single(r => r.FileName == fileName);

    private static ReportCitation Citation(ReportFileValidation report, string text) => report.Citations.Single(c => c.Text == text);

    private static string ErroresReport(string body) => $"# Errores\n\n{body}\n\n{FullMeta}\n";

    [Fact]
    public void Citations_resolve_like_the_viewer_and_by_unique_suffix()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("""
            - Exacta `src/Orders/OrderRepository.cs:10-12` y con barra invertida `src\Orders\OrderRepository.cs:3`.
            - Por sufijo `CGAR.bas:5` y `modules\CGAR.bas:1-20`.
            - Fuera de rango `CGAR.bas:18-25` y `src/Orders/OrderRepository.cs:0`.
            - Inexistente `src/Orders/Nope.cs:1` y compilado `Generated.cs:1`.
            - Ambigua `Util.cs:2`; con ruta, `a/Util.cs:2`.
            - Sin línea: `src/Orders/OrderRepository.cs`.
            - No son citas: `P0/P1`, `1:1`, `12:30`, `CGAR.bas` y `Pedidos.MontarFiltro`.
            """));

        var report = Report(Validate(), "05_errores_y_bugs.md");

        Assert.Equal(CitationStatuses.Ok, Citation(report, "src/Orders/OrderRepository.cs:10-12").Status);
        Assert.False(Citation(report, "src/Orders/OrderRepository.cs:10-12").BySuffix);
        Assert.Equal("src/Orders/OrderRepository.cs", Citation(report, @"src\Orders\OrderRepository.cs:3").Target);
        var suffix = Citation(report, "CGAR.bas:5");
        Assert.Equal((CitationStatuses.Ok, true, ".codebase/access_export/modules/CGAR.bas"), (suffix.Status, suffix.BySuffix, suffix.Target));
        Assert.Equal(CitationStatuses.Ok, Citation(report, @"modules\CGAR.bas:1-20").Status);
        var outOfRange = Citation(report, "CGAR.bas:18-25");
        Assert.Equal((CitationStatuses.OutOfRange, 20), (outOfRange.Status, outOfRange.FileLines));
        Assert.Equal(CitationStatuses.OutOfRange, Citation(report, "src/Orders/OrderRepository.cs:0").Status);
        Assert.Equal(CitationStatuses.Missing, Citation(report, "src/Orders/Nope.cs:1").Status);
        Assert.Equal(CitationStatuses.Missing, Citation(report, "Generated.cs:1").Status);
        var ambiguous = Citation(report, "Util.cs:2");
        Assert.Equal(CitationStatuses.Ambiguous, ambiguous.Status);
        Assert.Equal(["lib/a/Util.cs", "lib/b/Util.cs"], ambiguous.Candidates);
        Assert.Equal(CitationStatuses.Ok, Citation(report, "a/Util.cs:2").Status);
        Assert.Equal(CitationStatuses.Ok, Citation(report, "src/Orders/OrderRepository.cs").Status);
        Assert.Equal(11, report.Scores.CitationsTotal);
        Assert.Equal(6, report.Scores.CitationsValid);
        Assert.Equal(54.5, report.Scores.CitationsValidPercent);

        // Rotas son error; ambigua, aviso, con la línea del informe.
        var issues = report.Issues.Where(i => i.Check == ReportValidationChecks.Citations).ToList();
        Assert.Equal(4, issues.Count(i => i.Level == ReportValidationLevels.Error));
        Assert.Single(issues, i => i.Level == ReportValidationLevels.Warning && i.Line == 7);
        Assert.Equal(ReportCheckStatuses.Fail, report.Checks.Single(c => c.Check == ReportValidationChecks.Citations).Status);
    }

    [Fact]
    public void Citations_inside_code_blocks_do_not_count()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("```vb\n' ver `src/Orders/Nope.cs:1`\n```\n\nTexto `src/Orders/OrderRepository.cs:1`."));

        var report = Report(Validate(), "05_errores_y_bugs.md");

        Assert.Equal("src/Orders/OrderRepository.cs:1", Assert.Single(report.Citations).Text);
    }

    [Fact]
    public void Finding_ids_must_follow_the_format_and_be_unique()
    {
        WriteReport("06_arquitectura_acoplamiento.md", ErroresReport("""
            | ID | Hallazgo |
            |---|---|
            | ARC-001 | uno |
            | ARC-001 | otra vez |

            ### ARC-001. Primero

            ### ARC-002, Segundo

            ### ARC-002, Repetido

            ### DEP-7. Mal numerado

            Relacionado con `ARC-01`, `ARC_001` y `ARC-009`; `SHA-1` y `UTF-8` no son identificadores.
            """));

        var report = Report(Validate(), "06_arquitectura_acoplamiento.md");
        var ids = report.Issues.Where(i => i.Check == ReportValidationChecks.FindingIds).ToList();

        Assert.Equal(["ARC-001", "ARC-002"], report.Findings.Select(f => f.Id));
        Assert.Contains(ids, i => i.Level == ReportValidationLevels.Error && i.Message.StartsWith("`ARC-002` encabeza 2 hallazgos", StringComparison.Ordinal));
        Assert.Contains(ids, i => i.Level == ReportValidationLevels.Warning && i.Message.StartsWith("`ARC-001` aparece en 2 filas", StringComparison.Ordinal));
        Assert.Equal(["ARC-01", "ARC_001", "DEP-7"],
            ids.Where(i => i.Message.Contains("no sigue el formato", StringComparison.Ordinal)).Select(i => i.Message.Split('`')[1]).Order(StringComparer.Ordinal));
        Assert.Contains(ids, i => i.Level == ReportValidationLevels.Warning && i.Message.StartsWith("`ARC-009` se cita pero ningún hallazgo", StringComparison.Ordinal));
        Assert.Equal((2, 3, 1), (report.Scores.DuplicateIds, report.Scores.MalformedIds, report.Scores.UndefinedIdReferences));
        Assert.Equal(ReportCheckStatuses.Fail, report.Checks.Single(c => c.Check == ReportValidationChecks.FindingIds).Status);
    }

    [Fact]
    public void Report_prefixed_ids_are_recognised_and_checked_like_the_legacy_ones()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("""
            | ID | Severidad | Tipo | Evidencia |
            |---|---|---|---|
            | 05-SEC-001 | P0 | Inyección | `src/Orders/OrderRepository.cs:10` |
            | 05-SEC-001 | P0 | Inyección | `src/Orders/OrderRepository.cs:10` |

            ### 05-SEC-001, P0, inyección

            ### 05-ERR-002, P2, errores silenciados

            Ver `src/Orders/OrderRepository.cs:20`, `05-SEC-009` y `05-SEC-3`; `2026-10-01` y `SHA-1` no son identificadores.
            """));

        var report = Report(Validate(), "05_errores_y_bugs.md");
        var ids = report.Issues.Where(i => i.Check == ReportValidationChecks.FindingIds).ToList();

        Assert.Equal(["05-ERR-002", "05-SEC-001"], report.Findings.Select(f => f.Id).Order(StringComparer.Ordinal));
        Assert.Equal(0, report.Findings.Single(f => f.Id == "05-SEC-001").Priority);
        Assert.Contains(ids, i => i.Level == ReportValidationLevels.Error && i.Message.StartsWith("`05-SEC-3` no sigue el formato", StringComparison.Ordinal)
            && i.Message.Contains("`05-SEC-003`", StringComparison.Ordinal));
        Assert.Contains(ids, i => i.Level == ReportValidationLevels.Warning && i.Message.StartsWith("`05-SEC-009` se cita pero ningún hallazgo", StringComparison.Ordinal));
        Assert.Contains(ids, i => i.Level == ReportValidationLevels.Warning && i.Message.StartsWith("`05-SEC-001` aparece en 2 filas", StringComparison.Ordinal));
        Assert.Equal((1, 1, 1), (report.Scores.DuplicateIds, report.Scores.MalformedIds, report.Scores.UndefinedIdReferences));
        Assert.DoesNotContain(ids, i => i.Message.Contains("número de informe", StringComparison.Ordinal));
    }

    [Fact]
    public void A_report_prefixed_id_must_carry_the_number_of_its_own_report_and_not_mix_formats()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("""
            ### 07-SEC-001, P2, con el número de otro informe

            ### 05-ERR-001, P2, bien

            ### SEC-002, P2, sin prefijo en un informe que ya los usa

            Ver `src/Orders/OrderRepository.cs:1`.
            """));
        WriteReport("06_arquitectura_acoplamiento.md", ErroresReport("### ARC-001, P2, formato antiguo\n\nVer `src/Orders/OrderRepository.cs:1`."));

        var result = Validate();
        var messages = Report(result, "05_errores_y_bugs.md").Issues.Where(i => i.Check == ReportValidationChecks.FindingIds).ToList();

        Assert.Contains(messages, i => i.Level == ReportValidationLevels.Warning && i.Message.Contains("`07-SEC-001`", StringComparison.Ordinal)
            && i.Message.Contains("número de informe 05", StringComparison.Ordinal));
        Assert.Contains(messages, i => i.Level == ReportValidationLevels.Warning && i.Message.Contains("`SEC-002`", StringComparison.Ordinal)
            && i.Message.Contains("sin prefijo", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, i => i.Message.Contains("`05-ERR-001`", StringComparison.Ordinal));
        // Un informe antiguo, entero sin prefijo, no se avisa.
        Assert.DoesNotContain(Report(result, "06_arquitectura_acoplamiento.md").Issues, i => i.Check == ReportValidationChecks.FindingIds);
    }

    [Fact]
    public void Prefixed_ids_do_not_collide_across_reports_and_the_summary_resolves_them()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("### 05-SEC-001, P0, x\n\nVer `src/Orders/OrderRepository.cs:1`."));
        WriteReport("07_inventario_apis_seguridad.md", ErroresReport("### 07-SEC-001, P1, y\n\nVer `src/Orders/OrderRepository.cs:2`."));
        WriteReport("00_resumen_ejecutivo.md", ErroresReport("Críticos: `05-SEC-001` y `07-SEC-001`; `07-SEC-002` no existe."));

        var result = Validate();

        Assert.Empty(result.CrossReportIdCollisions);
        Assert.Equal(["`07-SEC-002` se cita pero ningún informe lo define."],
            Report(result, "00_resumen_ejecutivo.md").Issues.Where(i => i.Check == ReportValidationChecks.FindingIds).Select(i => i.Message));
        Assert.DoesNotContain(ReportValidationSummary.Describe(result, null), l => l.Message.Contains("repetidos entre informes", StringComparison.Ordinal));
    }

    [Fact]
    public void Ids_repeated_across_reports_are_a_warning_in_the_summary_and_not_a_failure()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("### SEC-001, P0, x\n\nVer `src/Orders/OrderRepository.cs:1`."));
        WriteReport("07_inventario_apis_seguridad.md", ErroresReport("### SEC-001, P1, y\n\nVer `src/Orders/OrderRepository.cs:2`."));

        var lines = ReportValidationSummary.Describe(Validate(), null);

        var line = Assert.Single(lines, l => l.Message.Contains("repetidos entre informes", StringComparison.Ordinal));
        Assert.Equal(AnalysisLogLevel.Warning, line.Level);
        Assert.Contains("SEC-001", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_auditor_report_must_cite_code_as_a_warning_and_the_summary_is_exempt()
    {
        WriteReport("01_analisis_funcional.md", ErroresReport("Descripción sin ninguna cita."));
        WriteReport("04_flujos_end_to_end.md", ErroresReport("Flujo con cita `src/Orders/OrderRepository.cs:5`."));
        WriteReport("05_errores_y_bugs.md", ErroresReport("### 05-ERR-001, P2, con cita\n\n`src/Orders/OrderRepository.cs:1`\n\n### 05-ERR-002, P2, sin cita\n\nTexto.\n\n### 05-ERR-003, P3, sin cita\n"));
        WriteReport("00_resumen_ejecutivo.md", ErroresReport("Resumen sin citas de código."));

        var result = Validate();

        var functional = Report(result, "01_analisis_funcional.md");
        var issue = Assert.Single(functional.Issues, i => i.Check == ReportValidationChecks.CitationsRequired);
        Assert.Equal(ReportValidationLevels.Warning, issue.Level);
        Assert.Equal(ReportCheckStatuses.Warn, functional.Checks.Single(c => c.Check == ReportValidationChecks.CitationsRequired).Status);
        Assert.DoesNotContain(Report(result, "04_flujos_end_to_end.md").Issues, i => i.Check == ReportValidationChecks.CitationsRequired);
        Assert.DoesNotContain(Report(result, "00_resumen_ejecutivo.md").Issues, i => i.Check == ReportValidationChecks.CitationsRequired);
        var errores = Report(result, "05_errores_y_bugs.md").Issues.Single(i => i.Check == ReportValidationChecks.CitationsRequired);
        Assert.Equal(ReportValidationLevels.Warning, errores.Level);
        Assert.Contains("05-ERR-002", errores.Message, StringComparison.Ordinal);
        Assert.Contains("05-ERR-003", errores.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("05-ERR-001", errores.Message, StringComparison.Ordinal);
        Assert.Equal((1, 2), (result.Totals.ReportsWithoutCitations, result.Totals.FindingsUncited));
        // Va en la línea de totales, sin añadir otra al log de la ejecución.
        var lines = ReportValidationSummary.Describe(result, null);
        Assert.Contains("Sin citas de código: 1 informe(s) sin ninguna y 2 hallazgo(s) sin cita.", lines[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(lines.Skip(1), l => l.Message.Contains("Sin citas de código", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bold_list_item_that_starts_with_an_id_defined_by_a_heading_is_a_reference_not_a_second_definition()
    {
        WriteReport("03_codigo_muerto.md", ErroresReport("""
            ### 03-CDM-001, P2, método privado

            Ver `src/Orders/OrderRepository.cs:3`.

            ## Plan de acción

            1. **03-CDM-001 y 03-CDM-002:** retirar el método.
            2. **03-CDM-001:** repetido, pero es el plan.
            """));

        var report = Report(Validate(), "03_codigo_muerto.md");

        Assert.Equal(["03-CDM-001"], report.Findings.Select(f => f.Id));
        Assert.Equal(0, report.Scores.DuplicateIds);
        Assert.DoesNotContain(report.Issues, i => i.Check == ReportValidationChecks.FindingIds && i.Level == ReportValidationLevels.Error);
    }

    [Fact]
    public void Two_bold_definitions_of_the_same_id_are_still_a_duplicate()
    {
        WriteReport("02_code_smells.md", ErroresReport("""
            **02-COH-001, uno.** `src/Orders/OrderRepository.cs:1`.

            **02-COH-001, otra vez.** `src/Orders/OrderRepository.cs:2`.
            """));

        var report = Report(Validate(), "02_code_smells.md");

        Assert.Equal(1, report.Scores.DuplicateIds);
    }

    [Fact]
    public void Every_P0_or_P1_finding_cites_a_real_location()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("""
            | ID | Severidad | Tipo | Evidencia |
            |---|---|---|---|
            | SEC-001 | P0 | Inyección | `src/Orders/OrderRepository.cs:10` |
            | SEC-002 | P1 | Secreto | `src/Orders/Nope.cs:3` |
            | ERR-001 | P2 | Errores | sin cita |
            | ERR-002 | P1 | Errores | sin cita |

            ### SEC-001, P0, inyección

            Texto.

            ### SEC-002, P1, secreto

            Texto.

            ### ERR-001, P2, errores silenciados

            ### ERR-003, P1, solo encabezado

            Ver `CGAR.bas:3-4`.
            """));

        var report = Report(Validate(), "05_errores_y_bugs.md");

        var priorities = report.Findings.ToDictionary(f => f.Id, f => f.Priority);
        Assert.Equal((0, 1, 2, 1, 1), (priorities["SEC-001"], priorities["SEC-002"], priorities["ERR-001"], priorities["ERR-002"], priorities["ERR-003"]));
        var severe = report.Issues.Where(i => i.Check == ReportValidationChecks.SevereFindings).Select(i => i.Message).ToList();
        Assert.Equal(2, severe.Count);
        Assert.Contains("`SEC-002` (P1) solo cita ubicaciones que no existen: `src/Orders/Nope.cs:3`.", severe);
        Assert.Contains("`ERR-002` (P1) no cita ninguna ubicación de código.", severe);
        Assert.Equal((4, 2), (report.Scores.SevereFindings, report.Scores.SevereFindingsWithoutLocation));
    }

    [Fact]
    public void Graph_names_are_checked_only_with_a_database_and_a_known_owner()
    {
        var db = Path.Combine(_temp.Path, "graph.db");
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE nodes (id INTEGER PRIMARY KEY, project TEXT, label TEXT, name TEXT, qualified_name TEXT);
                INSERT INTO nodes (project, label, name, qualified_name) VALUES
                  ('p', 'Module', 'CGAR', 'Module.CGAR'),
                  ('p', 'Sub', 'MontarFiltro', 'Module.CGAR.MontarFiltro'),
                  ('p', 'UserDocSection', 'Guia', 'userdoc.doc:guia#0001'),
                  ('p', 'Project', 'Guia', 'Guia');
                """;
            cmd.ExecuteNonQuery();
        }
        WriteReport("06_arquitectura_acoplamiento.md", ErroresReport(
            "`CGAR.MontarFiltro`, `Module.CGAR.MontarFiltro()`, `CGAR.NoExiste`, `ADODB.Connection`, `Guia.Algo` y el fichero `CGAR.bas`."));

        var withDb = Report(Validate(db), "06_arquitectura_acoplamiento.md");
        var withoutDb = Report(Validate(), "06_arquitectura_acoplamiento.md");

        Assert.Equal([("CGAR.MontarFiltro", true), ("Module.CGAR.MontarFiltro()", true), ("CGAR.NoExiste", false)],
            withDb.GraphNames.Select(n => (n.Text, n.Found)));
        Assert.Equal((3, 2, 2), (withDb.Scores.GraphNamesChecked!.Value, withDb.Scores.GraphNamesFound!.Value, withDb.Scores.GraphNamesSkipped!.Value));
        Assert.Single(withDb.Issues, i => i.Check == ReportValidationChecks.GraphNames && i.Level == ReportValidationLevels.Warning);
        Assert.Empty(withoutDb.GraphNames);
        Assert.Null(withoutDb.Scores.GraphNamesChecked);
        Assert.Equal(ReportCheckStatuses.Skipped, withoutDb.Checks.Single(c => c.Check == ReportValidationChecks.GraphNames).Status);
    }

    [Fact]
    public void A_missing_database_is_a_note_and_not_a_failure()
    {
        WriteReport("06_arquitectura_acoplamiento.md", ErroresReport("Texto."));

        var result = Validate(Path.Combine(_temp.Path, "no-existe.db"));

        Assert.False(result.GraphChecked);
        Assert.Contains(result.Notes, n => n.Contains("no-existe.db", StringComparison.Ordinal));
    }

    [Fact]
    public void Metadata_needs_the_line_the_health_and_the_provenance()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("Texto."));
        WriteReport("06_arquitectura_acoplamiento.md", "# A\n\nTexto.\n\n<!-- analysis-meta: health=Amber -->\n");
        WriteReport("07_inventario_apis_seguridad.md", "# A\n\n<!-- analysis-meta: health=Verde; model=m; system_sha=a; prompt_sha=b; graph=g -->\n\nCola.\n");
        WriteReport("08_deuda_tecnica_hotspots.md", "# A\n\nSin línea.\n");

        var result = Validate();

        Assert.Equal(ReportCheckStatuses.Pass, Report(result, "05_errores_y_bugs.md").Checks.Single(c => c.Check == ReportValidationChecks.Metadata).Status);
        Assert.Equal(["model", "system_sha", "prompt_sha", "graph"], Report(result, "06_arquitectura_acoplamiento.md").Scores.MetaMissingKeys);
        var wrongHealth = Report(result, "07_inventario_apis_seguridad.md").Issues.Where(i => i.Check == ReportValidationChecks.Metadata).ToList();
        Assert.Contains(wrongHealth, i => i.Level == ReportValidationLevels.Error && i.Message.Contains("`Verde`", StringComparison.Ordinal));
        Assert.Contains(wrongHealth, i => i.Level == ReportValidationLevels.Warning && i.Line == 3);
        Assert.False(Report(result, "08_deuda_tecnica_hotspots.md").Scores.MetaPresent);
        // El 07 tiene la procedencia completa aunque el semáforo esté mal.
        Assert.Equal(2, result.Totals.ReportsWithoutProvenance);
    }

    [Theory]
    [InlineData("flowchart TD", null)]
    [InlineData("flowchart LR;", null)]
    [InlineData("sequenceDiagram", null)]
    [InlineData("classDiagram", null)]
    [InlineData("erDiagram", null)]
    [InlineData("stateDiagram-v2", null)]
    [InlineData("flowchart TB", ReportValidationLevels.Warning)]
    [InlineData("graph TD", ReportValidationLevels.Warning)]
    [InlineData("stateDiagram", ReportValidationLevels.Error)]
    [InlineData("pie title Reparto", ReportValidationLevels.Error)]
    [InlineData("quadrantChart", ReportValidationLevels.Error)]
    [InlineData("mindmap", ReportValidationLevels.Error)]
    [InlineData("gantt", ReportValidationLevels.Error)]
    public void Mermaid_types_follow_the_guidelines(string header, string? level) =>
        Assert.Equal(level, ReportValidator.MermaidProblem(header)?.Level);

    [Fact]
    public void Mermaid_blocks_are_read_after_comments_and_front_matter()
    {
        WriteReport("06_arquitectura_acoplamiento.md", ErroresReport(
            "```mermaid\n%% comentario\n---\ntitle: X\n---\nflowchart TD\n  A --> B\n```\n\n```mermaid\npie\n  \"a\" : 1\n```\n\n```mermaid\nflowchart TB\n```"));

        var report = Report(Validate(), "06_arquitectura_acoplamiento.md");

        Assert.Equal((3, 1), (report.Scores.MermaidBlocks, report.Scores.MermaidInvalid));
        Assert.Equal([ReportValidationLevels.Error, ReportValidationLevels.Warning],
            report.Issues.Where(i => i.Check == ReportValidationChecks.Mermaid).Select(i => i.Level));
    }

    /// <summary>La lista de tipos del validador es la de la plantilla: si cambia la guía, este test lo dice.</summary>
    [Fact]
    public void The_allowed_mermaid_types_are_the_ones_in_the_template()
    {
        var guidelines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "templates", "_mermaid_guidelines.md"));
        var allowed = guidelines.Single(l => l.Contains("Utiliza únicamente", StringComparison.Ordinal));
        var forbidden = guidelines.Single(l => l.Contains("**PROHIBIDO** utilizar diagramas", StringComparison.Ordinal));

        Assert.Equal(ReportValidator.AllowedMermaidTypes, allowed.Split('`').Where((_, i) => i % 2 == 1));
        Assert.Equal(ReportValidator.ForbiddenMermaidTypes, forbidden.Split('`').Where((_, i) => i % 2 == 1));
    }

    [Fact]
    public void The_executive_summary_checks_references_to_reports_sections_and_findings_of_other_reports()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("## Matriz de criticidad\n\n### SEC-001, P0, x\n\nVer `src/Orders/OrderRepository.cs:1`."));
        WriteReport("07_inventario_apis_seguridad.md", ErroresReport("### SEC-001. Otro\n\nVer `src/Orders/OrderRepository.cs:2`."));
        WriteReport("00_resumen_ejecutivo.md", ErroresReport("""
            | Informe | Estado |
            |---|---|
            | `05_errores_y_bugs.md` | Red |
            | `09_inyeccion_dependencias.md` | — |

            Según `05_errores_y_bugs.md › Matriz de criticidad` y `Errores y bugs › No existe`, `SEC-001` es P0; `SEC-009` no existe.
            """));

        var result = Validate();
        var executive = Report(result, "00_resumen_ejecutivo.md");

        Assert.Equal(
            [SourceReferenceStatuses.Ok, SourceReferenceStatuses.MissingSource, SourceReferenceStatuses.Ok, SourceReferenceStatuses.MissingSection],
            executive.SourceReferences.Select(r => r.Status));
        Assert.Equal(["`SEC-009` se cita pero ningún informe lo define."],
            executive.Issues.Where(i => i.Check == ReportValidationChecks.FindingIds).Select(i => i.Message));
        var collision = Assert.Single(result.CrossReportIdCollisions);
        Assert.Equal(("SEC-001", 2), (collision.Id, collision.Reports.Count));
    }

    [Fact]
    public void Expected_findings_measure_coverage_and_unexpected_findings()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("""
            ### SEC-001, P0, inyección SQL en el repositorio

            La consulta se construye concatenando la entrada `src/Orders/OrderRepository.cs:10-14`.

            ### SEC-002, P1, secreto en claro

            `CGAR.bas:3`.

            ### ERR-001, P2, otro

            Texto.
            """));
        var expected = new ExpectedFindingsDocument
        {
            Findings =
            [
                new ExpectedFinding { Id = "EXP-1", Report = "errores", File = "OrderRepository.cs", StartLine = 12, Keywords = ["INYECCION", "concatenando"] },
                new ExpectedFinding { Id = "EXP-2", Report = "05_errores_y_bugs.md", File = "src/Orders/OrderRepository.cs", StartLine = 20, EndLine = 25 },
                new ExpectedFinding { Id = "EXP-3", Report = "errores", Keywords = ["secreto"] },
                new ExpectedFinding { Id = "EXP-4", Report = "nope", Keywords = ["x"] },
                new ExpectedFinding { Id = "EXP-5", Report = "errores" }
            ]
        };

        var comparison = ExpectedFindingsComparer.Compare(Validate(), expected);

        Assert.Equal((5, 2, 40.0), (comparison.ExpectedCount, comparison.Found, comparison.CoveragePercent!.Value));
        Assert.Equal(["SEC-001"], comparison.Items[0].MatchedFindings);
        Assert.False(comparison.Items[1].Found);
        Assert.Equal(["SEC-002"], comparison.Items[2].MatchedFindings);
        Assert.NotNull(comparison.Items[3].Problem);
        Assert.NotNull(comparison.Items[4].Problem);
        Assert.Equal(["ERR-001"], Assert.Single(comparison.Unexpected).Findings);
        Assert.Equal(1, comparison.UnexpectedCount);
    }

    [Fact]
    public void The_template_of_expected_findings_loads_and_is_empty()
    {
        var template = ExpectedFindingsDocument.Load(Path.Combine(AppContext.BaseDirectory, "evaluation", "expected-findings.template.json"));

        Assert.Empty(template.Findings);
    }

    [Fact]
    public void Validate_reports_mode_writes_to_the_output_folder_and_compares_with_expected_findings()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("### SEC-001, P0, x\n\n`src/Orders/Nope.cs:1`."));
        var output = Path.Combine(_temp.Path, "out");
        var expectedPath = Path.Combine(_temp.Path, "expected.json");
        File.WriteAllText(expectedPath, "// comentario\n{ \"findings\": [ { \"report\": \"errores\", \"keywords\": [\"x\"] }, ] }");
        var reporter = new CapturingReporter();

        var exit = ReportValidationCommand.Run(
            TestConfig.FromPairs(("validation-output", output), ("expected", expectedPath)), Repo, reporter);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(Reports, ReportValidationFile.FileName)));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, ReportValidationFile.FileName)));
        Assert.Equal(1, json.RootElement.GetProperty("totals").GetProperty("brokenCitations").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("expected").GetProperty("found").GetInt32());
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.StartsWith("⚠️ 1 cita(s) de código rota(s)", StringComparison.Ordinal));
        Assert.Contains(reporter.Entries, e => e.Message.Contains("05_errores_y_bugs.md:3 [severe_findings]", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_reports_mode_fails_without_a_reports_folder()
    {
        Directory.Delete(Reports);
        var reporter = new CapturingReporter();

        Assert.Equal(1, ReportValidationCommand.Run(TestConfig.FromPairs(), Repo, reporter));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Error);
    }

    [Fact]
    public void The_switch_is_removed_before_the_configuration_sees_it()
    {
        var rest = AnalysisRunOptions.ExtractSwitch(["--validate-reports", "--repo", "X", "--validate-reports=false"], AnalysisRunOptions.ValidateReportsSwitch, out var present);

        Assert.True(present);
        Assert.Equal(["--repo", "X"], rest);
        Assert.Equal("X", TestConfig.FromArgs(rest)["repo"]);
        AnalysisRunOptions.ExtractSwitch(["--repo", "X"], AnalysisRunOptions.ValidateReportsSwitch, out var absent);
        Assert.False(absent);
    }

    [Fact]
    public void After_a_pipeline_the_validation_is_written_next_to_the_reports_and_never_throws()
    {
        WriteReport("05_errores_y_bugs.md", ErroresReport("### SEC-001, P0, x\n\n`src/Orders/OrderRepository.cs:1`."));
        var reporter = new CapturingReporter();
        var input = new AnalysisPipelineInput { RepositoryPath = Repo, OutputReportsPath = Reports, DatabasePath = Path.Combine(Repo, ".codebase", "codebase.db") };

        var result = ReportValidationSummary.RunAfterPipeline(input, reporter);

        Assert.NotNull(result);
        Assert.True(File.Exists(Path.Combine(Reports, ReportValidationFile.FileName)));
        Assert.All(reporter.Entries, e => Assert.Equal(ReportValidationSummary.SourceName, e.Source));
        Assert.Equal(1, ReportValidationSummary.ToData(result)["citationsValid"]);

        var broken = new AnalysisPipelineInput { RepositoryPath = Repo, OutputReportsPath = "\0invalid", DatabasePath = string.Empty };
        Assert.Null(ReportValidationSummary.RunAfterPipeline(broken, reporter));
        Assert.Contains(reporter.Entries, e => e.Message.StartsWith("⚠️ No se pudieron validar", StringComparison.Ordinal));
    }
}
