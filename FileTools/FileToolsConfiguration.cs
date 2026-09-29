namespace AITaskAgent.FileTools;

/// <summary>
/// Process-wide root and allowed directories for all file tools.
/// </summary>
/// <remarks>
/// Directories can be added at runtime (a chat command widening the sandbox, for instance), so
/// reads and writes here are safe to interleave with tools resolving paths on other threads.
/// </remarks>
public static class FileToolsConfiguration
{
    private static readonly object Gate = new();
    private static string? _rootDirectory;
    private static IReadOnlyList<string> _additionalAllowed = [];

    /// <summary>
    /// Primary root directory: relative paths resolve against it and it is always allowed.
    /// </summary>
    /// <remarks>
    /// Defaults to a "workspace" subdirectory of the process's current working directory when
    /// nothing is configured explicitly - never <see cref="AppContext.BaseDirectory"/>, the
    /// executable's own directory, which would let the file tools overwrite the app's own
    /// binaries/config for any caller that forgets to set a root. Callers that need the old
    /// behavior must opt in explicitly by setting this property.
    /// </remarks>
    public static string RootDirectory
    {
        get
        {
            var root = Volatile.Read(ref _rootDirectory);
            return string.IsNullOrWhiteSpace(root) ? DefaultRootDirectory : root;
        }
        set => Volatile.Write(
            ref _rootDirectory,
            string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value));
    }

    private static string DefaultRootDirectory =>
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "workspace"));

    private static System.Text.Encoding? _defaultEncoding;

    /// <summary>
    /// Encoding for the files that are not valid UTF-8 and carry no BOM (<see cref="TextFileEncoding"/>). ISO-8859-1
    /// (Latin-1) unless the host sets another one, such as the source encoding a project declares; setting null restores
    /// Latin-1.
    /// </summary>
    /// <remarks>
    /// Not UTF-8: these are, by definition, bytes that do not decode as UTF-8, so a UTF-8 fallback would replace every
    /// non-ASCII character with U+FFFD, and an edit tool would write those replacements back. Latin-1 maps every byte to
    /// a character, so nothing is lost and a file edited through the tools keeps its bytes.
    /// </remarks>
    public static System.Text.Encoding DefaultEncoding
    {
        get => Volatile.Read(ref _defaultEncoding) ?? System.Text.Encoding.Latin1;
        set => Volatile.Write(ref _defaultEncoding, value);
    }

    /// <summary>
    /// Every directory the tools may touch: <see cref="RootDirectory"/> first, then the ones
    /// added afterwards.
    /// </summary>
    public static IReadOnlyList<string> AllowedDirectories
    {
        get
        {
            var additional = Volatile.Read(ref _additionalAllowed);
            var all = new List<string>(additional.Count + 1)
            {
                Path.GetFullPath(RootDirectory)
            };
            all.AddRange(additional);
            return all;
        }
    }

    /// <summary>
    /// Grants the tools access to another directory, on top of <see cref="RootDirectory"/>.
    /// Relative paths still resolve against the primary root only.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the directory was added, <c>false</c> when it was already allowed.
    /// </returns>
    public static bool AddAllowedDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Directory must not be empty.", nameof(path));
        }

        var full = Path.GetFullPath(path);

        lock (Gate)
        {
            if (IsAlreadyAllowed(full))
            {
                return false;
            }

            _additionalAllowed = [.. _additionalAllowed, full];
            return true;
        }
    }

    /// <summary>
    /// Drops every directory added through <see cref="AddAllowedDirectory"/>, leaving only
    /// <see cref="RootDirectory"/>.
    /// </summary>
    public static void ClearAdditionalAllowedDirectories() =>
        Volatile.Write(ref _additionalAllowed, []);

    /// <summary>
    /// Makes <paramref name="path"/> the primary root, granting access to it in the same step:
    /// the primary root is always allowed, so this needs no prior
    /// <see cref="AddAllowedDirectory"/>. If it had already been added, the duplicate entry is
    /// dropped. The previous root stays accessible unless the new one already covers it, so
    /// changing focus never silently revokes access.
    /// </summary>
    /// <returns>The previous root, when it was kept as an additional allowed directory.</returns>
    public static string? SetRootDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Directory must not be empty.", nameof(path));
        }

        var newRoot = Path.GetFullPath(path);

        lock (Gate)
        {
            var previousRoot = Path.GetFullPath(RootDirectory);
            RootDirectory = newRoot;

            // The new root is allowed by virtue of being the root: drop any entry added earlier
            // so it is not listed twice.
            var remaining = _additionalAllowed
                .Where(d => !string.Equals(d, newRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var keepPrevious = !IsWithin(previousRoot, newRoot)
                && !remaining.Any(d => string.Equals(d, previousRoot, StringComparison.OrdinalIgnoreCase));

            if (keepPrevious)
            {
                remaining.Add(previousRoot);
            }

            _additionalAllowed = remaining;
            return keepPrevious ? previousRoot : null;
        }
    }

    /// <summary>
    /// True when the path sits inside any allowed directory. Single source of truth for the
    /// sandbox check, so tools and callers cannot disagree about what is reachable.
    /// </summary>
    /// <remarks>
    /// This is the main security boundary, so a malformed or otherwise unresolvable
    /// <paramref name="path"/> (invalid characters, unsupported format, path too long, ...) must
    /// fail closed - return <c>false</c> - rather than let an exception escape and risk being
    /// swallowed by a caller's permissive <c>catch</c>.
    /// </remarks>
    public static bool IsAllowed(string path)
    {
        try
        {
            foreach (var root in AllowedDirectories)
            {
                if (IsWithin(path, root))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> itself or sits under it, once
    /// both are resolved to their real filesystem location.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="Path.GetFullPath(string)"/> comparison only normalizes the textual
    /// path - it does not know that a directory anywhere along it is a symlink or NTFS junction
    /// pointing outside the sandbox. <see cref="ResolveRealPath"/> walks the path and follows any
    /// reparse point it finds before the prefix comparison happens.
    /// </remarks>
    private static bool IsWithin(string path, string root)
    {
        var fullPath = ResolveRealPath(path);
        var rootPath = ResolveRealPath(root);

        // Compare with a trailing separator so a root named "root" does not match "root2".
        var sep = Path.DirectorySeparatorChar.ToString();
        var altSep = Path.AltDirectorySeparatorChar.ToString();

        var normalizedPath = fullPath.Replace(altSep, sep);
        var normalizedRoot = rootPath.Replace(altSep, sep);

        if (!normalizedPath.EndsWith(sep)) normalizedPath += sep;
        if (!normalizedRoot.EndsWith(sep)) normalizedRoot += sep;

        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to an absolute path and follows any symlink/junction
    /// (reparse point) found at any level along it, so containment checks compare real locations
    /// instead of the textual path a reparse point can make point anywhere.
    /// </summary>
    /// <remarks>
    /// Walks from the root down instead of calling
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> once on the leaf: that API only resolves the
    /// leaf itself when it is a reparse point, and would miss a reparse point on an intermediate
    /// directory (e.g. an allowed directory later replaced by a junction). Segments that do not
    /// exist yet (a file about to be created) simply stop the walk - there is nothing left to
    /// resolve past that point.
    /// </remarks>
    private static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);

        var root = Path.GetPathRoot(full) ?? string.Empty;
        var segments = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var segment in segments)
        {
            current = current.Length == 0 ? segment : Path.Combine(current, segment);

            FileSystemInfo? info =
                Directory.Exists(current) ? new DirectoryInfo(current) :
                File.Exists(current) ? new FileInfo(current) :
                null;

            if (info is null)
            {
                // Nothing on disk at this point yet (or access is denied probing it) - the rest
                // of the path cannot be a reparse point either, so stop resolving.
                break;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                current = target.FullName;
            }
        }

        return current;
    }

    private static bool IsAlreadyAllowed(string fullPath) =>
        string.Equals(Path.GetFullPath(RootDirectory), fullPath, StringComparison.OrdinalIgnoreCase)
        || _additionalAllowed.Any(d => string.Equals(d, fullPath, StringComparison.OrdinalIgnoreCase));
}
