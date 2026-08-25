using AITaskAgent.LLM.Models;
using System.Collections.Concurrent;
using System.Text;

namespace OpenAILLmService.Normalization;

public readonly struct ChunkResult
{
    public string Content   { get; init; }
    public string Reasoning { get; init; }

    public bool HasContent   => Content.Length > 0;
    public bool HasReasoning => Reasoning.Length > 0;
}

public sealed class TagExtractionContext
{
    public ParserState State { get; set; } = ParserState.Text;

    public TagDefinition? CurrentTag { get; set; }

    public StringBuilder MatchBuffer { get; } = new();

    public Dictionary<string, object> StateBag { get; } = [];
}

public sealed class TagDefinition
{
    public required string Name     { get; init; }
    public required string OpenTag  { get; init; }
    public required string CloseTag { get; init; }
}

public enum ParserState
{
    Text,
    OpenTagMatch,
    InsideTag,
    CloseTagMatch
}

public sealed class StreamingTagParser
{
    private readonly TagDefinition[] _tags;

    public StreamingTagParser(IEnumerable<TagDefinition> tags)
    {
        _tags = [.. tags.OrderByDescending(t => t.OpenTag.Length)];

        if (_tags.Length == 0)
            throw new ArgumentException("At least one tag must be provided.");
    }

    public ChunkResult ProcessChunk(string delta, TagExtractionContext ctx)
    {
        var content   = new StringBuilder();
        var reasoning = new StringBuilder();

        foreach (var c in delta)
        {
            switch (ctx.State)
            {
                case ParserState.Text:
                    HandleText(c, ctx, content);
                    break;

                case ParserState.OpenTagMatch:
                    HandleOpenMatch(c, ctx, content, reasoning);
                    break;

                case ParserState.InsideTag:
                    HandleInsideTag(c, ctx, content, reasoning);
                    break;

                case ParserState.CloseTagMatch:
                    HandleCloseMatch(c, ctx, content, reasoning);
                    break;
            }
        }

        return new ChunkResult
        {
            Content   = content.ToString(),
            Reasoning = reasoning.ToString()
        };
    }

    // ─────────────────────────────────────────────

    private static void HandleText(char c, TagExtractionContext ctx, StringBuilder content)
    {
        if (c != '<')
        {
            content.Append(c);
            return;
        }

        ctx.MatchBuffer.Clear();
        ctx.MatchBuffer.Append('<');
        ctx.State = ParserState.OpenTagMatch;
    }

    private void HandleOpenMatch(char c, TagExtractionContext ctx, StringBuilder content, StringBuilder reasoning)
    {
        ctx.MatchBuffer.Append(c);

        var match = TryMatchOpen(ctx.MatchBuffer.ToString(), out var tag, out var complete);

        if (!complete && match == MatchResult.Partial)
            return;

        if (match == MatchResult.Full)
        {
            ctx.CurrentTag = tag;
            ctx.State = ParserState.InsideTag;
            ctx.MatchBuffer.Clear();
            return;
        }

        content.Append(ctx.MatchBuffer);
        ctx.MatchBuffer.Clear();
        ctx.State = ParserState.Text;
    }

    private static void HandleInsideTag(char c, TagExtractionContext ctx, StringBuilder content, StringBuilder reasoning)
    {
        if (c != '<')
        {
            WriteByTag(ctx, c, content, reasoning);
            return;
        }

        ctx.MatchBuffer.Clear();
        ctx.MatchBuffer.Append('<');
        ctx.State = ParserState.CloseTagMatch;
    }

    private static void HandleCloseMatch(char c, TagExtractionContext ctx, StringBuilder content, StringBuilder reasoning)
    {
        ctx.MatchBuffer.Append(c);

        var match = TryMatchClose(ctx.MatchBuffer.ToString(), ctx.CurrentTag!, out var complete);

        if (!complete && match == MatchResult.Partial)
            return;

        if (match == MatchResult.Full)
        {
            ctx.CurrentTag = null;
            ctx.State = ParserState.Text;
            ctx.MatchBuffer.Clear();
            return;
        }

        WriteByTag(ctx, ctx.MatchBuffer.ToString(), content, reasoning);
        ctx.MatchBuffer.Clear();
        ctx.State = ParserState.InsideTag;
    }

