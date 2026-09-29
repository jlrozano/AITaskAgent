namespace CodebaseAnalysisWeb.Hosting;

/// <summary>
/// La web no tiene autenticación y puede leer el disco (sandbox de FileTools) y gastar la clave del LLM:
/// solo escucha en loopback (plan D12). El resto de la defensa son <c>AllowedHosts</c> (DNS rebinding),
/// los cuerpos JSON obligatorios (preflight CORS que no se concede) y la ausencia de CORS.
/// </summary>
public static class WebSecurity
{
    /// <summary>
    /// Falla si <c>urls</c> (appsettings, <c>ASPNETCORE_URLS</c> o <c>--urls</c>) o algún
    /// <c>Kestrel:Endpoints:*:Url</c> (que Kestrel antepone a <c>urls</c>) tiene una dirección que no es
    /// loopback, o si solo hay <c>HTTP_PORTS</c>/<c>HTTPS_PORTS</c>, que escuchan en todas las interfaces.
    /// </summary>
    public static void EnsureLoopbackUrls(IConfiguration configuration)
    {
        var endpoints = configuration.GetSection("Kestrel:Endpoints").GetChildren().ToList();
        foreach (var endpoint in endpoints)
        {
            var url = endpoint["Url"];
            if (string.IsNullOrWhiteSpace(url) || !IsLoopbackUrl(url.Trim()))
            {
                throw new InvalidOperationException(
                    $"El endpoint de Kestrel '{endpoint.Key}' ({url}) no es de loopback. {LoopbackOnly}");
            }
        }

        var urls = configuration["urls"];
        if (string.IsNullOrWhiteSpace(urls))
        {
            if (endpoints.Count == 0
                && (!string.IsNullOrWhiteSpace(configuration["http_ports"]) || !string.IsNullOrWhiteSpace(configuration["https_ports"])))
            {
                throw new InvalidOperationException(
                    "HTTP_PORTS/HTTPS_PORTS escuchan en todas las interfaces; configura 'Urls' con http://127.0.0.1:<puerto>.");
            }
            return;
        }

        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsLoopbackUrl(raw))
            {
                throw new InvalidOperationException($"La URL '{raw}' no es de loopback. {LoopbackOnly}");
            }
        }
    }

    private const string LoopbackOnly =
        "CodebaseAnalysisWeb solo escucha en 127.0.0.1, [::1] o localhost (los tres están en AllowedHosts).";

    public static bool IsLoopbackUrl(string url)
    {
        // Kestrel acepta comodines ("http://*:5000", "http://+:5000") que Uri no sabe leer: nunca son loopback.
        if (url.Contains("://*", StringComparison.Ordinal) || url.Contains("://+", StringComparison.Ordinal))
            return false;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }
}
