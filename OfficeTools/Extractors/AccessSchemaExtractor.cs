using System.Data;
using System.Data.OleDb;
using OfficeTools.Abstractions;
using OfficeTools.Models;

namespace OfficeTools.Extractors;

/// <summary>
/// Extractor for Microsoft Access database schemas (.accdb / .mdb) using OleDb.
/// </summary>
public class AccessSchemaExtractor : IAccessSchemaExtractor
{
    private const string PrimaryProvider = "Microsoft.ACE.OLEDB.12.0";
    private const string FallbackProvider = "Microsoft.ACE.OLEDB.16.0";

    /// <inheritdoc />
    public async Task<AccessDatabaseSchema> ExtractSchemaAsync(string filePath, string? password = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Run OleDb synchronous COM operations on a background thread
        return await Task.Run(() => ExtractSchema(filePath, password), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public AccessDatabaseSchema ExtractSchema(string filePath, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must not be null or empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Access database file not found at: '{filePath}'.", filePath);

        using var connection = CreateAndOpenConnection(filePath, password);

        var schema = new AccessDatabaseSchema
        {
            FilePath = Path.GetFullPath(filePath),
            DatabaseName = Path.GetFileName(filePath)
        };

        // 1. Extract tables
        var userTables = ExtractTables(connection);
        schema.Tables = userTables;

        // 2. Extract foreign keys
        schema.ForeignKeys = ExtractForeignKeys(connection);

        // 3. Extract queries / views
        schema.Queries = ExtractQueries(connection);

        return schema;
    }

    /// <inheritdoc />
    public bool CanConnect(string filePath, string? password, out string? errorMessage)
    {
        errorMessage = null;

        if (!OperatingSystem.IsWindows())
        {
            errorMessage = "Microsoft Access OleDb provider is only supported on Windows.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            errorMessage = $"Database file does not exist at: '{filePath}'.";
            return false;
        }

        try
        {
            using var conn = CreateAndOpenConnection(filePath, password);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static OleDbConnection CreateAndOpenConnection(string filePath, string? password)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Microsoft Access OleDb provider is only supported on Windows.");
        }

        string[] providersToTry = [PrimaryProvider, FallbackProvider];
        Exception? lastException = null;

        foreach (var provider in providersToTry)
        {
            var builder = new OleDbConnectionStringBuilder
            {
                Provider = provider,
                DataSource = filePath,
                PersistSecurityInfo = false
            };

            if (!string.IsNullOrEmpty(password))
            {
                builder["Jet OLEDB:Database Password"] = password;
            }

            try
            {
                var conn = new OleDbConnection(builder.ConnectionString);
                conn.Open();
                return conn;
            }
            catch (OleDbException ex)
            {
                lastException = ex;
                // If provider is not registered or not found, try next provider
                if (ex.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase) ||
                    ex.ErrorCode == unchecked((int)0x80040154)) // REGDB_E_CLASSNOTREG
                {
                    continue;
                }

                // If error is authentication or database-specific, rethrow with context
                throw new InvalidOperationException($"Failed to open Access database '{filePath}' using provider '{provider}': {ex.Message}", ex);
            }
            catch (PlatformNotSupportedException ex)
            {
                throw new PlatformNotSupportedException($"OLE DB is not supported on this platform: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw new InvalidOperationException(
            $"Neither '{PrimaryProvider}' nor '{FallbackProvider}' OLE DB providers are registered or available on this system. " +
            "Please ensure Microsoft Access Database Engine (ACE) redistributable is installed. " +
            $"Last error: {lastException?.Message}",
            lastException);
    }

    private static List<TableInfo> ExtractTables(OleDbConnection connection)
    {
        var tables = new List<TableInfo>();

        DataTable? tablesSchema;
        try
        {
            tablesSchema = connection.GetSchema("Tables");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to retrieve 'Tables' schema: {ex.Message}", ex);
        }

        if (tablesSchema == null)
            return tables;

        var userTableNames = new List<string>();

        foreach (DataRow row in tablesSchema.Rows)
        {
            string? tableName = row["TABLE_NAME"]?.ToString();
            string? tableType = row["TABLE_TYPE"]?.ToString();

            if (string.IsNullOrEmpty(tableName))
                continue;

            // Filter out system and temp tables
            if (string.Equals(tableType, "TABLE", StringComparison.OrdinalIgnoreCase) &&
                !tableName.StartsWith("MSys", StringComparison.OrdinalIgnoreCase) &&
                !tableName.StartsWith("~", StringComparison.OrdinalIgnoreCase))
            {
                userTableNames.Add(tableName);
            }
        }

        foreach (var tableName in userTableNames.OrderBy(t => t))
        {
            var tableInfo = new TableInfo { Name = tableName };

            // Extract columns
            tableInfo.Columns = ExtractColumns(connection, tableName);

            // Extract indexes
            tableInfo.Indexes = ExtractIndexes(connection, tableName);

            // Extract primary keys
            var pkIndexes = tableInfo.Indexes.Where(i => i.IsPrimaryKey).ToList();
            if (pkIndexes.Count > 0)
            {
                foreach (var pkIndex in pkIndexes)
                {
                    foreach (var col in pkIndex.Columns)
                    {
                        if (!tableInfo.PrimaryKeys.Contains(col))
                            tableInfo.PrimaryKeys.Add(col);
                    }
                }
            }

            tables.Add(tableInfo);
        }

        return tables;
    }

    private static List<ColumnInfo> ExtractColumns(OleDbConnection connection, string tableName)
    {
        var columns = new List<ColumnInfo>();

        try
        {
            var columnsSchema = connection.GetSchema("Columns", [null, null, tableName, null]);
            if (columnsSchema == null)
                return columns;

            foreach (DataRow row in columnsSchema.Rows)
            {
                string columnName = row["COLUMN_NAME"]?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(columnName))
                    continue;

                int dataTypeInt = row["DATA_TYPE"] != DBNull.Value ? Convert.ToInt32(row["DATA_TYPE"]) : 0;
                string typeName = Enum.IsDefined(typeof(OleDbType), dataTypeInt)
                    ? ((OleDbType)dataTypeInt).ToString()
                    : $"OleDbType({dataTypeInt})";

                int? size = null;
                if (row.Table.Columns.Contains("CHARACTER_MAXIMUM_LENGTH") && row["CHARACTER_MAXIMUM_LENGTH"] != DBNull.Value)
                {
                    size = Convert.ToInt32(row["CHARACTER_MAXIMUM_LENGTH"]);
                }

                bool isNullable = true;
                if (row.Table.Columns.Contains("IS_NULLABLE") && row["IS_NULLABLE"] != DBNull.Value)
                {
                    object nullVal = row["IS_NULLABLE"];
                    if (nullVal is bool b)
                        isNullable = b;
                    else if (bool.TryParse(nullVal.ToString(), out bool parsedBool))
                        isNullable = parsedBool;
                    else if (string.Equals(nullVal.ToString(), "NO", StringComparison.OrdinalIgnoreCase))
                        isNullable = false;
                }

                int ordinal = 0;
                if (row.Table.Columns.Contains("ORDINAL_POSITION") && row["ORDINAL_POSITION"] != DBNull.Value)
                {
                    ordinal = Convert.ToInt32(row["ORDINAL_POSITION"]);
                }

                string? defaultValue = null;
                if (row.Table.Columns.Contains("COLUMN_DEFAULT") && row["COLUMN_DEFAULT"] != DBNull.Value)
                {
                    defaultValue = row["COLUMN_DEFAULT"]?.ToString();
                }

                columns.Add(new ColumnInfo
                {
                    Name = columnName,
                    Type = typeName,
                    Size = size,
                    Nullable = isNullable,
                    OrdinalPosition = ordinal,
                    DefaultValue = defaultValue
                });
            }
        }
        catch
        {
            // Defensive fallback: return any columns extracted so far
        }

        return columns.OrderBy(c => c.OrdinalPosition).ToList();
    }

    private static List<IndexInfo> ExtractIndexes(OleDbConnection connection, string tableName)
    {
        var indexes = new Dictionary<string, IndexInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var indexesSchema = connection.GetSchema("Indexes", [null, null, null, null, tableName]);
            if (indexesSchema == null)
                return indexes.Values.ToList();

            foreach (DataRow row in indexesSchema.Rows)
            {
                string? indexName = row["INDEX_NAME"]?.ToString();
                string? columnName = row["COLUMN_NAME"]?.ToString();

                if (string.IsNullOrEmpty(indexName) || string.IsNullOrEmpty(columnName))
                    continue;

                if (!indexes.TryGetValue(indexName, out var indexInfo))
                {
                    bool isPrimary = false;
                    if (row.Table.Columns.Contains("PRIMARY_KEY") && row["PRIMARY_KEY"] != DBNull.Value)
                    {
                        if (row["PRIMARY_KEY"] is bool pb)
                            isPrimary = pb;
                        else if (bool.TryParse(row["PRIMARY_KEY"].ToString(), out bool parsedPb))
                            isPrimary = parsedPb;
                    }
                    else if (string.Equals(indexName, "PrimaryKey", StringComparison.OrdinalIgnoreCase))
                    {
                        isPrimary = true;
                    }

                    bool isUnique = false;
                    if (row.Table.Columns.Contains("UNIQUE") && row["UNIQUE"] != DBNull.Value)
                    {
                        if (row["UNIQUE"] is bool ub)
                            isUnique = ub;
                        else if (bool.TryParse(row["UNIQUE"].ToString(), out bool parsedUb))
                            isUnique = parsedUb;
                    }

                    indexInfo = new IndexInfo
                    {
                        Name = indexName,
                        IsPrimaryKey = isPrimary,
                        IsUnique = isUnique || isPrimary
                    };
                    indexes[indexName] = indexInfo;
                }

                if (!indexInfo.Columns.Contains(columnName))
                {
                    indexInfo.Columns.Add(columnName);
                }
            }
        }
        catch
        {
            // Defensive: index extraction failure should not crash schema retrieval
        }

        return indexes.Values.ToList();
    }

    private static List<ForeignKeyInfo> ExtractForeignKeys(OleDbConnection connection)
    {
        var foreignKeys = new List<ForeignKeyInfo>();

        try
        {
            // OleDbSchemaGuid.Foreign_Keys represents foreign keys in OLE DB
            var fkTable = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Foreign_Keys, null);
            if (fkTable == null)
                return foreignKeys;

            foreach (DataRow row in fkTable.Rows)
            {
                string name = row["FK_NAME"]?.ToString() ?? string.Empty;
                string pkTable = row["PK_TABLE_NAME"]?.ToString() ?? string.Empty;
                string fkTableCol = row["FK_TABLE_NAME"]?.ToString() ?? string.Empty;
                string pkCol = row["PK_COLUMN_NAME"]?.ToString() ?? string.Empty;
                string fkCol = row["FK_COLUMN_NAME"]?.ToString() ?? string.Empty;

                if (!string.IsNullOrEmpty(fkTableCol) && !string.IsNullOrEmpty(pkTable))
                {
                    foreignKeys.Add(new ForeignKeyInfo
                    {
                        Name = name,
                        FromTable = fkTableCol,
                        ToTable = pkTable,
                        FromColumn = fkCol,
                        ToColumn = pkCol
                    });
                }
            }
        }
        catch
        {
            // Defensive: Foreign keys schema may not be supported by all OleDb configurations
        }

        return foreignKeys;
    }

