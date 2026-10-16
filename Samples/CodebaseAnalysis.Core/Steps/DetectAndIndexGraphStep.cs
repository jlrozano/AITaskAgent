using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace CodebaseAnalysis.Core.Steps;

public class DetectAndIndexGraphStep : IStep<AnalysisPipelineInput, GraphStatusResult>
{
    public const string StepName = "DetectAndIndexGraphStep";

    public string Name => StepName;
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 1000;
    /// <summary><c>CodebaseAnalysis:Timeouts:DetectMinutes</c>; lo fija la factoría.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(15);
    public Type InputType => typeof(AnalysisPipelineInput);
    public Type OutputType => typeof(GraphStatusResult);

    public async Task<GraphStatusResult> ExecuteAsync(
        AnalysisPipelineInput input, 
        PipelineContext context, 
        int attempt, 
        GraphStatusResult? lastStepResult, 
        CancellationToken cancellationToken)
    {
        // Store input in context for subsequent steps
        context.Set(AnalysisContextKeys.PipelineInput, input);
        context.Set(AnalysisContextKeys.CbmDbPath, input.DatabasePath);
        context.Set(AnalysisContextKeys.KnowledgePaths, new Knowledge.KnowledgePaths(input.RepositoryPath, input.OutputReportsPath));

        var config = context.ServiceProvider.GetService<IConfiguration>();
        var (exePath, locatorError) = CodebaseMemoryLocator.Locate(config);

        bool dbExists = File.Exists(input.DatabasePath);
        bool hasStructuralNodes = dbExists && await HasStructuralGraphAsync(input.DatabasePath, cancellationToken);

        if (!dbExists || input.ForceReindex || !hasStructuralNodes)
        {
            if (exePath == null)
            {
                throw new InvalidOperationException(
                    $"[DetectAndIndexGraphStep] Fail-fast: {locatorError}. " +
                    $"La base de datos '{input.DatabasePath}' no existe o no contiene un grafo estructural válido (ni tipos ni funciones o métodos). " +
                    $"Asegúrate de instalar 'codebase-memory-mcp' o configurar 'McpServers:CodebaseMemory:Command' en appsettings.analysis.json.");
            }

            // La versión va a la huella de la indexación (H-9): sin ella, una versión de cbm que derive los QN de otra forma
            // solo se notaría por los nodos duplicados.
            var cbmVersion = await CodebaseMemoryLocator.GetVersionAsync(exePath, cancellationToken);

            // ForceReindex no destruye antes de construir (H-11): lo anterior sigue en su sitio hasta que la indexación
            // nueva termina, y si falla o se cancela se restaura desde la copia.
            var previous = input.ForceReindex ? BackupPreviousRun(input, context) : null;

            context.Report(StepName, AnalysisLogLevel.Info,
                $"🔍 Indexando repositorio '{input.RepositoryPath}' con CLI: {exePath} ({(cbmVersion != null ? $"versión {cbmVersion}" : "versión desconocida")})...");
            try
            {
                await RunCodebaseMemoryIndexAsync(context, exePath, input.RepositoryPath, input.DatabasePath, cancellationToken);
                await RecordCbmVersionAsync(context, input.DatabasePath, cbmVersion, cancellationToken);
                await RemovePipelineArtifactsFromGraphAsync(context, input, cancellationToken);
            }
            catch (Exception) when (previous != null)
            {
                previous.Restore(context);
                throw;
            }

            previous?.DiscardSuperseded(context);
        }
        else
        {
            context.Report(StepName, AnalysisLogLevel.Info, $"ℹ️ Grafo estructural detectado en '{input.DatabasePath}'. Omitiendo re-indexación inicial.");
        }

        // Ensure database directory and basic schema exist
        var dbDir = Path.GetDirectoryName(Path.GetFullPath(input.DatabasePath));
        if (!string.IsNullOrEmpty(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }

        await GraphSchema.EnsureAsync(input.DatabasePath, cancellationToken);

        // Con grafo nuevo o heredado: lo que cbm indexó de bin/, obj/ y los ficheros generados no es código del repositorio.
        await RemoveGeneratedArtifactsFromGraphAsync(context, input.DatabasePath, cancellationToken);

        // Check if graph already contains enriched nodes (e.g. CsProject or properties enriched by Roslyn/AI)
        bool isEnriched = !input.ForceReindex && await CheckIfEnrichedAsync(input.DatabasePath, cancellationToken);
        var (totalNodes, totalEdges) = await GetGraphCountsAsync(input.DatabasePath, cancellationToken);
        bool hasStructuralNodesPost = await HasStructuralGraphAsync(input.DatabasePath, cancellationToken);
        bool hasOfficeArtifacts = HasOfficeArtifacts(input.RepositoryPath);

        if (!hasStructuralNodesPost && !hasOfficeArtifacts)
        {
            throw new InvalidOperationException(
                $"[DetectAndIndexGraphStep] Fail-fast: El directorio '{input.RepositoryPath}' no contiene código fuente compatible " +
                $"(ni tipos ni funciones o métodos en el grafo) ni artefactos Office (.accdb, .docx). Se detiene el pipeline de forma controlada.");
        }

        if (hasOfficeArtifacts)
        {
            context.Report(StepName, AnalysisLogLevel.Info, $"[DetectAndIndexGraphStep] 📦 Ingestando artefactos de Office / Access (.accdb/.docx)...");
            var officeStep = new OfficeTools.Steps.IngestOfficeArtifactsStep();
            await ((IStep)officeStep).ExecuteAsync(new StepResult<string>(this, input.RepositoryPath), context, 1, null, cancellationToken);

            // Recalcular conteos de nodos y aristas tras la ingesta de tablas y documentos. La ingesta no marca el grafo
            // como enriquecido: un .docx en un repositorio .NET dejaba sin Roslyn todo el código (auditoría, H-10).
            (totalNodes, totalEdges) = await GetGraphCountsAsync(input.DatabasePath, cancellationToken);
            context.Report(StepName, AnalysisLogLevel.Info, $"[DetectAndIndexGraphStep] 📊 Grafo actualizado con Office: {totalNodes} nodos, {totalEdges} aristas.");
        }

        // Ingestar y enriquecer archivos Markdown (.md) y documentación del repositorio
        await WorkflowAndMarkdownEnricher.EnrichAsync(context, input.RepositoryPath, input.DatabasePath, input.OutputReportsPath, cancellationToken);
        (totalNodes, totalEdges) = await GetGraphCountsAsync(input.DatabasePath, cancellationToken);
        context.Report(StepName, AnalysisLogLevel.Info, $"[DetectAndIndexGraphStep] 📊 Grafo actualizado con Markdown: {totalNodes} nodos, {totalEdges} aristas.");

        if (await ReincorporateKnowledgeAsync(context, input, cancellationToken))
            (totalNodes, totalEdges) = await GetGraphCountsAsync(input.DatabasePath, cancellationToken);

        return new GraphStatusResult
        {
            ExistedBefore = dbExists && !input.ForceReindex,
            IsEnriched = isEnriched,
            TotalNodes = totalNodes,
            TotalEdges = totalEdges,
            DatabasePath = input.DatabasePath
        };
    }

    /// <summary>
    /// Plan v2 §8.1.4: tras la ingesta Markdown se reincorporan al grafo los documentos aportados y los informes propios
    /// marcados, para que los auditores los vean, y se retiran los informes del catálogo marcados (son la salida de la
    /// ejecución anterior; los reincorpora la síntesis con el contenido nuevo). Sin registro no hace nada ni dice nada.
    /// </summary>
    private static async Task<bool> ReincorporateKnowledgeAsync(PipelineContext context, AnalysisPipelineInput input, CancellationToken ct)
    {
        var paths = new Knowledge.KnowledgePaths(input.RepositoryPath, input.OutputReportsPath);
        if (new Knowledge.Graph.GraphSourceRegistry(paths).Sources().Count == 0)
            return false;

        var retired = await RetireCatalogReportsAsync(context, input, ct);
        var count = await Knowledge.Graph.KnowledgeGraphIngestor.ReincorporateAsync(input.DatabasePath, paths,
            new HashSet<Knowledge.SourceKind> { Knowledge.SourceKind.Document, Knowledge.SourceKind.Custom }, ct);
        if (count > 0)
            context.Report(StepName, AnalysisLogLevel.Info, $"🧩 Reincorporados al grafo {count} documentos aportados e informes propios.");
        return count > 0 || retired > 0;
    }

    /// <summary>
    /// Retira del grafo los informes del catálogo marcados y deja puesta <see cref="AnalysisContextKeys.CatalogReportsRetired"/>
    /// para que vuelvan en la síntesis o, si no se llega, en el <c>finally</c> de <see cref="CodebaseAnalysisPipeline"/>. Lo
    /// comparten la detección y la regeneración (<see cref="PrepareRegenerationStep"/>).
    /// </summary>
    internal static async Task<int> RetireCatalogReportsAsync(PipelineContext context, AnalysisPipelineInput input, CancellationToken ct)
    {
        var paths = new Knowledge.KnowledgePaths(input.RepositoryPath, input.OutputReportsPath);
        var retired = await Knowledge.Graph.KnowledgeGraphIngestor.RetireAsync(input.DatabasePath, paths, Knowledge.SourceKind.Report, ct);
        if (new Knowledge.Graph.GraphSourceRegistry(paths).Sources().Any(s => s.Kind == Knowledge.SourceKind.Report))
            context.SetMetadata(AnalysisContextKeys.CatalogReportsRetired, true);
        return retired;
    }

    /// <summary>Nombre de cada carpeta de <c>backups/</c>; la rotación solo toca las que lo siguen.</summary>
    internal const string BackupFolderFormat = "yyyyMMdd_HHmmss";

    /// <summary>
    /// ForceReindex parte de cero, pero lo anterior se conserva para poder comparar resultados: la BD (con sus
    /// <c>-wal</c>, <c>-shm</c> y <c>-journal</c>), la exportación de Access y los informes del pipeline se guardan en
    /// <c>&lt;carpeta de la BD&gt;/backups/&lt;yyyyMMdd_HHmmss&gt;/</c>, manteniendo su ruta relativa al repositorio. Cuando la
    /// BD está en <c>.codebase/</c>, los escáneres ya excluyen esa carpeta.
    /// </summary>
    /// <remarks>
    /// La BD y los informes se copian y siguen en su sitio mientras se indexa (H-11): si el indexador falla, la web no se
    /// queda sin grafo ni informes. Las carpetas <c>access_export</c> se mueven como antes, porque el indexador las leería
    /// como código y la ingesta de Office las regenera. El resultado sabe deshacerlo (<see cref="PreviousRunBackup.Restore"/>)
    /// y cerrarlo (<see cref="PreviousRunBackup.DiscardSuperseded"/>). Al terminar la copia se rotan las carpetas de
    /// <c>backups/</c> según <see cref="AnalysisPipelineInput.BackupsToKeep"/>.
    /// </remarks>
    internal static PreviousRunBackup BackupPreviousRun(AnalysisPipelineInput input, PipelineContext context)
    {
        var repoRoot = Path.GetFullPath(input.RepositoryPath);
        var dbPath = Path.GetFullPath(input.DatabasePath);
        var dbDir = Path.GetDirectoryName(dbPath)!;
        var backupsRoot = Path.Combine(dbDir, "backups");
        var backupDir = Path.Combine(backupsRoot, DateTime.Now.ToString(BackupFolderFormat, CultureInfo.InvariantCulture));

        string BackupTarget(string path)
        {
            var relative = Path.GetRelativePath(repoRoot, path);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? Path.Combine(backupDir, Path.GetFileName(path))
                : Path.Combine(backupDir, relative);
        }

        var backup = new PreviousRunBackup(backupDir, dbPath);

        // Las conexiones del pool mantienen abiertos la BD y su -wal: se cierran para copiar un estado estable.
        SqliteConnection.ClearAllPools();
        foreach (var file in DatabaseFiles(dbPath).Where(File.Exists))
        {
            CopyFile(file, BackupTarget(file));
            backup.DatabaseFiles.Add((file, BackupTarget(file)));
        }

        // OfficeTools exporta el código de cada .accdb/.mdb a <carpeta del fichero>/.codebase/access_export.
        var exportDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(dbDir, "access_export") };
        foreach (var accessFile in EnumerateRepositoryFiles(repoRoot).Where(IsAccessDatabase))
        {
            exportDirs.Add(Path.Combine(Path.GetDirectoryName(accessFile)!, ".codebase", "access_export"));
        }

        foreach (var exportDir in exportDirs.Where(Directory.Exists))
        {
            MoveDirectory(exportDir, BackupTarget(exportDir));
            backup.MovedDirectories.Add((exportDir, BackupTarget(exportDir)));
        }

        // Solo los informes que genera el pipeline, por nombre: OutputReportsPath es configurable y
        // vaciar la carpeta entera podría llevarse Markdown ajeno.
        if (!string.IsNullOrWhiteSpace(input.OutputReportsPath))
        {
            var reportNames = Enum.GetValues<ReportType>()
                .Select(AnalysisPipelineInput.GetReportFileName)
                .Append(AnalysisPipelineInput.ExecutiveReportFileName);

            foreach (var reportName in reportNames)
            {
                var reportPath = Path.GetFullPath(Path.Combine(input.OutputReportsPath, reportName));
                if (File.Exists(reportPath))
                {
                    CopyFile(reportPath, BackupTarget(reportPath));
                    backup.Reports.Add(reportPath);
                }
            }
        }

        var saved = backup.DatabaseFiles.Count + backup.MovedDirectories.Count + backup.Reports.Count;
        context.Report(StepName, AnalysisLogLevel.Info, saved > 0
            ? $"🗂️ ForceReindex: {saved} elementos de la ejecución anterior guardados en '{backupDir}'; la BD y los informes siguen en su sitio hasta que termine la indexación."
            : "ℹ️ ForceReindex: no había ejecución anterior que guardar.");

        var pruned = PruneBackups(backupsRoot, input.BackupsToKeep);
        if (pruned > 0)
            context.Report(StepName, AnalysisLogLevel.Info,
                $"🧹 ForceReindex: {pruned} {(pruned == 1 ? "copia antigua borrada" : "copias antiguas borradas")} de '{backupsRoot}' "
                + $"(se conservan las {input.BackupsToKeep} más recientes, CodebaseAnalysis:Backups:Keep).");
        return backup;
    }

