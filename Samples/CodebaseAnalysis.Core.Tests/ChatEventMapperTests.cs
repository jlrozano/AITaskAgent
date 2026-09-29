using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Progress;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tests;

public sealed class ChatEventMapperTests
{
    [Fact]
    public void Streaming_chunks_become_delta_or_thinking()
    {
        var delta = Assert.Single(ChatEventMapper.Map(new LlmResponseEvent { Content = "Hola", FinishReason = FinishReason.Streaming }));
        var thinking = Assert.Single(ChatEventMapper.Map(new LlmResponseEvent { Content = "pienso", FinishReason = FinishReason.Streaming, IsThinking = true }));

        Assert.Equal("delta", delta.Type);
        Assert.Equal("Hola", delta.Fields["text"]);
        Assert.Equal("thinking", thinking.Type);
        Assert.Equal("pienso", thinking.Fields["text"]);
    }

    [Fact]
    public void Final_event_of_a_streamed_iteration_without_tools_emits_nothing()
    {
        Assert.Empty(ChatEventMapper.Map(new LlmResponseEvent { Content = string.Empty, FinishReason = FinishReason.Stop, ToolCalls = [] }));
    }

    [Fact]
    public void Final_event_with_tool_calls_closes_the_segment()
    {
        var events = ChatEventMapper.Map(new LlmResponseEvent
        {
            Content = string.Empty,
            FinishReason = FinishReason.ToolCalls,
            ToolCalls = [new ToolCall { Id = "call_1", Name = "list_reports", Arguments = "{}" }]
        });

        var calls = Assert.Single(events);
        Assert.Equal("tool.calls", calls.Type);
        var call = Assert.Single((IEnumerable<Dictionary<string, object?>>)calls.Fields["calls"]!);
        Assert.Equal("call_1", call["id"]);
        Assert.Equal("list_reports", call["name"]);
        Assert.Equal("{}", call["arguments"]);
    }

    [Fact]
    public void Without_streaming_the_final_content_is_a_single_delta_before_the_tool_calls()
    {
        var events = ChatEventMapper.Map(new LlmResponseEvent
        {
            Content = "Respuesta completa",
            FinishReason = FinishReason.ToolCalls,
            ToolCalls = [new ToolCall { Id = "c", Name = "view_file", Arguments = "{}" }]
        });

        Assert.Equal(["delta", "tool.calls"], events.Select(e => e.Type));
        Assert.Equal("Respuesta completa", events[0].Fields["text"]);
    }

    [Fact]
    public void Other_choices_are_ignored()
    {
        Assert.Empty(ChatEventMapper.Map(new LlmResponseEvent { Content = "x", FinishReason = FinishReason.Streaming, ChoiceIndex = 1 }));
    }

    [Fact]
    public void Tool_events()
    {
        var started = Assert.Single(ChatEventMapper.Map(new ToolStartedEvent { ToolName = "read_report" }));
        var completed = Assert.Single(ChatEventMapper.Map(new ToolCompletedEvent
        {
            ToolName = "read_report",
            Success = false,
            Duration = TimeSpan.FromMilliseconds(12.4),
            ErrorMessage = "no existe"
        }));

        Assert.Equal("tool.started", started.Type);
        Assert.Equal("read_report", started.Fields["name"]);
        Assert.Equal("tool.completed", completed.Type);
        Assert.Equal(false, completed.Fields["success"]);
        Assert.Equal(12L, completed.Fields["durationMs"]);
        Assert.Equal("no existe", completed.Fields["error"]);
    }

    [Fact]
    public void Unrelated_events_are_ignored()
    {
        Assert.Empty(ChatEventMapper.Map(new StepStartedEvent { StepName = "ChatAssistant" }));
        Assert.Empty(ChatEventMapper.Map(new PipelineStartedEvent { PipelineName = "CodebaseChat" }));
    }

    [Fact]
    public void Payload_puts_the_type_first_and_the_fields_at_the_root()
    {
        var evt = new ChatStreamEvent("delta", new Dictionary<string, object?> { ["text"] = "á" });

        var json = JsonSerializer.Serialize(evt.ToPayload());

        Assert.Equal("{\"type\":\"delta\",\"text\":\"\\u00E1\"}", json);
    }
}
