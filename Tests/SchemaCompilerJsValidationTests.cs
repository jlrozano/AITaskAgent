using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.YAML.Compilation;

namespace AITaskAgent.Tests;

/// <summary>
/// The JS validation function is embedded in generated C# as a string literal. Only <c>"</c> and line breaks used to be
/// escaped, so a backslash (a regex like <c>\d+</c>, or the <c>\n</c> escape inside a JS string) broke the generated
/// code or changed the script's meaning.
/// </summary>
public sealed class SchemaCompilerJsValidationTests
{
    private const string Schema = """{ "type": "object", "properties": { "text": { "type": "string" } } }""";

    private static async Task<(bool IsValid, string? Error)> ValidateAsync(string js)
    {
        // Compiled assemblies live in the default load context under their name: keep it unique per test.
        var name = "JsValidation" + Guid.NewGuid().ToString("N");
        var type = await new SchemaCompiler().CompileAsync(name, Schema, js);
        var result = (IStepResult)Activator.CreateInstance(type, new EmptyStep("Seed"))!;
        return await result.ValidateAsync(new PipelineContext());
    }

    [Fact]
    public async Task A_script_with_backslashes_and_quotes_compiles_into_the_generated_validator()
    {
        var name = "JsCompile" + Guid.NewGuid().ToString("N");

        var type = await new SchemaCompiler().CompileAsync(name, Schema, """
            function validate(self, context) { return /\d+/.test('a1') && "a\nb".length === 3; }
            """);

        Assert.NotNull(type);
    }

    [RequiresV8Fact]
    public async Task Backslashes_regexes_and_escaped_newlines_reach_the_script_intact()
    {
        var (isValid, _) = await ValidateAsync("""
            function validate(self, context) {
                return /\d+/.test('a1') && "a\nb".length === 3 && 'q"uote'.length === 6;
            }
            """);

        Assert.True(isValid);
    }

    [RequiresV8Fact]
    public async Task A_script_that_returns_false_is_reported_invalid()
    {
        var (isValid, _) = await ValidateAsync("function validate(self, context) { return /\\d+/.test('abc'); }");

        Assert.False(isValid);
    }
}

/// <summary>Skipped where the ClearScript V8 native library cannot be loaded (it needs the VC++ runtime and a win-x64 host).</summary>
public sealed class RequiresV8FactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Unavailable = new(() =>
    {
        try
        {
            using var engine = new Microsoft.ClearScript.V8.V8ScriptEngine();
            engine.Evaluate("1");
            return null;
        }
        catch (Exception ex)
        {
            return "ClearScript V8 native library cannot be loaded here: " + ex.GetType().Name;
        }
    });

    public RequiresV8FactAttribute() => Skip = Unavailable.Value;
}
