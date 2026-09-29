using System.Net;
using System.Text;

namespace CodebaseAnalysisWeb.Tests;

/// <summary><c>GET /api/source</c>: solo texto y solo dentro del repositorio abierto.</summary>
public sealed class SourceEndpointsTests : IDisposable
{
    private readonly WebTestFactory _factory = new();
    private readonly TempDirectory _repo = new();

    public void Dispose()
    {
        _factory.Dispose();
        _repo.Dispose();
    }

    private async Task<HttpClient> OpenAsync()
    {
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        return client;
    }

    [Fact]
    public async Task Without_workspace_it_is_409()
    {
        var response = await _factory.CreateClient().GetAsync("/api/source?path=a.cs");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_text_file_comes_back_with_its_relative_path_and_line_count()
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".codebase", "export"));
        File.WriteAllText(Path.Combine(_repo.Path, ".codebase", "export", "M.bas"), "Sub A()\r\n  x = 1\r\nEnd Sub\r\n");
        var client = await OpenAsync();

        var json = await (await client.GetAsync("/api/source?path=" + Uri.EscapeDataString(".codebase\\export\\M.bas"))).ReadJsonAsync();

        Assert.Equal(".codebase/export/M.bas", json.GetProperty("path").GetString());
        Assert.Equal("Sub A()\n  x = 1\nEnd Sub\n", json.GetProperty("text").GetString());
        Assert.Equal(3, json.GetProperty("totalLines").GetInt32());
    }

    [Fact]
    public async Task Latin1_text_is_decoded_instead_of_mangled()
    {
        File.WriteAllBytes(Path.Combine(_repo.Path, "ansi.bas"), Encoding.Latin1.GetBytes("' Configuración\n"));
        var client = await OpenAsync();

        var json = await (await client.GetAsync("/api/source?path=ansi.bas")).ReadJsonAsync();

        Assert.Equal("' Configuración\n", json.GetProperty("text").GetString());
    }

    /// <summary>
    /// H-8: lo que no es UTF-8 se lee con la codificación que declara el repositorio (aquí la del <c>pom.xml</c>), como en el
    /// analizador y en las tools; el UTF-8 válido sigue siendo UTF-8.
    /// </summary>
    [Fact]
    public async Task Non_utf8_text_uses_the_encoding_the_repository_declares()
    {
        File.WriteAllText(Path.Combine(_repo.Path, "pom.xml"),
            "<project><properties><project.build.sourceEncoding>Cp1252</project.build.sourceEncoding></properties></project>");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // 0x80 es el euro en windows-1252 y un carácter de control en Latin-1.
        File.WriteAllBytes(Path.Combine(_repo.Path, "precio.bas"), Encoding.GetEncoding(1252).GetBytes("' Precio en € con acción\n"));
        File.WriteAllText(Path.Combine(_repo.Path, "utf8.cs"), "// Año de la acción\n", new UTF8Encoding(false));
        var client = await OpenAsync();

        var cp1252 = await (await client.GetAsync("/api/source?path=precio.bas")).ReadJsonAsync();
        var utf8 = await (await client.GetAsync("/api/source?path=utf8.cs")).ReadJsonAsync();

        Assert.Equal("' Precio en € con acción\n", cp1252.GetProperty("text").GetString());
        Assert.Equal("// Año de la acción\n", utf8.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("../fuera.txt", HttpStatusCode.BadRequest)]
    [InlineData("C:/Windows/win.ini", HttpStatusCode.BadRequest)]
    [InlineData("no-existe.cs", HttpStatusCode.NotFound)]
    [InlineData(".git/config", HttpStatusCode.NotFound)]
    [InlineData("bin.dat", HttpStatusCode.BadRequest)]
    public async Task Outside_hidden_missing_or_binary_is_refused(string path, HttpStatusCode expected)
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".git"));
        File.WriteAllText(Path.Combine(_repo.Path, ".git", "config"), "[core]");
        File.WriteAllBytes(Path.Combine(_repo.Path, "bin.dat"), [1, 2, 0, 3]);
        var client = await OpenAsync();

        var response = await client.GetAsync("/api/source?path=" + Uri.EscapeDataString(path));

        Assert.Equal(expected, response.StatusCode);
    }
}
