using AITaskAgent.Core.Models;
using AITaskAgent.FileTools.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.FileTools.Tests;

/// <summary>
/// Round-trip behaviour of the search and edit tools through their public ExecuteAsync, not just
/// the pure helpers underneath - confirms the JSON argument parsing and path resolution actually
/// wire correctly into FileToolsHelper/the filesystem.
/// </summary>
[Collection(FileToolsConfigurationCollection.Name)]
public class ToolBehaviorTests : IDisposable
{
    private readonly string _originalRoot = FileToolsConfiguration.RootDirectory;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fta-tests-" + Guid.NewGuid());

    public ToolBehaviorTests()
    {
        Directory.CreateDirectory(_root);
        FileToolsConfiguration.RootDirectory = _root;
    }

    public void Dispose()
    {
        FileToolsConfiguration.RootDirectory = _originalRoot;
        FileToolsConfiguration.ClearAdditionalAllowedDirectories();
        FileToolsConfiguration.SearchExcludedDirectories = null!;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static PipelineContext Context() => new();

    [Fact]
    public async Task ReplaceFileContentTool_replaces_the_unique_match()
    {
        var file = Path.Combine(_root, "target.cs");
        await File.WriteAllTextAsync(file, "line1\nold value\nline3");

        var tool = new ReplaceFileContentTool();
        var args = $$"""
            {
                "TargetFile": "{{file.Replace("\\", "\\\\")}}",
                "TargetContent": "old value",
                "ReplacementContent": "new value",
                "StartLine": 2,
                "EndLine": 2,
                "AllowMultiple": false
            }
            """;

        var result = await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        Assert.Contains("Successfully replaced", result);
        Assert.Equal("line1\nnew value\nline3", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task ReplaceFileContentTool_reports_a_miss_without_throwing()
    {
        var file = Path.Combine(_root, "target.cs");
        await File.WriteAllTextAsync(file, "line1\nline2");

        var tool = new ReplaceFileContentTool();
        var args = $$"""
            {
                "TargetFile": "{{file.Replace("\\", "\\\\")}}",
                "TargetContent": "does not exist",
                "ReplacementContent": "x",
                "StartLine": 1,
                "EndLine": 2,
                "AllowMultiple": false
            }
            """;

        var result = await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        Assert.Contains("Error", result);
        Assert.Equal("line1\nline2", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task MultiReplaceFileContentTool_applies_chunks_bottom_to_top_without_shifting_earlier_lines()
    {
        var file = Path.Combine(_root, "target.cs");
        await File.WriteAllTextAsync(file, "first\nsecond\nthird");

        var tool = new MultiReplaceFileContentTool();
        var args = $$"""
            {
                "TargetFile": "{{file.Replace("\\", "\\\\")}}",
                "CodeMarkdownLanguage": "text",
                "Instruction": "test",
                "Description": "test",
                "Complexity": 1,
                "ReplacementChunks": [
                    { "TargetContent": "first", "ReplacementContent": "FIRST", "StartLine": 1, "EndLine": 1, "AllowMultiple": false },
                    { "TargetContent": "third", "ReplacementContent": "THIRD", "StartLine": 3, "EndLine": 3, "AllowMultiple": false }
                ]
            }
            """;

        var result = await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        Assert.Contains("Successfully replaced 2 chunks", result);
        Assert.Equal("FIRST\nsecond\nTHIRD", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task GrepSearchTool_finds_a_literal_match_in_a_file()
    {
        var file = Path.Combine(_root, "code.cs");
        await File.WriteAllTextAsync(file, "public class Foo { }\npublic class Bar { }");

        var tool = new GrepSearchTool();
        var args = $$"""
            {"SearchPath": "{{file.Replace("\\", "\\\\")}}", "Query": "class Bar", "CaseInsensitive": false, "IsRegex": false}
            """;

        var result = await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        Assert.Contains("class Bar", result);
        Assert.DoesNotContain("class Foo", result);
    }

    [Fact]
    public async Task GrepSearchTool_reports_no_results_instead_of_an_empty_body()
    {
        var file = Path.Combine(_root, "code.cs");
        await File.WriteAllTextAsync(file, "nothing interesting here");

        var tool = new GrepSearchTool();
        var args = $$"""
            {"SearchPath": "{{file.Replace("\\", "\\\\")}}", "Query": "needle", "CaseInsensitive": false, "IsRegex": false}
            """;

        var result = await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        Assert.Equal("No results found", result);
    }

    /// <summary>
    /// A directory search on a large repository: no version-control, build or dependency folders, nothing under the
    /// host's excluded directories, no binaries, files in ordinal order and a trailing element without Filename when the
    /// cap is reached, so the same search gives the same capped answer every time.
    /// </summary>
    [Fact]
    public async Task GrepSearchTool_skips_tooling_folders_host_exclusions_and_binaries_and_says_when_it_is_capped()
    {
        foreach (var dir in new[] { "src", ".git", "node_modules/lib", "bin", "work/.data" })
            Directory.CreateDirectory(Path.Combine(_root, dir));
        for (var i = 0; i < 60; i++)
            await File.WriteAllTextAsync(Path.Combine(_root, "src", $"F{i:D2}.java"), "class F { String s = \"needle\"; }\n");
        await File.WriteAllTextAsync(Path.Combine(_root, ".git", "config"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "node_modules", "lib", "x.js"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "bin", "out.txt"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "work", ".data", "report.md"), "needle\n");
        await File.WriteAllBytesAsync(Path.Combine(_root, "blob.dat"), [0x6E, 0x65, 0x65, 0x64, 0x6C, 0x65, 0x00, 0x01]);
        FileToolsConfiguration.SearchExcludedDirectories = [Path.Combine(_root, "work", ".data")];

        var tool = new GrepSearchTool();
        var args = $$"""{"SearchPath": "{{_root.Replace("\\", "\\\\")}}", "Query": "needle"}""";
        var result = await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        var items = Newtonsoft.Json.Linq.JArray.Parse(result);
        var matches = items.Where(i => i["Filename"] != null).Select(i => (string)i["Filename"]!).ToList();
        Assert.Equal(GrepSearchTool.MaxMatches, matches.Count);
        Assert.All(matches, m => Assert.StartsWith("src", m));
        Assert.Equal(Path.Combine("src", "F00.java"), matches[0]);
        Assert.Equal(Path.Combine("src", "F49.java"), matches[^1]);
        var last = items[^1];
        Assert.Null(last?["Filename"]);
        Assert.True((bool)last!["Truncated"]!);

        // The same folder searched on purpose is searched.
        var direct = await tool.ExecuteAsync($$"""{"SearchPath": "{{Path.Combine(_root, "bin").Replace("\\", "\\\\")}}", "Query": "needle"}""",
            Context(), "test", NullLogger.Instance);
        Assert.Contains("out.txt", direct);
    }

    [Fact]
    public async Task GrepSearchTool_cuts_a_very_long_matching_line()
    {
        var file = Path.Combine(_root, "min.js");
        await File.WriteAllTextAsync(file, "var a='needle';" + new string('x', 5000));

        var result = await new GrepSearchTool().ExecuteAsync(
            $$"""{"SearchPath": "{{file.Replace("\\", "\\\\")}}", "Query": "needle"}""", Context(), "test", NullLogger.Instance);

        var line = (string)Newtonsoft.Json.Linq.JArray.Parse(result)[0]["LineContent"]!;
        Assert.Equal(GrepSearchTool.MaxLineChars + 1, line.Length);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public async Task FindByNameTool_finds_files_matching_the_extension_recursively()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(_root, "sub", "b.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(_root, "c.txt"), "");

        var tool = new FindByNameTool();
        var args = $$"""{"Directory": "{{_root.Replace("\\", "\\\\")}}", "Pattern": "**/*.cs"}""";

        var result = await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        Assert.Contains("a.cs", result);
        Assert.Contains("b.cs", result);
        Assert.DoesNotContain("c.txt", result);
    }

    [Fact]
    public async Task ListDirTool_lists_files_and_subdirectories_of_the_resolved_directory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "subdir"));
        await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "x");

        var tool = new ListDirTool();
        var args = $$"""{"DirectoryPath": "{{_root.Replace("\\", "\\\\")}}"}""";

        var result = await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        Assert.Contains("file.txt", result);
        Assert.Contains("subdir", result);
    }
}
