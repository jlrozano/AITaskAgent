using OfficeTools.Models;

namespace OfficeTools.Abstractions;

/// <summary>
/// Interface for reading WordprocessingML (.docx) documents and converting them to structured Markdown.
/// </summary>
public interface IDocxReader
{
    /// <summary>
    /// Asynchronously reads a .docx file and parses its contents into a structured <see cref="DocumentInfo"/> model.
    /// </summary>
    /// <param name="filePath">Full path to the .docx document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Structured document model including Markdown, sections, and extracted tables.</returns>
    Task<DocumentInfo> ReadDocumentAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously reads a .docx file and parses its contents into a structured <see cref="DocumentInfo"/> model.
    /// </summary>
    /// <param name="filePath">Full path to the .docx document.</param>
    /// <returns>Structured document model including Markdown, sections, and extracted tables.</returns>
    DocumentInfo ReadDocument(string filePath);

    /// <summary>
    /// Asynchronously extracts only the Markdown text representation from a .docx file.
    /// </summary>
    /// <param name="filePath">Full path to the .docx document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Document content formatted as Markdown.</returns>
    Task<string> ReadAsMarkdownAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously extracts only the Markdown text representation from a .docx file.
    /// </summary>
    /// <param name="filePath">Full path to the .docx document.</param>
    /// <returns>Document content formatted as Markdown.</returns>
    string ReadAsMarkdown(string filePath);
}