    /// <summary>
    /// Borra de <paramref name="backupsRoot"/> las carpetas con nombre <see cref="BackupFolderFormat"/> más antiguas, hasta
    /// dejar <paramref name="keep"/>; con 0 o menos no borra ninguna. Otras carpetas no se tocan. Devuelve las borradas.
    /// </summary>
    internal static int PruneBackups(string backupsRoot, int keep)
    {
        if (keep <= 0 || !Directory.Exists(backupsRoot))
            return 0;

        var stale = Directory.EnumerateDirectories(backupsRoot)
            .Where(d => DateTime.TryParseExact(Path.GetFileName(d), BackupFolderFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .Skip(keep)
            .ToList();
        foreach (var dir in stale)
            Directory.Delete(dir, recursive: true);
        return stale.Count;
    }

    /// <summary>La BD y los ficheros que SQLite deja a su lado.</summary>
    private static IEnumerable<string> DatabaseFiles(string dbPath) =>
        new[] { "", "-wal", "-shm", "-journal" }.Select(suffix => dbPath + suffix);

    /// <summary>
    /// Lo que <see cref="BackupPreviousRun"/> guardó, para restaurarlo si la indexación falla o para retirar lo que la
    /// ejecución nueva sustituye si termina bien.
    /// </summary>
    internal sealed class PreviousRunBackup(string directory, string databasePath)
    {
        public string Directory { get; } = directory;

        /// <summary>BD y ficheros de SQLite copiados: original y copia.</summary>
        public List<(string Original, string Copy)> DatabaseFiles { get; } = [];

        /// <summary>Carpetas <c>access_export</c> movidas: original y destino.</summary>
        public List<(string Original, string Copy)> MovedDirectories { get; } = [];

        /// <summary>Informes del pipeline copiados, por su ruta original.</summary>
        public List<string> Reports { get; } = [];

        /// <summary>
        /// Deja la BD como estaba (la copia sobre lo que haya escrito el indexador, sin ficheros de SQLite que no sean
        /// suyos) y devuelve las carpetas movidas; los informes no se tocaron. Un fallo aquí se avisa y no tapa el de la
        /// indexación.
        /// </summary>
        public void Restore(PipelineContext context)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                foreach (var file in DetectAndIndexGraphStep.DatabaseFiles(databasePath).Where(File.Exists))
                    File.Delete(file);
                foreach (var (original, copy) in DatabaseFiles)
                    File.Copy(copy, original, overwrite: true);
                foreach (var (original, copy) in MovedDirectories.Where(d => !System.IO.Directory.Exists(d.Original)))
                    CopyDirectory(copy, original);

                context.Report(StepName, AnalysisLogLevel.Warning,
                    $"↩️ ForceReindex: la indexación no terminó; se ha restaurado la ejecución anterior desde '{Directory}'.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.Report(StepName, AnalysisLogLevel.Error,
                    $"❌ ForceReindex: la indexación no terminó y no se pudo restaurar la ejecución anterior ({ex.Message}); la copia está en '{Directory}'.");
            }
        }

        /// <summary>La indexación nueva terminó: los informes anteriores salen de su sitio y quedan solo en la copia.</summary>
        public void DiscardSuperseded(PipelineContext context)
        {
            foreach (var report in Reports.Where(File.Exists))
                File.Delete(report);
            if (Reports.Count > 0)
                context.Report(StepName, AnalysisLogLevel.Info,
                    $"🗂️ ForceReindex: indexación terminada; los {Reports.Count} informes anteriores quedan solo en '{Directory}'.");
        }
    }

