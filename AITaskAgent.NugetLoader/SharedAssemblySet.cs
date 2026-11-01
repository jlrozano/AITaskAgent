using System.Reflection;
using System.Runtime.Loader;

namespace AITaskAgent.NugetLoader;

/// <summary>
/// The assemblies that always come from the host process, never from a package's NuGet closure (architecture §16.3, «host wins»): the
/// framework (<c>AITaskAgent.*</c>), <c>Newtonsoft.Json</c>, <c>NJsonSchema</c>, <c>Microsoft.Extensions.*</c> and the platform
/// (<c>System.*</c>, <c>Microsoft.NETCore.*</c>, …). They are the types a unit's code and the engine share: two copies would make
/// <c>IStep</c> of the unit a different type from <c>IStep</c> of the engine.
/// </summary>
public static class SharedAssemblySet
{
    private static readonly string[] Prefixes =
    [
        "AITaskAgent.", "Microsoft.Extensions.", "System.", "Microsoft.NETCore.", "Microsoft.Win32.", "NJsonSchema"
    ];

    private static readonly string[] Exact = ["Newtonsoft.Json", "Microsoft.CSharp", "NETStandard.Library", "System"];

    private static readonly string[] Metapackages = ["NETStandard.Library", "Microsoft.NETCore.App", "Microsoft.NETCore.Platforms", "Microsoft.NETCore.Targets"];

    // What the host added (settings): a name ending in '.' or '*' is a prefix, any other is an exact name. Replaced as a whole, never mutated.
    private static volatile string[] _extraPrefixes = [];
    private static volatile string[] _extraExact = [];

    /// <summary>
    /// Adds host-declared entries to the built-in set (replacing the ones of an earlier call): <c>"Acme.Shared."</c> or <c>"Acme.*"</c> is a
    /// prefix, <c>"Acme.Contracts"</c> an exact name. Meant to be called once at startup, before packages are resolved or loaded: contexts
    /// already created keep the set they saw. Blank entries are ignored. The host must really provide the assembly (see <see cref="HostWins"/>).
    /// </summary>
    public static void Configure(IEnumerable<string>? extra)
    {
        List<string> prefixes = [];
        List<string> exact = [];
        foreach (string raw in extra ?? [])
        {
            string entry = raw?.Trim() ?? string.Empty;
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry.EndsWith('*'))
            {
                prefixes.Add(entry.TrimEnd('*'));
            }
            else if (entry.EndsWith('.'))
            {
                prefixes.Add(entry);
            }
            else
            {
                exact.Add(entry);
            }
        }

        _extraPrefixes = [.. prefixes.Where(p => p.Length > 0)];
        _extraExact = [.. exact];
    }

    /// <summary>True when an assembly or package called <paramref name="name"/> belongs to the shared set.</summary>
    public static bool IsShared(string name)
        => Exact.Contains(name, StringComparer.OrdinalIgnoreCase) || Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || _extraExact.Contains(name, StringComparer.OrdinalIgnoreCase) || _extraPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when <paramref name="name"/> is shared and the host has it: the assembly a load context must not load again, whatever file
    /// name a reference to it has. Shared names the host does not have are left to the context (nothing to defer to).
    /// </summary>
    public static bool HostWins(string name) => IsShared(name) && HostVersion(name) != null;

    /// <summary>True for packages that only describe the platform and carry no assembly (the host is what they stand for).</summary>
    public static bool IsMetapackage(string id)
        => Metapackages.Any(m => id.Equals(m, StringComparison.OrdinalIgnoreCase) || id.StartsWith(m + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The version of the assembly <paramref name="name"/> in the host's default load context; null when the host has none.
    /// Loading a host assembly by name is harmless: it is the one the engine already uses or would use.
    /// </summary>
    public static Version? HostVersion(string name)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name)).GetName().Version;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True when the package <paramref name="id"/> is of the shared set and the host supplies it (so it is not downloaded).</summary>
    public static bool HostProvides(string id) => IsShared(id) && (IsMetapackage(id) || HostVersion(id) != null);
}
