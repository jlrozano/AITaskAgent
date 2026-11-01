using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using Microsoft.Extensions.DependencyInjection;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;

namespace AITaskAgent.NugetLoader;

/// <summary>Where a plugin comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginOrigin>))]
public enum PluginOrigin
{
    /// <summary>A NuGet package (its nuspec is the manifest), resolved and cached by <see cref="NugetResolver"/>.</summary>
    [JsonStringEnumMemberName("nuget")] Nuget,

    /// <summary>A folder with DLLs (a <c>dotnet publish</c> output), with an optional <c>plugin.json</c>.</summary>
    [JsonStringEnumMemberName("folder")] Folder
}

/// <summary>Where <see cref="PluginLoader"/> reads plugins from, caches them and records what it installed.</summary>
public sealed record PluginLoaderOptions
{
    /// <summary>NuGet sources: folder paths or <c>https://…/index.json</c> URLs, in order of preference. None = offline.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Folder of the private NuGet cache (the layout of architecture §16.4); it can be shared with other users of <see cref="NugetResolver"/>.</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>Folder of <c>plugins.json</c> and of <c>plugin-locks/&lt;id&gt;/&lt;version&gt;/nuget.lock.json</c> (one lock per NuGet plugin).</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>
    /// Folder of a <c>steps-packages.json</c> written by the former step-package loader, migrated into <c>plugins.json</c> (with its locks)
    /// the first time the registry is read and no <c>plugins.json</c> exists. Null = <see cref="InstallDirectory"/> itself.
    /// </summary>
    public string? LegacyStepPackagesDirectory { get; init; }

    /// <summary>
    /// Whether <see cref="PluginLoader.InstallAsync"/> may read <see cref="Sources"/> at all (folders and URLs alike, as
    /// <see cref="NugetOptions.AllowNetwork"/>). False = only the cache and the locks already written; reloading at start never reads them.
    /// </summary>
    public bool AllowNetwork { get; init; }

    /// <summary>Whether the <c>http(s)</c> sources may be read when <see cref="AllowNetwork"/> is true (folder sources always may). See <see cref="NugetOptions.AllowRemote"/>.</summary>
    public bool AllowRemote { get; init; } = true;

    /// <summary>How the application calls its setting of sources, for the messages that say none is configured; null = the designer's.</summary>
    public string? SourcesOptionName { get; init; }

    /// <summary>Target framework the dependency groups and libraries are chosen for.</summary>
    public string TargetFramework { get; init; } = "net10.0";

    /// <summary>Most files a folder plugin may have (everything is hashed); more is the problem <c>invalid-plugin</c>.</summary>
    public int MaxFolderFiles { get; init; } = 20_000;

    /// <summary>
    /// Most bytes one entry of a <c>.nupkg</c> may decompress to, when it is extracted or hashed to check the cache (see
    /// <see cref="NugetOptions.MaxExtractedEntryBytes"/>); past it the plugin is <c>nuget-too-large</c>.
    /// </summary>
    public long MaxExtractedEntryBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Most bytes all the entries of one <c>.nupkg</c> may decompress to (see <see cref="NugetOptions.MaxExtractedPackageBytes"/>).</summary>
    public long MaxExtractedPackageBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>
    /// The host's last word before a plugin's contributions are registered (after approval, after loading, after the loader's own collision
    /// checks): return problems to refuse the whole plugin (nothing of it is registered or recorded), for instance a tool name another
    /// plugin or the host already has. Null = accept.
    /// </summary>
    public Func<LoadedPlugin, IReadOnlyList<LoadProblem>>? Accept { get; init; }
}

/// <summary>Decides whether the code of a plugin may be loaded into the process. Asked before anything of the plugin is loaded.</summary>
public interface ITrustPolicy
{
    /// <summary>True to load <paramref name="request"/>'s plugin; false leaves it unloaded and unregistered.</summary>
    ValueTask<bool> ApproveAsync(TrustRequest request, CancellationToken cancellationToken);
}

/// <summary>Why a <see cref="TrustRequest"/> is made.</summary>
public enum TrustRequestKind
{
    /// <summary>A plugin installed for the first time (or never approved).</summary>
    Install,

    /// <summary>Another version of an installed plugin (it loads after a restart).</summary>
    Update,

    /// <summary>An installed plugin whose content is no longer the one that was approved.</summary>
    Changed
}

/// <summary>A package of the NuGet closure of a <see cref="TrustRequest"/>.</summary>
public sealed record TrustDependency(string Id, string Version, string Sha512);

