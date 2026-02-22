using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Constants;
using BRMS.Core.Core.NugetUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace BRMS.Core.Core;

public class NuGetPackageLoader(NuGetLoaderConfig? cfg, Microsoft.Extensions.Logging.ILogger msLogger)
{

    private const string CHECK = "[OK]";
    private const string CROSS = "[ERROR]";
    private const string ARROW = "-->";
    private readonly NuGetLoggerAdapter _logger = new(msLogger);
    private readonly List<Type> _pluginTypes = [];
    private readonly NuGetLoaderConfig? _cfg = cfg;

    // Resolver por paquete para administrados (fallback) en Default ALC
    private readonly Dictionary<string, bool> _resolvingRegisteredForPackagePath = new(StringComparer.OrdinalIgnoreCase);

    public IList<string> LoadErrors { get; } = [];
    public IList<string> LoadedPlugins { get; } = [];

    public async Task<bool> LoadPluginsAsync(IServiceCollection serviceCollection)
    {

        if (_cfg == null)
        {
            return true;
        }

        LoadErrors.Clear();

        foreach (NuGetPluginConfig package in _cfg.Plugins)
        {
            await LoadPackageAsync(
                package.PackageId,
                package.Version,
                serviceCollection

            );
        }

        return LoadErrors.Count == 0;
    }

    public void LoadAssembly(IServiceCollection serviceCollection, Assembly assembly)
    {
        if (assembly != null)
        {
            // Procesar tipos
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                //_logger.LogWarning($"Algunos tipos no se pudieron cargar desde {filePath}");
                types = rtle.Types.Where(t => t != null).ToArray()!;
                foreach (Exception? le in rtle.LoaderExceptions.Where(e => e != null).Take(3))
                {
                    _logger.LogDebug($"  LoaderException: {le!.Message}");
                }

                if (types.Length == 0)
                {
                    _logger.LogError($"{CROSS} No se pudo cargar ningún tipo desde {assembly.FullName}");
                    return;
                }

                _logger.LogInformation($"  {CHECK} Se cargaron {types.Length} tipos parcialmente");
            }

            foreach (Type? type in types.Where(t => t != null && !t.IsAbstract && !t.IsInterface))
            {
                try
                {
                    if (typeof(IPlugin).IsAssignableFrom(type))
                    {
                        _logger.LogDebug($"Plugin {type.Name} encontrado en {assembly.FullName}");
                        _pluginTypes.Add(type);
                        AddFromBaseType(serviceCollection, type, typeof(IPlugin), ServiceLifetime.Transient);
                        continue;
                    }

                    if (typeof(IBRMSConfigurationProvider).IsAssignableFrom(type))
                    {
                        _logger.LogDebug($"Configuration provider {type.Name} encontrado en {assembly.FullName}");
                        AddFromBaseType(serviceCollection, type, typeof(IBRMSConfigurationProvider), ServiceLifetime.Singleton);
                        continue;
                    }

                    if (typeof(IRule).IsAssignableFrom(type))
                    {
                        _logger.LogDebug($"Rule {type.Name} encontrada en {assembly.FullName}");
                        RuleManager.AddRule(type);
                        continue;
                    }

                    // Registro por atributos
                    AddFromBaseType(serviceCollection, type);
                }
                catch (FileLoadException fle) when (fle.Message.Contains("Could not load", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning($"Tipo {type.Name} omitido por dependencia faltante: {fle.Message}");
                }
                catch (TypeLoadException tle)
                {
                    _logger.LogWarning($"Tipo {type.Name} no se pudo cargar: {tle.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Error procesando tipo {type.Name}: {ex.GetType().Name} - {ex.Message}");
                }
            }

        }

    }

