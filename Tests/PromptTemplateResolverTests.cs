using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Support;
using AITaskAgent.Support.Template;

namespace AITaskAgent.Tests;

/// <summary>
/// Resolve is the single choke point TemplateLlmStep and YamlLlmStep both funnel prompts through:
/// a template-name string (when a provider is supplied) or literal text with {{...}} placeholders
/// (when it is not). These pin both paths, the Input/Context model it builds, and what it does
/// when a provider's Render comes back null: a ".md" file name throws, anything else renders inline.
/// </summary>
public sealed class PromptTemplateResolverTests
{
    private sealed class RecordingProvider(string? renderResult, List<(string Name, object Model)>? calls = null) : ITemplateProvider
    {
        public List<(string Name, object Model)> Calls { get; } = calls ?? [];
        public string? GetTemplate(string name) => null;
        public string? Render(string name, object model)
        {
            Calls.Add((name, model));
            return renderResult;
        }
    }

    private static StepResult<string> Input(string value) => new(new EmptyStep("Seed"), value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_returns_empty_string_for_null_or_whitespace_prompt(string? rawPrompt)
    {
        var result = PromptTemplateResolver.Resolve(rawPrompt, Input("x"), new PipelineContext());

        Assert.Equal("", result);
    }

    [Fact]
    public void Resolve_uses_templateProvider_render_when_provider_supplied()
    {
        var provider = new RecordingProvider("rendered content");

        var result = PromptTemplateResolver.Resolve("outliner_prompt", Input("x"), new PipelineContext(), provider);

        Assert.Equal("rendered content", result);
        Assert.Single(provider.Calls);
        Assert.Equal("outliner_prompt", provider.Calls[0].Name);
    }

    [Fact]
    public void Resolve_falls_back_to_TinyTemplateRendered_when_no_provider_supplied()
    {
        var result = PromptTemplateResolver.Resolve("Hello {{Input}}", Input("world"), new PipelineContext());

        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void Resolve_exposes_step_input_value_as_Input_in_the_render_model()
    {
        object? capturedModel = null;
        var provider = new RecordingProvider("ignored");

        PromptTemplateResolver.Resolve("prompt", Input("payload"), new PipelineContext(), provider);
        capturedModel = provider.Calls[0].Model;

        var inputProperty = capturedModel!.GetType().GetProperty("Input");
        Assert.NotNull(inputProperty);
        Assert.Equal("payload", inputProperty!.GetValue(capturedModel));
    }

    [Fact]
    public void Resolve_merges_context_metadata_with_variables_and_variables_win_on_key_collision()
    {
        var context = new PipelineContext();
        context.SetMetadata("shared", "from-metadata");
        context.SetMetadata("onlyMetadata", "metadata-value");
        var variables = new Dictionary<string, object?>
        {
            ["shared"] = "from-variable",
            ["onlyVariable"] = "variable-value"
        };
        var provider = new RecordingProvider("ignored");

        PromptTemplateResolver.Resolve("prompt", Input("x"), context, provider, variables);

        var model = provider.Calls[0].Model;
        var mergedContext = (IDictionary<string, object?>)model.GetType().GetProperty("Context")!.GetValue(model)!;
        Assert.Equal("from-variable", mergedContext["shared"]);
        Assert.Equal("metadata-value", mergedContext["onlymetadata"]);
        Assert.Equal("variable-value", mergedContext["onlyvariable"]);
    }

    [Fact]
    public void Resolve_renders_rawPrompt_as_inline_text_when_provider_has_no_such_template()
    {
        // It used to come back raw, with its {{...}} placeholders unrendered.
        var provider = new RecordingProvider(renderResult: null);

        var result = PromptTemplateResolver.Resolve("Hello {{Input}}", Input("world"), new PipelineContext(), provider);

        Assert.Equal("Hello world", result);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public void Resolve_keeps_a_single_word_without_placeholders_when_provider_has_no_such_template()
    {
        var provider = new RecordingProvider(renderResult: null);

        var result = PromptTemplateResolver.Resolve("outliner_prompt", Input("x"), new PipelineContext(), provider);

        Assert.Equal("outliner_prompt", result);
    }

    [Theory]
    [InlineData("Writer_prompt.md")]
    [InlineData("  Writer_prompt.MD ")]
    public void Resolve_throws_instead_of_sending_a_missing_template_file_name(string rawPrompt)
    {
        var provider = new RecordingProvider(renderResult: null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PromptTemplateResolver.Resolve(rawPrompt, Input("x"), new PipelineContext(), provider));

        Assert.Contains(rawPrompt.Trim(), ex.Message);
    }

    [Fact]
    public void Resolve_without_provider_still_renders_md_ending_text_inline()
    {
        // Without a provider there is no lookup to fail: the text is inline by definition.
        var result = PromptTemplateResolver.Resolve("see notes.md", Input("x"), new PipelineContext());

        Assert.Equal("see notes.md", result);
    }

    [Fact]
    public void Resolve_without_provider_resolves_a_relative_include_against_the_working_directory_not_the_step_path()
    {
        // CurrentStepPath es una ruta de pasos ("Pipeline/Step"), no un directorio: usarla como base
        // hacía que ningún @[ruta] relativo resolviera nunca.
        var fileName = $"_resolver-include-{Guid.NewGuid():N}.md";
        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        File.WriteAllText(fullPath, "incluido");
        try
        {
            var context = new PipelineContext();
            context.AddPathPart("Parent");

            var result = PromptTemplateResolver.Resolve($"[@[{fileName}]]", Input("x"), context);

            Assert.Equal("[incluido]", result);
        }
        finally
        {
            File.Delete(fullPath);
        }
    }
}
