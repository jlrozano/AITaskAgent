# AITaskAgent.Extensions.CSharp

Optional extension: `type: CSharpStep` runs C# written in the pipeline, **in the host process**, with the full API (`PipelineContext`,
previous result, `NextSteps`). Roslyn and `NJsonSchema.CodeGeneration.CSharp` live only here; the framework and the YAML engine do not
reference them. Contract: `docs/designer/architecture.md` §15.

```yaml
- id: Normalize
  type: CSharpStep
  code: |
    var name = call.InputAs<string>() ?? "";
    return call.Ok(new { name = Strings.Title(name) });   # Strings: a class of csharp/shared.cs
```

- The host compiles all `CSharpStep` bodies of a package, plus the shared declarations (`csharp/shared.cs`) and one class per package
  schema, into **one assembly** (`CSharpUnitCompiler.CompileAsync`), registers it (`services.AddCSharpUnit(unit, packageName)`) and the
  steps call their generated static method (`Steps.<Id>(StepCall call)`).
- `[Hook]`, `[Validator]`, `[ResultValidator]` on public static methods of `shared.cs` are registered in `IHookRegistry` as
  `Class.Method` (`[Hook(Name = "…")]` sets another name), source `unit:<package>`. `[ResponseType("Name")]` on a class registers its schema.
  The four attributes live in `AITaskAgent.Abstractions` (namespace `AITaskAgent.Core.Hooks`, a global using of the unit); the designer's plugins
  use the same attributes (architecture §25.1).
- Diagnostics are mapped through `#line` to the YAML line and to the line inside the body (`CSharpDiagnostic`).
- Cache: SHA-256 of the sources, emitted DLL/PDB in memory and, with `CSharpCompileOptions.CacheDirectory`, on disk. Each call returns a
  new `CSharpUnit` over its own collectible `AssemblyLoadContext`; dispose it (a service provider does) to unload.
- Measured on the development machine (xunit host, small unit): first compile in a process about 0.9 s, same sources again in the
  process about 20 ms (load only), new process with the disk cache about 100 ms.

**Not isolated.** The code runs with the host's permissions; cancellation is cooperative (`call.CancellationToken`). A designer package
that carries C# must be trusted before it runs (`ICSharpStepPolicy`). The reference set is an allowlist (no `System.Diagnostics.Process`,
`System.Net.*`): it stops mistakes, not malice.
