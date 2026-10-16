using System.Net;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>Rutas de lectura adicionales del chat: <c>/api/chat/read-roots</c>. Solo se guardan rutas; nada se indexa ni se copia.</summary>
public sealed class ChatReadRootsEndpointsTests : IDisposable
{
    private readonly WebTestFactory _factory = new();
    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _outside = new();

    public void Dispose()
    {
        _factory.Dispose();
        _repo.Dispose();
        _outside.Dispose();
    }

    private string Extra()
    {
        var path = Path.Combine(_outside.Path, "docs");
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task<HttpClient> OpenAsync()
    {
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        return client;
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path) =>
        await client.DeleteAsync($"/api/chat/read-roots?path={Uri.EscapeDataString(path)}");

    [Fact]
    public async Task Without_workspace_every_call_is_409_no_workspace()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/chat/read-roots")).StatusCode);
        var post = await client.PostAsync("/api/chat/read-roots", Http.JsonBody(new { path = Extra() }));
        Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);
        Assert.Equal("no_workspace", (await post.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await DeleteAsync(client, Extra())).StatusCode);
    }

    [Fact]
    public async Task Adding_a_path_lists_it_persists_it_and_creates_nothing_in_the_path_or_the_repository()
    {
        var client = await OpenAsync();
        var extra = Extra();
        File.WriteAllText(Path.Combine(extra, "notas.md"), "x");

        var created = await client.PostAsync("/api/chat/read-roots", Http.JsonBody(new { path = extra }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var added = (await created.ReadJsonAsync()).GetProperty("roots");
        Assert.Equal(extra, added[0].GetProperty("path").GetString());
        Assert.False(added[0].GetProperty("global").GetBoolean());

        // Es idempotente.
        await client.PostAsync("/api/chat/read-roots", Http.JsonBody(new { path = extra }));
        var listed = (await (await client.GetAsync("/api/chat/read-roots")).ReadJsonAsync()).GetProperty("roots");
        Assert.Equal(1, listed.GetArrayLength());
        Assert.True(File.Exists(Path.Combine(_repo.Path, ".codebase", "chat-read-roots.json")));

        // Solo se guardó la ruta: la carpeta externa sigue como estaba.
        Assert.Equal(["notas.md"], Directory.GetFileSystemEntries(extra).Select(Path.GetFileName));
    }

    [Fact]
    public async Task The_added_path_is_visible_to_the_next_session_of_the_same_repository()
    {
        var client = await OpenAsync();
        await client.PostAsync("/api/chat/read-roots", Http.JsonBody(new { path = Extra() }));

        using var second = _factory.CreateClient();
        await second.OpenWorkspaceAsync(_repo.Path);

        Assert.Equal(1, (await (await second.GetAsync("/api/chat/read-roots")).ReadJsonAsync()).GetProperty("roots").GetArrayLength());
    }

    [Theory]
    [InlineData(@"docs\relativa")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\servidor\recurso")]
    [InlineData("")]
    public async Task An_invalid_path_is_400_invalid_path_and_is_not_saved(string path)
    {
        var client = await OpenAsync();

        var response = await client.PostAsync("/api/chat/read-roots", Http.JsonBody(new { path }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.ReadJsonAsync();
        Assert.Equal("invalid_path", error.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.Equal(0, (await (await client.GetAsync("/api/chat/read-roots")).ReadJsonAsync()).GetProperty("roots").GetArrayLength());
    }

    [Fact]
    public async Task A_missing_path_traversal_and_the_codebase_folder_are_400_invalid_path()
    {
        var client = await OpenAsync();
        var extra = Extra();
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".codebase"));

        foreach (var path in new[] { Path.Combine(_outside.Path, "no-existe"), extra + @"\..\docs", Path.Combine(_repo.Path, ".codebase"), _repo.Path })
        {
            var response = await client.PostAsync("/api/chat/read-roots", Http.JsonBody(new { path }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_path", (await response.ReadJsonAsync()).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Removing_a_path_drops_it_and_an_unknown_one_is_404()
    {
        var client = await OpenAsync();
        var extra = Extra();
        await client.PostAsync("/api/chat/read-roots", Http.JsonBody(new { path = extra }));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, extra)).StatusCode);
        Assert.Equal(0, (await (await client.GetAsync("/api/chat/read-roots")).ReadJsonAsync()).GetProperty("roots").GetArrayLength());

        var missing = await DeleteAsync(client, extra);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await missing.ReadJsonAsync()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_path_that_stopped_being_valid_is_listed_with_its_problem()
    {
        var client = await OpenAsync();
        var extra = Extra();
        await client.PostAsync("/api/chat/read-roots", Http.JsonBody(new { path = extra }));
        Directory.Delete(extra);

        var root = (await (await client.GetAsync("/api/chat/read-roots")).ReadJsonAsync()).GetProperty("roots")[0];

        Assert.Equal(extra, root.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("problem").ValueKind);
    }
}
