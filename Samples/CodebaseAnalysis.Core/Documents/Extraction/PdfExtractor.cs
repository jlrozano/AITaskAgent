using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace CodebaseAnalysis.Core.Documents.Extraction;

/// <summary>PDF con PdfPig (plan v2 §7.2): <c>## Página N</c> con el texto en orden de lectura; sin texto, <c>empty</c> (no hay OCR).</summary>
public static class PdfExtractor
{
    public const int MaxPages = 1000;

    public static ExtractionResult Extract(string path, string title, CancellationToken ct)
    {
        using var document = PdfDocument.Open(path);
        var sb = new StringBuilder();
        var warnings = new List<string>();
        var pages = document.NumberOfPages;
        var withText = 0;
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            if (page.Number > MaxPages)
            {
                warnings.Add($"El PDF tiene {pages} páginas: solo se extraen las {MaxPages} primeras.");
                break;
            }

            var text = ContentOrderTextExtractor.GetText(page).Replace("\r\n", "\n").Trim();
            sb.Append("## Página ").Append(page.Number).Append("\n\n");
            if (text.Length > 0)
            {
                withText++;
                sb.Append(text).Append("\n\n");
            }
        }

        if (withText == 0)
        {
            return new ExtractionResult(string.Empty, [], ExtractionStatuses.Empty, pages, false,
                [.. warnings, "El PDF no tiene texto extraíble: probablemente es un escaneado y no hay OCR."]);
        }

        return DocumentExtractor.Markdown(sb.ToString(), title, warnings, pages);
    }
}
