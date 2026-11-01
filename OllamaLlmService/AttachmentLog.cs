using Newtonsoft.Json.Linq;

namespace AITaskAgent.Ollama;

/// <summary>Keeps image base64 out of the request logs.</summary>
internal static class AttachmentLog
{
    /// <summary>A copy of the body with every <c>messages[].images[]</c> entry replaced by its length (the body itself when there are none).</summary>
    public static JObject Safe(JObject body)
    {
        if (body["messages"] is not JArray messages || !messages.OfType<JObject>().Any(m => m["images"] is JArray))
            return body;

        var copy = (JObject)body.DeepClone();
        foreach (var m in ((JArray)copy["messages"]!).OfType<JObject>())
            if (m["images"] is JArray images)
                m["images"] = new JArray(images.Select(i => (JToken)$"<{i.ToString().Length} base64 chars omitted>"));
        return copy;
    }
}
