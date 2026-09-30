using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using OpenAILLmService.Normalization;

namespace OpenAILLmService.Tests;

public class ThinkTagReasoningExtractorTests
{
    private static LlmResponse Response(string content) => new()
    {
        Content = content,
        FinishReason = FinishReason.Stop
    };

    [Fact]
    public void Moves_think_block_content_into_reasoning()
    {
        var extractor = new ThinkTagReasoningExtractor();
        var response = Response("<think>internal monologue</think>the actual answer");

        var normalized = extractor.NormalizeAssembled(response);

        Assert.Equal("the actual answer", normalized.Content);
        Assert.Equal("internal monologue", normalized.Reasoning);
    }

    [Fact]
    public void Leaves_response_untouched_when_there_is_no_think_block()
    {
        var extractor = new ThinkTagReasoningExtractor();
        var response = Response("plain answer, no tags");

        var normalized = extractor.NormalizeAssembled(response);

        Assert.Equal("plain answer, no tags", normalized.Content);
        Assert.Null(normalized.Reasoning);
    }

    [Fact]
    public void Streaming_chunk_split_across_the_open_tag_boundary_is_still_recognized()
    {
        var extractor = new ThinkTagReasoningExtractor();
        var ctx = new TagExtractionContext();

        // "<thi" then "nk>reasoning</think>answer" - the open tag itself is split mid-chunk.
        var first = extractor.ProcessChunk("<thi", ctx);
        var second = extractor.ProcessChunk("nk>reasoning</think>answer", ctx);

        Assert.Equal(string.Empty, first.Content);
        Assert.Equal(string.Empty, first.Reasoning);
        Assert.Equal("reasoning", second.Reasoning);
        Assert.Equal("answer", second.Content);
    }

    [Fact]
    public void Flush_treats_an_incomplete_open_tag_at_stream_end_as_plain_content()
    {
        var extractor = new ThinkTagReasoningExtractor();
        var ctx = new TagExtractionContext();

        var chunk = extractor.ProcessChunk("hello <th", ctx);
        var flushed = extractor.Flush(ctx);

        Assert.Equal("hello ", chunk.Content);
        Assert.Equal("<th", flushed.Content);
        Assert.Equal(string.Empty, flushed.Reasoning);
    }

    [Fact]
    public void Flush_keeps_an_incomplete_close_tag_at_stream_end_as_reasoning()
    {
        // Inside <think> a partial "</thi" is still reasoning: the stream was cut before the tag closed.
        var extractor = new ThinkTagReasoningExtractor();
        var ctx = new TagExtractionContext();

        var chunk = extractor.ProcessChunk("<think>pienso</thi", ctx);
        var flushed = extractor.Flush(ctx);

        Assert.Equal("pienso", chunk.Reasoning);
        Assert.Equal("</thi", flushed.Reasoning);
        Assert.Equal(string.Empty, flushed.Content);
    }
}
