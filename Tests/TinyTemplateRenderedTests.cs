using AITaskAgent.Support.Template;

namespace AITaskAgent.Tests;

/// <summary>
/// The renderer serializes the model with a lowercasing naming strategy, but the 32 prompt
/// templates under Samples/*/templates use PascalCase placeholders. These pin the case-insensitive
/// lookup that keeps both working, since a miss renders as empty text rather than as an error.
/// </summary>
public class TinyTemplateRenderedTests
{
    private sealed record Model(string Content);

    [Fact]
    public void Resolves_pascal_case_placeholder()
    {
        var result = TinyTemplateRendered.Render("{{Content}}", new Model("hello"));

        Assert.Equal("hello", result);
    }

    [Fact]
    public void Resolves_lower_case_placeholder()
    {
        var result = TinyTemplateRendered.Render("{{content}}", new Model("hello"));

        Assert.Equal("hello", result);
    }

    [Fact]
    public void Resolves_nested_pascal_case_path()
    {
        var model = new { Value = new { OptimizedPrompt = "write a story" } };

        var result = TinyTemplateRendered.Render("{{Value.OptimizedPrompt}}", model);

        Assert.Equal("write a story", result);
    }

    [Fact]
    public void Falls_back_to_default_when_property_is_missing()
    {
        var result = TinyTemplateRendered.Render("{{Writer1 ?? No draft 1}}", new Model("x"));

        Assert.Equal("No draft 1", result);
    }

    [Fact]
    public void Keeps_surrounding_text()
    {
        var result = TinyTemplateRendered.Render("Draft: {{Content}}.", new Model("abc"));

        Assert.Equal("Draft: abc.", result);
    }

    [Fact]
    public void Dictionary_keys_keep_their_literal_casing()
    {
        // Dictionary keys are not lowercased by the serializer, so the exact-match path must win
        // before the lowercase retry kicks in.
        var model = new { Data = new Dictionary<string, string> { ["OriginalKey"] = "kept" } };

        var result = TinyTemplateRendered.Render("{{Data.OriginalKey}}", model);

        Assert.Equal("kept", result);
    }
}
