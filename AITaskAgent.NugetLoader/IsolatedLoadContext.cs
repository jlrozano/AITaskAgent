using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace AITaskAgent.NugetLoader;

/// <summary>
/// The load context of one extension (architecture §17.2). The contract assemblies and the framework are the host's, so an extension's
/// <c>IStep</c> is the engine's <c>IStep</c>; everything else the extension ships is private to it, so two extensions can bring different
/// versions of a library. Non-collectible: the catalog and the YAML reader keep the extension's types for the host's lifetime.
/// </summary>
/// <remarks>
/// Assemblies are loaded from bytes: nothing keeps a file open (the DLL can be rebuilt while the host runs). The main assembly comes from
/// the bytes the caller passes to <see cref="LoadMain"/>, so a caller that hashed those bytes loads exactly what it hashed. Private
/// dependencies are read from disk when the runtime first resolves them, and native libraries are loaded from their path. With approved
/// hashes (a folder plugin's tree, or the files a NuGet plugin's content check read) a dependency is loaded from the bytes that were checked
/// against them, and a native library is hashed through a handle that denies writes and is loaded before that handle is closed; a file that
/// is not approved is not loaded from here, one whose bytes changed is a <see cref="FileLoadException"/> whose <see cref="Exception.Data"/>
/// carries <see cref="ProblemKey"/> = <c>content-changed</c>. Without approved hashes nothing is checked.
/// </remarks>
public sealed class IsolatedLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly IReadOnlyDictionary<string, string> _nugetAssemblies;
    private readonly IReadOnlyList<string> _nativeDirectories;
    private readonly string _folder;
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?>? _approved;

    /// <summary>The key of <see cref="Exception.Data"/> that a failed content check carries (value <c>content-changed</c>).</summary>
    public const string ProblemKey = "plugin-problem";

    /// <param name="name">Name of the unit (an extension); the context is called <c>extension:name</c>.</param>
    /// <param name="mainAssemblyPath">The main assembly; its <c>.deps.json</c> drives the resolution of the rest.</param>
    /// <param name="nugetReferences">Assemblies of the unit's NuGet closure (see <see cref="NugetEnvironment"/>).</param>
    /// <param name="nativeDirectories">Folders searched for native libraries.</param>
    public IsolatedLoadContext(string name, string mainAssemblyPath, IReadOnlyList<string> nugetReferences, IReadOnlyList<string> nativeDirectories)
        : this("extension:" + name, mainAssemblyPath, nugetReferences, nativeDirectories, named: true)
    {
    }

    // `named` only tells this constructor apart from the public one (same parameter types): the name is used as given.
    private IsolatedLoadContext(string contextName, string mainAssemblyPath, IReadOnlyList<string> nugetReferences, IReadOnlyList<string> nativeDirectories, bool named, Func<string, string?>? approved = null)
        : base(contextName, isCollectible: false)
    {
        _ = named;
        _approved = approved;
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _folder = Path.GetDirectoryName(mainAssemblyPath) ?? string.Empty;
        _nugetAssemblies = nugetReferences
            .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _nativeDirectories = nativeDirectories;
    }

    /// <summary>
    /// A context whose name is <paramref name="contextName"/> as given (the constructor prefixes <c>extension:</c>); a plugin uses
    /// <see cref="PluginIdentity.ContextName"/> (<c>plugin:&lt;origin&gt;:&lt;id&gt;/&lt;version&gt;</c>). Same rules as the constructor.
    /// </summary>
    /// <param name="contextName">The name, as given.</param>
    /// <param name="mainAssemblyPath">The main assembly; its <c>.deps.json</c> drives the resolution of the rest.</param>
    /// <param name="nugetReferences">Assemblies of the NuGet closure.</param>
    /// <param name="nativeDirectories">Folders searched for native libraries.</param>
    /// <param name="tree">
    /// The approved files of a folder plugin: a private assembly is loaded only when it is a file of the tree and its bytes still have the
    /// approved hash (another file is not loaded from here; changed bytes are a <see cref="FileLoadException"/>). Null = no check.
    /// </param>
    public static IsolatedLoadContext Named(string contextName, string mainAssemblyPath, IReadOnlyList<string> nugetReferences, IReadOnlyList<string> nativeDirectories, PluginFolderTree? tree = null)
        => new(contextName, mainAssemblyPath, nugetReferences, nativeDirectories, named: true, tree == null ? null : tree.HashOf);

    /// <summary>
    /// As the other overload, with the approved files given as absolute path → lower-case hex SHA-512 (a NuGet plugin's assemblies and native
    /// libraries as its content check read them): a private assembly or native library not in the map is not loaded from here; one whose
    /// bytes changed is a <see cref="FileLoadException"/> (<c>content-changed</c>).
    /// </summary>
    public static IsolatedLoadContext Named(
        string contextName, string mainAssemblyPath, IReadOnlyList<string> nugetReferences, IReadOnlyList<string> nativeDirectories, IReadOnlyDictionary<string, string> approvedFiles)
    {
        ArgumentNullException.ThrowIfNull(approvedFiles);
        Dictionary<string, string> map = new(approvedFiles, StringComparer.OrdinalIgnoreCase);
        return new(contextName, mainAssemblyPath, nugetReferences, nativeDirectories, named: true, path => map.GetValueOrDefault(Path.GetFullPath(path)));
    }

    /// <summary>Simple names of the private assemblies this context loaded.</summary>
    public IReadOnlyList<string> Loaded => _loaded;

    /// <summary>Shared-set assemblies the folder carried that the host's copy replaced.</summary>
    public IReadOnlyCollection<string> Ignored => _ignored;

    private readonly List<string> _loaded = [];

    /// <summary>Loads the main assembly from its bytes.</summary>
    public Assembly LoadMain(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        return LoadFromStream(stream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? name = assemblyName.Name;
        if (name == null)
        {
            return null;
        }

        if (SharedAssemblySet.IsShared(name) && SharedAssemblySet.HostVersion(name) != null)
        {
            // The host's copy: a duplicate of the contract in the folder would make two types called IStep.
            if (_resolver.ResolveAssemblyToPath(assemblyName) is { } shipped && File.Exists(shipped))
            {
                _ignored.Add(Path.GetFileName(shipped));
            }

            return null;
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path == null || !File.Exists(path))
        {
            path = Path.Combine(_folder, name + ".dll");
            if (!File.Exists(path))
            {
                path = _nugetAssemblies.GetValueOrDefault(name);
            }
        }

        if (path == null || !File.Exists(path))
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (_approved != null)
        {
            // A plugin loads only what was approved, byte for byte.
            string? approved = _approved(path);
            if (approved == null)
            {
                return null;
            }

            if (!string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA512.HashData(bytes)), approved, StringComparison.Ordinal))
            {
                throw ContentChanged(path);
            }
        }

        _loaded.Add(name);
        return LoadFromStream(new MemoryStream(bytes, writable: false));
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        if (_resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path && File.Exists(path) && LoadNative(path) is { } resolved)
        {
            return resolved;
        }

        foreach (string directory in _nativeDirectories)
        {
            foreach (string candidate in Candidates(unmanagedDllName))
            {
                string full = Path.Combine(directory, candidate);
                if (File.Exists(full) && LoadNative(full) is { } loaded)
                {
                    return loaded;
                }
            }
        }

        return base.LoadUnmanagedDll(unmanagedDllName);
    }

    /// <summary>
    /// Loads the native library at <paramref name="path"/>. With approved hashes, only when it is approved and its bytes still have that hash: it
    /// is hashed through a handle that denies writes, which stays open until the library is loaded. Null = not approved (not loaded from here).
    /// </summary>
    private nint? LoadNative(string path)
    {
        if (_approved == null)
        {
            return LoadUnmanagedDllFromPath(path);
        }

        if (_approved(path) is not { } approved)
        {
            return null;
        }

        using FileStream guard = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA512.HashData(guard)), approved, StringComparison.Ordinal))
        {
            throw ContentChanged(path);
        }

        return LoadUnmanagedDllFromPath(path);
    }

    private static FileLoadException ContentChanged(string path)
    {
        FileLoadException exception = new(
            $"content-changed: '{Path.GetFileName(path)}' changed after the plugin was approved: it is not loaded. Approve the plugin again (install it again).", path);
        exception.Data[ProblemKey] = "content-changed";
        return exception;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        yield return name;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return name + ".dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
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
