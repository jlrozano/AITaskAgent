using System.Diagnostics;
using System.Globalization;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;
using CodebaseAnalysis.Core.Enrichment.Web;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// Enriquecimiento nativo por lenguaje (P1), entre la indexación y <see cref="DotNetEnricherStep"/>, en la fase
/// <c>enrich</c>. Recorre el repositorio en disco, deja <see cref="AnalysisContextKeys.DetectedLanguages"/> en el contexto
/// y ejecuta, por este orden, el analizador Java (si hay <c>.java</c> y está activado) y el plugin Struts/JSP (si hay
/// algún <c>struts-config*.xml</c>). Cada enriquecedor se salta si ya hay una pasada correcta sobre la misma indexación y
/// configuración (P3). Un enriquecedor que falla, o un JDK ausente, se queda en un aviso y una fila de
/// <c>enrichment_runs</c>: el análisis sigue con el grafo de cbm (criterio 8), del que se retira igualmente el código de
/// test si hay Java (D15).
/// </summary>
public sealed class LanguageEnricherStep : IStep<GraphStatusResult, GraphStatusResult>
{
    public const string StepName = "LanguageEnricherStep";

    /// <summary>Paquetes que faltan en el classpath que se nombran en el aviso; del resto solo se da el número.</summary>
    public const int MissingPackagesShown = 10;

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Con las opciones por defecto: el paso tiene un límite de <see cref="JavaAnalyzerOptions.StepTimeoutSeconds"/>.</summary>
    public LanguageEnricherStep() : this(new JavaAnalyzerOptions())
    {
    }

    /// <summary>El límite del paso sale de <c>JavaAnalyzer:StepTimeoutSeconds</c>: habrá repositorios de todos los tamaños.</summary>
    public LanguageEnricherStep(JavaAnalyzerOptions options)
    {
        Timeout = options.StepTimeout;
    }

    public string Name => StepName;
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 1000;

    /// <summary>
    /// Techo del paso entero (<c>JavaAnalyzer:StepTimeoutSeconds</c>, 3600 s por defecto; sin límite con 0). El proceso Java
    /// tiene el suyo (<c>JavaAnalyzer:TimeoutSeconds</c>, 900 s por defecto); este tiene que quedar por encima con margen
    /// para la escritura, y si no el paso lo avisa al arrancar.
    /// </summary>
    public TimeSpan? Timeout { get; }

    public Type InputType => typeof(GraphStatusResult);
    public Type OutputType => typeof(GraphStatusResult);

    public async Task<GraphStatusResult> ExecuteAsync(
        GraphStatusResult input,
        PipelineContext context,
        int attempt,
        GraphStatusResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        var pipelineInput = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput);
        var repositoryPath = Path.GetFullPath(pipelineInput?.RepositoryPath is { Length: > 0 } repo ? repo : Directory.GetCurrentDirectory());
        var databasePath = input.DatabasePath is { Length: > 0 } db ? db : pipelineInput?.DatabasePath ?? "";

        var configuration = context.ServiceProvider.GetService(typeof(IConfiguration)) as IConfiguration;
        var javaOptions = configuration != null ? JavaAnalyzerOptions.FromConfiguration(configuration) : new JavaAnalyzerOptions();
        var strutsOptions = configuration != null ? StrutsJspOptions.FromConfiguration(configuration) : new StrutsJspOptions();

        var testRoots = TestRoots.Resolve(repositoryPath, javaOptions.TestRoots);
        var scan = RepositoryScan.Scan(repositoryPath, testRoots, pipelineInput?.OutputReportsPath);
        context.Set(AnalysisContextKeys.DetectedLanguages, scan.Languages);
        context.Report(StepName, AnalysisLogLevel.Info, DescribeScan(scan));

        var struts = StrutsJspWriter.IsApplicable(scan, strutsOptions);
        var java = scan.JavaFiles.Count > 0;
        var testCodeRemoved = false;
        if (java && !javaOptions.Enabled)
        {
            context.Report(StepName, AnalysisLogLevel.Info, "ℹ️ Analizador Java desactivado (JavaAnalyzer:Enabled = false). Se continúa con el grafo de cbm.");
            testCodeRemoved = await RemoveTestNodesAsync(context, scan, databasePath, cancellationToken) > 0;
            java = false;
        }
        if (!java && !struts && !testCodeRemoved)
            return input;

