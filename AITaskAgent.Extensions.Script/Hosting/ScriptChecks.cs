using AITaskAgent.Script.Isolation;
using AITaskAgent.Script.Protocol;
using System.Text.RegularExpressions;

namespace AITaskAgent.Script.Hosting;

/// <summary>One finding of the static checks of a script step. <c>Error</c> blocks a run; <c>Warning</c> is advice.</summary>
public sealed record ScriptIssue(bool IsError, string Code, string Message, int? Line = null);

/// <summary>What the syntax check needs to compile: an id to report under, whether it is a step body or a library, and the text.</summary>
public sealed record ScriptSyntaxItem(string Id, bool IsLibrary, string Script);

/// <summary>Static checks of <c>ScriptStep</c>s that need no worker: declared names against the catalog and the grant, and the regex hints.</summary>
public static partial class ScriptChecks
{
    /// <summary>
    /// Checks one step. <paramref name="granted"/> is what the host grants (null = nothing). Errors: a name in <paramref name="uses"/>
    /// or <paramref name="allow"/> that the catalog does not know. Warnings: an <paramref name="allow"/> name the host does not
    /// grant; a <c>host.x.y(</c> mention of a function not in <paramref name="allow"/>; a <c>require('x')</c> of a library not in
    /// <paramref name="uses"/>. The mention checks are approximate (dynamic access defeats a regex): the guarantee is that the worker
    /// only defines what <c>allow</c> lists and <c>require</c> only opens what <c>uses</c> lists.
    /// </summary>
    public static IReadOnlyList<ScriptIssue> Check(
        string script,
        IReadOnlyCollection<string> uses,
        IReadOnlyCollection<string> allow,
        IScriptCatalog? catalog,
        IReadOnlySet<string>? granted)
    {
        List<ScriptIssue> issues = [];
        foreach (string name in uses)
        {
            if (catalog?.FindLibrary(name) == null)
            {
                issues.Add(new ScriptIssue(true, "script_library_unknown", $"the library '{name}' listed in 'uses' is not known."));
            }
        }

        foreach (string name in allow)
        {
            if (catalog?.FindHostFunction(name) == null)
            {
                issues.Add(new ScriptIssue(true, "script_host_function_unknown", $"the host function '{name}' listed in 'allow' does not exist."));
            }
            else if (catalog.FindHostFunction(name) is not ScriptObjectFunction && granted?.Contains(name) != true)
            {
                issues.Add(new ScriptIssue(
                    false, "script_host_function_not_granted", $"the host function '{name}' is not granted by the host: the step will fail until it is (default deny)."));
            }
        }

        HashSet<string> allowed = new(allow, StringComparer.Ordinal);
        foreach (Match match in HostCall().Matches(script))
        {
            string function = match.Groups[1].Value + "." + match.Groups[2].Value;
            if (!allowed.Contains(function))
            {
                issues.Add(new ScriptIssue(
                    false, "script_host_call_not_allowed", $"the script calls host.{function}(...), which is not in 'allow'.", LineOf(script, match.Index)));
            }
        }

        HashSet<string> used = new(uses, StringComparer.Ordinal);
        foreach (Match match in Require().Matches(script))
        {
            string name = match.Groups[1].Value;
            if (!used.Contains(name))
            {
                issues.Add(new ScriptIssue(
                    false, "script_require_not_declared", $"the script requires '{name}', which is not in 'uses'.", LineOf(script, match.Index)));
            }
        }

        return issues;
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    [GeneratedRegex(@"\bhost\s*\.\s*([A-Za-z_]\w*)\s*\.\s*([A-Za-z_]\w*)\s*\(")]
    private static partial Regex HostCall();

    [GeneratedRegex(@"\brequire\s*\(\s*['""]([^'""]+)['""]\s*\)")]
    private static partial Regex Require();
}

/// <summary>One syntax error found by <see cref="ScriptSyntax"/>.</summary>
public sealed record ScriptSyntaxIssue(string Id, string Message, int? Line);

/// <summary>
/// The functions a library exports, as the worker lists them (the library runs in a throwaway context of its own): its function names, or
/// <see cref="Error"/> when it threw or broke a limit while loading. The run and the designer's validation read the same listing.
/// </summary>
/// <param name="Library">The library.</param>
/// <param name="Functions">The names of its exported functions, sorted.</param>
/// <param name="Error">Why it could not be listed (the error at the library's own line), or null.</param>
public sealed record ScriptLibraryExports(string Library, IReadOnlyList<string> Functions, string? Error = null)
{
    /// <summary>Why a <c>js:</c> validator naming <paramref name="function"/> of this library cannot run, or null when the library exports it.</summary>
    public string? Problem(string function)
        => Error != null ? $"the library '{Library}' failed while loading: {Error}"
            : Functions.Contains(function, StringComparer.Ordinal) ? null
            : $"the library '{Library}' has no function '{function}' (it exports: {(Functions.Count == 0 ? "none" : string.Join(", ", Functions))}).";

