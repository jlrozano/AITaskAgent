using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Enrichment.Java;

/// <summary>Motivo por el que no se pudo ejecutar el analizador Java.</summary>
public enum JavaAnalyzerFailure
{
    /// <summary><c>JavaAnalyzer:JdkPath</c> está vacío.</summary>
    JdkNotConfigured,

    /// <summary>La carpeta del JDK no existe o no tiene <c>bin/java</c>.</summary>
    JdkNotFound,

    /// <summary>La carpeta no tiene un <c>release</c> con <c>JAVA_VERSION</c> legible: no se puede comprobar la versión.</summary>
    JdkVersionUnknown,

    /// <summary>El <c>release</c> declara una versión anterior a <see cref="JavaAnalyzerRunner.MinimumJavaVersion"/>.</summary>
    JdkTooOld,

    /// <summary>No está <c>JavaAnalyzer.java</c> junto al ejecutable.</summary>
    AnalyzerSourceNotFound,

    /// <summary>El proceso terminó con un código distinto de 0.</summary>
    ProcessFailed,

    /// <summary>El proceso superó <c>JavaAnalyzer:TimeoutSeconds</c> y se mató.</summary>
    Timeout,

    /// <summary>El proceso terminó bien pero no dejó el fichero de resultados.</summary>
    ResultMissing
}

/// <summary>Error tipado del analizador Java: el paso lo convierte en un aviso y una fila de <c>enrichment_runs</c>.</summary>
public sealed class JavaAnalyzerException(JavaAnalyzerFailure failure, string message, int? exitCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public JavaAnalyzerFailure Failure { get; } = failure;

    public int? ExitCode { get; } = exitCode;

    /// <summary>
    /// Fallos del JDK (ausente, no encontrado, versión desconocida o antigua): el enriquecimiento se omite (fila
    /// <c>skipped</c>). El resto son fallos de la pasada (fila <c>failed</c>).
    /// </summary>
    public bool IsJdkProblem => Failure is JavaAnalyzerFailure.JdkNotConfigured or JavaAnalyzerFailure.JdkNotFound
        or JavaAnalyzerFailure.JdkVersionUnknown or JavaAnalyzerFailure.JdkTooOld;
}

/// <summary>JDK validado: carpeta, ejecutable <c>java</c> y versión según su fichero <c>release</c>.</summary>
public sealed record JdkInfo(string JdkPath, string JavaExecutable, string Version, int FeatureVersion);

/// <summary>Línea <c>@@progress</c> del analizador.</summary>
public sealed record JavaAnalyzerProgress(string Phase, int Done, int Total);

/// <summary>Classpath compuesto: jars ya expandidos y entradas de <c>ProvidedClasspath</c> que no existen.</summary>
public sealed record JavaClasspath(IReadOnlyList<string> Entries, IReadOnlyList<string> Missing);

/// <summary>Lo que necesita una ejecución del analizador.</summary>
public sealed record JavaAnalyzerRunRequest
{
    public required RepositoryScanResult Scan { get; init; }
    public required JavaAnalyzerOptions Options { get; init; }
    public required JdkInfo Jdk { get; init; }
    public required JavaClasspath Classpath { get; init; }

    /// <summary>Nombre de la codificación por defecto (P6) que se pasa a Java.</summary>
    public required string DefaultEncoding { get; init; }

    /// <summary>Carpeta de trabajo (P4): petición, resultado y log.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Ruta de <c>JavaAnalyzer.java</c>; por defecto la de <see cref="JavaAnalyzerRunner.DefaultAnalyzerSourcePath"/>.</summary>
    public string? AnalyzerSourcePath { get; init; }
}

/// <summary>Resultado de <see cref="JavaAnalyzerRunner.RunAsync"/>.</summary>
public sealed record JavaAnalyzerRunResult(string ResultPath, string RequestPath, string LogPath, TimeSpan Duration);

