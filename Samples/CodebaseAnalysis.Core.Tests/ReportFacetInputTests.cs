using System.Reflection;
using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Los campos de preparación de <see cref="ReportFacetInput"/> (T18 parcial): lo que llega a las plantillas de los informes y
/// la copia por informe que necesita el envoltorio de modo B, porque las ramas del paralelo comparten el input.
/// </summary>
public sealed class ReportFacetInputTests
{
    [Fact]
    public void The_preparation_fields_default_to_an_empty_block()
    {
        var input = new ReportFacetInput();

        Assert.Equal("{}", input.RepositoryInventory);
        Assert.Equal("{}", input.DomainMap);
        Assert.Equal("{}", input.CoveragePlan);
        Assert.Equal("", input.InvestigationNotes);
        Assert.Equal("", input.CoverageSummary);
    }

    [Fact]
    public void The_preparation_fields_are_readable_as_template_parameters()
    {
        var input = new ReportFacetInput { RepositoryInventory = "i", DomainMap = "d", CoveragePlan = "c", InvestigationNotes = "n", CoverageSummary = "s" };

        var parameters = input.GetParameters();

        Assert.Equal("i", parameters["RepositoryInventory"]);
        Assert.Equal("d", parameters["DomainMap"]);
        Assert.Equal("c", parameters["CoveragePlan"]);
        Assert.Equal("n", parameters["InvestigationNotes"]);
        Assert.Equal("s", parameters["CoverageSummary"]);
    }

    /// <summary>El contrato de plantillas: las cinco propiedades se leen como <c>{{ snake_case }}</c> con el renderizador de la app.</summary>
    [Fact]
    public void The_templates_read_the_preparation_fields_in_snake_case()
    {
        var input = new ReportFacetInput { RepositoryInventory = "INV", DomainMap = "MAP", CoveragePlan = "PLAN", InvestigationNotes = "NOTAS" };

        var rendered = new CodebaseAnalysis.Core.Support.AnalysisTemplateRenderer().Render(
            "[{{ repository_inventory }}|{{ domain_map }}|{{ coverage_plan }}|{{ investigation_notes }}|{{ coverage_summary }}]", input);

        Assert.Equal("[INV|MAP|PLAN|NOTAS|]", rendered);
    }

    [Fact]
    public void A_clone_keeps_every_facet_and_does_not_share_state_with_the_original()
    {
        var original = new ReportFacetInput
        {
            RepositoryPath = "repo", DatabasePath = "db", OutputReportsPath = "out", DetectedLanguages = "Java",
            MetricsSummary = "m", FlowsCatalog = "f", HardcodedSecrets = "h", TodoMarkers = "t", DiSummary = "di",
            TotalNodes = 11, TotalEdges = 22, TotalLoc = 33, TotalRoutes = 4, TotalFlows = 5, DeadCodeCount = 6,
            RepositoryInventory = "inv", DomainMap = "map", CoveragePlan = "plan", InvestigationNotes = "notes", CoverageSummary = "cov"
        };
        original.RawFacets["clave"] = "valor";

        var clone = original.CloneForReport();
        clone.InvestigationNotes = "notas de un informe";
        clone.CoverageSummary = "cobertura de un informe";
        clone.RawFacets["otra"] = 1;

        Assert.NotSame(original, clone);
        Assert.Equal("notes", original.InvestigationNotes);
        Assert.Equal("cov", original.CoverageSummary);
        Assert.False(original.RawFacets.ContainsKey("otra"));
        Assert.NotSame(original.RawFacets, clone.RawFacets);
        Assert.Equal("valor", clone.RawFacets["clave"]);
        Assert.Equal(original.StepName, clone.StepName);
        Assert.Equal(original.StepType, clone.StepType);
    }

    /// <summary>
    /// Toda propiedad pública con setter se copia: una faceta añadida mañana no puede quedar fuera de la copia sin que este
    /// test lo note. Se rellena la original por reflexión con un valor distinto en cada propiedad.
    /// </summary>
    [Fact]
    public void A_clone_copies_every_settable_public_property()
    {
        var original = new ReportFacetInput();
        var number = 1;
        var settable = typeof(ReportFacetInput).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && p.DeclaringType == typeof(ReportFacetInput) && p.Name != nameof(ReportFacetInput.RawFacets))
            .ToList();
        foreach (var property in settable)
        {
            if (property.PropertyType == typeof(string))
                property.SetValue(original, $"valor {property.Name}");
            else if (property.PropertyType == typeof(int))
                property.SetValue(original, number++);
        }

        var clone = original.CloneForReport();

        foreach (var property in settable.Where(p => p.PropertyType == typeof(string) || p.PropertyType == typeof(int)))
            Assert.Equal(property.GetValue(original), property.GetValue(clone));
        Assert.True(settable.Count > 50);
    }

    [Fact]
    public void A_clone_of_a_failed_input_keeps_the_error()
    {
        var failed = new ReportFacetInput(new AITaskAgent.Core.Steps.EmptyStep("x"), new AITaskAgent.Core.Models.StepError { Message = "boom" });

        Assert.True(failed.CloneForReport().HasError);
    }
}
