namespace OfficeTools.Models;

/// <summary>
/// Result returned by the IngestOfficeArtifactsStep containing extracted schemas and documents.
/// </summary>
public class OfficeIngestionResult
{
    /// <summary>
    /// Extracted schemas from Access databases (.accdb / .mdb).
    /// </summary>
    public List<AccessDatabaseSchema> AccessSchemas { get; set; } = new();

    /// <summary>
    /// Extracted VBA projects, forms, controls and procedures from Access databases.
    /// </summary>
    public List<AccessVbaProject> VbaProjects { get; set; } = new();

    /// <summary>
    /// Processed Word documents (.docx).
    /// </summary>
    public List<DocumentInfo> Documents { get; set; } = new();

    /// <summary>
    /// Total count of processed Access database files.
    /// </summary>
    public int TotalAccessFiles => AccessSchemas.Count;

    /// <summary>
    /// Total count of processed Word documents.
    /// </summary>
    public int TotalWordFiles => Documents.Count;

    /// <summary>
    /// Human-readable summary of the ingestion process.
    /// </summary>
    public string Summary { get; set; } = string.Empty;
}
