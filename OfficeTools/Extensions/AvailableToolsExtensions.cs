using AITaskAgent.LLM.Tools.Implementation;
using OfficeTools.Extractors;
using OfficeTools.Tools;

namespace OfficeTools.Extensions;

/// <summary>
/// Extension methods for registering OfficeTools in <see cref="AvailableTools"/>.
/// </summary>
public static class AvailableToolsExtensions
{
    /// <summary>
    /// Registers OfficeTools (AccessSchemaTool, AccessQuerySqlTool, DocxSearchTool) in the <see cref="AvailableTools"/> registry.
    /// </summary>
    /// <param name="availableTools">The AvailableTools instance.</param>
    /// <param name="defaultDbPath">Optional default database file path (.accdb / .mdb).</param>
    /// <param name="defaultDocPath">Optional default Word document or directory path (.docx).</param>
    /// <returns>The same AvailableTools instance for chaining.</returns>
    public static AvailableTools RegisterOfficeTools(
        this AvailableTools availableTools,
        string? defaultDbPath = null,
        string? defaultDocPath = null)
    {
        var schemaExtractor = new AccessSchemaExtractor();
        var docxReader = new DocxReader();

        availableTools.Register(new AccessSchemaTool(schemaExtractor, defaultDbPath));
        availableTools.Register(new AccessQuerySqlTool(schemaExtractor, defaultDbPath));
        availableTools.Register(new DocxSearchTool(docxReader, defaultDocPath));

        return availableTools;
    }
}
