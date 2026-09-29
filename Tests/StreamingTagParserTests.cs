using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Streaming;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.Tests;

/// <summary>
/// Grammar and observability of the one tag scanner, independently of any step or transport.
/// </summary>
public sealed class StreamingTagParserTests
{
    /// <summary>Records every complete tag; derives from the base class so the completed event is emitted.</summary>
    private sealed class RecordingHandler : TagHandlerBase
    {
        public List<(Dictionary<string, string> Attributes, string Body)> Calls { get; } = [];

        public override string TagName => "write_file";

        public override string GetInstructions() => "### Tag: write_file";

        protected override ILogger Logger => NullLogger.Instance;

        protected override Task<string?> InternalOnCompleteTagAsync(
            Dictionary<string, string> attributes, string content, PipelineContext context, CancellationToken cancellationToken)
        {
            Calls.Add((attributes, content));
            return Task.FromResult<string?>($"[write_file: {attributes.GetValueOrDefault("path")}]");
        }
    }

    private static async Task<(string Output, List<IProgressEvent> Events, RecordingHandler Handler)> ScanAsync(
        string text, int chunkSize = int.MaxValue)
    {
        var handler = new RecordingHandler();
        var parser = new StreamingTagParser([handler]);
        var events = new List<IProgressEvent>();
        var channel = new EventChannel();
        var subscription = channel.Subscribe((ev, _) => { lock (events) events.Add(ev); return Task.CompletedTask; });
        var context = new PipelineContext(channel);

        var output = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var slice = text.Substring(i, Math.Min(chunkSize, text.Length - i));
            output.Append(await parser.ProcessChunkAsync(slice, context, CancellationToken.None));
        }
        output.Append(await parser.FlushAsync(context, CancellationToken.None));

        subscription.Dispose();
        await channel.DisposeAsync();

        lock (events) return (output.ToString(), [.. events], handler);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task The_line_break_after_the_opening_tag_and_before_the_closing_tag_are_syntax_not_body(string nl)
    {
        var (_, _, handler) = await ScanAsync($"<write_file path=\"a.txt\">{nl}line 1{nl}line 2{nl}</write_file>");

        Assert.Equal($"line 1{nl}line 2", Assert.Single(handler.Calls).Body);
    }

    [Fact]
    public async Task Only_one_line_break_is_removed_at_each_end()
    {
        var (_, _, handler) = await ScanAsync("<write_file path=\"a.txt\">\n\nbody\n\n</write_file>");

        Assert.Equal("\nbody\n", Assert.Single(handler.Calls).Body);
    }

    [Fact]
    public async Task An_inline_body_is_kept_verbatim()
    {
        var (output, _, handler) = await ScanAsync("antes <write_file path=\"a.txt\">body</write_file> después");

        Assert.Equal("body", Assert.Single(handler.Calls).Body);
        Assert.Equal("antes [write_file: a.txt] después", output);
    }

    [Theory]
    [InlineData("si x < \"umbral\" entonces y")]
    [InlineData("a <= b y c < d")]
    [InlineData("me gusta <3 mucho")]
    [InlineData("ver </x> literal")]
    public async Task A_less_than_sign_that_cannot_open_a_tag_comes_back_at_once(string text)
    {
        var handler = new RecordingHandler();
        var parser = new StreamingTagParser([handler]);
        var context = new PipelineContext();

        // Fed character by character: the text must come out as it goes in, not after the
        // scanner has buffered several words waiting for a '>' that never comes.
        var output = new System.Text.StringBuilder();
        var maxHeldBack = 0;
        var fed = 0;
        foreach (var ch in text)
        {
            fed++;
            output.Append(await parser.ProcessChunkAsync(ch.ToString(), context, CancellationToken.None));
            maxHeldBack = Math.Max(maxHeldBack, fed - output.Length);
        }
        output.Append(await parser.FlushAsync());

        Assert.Equal(text, output.ToString());
        Assert.Empty(handler.Calls);
        // Only the '<' itself may wait for the next character.
        Assert.True(maxHeldBack <= 1, $"the scanner held back {maxHeldBack} characters on plain text");
    }

    [Fact]
    public async Task Started_event_is_emitted_when_the_opening_tag_is_recognised_and_completed_when_it_closes()
    {
        var (_, events, _) = await ScanAsync("<write_file path=\"a.txt\" summary=\"s\">\nbody\n</write_file>", chunkSize: 3);

        var started = Assert.Single(events.OfType<TagStartedEvent>());
        Assert.Equal("write_file", started.TagName);
        Assert.Equal("a.txt", started.Attributes?["path"]);

        var completed = Assert.Single(events.OfType<TagCompletedEvent>());
        Assert.True(completed.Success);
        Assert.True(events.IndexOf(started) < events.IndexOf(completed));
    }

    [Fact]
    public async Task A_tag_that_never_closes_is_reported_as_a_failed_completion_and_runs_nothing()
    {
        var (output, events, handler) = await ScanAsync("<write_file path=\"a.txt\">\nbody without end", chunkSize: 4);

        Assert.Empty(handler.Calls);
        Assert.Equal("<write_file path=\"a.txt\">\nbody without end", output);
        Assert.Single(events.OfType<TagStartedEvent>());
        var completed = Assert.Single(events.OfType<TagCompletedEvent>());
        Assert.False(completed.Success);
        Assert.Contains("never closed", completed.ErrorMessage);
    }

    [Fact]
    public async Task Unknown_tags_pass_through_without_events()
    {
        var (output, events, handler) = await ScanAsync("<b>bold</b> y <br/> y List<string>");

        Assert.Equal("<b>bold</b> y <br/> y List<string>", output);
        Assert.Empty(handler.Calls);
        Assert.Empty(events.OfType<TagStartedEvent>());
    }
}