    private static List<QueryInfo> ExtractQueries(OleDbConnection connection)
    {
        var queries = new Dictionary<string, QueryInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Try extracting views/queries using schema metadata
        try
        {
            var viewsSchema = connection.GetSchema("Views");
            if (viewsSchema != null)
            {
                foreach (DataRow row in viewsSchema.Rows)
                {
                    string viewName = row["TABLE_NAME"]?.ToString() ?? string.Empty;
                    string viewDef = string.Empty;
                    if (row.Table.Columns.Contains("VIEW_DEFINITION") && row["VIEW_DEFINITION"] != DBNull.Value)
                    {
                        viewDef = row["VIEW_DEFINITION"]?.ToString() ?? string.Empty;
                    }

                    if (!string.IsNullOrEmpty(viewName))
                    {
                        queries[viewName] = new QueryInfo
                        {
                            Name = viewName,
                            Sql = viewDef
                        };
                    }
                }
            }
        }
        catch
        {
            // Defensive fallback
        }

        // 2. Query MSysObjects for queries (Type = 5 in Access represents stored queries/views)
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT [Name] FROM MSysObjects WHERE [Type] = 5 AND [Flags] = 0";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string queryName = reader.GetString(0);
                if (!string.IsNullOrEmpty(queryName) && !queries.ContainsKey(queryName))
                {
                    queries[queryName] = new QueryInfo
                    {
                        Name = queryName,
                        Sql = string.Empty // SQL body is often stored in binary form or views schema
                    };
                }
            }
        }
        catch
        {
            // MSysObjects read access often requires explicit permissions in Access workgroups.
            // Defensive ignore if permission denied or table restricted.
        }

        return queries.Values.OrderBy(q => q.Name).ToList();
    }
}