/// <summary>What a <see cref="ITrustPolicy"/> is asked to approve: the exact content, by hash, that would be loaded.</summary>
/// <param name="Id">The plugin's id.</param>
/// <param name="Version">The exact version (NuGet: the one the lock pins; folder: the manifest's or the assembly's).</param>
/// <param name="Sha512">
/// <c>sha512-&lt;base64&gt;</c>. NuGet: of the locked <c>.nupkg</c>. Folder: of the whole tree (<see cref="PluginFolder.Hash"/>).
/// </param>
/// <param name="Dependencies">The rest of the NuGet closure (the host's shared set is left out); empty for a folder.</param>
/// <param name="Assemblies">File names of every managed assembly that may be loaded: the plugin's and its closure's (for a folder, every DLL of it).</param>
/// <param name="Kind">Why it is asked.</param>
public sealed record TrustRequest(
    string Id, string Version, string Sha512, IReadOnlyList<TrustDependency> Dependencies, IReadOnlyList<string> Assemblies, TrustRequestKind Kind)
{
    /// <summary>File names of the native libraries of the closure (<c>runtimes/&lt;rid&gt;/native</c>) that the plugin's code may load; empty when none.</summary>
    public IReadOnlyList<string> NativeLibraries { get; init; } = [];

    /// <summary>Where the plugin comes from.</summary>
    public PluginOrigin Origin { get; init; } = PluginOrigin.Nuget;

    /// <summary>The folder of a folder plugin (absolute); null for NuGet.</summary>
    public string? Folder { get; init; }

    /// <summary>What the plugin would contribute, read from metadata without loading anything (null when it could not be read).</summary>
    public PluginContributions? Contributions { get; init; }
}

/// <summary>Approves everything. Only for tests and for automation that has already decided, outside the process, that the sources are trusted.</summary>
public sealed class AlwaysTrust : ITrustPolicy
{
    /// <summary>The one instance.</summary>
    public static AlwaysTrust Instance { get; } = new();

    /// <inheritdoc/>
    public ValueTask<bool> ApproveAsync(TrustRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}

/// <summary>Approves nothing: plugins already approved (by hash) still load, new or changed ones do not.</summary>
public sealed class NeverTrust : ITrustPolicy
{
    /// <summary>The one instance.</summary>
    public static NeverTrust Instance { get; } = new();

