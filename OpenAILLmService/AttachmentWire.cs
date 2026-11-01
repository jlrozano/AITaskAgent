using AITaskAgent.LLM.Models;
using Newtonsoft.Json.Linq;

namespace OpenAILLmService;

/// <summary>Wire format of message attachments for the OpenAI-family APIs, and base64 redaction for logs.</summary>
internal static class AttachmentWire
{
    internal const string PdfNeedsResponsesApi =
        "PDF solo con Responses API: la API de chat completions no admite ficheros PDF. " +
        "Use el servicio Responses (OpenAIResponses / AzureOpenAIResponses) o adjunte el contenido como texto o imagen.";

    /// <summary>Chat completions: <c>content</c> of a user message. A plain string when there are no attachments.</summary>
    public static JToken ChatUserContent(Message message)
    {
        if (message.Attachments is not { Count: > 0 })
            return message.Content is null ? JValue.CreateNull() : new JValue(message.Content);

        var parts = new JArray();
        if (!string.IsNullOrEmpty(message.Content))
            parts.Add(new JObject { ["type"] = "text", ["text"] = message.Content });

        foreach (var a in message.Attachments)
        {
            if (a.Kind == AttachmentKind.Pdf)
                throw new NotSupportedException(PdfNeedsResponsesApi);
            parts.Add(new JObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JObject { ["url"] = a.DataUrl, ["detail"] = "auto" }
            });
        }

        return parts;
    }

    /// <summary>Responses API: the content parts of a user message (always starts with the text part, as before).</summary>
    public static JArray ResponsesUserContent(Message message)
    {
        var parts = new JArray();
        if (message.Attachments is not { Count: > 0 })
        {
            parts.Add(new JObject { ["type"] = "input_text", ["text"] = message.Content ?? string.Empty });
            return parts;
        }

        if (!string.IsNullOrEmpty(message.Content))
            parts.Add(new JObject { ["type"] = "input_text", ["text"] = message.Content });

        foreach (var a in message.Attachments)
        {
            if (a.Kind == AttachmentKind.Pdf)
                parts.Add(new JObject
                {
                    ["type"] = "input_file",
                    ["filename"] = string.IsNullOrWhiteSpace(a.Name) ? "document.pdf" : a.Name,
                    ["file_data"] = a.DataUrl
                });
            else
                parts.Add(new JObject { ["type"] = "input_image", ["image_url"] = a.DataUrl });
        }

        return parts;
    }

    /// <summary>
    /// A copy of the request body for logging with every <c>data:...;base64,</c> payload replaced by its length,
    /// so images and PDFs never reach a log. Returns the body itself when it carries none.
    /// </summary>
    public static JObject LogSafe(JObject body)
    {
        List<JValue>? hits = null;
        foreach (var token in body.DescendantsAndSelf())
            if (token is JValue { Type: JTokenType.String } v && IsDataUrl(v))
                (hits ??= []).Add(v);
        if (hits is null) return body;

        var copy = (JObject)body.DeepClone();
        foreach (var token in copy.DescendantsAndSelf().OfType<JValue>().Where(v => v.Type == JTokenType.String && IsDataUrl(v)).ToList())
        {
            var s = (string)token.Value!;
            var comma = s.IndexOf(',');
            token.Value = $"{s[..(comma + 1)]}<{s.Length - comma - 1} base64 chars omitted>";
        }

        return copy;
    }

    private static bool IsDataUrl(JValue v) =>
        v.Value is string s && s.StartsWith("data:", StringComparison.Ordinal) && s.Contains(";base64,", StringComparison.Ordinal);
}
