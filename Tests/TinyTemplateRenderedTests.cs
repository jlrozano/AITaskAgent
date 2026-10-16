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

    [Fact]
    public void Resolves_file_include_with_at_bracket_syntax()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var partialPath = Path.Combine(tempDir, "_partial.md");
            File.WriteAllText(partialPath, "Contenido Parcial {{value}}");

            var template = "Inicio: @[_partial.md] Fin.";
            var model = new { value = "123" };

            var result = TinyTemplateRendered.Render(template, model, baseDirectory: tempDir);

            Assert.Equal("Inicio: Contenido Parcial 123 Fin.", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Resolves_file_include_with_scriban_syntax()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var partialPath = Path.Combine(tempDir, "_partial.md");
            File.WriteAllText(partialPath, "Directiva {{status}}");

            var template = "Cabecera\n{{ include '_partial.md' }}\nPie";
            var model = new { status = "OK" };

            var result = TinyTemplateRendered.Render(template, model, baseDirectory: tempDir);

            Assert.Equal("Cabecera\nDirectiva OK\nPie", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Unresolved_placeholder_logs_a_warning_with_its_name_and_still_renders_empty()
    {
        var logger = new RecordingLogger();

        var result = TinyTemplateRendered.Render("a{{ nope.missing }}b", new Model("x"), logger: logger);

        Assert.Equal("ab", result);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.Contains("nope.missing", warning.Message);
    }

    [Fact]
    public void Resolved_or_defaulted_placeholders_do_not_log()
    {
        var logger = new RecordingLogger();

        var result = TinyTemplateRendered.Render("{{ content }} {{ other ?? fallback }}", new Model("x"), logger: logger);

        Assert.Equal("x fallback", result);
        Assert.Empty(logger.Entries);
    }
}
