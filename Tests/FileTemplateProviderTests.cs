using AITaskAgent.Support.Template;

namespace AITaskAgent.Tests;

/// <summary>
/// LoadTemplateFromFile used to look up the name verbatim (no ".md" suffix) and return the name
/// itself instead of null on a miss - a missing template silently rendered as its own filename
/// instead of failing loud. These pin the fixed contract: GetTemplate finds "name" on disk as
/// "name.md", and returns null (not the name) when nothing is found or a path escapes folderPath.
/// </summary>
public sealed class FileTemplateProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("FileTemplateProviderTests").FullName;

    private void WriteTemplate(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, $"{name}.md"), content);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GetTemplate_finds_file_when_name_has_no_extension()
    {
        WriteTemplate("outliner_prompt", "Create a story outline for {{Input.Idea}}");
        var provider = new FileTemplateProvider(_dir);

        var template = provider.GetTemplate("outliner_prompt");

        Assert.Equal("Create a story outline for {{Input.Idea}}", template);
    }

    [Fact]
    public void GetTemplate_returns_null_when_file_does_not_exist()
    {
        var provider = new FileTemplateProvider(_dir);

        Assert.Null(provider.GetTemplate("does_not_exist"));
    }

    [Fact]
    public void GetTemplate_returns_null_for_path_traversal_attempt()
    {
        var secretDir = Directory.GetParent(_dir)!.FullName;
        File.WriteAllText(Path.Combine(secretDir, "secret.md"), "leaked");
        var provider = new FileTemplateProvider(_dir);

        try
        {
            Assert.Null(provider.GetTemplate("../secret"));
        }
        finally
        {
            File.Delete(Path.Combine(secretDir, "secret.md"));
        }
    }

    [Fact]
    public void GetTemplate_serves_from_cache_within_ttl()
    {
        WriteTemplate("cached", "original");
        var provider = new FileTemplateProvider(_dir, enableCache: true, ttl: TimeSpan.FromMinutes(5));

        var first = provider.GetTemplate("cached");
        WriteTemplate("cached", "overwritten");
        var second = provider.GetTemplate("cached");

        Assert.Equal("original", first);
        Assert.Equal("original", second);
    }

    [Fact]
    public async Task GetTemplate_reloads_after_ttl_expires()
    {
        WriteTemplate("expiring", "original");
        var provider = new FileTemplateProvider(_dir, enableCache: true, ttl: TimeSpan.FromMilliseconds(50));

        var first = provider.GetTemplate("expiring");
        await Task.Delay(150);
        WriteTemplate("expiring", "reloaded");
        var second = provider.GetTemplate("expiring");

        Assert.Equal("original", first);
        Assert.Equal("reloaded", second);
    }

    [Fact]
    public void Render_throws_FileNotFoundException_when_validateTemplates_true_and_template_missing()
    {
        var provider = new FileTemplateProvider(_dir, validateTemplates: true);

        Assert.Throws<FileNotFoundException>(() => provider.Render("missing_prompt", new { }));
    }

    [Fact]
    public void Render_returns_null_when_validateTemplates_false_and_template_missing()
    {
        // Pins the fixed contract: a missing template must never render as its own name - that
        // silently handed the LLM a literal template name instead of content. null lets callers
        // (e.g. PromptTemplateResolver) decide explicitly what to do about a miss.
        var provider = new FileTemplateProvider(_dir, validateTemplates: false);

        var result = provider.Render("missing_prompt", new { });

        Assert.Null(result);
    }

    [Fact]
    public void Render_uses_TinyTemplateRendered_by_default_to_interpolate_model()
    {
        WriteTemplate("greeting", "Hello {{Name}}!");
        var provider = new FileTemplateProvider(_dir);

        var result = provider.Render("greeting", new { Name = "world" });

        Assert.Equal("Hello world!", result);
    }
}
