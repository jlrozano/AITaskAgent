using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Tests;

public sealed class SeverityContrastTests
{
    private static string Errors(string row) => string.Join("\n",
        "# Errores", "",
        "| ID | Severidad | Tipo | Evidencia |", "|---|---|---|---|", row, "");

    private static string Dead(string heading, string body) => string.Join("\n", "# Muerto", "", heading, body, "");

    [Fact]
    public void Spans_are_normalized_and_ranges_are_ordered()
    {
        var spans = SeverityContrast.SpansOf("`.\\Core\\Steps\\StepManager.cs:128-100`, `LLM/A.cs:7` y `no-es-cita.txt`");

        Assert.Equal([new FindingSpan("Core/Steps/StepManager.cs", 100, 128), new FindingSpan("LLM/A.cs", 7, 7)], spans);
    }

    [Fact]
    public void A_severe_finding_and_a_milder_one_over_nearby_code_of_another_report_are_a_discrepancy()
    {
        var errors = SeverityContrastTests.Errors("| 05-ERR-003 | P1 | Activación | `Core/Steps/StepManager.cs:100-128` |");
        var dead = Dead("### 03-API-004 · P3 · Sin usos internos", "**Prioridad P3.** `Core/Steps/StepManager.cs:46-83`");

        var found = SeverityContrast.Find([
            ("05.md", ReportValidator.SummarizeFindings("05.md", errors)),
            ("03.md", ReportValidator.SummarizeFindings("03.md", dead))]);

        var discrepancy = Assert.Single(found);
        Assert.Equal(("05.md", "05-ERR-003", 1), (discrepancy.SevereReport, discrepancy.SevereId, discrepancy.SeverePriority));
        Assert.Equal(("03.md", "03-API-004", 3), (discrepancy.OtherReport, discrepancy.OtherId, discrepancy.OtherPriority));
        var text = SeverityContrast.Format(found);
        Assert.Contains("`05-ERR-003` (P1", text);
        Assert.Contains("`03-API-004` (P3", text);
    }

    [Fact]
    public void Same_report_distant_code_equal_severity_and_mild_pairs_are_not_discrepancies()
    {
        var severe = Errors("| 05-ERR-003 | P1 | Activación | `Core/A.cs:10-20` |");
        var farAway = Dead("### 03-API-004 · P3 · Lejos", "**Prioridad P3.** `Core/A.cs:500-520`");
        var otherFile = Dead("### 03-API-005 · P3 · Otro fichero", "**Prioridad P3.** `Core/B.cs:10-20`");
        var sameLevel = string.Join("\n", "# X", "", "| ID | Severidad | Tipo | Evidencia |", "|---|---|---|---|", "| 06-ARQ-001 | P1 | Igual | `Core/A.cs:12` |", "");
        var mildOnly = string.Join("\n", "# Y", "", "| ID | Severidad | Tipo | Evidencia |", "|---|---|---|---|", "| 08-IMP-001 | P2 | Leve | `Core/A.cs:12` |", "| 08-IMP-002 | P3 | Leve | `Core/A.cs:14` |", "");

        Assert.Empty(SeverityContrast.Find([
            ("05.md", ReportValidator.SummarizeFindings("05.md", severe)),
            ("03.md", ReportValidator.SummarizeFindings("03.md", farAway)),
            ("03b.md", ReportValidator.SummarizeFindings("03b.md", otherFile)),
            ("06.md", ReportValidator.SummarizeFindings("06.md", sameLevel))]));
        Assert.Empty(SeverityContrast.Find([("08.md", ReportValidator.SummarizeFindings("08.md", mildOnly))]));

        // Un solo nivel de diferencia (P1 frente a P2) es una opinión, no una contradicción.
        var oneLevel = string.Join("\n", "# W", "", "| ID | Severidad | Tipo | Evidencia |", "|---|---|---|---|", "| 04-PRO-009 | P2 | Cerca | `Core/A.cs:12` |", "");
        Assert.Empty(SeverityContrast.Find([
            ("05.md", ReportValidator.SummarizeFindings("05.md", severe)),
            ("04.md", ReportValidator.SummarizeFindings("04.md", oneLevel))]));

        // Dentro del mismo informe no se compara.
        var both = string.Join("\n", "# Z", "", "| ID | Severidad | Tipo | Evidencia |", "|---|---|---|---|",
            "| 05-ERR-001 | P0 | Grave | `Core/A.cs:10` |", "| 05-ERR-002 | P3 | Leve | `Core/A.cs:11` |", "");
        Assert.Empty(SeverityContrast.Find([("05.md", ReportValidator.SummarizeFindings("05.md", both))]));
    }

    [Fact]
    public void No_discrepancies_gives_empty_text()
    {
        Assert.Equal(string.Empty, SeverityContrast.Format([]));
    }
}
