using System.Text;
using AITaskAgent.LLM.Tools.Base;

namespace CodebaseAnalysis.Core.Progress;

/// <summary>
/// Entrada y salida de una llamada a herramienta tal como viajan en los eventos <c>tool.started</c> y <c>tool.completed</c> (del
/// análisis y del chat): las saca del <c>AdditionalData</c> de los eventos del framework (claves <see cref="LlmTool.ParametersDataKey"/>
/// y <see cref="LlmTool.ResultDataKey"/>, las mismas de <c>McpToolWrapper</c>) y las trunca en bytes UTF-8.
/// </summary>
public static class ToolEventPayload
{
    /// <summary>Tope de bytes UTF-8 de la salida de una tool en un evento; lo que pasa se corta y se marca <c>outputTruncated</c>.</summary>
    public const int MaxOutputBytes = 16 * 1024;

    /// <summary>Tope de bytes UTF-8 de los argumentos de una tool en un evento (un <c>create_report</c> lleva el informe entero).</summary>
    public const int MaxArgumentsBytes = 16 * 1024;

    /// <summary>
    /// Añade a <paramref name="data"/> <c>arguments</c> (y <c>argumentsTruncated</c> si se cortaron) y, si
    /// <paramref name="includeOutput"/>, <c>output</c>, <c>outputTruncated</c> y <c>outputBytes</c> (el tamaño entero). No añade
    /// nada que el evento no traiga: un consumidor que no los conoce sigue igual.
    /// </summary>
    public static void AddTo(Dictionary<string, object?> data, IReadOnlyDictionary<string, object>? additionalData, bool includeOutput)
    {
        if (additionalData == null)
            return;

        if (Text(additionalData, LlmTool.ParametersDataKey) is { } arguments)
        {
            var (text, truncated, _) = TruncateUtf8(arguments, MaxArgumentsBytes);
            data["arguments"] = text;
            if (truncated)
                data["argumentsTruncated"] = true;
        }

        if (includeOutput && Text(additionalData, LlmTool.ResultDataKey) is { } result)
        {
            var (text, truncated, bytes) = TruncateUtf8(result, MaxOutputBytes);
            data["output"] = text;
            data["outputTruncated"] = truncated;
            data["outputBytes"] = bytes;
        }
    }

    /// <summary>El valor como texto: una cadena tal cual y cualquier otro objeto (un <c>JToken</c> de MCP) por su <c>ToString</c>.</summary>
    private static string? Text(IReadOnlyDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) ? value as string ?? value?.ToString() : null;

    /// <summary>
    /// <paramref name="text"/> cortado a <paramref name="maxBytes"/> bytes UTF-8 sin partir un carácter (ni un par suplente), si
    /// cortado, y su tamaño entero en bytes.
    /// </summary>
    public static (string Text, bool Truncated, long TotalBytes) TruncateUtf8(string text, int maxBytes)
    {
        var total = Encoding.UTF8.GetByteCount(text);
        if (total <= maxBytes)
            return (text, false, total);

        // Carácter a carácter (un par suplente cuenta como uno) mientras quepa: solo se recorre lo que se conserva.
        var bytes = 0;
        var end = 0;
        while (end < text.Length)
        {
            var width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(end, width));
            if (bytes + size > maxBytes)
                break;
            bytes += size;
            end += width;
        }
        return (text[..end], true, total);
    }
}
