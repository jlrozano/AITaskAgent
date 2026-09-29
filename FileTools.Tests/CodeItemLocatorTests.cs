using AITaskAgent.Core.Models;
using AITaskAgent.FileTools.CodeItems;
using AITaskAgent.FileTools.Tools;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.FileTools.Tests;

/// <summary>
/// <c>view_code_item</c> asks its <see cref="ICodeItemLocator"/>s in order and then the built-in C# one: a host adds a
/// language without FileTools knowing it. Every declaration shown keeps the <c>=== path ===</c> / <c>Location:</c> header
/// that the chat's claim auditor reads; overloads are listed without it, because their code is not shown.
/// </summary>
[Collection(FileToolsConfigurationCollection.Name)]
public class CodeItemLocatorTests : IDisposable
{
    private readonly string _originalRoot = FileToolsConfiguration.RootDirectory;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fta-items-" + Guid.NewGuid());

    public CodeItemLocatorTests()
    {
        Directory.CreateDirectory(_root);
        FileToolsConfiguration.RootDirectory = _root;
    }

    public void Dispose()
    {
        FileToolsConfiguration.RootDirectory = _originalRoot;
        FileToolsConfiguration.ClearAdditionalAllowedDirectories();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string Json(string path) => path.Replace("\\", "\\\\");

    /// <summary>Answers for <c>.java</c> only, from a fixed table.</summary>
    private sealed class TableLocator(Dictionary<string, CodeItemLookup> table) : ICodeItemLocator
    {
        public List<string> Asked { get; } = [];

        public ValueTask<IReadOnlyList<CodeItemLookup>?> LocateAsync(CodeItemRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(Path.GetFileName(request.FilePath));
            if (!request.FilePath.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IReadOnlyList<CodeItemLookup>?>(null);
            return ValueTask.FromResult<IReadOnlyList<CodeItemLookup>?>(
                [.. request.NodePaths.Select(p => table.TryGetValue(p, out var l) ? l : CodeItemLookup.NotFound(["Pedido", "Pedido.total"]))]);
        }
    }

    private string WriteJava() =>
        WriteFile("Pedido.java", "class Pedido {\n    int total() {\n        return 1;\n    }\n    int total(int x) {\n        return x;\n    }\n}\n");

    private string WriteFile(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static TableLocator Locator() => new(new()
    {
        ["Pedido.total()"] = CodeItemLookup.Found(new CodeItemMatch { StartLine = 2, EndLine = 4 }),
        ["Pedido.total"] = new(
        [
            new CodeItemMatch { StartLine = 2, EndLine = 4, NodePath = "Pedido.total()", Detail = "int total()" },
            new CodeItemMatch { StartLine = 5, EndLine = 7, NodePath = "Pedido.total(int)", Detail = "int total(int x)" }
        ], [])
    });

    [Fact]
    public async Task A_locator_that_handles_the_file_gives_the_range_and_the_tool_shows_those_lines_with_the_location_header()
    {
        var file = WriteJava();
        var tool = new ViewCodeItemTool([Locator()]);

        var result = await tool.ExecuteAsync($$"""{"File": "{{Json(file)}}", "NodePaths": ["Pedido.total()"]}""", new PipelineContext(), "test", NullLogger.Instance);

        Assert.Equal($"=== Pedido.total() ===\nLocation: {file}:2-4\n\n    int total() {{\n        return 1;\n    }}", result.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Overloads_are_listed_by_their_own_node_path_without_code_or_location_header()
    {
        var file = WriteJava();

        var result = await new ViewCodeItemTool([Locator()]).ExecuteAsync(
            $$"""{"File": "{{Json(file)}}", "NodePaths": ["Pedido.total"]}""", new PipelineContext(), "test", NullLogger.Instance);

        Assert.Contains("'Pedido.total' matches 2 declarations", result);
        Assert.Contains("- `Pedido.total()` (lines 2-4): int total()", result);
        Assert.Contains("- `Pedido.total(int)` (lines 5-7): int total(int x)", result);
        Assert.DoesNotContain("Location:", result);
        Assert.DoesNotContain("return x;", result);
        Assert.DoesNotContain("TOOL_ERROR", result);
    }

    [Fact]
    public async Task Nothing_found_by_the_locator_is_a_tool_error_with_its_available_nodes()
    {
        var file = WriteJava();

        var result = await new ViewCodeItemTool([Locator()]).ExecuteAsync(
            $$"""{"File": "{{Json(file)}}", "NodePaths": ["Pedido.otro"]}""", new PipelineContext(), "test", NullLogger.Instance);

        Assert.StartsWith("TOOL_ERROR: None of the requested node paths were found.", result);
        Assert.Contains("Pedido.total", result);
    }

    [Fact]
    public async Task A_file_the_locator_does_not_handle_goes_to_the_built_in_csharp_locator()
    {
        var file = WriteFile("Pedido.cs", "class Pedido\n{\n    int Total() => 1;\n}\n");
        var locator = Locator();

        var result = await new ViewCodeItemTool([locator]).ExecuteAsync(
            $$"""{"File": "{{Json(file)}}", "NodePaths": ["Pedido.Total"]}""", new PipelineContext(), "test", NullLogger.Instance);

        Assert.Equal(["Pedido.cs"], locator.Asked);
        Assert.Equal($"=== Pedido.Total ===\nLocation: {file}:3-3\n\nint Total() => 1;", result.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Without_locators_the_output_is_the_csharp_one()
    {
        var file = WriteFile("Pedido.cs", "class Pedido\n{\n    /// <summary>Doc.</summary>\n    int Total() => 1;\n}\n");

        var result = await new ViewCodeItemTool().ExecuteAsync(
            $$"""{"File": "{{Json(file)}}", "NodePaths": ["Pedido.Total", "Pedido.Falta"]}""", new PipelineContext(), "test", NullLogger.Instance);

        // Roslyn's full string keeps the leading doc comment; the range is the declaration's.
        Assert.Equal($"=== Pedido.Total ===\nLocation: {file}:4-4\n\n/// <summary>Doc.</summary>\n    int Total() => 1;\n\n⚠️ Not found: Pedido.Falta",
            result.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task The_service_collection_passes_the_registered_locators_to_the_tool()
    {
        var file = WriteJava();
        var services = new ServiceCollection();
        services.AddSingleton<ICodeItemLocator>(Locator());
        services.AddFileTools(_root);
        using var provider = services.BuildServiceProvider();

        var tool = provider.GetServices<ITool>().OfType<ViewCodeItemTool>().Single();
        var result = await tool.ExecuteAsync($$"""{"File": "{{Json(file)}}", "NodePaths": ["Pedido.total()"]}""", new PipelineContext(), "test", NullLogger.Instance);

        Assert.Contains($"Location: {file}:2-4", result);
    }
}