    /// <inheritdoc/>
    public ValueTask<bool> ApproveAsync(TrustRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

/// <summary>A step type registered from a plugin. <see cref="Tagged"/> false = it has no <c>[StepTag]</c> and its tag is its <see cref="Type.FullName"/>.</summary>
public sealed record RegisteredStep(string Tag, Type Type, bool Tagged);

/// <summary>
/// A step type of the plugin that was not registered. <see cref="Reason"/>: <c>not-public</c> (a concrete step the author did not make public; abstract
/// classes, interfaces and open generics are bases, never reported), <c>rejected</c> (the registry refused it), <c>type-load-failed</c> (it could not be described: a property of a type
/// from a missing assembly, an attribute whose constructor throws…).
/// </summary>
public sealed record SkippedStep(string Type, string Reason, string Message);

/// <summary>
/// Something wrong with a plugin (or part of it). Codes that stop it: <c>invalid-package</c>, <c>invalid-plugin</c>, <c>bad-manifest</c>,
/// <c>contract-too-new</c>, <c>id-mismatch</c>, <c>duplicate-id</c>, <c>nuget-offline</c>, <c>nuget-not-found</c>, <c>nuget-conflict</c>,
/// <c>nuget-not-restored</c>, <c>nuget-hash-mismatch</c>, <c>nuget-too-large</c>, <c>nuget-failed</c>, <c>hash-mismatch</c>,
/// <c>content-mismatch</c>, <c>lock-missing</c>, <c>no-assemblies</c>, <c>load-failed</c>, <c>collision</c> (a tag, hook name or response type
/// name another owner has: the message names both), <c>multiple-configurations</c>, <c>multiple-extensions</c> (several <c>IExtension</c> classes and
/// the manifest does not name one), <c>extension-not-found</c> (the manifest names a class that is not there), <c>invalid-extension</c> (the only
/// <c>IExtension</c> cannot be created), <c>extension-failed</c> (its constructor or <c>Register</c> threw), <c>restart-required</c>,
/// <c>not-approved</c>, and whatever <see cref="PluginLoaderOptions.Accept"/> returns. Warnings (the plugin still loads): <c>info-mismatch</c>
/// (<c>IExtension.Info()</c> does not match what <c>Register</c> registered: the message names the items), <c>info-failed</c> (<c>Info()</c>
/// threw), <c>ignored-extension</c> (another <c>IExtension</c> class that cannot be created), <c>legacy-manifest</c> (<c>extension.json</c> read as <c>plugin.json</c>), <c>invalid-hook</c>, <c>invalid-response-type</c>,
/// <c>invalid-contribution</c>, <c>type-load-failed</c>. <c>store-corrupt</c> is in <see cref="PluginLoader.StoreProblems"/> for the whole file
/// and in a reload report for an entry whose version is not exact.
/// </summary>
public sealed record LoadProblem(string Code, string Message);

/// <summary>What one install or reload of a plugin did.</summary>
public sealed record LoadReport
{
    /// <summary>The plugin's id.</summary>
    public required string Id { get; init; }

    /// <summary>The exact version (the requested text when nothing was resolved).</summary>
    public required string Version { get; init; }

    /// <summary>Where the plugin comes from.</summary>
    public PluginOrigin Origin { get; init; } = PluginOrigin.Nuget;

    /// <summary>The folder of a folder plugin; null for NuGet.</summary>
    public string? Folder { get; init; }

    /// <summary>Whether the trust policy approved the plugin (or its hash was already approved).</summary>
    public bool Approved { get; init; }

    /// <summary>
    /// Set by the install methods: true when <c>plugins.json</c> records the plugin at <see cref="Version"/> with this content after the
    /// install (installed now, recorded for the next start, or already installed). False when it was refused, failed, or approved but
    /// nothing of it could be registered.
    /// </summary>
    public bool Recorded { get; init; }

    /// <summary>The step types registered (or already registered by this loader).</summary>
    public IReadOnlyList<RegisteredStep> Registered { get; init; } = [];

    /// <summary>Step types of the plugin left out, with the reason.</summary>
    public IReadOnlyList<SkippedStep> Skipped { get; init; } = [];

    /// <summary>What went wrong (see <see cref="LoadProblem"/> for the codes); empty when the plugin loaded fully.</summary>
    public IReadOnlyList<LoadProblem> Problems { get; init; } = [];

    /// <summary>For the record: NuGet notes (host-supplied packages), plugins already loaded, assemblies scanned.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>What the plugin contributes, read from metadata (null when it was not read: the request was invalid or could not be resolved).</summary>
    public PluginContributions? Contributions { get; init; }

    /// <summary>The loaded plugin when it is registered in this process (now or before); null otherwise.</summary>
    public LoadedPlugin? Plugin { get; init; }

    /// <summary>True when the change is recorded but only takes effect after the process restarts.</summary>
    public bool RestartRequired => Problems.Any(p => p.Code == "restart-required");
}

/// <summary>A plugin recorded in <c>plugins.json</c>.</summary>
/// <param name="Id">The plugin's id.</param>
/// <param name="Version">The exact version.</param>
/// <param name="Sha512">NuGet: SHA-512 of the locked <c>.nupkg</c>; folder: the tree hash recorded when it was approved.</param>
/// <param name="ApprovedSha512">The hash the trust policy approved; reloading asks again when the content no longer has it.</param>
/// <param name="InstalledAtUtc">When it was installed or last updated.</param>
/// <param name="Steps">The tags it registered the last time it loaded.</param>
public sealed record InstalledPlugin(string Id, string Version, string Sha512, string? ApprovedSha512, DateTimeOffset InstalledAtUtc, IReadOnlyList<string> Steps)
{
    /// <summary>Where it comes from.</summary>
    public PluginOrigin Origin { get; init; } = PluginOrigin.Nuget;

    /// <summary>The folder of a folder plugin (absolute); null for NuGet.</summary>
    public string? Folder { get; init; }

    /// <summary>NuGet: the version or range the install asked for; null for a folder.</summary>
    public string? Requested { get; init; }

    /// <summary>The manifest's description, when there is one.</summary>
    public string? Description { get; init; }
}

/// <summary>What <see cref="PluginLoader.RemoveAsync"/> did.</summary>
/// <param name="Id">The id asked for.</param>
/// <param name="Removed">False when it was not installed (or the id is not valid).</param>
/// <param name="RestartRequired">True when its types are loaded in this process: they stay registered until it restarts (contexts are not collectible).</param>
/// <param name="Message">What happened, for the user.</param>
public sealed record RemoveResult(string Id, bool Removed, bool RestartRequired, string Message);

/// <summary>A hook, validator or result validator a plugin declares (<c>[Hook]</c>, <c>[Validator]</c>, <c>[ResultValidator]</c>).</summary>
/// <param name="Name">Registry name: <c>[Hook(Name = …)]</c> or <c>Class.Method</c>.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Method">The <c>public static</c> method.</param>
/// <param name="Description">The attribute's description.</param>
public sealed record PluginHook(string Name, HookKind Kind, MethodInfo Method, string? Description)
{
    /// <summary>The method as a delegate of its own shape (<c>Func</c>/<c>Action</c> when it fits); <see cref="IHookRegistry"/> rebinds it to the slot.</summary>
    public Delegate CreateDelegate()
    {
        Type[] types = [.. Method.GetParameters().Select(p => p.ParameterType), Method.ReturnType];
        return Method.CreateDelegate(Expression.GetDelegateType(types));
    }
}

/// <summary>A class of a plugin marked <c>[ResponseType]</c>: its schema is registered under <see cref="Name"/>.</summary>
public sealed record PluginResponseType(string Name, Type Type);

/// <summary>
/// A plugin registered in this process: its steps are in the loader's <c>IStepRegistry</c>; the rest is for the host to wire (the loader
/// instantiates nothing of it, except the plugin's <see cref="IExtension"/> when it has one): tools with a parameterless constructor, tool
/// sources, hooks (<see cref="PluginRegistration.AddHooks"/>), response types, template sources and its configuration class. For a plugin driven by an
/// <see cref="IExtension"/> the lists are what <c>Register</c> really registered (<see cref="Extension"/> has the instances and the comparison with
/// <c>Info()</c>), not what the metadata scan predicted.
/// </summary>
public sealed record LoadedPlugin(
    string Id,
    string Version,
    PluginOrigin Origin,
    string Folder,
    IReadOnlyList<Assembly> Assemblies,
    IReadOnlyList<RegisteredStep> Steps,
    IReadOnlyList<Type> Tools,
    IReadOnlyList<Type> ToolSources,
    IReadOnlyList<PluginHook> Hooks,
    IReadOnlyList<PluginResponseType> ResponseTypes,
    IReadOnlyList<Type> TemplateSources,
    Type? Configuration)
{
    /// <summary>The manifest's description, when there is one.</summary>
    public string? Description { get; init; }

    /// <summary>The source name of its hooks in an <see cref="IHookRegistry"/>: <c>plugin:&lt;id&gt;</c>.</summary>
    public string HookSource => "plugin:" + Id;

    /// <summary>The <see cref="IExtension"/> that drives the plugin and what it registered; null for a plugin loaded by type discovery (it has none).</summary>
    public PluginExtension? Extension { get; init; }
}

/// <summary>
/// The <see cref="IExtension"/> of a plugin after <see cref="PluginLoader"/> ran its <c>Register</c> against a capturing builder
/// (<see cref="ExtensionCapture"/>): the instance, its declaration (<c>Info()</c>), the real registration and the difference between them.
/// </summary>
/// <param name="Instance">The extension, created inside the plugin's load context after approval.</param>
/// <param name="TypeName">Its full class name.</param>
/// <param name="Declared">What <c>Info()</c> announced (empty when it threw: <c>info-failed</c>).</param>
/// <param name="Registered">What <c>Register</c> really registered (and the configuration the extension reports), in the same shape.</param>
/// <param name="Missing">Announced by <c>Info()</c> and not registered.</param>
/// <param name="Extra">Registered and not announced.</param>
/// <param name="ToolInstances"><c>ITool</c> instances passed to the builder (shared by every engine).</param>
/// <param name="ToolSourceInstances"><c>IToolSource</c> instances passed to the builder.</param>
/// <param name="TemplateSourceInstances"><c>ITemplateSource</c> instances passed to the builder.</param>
/// <param name="Services">The services the extension registered in the capturing builder (the host adds them to the engine collection).</param>
public sealed record PluginExtension(
    IExtension Instance,
    string TypeName,
    ExtensionInfo Declared,
    ExtensionInfo Registered,
    IReadOnlyList<ExtensionEntry> Missing,
    IReadOnlyList<ExtensionEntry> Extra,
    IReadOnlyList<object> ToolInstances,
    IReadOnlyList<object> ToolSourceInstances,
    IReadOnlyList<object> TemplateSourceInstances,
    IServiceCollection Services)
{
    /// <summary>True when <c>Info()</c> announced exactly what was registered.</summary>
    public bool Matches => Missing.Count == 0 && Extra.Count == 0;
}

/// <summary>Who a type belongs to, read from the name of its load context (<c>plugin:&lt;origin&gt;:&lt;id&gt;/&lt;version&gt;</c>).</summary>
/// <param name="Origin">Where the plugin comes from.</param>
/// <param name="Id">Its id.</param>
/// <param name="Version">Its exact version.</param>
public sealed record PluginIdentity(PluginOrigin Origin, string Id, string Version)
{
    /// <summary>The prefix of the load context of every plugin.</summary>
    public const string ContextPrefix = "plugin:";

    /// <summary>The name of the load context of a plugin.</summary>
    public static string ContextName(PluginOrigin origin, string id, string version) => $"{ContextPrefix}{OriginText(origin)}:{id}/{version}";

    /// <summary>The plugin of <paramref name="type"/>, or null when it does not come from a plugin.</summary>
    public static PluginIdentity? Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Of(type.Assembly);
    }

    /// <summary>The plugin of <paramref name="assembly"/>, or null when it does not come from a plugin.</summary>
    public static PluginIdentity? Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Parse(AssemblyLoadContext.GetLoadContext(assembly)?.Name);
    }

