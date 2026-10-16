using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="ReportValidator.CreateContentValidator"/>: la misma validación que sobre ficheros, pero con el contenido en
/// memoria (lo que necesita el verificador, que valida el borrador antes de escribirlo).
/// </summary>
public sealed class ReportContentValidatorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public ReportContentValidatorTests()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "src"));
        File.WriteAllText(Path.Combine(_temp.Path, "src", "A.java"), string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n")));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Validates_the_text_without_a_file_on_disk()
    {
        var reports = Path.Combine(_temp.Path, "reports");
        var validator = ReportValidator.CreateContentValidator(new ReportValidationRequest(_temp.Path, reports));

        var result = validator.Validate("06_arquitectura_acoplamiento.md",
            "# Arquitectura\n\nBien: `src/A.java:3`. Mal: `src/A.java:99` y `src/Nope.java:1`.\n\n<!-- analysis-meta: health=Amber -->\n");

        Assert.Equal("06_arquitectura_acoplamiento.md", result.FileName);
        Assert.Equal(3, result.Citations.Count);
        Assert.Equal(1, result.Scores.CitationsValid);
        Assert.Equal(CitationStatuses.OutOfRange, result.Citations.Single(c => c.Text == "src/A.java:99").Status);
        Assert.Equal(CitationStatuses.Missing, result.Citations.Single(c => c.Text == "src/Nope.java:1").Status);
        Assert.Contains(result.Issues, i => i.Check == ReportValidationChecks.Citations && i.Message.Contains("src/A.java:99"));
        Assert.False(Directory.Exists(reports), "validar texto no debe crear la carpeta de informes");
    }

    /// <summary>
    /// El apartado «Cambios de la verificación» nombra a propósito las citas erróneas que corrigió y los identificadores de los
    /// hallazgos que tocó: validado como parte del informe, producía citas rotas e identificadores repetidos que no existen.
    /// </summary>
    private const string VerifiedReport =
        "# Arquitectura\n\n## ARC-001 Capas\n\nBien: `src/A.java:3`.\n\n"
        + "## Cambios de la verificación\n\n- **ARC-001:** la cita `src/Nope.java:9` pasa a `src/A.java:3`. Motivo: `src/Gone.java:1`.\n\n"
        + "<!-- analysis-meta: health=Amber -->\n";

    [Fact]
    public void The_changes_section_of_a_verified_report_is_not_validated_when_validating_text()
    {
        var validator = ReportValidator.CreateContentValidator(new ReportValidationRequest(_temp.Path, Path.Combine(_temp.Path, "reports")));

        var result = validator.Validate("06_arquitectura_acoplamiento.md", VerifiedReport);

        Assert.Equal(1, result.Scores.CitationsTotal);
        Assert.DoesNotContain(result.Issues, i => i.Check is ReportValidationChecks.Citations or ReportValidationChecks.FindingIds);
    }

    [Fact]
    public void The_changes_section_of_a_verified_report_is_not_validated_when_validating_the_folder_either()
    {
        var reports = Path.Combine(_temp.Path, "reports");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "06_arquitectura_acoplamiento.md"), VerifiedReport);

        var report = ReportValidator.Validate(new ReportValidationRequest(_temp.Path, reports)).Reports.Single();

        Assert.Equal(1, report.Scores.CitationsTotal);
        Assert.Equal(0, report.Scores.DuplicateIds);
        Assert.DoesNotContain(report.Issues, i => i.Check is ReportValidationChecks.Citations or ReportValidationChecks.FindingIds);
    }

    [Fact]
    public void The_same_validator_can_be_reused_for_several_reports()
    {
        var validator = ReportValidator.CreateContentValidator(new ReportValidationRequest(_temp.Path, Path.Combine(_temp.Path, "reports")));

        var first = validator.Validate("01_analisis_funcional.md", "# A\n\n`src/A.java:1`\n");
        var second = validator.Validate("02_code_smells.md", "# B\n\n`src/A.java:50`\n");

        Assert.Equal(1, first.Scores.CitationsValid);
        Assert.Equal(0, second.Scores.CitationsValid);
    }
}
