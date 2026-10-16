using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Lo determinista del verificador (<see cref="ReportVerification"/>): el texto que recibe el modelo, la interpretación de su
/// respuesta y el informe que se compone. Sin modelo ni pipeline.
/// </summary>
public sealed class ReportVerificationTests : IDisposable
{
    private const string Meta = "<!-- analysis-meta: health=Amber; model=m; provider=fake; system_sha=aaaaaaaaaaaa; prompt_sha=bbbbbbbbbbbb; graph=cbm:1 -->";

    private readonly TempDirectory _repo = new();

    public ReportVerificationTests()
    {
        var path = Path.Combine(_repo.Path, "src", "Dao.java");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Concat(Enumerable.Range(1, 40).Select(i => $"code {i}\n")));
    }

    public void Dispose() => _repo.Dispose();

    private ReportFileValidation Validate(string content) =>
        ReportValidator.CreateContentValidator(new ReportValidationRequest(_repo.Path, Path.Combine(_repo.Path, "reports")))
            .Validate("06_arquitectura_acoplamiento.md", content);

    [Fact]
    public void The_input_has_the_draft_the_failures_and_the_code_around_each_resolved_citation()
    {
        var draft = $"# Arquitectura\n\nEl DAO cierra en `src/Dao.java:20` y no existe `src/Nope.java:3`.\n\n{Meta}\n";

        var input = ReportVerification.BuildInput("06_arquitectura_acoplamiento.md", draft, Validate(draft), _repo.Path);

        Assert.Contains("<borrador>\n# Arquitectura", input);
        Assert.Contains("`src/Nope.java:3`", input[input.IndexOf("<fallos_validacion>", StringComparison.Ordinal)..]);
        var evidence = input[input.IndexOf("<evidencias>", StringComparison.Ordinal)..];
        Assert.Contains("### `src/Dao.java:14-26`", evidence);
        Assert.Contains("   20 | code 20", evidence);
        Assert.DoesNotContain("code 27", evidence);
        Assert.DoesNotContain("analysis-meta: health=Amber; model", input[input.IndexOf("<fallos_validacion>", StringComparison.Ordinal)..input.IndexOf("<evidencias>", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Counts_in_the_draft_are_listed_to_be_checked_but_citations_lines_and_units_are_not()
    {
        var draft = "# X\n\nLa herencia de las tres acciones respecto de `web.fw.Action` y 12 métodos públicos (`src/Dao.java:12-14`).\n"
            + "Tarda 5 minutos, ocupa 30 líneas y un 40 % del total; hay cuatro implementaciones de `Exporter`.\n"
            + "```\nlas siete clases de un bloque de código\n```\n";

        var input = ReportVerification.BuildInput("06_arquitectura_acoplamiento.md", draft, Validate(draft), _repo.Path);

        var counts = input[input.IndexOf("<recuentos>", StringComparison.Ordinal)..input.IndexOf("</recuentos>", StringComparison.Ordinal)];
        var entries = counts.Split('\n').Where(l => l.StartsWith("- línea", StringComparison.Ordinal)).ToList();
        // Los recuentos de elementos del código; no «5 minutos», «30 líneas», «40 %» ni lo que hay en un bloque de código.
        Assert.Equal(3, entries.Count);
        Assert.Contains("línea 3: «La herencia de las tres acciones respecto de", entries[0]);
        Assert.Contains("12 métodos públicos", entries[1]);
        Assert.Contains("hay cuatro implementaciones", entries[2]);
        Assert.DoesNotContain("siete", counts);
    }

    [Fact]
    public void A_clean_draft_says_so_in_the_failures_section_and_a_citationless_one_in_the_evidence_section()
    {
        var draft = $"# Sin citas\n\nTexto.\n\n{Meta}\n";

        var input = ReportVerification.BuildInput("01_analisis_funcional.md", draft, Validate(draft), _repo.Path);

        Assert.Contains("Ninguno:", input);
        Assert.Contains("Ninguna: el borrador no cita", input);
    }

    [Fact]
    public void File_include_sequences_are_neutralized_in_the_input_and_restored_in_the_report()
    {
        var draft = "# Informe\n\nEl atributo `@[Authorize]` y `@[secreto.md]`.\n";

        var input = ReportVerification.BuildInput("05_errores_y_bugs.md", draft, Validate(draft), _repo.Path);

        Assert.DoesNotContain("@[", input);
        Assert.Contains("@(at)[Authorize]", input);
        Assert.Equal("`@[Authorize]`", ReportVerification.Restore("`@(at)[Authorize]`"));
    }

    [Fact]
    public void Overlapping_ranges_of_the_same_file_are_listed_once_and_long_ranges_are_cut()
    {
        var draft = "# X\n\n`src/Dao.java:20` y de nuevo `src/Dao.java:20` y `src/Dao.java:1-40`.\n";

        var input = ReportVerification.BuildInput("02_code_smells.md", draft, Validate(draft), _repo.Path);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(input, "### `src/Dao.java:14-26`"));
        Assert.Contains("### `src/Dao.java:1-40`", input);
    }

    [Theory]
    [InlineData("SIN CAMBIOS\nHe contrastado las cuatro acciones con struts-config.xml.")]
    [InlineData("**SIN CAMBIOS**")]
    [InlineData("```markdown\nSIN CAMBIOS\n```")]
    public void The_short_answer_means_no_changes(string answer)
    {
        Assert.Equal(VerificationOutputKind.NoChanges, ReportVerification.ParseOutput(answer, "# Informe\n").Kind);
    }

    [Fact]
    public void A_corrected_report_counts_the_bullets_of_its_changes_section()
    {
        var original = "# Informe\n\n" + new string('x', 200) + "\n\n" + Meta + "\n";
        var answer = "# Informe\n\n" + new string('x', 190) + "\n\n## Cambios de la verificación\n\n- **ARC-001:** decía tres, son cuatro. Motivo: `a.xml:5`.\n- **ARC-002:** cita desplazada. Motivo: `B.java:9`.\n";

        var output = ReportVerification.ParseOutput(answer, original);

        Assert.Equal(VerificationOutputKind.Corrected, output.Kind);
        Assert.Equal(2, output.Changes);
        Assert.StartsWith("# Informe", output.Report);
    }

    [Fact]
    public void A_corrected_report_wrapped_in_a_code_fence_is_unwrapped()
    {
        var original = "# Informe\n\n" + new string('x', 100) + "\n";
        var answer = "```markdown\n# Informe\n\n" + new string('x', 100) + "\n\n## Cambios de la verificación\n\n- **A:** x. Motivo: y.\n```";

        var output = ReportVerification.ParseOutput(answer, original);

        Assert.Equal(VerificationOutputKind.Corrected, output.Kind);
        Assert.DoesNotContain("```", output.Report);
    }

    [Theory]
    [InlineData("", "vacía")]
    [InlineData("Claro, aquí tienes el informe.", "no empieza por el título")]
    [InlineData("# Informe\n\nTexto sin apartado.\n", "falta el apartado")]
    [InlineData("# Informe\n\n## Cambios de la verificación\n\nSe ha mejorado la redacción.\n", "no enumera ningún cambio")]
    public void An_unusable_answer_is_invalid_and_says_why(string answer, string expectedProblem)
    {
        var output = ReportVerification.ParseOutput(answer, "# Informe\n");

        Assert.Equal(VerificationOutputKind.Invalid, output.Kind);
        Assert.Contains(expectedProblem, output.Problem);
    }

    [Fact]
    public void A_report_less_than_half_the_original_is_discarded_as_truncated()
    {
        var original = "# Informe\n\n" + new string('x', 1000) + "\n";
        var answer = "# Informe\n\nCorto.\n\n## Cambios de la verificación\n\n- **A:** x. Motivo: y.\n";

        var output = ReportVerification.ParseOutput(answer, original);

        Assert.Equal(VerificationOutputKind.Invalid, output.Kind);
        Assert.Contains("menos de la mitad", output.Problem);
    }

    [Fact]
    public void An_empty_changes_section_that_says_there_are_none_is_no_changes()
    {
        var answer = "# Informe\n\ntexto\n\n## Cambios de la verificación\n\nSin cambios: todo coincide con el código.\n";

        Assert.Equal(VerificationOutputKind.NoChanges, ReportVerification.ParseOutput(answer, "# Informe\n").Kind);
    }

    [Fact]
    public void Compose_keeps_the_original_meta_line_last_and_drops_the_one_the_model_wrote()
    {
        var original = $"# Informe\n\ntexto\n\n{Meta}\n";
        var corrected = "# Informe\n\ntexto `@(at)[X]`\n\n## Cambios de la verificación\n\n- **A:** x. Motivo: y.\n\n<!-- analysis-meta: health=Green -->\n";

        var composed = ReportVerification.Compose(original, corrected);

        Assert.EndsWith(Meta + "\n", composed);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(composed, "analysis-meta"));
        Assert.Contains("`@[X]`", composed);
        Assert.Contains("## Cambios de la verificación", composed);
        Assert.True(composed.IndexOf("## Cambios", StringComparison.Ordinal) < composed.IndexOf("analysis-meta", StringComparison.Ordinal));
    }

    [Fact]
    public void A_report_whose_text_does_not_change_is_the_same_body_even_with_a_changes_section_and_other_whitespace()
    {
        var original = $"# Informe\n\nUna frase.\n\nOtra   frase.\n\n{Meta}\n";
        var echoed = "# Informe\n\nUna frase.\n\nOtra frase.\n\n## Cambios de la verificación\n\n- **A:** cita que queda igual.\n\n<!-- analysis-meta: health=Green -->\n";
        var edited = "# Informe\n\nUna frase corregida.\n\nOtra frase.\n\n## Cambios de la verificación\n\n- **A:** x.\n";

        Assert.True(ReportVerification.SameBody(original, echoed));
        Assert.False(ReportVerification.SameBody(original, edited));
        Assert.True(ReportVerification.SameBody(original, ReportVerification.Neutralize(original)));
    }

    [Theory]
    [InlineData(0, 0, "ok")]
    [InlineData(2, 0, "corrected(2)")]
    [InlineData(0, 3, "warnings(3)")]
    [InlineData(2, 3, "corrected(2),warnings(3)")]
    public void The_meta_value_summarises_the_outcome(int changes, int warnings, string expected)
    {
        Assert.Equal(expected, ReportVerification.MetaValue(changes, warnings));
        Assert.Contains($"verification={expected}", ReportVerification.WithVerificationMeta($"# X\n\n{Meta}\n", changes, warnings));
    }

    [Fact]
    public void The_changes_section_is_left_out_of_what_is_validated_after_verifying()
    {
        var report = "# X\n\n`src/Dao.java:20` y `src/Nope.java:3`.\n\n## Cambios de la verificación\n\n- **A:** la cita `src/Gone.java:1` no existía. Motivo: `src/Old.java:2`.\n";

        var meta = "<!-- analysis-meta: health=Amber -->";
        var withMeta = report + "\n" + meta + "\n";

        var trimmed = ReportVerification.WithoutChangesSection(withMeta);

        Assert.Equal("# X\n\n`src/Dao.java:20` y `src/Nope.java:3`.\n\n" + meta + "\n", trimmed);
        Assert.Equal(1, ReportVerification.CountWarnings(Validate(trimmed)));
        var renamed = report.Replace("## Cambios de la verificación", "## Otro");
        Assert.Equal(renamed, ReportVerification.WithoutChangesSection(renamed));
    }

    [Fact]
    public void Only_validation_errors_other_than_the_meta_line_count_as_warnings()
    {
        var draft = "# X\n\n`src/Dao.java:20` y `src/Nope.java:3`.\n";

        var validation = Validate(draft);

        Assert.Contains(validation.Issues, i => i.Check == ReportValidationChecks.Metadata);
        Assert.Equal(1, ReportVerification.CountWarnings(validation));
    }
}
