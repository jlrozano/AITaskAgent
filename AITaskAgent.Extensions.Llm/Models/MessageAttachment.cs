using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AITaskAgent.LLM.Models;

/// <summary>Kind of binary content a <see cref="MessageAttachment"/> carries.</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum AttachmentKind
{
    /// <summary>An image (PNG, JPEG, WebP or GIF).</summary>
    Image,

    /// <summary>A PDF document. Only providers with a native file input accept it (OpenAI/Azure Responses API, Gemini).</summary>
    Pdf
}

/// <summary>
/// A binary attachment (image or PDF) of a user <see cref="Message"/>. The content travels as base64 text so the
/// record serializes as plain JSON/YAML. It is never written to logs: <see cref="ToString"/> and the services' request
/// logging replace it with its size.
/// </summary>
public sealed record MessageAttachment
{
    /// <summary>Estimated tokens of one image, used by <see cref="Conversation.MessageHistory"/> budgeting (providers bill roughly 0.5-1.5k).</summary>
    public const int EstimatedTokensPerImage = 1000;

    /// <summary>Estimated tokens of one PDF page; the page count is estimated as one page per <see cref="BytesPerEstimatedPdfPage"/> bytes (minimum 1).</summary>
    public const int EstimatedTokensPerPdfPage = 1500;

    /// <summary>Bytes taken as one PDF page for the token estimate.</summary>
    public const int BytesPerEstimatedPdfPage = 50_000;

    /// <summary>Image or PDF.</summary>
    public required AttachmentKind Kind { get; init; }

    /// <summary>MIME type, e.g. <c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>, <c>image/gif</c>, <c>application/pdf</c>.</summary>
    public required string MediaType { get; init; }

    /// <summary>The content in base64 (no <c>data:</c> prefix).</summary>
    public required string DataBase64 { get; init; }

    /// <summary>Optional file name (sent as <c>filename</c> for PDFs).</summary>
    public string? Name { get; init; }

    /// <summary>Builds an attachment from raw bytes.</summary>
    public static MessageAttachment FromBytes(AttachmentKind kind, string mediaType, ReadOnlyMemory<byte> data, string? name = null) => new()
    {
        Kind = kind,
        MediaType = mediaType,
        Name = name,
        DataBase64 = Convert.ToBase64String(data.Span)
    };

    /// <summary>Builds an attachment from a <see cref="BinaryData"/>.</summary>
    public static MessageAttachment FromBinaryData(AttachmentKind kind, string mediaType, BinaryData data, string? name = null) =>
        FromBytes(kind, mediaType, data.ToMemory(), name);

    /// <summary>The content as <see cref="BinaryData"/>.</summary>
    [JsonIgnore]
    public BinaryData Data => BinaryData.FromBytes(Convert.FromBase64String(DataBase64));

    /// <summary>The content as a <c>data:</c> URL (<c>data:{MediaType};base64,{DataBase64}</c>).</summary>
    [JsonIgnore]
    public string DataUrl => $"data:{MediaType};base64,{DataBase64}";

    /// <summary>Approximate decoded size in bytes.</summary>
    [JsonIgnore]
    public long ApproximateSizeBytes => DataBase64.Length / 4L * 3;

    /// <summary>Token estimate used by the history budget (see the constants).</summary>
    public int EstimateTokens() => Kind == AttachmentKind.Pdf
        ? (int)Math.Max(1, ApproximateSizeBytes / BytesPerEstimatedPdfPage) * EstimatedTokensPerPdfPage
        : EstimatedTokensPerImage;

    // Keeps the base64 out of any log that prints the record.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}, MediaType = {MediaType}, Name = {Name}, Size = ~{ApproximateSizeBytes} bytes");
        return true;
    }
}
