using System.Diagnostics;
using System.Text;
using CodebaseAnalysis.Core.Enrichment;

namespace DotNetEnricher;

/// <summary>
/// Qué hizo (o no) la restauración y con qué propiedades globales hay que cargar la solución: siempre las que redirigen
/// <c>obj/</c> a la caché y, si se restauró, las que además leen de ella los <c>project.assets.json</c> (<see cref="Redirect"/>).
/// <see cref="Properties"/> vacío = cargar la solución tal cual (solo cuando la caché no se pudo preparar).
/// </summary>
internal sealed record RestoreOutcome(
    bool Attempted, bool Succeeded, IReadOnlyDictionary<string, string> Properties, string Message)
{
    public bool Redirect => Properties.ContainsKey(NuGetRestore.RestoreRootProperty);

    /// <summary>Sin restauración ni redirección alguna: se carga la solución tal cual.</summary>
    public static RestoreOutcome Skipped(string message) =>
        new(false, false, new Dictionary<string, string>(), message);
}

/// <summary>
/// Restaura los paquetes NuGet de la solución <b>sin escribir en el repositorio analizado</b>. Sin <c>project.assets.json</c>
/// Roslyn no resuelve los paquetes (p. ej. <c>SqlConnection</c>) y se pierden las reglas que dependen de sus tipos.
///
/// La salida de <c>dotnet restore</c> (<c>project.assets.json</c>, <c>nuget.g.props</c>, <c>nuget.g.targets</c>) va a
/// <c>$(MSBuildProjectExtensionsPath)</c>, por defecto <c>obj/</c> del proyecto. Se redirige a una carpeta de caché, una por
/// proyecto, con un fichero de propiedades que se importa al principio y al final de <c>Microsoft.Common.props</c>
/// (<c>CustomBefore/AfterMicrosoftCommonProps</c>): al principio, para que los <c>nuget.g.props</c> se lean de la caché; al
/// final, para ganar a un <c>Directory.Build.props</c> del repositorio que fije otra ruta. Las mismas propiedades globales se
/// pasan a <c>MSBuildWorkspace.Create</c>, así que la carga lee lo que la restauración escribió. Es un proceso aparte (el CLI
/// ya lo es por diseño) para que el timeout mate <c>dotnet restore</c> con todo su árbol.
/// </summary>
internal static class NuGetRestore
{
    public const string BeforePropsFileName = "DotNetEnricher.before.props";
    public const string AfterPropsFileName = "DotNetEnricher.after.props";
    public const string RestoreRootProperty = "DotNetEnricherRestoreRoot";
    public const string ObjRootProperty = "DotNetEnricherObjRoot";
    public const string BinRootProperty = "DotNetEnricherBinRoot";

    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", ".git", ".vs", ".codebase", "reports", "logs"];

