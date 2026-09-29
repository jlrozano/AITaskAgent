using CodebaseAnalysisWeb.Hosting;
using Microsoft.AspNetCore.Hosting;
using System.Net;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>
/// CSP de la SPA (auditoría V2, punto 4): <c>index.html</c> la lleva tanto por <c>/</c> como por el fallback; los
/// assets y la API, no. El <c>wwwroot</c> de la prueba es una carpeta temporal, porque el real solo existe tras
/// compilar la SPA.
/// </summary>
public sealed class SpaCspTests : IDisposable
{
    private readonly TempDirectory _webRoot = new();

    public SpaCspTests()
    {
        File.WriteAllText(Path.Combine(_webRoot.Path, "index.html"), "<!doctype html><html><body><div id=\"root\"></div><script type=\"module\" src=\"/assets/app.js\"></script></body></html>");
        Directory.CreateDirectory(Path.Combine(_webRoot.Path, "assets"));
        File.WriteAllText(Path.Combine(_webRoot.Path, "assets", "app.js"), "console.log('ok');");
    }

    public void Dispose() => _webRoot.Dispose();

    private static string? Csp(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Content-Security-Policy", out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task Index_html_carries_the_spa_csp_by_root_and_by_fallback_and_nothing_else_does()
    {
        using var factory = new WebTestFactory();
        using var withWebRoot = factory.WithWebHostBuilder(b => b.UseWebRoot(_webRoot.Path));
        var client = withWebRoot.CreateClient();

        foreach (var url in new[] { "/", "/index.html", "/cualquier/ruta" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("id=\"root\"", await response.Content.ReadAsStringAsync());
            var csp = Csp(response);
            Assert.Equal(SpaSecurityHeaders.ContentSecurityPolicy, csp);
            Assert.Contains("script-src 'self';", csp);
            Assert.Contains("object-src 'none'", csp);
            Assert.Contains("frame-src 'self'", csp);
            Assert.DoesNotContain("unsafe-eval", csp);
        }

        var asset = await client.GetAsync("/assets/app.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Null(Csp(asset));

        var api = await client.GetAsync("/api/meta");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.Null(Csp(api));
        Assert.Null(Csp(await client.GetAsync("/api/no-existe")));
    }
}
