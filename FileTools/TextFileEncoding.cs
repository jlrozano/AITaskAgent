using System.Text;
using System.Text.Unicode;

namespace AITaskAgent.FileTools;

/// <summary>
/// The rule every reading tool applies to decide how a file is decoded, file by file: a byte-order mark wins (UTF-8,
/// UTF-16 or UTF-32); otherwise UTF-8 when the bytes are valid UTF-8; otherwise
/// <see cref="FileToolsConfiguration.DefaultEncoding"/>. Reading everything as UTF-8 turned every accented character of
/// an ISO-8859-1 or windows-1252 file into U+FFFD, made a search for an accented word find nothing there, and an edit
/// wrote the replacement characters back to disk.
/// </summary>
/// <remarks>
/// The returned <see cref="Encoding"/> is the one to write the file back with: it keeps the BOM when the file had one
/// and omits it when it did not.
/// </remarks>
public static class TextFileEncoding
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>True when the bytes are strict UTF-8.</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes) => Utf8.IsValid(bytes);

    /// <summary>
    /// Decodes <paramref name="bytes"/> by the rule above, without the BOM in the text. With no
    /// <paramref name="defaultEncoding"/>, the one configured in <see cref="FileToolsConfiguration.DefaultEncoding"/>.
    /// </summary>
    public static (string Text, Encoding Encoding) Decode(ReadOnlySpan<byte> bytes, Encoding? defaultEncoding = null)
    {
        if (DetectBom(bytes) is { } bom)
            return (bom.Encoding.GetString(bytes[bom.Length..]), bom.Encoding);

        if (IsValidUtf8(bytes))
            return (Utf8NoBom.GetString(bytes), Utf8NoBom);

        var fallback = defaultEncoding ?? FileToolsConfiguration.DefaultEncoding;
        return (fallback.GetString(bytes), fallback);
    }

    /// <summary>The whole file, decoded by the rule above, and the encoding to write it back with.</summary>
    public static async Task<(string Text, Encoding Encoding)> ReadAllTextAsync(string path, CancellationToken ct = default) =>
        Decode(await File.ReadAllBytesAsync(path, ct));

    /// <summary>
    /// The file's lines, decoded by the rule above. Splits like <see cref="File.ReadAllLinesAsync(string, CancellationToken)"/>:
    /// on <c>\r\n</c>, <c>\n</c> or <c>\r</c>, with no empty entry after a final line break.
    /// </summary>
    public static async Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct = default)
    {
        var (text, _) = await ReadAllTextAsync(path, ct);
        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return [.. lines];
    }

    /// <summary>The encoding a BOM at the start of <paramref name="bytes"/> declares, and the BOM's length.</summary>
    private static (Encoding Encoding, int Length)? DetectBom(ReadOnlySpan<byte> bytes)
    {
        // UTF-32 LE's BOM (FF FE 00 00) starts with the same two bytes as UTF-16 LE's (FF FE), so the 4-byte patterns
        // must be checked before the 2-byte ones.
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        return null;
    }
}
