using OfficeTools.Models;
using OfficeTools.Steps;

namespace OfficeTools.Tests;

/// <summary>
/// Heurísticas de defectos de los procedimientos VBA (<c>detected_issues</c>): solo reglas genéricas, ninguna que dependa
/// de nombres de un repositorio concreto (auditoría 2026-09-29, H-6).
/// </summary>
public class IngestOfficeArtifactsStepTests
{
    private static List<string> IssuesOf(params string[] body)
    {
        var proc = new AccessVbaProcedure { Name = "Proc", StartLine = 1, EndLine = body.Length, BodyLines = [.. body] };
        IngestOfficeArtifactsStep.CreateProcedureProps(proc);
        return proc.DetectedIssues;
    }

    [Fact]
    public void Generic_rules_still_flag_blind_error_handling_and_a_misquoted_like()
    {
        Assert.Equal(["Manejo de excepciones ciego: 'On Error Resume Next' sin captura estructurada"],
            IssuesOf("On Error Resume Next", "x = 1"));
        Assert.Equal(["Defecto sintáctico en cláusula LIKE con comilla mal posicionada"],
            IssuesOf("sql = \"SELECT * FROM T WHERE c LIKE %'\" & v"));
    }

    [Fact]
    public void Structured_error_handling_is_not_flagged()
    {
        Assert.Empty(IssuesOf("On Error GoTo Fallo", "On Error Resume Next", "Fallo:"));
    }

    [Fact]
    public void Identifiers_that_only_exist_in_one_repository_are_not_rules()
    {
        // Eran reglas cableadas con nombres del repositorio de prueba: el banco de pruebas recibía las respuestas hechas.
        Assert.Empty(IssuesOf(
            "sql = \"set nocout on\"",
            "If sfFECALT_Fin > 0 Then filtro = \"FECCON = \" & sfFECALT_Fin"));
    }
}
