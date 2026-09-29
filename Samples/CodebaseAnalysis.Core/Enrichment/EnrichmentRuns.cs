using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>Recuentos de una pasada de un enriquecedor; son las columnas de recuento de <c>enrichment_runs</c>.</summary>
public sealed record EnrichmentOutcome
{
    public int FilesAnalyzed { get; init; }

    /// <summary>Ficheros analizados sin ningún nodo en el grafo de cbm.</summary>
    public int FilesUnmatched { get; init; }

    public int NodesCreated { get; init; }

    /// <summary>Nodos de cbm cuyas propiedades se han completado.</summary>
    public int NodesUpdated { get; init; }

    /// <summary>Nodos de cbm retirados (código de test).</summary>
    public int NodesRemoved { get; init; }

    /// <summary>Aristas de cbm borradas para sustituirlas por las resueltas.</summary>
    public int CbmEdgesReplaced { get; init; }

    public int EdgesCreated { get; init; }

    /// <summary>JSON: llamadas sin resolver agrupadas por motivo.</summary>
    public string? Unresolved { get; init; }

    /// <summary>JSON: diagnósticos de la pasada.</summary>
    public string? Diagnostics { get; init; }

    /// <summary>JSON: detalle propio de cada enriquecedor.</summary>
    public string? Details { get; init; }

    /// <summary>Id de la fila de <c>enrichment_runs</c> que registró la pasada, si la registró quien la escribió.</summary>
    public long? RunId { get; init; }
}

/// <summary>Una fila de <c>enrichment_runs</c>.</summary>
public sealed record EnrichmentRun
{
    public long Id { get; init; }
    public required string Enricher { get; init; }
    public required string EnricherVersion { get; init; }

    /// <summary>Versión de la herramienta externa (p. ej. la del JDK), si la hay.</summary>
    public string? ToolVersion { get; init; }

