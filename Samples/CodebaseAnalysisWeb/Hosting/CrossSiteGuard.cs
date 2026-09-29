using CodebaseAnalysisWeb.Api;

namespace CodebaseAnalysisWeb.Hosting;

/// <summary>
/// Defensa adicional para las rutas que cambian estado (plan v2 §5.6): en <c>/api</c>, cualquier método que no sea
/// GET ni HEAD con <c>Sec-Fetch-Site</c> presente y distinto de <c>same-origin</c> o <c>none</c> es 403
/// <c>cross_site_request</c>. Sin la cabecera (clientes que no son navegadores, tests) pasa: la cabecera la pone el
/// navegador y una página no la puede quitar ni falsear. Se suma a <c>AllowedHosts</c>, al JSON obligatorio y al
/// <c>application/octet-stream</c> de la subida, que ya fuerzan un preflight que no se concede. El proxy de Vite no la
/// dispara, porque el navegador habla con su mismo origen.
/// </summary>
public static class CrossSiteGuard
{
    public const string HeaderName = "Sec-Fetch-Site";

    public static IApplicationBuilder UseCrossSiteGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsRejected(context.Request))
            {
                await ApiErrorsFactory.CrossSiteRequest().ToResult(StatusCodes.Status403Forbidden).ExecuteAsync(context);
                return;
            }

            await next(context);
        });

    public static bool IsRejected(HttpRequest request)
    {
        if (!request.Path.StartsWithSegments("/api") || HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            return false;

        if (!request.Headers.TryGetValue(HeaderName, out var values))
            return false;

        var site = values.ToString().Trim();
        return !string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(site, "none", StringComparison.OrdinalIgnoreCase);
    }
}