/// <summary>
/// Lanza <c>JavaAnalyzer.java</c> con el <i>source launcher</i> del JDK configurado (D13): valida el JDK por su fichero
/// <c>release</c>, compone el classpath, escribe la petición (P5) en la carpeta de trabajo (P4), lee la salida estándar
/// línea a línea (progreso y log) y aplica <c>JavaAnalyzer:TimeoutSeconds</c>. No sabe nada del grafo: devuelve la ruta
/// del fichero de resultados.
/// </summary>
public static class JavaAnalyzerRunner
{
    public const int MinimumJavaVersion = 17;
    public const string RequestFileName = "request.txt";
    public const string ResultFileName = "java-analysis.jsonl";
    public const string LogFileName = "java-analyzer.log";
    public const string ProgressPrefix = "@@progress ";

    /// <summary>Exports que exige el analizador (ver su cabecera): <c>javac.code</c> siempre y <c>javac.api</c> para los lotes.</summary>
    public static readonly IReadOnlyList<string> AddExports =
    [
        "--add-exports", "jdk.compiler/com.sun.tools.javac.code=ALL-UNNAMED",
        "--add-exports", "jdk.compiler/com.sun.tools.javac.api=ALL-UNNAMED"
    ];

    /// <summary>Líneas de stderr que se conservan para el mensaje de error.</summary>
    private const int StderrTail = 20;