    // ─────────────────────────────────────────────

    private static void WriteByTag(TagExtractionContext ctx, char c, StringBuilder content, StringBuilder reasoning)
    {
        if (ctx.CurrentTag != null && IsReasoningTag(ctx.CurrentTag))
            reasoning.Append(c);
        else
            content.Append(c);
    }

    private static void WriteByTag(TagExtractionContext ctx, string value, StringBuilder content, StringBuilder reasoning)
    {
        if (ctx.CurrentTag != null && IsReasoningTag(ctx.CurrentTag))
            reasoning.Append(value);
        else
            content.Append(value);
    }

    private static bool IsReasoningTag(TagDefinition tag)
        => tag.Name.Equals("think", StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────

    private enum MatchResult { None, Partial, Full }

    private MatchResult TryMatchOpen(string buffer, out TagDefinition? tag, out bool complete)
    {
        tag = null;
        complete = false;

        foreach (var t in _tags)
        {
            if (t.OpenTag.StartsWith(buffer, StringComparison.OrdinalIgnoreCase))
            {
                if (t.OpenTag.Equals(buffer, StringComparison.OrdinalIgnoreCase))
                {
                    tag = t;
                    complete = true;
                    return MatchResult.Full;
                }

                return MatchResult.Partial;
            }
        }

        return MatchResult.None;
    }

    private static MatchResult TryMatchClose(string buffer, TagDefinition tag, out bool complete)
    {
        complete = false;

        if (tag.CloseTag.StartsWith(buffer, StringComparison.OrdinalIgnoreCase))
        {
            if (tag.CloseTag.Equals(buffer, StringComparison.OrdinalIgnoreCase))
            {
                complete = true;
                return MatchResult.Full;
            }

            return MatchResult.Partial;
        }

        return MatchResult.None;
    }

    // ─────────────────────────────────────────────
    // FLUSH
    // ─────────────────────────────────────────────

    public static ChunkResult Flush(TagExtractionContext ctx)
    {
        var pending = ctx.MatchBuffer.ToString();
        ctx.MatchBuffer.Clear();

        return ctx.State switch
        {
            ParserState.InsideTag => new ChunkResult { Reasoning = pending },
            _                      => new ChunkResult { Content   = pending }
        };
    }

    // ─────────────────────────────────────────────
    // NON-STREAMING
    // ─────────────────────────────────────────────

    public LlmResponse NormalizeAssembled(LlmResponse response)
    {
        if (string.IsNullOrEmpty(response.Content))
            return response;

        var ctx = new TagExtractionContext();

        var result = ProcessChunk(response.Content, ctx);
        var flushed = Flush(ctx);

        var content   = result.Content + flushed.Content;
        var reasoning = result.Reasoning + flushed.Reasoning;

        if (reasoning.Length == 0)
            return response;

        return response with
        {
            Content   = content,
            Reasoning = reasoning
        };
    }
}

// ─────────────────────────────────────────────

public sealed class ThinkTagReasoningExtractor : IReasoningExtractor
{
    private readonly StreamingTagParser _parser = new([
        new TagDefinition
        {
            Name     = "think",
            OpenTag  = "<think>",
            CloseTag = "</think>"
        }
    ]);

    public ChunkResult ProcessChunk(string delta, TagExtractionContext ctx)
        => _parser.ProcessChunk(delta, ctx);

    public ChunkResult Flush(TagExtractionContext ctx)
        => StreamingTagParser.Flush(ctx);

    public LlmResponse NormalizeAssembled(LlmResponse response)
        => _parser.NormalizeAssembled(response);
}