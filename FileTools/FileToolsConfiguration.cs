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
    /// Defaults to the executable's directory, never the process working directory, so results
    /// do not change depending on where the process was launched from.
    /// </remarks>
    public static string RootDirectory
    {
        get
        {
            var root = Volatile.Read(ref _rootDirectory);
            return string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : root;
        }
        set => Volatile.Write(
            ref _rootDirectory,
            string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value));
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
    public static bool IsAllowed(string path)
    {
        foreach (var root in AllowedDirectories)
        {
            if (IsWithin(path, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> itself or sits under it.
    /// </summary>
    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetFullPath(root);

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

    private static bool IsAlreadyAllowed(string fullPath) =>
        string.Equals(Path.GetFullPath(RootDirectory), fullPath, StringComparison.OrdinalIgnoreCase)
        || _additionalAllowed.Any(d => string.Equals(d, fullPath, StringComparison.OrdinalIgnoreCase));
}
