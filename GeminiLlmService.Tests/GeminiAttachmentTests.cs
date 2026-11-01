using AITaskAgent.LLM.Models;
using GeminiLlmService;

namespace GeminiLlmService.Tests;

public class GeminiAttachmentTests
{
    private const string PngB64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAHnOcQAAAAABJRU5ErkJggg==";

    [Fact]
    public void Attachments_become_inlineData_parts_after_the_text()
    {
        var message = new Message
        {
            Role = "user",
            Content = "mira",
            Attachments =
            [
                new MessageAttachment { Kind = AttachmentKind.Image, MediaType = "image/png", DataBase64 = PngB64 },
                MessageAttachment.FromBytes(AttachmentKind.Pdf, "application/pdf", new byte[] { 37, 80, 68, 70 })
            ]
        };

        var content = message.ToGeminiContent();

        Assert.Equal("user", content.Role);
        Assert.Equal(3, content.Parts!.Count);
        Assert.Equal("mira", content.Parts[0].Text);
        Assert.Equal("image/png", content.Parts[1].InlineData!.MimeType);
        Assert.Equal(Convert.FromBase64String(PngB64), content.Parts[1].InlineData!.Data);
        Assert.Equal("application/pdf", content.Parts[2].InlineData!.MimeType);
    }

    [Fact]
    public void Message_without_attachments_has_only_its_text_part()
    {
        var content = new Message { Role = "user", Content = "hola" }.ToGeminiContent();
        var part = Assert.Single(content.Parts!);
        Assert.Equal("hola", part.Text);
        Assert.Null(part.InlineData);
    }

    [Fact]
    public void Debug_log_redaction_replaces_the_inlineData_base64()
    {
        var content = new Message
        {
            Role = "user",
            Content = "x",
            Attachments = [new MessageAttachment { Kind = AttachmentKind.Image, MediaType = "image/png", DataBase64 = PngB64 }]
        }.ToGeminiContent();

        var json = GeminiLlmService.RedactInlineData(Newtonsoft.Json.Linq.JToken.FromObject(new[] { content })).ToString();

        Assert.DoesNotContain(PngB64, json, StringComparison.Ordinal);
        Assert.Contains("base64 chars omitted", json);
    }
}
