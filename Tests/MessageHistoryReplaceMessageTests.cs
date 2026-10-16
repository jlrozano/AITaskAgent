using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;

namespace AITaskAgent.Tests;

/// <summary>
/// ReplaceMessage swaps one message in place: same count, same position, bookmarks untouched.
/// </summary>
public sealed class MessageHistoryReplaceMessageTests
{
    private static Message Msg(string role, string content, string? toolCallId = null) =>
        new() { Role = role, Content = content, ToolCallId = toolCallId };

    [Fact]
    public void Replaces_the_message_at_the_index_and_keeps_order_and_bookmarks()
    {
        var history = new MessageHistory();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u1"));
        var bookmark = history.CreateBookmark();
        history.AddMessage(Msg(LlmConstants.MessageRoles.Tool, "long result", "c1"));
        history.AddMessage(Msg(LlmConstants.MessageRoles.Assistant, "a1"));

        history.ReplaceMessage(1, history.Messages[1] with { Content = "note" });

        Assert.Equal(["u1", "note", "a1"], history.Messages.Select(m => m.Content));
        Assert.Equal("c1", history.Messages[1].ToolCallId);
        Assert.Equal(1, history.Bookmarks[bookmark]);
        Assert.Equal(2, history.GetMessagesFromBookmark(bookmark).Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Out_of_range_index_throws(int index)
    {
        var history = new MessageHistory();
        history.AddMessage(Msg(LlmConstants.MessageRoles.User, "u1"));

        Assert.Throws<ArgumentOutOfRangeException>(() => history.ReplaceMessage(index, Msg(LlmConstants.MessageRoles.User, "x")));
        Assert.Equal("u1", history.Messages[0].Content);
    }

    [Fact]
    public void Conversation_shortcut_replaces_and_updates_last_activity()
    {
        var conversation = new ConversationContext();
        conversation.AddUserMessage("u1");
        conversation.LastActivityAt = DateTime.UtcNow.AddHours(-1);
        var before = conversation.LastActivityAt;

        conversation.ReplaceMessage(0, Msg(LlmConstants.MessageRoles.User, "u1'"));

        Assert.Equal("u1'", conversation.History.Messages[0].Content);
        Assert.True(conversation.LastActivityAt > before);
    }
}
