using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace AITaskAgent.CSharp;

/// <summary>
/// The collectible load context of one compiled unit. Besides the unit's own assembly it finds the assemblies the host resolved for it
/// (NuGet packages of the package, see <see cref="CSharpCompileOptions.AdditionalReferences"/>) by simple name, and their native libraries
/// in <see cref="CSharpCompileOptions.NativeLibraryDirectories"/>. Anything it does not know falls through to the default context, which is
/// how the shared set (the framework, Newtonsoft.Json, NJsonSchema, <c>Microsoft.Extensions.*</c>) always comes from the host.
/// </summary>
internal sealed class UnitLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, string> _assemblies;
    private readonly IReadOnlyList<string> _nativeDirectories;
    private readonly Func<string, bool> _isShared;

    public UnitLoadContext(string name, IEnumerable<string> assemblies, IEnumerable<string> nativeDirectories, Func<string, bool>? isShared = null)
        : base(name, isCollectible: true)
    {
        _isShared = isShared ?? (static n => n.StartsWith("AITaskAgent.", StringComparison.OrdinalIgnoreCase));
        _assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in assemblies)
        {
            _assemblies.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }

        _nativeDirectories = [.. nativeDirectories];
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The host's assembly, whatever file name a reference to it has: loading it again would give the unit a second copy of its types.
        if (assemblyName.Name is { } shared && _isShared(shared))
        {
            return null;
        }

        if (assemblyName.Name is { } name && _assemblies.TryGetValue(name, out string? path) && File.Exists(path))
        {
            // From bytes, not from the path: the cache files stay free (they can be replaced while the unit is loaded).
            using MemoryStream stream = new(File.ReadAllBytes(path));
            return LoadFromStream(stream);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (string directory in _nativeDirectories)
        {
            foreach (string candidate in Candidates(unmanagedDllName))
            {
                string path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                {
                    return NativeLibrary.Load(path);
                }
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        yield return name;
        if (OperatingSystem.IsWindows())
        {
            yield return name + ".dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "lib" + name + ".dylib";
            yield return name + ".dylib";
        }
        else
        {
            yield return "lib" + name + ".so";
            yield return name + ".so";
        }
    }
}
