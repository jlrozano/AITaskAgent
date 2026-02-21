# YAML-Driven Dynamic Pipelines Unified Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement the YAML pipeline orchestration engine, translating the complex unified design (dynamic C# compilation, V8 JavaScript validation, and YamlDotNet reflection mapping) into reality without compromising the strictly-typed core execution engine.

**Architecture:** 
1. `SchemaCompiler` generates pure C# types from JSON Schema at runtime (using Roslyn) which implement `IStepResult` and execute `ValidateAsync()` using V8/ClearScript. 
2. `BaseLlmStep` transitions to non-generic (using injected `Type` parameters). 
3. `YamlPipelineFactory` deserializes DAG YAML using `YamlDotNet` native tags mapping to discovered `IYamlStep` instances.

**Tech Stack:** C# 14, NJsonSchema, Microsoft.CodeAnalysis (Roslyn), YamlDotNet, Microsoft.ClearScript.V8

---

### Task 1: Core Base Refactoring (`BaseLlmStep`)

**Files:**
- Modify: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\LLM\Steps\BaseLlmStep.cs`
- Modify: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\Core\Abstractions\IStep.cs`
- Modify: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\Core\Abstractions\IStepResult.cs`

**Step 1: Write the failing test**
Create `c:\GeminiCli\CodeGui\AITaskFramework\Tests\LLM\NonGenericBaseStepTests.cs`.
Test that `BaseLlmStep` can be instantiated passing `typeof(IStepResult)` as arguments instead of as generic type parameters (`<TIn, TOut>`).

**Step 2: Run test to verify it fails**
Run: `dotnet test Tests/LLM/NonGenericBaseStepTests.cs`
Expected: Compilation error, `BaseLlmStep` expects 2 generic arguments.

**Step 3: Write minimal implementation**
Redefine `BaseLlmStep` without generics:
```csharp
public abstract class BaseLlmStep(
    ILlmService llmService,
    string name,
    Type inputType,
    Type outputType,
    LlmProviderConfig profile) : IStep
{
    public Type InputType { get; } = inputType;
    public Type OutputType { get; } = outputType;
    
    // Convert logic to use raw IStepResult instead of TIn/TOut
    public async Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
       // ... internal logic using reflection where necessary and downcasting
    }
}
```
Update all inherited classes (like `TypedLlmStep<TIn, TOut>`) to pass `typeof(TIn)` and `typeof(TOut)` to `base()`.

**Step 4: Run test to verify it passes**
Run: `dotnet build` then `dotnet test`
Expected: Build succeeds and tests pass.

**Step 5: Commit**
```bash
git add .
git commit -m "refactor: transition BaseLlmStep to a non-generic architecture to support runtime reflection"
```

---

### Task 2: YAML Abstractions and Dependency Mapping

**Files:**
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\YAML\Abstractions\IYamlStep.cs`
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\YAML\Attributes\YamlStepTagAttribute.cs`
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\YAML\Steps\YamlLlmStep.cs`

**Step 1: Write the failing test**
Create `Test/YAML/YamlStepTests.cs`. Ensure `YamlLlmStep` has `YamlStepTagAttribute("LlmStep")` and implements `IYamlStep` properties (`StepId`, `InputSchema`, `OutputSchema`).

**Step 2: Run test to verify it fails**
Run: `dotnet test Tests/YAML/YamlStepTests.cs`
Expected: FAIL, missing classes.

**Step 3: Write minimal implementation**
```csharp
public interface IYamlStep 
{
    string StepId { get; init; }
    string InputSchema { get; init; }
    string OutputSchema { get; init; }
}

[AttributeUsage(AttributeTargets.Class)]
public class YamlStepTagAttribute(string tagName) : Attribute 
{
    public string TagName { get; } = tagName;
}

[YamlStepTag("LlmStep")]
public class YamlLlmStep : BaseLlmStep, IYamlStep
{
    public required string StepId { get; init; }
    public required string InputSchema { get; init; }
    public required string OutputSchema { get; init; }
    public string? SystemPrompt { get; init; } // Example of @template receiver
    
    public YamlLlmStep(ILlmService llmService) 
        : base(llmService, "YamlLlmStep", typeof(IStepResult), typeof(IStepResult), default) {}
}
```

