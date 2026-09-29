using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;

namespace AITaskAgent.Tests;

/// <summary>
/// The sliding window must never hand a provider a `tool` message whose requesting assistant
/// message fell outside the window - OpenAI rejects that with 400, and the OpenAI service only
/// started applying the window at all in this change.
/// </summary>
public sealed class MessageHistorySlidingWindowTests
{
    // One token per character, so budgets are exact.
    private static MessageHistory History() => new(maxTokens: 1000, tokenCounter: s => s.Length);

    private static Message Msg(string role, string content, string? toolCallId = null) =>
        new() { Role = role, Content = content, ToolCallId = toolCallId };

    [Fact]
    public void Orphaned_tool_results_at_the_start_of_the_window_are_dropped()
    {
        var history = History();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u1"));          // kept: first 2
        history.AddMessage(Msg(LlmConstants.MessageRoles.Assistant, "a1"));     // kept: first 2
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u2"));
        history.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.Assistant,
            Content = "let me look",                                                 // 11 tokens
            ToolCalls = [new ToolCall { Id = "c1", Name = "list_dir", Arguments = "{}" }]
        });
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "0123456789", "c1")); // 10 tokens
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "0123456789", "c1")); // 10 tokens
        history.AddMessage(Msg(LlmConstants.MessageRoles.Assistant, "final"));       // 5 tokens

        // 4 (first two) + 5 + 10 + 10 = 29 fits; the assistant(tool_calls) message before them
        // (11 more) does not, so both tool results are orphaned and must go with it.
        var window = history.GetMessagesWithSlidingWindow(keepFirstN: 2, maxTokens: 29);

        Assert.Equal(["u1", "a1", "final"], window.Select(m => m.Content));
        Assert.DoesNotContain(window, m => m.Role == LlmConstants.MessageRoles.Tool);
    }

    /// <summary>
    /// Reproduces a real Azure OpenAI 400 ("tool_call_id did not have response message"): the
    /// second-ever LLM call of a turn sees exactly [user, assistant(tool_calls), tool] - only 3
    /// messages, so the assistant lands in the always-kept keepFirstN=2 head and the tool result
    /// lands in recentMessages. The orphan check used to strip ANY leading tool message from
    /// recentMessages without checking whether its owner was actually excluded, so it dropped a
    /// tool result whose assistant message was right there in the request.
    /// </summary>
    [Fact]
    public void A_tool_result_whose_owner_is_in_the_always_kept_head_is_not_dropped()
    {
        var history = History();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u1"));
        history.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.Assistant,
            Content = "",
            ToolCalls = [new ToolCall { Id = "c1", Name = "find_by_name", Arguments = "{}" }]
        });
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "result", "c1"));

        var window = history.GetMessagesWithSlidingWindow(keepFirstN: 2, maxTokens: 1000);

        Assert.Equal(3, window.Count);
        Assert.Equal(LlmConstants.MessageRoles.Tool, window[2].Role);
    }

    /// <summary>
    /// Reproduces a real Azure OpenAI 400 from the CodebaseAnalysisPipeline sample: a stateless
    /// step whose prompt alone exceeds the budget. The assistant(tool_calls) message is in the
    /// always-kept head, so it is sent; its tool results are not, because the head already spent
    /// the budget - and a request with tool calls but no tool responses is invalid. The results
    /// must travel with their assistant message even if that exceeds the budget.
    /// </summary>
    [Fact]
    public void Tool_results_of_an_assistant_in_the_head_are_kept_even_over_budget()
    {
        var history = History();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, new string('x', 50)));   // 50 tokens
        history.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.Assistant,
            Content = "",
            ToolCalls =
            [
                new ToolCall { Id = "c1", Name = "view_file", Arguments = "{}" },
                new ToolCall { Id = "c2", Name = "view_file", Arguments = "{}" }
            ]
        });
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "result one", "c1"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "result two", "c2"));

        var window = history.GetMessagesWithSlidingWindow(keepFirstN: 2, maxTokens: 20);

        Assert.Equal(4, window.Count);
        Assert.Equal(["c1", "c2"], window.Where(m => m.Role == LlmConstants.MessageRoles.Tool).Select(m => m.ToolCallId));
    }

    [Fact]
    public void A_complete_tool_group_that_fits_is_kept_whole()
    {
        var history = History();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u1"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Assistant, "a1"));
        history.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.Assistant,
            Content = "",
            ToolCalls = [new ToolCall { Id = "c1", Name = "list_dir", Arguments = "{}" }]
        });
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "result", "c1"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Assistant, "final"));

        var window = history.GetMessagesWithSlidingWindow(keepFirstN: 2, maxTokens: 1000);

        Assert.Equal(5, window.Count);
        Assert.Equal(LlmConstants.MessageRoles.Tool, window[3].Role);
    }

    [Fact]
    public void Nothing_is_dropped_when_the_history_fits()
    {
        var history = History();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u1"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Assistant, "a1"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u2"));

        Assert.Equal(3, history.GetMessagesWithSlidingWindow(keepFirstN: 2, maxTokens: 1000).Count);
    }

    private static Message ToolCalls(params string[] ids) => new()
    {
        Role = LlmConstants.MessageRoles.Assistant,
        Content = "",
        ToolCalls = [.. ids.Select(id => new ToolCall { Id = id, Name = "view_file", Arguments = "{}" })]
    };

    /// <summary>
    /// A head that already spends the budget used to leave the window empty after it: the model
    /// never saw the results of its latest tool calls and kept repeating them.
    /// </summary>
    [Fact]
    public void The_last_tool_group_is_kept_even_when_the_head_spends_the_budget()
    {
        var history = History();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, new string('x', 50)));
        history.AddMessage(ToolCalls("c1"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "result one", "c1"));
        history.AddMessage(ToolCalls("c2", "c3"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "result two", "c2"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "result three", "c3"));

        var window = history.GetMessagesWithSlidingWindow(keepFirstN: 2, maxTokens: 20);

        Assert.Equal(6, window.Count);
        Assert.Equal(["c1", "c2", "c3"], window.Where(m => m.Role == LlmConstants.MessageRoles.Tool).Select(m => m.ToolCallId));
    }

    [Fact]
    public void The_last_message_is_kept_even_over_budget()
    {
        var history = History();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u1"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Assistant, "a1"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Assistant, "a2"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, new string('y', 50)));

        var window = history.GetMessagesWithSlidingWindow(keepFirstN: 2, maxTokens: 10);

        Assert.Equal(["u1", "a1", new string('y', 50)], window.Select(m => m.Content));
    }

    /// <summary>
    /// Reproduces the Azure 400 of the CodebaseAnalysisPipeline sample with UseSlidingWindow=false:
    /// eight large tool results exceeded the budget, trimming backwards cut the run in the middle and
    /// the request started with a tool message whose assistant(tool_calls) message was missing.
    /// </summary>
    [Fact]
    public void Recent_messages_keep_the_last_tool_group_whole_even_over_budget()
    {
        var history = History();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "prompt"));
        history.AddMessage(ToolCalls("c1", "c2"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "0123456789", "c1"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "0123456789", "c2"));

        var recent = history.GetRecentMessages(maxTokens: 15);

        Assert.Equal(3, recent.Count);
        Assert.Equal(LlmConstants.MessageRoles.Assistant, recent[0].Role);
    }

    [Fact]
    public void Recent_messages_never_start_with_an_orphaned_tool_result()
    {
        var history = History();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u1"));
        history.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.Assistant,
            Content = "let me look",                                                 // 11 tokens
            ToolCalls = [new ToolCall { Id = "c1", Name = "list_dir", Arguments = "{}" }]
        });
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "01234", "c1"));      // 5 tokens
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u2"));               // 2 tokens

        // u2 + the tool result fit in 10; their assistant(tool_calls) message does not.
        var recent = history.GetRecentMessages(maxTokens: 10);

        Assert.Equal(["u2"], recent.Select(m => m.Content));
    }

    [Fact]
    public void Without_sliding_window_the_whole_history_is_sent_regardless_of_the_budget()
    {
        var conversation = new ConversationContext(maxTokens: 1000, tokenCounter: s => s.Length);
        conversation.History.AddMessage(Msg(LlmConstants.MessageRoles.User, new string('x', 50)));
        conversation.History.AddMessage(ToolCalls("c1"));
        conversation.History.AddMessage(Msg(LlmConstants.MessageRoles.Tool, new string('r', 50), "c1"));

        var messages = conversation.GetMessagesForRequest(maxTokens: 10, useSlidingWindow: false);

        Assert.Equal(3, messages.Count);
    }
}