    /// <summary>Parses a context name made by <see cref="ContextName"/>; null for anything else.</summary>
    public static PluginIdentity? Parse(string? contextName)
    {
        if (contextName == null || !contextName.StartsWith(ContextPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string rest = contextName[ContextPrefix.Length..];
        int colon = rest.IndexOf(':');
        int slash = rest.LastIndexOf('/');
        if (colon <= 0 || slash <= colon + 1 || slash == rest.Length - 1)
        {
            return null;
        }

        PluginOrigin? origin = rest[..colon] switch { "nuget" => PluginOrigin.Nuget, "folder" => PluginOrigin.Folder, _ => null };
        return origin == null ? null : new PluginIdentity(origin.Value, rest[(colon + 1)..slash], rest[(slash + 1)..]);
    }

    /// <summary><c>nuget</c> or <c>folder</c>.</summary>
    public static string OriginText(PluginOrigin origin) => origin == PluginOrigin.Folder ? "folder" : "nuget";

    /// <inheritdoc/>
    public override string ToString() => $"plugin '{Id}' {Version} ({OriginText(Origin)})";
}

/// <summary>Wiring helpers for what a <see cref="LoadedPlugin"/> brings besides steps.</summary>
public static class PluginRegistration
{
    /// <summary>
    /// Adds the hooks of <paramref name="plugin"/> to <paramref name="registry"/> with source <c>plugin:&lt;id&gt;</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A name is already registered (the registry's message names both sources).</exception>
    public static void AddHooks(HookRegistry registry, LoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(plugin);
        foreach (PluginHook hook in plugin.Hooks)
        {
            registry.Add(hook.Name, hook.Kind, hook.CreateDelegate(), hook.Description, plugin.HookSource);
        }
    }
}

/// <summary>An entry of <c>plugins.json</c> as written.</summary>
internal sealed record PluginEntry
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("origin")] public PluginOrigin Origin { get; init; } = PluginOrigin.Nuget;

