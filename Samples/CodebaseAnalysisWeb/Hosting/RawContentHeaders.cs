using CodebaseAnalysis.Core.Documents;
using Microsoft.Net.Http.Headers;

namespace CodebaseAnalysisWeb.Hosting;

/// <summary>
/// Cabeceras de <c>GET /api/documents/{id}/raw</c> (plan v2 §7.5). Siempre <c>nosniff</c>, <c>no-store</c>,
/// <c>Cross-Origin-Resource-Policy: same-origin</c> y el tipo detectado por contenido (nunca el que declaró el
/// navegador). PDF e imágenes, <c>inline</c>; HTML y SVG, <c>inline</c> con una CSP <c>sandbox</c> que los ejecuta en un
/// origen opaco y sin scripts (el HTML, con <c>charset=utf-8</c>); el resto, <c>application/octet-stream</c> como adjunto.
/// </summary>
public static class RawContentHeaders
{
    public const string SandboxPolicy = "sandbox; default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:";

    /// <summary>Pone las cabeceras y devuelve el <c>Content-Type</c> con el que servir el fichero.</summary>
    public static string Apply(HttpResponse response, DocumentRecord record)
    {
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        response.Headers[HeaderNames.CacheControl] = "no-store";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";

        var inline = record.Family is DocumentFamilies.Pdf or DocumentFamilies.Image or DocumentFamilies.Html or DocumentFamilies.Svg;
        if (record.Family is DocumentFamilies.Html or DocumentFamilies.Svg)
            response.Headers[HeaderNames.ContentSecurityPolicy] = SandboxPolicy;

        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(record.OriginalFileName);
        response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();

        if (!inline)
            return "application/octet-stream";
        // El HTML ya se validó como UTF-8 estricto al subirlo: sin charset, uno sin <meta charset> se pintaría en
        // windows-1252 y con los acentos rotos.
        return record.Family == DocumentFamilies.Html ? $"{record.MediaType}; charset=utf-8" : record.MediaType;
    }
}