    /// <summary>Los <c>.csproj</c> del repositorio, sin los de <c>bin</c>, <c>obj</c> ni las carpetas de salida del pipeline.</summary>
    public static IReadOnlyList<string> FindProjects(string repoRoot)
    {
        var projects = new List<string>();
        if (!Directory.Exists(repoRoot))
            return projects;
        var pending = new Stack<string>();
        pending.Push(repoRoot);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                projects.AddRange(Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly));
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!SkippedFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                        pending.Push(sub);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Una carpeta ilegible no impide restaurar el resto.
            }
        }
        projects.Sort(StringComparer.Ordinal);
        return projects;
    }

    /// <summary><c>obj/project.assets.json</c> del proyecto, donde lo deja una restauración normal.</summary>
    public static bool HasAssets(string projectFile) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(projectFile)!, "obj", "project.assets.json"));

    /// <summary>
    /// <see cref="DotNetRestoreMode.Always"/> restaura siempre; <see cref="DotNetRestoreMode.Auto"/> solo si a algún proyecto le
    /// falta su <c>project.assets.json</c>; <see cref="DotNetRestoreMode.Never"/> nunca. Sin proyectos no hay nada que restaurar.
    /// </summary>
    public static bool NeedsRestore(DotNetRestoreMode mode, IReadOnlyList<string> projects) => mode switch
    {
        _ when projects.Count == 0 => false,
        DotNetRestoreMode.Never => false,
        DotNetRestoreMode.Always => true,
        _ => projects.Any(p => !HasAssets(p))
    };

    /// <summary>
    /// Propiedades globales de la restauración y de la carga de la solución (las mismas en las dos). Siempre redirigen
    /// <c>obj/</c> y <c>bin/</c> (<c>BaseIntermediateOutputPath</c>, <c>BaseOutputPath</c>) a la caché: la carga de MSBuildWorkspace ejecuta destinos de diseño que
    /// escriben ahí <c>AssemblyInfo.cs</c> y <c>GlobalUsings.g.cs</c>. Con <paramref name="redirectRestore"/> redirigen además
    /// la salida de NuGet (<c>project.assets.json</c>); sin él, los assets se siguen leyendo de <c>obj/</c> del repositorio.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GlobalProperties(string cacheRoot, bool redirectRestore = true)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot)).Replace('\\', '/');
        var props = new Dictionary<string, string>
        {
            ["CustomBeforeMicrosoftCommonProps"] = Path.Combine(Path.GetFullPath(cacheRoot), BeforePropsFileName),
            ["CustomAfterMicrosoftCommonProps"] = Path.Combine(Path.GetFullPath(cacheRoot), AfterPropsFileName),
            [ObjRootProperty] = root + "/obj/",
            [BinRootProperty] = root + "/bin/"
        };
        if (redirectRestore)
            props[RestoreRootProperty] = root + "/pkg/";
        return props;
    }

    // Una carpeta por proyecto (nombre y hash de su ruta, por si dos proyectos se llaman igual). Se importa dos veces con el
    // mismo contenido (dos ficheros, porque MSBuild avisa de un import repetido): al principio, para que los nuget.g.props se
    // lean de la caché, y al final, para ganar a un Directory.Build.props del repositorio que fije otra ruta.
    private const string PropsContent = """
        <Project>
          <PropertyGroup Condition="'$(DotNetEnricherObjRoot)' != ''">
            <MSBuildProjectExtensionsPath Condition="'$(MSBuildProjectExtensionsPath)' == '' and '$(DotNetEnricherRestoreRoot)' == ''">$(MSBuildProjectDirectory)/obj/</MSBuildProjectExtensionsPath>
            <BaseIntermediateOutputPath>$(DotNetEnricherObjRoot)$(MSBuildProjectName)-$([MSBuild]::StableStringHash('$(MSBuildProjectDirectory)'))/</BaseIntermediateOutputPath>
            <BaseOutputPath>$(DotNetEnricherBinRoot)$(MSBuildProjectName)-$([MSBuild]::StableStringHash('$(MSBuildProjectDirectory)'))/</BaseOutputPath>
          </PropertyGroup>
          <PropertyGroup Condition="'$(DotNetEnricherRestoreRoot)' != ''">
            <MSBuildProjectExtensionsPath>$(DotNetEnricherRestoreRoot)$(MSBuildProjectName)-$([MSBuild]::StableStringHash('$(MSBuildProjectDirectory)'))/</MSBuildProjectExtensionsPath>
            <RestoreOutputPath>$(MSBuildProjectExtensionsPath)</RestoreOutputPath>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>Crea la caché y sus dos ficheros de propiedades; false (con el motivo) si no se puede escribir.</summary>
    private static async Task<string?> PrepareCacheAsync(string fullCache, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(fullCache);
            foreach (var name in new[] { BeforePropsFileName, AfterPropsFileName })
                await File.WriteAllTextAsync(Path.Combine(fullCache, name), PropsContent, ct);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"no se pudo preparar la caché '{fullCache}': {ex.Message}";
        }
    }

    /// <summary>
    /// Decide si restaurar y, si hace falta, lo hace en <paramref name="cacheRoot"/> (fuera del repositorio). Nunca lanza por un
    /// fallo de la restauración: devuelve el resultado con un mensaje y el paso sigue sin paquetes.
    /// </summary>
    /// <remarks><c>timeoutSeconds</c> limita <c>dotnet restore</c> (0 = sin límite); <c>dotnetHost</c> es la ruta de <c>dotnet</c> (null = la deduce del entorno).</remarks>
    public static async Task<RestoreOutcome> PrepareAsync(
        DotNetRestoreMode mode, string solutionPath, string repoRoot, string cacheRoot, int timeoutSeconds, string? dotnetHost,
        TextWriter log, CancellationToken ct)
    {
        // Hacia dentro del repositorio no se escribe nunca: una caché dentro de él solo vale bajo .codebase (la carpeta del pipeline).
        var fullCache = Path.GetFullPath(cacheRoot);
        if (IsInside(fullCache, repoRoot) && !IsInside(fullCache, Path.Combine(repoRoot, ".codebase")))
            return RestoreOutcome.Skipped($"la caché '{fullCache}' estaría dentro del repositorio; no se restaura ni se redirige obj/");

        var projects = FindProjects(repoRoot);
        var needsRestore = mode != DotNetRestoreMode.Never && NeedsRestore(mode, projects);
        var cacheError = await PrepareCacheAsync(fullCache, ct);
        if (cacheError != null)
            return new RestoreOutcome(needsRestore, false, new Dictionary<string, string>(), cacheError + (needsRestore ? "; se sigue sin paquetes" : ""));

        // Sin restauración, la carga solo redirige obj/ (assets, si los hay, se leen del repositorio).
        if (!needsRestore)
        {
            var why = mode == DotNetRestoreMode.Never ? "restauración desactivada (DotNetEnricher:Restore = never)"
                : projects.Count == 0 ? "no hay proyectos .csproj que restaurar"
                : $"los {projects.Count} proyecto(s) ya tienen su project.assets.json en el repositorio; se usan sin restaurar";
            return new RestoreOutcome(false, false, GlobalProperties(fullCache, redirectRestore: false), why);
        }

        var properties = GlobalProperties(fullCache);

        log.WriteLine($"[restore] dotnet restore de {Path.GetFileName(solutionPath)} con la salida en {fullCache} (límite {(timeoutSeconds > 0 ? timeoutSeconds + " s" : "ninguno")})...");
        var stopwatch = Stopwatch.StartNew();
        var run = await RunRestoreAsync(solutionPath, properties, timeoutSeconds, dotnetHost, log, ct);
        var seconds = stopwatch.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        if (run.Succeeded)
            return new RestoreOutcome(true, true, properties, $"restauración correcta en {seconds} s");

        // La restauración falló o se cortó. Si dejó assets en la caché y el repositorio no tiene ninguno propio, se usan los
        // parciales (NuGet escribe el fichero aunque algún paquete no se resuelva); si no, se carga sin redirección.
        var partial = Directory.Exists(fullCache) && Directory.EnumerateFiles(fullCache, "project.assets.json", SearchOption.AllDirectories).Any();
        var repoHasAssets = projects.Any(HasAssets);
        var redirect = partial && !repoHasAssets;
        return new RestoreOutcome(true, false, redirect ? properties : GlobalProperties(fullCache, redirectRestore: false),
            $"la restauración de paquetes NuGet {run.Reason} tras {seconds} s; Roslyn seguirá sin resolver los paquetes que falten"
            + (redirect ? " (se usan los resultados parciales)" : "") + "."
            + (run.Tail.Length > 0 ? "\n" + run.Tail : ""));
    }

    private sealed record RunResult(bool Succeeded, string Reason, string Tail);

    private static async Task<RunResult> RunRestoreAsync(
        string solutionPath, IReadOnlyDictionary<string, string> properties, int timeoutSeconds, string? dotnetHost, TextWriter log,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = dotnetHost ?? ResolveDotnetHost(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(solutionPath)!
        };
        psi.ArgumentList.Add("restore");
        psi.ArgumentList.Add(solutionPath);
        foreach (var (key, value) in properties)
            psi.ArgumentList.Add($"-p:{key}={value}");
        // Un origen caído (entorno corporativo sin acceso) no debe tumbar toda la restauración.
        psi.ArgumentList.Add("--ignore-failed-sources");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("-v:minimal");
        // El MSBuild que localizó MSBuildLocator en este proceso no es el que debe usar el SDK hijo.
        foreach (var variable in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath" })
            psi.Environment.Remove(variable);
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        // Sin reutilizar nodos de MSBuild: un nodo nuevo hereda la salida redirigida y se queda vivo al acabar la
        // restauración, así que la lectura de la salida no terminaba hasta agotar el límite (visto en RestoreTests).
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        var output = new List<string>();
        void Collect(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (output) output.Add(line.Trim());
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);
        try
        {
            if (!process.Start())
                return new RunResult(false, "no se pudo iniciar", "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new RunResult(false, $"no se pudo iniciar ({ex.Message})", "");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds > 0)
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (ct.IsCancellationRequested)
                throw;
            return new RunResult(false, $"superó su límite de {timeoutSeconds} s (DotNetEnricher:RestoreTimeoutSeconds) y se detuvo", Tail());
        }

        // Tras el Exited quedan por llegar las últimas líneas redirigidas.
        await process.WaitForExitAsync(CancellationToken.None);
        foreach (var line in Snapshot()) log.WriteLine($"  [restore] {line}");
        return process.ExitCode == 0
            ? new RunResult(true, "", "")
            : new RunResult(false, $"falló con código {process.ExitCode}", Tail());

        string[] Snapshot() { lock (output) return [.. output]; }
        string Tail() => string.Join("\n", Snapshot().TakeLast(8));
    }

    /// <summary>
    /// <c>DOTNET_HOST_PATH</c>, si no el <c>dotnet</c> de la raíz de la instalación del SDK cuyo MSBuild se registró (<c>…/dotnet/sdk/x.y.z</c>)
    /// y, en último caso, el que haya en el PATH.
    /// </summary>
    internal static string ResolveDotnetHost(string? msbuildPath = null)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } fromEnv && File.Exists(fromEnv))
            return fromEnv;
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (!string.IsNullOrWhiteSpace(msbuildPath))
        {
            var root = Directory.GetParent(Path.TrimEndingDirectorySeparator(msbuildPath))?.Parent?.FullName;
            if (root != null && File.Exists(Path.Combine(root, exe)))
                return Path.Combine(root, exe);
        }
        return "dotnet";
    }

    private static bool IsInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>Caché por defecto sin <c>--cache-dir</c>: bajo <c>%LOCALAPPDATA%</c>, una carpeta por solución.</summary>
    public static string DefaultCacheRoot(string solutionPath)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(solutionPath).ToLowerInvariant())))[..12];
        var baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseFolder)) baseFolder = Path.GetTempPath();
        return Path.Combine(baseFolder, "DotNetEnricher", "restore", hash);
    }
}