    private static void CopyFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        Directory.CreateDirectory(target);
    }

    // Copia y borra en vez de Directory.Move, que falla si origen y destino están en volúmenes distintos.
    private static void MoveDirectory(string source, string target)
    {
        CopyDirectory(source, target);
        Directory.Delete(source, recursive: true);
    }

    private static IEnumerable<string> EnumerateRepositoryFiles(string repoRoot)
    {
        if (!Directory.Exists(repoRoot))
            return [];

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(repoRoot, "*", options)
            .Where(f => !RepositoryPathFilter.IsExcluded(repoRoot, f));
    }

    private static bool IsAccessDatabase(string path) =>
        path.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase);

    private static async Task RunCodebaseMemoryIndexAsync(PipelineContext context, string exePath, string repositoryPath, string targetDbPath, CancellationToken cancellationToken)
    {
        var normalizedRepoPath = Path.GetFullPath(repositoryPath).Replace('\\', '/');
        var jsonArgs = $"{{\"repo_path\": \"{normalizedRepoPath}\"}}";

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("cli");
        startInfo.ArgumentList.Add("index_repository");
        startInfo.ArgumentList.Add(jsonArgs);

        using var process = new Process { StartInfo = startInfo };

        // El indexador informa del proyecto que ha creado, y su caché se llama igual. Deducir ese
        // nombre de la ruta recibida fallaba cuando la ruta usa nombres cortos 8.3 (por ejemplo, `NOMBRE~1` en lugar de la carpeta del usuario): el
        // indexador la resuelve al nombre largo, no se encontraba la caché y el grafo quedaba vacío.
        string? reportedProject = null;
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                context.Report(StepName, AnalysisLogLevel.Info, $"   [indexador] {e.Data}", AnalysisEventTypes.Channels.Indexer);
                reportedProject ??= TryReadProjectName(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                context.Report(StepName, AnalysisLogLevel.Info, $"   [indexador-err] {e.Data}", AnalysisEventTypes.Channels.Indexer);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"No se pudo iniciar el proceso de indexación: {exePath}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await ProcessCancellation.WaitForExitOrKillAsync(process, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"El indexador codebase-memory finalizó con código de error {process.ExitCode}.");
        }

        context.Report(StepName, AnalysisLogLevel.Success, $"✅ Indexación estructural de codebase-memory completada.");

        // Buscar el archivo .db generado en el caché de codebase-memory-mcp y sincronizarlo
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheDir = Path.Combine(userProfile, ".cache", "codebase-memory-mcp");
        var sanitizedName = reportedProject ?? normalizedRepoPath.Trim('/').Replace('/', '-').Replace(':', '-');
        var expectedCacheDb = Path.Combine(cacheDir, $"{sanitizedName}.db");
        if (!File.Exists(expectedCacheDb))
        {
            throw new InvalidOperationException(
                $"El indexador terminó sin error, pero no se encuentra su base de datos en '{expectedCacheDb}'.");
        }

        if (File.Exists(expectedCacheDb) && !string.Equals(Path.GetFullPath(expectedCacheDb), Path.GetFullPath(targetDbPath), StringComparison.OrdinalIgnoreCase))
        {
            var targetDir = Path.GetDirectoryName(Path.GetFullPath(targetDbPath));
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
            // Con ForceReindex la BD anterior sigue en su sitio: su -wal o su -journal no pueden aplicarse sobre la nueva.
            SqliteConnection.ClearAllPools();
            foreach (var sidecar in DatabaseFiles(Path.GetFullPath(targetDbPath)).Skip(1).Where(File.Exists))
                File.Delete(sidecar);
            File.Copy(expectedCacheDb, targetDbPath, overwrite: true);
            context.Report(StepName, AnalysisLogLevel.Info, $"📋 Sincronizada base de datos SQLite ({new FileInfo(targetDbPath).Length / 1024} KB) hacia: {targetDbPath}");
        }
    }

    /// <summary>
    /// El indexador recorre todo el repositorio, incluida la salida del propio pipeline: los informes
    /// de <c>reports/</c> y las copias de <c>.codebase/backups/</c> acababan como nodos del grafo y
    /// se auditaban en la siguiente pasada. Se quitan aquí, por ruta, para no depender de las reglas
    /// de exclusión del indexador ni escribir ficheros de configuración en el repositorio analizado.
    /// </summary>
    private static async Task RemovePipelineArtifactsFromGraphAsync(PipelineContext context, AnalysisPipelineInput input, CancellationToken ct)
    {
        var repoRoot = Path.GetFullPath(input.RepositoryPath);
        // <repo>/.codebase también, por si DatabasePath está en otro sitio: ahí viven documentos, historial y conocimiento (plan v2 §4.1).
        var prefixes = new[] { Path.GetDirectoryName(Path.GetFullPath(input.DatabasePath)), input.OutputReportsPath, Path.Combine(repoRoot, Knowledge.KnowledgePaths.CodebaseFolderName) }
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.GetRelativePath(repoRoot, Path.GetFullPath(d!)).Replace('\\', '/'))
            // Una carpeta fuera del repositorio no la indexa nadie; "." sería el repositorio entero.
            .Where(rel => rel != "." && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (prefixes.Count == 0 || !File.Exists(input.DatabasePath))
            return;

        using var conn = new SqliteConnection($"Data Source={input.DatabasePath}");
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();

        var removed = 0;
        foreach (var prefix in prefixes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                CREATE TEMP TABLE IF NOT EXISTS artifact_ids(id INTEGER PRIMARY KEY);
                DELETE FROM artifact_ids;
                INSERT INTO artifact_ids
                    SELECT id FROM nodes
                    WHERE replace(file_path, '\', '/') = @exact
                       OR replace(file_path, '\', '/') LIKE @prefix || '/%' ESCAPE '!';
                DELETE FROM edges WHERE source_id IN (SELECT id FROM artifact_ids) OR target_id IN (SELECT id FROM artifact_ids);
                DELETE FROM nodes WHERE id IN (SELECT id FROM artifact_ids);
                SELECT COUNT(1) FROM artifact_ids;";
            cmd.Parameters.AddWithValue("@exact", prefix);
            cmd.Parameters.AddWithValue("@prefix", prefix.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_"));
            removed += Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        await tx.CommitAsync(ct);
        if (removed > 0)
        {
            context.Report(StepName, AnalysisLogLevel.Info, $"🧹 Retirados del grafo {removed} nodos de la salida del propio pipeline ({string.Join(", ", prefixes)}).");
        }
    }

    /// <summary>
    /// cbm indexa la salida de compilación (los .json y .xml de <c>bin/</c> daban miles de nodos) y los ficheros generados;
    /// falseaban totales, métricas, ciclos y comunidades. Se retiran por ruta (<see cref="Enrichment.GeneratedArtifacts"/>),
    /// sin un <c>.cbmignore</c>, porque no se escribe en el repositorio analizado. Es idempotente y corre también sobre un
    /// grafo que ya existía, para que las pasadas siguientes (enriquecedores incluidos) no lo vean.
    /// </summary>
    private static async Task RemoveGeneratedArtifactsFromGraphAsync(PipelineContext context, string dbPath, CancellationToken ct)
    {
        if (!File.Exists(dbPath))
            return;

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var (nodes, edges) = await Enrichment.GeneratedArtifacts.RemoveFromGraphAsync(conn, tx, ct);
        await tx.CommitAsync(ct);

        if (nodes > 0)
            context.Report(StepName, AnalysisLogLevel.Info,
                $"🧹 Retirados del grafo {nodes} nodos de salida de compilación y ficheros generados (bin/, obj/, *.g.cs…) y {edges} aristas.");
    }

    /// <summary>
    /// Apunta la versión de cbm de esta indexación (<see cref="Enrichment.EnrichmentRuns.RecordCbmVersionAsync"/>), que entra
    /// en la huella de los enriquecedores. Sin versión lo avisa: la huella queda como antes, con el nombre y la fecha.
    /// </summary>
    private static async Task RecordCbmVersionAsync(PipelineContext context, string dbPath, string? cbmVersion, CancellationToken ct)
    {
        if (cbmVersion == null)
        {
            context.Report(StepName, AnalysisLogLevel.Warning,
                "⚠️ No se pudo leer la versión de codebase-memory-mcp (--version): la huella de la indexación queda sin ella.");
            return;
        }
        if (!File.Exists(dbPath))
            return;

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);
        await Enrichment.EnrichmentRuns.RecordCbmVersionAsync(conn, cbmVersion, ct);
    }

    private static string? TryReadProjectName(string line)
    {
        if (!line.TrimStart().StartsWith('{'))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("project", out var project) && project.ValueKind == System.Text.Json.JsonValueKind.String
                ? project.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal static async Task<bool> HasStructuralGraphAsync(string dbPath, CancellationToken ct)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            // Sin 'DocSection': esos nodos los inserta el propio pipeline desde los .md del repositorio,
            // así que una BD con solo documentación se tomaba por grafo estructural y no se reindexaba.
            // Funciones y métodos cuentan como código: con solo tipos, un repositorio de funciones
            // (Python, Go, C, JavaScript) se rechazaba como si no tuviera código fuente.
            cmd.CommandText = @"SELECT COUNT(1) FROM nodes WHERE label IN ('Class', 'Interface', 'Struct', 'Record', 'Enum', 'Trait',
                                'Function', 'Method', 'CsProject', 'AccessTable', 'AccessQuery') LIMIT 1;";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasOfficeArtifacts(string path) =>
        EnumerateRepositoryFiles(Path.GetFullPath(path))
            .Any(f => IsAccessDatabase(f) || f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>origin</c> de los nodos que escribe la ingesta de OfficeTools: sus procedimientos VBA traen <c>cyclomatic</c>, pero
    /// no son el enriquecimiento de código de <see cref="DotNetEnricherStep"/>.
    /// </summary>
    internal static readonly string[] OfficeOrigins = ["office_tools", "access_vba_extractor"];

    /// <summary>
    /// Si el grafo ya tiene el enriquecimiento de código de <see cref="DotNetEnricherStep"/> (Roslyn): nodos <c>CsProject</c> o
    /// con <c>afferent_coupling</c>, <c>cyclomatic</c> o <c>in_cycle</c>. No cuentan los de la ingesta de Office ni los del
    /// analizador Java, que calculan también <c>cyclomatic</c> sobre otro código y llevan su propio registro en
    /// <c>enrichment_runs</c>: con ellos, un repositorio .NET con un .docx o con Java se quedaba sin Roslyn (auditoría, H-10).
    /// Tampoco los que creó el fallback del LLM (<c>origin = ai_enricher</c>): su salida es una estimación, ya no escribe
    /// métricas, y marcaba el grafo como enriquecido hasta un <c>ForceReindex</c> (auditoría, H-3).
    /// </summary>
    internal static async Task<bool> CheckIfEnrichedAsync(string dbPath, CancellationToken ct)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT COUNT(1) FROM nodes
                WHERE label = 'CsProject'
                   OR ((json_extract(properties, '$.afferent_coupling') IS NOT NULL
                        OR json_extract(properties, '$.cyclomatic') IS NOT NULL
                        OR json_extract(properties, '$.in_cycle') IS NOT NULL)
                       AND coalesce(json_extract(properties, '$.origin'), '') NOT IN ({string.Join(", ", OfficeOrigins.Append(SemanticEnrichmentStep.OriginAiEnricher).Select(o => $"'{o}'"))})
                       AND NOT {Enrichment.EnrichmentMarks.EnrichedByPredicate($"'{Enrichment.Java.JavaGraphWriter.EnricherName}'")})
                LIMIT 1;";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    internal static async Task<(int TotalNodes, int TotalEdges)> GetGraphCountsAsync(string dbPath, CancellationToken ct)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            await conn.OpenAsync(ct);

            using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(1) FROM nodes;";
            var nodes = Convert.ToInt32(await cmd1.ExecuteScalarAsync(ct));

            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "SELECT COUNT(1) FROM edges;";
            var edges = Convert.ToInt32(await cmd2.ExecuteScalarAsync(ct));

            return (nodes, edges);
        }
        catch
        {
            return (0, 0);
        }
    }

    public Task FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult((result is GraphStatusResult, (string?)null));
}
