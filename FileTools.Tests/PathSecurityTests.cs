using AITaskAgent.Core.Models;
using AITaskAgent.FileTools.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.FileTools.Tests;

/// <summary>
/// End-to-end sandbox check: every tool resolves its path argument (BaseFileTool.ResolvePath)
/// and then validates it against FileToolsConfiguration.AllowedDirectories
/// (BaseFileTool.ValidatePath) before touching disk. These tests exercise that boundary through
/// the real tools - not a re-implementation of the check - because that boundary is what actually
/// stands between an LLM-controlled path argument and the rest of the filesystem.
/// </summary>
[Collection(FileToolsConfigurationCollection.Name)]
public class PathSecurityTests : IDisposable
{
    private readonly string _originalRoot = FileToolsConfiguration.RootDirectory;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fta-tests-" + Guid.NewGuid());
    private readonly string _outsideRoot = Path.Combine(Path.GetTempPath(), "fta-tests-outside-" + Guid.NewGuid());

    public PathSecurityTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideRoot);
        FileToolsConfiguration.RootDirectory = _root;
    }

    public void Dispose()
    {
        FileToolsConfiguration.RootDirectory = _originalRoot;
        FileToolsConfiguration.ClearAdditionalAllowedDirectories();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_outsideRoot, recursive: true); } catch { /* best effort */ }
    }

    private static PipelineContext Context() => new();

    [Fact]
    public async Task ViewFileTool_rejects_a_relative_traversal_out_of_the_root()
    {
        var secret = Path.Combine(_outsideRoot, "secret.txt");
        await File.WriteAllTextAsync(secret, "top secret");

        var relativeEscape = Path.GetRelativePath(_root, secret);
        var tool = new ViewFileTool();
        var args = $$"""{"AbsolutePath": "{{relativeEscape.Replace("\\", "\\\\")}}"}""";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance));
    }

    [Fact]
    public async Task ViewFileTool_rejects_an_absolute_path_outside_every_allowed_directory()
    {
        var secret = Path.Combine(_outsideRoot, "secret.txt");
        await File.WriteAllTextAsync(secret, "top secret");

        var tool = new ViewFileTool();
        var args = $$"""{"AbsolutePath": "{{secret.Replace("\\", "\\\\")}}"}""";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance));
    }

    [Fact]
    public async Task ViewFileTool_reads_a_file_inside_the_root()
    {
        var file = Path.Combine(_root, "inside.txt");
        await File.WriteAllTextAsync(file, "hello");

        var tool = new ViewFileTool();
        var args = $$"""{"AbsolutePath": "{{file.Replace("\\", "\\\\")}}"}""";

        var result = await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task ViewFileTool_reads_a_directory_granted_via_AddAllowedDirectory()
    {
        FileToolsConfiguration.AddAllowedDirectory(_outsideRoot);
        var file = Path.Combine(_outsideRoot, "granted.txt");
        await File.WriteAllTextAsync(file, "granted content");

        var tool = new ViewFileTool();
        var args = $$"""{"AbsolutePath": "{{file.Replace("\\", "\\\\")}}"}""";

        var result = await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        Assert.Contains("granted content", result);
    }

    [Fact]
    public async Task WriteToFileTool_refuses_to_write_outside_the_root()
    {
        var target = Path.Combine(_outsideRoot, "malicious.txt");

        var tool = new WriteToFileTool();
        var args = $$"""
            {"TargetFile": "{{target.Replace("\\", "\\\\")}}", "Overwrite": true, "CodeContent": "pwned"}
            """;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task WriteToFileTool_writes_inside_the_root()
    {
        var target = Path.Combine(_root, "created.txt");

        var tool = new WriteToFileTool();
        var args = $$"""
            {"TargetFile": "{{target.Replace("\\", "\\\\")}}", "Overwrite": true, "CodeContent": "hello"}
            """;

        await tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance);

        Assert.Equal("hello", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task ListDirTool_rejects_a_directory_outside_every_allowed_directory()
    {
        var tool = new ListDirTool();
        var args = $$"""{"DirectoryPath": "{{_outsideRoot.Replace("\\", "\\\\")}}"}""";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => tool.ExecuteAsync(args, Context(), "test", NullLogger.Instance));
    }
}
