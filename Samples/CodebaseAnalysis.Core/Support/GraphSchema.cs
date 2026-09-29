using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Esquema mínimo que el pipeline necesita en la BD del grafo: crea <c>nodes</c> y <c>edges</c> si no
/// existen, añade las columnas de compatibilidad que falten y rellena las duplicadas. Es idempotente,
/// y es público para que los tests creen un grafo con el mismo DDL que usa el pipeline.
/// </summary>
public static class GraphSchema
{
    public static async Task EnsureAsync(string dbPath, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS nodes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project TEXT DEFAULT '',
                project_id INTEGER DEFAULT 1,
                label TEXT NOT NULL,
                name TEXT NOT NULL,
                qualified_name TEXT NOT NULL,
                file_path TEXT DEFAULT '',
                file TEXT DEFAULT '',
                start_line INTEGER DEFAULT 0,
                end_line INTEGER DEFAULT 0,
                line_start INTEGER DEFAULT 0,
                line_end INTEGER DEFAULT 0,
                properties TEXT DEFAULT '{}',
                UNIQUE(project, qualified_name)
            );

            CREATE TABLE IF NOT EXISTS edges (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project TEXT DEFAULT '',
                project_id INTEGER DEFAULT 1,
                source_id INTEGER NOT NULL,
                target_id INTEGER NOT NULL,
                type TEXT NOT NULL,
                properties TEXT DEFAULT '{}',
                UNIQUE(source_id, target_id, type)
            );

            CREATE INDEX IF NOT EXISTS idx_nodes_label ON nodes(label);
            CREATE INDEX IF NOT EXISTS idx_nodes_qname ON nodes(qualified_name);
            CREATE INDEX IF NOT EXISTS idx_edges_type ON edges(type);
        ";
        await cmd.ExecuteNonQueryAsync(ct);

        // Defensive column migrations if database was initialized by codebase-memory-mcp or older schemas
        var alterStatements = new[]
        {
            "ALTER TABLE nodes ADD COLUMN project TEXT DEFAULT '';",
            "ALTER TABLE nodes ADD COLUMN project_id INTEGER DEFAULT 1;",
            "ALTER TABLE nodes ADD COLUMN file TEXT DEFAULT '';",
            "ALTER TABLE nodes ADD COLUMN file_path TEXT DEFAULT '';",
            "ALTER TABLE nodes ADD COLUMN line_start INTEGER DEFAULT 0;",
            "ALTER TABLE nodes ADD COLUMN line_end INTEGER DEFAULT 0;",
            "ALTER TABLE nodes ADD COLUMN start_line INTEGER DEFAULT 0;",
            "ALTER TABLE nodes ADD COLUMN end_line INTEGER DEFAULT 0;",
            "ALTER TABLE edges ADD COLUMN project TEXT DEFAULT '';",
            "ALTER TABLE edges ADD COLUMN project_id INTEGER DEFAULT 1;"
        };

        foreach (var sql in alterStatements)
        {
            try
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = sql;
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
            catch
            {
                // Column already exists
            }
        }

        // Defensive unique indexes to ensure compatibility with all ON CONFLICT targets across steps
        var indexStatements = new[]
        {
            "CREATE UNIQUE INDEX IF NOT EXISTS uq_nodes_project_qname ON nodes(project, qualified_name);",
            "CREATE UNIQUE INDEX IF NOT EXISTS uq_nodes_projid_qname ON nodes(project_id, qualified_name);",
            "CREATE UNIQUE INDEX IF NOT EXISTS uq_edges_src_tgt_type ON edges(source_id, target_id, type);",
            "CREATE UNIQUE INDEX IF NOT EXISTS uq_edges_projid_src_tgt_type ON edges(project_id, source_id, target_id, type);"
        };

        foreach (var sql in indexStatements)
        {
            try
            {
                using var idxCmd = conn.CreateCommand();
                idxCmd.CommandText = sql;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }
            catch
            {
                // Index already exists or constraint already satisfied
            }
        }

        // Backfill compatibility columns
        try
        {
            using var syncCmd = conn.CreateCommand();
            syncCmd.CommandText = @"
                UPDATE nodes SET file = file_path WHERE (file IS NULL OR file = '') AND file_path IS NOT NULL AND file_path != '';
                UPDATE nodes SET file_path = file WHERE (file_path IS NULL OR file_path = '') AND file IS NOT NULL AND file != '';
                UPDATE nodes SET line_start = start_line WHERE (line_start IS NULL OR line_start = 0) AND start_line IS NOT NULL AND start_line != 0;
                UPDATE nodes SET start_line = line_start WHERE (start_line IS NULL OR start_line = 0) AND line_start IS NOT NULL AND line_start != 0;
                UPDATE nodes SET line_end = end_line WHERE (line_end IS NULL OR line_end = 0) AND end_line IS NOT NULL AND end_line != 0;
                UPDATE nodes SET end_line = line_end WHERE (end_line IS NULL OR end_line = 0) AND line_end IS NOT NULL AND line_end != 0;
                UPDATE nodes SET project = 'Codebase' WHERE project IS NULL OR project = '';
                UPDATE edges SET project = 'Codebase' WHERE project IS NULL OR project = '';
            ";
            await syncCmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Silently ignore if source columns are absent
        }

        // cbm deja en algunos nodos un `properties` que no es JSON: lo corta en 2047 bytes o no escapa los saltos de
        // línea de `base_classes` (35 nodos de 288 527 en un repositorio Java de 8 420 ficheros). Basta uno para que
        // json_extract aborte la consulta entera, y la mayoría de lecturas del grafo no pasan por json_valid: la
        // extracción de facetas fallaba y las consultas protegidas por QueryToJsonAsync llegaban como «Faceta no
        // disponible». Se conserva el texto original bajo cbm_raw_properties, igual que EnrichmentMarks.ParseProperties.
        using var repairCmd = conn.CreateCommand();
        repairCmd.CommandText = $@"
            UPDATE nodes SET properties = '{{}}' WHERE properties IS NOT NULL AND trim(properties) = '';
            UPDATE nodes SET properties = json_object('{Enrichment.EnrichmentMarks.RawPropertiesKey}', properties)
                WHERE properties IS NOT NULL AND json_valid(properties) = 0;
        ";
        await repairCmd.ExecuteNonQueryAsync(ct);
    }
}