    public async Task RegisterPluginsAsync(IServiceProvider serviceProvider)
    {
        foreach (Type pluginType in _pluginTypes)
        {
            if (serviceProvider.GetService(pluginType) is not IPlugin plugin)
            {
                _logger.LogError($"Error instanciando plugin {pluginType.FullName}");
                continue;
            }
            _logger.LogInformation($"Ejecutando registro de {pluginType.FullName}");
            await plugin.Register();
            _logger.LogInformation($"Registro {pluginType.FullName} ejecutado.");
        }
    }
    private static string PickCompatibleTfm(string[] available, string desired)
    {
        // Normaliza a minúsculas
        var set = new HashSet<string>(available.Select(a => a.ToLowerInvariant()));

        // 1. Exacto
        if (set.Contains(desired.ToLowerInvariant()))
        {
            return desired;
        }

        // 2. NetX.Y fallback descendente
        if (desired.StartsWith("net", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(new string([.. desired.Skip(3).TakeWhile(char.IsDigit)]), out int ver))
        {
            for (int v = ver; v >= 5; v--) // baja hasta net5.0
            {
                string candidate = $"net{v}.0";
                if (set.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        // 3. Netstandard
        if (set.Contains("netstandard2.1"))
        {
            return "netstandard2.1";
        }

        if (set.Contains("netstandard2.0"))
        {
            return "netstandard2.0";
        }

        if (set.Contains("netstandard1.0"))
        {
            return "netstandard1.0";
        }

        // 4. Último recurso: el primero disponible
        return available.First();
    }

    private async Task LoadPackageAsync(string packageId, string version, IServiceCollection serviceCollection)
    {

        if (IsSharedAssembly(packageId)) //, version, _cfg!.GlobalPackagesFolder))
        {
            _logger.LogDebug($"[SKIPPED...] {packageId}.{version}");
            return;
        }
        _logger.LogInformation($"[LOADING....] {packageId}.{version}");
        var cache = new SourceCacheContext();
        IEnumerable<Lazy<INuGetResourceProvider>> repositories = Repository.Provider.GetCoreV3();

        var packageVersion = new NuGetVersion(version);
        string packagePath = Path.Combine(_cfg!.GlobalPackagesFolder, packageId, version);
        _ = Directory.CreateDirectory(packagePath);

        using var packageStream = new MemoryStream();

        FindPackageByIdResource? resource = null;


        foreach (string? source in _cfg.PackageSources.Select(s => s.Url))
        {
            try
            {
                if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    SourceRepository sourceRepository = Repository.Factory.GetCoreV3(source);
                    resource = await sourceRepository.GetResourceAsync<FindPackageByIdResource>();
                }
                else
                {
                    var packageSource = new PackageSource(source);
                    var sourceRepository = new SourceRepository(packageSource, repositories);
                    resource = await sourceRepository.GetResourceAsync<FindPackageByIdResource>();
                }

                if (await resource.CopyNupkgToStreamAsync(packageId, packageVersion, packageStream, cache, _logger, default))
                {
                    _ = packageStream.Seek(0, SeekOrigin.Begin);
                    break;
                }
            }
            catch
            {
                continue;
            }
        }

        if (packageStream == null || packageStream.Length < 1)
        {
            _logger.LogInformation($"No se encontró nuget {packageId}.{version}");
            LoadErrors.Add(packageId);
            return;
        }

        var packageReader = new PackageArchiveReader(packageStream);
        var allFiles = packageReader.GetFiles().ToList();

        //try
        //{
        //    using var packageFile = File.Create(Path.Combine(_cfg.GlobalPackagesFolder, $"{packageId}.{packageVersion}.zip"));

        //    await packageStream.CopyToAsync(packageFile);
        //    packageStream.Seek(0, SeekOrigin.Begin);
        //}
        //catch { }


        //try
        //{

        //    await File.WriteAllTextAsync(Path.Combine(_cfg.GlobalPackagesFolder, $"{packageId}.{packageVersion}.txt"),
        //    string.Join("\n", allFiles));

        //}
        //catch { }

        // TFM preferido del host + fallback
        string tfm = GetPreferredTargetFramework(packageReader);
        string rid = GetCurrentRuntimeIdentifier();

        // Registrar resolutor administrado (fallback) una sola vez por carpeta de paquete
        RegisterDefaultResolvingFallback(packagePath, tfm);
        _logger.LogDebug($"Cargando último framewrok compatible...{packageId}.{packageVersion}");
        string[] available = [.. allFiles
         //.Select(f => f.Replace('\\', '/')) // normalizar separadores
         //.Where(f => f.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
         .Select(f => f.Split('/'))
         .Where(parts => parts.Length > 2) // lib/{tfm}/{file}
         .Select(parts => parts[1])        // el TFM
         .Distinct(StringComparer.OrdinalIgnoreCase)];


        string chosenTfm = PickCompatibleTfm(available, tfm);

        // En el filtro de archivos, corrige la extensión:

        IEnumerable<string> files = allFiles
            .Where(f => f.Contains("/" + chosenTfm + "/") ||
                        f.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".resource.dll", StringComparison.OrdinalIgnoreCase));

        // Descargar/copiar archivos relevantes del paquete
        //var files = packageReader.GetFiles()
        //.Where(f =>
        //    f.Contains("/" + tfm + "/") || f.Contains("lib/"));
        //f.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase) ||
        //f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));

        foreach (string file in files)
        {
            string filePath = Path.Combine(packagePath, file.Replace("/", "\\"));
            if (!File.Exists(filePath))
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                try
                {
                    using FileStream fileStream = File.Create(filePath);
                    await packageReader.GetStream(file).CopyToAsync(fileStream);
                }
                catch
                {
                    // ignorar condiciones de carrera
                }
            }
        }

        // Resolver dependencias transitivas desde nuspec
        string framework = tfm;

        // Dentro de LoadPackageAsync, reemplaza el bloque de dependencias por esto:

        // Resolver dependencias transitivas desde nuspec (incluyendo RID/nativos)
        var dependencies = packageReader
            .GetPackageDependencies()
            .SelectMany(g => g.Packages) // no solo el grupo exacto, todos
            .Where(p => p?.Id != null && !LoadedPlugins.Contains(p.Id))
            .ToList();

        foreach (PackageDependency? pkg in dependencies)
        {
            _logger.LogDebug($"--> Resolviendo dependencia {pkg.Id} {pkg.VersionRange}");
            await LoadPackageAsync(pkg.Id, pkg.VersionRange?.OriginalString ?? "", serviceCollection);
        }


        //var dependencies = packageReader
        //    .GetPackageDependencies()
        //    .Where(d => d.TargetFramework?.ToString() == framework)
        //    .ToList();

        //foreach (var packRef in dependencies)
        //{
        //    foreach (var pkg in packRef.Packages.Where(p => p?.Id != null && !LoadedPlugins.Contains(p.Id)))
        //    {
        //        await LoadPackageAsync(pkg.Id, pkg.VersionRange.OriginalString, serviceCollection);
        //    }
        //}

        bool error = false;
        IEnumerable<string> allDlls = Directory.EnumerateFiles(packagePath, "*.dll", SearchOption.AllDirectories);
        //.Where(p =>
        //    p.Replace('/', '\\').Contains($"\\lib\\{tfm}\\") ||
        //    p.Replace('/', '\\').Contains("\\runtimes\\") ||
        //    p.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
        //.ToList();
        var assemblies = new List<Assembly>();
        foreach (string filePath in allDlls)
        {
            // if (!File.Exists(filePath)) continue;

            // Saltar assemblies compartidos (host-primero) para no duplicar
            AssemblyName? asmName = SafeGetAssemblyName(filePath);
            if (asmName != null && IsSharedAssembly(asmName.Name)) //, asmName.Version?.ToString() ?? "", _cfg.GlobalPackagesFolder))
            {
                _logger.LogDebug($"Saltando carga de shared assembly {asmName.Name} ({filePath}) — lo provee el host");
                continue;
            }

            try
            {
                // Recursos satélite: registrar ResourceManager y no procesar tipos
                if (filePath.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
                {
                    Assembly resourcesAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(filePath);
                    TryRegisterResourceManager(resourcesAsm, packageId, filePath);
                    continue;
                }

                Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(filePath);

                // Adjuntar resolver nativo por-assembly (host-primero, luego paquete)
                AttachNativeResolver(assembly, packagePath, rid);
                assemblies.Add(assembly);
            }
            catch (ReflectionTypeLoadException rtlex)
            {
                _logger.LogWarning($"Error al cargar tipos desde {filePath}: {rtlex.Message}");
                foreach (Exception? le in rtlex.LoaderExceptions.Where(e => e != null))
                {
                    _logger.LogWarning($"  LoaderException: {le!.GetType().Name} - {le.Message}");
                }
            }
            catch (BadImageFormatException)
            {
                // No es un assemby, no hay que hacer nada
            }
            catch (Exception ex)
            {
                error = true;
                _logger.LogWarning($"Error al procesar assembly {filePath}: {ex.Message}");
            }
        }
        foreach (Assembly assembly in assemblies)
        {
            LoadAssembly(serviceCollection, assembly);
        }

        if (error)
        {
            LoadErrors.Add(packageId);
        }
        else
        {
            LoadedPlugins.Add(packageId);
        }

        _logger.LogInformation($"[LOADED] {packageId}.{version}");
    }


    // Host-primero: si Default no lo resuelve, buscar en la carpeta del paquete
    private void RegisterDefaultResolvingFallback(string packagePath, string tfm)
    {
        if (_resolvingRegisteredForPackagePath.ContainsKey(packagePath))
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name.Name) ||
                    name.Name.StartsWith("window", StringComparison.CurrentCultureIgnoreCase) ||
                    name.Name.StartsWith("microsoft.", StringComparison.CurrentCultureIgnoreCase))
                {
                    return null;
                }

                // usar assemblies ya cargados en host si coinciden
                Assembly? hostAsm = AssemblyLoadContext.Default.Assemblies
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(a.GetName().Name, $"{name.Name}.dll", StringComparison.OrdinalIgnoreCase));
                if (hostAsm != null)
                {
                    return hostAsm;
                }