    /// <summary>Reads the worker's listing (<c>{ lib: { functions } | { error } }</c>).</summary>
    internal static IReadOnlyDictionary<string, ScriptLibraryExports> From(Newtonsoft.Json.Linq.JObject? listed)
    {
        Dictionary<string, ScriptLibraryExports> exports = new(StringComparer.Ordinal);
        foreach (Newtonsoft.Json.Linq.JProperty entry in listed?.Properties() ?? [])
        {
            exports[entry.Name] = entry.Value is Newtonsoft.Json.Linq.JObject value && value.Value<string>("error") is { } error
                ? new ScriptLibraryExports(entry.Name, [], error)
                : new ScriptLibraryExports(entry.Name, [.. (entry.Value["functions"] as Newtonsoft.Json.Linq.JArray ?? []).Values<string>().OfType<string>()]);
        }

        return exports;
    }
}

/// <summary>What <see cref="ScriptSyntax.CheckAsync(IReadOnlyList{ScriptSyntaxItem}, IReadOnlyList{string}, CancellationToken)"/> found.</summary>
/// <param name="Issues">The syntax errors (an empty id: the check itself could not run).</param>
/// <param name="Exports">The functions of each library asked for (empty when the scripts did not compile).</param>
public sealed record ScriptCheckResult(IReadOnlyList<ScriptSyntaxIssue> Issues, IReadOnlyDictionary<string, ScriptLibraryExports> Exports);

/// <summary>
/// Compiles scripts and libraries in one worker process, without running them, to report syntax errors early. It joins
/// them in the same bundle a run compiles when it starts (<c>ScriptBundle</c>), so validation and run agree.
/// </summary>
public static partial class ScriptSyntax
{
    /// <summary>
    /// Compiles every item in one worker (a start-up of about a second, whatever the number of items). A failure to run
    /// the check itself (no worker) is returned as a single issue with an empty id, never thrown. Item ids must be unique.
    /// </summary>
    public static async Task<IReadOnlyList<ScriptSyntaxIssue>> CheckAsync(
        IReadOnlyList<ScriptSyntaxItem> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return [];
        }

        List<BundlePiece> pieces = [.. items.Select(i => new BundlePiece(i.Id, i.IsLibrary, i.Id, i.Script))];
        (IReadOnlyList<(string Id, string Error)> errors, string? failure) = await ScriptRunSession.CheckAsync(pieces, cancellationToken);
        if (failure != null)
        {
            return [new ScriptSyntaxIssue("", failure, null)];
        }

        return [.. errors.Select(e => new ScriptSyntaxIssue(e.Id, e.Error, LineIn(e.Error)))];
    }

    /// <summary>
    /// <see cref="CheckAsync(IReadOnlyList{ScriptSyntaxItem}, CancellationToken)"/> that also lists the functions the libraries
    /// <paramref name="exports"/> export (by their library names, the ids without <c>lib:</c>), as the run does when it starts: what the
    /// designer offers for <c>js:&lt;library&gt;.&lt;function&gt;</c> and checks against.
    /// </summary>
    public static async Task<ScriptCheckResult> CheckAsync(
        IReadOnlyList<ScriptSyntaxItem> items, IReadOnlyList<string> exports, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return new ScriptCheckResult([], new Dictionary<string, ScriptLibraryExports>());
        }

        List<BundlePiece> pieces = [.. items.Select(i => new BundlePiece(
            i.Id, i.IsLibrary, i.IsLibrary && i.Id.StartsWith("lib:", StringComparison.Ordinal) ? i.Id[4..] : i.Id, i.Script))];
        (IReadOnlyList<(string Id, string Error)> errors, string? failure, Newtonsoft.Json.Linq.JObject? listed) =
            await ScriptRunSession.CheckAsync(pieces, exports, cancellationToken);
        if (failure != null)
        {
            return new ScriptCheckResult([new ScriptSyntaxIssue("", failure, null)], new Dictionary<string, ScriptLibraryExports>());
        }

        return new ScriptCheckResult([.. errors.Select(e => new ScriptSyntaxIssue(e.Id, e.Error, LineIn(e.Error)))], ScriptLibraryExports.From(listed));
    }

    private static int? LineIn(string details)
    {
        Match match = Position().Match(details);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@":(\d+):\d+")]
    private static partial Regex Position();
}
