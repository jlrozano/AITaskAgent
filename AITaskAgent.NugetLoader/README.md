# AITaskAgent.NugetLoader

The one NuGet resolver of the applications built on AITaskAgent (`NugetResolver`: declarations → `nuget.lock.json` → assemblies from a
private cache, architecture §16 of `docs/designer/architecture.md`), its load context (`IsolatedLoadContext`, the host wins for
`SharedAssemblySet`), **`PluginLoader`** (installing plugins while the process runs: NuGet packages or folders of DLLs that bring steps,
tools, hooks, templates and configuration) and **`LocalNupkgRepository`** (a folder of `.nupkg` files added from the computer). It
references `AITaskAgent.Abstractions` only (no `AITaskAgent.Core`, no `AITaskAgent.Extensions.Llm`, no ASP.NET, nothing of the designer):
the contracts of Core and Llm (`ITemplateSource`, `ITool`, `IToolSource`) are recognised by name in metadata. The contract is
`docs/designer/architecture.md`, section «Plugins: unified loading (contract)».

## Using it from any application

```csharp
IStepRegistry registry = app.Services.GetRequiredService<IStepRegistry>();
PluginLoader plugins = new(new PluginLoaderOptions
{
    Sources = [repositoryFolder, "https://api.nuget.org/v3/index.json"],   // folders or URLs; none = offline
    AllowNetwork = true,                                                   // false (default): only the cache and existing locks
    AllowRemote = true,                                                    // false: folder sources only, the https ones are never read
    CacheDirectory = Path.Combine(dataDir, "nuget"),
    InstallDirectory = dataDir,                                            // plugins.json + plugin-locks/
    LegacyStepPackagesDirectory = Path.Combine(dataDir, "step-packages"),  // migrated once from steps-packages.json
    SourcesOptionName = "MyApp:NuGetSources",                              // quoted when no source is configured
    Accept = plugin => ToolNameClashes(plugin)                             // the host's veto (all or nothing)
}, registry, new MyTrustDialog(), logger);
plugins.PluginsChanged += (_, report) => RefreshCatalog(report);

await plugins.LoadInstalledAsync();                                        // at start: from the cache, never from a source
LoadReport nuget = await plugins.InstallAsync("Acme.Steps", "1.2.0");      // version or range, never floating
LoadReport folder = await plugins.InstallFolderAsync(@"C:\plugins\Acme");  // plugin.json optional
foreach (LoadedPlugin p in plugins.Loaded)
{
    PluginRegistration.AddHooks(hookRegistry, p);                          // source "plugin:<id>"
    // p.Tools (parameterless ITool), p.ToolSources, p.TemplateSources, p.ResponseTypes, p.Configuration: the host wires them.
}
```

The install methods and `LoadInstalledAsync` report problems in the `LoadReport` (`Problems`, `Skipped`) instead of throwing; only
cancellation and I/O failures of the install folder are thrown. `PluginLoader.InspectFolder(folder)` reads a folder (manifest, tree hash,
contributions) without loading or recording anything.

## What a plugin contributes

Read from the **metadata** of the plugin's own assemblies (`PluginScanner`, `System.Reflection.Metadata`): nothing is loaded before
approval, and the result (`PluginContributions`) is part of the trust request. Own assemblies: the direct package's `lib/<tfm>` DLLs of a
NuGet plugin (never its dependencies), the `assemblies` of `plugin.json` of a folder (or `<folder>.dll`, or its only DLL). A NuGet package
with `plugin/plugin.json` is a folder inside the package: `plugin/` is extracted with its relative paths, read with the folder rules and is the
plugin's `PluginContext.Folder`, while its nuspec dependencies are resolved from the sources as usual (architecture §25.2). Base types and
interfaces are followed into the plugin's dependency files (metadata) and into the host's shared assemblies.

- `IStep`: public, concrete, non-generic; tag = `[StepTag]`, else `Type.FullName` (never dropped).
- `ITool` with a public parameterless constructor; `IToolSource` (tools with arguments, `Create(PluginContext)`).
- `public static` methods with `[Hook(Name = …)]` (name `Class.Method` without `Name`), `[Validator]`, `[ResultValidator]`.
- Classes with `[ResponseType]` / `[ResponseType("Name")]`.
- `ITemplateSource`; at most one `IPluginConfiguration` (two is `multiple-configurations`).
- `IExtension` (entry point, 2026-10-22): the one public concrete class that implements it (public parameterless constructor), or the one `plugin.json`
  names with `"extension": "Full.Type.Name"`, is reported as `PluginContributions.Extension`; several without a name are `multiple-extensions`, a name that
  is not there `extension-not-found`, an `IExtension` that cannot be created `invalid-extension` (all stop the plugin, from metadata, before trust).

