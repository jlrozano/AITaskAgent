using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.ClearScript.V8;
using NJsonSchema;
using NJsonSchema.CodeGeneration.CSharp;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace AITaskAgent.YAML.Compilation;

/// <summary>
/// Compiles JSON Schemas into in-memory C# types that implement IStepResult.
/// Uses NJsonSchema for schema parsing, Roslyn for C# compilation,
/// and ClearScript V8 for JavaScript validation functions.
/// </summary>
public class SchemaCompiler
{
    private readonly ConcurrentDictionary<string, Type> _compiled = new();

    /// <summary>Compiles all JSON Schema files (*.json) in a folder and its subdirectories.</summary>
    public async Task CompileFromFolderAsync(string schemasFolder)
    {
        var files = Directory.GetFiles(schemasFolder, "*.json", SearchOption.AllDirectories);
        var tasks = files.Select(async file =>
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var json = await File.ReadAllTextAsync(file);
            await CompileAsync(name, json);
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Compiles a single JSON Schema into an in-memory C# type implementing IStepResult.
    /// Optionally supports a JS validate(self, context) function for semantic validation.
    /// </summary>
    public async Task<Type> CompileAsync(string name, string jsonSchema, string? jsValidateFunction = null)
    {
        if (_compiled.TryGetValue(name, out var cached))
            return cached;

        var schema = await JsonSchema.FromJsonAsync(jsonSchema);

        var settings = new CSharpGeneratorSettings
        {
            Namespace = "AITaskAgent.YAML.Generated",
            ClassStyle = CSharpClassStyle.Poco,
            GenerateOptionalPropertiesAsNullable = true,
            GenerateNullableReferenceTypes = true,
        };

        var generator = new CSharpGenerator(schema, settings);
        var rawCode = generator.GenerateFile(name);

        var validationMethod = BuildValidationMethod(jsValidateFunction);
        var enrichedCode = EnrichGeneratedCode(name, rawCode, validationMethod);

        var type = await CompileAndLoadAsync(name, enrichedCode);
        _compiled[name] = type;
        return type;
    }

    /// <summary>Returns a previously compiled type by schema name.</summary>
    public Type GetCompiledType(string name)
    {
        if (_compiled.TryGetValue(name, out var type))
            return type;
        throw new KeyNotFoundException($"Schema '{name}' has not been compiled. Call CompileAsync first.");
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string BuildValidationMethod(string? jsFunction)
    {
        if (string.IsNullOrWhiteSpace(jsFunction))
        {
            return "return Task.FromResult((true, (string?)null));";
        }

        // Embed the JS function and invoke it via ClearScript V8
        var escapedJs = jsFunction.Replace("\"", "\\\"").Replace("\r\n", "\\n").Replace("\n", "\\n");
        return $$"""
            using var engine = new global::Microsoft.ClearScript.V8.V8ScriptEngine();
            engine.AddHostObject("context", context);
            engine.AddHostObject("self", this);
            engine.Execute("{{escapedJs}}");
            var jsResult = engine.Invoke("validate", this, context);
            bool isValid = jsResult is bool b ? b : true;
            return Task.FromResult((isValid, (string?)null));
            """;
    }

    private static string EnrichGeneratedCode(string className, string rawCode, string validationBody)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using AITaskAgent.Core.Abstractions;");
        sb.AppendLine("using AITaskAgent.Core.Models;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();
        sb.AppendLine(rawCode);

        // Inject IStepResult implementation into the generated class
        // We append a partial implementation after the raw code
        sb.AppendLine($$"""
            namespace AITaskAgent.YAML.Generated
            {
                public partial class {{className}} : AITaskAgent.Core.Abstractions.IStepResult
                {
                    private AITaskAgent.Core.Abstractions.IStep? _step;
                    public AITaskAgent.Core.Abstractions.IStep Step => _step!;
                    internal void SetStep(AITaskAgent.Core.Abstractions.IStep step) => _step = step;
                    public AITaskAgent.Core.Abstractions.IStepError? Error { get; set; }
                    public bool HasError => Error != null;
                    public object? Value => this;
                    public System.Collections.Generic.List<AITaskAgent.Core.Abstractions.IStep> NextSteps { get; } = new();

                    public Task<(bool IsValid, string? Error)> ValidateAsync(AITaskAgent.Core.Models.PipelineContext context)
                    {
                        {{validationBody}}
                    }
                }
            }
            """);

        return sb.ToString();
    }

    private static async Task<Type> CompileAndLoadAsync(string typeName, string sourceCode)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        // Gather references needed for compilation
        var references = GetRequiredReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: $"AITaskAgent.YAML.Generated.{typeName}",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = string.Join(Environment.NewLine,
                result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
            throw new InvalidOperationException($"Schema compilation failed for '{typeName}':\n{errors}");
        }

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(ms);

        return assembly.GetType($"AITaskAgent.YAML.Generated.{typeName}")
            ?? throw new InvalidOperationException($"Type 'AITaskAgent.YAML.Generated.{typeName}' not found in compiled assembly.");
    }

    private static MetadataReference[] GetRequiredReferences()
    {
        var refs = new List<MetadataReference>();

        // Core runtime references
        var coreAssembly = typeof(object).Assembly;
        refs.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(Task).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location));

        // Newtonsoft.Json for the generated POCO
        refs.Add(MetadataReference.CreateFromFile(typeof(Newtonsoft.Json.JsonConvert).Assembly.Location));

        // Framework references
        refs.Add(MetadataReference.CreateFromFile(typeof(IStepResult).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(PipelineContext).Assembly.Location));

        // ClearScript V8 (only needed if JS validation is used, but include always)
        try
        {
            refs.Add(MetadataReference.CreateFromFile(typeof(V8ScriptEngine).Assembly.Location));
        }
        catch { /* optional */ }

        // netstandard / System assemblies
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var dll in new[] { "netstandard.dll", "System.Runtime.dll", "System.Collections.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        return [.. refs];
    }
}
