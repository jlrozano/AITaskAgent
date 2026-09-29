using OfficeTools.Models;

namespace OfficeTools.Abstractions;

/// <summary>
/// Interface for extracting schema information from Microsoft Access database files (.accdb / .mdb) using OleDb.
/// </summary>
public interface IAccessSchemaExtractor
{
    /// <summary>
    /// Asynchronously extracts the database schema including tables, columns, indexes, foreign keys, and queries.
    /// </summary>
    /// <param name="filePath">Full path to the Access database file (.accdb or .mdb).</param>
    /// <param name="password">Optional database password if encrypted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Extracted database schema information.</returns>
    Task<AccessDatabaseSchema> ExtractSchemaAsync(string filePath, string? password = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously extracts the database schema including tables, columns, indexes, foreign keys, and queries.
    /// </summary>
    /// <param name="filePath">Full path to the Access database file (.accdb or .mdb).</param>
    /// <param name="password">Optional database password if encrypted.</param>
    /// <returns>Extracted database schema information.</returns>
    AccessDatabaseSchema ExtractSchema(string filePath, string? password = null);

    /// <summary>
    /// Checks whether an OLE DB connection can be established to the specified Access database file.
    /// </summary>
    /// <param name="filePath">Full path to the Access database file.</param>
    /// <param name="password">Optional database password.</param>
    /// <param name="errorMessage">Error message details if connection check fails.</param>
    /// <returns>True if connection succeeds; otherwise, false.</returns>
    bool CanConnect(string filePath, string? password, out string? errorMessage);
}
