using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysis.Core.Tests;

public sealed class CatalogAndNamingTests
{
    [Fact]
    public void Auditor_step_names_round_trip()
    {
        foreach (var type in Enum.GetValues<ReportType>())
        {
            var name = AnalysisPipelineFactory.AuditorStepName(type);

            Assert.Equal($"Auditor_{type}", name);
            Assert.True(AnalysisPipelineFactory.TryParseAuditorStepName(name, out var parsed));
            Assert.Equal(type, parsed);
        }
    }

    [Fact]
    public void Investigation_step_names_round_trip_with_their_report_and_subsystem()
    {
        foreach (var type in Enum.GetValues<ReportType>())
        {
            foreach (var id in new[] { "billing", "orders-3fa9c1", "root", "a_b" })
            {
                var name = AnalysisPipelineFactory.InvestigationStepName(type, id);

                Assert.Equal($"Investigation_{type}_{id}", name);
                Assert.True(AnalysisPipelineFactory.TryParseInvestigationStepName(name, out var parsedType, out var parsedId));
                Assert.Equal((type, id), (parsedType, parsedId));
                // Una investigación no es un auditor ni un verificador.
                Assert.False(AnalysisPipelineFactory.TryParseAuditorStepName(name, out _));
                Assert.False(AnalysisPipelineFactory.TryParseVerificationStepName(name, out _));
            }
        }
    }

    [Theory]
    [InlineData("Auditor_FlujosEndToEnd")]
    [InlineData("Investigation_")]
    [InlineData("Investigation_FlujosEndToEnd")]
    [InlineData("Investigation_FlujosEndToEnd_")]
    [InlineData("Investigation_Nope_billing")]
    [InlineData("Investigation_99_billing")]
    [InlineData("investigation_FlujosEndToEnd_billing")]
    public void Names_that_are_not_an_investigation_are_rejected(string name)
    {
        Assert.False(AnalysisPipelineFactory.TryParseInvestigationStepName(name, out _, out var id));
        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void An_investigation_name_needs_a_subsystem_id()
    {
        Assert.Throws<ArgumentException>(() => AnalysisPipelineFactory.InvestigationStepName(ReportType.FlujosEndToEnd, " "));
    }

    [Theory]
    [InlineData("SynthesizerLlm")]
    [InlineData("Auditor_")]
    [InlineData("Auditor_Nope")]
    [InlineData("auditor_CodeSmells")]
    public void Non_auditor_names_are_rejected(string name)
    {
        Assert.False(AnalysisPipelineFactory.TryParseAuditorStepName(name, out _));
    }

    [Fact]
    public void Catalog_covers_every_report_with_the_existing_mappings()
    {
        Assert.Equal(Enum.GetValues<ReportType>(), ReportCatalog.All.Select(d => d.Type));
        Assert.Equal(ReportCatalog.All.Count, ReportCatalog.All.Select(d => d.FileName).Distinct().Count());

        foreach (var d in ReportCatalog.All)
        {
            Assert.Equal(d.Type, AnalysisPipelineInput.ParseReportType(d.Alias));
            Assert.Equal(AnalysisPipelineInput.GetReportFileName(d.Type), d.FileName);
            Assert.Equal(AnalysisPipelineInput.GetRoleName(d.Type), d.RoleName);
            Assert.Equal(AnalysisPipelineInput.GetTemplatePrefix(d.Type), d.TemplatePrefix);
            Assert.False(string.IsNullOrWhiteSpace(d.Title));
            Assert.True(ReportCatalog.TryGetByFileName(d.FileName.ToUpperInvariant(), out var byName));
            Assert.Same(d, byName);
        }

        Assert.False(ReportCatalog.TryGetByFileName(ReportCatalog.ExecutiveReportFileName, out _));
    }
}
