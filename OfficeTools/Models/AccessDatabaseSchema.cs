namespace OfficeTools.Models;

/// <summary>
/// Represents the schema information of an Access database (.accdb / .mdb).
/// </summary>
public class AccessDatabaseSchema
{
    /// <summary>
    /// File path to the Access database.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Database name or file name.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Tables extracted from the database schema.
    /// </summary>
    public List<TableInfo> Tables { get; set; } = new();

    /// <summary>
    /// Foreign keys extracted from the database schema.
    /// </summary>
    public List<ForeignKeyInfo> ForeignKeys { get; set; } = new();

    /// <summary>
    /// Queries/Views extracted from the database.
    /// </summary>
    public List<QueryInfo> Queries { get; set; } = new();
}

/// <summary>
/// Information about a database table.
/// </summary>
public class TableInfo
{
    /// <summary>
    /// Table name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Columns in the table.
    /// </summary>
    public List<ColumnInfo> Columns { get; set; } = new();

    /// <summary>
    /// Primary key column names.
    /// </summary>
    public List<string> PrimaryKeys { get; set; } = new();

    /// <summary>
    /// Indexes defined on the table.
    /// </summary>
    public List<IndexInfo> Indexes { get; set; } = new();
}

/// <summary>
/// Information about a table column.
/// </summary>
public class ColumnInfo
{
    /// <summary>
    /// Column name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Data type of the column.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Maximum size or character length of the column, if applicable.
    /// </summary>
    public int? Size { get; set; }

    /// <summary>
    /// Indicates whether the column allows null values.
    /// </summary>
    public bool Nullable { get; set; }

    /// <summary>
    /// Ordinal position of the column within the table.
    /// </summary>
    public int OrdinalPosition { get; set; }

    /// <summary>
    /// Default value expression of the column, if defined.
    /// </summary>
    public string? DefaultValue { get; set; }
}

/// <summary>
/// Information about an index defined on a table.
/// </summary>
public class IndexInfo
{
    /// <summary>
    /// Index name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Column names included in this index.
    /// </summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>
    /// Indicates whether the index enforces uniqueness.
    /// </summary>
    public bool IsUnique { get; set; }

    /// <summary>
    /// Indicates whether this index represents the primary key.
    /// </summary>
    public bool IsPrimaryKey { get; set; }
}

/// <summary>
/// Information about a foreign key relationship between tables.
/// </summary>
public class ForeignKeyInfo
{
    /// <summary>
    /// Constraint or relationship name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Originating table containing the foreign key.
    /// </summary>
    public string FromTable { get; set; } = string.Empty;

    /// <summary>
    /// Target referenced table.
    /// </summary>
    public string ToTable { get; set; } = string.Empty;

    /// <summary>
    /// Originating column containing the foreign key.
    /// </summary>
    public string FromColumn { get; set; } = string.Empty;

    /// <summary>
    /// Target referenced column in the target table.
    /// </summary>
    public string ToColumn { get; set; } = string.Empty;
}

/// <summary>
/// Information about a stored query or view.
/// </summary>
public class QueryInfo
{
    /// <summary>
    /// Query or view name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// SQL definition of the query.
    /// </summary>
    public string Sql { get; set; } = string.Empty;
}