    [JsonPropertyName("version")] public required string Version { get; init; }

    /// <summary>NuGet: the version or range the install asked for (the lock's <c>requiresHash</c> is made from it).</summary>
    [JsonPropertyName("requested")] public string? Requested { get; init; }

    /// <summary>Folder: the plugin's folder (absolute).</summary>
    [JsonPropertyName("folder")] public string? Folder { get; init; }

    [JsonPropertyName("sha512")] public required string Sha512 { get; init; }

    [JsonPropertyName("approvedSha512")] public string? ApprovedSha512 { get; init; }

    /// <summary>NuGet: SHA-256 of the whole approved closure (id/version/hash of every locked package): a changed dependency asks again too.</summary>
    [JsonPropertyName("approvedClosure")] public string? ApprovedClosure { get; init; }

    [JsonPropertyName("description")] public string? Description { get; init; }

    [JsonPropertyName("installedAtUtc")] public DateTimeOffset InstalledAtUtc { get; init; }

    [JsonPropertyName("steps")] public IReadOnlyList<string> Steps { get; init; } = [];

    public InstalledPlugin ToPublic() => new(Id, Version, Sha512, ApprovedSha512, InstalledAtUtc, Steps)
    {
        Origin = Origin,
        Folder = Folder,
        Requested = Requested,
        Description = Description
    };
}

/// <summary>The whole of <c>plugins.json</c>.</summary>
internal sealed class PluginFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    [JsonPropertyName("plugins")] public List<PluginEntry> Plugins { get; set; } = [];
}