A plugin with an `IExtension` is **driven by it**: after approval (never before) the loader creates it inside the plugin's load context, calls `Info()` and
runs `Register` against an `ExtensionCapture`; what it really registered is what `LoadedPlugin` carries (steps go to the loader's registry), and a
difference with `Info()` is the warning `info-mismatch`. `PluginExtensionRunner.Run` does the same for a host that references the plugin directly. A
plugin without an `IExtension` loads by the type discovery above (architecture §25.3a).

## Trust

Nothing of a plugin is loaded until `ITrustPolicy.ApproveAsync` says yes. The request carries the exact content: for NuGet the locked
`.nupkg` SHA-512, the id/version/hash of its closure, the file names of every managed assembly (`Assemblies`) and native library
(`NativeLibraries`); for a folder the SHA-512 of its whole tree (every file: `relative/path\t<hex sha512>\n` lines sorted, hashed) and its
DLLs; for both `Origin`, `Folder` and `Contributions`. The approved hash is remembered in `plugins.json`: a reload with the same content
asks nothing, a changed one asks again (`TrustRequestKind.Changed`). `AlwaysTrust` is for tests and for automation that decided trust
elsewhere; `NeverTrust` loads only what was already approved. Before loading, every cached `.nupkg` is checked against the lock and every
extracted file against its `.nupkg`; a folder plugin's own assemblies are loaded from bytes checked against the tree, and its load context
loads a private dependency only when it is a file of the approved tree with the approved hash.

A host that approves in two steps over HTTP (the designer's `ApprovalTrustPolicy`) protects against installs nobody meant and against
cross-site requests, not against another process of the same user: without authentication, any local process can ask and then approve.

## All or nothing

A plugin whose step tag, hook name or response type name another owner already has (another plugin of the loader, or the host) is not
registered at all (`collision`, the message names both owners); checked from metadata before trust, and again after loading. The host
adds its own checks (tool names) with `PluginLoaderOptions.Accept`. Another version of a loaded plugin is recorded for the next start
(`restart-required`).

## Local repository of `.nupkg` files

`LocalNupkgRepository(folder)` adds packages from a stream, a file or a folder (at most 200 files): the zip directory and the nuspec are
the only things read (zip signature, one `.nuspec` at the root, nuspec under 1 MiB of real bytes and without DTD, id and version from the
nuspec, id checked as a NuGet id), the file is written to a temporary file with a size limit (100 MiB) and moved to `id.version.nupkg`
(lower case, normalized version). The same bytes again are idempotent; other bytes for the same id and version are `package_conflict`.
Adding loads nothing: register the folder as a source of the loader (`Sources`) and install from it, offline with `AllowRemote = false`.

## Limits

- **Adding is live; removing and updating need a restart.** Load contexts are not collectible and `IStepRegistry` has no removal.
- **The plugin's code runs in the host process**, with its permissions and its secrets in memory. Trust is the only barrier: no sandbox.
- Extraction into the cache counts the bytes really written (`NugetOptions.MaxExtractedEntryBytes`, `MaxExtractedPackageBytes`,
  `MaxPackageEntries`): a decompression bomb stops with `too_large`.
- Several loaders or processes over one install folder keep each other's entries: every change re-reads `plugins.json` under
  `plugins.json.lock` (retried for up to 10 s); a reload that changes nothing writes nothing. Two installs of the same plugin at the same
  time end with whichever wrote last.
- Dependencies in the shared set (`AITaskAgent.*`, `Microsoft.Extensions.*`, …) come from the host only when the host can load them by
  name. A host without `AITaskAgent.Core` makes a package built on `StepBase` download and load its own private copy of Core.
- NuGet dependencies are read again when the load context first resolves them and native libraries are loaded from their path: a file
  swapped in the cache between the check and that moment is not detected (whoever can write the cache can already run code as the user).
- A folder plugin ships its dependencies in the folder: the `nuget` section of a legacy `extension.json` is ignored (with a warning).
