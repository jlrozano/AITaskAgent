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
