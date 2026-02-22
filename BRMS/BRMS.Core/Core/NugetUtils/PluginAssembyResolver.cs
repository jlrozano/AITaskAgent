using System.Reflection;
using System.Runtime.Loader;

namespace BRMS.Core.Core.NugetUtils;

/// <summary>
/// Resolvedor de ensamblajes para cargar dependencias transitivas desde el directorio de paquetes NuGet
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _packagePath;
    private readonly string _tfm;
    private readonly string _rid;

    public PluginLoadContext(string packagePath, string tfm, string rid, bool isCollectible = true)
        : base($"PluginLoadContext:{Path.GetFileName(packagePath)}", isCollectible)
    {
        _packagePath = packagePath;
        _tfm = tfm;
        _rid = rid;

        // Resolver adicional para administrados y satélites
        Resolving += OnResolving;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Probing básico para administrados
        string[] candidates =
        [
        Path.Combine(_packagePath, "lib", _tfm, $"{assemblyName.Name}.dll"),
        Path.Combine(_packagePath, "runtimes", _rid, "lib", _tfm, $"{assemblyName.Name}.dll"),
        Path.Combine(_packagePath, "lib", $"{assemblyName.Name}.dll"),
    ];

        foreach (string? path in candidates)
        {
            if (File.Exists(path))
            {
                return LoadFromAssemblyPath(path);
            }
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string ext = OperatingSystem.IsWindows()
            ? ".dll"
            : OperatingSystem.IsMacOS()
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

        string nativeDir = Path.Combine(_packagePath, "runtimes", _rid, "native");
        if (Directory.Exists(nativeDir))
        {
            string? candidate = Directory.EnumerateFiles(nativeDir)
                .FirstOrDefault(p => IsMatch(Path.GetFileName(p), unmanagedDllName, ext));

            if (candidate != null)
            {
                return LoadUnmanagedDllFromPath(candidate);
            }
        }

        return base.LoadUnmanagedDll(unmanagedDllName);
    }

    private Assembly? OnResolving(AssemblyLoadContext ctx, AssemblyName name)
    {
        // Resolver satélites de recursos
        if (name.Name!.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = name.Name[..^".resources".Length];
            string? culture = name.CultureName ?? Thread.CurrentThread.CurrentUICulture?.Name;

            if (!string.IsNullOrEmpty(culture))
            {
                string satellitePath = Path.Combine(_packagePath, "lib", _tfm, culture, $"{baseName}.resources.dll");
                if (File.Exists(satellitePath))
                {
                    return LoadFromAssemblyPath(satellitePath);
                }
            }
        }

        // Fallback: búsqueda recursiva
        string? match = Directory.EnumerateFiles(_packagePath, name.Name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
        return match != null ? LoadFromAssemblyPath(match) : null;
    }
}



//    internal class PluginAssemblyResolver(string packageDirectory, string globalPackagesFolder, global::NuGet.Common.ILogger logger) : IDisposable
//    {
//        private readonly string _packageDirectory = packageDirectory;
//        private readonly string _globalPackagesFolder = globalPackagesFolder;
//        private readonly global::NuGet.Common.ILogger _nuGetLogger = logger;
//        private bool _registered = false;
//        private readonly HashSet<string> _configuredNativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

//        // Constantes para logging
//        private const string CHECK = "[OK]";
//        private const string CROSS = "[ERROR]";
//        private const string ARROW = "-->";

//        public void Register()
//        {
//            if (!_registered)
//            {
//                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

//                // Configurar rutas nativas al PATH
//                ConfigureNativeDllPaths();

//                _registered = true;
//            }
//        }

//        public void Unregister()
//        {
//            if (_registered)
//            {
//                AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
//                _registered = false;
//            }
//        }

//        /// <summary>
//        /// Actualiza las rutas nativas en el PATH (llamar después de descargar nuevos paquetes)
//        /// </summary>
//        public void RefreshNativePaths()
//        {
//            ConfigureNativeDllPaths();
//        }

//        /// <summary>
//        /// Agrega directorios 'native' del RID correcto al PATH del proceso
//        /// </summary>
//        private void ConfigureNativeDllPaths()
//        {
//            try
//            {
//                if (!Directory.Exists(_globalPackagesFolder))
//                {
//                    _nuGetLogger.LogDebug($"Carpeta de paquetes no existe: {_globalPackagesFolder}");
//                    return;
//                }

//                var rid = GetCurrentRuntimeIdentifier();
//                _nuGetLogger.LogDebug($"Buscando DLLs nativas para RID: {rid}");

//                // Buscar solo directorios native del RID correcto
//                var nativeDirs = Directory.GetDirectories(_globalPackagesFolder, "native", SearchOption.AllDirectories)
//                    .Where(dir => dir.Replace('/', '\\').Contains($"\\runtimes\\{rid}\\", StringComparison.OrdinalIgnoreCase))
//                    .ToArray();

//                _nuGetLogger.LogDebug($"Encontrados {nativeDirs.Length} directorios 'native' para {rid}");

//                if (nativeDirs.Length == 0)
//                {
//                    return;
//                }

//                // Filtrar rutas ya configuradas
//                var newPaths = nativeDirs.Where(p => !_configuredNativePaths.Contains(p)).ToList();

//                if (newPaths.Count == 0)
//                {
//                    _nuGetLogger.LogDebug("Todas las rutas nativas ya están configuradas");
//                    return;
//                }

//                // Agregar al PATH del proceso
//                string currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? "";
//                string newPath = string.Join(Path.PathSeparator, newPaths) + Path.PathSeparator + currentPath;
//                Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.Process);

//                // Trackear rutas agregadas
//                foreach (var path in newPaths)
//                {
//                    _configuredNativePaths.Add(path);
//                }

//                _nuGetLogger.LogInformation($"{CHECK} Agregadas {newPaths.Count} rutas nativas al PATH para {rid}");
//                foreach (var path in newPaths)
//                {
//                    _nuGetLogger.LogDebug($"  {ARROW} {path}");
//                }
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogWarning($"Error configurando rutas nativas: {ex.Message}");
//            }
//        }

//        private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
//        {
//            try
//            {
//                var assemblyName = new AssemblyName(args.Name);

//                if (ShouldSkipAssembly(assemblyName))
//                {
//                    return null;
//                }

//                _nuGetLogger.LogDebug($"Resolviendo ensamblaje: {assemblyName.Name} (v{assemblyName.Version})");

//                if (string.IsNullOrEmpty(assemblyName.Name))
//                {
//                    _nuGetLogger.LogWarning("AssemblyName.Name es nulo o vacio; no se puede resolver.");
//                    return null;
//                }

//                // Verificar si es una DLL satélite de recursos
//                if (assemblyName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
//                {
//                    var satelliteAssembly = ResolveSatelliteAssembly(assemblyName);
//                    if (satelliteAssembly != null)
//                    {
//                        return satelliteAssembly;
//                    }

//                    _nuGetLogger.LogDebug($"DLL satelite no encontrada (normal): {assemblyName.Name}");
//                    return null;
//                }

//                var assemblyFile = FindAssemblyInGlobalCache(assemblyName.Name, assemblyName.Version);
//                if (assemblyFile != null)
//                {
//                    _nuGetLogger.LogInformation($"{CHECK} Ensamblaje resuelto desde cache global: {assemblyFile}");
//                    return Assembly.LoadFrom(assemblyFile);
//                }

//                assemblyFile = FindAssemblyFile(_packageDirectory, assemblyName.Name);
//                if (assemblyFile != null)
//                {
//                    _nuGetLogger.LogInformation($"{CHECK} Ensamblaje encontrado en directorio especifico: {assemblyFile}");
//                    return Assembly.LoadFrom(assemblyFile);
//                }

//                _nuGetLogger.LogWarning($"{CROSS} No se pudo resolver el ensamblaje: {assemblyName.Name}");
//                return null;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogError($"Error al resolver ensamblaje {args.Name}: {ex.Message}");
//                return null;
//            }
//        }

//        private bool ShouldSkipAssembly(AssemblyName assemblyName)
//        {
//            if (string.IsNullOrEmpty(assemblyName.Name))
//                return true;

//            // Assemblies exactos del sistema que nunca debemos resolver
//            var skipExact = new[]
//            {
//                "mscorlib",
//                "netstandard",
//                "System.Private.CoreLib"
//            };

//            if (skipExact.Any(name => assemblyName.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
//                return true;

//            // Prefijos de assemblies del sistema (más específicos)
//            var skipPrefixes = new[]
//            {
//                "System.Runtime.",
//                "System.Collections.",
//                "System.Linq.",
//                "System.Threading.",
//                "System.IO.",
//                "System.Reflection.",
//                "Microsoft.Win32."

//            };

//            if (skipPrefixes.Any(prefix => assemblyName.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
//                return true;

//            // Recursos de assemblies del sistema
//            var systemResourceAssemblies = new[]
//            {
//                "NuGet.Protocol.resources",
//                "NuGet.Common.resources",
//                "NuGet.Configuration.resources",
//                "System.Private.CoreLib.resources"
//            };

//            return systemResourceAssemblies.Any(name => assemblyName.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
//        }

//        private string? FindAssemblyInGlobalCache(string assemblyName, Version? version)
//        {
//            try
//            {
//                _nuGetLogger.LogDebug($"Buscando {assemblyName} en cache global: {_globalPackagesFolder}");

//                var exactMatches = FindAssemblyByPackageName(assemblyName, version);
//                if (exactMatches.Count != 0)
//                {
//                    return SelectBestAssembly(exactMatches, assemblyName);
//                }

//                var allMatches = FindAssemblyInAllPackages(assemblyName);
//                if (allMatches.Count != 0)
//                {
//                    return SelectBestAssembly(allMatches, assemblyName);
//                }

//                return null;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error en busqueda global para {assemblyName}: {ex.Message}");
//                return null;
//            }
//        }

//        private List<string> FindAssemblyByPackageName(string assemblyName, Version? version)
//        {
//            var results = new List<string>();

//            try
//            {
//                var packageDirs = Directory.GetDirectories(_globalPackagesFolder, assemblyName.ToLowerInvariant(), SearchOption.TopDirectoryOnly);

//                foreach (var packageDir in packageDirs)
//                {
//                    var versionDirs = Directory.GetDirectories(packageDir);

//                    if (version != null)
//                    {
//                        var versionDir = versionDirs.FirstOrDefault(d =>
//                            Path.GetFileName(d).StartsWith(version.ToString()));
//                        if (versionDir != null)
//                        {
//                            results.AddRange(FindDllsInPackageDirectory(versionDir, assemblyName));
//                        }
//                    }
//                    else
//                    {
//                        var latestVersionDir = versionDirs
//                            .OrderByDescending(d => Path.GetFileName(d))
//                            .FirstOrDefault();
//                        if (latestVersionDir != null)
//                        {
//                            results.AddRange(FindDllsInPackageDirectory(latestVersionDir, assemblyName));
//                        }
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error buscando paquete {assemblyName}: {ex.Message}");
//            }

//            return results;
//        }

//        private List<string> FindAssemblyInAllPackages(string assemblyName)
//        {
//            var results = new List<string>();

//            try
//            {
//                var allDlls = Directory.EnumerateFiles(_globalPackagesFolder, $"{assemblyName}.dll", SearchOption.AllDirectories);
//                results.AddRange(allDlls);
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error en busqueda recursiva para {assemblyName}: {ex.Message}");
//            }

//            return results;
//        }

//        private List<string> FindDllsInPackageDirectory(string packageDir, string assemblyName)
//        {
//            var results = new List<string>();

//            try
//            {
//                var tfm = GetCurrentTargetFramework();

//                var searchPaths = new[]
//                {
//                    Path.Combine(packageDir, "lib", tfm),
//                    Path.Combine(packageDir, "runtimes", "win-x64", "lib", tfm),
//                    Path.Combine(packageDir, "runtimes", "win", "lib", tfm),
//                    Path.Combine(packageDir, "lib", "netstandard2.0"),
//                    Path.Combine(packageDir, "lib", "netstandard2.1"),
//                    Path.Combine(packageDir, "lib")
//                };

//                foreach (var searchPath in searchPaths)
//                {
//                    if (Directory.Exists(searchPath))
//                    {
//                        var dlls = Directory.GetFiles(searchPath, $"{assemblyName}.dll", SearchOption.AllDirectories);
//                        results.AddRange(dlls);
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error buscando DLLs en {packageDir}: {ex.Message}");
//            }

//            return results;
//        }

//        private string? SelectBestAssembly(List<string> candidates, string assemblyName)
//        {
//            if (candidates.Count == 0) return null;
//            if (candidates.Count == 1) return candidates.First();

//            var tfm = GetCurrentTargetFramework();

//            var ordered = candidates
//                .OrderByDescending(f => f.Contains($"\\lib\\{tfm}\\"))
//                .ThenByDescending(f => f.Contains("\\runtimes\\") && f.Contains($"\\lib\\{tfm}\\"))
//                .ThenByDescending(f => f.Contains("\\lib\\netstandard2."))
//                .ThenBy(f => f.Length);

//            var selected = ordered.First();
//            _nuGetLogger.LogDebug($"Seleccionado {selected} de {candidates.Count} candidatos para {assemblyName}");

//            return selected;
//        }

//        private string? FindAssemblyFile(string directory, string assemblyName)
//        {
//            try
//            {
//                var tfm = GetCurrentTargetFramework();
//                var allDlls = Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
//                    .Where(f => string.Equals(Path.GetFileName(f), $"{assemblyName}.dll", StringComparison.OrdinalIgnoreCase))
//                    .ToList();

//                if (allDlls.Count == 0)
//                {
//                    return null;
//                }

//                static string Normalize(string path) => path.Replace('/', '\\').ToLowerInvariant();

//                var ordered = allDlls
//                    .OrderByDescending(f => Normalize(f).Contains($"\\lib\\{tfm}\\"))
//                    .ThenByDescending(f => Normalize(f).Contains("\\runtimes\\") && Normalize(f).Contains($"\\lib\\{tfm}\\"))
//                    .ThenBy(f => f.Length);

//                return ordered.FirstOrDefault() ?? allDlls.First();
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error buscando ensamblaje {assemblyName} en {directory}: {ex.Message}");
//            }

//            return null;
//        }

//        private Assembly? ResolveSatelliteAssembly(AssemblyName assemblyName)
//        {
//            try
//            {
//                var baseName = assemblyName.Name.Replace(".resources", "", StringComparison.OrdinalIgnoreCase);
//                _nuGetLogger.LogDebug($"Resolviendo DLL satelite para: {baseName}");

//                var culture = Thread.CurrentThread.CurrentUICulture;
//                var cultureName = culture.Name;

//                if (string.IsNullOrEmpty(cultureName))
//                {
//                    _nuGetLogger.LogDebug("No hay cultura especifica, usando cultura neutral");
//                    return null;
//                }

//                var satelliteFile = FindSatelliteAssemblyInGlobalCache(baseName, cultureName);
//                if (satelliteFile != null)
//                {
//                    _nuGetLogger.LogInformation($"{CHECK} DLL satelite encontrada: {satelliteFile}");
//                    return Assembly.LoadFrom(satelliteFile);
//                }

//                satelliteFile = FindSatelliteAssemblyFile(_packageDirectory, baseName, cultureName);
//                if (satelliteFile != null)
//                {
//                    _nuGetLogger.LogInformation($"{CHECK} DLL satelite encontrada en directorio especifico: {satelliteFile}");
//                    return Assembly.LoadFrom(satelliteFile);
//                }

//                _nuGetLogger.LogDebug($"No se encontro DLL satelite para {baseName} en cultura {cultureName}");
//                return null;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogError($"Error resolviendo DLL satelite {assemblyName.Name}: {ex.Message}");
//                return null;
//            }
//        }

//        private string? FindSatelliteAssemblyInGlobalCache(string baseName, string cultureName)
//        {
//            try
//            {
//                var tfm = GetCurrentTargetFramework();

//                var packageDirs = Directory.GetDirectories(_globalPackagesFolder, "*", SearchOption.TopDirectoryOnly)
//                    .Where(dir => Directory.EnumerateFiles(dir, $"{baseName}.dll", SearchOption.AllDirectories).Any());

//                foreach (var packageDir in packageDirs)
//                {
//                    var versionDirs = Directory.GetDirectories(packageDir);
//                    foreach (var versionDir in versionDirs)
//                    {
//                        var culturePath = Path.Combine(versionDir, "lib", tfm, cultureName, $"{baseName}.resources.dll");
//                        if (File.Exists(culturePath))
//                        {
//                            return culturePath;
//                        }

//                        var runtimePaths = new[]
//                        {
//                            Path.Combine(versionDir, "runtimes", "win-x64", "lib", tfm, cultureName, $"{baseName}.resources.dll"),
//                            Path.Combine(versionDir, "runtimes", "win", "lib", tfm, cultureName, $"{baseName}.resources.dll")
//                        };

//                        foreach (var runtimePath in runtimePaths)
//                        {
//                            if (File.Exists(runtimePath))
//                            {
//                                return runtimePath;
//                            }
//                        }
//                    }
//                }

//                return null;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error buscando DLL satelite en cache global: {ex.Message}");
//                return null;
//            }
//        }

//        private string? FindSatelliteAssemblyFile(string directory, string baseName, string cultureName)
//        {
//            try
//            {
//                var tfm = GetCurrentTargetFramework();

//                var searchPatterns = new[]
//                {
//                    Path.Combine(directory, "lib", tfm, cultureName, $"{baseName}.resources.dll"),
//                    Path.Combine(directory, cultureName, $"{baseName}.resources.dll"),
//                    Path.Combine(directory, "lib", cultureName, $"{baseName}.resources.dll")
//                };

//                foreach (var pattern in searchPatterns)
//                {
//                    if (File.Exists(pattern))
//                    {
//                        return pattern;
//                    }
//                }

//                var recursiveSearch = Directory.EnumerateFiles(directory, $"{baseName}.resources.dll", SearchOption.AllDirectories)
//                    .Where(f => f.Contains($"\\{cultureName}\\") || f.Contains($"/{cultureName}/"))
//                    .FirstOrDefault();

//                return recursiveSearch;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error buscando DLL satelite en directorio especifico: {ex.Message}");
//                return null;
//            }
//        }

//        private static string GetCurrentRuntimeIdentifier()
//        {
//            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
//            {
//                return RuntimeInformation.ProcessArchitecture switch
//                {
//                    Architecture.X64 => "win-x64",
//                    Architecture.X86 => "win-x86",
//                    Architecture.Arm64 => "win-arm64",
//                    _ => "win-x64"
//                };
//            }
//            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
//            {
//                return RuntimeInformation.ProcessArchitecture switch
//                {
//                    Architecture.X64 => "linux-x64",
//                    Architecture.Arm64 => "linux-arm64",
//                    _ => "linux-x64"
//                };
//            }
//            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
//            {
//                return RuntimeInformation.ProcessArchitecture switch
//                {
//                    Architecture.X64 => "osx-x64",
//                    Architecture.Arm64 => "osx-arm64",
//                    _ => "osx-x64"
//                };
//            }

//            return "win-x64";
//        }

//        private static string GetCurrentTargetFramework()
//        {
//            var currentAssembly = Assembly.GetExecutingAssembly();

//            if (currentAssembly
//                .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
//                .FirstOrDefault() is System.Runtime.Versioning.TargetFrameworkAttribute targetFrameworkAttribute)
//            {
//                var fx = NuGetFramework.ParseFrameworkName(targetFrameworkAttribute.FrameworkName, DefaultFrameworkNameProvider.Instance);
//                var shortName = fx.GetShortFolderName();
//                if (!string.IsNullOrEmpty(shortName))
//                {
//                    return shortName;
//                }
//            }

//            return "net9.0";
//        }

//        public void Dispose()
//        {
//            Unregister();
//        }
//    }
//}

//using NuGet.Frameworks;
//using System.Reflection;
//using BRMS.Core.Core.NuGet;

//namespace BRMS.Core.Core.nuget
//{
//    /// <summary>
//    /// Resolvedor de ensamblajes para cargar dependencias transitivas desde el directorio de paquetes NuGet
//    /// </summary>
//    internal class PluginAssemblyResolver(string packageDirectory, string globalPackagesFolder, global::NuGet.Common.ILogger logger) : IDisposable
//    {
//        private readonly string _packageDirectory = packageDirectory;
//        private readonly string _globalPackagesFolder = globalPackagesFolder;
//        private readonly global::NuGet.Common.ILogger _nuGetLogger = logger;
//        private bool _registered = false;

//        public void Register()
//        {
//            if (!_registered)
//            {
//                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
//                ConfigureNativeDllPaths();
//                _registered = true;

//            }
//        }

//        public void Unregister()
//        {
//            if (_registered)
//            {
//                AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
//                _registered = false;

//            }
//        }
//        private void ConfigureNativeDllPaths()
//        {
//            try
//            {
//                var nativePaths = new List<string>();

//                if (Directory.Exists(_globalPackagesFolder))
//                {
//                    var nativeDirs = Directory.GetDirectories(_globalPackagesFolder, "native", SearchOption.AllDirectories);
//                    nativePaths.AddRange(nativeDirs);

//                    _nuGetLogger.LogDebug($"Encontrados {nativeDirs.Length} directorios 'native' en caché global");
//                }

//                if (nativePaths.Count == 0)
//                {
//                    _nuGetLogger.LogDebug("No se encontraron directorios nativos para agregar al PATH");
//                    return;
//                }

//                string currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? "";
//                var existingPaths = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToHashSet();

//                var pathsToAdd = nativePaths.Where(p => !existingPaths.Contains(p)).ToList();

//                if (pathsToAdd.Count > 0)
//                {
//                    string newPath = string.Join(Path.PathSeparator, pathsToAdd) + Path.PathSeparator + currentPath;
//                    Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.Process);

//                    _nuGetLogger.LogInformation($"✓ Agregadas {pathsToAdd.Count} rutas nativas al PATH:");
//                    foreach (var path in pathsToAdd)
//                    {
//                        _nuGetLogger.LogDebug($"  - {path}");
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogWarning($"Error configurando rutas nativas: {ex.Message}");
//            }
//        }
//        private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
//        {
//            try
//            {
//                var assemblyName = new AssemblyName(args.Name);

//                // Filtrar ensamblajes que no deberíamos resolver para evitar interferencia
//                if (ShouldSkipAssembly(assemblyName))
//                {
//                    return null; // Dejar que el runtime maneje estos ensamblajes
//                }

//                _nuGetLogger.LogDebug($"Resolviendo ensamblaje: {assemblyName.Name} (v{assemblyName.Version})");

//                // Validar nombre de ensamblado
//                if (string.IsNullOrEmpty(assemblyName.Name))
//                {
//                    _nuGetLogger.LogWarning("AssemblyName.Name es nulo o vacío; no se puede resolver.");
//                    return null;
//                }

//                // Verificar si es una DLL satélite de recursos
//                if (assemblyName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
//                {
//                    var satelliteAssembly = ResolveSatelliteAssembly(assemblyName);
//                    if (satelliteAssembly != null)
//                    {
//                        return satelliteAssembly;
//                    }

//                    // Para ensamblajes de recursos, NO generar warning - es normal que no existan
//                    _nuGetLogger.LogDebug($"DLL satélite no encontrada (normal): {assemblyName.Name}");
//                    return null;
//                }

//                var assemblyFile = FindAssemblyInGlobalCache(assemblyName.Name, assemblyName.Version);
//                if (assemblyFile != null)
//                {
//                    _nuGetLogger.LogInformation($"✓ Ensamblaje resuelto desde caché global: {assemblyFile}");
//                    return Assembly.LoadFrom(assemblyFile);
//                }

//                // Fallback: búsqueda en directorio específico (compatibilidad)
//                assemblyFile = FindAssemblyFile(_packageDirectory, assemblyName.Name);
//                if (assemblyFile != null)
//                {
//                    _nuGetLogger.LogInformation($"✓ Ensamblaje encontrado en directorio específico: {assemblyFile}");
//                    return Assembly.LoadFrom(assemblyFile);
//                }

//                // Solo generar warning para ensamblajes que realmente deberían existir
//                _nuGetLogger.LogWarning($"❌ No se pudo resolver el ensamblaje: {assemblyName.Name}");
//                return null;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogError($"Error al resolver ensamblaje {args.Name}: {ex.Message}");
//                return null;
//            }
//        }

//        private bool ShouldSkipAssembly(AssemblyName assemblyName)
//        {
//            if (string.IsNullOrEmpty(assemblyName.Name))
//                return true;

//            // Lista de prefijos de ensamblajes que no deberíamos resolver
//            var skipPrefixes = new[]
//            {
//                "NuGet.",
//                "System.",
//                "Microsoft.Extensions.",
//                "Microsoft.AspNetCore."
//            };

//            // Lista de ensamblajes de recursos del sistema que no deberíamos resolver
//            var systemResourceAssemblies = new[]
//            {
//                "NuGet.Protocol.resources",
//                "NuGet.Common.resources",
//                "NuGet.Configuration.resources",
//                "System.Private.CoreLib.resources"
//            };

//            return skipPrefixes.Any(prefix => assemblyName.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
//                   systemResourceAssemblies.Any(name => assemblyName.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
//        }

//        /// <summary>
//        /// Busca un ensamblaje en toda la caché global de NuGet de forma inteligente
//        /// </summary>
//        private string? FindAssemblyInGlobalCache(string assemblyName, Version? version)
//        {
//            try
//            {
//                _nuGetLogger.LogDebug($"Buscando {assemblyName} en caché global: {_globalPackagesFolder}");

//                // 1. Buscar por nombre exacto del paquete
//                var exactMatches = FindAssemblyByPackageName(assemblyName, version);
//                if (exactMatches.Count != 0)
//                {
//                    return SelectBestAssembly(exactMatches, assemblyName);
//                }

//                // 2. Buscar en todos los paquetes (para dependencias transitivas)
//                var allMatches = FindAssemblyInAllPackages(assemblyName);
//                if (allMatches.Count != 0)
//                {
//                    return SelectBestAssembly(allMatches, assemblyName);
//                }

//                return null;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error en búsqueda global para {assemblyName}: {ex.Message}");
//                return null;
//            }
//        }

//        /// <summary>
//        /// Busca un ensamblaje por nombre de paquete exacto
//        /// </summary>
//        private List<string> FindAssemblyByPackageName(string assemblyName, Version? version)
//        {
//            var results = new List<string>();

//            try
//            {
//                // Buscar directorio del paquete (formato: packagename/version/)
//                var packageDirs = Directory.GetDirectories(_globalPackagesFolder, assemblyName.ToLowerInvariant(), SearchOption.TopDirectoryOnly);

//                foreach (var packageDir in packageDirs)
//                {
//                    var versionDirs = Directory.GetDirectories(packageDir);

//                    // Si se especifica versión, buscar esa versión específica
//                    if (version != null)
//                    {
//                        var versionDir = versionDirs.FirstOrDefault(d =>
//                            Path.GetFileName(d).StartsWith(version.ToString()));
//                        if (versionDir != null)
//                        {
//                            results.AddRange(FindDllsInPackageDirectory(versionDir, assemblyName));
//                        }
//                    }
//                    else
//                    {
//                        // Sin versión específica, buscar en la versión más reciente
//                        var latestVersionDir = versionDirs
//                            .OrderByDescending(d => Path.GetFileName(d))
//                            .FirstOrDefault();
//                        if (latestVersionDir != null)
//                        {
//                            results.AddRange(FindDllsInPackageDirectory(latestVersionDir, assemblyName));
//                        }
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error buscando paquete {assemblyName}: {ex.Message}");
//            }

//            return results;
//        }

//        /// <summary>
//        /// Busca un ensamblaje en TODOS los paquetes de la caché global
//        /// </summary>
//        private List<string> FindAssemblyInAllPackages(string assemblyName)
//        {
//            var results = new List<string>();

//            try
//            {
//                // Buscar recursivamente en todos los directorios de paquetes
//                var allDlls = Directory.EnumerateFiles(_globalPackagesFolder, $"{assemblyName}.dll", SearchOption.AllDirectories);
//                results.AddRange(allDlls);
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error en búsqueda recursiva para {assemblyName}: {ex.Message}");
//            }

//            return results;
//        }

//        /// <summary>
//        /// Encuentra DLLs en un directorio de paquete específico
//        /// </summary>
//        private List<string> FindDllsInPackageDirectory(string packageDir, string assemblyName)
//        {
//            var results = new List<string>();

//            try
//            {
//                var tfm = GetCurrentTargetFramework();

//                // Buscar en orden de prioridad:
//                // 1. lib/{tfm}/
//                // 2. runtimes/{rid}/lib/{tfm}/
//                // 3. lib/netstandard2.0/ (fallback común)
//                // 4. Cualquier lib/

//                var searchPaths = new[]
//                {
//                    Path.Combine(packageDir, "lib", tfm),
//                    Path.Combine(packageDir, "runtimes", "win-x64", "lib", tfm),
//                    Path.Combine(packageDir, "runtimes", "win", "lib", tfm),
//                    Path.Combine(packageDir, "lib", "netstandard2.0"),
//                    Path.Combine(packageDir, "lib", "netstandard2.1"),
//                    Path.Combine(packageDir, "lib")
//                };

//                foreach (var searchPath in searchPaths)
//                {
//                    if (Directory.Exists(searchPath))
//                    {
//                        var dlls = Directory.GetFiles(searchPath, $"{assemblyName}.dll", SearchOption.AllDirectories);
//                        results.AddRange(dlls);
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error buscando DLLs en {packageDir}: {ex.Message}");
//            }

//            return results;
//        }

//        /// <summary>
//        /// Selecciona el mejor ensamblaje de una lista de candidatos
//        /// </summary>
//        private string? SelectBestAssembly(List<string> candidates, string assemblyName)
//        {
//            if (candidates.Count == 0) return null;
//            if (candidates.Count == 1) return candidates.First();

//            var tfm = GetCurrentTargetFramework();

//            // Ordenar por prioridad:
//            // 1. Que contenga el TFM actual
//            // 2. Que esté en runtimes específicos
//            // 3. Ruta más corta (menos anidado)
//            var ordered = candidates
//                .OrderByDescending(f => f.Contains($"\\lib\\{tfm}\\"))
//                .ThenByDescending(f => f.Contains("\\runtimes\\") && f.Contains($"\\lib\\{tfm}\\"))
//                .ThenByDescending(f => f.Contains("\\lib\\netstandard2."))
//                .ThenBy(f => f.Length);

//            var selected = ordered.First();
//            _nuGetLogger.LogDebug($"Seleccionado {selected} de {candidates.Count} candidatos para {assemblyName}");

//            return selected;
//        }

//        private string? FindAssemblyFile(string directory, string assemblyName)
//        {
//            try
//            {
//                var tfm = GetCurrentTargetFramework();
//                var allDlls = Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
//                    .Where(f => string.Equals(Path.GetFileName(f), $"{assemblyName}.dll", StringComparison.OrdinalIgnoreCase))
//                    .ToList();

//                if (allDlls.Count == 0)
//                {
//                    return null;
//                }

//                static string Normalize(string path) => path.Replace('/', '\\').ToLowerInvariant();

//                var ordered = allDlls
//                    .OrderByDescending(f => Normalize(f).Contains($"\\lib\\{tfm}\\"))
//                    .ThenByDescending(f => Normalize(f).Contains("\\runtimes\\") && Normalize(f).Contains($"\\lib\\{tfm}\\"))
//                    .ThenBy(f => f.Length);

//                return ordered.FirstOrDefault() ?? allDlls.First();
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error buscando ensamblaje {assemblyName} en {directory}: {ex.Message}");
//            }

//            return null;
//        }

//        /// <summary>
//        /// Verifica si un assembly con el nombre especificado ya está cargado en el AppDomain actual
//        /// </summary>
//        private bool IsAssemblyAlreadyLoaded(string packageId)
//        {
//            try
//            {
//                // Obtener todos los assemblies cargados en el AppDomain actual
//                var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();

//                // Buscar por nombre exacto del paquete
//                var exactMatch = loadedAssemblies.Any(assembly =>
//                    string.Equals(assembly.GetName().Name, packageId, StringComparison.OrdinalIgnoreCase));

//                if (exactMatch)
//                {
//                    var matchedAssembly = loadedAssemblies.First(assembly =>
//                        string.Equals(assembly.GetName().Name, packageId, StringComparison.OrdinalIgnoreCase));

//                    _nuGetLogger.LogDebug($"Assembly ya cargado: {packageId} v{matchedAssembly.GetName().Version} desde {matchedAssembly.Location}");
//                    return true;
//                }

//                // Buscar por nombres comunes de assemblies (algunos paquetes NuGet tienen nombres diferentes al assembly)
//                var commonMappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
//                {
//                    { "Microsoft.Extensions.Logging", new[] { "Microsoft.Extensions.Logging", "Microsoft.Extensions.Logging.Abstractions" } },
//                    { "Microsoft.Extensions.Logging.Abstractions", new[] { "Microsoft.Extensions.Logging.Abstractions" } },
//                    { "Microsoft.Extensions.DependencyInjection", new[] { "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions" } },
//                    { "Microsoft.Extensions.Configuration", new[] { "Microsoft.Extensions.Configuration", "Microsoft.Extensions.Configuration.Abstractions" } },
//                    { "Newtonsoft.Json", new[] { "Newtonsoft.Json" } },
//                    { "System.Text.Json", new[] { "System.Text.Json" } },
//                    { "Microsoft.Extensions.Hosting", new[] { "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Hosting.Abstractions" } },
//                    { "System.ComponentModel.Annotations", new[] { "System.ComponentModel.Annotations" } },
//                    { "System.Memory", new[] { "System.Memory" } },
//                    { "System.Buffers", new[] { "System.Buffers" } }
//                };

//                if (commonMappings.TryGetValue(packageId, out var assemblyNames))
//                {
//                    foreach (var assemblyName in assemblyNames)
//                    {
//                        var found = loadedAssemblies.Any(assembly =>
//                            string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));

//                        if (found)
//                        {
//                            var matchedAssembly = loadedAssemblies.First(assembly =>
//                                string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));

//                            _nuGetLogger.LogInformation($"✓ Dependencia omitida (ya cargada): {packageId} → {assemblyName} v{matchedAssembly.GetName().Version}");
//                            return true;
//                        }
//                    }
//                }

//                return false;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogWarning($"Error verificando assembly cargado para {packageId}: {ex.Message}");
//                return false; // En caso de error, proceder con la descarga por seguridad
//            }
//        }

//        /// <summary>
//        /// Resuelve DLLs satélite de recursos buscando en carpetas de idiomas
//        /// </summary>
//        private Assembly? ResolveSatelliteAssembly(AssemblyName assemblyName)
//        {
//            try
//            {
//                // Extraer el nombre base del assembly (sin .resources)
//                var baseName = assemblyName.Name.Replace(".resources", "", StringComparison.OrdinalIgnoreCase);
//                _nuGetLogger.LogDebug($"Resolviendo DLL satélite para: {baseName}");

//                // Obtener la cultura del contexto actual
//                var culture = Thread.CurrentThread.CurrentUICulture;
//                var cultureName = culture.Name;

//                if (string.IsNullOrEmpty(cultureName))
//                {
//                    _nuGetLogger.LogDebug("No hay cultura específica, usando cultura neutral");
//                    return null;
//                }

//                // Buscar la DLL satélite en la caché global
//                var satelliteFile = FindSatelliteAssemblyInGlobalCache(baseName, cultureName);
//                if (satelliteFile != null)
//                {
//                    _nuGetLogger.LogInformation($"✓ DLL satélite encontrada: {satelliteFile}");
//                    return Assembly.LoadFrom(satelliteFile);
//                }

//                // Fallback: buscar en directorio específico
//                satelliteFile = FindSatelliteAssemblyFile(_packageDirectory, baseName, cultureName);
//                if (satelliteFile != null)
//                {
//                    _nuGetLogger.LogInformation($"✓ DLL satélite encontrada en directorio específico: {satelliteFile}");
//                    return Assembly.LoadFrom(satelliteFile);
//                }

//                _nuGetLogger.LogDebug($"No se encontró DLL satélite para {baseName} en cultura {cultureName}");
//                return null;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogError($"Error resolviendo DLL satélite {assemblyName.Name}: {ex.Message}");
//                return null;
//            }
//        }

//        /// <summary>
//        /// Busca una DLL satélite en la caché global de NuGet
//        /// </summary>
//        private string? FindSatelliteAssemblyInGlobalCache(string baseName, string cultureName)
//        {
//            try
//            {
//                var tfm = GetCurrentTargetFramework();

//                // Buscar en todos los paquetes que podrían contener el assembly base
//                var packageDirs = Directory.GetDirectories(_globalPackagesFolder, "*", SearchOption.TopDirectoryOnly)
//                    .Where(dir => Directory.EnumerateFiles(dir, $"{baseName}.dll", SearchOption.AllDirectories).Any());

//                foreach (var packageDir in packageDirs)
//                {
//                    var versionDirs = Directory.GetDirectories(packageDir);
//                    foreach (var versionDir in versionDirs)
//                    {
//                        // Buscar en lib/{tfm}/{culture}/{baseName}.resources.dll
//                        var culturePath = Path.Combine(versionDir, "lib", tfm, cultureName, $"{baseName}.resources.dll");
//                        if (File.Exists(culturePath))
//                        {
//                            return culturePath;
//                        }

//                        // Buscar también en runtimes
//                        var runtimePaths = new[]
//                        {
//                            Path.Combine(versionDir, "runtimes", "win-x64", "lib", tfm, cultureName, $"{baseName}.resources.dll"),
//                            Path.Combine(versionDir, "runtimes", "win", "lib", tfm, cultureName, $"{baseName}.resources.dll")
//                        };

//                        foreach (var runtimePath in runtimePaths)
//                        {
//                            if (File.Exists(runtimePath))
//                            {
//                                return runtimePath;
//                            }
//                        }
//                    }
//                }

//                return null;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error buscando DLL satélite en caché global: {ex.Message}");
//                return null;
//            }
//        }

//        /// <summary>
//        /// Busca una DLL satélite en un directorio específico
//        /// </summary>
//        private string? FindSatelliteAssemblyFile(string directory, string baseName, string cultureName)
//        {
//            try
//            {
//                var tfm = GetCurrentTargetFramework();

//                // Patrones de búsqueda para DLLs satélite
//                var searchPatterns = new[]
//                {
//                    Path.Combine(directory, "lib", tfm, cultureName, $"{baseName}.resources.dll"),
//                    Path.Combine(directory, cultureName, $"{baseName}.resources.dll"),
//                    Path.Combine(directory, "lib", cultureName, $"{baseName}.resources.dll")
//                };

//                foreach (var pattern in searchPatterns)
//                {
//                    if (File.Exists(pattern))
//                    {
//                        return pattern;
//                    }
//                }

//                // Búsqueda recursiva como último recurso
//                var recursiveSearch = Directory.EnumerateFiles(directory, $"{baseName}.resources.dll", SearchOption.AllDirectories)
//                    .Where(f => f.Contains($"\\{cultureName}\\") || f.Contains($"/{cultureName}/"))
//                    .FirstOrDefault();

//                return recursiveSearch;
//            }
//            catch (Exception ex)
//            {
//                _nuGetLogger.LogDebug($"Error buscando DLL satélite en directorio específico: {ex.Message}");
//                return null;
//            }
//        }

//        private static string GetCurrentTargetFramework()
//        {
//            // Determinar el framework actual basado en el ensamblaje actual usando NuGet.Frameworks
//            var currentAssembly = Assembly.GetExecutingAssembly();

//            if (currentAssembly
//                .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
//                .FirstOrDefault() is System.Runtime.Versioning.TargetFrameworkAttribute targetFrameworkAttribute)
//            {
//                var fx = NuGetFramework.ParseFrameworkName(targetFrameworkAttribute.FrameworkName, DefaultFrameworkNameProvider.Instance);
//                var shortName = fx.GetShortFolderName();
//                if (!string.IsNullOrEmpty(shortName))
//                {
//                    return shortName;
//                }
//            }

//            // Valor por defecto común
//            return "net9.0";
//        }

//        public void Dispose()
//        {
//            Unregister();
//        }
//    }


//}
