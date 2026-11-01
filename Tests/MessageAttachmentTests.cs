using AITaskAgent.LLM.Conversation;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Conversation.Storage;
using AITaskAgent.LLM.Models;

namespace AITaskAgent.Tests;

/// <summary>Attachments (images, PDFs) on user messages: API overload, token budget, sliding window, storage round-trip, ToString.</summary>
public sealed class MessageAttachmentTests
{
    private const string PngB64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAHnOcQAAAAABJRU5ErkJggg==";

    private static MessageAttachment Png() => new() { Kind = AttachmentKind.Image, MediaType = "image/png", DataBase64 = PngB64, Name = "a.png" };

    private static MessageAttachment Pdf(int bytes) =>
        MessageAttachment.FromBytes(AttachmentKind.Pdf, "application/pdf", new byte[bytes], "d.pdf");

    [Fact]
    public void AddUserMessage_without_attachments_is_the_same_message_as_before()
    {
        var a = new ConversationContext().AddUserMessage("hola");
        var b = new ConversationContext().AddUserMessage("hola", null);
        var c = new ConversationContext().AddUserMessage("hola", []);

        foreach (var ctx in new[] { a, b, c })
        {
            var m = Assert.Single(ctx.History.Messages);
            Assert.Equal("hola", m.Content);
            Assert.Null(m.Attachments);
        }
    }

    [Fact]
    public void AddUserMessage_keeps_the_attachments()
    {
        var ctx = new ConversationContext().AddUserMessage("mira", [Png(), Pdf(10)]);
        var m = Assert.Single(ctx.History.Messages);
        Assert.Equal(2, m.Attachments!.Count);
        Assert.Equal(AttachmentKind.Image, m.Attachments[0].Kind);
    }

    [Fact]
    public void FromBytes_and_Data_roundtrip()
    {
        var a = MessageAttachment.FromBytes(AttachmentKind.Image, "image/png", new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, a.Data.ToArray());
        Assert.Equal("data:image/png;base64,AQID", a.DataUrl);
    }

    [Fact]
    public void Token_estimate_is_fixed_per_image_and_per_pdf_page_not_the_base64_length()
    {
        var history = new MessageHistory();
        history.AddMessage(new Message { Role = "user", Content = "", Attachments = [Png()] });
        Assert.Equal(MessageAttachment.EstimatedTokensPerImage, history.GetTotalTokenCount());

        var pdf = new MessageHistory();
        pdf.AddMessage(new Message { Role = "user", Content = "", Attachments = [Pdf(MessageAttachment.BytesPerEstimatedPdfPage * 3)] });
        Assert.Equal(3 * MessageAttachment.EstimatedTokensPerPdfPage, pdf.GetTotalTokenCount());

        var tiny = new MessageHistory();
        tiny.AddMessage(new Message { Role = "user", Content = "", Attachments = [Pdf(10)] });
        Assert.Equal(MessageAttachment.EstimatedTokensPerPdfPage, tiny.GetTotalTokenCount());
    }

    [Fact]
    public void Sliding_window_counts_attachments_and_still_keeps_the_last_message()
    {
        var history = new MessageHistory(maxTokens: 1500);
        history.AddMessage(new Message { Role = "system", Content = "s" });
        history.AddMessage(new Message { Role = "user", Content = "old", Attachments = [Png()] });
        history.AddMessage(new Message { Role = "assistant", Content = "ok" });
        history.AddMessage(new Message { Role = "user", Content = "new", Attachments = [Png()] });

        var window = history.GetMessagesWithSlidingWindow(keepFirstN: 1, maxTokens: 1500);

        Assert.Contains(window, m => m.Content == "new" && m.Attachments is { Count: 1 });
        Assert.DoesNotContain(window, m => m.Content == "old");
        Assert.Equal(new[] { "s", "ok", "new" }, window.Select(m => m.Content));
    }

    [Fact]
    public void CopyFrom_keeps_the_attachments()
    {
        var src = new MessageHistory();
        src.AddMessage(new Message { Role = "user", Content = "x", Attachments = [Png()] });
        var dst = new MessageHistory();
        dst.CopyFrom(src);
        Assert.Equal(PngB64, dst.Messages[0].Attachments![0].DataBase64);
    }

    [Fact]
    public async Task Json_and_yaml_storage_keep_the_attachments()
    {
        var dir = Path.Combine(Path.GetTempPath(), "att-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ctx = new ConversationContext { ConversationId = "c1" };
            ctx.AddUserMessage("mira", [Png(), Pdf(5)]);
            ctx.AddAssistantMessage("vale");

            foreach (IConversationStorage storage in new IConversationStorage[]
                     { new FileConversationStorage(Path.Combine(dir, "json")), new YamlFileConversationStorage(Path.Combine(dir, "yaml")) })
            {
                await storage.SaveConversationAsync(ctx);
                var loaded = await storage.GetConversationAsync("c1");

                var user = loaded!.History.Messages[0];
                Assert.Equal(2, user.Attachments!.Count);
                Assert.Equal(PngB64, user.Attachments[0].DataBase64);
                Assert.Equal(AttachmentKind.Image, user.Attachments[0].Kind);
                Assert.Equal("image/png", user.Attachments[0].MediaType);
                Assert.Equal("a.png", user.Attachments[0].Name);
                Assert.Equal(AttachmentKind.Pdf, user.Attachments[1].Kind);
                Assert.Null(loaded.History.Messages[1].Attachments);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ToString_never_prints_the_base64()
    {
        Assert.DoesNotContain(PngB64, Png().ToString());
        Assert.Contains("image/png", Png().ToString());
    }
}
