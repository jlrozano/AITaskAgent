namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Shared helper utilities for file tools.
/// Provides path validation, normalized file reading, and line-range-aware content replacement.
/// </summary>
internal static class FileToolsHelper
{
    /// <summary>
    /// Resolves a relative path against <paramref name="rootDirectory"/> and validates that
    /// the result stays within that root (prevents path traversal attacks).
    /// </summary>
    /// <param name="rootDirectory">The allowed base directory.</param>
    /// <param name="relativePath">The relative (or absolute) path to resolve.</param>
    /// <returns>The fully-resolved, validated absolute path.</returns>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the resolved path escapes <paramref name="rootDirectory"/>.
    /// </exception>
    public static string ResolveSafePath(string rootDirectory, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        var safeRoot = Path.GetFullPath(rootDirectory);

        // Ensure the safe root ends with a separator so "root2" doesn't match "root"
        if (!safeRoot.EndsWith(Path.DirectorySeparatorChar))
            safeRoot += Path.DirectorySeparatorChar;

        if (!full.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Path '{relativePath}' resolves outside the allowed root directory '{rootDirectory}'.");

        return full;
    }

    /// <summary>
    /// Reads the file at <paramref name="path"/> and normalises all line endings to <c>\n</c>.
    /// </summary>
    public static async Task<string> ReadNormalizedAsync(string path, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(path, ct);
        return content.Replace("\r\n", "\n");
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