**Step 4: Run test to verify it passes**
Run: `dotnet test Tests/YAML/YamlStepTests.cs`
Expected: PASS

**Step 5: Commit**
```bash
git add .
git commit -m "feat: introduce Native Yaml abstractions and YamlLlmStep"
```

---

### Task 3: SchemaCompiler & Javascript Validation (V8 ClearScript)

**Files:**
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\Data\SchemaCompiler.cs`
- Modify: `c:\GeminiCli\CodeGui\AITaskFramework\AITaskFramework.csproj` (Add Microsoft.ClearScript.V8 reference)

**Step 1: Write the failing test**
Create `Tests/Data/SchemaCompilerTests.cs`. Try to compile a minimal JSON schema into a `Type` and invoke its `ValidateAsync()` method which depends on JS.

**Step 2: Run test to verify it fails**
Run: `dotnet test Tests/Data/SchemaCompilerTests.cs`
Expected: FAIL, missing implementation.

**Step 3: Write minimal implementation**
1. Add NuGet: `dotnet add package Microsoft.ClearScript.V8`
2. Implement `SchemaCompiler.Compile(string name, string jsonSchema, string jsValidationCode)`.
3. Use `NJsonSchema.CodeGeneration.CSharp` to generate the DTO class string.
4. Modify the class string to implement `IStepResult`.
5. Implement `ValidateAsync()` inside the generated string to invoke `new V8ScriptEngine()` and evaluate `jsValidationCode` passing `this` and `PipelineContext`.
6. Compile into memory using `CSharpCompilation` from Roslyn.

**Step 4: Run test to verify it passes**
Run: `dotnet test Tests/Data/SchemaCompilerTests.cs`
Expected: PASS

**Step 5: Commit**
```bash
git add .
git commit -m "feat: implement dynamic schema compilation with ClearScript v8 validation injection"
```

---

### Task 4: YamlPipelineFactory (DAG & Orchestration)

**Files:**
- Create: `c:\GeminiCli\CodeGui\AITaskFramework\Framework\YAML\Execution\YamlPipelineFactory.cs`

**Step 1: Write the failing test**
Create `Tests/YAML/YamlPipelineFactoryTests.cs`. Provide a YAML string containing DAG references (`dependsOn` arrays). Attempt to generate a `Pipeline`.

**Step 2: Run test to verify it fails**
Run: `dotnet test Tests/YAML/YamlPipelineFactoryTests.cs`
Expected: FAIL, missing class.

**Step 3: Write minimal implementation**
1. Implement `YamlPipelineFactory` receiving `IServiceProvider` and `SchemaCompiler`.
2. Reflection scanning: `AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes()).Where(t => t.GetCustomAttribute<YamlStepTagAttribute>() != null)`.
3. Build `DeserializerBuilder` and register all discovered types with `.WithTagMapping($"!{attr.TagName}", type)`.
4. Parse YAML. Traverse the result.
5. Identify `InputSchema` strings, fetch the compiled `Type` from `SchemaCompiler`, and inject them into the deserialized `YamlLlmStep` instances via internal properties or reflection.
6. Check for circular dependencies (`dependsOn`). Build the `NextSteps` list for the native `Pipeline` builder.

**Step 4: Run test to verify it passes**
Run: `dotnet test Tests/YAML/YamlPipelineFactoryTests.cs`
Expected: PASS

**Step 5: Commit**
```bash
git add .
git commit -m "feat: implement YamlPipelineFactory with Reflection Discovery and DAG resolution"
```