    public required string IndexFingerprint { get; init; }
    public required string ConfigFingerprint { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public long DurationMs { get; init; }

    /// <summary><see cref="EnrichmentRuns.StatusOk"/>, <see cref="EnrichmentRuns.StatusFailed"/> o <see cref="EnrichmentRuns.StatusSkipped"/>.</summary>
    public required string Status { get; init; }

    /// <summary>Recuentos; null en una pasada que no llegó a escribir.</summary>
    public EnrichmentOutcome? Outcome { get; init; }

    public string? Message { get; init; }
}

/// <summary>
/// Registro de las pasadas de los enriquecedores sobre la copia del grafo (D8, P3). Una pasada correcta con el mismo
/// enriquecedor, versión, huella de la indexación y huella de configuración permite saltarse la siguiente.
/// </summary>
public static class EnrichmentRuns
{
    public const string StatusOk = "ok";
    public const string StatusFailed = "failed";
    public const string StatusSkipped = "skipped";

    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS enrichment_runs(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            enricher TEXT NOT NULL,
            enricher_version TEXT NOT NULL,
            tool_version TEXT,
            index_fingerprint TEXT NOT NULL,
            config_fingerprint TEXT NOT NULL,
            started_at TEXT NOT NULL,
            duration_ms INTEGER NOT NULL,
            status TEXT NOT NULL,
            files_analyzed INTEGER,
            files_unmatched INTEGER,
            nodes_created INTEGER,
            nodes_updated INTEGER,
            nodes_removed INTEGER,
            cbm_edges_replaced INTEGER,
            edges_created INTEGER,
            unresolved TEXT,
            diagnostics TEXT,
            details TEXT,
            message TEXT)
        """;

    public static async Task EnsureAsync(SqliteConnection conn, CancellationToken ct, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = CreateTableSql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Huella de la indexación de cbm: <c>projects.name|projects.indexed_at</c> del proyecto de los nodos (o de la primera
    /// fila si no casa ninguna). Null si la BD no tiene la tabla <c>projects</c> o está vacía: entonces no hay forma de
    /// saber si la indexación cambió y el enriquecedor se ejecuta siempre.
    /// </summary>
    public static async Task<string?> IndexFingerprintAsync(SqliteConnection conn, CancellationToken ct, SqliteTransaction? tx = null)
    {
        if (!await TableExistsAsync(conn, "projects", ct, tx))
            return null;

        var project = await GraphProject.ResolveAsync(conn, "", ct, tx);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT name || '|' || indexed_at FROM projects
            ORDER BY CASE WHEN name = @project THEN 0 ELSE 1 END, name
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@project", project);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>SHA-256 en hexadecimal de los valores que cambian el resultado de un enriquecedor, en orden.</summary>
    public static string ConfigFingerprint(IEnumerable<string?> values)
    {
        // El separador no puede aparecer en rutas ni opciones, así ["a", "bc"] y ["ab", "c"] no dan la misma huella.
        var text = string.Join("\u001f", values.Select(v => v ?? "\u0000"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static async Task<bool> HasSuccessfulRunAsync(
        SqliteConnection conn,
        string enricher,
        string enricherVersion,
        string? indexFingerprint,
        string configFingerprint,
        CancellationToken ct,
        SqliteTransaction? tx = null)
    {
        if (indexFingerprint == null || !await TableExistsAsync(conn, "enrichment_runs", ct, tx))
            return false;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT 1 FROM enrichment_runs
            WHERE enricher = @enricher AND enricher_version = @version
              AND index_fingerprint = @index AND config_fingerprint = @config AND status = 'ok'
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@enricher", enricher);
        cmd.Parameters.AddWithValue("@version", enricherVersion);
        cmd.Parameters.AddWithValue("@index", indexFingerprint);
        cmd.Parameters.AddWithValue("@config", configFingerprint);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    /// <summary>Id de la última fila <c>ok</c> del enriquecedor, o null si no hay ninguna (o no existe la tabla).</summary>
    public static async Task<long?> LastSuccessfulRunIdAsync(SqliteConnection conn, string enricher, CancellationToken ct, SqliteTransaction? tx = null)
    {
        if (!await TableExistsAsync(conn, "enrichment_runs", ct, tx))
            return null;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT MAX(id) FROM enrichment_runs WHERE enricher = @enricher AND status = 'ok'";
        cmd.Parameters.AddWithValue("@enricher", enricher);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    /// <summary>Inserta la fila (creando la tabla si falta) y devuelve su id.</summary>
    public static async Task<long> InsertAsync(SqliteConnection conn, EnrichmentRun run, CancellationToken ct, SqliteTransaction? tx = null)
    {
        await EnsureAsync(conn, ct, tx);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO enrichment_runs(
                enricher, enricher_version, tool_version, index_fingerprint, config_fingerprint, started_at, duration_ms,
                status, files_analyzed, files_unmatched, nodes_created, nodes_updated, nodes_removed, cbm_edges_replaced,
                edges_created, unresolved, diagnostics, details, message)
            VALUES (
                @enricher, @version, @tool, @index, @config, @started, @duration,
                @status, @filesAnalyzed, @filesUnmatched, @nodesCreated, @nodesUpdated, @nodesRemoved, @cbmEdgesReplaced,
                @edgesCreated, @unresolved, @diagnostics, @details, @message);
            SELECT last_insert_rowid();
            """;
        var outcome = run.Outcome;
        cmd.Parameters.AddWithValue("@enricher", run.Enricher);
        cmd.Parameters.AddWithValue("@version", run.EnricherVersion);
        cmd.Parameters.AddWithValue("@tool", (object?)run.ToolVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@index", run.IndexFingerprint);
        cmd.Parameters.AddWithValue("@config", run.ConfigFingerprint);
        cmd.Parameters.AddWithValue("@started", run.StartedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@duration", run.DurationMs);
        cmd.Parameters.AddWithValue("@status", run.Status);
        cmd.Parameters.AddWithValue("@filesAnalyzed", (object?)outcome?.FilesAnalyzed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filesUnmatched", (object?)outcome?.FilesUnmatched ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nodesCreated", (object?)outcome?.NodesCreated ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nodesUpdated", (object?)outcome?.NodesUpdated ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nodesRemoved", (object?)outcome?.NodesRemoved ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cbmEdgesReplaced", (object?)outcome?.CbmEdgesReplaced ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@edgesCreated", (object?)outcome?.EdgesCreated ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@unresolved", (object?)outcome?.Unresolved ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@diagnostics", (object?)outcome?.Diagnostics ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@details", (object?)outcome?.Details ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@message", (object?)run.Message ?? DBNull.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Filas de un enriquecedor (o de todos), de la más antigua a la más reciente.</summary>
    public static async Task<IReadOnlyList<EnrichmentRun>> ListAsync(SqliteConnection conn, CancellationToken ct, string? enricher = null, SqliteTransaction? tx = null)
    {
        if (!await TableExistsAsync(conn, "enrichment_runs", ct, tx))
            return [];

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, enricher, enricher_version, tool_version, index_fingerprint, config_fingerprint, started_at,
                   duration_ms, status, files_analyzed, files_unmatched, nodes_created, nodes_updated, nodes_removed,
                   cbm_edges_replaced, edges_created, unresolved, diagnostics, details, message
            FROM enrichment_runs
            WHERE @enricher IS NULL OR enricher = @enricher
            ORDER BY id
            """;
        cmd.Parameters.AddWithValue("@enricher", (object?)enricher ?? DBNull.Value);

        var runs = new List<EnrichmentRun>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int? Int(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

            var hasOutcome = !reader.IsDBNull(9);
            runs.Add(new EnrichmentRun
            {
                Id = reader.GetInt64(0),
                Enricher = reader.GetString(1),
                EnricherVersion = reader.GetString(2),
                ToolVersion = Text(3),
                IndexFingerprint = reader.GetString(4),
                ConfigFingerprint = reader.GetString(5),
                StartedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                DurationMs = reader.GetInt64(7),
                Status = reader.GetString(8),
                Outcome = hasOutcome
                    ? new EnrichmentOutcome
                    {
                        FilesAnalyzed = Int(9) ?? 0,
                        FilesUnmatched = Int(10) ?? 0,
                        NodesCreated = Int(11) ?? 0,
                        NodesUpdated = Int(12) ?? 0,
                        NodesRemoved = Int(13) ?? 0,
                        CbmEdgesReplaced = Int(14) ?? 0,
                        EdgesCreated = Int(15) ?? 0,
                        Unresolved = Text(16),
                        Diagnostics = Text(17),
                        Details = Text(18),
                        RunId = reader.GetInt64(0)
                    }
                    : null,
                Message = Text(19)
            });
        }
        return runs;
    }

    internal static async Task<bool> TableExistsAsync(SqliteConnection conn, string table, CancellationToken ct, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", table);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }
}
