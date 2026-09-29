using OfficeTools.Models;

namespace OfficeTools.Abstractions;

/// <summary>
/// Abstraction for exporting and parsing VBA code, forms, controls, and procedures from Microsoft Access databases.
/// </summary>
public interface IAccessVbaExtractor
{
    /// <summary>
    /// Checks whether Access COM automation is available on the current operating environment.
    /// </summary>
    bool IsAccessComAvailable();

    /// <summary>
    /// Exports all modules, forms, reports, queries, and tables from an Access database via COM automation,
    /// and parses them into an <see cref="AccessVbaProject"/> model.
    /// </summary>
    /// <param name="databasePath">Path to the .accdb or .mdb file.</param>
    /// <param name="exportDirectory">Optional directory where exported text files will be saved. If null, a default path is used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Extracted <see cref="AccessVbaProject"/> or null if extraction failed.</returns>
    Task<AccessVbaProject?> ExtractVbaAndFormsAsync(
        string databasePath,
        string? exportDirectory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses an existing directory containing previously exported Access files (.bas, .cls, .txt forms, manifest.json).
    /// </summary>
    /// <param name="exportDirectory">Directory containing exported files.</param>
    /// <param name="databasePath">Original database path for reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed <see cref="AccessVbaProject"/>.</returns>
    Task<AccessVbaProject> ParseExportDirectoryAsync(
        string exportDirectory,
        string databasePath,
        CancellationToken cancellationToken = default);
}
