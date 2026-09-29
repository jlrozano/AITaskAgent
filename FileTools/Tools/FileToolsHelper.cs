using System.Text;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Shared helper utilities for file tools.
/// Provides encoding-preserving reads/writes and line-range-aware content replacement.
/// </summary>
internal static class FileToolsHelper
{
    /// <summary>
    /// Reads the file at <paramref name="path"/> and reports the exact encoding (including
    /// whether it had a byte-order mark) it was read with, so a caller that rewrites the file can
    /// round-trip it instead of always falling back to UTF-8.
    /// </summary>
    /// <remarks>
    /// Decoded by <see cref="TextFileEncoding"/>: the BOM, else UTF-8 when the bytes are valid
    /// UTF-8, else <see cref="FileToolsConfiguration.DefaultEncoding"/>. The last case used to read
    /// as UTF-8 too, so editing an ISO-8859-1 file wrote U+FFFD in place of every accent.
    /// </remarks>
    public static Task<(string Content, Encoding Encoding)> ReadWithEncodingAsync(
        string path, CancellationToken ct) =>
        TextFileEncoding.ReadAllTextAsync(path, ct);

    /// <summary>
    /// Returns the line-ending style found in <paramref name="content"/> - <c>"\r\n"</c>,
    /// <c>"\n"</c> or <c>"\r"</c> - based on the first line break encountered, so a caller that
    /// edits the file in \n-space can convert back before writing instead of silently switching
    /// the whole file to LF. Defaults to <c>"\n"</c> when the content has no line breaks at all.
    /// </summary>
    public static string DetectLineEnding(string content)
    {
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\r')
            {
                return i + 1 < content.Length && content[i + 1] == '\n' ? "\r\n" : "\r";
            }

            if (content[i] == '\n')
            {
                return "\n";
            }
        }

        return "\n";
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically: the new content
    /// lands in a temp file next to the target first, then <see cref="File.Move(string, string, bool)"/>
    /// replaces the target in one filesystem operation. A crash or cancellation mid-write leaves
    /// either the old file untouched or the new one fully in place - never a truncated/corrupt
    /// target, which a direct <c>File.WriteAllTextAsync</c> to the destination could leave behind.
    /// </summary>
    public static async Task WriteAtomicAsync(
        string path, string content, Encoding encoding, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        var tempPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempPath, content, encoding, ct);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    /// <summary>
    /// Applies a single replacement chunk to <paramref name="fileContent"/>, confining the
    /// search for <paramref name="targetContent"/> to the line range
    /// [<paramref name="startLine"/>, <paramref name="endLine"/>] (1-indexed, inclusive).
    /// </summary>
    /// <param name="fileContent">
    /// The full file content. All line endings must already be normalised to <c>\n</c>.
    /// </param>
    /// <param name="targetContent">Exact text to search for (will be normalised to <c>\n</c>).</param>
    /// <param name="replacementContent">Text to replace the target with (will be normalised to <c>\n</c>).</param>
    /// <param name="startLine">First line of the search window (1-indexed).</param>
    /// <param name="endLine">Last line of the search window (1-indexed).</param>
    /// <param name="allowMultiple">
    /// When <c>true</c>, all occurrences of the target inside the window are replaced.
    /// When <c>false</c>, more than one occurrence inside the window throws.
    /// </param>
    /// <returns>
    /// The reconstructed file content after the replacement, or <c>null</c> when
    /// <paramref name="targetContent"/> was not found inside the specified range
    /// (the caller is responsible for converting this into an appropriate error message).
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="allowMultiple"/> is <c>false</c> and the target appears
    /// more than once within the search window.
    /// </exception>
    public static string? ApplyChunk(
        string fileContent,
        string targetContent,
        string replacementContent,
        int startLine,
        int endLine,
        bool allowMultiple)
    {
        // Normalise line endings in inputs
        var normalized = fileContent.Replace("\r\n", "\n");
        var normalizedTarget = targetContent.Replace("\r\n", "\n");
        var normalizedReplacement = replacementContent.Replace("\r\n", "\n");

        var lines = normalized.Split('\n');
        var totalLines = lines.Length;

        // Clamp to valid indices (1-indexed → 0-indexed)
        var start = Math.Max(0, startLine - 1);
        var end = Math.Min(totalLines - 1, endLine - 1);

        if (start > end)
        {
            // Degenerate range — treat as not-found so the caller can emit a clear error
            return null;
        }

        // Extract only the bounded fragment for the search
        var fragment = string.Join('\n', lines[start..(end + 1)]);

        if (!fragment.Contains(normalizedTarget, StringComparison.Ordinal))
            return null; // target not found in range → caller emits error

        if (!allowMultiple)
        {
            var firstIdx = fragment.IndexOf(normalizedTarget, StringComparison.Ordinal);
            var secondIdx = fragment.IndexOf(normalizedTarget, firstIdx + 1, StringComparison.Ordinal);
            if (secondIdx >= 0)
                throw new InvalidOperationException(
                    $"TargetContent appears more than once within the line range " +
                    $"[{startLine}, {endLine}]. " +
                    "Set AllowMultiple=true or narrow the range to target a unique occurrence.");
        }

        var replacedFragment = fragment.Replace(normalizedTarget, normalizedReplacement, StringComparison.Ordinal);

        // Reconstruct: content before the window + replaced window + content after the window
        var before = start > 0
            ? string.Join('\n', lines[0..start]) + '\n'
            : string.Empty;

        var after = end < totalLines - 1
            ? '\n' + string.Join('\n', lines[(end + 1)..])
            : string.Empty;

        return before + replacedFragment + after;
    }
}
