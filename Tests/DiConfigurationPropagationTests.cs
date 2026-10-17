using AITaskAgent.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AITaskAgent.Tests;

/// <summary>
/// Options bound under <c>AITaskAgent:*</c> have to reach the objects the DI registration builds: the conversation
/// (KeepFirstNMessages, UseBookmarks) and the event channel (capacity per subscriber).
/// </summary>
public sealed class DiConfigurationPropagationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddAITaskAgent();
        return services.BuildServiceProvider();
    }

    private static ConversationContext Conversation(Dictionary<string, string?> values)
    {
        using var provider = Build(values);
        return provider.GetRequiredService<ConversationContext>();
    }

    // ── ConversationContext ──────────────────────────────────────────────────

    private static int MessagesFromBookmark(ConversationContext conversation)
    {
        conversation.AddUserMessage("one").AddUserMessage("two");
        var bookmark = conversation.CreateBookmark();
        conversation.AddUserMessage("three");
        return conversation.GetMessagesForRequest(fromBookmark: bookmark).Count;
    }

    [Fact]
    public void Conversation_from_DI_honours_UseBookmarks_false()
    {
        var conversation = Conversation(new() { ["AITaskAgent:Conversation:UseBookmarks"] = "false" });

        // Bookmarks disabled: the request carries the window, not only what follows the bookmark.
        Assert.Equal(3, MessagesFromBookmark(conversation));
    }

    [Fact]
    public void Conversation_from_DI_uses_bookmarks_by_default()
    {
        var conversation = Conversation(new());

        Assert.Equal(1, MessagesFromBookmark(conversation));
    }

    // ── EventChannel ─────────────────────────────────────────────────────────

    private static StepProgressEvent Event(int n) =>
        new() { StepName = "Probe", CorrelationId = "c", EventType = "step.progress", Message = $"m{n}" };

    /// <summary>
    /// Blocks the subscriber on its first event, sends five more and releases it: it receives the first event plus the
    /// newest <c>capacity</c> ones (DropOldest), so the delivered count reveals the capacity.
    /// </summary>
    private static async Task<int> DeliveredWithBlockedSubscriberAsync(EventChannel channel)
    {
        var delivered = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Subscribe(async (_, _) =>
        {
            Interlocked.Increment(ref delivered);
            firstStarted.TrySetResult();
            await gate.Task;
        });

        await channel.SendAsync(Event(1));
        await firstStarted.Task;
        for (var i = 2; i <= 6; i++)
            await channel.SendAsync(Event(i));

        gate.SetResult();
        await channel.DisposeAsync();
        return delivered;
    }

    [Fact]
    public async Task EventChannelCapacity_from_configuration_reaches_the_registered_channel()
    {
        await using var provider = Build(new() { ["AITaskAgent:Observability:EventChannelCapacity"] = "2" });
        var channel = Assert.IsType<EventChannel>(provider.GetRequiredService<IEventChannel>());

        Assert.Equal(3, await DeliveredWithBlockedSubscriberAsync(channel));
    }

    [Fact]
    public async Task The_registered_channel_defaults_to_a_capacity_of_1000()
    {
        await using var provider = Build(new());
        var channel = Assert.IsType<EventChannel>(provider.GetRequiredService<IEventChannel>());

        Assert.Equal(6, await DeliveredWithBlockedSubscriberAsync(channel));
        Assert.Equal(1000, new ObservabilityOptions().EventChannelCapacity);
    }

    [Fact]
    public async Task The_options_constructor_sets_the_capacity()
    {
        var channel = new EventChannel(new EventChannelOptions { ChannelCapacity = 2 });

        Assert.Equal(3, await DeliveredWithBlockedSubscriberAsync(channel));
    }

    [Fact]
    public void A_capacity_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventChannel(new EventChannelOptions { ChannelCapacity = 0 }));
    }
}
