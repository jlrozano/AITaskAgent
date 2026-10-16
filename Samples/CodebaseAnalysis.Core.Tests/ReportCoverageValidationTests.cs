using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="ReportValidationChecks.Coverage"/> (T24, criterio A6): un informe en modo B cuyo plan de cobertura del disco
/// (<c>_coverage_plan.json</c>) eligió subsistemas tiene que llevar el apartado «Cobertura» y nombrar cada uno. Cuenta como aviso,
/// no bloquea, y sin plan en disco (preparación apagada o informes de antes) no se comprueba.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ReportCoverageValidationTests : IDisposable
{
    private const string Meta = "<!-- analysis-meta: health=Green -->";
    private const string Funcional = "01_analisis_funcional.md";

    private readonly TempDirectory _temp = new();

    private string Reports => Path.Combine(_temp.Path, "reports");

    public ReportCoverageValidationTests() => Directory.CreateDirectory(Reports);

    public void Dispose() => _temp.Dispose();

    private static CoverageSelection Selection(string id, string name, int rank) =>
        new() { SubsystemId = id, Name = name, Rank = rank, Score = 1.0 - rank / 10.0, Loc = 100 };

    private void WritePlan(params ReportCoverage[] reports)
    {
        var plan = new CoveragePlan { GraphFingerprint = "fp", MaxSubsystemsPerReport = 8, Reports = reports };
        Assert.Null(PreparationArtifacts.TryWrite(Reports, plan));
    }

    private static ReportCoverage Coverage(ReportType type, params (string Id, string Name)[] chosen) => new()
    {
        Report = type,
        Chosen = [.. chosen.Select((c, i) => Selection(c.Id, c.Name, i + 1))]
    };

    private static readonly (string, string)[] Two = [("billing", "Facturación"), ("catalog", "Catálogo de productos")];

    private ReportFileValidation Validate(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(Reports, fileName), content);
        return ReportValidator.Validate(new ReportValidationRequest(_temp.Path, Reports)).Reports.Single(r => r.FileName == fileName);
    }

    private static ReportValidationIssue[] CoverageIssues(ReportFileValidation report) =>
        [.. report.Issues.Where(i => i.Check == ReportValidationChecks.Coverage)];

    private static string Body(string coverageSection) => $"# Informe\n\n## Resumen\n\nTexto.\n\n{coverageSection}\n{Meta}\n";

    [Fact]
    public void A_mode_b_report_with_the_section_naming_every_chosen_subsystem_passes()
    {
        WritePlan(Coverage(ReportType.AnalisisFuncional, Two));

        var report = Validate(Funcional, Body("## Cobertura\n\n- Facturación: cubierto.\n- Catálogo de productos: cubierto.\n"));

        Assert.Empty(CoverageIssues(report));
        var check = report.Checks.Single(c => c.Check == ReportValidationChecks.Coverage);
        Assert.Equal(ReportCheckStatuses.Pass, check.Status);
        Assert.Equal((2, 0), (check.Passed, check.Failed));
    }

    [Fact]
    public void A_mode_b_report_without_the_coverage_section_gets_a_warning_and_not_an_error()
    {
        WritePlan(Coverage(ReportType.AnalisisFuncional, Two));

        var report = Validate(Funcional, Body(""));

        var issue = Assert.Single(CoverageIssues(report));
        Assert.Equal(ReportValidationLevels.Warning, issue.Level);
        Assert.Contains("Cobertura", issue.Message);
        Assert.Equal(ReportCheckStatuses.Warn, report.Checks.Single(c => c.Check == ReportValidationChecks.Coverage).Status);
        Assert.DoesNotContain(report.Issues, i => i.Check == ReportValidationChecks.Coverage && i.Level == ReportValidationLevels.Error);
    }

    [Fact]
    public void A_chosen_subsystem_missing_from_the_section_is_named_in_a_warning_each_and_comparison_ignores_case()
    {
        WritePlan(Coverage(ReportType.AnalisisFuncional, ("billing", "Facturación"), ("catalog", "Catálogo de productos"), ("shipping", "Envíos")));

        var report = Validate(Funcional, Body("## Cobertura\n\n- FACTURACIÓN: cubierto.\n"));

        var issues = CoverageIssues(report);
        Assert.Equal(2, issues.Length);
        Assert.Contains(issues, i => i.Message.Contains("Catálogo de productos", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("Envíos", StringComparison.Ordinal));
        Assert.All(issues, i => Assert.Equal(ReportValidationLevels.Warning, i.Level));
        var check = report.Checks.Single(c => c.Check == ReportValidationChecks.Coverage);
        Assert.Equal((1, 2), (check.Passed, check.Failed));
    }

    [Fact]
    public void Names_outside_the_coverage_section_do_not_count_and_the_section_ends_at_the_next_heading_of_its_level()
    {
        WritePlan(Coverage(ReportType.AnalisisFuncional, Two));
        var text = "# Informe\n\nFacturación y Catálogo de productos se describen aquí.\n\n## Cobertura\n\n- Facturación.\n\n## Anexo\n\nCatálogo de productos.\n\n" + Meta + "\n";

        var report = Validate(Funcional, text);

        var issue = Assert.Single(CoverageIssues(report));
        Assert.Contains("Catálogo de productos", issue.Message);
    }

    [Theory]
    [InlineData("## 9. Cobertura\n\n- Facturación\n- Catálogo de productos\n")]
    [InlineData("### Cobertura del análisis\n\n- Facturación\n- Catálogo de productos\n")]
    [InlineData("## COBERTURA\n\nFacturación; Catálogo de productos.\n")]
    public void The_coverage_heading_may_be_numbered_deeper_or_longer(string section)
    {
        WritePlan(Coverage(ReportType.AnalisisFuncional, Two));

        Assert.Empty(CoverageIssues(Validate(Funcional, Body(section))));
    }

    [Fact]
    public void A_heading_inside_a_code_block_is_not_the_coverage_section()
    {
        WritePlan(Coverage(ReportType.AnalisisFuncional, Two));
        var text = "# Informe\n\n```markdown\n## Cobertura\n\n- Facturación\n- Catálogo de productos\n```\n\n" + Meta + "\n";

        var issue = Assert.Single(CoverageIssues(Validate(Funcional, text)));

        Assert.Contains("apartado", issue.Message);
    }

    [Fact]
    public void Without_a_plan_on_disk_nothing_is_checked_and_no_check_is_listed()
    {
        var report = Validate(Funcional, Body(""));

        Assert.Empty(CoverageIssues(report));
        Assert.DoesNotContain(report.Checks, c => c.Check == ReportValidationChecks.Coverage);
    }

    [Fact]
    public void A_corrupt_plan_is_not_checked_either()
    {
        File.WriteAllText(Path.Combine(Reports, CoveragePlan.FileName), "{ no es json");

        var report = Validate(Funcional, Body(""));

        Assert.DoesNotContain(report.Checks, c => c.Check == ReportValidationChecks.Coverage);
    }

    [Fact]
    public void A_report_that_is_not_in_mode_b_or_whose_plan_has_no_chosen_subsystems_is_not_checked()
    {
        WritePlan(Coverage(ReportType.AnalisisFuncional, Two), Coverage(ReportType.FlujosEndToEnd));

        var smells = Validate("02_code_smells.md", Body(""));
        var flows = Validate("04_flujos_end_to_end.md", Body(""));
        var executive = Validate(ReportCatalog.ExecutiveReportFileName, Body(""));

        Assert.All(new[] { smells, flows, executive }, r => Assert.DoesNotContain(r.Checks, c => c.Check == ReportValidationChecks.Coverage));
    }

    [Fact]
    public void Only_the_subsystems_still_chosen_after_a_time_budget_trim_are_required()
    {
        // TimeBudgetStep reescribe el plan del disco sin los recortados: estos ya no se exigen.
        var trimmed = Coverage(ReportType.AnalisisFuncional, ("billing", "Facturación")) with
        {
            Excluded = [new CoverageExclusion { SubsystemId = "catalog", Name = "Catálogo de productos", Reason = ExclusionReasons.TrimmedByTimeBudget }]
        };
        WritePlan(trimmed);

        var report = Validate(Funcional, Body("## Cobertura\n\n- Facturación.\n"));

        Assert.Empty(CoverageIssues(report));
    }

    [Fact]
    public void The_check_flows_into_the_totals_the_summary_and_the_validation_file_as_a_warning()
    {
        WritePlan(Coverage(ReportType.AnalisisFuncional, Two));
        File.WriteAllText(Path.Combine(Reports, Funcional), Body(""));

        var result = ReportValidator.Validate(new ReportValidationRequest(_temp.Path, Reports));

        Assert.True(result.Totals.Warnings >= 1);
        Assert.Equal(0, result.Reports.Single().Issues.Count(i => i.Check == ReportValidationChecks.Coverage && i.Level == ReportValidationLevels.Error));
        Assert.Null(ReportValidationFile.TryWrite(Reports, result));
        Assert.Contains("\"coverage\"", File.ReadAllText(Path.Combine(Reports, ReportValidationFile.FileName)));
        Assert.NotEmpty(ReportValidationSummary.Describe(result));
    }

    [Fact]
    public void The_content_validator_used_by_the_verifier_applies_it_too_but_the_verifier_is_not_asked_to_fix_it()
    {
        WritePlan(Coverage(ReportType.AnalisisFuncional, Two));
        var validator = ReportValidator.CreateContentValidator(new ReportValidationRequest(_temp.Path, Reports));

        var validation = validator.Validate(Funcional, Body(""));

        Assert.Single(CoverageIssues(validation));
        var input = ReportVerification.BuildInput(Funcional, Body(""), validation, _temp.Path);
        Assert.DoesNotContain("[coverage", input, StringComparison.Ordinal);
    }

    [Fact]
    public void Validating_text_does_not_create_the_reports_folder_even_when_looking_for_a_plan()
    {
        var missing = Path.Combine(_temp.Path, "no-existe");
        var validator = ReportValidator.CreateContentValidator(new ReportValidationRequest(_temp.Path, missing));

        var validation = validator.Validate(Funcional, Body(""));

        Assert.DoesNotContain(validation.Checks, c => c.Check == ReportValidationChecks.Coverage);
        Assert.False(Directory.Exists(missing));
    }
}