        if (java && StepTimeoutBelowProcess(javaOptions) is { } warning)
            context.Report(StepName, AnalysisLogLevel.Warning, warning);

        var wrote = false;
        if (java)
            wrote |= await RunJavaAsync(context, scan, javaOptions, databasePath, cancellationToken);
        if (struts)
            wrote |= await RunStrutsAsync(context, scan, strutsOptions, javaOptions, databasePath, cancellationToken);
        // La alcanzabilidad necesita las aristas de javac y las rutas y formularios de Struts: va al final y solo si alguna escribió.
        if (java && wrote)
            await RunReachabilityAsync(context, scan, databasePath, cancellationToken);

        var (nodes, edges) = await CountAsync(databasePath, cancellationToken);
        return new GraphStatusResult
        {
            ExistedBefore = input.ExistedBefore,
            IsEnriched = input.IsEnriched,
            TotalNodes = nodes,
            TotalEdges = edges,
            DatabasePath = input.DatabasePath,
            Facets = input.Facets
        };
    }

    // ---------------------------------------------------------------- analizador Java

    /// <summary>Devuelve si la pasada escribió en el grafo (false si se omitió o falló).</summary>
    private static async Task<bool> RunJavaAsync(
        PipelineContext context, RepositoryScanResult scan, JavaAnalyzerOptions options, string databasePath, CancellationToken ct)
    {
        var enricher = JavaGraphWriter.EnricherName;
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        string? indexFingerprint = null;
        var configFingerprint = "";
        string? toolVersion = null;
        try
        {
            var encoding = SourceEncoding.ResolveDefault(scan.Root, options.DefaultEncoding);
            var classpath = JavaAnalyzerRunner.ComposeClasspath(scan, options.ProvidedClasspath);
            configFingerprint = EnrichmentRuns.ConfigFingerprint([
                encoding.Name,
                .. classpath.Entries.Select(e => "cp:" + e),
                .. scan.TestRoots.Select(r => "test:" + r)
            ]);

            using (var conn = await OpenAsync(databasePath, ct))
            {
                indexFingerprint = await EnrichmentRuns.IndexFingerprintAsync(conn, ct);
                if (await EnrichmentRuns.HasSuccessfulRunAsync(conn, enricher, JavaGraphWriter.EnricherVersion, indexFingerprint, configFingerprint, ct))
                {
                    context.Report(StepName, AnalysisLogLevel.Info,
                        "ℹ️ Analizador Java: el grafo ya está enriquecido para esta indexación y esta configuración; se omite. "
                        + "Se repite si cbm vuelve a indexar el repositorio (p. ej. con «Forzar reindexación») o si cambia la configuración.");
                    return false;
                }
            }

            JdkInfo jdk;
            try
            {
                jdk = JavaAnalyzerRunner.ResolveJdk(options.JdkPath);
            }
            catch (JavaAnalyzerException ex) when (ex.IsJdkProblem)
            {
                context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ Analizador Java: {ex.Message} Se continúa con el grafo de cbm.");
                var removed = await RemoveTestNodesAsync(context, scan, databasePath, ct);
                await RecordAsync(databasePath, enricher, JavaGraphWriter.EnricherVersion, null, indexFingerprint, configFingerprint,
                    startedAt, stopwatch, EnrichmentRuns.StatusSkipped, ex.Message, ct, removed);
                return false;
            }

            toolVersion = jdk.Version;
            context.Report(StepName, AnalysisLogLevel.Info, $"☕ Analizador Java: JDK {jdk.Version} en '{jdk.JdkPath}'.");
            context.Report(StepName, AnalysisLogLevel.Info,
                $"☕ Analizador Java: {P(scan.JavaFiles.Count, "fichero", "ficheros")} .java, {P(classpath.Entries.Count, "jar", "jars")} en el classpath, "
                + $"codificación por defecto {encoding.Name} ({encoding.Origin}).");
            if (classpath.Missing.Count > 0)
                context.Report(StepName, AnalysisLogLevel.Warning,
                    $"⚠️ Analizador Java: no existen estas entradas de JavaAnalyzer:ProvidedClasspath: {string.Join(", ", classpath.Missing)}.");

            var run = await JavaAnalyzerRunner.RunAsync(new JavaAnalyzerRunRequest
                {
                    Scan = scan,
                    Options = options,
                    Jdk = jdk,
                    Classpath = classpath,
                    DefaultEncoding = encoding.Name,
                    WorkingDirectory = JavaAnalyzerRunner.WorkingDirectoryFor(databasePath)
                },
                line => context.Report(StepName, AnalysisLogLevel.Info, $"   [java] {line}", AnalysisEventTypes.Channels.Indexer),
                progress => context.Report(StepName, AnalysisLogLevel.Info, DescribeProgress(progress)),
                ct);

            var result = await JavaGraphWriter.ApplyAsync(new JavaGraphWriteRequest
            {
                DatabasePath = databasePath,
                AnalyzedFiles = scan.JavaFiles,
                TestRoots = scan.TestRoots,
                ConfigFingerprint = configFingerprint,
                ToolVersion = jdk.Version,
                StartedAt = startedAt,
                RepositoryPath = scan.Root
            }, JavaAnalysisReader.Read(run.ResultPath), ct);

            ReportJavaResult(context, result, stopwatch.Elapsed);
            DeleteIntermediate(context, run.ResultPath, options.KeepIntermediate);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ Analizador Java: falló: {ex.Message} Se continúa con el grafo de cbm.");
            // Un fallo dentro de JavaGraphWriter deshace también su retirada del código de test.
            var removed = await RemoveTestNodesAsync(context, scan, databasePath, ct);
            await RecordAsync(databasePath, enricher, JavaGraphWriter.EnricherVersion, toolVersion, indexFingerprint, configFingerprint,
                startedAt, stopwatch, EnrichmentRuns.StatusFailed, ex.Message, ct, removed);
            return false;
        }
    }

    /// <summary>
    /// Borra el resultado del analizador ya aplicado al grafo (auditoría, H-11: en el repositorio objetivo pasa de 700 MB), salvo
    /// con <c>JavaAnalyzer:KeepIntermediate</c>. La petición y el log se quedan: son pequeños y dicen qué se analizó. Un fallo
    /// al borrar solo se avisa.
    /// </summary>
    private static void DeleteIntermediate(PipelineContext context, string resultPath, bool keep)
    {
        if (keep)
        {
            context.Report(StepName, AnalysisLogLevel.Info,
                $"ℹ️ Analizador Java: se conserva '{resultPath}' (JavaAnalyzer:KeepIntermediate).");
            return;
        }
        try
        {
            File.Delete(resultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ Analizador Java: no se pudo borrar '{resultPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// D15: el código de test no entra en el grafo aunque el analizador Java no llegue a escribir (desactivado, sin un JDK
    /// válido o con un fallo), porque entonces tampoco ocurre la retirada que hace <see cref="JavaGraphWriter"/>. Va en su
    /// propia transacción. Devuelve los nodos retirados, o null si no hay BD o no se pudo (queda en un aviso).
    /// </summary>
    private static async Task<int?> RemoveTestNodesAsync(
        PipelineContext context, RepositoryScanResult scan, string databasePath, CancellationToken ct)
    {
        if (scan.TestRoots.Count == 0)
            return 0;
        if (!File.Exists(databasePath))
            return null;
        try
        {
            using var conn = await EnrichmentMarks.OpenForBulkWriteAsync(databasePath, ct);
            using var tx = conn.BeginTransaction();
            var (nodes, _) = await EnrichmentMarks.RemoveNodesUnderAsync(conn, tx, scan.TestRoots, ct);
            tx.Commit();
            if (nodes > 0)
                context.Report(StepName, AnalysisLogLevel.Info,
                    $"ℹ️ Analizador Java: sin su pasada, se retiran igualmente del grafo {P(nodes, "nodo", "nodos")} del código de test "
                    + $"({string.Join(", ", scan.TestRoots)}).");
            return nodes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ Analizador Java: no se pudieron retirar del grafo los nodos de test: {ex.Message}");
            return null;
        }
    }

    private static void ReportJavaResult(PipelineContext context, JavaGraphWriteResult result, TimeSpan duration)
    {
        var summary = result.Summary;
        var stats = result.Stats;
        var outcome = result.Outcome;

        var calls = summary.Calls.Values.Sum();
        var unresolved = summary.Calls.GetValueOrDefault(JavaOrigins.Unresolved);
        var resolved = calls == 0 ? "sin llamadas" : $"{Percent(calls - unresolved, calls)} de las llamadas resueltas ({N(unresolved)} sin resolver de {N(calls)})";
        if (summary.Refs is { } refs)
        {
            // Q1: las referencias a tipos y campos cuentan todos los orígenes; sin resolver es lo que javac no supo atribuir.
            var references = refs.Type.Values.Sum() + refs.Field.Values.Sum();
            var referencesUnresolved = refs.Type.GetValueOrDefault(JavaOrigins.Unresolved) + refs.Field.GetValueOrDefault(JavaOrigins.Unresolved);
            resolved += references == 0
                ? "; sin referencias a tipos ni campos"
                : $"; {Percent(references - referencesUnresolved, references)} de las referencias a tipos y campos resueltas "
                  + $"({N(referencesUnresolved)} sin resolver de {N(references)})";
        }
        var created = summary.Refs == null
            ? $"{N(stats.CallEdges)} CALLS creadas"
            : $"{N(stats.CallEdges)} CALLS, {N(stats.UsesTypeEdges)} USES_TYPE, {N(stats.ReadsEdges)} READS y {N(stats.WritesEdges)} WRITES creadas";
        var replaced = stats.CbmEdgesReplacedByType.TryGetValue("USAGE", out var usage)
            ? $"{P(outcome.CbmEdgesReplaced, "arista", "aristas")} de cbm sustituidas ({N(usage)} USAGE)"
            : $"{P(outcome.CbmEdgesReplaced, "arista", "aristas")} CALLS/INHERITS/IMPLEMENTS de cbm sustituidas";
        var fields = stats.FieldsMatched + stats.FieldsCreated + stats.FieldsUnmatched;
        var fieldsText = fields == 0
            ? ""
            : $"{N(stats.FieldsMatched)} de {P(fields, "campo casado", "campos casados")} con cbm ({Percent(stats.FieldsMatched, fields)}), "
              + $"{N(stats.FieldsCreated)} {(stats.FieldsCreated == 1 ? "creado" : "creados")} y {N(stats.FieldsUnmatched)} sin nodo; ";
        context.Report(StepName, AnalysisLogLevel.Success,
            $"✅ Analizador Java: {P(outcome.FilesAnalyzed, "fichero", "ficheros")} en {Duration(duration)}; {resolved}; "
            + $"{replaced} y {created}; {fieldsText}"
            + $"{P(stats.OverloadsCreated, "sobrecarga creada", "sobrecargas creadas")}; {P(outcome.NodesRemoved, "nodo de test retirado", "nodos de test retirados")}.");

        if (CbmDriftWarning(stats) is { } drift)
            context.Report(StepName, AnalysisLogLevel.Warning, drift);

        // Criterio 9: el desglose de la escritura en el grafo dice dónde se va el tiempo sin tener que perfilar.
        if (stats.WriteTimingsMs.Count > 0)
            context.Report(StepName, AnalysisLogLevel.Info,
                $"⏱️ Analizador Java: escritura en el grafo en {Duration(TimeSpan.FromMilliseconds(stats.WriteTimingsMs.Sum(t => t.Value)))} "
                + $"({string.Join(", ", stats.WriteTimingsMs.Select(t => $"{t.Key} {(t.Value / 1000.0).ToString("0.0", Spanish)} s"))}).");

        var missing = summary.MissingPackages ?? [];
        if (missing.Count > 0)
        {
            var shown = string.Join(", ", missing.Take(MissingPackagesShown));
            var rest = missing.Count > MissingPackagesShown ? $" y {missing.Count - MissingPackagesShown} más" : "";
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ Analizador Java: {(missing.Count == 1 ? "falta 1 paquete" : $"faltan {N(missing.Count)} paquetes")} en el classpath: {shown}{rest}. "
                + "Añade sus jars en JavaAnalyzer:ProvidedClasspath.");
        }

        if (stats.FilesWithErrors > 0)
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ Analizador Java: {P(stats.FilesWithErrors, "fichero", "ficheros")} con errores de compilación; parte de sus llamadas pueden quedar sin resolver.");
    }

    /// <summary>
    /// Aviso de declaraciones sin casar con cbm (H-9): con más de <see cref="JavaGraphWriter.CbmDriftThreshold"/> de tipos, de
    /// métodos o de campos creados como nodos nuevos, la pasada no falla, pero el grafo tiene nodos que cbm no conoce. Separa las dos
    /// causas: ficheros que cbm no indexó (sin ningún nodo suyo), con la pista del MAX_PATH de Windows si alguno llega a
    /// <see cref="JavaGraphWriter.WindowsMaxPath"/> caracteres, y nombres que no casan en los ficheros que sí indexó, que es lo
    /// único que apunta a un cambio de versión de cbm. Null si casan.
    /// </summary>
    internal static string? CbmDriftWarning(JavaGraphStats stats)
    {
        if (!stats.ExceedsUnmatchedThreshold)
            return null;

        var threshold = $"{(JavaGraphWriter.CbmDriftThreshold * 100).ToString("0", Spanish)} %";
        var parts = new List<string>();
        var unindexedDeclarations = stats.TypesCreatedInUnindexedFiles + stats.MethodsCreatedInUnindexedFiles + stats.FieldsCreatedInUnindexedFiles;
        if (unindexedDeclarations > 0)
        {
            var files = stats.UnindexedFiles == 1 ? "1 fichero .java que el analizador sí leyó" : $"{N(stats.UnindexedFiles)} ficheros .java que el analizador sí leyó";
            var declarations = stats.FieldsCreatedInUnindexedFiles > 0
                ? $"{P(stats.TypesCreatedInUnindexedFiles, "tipo", "tipos")}, {P(stats.MethodsCreatedInUnindexedFiles, "método", "métodos")} sin contar sobrecargas "
                  + $"y {P(stats.FieldsCreatedInUnindexedFiles, "campo", "campos")}"
                : $"{P(stats.TypesCreatedInUnindexedFiles, "tipo", "tipos")} y {P(stats.MethodsCreatedInUnindexedFiles, "método", "métodos")} sin contar sobrecargas";
            var unindexed = $"cbm no indexó {files} (no tiene ningún nodo suyo); sus declaraciones se han creado como nodos nuevos: {declarations}.";
            if (stats.UnindexedLongPathFiles is int longPaths && longPaths > 0)
                unindexed += $" {(longPaths == stats.UnindexedFiles ? (longPaths == 1 ? "Ese fichero tiene" : "Todos tienen") : (longPaths == 1 ? "1 de ellos tiene" : $"{N(longPaths)} de ellos tienen"))} "
                    + $"una ruta absoluta de {JavaGraphWriter.WindowsMaxPath} caracteres o más, el límite MAX_PATH de Windows, que cbm no indexa: "
                    + "copia el repositorio a una ruta más corta.";
            parts.Add(unindexed);
        }

        var types = stats.TypesMatched + stats.TypesCreatedNameMismatch;
        var methods = stats.MethodsMatched + stats.MethodsCreatedNameMismatch;
        var fields = stats.FieldsMatched + stats.FieldsCreatedNameMismatch;
        var typesShare = $"{Share(stats.TypesCreatedNameMismatch, types)} de los tipos ({N(stats.TypesCreatedNameMismatch)} de {N(types)})";
        var methodsShare = $"{Share(stats.MethodsCreatedNameMismatch, methods)} de los métodos sin contar sobrecargas ({N(stats.MethodsCreatedNameMismatch)} de {N(methods)})";
        // Un resultado de schema 1 no trae campos: el texto queda como antes.
        var mismatch = fields > 0
            ? $"{typesShare}, {methodsShare} y {Share(stats.FieldsCreatedNameMismatch, fields)} de los campos ({N(stats.FieldsCreatedNameMismatch)} de {N(fields)})"
            : $"{typesShare} y {methodsShare}";
        var scope = unindexedDeclarations > 0 ? " en los ficheros que cbm sí indexó" : "";
        if (stats.SuspectsCbmDrift)
            parts.Add($"No casan con un nodo de cbm{scope} {mismatch}, por encima del {threshold}, y se han creado como nodos nuevos. "
                + "Los nombres de cbm no casan con los del analizador: ¿ha cambiado la versión de cbm?");
        else
            parts.Add($"En los ficheros que cbm sí indexó los nombres casan: no casan {mismatch}, por debajo del {threshold}.");

        var text = string.Join(" ", parts);
        return $"⚠️ Analizador Java: {char.ToLower(text[0], Spanish)}{text[1..]}";

        static string Share(long part, long total) => total > 0 ? Percent(part, total) : Percent(0, 1);
    }

    // ---------------------------------------------------------------- plugin Struts/JSP

    private static async Task<bool> RunStrutsAsync(
        PipelineContext context, RepositoryScanResult scan, StrutsJspOptions options, JavaAnalyzerOptions javaOptions,
        string databasePath, CancellationToken ct)
    {
        var enricher = StrutsJspWriter.EnricherName;
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        string? indexFingerprint = null;
        var configFingerprint = "";
        try
        {
            var encoding = SourceEncoding.ResolveDefault(scan.Root, javaOptions.DefaultEncoding);
            using (var conn = await OpenAsync(databasePath, ct))
            {
                indexFingerprint = await EnrichmentRuns.IndexFingerprintAsync(conn, ct);
                // Con la última pasada Java en la huella, una pasada Java nueva obliga a repetir el plugin (ver ConfigFingerprint).
                var lastJavaRun = await EnrichmentRuns.LastSuccessfulRunIdAsync(conn, JavaGraphWriter.EnricherName, ct);
                configFingerprint = StrutsJspWriter.ConfigFingerprint(options, encoding.Encoding, lastJavaRun);
                if (await EnrichmentRuns.HasSuccessfulRunAsync(conn, enricher, StrutsJspWriter.EnricherVersion, indexFingerprint, configFingerprint, ct))
                {
                    context.Report(StepName, AnalysisLogLevel.Info,
                        "ℹ️ Struts/JSP: el grafo ya tiene las acciones y las JSP de esta indexación y esta configuración; se omite.");
                    return false;
                }
            }

            var result = await StrutsJspWriter.ApplyAsync(databasePath, scan, options, encoding.Encoding, configFingerprint, startedAt, ct);
            var stats = result.Stats;
            context.Report(StepName, AnalysisLogLevel.Success,
                $"✅ Struts/JSP: {P(stats.Actions, "acción", "acciones")}, {N(stats.Jsps)} JSP ({P(stats.Suspicious, "sospechosa", "sospechosas")}), "
                + $"{P(stats.ForwardsUnresolved, "forward", "forwards")} sin resolver, en {Duration(stopwatch.Elapsed)}.");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ Struts/JSP: falló: {ex.Message} Se continúa sin las acciones ni las JSP.");
            await RecordAsync(databasePath, enricher, StrutsJspWriter.EnricherVersion, null, indexFingerprint, configFingerprint,
                startedAt, stopwatch, EnrichmentRuns.StatusFailed, ex.Message, ct);
            return false;
        }
    }

    // ---------------------------------------------------------------- alcanzabilidad y código muerto de Java

    /// <summary>
    /// <see cref="JavaReachabilityWriter"/> sobre el grafo ya escrito por el analizador Java y el plugin Struts/JSP. Un fallo se
    /// queda en un aviso: el grafo conserva lo escrito y simplemente no lleva <c>is_reachable</c> ni <c>dead_code_candidate</c>.
    /// </summary>
    private static async Task RunReachabilityAsync(PipelineContext context, RepositoryScanResult scan, string databasePath, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var reflection = DynamicInstantiationScanner.Scan(scan, ct);
            var stats = await JavaReachabilityWriter.ApplyAsync(databasePath, scan, reflection, ct);
            var roots = stats.Roots.Count == 0 ? "sin raíces" : "raíces: " + string.Join(", ", stats.Roots.Select(r => $"{r.Key} {N(r.Value)}"));
            context.Report(StepName, AnalysisLogLevel.Success,
                $"✅ Código muerto de Java: {P(stats.DeadTotal, "candidato", "candidatos")} (alta {N(stats.DeadHigh)}, media {N(stats.DeadMedium)}, "
                + $"baja {N(stats.DeadLow)}; {P(stats.DeadPrivateMethods, "método privado sin uso", "métodos privados sin uso")}, "
                + $"{N(stats.KeptForReflection)} conservados por posible reflexión) y {N(stats.Reachable)} alcanzables; {roots}; en {Duration(stopwatch.Elapsed)}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ Código muerto de Java: falló: {ex.Message} Se continúa sin is_reachable ni dead_code_candidate en Java.");
        }
    }

    // ---------------------------------------------------------------- apoyo

    /// <summary>
    /// Aviso si el límite del paso no deja al proceso Java su <c>TimeoutSeconds</c> más el margen para la escritura (un
    /// proceso sin límite no cabe en un paso con límite); null si cabe o si el paso no tiene límite. No es un error: el paso
    /// sigue, pero puede cortarse antes que el proceso.
    /// </summary>
    private string? StepTimeoutBelowProcess(JavaAnalyzerOptions options)
    {
        if (Timeout is not { } limit || limit == System.Threading.Timeout.InfiniteTimeSpan)
            return null;
        var process = options.TimeoutSeconds;
        if (process > 0 && limit.TotalSeconds >= process + JavaAnalyzerOptions.StepTimeoutMarginSeconds)
            return null;
        return $"⚠️ Analizador Java: el timeout del paso es menor que el del proceso Java: {N((long)limit.TotalSeconds)} s "
            + $"(JavaAnalyzer:StepTimeoutSeconds) frente a {(process > 0 ? $"{N(process)} s" : "sin límite")} (JavaAnalyzer:TimeoutSeconds) "
            + $"más {N(JavaAnalyzerOptions.StepTimeoutMarginSeconds)} s para la escritura en el grafo. El paso puede cortarse antes que el proceso.";
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath, CancellationToken ct)
    {
        EnrichmentMarks.AssertWorkingCopy(databasePath);
        var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// Fila <c>skipped</c> o <c>failed</c>; si ni eso se puede escribir, el aviso ya emitido basta. Con
    /// <paramref name="nodesRemoved"/> (los nodos de test retirados sin la pasada) la fila lleva recuentos, todos a cero salvo
    /// <c>nodes_removed</c>.
    /// </summary>
    private static async Task RecordAsync(
        string databasePath, string enricher, string version, string? toolVersion, string? indexFingerprint, string configFingerprint,
        DateTimeOffset startedAt, Stopwatch stopwatch, string status, string message, CancellationToken ct, int? nodesRemoved = null)
    {
        try
        {
            using var conn = await OpenAsync(databasePath, ct);
            await EnrichmentRuns.InsertAsync(conn, new EnrichmentRun
            {
                Enricher = enricher,
                EnricherVersion = version,
                ToolVersion = toolVersion,
                IndexFingerprint = indexFingerprint ?? "",
                ConfigFingerprint = configFingerprint,
                StartedAt = startedAt,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Status = status,
                Outcome = nodesRemoved is { } removed ? new EnrichmentOutcome { NodesRemoved = removed } : null,
                Message = message
            }, ct);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // La BD es la del propio análisis: si no se puede escribir, lo dirá el paso siguiente.
        }
    }

    private static async Task<(int Nodes, int Edges)> CountAsync(string databasePath, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(1) FROM nodes), (SELECT COUNT(1) FROM edges)";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static string DescribeScan(RepositoryScanResult scan)
    {
        var languages = new List<string>();
        if (scan.JavaFiles.Count > 0)
            languages.Add($"Java ({P(scan.JavaFiles.Count, "fichero", "ficheros")} .java)");
        if (scan.CSharpFiles.Count > 0)
            languages.Add($"C# ({P(scan.CSharpFiles.Count, "fichero", "ficheros")} .cs)");

        var detected = languages.Count > 0 ? string.Join(" y ", languages) : "ni Java ni C#";
        var web = scan.StrutsConfigs.Count > 0 || scan.JspFiles.Count > 0
            ? $"; {N(scan.StrutsConfigs.Count)} struts-config y {N(scan.JspFiles.Count)} JSP"
            : "";
        var tests = scan.TestRoots.Count > 0 ? $"; raíces de test excluidas: {string.Join(", ", scan.TestRoots)}" : "";
        return $"🔎 Lenguajes detectados en disco: {detected}{web}{tests}.";
    }

    private static string DescribeProgress(JavaAnalyzerProgress progress)
    {
        var phase = progress.Phase switch
        {
            "parse" => "lectura",
            "analyze" => "análisis",
            "scan" => "extracción",
            _ => progress.Phase
        };
        var percent = progress.Total > 0 ? $" ({Percent(progress.Done, progress.Total, decimals: 0)})" : "";
        return $"⏳ Analizador Java: {phase} {N(progress.Done)}/{N(progress.Total)} ficheros{percent}.";
    }

    private static string N(long value) => value.ToString("N0", Spanish);

    private static string P(long value, string singular, string plural) => $"{N(value)} {(value == 1 ? singular : plural)}";

    private static string Percent(long part, long total, int decimals = 2) =>
        (100.0 * part / total).ToString(decimals == 0 ? "0" : "0.00", Spanish) + " %";

    private static string Duration(TimeSpan duration) =>
        duration.TotalSeconds < 60
            ? duration.TotalSeconds.ToString("0.0", Spanish) + " s"
            : $"{(int)duration.TotalMinutes} min {duration.Seconds} s";

    public Task FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult((result is GraphStatusResult, (string?)null));
}
