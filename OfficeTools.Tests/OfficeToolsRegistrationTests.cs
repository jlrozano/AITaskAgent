using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.LLM.Tools.Implementation;
using Microsoft.Extensions.DependencyInjection;
using OfficeTools.Abstractions;
using OfficeTools.Extensions;
using OfficeTools.Extractors;
using OfficeTools.Tools;

namespace OfficeTools.Tests;

public class OfficeToolsRegistrationTests
{
    [Fact]
    public void AddOfficeTools_RegistersExtractorsAndToolsInServiceCollection()
    {
        var services = new ServiceCollection();

        services.AddOfficeTools();

        using var provider = services.BuildServiceProvider();

        // 1. Verify extractors
        var schemaExtractor = provider.GetService<IAccessSchemaExtractor>();
        Assert.NotNull(schemaExtractor);
        Assert.IsType<AccessSchemaExtractor>(schemaExtractor);

        var docxReader = provider.GetService<IDocxReader>();
        Assert.NotNull(docxReader);
        Assert.IsType<DocxReader>(docxReader);

        // 2. Verify specific concrete tool registrations
        var accessSchemaTool = provider.GetService<AccessSchemaTool>();
        Assert.NotNull(accessSchemaTool);
        Assert.Equal("access_schema", accessSchemaTool.Name);

        var accessQueryTool = provider.GetService<AccessQuerySqlTool>();
        Assert.NotNull(accessQueryTool);
        Assert.Equal("access_query_sql", accessQueryTool.Name);

        var docxSearchTool = provider.GetService<DocxSearchTool>();
        Assert.NotNull(docxSearchTool);
        Assert.Equal("docx_search", docxSearchTool.Name);

        // 3. Verify ITool registrations
        var allTools = provider.GetServices<ITool>().ToList();
        Assert.Contains(allTools, t => t.Name == "access_schema");
        Assert.Contains(allTools, t => t.Name == "access_query_sql");
        Assert.Contains(allTools, t => t.Name == "docx_search");
    }

    [Fact]
    public void RegisterOfficeTools_RegistersToolsInAvailableToolsRegistry()
    {
        var registry = AvailableTools.Instance;

        registry.RegisterOfficeTools();

        var schemaTool = registry.GetTool("access_schema");
        Assert.NotNull(schemaTool);
        Assert.IsType<AccessSchemaTool>(schemaTool);

        var queryTool = registry.GetTool("access_query_sql");
        Assert.NotNull(queryTool);
        Assert.IsType<AccessQuerySqlTool>(queryTool);

        var docxTool = registry.GetTool("docx_search");
        Assert.NotNull(docxTool);
        Assert.IsType<DocxSearchTool>(docxTool);
    }

    [Fact]
    public void RegisterOfficeTools_CustomInstance_RegistersSuccessfully()
    {
        var customRegistry = new AvailableTools();

        customRegistry.RegisterOfficeTools(defaultDbPath: "test.accdb", defaultDocPath: "test.docx");

        var tools = customRegistry.GetTools("access_schema", "access_query_sql", "docx_search");
        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Name == "access_schema");
        Assert.Contains(tools, t => t.Name == "access_query_sql");
        Assert.Contains(tools, t => t.Name == "docx_search");
    }
}
