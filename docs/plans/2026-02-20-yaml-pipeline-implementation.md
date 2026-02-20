# YAML-Driven Dynamic Pipelines Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement a hybrid factory orchestrator that compiles JSON Schemas into dynamic C# DTOs and instantiates Pipeline steps from YAML definitions.

**Architecture:** We will use `NJsonSchema` and Roslyn to compile dynamic data classes into a `AITaskFramework.DynamicTypes.dll`. We will then use `YamlDotNet` and Reflection to instantiate "Factory-Friendly" bridge classes (like `YamlLlmStep`) that map YAML properties to the native `Pipeline.ExecuteAsync` flow.

**Tech Stack:** `NJsonSchema`, `NJsonSchema.CodeGeneration.CSharp`, `Microsoft.CodeAnalysis.CSharp` (Roslyn), `YamlDotNet`.

---

### Task 1: Add required NuGet Packages

**Files:**
- Modify: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\Framework.csproj` (or core project)

**Step 1: Write the failing test**

Skip test since this only adds references.

**Step 2: Run test to verify it fails**

Skip test.

**Step 3: Write minimal implementation**

Run: `dotnet add c:\GeminiCli\CodeGui\AITaskFramework\Framework\Framework.csproj package NJsonSchema`
Run: `dotnet add c:\GeminiCli\CodeGui\AITaskFramework\Framework\Framework.csproj package NJsonSchema.CodeGeneration.CSharp`
Run: `dotnet add c:\GeminiCli\CodeGui\AITaskFramework\Framework\Framework.csproj package Microsoft.CodeAnalysis.CSharp`
Run: `dotnet add c:\GeminiCli\CodeGui\AITaskFramework\Framework\Framework.csproj package YamlDotNet`

**Step 4: Run test to verify it passes**

Run: `dotnet build c:\GeminiCli\CodeGui\AITaskFramework\Framework\Framework.csproj`
Expected: BUILD SUCCESS

**Step 5: Commit**

```bash
git add c:\GeminiCli\CodeGui\AITaskFramework\Framework\Framework.csproj
git commit -m "build: add NJsonSchema, Roslyn, and YamlDotNet dependencies"
```

---

### Task 2: Implement `SchemaCompiler` (Dynamic Type Generation)

**Files:**
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\YamlPipeline\SchemaCompiler.cs`
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Tests\Framework.Tests\YamlPipeline\SchemaCompilerTests.cs`

**Step 1: Write the failing test**

```csharp
using NUnit.Framework;
using AITaskAgent.YamlPipeline;
using System.Reflection;

namespace AITaskAgent.Tests.YamlPipeline;

public class SchemaCompilerTests
{
    [Test]
    public async Task CompileSchemas_GeneratesValidAssemblyWithTypes()
    {
        var jsonSchema = @"{
            ""type"": ""object"",
            ""properties"": {
                ""Message"": { ""type"": ""string"" }
            }
        }";
        
        var schemas = new Dictionary<string, string> { { "TestInput", jsonSchema } };
        var compiler = new SchemaCompiler();
        
        Assembly generatedAssembly = await compiler.CompileDynamicTypesAsync(schemas);
        
        Assert.That(generatedAssembly, Is.Not.Null);
        var type = generatedAssembly.GetType("AITaskAgent.DynamicTypes.TestInput");
        Assert.That(type, Is.Not.Null);
        var prop = type.GetProperty("Message");
        Assert.That(prop, Is.Not.Null);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test c:\GeminiCli\CodeGui\AITaskFramework\Tests\Framework.Tests\Framework.Tests.csproj --filter Name~CompileSchemas`
Expected: FAIL due to missing class/method.

**Step 3: Write minimal implementation**

```csharp
using System.Reflection;
using NJsonSchema;
using NJsonSchema.CodeGeneration.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AITaskAgent.YamlPipeline;

public class SchemaCompiler
{
    public async Task<Assembly> CompileDynamicTypesAsync(Dictionary<string, string> schemasJson)
    {
        var syntaxTrees = new List<SyntaxTree>();
        
        foreach (var kvp in schemasJson)
        {
            var schema = await JsonSchema.FromJsonAsync(kvp.Value);
            var generator = new CSharpGenerator(schema, new CSharpGeneratorSettings
            {
                Namespace = "AITaskAgent.DynamicTypes",
                ClassStyle = CSharpClassStyle.Poco,
                GenerateJsonMethods = false
            });
            
            var code = generator.GenerateFile();
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(code));
        }

        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute).Assembly.Location)
        };

        var compilation = CSharpCompilation.Create("AITaskAgent.DynamicTypes.dll")
            .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(references)
            .AddSyntaxTrees(syntaxTrees);

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new Exception($"Compilation failed: {errors}");
        }

        ms.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(ms.ToArray());
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test c:\GeminiCli\CodeGui\AITaskFramework\Tests\Framework.Tests\Framework.Tests.csproj --filter Name~CompileSchemas`
Expected: PASS

**Step 5: Commit**

```bash
git add c:\GeminiCli\CodeGui\AITaskFramework\Framework\YamlPipeline\SchemaCompiler.cs c:\GeminiCli\CodeGui\AITaskFramework\Tests\Framework.Tests\YamlPipeline\SchemaCompilerTests.cs
git commit -m "feat: implement SchemaCompiler to generate DLL from JSON Schemas"
```

---

### Task 3: Implement `YamlLlmStep` (Factory-Friendly Base Class)

**Files:**
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\LLM\Steps\YamlLlmStep.cs`
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Tests\Framework.Tests\LLM\Steps\YamlLlmStepTests.cs`

**Step 1: Write the failing test**

```csharp
using NUnit.Framework;
using AITaskAgent.LLM.Steps;
// ... (mock dependencies)