                // fallback: buscar en carpeta del paquete
                string? candidate = Directory.EnumerateFiles(packagePath, name.Name + ".dll", SearchOption.AllDirectories)
                    .OrderByDescending(p => p.Replace('/', '\\').Contains($"\\lib\\{tfm}\\"))
                    .ThenBy(p => p.Length)
                    .FirstOrDefault();
                return candidate != null ? ctx.LoadFromAssemblyPath(candidate) : null;
            }
            catch
            {
                return null;
            }
        };

        _resolvingRegisteredForPackagePath[packagePath] = true;
        _logger.LogDebug($"{CHECK} Resolving fallback registrado para {packagePath}");
    }

    private void AttachNativeResolver(Assembly managedAssembly, string packagePath, string rid)
    {

        try
        {
            NativeLibrary.SetDllImportResolver(managedAssembly, (name, asm, searchPath) =>
        {
            if (NativeLibrary.TryLoad(name, out nint handle))
            {
                return handle;
            }

            string ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ".dll"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? ".dylib"
                    : ".so";

            static bool IsMatch(string fileName, string libraryName, string osExt)
            {
                if (Path.HasExtension(libraryName))
                {
                    return string.Equals(fileName, libraryName, StringComparison.OrdinalIgnoreCase);
                }

                if (!fileName.EndsWith(osExt, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string stem = Path.GetFileNameWithoutExtension(fileName);
                if (string.Equals(stem, libraryName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string libName = "lib" + libraryName;
                return string.Equals(stem, libName, StringComparison.OrdinalIgnoreCase) || stem.StartsWith(libraryName + ".", StringComparison.OrdinalIgnoreCase) || stem.StartsWith(libName + ".", StringComparison.OrdinalIgnoreCase);
            }

            static IEnumerable<string> GetCandidateFileNames(string libraryName, string osExt)
            {
                if (Path.HasExtension(libraryName))
                {
                    return [libraryName];
                }

                if (osExt == ".dll")
                {
                    return [libraryName + osExt];
                }

                string libName = libraryName.StartsWith("lib", StringComparison.OrdinalIgnoreCase)
                    ? libraryName
                    : "lib" + libraryName;

                return [libName + osExt, libraryName + osExt];
            }

            // 2. Buscar en runtimes/{rid}/native
            string nativeDir = Path.Combine(packagePath, "runtimes", rid, "native");
            foreach (string fileName in GetCandidateFileNames(name, ext))
            {
                string preferred = Path.Combine(nativeDir, fileName);
                if (File.Exists(preferred))
                {
                    return NativeLibrary.Load(preferred);
                }
            }

            if (Directory.Exists(nativeDir))
            {
                string? match = Directory.EnumerateFiles(nativeDir)
                    .FirstOrDefault(p => IsMatch(Path.GetFileName(p), name, ext));
                if (match != null)
                {
                    return NativeLibrary.Load(match);
                }
            }

            string ridNativeSegment = $"{Path.DirectorySeparatorChar}runtimes{Path.DirectorySeparatorChar}{rid}{Path.DirectorySeparatorChar}native{Path.DirectorySeparatorChar}";

            string? probe = Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories)
                .Where(p => p.Contains(ridNativeSegment, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(p => IsMatch(Path.GetFileName(p), name, ext));

            probe ??= Directory.EnumerateFiles(_cfg?.GlobalPackagesFolder ?? "", "*", SearchOption.AllDirectories)
                .Where(p => p.Contains(ridNativeSegment, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(p => IsMatch(Path.GetFileName(p), name, ext));

            return probe != null ? NativeLibrary.Load(probe) : IntPtr.Zero;
        });
        }
        catch { }
    }

    private static AssemblyName? SafeGetAssemblyName(string path)
    {
        try { return AssemblyName.GetAssemblyName(path); }
        catch { return null; }
    }

    private static bool IsSharedAssembly(string? name) //, string version, string rootPath)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // Framework y runtime: siempre compartidos
        if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Infraestructura común: compartidos
        if (name.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.StartsWith("Microsoft.NetCore.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.StartsWith("Microsoft.Net.Native.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("Microsoft.Win32.Registry", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("Microsoft.Win32.Primitives", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Caso especial: Bcl.AsyncInterfaces → en .NET 5+ ya está en System.Runtime
        if (name.Equals("Microsoft.Bcl.AsyncInterfaces", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("Microsoft.Bcl.AsyncInterfaces", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("Microsoft.Bcl.HashCode", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("Microsoft.Bcl.TimeProvider", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("System.Buffers", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("System.Memory", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("System.Numerics.Vectors", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("System.Runtime.CompilerServices.Unsafe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("System.Threading.Tasks.Extensions", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Casos grises: solo si ya están cargados en el host o ya descargados en disco
        if (name.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.Bcl.", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase))
        {
            // 1. ¿Ya está cargado en el host?
            if (AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            //// 2. ¿Ya está descargado en la carpeta de paquetes?
            //var packagePath = Path.Combine(rootPath, name, version);
            //if (Directory.Exists(packagePath))
            //    return true;
        }

        // Tus propias abstracciones compartidas
        return name.StartsWith("BRMS.Abstractions", StringComparison.OrdinalIgnoreCase);
    }


    private static string GetPreferredTargetFramework(PackageArchiveReader packageReader)
    {
        // Intentar netX primero; si no, netstandard
        IEnumerable<FrameworkSpecificGroup> frameworks = packageReader.GetReferenceItems();
        if (frameworks == null)
        {
            return GetHostTfmOrDefault();
        }

        string? bestNet = frameworks
            .Where(f => f.TargetFramework?.ToString().StartsWith("net") ?? false)
            .OrderByDescending(f => f.TargetFramework!.ToString())
            .Select(f => f.TargetFramework!.ToString())
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(bestNet))
        {
            return bestNet;
        }

        string? bestNetStd = frameworks
            .Where(f => f.TargetFramework?.ToString().StartsWith("netstandard") ?? false)
            .OrderByDescending(f => f.TargetFramework!.ToString())
            .Select(f => f.TargetFramework!.ToString())
            .FirstOrDefault();

        return bestNetStd ?? GetHostTfmOrDefault();
    }

    private static string GetHostTfmOrDefault()
    {
        var currentAssembly = Assembly.GetExecutingAssembly();
        TargetFrameworkAttribute? attr = currentAssembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
        if (attr != null)
        {
            var fx = NuGet.Frameworks.NuGetFramework.ParseFrameworkName(attr.FrameworkName, NuGet.Frameworks.DefaultFrameworkNameProvider.Instance);
            string shortName = fx.GetShortFolderName();
            if (!string.IsNullOrEmpty(shortName))
            {
                return shortName;
            }
        }
        return "netstandard2.0";
    }

    private static string GetCurrentRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64"
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => "linux-x64"
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => "osx-x64"
            };
        }
        return "win-x64";
    }

    private void TryRegisterResourceManager(Assembly assembly, string packageId, string filePath)
    {
        try
        {
            // Nombre base sin extensión
            string baseName = Path.GetFileNameWithoutExtension(filePath);

            // Caso específico: "Resources.Resources" → "Resources"
            baseName = baseName.Replace(".Resources.Resources", ".Resources", StringComparison.OrdinalIgnoreCase);

            // Quitar sufijo ".resources" si lo tiene al final
            if (baseName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName[..^".resources".Length];
            }

            // Quitar sufijo de cultura si lo hay (ej. ".en", ".es", ".fr")
            string[] parts = baseName.Split('.');
            if (parts.Length > 1)
            {
                string last = parts[^1];
                try
                {
                    var culture = new System.Globalization.CultureInfo(last);
                    // Si es cultura válida, recortamos
                    baseName = string.Join('.', parts.Take(parts.Length - 1));
                }
                catch (CultureNotFoundException)
                {
                    // no era cultura, lo dejamos tal cual
                }
            }

            // Crear ResourceManager con el baseName limpio
            var rm = new ResourceManager(baseName, assembly);

            // Validar que realmente hay recursos
            ResourceSet? testSet = rm.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, false);
            if (testSet != null)
            {
                ResourcesManager.AddResourceManager(rm);
                _logger.LogInformation($"[OK] Recursos registrados: {baseName} desde {packageId}");
            }
            else
            {
                _logger.LogWarning($"ResourceManager creado pero sin recursos accesibles: {baseName}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Error al registrar ResourceManager desde {filePath}: {ex.Message}");
        }
    }

    private void TryAddResourceManager(ResourceManager rm, string baseName, string packageId)
    {
        try
        {
            ResourceSet? testSet = rm.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, false);
            if (testSet != null)
            {
                ResourcesManager.AddResourceManager(rm);
                _logger.LogInformation($"{CHECK} Recursos registrados: {baseName} desde {packageId}");
            }
            else
            {
                _logger.LogWarning($"ResourceManager creado pero sin recursos accesibles: {baseName}");
            }
        }
        catch (Exception testEx)
        {
            _logger.LogWarning($"ResourceManager no puede acceder a recursos de {baseName}: {testEx.Message}");
        }
    }

    private static void AddFromBaseType(IServiceCollection services, Type type, Type? baseType = null, ServiceLifetime? defaultLifeTime = null)
    {
        ServiceLifetimeAttribute? lifetimeAttr = type.GetCustomAttribute<ServiceLifetimeAttribute>(true);
        ServiceLifetime? lifetime = lifetimeAttr?.Lifetime ?? defaultLifeTime;
        if (lifetime == null)
        {
            return;
        }

        IEnumerable<Type> interfaces = baseType != null
            ? type.GetInterfaces().Where(i => baseType.IsAssignableFrom(i) && i != baseType) : [];

        bool hasInterfaces = false;
        foreach (Type? interfaz in interfaces)
        {
            services.TryAdd(new ServiceDescriptor(interfaz, type, lifetime));
            hasInterfaces = true;
        }

        if (!hasInterfaces)
        {
            services.TryAdd(new ServiceDescriptor(type, type, lifetime.Value));
        }
    }
}