    private static readonly Regex ProgressLine = new(
        @"^@@progress\s+phase=(?<phase>\w+)\s+done=(?<done>\d+)\s+total=(?<total>\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex JavaVersionLine = new(
        @"^\s*JAVA_VERSION\s*=\s*""?(?<version>[^""\s]+)""?", RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>El analizador viaja como contenido de Core y llega a la salida de quien la referencia.</summary>
    public static string DefaultAnalyzerSourcePath => Path.Combine(AppContext.BaseDirectory, "Enrichment", "Java", "JavaAnalyzer.java");

    /// <summary>Carpeta de trabajo (P4): <c>&lt;directorio de la BD&gt;/enrichment/java/</c>.</summary>
    public static string WorkingDirectoryFor(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".", "enrichment", "java");

    /// <summary>
    /// Valida el JDK: la carpeta existe, su <c>release</c> declara Java <see cref="MinimumJavaVersion"/> o superior y
    /// tiene <c>bin/java</c>. No ejecuta nada.
    /// </summary>
    /// <exception cref="JavaAnalyzerException">Con un fallo de JDK (<see cref="JavaAnalyzerException.IsJdkProblem"/>).</exception>
    public static JdkInfo ResolveJdk(string? jdkPath)
    {
        if (string.IsNullOrWhiteSpace(jdkPath))
            throw new JavaAnalyzerException(JavaAnalyzerFailure.JdkNotConfigured,
                $"no hay JDK configurado (JavaAnalyzer:JdkPath); hace falta un JDK {MinimumJavaVersion} o superior.");

        var root = Path.GetFullPath(jdkPath.Trim());
        if (!Directory.Exists(root))
            throw new JavaAnalyzerException(JavaAnalyzerFailure.JdkNotFound, $"no existe la carpeta del JDK '{root}' (JavaAnalyzer:JdkPath).");

        var releasePath = Path.Combine(root, "release");
        var match = File.Exists(releasePath) ? JavaVersionLine.Match(File.ReadAllText(releasePath)) : Match.Empty;
        if (!match.Success || FeatureVersion(match.Groups["version"].Value) is not { } feature)
            throw new JavaAnalyzerException(JavaAnalyzerFailure.JdkVersionUnknown,
                $"'{root}' no tiene un fichero 'release' con JAVA_VERSION: no se puede comprobar que sea un JDK {MinimumJavaVersion} o superior.");

        var version = match.Groups["version"].Value;
        if (feature < MinimumJavaVersion)
            throw new JavaAnalyzerException(JavaAnalyzerFailure.JdkTooOld,
                $"el JDK de '{root}' es la versión {version}; hace falta la {MinimumJavaVersion} o superior.");

        var java = new[] { "java.exe", "java" }.Select(n => Path.Combine(root, "bin", n)).FirstOrDefault(File.Exists)
            ?? throw new JavaAnalyzerException(JavaAnalyzerFailure.JdkNotFound, $"'{root}' no tiene bin/java: no es un JDK.");
        return new JdkInfo(root, java, version, feature);
    }

    /// <summary>Versión principal de una <c>JAVA_VERSION</c>: <c>17.0.20.1</c> → 17, <c>1.8.0_391</c> → 8.</summary>
    public static int? FeatureVersion(string version)
    {
        var parts = version.Split('.', '_', '-', '+');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first))
            return null;
        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var legacy))
            return legacy;
        return first;
    }

    /// <summary>
    /// Classpath: los jars del repositorio más <c>ProvidedClasspath</c>. Una entrada de <c>ProvidedClasspath</c> relativa
    /// se resuelve contra el repositorio y una carpeta se expande a sus <c>*.jar</c> (sin subcarpetas). Sin repetidos.
    /// </summary>
    public static JavaClasspath ComposeClasspath(RepositoryScanResult scan, IEnumerable<string> providedClasspath)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        void Add(string path)
        {
            if (seen.Add(path))
                entries.Add(path);
        }

        foreach (var jar in scan.Jars)
            Add(scan.FullPath(jar));

        foreach (var entry in providedClasspath.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()))
        {
            var full = Path.GetFullPath(Path.IsPathRooted(entry) ? entry : Path.Combine(scan.Root, entry));
            if (Directory.Exists(full))
            {
                foreach (var jar in Directory.EnumerateFiles(full, "*.jar", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
                    Add(Path.GetFullPath(jar));
            }
            else if (File.Exists(full))
                Add(full);
            else
                missing.Add(entry);
        }
        return new JavaClasspath(entries, missing);
    }

    /// <summary>Texto de la petición (P5): <c>clave=valor</c>, listas con la clave repetida.</summary>
    public static string BuildRequest(JavaAnalyzerRunRequest request)
    {
        var text = new StringBuilder();
        void Line(string key, string value) => text.Append(key).Append('=').Append(value).Append('\n');

        Line("root", request.Scan.Root);
        Line("out", Path.GetFullPath(request.WorkingDirectory));
        Line("defaultEncoding", request.DefaultEncoding);
        Line("batchSize", request.Options.BatchSize.ToString(CultureInfo.InvariantCulture));
        Line("maxParallelBatches", Math.Max(1, request.Options.MaxParallelBatches).ToString(CultureInfo.InvariantCulture));
        foreach (var entry in request.Classpath.Entries)
            Line("classpath", entry);
        foreach (var file in request.Scan.JavaFiles)
            Line("file", file);
        return text.ToString();
    }

    /// <param name="request">JDK validado, classpath, opciones y carpeta de trabajo.</param>
    /// <param name="onOutput">Cada línea de stdout y stderr, tal cual (el paso la lleva al canal <c>indexer</c>).</param>
    /// <param name="onProgress">Progreso resumido: como mucho una vez por fase y por cada 10 % de ficheros.</param>
    /// <param name="ct">Cancelación del análisis: mata el proceso y relanza.</param>
    /// <exception cref="JavaAnalyzerException">Si el analizador no está, falla, supera el timeout o no deja resultado.</exception>
    public static async Task<JavaAnalyzerRunResult> RunAsync(
        JavaAnalyzerRunRequest request,
        Action<string>? onOutput,
        Action<JavaAnalyzerProgress>? onProgress,
        CancellationToken ct)
    {
        var source = Path.GetFullPath(request.AnalyzerSourcePath ?? DefaultAnalyzerSourcePath);
        if (!File.Exists(source))
            throw new JavaAnalyzerException(JavaAnalyzerFailure.AnalyzerSourceNotFound, $"no se encuentra el analizador '{source}'.");

        var workDir = Path.GetFullPath(request.WorkingDirectory);
        Directory.CreateDirectory(workDir);
        var requestPath = Path.Combine(workDir, RequestFileName);
        var resultPath = Path.Combine(workDir, ResultFileName);
        var logPath = Path.Combine(workDir, LogFileName);

        // Un resultado de una pasada anterior no puede pasar por el de esta.
        File.Delete(resultPath);
        File.Delete(resultPath + ".tmp");
        await File.WriteAllTextAsync(requestPath, BuildRequest(request), new UTF8Encoding(false), ct);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Jdk.JavaExecutable,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var option in request.Options.JvmOptions)
            startInfo.ArgumentList.Add(option);
        foreach (var argument in AddExports)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(source);
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);

        var throttle = new ProgressThrottle(onProgress);
        var stderrTail = new Queue<string>();
        var logLock = new Lock();
        // Tras un timeout o una cancelación el proceso muerto aún puede entregar líneas: se descartan en lugar de escribir
        // en el log ya cerrado desde el hilo del evento.
        var closed = false;
        using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false));
        log.WriteLine($"# {DateTimeOffset.Now:o} {startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)}");

        void Record(string line, bool isError)
        {
            lock (logLock)
            {
                if (closed)
                    return;
                log.WriteLine(isError ? "[err] " + line : line);
                if (isError)
                {
                    stderrTail.Enqueue(line);
                    if (stderrTail.Count > StderrTail)
                        stderrTail.Dequeue();
                }
            }
            onOutput?.Invoke(line);
            if (!isError && ProgressLine.Match(line) is { Success: true } m)
                throttle.Report(new JavaAnalyzerProgress(m.Groups["phase"].Value,
                    int.Parse(m.Groups["done"].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture)));
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Record(e.Data, isError: false);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Record(e.Data, isError: true);
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await StartAndWaitAsync();
        }
        finally
        {
            lock (logLock)
                closed = true;
        }

        async Task<JavaAnalyzerRunResult> StartAndWaitAsync()
        {
            try
            {
                if (!process.Start())
                    throw new JavaAnalyzerException(JavaAnalyzerFailure.ProcessFailed, $"no se pudo iniciar '{startInfo.FileName}'.");
            }
            catch (Win32Exception ex)
            {
                throw new JavaAnalyzerException(JavaAnalyzerFailure.ProcessFailed, $"no se pudo iniciar '{startInfo.FileName}': {ex.Message}", inner: ex);
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (request.Options.TimeoutSeconds > 0)
                timeout.CancelAfter(TimeSpan.FromSeconds(request.Options.TimeoutSeconds));
            try
            {
                await ProcessCancellation.WaitForExitOrKillAsync(process, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new JavaAnalyzerException(JavaAnalyzerFailure.Timeout,
                    $"superó JavaAnalyzer:TimeoutSeconds ({request.Options.TimeoutSeconds} s) y se ha detenido.");
            }

            // Tras el exit quedan por leer las últimas líneas de las lecturas asíncronas.
            process.WaitForExit();
            stopwatch.Stop();

            if (process.ExitCode != 0)
            {
                string tail;
                lock (logLock)
                    tail = string.Join(" | ", stderrTail);
                var reason = process.ExitCode switch
                {
                    1 => "petición inválida o el launcher no pudo compilar el analizador",
                    2 => "javac no pudo arrancar",
                    3 => "fallo inesperado durante el análisis (p. ej. falta de memoria)",
                    _ => "código de salida inesperado"
                };
                throw new JavaAnalyzerException(JavaAnalyzerFailure.ProcessFailed,
                    $"el proceso terminó con código {process.ExitCode} ({reason}). Log: {logPath}"
                    + (tail.Length > 0 ? $". Últimas líneas de error: {tail}" : ""),
                    process.ExitCode);
            }

            if (!File.Exists(resultPath))
                throw new JavaAnalyzerException(JavaAnalyzerFailure.ResultMissing, $"el proceso terminó sin dejar '{resultPath}'. Log: {logPath}");

            return new JavaAnalyzerRunResult(resultPath, requestPath, logPath, stopwatch.Elapsed);
        }
    }

    /// <summary>Deja pasar la primera línea de cada fase, cada 10 % más de ficheros y la última.</summary>
    private sealed class ProgressThrottle(Action<JavaAnalyzerProgress>? sink)
    {
        private readonly Lock _lock = new();
        private string? _phase;
        private int _bucket = -1;

        public void Report(JavaAnalyzerProgress progress)
        {
            if (sink == null)
                return;
            var bucket = progress.Total <= 0 ? 10 : (int)Math.Min(10, 10L * progress.Done / progress.Total);
            lock (_lock)
            {
                if (progress.Phase == _phase && bucket <= _bucket)
                    return;
                _phase = progress.Phase;
                _bucket = bucket;
            }
            sink(progress);
        }
    }
}