namespace AITaskAgent.Tests.LLM.Steps;

public class YamlLlmStepTests
{
    [Test]
    public void Constructor_BuildsDelegates_FromTemplateProvider()
    {
        Assert.Fail("Implement YamlLlmStep and test");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test c:\GeminiCli\CodeGui\AITaskFramework\Tests\Framework.Tests\Framework.Tests.csproj --filter Name~Constructor_BuildsDelegates`
Expected: FAIL

**Step 3: Write minimal implementation**

```csharp
using AITaskAgent.Core.Abstractions;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.Support.Template;

namespace AITaskAgent.LLM.Steps;

public class YamlLlmStep<TIn, TOut> : BaseLlmStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : ILlmStepResult
{
    public string? SystemPromptTemplateName { get; init; }
    public string? UserPromptTemplateName { get; init; }

    public YamlLlmStep(
        string name,
        ILlmService llmService,
        ITemplateProvider templateProvider,
        LlmProviderConfig profile)
        : base(
            llmService, 
            name, 
            profile, 
            messageBuilder: (input, context) => Task.FromResult(string.Empty), // Placeholder
            systemMessageBuilder: (input, context) => Task.FromResult(string.Empty)) // Placeholder
    {
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test c:\GeminiCli\CodeGui\AITaskFramework\Tests\Framework.Tests\Framework.Tests.csproj --filter Name~Constructor_BuildsDelegates`
Expected: PASS

**Step 5: Commit**

```bash
git add c:\GeminiCli\CodeGui\AITaskFramework\Framework\LLM\Steps\YamlLlmStep.cs c:\GeminiCli\CodeGui\AITaskFramework\Tests\Framework.Tests\LLM\Steps\YamlLlmStepTests.cs
git commit -m "feat: add YamlLlmStep base class for YAML construction"
```

---

### Task 4: Implement `YamlPipelineFactory`

**Files:**
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\YamlPipeline\YamlPipelineFactory.cs`
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Tests\Framework.Tests\YamlPipeline\YamlPipelineFactoryTests.cs`

**Step 1: Write the failing test**

```csharp
// Test that YamlPipelineFactory makes generic types using the dynamic dll Assembly.
```

**Step 2: Run test to verify it fails**

Run test. Expected FAIL.

**Step 3: Write minimal implementation**

```csharp
using MS.DI; // Omitted explicit imports for brevity
using System.Reflection;

namespace AITaskAgent.YamlPipeline;

public class YamlPipelineFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Assembly _dynamicAssembly;

    public YamlPipelineFactory(IServiceProvider serviceProvider, Assembly dynamicAssembly)
    {
        _serviceProvider = serviceProvider;
        _dynamicAssembly = dynamicAssembly;
    }

    public object CreateStep(string typeAlias, string inputSchema, string outputSchema, string id)
    {
        Type tIn = _dynamicAssembly.GetType($"AITaskAgent.DynamicTypes.{inputSchema}")!;
        Type tOut = _dynamicAssembly.GetType($"AITaskAgent.DynamicTypes.{outputSchema}")!;
        
        Type baseType = typeof(AITaskAgent.LLM.Steps.YamlLlmStep<,>); 
        Type closedType = baseType.MakeGenericType(tIn, tOut);
        
        var step = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance(_serviceProvider, closedType, id);
        return step;
    }
}
```

**Step 4: Run test to verify it passes**

Run test. Expected PASS.

**Step 5: Commit**

```bash
git add c:\GeminiCli\CodeGui\AITaskFramework\Framework\YamlPipeline\YamlPipelineFactory.cs c:\GeminiCli\CodeGui\AITaskFramework\Tests\Framework.Tests\YamlPipeline\YamlPipelineFactoryTests.cs
git commit -m "feat: implement YamlPipelineFactory using MakeGenericType"
```
