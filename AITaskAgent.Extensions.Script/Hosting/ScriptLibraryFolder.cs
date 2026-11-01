using System.Text;
using System.Text.RegularExpressions;

namespace AITaskAgent.Script.Hosting;

/// <summary>
/// The JavaScript libraries of a plugin (architecture §18.7): the <c>*.js</c> files at the top of <c>&lt;folder&gt;/js</c>, where the folder is
/// the plugin's <c>PluginContext.Folder</c> (a folder plugin, or the extracted <c>plugin/</c> of a <c>.nupkg</c>, so a package carries them as
/// <c>plugin/js/&lt;name&gt;.js</c>). Each file is one library named after the file without <c>.js</c>; a step <c>require</c>s it after listing
/// it in <c>uses</c>, and a JavaScript validator names its functions as <c>js:&lt;name&gt;.&lt;function&gt;</c>.
/// </summary>
public static partial class ScriptLibraryFolder
{
    /// <summary>The subfolder of a plugin's folder that holds its libraries.</summary>
    public const string FolderName = "js";

    /// <summary>Largest library file read, in bytes.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>
    /// Reads the libraries of <paramref name="folder"/>/<see cref="FolderName"/>, sorted by name. A file with an invalid name (letters, digits,
    /// <c>.</c>, <c>_</c>, <c>-</c>), larger than <see cref="MaxFileBytes"/> or not UTF-8 text is left out and named in <c>Problems</c>.
    /// </summary>
    /// <param name="folder">The plugin's folder.</param>
    /// <param name="owner">Who the libraries belong to, for their description (<c>plugin 'X'</c>).</param>
    public static (IReadOnlyList<ScriptLibrary> Libraries, IReadOnlyList<string> Problems) Load(string folder, string owner)
    {
        List<ScriptLibrary> libraries = [];
        List<string> problems = [];
        string js = Path.Combine(folder, FolderName);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(js))
        {
            return (libraries, problems);
        }

        UTF8Encoding strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        foreach (string file in Directory.EnumerateFiles(js, "*.js", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!ValidName().IsMatch(name))
            {
                problems.Add($"{FolderName}/{Path.GetFileName(file)}: the name is not a valid library name (letters, digits, '.', '_', '-').");
                continue;
            }

            FileInfo info = new(file);
            if (info.Length > MaxFileBytes)
            {
                problems.Add($"{FolderName}/{Path.GetFileName(file)}: larger than {MaxFileBytes / 1024} KB.");
                continue;
            }

            try
            {
                string source = strict.GetString(File.ReadAllBytes(file)).TrimStart('﻿');
                libraries.Add(new ScriptLibrary(name, source, $"JavaScript library of {owner} ({FolderName}/{name}.js)."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                problems.Add($"{FolderName}/{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return (libraries, problems);
    }

    /// <summary>True when <paramref name="name"/> is a valid library name.</summary>
    public static bool IsValidName(string? name) => name != null && ValidName().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$")]
    private static partial Regex ValidName();
}

/// <summary>What a library needs: itself and the libraries it <c>require</c>s with a literal name, transitively.</summary>
public static partial class ScriptLibraryClosure
{
    /// <summary>
    /// <paramref name="roots"/> and every library of <paramref name="catalog"/> they <c>require('name')</c> with a literal name, transitively
    /// (a dynamic <c>require</c> is not followed: that library must be named by another root). Names the catalog does not know are left out.
    /// </summary>
    public static IReadOnlyList<IScriptLibrary> Of(IScriptCatalog? catalog, IEnumerable<string> roots)
    {
        List<IScriptLibrary> found = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        Queue<string> pending = new(roots);
        while (pending.TryDequeue(out string? name))
        {
            if (!seen.Add(name) || catalog?.FindLibrary(name) is not { } library)
            {
                continue;
            }

            found.Add(library);
            foreach (Match match in Require().Matches(library.Source))
            {
                pending.Enqueue(match.Groups[1].Value);
            }
        }

        return found;
    }

    [GeneratedRegex(@"\brequire\s*\(\s*['""]([^'""]+)['""]\s*\)")]
    private static partial Regex Require();
}
