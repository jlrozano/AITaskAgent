using AITaskAgent.Core.Models;
using AITaskAgent.FileTools.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.FileTools.Tests;

/// <summary>
/// WriteFileTagHandler is a second, independent sandbox: the &lt;write_file&gt; XML tag an LLM can
/// emit inline in its response text, checked against its own constructor-supplied baseDirectory
/// rather than FileToolsConfiguration. It needs its own traversal coverage - a fix to the tool
/// sandbox would not touch this path at all.
/// </summary>
public class WriteFileTagHandlerTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(Path.GetTempPath(), "fta-tests-tag-" + Guid.NewGuid());

    public WriteFileTagHandlerTests() => Directory.CreateDirectory(_baseDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_baseDirectory, recursive: true); } catch { /* best effort */ }
    }

    private WriteFileTagHandler Handler() => new(_baseDirectory, NullLogger<WriteFileTagHandler>.Instance);

    [Fact]
    public async Task Rejects_a_relative_traversal_out_of_the_base_directory()
    {
        var attributes = new Dictionary<string, string> { ["path"] = "../escaped.txt" };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Handler().OnCompleteTagAsync(attributes, "pwned", new PipelineContext(), CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_baseDirectory)!, "escaped.txt")));
    }

    [Fact]
    public async Task Writes_a_file_inside_the_base_directory()
    {
        var attributes = new Dictionary<string, string> { ["path"] = "notes/todo.md" };

        var placeholder = await Handler().OnCompleteTagAsync(
            attributes, "buy milk", new PipelineContext(), CancellationToken.None);

        var written = Path.Combine(_baseDirectory, "notes", "todo.md");
        Assert.Equal("buy milk", await File.ReadAllTextAsync(written));
        Assert.NotNull(placeholder);
    }

    [Fact]
    public async Task Uses_the_provided_summary_as_the_placeholder()
    {
        var attributes = new Dictionary<string, string>
        {
            ["path"] = "file.txt",
            ["summary"] = "Created the config file",
        };

        var placeholder = await Handler().OnCompleteTagAsync(
            attributes, "content", new PipelineContext(), CancellationToken.None);

        Assert.Equal("Created the config file", placeholder);
    }

    [Fact]
    public async Task Throws_when_the_path_attribute_is_missing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Handler().OnCompleteTagAsync(
                new Dictionary<string, string>(), "content", new PipelineContext(), CancellationToken.None));
    }
}
