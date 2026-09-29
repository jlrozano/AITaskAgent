using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace CodebaseAnalysisWeb.Hosting;

/// <summary>
/// Cabeceras de <c>index.html</c> de la SPA, lo sirva <c>UseDefaultFiles</c> + <c>UseStaticFiles</c> o el fallback. La
/// CSP es la que admite la app compilada por Vite: scripts y estilos propios (mermaid pinta con atributos <c>style</c>
/// y bloques <c>&lt;style&gt;</c> en línea, de ahí <c>'unsafe-inline'</c> solo en estilos), imágenes <c>data:</c> y
/// <c>blob:</c> (el favicon es <c>data:</c>), marcos solo del mismo origen (visor de PDF y HTML en sandbox de
/// <c>/api/documents/{id}/raw</c>, que lleva su propia CSP) y sin <c>object</c>/<c>embed</c>. Los assets y la API no
/// la llevan.
/// </summary>
public static class SpaSecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "font-src 'self' data:; connect-src 'self'; frame-src 'self'; object-src 'none'; base-uri 'self'; " +
        "form-action 'self'; frame-ancestors 'self'";

    public const string IndexFileName = "index.html";

    /// <summary>Opciones de ficheros estáticos que ponen la CSP a <c>index.html</c> y a nada más.</summary>
    public static StaticFileOptions StaticFileOptions() => new()
    {
        OnPrepareResponse = context =>
        {
            if (string.Equals(context.File.Name, IndexFileName, StringComparison.OrdinalIgnoreCase))
            {
                var headers = context.Context.Response.Headers;
                headers[HeaderNames.ContentSecurityPolicy] = ContentSecurityPolicy;
                headers[HeaderNames.XContentTypeOptions] = "nosniff";
                // La SPA cambia de hashes en cada build: index.html no se cachea para que apunte siempre a los actuales.
                headers[HeaderNames.CacheControl] = "no-cache";
            }
        }
    };
}
