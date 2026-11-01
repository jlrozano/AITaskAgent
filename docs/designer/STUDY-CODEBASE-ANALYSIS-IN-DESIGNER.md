# Study: bringing CodebaseAnalysis into the workflow designer (2026-10-19)

> **Note (2026-10-21): historical study.** The «extension» model it proposes (an `IExtension` in a folder with `extension.json`, loaded at start) was replaced by **plugins** (`docs/designer/architecture.md` §25): NuGet packages or folders (`plugin.json`) with steps, tools, hooks, templates and configuration, loaded after an explicit approval. `CodebaseAnalysis.Designer` is now a folder plugin (§19). Terms, paths, names and commands below are those of 2026-10-19 and are not rewritten; the table of renames is architecture §17.2.

Four read-only studies (A-D) made by separate agents; this page is the summary and the decisions they leave to you. The four
reports follow in full, with `[V]` (read the line), `[I]` (inferred) and `[?]` (unknown) marks and `file:line` references.
Nothing was built, run or changed in code. The decisions you gave on 2026-10-19 (dynamic loading yes, `CmdStep` in the core,
Office removed, extensible JavaScript, LLM steps replicable) are taken as given; the studies only say how.

## 1. What the studies found

1. **CodebaseAnalysis is not a YAML pipeline waiting to be written** (study B). Its 17 files in `Steps/` are 14 real steps that
   implement `IStep<TIn,TOut>` directly, 1 `ParallelStep` subclass, 1 LLM base class and 1 static helper. Almost all mix
   deterministic C#, SQLite and external processes with a small LLM sub-call. Realistic design: **registered C# steps for the
   deterministic work and YAML `LlmStep` for the pure LLM calls**, not an all-YAML pipeline.
2. **LLM steps are mostly replicable** (B). `CustomStatelessTemplateLlmStep` and `YamlLlmStep` share `BaseLlmStep` (retries,
   provider fallback, tool loop, repetition detector). Pure calls (role, prompts, no history, tools, tool iterations, timeout,
   retries) are expressible in YAML. What the YAML step lacks: the final answer without tools after the tool limit
   (`ToolLimitFinalAnswerPrompt`), `tagHandlers` by name, dynamic fan-out, and timeouts computed at run time.
3. **Dynamic loading is viable but does not unlock CodebaseAnalysis by itself** (C). A custom `AssemblyLoadContext` that shares
   the contract with the host (Abstractions, Newtonsoft, NJsonSchema) works; BRMS gives ideas, not code (it loads everything
   into the default context and never unloads). The blocker is that CodebaseAnalysis steps have no `[StepTag]` or `IExtension`
   and take services in their constructors: making them YAML-declarable is a separate piece of work (cost L).
4. **BRMS has no real mechanism to add JS functions or libraries** (A); its hooks are dead code. `ScriptStep` already has the natural
   injection point in the worker bootstrap (`ScriptRunner.cs:28-51`).
5. **`CmdStep` is feasible in the core** (D), cost M. Registered by assembly scan it would exist in every host without opt-in,
   and the designer's trust dialog does not look at it today: both need handling together.
6. **Office removal is a cheap delete** except that "Office" is two features (D): (A) ingesting `.accdb/.mdb/.docx` from the
   analyzed repository, and (B) extracting `.docx/.xlsx/.pptx` uploaded by the user in the chat. Removing B is a visible regression.
7. **Global state** (D): `AvailableTools.Instance`, `FileToolsConfiguration` and `SqliteConnection.ClearAllPools()` are the
   shared mutable pieces. The designer already serializes runs (`MaxConcurrentRuns = 1`).
8. **`CSharpStep` (your Talend-like idea)** (C, addendum): viable and cheaper than dynamic loading for small steps of your own,
   with the source traveling as text inside the package. Only with an isolated worker process and mandatory trust at execution;
   it does not replace dynamic loading for the heavy CodebaseAnalysis steps.

## 2. Decisions that are yours

| # | Decision | Options / recommendation |
|---|---|---|
| D-1 | Office scope | Remove (A) repository ingestion only, or (A)+(B) including uploads in the chat. Recommendation: (A) now, keep (B) unless you want to drop `docx/xlsx/pptx` uploads. |
| D-2 | Framework changes (each needs your written approval) | F1 final answer after tool limit in `BaseLlmStep`; F4 tolerant mode in `ParallelStep`; F5 `ForEachStep`; F6 dynamic timeout by expression; `CmdStep` registration (`AddCmdStep()` vs assembly scan); `FileToolsConfiguration` scoping; `TemplateLlmStep` resolving tools by DI. |
| D-3 | Where an extension's configuration travels | In the package `config.json` (goes through trust and encryption) or in a file of its own. Today `config.json` reaches the engine only as `LlmProviders`, `Timeouts` and `Storage`. |
| D-4 | Concurrency | Keep serialized runs (no change) or scope the global state per run (cost M, framework changes). |
| D-5 | Two runs over the same repository share the SQLite DB and `.codebase` | No code isolates this: product decision. |
| D-6 | `CmdStep` minimum | Study D proposes: literal `executable`, command list in the trust dialog and in the trust hash, `inheritEnvironment: minimal` by default, and an `ICmdStepPolicy` hook so the designer can deny or confine. You accepted the risk; this is only the minimum the designer would need to show what a foreign package runs. |
| D-7 | Legacy origin filter | Keep the `office_tools` / `access_vba_extractor` filter in `CheckIfEnrichedAsync` when removing Office, or an old graph would count as enriched by Roslyn. |

## 3. Proposed order

1. **D2 Office removal** (S/M), after D-1.
2. **`CmdStep` with the trust integration in the designer** (M), D-2 and D-6.
3. **JavaScript extensions** (A): `proto` and closed `require`, small native library in the worker, host functions with deny by
   default (`file.readText` under allowed roots, `config.get`), `IExtension` catalog, package declaration plus static validation. M-L in total.
4. **CodebaseAnalysis in the designer by static reference** (B, D): small designer extension project with `IExtension`;
   `StepBase` adapters for the front half (detect, enrichers, facets, inventory); YAML `LlmStep` for synthesis, verifier and
   domain naming; the nine auditors in mode A with a separate report-writing step; framework changes F1 and F4; register
   `AnalysisTemplateProvider` as the package `ITemplateProvider` (do not rewrite the 45 templates); extend the config bridge;
   keep step ids (`Auditor_*`, `Investigation_*`, `Verify_*`, `SynthesizerLlm`, `ParallelReportsExecution`); parity test against
   the factory pipeline; additive migration so the 1061 existing tests keep passing.
5. **Dynamic loading** (C): own `AssemblyLoadContext`, `extension.json` with an integer `contract`, folder per extension,
   `requires.extensions` in the package manifest (by name, not inside the zip), trust shows name, version, path and hash.
6. **`CSharpStep`** in a sibling extension with an isolated worker, only if small custom steps are still needed.

## 4. Open questions the studies could not answer

Real LLM runs were not made, so the quality and cost impact of removing the notes protocol (tag handlers) is unknown; Roslyn
compile times and disk weight were not measured; the Linux process-tree kill and the Windows job-object race are general
knowledge, not checked in the repo; whether `FileTemplateProvider` supports `@[file.md]`; whether `YamlEmitter` round-trips a
step with an unknown tag (needed to open a package read-only when an extension is missing); and whether the CodebaseAnalysis
`ReportFacetInput` steps can run in the YAML engine at all. Each study lists its own.

## 5. Revision after the user's clarification (2026-10-19, later the same day)

The user corrected the step model that study C's addendum (and study A) assumed. What they want, and what it changes:

- **A C# or JavaScript step receives the pipeline context and the previous step's output** (the model of `DelegatedStep`: step
  instance, input, `PipelineContext`, attempt, last result). `[V]` `DelegatedStep` takes exactly those
  (`AITaskAgent.Core/Steps/DelegatedStep.cs:12-26`). The `CSharpContext` with only `Get/Set/Log` proposed in study C (A.1) is **too
  narrow**; it is superseded by the full `PipelineContext` and the typed previous result.
- **A C# step can define new classes, instantiate them, inherit and do whatever it needs.** The classes it defines live inside the
  compiled step assembly: **they do not have to be visible in the designer** (no registration, no catalog entry); only the step
  that carries the code is in the catalog. This also removes the "heavy classes need a real assembly" objection for anything the
  author is willing to write inside the step, but not for code that needs its own NuGet dependencies, DI-built services or tests.
- **A step can send `NextSteps` with new steps.** `[V]` the framework already supports it: `IStepResult.NextSteps` is a
  `List<IStep>` (`AITaskAgent.Abstractions/Abstractions/IStepResult.cs:32`) and the pipeline honours it
  (`AITaskAgent.Core/Execution/Pipeline.cs:170,225`). `[V]` `ScriptStep` cannot do it today (no `NextSteps` anywhere in
  `AITaskAgent.Extensions.Script`).

Consequences for the plan (these replace the corresponding statements of C's addendum):

1. **A C# step with the full context and live `IStep` instances in `NextSteps` must run in the host process.** Objects cannot cross
   the worker's process boundary (study A: only JSON crosses it). So the "worker only" recommendation of the addendum conflicts
   with this requirement. In process, an `Environment.Exit` takes down the backend and an infinite loop cannot be stopped (no
   `Thread.Abort`; only cooperative cancellation through the token). The user already accepted this level of risk for `CmdStep`
   ("I would be programming in C#"). Honest remaining point: a package written by someone else now runs code with the host's
   permissions and memory (including the master password), so the trust dialog must show the code and its hash.
2. **JavaScript keeps the worker (JSON in and out) and can still send next steps declaratively**: the script returns step
   definitions (`{ type, ...config }`, the same shape as YAML v2), and the host builds them with the step registry
   (`IStepRegistry.Create(JObject)`) and puts them in `NextSteps`. That needs a protocol extension (study A) and validation of the
   returned definitions, but no live objects.
3. **Dynamic steps and the designer UI:** steps created at run time are not in the YAML nor in the canvas; the execution view maps
   events to canvas nodes by step id, so events of unknown ids need a place to show (nested under the step that created them).
4. **Study B's categories are unchanged** for CodebaseAnalysis (heavy steps still need real assemblies), but a `CSharpStep` with
   the full context is now a credible way to host *glue* logic of the pipeline in the package itself, which study B classed as C.
5. **Decision to take:** one `CSharpStep` in process with the full API (what the user describes), with an optional narrow `isolated`
   mode later, or only the narrow isolated one. The user's description implies the first.

## 6. User's design for C# steps: one compilation unit per package (2026-10-19)

The user's proposal, which replaces the "one compile per step" shape of study C's addendum: the code of **all** the package's
`CSharpStep`s is compiled **together**, into one assembly, with an ad-hoc **static class generated for each package** that has
one method per C# step (parameters = what the step receives, return = what it produces). At pipeline creation each C# step is
**replaced by a `DelegatedStep`** that calls the matching static method.

Assessment (recommendation, not evidence):

- **It is better than per-step compilation.** One compile per package (cached by hash of the sources), classes and helpers shared
  between steps (answers the "shared helpers" objection of study C), inheritance across steps, and it matches Talend's "routines".
  `DelegatedStep` already exists and is how `NextSteps` and the full `PipelineContext` reach the code (`DelegatedStep.cs:12-26`).
- **Method signature to fix:** `Task<IStepResult> StepName(<named step properties from YAML>, StepInput input, PipelineContext context, CancellationToken ct)`;
  YAML properties of the step are bound by name as typed parameters (validated before running), `input` is the previous result.
- **Where the code lives:** the body of each step as a YAML property (like `script:` of `ScriptStep`) plus one shared-declarations
  entry in the package for common classes; the designer **generates** the static class. `#line` directives map compiler
  diagnostics back to the YAML file, step and line.
- **Costs to accept:** one error anywhere breaks the whole unit (diagnostics are reported per step, but nothing runs until it
  compiles); any edit recompiles everything (fast and cached for small code); in-process risk as already discussed; the unit must be
  provided to the YAML factory through a host service (an `ICSharpUnit` style provider in a sibling extension), so the framework
  needs the extension but not Roslyn; a new package entry type (RN-01 and `manifest` need an optional field) and its hash in the trust.
- **Still not covered:** heavy CodebaseAnalysis steps (own NuGet dependencies, DI, tests).

## 7. External NuGet packages per workflow package (user question, 2026-10-19)

Question: can a package declare external NuGet packages, loaded (and unloaded) dynamically per package, with their dependencies
resolved? The user remembers that BRMS did resolve dependencies when they tried it. Study C had left NuGet out of v1 (§6 there).

Findings (from study C, `[V]` unless marked):
- **BRMS did resolve dependencies**: it reads the nuspec dependency groups and recurses (`BRMS:Core/NugetPackageLoader.cs:358-368`), so
  the user's memory is right. Its limits: it reads *all* dependency groups, not only the matching target framework; it uses the version
  range text as the version; it loads everything into `AssemblyLoadContext.Default` (no unload, and with two packages needing
  different versions the first loaded wins, `:476-482`); it extracts into the user's `~/.nuget/packages`; it is ~815 lines.
- **Per-package isolation is possible** with one `AssemblyLoadContext` per workflow package, which BRMS never did. The shared-set rule
  of study C (§2.2) still applies: `AITaskAgent.Abstractions`, Newtonsoft.Json, NJsonSchema and `Microsoft.Extensions.*` come from the host.
- **Unload** is realistic only for the generated C# unit of §6: the step registry holds the `CSharpStep` type, not the user assembly,
  so a collectible context per package version can be released when the package is closed `[I]`, unless user code left a thread,
  timer or pending continuation alive (study C A.4). It is **not** realistic for steps registered from a NuGet package, because the
  catalog and `YamlReader` singletons keep their `Type`s (`WorkflowDesignerServiceCollectionExtensions.cs:39-40`).

Recommendation (not evidence):
1. Declare packages in the manifest (`requires.nuget: [{ id, version }]`) and ship a **lock file** in the package (exact versions
   and content hashes); never resolve floating versions at run time.
2. Resolve with the official NuGet client libraries (target-framework-specific dependency groups, version ranges, one resolved graph
   per package) instead of copying BRMS's recursion; restore into a **private cache** of the designer (`data/nuget/`), not the user's.
3. Load each package version into its own collectible context; versions of a library may differ between packages; conflicts with the
   shared set are unified to the host's version and reported.
4. Compile the generated C# unit (§6) against the loaded assemblies, so the code can use the libraries directly.
5. Trust: restoring from the network when opening a foreign package is supply-chain exposure; pin hashes, allow only configured
   sources, work offline from the cache, and list the packages with hashes in the trust dialog. NuGet packages are not stored
   inside the zip (size limits), so an air-gapped machine needs a local source (note that `C:\Asisa\NuGetLocal` does not exist here).
6. Native libraries (`runtimes/<rid>/native`) need an explicit resolver, as BRMS did (`:502-598`).

Cost: M-L (L with lock file, trust and offline cache). Order: after the C# unit of §6.

## 8. Decisions taken by the user (2026-10-19)

| # | Decision |
|---|---|
| D-1 | **A**: remove only the ingestion of Office/Access artifacts from the analyzed repository. Uploads of `.docx/.xlsx/.pptx` in the chat stay. |
| D-3 | An extension's configuration belongs to the extension: the extension defines the schema (`ConfigurationModel()`); the values are saved under the extension's section in the package `config.json` (recommendation accepted in outline; wording to confirm). |
| D-5 | Two runs over the same repository share the SQLite DB and `.codebase` **by design**. |
| D-6 | `CmdStep` minimum accepted. Main use: running external executables such as `cmd`. |
| D-7 | The legacy origin filters are removed; they stay in the original example. This work is a complex test, not a replacement. |
| extra | `CSharpStep`: in process, with the full API (as described by the user). NuGet per package: yes, after the C# unit, as proposed. |
| CmdStep | Part of the core. It runs an **external executable or batch file** (`.exe`, `.bat`), not an interactive shell; its result is the **text of its standard output**. Registration: the user said it belongs to the core (assumed: registered by the normal assembly scan, no opt-in per host; the trust dialog must list its commands). |
| naming | The step is called **`ExecStep`** (decided by the coordinator on the user's request for a name matching the limitation): it runs an external executable or batch file, not a shell. |
| Office scope (reading) | The user said the Office/Access pieces "stay in the original example; this is only a complex test". Read as: **the original `CodebaseAnalysis` sample is NOT modified**; Office/Access support is left out of the *new* designer-hosted variant when it is built. No code of the original sample has been removed. To be confirmed by the user. |
| D-2 rest | `FileToolsConfiguration` stays **static**. `TemplateLlmStep` is **not touched** (`AvailableTools.Instance` stays for CodebaseAnalysis). |
| D-4 | Designer runs stay **serial** (`MaxConcurrentRuns = 1`). |
| hooks scope | The user **disagrees with limiting hooks to LLM steps** (study E): they must exist for every step, at `StepBase` level: a **validate** hook for the result, a hook to **attach data to / post-process a result**, and an **enrich/observability** hook; plus C#-defined validators (and result types) usable from YAML steps. Study E's design is to be revised accordingly (generic `Hooks` on `StepBase`; LLM-specific hooks only in the LLM steps). |
| workspace | **Decided (user, 2026-10-19):** the user's workspace (with keys) leaves the repository; the app gets an **«open workspace»** option so nothing travels by mistake; the user will add example workspaces without keys when pushing. |
| result validation | **Decided:** no exception to the virtual-default rule: `JsonStepResult:33` and `LlmJsonStepResult:21` (and the 3 LLM methods `BaseLlmStep.FinalizeAsync`, `YamlLlmStep.BuildUserMessageAsync/BuildSystemMessageAsync`) are modified to **call `base`**, so the virtual default dispatches to the hook. `RetryMiddleware` needs no non-virtual call. |
| C# response types | `StepResultFactory` does not need registration: it builds instances and the schema by reflection from any `Type` (`StepResultFactory.cs:65,92-97`, cached by type). What YAML needs is a **name → schema** entry (`SchemaRegistry.Add`); the schema of a C# class can come from either builder (study F recommends making `StepResultFactory`'s schema builder public). A class of the compiled unit is a normal `Type`, so no extra registration is needed to instantiate it. |
| schema classes | **Decided (user, 2026-10-19):** schema-first. The C# extension (`AITaskAgent.Extensions.CSharp`) generates one C# class per package schema (`NJsonSchema.CodeGeneration.CSharp`, which lives only in that extension, not in the core) into the same generated source as the static class of the steps, before compiling; classes written by hand in the unit become schemas the other way; a name defined both ways is a compile error. Today nothing does this: `SchemaCompiler` was removed in v2 (`ARCHITECTURE-v2.md:43,83`). |
| deployment | The **CLI already runs a package zip** with everything the designer created, so standalone execution does not need generated code. Consequence: everything the C# unit needs (Roslyn compile, schema class generation, NuGet restore, extension loading) must live in the shared designer Core, used by both the CLI and the Web, with a hash-keyed local cache. The idea of «export as a C# project» is only a possible debugging aid, low priority, not a deployment path. |
| NuGet in C# | **Decided (user, 2026-10-19):** NuGet packages can be used by C# extensions and by the `CSharpStep` unit. They are declared in **sections to import**: `requires.nuget` in the package `manifest.json` (for the package's C# unit) and a `nuget` section in an extension's `extension.json` (for the extension). Both are resolved by **the same resolver in the shared designer Core**, so any change affects the CLI and the Web equally (lock file with exact versions and hashes, private cache, one load context per package, shared-set rule; study §7). Package sources (URL or local folder) are configuration of the workspace/user, not of the zip. |
| F4 | **Approved**: `failFast: false` in `ParallelStep`. |
| F5, F6 | **Deferred** (mode B stays a registered C# step; static timeouts for now). |
| F1 | **Not approved as written.** The user asks whether it can be done with the C# step, and opens a design question: framework steps that were designed for inheritance (virtual hooks such as `BuildUserMessageAsync`, `BuildLlmRequestAsync`, `ParseLlmResponseAsync`, `GetConversationContext`, `AddConversationToContext`, `ConfigureLlmRequest` in `BaseLlmStep.cs`) could also expose a **delegate property** per hook that the base method checks and invokes when assigned, so code (e.g. a method of the package's generated C# class) can be attached without subclassing. Pending a design study of the hooks before F1 and F2 are decided. |

---

# Study A: giving ScriptStep extra functions and code

Legend: `[V]` read the line, `[I]` inferred, `[?]` unknown. Recommendations are not marked.
Paths: `FW` = `C:\GeminiCli\AITask\AITaskAgent`; `BDP` = `\\nas_rozano\Trabajo_jl\Asisa\BDP`.

## 0. Headline

1. BRMS has **no mechanism to add functions or library code to JS**. It has three hooks, and two of them are dead or unused (section 1). What it really has is a way to inject **host objects** (C# objects) into an **in-process** V8, with no isolation.
2. `Designer/` is not a richer copy of that: it uses **Jint**, not ClearScript, and exposes only `console`, `context`/`input`/`output` (section 1.5). It adds nothing to copy.
3. The `ScriptStep` worker already has the right shape for extension: a bootstrap closure plus three bridge delegates. Adding functions means widening that closure, not redesigning it (section 2).
4. Recommendation: three tiers (JS prelude per package, worker-native functions, host functions over the pipe), with the declaration in YAML/manifest and the registry in DI via `IExtension` (section 4). Estimated M for the base, L with designer catalog and static validation.

Doc inconsistency found: `docs/ARCHITECTURE-v2.md:61` still says "`ScriptStep` runs in the host process ... it must not run untrusted scripts" `[V]`. `docs/ARCHITECTURE-v2.md:57` and the README say the opposite `[V]`. Line 61 is stale.

## 1. How BRMS adds functions and code to JS

### 1.0 Where it lives
The directory the user pointed at (`Engine/StdRules`) does not exist `[V]`. The JS code is in `BDP\BRMS.StdRules\Modules\Scripting\` (about 3,080 lines across 22 files `[V]`, `wc -l`). `BDP\BRMS.Core` only has the plugin abstractions (`Plugin<T>`, `RuleManager`, `NuGetPackageLoader`) and no JS (grep for jint/clearscript in `BRMS.Core/*.cs` found nothing `[V]`; ClearScript appears only in `BRMS.Core/BRMS.StdRules.csproj`, which is a stray copy of the StdRules csproj `[V]`). `BRMS.Engine` has no JS either (grep found no ClearScript usage `[V]`).

Engine: ClearScript V8 7.5.0 with ICUData and native win-x64/linux-x64 (`BDP\BRMS.StdRules\BRMS.StdRules.csproj:5-9` `[V]`). Same package and version as the framework worker (`FW\AITaskAgent.Extensions.Script.Worker\*.csproj`, ClearScript 7.5.0 `[V]`).

### 1.1 Mechanism A: `ScriptProgramBuilder.AddHostedObject` (global registry, effectively unused)
- `ScriptProgramBuilder.cs:12` holds one static shared `V8Runtime`; `:46` `AddHostedObject(string name, Func<object> factory)` stores into a static dictionary `[V]`.
- `:58-62` `CreateEngine()` makes an engine per rule and calls `engine.AddHostObject(obj.Key, obj.Value)` for each entry `[V]`.
- Observation: it passes `obj.Value`, the `Func<object>` itself, not `factory()`. In ClearScript that exposes the delegate as a callable, so JS would have to write `name()`. `[I]` (I know ClearScript semantics, I did not run it); the "factory" name suggests the author expected an instance per engine. Likely a latent bug.
- No caller of `AddHostedObject` in `BRMS.StdRules`, `BRMS.Engine`, `BRMS.Core`, `BRMS.StdRules.Tests`, `BDPService`, `BDP.Processing.Core` `[V]` (grep per directory). The rest of the solution and NuGet plugins were not searched: it may be used from an external plugin `[?]`.

### 1.2 Mechanism B: library `.js` files (`LibPath`), dead code
- `StdRulesConfiguration.cs:21` declares `string? LibPath` ("directory containing JavaScript library files to load and compile into the script engine") `[V]`.
- `ScriptProgramBuilder.cs:23-39` is the commented-out `Configure`: it would concatenate every `*.js` under `LibPath` and compile it into `_compiledScript` `[V]`. `:14` `_compiledScript` is `static readonly` and never assigned, so `:63-66` (`if (_compiledScript != null) engine.Execute(...)`) never runs `[V]`.
- Net: the intended design of "common JS libraries" exists on paper, was disabled, and nothing calls `Configure`. Do not cite it as working behaviour.
- The commented design also has a flaw worth not copying: it compiles on `_runtime.CreateScriptEngine()` (one engine) and then executes the compiled script in other engines, which ClearScript allows only for engines of the same runtime `[I]`, and all engines share the runtime, which is a cross-rule shared-state risk.

### 1.3 Mechanism C: per-rule host objects (`AddHostObject` / `ConfigureHostObjects`)
This is the one that works.
- `JsScriptRule.cs:75-78` `AddHostObject(name, obj)` wraps `JObject.FromObject(obj)` in a `DynamicObjectWrapper` and queues it `[V]` (so these "host objects" are JSON snapshots, not live services).
- `:99` always registers `console` (`ConsoleLogger`, which also does `Console.WriteLine`, `ConsoleLogger.cs:28,43,58` `[V]`).
- `:102` `ConfigureHostObjects()` is a virtual hook with the comment example `_engine.AddHostObject("db", new DbJsHostService(...))` (`:132` `[V]`). `DbJsHostService` does not exist in the repos I searched `[V]` (no hit). So the hook is documented but no implementation is present.
- `:105-108` registers queued objects; `:111-117` wraps the expression as `function rule_<guid>(context, <extra params>) { return <expr>; }` and keeps the delegate `[V]`. `AddFunctionParameter` (`:85`) adds extra function parameters `[V]`; nobody in the searched code calls it.
- `JsDynamicScriptRule.cs:30-40` builds an `ExpandoObject` `parameters` from `AdditionalParameters` and exposes it with `AddHostObject("parameters", ...)`; `:48` `AddCustomParameters` is an empty virtual extension point `[V]`.
- Call-time context: `:158-166` builds an anonymous object `{ oldValue, newValue, source }` with `DynamicObjectWrapper` (JObject proxied through `DynamicObject`) and invokes the function `[V]`.

### 1.4 What `DynamicRuleManager` really does
It does not add functions or libraries. It registers **rule types**: `DynamicRuleManager.cs:80-105` generates a .NET class with `Reflection.Emit` per `JsRules` entry (subclass of `DynamicJsScriptValidator/Normalizer/Transformation`, `:156-165`) and calls `RuleManager.AddRule(type, factory, ruleId)` at `:103` `[V]`. The JS is just the `Expression` string of the config (`DynamicRuleConfiguration.cs:23`) `[V]`. Lifecycle: `StdRulesPlugin.Register` calls `new DynamicRuleManager(config).LoadRulesAsync(logger)` at startup (`StdRulesPlugin.cs:28-30` `[V]`); the plugin itself is loaded by `PluginLoader.LoadPluginsAsync` (`BRMS.Engine\Services\BRMSEngine.cs:123` `[V]`) from NuGet (`BRMS.Core\Core\NugetPackageLoader.cs`, existence `[V]`, internals not read `[?]`). Types are never unloaded; a duplicate key throws (`DynamicRuleManager.cs:84-87` `[V]`).

The analogue of that for us is "declare a reusable step/rule with a name, parameters and a JSON Schema so the UI renders a form", which the framework already covers with `IStepRegistry.Describe`. It is not the analogue of "add functions".

Documentation of the JS rules (`BDP\specs\how-to\how-to-brms-js-rules.md`, 85 lines `[V]`) lists only `context.oldValue/newValue/source` and `parameters`; there is no mention of helper functions, `require` or libraries.

### 1.5 Security and isolation in BRMS
- In-process: the engine runs in the service process (`JsScriptRule.cs:95`) `[V]`.
- No limits: `CreateEngine` (`ScriptProgramBuilder.cs:56-68`) sets no heap, stack, timeout or `Interrupt` `[V]`. A rule with `while(true){}` blocks a processing thread `[I]` (no watchdog found in the files read).
- `DisableTypeRestriction` and `AddHostType` are not used; host objects are `DynamicObject`/`ExpandoObject`/`ConsoleLogger` only `[V]` (read). With ClearScript defaults that limits what JS can reach to the public surface of the passed objects `[I]`.
- Shared `V8Runtime` across all engines (`ScriptProgramBuilder.cs:12`) `[V]`: one runtime for all rules means one heap constraint domain and serialization of script execution per runtime `[I]`.
- Lifecycle: engine created lazily on first execution and disposed with the rule (`JsScriptRule.cs:150-153`, `:182-189` `[V]` partially; `Dispose` read).

### 1.6 Local copy `Designer/` (parked, Jint)
- Not in the build (`Designer/README.md`: "parked ... excluded from `AITaskAgent.slnx`") `[V]`, no consumers.
- `Designer/Scripting/JsEngine.cs:56-62` Jint with `TimeoutInterval`, `LimitRecursion(100)`, `MaxStatements(10000)`, `Strict()` `[V]`; `:65-70` exposes only `console` `[V]`; `Execute` sets `context` and "additional parameters" as plain values (`:95-107`) `[V]`; `ExecuteFunction` wraps `function(input, output)` (`:140-144`) `[V]`.
- `Designer/Dynamic/DynamicJsSteps.cs:46-66` runs the expression in `Task.Run`, new engine per step instance (`_engine ??= new JsEngine()`, `:50`) `[V]`. `DynamicStepManager.cs:101-137` is the `Reflection.Emit` registry (copy of `DynamicRuleManager`) `[V]`.
- Conclusion: it is a smaller, older, Jint-based ancestor with no function-injection mechanism. Not a source for design, apart from the (already superseded) idea of per-step "additional parameters".

### 1.7 Lessons from BRMS
1. The extension points BRMS names (hosted objects, lib path, host-object hook) are the right concepts but half-built: nobody wired them. Our design should ship with a first consumer and tests.
2. Its host objects are JSON snapshots wrapped in `DynamicObject`; we already have the equivalent in `context.get/set`.
3. In-process with no limits is exactly what the framework moved away from.
4. A library mechanism needs a defined load order and a single place for compile: BRMS's commented code concatenates all `*.js` in directory order (`:27-31`), which makes load order dependent on file system enumeration `[V]`.

## 2. How ScriptStep works today

### 2.1 What the script sees
- Globals: `log`, `console` (frozen, 5 methods), `context` (frozen `get`/`set`), and the function arguments `input` and `context` (`ScriptRunner.cs:28-51` bootstrap `[V]`; `:130-131` wrapper `(function (input, context) { <script> \n})(JSON.parse(__input), globalThis.context)` `[V]`).
- The bootstrap receives three delegates `__hostLog/__hostGet/__hostSet`, captures them in a closure and deletes the globals (`:49-50`) `[V]`. Delegates registered at `:116-118` `[V]`.
- Engine flags: `V8ScriptEngineFlags.DisableGlobalMembers`, `DisableTypeRestriction = false`, no `AddHostType/AddHostObject` (`:104-112`) `[V]`. No document loader or modules configured `[V]` (not present in the file).

### 2.2 The protocol
- Envelope: JSON lines on stdin/stdout, kind in `t` (`ScriptProtocol.cs:55-65` `[V]`). Worker to host: `ready`, `log`, `get`, `set`, `result`. Host to worker: `run`, `value` (`ScriptProtocol.cs:50-53` `[V]`).
- `run` carries `name, script, input, timeoutMs, heapMb, stackKb` only (`ScriptWorkerClient.cs:160-169` `[V]`; parsed at `Worker/Program.cs:34-40` `[V]`; record types `ScriptWorkerClient.cs:11`, `ScriptRunner.cs:10` `[V]`).
- `get` is the only synchronous round trip: the worker sends `get`, blocks on `channel.Read()` (`ScriptRunner.cs:197-203` `[V]`); the host answers inline from the same loop (`ScriptWorkerClient.cs:190-196` `[V]`). The reply has only `json` (null for absent); there is no error field `[V]`. `set` and `log` are fire-and-forget (`:184-189`) `[V]`.
- Host side surface: `IScriptHostBridge` with `Get/Set/Log` (`ScriptWorkerClient.cs:17-27` `[V]`), implemented by the private `ContextBridge` inside `ScriptStep` (`ScriptStep.cs:189-222` `[V]`). `Get` only exposes JSON-like values (`:199-206` `[V]`).
- Deadline: `run.CancelAfter(TimeoutMs + 2 s)` for the whole conversation, including time the host spends answering a `get` (`ScriptWorkerClient.cs:172` `[V]`). The worker's own timer calls `engine.Interrupt()` at `TimeoutMs` (`ScriptRunner.cs:137-148` `[V]`), but a worker blocked in `channel.Read()` cannot be interrupted; the host kill is the backstop `[I]` from the code shape.

### 2.3 What is exposed or not because of the process boundary
| Capability | Possible? | Notes |
|---|---|---|
| Pure JS code (prelude, utilities, regex, JSON, `Date`, `Math`, `Intl`) | Yes, no boundary | V8 has these. ICU data is referenced (`Worker.csproj` ICUData `[V]`) but `InvariantGlobalization=true` (`Worker.csproj` `[V]`) is about .NET, not V8 `Intl`; `Intl` locale behaviour not verified `[?]`. |
| Worker-native function (.NET delegate living in the worker): hash, base64, GUID, path normalisation, culture-free date formatting, big regex | Yes, no pipe | The worker is a .NET exe with ClearScript; any `Func<>` can be given with `engine.Script.x = ...` as is done at `ScriptRunner.cs:116-118` `[V]`. Costs memory inside the capped process; cannot touch host state. |
| Host function (read an allowed file, call a registered C# function, read config) | Yes, over the pipe | One request/response per call, JSON strings only. Needs new message kinds and an error channel. |
| Passing live .NET objects or delegates from host to worker | No | The worker is a different process; only JSON crosses. `AddHostObject` of a host service is impossible `[V]` by architecture. |
| File system, network, process from the worker itself | Possible in the .NET worker, but should not be | Job object restricts memory and process creation (README:42, `WindowsJobObject.cs` exists `[V]`; its other limits not read `[?]`); it does not restrict file reads `[I]`. Keep I/O in the host with an allow-list. |
| JS modules / `require` | Not today | Needs a source map delivered per run, or ClearScript's document loader `[?]`. |

### 2.4 Cost of crossing the border
- Per-call cost: one line out, one line in, JSON string in and out, double encoding (a JSON payload inside a JSON string, as `get` does today). `[I]` Microseconds to a millisecond per call on a local pipe, negligible next to the 0.8 s startup (README:48 `[V]`). A script that calls a host function 100,000 times in a loop would hurt; the `timeoutMs` bounds it.
- Payload size: one line per message; stdin buffer 64 KB, a line can be larger (`Program.cs:22-23` `[V]`). A big file (MBs) returned in one line works but doubles in memory (host string, JSON-escaped string, V8 string) `[I]`; needs a size cap per call and per run.
- Reentrancy: the worker is single-threaded on the channel by contract (`ScriptProtocol.cs:50-53` remark `[V]`); host functions must not call back into the worker.
- The host must stay responsive: `ConverseAsync` answers inline (`:190-196`), so a slow host function consumes the script's own time budget (good: bounded) but blocks reading `log` lines meanwhile (harmless).
- Asynchronous host functions: the JS side can only be synchronous in this design (the worker blocks); JS promises would add nothing because the worker has no event loop that continues while a call is pending `[I]`. The host side may be `async` (the loop is already `async`, `ScriptWorkerClient.cs:124`).
- Startup per run: whatever is added at bootstrap (prelude compile) is paid on each execution because each execution is a new process `[V]` (README:46-48). V8 code cache across processes: possible with ClearScript `V8CacheKind` on the `DocumentInfo`/`Compile` APIs `[?]` (not verified against 7.5.0).

### 2.5 Facts about the step and registry relevant to design
- `ScriptStep` has only a parameterless constructor and a `string name` constructor (`ScriptStep.cs:87-96` `[V]`); it holds no `IServiceProvider`. The registry says `IStepRegistry.Create(tag, config, services)` takes an optional provider (`IStepRegistry.cs:55` `[V]`), and ARCHITECTURE-v2 says `Create` "walks the same constructors as with one, using only default values" (`docs/ARCHITECTURE-v2.md:61` `[V]`). So a constructor taking an optional `IScriptLibraryCatalog` should be viable `[I]` (I did not read `StepRegistry.cs`).
- `ScriptExtension.Register` only does `builder.AddStep<ScriptStep>()` (`ScriptExtension.cs:19` `[V]`). `IExtension` already has `ConfigurationType` and `ConfigurationModel()` (JSON Schema of extension config) (`IExtension.cs` `[V]`), and `IExtensionBuilder` exposes `Services` (`IExtension.cs:40-` `[V]`): the registration of functions can ride on that.
- Step description and designer form come from public properties with `[Description]` through `IStepRegistry.Describe` (`ScriptStep.cs:99-132` properties `[V]`; `Describe` signature `IStepRegistry.cs:59,66` `[V]`).
- Designer packages: `manifest.json` has `[JsonExtensionData] Extra` that preserves unknown keys (`PackageManifest.cs:95` `[V]`), a `FormatVersion` of 1 (`:16,60` `[V]`), and the zip already has `schemas/` and `prompts/` folders (`PackageManifest.cs:32` `[V]`; read via `PackageArchive.cs:98` and `LibraryStore.cs:678-679` `[V]`). So a `scripts/` folder follows an existing pattern.

## 3. Design options

Common vocabulary: **prelude** = JS text run before the user script in the same engine; **worker-native** = .NET delegate inside the worker; **host function** = executed in the host process, called through the pipe.

### O1. YAML-inline prelude (`prelude:` / `libs:` property on `ScriptStep`)
- What: a new string property (or list of named snippets) sent in `run`; the bootstrap executes it before the script.
- Pros: trivial; purely JS; no new trust surface; portable inside a package (it is in the YAML).
- Cons: copy/paste across steps; no reuse; no catalog; no static name check.
- Protocol impact: `run` gains `prelude` (string). Back/forward compatibility: an old worker ignores unknown fields (it reads by key, `Program.cs:34-40` `[V]`), so it would silently not run the prelude; version the protocol (see section 5 of this list).
- Resources: prelude counts against the same time/heap; compile cost per run.
- Cost: S.

### O2. Named JS libraries from a registry + `require(name)` (CommonJS-style)
- What: libraries registered in DI (`IScriptLibrary { Name, Source, Version }`) by extensions or by the app; a step declares `uses: [text, dates]` (or the script calls `require('text')`). The host sends only the declared libraries' sources in `run`. Bootstrap defines `require` over a closed map (no file access) and a module wrapper `(function(module, exports, require){...})`.
- Pros: reuse, versioning, a catalog (name, description, function signatures) the designer can list; `require` is a familiar idiom; closed map keeps the "no `require`/`module` from the filesystem" guarantee (the docs state they do not exist today, `yaml-pipelines.md:211` `[V]`; this would add them, so docs must change).
- Cons: loader semantics to define (cycles, caching per run, errors with the module name and line); hybrid of declared vs lazily required needs a rule: recommend declared only, so the host can send the exact set and validation is static.
- Protocol impact: `run` gains `modules: { name: source }` (or a list); no new runtime messages. ClearScript's own module/document loader is an alternative, but I have not verified its API for 7.5.0 `[?]`; a hand-written closed `require` is a few dozen lines `[I]`.
- Resources: total prelude size cap (for example 256 KB per run) and a compile-time check.
- Cost: M.

### O3. Worker-native functions (a fixed "standard library" implemented in C# inside the worker)
- What: namespaced objects built by the worker bootstrap (`text`, `hash`, `path`, `date`, `json`) backed by .NET delegates (`sha256`, `base64`, `Path.Combine` logic, `TimeZoneInfo`, regex with timeout).
- Pros: capabilities V8 does not have (hashing, base64, GUID, culture/timezone) with no round trip and no host exposure; deterministic; easy to unit test (worker project); no trust issue because they are pure functions.
- Cons: they live in the worker assembly, so adding one needs a framework release, not an app plugin (unless the worker loads app assemblies, which breaks "bare worker" and is rejected below); memory they use counts against the worker limit.
- Protocol impact: none.
- Cost: S for a small fixed set; M if it should be extensible (extensibility needs assembly loading in the worker, not recommended).

### O4. Host functions over the pipe (`host.call(name, args)`)
- What: C# functions registered in the app (`IScriptHostFunction { Name, Description, ArgsSchema, Handler(JToken args, ScriptCallContext ctx) }`) and invoked by the script as `fn.<name>(args)` or `host.call('readFile', {path})`.
- Pros: the only way to reach host state (read an allowed file, query a registered service, call business C# code); governed in one place (allow-list, size caps, auditing); same trust model as `context.get/set`.
- Cons: biggest security surface; must be explicitly allowed per step/package; new protocol messages; error handling.
- Protocol impact: replace the single-purpose `get` with a generic `call` (worker to host: `id`, `fn`, `args` as JSON string) and reply `value` gaining `ok` and `error` (today only `json`: `ScriptWorkerClient.cs:191-195` `[V]`). Keep `get/set/log` for compatibility, or re-implement them on top of `call`. Reply correlation by `id` is not needed while the worker is strictly sequential, but add it now to avoid a later break.
- Resources: per-run call budget (max calls, max bytes in/out), per-call timeout inside the step's `timeoutMs`; a function that needs longer should be refused at registration, not silently killed. Errors from a host function become a JS exception inside the script (so the script can `try/catch`) and, if uncaught, the step error includes the function name.
- Host-function examples to ship: `file.readText(path)` restricted to configured roots (canonicalise, reject `..`, symlinks and UNC unless allowed, size cap, text only), `file.exists`, `config.get(name)` (read-only values from an options object), plus the extension point for app functions.
- Cost: M (protocol, host registry, allow-list, tests); L if write access or streaming is added (not recommended).

### O5. Package-carried code (`scripts/` folder in the designer zip + manifest declaration)
- What: the zip carries `scripts/<name>.js`; `manifest.json` gets a section such as `scripts: [{name, file, sha256}]` and `requires: { hostFunctions: ["file.readText"], libraries: ["text@1"] }`. When a workflow is opened or run, the designer/host resolves each requirement against the catalog and refuses to run if something is missing or not allowed.
- Pros: a package is self-contained and portable; the manifest is the audit surface (what code runs, what capabilities it asks for); fits the existing `schemas/` and `prompts/` pattern (`PackageManifest.cs:32` `[V]`); old builds preserve unknown manifest keys (`Extra`, `:95` `[V]`), so round-trip is safe even in a build that does not understand them, but such a build would run the workflow without the code, so bump `formatVersion` to make older builds refuse (`PackageManifest.cs:16` says "highest this build reads" `[V]`; refusal behaviour not verified `[?]`).
- Cons: code coming from a zip is **untrusted** (someone else's package): it may run only as JS in the sandbox (O1/O2 tier); host-function access needs an explicit approval/allow-list on the importing side, never a self-grant by the manifest. Hash pins stop silent edits, not malicious packages.
- Protocol impact: none beyond O2/O4; the loading is in the host.
- Cost: M (designer + manifest + validation); L with an approval UI.

### O6. Registration through `IExtension` and a catalog visible to the designer
- What: `ScriptExtension` (or other extensions) contribute `IScriptLibrary`/`IScriptHostFunction` entries via `IExtensionBuilder.Services`; a `ScriptCatalog` service lists them with name, description, signature, a JSON Schema for arguments and examples. `IStepRegistry.Describe("ScriptStep")` is extended (or a sibling endpoint is added) so the designer shows a picker and autocompletion.
- Pros: the framework's own way to extend (`IExtension.Register`, no dynamic assembly loading: `IExtension.cs` doc comment `[V]`); consistent with how `LlmStep` gets its roles/providers `[I]`; catalog is the single source for validation and UI.
- Cons: `ScriptStep` needs access to the catalog (constructor injection, section 2.5); a YAML-only host (`CreateFromFolderAsync`) must register the extension before building steps.
- Cost: M.

### O7. Static validation before running
- What: when the step is built (not at run time), check: `uses/libs` names exist in the catalog; every host function the script mentions (`fn.x(`, `host.call('x'`) is allowed; the script, prelude and libraries parse. Parsing needs V8: do it in a worker (a `validate` message that compiles without running), or with a JS parser in the host (adds a dependency).
- Pros: errors at pipeline load with the step id, like other step validation; avoids paying 0.8 s to find a typo.
- Cons: name detection by regex over source is approximate (dynamic access defeats it), so it can only be advisory for host functions; the hard guarantee must stay at run time (the worker only defines what the step declared).
- Protocol impact: optional `validate` run mode in the worker, or a flag on `run` (`compileOnly`).
- Cost: S for name checks against the catalog; M for syntax check through the worker.

### Rejected or discouraged
- Load assemblies or C# plugins inside the worker: breaks the "bare worker" guarantee and the "no dynamic loading" rule in `IExtension.cs` `[V]`. Host functions cover the need.
- Put live `AddHostObject` services in the worker (BRMS style): impossible across the process boundary and contrary to the isolation goal.
- `eval`/`Function` hardening: out of scope; the isolation is the process, not the language.
- Persisting state between runs in the worker (warm worker pool): would remove the 0.8 s but changes the one-process-per-execution guarantee; separate decision, not needed for this feature.

### Protocol versioning (applies to all)
The shared `ScriptProtocol.cs` is compiled into both projects (`Worker.csproj` link `[V]`), and host and worker ship together (README:49-51 `[V]`), so skew is limited to a stale worker found by `AITASKAGENT_SCRIPT_WORKER`. Add a `proto` integer to `ready` and to `run` so a mismatch fails with a clear message instead of silently ignoring a `prelude` (today an unknown field is ignored `[V]`, `Program.cs:34-40`).

## 4. Recommendation

Build in this order, each step usable on its own:

1. **Run message and bootstrap generalisation (S).** Add `proto`, `modules` (name to source) and a closed `require` to the bootstrap; keep `get/set/log` untouched. Plus a per-run size cap on modules. This delivers O1 + O2 and "librerias JS propias".
2. **Worker-native standard library (S-M, O3).** A small fixed set, namespaced and frozen, for what V8 lacks: `hash.sha256/md5`, `base64`, `uuid`, `path.*` (pure string logic), `date.format/parse` with explicit time zone and invariant culture, `text.*` helpers that are easier in C#. Pure JS helpers (JSON utilities, array/regex helpers) go in the prelude library instead. Keeps worker I/O-free.
3. **Host functions (M, O4).** Generic `call` request and an `ok/error` reply; `IScriptHostFunction` registry in DI; ship two built-ins (`file.readText/exists` under configured roots, `config.get`); everything default-deny, enabled per step by a declared list (`allow: [file.readText]`) and, for packages, by an allow-list on the host that the manifest cannot widen.
4. **Catalog and `IExtension` registration (M, O6).** `ScriptCatalog` service; extend `Describe` or add a catalog endpoint; designer picker and autocompletion.
5. **Package declaration (M, O5)** and **static name validation (S, O7)** once 1-4 exist: `scripts/` folder, `manifest.requires`, `formatVersion` bump, validation at open time.

Rationale in three lines: the worker already has the exact injection point (closure over delegates) so tiers 1-2 are cheap and risk-free; the only thing that needs real security design is tier 3, so it goes behind a default-deny list; BRMS shows what happens when the hooks are added without a consumer or a catalog (dead `LibPath`, unused `AddHostedObject`), so each step above should land with a test and a sample.

Estimated total: M-L (about 5 steps; steps 1-2 are days, steps 3-5 are the bulk). Breaking changes: none for existing YAML; `docs/guides/yaml-pipelines.md:211` and `ScriptStep` remarks (`ScriptStep.cs:34-39`) must be updated since they state that `require`/`module` do not exist and nothing is exposed.

Resource-limit rules to carry over: modules and prelude count in `timeoutMs` and heap; add `maxCalls` and `maxCallBytes` per run for host functions (defaults, with hard ceilings that YAML cannot raise, in the same spirit as `ScriptLimits.MaxHeapMbCeiling`, `ScriptProtocol.cs:16` `[V]`); host function time is already bounded by `TimeoutMs + 2 s` (`ScriptWorkerClient.cs:172` `[V]`).

## 5. What I could not check

- Whether anything outside the searched directories (rest of the BDP solution, NuGet plugins, `BRMS.StdRules.Tests`) calls `ScriptProgramBuilder.AddHostedObject`; I grepped `BRMS.StdRules`, `BRMS.Engine`, `BRMS.Core`, `BRMS.StdRules.Tests`, `BDPService`, `BDP.Processing.Core` only. A whole-NAS grep timed out (zip files, `bin/obj`).
- How ClearScript 7.5.0 treats `AddHostObject(name, Func<object>)` at runtime (delegate callable vs. invoked); inferred from API knowledge, not run.
- `NuGetPackageLoader` internals and ADR-008 (`specs/adrs/008-plugins-nuget-dynamico.md` is listed in a grep hit but my later `sed` on that path failed from the wrong working directory; I did not re-read it). This affects only how BRMS ships plugins, not the JS question.
- ClearScript document loader / `V8CacheKind` availability and behaviour for modules and cross-process code cache in 7.5.0; assumed a hand-written closed `require`.
- `StepRegistry.cs` constructor selection with a service provider (read only the interface and the doc claim), so "ScriptStep can take an optional catalog in its constructor" is `[I]`.
- `WindowsJobObject.cs` limits beyond memory and process count (file system is assumed unrestricted).
- V8 `Intl`/ICU behaviour in the worker with `InvariantGlobalization=true`.
- Performance numbers for a host-function round trip: none measured; only reasoned.
- I did not run any build, test or worker; nothing was modified in the repo or the NAS.


---

# Study B: CodebaseAnalysis steps vs framework LlmStep / designer (YAML v2)

Read-only study. Paths are relative to `C:\GeminiCli\AITask\AITaskAgent` unless stated. `CA` = `Samples/CodebaseAnalysis.Core`.
Evidence levels: `[V]` line read, `[I]` inferred, `[?]` not known. Recommendations are not marked.

## 0. Direct answer to the user's question

**Are the workflow LlmSteps equal or replicable to the current CodebaseAnalysis LlmSteps, beyond having been inherited for convenience?**

- **Same engine underneath: yes.** `CustomStatelessTemplateLlmStep<TIn,TOut>` derives from `StatelessTemplateLlmStep<,>` -> `TemplateLlmStep<,>` -> `BaseLlmStep<,>` -> `BaseLlmStep` `[V CA/Steps/CustomStatelessTemplateLlmStep.cs:21; AITaskAgent.Extensions.Llm/Steps/StatelessTemplateLlmStep.cs:25-33; TemplateLlmStep.cs:17; BaseLlmStep.cs:38]`. `YamlLlmStep` derives directly from `BaseLlmStep` `[V YamlLlmStep.cs:57]`. Retry, fallback providers, tool loop, repeat detector, JSON repair feedback, telemetry all live in `BaseLlmStep` and are shared `[V BaseLlmStep.cs:282-400, BaseLlmStep.LlmInvocation.cs:14-35]`.
- **Not equal in behaviour: `CustomStatelessTemplateLlmStep` adds six things that `YamlLlmStep` has no property for** (section 2.2): (1) "final answer without tools" attempt after the tool limit or a cut loop, (2) report post-processing + writing to disk + provenance, (3) note protocol (`TagHandlers`), (4) typed In/Out (`ReportFacetInput` -> `SingleReportResult`) instead of JSON, (5) templates through `AnalysisTemplateProvider`/`AnalysisTemplateRenderer` (flattened snake_case variables) instead of the `{{ input.x }}` model, (6) tools taken from the static `AvailableTools.Instance` with a per-run `DatabasePath` baked into each tool.
- **Replicable with configuration alone today:** the "plain" LLM calls: role, system/user prompt (template name), clean conversation (`useConversationHistory: false`), tool names, `maxToolIterations`, `timeout`, `maxRetries`, optional JSON output schema. That covers, in practice, the *core* of Enricher, DomainNaming, ExecutiveSynthesizer, Verifier, SharedPass reader/judge, and the *LLM call* of each auditor/investigation.
- **Not replicable without framework changes:** (1), (3) and (6) above, plus dynamic fan-out (investigations per subsystem), run-time timeouts written into sibling steps (`TimeBudgetStep`), and the tolerant parallel. Minimal changes are in section 2.4.
- **Honest caveat:** "replicable" means the *LLM call*. 14 of the 17 files in `Steps/` are hybrids (deterministic C# + SQLite + processes + one embedded LLM sub-step); the LLM part is a small fraction of each. The realistic design is **registered C# steps that keep the deterministic work + YAML `LlmStep`s for the pure LLM calls**, not an all-YAML pipeline.

## 1. Inventory

### 1.0 Scope facts

- `Steps/` has 17 `.cs` files `[V directory listing]`. Of those: 14 are real steps implementing `IStep<TIn,TOut>` directly (not `StepBase`) `[V grep "IStep<" CA/Steps/*.cs, class lines cited below]`; 1 is `TolerantReportsParallelStep : ParallelStep` `[V TolerantReportsParallelStep.cs:17]`; 1 is the LLM base `CustomStatelessTemplateLlmStep` `[V :21]`; 1 is a static helper (`ProvidedDocumentationFacet`, `[V ProvidedDocumentationFacet.cs:17]`), not a step.
- No `IStep`/`StepBase` class exists in `Reports/`, `Preparation/`, `Enrichment/`, `Knowledge/`, `Notes/`, `Hosting/`, `Support/`, `Tools/`, `Progress/` `[V grep for ": IStep|: StepBase|IStep<|: ParallelStep" returned nothing]`. They are services/helpers called from the steps. LLM steps outside `Steps/`: `SharedPassReaderStep` and `SharedPassJudgeStep` (both `CustomStatelessTemplateLlmStep` subclasses) `[V SharedPass/BatchReader.cs:37, FindingJudge.cs:35]`, and the chat's `AuditedChatStep : BaseLlmStep<,>` `[V Chat/ChatAgent.cs:339-340]` (chat is not part of the pipeline).
- Common pattern: every step reads the run state from `PipelineContext.Metadata` through the sample's own `context.Get<T>/Set` `[V Support/PipelineContextExtensions.cs:7-15]`, keyed by `AnalysisContextKeys` `[V Progress/AnalysisContextKeys.cs]`. Values are .NET objects (`AnalysisPipelineInput`, `DomainMap`, `RepositoryInventory`, `CoveragePlan`...). `ScriptStep`'s `context.get` only exposes JSON-like values and never arbitrary .NET objects `[V AITaskAgent.Extensions.Script/Steps/ScriptStep.cs:25-30 (remarks)]`.
- Typed data carriers: `AnalysisPipelineInput` (a `StepResult`), `ReportFacetInput` (~100 string properties of JSON facets), `GraphStatusResult`, `SingleReportResult`, `ParallelResult` `[V Models/AnalysisPipelineInput.cs:30; Models/ReportFacetInput.cs:6]`. `IStep<TIn,TOut>.ExecuteAsync` throws if the input type is not assignable `[V AITaskAgent.Abstractions/Abstractions/IStep.cs:77-85]`. In YAML the input is a `JsonStepResult` `[V AITaskAgent.Yaml/Execution/YamlPipelineFactory.cs:55]`, so typed steps cannot be chained straight from YAML without an adapter (section 4).

### 1.1 Table (one row per step)

Legend: **A** = equivalent to `LlmStep` with config; **B** = replicable as JS `ScriptStep`; **C** = registered C#; **D** = orchestration. "Hyb" = hybrid, the dominant part is shown first.

| # | Step (file:class line) | Base / interface | What it does | Global state / services / files | In -> Out | Cat |
|---|---|---|---|---|---|---|
| 1 | `DetectAndIndexGraphStep` (`DetectAndIndexGraphStep.cs:16`) | `IStep<AnalysisPipelineInput,GraphStatusResult>` | Locates and runs the `codebase-memory` CLI to index the repo, backs up the previous run, ingests Office/Markdown, retires catalog reports from the graph | Seeds context keys `PipelineInput`, `CbmDbPath`, `KnowledgePaths` (`:37-39`); `IConfiguration` via `context.ServiceProvider`; SQLite DB; file backups under `<db>/backups`; child process; `CatalogReportsRetired` flag (`:170`) | `AnalysisPipelineInput` -> `GraphStatusResult` | C |
| 2 | `PrepareRegenerationStep` (`PrepareRegenerationStep.cs:16`) | same | Regeneration/verification entry: same context seeding as #1, checks DB and structural graph, ensures schema, retires catalog reports | Same keys (`:35`), SQLite, files | `AnalysisPipelineInput` -> `GraphStatusResult` | C |
| 3 | `LanguageEnricherStep` (`LanguageEnricherStep.cs:25`) | `IStep<GraphStatusResult,GraphStatusResult>` | Scans repo, runs Java analyzer (JDK process) and Struts/JSP plugin, reachability; records `enrichment_runs` | `context.Get<AnalysisPipelineInput>` (`:67`); sets `DetectedLanguages`; `IConfiguration` options (`JavaAnalyzerOptions`, `StrutsJspOptions`); SQLite; child process; own `Timeout` from options (`:51-54`) | `GraphStatusResult` -> same | C |
| 4 | `DotNetEnricherStep` (`DotNetEnricherStep.cs:24`) | same, ctor takes a fallback `IStep` | Runs the `Samples/DotNetEnricher` CLI (Roslyn) as process; if not applicable/failing, calls the LLM fallback (#5) | Reads `DetectedLanguages` from `context.Metadata` (`:66-70`); `AnalysisPipelineInput` (`:75`); SQLite `enrichment_runs`; process | `GraphStatusResult` -> same | C (+ Hyb with #5) |
| 5 | `SemanticEnrichmentStep` (`SemanticEnrichmentStep.cs:16`) | same, ctor takes `CustomStatelessTemplateLlmStep<EnrichmentBatchInput,LlmStringStepResult>` | Scans key files (controllers/routes/DI), asks the Enricher LLM for JSON, cleans fences, validates against property allow-lists, writes nodes/edges to SQLite, records the run | `AnalysisPipelineInput` (`:77`); SQLite; fingerprint cache; the LLM call at `:133` is invoked directly with no middleware | `GraphStatusResult` -> same | Hyb: LLM call = A; scan/parse/write = C |
| 6 | `SqlFacetExtractorStep` (`SqlFacetExtractorStep.cs:12`, 1379 lines) | `IStep<GraphStatusResult,ReportFacetInput>` | ~hundreds of SQL queries on the graph DB producing the JSON facets (routes, flows, dead code, smells, DI, compiler facts...) | `AnalysisPipelineInput` (`:38`); sets `GraphProvenance` in context; `Documents.DocumentOptions` from config; SQLite | `GraphStatusResult` -> `ReportFacetInput` | C |
| 7 | `RepositoryInventoryStep` (`RepositoryInventoryStep.cs:28`) | `IStep<ReportFacetInput,ReportFacetInput>` | Inventory: modules, source sets, entry points, tables (also scans SQL in strings), languages, production LOC; writes `_inventory.json` artifact | Reads `AnalysisPipelineInput` (`:144-149`), `GraphProvenance` (`:202`); sets `RepositoryInventory`, `InventoryEntryPoints`, `InventorySqlTables`; `PreparationOptions.FromServices`; files under reports path; SQLite | `ReportFacetInput` -> same (mutated) | C |
| 8 | `DomainDiscoveryStep` (`DomainDiscoveryStep.cs:32`) | same | Builds the subsystem map (structure + graph + vocabulary clustering) deterministically, then names subsystems with an LLM in batches under its own time limit; writes `_domain_map.json` and `Subsystem` graph nodes; can reuse saved map on regeneration | Reads `RepositoryInventory` (`:101`), `InventoryEntryPoints`/`InventorySqlTables` (`:218-219`), `AnalysisPipelineInput` (`:111,154,206`); sets `DomainMap`; ctor takes naming LLM step (`:42,56`), called at `:242`; files; SQLite | `ReportFacetInput` -> same | Hyb: map = C; naming call = A |
| 9 | `CoveragePlanningStep` (`CoveragePlanningStep.cs:23`) | same | Scores and distributes subsystems per report (`CoveragePlanner`), reads investigation questions from templates, writes `_coverage_plan.json` | Reads `DomainMap` (`:65`), `AnalysisPipelineInput` (`:74`); sets `CoveragePlan`; ctor takes `ITemplateProvider` + `AuditorStepRegistry` | `ReportFacetInput` -> same | C (pure compute, but over .NET objects + templates + files) |
| 10 | `TimeBudgetStep` (`TimeBudgetStep.cs:33`) | same | Computes per-report/per-investigation time budget from code size and **writes `Timeout` into sibling step objects** through `AuditorStepRegistry`; may trim subsystems to fit the pipeline ceiling | Reads `CoveragePlan` (`:82`), `AnalysisPipelineInput` (`:106`), `RepositoryInventory`/`DomainMap` (`:220-222`); `AnalysisTimeouts.FromServices`; mutates other steps' `Timeout` | `ReportFacetInput` -> same | C / D (cannot be YAML, see 3.4) |
| 11 | `SharedPassStep` (`SharedPassStep.cs:31`) | same; ctor takes two `LlmRole`s + `ITemplateProvider` + options | Optional shared reading pass: batches per subsystem, parallel LLM reads (`BatchReader`), deterministic consolidation, LLM judge (`FindingJudge`), cost/call/token/time caps, writes registry to graph and `shared_pass_registry.json` | Reads `DomainMap` (`:74`), `AnalysisPipelineInput` (`:93`); sets `SharedPassRegistry`; `SharedPassCostBudget` (hard caps); `Timeout` infinite on purpose (`:~62`); SQLite, files | `ReportFacetInput` -> same | C (LLM calls inside are A-like but orchestrated by code with budgets) |
| 12 | `SubsystemInvestigationReportStep` (`SubsystemInvestigationReportStep.cs:51`) | `IStep<ReportFacetInput,SingleReportResult>, IWritableTimeout, IInvestigationTimeouts` | Report in "mode B": runs one LLM investigation per chosen subsystem (up to N in parallel, semaphore), then a consolidation LLM call with the notes; degrades to the plain auditor | Reads `CoveragePlan` (`:170`), `DomainMap`/`RepositoryInventory` (`:182-183`), `AnalysisPipelineInput` (`:295`); re-implements retry loop `RunWithRetriesAsync` (`:179,219`); sub-steps invoked without middleware; per-investigation `CloneForBranch` | `ReportFacetInput` -> `SingleReportResult` | D + C (dynamic fan-out) with A inside |
| 13 | `TolerantReportsParallelStep` (`TolerantReportsParallelStep.cs:17`) | `ParallelStep` subclass | Clears the aggregated error unless all branches failed, so synthesis runs with the reports that exist | Sets `InputType = typeof(StepResult)` (`:23`); `context.Report` | any -> `ParallelResult` | D (tiny C) |
| 14 | `ExecutiveSynthesizerStep` (`ExecutiveSynthesizerStep.cs:60`) | `IStep<ParallelResult,AnalysisPipelineFinalResult>`, ctor takes the LLM sub-step | Collects branch results and on-disk reports, builds a digest (counts, critical findings, coverage), calls the synthesis LLM, falls back to a code-generated summary, writes `00_resumen_ejecutivo.md`, reincorporates reports into the graph | `AnalysisPipelineInput` (`:96`), `ReportFacetInput` from context/`StepResults` (`:193`), `PipelineStartedAt`, `CatalogReportsRetired`; files; SQLite via `KnowledgeGraphIngestor`; LLM at `:265` | `ParallelResult` -> `AnalysisPipelineFinalResult` | Hyb: LLM = A; digest = B (partly) ; write/reincorporate = C |
| 15 | `VerifyReportStep` (`VerifyReportStep.cs:24`) | `IStep<GraphStatusResult,VerificationResult>` | Reads a written report, validates deterministically, calls the verifier LLM (tools) under its own time limit, validates the output, writes new version via `ReportWriter` (keeps baseline for undo) | `AnalysisPipelineInput` (`:61`), `KnowledgePaths` (`:121`); files; `ReportValidator`; sub-step invoked directly | `GraphStatusResult` -> `VerificationResult` | Hyb: LLM = A; validate/write = C |
| 16 | `CustomStatelessTemplateLlmStep<TIn,TOut>` (`CustomStatelessTemplateLlmStep.cs:21`) | `StatelessTemplateLlmStep<,>` | The LLM building block used by all auditors, investigations, naming, enrichment, synthesis, verifier (details in section 2) | `context.Get<AnalysisPipelineInput>` (`:156`), `GraphProvenance` (`:200`), writes report files (`:165`), `NoteSessions` | `TIn` -> `TOut` | A + extras |
| 17 | `ProvidedDocumentationFacet` (`ProvidedDocumentationFacet.cs:17`) | static helper | JSON facet of user-provided documents (DocumentStore + graph links) | `DocumentStore`, SQLite | n/a | not a step; C |

Not in `Steps/` but part of the flow: `SharedPassReaderStep` (`BatchReader.cs:37`) and `SharedPassJudgeStep` (`FindingJudge.cs:35`) are `CustomStatelessTemplateLlmStep` subclasses. The reader overrides `BuildUserMessageAsync` to paint markers and substitute the code packet *after* rendering, because the template engine would interpret `{{ }}` and `@[ ]` inside source code `[V BatchReader.cs:44-52]`. That is a real behaviour a YAML `userPrompt` cannot express (the framework renders the prompt over the whole model).

### 1.2 Category summary

- **A (pure LlmStep equivalents, as the LLM call only):** Enricher call (#5), DomainNaming call (#8), Synthesis call (#14), Verifier call (#15), SharedPass reader/judge call (#11), each Auditor and Investigation call (#12/#16, with the extras of 2.2).
- **B (ScriptStep JS candidates):** only fragments, none whole: the digest of #14 (counting severities, building text) and the report markdown clean-up of #16 (`RemoveTextBeforeTitle`, `RemoveFileNameHeading`, meta line). A JS worker has no file/SQLite/process access `[V ScriptStep.cs:30-40 (remarks: bare V8, no .NET types, no file/network APIs)]`, so anything touching disk or the graph cannot be B.
- **C (registered C#):** #1, #2, #3, #4, #6, #7, #9, #10, #11, #14 (write+reincorporate), #15 (validate+write), #5 (parse+write), #8 (map).
- **D (orchestration):** #12 (dynamic fan-out), #13 (tolerant parallel), #10 (writes timeouts), plus the factory flow (section 3).

## 2. LlmStep analysis

### 2.1 What the two classes have in common (no work needed)

- Retry loop with error feedback to the model, on parse/validation errors `[V BaseLlmStep.cs:344-390]`; invoked by `RetryMiddleware`, which also calls `SwitchToNextFallback` `[V Execution/Middlewares/RetryMiddleware.cs:66,83]`.
- Provider fallbacks only for `LlmProviderException` `[V BaseLlmStep.cs:175-183]`.
- Tool loop with `MaxToolIterations` (default 5, `init`) `[V BaseLlmStep.cs:106]`; throws `ToolIterationLimitExceededException` `[V BaseLlmStep.LlmInvocation.cs:24-34]`; repeat detector that cuts the loop and sets `ToolLoopStopMessage` `[V BaseLlmStep.LlmInvocation.cs:253-356]`.
- Conversation compaction at `FinalizeAsync` `[V BaseLlmStep.cs:232-280]`.
- Per-invocation state in `LlmPipelineContext.StepState`, safe for parallel branches `[V BaseLlmStep.cs:128-160 region]`.
- Config visible in YAML: `profileName`, `userPrompt`, `systemPrompt` (public `SystemPrompt` of `BaseLlmStep`, not hidden `[V BaseLlmStep.cs:171, YamlLlmStep.cs hides only Prompt/Tools/TagHandlers/Enrichable]`), `useConversationHistory`, `tools`, plus inherited `maxRetries`, `timeout`, `maxToolIterations` `[V docs/guides/yaml-pipelines.md:178-182]`, `outputSchema`.

### 2.2 What `CustomStatelessTemplateLlmStep` adds, item by item

| # | Own behaviour | Where | In `YamlLlmStep`? | Replicable by YAML props today? |
|---|---|---|---|---|
| 1 | **No history** (clean conversation, uses role `MaxTokens` as window) | inherited from `StatelessTemplateLlmStep.GetConversationContext` `[V StatelessTemplateLlmStep.cs:62-77]` | `useConversationHistory: false` creates `new ConversationContext(history.MaxTokens, ...)` `[V YamlLlmStep.cs:~253-258]` | **Yes**, one nuance: the stateless step sizes the window with `role.Inference.MaxTokens ?? history.MaxTokens` `[V StatelessTemplateLlmStep.cs:41,71]`, the Yaml one with `history.MaxTokens`. Effect on long tool conversations `[?]` (not measured). |
| 2 | **Prompt by template name**, system prompt `role.SystemPrompt ?? "<prefix>_system"` | ctor `[V CustomStatelessTemplateLlmStep.cs:76-77]`; factory `[V AnalysisPipelineFactory.cs:550]` | `userPrompt`/`systemPrompt` accept template name or inline `[V YamlLlmStep.cs:100, PromptTemplateResolver.cs:26-33]` | **Yes** for names. But the *renderer* differs, see item 9. |
| 3 | **Tools by name** | `TemplateLlmStep` ctor: `AvailableTools.Instance.GetTools(names)` at construction `[V TemplateLlmStep.cs:39]` | `tools: [..]`, resolved at build from DI `ITool` / `ITools` `[V YamlLlmStep.cs:~155-185]` | **Yes** if the tools are registered in the designer's service collection (via `ConfigureEngineServices`, which runs last `[V EngineServices.cs:123]`). The sample's tools need `DatabasePath`/`RepositoryPath` at construction `[V Hosting/AnalysisToolRegistration.cs:23,35]`, i.e. known before the pipeline is built. |
| 4 | **Tool-limit final answer** (`ToolLimitFinalAnswerPrompt`): when `ToolIterationLimitExceeded` or `ToolLoopCut`, one more attempt on the *same conversation*, user message = that prompt, `tool_choice: none`, tools kept for cache | `[V CustomStatelessTemplateLlmStep.cs:33,102-112,214-226,250-265]` | **No**. The base fails the step on the limit (`ToolIterationLimitExceededException` is returned as error `[V BaseLlmStep.cs:355]`) | **No.** Needs framework change (2.4-F1). The sample's own test of this path: `CustomStatelessTemplateLlmStepTests.cs` `[I]`. Used by every auditor, investigation, verifier (`AnalysisPipelineFactory.cs:327,558`). |
| 5 | **Report post-processing** for `SingleReportResult`: set type and file name, strip note markers and text before title, `PopulateDerivedMetadata`, reconcile `critical=` with the severity matrix, warn if no `analysis-meta`, write provenance (model, template SHAs, graph fingerprint) | `[V :130-151]` | No (`YamlLlmStep` yields `LlmStringStepResult`/`LlmJsonStepResult`) | Not by LlmStep props. Text transforms could be a `ScriptStep` (B) over the string; provenance needs template SHAs and `GraphProvenance` from context (.NET) -> C. |
| 6 | **Writes the report to disk** (single write point, survives synthesis failure) + `ReportWritten` event | `[V :154-172]` | No | No. A registered C# step ("ReportSink") or a framework file-write step `[?]` (none found; the FileTools registered by the sample are read-only `[V AnalysisToolRegistration.cs:~30 comment]`). |
| 7 | **Note protocol**: `TagHandlers=[NoteTagHandler]`, `NoteSessions.Begin`, `NoteProtocolTool` wraps the tools to replace tool results by the model's notes | `[V :228-242,247-248,278-293; AnalysisToolRegistration.cs:80-82; AnalysisPipelineFactory.cs:560]` | `TagHandlers` is `[JsonIgnore]` (live objects) `[V YamlLlmStep.cs:~122-129]` | **No.** Needs a by-name handler registry (2.4-F2) and a way to wrap tools. Optional feature: `NoteProtocol:Auditors` / `Investigations` flags `[V AnalysisPipelineFactory.cs:424,434]`, so it can be left out in a first migration at a quality cost `[?]` (not measured). |
| 8 | **Per-call console/UI reporting** (`context.Report`, `🤖 [LLM] ...`, elapsed time, warnings for truncated output `FinishReason.Length`, missing `analysis-meta`) | `[V :89-128]` | Framework emits its own events/telemetry; sample's `IAnalysisReporter` events (`AnalysisEventMapper`) are not produced | Not by props. The web progress mapping depends on those messages and on step names `Auditor_*`, `Investigation_*`, `Verify_*`, `SynthesizerLlm`... `[V AnalysisPipelineFactory.cs:35-114; Progress/AnalysisEventMapper.cs]` `[I]` for the exact dependency. |
| 9 | **Template engine**: `AnalysisTemplateProvider` + `AnalysisTemplateRenderer` flatten `Input` properties (and `StepResult.Value`) to top-level snake_case variables, `{{ detected_languages }}`; `@[file.md]` includes | `[V Support/AnalysisTemplateRenderer.cs:20-80, AnalysisTemplateProvider.cs:63-76; templates/report_errores_prompt.md]` | `PromptTemplateResolver` renders over `{ Input, Context }` through the registered `ITemplateProvider` `[V PromptTemplateResolver.cs:46-66]` | Yes **only if** the sample's `AnalysisTemplateProvider` is registered as the designer's `ITemplateProvider` (replacing the `FileTemplateProvider` the designer registers `[V EngineServices.cs:94-97]` via `ConfigureEngineServices`, which runs last `[V :123]`) **and** the input is an object whose public properties are the facets (`ReportFacetInput`). If the input is a JSON object the flatten works over `JObject` properties `[?]` (not read). Alternative: rewrite the 45 templates to `{{ input.x }}`: 45 files `[V ls templates | wc]`, high risk of silent prompt drift. |
| 10 | **Typed In/Out and no JSON parse** (`LlmStringStepResult`, `SingleReportResult`) | generics | `YamlLlmStep` is non-generic (`IStepResult` -> `LlmStringStepResult`/`LlmJsonStepResult`) `[V YamlLlmStep.cs:~82-87]` | Output as text is fine (all CA LLM calls return markdown/free text or JSON-in-text parsed by the caller, e.g. `CleanJsonFences` `[V SemanticEnrichmentStep.cs:~146]`). The parse stays in the next C# step. |
| 11 | **Invoked directly, without middleware** (`ExecuteStepAsync`) from hybrid steps, with own `CancellationTokenSource` timeout and own retry loop | `[V :315-322; SubsystemInvestigationReportStep.cs:46-49,179,219; VerifyReportStep.cs:~70-90]` | n/a | In YAML each LlmStep is a normal step: `RetryMiddleware` and `TimeoutMiddleware` do apply `[V TimeoutMiddleware.cs:19-45]` (this is *better* than today, and removes the re-implemented retry loop), but a hybrid step cannot "call the LlmStep that is next to it" (see 3.5). |
| 12 | **Timeout** is whatever the factory sets (`Timeout = timeouts.Auditor`) or `TimeBudgetStep` writes later | `[V AnalysisPipelineFactory.cs:556; TimeBudgetStep.cs:~25-31 summary]` | `timeout` property exists (`StepBase.Timeout`, public set, `TimeSpan?` untyped in the description) `[V StepBase.cs:73; TypeSchemaBuilder.cs:140-163 (TimeSpan not primitive -> untyped)]` | Static values yes (`timeout: "00:15:00"`) `[I]` (Newtonsoft parses `TimeSpan` strings; not tested here). Dynamic values no. `0 = no limit` semantic of `CodebaseAnalysis:Timeouts` maps to `Timeout.InfiniteTimeSpan`; whether YAML accepts `"-00:00:00.001"` or similar `[?]`. |

### 2.3 Fallback / error-handling nuance worth knowing

`SwitchToNextFallback` in the custom step converts "tool limit exceeded" into the final-answer attempt, **before** the base decides on provider fallback `[V :214-226]`, and refuses provider fallback once in final-answer mode (the tool traffic of another engine would break) `[V :223-225]`. Any framework-level replacement (F1) must keep both rules; otherwise the retry middleware would replay the whole tool loop on another provider.

### 2.4 Minimal framework / designer changes (recommended)

| Id | Change | Where | Size | Unlocks |
|---|---|---|---|---|
| F1 | `YamlLlmStep`/`BaseLlmStep`: property `toolLimitFinalAnswerPrompt` (string, YAML-visible). Move the final-answer mode (state per invocation, `SwitchToNextFallback` precedence, `tool_choice: none` request) from `CustomStatelessTemplateLlmStep` into `BaseLlmStep`, off by default | `AITaskAgent.Extensions.Llm/Steps/BaseLlmStep*.cs` + the sample then drops its copy | M (the logic is ~60 lines but touches fallback semantics and has its own tests) | auditors, investigations, verifier as pure YAML LlmSteps |
| F2 | By-name tag handlers: `tagHandlers: [notes]` resolved from a DI registry (`ITagHandlerRegistry`), analogous to `ResolveTools` | `YamlLlmStep` + new registry in LLM extension | M | note protocol in YAML; also needs `NoteSessions.Begin` which today happens in `GetConversationContext` of the sample's step `[V :238-239]` -> a hook (`OnConversationCreated`) on `BaseLlmStep` |
| F3 | Optional post-processor hook by name on `YamlLlmStep` (`resultProcessor: report`) **or** leave it to a following C# step. Recommendation: leave to the C# step (smaller framework surface) | n/a | S in sample | report clean-up/provenance/write |
| F4 | `ParallelStep`: `failFast: false` (tolerant) property. The sample's subclass only clears `Error` after the base runs `[V TolerantReportsParallelStep.cs:27-44]` | `AITaskAgent.Core/Steps/ParallelStep.cs` | S | removes #13 as custom class (or keep it as a registered `[StepTag]` class: 0 framework change) |
| F5 | Dynamic fan-out: `ForEachStep` (items expression over input/context, `maxDegreeOfParallelism`, body steps, results as list) | `AITaskAgent.Core/Steps/` + YAML container | L | #12 investigations without C# orchestration |
| F6 | Dynamic timeouts: allow `timeout` to be a template expression over `context` evaluated when the step starts (TimeoutMiddleware reads `step.Timeout` per call `[V TimeoutMiddleware.cs:28]`) | Core | M | `TimeBudgetStep` replaced by a ScriptStep that sets `context.budget.*` |
| D1 | Designer: `ConfigureEngineServices` already allows registering the sample's `ITemplateProvider`, tools, roles `[V WorkflowDesignerOptions.cs:76; EngineServices.cs:123]`. Needs: a documented way to pass **run-time constants** (repo path, db path, reports path) to tools built at pipeline build time | `Samples/WorkflowDesigner.Core` | S/M | tool construction (see 4) |
| D2 | Designer timeouts `TimeoutSettings.Apply` only touches `StepBase` instances with `Timeout == null` `[V Engine/TimeoutSettings.cs:79]`: any registered C# step that implements `IStep` directly (like all 14 here) gets the framework default, not the designer limit | designer | none; just derive from `StepBase` when migrating | consistent timeouts |

## 3. `AnalysisPipelineFactory`: what YAML v2 can and cannot express

### 3.1 Flow (what the factory builds)

Full pipeline (`CreatePipeline`) `[V AnalysisPipelineFactory.cs:144-199]`: Detect -> LanguageEnricher -> DotNetEnricher(+semantic fallback) -> SqlFacetExtractor -> [Inventory -> DomainDiscovery -> CoveragePlanning -> TimeBudget if `Preparation:Mode=Subsystems`] -> [SharedPass if `SharedPass:Enabled`] -> TolerantReportsParallel (one branch per requested report) -> ExecutiveSynthesizer. Regeneration `[V :217-244]`: PrepareRegeneration -> SqlFacet -> preparation (reuse saved map) -> reports -> optional synthesis. Verification `[V :253-294]`: PrepareRegeneration -> TolerantParallel(Verify_* per report) -> optional synthesis.

### 3.2 Expressible in YAML v2

| Factory feature | YAML v2 construct | Notes |
|---|---|---|
| Linear sequence of steps | top-level `steps` with `dependsOn` / implicit order (`GroupStep`, topological order) `[V YamlPipelineFactory.cs:~150-165; docs yaml-pipelines.md:123]` | yes |
| Three pipelines (full / regeneration / verification) | three YAML documents/packages, or one with `SwitchStep` on `input.mode` | `SwitchStep` compares case keys as strings, case-insensitive `[V DeclarativeSwitchStep.cs:55-75]`: fine for `mode` |
| `Preparation:Mode = Off/Subsystems`, `SharedPass:Enabled` conditionals | `SwitchStep on: context.preparationMode` with `Subsystems` case containing the 4 steps and `default` empty | `default: []`/null: "Null makes an unmatched value an error" `[V DeclarativeSwitchStep.cs:42]`, so an explicit empty `default` is needed `[?]` whether an empty list is accepted. Boolean flags: `on:` renders to string `"True"/"true"` `[?]`. |
| Report set chosen by `RequestedReports` (9 enum values, default all) | fixed branches in `ParallelStep.branches`; selecting a subset needs either one `SwitchStep`/guard per branch or a custom step | `[V ParallelStep.cs:44-50]` branches are static. A per-branch guard "skip if not requested" can be a `SwitchStep` inside each branch: 9 small switches |
| Per-report parameters: role, template prefix, tools, `MaxToolIterations`, timeout | one `LlmStep` per report with `profileName`, `userPrompt`, `systemPrompt`, `tools`, `maxToolIterations`, `timeout` | 9 near-identical blocks. Package "step templates" (spec D5, copy-insert) fit exactly here `[V Samples/WorkflowDesignerWeb/PACKAGES-AND-TEMPLATES-SPEC.md:D5]` |
| Role resolution with a clear error naming the role | `YamlLlmStep.ResolveRole` fails at build with the available roles `[V YamlLlmStep.cs:~176-197]` | equivalent or better than `ResolveRole` in the factory `[V AnalysisPipelineFactory.cs:339-347]` |
| Parallel cap `MaxParallelAuditors` | `ParallelStep.maxDegreeOfParallelism` (public `init`, schema-visible `[I]`) `[V ParallelStep.cs:57-63]` | yes (static value) |
| Step/pipeline timeouts (`CodebaseAnalysis:Timeouts`) | per-step `timeout`, designer `Timeouts` section (`config.json`) `[V PACKAGES-AND-TEMPLATES-SPEC.md:~4.1; Engine/TimeoutSettings.cs:67-97]` | static; 0/infinite `[?]` |
| Loops (none in the factory) | n/a | the factory has no loops; `LoopStep` is not needed |
| `SuspendStep` (none in the factory) | n/a | no human-in-the-loop in the analysis |

### 3.3 Not expressible, with reason

| Factory feature | Why not | Workaround |
|---|---|---|
| **Typed data flow** (`AnalysisPipelineInput` -> `GraphStatusResult` -> `ReportFacetInput`...) between steps | YAML passes `JsonStepResult`; `IStep<TIn,TOut>` throws on type mismatch `[V IStep.cs:77-85]` | registered steps declared as `StepBase` with `InputType = typeof(IStepResult)` and convert internally; or one adapter step "Init" that reads JSON, builds `AnalysisPipelineInput`, seeds context keys, and a "Facets" step that returns the typed object only through `context` |
| Context objects (`DomainMap`, `RepositoryInventory`, `CoveragePlan`, `GraphProvenance`, `AuditorStepRegistry`) shared via `Metadata` | stay inside C# steps; ScriptStep cannot read them `[V ScriptStep.cs remarks]` | keep them in C# steps; expose only JSON summaries already present (`ReportFacetInput.DomainMap/CoveragePlan` are JSON strings `[V ExecutiveSynthesizerStep.cs, TimeBudgetStep.cs:~27]`) |
| **`TimeBudgetStep`**: computes timeouts at run time and writes them into the `Timeout` of steps created earlier through `AuditorStepRegistry` `[V TimeBudgetStep.cs:summary; AnalysisPipelineFactory.cs:415-447]` | YAML steps are built once and their `Timeout` is a static property; no expression | F6 (dynamic timeout) or keep a C# step + keep the registry; the registry only works if the report steps are *the same objects* the C# step holds -> not possible when the YAML factory builds them `[I]` |
| **Mode B**: N investigations per report, N decided at run time by `CoveragePlan`, semaphore of `MaxParallelInvestigations`, tolerant to individual failure, then consolidation | `ParallelStep.branches` is a static list `[V ParallelStep.cs:44-50]`; `LoopStep` is sequential with no item binding other than the iteration counter `[V LoopStep.cs:43-49]` | F5 `ForEachStep`, or keep `SubsystemInvestigationReportStep` as a registered C# step that builds its LLM sub-steps by code (needs the `roles`, `templateProvider`, tool list at construction) |
| **`TolerantReportsParallelStep`** | `ParallelStep` sets the aggregate error when any branch fails `[V TolerantReportsParallelStep.cs:10-13,36-43]` | F4, or register the subclass with a `[StepTag]` (it inherits `Branches`; container property detection is by type `[V YamlPipelineFactory.cs:~44 doc]`) `[I]` |
| **Synthesis reading siblings** (`ParallelResult` of the reports + disk reports + `StepResults` scans) | needs the `ParallelResult` typed value and .NET objects `[V ExecutiveSynthesizerStep.cs:~142-167]` | C# step downstream of the parallel; the LLM call inside can stay inside (invoked directly, as today) or become a YAML `LlmStep` fed by a C# "DigestStep" |
| **Finally-block restore** of retired catalog reports on any exit (`CodebaseAnalysisPipeline.ExecuteAsync` finally) `[V AnalysisPipelineFactory.cs:630-633, 641-663]` | no try/finally construct in YAML; failures/cancellation skip downstream steps | host-level concern: the designer's `RunManager` would need a "cleanup hook" per package `[?]` (not read), or the registered "Detect" step registers a cleanup in context that the host runs |
| **Pipeline-level timeout wrapper** with its own message and distinguishing user cancel vs timeout `[V :598-628]` | designer has workflow limit (`TimeoutSettings.Workflow`) `[V EngineServices.cs:~170-177]` but message/semantics differ | accept designer's behaviour |
| **Engine events -> `IAnalysisReporter` -> web progress** (`context.Report`, `ReportWritten`, `sharedpass.progress`) | the designer streams its own events `[V EngineServices.cs: eventChannel param]`; `context.Report` goes to `AnalysisContextKeys.Reporter` from context `[V AnalysisRunner.cs:34]` | registered steps can keep calling `context.Report` if the host puts a reporter in context; web progress UI of CodebaseAnalysisWeb would not see YAML-only LlmSteps (`Auditor_*` names) without a mapping `[I]` |
| **Effective limits report** (`EffectiveLimits.DescribeAnalysis`) `[V AnalysisRunner.cs:~124-128]` | host feature | out of scope of YAML |
| **`CallLimit` / `MaxConcurrentRequests`** read from roles to size the budget `[V AnalysisPipelineFactory.cs:388-397]` | provider option, used by `TimeBudgetStep` | only matters if `TimeBudgetStep` stays |

### 3.4 Effective limits: where they come from and where they would live

All CA limits are configuration read by the factory/steps: `CodebaseAnalysis:Timeouts` (7 keys `[V Hosting/AnalysisTimeouts.cs:21-50]`), `Auditors:MaxToolIterations[ByRole]` and `Reports:MaxParallelAuditors` (in `AnalysisPipelineInput` `[V Models/AnalysisPipelineInput.cs:36-52]`), `Preparation:*`, `SharedPass:*` (caps), `JavaAnalyzer`, `DotNetEnricher` `[V appsettings.analysis.json]`. In the repo's `appsettings.analysis.json` all `Timeouts` are `0` (= no limit) and `Auditors.MaxToolIterations = 100` `[V appsettings.analysis.json:~19-34]`. In YAML these become per-step properties (`timeout`, `maxToolIterations`) of each LlmStep, and the registered steps keep reading their own sections from `IConfiguration`. The designer's `config.json` -> `IConfiguration` bridge only forwards `AITaskAgent:LlmProviders`, `Timeouts`, `Storage` `[V EngineServices.cs:150-205]`; **`CodebaseAnalysis:*`, `JavaAnalyzer`, `DotNetEnricher` sections would not reach the registered steps** unless `ConfigureEngineServices` adds them `[I]` (the bridge builds the configuration only from those sections; `ConfigureEngineServices` can replace `IConfiguration` `[I]`).

## 4. Effort per step to make it buildable from YAML, and regression risk

Common prerequisites for every registered step (cost shared, counted once):

- **P1** Derive from `StepBase` (not bare `IStep`), add `[StepTag("...")]`, public settable config properties, DI constructor. The registry creates through DI and populates properties from JSON `[V StepRegistry.cs:~84-97; custom-extensions.md]`; requires an `IExtension` (`AddStep<T>`) or `AddStepsFromAssembly` `[V StepRegistry.cs:~64-83]`. The assembly is referenced statically ("no dynamic loading") `[V docs/extensibility/custom-extensions.md:3]`. Cost: S per step; **M** for the 14 together because typed `IStep<TIn,TOut>` -> `StepBase.ExecuteAsync(IStepResult ...)` changes the signature of each `ExecuteAsync` and the tests call them directly (see risk).
- **P2** An `Init` step (or host code) that builds `AnalysisPipelineInput` from JSON and seeds `PipelineInput`, `CbmDbPath`, `ReportsPath`, `Reporter`, `PipelineStartedAt` `[V AnalysisRunner.cs:31-39]`. Cost: S-M.
- **P3** Tool registration into the designer's service collection per run (`GraphMapTool(dbPath)`, ... `NoteProtocolTool.Wrap`, `FileToolsConfiguration` statics `[V AnalysisToolRegistration.cs:23-48]`). **`FileToolsConfiguration.RootDirectory` and `SearchExcludedDirectories` are static** `[V :35,48]`: two concurrent runs of different repos in one designer process would collide `[I]`. Cost: M, risk of a real defect in a multi-run host.
- **P4** `ITemplateProvider` = `AnalysisTemplateProvider` for the package, or template rewrite. Cost: S (provider) / L (rewrite 45 templates).
- **P5** Roles: 12 required + 2 shared-pass roles `[V Hosting/LlmRoleFactory.cs:24-30]` map to designer `Roles` in `config.json`; `LlmRoleFactory` ajusts endpoints (Azure key priority `Asistente:*`) `[V LlmRoleFactory.cs:3-9]`: that adjustment would not happen in the designer `[I]`. Cost: S config, M if the Azure-specific behaviour is required.

| Step | Configurable props to expose (YAML) | Build via DI | Effort | Regression risk on `CodebaseAnalysis.Core.Tests` (1061 `[Fact]`/`[Theory]` in the project `[V grep count]`) |
|---|---|---|---|---|
| DetectAndIndexGraph | `forceReindex`, timeout, none else (input is the init object) | needs `IConfiguration` (already via `context.ServiceProvider`) | M | 10 test files / 23 refs `[V grep]`. Typed `ExecuteAsync(AnalysisPipelineInput,...)` is called directly by tests `[I]`. Keep `IStep<,>` and add a thin `StepBase` adapter that delegates: **zero change in tests** |
| PrepareRegeneration | none | none | S | 4 files / 11 refs. Same adapter approach |
| LanguageEnricher | `timeout` (today from `JavaAnalyzerOptions`) | ctor `JavaAnalyzerOptions` (factory builds it from config `[V :135-138]`) | S | 7 files / 24 refs; low if adapter |
| DotNetEnricher | `timeout` | ctor takes a fallback `IStep` (SemanticEnrichment) | M: fallback wiring by DI (`IStep<GraphStatusResult,GraphStatusResult>` registration) or by YAML `SwitchStep` | 9 files / 25 refs |
| SemanticEnrichment | none; LLM role/prompt by constructor | ctor takes `CustomStatelessTemplateLlmStep` | M: either keep it constructed by DI factory (role "Enricher") or split into LlmStep + `ApplyEnrichment` C# step | 2 files / 8 refs |
| SqlFacetExtractor | none | none | S (adapter) | 13 files / 22 refs, **highest test fan-out**: do not touch the typed signature |
| RepositoryInventory | `sqlScanMaxFiles`, `sqlScanTimeBudget` (ctor args) | optional ctor args | S | 5 files / 15 refs |
| DomainDiscovery | `reuseSavedMap`, naming timeout | ctor: naming LLM step, flags | M | 5 files / 18 refs; naming sub-step creation `CreateNamingStep` is public static and reused by tests `[V :94-106]` |
| CoveragePlanning | none | ctor `ITemplateProvider`, `AuditorStepRegistry` (the registry is meaningless in YAML: only modes B/A of sibling steps) | S-M: registry must be replaced by "which reports exist" input `[I]` | 5 files / 16 refs |
| TimeBudget | `callLimit` | ctor `AuditorStepRegistry` | **L / blocked** without F6; keep out of the first migration (use static timeouts) | 6 files / 41 refs (most references of any step) |
| SharedPass | all `SharedPass:*` options already come from `IConfiguration` `[V CA SharedPassOptions.cs:FromServices]`; roles by name | ctor: 2 roles + templates + options | M | 2 files / 14 refs |
| SubsystemInvestigationReport | `reportType` | ctor with 3 LLM step instances + factory lambda | **L** (needs F5 or stays as C# builder) | 5 files / 20 refs |
| TolerantReportsParallel | `branches`, `maxDegreeOfParallelism`, `name` | parameterless ctor missing: only `(string name, params IStep[] branches)` `[V :21-25]`; registry needs a ctor DI can satisfy and `Branches` populated by factory `[V ParallelStep.cs:34-41]` | S (add parameterless ctor + `[StepTag]`) | 4 files / 15 refs |
| ExecutiveSynthesizer | none | ctor takes the LLM step | M (keep the LLM step inside, built by DI) | 7 files / 16 refs |
| VerifyReport | `reportType` | ctor: LLM step, `Lazy<ReportContentValidator>`, timeout | M | 1 file / 1 ref (`VerificationPipelineTests`) |
| CustomStatelessTemplateLlmStep | n/a (replaced by `YamlLlmStep` + F1/F2, or registered as `[StepTag("CaLlmStep")]` subclass of `YamlLlmStep`) | | M-L | 8 files / 19 refs; its own unit tests in `CustomStatelessTemplateLlmStepTests.cs` pin the final-answer behaviour; if F1 moves logic to the base, those tests must still pass against the moved code |
| Factory (`AnalysisPipelineFactory`) | | | stays as is for the CLI/Web | 31 test files / 158 refs `[V grep]`, plus `CodebaseAnalysisWeb/Hosting/AnalysisHost.cs`. **Do not delete or change signatures**; add the YAML path next to it |

Regression strategy: additive (new `StepBase` adapters, new package) rather than converting existing classes. The CLI/Web keep using the factory; the designer package uses the same C# steps through adapters. Main risk is divergence between the two flows, mitigated by one parity test that runs both on `Fixtures/` (offline; the tests `OfflinePipelineTests`, `PreparationPipelineTests`, `RegenerationTests`, `VerificationPipelineTests`, `ReincorporationPipelineTests` exist `[V directory listing]`; how they stub the LLM `[?]` not read).

Other regression risks:

- Event/progress mapping depends on step names and phases `[V AnalysisPipelineFactory.cs:26-114]`; YAML `id`s must reproduce `Auditor_<ReportType>`, `Investigation_<type>_<id>`, `Verify_<type>`, `SemanticEnricherLlm`, `SynthesizerLlm`, `ParallelReportsExecution` `[V :27-36]`, else `AnalysisEventMapper`/web progress changes `[I]`.
- Concurrency/statics: `FileToolsConfiguration` statics (P3); `AvailableTools.Instance` is process-wide `[V AvailableTools.cs:10]`; `YamlLlmStep` instead looks tools up in the provider it was created with `[V YamlLlmStep.cs:~155]`. A mixed setup is possible, a double registration leads to which wins `[?]`.
- `ConditionalWeakTable`-based invocation state in the custom step is keyed by `PipelineContext` identity `[V CustomStatelessTemplateLlmStep.cs:47-50]`; the `CloneForBranch` contexts of `ParallelStep` are distinct objects, so it works under YAML parallel `[I]`.
- Prompt drift: any change in how templates render (flatten vs `input.x`) changes the prompt silently (the engine "has no conditionals" and tolerates missing keys `[V ExecutiveSynthesizerStep.cs:~37 comment; PromptTemplateResolver.cs strictMode:false]`). Needs a golden test: render each of the 45 templates through both providers and diff.

## 5. Recommended migration order

1. **Framework first (small, additive, with tests):** F4 or the `[StepTag]` on the tolerant parallel (S); **F1** final-answer-after-tool-limit in `BaseLlmStep`/`YamlLlmStep` (M). Without F1 the auditors cannot be pure YAML without regressing behaviour (a tool-limit would fail the report instead of writing it).
2. **Designer host for CA:** `IExtension` `CodebaseAnalysisExtension` in a new tiny project referencing `CodebaseAnalysis.Core` (registers steps, `AnalysisTemplateProvider`, tools per run, `IConfiguration` sections `CodebaseAnalysis:*`, `JavaAnalyzer`, `DotNetEnricher`). Solve P2/P3/P4/P5 here. No change to the factory.
3. **Adapters for the 14 steps as `StepBase`** (P1), starting with the stateless and cheapest to test: `SqlFacetExtractor`, `RepositoryInventory`, `CoveragePlanning`, `PrepareRegeneration`, `Detect`, `LanguageEnricher`, `DotNetEnricher`. These give a YAML "front half" (detect -> enrich -> facets -> inventory) with no LLM involved.
4. **First LLM pieces as YAML `LlmStep`:** Synthesis and Verifier (single call, tools list short: `VerifierToolNames` `[V AnalysisPipelineFactory.cs:303-304]`), then DomainNaming. Keep their deterministic wrappers as registered steps (digest step before, write step after).
5. **Auditors as YAML `LlmStep`s** (9 blocks, step-template candidate), mode A only (no Preparation), with F1; the report post-processing/write as a registered `ReportSink` step after each (or one after the parallel). Note protocol (F2) off at first; measure quality before enabling `[?]`.
6. **Mode B and TimeBudget last** (F5/F6 or leave as C# registered steps): they are the heaviest, most-tested (`TimeBudgetStep` 41 refs, `SubsystemInvestigation` 20 refs) and least YAML-shaped. Acceptable end state: preparation + mode B stay inside registered C# steps, only the "simple" calls are YAML.
7. Parity tests (factory pipeline vs YAML package) before declaring done; only then consider removing duplicates from the sample.

What I would **not** do: convert the 45 templates to `{{ input.x }}` up front, port `SqlFacetExtractor`/`CoveragePlanner` to JS (B does not fit), or change `CustomStatelessTemplateLlmStep` in place before F1 exists.

## 6. What I could not verify

- Whether `FileTemplateProvider` (designer) supports `@[file.md]` includes the way `AnalysisTemplateRenderer` does; I read `TinyTemplateRendered` use only in `PromptTemplateResolver.RenderInline` `[V :111-117]`.
- Whether `AnalysisTemplateRenderer` flattening works over a `JObject`/JSON input (it uses reflection on public properties `[V AnalysisTemplateRenderer.cs:~30-60]`), i.e. whether a JSON `ReportFacetInput` would feed the existing templates unchanged.
- Whether an empty `default:` list is accepted by the YAML factory for `SwitchStep`, how booleans render in `on:` expressions, and the truthiness rules of `TemplateExpression` beyond the doc line.
- How `TimeSpan` values (`timeout`) are accepted in YAML and whether "no limit" can be written; I only know `StepBase.Timeout` is a public `TimeSpan?` and untyped in the described schema.
- Whether Newtonsoft `Populate` sets `init` properties of `BaseLlmStep` (`MaxToolIterations`) correctly; the guide says it works `[V docs/guides/yaml-pipelines.md:182]` and a test uses it `[V Tests/YamlPipelineV2Tests.cs:395]`, but I did not run it.
- Whether the container detection of `YamlPipelineFactory` handles a `ParallelStep` subclass (inherited `Branches`) — `[I]` from the by-type description, not tested.
- How the offline tests stub the LLM and which of them would break if step classes changed base; I counted references only, I did not read the tests. I did not read `ChatAgent`, `ReportValidator`, `KnowledgeGraphIngestor` internals, `BatchReader`/`FindingJudge` bodies beyond their step wrappers, the rest of `SqlFacetExtractorStep` (1379 lines), `DetectAndIndexGraphStep` beyond line ~200, `SubsystemInvestigationReportStep` beyond line 200, or `ExecutiveSynthesizerStep` beyond ~330.
- Quality/cost impact of dropping the note protocol or the stateless window sizing nuance: not measured (`project memory` notes real runs cost ~10 USD, so no test was run, and none was asked for).
- Whether the designer's `RunManager` has any cleanup/finally hook for the "retired catalog reports" restore.
- Pipeline `IPipeline.ExecuteAsync(name, steps, input, context, timeout, ct)` signature and the `CodebaseAnalysisPipeline` wrapper semantics are known only from the sample's use `[V AnalysisPipelineFactory.cs:622-623]`.


---

# Study C: dynamic extension loading for the designer Core and CLI

Read-only study, 2026-10-19. Nothing was built, run or edited. Evidence marks: `[V]` I read the line, `[I]` inferred, `[?]` unknown. Recommendations are not marked.

Paths: `R:` = `C:\GeminiCli\AITask\AITaskAgent`; `BRMS:` = `\\nas_rozano\Trabajo_jl\Asisa\BDP\BRMS.Core`.

## 0. Findings that change the premise (read first)

1. **`Designer/Dynamic/DynamicStepManager.cs` is not assembly loading.** It emits types with `System.Reflection.Emit` (`AssemblyBuilder.DefineDynamicAssembly`, `DefineType` over `DynamicJsActionStep`/`DynamicJsTransformStep`/`DynamicJsValidatorStep`) to wrap JavaScript steps `[V]` `R:Designer/Dynamic/DynamicStepManager.cs:89-93,117-126,174-180`. It registers into a static `StepManager` `[V]` `:130-133` and keeps static state `[V]` `:69-71`. It is also parked: not in the build, excluded from `AITaskAgent.slnx` `[V]` `R:Designer/README.md:1-5`. **It contributes nothing to loading DLLs.** `ScriptStep` already covers its use case `[V]` `R:Designer/README.md:15-18`.
2. **BRMS never isolated its plugins.** The real loader (`NuGetPackageLoader`) loads every DLL into `AssemblyLoadContext.Default` `[V]` `BRMS:Core/NugetPackageLoader.cs:409,414`. `PluginLoadContext` (collectible, per-package, `Load` override, native probing) exists `[V]` `BRMS:Core/NugetUtils/PluginAssemblyResolver.cs:9-31,38-57,64-107`, but nothing instantiates it: the only match of `PluginLoadContext` in non-comment code is its own declaration `[V]` (grep over `BRMS.Core/**/*.cs`, excluding `obj`). `McMaster.NETCore.Plugins 2.0.0` is referenced in `BRMS.Core.csproj` `[V]` `BRMS:BRMS.Core.csproj:40` and no `.cs` file uses it `[V]` (grep `McMaster|PluginLoader`: no matches). Conclusion: BRMS is a source of **ideas and pitfalls**, not a design to copy.
3. **The CodebaseAnalysis steps cannot be loaded as extensions today, with or without dynamic loading.** There is no `[StepTag]` and no `IExtension` anywhere under `Samples/` `[V]` (grep `StepTag|: *IExtension|AddStep<` over `Samples/`: no matches; the only `[StepTag]` are in the framework projects, e.g. `R:AITaskAgent.Core/Steps/GroupStep.cs:13`). They are typed `IStep<ReportFacetInput, ReportFacetInput>` with constructor parameters such as `AuditorStepRegistry` `[V]` `R:Samples/CodebaseAnalysis.Core/Steps/TimeBudgetStep.cs:33,47`, `R:Samples/CodebaseAnalysis.Core/Steps/RepositoryInventoryStep.cs:28,85`. They are built by `AnalysisPipelineFactory` in C# `[I]` (name). Making them YAML-declarable is a separate job (tags, an `IExtension`, input/output types the YAML engine understands). Dynamic loading only solves "how do the DLLs get in"; the example the user has in mind needs that second job. `[?]` I did not check whether `ReportFacetInput` steps can run inside the YAML pipeline at all.
4. **A dynamic loader is one piece of a three-piece contract**: (a) the host loads the DLL, (b) the DLL is an `IExtension` with `[StepTag]` steps, (c) the type identity of the contract assemblies is shared with the host. (c) is the one that fails silently (§2.3).

## 1. How BRMS loaded (and what the `Designer/Dynamic` copy is)

### 1.1 BRMS mechanism `[V]` unless marked

| Aspect | What it does | Reference |
|---|---|---|
| Entry | `NuGetPackageLoader(NuGetLoaderConfig, ILogger)`; `LoadPluginsAsync(IServiceCollection)` iterates `cfg.Plugins` (PackageId + Version) and returns `LoadErrors.Count == 0` | `BRMS:Core/NugetPackageLoader.cs:28,56-77` |
| Config | `PackageSources[]` (URL or local folder), `GlobalPackagesFolder` (default `%USERPROFILE%\.nuget\packages`), `DefaultTargetFramework` (`net9.0`), `Plugins[]` | `BRMS:Core/NugetUtils/NuGetLoaderConfig.cs` |
| Discovery of the package | Tries each source in order; downloads the `.nupkg` to a stream (`CopyNupkgToStreamAsync`); no stream = `LoadErrors.Add` and return | `NugetPackageLoader.cs:245-278` |
| Extraction | Copies `lib/<chosenTfm>/*`, `runtimes/*` and `*.resource.dll` to `<GlobalPackagesFolder>/<id>/<version>/` (inside the user's NuGet cache, not a private folder) | `:309-350` |
| TFM selection | exact, then descending `netN.0` down to `net5.0`, then `netstandard2.1/2.0/1.0`, then the first one | `:179-222,303-318` |
| Dependencies | Reads **all** dependency groups of the nuspec (not only the matching TFM) and recurses by `Id` + `VersionRange.OriginalString` as the "version" | `:358-368`; the comment at `:352-354` and the dead block `:371-382` show the previous attempt |
| Shared-with-host list | `IsSharedAssembly(name)`: `System.*`, `netstandard`, `mscorlib`, `runtime.*`, `Microsoft.AspNetCore.*`, a list of BCL polyfills, and (only if already loaded in the host) `Microsoft.Extensions.*`/`Microsoft.Bcl.*`; plus `BRMS.Abstractions*` always. A shared package is skipped entirely (not downloaded) | `:227-231,398-402,604-745` |
| Loading | **Every other DLL** in the package folder goes to `AssemblyLoadContext.Default.LoadFromAssemblyPath` | `:385-419` |
| Fallback resolver | `AssemblyLoadContext.Default.Resolving +=` one per package path: skips `window*`/`microsoft.*`; returns an assembly already in Default with the same simple name (**any version**); otherwise searches the package folder recursively | `:457-499` |
| Native libs | `NativeLibrary.SetDllImportResolver` per assembly (host first, then `runtimes/<rid>/native`, then the whole NuGet cache) | `:502-598` |
| Type discovery | `assembly.GetTypes()`; on `ReflectionTypeLoadException` keeps the types that loaded (logs 3 loader exceptions); per type `IPlugin` / `IBRMSConfigurationProvider` / `IRule` / attributed services; per-type `try/catch` for `FileLoadException`, `TypeLoadException`, any | `:85-158` |
| Registration | Plugins into DI (transient); rules into the **static** `RuleManager` | `:117-137`; `BRMS:Core/RuleManager.cs:24-31` |
| Post-build step | `RegisterPluginsAsync(IServiceProvider)` resolves each `IPlugin` and awaits `Register()` | `:165-178` |
| Errors | `LoadErrors` (package ids) and `LoadedPlugins`; a failed DLL logs a warning and marks the package; the host keeps running | `:44-49,420-450` |
| Isolation / unload / versions | **None.** No ALC per plugin (§0.2), no `Unload`, two packages needing different versions of a library: the first loaded wins in Default, the second gets the first's `[V]` by the `Resolving` rule `:476-482` | -- |

### 1.2 What is worth taking

- Skip-the-host-assemblies rule (`IsSharedAssembly`) as a concept: the contract and the framework must come from the host.
- Per-type error tolerance (`ReflectionTypeLoadException`, `TypeLoadException`): one broken type must not lose the assembly.
- Two-phase flow (load and register in the collection, then a post-build callback) is **not** needed here: `IExtension.Register(IExtensionBuilder)` already runs before the provider `[V]` `R:AITaskAgent.Core/Configuration/ExtensionServiceCollectionExtensions.cs:28`.
- `LoadErrors`/`LoadedPlugins` as a report the host can show.

### 1.3 What is not worth taking

- NuGet download and nuspec dependency recursion inside the host: 815 non-comment lines in a 924-line file, with a comment trail of failed approaches `[V]` (`awk` count). It needs `NuGet.*` 7.3.1 packages `[V]` `BRMS.Core.csproj:45-49`, network, a sources config and a cache policy. The designer's extensions are local DLLs a developer builds; resolving them with `AssemblyDependencyResolver` and a `.deps.json` covers that without NuGet.
- Loading into `Default` (no isolation, no unload, version conflicts decided by order).
- Static registration (`RuleManager`, `StepManager`): the designer builds a fresh `ServiceCollection` and `IStepRegistry` per catalog, validation and run `[V]` `R:Samples/WorkflowDesigner.Core/Engine/EngineServices.cs:71,116-123`.
- Extracting into `~/.nuget/packages`.
- The `Resolving` hook that returns "any assembly with the same simple name" (it silently mixes versions).

### 1.4 `Designer/Dynamic` copy

Useless for this goal (see §0.1). Keep it parked; do not revive it. If the user later wants "steps defined in the UI" (JS wrapped as steps), `ScriptStep` already does it declaratively `[V]` `R:AITaskAgent.Extensions.Script/README.md:3`.

## 2. Proposed design

### 2.1 Where the current code stands (the seams)

- `AddWorkflowDesigner` registers `StepCatalog` as a singleton built **once**, from `StepCatalog.Create(options)` `[V]` `R:Samples/WorkflowDesigner.Core/WorkflowDesignerServiceCollectionExtensions.cs:39`; `YamlReader` is built from that catalog `[V]` `:40`. So extension set = fixed at host start. Consistent with "restart to change extensions".
- `StepCatalog.Create` calls `EngineServices.Create(options, null, LlmServiceMode.None)`, gets `GetOrAddStepRegistry()` and reads the `IExtension` instances out of the `ServiceDescriptor`s (`ImplementationInstance`) `[V]` `R:Samples/WorkflowDesigner.Core/Catalog/StepCatalog.cs:62-73`. Anything added with `services.AddExtension(instance)` shows up. The designer-built collection ends with `options.ConfigureEngineServices?.Invoke(services)` `[V]` `EngineServices.cs:123`. **That hook already is the registration seam**; every catalog, validation and run goes through it `[V]` (`WorkflowDesignerOptions.cs:70-76` doc comment says so; the three callers are catalog `StepCatalog.cs:66`, and per the docs validation and runs `[V]` `docs/designer/core-api.md:35-37`).
- `AddExtension` is idempotent by `Name`, comparing against registered `ImplementationInstance`s `[V]` `ExtensionServiceCollectionExtensions.cs:20-35`. Loading the extension **instances once** and calling `AddExtension(sameInstance)` per collection is safe `[I]` (each call runs `Register` on a new registry; `Register` must not hold per-collection state in fields: a rule to put in the extension guide).
- `StepRegistry.Add` rejects types that are not assignable to `IStep`, abstract, open generic `[V]` `R:AITaskAgent.Core/Steps/StepRegistry.cs:32-42`; two types on the same tag throw `InvalidOperationException` `[V]` `:51-61`. `AddFromAssembly` only takes types with `[StepTag]` `[V]` `:72-77`.
- `StepCatalog.CategoryOf` is decided by the step's assembly: Core => `flow`, Llm => `llm`, Script => `script`, **anything else => `other`** `[V]` `StepCatalog.cs:154-161`. An extension's steps already land in `other`.
- `IExtension.ConfigurationModel()` already feeds the DTO: `new ExtensionDto(e.Name, ToJson(e.ConfigurationModel()))` `[V]` `StepCatalog.cs:136-138`; default implementation builds it from `ConfigurationType` `[V]` `R:AITaskAgent.Abstractions/Abstractions/IExtension.cs:26-33`.
- `IExtension.cs:10` documents "there is no dynamic assembly loading" `[V]`, and so do `R:docs/extensibility/custom-extensions.md:3` and `R:docs/ARCHITECTURE-v2.md:53` `[V]`. Those three texts must be amended: the **framework** still does not load anything; the **designer** does. Keep that division: the loader lives in the designer Core, not in `AITaskAgent.Core`.

### 2.2 Where extensions live (search order)

Proposal, in `WorkflowDesignerOptions`: `ExtensionDirectories` (list, absolute) plus the conventions below. Each extension is **one subfolder** `<root>/<ExtensionName>/` containing its main DLL, its `.deps.json` and its own dependencies (the output of `dotnet publish`).

1. `<install dir>/extensions/` (next to the executable: global, shared by Web and CLI of that install).
2. `<Workspace>/extensions/` (the workspace folder: `workflows/` and `library/` are already there `[V]` `WorkflowDesignerOptions.cs:8`; project-specific extensions travel with the workspace).
3. CLI `--extensions <dir>` (repeatable) and Web config `WorkflowDesigner:ExtensionDirectories`.
4. NuGet: **not in v1** (see §6). If wanted later: a `workflow-designer extension install <id>@<ver>` command that does `dotnet publish`-like restore into folder 1 or 2, so loading code never touches the network.

Duplicate extension name across roots: the earlier root wins, the later one is reported as a load problem (do not guess which is newer).

### 2.3 `AssemblyLoadContext` per extension

**Rule: contract assemblies and the framework are shared with the host; everything else is private to the extension.**

Why this is not optional: `StepRegistry.Add` checks `typeof(IStep).IsAssignableFrom(stepType)` `[V]` `StepRegistry.cs:32`. If an extension carried its own copy of `AITaskAgent.Abstractions.dll`, its `IStep` would be a different type and the check throws "does not implement IStep" while the class textually does. Same for `IExtension`, `[StepTag]` (compared by `GetCustomAttribute<StepTagAttribute>`, a different attribute type is invisible `[I]`), `JObject` and `JsonSchema`: the registry contract exposes `Newtonsoft.Json.Linq.JObject` in `IStepRegistry.Create` `[V]` `R:AITaskAgent.Abstractions/Abstractions/IStepRegistry.cs:2,55`, and `IExtension.ConfigurationModel()` returns `NJsonSchema.JsonSchema` `[V]` `IExtension.cs:2,32`. Newtonsoft and NJsonSchema are therefore **part of the contract**, not private dependencies. Both projects pin `Newtonsoft.Json 13.0.4` and `NJsonSchema 11.6.1` `[V]` (`AITaskAgent.Abstractions.csproj`, `AITaskAgent.Core.csproj`).

Shared set (resolved from the host / `Default`, never loaded from the extension folder):

| Group | Assemblies | Why |
|---|---|---|
| Runtime | `System.*`, `Microsoft.*` BCL, `netstandard` | framework |
| Framework contract | `AITaskAgent.Abstractions`, `AITaskAgent.Core`, `AITaskAgent.Yaml`, `AITaskAgent.Extensions.Llm`, `AITaskAgent.Extensions.Script`, `FileTools`, `McpIntegration`, `OpenAILLmService`, `GeminiLlmService` (everything the host references) | type identity; extension steps derive from these |
| Contract-visible 3rd party | `Newtonsoft.Json`, `NJsonSchema` (+ `NJsonSchema.*`), `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options` | they appear in public signatures (`IServiceCollection` in `IExtensionBuilder.Services` `[V]` `IExtension.cs:43`) |
| Private to the extension | everything else it ships (e.g. `Microsoft.Data.Sqlite`, `PdfPig`, `DocumentFormat.OpenXml` that CodebaseAnalysis.Core uses `[V]` `R:Samples/CodebaseAnalysis.Core/CodebaseAnalysis.Core.csproj`) | isolation: two extensions may bring different versions |

Implementation, two options:

- **A. Own ~120-line `ExtensionLoadContext : AssemblyLoadContext`**: `Load(name)` first asks the host (`Default.Assemblies` by simple name; if the host has it, return that one), else `AssemblyDependencyResolver(mainDll).ResolveAssemblyToPath(name)`; `LoadUnmanagedDll` through the same resolver. Isolated or collectible by flag. No new dependency.
- **B. `McMaster.NETCore.Plugins`** (`PluginLoader.CreateFromAssemblyFile(path, sharedTypes: [typeof(IExtension), ...])`): does the same via `.deps.json` and shared types, widely used. It is already a reference of BRMS `[V]` (unused there). `[?]` I did not read its current docs or check version compatibility with net10.0; the claim that it behaves as above is from general knowledge `[I]`.

Recommendation: A, because the shared-set rule above is the whole logic and a third-party loader would still need it spelled out as `sharedTypes`.

**Version of host libraries.** A DLL compiled against `Newtonsoft.Json 13.0.4` runs on the host's 13.x `[I]` (same major, binding to higher minor works in .NET). One compiled against a **higher** version than the host ships will fail at call time with `FileLoadException` or `MissingMethodException` `[I]`. Rule: an extension must build against the same framework version as the host (see §2.6 contract version); the host reports the loader exception verbatim.

**Unload / collection.** A collectible ALC only unloads when nothing references its types: not the `StepRegistry` (which holds `Type`s), not any DI singleton, not `ConcurrentDictionary` caches such as `StepRegistry._descriptionCache` `[V]` `StepRegistry.cs:21`, not Newtonsoft's static type caches `[I]`, not threads or timers the extension started. Per-run collections are disposed, but the catalog and `YamlReader` singletons keep `Type`s for the host's lifetime `[V]` `WorkflowDesignerServiceCollectionExtensions.cs:39-40`. So **hot unload would not work in practice** without redesigning the catalog as replaceable. Recommendation: **non-collectible, loaded at start, changes need a restart** (the Web is a local backend, the CLI is one process per command). Say so in the docs; do not promise `Unload`.

### 2.4 Discovery

Order of preference:

1. **Manifest `extension.json`** in the extension folder (cheap to read, no code executed for listing): `{ "name": "CodebaseAnalysis", "version": "1.2.0", "assembly": "CodebaseAnalysis.Extension.dll", "contract": 1, "frameworkMin": "10.0.0", "description": "...", "extensionType": "CodebaseAnalysis.Extension.CodebaseExtension" }`. `extensionType` is optional.
2. **Reflection fallback**: in the main assembly, public non-abstract types implementing `IExtension` with a public parameterless constructor (`AddExtension<TExtension>` already requires `new()` `[V]` `ExtensionServiceCollectionExtensions.cs:39-41`). If the manifest names a type, take only that one; otherwise all of them, and fail the extension if there are none.
3. **No attribute scan of all assemblies** (BRMS scanned every type of every DLL, `NugetPackageLoader.cs:113-141`): only the main assembly; its private dependencies are never scanned. It avoids side effects of loading unrelated types and keeps listing fast.

Manifest absence is not an error (reflection only), but `package info`/validation (§3) works with names, so an extension without a manifest cannot satisfy `version >=`. Make `name` come from `IExtension.Name` and `version` from the assembly's `AssemblyInformationalVersion` when there is no manifest.

### 2.5 Registration into `EngineServices` / `StepCatalog`

New internal type in the Core, `ExtensionHost` (loads once; `IReadOnlyList<LoadedExtension>`, `IReadOnlyList<ExtensionProblem>`):

- `LoadedExtension(Name, Version, Folder, Assembly, IExtension Instance, string Source /*root*/, string Hash /*sha256 of main DLL*/)`.
- `EngineServices.Create` adds, **before** `options.ConfigureEngineServices` (so the host can still override) but after `AddAITaskAgentYaml`: `foreach (e in host.Extensions) services.AddExtension(e.Instance);`. One insertion at `EngineServices.cs:121-123` `[V]` (the lines between `extras?.Tools?.Register(services)` and `ConfigureEngineServices`). The existing static `KnownLlmServices` dictionary `[V]` `EngineServices.cs:54-60` is hard-coded for LLM services; an extension that brings its own `ILlmService` would need a way to be listed. `[?]` Not needed for the stated goal; skip, but note that `ServiceType` stays closed.
- `StepCatalog.Create(options)` already derives extensions and steps from the collection (§2.1); no change for the catalog list. Add to `StepTypeDto` an optional `extension` (name) so the UI can show the origin. Today `CategoryOf` returns only `other`; an extension may want its own group, so allow it to declare `StepCategory` through an optional attribute on the step (`[StepCategory("analysis")]`) or in `extension.json`. `[?]` Requires the frontend `StepCategory` union to widen: `ClientApp/src/api/types.ts:104` types it as `StepCategory`; `paletteModel.ts:37` already folds unknown values into `other` `[V]`, so an unknown category degrades gracefully but would not get its own group.
- `ExtensionHost` lives in `AddWorkflowDesigner` as a singleton registered before the catalog (`WorkflowDesignerServiceCollectionExtensions.cs:39`).

### 2.6 `ConfigurationModel()` to the frontend, help, compatibility

- `ExtensionDto(name, configurationSchema)` already exists `[V]` `StepCatalog.cs:138`; it needs `version`, `source` (folder), `description`, `problems`. Today the frontend gets `extensions: ExtensionDto[]` `[V]` `ClientApp/src/api/types.ts:137`; adding fields is additive.
- Where does an extension's configuration **live**? `config.json` is the PascalCase shape of `environment.json` with fixed sections `[V]` (`PACKAGES-AND-TEMPLATES-SPEC.md:69-87`). The `ConfigurationModel()` schema describes the extension's section; an extension reads it from `IConfiguration`, which `EngineServices.BuildConfiguration` builds from `AITaskAgent:LlmProviders|Timeouts|Storage` only `[V]` `EngineServices.cs:160-200`. **There is no path today for an extension's section to reach the engine** and `IExtensionBuilder` does not expose configuration `[V]` (`R:docs/extensibility/custom-extensions.md:99`). Needed: the Core passes unknown top-level sections of `config.json` as `AITaskAgent:Extensions:<Name>` into `BuildConfiguration`. Size M; without it extensions have steps but no per-package configuration. `[?]` Decision for the user: do extension settings travel in the package's `config.json` (and then they pass through trust and secrets handling, e.g. `enc:v2:` in `ConfigStore`) or in an extension-local file.
- **Compatibility**: an `int Contract` that the host owns (`ExtensionContract.Current = 1`), the manifest's `contract` must be `<= Current` and `>=` the host's minimum supported; plus `frameworkMin`. The loader also compares the `AssemblyName.Version` of `AITaskAgent.Abstractions` that the extension was compiled against (read from its `AssemblyReferences` with `MetadataLoadContext`, or simply `Assembly.GetReferencedAssemblies()` after loading) against the host's: higher major/minor than the host = refuse with a clear message before running any code from it; lower = load (additive contract). `[?]` Whether the framework versions its assemblies in a way that makes this comparison meaningful (assembly versions of the `AITaskAgent.*` projects): I did not check the csproj `Version`/`AssemblyVersion` (the csproj files I read have neither), so today every build is `1.0.0.0` `[I]` and the check would be vacuous. That makes the **explicit `contract` integer in the manifest** necessary rather than optional.

### 2.7 Load errors that do not take the host down

- Per extension: `try { load ALC; read manifest; resolve type; new(); } catch` and also `Register` inside a `try` at host start, in a **scratch** `ServiceCollection` (so a throwing `Register` or a tag clash between two extensions is found once, at start, and the extension is excluded, rather than at the first run).
- `ExtensionProblem(Folder, Name?, Code, Message)`: `contract-too-new`, `bad-manifest`, `no-extension-type`, `load-failed` (with `LoaderExceptions` truncated like BRMS `NugetPackageLoader.cs:95-102`), `register-failed`, `tag-conflict`, `duplicate-name`.
- Tag conflicts: `StepRegistry.Add` throws `InvalidOperationException` on a second type under the same tag `[V]` `StepRegistry.cs:58-61`; in the scratch pass, exclude the later extension as a whole (partial registration of one extension would leave a half-working set).
- Exposure: `CatalogDto` gets `extensionProblems`; Web `GET /api/catalog` shows them in a banner; CLI `list-steps` prints them to stderr and `--strict` makes them exit 1. `[V]` the CLI `list-steps` builds `StepCatalog.Create(new WorkflowDesignerOptions())` with no workspace `R:Samples/WorkflowDesigner.Cli/Commands/ListStepsCommand.cs:28-29`: with extensions in `<Workspace>/extensions` it needs a `--workspace` option or `--extensions`.
- A step that throws at run time is the existing step-failure path (run error), not a host failure.

## 3. Relation to the zip packages

### 3.1 Declaring requirements in `manifest.json`

RN-01 says only the entries of §4.1 are accepted `[V]` `PACKAGES-AND-TEMPLATES-SPEC.md:98`, enforced by `entry-not-allowed` `[V]` `R:Samples/WorkflowDesigner.Core/Packages/PackageArchive.cs:284,557`. The manifest, in contrast, preserves unknown properties on save through `[JsonExtensionData] Extra` `[V]` `R:Samples/WorkflowDesigner.Core/Packages/PackageManifest.cs:94-96`. A new optional manifest field is therefore compatible with `formatVersion 1` readers (they keep it) `[I]`; whether to bump `formatVersion` is a decision: RN-05 rejects a higher version `[V]` spec line 102, so bumping would lock old readers out. Recommendation: no bump, additive optional field; **the Core derives requirements, the manifest only records what the package was last validated against** (same philosophy as templates: `§7.5` "derived, not written by hand" `[V]` spec line 292).

```json
"requires": {
  "extensions": [
    { "name": "CodebaseAnalysis", "min": "1.2.0", "sha256": "ab12..." }
  ]
}
```

- `name` = `IExtension.Name` (the dedupe key in `AddExtension` `[V]`).
- `min` = minimum version (semver); no `max` (extension authors keep backward compatibility, as with the contract).
- `sha256` optional = hash of the main DLL the package was built against. A warning if different, never a block (a rebuilt DLL changes the hash on every compile; it is useful for pinning in published packages).
- Derived at save: from `workflow.yaml` the set of step tags -> owning extension (`StepCatalog` knows each tag's `Type`, so the assembly, so the extension). Write `requires` only for tags that an extension owns. A package that uses only built-in steps has no `requires` and stays byte-identical to today's (important: RN-06 determinism and existing zips).

### 3.2 What the designer and CLI do when something is missing

Today an unknown `type` is not an error at read time: `YamlReader` looks the tag up with `catalog.Find` and, if null, still reads the step; container keys are not recognised (children of an unknown container would be read as plain properties) and parameter types fall back to `None` `[V]` `R:Samples/WorkflowDesigner.Core/Documents/YamlReader.cs:255-256,291,304`. `[?]` Whether a re-emit of such a step reproduces the original YAML byte for byte (nested steps under an unknown container as raw properties): I did not trace the emitter; **this is the biggest risk for "open a package without the extension and save it"**. Rule to implement: when a package has unknown tags, open it **read-only** (or block saving the workflow) with the message below; do not allow editing a document the reader cannot model.

Proposed behaviour, one message everywhere: `Step 'X' (type 'Y') needs extension 'Z' >= 1.2.0, which is not installed (searched: <dirs>).`

| Place | Behaviour |
|---|---|
| Validation (`WorkflowValidator`) | an error per step with an unknown tag naming the required extension from `manifest.requires` (or "unknown step type" without it). Distinct code `extension_missing` vs `step_unknown`. |
| Run (`RunManager`) | refuse before starting (same check as validation), 422 in the Web. |
| Open in the Web | opens, banner with the missing extensions, editing of unknown steps disabled, save blocked while unknown tags exist (`[?]` see the emitter caveat). |
| CLI `validate`, `run`, `package info` | exit code as for validation errors; `package info` lists `requires` with `present / missing / version too low`. |
| Version too low | error with both versions. |
| Hash differs | warning. |

### 3.3 Can the extension travel inside the zip?

**Recommendation: no, by name only.** Reasons:

1. RN-01 allows only a fixed list of entries, with the explicit example "a zip with `scripts/x.js` ... is rejected whole" `[V]` spec line 98. Admitting `extensions/*.dll` would break the container invariant that the zip is **data** (YAML, JSON, Markdown) and that `ExtractTo`/validation never produce an executable `[V]` (`PackageArchive.cs:284`, "never ExtractToDirectory" `core-api.md:377`).
2. Trust model: today a foreign package is "equivalent to running its code" only through the `Mcp`/`FileTools` configuration it can set, and the user sees exactly those `[V]` spec line 313. A DLL inside would make **every imported zip** an arbitrary-code carrier invoked by the act of listing or validating (catalog is built at start, but the extension would have to be loaded to validate), not by running it.
3. Limits: zip 20 MiB / entry 5 MiB / ratio 100:1 `[V]` `core-api.md:383`; a CodebaseAnalysis-sized extension (SQLite, PdfPig, OpenXml `[V]` csproj) does not fit and shares nothing across packages.
4. Determinism and dedup: N packages would each carry a copy.

The portable alternative for sharing: an **extension bundle** (a zip of an extension folder, kind distinct from `package`, installed by an explicit `extension install <zip>` with its own trust dialog, §4). That is a separate artifact, off the package format.

## 4. Security (honest model)

**What loading does.** `LoadFromAssemblyPath` plus instantiating the `IExtension` executes the extension's code with the process's identity and permissions: module initializers and static constructors run at load, `Register` runs at host start, steps run in the run's process. .NET has no sandbox for managed code (no CAS since .NET Core) `[I]`. An `AssemblyLoadContext` isolates **types and versions**, not capabilities.

**Fit with the existing model.**

- `PackageTrust` hashes only `Mcp` servers and `FileTools` folders/root `[V]` `R:Samples/WorkflowDesignerWeb/Hosting/PackageTrust.cs:112-124`; the DTO shows them `[V]` `:12-27`. It guards **package-supplied configuration**. A DLL in an extension folder is **not** package content: it is installed by the machine's user, the same way the user installs the CLI itself. The right model is therefore two separate trusts:
  1. **Extension trust** (host-level, about code): which DLLs may be loaded. Installed = trusted, because only someone with write access to `extensions/` can add one; the file system is the boundary.
  2. **Package trust** (existing, about data that triggers behaviour).
- A foreign package can now reach more code: it can name any step tag an installed extension provides and set that step's properties. An extension step may read files, call HTTP, spawn processes (CodebaseAnalysis runs indexers and graph tools `[I]`), exactly as the user wrote it. So an installed extension **widens what an imported package can do**, and `PackageTrust.Required` today would not notice: a package with no `Mcp` and no `FileTools` entries is "trusted as is" `[V]` `PackageTrust.cs:120,123`, "so `hello-script` ... run without ceremony" `[V]` `docs/designer/architecture.md:589-591`. **Required change (S):** `required` also becomes true when the workflow uses any step whose owning extension is **not built in** (framework `flow`/`llm`/`script`, or an extension flagged `trustedByDefault` by the host config), and `TrustDto` shows the extension name, version, folder and DLL sha256 per used extension, plus the steps of that package that use it. The trust hash includes the extension identity (name + sha256), so replacing the DLL asks again.
- Script steps are the model: `ScriptStep` runs in its own worker process with memory and time limits and a job object `[V]` `R:AITaskAgent.Extensions.Script/README.md:3,38-44`; dynamic extension steps get **none of that**: they run in the host process, unlimited, with the host's secrets in memory (`MasterKeyProvider`, decrypted provider keys) `[I]`. This is the honest difference: a malicious or buggy extension can read the master key and every secret. Out-of-process extensions (the `ScriptStep` worker pattern) would be the only real mitigation and are out of scope (cost L, and `IStep` cannot cross a process boundary without a protocol `[I]`).

**Dialog contents** (Web, when a package that needs a non-built-in extension is first run/validated/with tools, and in the "Extensions" admin page):

- Per extension: name, version, folder path, sha256 of the main DLL, signer if Authenticode/strong-name present (`[?]` not checked, probably unsigned), the list of assemblies it brought, and **which steps of this package use it**.
- Plain-language warning: "extensions run code with your permissions and can read your keys"; the existing MCP/FileTools block is kept as is.
- A first-use confirmation for the **extension itself** (not just the package) when its DLL hash changes (stored next to `trust.json` style in `<data>/extensions/trust.json`, `[I]` new file).

**What this protects:** accidental load of an unlisted DLL; silent replacement of a DLL (hash); an imported package silently activating powerful steps; load failures taking the host down. **What it does not:** a hostile DLL that the user installs and trusts; a hostile DLL's reach into host memory; supply-chain issues in the extension's private dependencies; a package author who shares a package together with a link to "install this extension" (social engineering: the dialog text must say the code is not reviewed).

CLI: consistent with spec §8 ("The CLI does not ask (whoever launches it decides), but `package info` shows it and `run` prints it to stderr if there is `Stdio`" `[V]` spec line 313; implemented `[V]` `R:Samples/WorkflowDesigner.Cli/PackageSupport.cs:131-132`, `RunCommand.cs:120-122`). Same pattern: `--extensions <dir>` is an explicit act; `run` prints to stderr `loaded extension 'X' 1.2.0 from <dir> (sha256 ...)` for each non-built-in extension, and warns when an extension is auto-discovered from `<Workspace>/extensions` rather than passed explicitly.

## 5. Impact

### 5.1 Tests (no network, no LLM)

- Today tests inject behaviour through `ConfigureEngineServices` and a scripted `ILlmService` `[V]` `R:Samples/WorkflowDesigner.Core.Tests/TestSupport.cs:15-60`, `ApiKeyEncryptionTests.cs:223-224`. Catalog tests assert extension names `AITaskAgent.Llm` and `Script` `[V]` `CatalogTests.cs:75-79`.
- Add a tiny test-only project `WorkflowDesigner.TestExtension` (net10.0, one `IExtension` + one `[StepTag("TestUppercase")]` step + one broken variant), referenced from `WorkflowDesigner.Core.Tests` with `ReferenceOutputAssembly="false"` and a build target copying its publish output into `<test bin>/test-extensions/<name>/` (framework DLLs excluded the way a real extension would; this is exactly what exercises the shared-set rule). Cases, all local: loads and appears in the catalog; step runs inside a pipeline; two extensions with different `Newtonsoft`-independent private dependencies coexist `[?]` needs a tiny private dependency in the fixture; tag conflict excludes the later one; `contract` too new => problem, host intact; throwing `Register` => problem, host intact; missing manifest => reflection fallback; duplicate name; package with `requires` and the extension absent => `extension_missing`; version too low.
- The type-identity bug (a duplicate `AITaskAgent.Abstractions.dll` copied into the extension folder) deserves its own test: it must produce a clear `load-failed`/shared-set message, not "does not implement IStep" `[V]` message text `StepRegistry.cs:34`.
- Build order trap `[I]`: a `ProjectReference` with `ReferenceOutputAssembly=false` does not order or copy anything by itself; use `<Targets>Publish</Targets>`-style `MSBuild` task or a post-build `Copy`. Mention in the test project, not in production.
- `Core.Tests` mutate process-wide static state (`FileToolsConfiguration`) already `[V]` `Engine/EngineTools.cs:313-329`; ALC loading is process-wide too, so loader tests must not run in parallel with themselves (xunit collection).

### 5.2 Frontend

- Catalog types gain `extension?`, `extensions[].version/source/problems`. Palette groups by `category` and uses `description` as help `[V]` `ClientApp/src/features/palette/StepPalette.tsx:5`; unknown categories already fold to `other` `[V]` `paletteModel.ts:37`. v1 needs only: show the extension name in the palette item tooltip, a "Extensions" panel with problems (S), and the missing-extension banner (S).
- Parameter forms come from the step's JSON Schema (`parameters`) plus `x-editor` hints chosen **by category** for script/llm only `[V]` `StepCatalog.cs:167-185`: an extension step gets generic editors; a new hint needs a framework attribute (not planned).
- Layout of unknown steps: the canvas reads the catalog for containers `[I]` (`useCanvasModel`); an unknown tag renders as a leaf. Needs a visible "unavailable step" node style (M).

### 5.3 CLI

- `--extensions <dir>` (repeatable), `--workspace` implies `<ws>/extensions`, `WORKFLOW_DESIGNER_EXTENSIONS` (path list) for CI. `ListStepsCommand` opens no workspace by design `[V]` `ListStepsCommand.cs:28` and needs the new option.
- `CliHost.Open` builds `WorkflowDesignerOptions` from `--workspace`/`--data` `[V]` `R:Samples/WorkflowDesigner.Cli/CliHost.cs:53-67`; `OpenTemporary` for validate `[V]` `:85-91`. Both need to pass the extension directories (validate must load them: otherwise every package with an extension step fails validation).
- New commands (optional, M): `extension list`, `extension info <dir>`, `extension verify <dir>` (load in a scratch collection, report problems), useful as the author's test of §2.7.

### 5.4 Publish and compilation

- `dotnet publish` of the Web/CLI does not include extensions (they are not referenced); the install gets an empty `extensions/` and the SPA copy step is unchanged `[V]` `WorkflowDesignerWeb.csproj` publish notes. A published extension is its own `dotnet publish` output folder; its `.deps.json` makes `AssemblyDependencyResolver` work. For an extension project: reference framework projects with `<Private>false</Private>` (`ExcludeAssets="runtime"` for package references) so the framework DLLs are **not** copied beside it; otherwise they sit in the folder and the loader must still ignore them (it does, by the shared-set rule, but copying them invites confusion and size).
- **Optional static reference for development** (debugging with breakpoints and F5 without copying DLLs): a `ProjectReference` from a dev-only host project (or a `Directory.Build` property `-p:StaticExtension=...`) plus `options.ConfigureEngineServices = s => s.AddExtension<MyExtension>()`, which is the path that already works today `[V]` (`WorkflowDesignerOptions.cs:70-76`). The loader skips an extension whose `Name` is already registered statically: that already happens by design because `AddExtension` ignores duplicates by `Name` `[V]` `ExtensionServiceCollectionExtensions.cs:20-35` (so register static ones **first**, i.e. loader after, or simply let the loader check `services` before adding).
- Single-file / trimmed / AOT publish of the host would break reflection loading `[I]`; the current csproj files set none of these `[V]` (no `PublishSingleFile`/`PublishTrimmed` in the three csproj I read). Note it as a constraint.

## 6. Options compared and recommendation

| | A. Pure dynamic loading | B. Static reference + registration in `Program.cs` / `ConfigureEngineServices` | C. Hybrid (recommended) |
|---|---|---|---|
| New extension without recompiling the designer | yes | no (rebuild host) | yes |
| CodebaseAnalysis as extension | yes (after the step-tag work, §0.3) | yes (same work) | yes |
| Type identity / version risks | real (§2.3) | none (one build) | only on the dynamic path; dev path has none |
| Isolation of private dependencies | yes (ALC) | no (one dependency graph; a conflict is a build error, which is arguably good) | yes on the dynamic path |
| Security story | needs trust UI for code | none new (code is in the build) | same as A; static ones count as built in |
| Debugging experience | worse (attach, copy DLL) | best | best for authors, good for users |
| Effort | M-L | S (already works today `[V]` `WorkflowDesignerOptions.cs:70-76`) | A + 10% |
| Tests | needs fixture project | trivial | fixture project |

Recommendation: **C**. The user's decision is that an extension is something the designer's user adds without a rebuild, so A must exist; B is already supported and costs nothing to keep as the author's inner loop.

### Cost per piece (S < 1 day, M = 1-3 days, L > 3 days)

| Piece | Size | Note |
|---|---|---|
| `ExtensionLoadContext` (shared set, `AssemblyDependencyResolver`, native) | M | non-collectible, §2.3 |
| `ExtensionHost`: scan roots, manifest, reflection fallback, scratch `Register`, problems list | M | §2.4, §2.7 |
| `EngineServices` hook + `StepCatalog` (extension origin, DTO fields) | S | one line at `EngineServices.cs:121-123` plus DTO |
| `WorkflowDesignerOptions.ExtensionDirectories`, Web config, CLI `--extensions` (`list-steps`, `validate`, `run`, `CliHost`) | S-M | |
| Extension configuration into `AITaskAgent:Extensions:<Name>` and per-package config section | M | decision pending (§2.6) |
| Contract integer + compatibility check | S | explicit integer, §2.6 |
| `manifest.requires` derive-and-write; validator/run `extension_missing`; `package info` | M | §3 |
| Read-only open / save blocked with unknown tags (needs the emitter check) | M | `[?]` risk §3.2 |
| `PackageTrust` extended to extensions (+ DTO + dialog + `<data>/extensions/trust.json`) | M | §4 |
| Frontend: extension info panel, missing-extension banner, unavailable-step node | M | §5.2 |
| Test fixture project + loader tests | M | §5.1 |
| Docs: amend the three "no dynamic loading" texts, extension author guide | S | `IExtension.cs:10`, `docs/extensibility/custom-extensions.md:3`, `docs/ARCHITECTURE-v2.md:53` |
| Make CodebaseAnalysis steps YAML-declarable extension (tags, `IExtension`, I/O types) | L | independent of the loader, §0.3 |
| `extension install` from NuGet | L | not in v1 |
| Hot unload / reload | L and probably not feasible with current singletons | not recommended |
| Out-of-process extensions (sandbox) | L | the only real security mitigation; defer |

Suggested order: loader and host + hook + tests (the vertical slice that proves §2.3 with a fixture), then CLI `--extensions`, then `requires`/validation, then trust and UI.

## 7. What I could not check

- Whether the CodebaseAnalysis steps (`IStep<ReportFacetInput, ...>`) can be declared in YAML at all, and what `AnalysisPipelineFactory` assumes (`[?]`, §0.3).
- Whether `YamlEmitter` round-trips a step with an unknown tag and nested steps unchanged (`[?]`, §3.2). Needed to decide read-only vs editable on missing extensions.
- The assembly version policy of the `AITaskAgent.*` projects: the csproj files I read declare no `Version`/`AssemblyVersion`, so a version-based compatibility check is probably vacuous (`[I]`, §2.6).
- `McMaster.NETCore.Plugins` current API and net10.0 support (not read, general knowledge only).
- Behaviour of Newtonsoft/NJsonSchema static caches and the memory behaviour of a collectible ALC in this host (no experiment run; no build was made).
- Whether any current Web/CLI test would break if `StepCatalog` gains DTO fields (tests assert the exact serialized key order of `StepTypeDto`: `["tag","description","category","parameters","containers","structural"]` `[V]` `CatalogTests.cs` near line 90; an added `extension` property must be placed last or that test updated).
- The frontend behaviour on an unknown `type` at canvas level (read `StepPalette`/`paletteModel` only, not the canvas).
- Authenticode/strong-name state of any candidate extension.
- I did not read BRMS's host application code, only `BRMS.Core`; how its `NuGetLoaderConfig` is actually populated in production is unknown.

---

# Addendum: CSharpStep (in-memory Roslyn), alternative to dynamic assembly loading

Scope extension requested 2026-10-19. Same marks and rules. Idea: C# source travels as text in the package and is compiled in memory when the workflow runs; YAML stays the model; C# only in leaf steps.

## A.1 Fit with `StepRegistry` / `IExtension`

- `ScriptStep` is the exact template: `[StepTag("ScriptStep")]`, `[Description]`, `class ScriptStep : StepBase`, properties `Script`, `TimeoutMs`, `MaxHeapMb`, `MaxStackKb` `[V]` `R:AITaskAgent.Extensions.Script/Steps/ScriptStep.cs:56-58,99-124`. It converts the input to a JSON token, sends it to the worker with the context through a bridge, and returns a `JsonStepResult` `[V]` `ScriptStep.cs:~152-176`. A `CSharpStep` would be a sibling extension `AITaskAgent.Extensions.CSharp` (`IExtension` + one step), registered with `AddExtension` like `AddScriptExtension` `[V]` `R:AITaskAgent.Extensions.Script/Configuration/ScriptServiceCollectionExtensions.cs:11`. The designer would add it in `EngineServices.Create` next to `AddScriptExtension()` `[V]` `R:Samples/WorkflowDesigner.Core/Engine/EngineServices.cs:117`; `CategoryOf` would get a `csharp` category (today unknown assemblies fall to `other`) `[V]` `StepCatalog.cs:154-161`. The step's `parameters` schema, description and `x-editor` hint come through the registry for free (`code:csharp` would be a new hint; today only `code:javascript`, `StepCatalog.cs:172-176`).
- `DelegatedStep` is **not** a usable template: it takes a compiled `Func<...>` in its constructor `[V]` `R:AITaskAgent.Core/Steps/DelegatedStep.cs:12-15`, so YAML text cannot build it. The parked `Designer/Dynamic` `DynamicJs*` steps were the same idea (§0.1). `ScriptStep` already solved "code as text in YAML" for JS.
- Proposed fixed signature (JSON in, JSON out, like `ScriptStep`, so both execution modes in A.6 stay possible):

```csharp
// The YAML text is the BODY; the wrapper is generated.
public static async Task<object?> Run(JToken input, CSharpContext context, CancellationToken cancellationToken) { /* body */ }
// CSharpContext: Get(string key) -> JToken?, Set(string key, JToken value), Log(string message). Same surface as ScriptStep's context.get/set/log.
```

  The return value is serialized with `JToken.FromObject` (as `ScriptStep.ToJsonToken` does `[V]` `ScriptStep.cs:~181-187`). Fixed default usings (System, System.Linq, System.Collections.Generic, System.Text, System.Threading.Tasks, Newtonsoft.Json.Linq). **Do not hand the step `PipelineContext` or DI**: that ties it to in-process execution and to framework versions; if a step needs them, the answer is a real assembly (A.9). `attempt` and `lastStepResult` of `DelegatedStep` are dropped on purpose.

## A.2 Roslyn: packages, weight, and the v2 decision

- v2 removed Roslyn **from the framework**: "The framework loses `Microsoft.CodeAnalysis.CSharp` and `NJsonSchema.CodeGeneration.CSharp` (which `SchemaCompiler` used)" `[V]` `R:docs/ARCHITECTURE-v2.md:43`; "YAML v2 without Roslyn" `[V]` `R:docs/RETOMAR.md:11`. The reason was that `SchemaCompiler` generated a C# type per schema (`ARCHITECTURE-v2.md:77`). A `CSharpStep` in a **separate optional extension** does not break that, but it does reverse the "YAML engine without Roslyn" line in the docs, so the extension must stay outside `AITaskAgent.Core` and `AITaskAgent.Yaml`. The user's remark "not needed in BRMS" is not in the repo docs I read `[?]`; I found no Roslyn or `CSharpScript` use in `BRMS.Core` `[V]` (grep over `*.cs` and `*.csproj`: no matches).
- Roslyn is **already in the repo** at 5.0.0: `FileTools` (`Microsoft.CodeAnalysis.CSharp` `[V]` `R:FileTools/FileTools.csproj:10`) and `DotNetEnricher` (`...CSharp.Workspaces`, `Workspaces.MSBuild` `[V]` `R:Samples/DotNetEnricher/DotNetEnricher.csproj:18-19`, comment "la misma versión de Roslyn que el Framework y FileTools" `:17`). The designer Core references `FileTools` `[V]` `R:Samples/WorkflowDesigner.Core/WorkflowDesigner.Core.csproj:21`, so Roslyn already reaches it transitively. Compiling needs only `Microsoft.CodeAnalysis.CSharp`: no new package for the compile-in-host variant. Size: a few MB per compiler assembly plus localized resource folders, tens of MB in total `[I]`; not measured `[?]`.
- If compilation happens in a separate worker (A.6), that worker takes `Microsoft.CodeAnalysis.CSharp` where the Script worker takes ClearScript `[V]` `R:AITaskAgent.Extensions.Script.Worker/*.csproj:24-28`.

## A.3 Metadata references under the .NET runtime without SDK

- Reference assemblies (`Microsoft.NETCore.App.Ref`) live in the SDK/targeting pack, not in a runtime-only install `[I]`. Without an SDK the usual way is to reference the **implementation** assemblies of the running runtime: enumerate `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")` and use `MetadataReference.CreateFromFile` over a fixed allowlist (System.Runtime, System.Private.CoreLib, System.Linq, System.Collections, System.Text.*, Newtonsoft.Json, the host contract assemblies) `[I]`. It compiles; quirks: type forwarding through `System.Runtime`, and implementation-only public types visible that a reference surface would hide `[I]`.
- Use an **allowlist**, not the whole TPA: it decides what the author can call and is the only cheap lever on the surface. Leaving out `System.Diagnostics.Process`, `System.Net.*`, `System.IO.FileSystem` stops honest mistakes, **not malice** (reflection via `typeof(object).Assembly`, `Type.GetType` by name).
- Single-file or trimmed publish makes `Assembly.Location` empty `[I]`; the hosts do not publish that way today (no such properties in the csproj files I read).
- Shipping pinned reference DLLs as resources gives deterministic output across machines at tens of MB `[I]`; decide before the first shared package depends on it. `[?]` Not tested.

## A.4 Collectible `AssemblyLoadContext`

`new AssemblyLoadContext(name, isCollectible: true)` + `LoadFromStream(emitted dll)`; call a static `Run` through a delegate created once; drop the delegate and `Assembly`, `Unload()`. Here, unlike extension DLLs (§2.3), it can work: the registry stores one registered `CSharpStep` type by tag `[V]` `R:AITaskAgent.Core/Steps/StepRegistry.cs:20,63`, and does not hold the user-code assembly, so the generated assembly can be per hash and collectible `[I]`. Unload is blocked by a still-running thread, a pending `async` continuation, a static event, `Task.Run` leftovers or a `Timer` the user code started `[I]`: an infinite loop **pins the context forever**. So collectible protects memory against many edited versions of a small step, not against misbehaviour.

## A.5 Cache by hash, compile time, error mapping

- Key = sha256 of (wrapper version + fixed usings + reference-set id + body). Cache the **emitted DLL** (and PDB) on disk under `<data>/csharp-cache/<hash>.dll`, plus an in-memory `Lazy` per hash. Same code across packages and runs compiles once.
- Time: the first Roslyn compilation in a process is slow (JIT of the compiler, order of 1-3 s) and warm compiles of a ~20-line body take tens to a few hundred ms `[I]`; **not measured (`[?]`)**. This matters for A.6: compile per run inside a one-process-per-run worker pays the cold cost every time unless the DLL is cached on disk and the worker only loads it. For reference, `ScriptStep` already costs about 0.8 s per execution for process plus V8 startup `[V]` `R:AITaskAgent.Extensions.Script/README.md:50-53`.
- Compile at **validation** time too (the designer validates by building the real pipeline `[V]` `R:Samples/WorkflowDesigner.Core/Validation/WorkflowValidator.cs:42`), so a syntax error shows before a run. Emitting does not execute user code (no analyzers or generators loaded) `[I]`, so validating an untrusted package stays safe.
- Mapping: generate a header with a **known number of lines**; take `Diagnostic.Location.GetLineSpan().StartLinePosition` and subtract the header to get line and column inside the body; add the YAML line where the block scalar starts. The error model already carries 1-based `Line` and `Column` of the YAML `[V]` `R:Samples/WorkflowDesigner.Core/Documents/WorkflowError.cs:7-14` and `YamlLineMap` has each step's span and key lines `[V]` `WorkflowError.cs:30-42`. The column needs the block's indentation offset `[I]`. `[?]` The map stores one line per key (`keyLines`), not the lines inside a block scalar, so it needs a small extension. Keep the CS diagnostic id and severity; warnings do not block.

## A.6 In-process vs separate process

| | In-process (backend/CLI) | Separate worker (like `ScriptStep`) |
|---|---|---|
| `Environment.Exit(0)` in the body | **kills the Web backend / CLI** `[I]` | kills the worker only; the host returns an error result with the step id `[V]` `README.md:44` |
| `while(true){}` | cannot be stopped: .NET has no `Thread.Abort`, cancellation is cooperative; a pinned core and thread until restart `[I]` | host kills the process at `timeoutMs` + 2 s grace, or on cancel `[V]` `R:AITaskAgent.Extensions.Script/Isolation/ScriptWorkerClient.cs:43,172,301-308` |
| Stack overflow / unhandled exception on another thread | process crash `[I]` | worker crash only |
| Memory runaway | host OOM | Windows job object; elsewhere a 25 ms watchdog `[V]` `README.md:42-43` (Linux path not run, `README.md:57`) |
| Static state shared with host (`FileToolsConfiguration` is already process-wide `[V]` `Engine/EngineTools.cs:313-329`) | yes | no |
| Cost per run | ~0 with cached compile | +0.4-0.6 s .NET startup `[V]` `README.md:50-52`; compile not repeated if the host compiles and caches the DLL |
| Access to `PipelineContext`/DI/real types | yes | JSON plus a bridge only (as `context.get` in `ScriptStep` `[V]` `README.md:33-34`) |

**Can the `ScriptStep` worker be reused?** The mechanics (process start, JSON-lines protocol, job object, kill, watchdog) are reusable in principle, but `ScriptWorkerClient` and the protocol types are `internal`, the request carries V8 limits (`ScriptProtocol.cs:7`; `WorkerRequest(Name, Script, inputJson, TimeoutMs, MaxHeapMb, MaxStackKb)` `[V]` `ScriptStep.cs:~158`), and the worker project is bound to ClearScript. Feasible path: extract a neutral isolation library (client, job object, locator, envelope) and add `AITaskAgent.Extensions.CSharp.Worker`, which loads the cached DLL and calls `Run`; the V8 heap/stack knobs go away and the job-object limit stays process-wide `[I]`. Cost M-L, and it touches a component with 51 tests `[V]` `R:docs/RETOMAR.md:11`. The worker does not need Roslyn if the host compiles (smaller, faster start).

**Recommendation:** worker only. In-process buys real types and costs the host's availability on a typo; the coordinator's risk statement (`Environment.Exit` or an infinite loop takes the backend down) is correct. If ever wanted, in-process should be an opt-in `trusted: true` for the developer's own workspace, never the default.

## A.7 Debugging and browser editing

- The frontend has **no code-editor dependency**: runtime dependencies are `@dagrejs/dagre`, `@xyflow/react`, `react`, `react-dom`, `react-resizable-panels` `[V]` `R:Samples/WorkflowDesignerWeb/ClientApp/package.json:17-23`; no Monaco or CodeMirror. The hint `x-editor: code:javascript` exists `[V]` `ClientApp/src/api/types.ts:31`, `ParametersForm.tsx` consumes `x-editor`; which widget renders code I did not read (`[?]`, probably a textarea). A C# editor means adding Monaco (bundle of MB) or CodeMirror 6 (C-like highlighting, lighter) `[I]`. Neither gives IntelliSense or diagnostics for C#: that needs a language service (OmniSharp/Roslyn LSP behind a socket), cost L and more server surface `[I]`.
- Cheap alternative with real feedback: the **backend compile endpoint** (validation already builds for real `[V]` `WorkflowValidator.cs:42`) returns diagnostics with line and column (A.5) and the editor draws markers: compile on pause, no completion.
- Debugging: no browser debugger. Offer "Run this step with a sample input" (compile, worker run, return JSON, logs, exception stack with mapped lines) and emit a portable PDB so stack traces carry line numbers mapped like A.5 `[I]`.

## A.8 Trust (the code travels in the zip)

- Inline code already travels today: `ScriptStep.script` is JS inside `workflow.yaml`, RN-01 forbids separate `scripts/*.js` `[V]` spec line 98, and that JS runs in V8 where only `context.get/set` and logging reach the host, in a limited process `[V]` `README.md:33-34,38-44`. That is why `PackageTrust` never covered it: it hashes `Mcp` and `FileTools` only `[V]` `R:Samples/WorkflowDesignerWeb/Hosting/PackageTrust.cs:112-124`.
- **C# is qualitatively different**: native code with the user's permissions (files, network, processes, environment variables; and the `enc:v2` secrets and master key if in-process `[I]`). Importing a foreign zip and pressing Run must not be "no ceremony".
- Required changes, same shape as §4: `PackageTrust.Required` also true when the workflow contains a `CSharpStep`; `TrustDto` lists each C# step (id, line count, **the code**, sha256); the trust hash includes the code hashes, so editing the zip by hand invalidates trust (like a changed `Mcp`: `PackageTrust.cs:121-123`); edits made in the designer keep a trusted package trusted (existing rule `[V]` `docs/designer/architecture.md:593-595`).
- **Compile is safe without trust, execute needs trust**: require it in `POST /runs`, session turns and "run this step", not in validation. This matches today's rule that validation needs trust only when a step carries `tools` `[V]` `architecture.md:596-597`.
- A worker is **containment, not a sandbox**: it survives crashes and hangs, it does not stop `File.Delete` or `HttpClient`. The dialog must say "this package contains C# code that runs with your permissions". Hardening with real effect (restricted token or AppContainer on Windows, network isolation on Linux) is L. The compile-time API allowlist is a speed bump only (A.3).
- CLI: same rule as spec §8 ("does not ask, but prints") `[V]` spec line 313: print one stderr line per C# step before running; `package info` lists them.

## A.9 What it solves and what it does not

Solves: small user logic per package (parsing, mapping, validation, light IO) without building or installing a DLL; travels with the package and does not touch the zip format (the code is a YAML block scalar, RN-01 unchanged); versioned with the package; no extension `AssemblyLoadContext` or host-library version conflicts (the body only sees the allowlisted references).

Does **not** solve:
- **The 17 CodebaseAnalysis steps** `[V]` (17 files in `R:Samples/CodebaseAnalysis.Core/Steps/`). They are typed `IStep<ReportFacetInput,...>` with constructor DI (`AuditorStepRegistry`) `[V]` `TimeBudgetStep.cs:33,47`, and the project uses SQLite, PdfPig, OpenXml `[V]` `CodebaseAnalysis.Core.csproj`. A JSON-in/JSON-out body in a worker cannot host them. They need a real assembly plus `IExtension` and `[StepTag]` (§0.3), i.e. dynamic loading or a static reference.
- Steps that need the framework's real types (`PipelineContext`, `ILlmService`, DI): excluded by design.
- Shared helpers between C# steps (a body is one method): each step re-pastes, or the user needs an assembly. A package-level "helpers" block is a later feature `[I]`.
- Third-party NuGet libraries: the allowlist rules them out.
- Debugger or IntelliSense.

## A.10 Cost and recommended order

| Piece | Size | Note |
|---|---|---|
| `AITaskAgent.Extensions.CSharp`: step, wrapper generator, allowlisted references, compile + emit + diagnostics + hash cache | M | Roslyn already in repo `[V]`; compile in host only |
| Line/column mapping into the YAML block scalar (extend `YamlLineMap`) | S-M | A.5 |
| Worker (extract neutral isolation library from Script; new worker loading cached DLL) | M-L | A.6; refactors a tested component |
| In-process opt-in mode | S | not as default |
| `PackageTrust` + dialog showing the code, hash over code, CLI stderr lines | M | A.8 |
| Frontend: `code:csharp` editor (CodeMirror 6 or Monaco), diagnostic markers, "run step with sample" | M | A.7; no completion (L, not planned) |
| Tests: mapped compile errors, cache hit, worker killed on `Environment.Exit` and `while(true)`, trust required to execute but not to validate | M | no network or LLM needed `[I]` |
| Docs: `ARCHITECTURE-v2.md:43,77` and `RETOMAR.md:11` say "without Roslyn"; add "the framework and YAML engine; `CSharpStep` is an optional extension" | S | |

Order against the other options:
1. **Dynamic assembly loading first** if CodebaseAnalysis or other heavy extensions are the near-term need: it is the only path for them, and they also need the tag/`IExtension` work (L) either way.
2. **CSharpStep second** if the immediate wish is small own steps: independent of the loader (only the trust UI is shared), cheaper per visible feature, but with a higher security cost because the code arrives inside foreign zips, a case the loader's model avoids (extensions are installed by the machine's user).
3. If only one can be funded now: CSharpStep with **worker + trust** for "my own small steps in my own packages"; dynamic loading for "CodebaseAnalysis and future heavy extensions". They do not substitute for each other. Do not build CSharpStep in-process only.

## A.11 Not checked (addendum)

- Roslyn cold/warm compile time and package/disk size (no build or run allowed).
- Compiling against TPA implementation assemblies on this machine's runtime (no experiment).
- Which widget renders `code:javascript` in the inspector and whether it can host a second language.
- Whether `YamlLineMap` can address lines inside a block scalar (read only the `StepSpan`/`keyLines` shape).
- Whether the worker protocol can be generalized without breaking the 51 Script tests.
- Feasibility of AppContainer or restricted tokens for the worker.


---

# Study D: CmdStep, Office/Access removal, CodebaseAnalysis global state

Read-only study, 2026-10-19. Paths are relative to `C:\GeminiCli\AITask\AITaskAgent`.
Marks: `[V]` read the line, `[I]` inferred, `[?]` unknown. Recommendations are not marked.
Decisions D1-D3 are taken as given; this only studies how to do them.

---------------------------------------------------------------------------------------------------

## D1. `CmdStep` in `AITaskAgent.Core`

### 1.1 What the repo gives us today

- Core steps are registered by assembly scan: `registry.AddFromAssembly(typeof(StepRegistry).Assembly)` for every step with `[StepTag]` [V] `AITaskAgent.Core/Configuration/ExtensionServiceCollectionExtensions.cs:66`, `AITaskAgent.Core/Steps/StepRegistry.cs:68-85`. Consequence: a `[StepTag("CmdStep")]` class in Core is available in every host that calls `AddAITaskAgentCore()`, including the designer engine, with no opt-in [I from the two lines above]. See 1.9 for what that implies.
- Closest models: `SuspendStep` (Core, JSON result, `MaxRetries = 1`) [V] `AITaskAgent.Core/Steps/SuspendStep.cs:59`; `ScriptStep` (extension, process isolation, own `TimeoutMs`, `MaxRetries = 1`) [V] `AITaskAgent.Extensions.Script/Steps/ScriptStep.cs:95`. `DelegatedStep<TIn,TOut>` exists but is the programmatic delegate step, not declarable in YAML (no `[StepTag]`) [V] `AITaskAgent.Core/Steps/DelegatedStep.cs:1-35`.
- `StepBase` gives: `Name`, `MaxRetries` (default 3) [V] `StepBase.cs:31`, `Timeout` (TimeSpan?) [V] `StepBase.cs:73`, `CreateErrorResult(message, exception)` [V] `StepBase.cs:106`, `EnrichStartedEvent` hook [V] `StepBase.cs:151`, `Logger` [V] `StepBase.cs:63-ish`.
- YAML properties are filled with `JsonSerializer.Populate` over public settable properties, and the schema comes from `TypeSchemaBuilder.Build(type)` [V] `StepRegistry.cs:100-111,142-146`. So `string[]`, `Dictionary<string,string>`, `int`, `bool`, enums are declarable; a "string or list" union is not a natural type here [I].
- The only error type is `IStepError { Message, OriginalException }` and the record `StepError` [V] `AITaskAgent.Abstractions/Abstractions/IStepError.cs`, `AITaskAgent.Abstractions/Models/StepError.cs:9-16`. There is no error code or kind field. "Typed" error therefore means: put a typed exception in `OriginalException` (as `TimeoutMiddleware` does with `TimeoutException`) [V] `TimeoutMiddleware.cs:~70`.
- Retry semantics matter a lot for a command: an error result WITH an exception is not retried; one WITHOUT an exception is retried up to `MaxRetries` (default 3) [V] `AITaskAgent.Core/Execution/Middlewares/RetryMiddleware.cs:79-97`. A command that has side effects must not be silently re-run.
- Timeout: `TimeoutMiddleware` wraps Retry, so the timeout is global over all attempts; on expiry it cancels the token and, if the step returns an error result instead of throwing, the middleware replaces the result with a `TimeoutException` error [V] `TimeoutMiddleware.cs:41-62`. The designer's default step timeout is 300 s [V] `docs/designer/architecture.md:1106` (`"DefaultStepTimeout": 300`). A CmdStep must therefore honour cancellation by killing the process, otherwise the middleware reports a timeout while the process keeps running.
- Template engine: `TemplateExpression.Render(template, input, context)` renders `{{input.x}}` / `{{context.x}}`; text without `{{` is returned as is [V] `AITaskAgent.Core/Support/Template/TemplateExpression.cs:39-47`. `Resolve` keeps JSON structure for a bare path [V] `:56-70`.
- Process-kill precedent: `ScriptWorkerClient.Kill` does `process.Kill(entireProcessTree: true)` + `WaitForExit` [V] `AITaskAgent.Extensions.Script/Isolation/ScriptWorkerClient.cs:301-309`. The `WindowsJobObject` is `internal` to the Script extension and hard-codes `ActiveProcessLimit = 1` (forbids children) plus a memory limit, with `KILL_ON_JOB_CLOSE` [V] `Isolation/WindowsJobObject.cs:14,16-18,69-73`. It is not reusable as is for a command that spawns children.

### 1.2 Proposed YAML (flat properties, no union types)

```yaml
- id: build
  type: CmdStep
  executable: dotnet                      # required; literal only (no templates), see 1.9
  args: ["build", "-c", "{{input.config}}", "--nologo"]   # list: each item rendered separately
  # commandLine: 'dotnet build -c {{input.config}}'       # alternative to args, mutually exclusive
  workingDirectory: C:\work\proj          # optional; default: process current directory
  env:                                    # optional; values rendered with templates
    DOTNET_CLI_TELEMETRY_OPTOUT: "1"
  inheritEnvironment: minimal             # minimal | all  (default minimal, see 1.7)
  stdin: "{{input.text}}"                 # optional; text sent to stdin then closed
  timeoutMs: 60000                        # own limit, default 60000; <=0 is a construction error
  okExitCodes: [0]                        # default [0]
  maxOutputChars: 1000000                 # per stream (stdout / stderr); default 1_000_000
  onOutputLimit: truncate                 # truncate | fail
  encoding: utf-8                         # utf-8 default; e.g. windows-1252, ibm850
  result: text                            # text | json | lines | full  (see 1.4)
  maxRetries: 1                           # StepBase.MaxRetries; CmdStep default is 1 (see 1.6)
```

Notes:
- Properties are separate (`args` is `string[]`, `commandLine` is `string`) to avoid a "string or list" converter; the step validates "exactly one of `args`/`commandLine`" in `ExecuteAsync` (like `ScriptStep` validates `script` in `ScriptStep.cs:~127`), or better in a setter that throws at build time so the YAML factory reports it with line/column [I: ScriptStep's heap setter already throws `ArgumentOutOfRangeException` for this purpose, `ScriptStep.cs:~118-126`].
- `timeoutMs` as own property (as `ScriptStep.TimeoutMs`), distinct from the inherited `timeout` of `StepBase`. Reason: the step wants to kill the tree itself and return a typed error; the middleware timeout is the outer safety net.
- `workingDirectory` is not resolved by the framework: Core has no notion of a workspace folder. The designer host should be able to set a base (see policy hook, 1.9).

### 1.3 Arguments: `ArgumentList`, never a shell by default

- Use `ProcessStartInfo.UseShellExecute = false` and fill `ProcessStartInfo.ArgumentList` item by item. No quoting/escaping to get wrong, and a rendered value that contains spaces, quotes, `&`, `|`, `;` stays one argument. This is the main thing that makes `{{input.x}}` in args safe from shell injection.
- `args` list: render each item with `TemplateExpression.Render` independently. A template that renders to an empty string stays an (empty) argument; it is not dropped silently (surprising otherwise). A value starting with `-` can still be taken as an option by the target program: this is argument injection, not shell injection, and cannot be solved generically; document it.
- `commandLine` form (one line): tokenise the TEMPLATE first (simple rules: whitespace separated, double quotes group, backslash is literal on Windows), then render each token. Never render first and tokenise after, otherwise a value with spaces splits into several arguments. Since the tokeniser lives in the step, document its small grammar.
- Shell is opt-in and explicit: `shell: false` default; `shell: true` runs `cmd.exe /d /c` on Windows or `/bin/sh -c` on Linux with the rendered `commandLine` as one string. In that mode templates are rendered into a string that the shell parses, so injection is possible by construction; either forbid templates in shell mode or require them to go through an explicit `| shellquote` filter [I: whether `TinyTemplateRendered` supports custom filters, `?`]. Suggestion: ship v1 without `shell` at all (YAML can write `executable: cmd`, `args: ["/c", "..."]` by hand, which makes the choice visible in the catalog and in trust UI).
- Windows quirk to document: `executable: npm` / `*.cmd` / `*.bat` are not executables; `CreateProcess` only runs `.exe` directly [general Windows behaviour, not read in repo]. Running a `.cmd` goes through `cmd.exe` and its own quoting rules (the BatBadBut family of problems). Recommend: the step refuses an `executable` ending in `.cmd`/`.bat` unless `allowBatch: true`, with a message saying why.

### 1.4 stdin, stdout, stderr, exit code, result shape

- Redirect all three; read stdout and stderr concurrently (two tasks) while waiting for exit, to avoid the classic pipe-full deadlock. Cap each stream at `maxOutputChars` while still draining the pipe (keep reading, stop appending). `onOutputLimit: truncate` keeps the head and marks `truncated`; `fail` kills the tree and returns an error.
- Decode with `StandardOutputEncoding`/`StandardErrorEncoding` = `encoding` (default UTF-8, no BOM issues). Non-UTF-8 consoles on Windows use OEM code pages (Spanish: 850) [general]; for names other than UTF-8/UTF-16/Latin-1, `Encoding.GetEncoding` needs `CodePagesEncodingProvider` registered [general .NET behaviour; `?` whether anything in the repo already registers it]. Minimal v1: accept `utf-8`, `utf-16`, `iso-8859-1`, `windows-1252`, and register the provider inside the step before resolving the name.
- `stdin`: a rendered template written then closed. For big or binary input a file is better; v1 only text. Cap it (e.g. 1 MB).
- Exit code: `okExitCodes` default `[0]`. Not in the list = error result (1.5). Exit code is always in the result of `result: full` and in the error.
- `result` values:
  - `text` (default): JSON string = stdout (trimmed of one trailing newline).
  - `json`: stdout parsed as JSON (error if it does not parse: kind `InvalidJson`, includes the first 200 chars).
  - `lines`: JSON array of stdout lines.
  - `full`: `{ "exitCode": 0, "stdout": "...", "stderr": "...", "truncated": false, "durationMs": 123 }`.
  Output type `JsonStepResult` like `ScriptStep`/`SuspendStep` [V] `ScriptStep.cs:~92`, `SuspendStep.cs:58`, so the next step reads `{{input...}}`. Declaring `IJsonSchemaStep` (as `SuspendStep` does) lets `outputSchema` validate the JSON [V] `SuspendStep.cs:~82`; optional.
- stderr is not mixed into the result by default (only in errors and in `full`); it is logged at Debug.

### 1.5 Typed errors

`IStepError` has no code (see 1.1), so:

- Define in Core a sealed `CmdStepException : Exception` with `Kind` (`StartFailed`, `NonZeroExit`, `OutputLimit`, `InvalidJson`, `Cancelled`) and `ExitCode`. Always return `ErrorStepResult.FromException(this, new CmdStepException(...))` / `CreateErrorResult(msg, ex)`. Effects:
  - exception present = `RetryMiddleware` does not retry [V] `RetryMiddleware.cs:79-90`. That is what we want for a command (non-idempotent).
  - the designer's `ErrorTypes.Of` returns `Exception:CmdStepException` for it [V] `Samples/WorkflowDesigner.Core/Execution/ErrorTypes.cs` (rule: anything else becomes `Exception:<TypeName>`, per its doc and `Of(Exception)` tail). A small designer addition (`CmdExit<code>`, `CmdStartFailed`) would give finer types; that is a sample change, not framework.
- Timeout: when the step's own `timeoutMs` fires, kill the tree and return a `TimeoutException` (inside `CmdStepException` or alone) so `ErrorTypes` maps it to `Timeout` (it looks for `TimeoutException` in the chain [V] `ErrorTypes.cs:~45`). When the pipeline token is cancelled (outer `TimeoutMiddleware` or user cancel): kill the tree, then either throw `OperationCanceledException` or return the error; both end as `Timeout`/`Cancelled` thanks to `TimeoutMiddleware.cs:54-62`.
- Error message includes: executable name, exit code, and the last ~2 KB of stderr (and stdout if stderr is empty). Never the env values. Full rendered args only at Debug (they may carry secrets rendered from context).

### 1.6 Pipeline integration

- `MaxRetries = 1` in the constructor (like `ScriptStep`, `SuspendStep`). `maxRetries` can still be raised in YAML; document that retries re-run the command.
- Events: `StepStarted/Completed` come from `ObservabilityMiddleware` as for any step; `EnrichStartedEvent` can add `AdditionalData` = executable name + number of args + working directory (no env, no args text) [I: AdditionalData is the documented purpose at `StepBase.cs:147-150`]. Completed: exit code, duration, bytes of stdout/stderr, truncated flag. No per-line streaming event in v1.
- Logging: `Logger.LogInformation("CmdStep {Step} runs {Executable} ({ArgCount} args)")`; Debug adds args.
- The step keeps no state in fields between runs (as `SuspendStep` states in its remarks) so one instance can serve concurrent runs [V] `SuspendStep.cs:43-46` for the model. Each execution has its own `Process`.
- It works inside `GroupStep`/`LoopStep`/`ParallelStep` without special handling [I].

### 1.7 Environment variables

- Child inherits the parent environment by default in .NET. In the designer the LLM keys can live in environment variables (`${VAR}` references in config: `docs/designer/architecture.md:1162` mentions the variable a `${VAR}` names) [V that docs say it; `I` that the process env holds them]. A shell-capable child would see them.
- Proposal: `inheritEnvironment: minimal` default = clear the environment and copy a short allow-list (`PATH`, `SystemRoot`, `ComSpec`, `PATHEXT`, `TEMP`, `TMP`, `HOME`, `USERPROFILE`, `LANG`, `DOTNET_*`), then apply `env`. `all` restores full inheritance. This costs the author one word when a tool needs a variable, and removes the "leaks API keys by default" case. It is a default, not a prohibition.

### 1.8 Killing the process tree (Windows and Linux)

- Baseline (cheap, reuses a proven call): `process.Kill(entireProcessTree: true)` then `WaitForExit(bounded)`, as `ScriptWorkerClient.Kill` [V] `ScriptWorkerClient.cs:301-309`. Limits: it enumerates descendants at kill time [general .NET behaviour]; a grandchild spawned between the snapshot and the kill, or orphaned (parent already dead), can survive. [?] not tested here.
- Windows, robust: a Job Object without `ActiveProcessLimit` and without memory limit, only `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; assign the process right after start. Closing the handle (dispose, or the host crashing) kills the whole tree, including grandchildren. Create the process suspended or assign immediately; there is a short window between `Process.Start` and `AssignProcessToJobObject` where a very fast child could escape [general]. The existing class cannot be used: `internal`, in the Script extension, flags fixed to `LimitActiveProcess | LimitProcessMemory | LimitKillOnJobClose` [V] `WindowsJobObject.cs:69-73`. Options: (a) move it to Core as `internal` with a parameter (`allowChildren`, `memoryLimit?`) and make the Script extension use it through `InternalsVisibleTo`; (b) write a second small class in Core. (a) is a refactor of `AITaskAgent.Extensions.Script` (touches `ScriptWorkerClient.WindowsJobObjectHolder`, `ScriptWorkerClient.cs:350-376`) and its tests; (b) duplicates ~100 lines of P/Invoke. Recommend (b) for v1 (no risk to ScriptStep), and unify later.
- Linux: no job objects. Options, in order of effort: (1) baseline `Kill(entireProcessTree:true)`; (2) start the child in its own process group/session and `kill(-pgid, SIGKILL)` via P/Invoke to `libc`; .NET has no public API to set the process group on `Process.Start` [general], so it needs `setsid` as a wrapper executable (changes argv, requires `/usr/bin/setsid`) or `posix_spawn` through P/Invoke (large). (3) cgroups: out of scope. Recommend v1 = (1) with a documented limitation, plus on timeout first send SIGTERM-equivalent? .NET `Kill` is SIGKILL only; keep it simple.
- Pipes held by grandchildren: after the main process is killed, `ReadToEnd` on its redirected pipes can still block if a surviving grandchild holds the write end [general]. Always read with the cancellation token and give up waiting for the readers after a short bound (e.g. 2 s) once the process is dead.
- Cancellation flow: `using var linked = CreateLinkedTokenSource(ct); linked.CancelAfter(timeoutMs)`; `await process.WaitForExitAsync(linked.Token)`; on cancel kill tree, await readers (bounded), classify: own timeout vs outer cancel by `ct.IsCancellationRequested`.

### 1.9 Visibility in the designer; trust (`PackageTrust`)

What exists today:
- Catalog: `StepCatalog.CategoryOf` puts every step of the Core assembly in `flow` [V] `Samples/WorkflowDesigner.Core/Catalog/StepCatalog.cs:154-161` (`assembly == typeof(GroupStep).Assembly ? StepCategories.Flow`). A CmdStep in Core would appear as "flow" next to `GroupStep`/`LoopStep`, which is misleading. Needed: a `system` category in `StepCategories` [V] `CatalogDto.cs:73-79` and a rule by type (sample change, S). The properties editor needs no change: parameters come from `registry.Describe` (`StepCatalog.cs:86`). Editor hints (`AddEditorHints`, `:166-185`) could mark `args` as a list editor (optional).
- Trust: `PackageTrust.Describe` computes `required` from MCP servers and FileTools folders only; the hash covers `{ mcp, root, directories }` [V] `Samples/WorkflowDesignerWeb/Hosting/PackageTrust.cs:120-121`. Workflow YAML is not part of it. So, as it is today, importing a package with a `CmdStep` would run with NO confirmation [I from those two lines + the fact that `Require` is applied at run/tools endpoints: `RunEndpoints.cs:28`, `SessionEndpoints.cs:76`, `PackageEndpoints.cs:265,276,292`].
- To cover commands, `Describe` would have to read the package's workflow(s) and list every `CmdStep`. A model exists: `WorkflowDocument.AllSteps()` walks containers [V] `Samples/WorkflowDesigner.Core/Documents/WorkflowDocument.cs:23`, and `StepNode.Properties` is a `JsonObject` [V] `:55`.

What it costs to show the commands of an imported package (no prohibition, only costs):
1. Extend `TrustDto` with `Commands: [{ stepId, executable, args[], commandLine?, workingDirectory?, envKeys[], shell }]` and include them in `canonical`/hash (S-M). Env VALUES are not shown or hashed (same rule as MCP headers: "no headers, no environment values", `PackageTrust.cs:13`), only names.
2. Static display only works for literals. Arguments with `{{...}}` are shown as the template text plus a "dynamic" flag; `executable` with a template cannot be shown, hence proposed rule: `executable` is literal only (validated at build time). Without this rule the UI would show a command it cannot know.
3. Library steps/templates (`Library/TemplateEngine.cs`) and workflows reachable from several files: the scan must run over the effective workflow (the one a run will execute), including steps added from the library [I]. If the scan reads a different copy than the run, trust is bypassable; this is the single correctness risk of the feature.
4. Edit friction: any edit of the workflow changes the hash. Today designer edits of config keep trust (`PackageTrust` doc, "Edits made through the designer's own PUT /packages/{id}/config keep a trusted package trusted"). Same rule for workflow edits made via the designer API; edits outside (import, file edited by hand) untrust. Needs the workflow PUT endpoints to re-record the hash when it was already trusted (S-M).
5. Front end: the trust dialog (ensureTrusted) shows MCP and folders [V docs `architecture.md:207,589-594`]; add a "Commands" block. `[?]` front-end files not read.
6. Tests: `TrustTests` style (not read). Needs new cases: untrusted with a CmdStep, hash changes when args change, env value change does not change hash.

Host-level lever worth including in v1 (Core, small): an optional DI service `ICmdStepPolicy` resolved from `context.ServiceProvider`; if present it is called with the resolved command (executable, rendered args, cwd) and may allow, deny, or rewrite cwd. Default (no service) = allow. The designer can then deny `CmdStep` for runs of untrusted packages or confine `workingDirectory` to the package folder, without Core knowing about packages. Cost S.

Also consider registration: because core tags are scanned (1.1), every application gets CmdStep. If you want hosts to opt in, either the scan skips it (no `[StepTag]`, registered by `services.AddCmdStep()`), or the registry has a deny list. Both are framework decisions; flagging only. It does not contradict "CmdStep in Core": it stays in the Core assembly.

### 1.10 Honest objection (short)

The decision (CmdStep = the same as writing C#, for a person who knows the risks) holds when the author is the one who runs. What changes is the case you describe in the designer: the package is written by someone else and opened by a person who did not write it. Then the risk is not "a developer runs their own code", it is "a document runs code". Differences that matter:
- A workflow file looks like data. People open data to read it; they do not review it like a program. Trust today asks for confirmation only when MCP servers or folders appear (`PackageTrust.cs:120`), so it trains the habit of "no prompt = safe".
- Confirmation exists for MCP servers already, which are also "start a process with arguments" [V] `TrustedMcpServer(Name, TransportType, Command, Arguments, WorkingDirectory, Url)` `PackageTrust.cs:12-13`; CmdStep is the same class of capability, so putting it under the same gate is consistent, not a new restriction.
- The LLM angle is covered by your decision (not given to a model). Residual path: a template `{{input.x}}` in `args` fed by LLM output makes an LLM choose arguments of a command. With `ArgumentList` this is not shell injection but argument injection.

Minimum I would propose (not a refusal): (1) `executable` literal; (2) show the command list in the existing trust dialog and include it in the hash; (3) `inheritEnvironment: minimal` default; (4) `ICmdStepPolicy` hook so the designer can deny/confine. Everything else is the author's choice.

### 1.11 Cost and tests

Cost:
- Core: `CmdStep` (+ `CmdStepException`, small tokenizer, process runner with bounded readers and Windows job object): M.
- Windows job object copy in Core: S. Linux process-group kill: M extra, recommend skipping in v1 [I].
- Designer: category `system` + trust listing + hash + workflow-edit re-record + dialog: M.
- Docs: a section in `docs/guides/yaml-pipelines.md`, same format as `SuspendStep`/`ScriptStep` (heading `## \`CmdStep\`: ...`, YAML example, paragraph on behaviour, limits, errors, row in "Common Errors" table; see `yaml-pipelines.md:154-168` and `:188-219`), plus the list of core steps at `yaml-pipelines.md:9`, `docs/ARCHITECTURE-v2.md`, `docs/Funcional.md:2851` and `docs/functional_en.md:2851` which enumerate Core steps, plus `docs/designer/core-api.md:54` categories. S.
- Total D1: M (L if the Linux tree-kill and the designer trust UI both land in the same change).

Tests (harmless commands only; `Tests/` project holds Core step tests, e.g. `Tests/SuspendStepTests.cs`):
- Cross-platform harmless child: the test host itself: `dotnet` is guaranteed present on a dev machine running these tests [I], or better, use a tiny helper console project/the test assembly with an argument-driven `Main` (echo, exit N, sleep, spawn-child-and-sleep, write N MB). That avoids `cmd`/`sh` differences.
- Cases: echo stdout; stderr captured and not in result; exit code 3 with `okExitCodes: [0,3]` ok and without = error with `CmdStepException.ExitCode == 3`; args with spaces, quotes, `&`, `;`, `|` arrive as a single argument (helper echoes `argv`), which proves "no shell"; `{{input.x}}` rendered per item; `commandLine` tokenisation renders after tokenising; timeout kills a sleeping child (assert process gone) and returns `TimeoutException` (`ErrorTypes`-style check can live in Designer tests); cancel token kills tree; grandchild case (helper spawns a child that sleeps, assert both dead after timeout; on Windows with the job object; Linux case marked `[?]`/skipped); output cap truncate and fail; invalid JSON for `result: json`; invalid `encoding` is a construction error; `MaxRetries` default 1 and a failing command runs once (counter file written by the helper); stdin round trip; env minimal hides a canary variable set by the test; YAML round trip through `YamlPipelineFactory` (unknown property error includes valid properties).
- Designer tests: catalog lists `CmdStep` with category `system`; trust required with a CmdStep, hash includes args, excludes env values.
- Nothing dangerous: no `rm`, no network, no shell.

---------------------------------------------------------------------------------------------------

## D2. Remove Access and every Office format from CodebaseAnalysis

### 2.0 Scope reading

"Access y cualquier formato Office" covers two different features, and the second is a user-visible loss, so confirm it:
- (A) Repository ingestion of `.accdb`/`.mdb` (Access schema, VBA, forms) and of `.docx` found in the analysed repository: `IngestOfficeArtifactsStep` from `OfficeTools`.
- (B) User-uploaded documents `.docx`/`.xlsx`/`.pptx` extracted to Markdown for the knowledge/chat feature: `OfficeExtractors`, `DocumentTypeRegistry`, `ContentSniffer`, web uploader.
I treat both as in scope ("cualquier formato Office"). If (B) should stay, only the `OfficeTools` project reference goes away and `OfficeExtractors.Docx` has to be rewritten without `DocxReader` (it uses it, `OfficeExtractors.cs:~25-32`); then `DocumentFormat.OpenXml` stays in Core's csproj.

### 2.1 Packages

- `PdfPig` is used only by `PdfExtractor` [V] `Samples/CodebaseAnalysis.Core/Documents/Extraction/PdfExtractor.cs:2-3` and by the test sample builder `SampleFiles.cs:5-8`. Not Office. Keep.
- `ReverseMarkdown` is used by the HTML extractor [V] `DocumentExtractor.cs:225-229`. Not Office. Keep.
- `DocumentFormat.OpenXml` in Core is used by `OfficeExtractors` [V] `OfficeExtractors.cs:1-2,8`; in tests by `SampleFiles.cs:1-12` and `PostAuditV2CoreTests.cs:22-24,162-202`. Remove from Core csproj only if (B) goes [V] `CodebaseAnalysis.Core.csproj:22`.
- So PDF and Markdown (and txt, code, json/yaml/xml, csv, html, images, svg) keep working: `DocumentExtractor.ExtractCore` switch cases other than Docx/Xlsx/Pptx do not touch OfficeTools [V] `DocumentExtractor.cs:60-74`.

### 2.2 `OfficeTools` project: who references it

`grep` over `*.csproj`, `*.sln`, `*.slnx`, `*.props` [V]:
- `Samples/CodebaseAnalysis.Core/CodebaseAnalysis.Core.csproj:6` (project reference).
- `OfficeTools.Tests/OfficeTools.Tests.csproj:26`.
- `AITaskAgent.sln:22,24` and `AITaskAgent.slnx:11,24`.
No other extension, sample, designer or CLI project references it. `OfficeToolsExtension` / `AddOfficeTools()` are not called by any host outside the OfficeTools folder (grep excluding `OfficeTools*`, `docs` found only `README.md:82`) [V]. Conclusion: deleting the project and its test project breaks nothing else in the build [V by those greps; `?` NuGet/other repos outside this folder].
Contents to be deleted: `OfficeTools/` (Abstractions, Extensions, Extractors, Models, Steps/`IngestOfficeArtifactsStep.cs`, Tools: `AccessQuerySqlTool`, `AccessSchemaTool`, `DocxSearchTool`) and `OfficeTools.Tests/` (8 test files, about 28 `[Fact]/[Theory]`: counts per file 8+1+4+6+3+3+3 [V by grep -c]; `RealAccessAndDocxIntegrationTests` presumably needs Access/OleDb, `?`).
`System.Data.OleDb` and `Microsoft.Data.Sqlite` are package refs of OfficeTools only [V] `OfficeTools.csproj:11-14`; Core has its own Sqlite reference [V] `CodebaseAnalysis.Core.csproj:10`. Side benefit: OleDb/COM Access dependency disappears (the README says Access COM is needed, `CodebaseAnalysisPipeline/README.md:13`).

### 2.3 Inventory by action

**A. Delete projects**
- `OfficeTools/`, `OfficeTools.Tests/`; remove entries from `AITaskAgent.sln` (lines 22, 24 plus their GUID config lines [I]) and `AITaskAgent.slnx` (lines 11, 24).

**B. Remove references / code**
- `Samples/CodebaseAnalysis.Core/CodebaseAnalysis.Core.csproj`: line 6 `ProjectReference OfficeTools`; line 19 comment; line 22 `DocumentFormat.OpenXml` (if B scope).
- `Hosting/AnalysisToolRegistration.cs`: `using OfficeTools.Extensions;` (line 8), `RegisterOfficeTools` call (line 31), doc comment mentioning Office (line 13) [V].
- `Steps/DetectAndIndexGraphStep.cs`: `HasOfficeArtifacts` and its use [V] lines 99, 101-109, 111-121, 590-593; the fail-fast message that names `.accdb, .docx` (108); `IsAccessDatabase` (375-376) and the `access_export` backup/move logic in `BackupPreviousRun` [V] lines 185-186, 215-225, 295 and `PreviousRunBackup.MovedDirectories`/Restore; `OfficeOrigins` and its use in `CheckIfEnrichedAsync` [V] `:597-600,613-ish` (remove the `NOT IN` list entry for office origins; keep `ai_enricher`); the label list at `:582` (`'AccessTable','AccessQuery'`).
  - Decision needed: old DBs/graphs may still hold `office_tools` / `access_vba_extractor` origins or `AccessTable/AccessQuery` labels; with removal `CheckIfEnrichedAsync` would treat those nodes' `cyclomatic` as Roslyn enrichment (H-10 regression) on a repo that had Access before. Keep `OfficeOrigins` filter as a tolerated legacy (one line) or require `ForceReindex`. Recommend keeping the origins filter for old DBs and deleting everything that creates Office data.
- `Documents/Extraction/OfficeExtractors.cs` (delete), `DocumentExtractor.cs:69-71` (3 switch cases), `DocumentTypeRegistry.cs:13-15,69-71` (families and 3 rows; it is documented as a contract exposed by `/api/meta`, `DocumentTypeRegistry.cs` header comment, so the web client changes too), `ContentSniffer.cs:8,19,54-58,103,116-146,172-176` (ZIP/Office checks; keep OLE/legacy detection only to give the clearer rejection message if wanted), `DocumentStore.cs:30,277-278` (`LegacyOffice` message "guárdalo como .docx..." becomes misleading: change text).
- `Steps/SqlFacetExtractorStep.cs`: labels `AccessQuery` in verb mapping and queries [V] `:113,116,588`; language detection of `.accdb/.mdb/.xlsx` and "Microsoft Access / VBA" [V] `:1277-1285,1314-1315,1339-1343,1360-1362`. Decide: VBA source files (`.bas/.cls/.frm`) in a repo are source code, not Office; the "VBA" language entry for those extensions may stay. Only Access database and Office document entries go.
- `Knowledge/Graph/MentionResolver.cs:38` (label list with `AccessTable`, `AccessQuery`), `Tools/GraphNeighborsTool.cs` (provenance "ingesta Office": `OfficeSourcePredicate`, `Office` count/kind, header text, lines 17-18,70,221,313-317,419-423,465-467,488-513) [V]. Removing it changes output text of `graph_neighbors` (tests assert it, see 2.5).
- `Reports/ReportValidator.cs:29-30,1291-1295`: indexing of `.codebase/access_export` for citations. Can go (tests assert it, see 2.5).
- `Support/HardcodedSecretScanner.cs:20`: extension list includes `.accdb,.mdb,.docx,.xls...` as binary files to skip. Keep: they are binaries the scanner must keep skipping even though we do not process them [I].
- `Knowledge/KnowledgePaths.cs:5` comment mention; `Tools/DocumentTools.cs:32` description text ("PDF, Word, Excel, imágenes"); `Steps/SemanticEnrichmentStep.cs:86` comment.
- Web: `CodebaseAnalysisWeb/Api/SourceEndpoints.cs:56` comment; ClientApp: `src/api/types.ts:241-242` (`'xlsx'|'pptx'` families, and docx), `components/DocumentViewer.tsx:31-32,210`, `components/DocumentUploader.tsx:128` text "PDF, Office, ...", `hooks/useUploadQueue.ts:23` (`OFFICE_LEGACY` map), and the tests `DocumentUploader.test.tsx`, `SourceViewer.test.tsx`, `test/fixtures.ts` (3 hits) [V list by grep; content not individually read except the lines above].

**C. Edit text (prompts/templates)** [V by grep]
- `templates/report_apis_prompt.md:7,71-73` (`AccessQuery` in label list, VBA/Access desktop branch), `report_arquitectura_prompt.md:58`, `report_arquitectura_system.md:13`, `report_codesmells_system.md:11-12`, `report_codigomuerto_prompt.md:97`, `report_deuda_system.md:11,15` (hotspot "si el prompt indica que es un repositorio Office"), `report_di_prompt.md:71`, `report_di_system.md:17,23`, `report_errores_prompt.md:67`, `report_sintesis_system.md:12`, `_common_rules.md:19`.
- Judgement: lines that talk about "VBA / Access" as a language/technology of an analysed repo can stay if you still want to analyse VBA source; they only stop having Access-specific graph data. If the goal is "no Access at all", remove those branches. This is a product decision, not mechanical.
- `Samples/CodebaseAnalysis.Core/evaluation/expected-findings.template.json` and `Fixtures/shared-pass-2026-10-16/README.md` matched the loose grep for "access/word"; likely unrelated [?] not read.

**D. Configuration**
- `appsettings.analysis.json` was not found by the targeted regex (no `Office|OpenXml|accdb|OfficeTools` hit in `*.json` under Core) [V], so no config key to remove. `[?]` any `CodebaseAnalysis:...Office...` key under `Samples/CodebaseAnalysisWeb` or the CLI appsettings: the grep covered `*.json` in those folders and found nothing.

**E. Documentation**
- `README.md:26,82`; `docs/getting-started/architecture.md:19,22`; `docs/getting-started/installation.md:28`; `docs/extensibility/custom-extensions.md:3`; `docs/api-reference/extensions.md:49`; `docs/ARCHITECTURE-v2.md:3,21,76` (including the mermaid node `OT["OfficeTools"]`, and the image sources under `docs/assets/images` if they draw OfficeTools, `?`); `docs/Funcional.md:1755`; `docs/functional_en.md` (same line, `?` not grepped in detail); `docs/informe-reestructuracion-v2.md` and `docs/RETOMAR.md` (match "OfficeTools"; history documents: decide whether to edit or leave as record); `docs/guides/codebase-analysis-flujo-y-politica-de-informes.md` lines 17, 51, 71-73, 84, 86, 95-103, 516, 620, 708, 754-755, 778, 914, 1001-1003 (section "1.3 Office, Markdown and knowledge ingestion" is removed or rewritten; many citations are `fichero:línea` and will go stale after edits, so line references in that guide must be re-checked); `Samples/CodebaseAnalysisPipeline/README.md:3,13,78,82` and the `ForceReindex` description; `Samples/CodebaseAnalysisWeb/README.md:11,176,277,312` (documents section lists supported types; line 312 "OfficeTools output"); `Samples/CodebaseAnalysisPipeline/Program.cs:170-172` (console messages "Office registradas"). Estimated 50-70 text edits.

### 2.4 Tests

Disappear:
- All `OfficeTools.Tests` (8 files).
- In `CodebaseAnalysis.Core.Tests`: `EnrichedGraphMarkTests` `A_docx_in_the_repository_does_not_mark_the_graph_as_enriched` (`:63-78`) and the helper line `:299` (`Datos.docx`) [V]; `GraphToolsTests` provenance test of Office edges (`:295-320`) [V]; `DocumentTests.cs:87-104` assertions for docx/xlsx/pptx and `:125-127,147-148,166` (`hoja.docx` mismatch, `bomba.docx` zip bomb) partially; `PostAuditV2CoreTests.cs:~160-202` xlsx cap/cancel tests; `ReportValidatorTests.cs:27-29,76` (`access_export` citation, if that code goes); `EnrichedGraphMarkTests.cs:48` InlineData `access_vba_extractor` (keep if legacy origin filter kept).
Need adjusting:
- `SampleFiles.cs` (remove `Docx()`, `Xlsx()`, `Pptx()` builders and the three entries of `All()` [V] `:205-221`), because `DocumentTests.cs:53,57,323` and `CodebaseAnalysisWeb.Tests/V2DocumentsKnowledgeTests.cs:73-85` count `SampleFiles.All()`; they assert `ids["especificacion.docx"]` text at `V2DocumentsKnowledgeTests.cs:85` which must move to another sample (e.g. the PDF/markdown one).
- `KnowledgeContractsTests.cs:92` (`.doc` unsupported) stays valid.
- `CodebaseAnalysisWeb.Tests/V2DocumentsKnowledgeTests.cs:133` (`viejo.xls` -> unsupported_type) stays valid after the change (all of `.xls/.xlsx` become unsupported); the message text may change.
- Web client tests: `DocumentUploader.test.tsx`, `SourceViewer.test.tsx`, `test/fixtures.ts` [V listed; content `?`].
- `OfflinePipelineTests.cs:18` comment only.
Net: tests that guard the "Office never marks the graph as enriched" invariant (H-10) lose their trigger; the invariant for Java marks remains tested elsewhere (`JavaTypeRoleTests` unrelated).

### 2.5 Risks

- Other extensions using OfficeTools: none found (2.2).
- PDF/Markdown reading is not lost (2.1).
- Real loss: uploaded Word/Excel/PowerPoint documents become `unsupported_type` (a user-visible regression for the chat/knowledge feature) and repositories with `.accdb` lose all analysis of Access schema/VBA/forms/queries. Pipelines run on a repo with only `.accdb`/`.docx` now hit the fail-fast at `DetectAndIndexGraphStep.cs:104-109` ("no source code"); that is intended but the message must change.
- Existing `.codebase` data from earlier runs (`access_export`, `DocSection` from docx, `UserDoc*` of docx uploads already ingested) stays on disk; stored `DocumentRecord`s with family `docx/xlsx/pptx` would be listed but cannot be re-extracted; `DocumentStore` behaviour on unknown family is `?` (not read). Decide migrate/ignore.
- Stale `fichero:línea` references in the long guide (2.3-E).
- Order of edits matters because `DocumentTypeRegistry` is an API contract (`/api/meta`) consumed by the client: change server and client in one commit.

### 2.6 Recommended order and cost

1. Decide scope A only vs A+B (2.0) and what to do with legacy origins (2.3-B). 
2. Remove (A): `AnalysisToolRegistration` line, `DetectAndIndexGraphStep` Office/Access code, `OfficeTools` project reference; delete `OfficeTools` and `OfficeTools.Tests`, edit `.sln`/`.slnx`. Build, run Core tests.
3. Remove (B) if chosen: `OfficeExtractors`, registry/sniffer/store text, csproj OpenXml, `SampleFiles`, tests; then client files and client tests in the same change.
4. Clean labels/provenance text (`GraphNeighborsTool`, `SqlFacetExtractorStep`, `MentionResolver`, `ReportValidator`) and tests that assert it.
5. Templates (prompts) edit. Prompt changes alter report behaviour: out of "mechanical" and needs an eval run [I per your earlier rule that LLM-affecting changes are measured, memory index].
6. Docs last (re-verify citations).
Cost: S for (A) code and project deletion; M for the whole including (B), client, tests, 50-70 doc edits. Recommend splitting in two commits: "remove OfficeTools and Access ingestion", then "remove Office document formats and texts".

---------------------------------------------------------------------------------------------------

## D3. Global state of CodebaseAnalysis that blocks running it in the designer

### 3.1 How the designer already isolates things (so we only fix what is left)

- The designer builds one service collection per run and replaces the framework's `ITools` with a per-collection `AvailableTools` [V] `Samples/WorkflowDesigner.Core/Engine/EngineServices.cs:120` and `EngineTools.cs:59-65`. `YamlLlmStep` reads `ITools` from DI, "nothing static is read" [V] `AITaskAgent.Extensions.Llm/Steps/YamlLlmStep.cs:202-220`.
- Designer limits runs with `MaxConcurrentRuns = 1` by default [V] `RunManager.cs:532-534`, `docs/designer/core-api.md:21`, and documents the FileTools hazard [V] `core-api.md:268`; `FileToolsScope.Apply` replaces the static roots under a lock [V] `EngineTools.cs:313-332`.
So with one run at a time the designer works today; the work below is only needed for concurrent runs or several packages at once, or for CodebaseAnalysis's own steps (C# classes, not `YamlLlmStep`).

### 3.2 Inventory of static / process-shared state touched by the CodebaseAnalysis pipeline

| # | State | Where | Scope today | Per-run problem |
|---|---|---|---|---|
| 1 | `AvailableTools.Instance` (static singleton registry, last registration of a name wins) | framework `AITaskAgent.Extensions.Llm/Tools/Implementation/AvailableTools.cs:10-12` [V]; filled by `AnalysisToolRegistration.Register` (20+ `Register` calls) [V] `Hosting/AnalysisToolRegistration.cs:23-82`; read in the constructor of `TemplateLlmStep` [V] `AITaskAgent.Extensions.Llm/Steps/TemplateLlmStep.cs:39` and by the chat [V] `Chat/ChatAgent.cs:233` | process | Two runs with different repos: tools hold the last run's `DatabasePath`/`RepositoryPath` (graph tools get them in constructors; see row 2) |
| 2 | Tool instances built with run paths | `GraphMapTool(input.DatabasePath)`..., `GraphCodeItemLocator(db, repo)`, `ListDocumentsTool(knowledge)` [V] `AnalysisToolRegistration.cs:23-30,43-44,70-74` | in the static registry | But most graph tools take the DB from the context first: `context.Get<string>(AnalysisContextKeys.CbmDbPath)` [V] `GraphMapTool.cs:48`, `GraphDomainTool.cs:66`, `GraphFlowsTool.cs:51`, `GraphHealthTool.cs:48`, `GraphNeighborsTool.cs:153`; documents tools prefer `KnowledgePaths` from context (`DocumentTools.cs:6-23`). Ctor values are only fallbacks [V for those lines; `?` GraphSearchTool and `GraphCodeItemLocator`, which use ctor values: `GraphCodeItemLocator.cs:18,91-98` [V]] |
| 3 | `FileToolsConfiguration` (root, additional dirs, default encoding, search-excluded dirs) | `FileTools/FileToolsConfiguration.cs` static class [V]; written by `AnalysisToolRegistration.cs:35,38,48`, `AllowedReadRoots.Apply` [V] `Support/AllowedReadRoots.cs:92-110`, web `AnalysisRunManager.cs:493-494`; read by 27 call sites in FileTools (`ValidatePath/ResolvePath/IsAllowed`) and by `GraphSearchTool.cs:286`, `SqlTableScanner.cs:177`, `DynamicInstantiationScanner.cs:114` [V] | process | Root, sandbox and encoding of run B overwrite run A mid-flight. Real security aspect: the sandbox root is the repo the tools may read |
| 4 | `FileShaCache` | `Knowledge/FileShaCache.cs:16` [V] | static `ConcurrentDictionary` keyed by full path, validated by length+mtime | Safe across runs (key is the path, entries self-invalidate). Only memory growth |
| 5 | `EmbeddingStore.Cache` / `Versions` | `Knowledge/Embeddings/EmbeddingStore.cs:31-32,171-193` [V] | static, keyed by DB path | Safe across repos; invalidation per DB path |
| 6 | Write gates: `DocumentStore.WriteGate`, `RecordLocks`, `KnowledgeGraphIngestor.WriteGate`, `GraphSourceRegistry.WriteLock`, `ReportHistoryStore.WriteLock`, `ReportWriter.WriteLock`, `ReadRootsStore.Gate` | e.g. `DocumentStore.cs:28,46`, `KnowledgeGraphIngestor.cs:34`, `ReportHistoryStore.cs:60`, `ReportWriter.cs:75`, `GraphSourceRegistry.cs:15`, `ReadRootsStore.cs:13` [V] | process | Not correctness bugs: they serialise writers of different repositories (contention only) |
| 7 | `SqliteConnection.ClearAllPools()` | `Steps/DetectAndIndexGraphStep.cs:209,310,451` [V] | process-wide ADO.NET pool | A run clearing pools while another uses `Cache=Shared` read connections on its own DB may drop its pooled connections; effect is performance/transient errors `[I]`, not data loss. `OpenReadOnly` uses `Mode=ReadOnly;Cache=Shared` [V] `Tools/CbmGraphDb.cs:~41` |
| 8 | `Directory.GetCurrentDirectory()` as fallback for repo/db/reports | `DotNetEnricherStep.cs:76`, `ExecutiveSynthesizerStep.cs:99`, `LanguageEnricherStep.cs:68`, `RepositoryInventoryStep.cs:152`, `SqlFacetExtractorStep.cs:42,1230`, `CbmGraphDb.cs:34` [V] | process | Only when input paths are empty; with explicit paths there is no issue. `FileToolsConfiguration` default root also uses the cwd |
| 9 | `Console.WriteLine` in `CbmGraphDb.SelectAsync` error path and the default reporter fallback ("Si falta, se usa la consola", `AnalysisContextKeys.cs:~16`) | [V] `CbmGraphDb.cs:~79`; `OfficeTools` writes to the console too (`CodebaseAnalysisWeb/README.md:312`) | process | Output interleaved, not isolated; irrelevant after D2 for OfficeTools |
| 10 | `AnalysisPipelineInput` | `Models/AnalysisPipelineInput.cs:22` [V]; put in context as `AnalysisContextKeys.PipelineInput` by `AnalysisRunner.CreateContext` [V] `Hosting/AnalysisRunner.cs:26-38` | per run (context) | None: it is a per-run object; also a step input (it derives from `StepResult`). Not static |
| 11 | Per-run state already in context | `CbmDbPath`, `ReportsPath`, `Reporter`, `KnowledgePaths`, `ChatTurn`, `GraphProvenance`, etc. [V] `Progress/AnalysisContextKeys.cs:6-60` | per run | Good: this is the pattern to extend |
| 12 | Step instance state | `CustomStatelessTemplateLlmStep` keeps invocation state in a `ConditionalWeakTable<PipelineContext,...>` by context identity [V] `Steps/CustomStatelessTemplateLlmStep.cs:39-52`; but `_systemSha/_promptSha/_shasComputed` fields are per step instance [V] `:54-56` (cached template hashes, same value for any run if templates are the same) | per step | OK if the template provider is the same for all runs |
| 13 | Web workspace singleton | `WorkspaceState.Activate` registers tools and sets FileTools sandbox for the single active repo; analysis run manager registers tools before each run [V] `CodebaseAnalysisWeb/Workspace/WorkspaceState.cs:~171-190`, `Analysis/AnalysisRunManager.cs:493-494` | process | The web app is single-workspace by design (`Activate` "tras comprobar que no corre nada") [V]. Not the designer's problem |
| 14 | `KnowledgePaths` | immutable record of two paths [V] `Knowledge/KnowledgePaths.cs:12-30` | per use | None |
| 15 | DB paths | `input.DatabasePath` per run; default `.codebase/codebase.db` under the repo [I from `KnowledgePaths`/`CbmGraphDb` docs] | per repo | Two runs on the same repo share the same DB file: SQLite locking, not a static-state problem. Two packages analysing the same repo concurrently conflict regardless of code isolation |
| 16 | `TemplateProvider`/templates dir | templates "junto al ejecutable" [V] `AnalysisRunner.cs:42-43` comment | process, read-only | None |

Net: the real shared mutable state is rows 1-3 (+ the pool clearing, row 7). Rows 4-6 are harmless caches/locks. Everything else per run already lives in `PipelineContext`.

### 3.3 Options to isolate per run

**Option A (sample-only, S): stop registering run-specific tools in the global registry; make tools read per-run data from the context.**
- Tools already read `CbmDbPath`/`KnowledgePaths` from the context (rows 2). Change the remaining ones (`GraphSearchTool`, `GraphCodeItemLocator`/`ViewCodeItemTool` locator, `ListDocumentsTool` fallbacks) to take db/repo from context, and register tools once at startup without paths. `GraphCodeItemLocator` is constructed by FileTools' `ViewCodeItemTool` with an `ICodeItemLocator` that has no context parameter [V] `ViewCodeItemTool.cs:32-35`; look at whether `ICodeItemLocator` receives context `[?]` (interface not read). If not, it needs a framework change or an `AsyncLocal` ambient (Option C).
- Cost S-M, risk low, sample only. Fixes row 2; not rows 1 and 3.

**Option B (sample, M): per-run `ITools`, as the designer does, plus a CodebaseAnalysis step base that gets tools from DI.**
- `CustomStatelessTemplateLlmStep : StatelessTemplateLlmStep : TemplateLlmStep` resolves tools in the constructor from `AvailableTools.Instance` [V] `TemplateLlmStep.cs:39` (framework). Override/replace in the sample: `CustomStatelessTemplateLlmStep` could resolve its `Tools` from `context.ServiceProvider.GetService<ITools>()` at execution time instead of the constructor `[I: needs the base class to expose Tools as settable; not read]`. Then `AnalysisPipelineFactory` would build steps without needing the static registry, and each run's service collection carries its own `AvailableTools` (like `EngineToolSet.Register`).
- Cost M, risk medium (touches how steps get tools; test with the existing offline pipeline tests). If the base class does not allow it, it becomes a framework change (see 3.4).

**Option C (framework, M-L): make FileTools per-run via the context.**
- `BaseFileTool.ValidatePath`/`ResolvePath` are `protected static` reading `FileToolsConfiguration` [V] `FileTools/Tools/BaseFileTool.cs:276-320`; 27 call sites in FileTools and 3 in the sample. Replace the static with a resolution chain: (1) a `FileToolsScope` object found in the `PipelineContext` (metadata key or a DI service in `context.ServiceProvider`), (2) fall back to the static (so nothing breaks). `ITool.ExecuteAsync` already receives `PipelineContext` [V] `AITaskAgent.Extensions.Llm/Tools/Abstractions/ITool.cs`, as `ReadRootsAwareTool.ExecuteAsync` shows [V] `Tools/ReadRootsAwareTool.cs:38`. Static helpers would become instance/overload methods taking the context.
- Same for `DefaultEncoding` and `SearchExcludedDirectories`.
- Cost M-L (10 tool classes + helpers + 27 sites + `FileTools.Tests`), risk medium: security-sensitive code (sandbox root check). Compatible: default behaviour unchanged when no scope in context.
- Alternative lighter variant (S-M, framework): an `AsyncLocal<FileToolsScopeData?>` inside `FileToolsConfiguration` with `using (FileToolsConfiguration.BeginScope(root, additional, encoding, excluded))`; every getter returns the ambient value when set, else the static. Pros: tools untouched (they call the same static getters); each run wraps its execution in a scope. Cons: `AsyncLocal` flows to child tasks (good for `Parallel.ForEachAsync` in the auditors) but not to code running on threads started outside the flow (e.g. background tasks created before the scope); the pipeline `ParallelStep` uses Tasks so it flows `[I]`. Needs a decision on the one place that opens the scope (the host, e.g. `RunManager` around `ExecuteAsync`, like `FileToolsScope.Apply` today at `RunManager.cs:579`). Recommended over the context-passing variant for cost: S-M, change confined to `FileToolsConfiguration` (+ tests) and one line in the designer.

**Option D (framework, M): remove `AvailableTools.Instance` from `TemplateLlmStep`.**
- `TemplateLlmStep` takes `IList<string>? tools` and resolves names statically in its constructor [V] `TemplateLlmStep.cs:39`. Change to keep names and resolve through `context.ServiceProvider.GetService<ITools>()` at execution, falling back to `AvailableTools.Instance` when no `ITools` is registered. `YamlLlmStep` already does the DI approach [V] `YamlLlmStep.cs:202-220`, so the pattern exists in the same assembly. `LlmServiceCollectionExtensions` keeps `services.AddSingleton<ITools>(AvailableTools.Instance)` [V] `:68` for compatibility. Cost M (touches `TemplateLlmStep`, `StatelessTemplateLlmStep`, step tests), risk low-medium (timing: tools resolved at execution instead of construction changes the "registered after construction gets lost" failure mode that `AnalysisToolRegistration` documents, `AnalysisToolRegistration.cs:11-14`).

**Option E (sample, S): serialise instead of isolate.** Keep one analysis at a time through a gate in the host (as the web does) and keep `MaxConcurrentRuns = 1` in the designer. Cost S, risk none, zero concurrency. This is the current de-facto behaviour; it is what the documented limitation says [V] `core-api.md:268`.

Row 7 (`ClearAllPools`): replace by `SqliteConnection.ClearPool(connection)` per DB path (needs a connection object) or close the connections the step itself opened; sample-only, S. Only matters with concurrent runs.

### 3.4 What is framework (needs approval) and what is sample

Framework (approval required):
- `FileTools/FileToolsConfiguration.cs` scope (Option C-lighter) or `BaseFileTool` context resolution (Option C).
- `AITaskAgent.Extensions.Llm/Steps/TemplateLlmStep.cs` tool resolution (Option D), and possibly `StatelessTemplateLlmStep`.
- Any change to `AvailableTools.Instance` (e.g. mark it `[Obsolete]`/documented as process-wide default): the class is public API.
Sample (CodebaseAnalysis and designer):
- `AnalysisToolRegistration` (stop passing paths into tool constructors, split "register once" from "per-run data in context"), `GraphSearchTool`/`GraphCodeItemLocator`, `ListDocumentsTool` fallbacks, `ClearAllPools`, `Console` fallbacks.
- `CustomStatelessTemplateLlmStep` resolving tools from the context (Option B, if the base class allows overriding).
- Designer: open the FileTools scope around the run; `FileToolsScope` in `EngineTools.cs` becomes obsolete if C-lighter lands.

### 3.5 Suggested cut and cost

- Minimal, no framework: Option E + Option A (+ pool fix). Cost S-M, risk low. Gives "several packages" safely only if runs are serialised; two repos at once still collide on the FileTools root (row 3).
- Real concurrency: add `AsyncLocal` scope in `FileToolsConfiguration` (S-M, framework) and Option D or B for tools (M). Total M, medium risk.
- Same repository from two runs at once remains unsolved by any of this (shared SQLite DB and `.codebase` folder); needs a per-run DB path decision (product).

---------------------------------------------------------------------------------------------------

## Orden recomendado y dependencias entre D1, D2 y D3

1. **D2 first.** Independent, mostly deletion, shrinks the sample, removes `OfficeTools` (which was a user of `AvailableTools.Instance` and the console) and a pile of conditional code in `DetectAndIndexGraphStep`; D3 gets simpler (one fewer `Register` family, no OfficeTools console output). No dependency on D1 or D3. Do (A) then (B) as in 2.6.
2. **D3 minimal (Option E + A + pool fix) next**, after D2 so the registration file is touched once. The framework parts of D3 (AsyncLocal scope, TemplateLlmStep) need your approval and are only worth doing if concurrent runs are a real goal; they do not block D1.
3. **D1 last.** Independent of D2/D3 in code. It touches the designer catalog/trust (shared with any D3 designer line), so doing D3's designer line (scope around the run) first avoids two edits in `RunManager`/`EngineTools`. Dependency inside D1: the trust listing must land together with CmdStep availability in the designer (or the designer must deny CmdStep until it does, via the policy hook), otherwise there is a window where imported packages run commands with no prompt (1.9).
Dependencies: D1 policy hook and D3 FileTools scope both want a "run scope" object in the host; if both are planned, define it once (a per-run service in the engine collection read via `context.ServiceProvider`).
Overall: D2 = S/M, D3 = S-M (minimal) or M (concurrent), D1 = M.

## Lo que no pude comprobar

- Nothing was built or run; no test executed.
- Linux behaviour of `Process.Kill(entireProcessTree: true)` and of process groups; Windows job-object assignment race; behaviour of orphaned grandchildren holding pipes (all from general knowledge, `[?]` for this repo/runtime). `docs/ARCHITECTURE-v2.md:96` itself says the Linux memory watchdog of ScriptStep "has not been run" [V].
- Whether `TinyTemplateRendered` supports custom filters (for a `shellquote`).
- Whether `ICodeItemLocator` gets a `PipelineContext` (affects Option A for `GraphCodeItemLocator`).
- Whether `TemplateLlmStep.Tools` can be overridden/set after construction (affects Option B).
- Front-end files of the designer (trust dialog) and the designer's `TrustTests`; `docs/assets/images` drawings that mention OfficeTools.
- How `DocumentStore` handles already-stored records with family `docx/xlsx/pptx` after D2-B.
- How CodebaseAnalysis is meant to run "inside the designer" (a YAML package using custom C# steps, or a port to YAML steps): I assumed C# steps registered through an extension; if it becomes pure YAML + `YamlLlmStep`, rows 1-2 of the D3 table mostly disappear because `YamlLlmStep` already reads `ITools` from DI.
- The exact line numbers of a few items marked with `~` (approximate: `StepBase.Logger`, `ScriptStep` setters, `TimeoutMiddleware` return line, `CbmGraphDb` console write, `ErrorTypes` timeout check); the claims themselves were read.
- Loose greps for "access/word" matched many unrelated files (`expected-findings.template.json`, fixtures README); they were not read and are not counted as Office references.


---



---

# Study E: delegate properties for step hooks (F1 / F2 without subclasses)

Read-only study, 2026-10-19. `[V]` = I read the line; `[I]` = inferred; `[?]` = not known. Recommendations are not marked.
Paths are relative to `C:\GeminiCli\AITask\AITaskAgent` unless absolute.

## 0. Headline

- The idea is sound and cheap **for LLM steps**; almost no value for Core steps. Of ~25 virtual hooks only 7 are worth a delegate
  (section 5). The rest are either never overridden, framework-internal, or contracts that other code already implements through
  interfaces.
- `[V]` The framework already has two of the three ingredients: (a) a delegate-carrying step (`DelegatedStep.cs:12-26`) and (b) a
  "name in YAML, resolved against DI at build time" pattern (`YamlLlmStep.ToolNames` resolved by `ResolveTools` in the
  `[OnDeserialized]` callback, `YamlLlmStep.cs:113,240-245`). A named hook is exactly (b) with a different registry.
- The one hard problem is not the delegate property: it is that **F1 cannot be a plain hook around a virtual method**. The
  "final answer after the tool limit" is a *retry strategy* that crosses `ExecuteAsync`, `SwitchToNextFallback`, `RetryMiddleware`
  and `FinalizeAsync` (section 4.1). The hook must be a *decision* (`onToolLimit`) consumed by new code in `BaseLlmStep`, not a
  wrapper of `BuildUserMessageAsync`.
- Recommendation in one line: add a small `LlmStepHooks` object (delegates, `[JsonIgnore]`) plus a `hooks:` name map on `YamlLlmStep`
  resolved through an `IHookRegistry`; implement `onToolLimit`, `onConversationCreated` and `afterResult` first; leave
  `SwitchToNextFallback`, `FinalizeAsync`, `SaveState/RestoreState`, `Validate` and the Enrich hooks as they are.

## 1. Inventory of existing hooks

Columns: signature, who overrides it today, state it needs, delegate candidate (Y / maybe / N).
"Framework" = projects under the repo root; "Samples" = `Samples/`.

### 1.1 `StepBase` (`AITaskAgent.Core/Steps/StepBase.cs`)

| Hook | Signature | Overridden by | Needs | Candidate |
|---|---|---|---|---|
| `ExecuteAsync` (abstract) | `Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext, int attempt, IStepResult? last, CancellationToken)` `:127` | every step; `TolerantReportsParallelStep`, `StatelessRewriterStep` (`Samples/AdvancedLlmPipeline/StoryPipelineFactory.cs:200-215`), `CustomStatelessTemplateLlmStep.cs:82` | all | N here: this is what `DelegatedStep` already is |
| `EnrichActivityBefore/After`, `EnrichStartedEvent/CompletedEvent` | `protected internal virtual void ...(Activity?, ...)` `:133,142`; `protected virtual ...` `:151,160` | no override found in the framework `[V]` grep of `override .*Enrich` over `*.cs` (only `WriteFileTagHandler.EnrichCompletedEvent`, a different class, `FileTools/Streaming/WriteFileTagHandler.cs:110`) | activity, event | N (already delegated to `IEnrichable Enrichable`, `:92`, which is `[JsonIgnore]`d only in `YamlLlmStep`) |
| `FinalizeAsync` | `protected virtual Task FinalizeAsync(IStepResult, PipelineContext, CancellationToken)` `:170` | `BaseLlmStep.cs:232`, `IntentionAnalyzerStep.cs:75`, `CustomStatelessTemplateLlmStep.cs:267`, `ChatAgent.AuditedChatStep` (`Samples/CodebaseAnalysis.Core/Chat/ChatAgent.cs:344`) | result, context | maybe (as `afterResult`, see 5) |
| `Validate` | `protected virtual Task<(bool, string?)> Validate(IStepResult, PipelineContext, CancellationToken)` `:180`; iterates `_validators` | no override `[V]` | result | N: `IStepValidator` + `AddValidator` (`:86`) already is the extension point |
| `SaveStateAsync` / `RestoreStateAsync` | `:226` / `:232`; no `PipelineContext` argument (`IResume.cs`) | `StatelessTemplateLlmStep.cs:41,51` | step instance fields | N (design flaw already documented in `StatelessTemplateLlmStep.cs` remarks) |
| `SwitchToNextFallback` | `protected virtual bool (IStepResult error, PipelineContext, int attempt)` `:234`; exposed via `IFallbackable` and called by `RetryMiddleware` | `BaseLlmStep.cs:175`, `CustomStatelessTemplateLlmStep.cs:214` | per-invocation state | N as a delegate (section 3.6) |
| `Schema` | `protected virtual JsonSchema?` `:100` | `SuspendStep.cs:108`, `YamlLlmStep.cs:171` | schema | N (data, not behaviour) |
| `Timeout`, `RequiredContext`, `Resume`, `Enrichable` | virtual properties `:73,76,32,92` | `BaseLlmStep.cs:118` (`RequiredContext`) | - | N |

`[V]` `Enrichable` and `Timeout` are public settable and **not** `[JsonIgnore]` in `StepBase`, so `TypeSchemaBuilder` describes them for
every step (interface type -> untyped, `TypeSchemaBuilder.cs` FillType, "Interfaces ... stay untyped"). Only `YamlLlmStep` hides
`Enrichable` (`YamlLlmStep.cs:146-152`). A `Func<...>` property added to a step would therefore appear in the catalog as an untyped
YAML key that fails on `Populate`. Delegate properties **must** be `[JsonIgnore]`.

### 1.2 `BaseLlmStep` (`AITaskAgent.Extensions.Llm/Steps/BaseLlmStep.cs`, `BaseLlmStep.LlmInvocation.cs`)

| Hook | Signature | When it runs | Overridden by | State it needs | Candidate |
|---|---|---|---|---|---|
| `BuildUserMessageAsync` | `protected virtual Task<string?> (IStepResult input, PipelineContext)` `:227` | attempt 1 only (`:291,307`) | `TemplateLlmStep.cs:63`, `YamlLlmStep.cs:249`, `CustomStatelessTemplateLlmStep.cs:250`, `SharedPass/BatchReader.cs:44`, `SharedPass/FindingJudge.cs:40` | input, context | Y |
| `BuildSystemMessageAsync` | `... Task<string?> (input, context)` `:230` | inside `BuildLlmRequestAsync` `:419` | `TemplateLlmStep.cs:70`, `YamlLlmStep.cs:255` | same | Y (same shape as above) |
| `GetConversationContext` | `protected virtual ConversationContext (PipelineContext)` `:402` | attempt 1 only `:296` | `StatelessTemplateLlmStep.cs:63`, `YamlLlmStep.cs:261`, `CustomStatelessTemplateLlmStep.cs:228` | context; per-invocation state in the sample | maybe (replace = dangerous; the useful part is the *notification*, see `onConversationCreated`) |
| `AddConversationToContext` | `protected virtual void (PipelineContext, ConversationContext, ILlmStepResult)` `:404`, empty body | `FinalizeAsync` `:272-273` | **no override anywhere** `[V]` grep | conversation, result | maybe: zero users, so zero evidence of need |
| `BuildLlmRequestAsync` | `protected virtual Task<LlmRequest> (input, context, ConversationContext, CancellationToken)` `:411` | attempt 1 only `:310` | **none** `[V]` | everything | N: too large, nobody overrides it; `ConfigureLlmRequest` covers it |
| `ConfigureLlmRequest` | `protected virtual LlmRequest (LlmRequest, PipelineContext)` `:482` | inside `BuildLlmRequestAsync` `:472` | `CustomStatelessTemplateLlmStep.cs:255` only | request (record, `with`-copy) | Y |
| `ParseLlmResponseAsync` | `protected virtual Task<(IStepResult? Result, string? Error)> (LlmResult, PipelineContext)` `:499` | every attempt `:376` | `YamlLlmStep.cs:270` | response, context | Y (as `parseResult`) |
| `SwitchToNextFallback` | `:175` (override of `StepBase`) | after a failed attempt, called by `RetryMiddleware` | `CustomStatelessTemplateLlmStep.cs:214` | `InvocationState` (`FallbackIndex`, `Conversation`, `InitialBookmark`, `:128-147`) | N as delegate; the F1 logic moves *into* it |
| `FinalizeAsync` | `:232` | after the last attempt (success or failure) | see 1.1 | state, conversation | maybe |
| `SystemTemplateProvider` (in `TemplateLlmStep`) | `protected virtual ITemplateProvider?` `:85` | prompt rendering | `IntentionAnalyzerStep.cs:72` | - | N (data) |
| virtual properties `Tools`, `TagHandlers`, `Prompt` | `:108,115,173` | - | `YamlLlmStep` (re-declared only to add `[JsonIgnore]`, `:122-152`) | - | N |

Not virtual but effectively hooks: `EnrichSystemPromptWithTags` (`:677`, private static), `ExecuteToolCallsAsync` (`LlmInvocation.cs:393`,
private), `IsStuckRepeatingToolCalls` (`:265`, private), `StopWithMessage` (`:345`, private static). These are policy, not extension points.

### 1.3 Derived LLM steps

- `TemplateLlmStep`: overrides the two message builders to resolve templates (`TemplateLlmStep.cs:63-75`); adds `SystemTemplateProvider`.
- `StatelessTemplateLlmStep`: `GetConversationContext` (fresh conversation, window = role `MaxTokens`) and `Save/RestoreState` through
  instance fields (`StatelessTemplateLlmStep.cs:41-77`). `[V]` the instance fields `_restoredConversation/_currentConversation` are shared by
  concurrent runs, as its own remarks admit.
- `YamlLlmStep`: re-implements `BuildUserMessageAsync`, `BuildSystemMessageAsync`, `GetConversationContext`, `ParseLlmResponseAsync` (`:249-278`).
- `IntentionAnalyzerStep`: `FinalizeAsync` + `SystemTemplateProvider` (`:72,75`). `RouterStep`: `SelectTargetStep`, `GetRoutingReason` (`RouterStep.cs:43,48`).

### 1.4 Core flow steps

| Step | Extension points | Verdict |
|---|---|---|
| `DelegatedStep<TIn,TOut>` | ctor `Func<DelegatedStep<TIn,TOut>, TIn, PipelineContext, int, TOut?, Task<TOut>>` `DelegatedStep.cs:12-14`; catches exceptions into `CreateErrorResult` `:28-31` | the precedent; not YAML-creatable (generic, ctor-only) |
| `ActionStep<TIn>` | ctor `Func<TIn, PipelineContext, int, ActionResult?, Task>` `ActionStep.cs:10-12`; swallows exceptions into an error result `:42-47` | precedent |
| `SwitchStep<TIn>` | ctor `Func<TIn, PipelineContext, (IStep, IStepResult)> selector` `:15-17`, plus virtual `SelectTargetStep` `:30` and `GetRoutingReason` `:90` | it already offers both forms (delegate **and** virtual); `selector` is unused when the protected ctor is used `:21-24` |
| `LoopStep`, `GroupStep`, `ParallelStep`, `SuspendStep`, `BreakStep`, `DeclarativeSwitchStep` | none (only `ExecuteAsync`) `[V]` grep `virtual` over `Core/Steps` returns only `StepBase` and `SwitchStep` | nothing to convert; `TolerantReportsParallelStep` overrides `ExecuteAsync` and clears the error afterwards (F4 already decided as a property) |

### 1.5 Interfaces that already are hooks

- `IStepValidator` (`AITaskAgent.Abstractions/Abstractions/IStepValidator.cs`): `Id` + `ValidateAsync(result, context, ct)`; the doc comment says the
  `Id` is "used to reference it from YAML configuration" `[V]`, **but** `StepBase.AddValidator` has no caller and `YamlPipelineFactory` does not attach
  validators (`StepBase.cs:81-85` says so itself). So the by-name reference was designed and never wired. This is the closest precedent
  to `hooks:` and a ready-made consumer of an `IHookRegistry`.
- `ITagHandler` (`AITaskAgent.Extensions.Llm/Streaming/ITagHandler.cs`): `TagName`, `GetInstructions()`, `OnCompleteTagAsync(attributes, content, PipelineContext, ct)`.
  Instances are held in `BaseLlmStep.TagHandlers` (`:115`, `init`) and `[JsonIgnore]`d in `YamlLlmStep.cs:138-144`; the scanner is created per response
  (`LlmInvocation.cs:566,643`).
- `IFallbackable`, `IResume`, `IEnrichable`, `IPipelineMiddleware` (`IPipelineMiddleware.cs`: `InvokeAsync(step, input, context, next, ct)`): interface-level extension points
  used by the runtime, not by user code.

### 1.6 What the samples actually override (the evidence of need)

`[V]` grep of `override` over the repo: samples override only `BuildUserMessageAsync` (3 steps), `GetConversationContext`, `SwitchToNextFallback`,
`ConfigureLlmRequest`, `FinalizeAsync` (3 steps), `ExecuteAsync`. **Nobody overrides `BuildLlmRequestAsync`, `AddConversationToContext`,
`BuildSystemMessageAsync` (outside the framework), `ParseLlmResponseAsync` (outside `YamlLlmStep`) or any Enrich hook.** Converting them would add
surface without a user.

## 2. Precedents to imitate

| Precedent | Where | What to copy |
|---|---|---|
| Delegate with explicit `this` + context + attempt + last result | `DelegatedStep.cs:12-14` | first parameter is the step, so the delegate reaches `Name`, `Logger`-like members without capturing |
| Exceptions turned into an error result carrying `OriginalException` | `DelegatedStep.cs:28-31`, `ActionStep.cs:42-47` | `OriginalException != null` means `RetryMiddleware` does **not** retry (`RetryMiddleware.cs` CASE 1) |
| Names in YAML resolved against DI when the config is populated; unknown name = build error listing the available ones | `YamlLlmStep.ResolveTools` `:113,240-245` and its error text | same for hooks: fail while the pipeline is built, never at run time |
| `[JsonIgnore]` on live-object properties | `YamlLlmStep.cs:122-152` | all delegate properties |
| Inherited-property hiding by re-declaration | `YamlLlmStep.cs:122-152` | needed if a delegate property is added to `StepBase` rather than to a hooks object (avoid) |
| Validators by `Id` | `IStepValidator.Id` + `AddValidator` | unwired; an `IHookRegistry` could finally wire it (`validators: [id]`) |
| Both a delegate and a virtual for the same decision | `SwitchStep` `:15-30` | the coexistence rule in 3.3 |
| Per-invocation state in `LlmPipelineContext.StepState`, keyed by `CurrentStepPath` | `BaseLlmStep.cs:128-169` (internal to the context) | where hook state must live; the sample's `ConditionalWeakTable` exists only because `StepState` is internal (`CustomStatelessTemplateLlmStep.cs:47-50`, `LlmPipelineContext.cs:57`) |
| Extension configuration schema with a per-extension section | `IExtension.ConfigurationModel()` (`IExtension.cs`) | not for hooks; hooks are per-step bindings |

## 3. Proposed design

### 3.1 Shape: one hooks object, not N properties

```csharp
public sealed class LlmStepHooks
{
    public HookAround<UserMessageArgs, string?>?  BuildUserMessage   { get; set; }
    public HookAround<SystemMessageArgs, string?>? BuildSystemMessage { get; set; }
    public Func<RequestArgs, LlmRequest>?          ConfigureRequest  { get; set; }   // transform
    public Func<ParseArgs, Task<(IStepResult?, string?)>>? ParseResponse { get; set; } // replaces, may call base via args.Base()
    public Func<ConversationCreatedArgs, Task>?    OnConversationCreated { get; set; } // notify
    public Func<ToolLimitContext, Task<ToolLimitDecision>>? OnToolLimit  { get; set; } // decision
    public Func<ResultArgs, Task<IStepResult>>?    AfterResult       { get; set; }   // post-process
}
// BaseLlmStep:  [JsonIgnore] public LlmStepHooks? Hooks { get; set; }
```

Why one object: (1) a single `[JsonIgnore]` property on `BaseLlmStep` and a single re-declaration in `YamlLlmStep` instead of 7 per class;
(2) `TypeSchemaBuilder` never sees it; (3) the binding map (`hooks:` in YAML) maps 1:1 to its members, so the catalog can list the hook names
from the type without reflection over the step; (4) future hooks are additive.

`HookAround<TArgs,T>` = `Func<TArgs, Func<Task<T>>, Task<T>>`: the delegate gets the arguments and `next`, the rest of the chain (the virtual
method with every subclass override). Not calling `next` = replace; calling it before/after = wrap. One delegate type covers
replace, before, after and wrap, which is why I do not propose separate "before" and "after" properties.

### 3.2 Arguments (records, immutable, so adding a field never breaks a bound method)

Common: every args record carries `BaseLlmStep Step`, `PipelineContext Context`, `int Attempt` (where meaningful), `CancellationToken CancellationToken`.

- `UserMessageArgs(Step, IStepResult Input, Context, CancellationToken)`; `SystemMessageArgs` same.
- `RequestArgs(Step, LlmRequest Request, Context, bool IsFinalAnswerAttempt)`; returns the request (records are `with`-copied, as the sample does).
- `ParseArgs(Step, LlmResult Response, Context, Func<Task<(IStepResult?, string?)>> Base)`.
- `ConversationCreatedArgs(Step, ConversationContext Conversation, Context, bool IsFinalAnswerAttempt)`.
- `ResultArgs(Step, IStepResult Result, Context, int Attempt, TimeSpan Elapsed, CancellationToken)` -> returns the (possibly replaced) result.
- `ToolLimitContext(Step, Context, ToolLimitReason Reason, string? StopMessage, ConversationContext Conversation, int Iterations, CancellationToken)`;
  `Reason` = `IterationLimit | LoopCut` (matching `ToolIterationLimitExceededException` and the `ToolLoopStopMessage` cut, `LlmInvocation.cs:34,142,197`).
- `ToolLimitDecision`: `Fail` (default, today's behaviour), `FinalAnswer(string prompt)` (extra attempt, same conversation, tools forbidden),
  `Accept` (keep the cut turn as the answer, as `ToolLoopStopMessage` does today when no final prompt is configured).

`[I]` The per-invocation state (`InvocationState`, `BaseLlmStep.cs:128`) stays private. Hooks that need memory across calls use
`PipelineContext.Metadata` (the same rule the step docs give to step authors, `LlmPipelineContext.cs` remarks) or a context-keyed table, exactly
as `NoteSessions` does (`NoteSession.cs:233-266`).

### 3.3 Order with an inherited override

Dispatch from the **call site**, not from the virtual's default body:

```
ExecuteAsync -> state.Message = await RunHook(Hooks?.BuildUserMessage, args, next: () => BuildUserMessageAsync(input, context))
```

so the chain is `delegate -> subclass override -> base default`. Rationale: if the delegate lived in the base default body, a subclass that does
not call `base.X` (e.g. `YamlLlmStep.cs:249`, which does not) would silently disable it, and `TemplateLlmStep.cs:63-68` would run *before* the
delegate sees the raw prompt. With the call-site dispatch the delegate can post-process the rendered text (`await next()`) or replace it.
Without a delegate (`Hooks == null`) the code path is byte-for-byte today's, so existing subclasses and tests do not change behaviour `[I]`.
Rule to document: virtual = extension by *type*, delegate = extension by *instance*; both coexist, delegate outermost.

### 3.4 Exceptions

- Build hooks (user/system message, request): exception propagates out of `ExecuteAsync` as today; `RetryMiddleware` catches it, treats it as fatal,
  asks `SwitchToNextFallback` (false for a non-provider error, `BaseLlmStep.cs:180`) and finalizes (`RetryMiddleware.cs` exception block). I would wrap it in a
  `StepHookException(hookName, step, inner)` so the failure names the hook; `[I]` this is the only behavioural addition and is on a new path.
- `ParseResponse`: same convention as the base (`BaseLlmStep.cs:547-554`): an exception becomes the `Error` string, which is recoverable and retried with the
  message as model feedback. Document it, because it differs from the other hooks.
- `OnConversationCreated`, `AfterResult`: exceptions are fatal for the step (return an error result with `OriginalException`, like `DelegatedStep.cs:28-31`), so a
  broken note-session setup does not run an LLM call without the protocol.
- `OnToolLimit`: exception = `Fail` decision plus a warning log; falling back to "fail the step as before" is the safe direction.
- Cancellation: `OperationCanceledException` propagates unchanged (as `LlmInvocation.cs:64`).

### 3.5 Retries and attempts

`[V]` Per-attempt facts that constrain where each hook may fire: `GetConversationContext`, `BuildUserMessage` and `BuildLlmRequest` run only when
`attempt == 1` (`BaseLlmStep.cs:291-310`); retries (`attempt > 1`) only append the error feedback `:326-343`; `ParseLlmResponse` runs every attempt `:376`.
A fallback switch resets `attempt` to 1 in `RetryMiddleware`, so these three run **again** after a provider switch or a final-answer switch. Hooks receive
`Attempt` and, for the first three, an `IsFinalAnswerAttempt`/`IsFallbackRound` flag so a hook that must be idempotent can tell. `OnConversationCreated`
must therefore be idempotent (as `NoteSessions.Begin` is: it reuses a session on the same conversation, `NoteSession.cs:243-246`).

### 3.6 `SwitchToNextFallback` stays a virtual; the delegate is not a replacement

`SwitchToNextFallback` mutates `InvocationState.FallbackIndex` and rewinds the bookmark (`BaseLlmStep.cs:211-222`). A user delegate that returns `true`
without that bookkeeping would make `RetryMiddleware` restart at `attempt = 1` against the same provider and the same conversation, i.e. an unbounded
replay of the tool loop (the failure `LlmFallbackClassificationTests` guards: `Tests/LlmFallbackClassificationTests.cs:76-94`). So:

- No `SwitchToNextFallback` delegate. Its two rules are made part of the base and driven by `OnToolLimit` (4.1).
- Rules that must remain true after the move, each with an existing test to keep green: (R1) a tool-limit / loop-cut is **not** a provider failure and
  never switches provider (`BaseLlmStep.cs:180`; `LlmFallbackClassificationTests:76-94`); (R2) once in final-answer mode the step never switches provider
  (`CustomStatelessTemplateLlmStep.cs:223-225`); (R3) the final-answer attempt happens at most once per invocation (`!state.FinalAnswerMode`,
  `:210`); (R4) the final-answer switch does **not** restore the bookmark (`:218`), so the conversation keeps the tool evidence; (R5) the state is
  per invocation, not per step instance (`CustomStatelessTemplateLlmStepTests.cs:113`, the parallel-run test).

### 3.7 Thread-safety

`[V]` The step object is registered once and shared by concurrent runs and parallel branches (`BaseLlmStep.cs:120-126`). Consequences: `Hooks` is set at
build time and read-only afterwards (document; do not make delegates re-assignable during a run); delegates must be re-entrant; any state goes to
`PipelineContext`/context-keyed storage, never to captured fields. The args carry the `PipelineContext` so this is possible. The generated static class of
the package `[I]` is stateless by construction if its methods only use their arguments.

### 3.8 Binding from C# and from YAML

C#: `step.Hooks = new LlmStepHooks { OnToolLimit = ctx => ... }` (assignable; `init` would also work but blocks `Hooks.X = ...` after construction; use
`set` on the members, as `Tools` does).

YAML v2, on `YamlLlmStep` (and, by inheritance, any step that exposes the map):

```yaml
- id: Auditor_Errores
  type: LlmStep
  userPrompt: ...
  toolLimitFinalAnswerPrompt: "..."          # sugar for the default OnToolLimit
  hooks:
    onToolLimit: Auditor.FinalAnswer          # "<Class>.<Method>" of the package unit
    onConversationCreated: Notes.Begin
    afterResult: Reports.Postprocess
```

Implementation sketch:

- `public Dictionary<string,string>? HookNames { get; set; }` with `[JsonProperty("hooks")]` and a `[Description]`; typed string->string so
  `TypeSchemaBuilder` (flat flavour) describes it as an untyped/`object` property `[I]` (flat flavour only types primitives and arrays of primitives, `TypeSchemaBuilder.cs`
  FillType; a dictionary is untyped there, which `YamlPipelineFactory.CheckValue` accepts, `YamlPipelineFactory.cs:455-457`). To get proper validation the catalog (not
  the framework builder) should enrich it: see 3.10.
- `ResolveHooks()` next to `ResolveTools()` in `OnConfigured` (`YamlLlmStep.cs:240-245`): for each `(hook, name)` look up `IHookRegistry` from `_services`
  (the provider the step was created with, `YamlLlmStep.cs:73-77`), check the hook key is a known member of `LlmStepHooks`, resolve `name` to a delegate of that
  member's type, assign. Unknown hook key, unknown name or wrong signature = `InvalidOperationException` naming the step and listing available names, which
  `YamlPipelineFactory` already wraps into `Step 'x' (type 'LlmStep') could not be created: ...` (`YamlPipelineFactory.cs:296-304`). Zero change in
  `YamlPipelineFactory` or `StepRegistry` for this case `[V]` (`StepRegistry.Create` just `Populate`s, `StepRegistry.cs:100-111`).
- `IHookRegistry` (in `AITaskAgent.Abstractions`, so Core and the C#-unit extension can use it without Roslyn):
  `bool TryGet(string name, Type delegateType, out Delegate d)`, `IReadOnlyCollection<HookInfo> List()`. A hook is `HookInfo(name, delegateType, description)`.
  The compiled package unit (study STUDY section 6) registers each public static method of its generated class under `<Class>.<Method>`; a host can also register by
  hand `registry.Add("Notes.Begin", (Func<ConversationCreatedArgs,Task>)Notes.Begin)`.
- Reflection-based binding of an arbitrary static method (not a prebuilt delegate) is `Delegate.CreateDelegate(delegateType, methodInfo)`, which fails with
  a clear `ArgumentException` on signature mismatch; that is also the static validation primitive (3.11).

Generalisation beyond `YamlLlmStep`: a base class `HookableStep`? Not needed. Only LLM steps have hook families. `Core` steps keep `DelegatedStep`.

### 3.9 Suspension and resume

`[V]` What is persisted is the context: `PipelineContextSerializer` saves Metadata and, for `IPersistableContextState`, the conversation; `StepState` and bookmarks
are explicitly **not** kept (`LlmPipelineContext.cs:20-23`); `PipelineResume` keys live in `Metadata` (`PipelineResume.cs:9-12,20-34`). Steps are not serialized.
`[I]` On resume the host rebuilds the pipeline from YAML (a new `YamlPipelineFactory` run), so `ResolveHooks()` runs again and delegates are re-bound by name
for free. Consequences to document and test: (a) a package compiled differently at resume time (source edited) rebinds to the new code, there is no snapshot of
the delegate; the designer's trust hash on the unit is the guard; (b) a hook must not keep state in a closure across a suspension; (c) hook state that must
survive goes to `Metadata` (persisted) and not to a `ConditionalWeakTable` keyed by context (`NoteSessions`, `NoteSession.cs:233-236` is lost on resume: this is
already true today of the sample, so no regression; `[?]` whether the notes protocol is ever active across a `SuspendStep`, not checked).
`StatelessTemplateLlmStep` suspension uses instance fields (`:41-60`); hooks do not touch that.

### 3.10 Catalog and inspector (designer)

`[V]` The designer catalog builds per-step parameter schemas from `IStepRegistry.Describe` and adds `x-editor` hints by `(category, property)`
(`Samples/WorkflowDesigner.Core/Catalog/StepCatalog.cs:164-186`). Proposal:

- `hooks` appears as an object property. Add in `AddEditorHints` a case `(StepCategories.Llm, "hooks") => "hooks"`, and extend the catalog DTO with a
  per-step list of hook slots (`name`, `description`, `delegateSignature` as text, e.g. `ToolLimitContext -> Task<ToolLimitDecision>`), generated from
  `LlmStepHooks` members via `[Description]` and the delegate's generic arguments.
- The inspector then shows one dropdown per slot, filled from a new endpoint (`GET /api/hooks`, like `GET /api/tools` for `tools`, `StepCatalog.cs:165`)
  that lists `IHookRegistry.List()` filtered by delegate type. `x-editor` is a free-form string hint `[V]`, so no schema-format change.
- Package-defined hooks appear only after the package unit compiled: the catalog must treat the list as dynamic per package `[I]`.

### 3.11 Static validation

Two levels, both before running:
1. Build time (always on): `ResolveHooks()` (3.8): key known, name registered, `Delegate.CreateDelegate` accepts the signature.
2. Designer validation (`WorkflowValidator`/`WorkflowCompiler`, which already instantiate through `YamlPipelineFactory`, grep `Samples/WorkflowDesigner.Core/Validation/*`):
   they get level 1 for free if the compiled package unit is registered in the validation host. When the unit failed to compile, report the hook as "unresolved:
   package unit has errors" instead of "unknown name".

### 3.12 Impact summary

| Piece | Change |
|---|---|
| `StepRegistry`, `YamlPipelineFactory` | none for hooks on `YamlLlmStep` (name resolution happens in the step's `[OnDeserialized]`); optionally later a generic `IHookable` contract so the factory does it for any step |
| `TypeSchemaBuilder` | none; `Hooks` is `[JsonIgnore]`, `HookNames` is a plain dictionary property |
| `BaseLlmStep` | `Hooks` property, call-site dispatch at 7 places, `InvocationState` gains `FinalAnswerMode` |
| `YamlLlmStep` | `HookNames`, `ResolveHooks()`, re-declare `Hooks` with `[JsonIgnore]` (as for `Tools`, `YamlLlmStep.cs:130-136`) |
| `AITaskAgent.Abstractions` | `IHookRegistry`, `HookInfo` |
| DI | one default registry (`AddAITaskAgent`) `[?]` where exactly: not read |
| Designer | `x-editor: hooks`, catalog slots, `/api/hooks` |

## 4. Application to F1 and F2

### 4.1 F1: final answer after the tool limit

What the sample does today (`CustomStatelessTemplateLlmStep.cs`):

1. `ExecuteAsync` base runs the tool loop; on the iteration limit the result is an error with `ToolIterationLimitExceededException` (`BaseLlmStep.cs:355-358`);
   on a repeat-detector / tool-requested cut the result is a *success* flagged with `ToolLoopStopMessage` (`LlmInvocation.cs:142,197,345-364`;
   `BaseLlmStep.cs:569`). The sample turns the second into an error with `ToolLoopCutException` (`:102-103`) so both take the same path.
2. `RetryMiddleware` sees an error with `OriginalException` -> asks `SwitchToNextFallback` (`RetryMiddleware.cs` CASE 1) -> the sample returns `true` and sets
   `FinalAnswerMode` without touching the bookmark (`:214-221`); `attempt` resets to 1.
3. On the new attempt 1: `GetConversationContext` returns the stored conversation instead of a fresh one (`:228-233`), `BuildUserMessageAsync` returns
   `ToolLimitFinalAnswerPrompt` (`:250-253`), `ConfigureLlmRequest` adds `tool_choice: none` and keeps the tool definitions (cached prefix) (`:255-264`).
4. `FinalizeAsync` clears the per-context state (`:267-272`).

Proposed framework version:

- `BaseLlmStep.ToolLimitFinalAnswerPrompt` (public `string?`, `init`; **same name** so `AnalysisPipelineFactory.cs:327,558` compile unchanged) and the hook
  `Hooks.OnToolLimit`. Property = the default hook: `ctx => FinalAnswer(ToolLimitFinalAnswerPrompt)` when set, `Fail` otherwise. A hook assigned overrides the property.
- `InvocationState` gets `FinalAnswerMode` and `FinalAnswerPrompt` (the framework can see `state.Conversation`, which the sample could not, `BaseLlmStep.cs:131`;
  that is why the sample needed its own `ConditionalWeakTable`).
- `BaseLlmStep.SwitchToNextFallback` order: (1) if not final-answer mode, conversation exists and the error is `ToolIterationLimitExceededException` or the new
  public `ToolLoopCutException` (moved to `AITaskAgent.Extensions.Llm/Models/`) -> ask `OnToolLimit`; `FinalAnswer` -> set mode, return `true` without restoring the bookmark;
  (2) if in final-answer mode -> return `false` (R2); (3) otherwise the existing provider-failure rule (`:180-224`).
- The `ToolLoopStopMessage` -> error conversion moves into `ExecuteAsync` after `ParseLlmResponseAsync` (`:376`), only when a decision hook or the property is
  configured and not already in final-answer mode (as `:102`); otherwise the cut stays a normal answer, as now.
- In final-answer mode `ExecuteAsync` attempt 1 must: reuse `state.Conversation` instead of calling `GetConversationContext` (but still fire
  `OnConversationCreated` with `IsFinalAnswerAttempt = true`); **keep the original `InitialBookmark` and the original `Message`**; use `ToolLimitFinalAnswerPrompt` as the user
  message; and add `tool_choice: none` after `ConfigureLlmRequest` (so a user `ConfigureRequest` hook cannot drop it).
- `[V]` The bookmark/Message point is a real behavioural difference I found while reading: the sample is only correct because its conversation is the throw-away
  stateless one. `ExecuteAsync` creates a *new* bookmark on every attempt 1 (`BaseLlmStep.cs:298`) and `FinalizeAsync` writes `state.Message` as the canonical user
  message (`:257-260`). In a *stateful* step (shared conversation) a naive move would (a) bookmark after the tool traffic, leaking it into the persistent history, and
  (b) record the final-answer prompt as the user's turn. `[I]` Hence the two "keep" rules above. This needs its own test (stateful step + tool limit).
- `FinalAnswerRequests` (`CustomStatelessTemplateLlmStep.cs:337-347`) moves to the framework unchanged (the two sample test files use it:
  `CustomStatelessTemplateLlmStepTests.cs:68`, `NoteProtocolTests.cs:84,321,357`).
- `[?]` Providers other than OpenAI-compatible: the sample's comment says `tool_choice` goes "tal cual al cuerpo en los servicios OpenAI-compatibles" `[V]
  CustomStatelessTemplateLlmStep.cs:340`. Whether the other `ILlmService` implementations honour `Options["tool_choice"]` was not checked; a provider that ignores
  it would let the model call tools in the "no tools" attempt, which would hit the tool loop again (limit counter restarts) instead of answering.

What stays in the sample: the report post-processing and file write (`:130-172`), the progress reports (`:89-128`), provenance (`:183-201`). Those are F3 (`afterResult`
or a following C# step); with `AfterResult` they become `Reports.Postprocess(ResultArgs)` of the package unit, and the sample class collapses to a thin subclass or disappears.

### 4.2 F2: note protocol

Today: `TagHandlers = [NoteTagHandler]` set in the factory (`AnalysisPipelineFactory.cs:560`), `UsesNoteProtocol` checks the list (`CustomStatelessTemplateLlmStep.cs:248`),
`NoteSessions.Begin(context, conversation, Name, NoteOptions)` is called from `GetConversationContext` on every attempt 1 (`:238-239`), `CloseNoteSession` from `FinalizeAsync`
(`:267-272,278-293`), and the tools are wrapped by `NoteProtocolTool` (`Samples/CodebaseAnalysis.Core/Notes/NoteProtocolTool.cs:13`; wrapping at `AnalysisToolRegistration.cs:80-82`, cited by study B).

With hooks:

- `OnConversationCreated` replaces the `GetConversationContext` override for this purpose: `Notes.Begin(ConversationCreatedArgs a) => NoteSessions.Begin(a.Context, a.Conversation, a.Step.Name, options)`.
  `NoteSessions.Begin` is already idempotent over the same conversation (`NoteSession.cs:243-246`), which is exactly what the final-answer attempt needs.
- Closing the session (`CloseNoteSession`) needs the end-of-invocation moment: `AfterResult` fires after `FinalizeAsync`'s compaction? The current `FinalizeAsync` runs *after*
  the result is final and after retries. `AfterResult` as proposed (per attempt, in `ExecuteAsync`) is not the same moment. So F2 needs a second notification,
  `OnFinalize(ResultArgs)` (call-site dispatch from `BaseLlmStep.FinalizeAsync`, `:232`), which I would add alongside. Without it the guardrail "warn about results with no note"
  (`:278-293`) cannot move out of the sample. Cost S.
- Tag handlers by name: `tagHandlers: [notes]` resolved from an `ITagHandlerRegistry` is a second, separate registry (study B, F2). The hook registry could carry handler
  factories too (`HookInfo` with delegate type `Func<ITagHandler>`), but `ITagHandler` instances are *objects with an interface*, not delegates, and `IStepValidator`/`ITool`
  show the repo's convention for those: DI-registered services by name (`ResolveTools` uses `GetServices<ITool>()`, `YamlLlmStep.cs:~185-200`). Recommendation: keep tag
  handlers as DI services resolved by name (`TagHandlerNames` + `ResolveTagHandlers()`, same pattern as `ToolNames`) and use hooks only for behaviour that is a method.
- The `NoteProtocolTool` wrapper: not a hook. It is "tools by name" with a decorator; leave it to whoever registers the tools (the package/host), as today.
- So after the change F2 is: `tagHandlers: [notes]`, `hooks: { onConversationCreated: Notes.Begin, onFinalize: Notes.End }`, tools registered wrapped. `UsesNoteProtocol` goes away
  (it is only read by the same sample step). Nothing about notes remains special in the framework.

## 5. What to convert first, what not

Recommended order (value / risk), with cost S/M/L:

| # | Piece | Why | Cost |
|---|---|---|---|
| 1 | `LlmStepHooks` + `Hooks` property + call-site dispatch skeleton with exception wrapper | prerequisite; no behaviour change when null | S |
| 2 | `IHookRegistry` + `YamlLlmStep.HookNames/ResolveHooks` + DI default | unlocks YAML binding; copies `ResolveTools`; errors at build time | M |
| 3 | `OnConversationCreated` (+ `OnFinalize`) | needed by F2; tiny; idempotence rule already satisfied by `NoteSessions.Begin` | S |
| 4 | F1 in `BaseLlmStep` (final-answer mode, `ToolLimitFinalAnswerPrompt`, `OnToolLimit`, `ToolLoopCutException`, `FinalAnswerRequests` moved) | the actual motivator; touches fallback semantics; own tests incl. stateful step | M |
| 5 | `BuildUserMessage` / `BuildSystemMessage` / `ParseResponse` / `ConfigureRequest` around-hooks | cheap once 1 exists; used by `BatchReader`/`FindingJudge`-style steps and the designer's C# glue | S each |
| 6 | `AfterResult` (post-process the final result) | F3: lets the report post-processing live in the package unit | S/M (decide per-attempt vs after-finalize, see 4.2) |
| 7 | Designer: catalog slots, `x-editor: hooks`, `/api/hooks`, validation message | needed for the designer, not for the runtime | M |
| 8 | Wire `IStepValidator` by `Id` (`validators: [id]`) through the same registry | finishes a design that was left half done (`StepBase.cs:81-85`) | S, optional, separate |

Do **not** convert (reason):

- `SwitchToNextFallback`: bookkeeping and the replay hazard (3.6). Expose a decision hook (`OnToolLimit`) instead.
- `FinalizeAsync` as a free-for-all hook: it runs in `RetryMiddleware` only (`BaseLlmStep.cs:243-247` comments), so a step invoked directly skips it; give
  hooks the narrow `OnFinalize` instead of the raw method.
- `SaveStateAsync` / `RestoreStateAsync`: no `PipelineContext` parameter, so a delegate could only use closures (not re-entrant); fix the interface first.
- `BuildLlmRequestAsync`, `AddConversationToContext`, the Enrich hooks, `Validate`, `Schema`: no overrides anywhere (or already served by an interface), so no demand and
  every new public surface is a contract. `[V]` greps in 1.6.
- `GetConversationContext` as *replace*: `StatelessTemplateLlmStep`/`YamlLlmStep` already differ in how they size the window (study B 2.2 item 1); a replace-delegate would
  be a third variant. Offer only the notification.
- Core flow steps (`LoopStep`, `GroupStep`, `ParallelStep`, ...): no hooks exist; `DelegatedStep` + a package method already covers custom logic.

Compatibility with existing subclasses and tests:

- Hooks null (default) = same code path; the delegate wrapper is `Hooks?.X is { } h ? h(args, next) : next()`. `[I]` No test should change: `Tests/LlmFallbackClassificationTests.cs`,
  `StatelessTemplateLlmStepTests`, `YamlLlmStepToolsTests`, `ParallelBranchIsolationTests`, and the sample's `CustomStatelessTemplateLlmStepTests`, `NoteProtocolTests`, `ToolLoopGuardTests`.
- The F1 move is the only change in behaviour surface. Keep the sample step compiling and green during the move by (a) leaving it in place, (b) adding the base feature behind the
  same property name, (c) deleting the sample copy in a separate step once the sample tests pass against the base implementation (they use `FinalAnswerRequests` and
  the step's public API only, `[V]` greps above). If the sample keeps its own `ToolLimitFinalAnswerPrompt` while the base also has it, `init` hiding by `new` would silently
  split the value: delete the sample's property in the same commit that adds the base's.
- Adding `Hooks` to `BaseLlmStep` adds a public property to a public class: `[JsonIgnore]` keeps the catalog stable, and `YamlLlmStep` must re-declare it like `Tools`
  or the registry would describe it `[V]` (`YamlLlmStep.cs:116-120` comment states the rule).

## 6. What I could not check

- `[?]` Whether every `ILlmService` implementation honours `Options["tool_choice"]` (only the sample comment says OpenAI-compatible ones do).
- `[?]` The full list of tests that construct `BaseLlmStep` subclasses and rely on `attempt==1` side effects after a fallback switch; I read the names (`LlmFallbackClassificationTests`,
  `CustomStatelessTemplateLlmStepTests`, `NoteProtocolTests`) but not their bodies beyond the lines cited.
- `[?]` Where the default DI registration of an `IHookRegistry` should live (`AddAITaskAgent` / `LlmExtension`); I did not read the registration code.
- `[?]` Exact `ConfigurationModel`/`x-editor` rendering of a `Dictionary<string,string>` property in the frontend (I read only the C# side of the catalog).
- `[?]` Whether the note protocol is ever active across a `SuspendStep`/resume (state is a context-keyed weak table, not persisted).
- `[?]` `ToolLoopStopMessage` semantics for non-`LlmStepResult` results (`BaseLlmStep.cs:561` guards with `is LlmStepResult`; steps whose `TOut` is another `ILlmStepResult` never get it,
  which is also how the sample's `result is LlmStepResult` test behaves `:102`).
- `[?]` Cost of the Roslyn-compiled unit registering methods as `IHookRegistry` entries: depends on the not-yet-built `CSharpStep` extension (study section 6).
- `[I]` All design statements in section 3 are proposals; nothing was built or run, no build or test executed.



---

# Study F: generic step hooks on `StepBase`, C# validators and response types usable from YAML (revision of Study E)

Read-only study, 2026-10-19. `[V]` = I read the line; `[I]` = inferred; `[?]` = not known. Recommendations are not marked.
Paths relative to `C:\GeminiCli\AITask\AITaskAgent`. Nothing was built, run or changed. Inputs: Study E and sections 5, 6, 8 of
`docs/designer/STUDY-CODEBASE-ANALYSIS-IN-DESIGNER.md`, plus the code cited below.

## 0. Headline

1. **`StepBase` already has a non-virtual layer where generic hooks fit**: the explicit interface implementations
   (`StepBase.cs:194-216`: `IEnrichable.*`, `IStep.FinalizeAsync`, `IStep.ExecuteAsync`, `IStep.ValidateAsync`) `[V]`. The runtime only ever calls
   those (`RetryMiddleware.cs:104,132,160`, `ObservabilityMiddleware.cs:27-50`) `[V]`. Study E's "dispatch from the call site" survives for
   everything that is *additive*; for *replacing/wrapping* hooks the user's rule (virtual default calls the delegate) works if three framework
   overrides are adapted (section 2.3).
2. **Three of the hooks the user asks for already exist as extension points and only lack a name binding**: semantic validation
   (`IStepValidator` + `AddValidator`, unwired, `StepBase.cs:81-90`), `Enrichable` (`:92`) and `Validate` (`:180`, no override in the repo `[V]`
   grep). They need a registry and a YAML key, not a new mechanism. `ValidatorTagAttribute` (`Attributes/ValidatorTagAttribute.cs`) has **zero users**
   `[V]` grep: dead, to delete or to ignore.
3. **The user's rule fails exactly where it matters for result validation**: `StepResult.ValidateAsync` is virtual, but the result types YAML steps
   produce override it without calling `base` (`JsonStepResult.cs:33`, `LlmJsonStepResult.cs:21`; also `LlmStringStepResult.cs:16`,
   `CriticResult.cs:75`) `[V]`. A hook placed in the virtual default of `StepResult.ValidateAsync` would be ignored by `JsonStepResult`. So result-attached
   validation is run by a non-virtual path in `RetryMiddleware` (section 4). This is a deliberate exception to the rule: decision for the user.
4. **14 CodebaseAnalysis steps do not derive from `StepBase`** (they implement `IStep<TIn,TOut>` directly, e.g. `DotNetEnricherStep.cs:24`,
   `SharedPassStep.cs:31`, `VerifyReportStep.cs:24`) `[V]`. `StepBase` hooks do not reach them; they are configured from C# anyway.
5. **For response types do not use `TypeSchemaBuilder`; reuse the builder `StepResultFactory` already uses** (`StepResultFactory.cs:196-300`,
   private): strict-mode (`additionalProperties:false`), `[Description]`, validation attributes, `[ExcludeFromLlmSchema]`, nested types `[V]`.
   `TypeSchemaBuilder` describes *configuration* (settable public properties, flat flavour leaves nested classes untyped,
   `TypeSchemaBuilder.cs:45-46,135`) `[V]`. Making the former public is the cheaper and more faithful option.

## 1. Current state, facts that drive the design

| Fact | Evidence |
|---|---|
| `StepBase(name, inputType, outputType)`; `MaxRetries = 3` public settable | `StepBase.cs:18,31` `[V]` |
| Semantic validators: private list, `Validators` getter-only (so not in the catalog), `AddValidator` has no caller | `StepBase.cs:21,79,86-90`; grep `AddValidator` over `*.cs`: only the definition `[V]` |
| `Validate` virtual iterates `_validators`, no override anywhere | `StepBase.cs:180-191`; grep `override .*Validate\(` empty `[V]` |
| `Enrichable` is `virtual`, public settable, not `[JsonIgnore]` in `StepBase`; only `YamlLlmStep` hides it | `StepBase.cs:92`; `YamlLlmStep.cs:145-151` `[V]` |
| Enrich virtuals call `Enrichable?.X`; `ObservabilityMiddleware` invokes them **before** its `try`, so an exception there fails the step | `StepBase.cs:133-162`; `ObservabilityMiddleware.cs:27-50` (try starts after `:50`) `[V]` |
| Structural result validation, then semantic validation, in that order; both only for non-error results | `RetryMiddleware.cs:79-97` (error cases return/continue first), `:104`, `:132` `[V]` |
| Validation failure = `result.Error = StepError{Message}` without exception, `attempt++`, re-execute | `RetryMiddleware.cs:122-129,146-153` `[V]` |
| The `try/catch` of the loop covers only `step.ExecuteAsync`; exceptions thrown by `result.ValidateAsync`, `step.ValidateAsync` or `FinalizeAsync` (lines 160,173) propagate | `RetryMiddleware.cs:56-76` vs `:104,132,160` `[V]` |
| `FinalizeAsync` runs only inside `RetryMiddleware` (exit points `:73,89,160,173`) | `[V]` |
| `IStepResult.ValidateAsync(PipelineContext)` has no step reference and no `CancellationToken` | `IStepResult.cs:20-21` `[V]` |
| `StepResult.ValidateAsync` virtual returns valid; overrides without `base`: Json, LlmJson, LlmString, Critic; `LlmIntentionResult` calls base | `StepResult.cs:51`; `JsonStepResult.cs:33`; `LlmJsonStepResult.cs:21`; `LlmStringStepResult.cs:16`; `CriticResult.cs:75`; `LlmIntentionResult.cs:12-14` `[V]` |
| `JsonStepResult.Validate(JToken, JsonSchema)` is the NJsonSchema validation, shared with the LLM result | `JsonStepResult.cs:41-63`, `LlmJsonStepResult.cs:22` `[V]` |
| `ScriptStep : StepBase`, ctor `(IStepResult -> JsonStepResult)`, creates `new JsonStepResult(this, value)` **without schema** | `ScriptStep.cs:58,92,171` `[V]` |
| `ExecStep : StepBase`, `(IStepResult -> StepResult<string>)`, overrides `EnrichStartedEvent` and calls base | `ExecStep.cs:59,90,150-154,260` `[V]` |
| Only `SuspendStep` and `YamlLlmStep` implement `IJsonSchemaStep` (so only they accept `inputSchema`/`outputSchema`) | grep `IJsonSchemaStep` `[V]`; `YamlPipelineFactory.cs:330-342` throws otherwise |
| Factory resolves schema names against `SchemaRegistry`; unknown name = build error listing the registered ones | `YamlPipelineFactory.cs:549-560` `[V]` |
| `SchemaRegistry.Add(name, JsonSchema)` exists; duplicate name throws; registered as singleton with `TryAddSingleton` | `SchemaRegistry.cs:87-91,144-148`; `YamlServiceCollectionExtensions.cs:23` `[V]` |
| `YamlPipelineFactory` is built from `(IServiceProvider, IStepRegistry, SchemaRegistry, IPipeline)` and only sets `Name` when `step is StepBase` | `YamlPipelineFactory.cs:101-105`, `:307-318` `[V]` |
| Catalog: `StructuralKeys` (`id,type,dependsOn,inputSchema,outputSchema`) are removed from the parameter schema; `inputSchema/outputSchema` are listed per step only for `IJsonSchemaStep` | `StepCatalog.cs:23-24,95-98,113-119` `[V]` |
| `enrichable` is a YAML key today with no type; nothing in the designer filters it | no match for `enrichable` (case-insensitive) in `Samples/WorkflowDesigner*` sources `[V]`; `TypeSchemaBuilder` leaves interfaces untyped (doc remarks, `:16-22`) |
| `TypeSchemaBuilder` skips properties without a public setter or with `[JsonIgnore]` | `TypeSchemaBuilder.cs:45-46` `[V]` |
| Working tree has an **uncommitted** change to `TypeSchemaBuilder` (flat flavour now describes string-keyed dictionaries as objects) | `git diff` `[V]`; a `hooks: {slot: name}` map depends on it |

## 2. Design: generic hooks on `StepBase`

### 2.1 Shape

```csharp
// AITaskAgent.Abstractions (so the Yaml project and the C# unit can reference it without Core)
public sealed record StepHooks
{
    public HookAround<PostProcessArgs, IStepResult>?   PostProcess  { get; init; }  // after ExecuteAsync, before validation, per attempt
    public Func<FinalizeArgs, Task>?                    OnFinalize   { get; init; }  // once, after the last attempt
    public Func<EnrichActivityArgs, Task/void>?         EnrichActivityBefore/After  // see 2.4
    public Func<EnrichEventArgs<StepStartedEvent>, StepStartedEvent>?        EnrichStarted   { get; init; }
    public Func<EnrichEventArgs<IStepCompletedEvent>, IStepCompletedEvent>?  EnrichCompleted { get; init; }
    // NOT slots: validators (lists, 2.2) and LLM hooks (LlmStepHooks, 2.5)
}
public delegate Task<T> HookAround<TArgs, T>(TArgs args, Func<Task<T>> next);

// StepBase
[JsonIgnore] public StepHooks? Hooks { get; set; }   // set once at build time; record is immutable
```

- `StepHooks` is an **immutable record**; `Hooks` is replaced atomically, never mutated (steps are shared by concurrent runs and parallel branches;
  Study E 3.7, `BaseLlmStep.cs:120-126`). Delegates must be re-entrant and keep state in `PipelineContext`.
- All args are records with `Step` (`StepBase`), `Context`, `Attempt` (where it exists), `CancellationToken`, so a new field never breaks a bound method.
- `Hooks`, and the lists of 2.2, are `[JsonIgnore]` and non-virtual: the registry never describes them (`TypeSchemaBuilder.cs:45-46`).
- `Enrichable` gets `[JsonIgnore]` in `StepBase` (not only in `YamlLlmStep`), which removes the untyped `enrichable` key from every step's
  catalog schema. Behavioural change, but the key never worked from YAML (a live object cannot come from JSON) `[I]`. `YamlLlmStep`'s re-declaration
  can stay; whether `Attribute.IsDefined` finds `[JsonIgnore]` on an overridden property through the base declaration is `[?]`, so keep it.

### 2.2 Validation hooks are lists of validators, not a slot (one mechanism)

- `validators: [name, ...]` fills the existing `_validators` list (`StepBase.cs:21`) with `IStepValidator` objects. A C# method
  `Task<(bool,string?)> M(ValidateArgs)` of the package unit is wrapped in a `DelegateValidator : IStepValidator` with `Id = name`. Hence
  "a `Validate` hook" is just a validator with a delegate behind it; `Validate` (`:180`) keeps iterating the list (that is the rule "virtual default calls the
  hook"; no override exists to adapt `[V]`).
- `ValidateArgs(StepBase Step, IStepResult Result, PipelineContext Context, int Attempt, CancellationToken ct)`. Wider than `IStepValidator.ValidateAsync`
  (`IStepValidator.cs:~20`) because it carries the step and attempt; the interface is unchanged.
- Order: validators in declaration order, first failure short-circuits (as today, `StepBase.cs:84`). Failure goes through the existing semantic path
  (`RetryMiddleware.cs:132-153`, `ValidationType = Semantic`).
- Result validation (the response's own contract) is a **different list** with a different slot in the middleware, see section 4.

### 2.3 Precedence with overrides (the rule, and what it forces us to touch)

Rule to document, in one paragraph:

> A hook is the **default implementation** of its virtual method, one layer above the class's built-in behaviour. For `Foo()` on step `S`: the most derived
> override runs first; each `base.Foo()` goes one level up; the first class in the chain that is hook-aware consults `Hooks.Foo`, passing `next` = its own
> built-in behaviour. An override that does not call `base` replaces the hook too (inheritance wins over the instance delegate). *Notification* hooks
> (additive, no return value that the runtime consumes) fire at the call site and are never bypassed.

Two corollaries enforced by code, not by documentation:

- **R1. A framework class that overrides a hookable virtual without calling `base` must dispatch the hook itself** (shared protected helper). A
  parameterised test enumerates the hookable virtuals of every framework step type and asserts the hook ran.
- **R2. Notification hooks (`OnFinalize`, `OnConversationCreated`, `Enrich*` after-the-fact) are dispatched from the explicit interface
  implementation / `ExecuteAsync` call site**, not from the virtual default. Immune to overrides, no adaptation needed.
- **R3. Bind-time warning**: the binder compares `MethodInfo.GetBaseDefinition()` vs the most derived declaring type; if the step's runtime type overrides a hookable
  virtual and that type is not on the framework's hook-aware list, it logs "hook 'x' runs only if the override calls base". Whether an override calls base is `[?]`
  (not checkable without IL), so this is a warning, not an error.

Overrides of hookable virtuals found in the repo (`[V]` grep `override .*<name>` over `*.cs`, then `base.<name>` in each file):

| Virtual | Override | Calls `base`? | Action |
|---|---|---|---|
| `FinalizeAsync` (StepBase `:170`) | `BaseLlmStep.cs:232` | **No** (no `base.FinalizeAsync` in the file; early `return Task.CompletedTask` at `:240`) | **Adapt**: make it async, call `await base.FinalizeAsync(...)` after the compaction on every exit. Without it, **no LLM step honours `OnFinalize`** |
| | `IntentionAnalyzerStep.cs:75` | yes (`:77`) | none |
| | `CustomStatelessTemplateLlmStep.cs:267` | yes (`:269`) | none |
| | `Samples/CodebaseAnalysis.Core/Chat/ChatAgent.cs:344` | yes (`:347`) | none |
| `EnrichStartedEvent` | `ExecStep.cs:150` | yes (`:154`) | none (hook runs inside base after `Enrichable`) |
| `Validate` | none | n/a | none |
| `BuildUserMessageAsync` (LLM) | `YamlLlmStep.cs:249` | **No** | **Adapt**: dispatch with `next` = its own rendering of `userPrompt` |
| | `TemplateLlmStep.cs:63` | yes (`:65`) | none; note the hook then supplies the *template text* that is rendered afterwards (document it) |
| | `CustomStatelessTemplateLlmStep.cs:250`, `BatchReader.cs:44`, `FindingJudge.cs:40` | yes (`:253`, `:47-48`, `:43-44`) | none; BatchReader/FindingJudge call base with a synthetic input, a hook would see that input |
| `BuildSystemMessageAsync` | `YamlLlmStep.cs:255` | **No** | **Adapt** as above |
| | `TemplateLlmStep.cs:70` | yes (`:73`) | none |
| `ParseLlmResponseAsync` | `YamlLlmStep.cs:270` | yes (`:272`) | none; `YamlLlmStep` sets `json.Schema` *after* the hook, which is the right order |
| `ConfigureLlmRequest` | `CustomStatelessTemplateLlmStep.cs:255` | yes (`:257`) | none |
| `GetConversationContext` | `StatelessTemplateLlmStep.cs:63`, `YamlLlmStep.cs:261`, `CustomStateless...:228` (the last calls base `:233`) | two do not | **no hook** (replace is dangerous, Study E); `OnConversationCreated` is a call-site notification at `BaseLlmStep.cs:296`, so no adaptation (R2) |
| `SwitchToNextFallback`, `SaveStateAsync`, `RestoreStateAsync` | `BaseLlmStep.cs:175`, `CustomStateless...:214` (base `:225`), `StatelessTemplate...:41,51` | n/a | **no hooks** (Study E 3.6, 5) |

Net adaptation: **3 methods in 2 framework files** (`BaseLlmStep.FinalizeAsync`, `YamlLlmStep.BuildUserMessageAsync/BuildSystemMessageAsync`). No sample
override needs a change. Reflection test (R1) is the safety net for future overrides.

Why this is different from Study E: E dispatched at the call site so that `delegate > override`. With the user's rule it is `override > delegate`
(unless the override calls base). Practical consequence: an author who *subclasses and also* binds a hook gets the subclass behaviour; a YAML author
(the main consumer) only has `YamlLlmStep` and the framework steps, all made hook-aware by the three adaptations above. I see no case in the repo where
the weaker precedence loses something that Study E's would have kept, other than `TemplateLlmStep`'s template-text nuance.

### 2.4 Map: what belongs to `StepBase` (all steps) and what only to LLM steps

| Hook | Where | Kind | Dispatch | What the default virtual does |
|---|---|---|---|---|
| `validators` (semantic) | `StepBase` | list of validators | `Validate` virtual (`:180`) | iterates `_validators` (already) |
| `resultValidators` | `StepBase` | list, runs in the structural slot | middleware, non-virtual (section 4) | n/a |
| `postProcess` | `StepBase` | around (replace / before / after / wrap) | new `protected virtual Task<IStepResult> PostProcessAsync(...)`, called from `IStep.ExecuteAsync` wrapper (`StepBase.cs:209-213`) after `ExecuteAsync` returns | no hook: returns the result unchanged |
| `onFinalize` | `StepBase` | notify | explicit `IStep.FinalizeAsync` (`:206`) calls virtual, then hook (R2) | virtual no-op (`:170`) |
| `enrichActivityBefore/After`, `enrichStarted`, `enrichCompleted` | `StepBase` | transform | the four virtuals (`:133-163`): `Enrichable` first, then the hook | `Enrichable` only |
| `buildUserMessage`, `buildSystemMessage` | `BaseLlmStep` | around | virtual default (`:227,230`) | `Prompt` / `SystemPrompt` |
| `configureRequest` | `BaseLlmStep` | transform | virtual default (`:482` per E) | returns the request |
| `parseResponse` | `BaseLlmStep` | around, `next` = base parse | virtual default (`:499`) | built-in parsing |
| `onConversationCreated` | `BaseLlmStep` | notify, idempotent | call site `:296` (attempt 1, also after fallback rounds, E 3.5) | nothing |
| `onToolLimit` | `BaseLlmStep` | decision | inside `SwitchToNextFallback` (`:175`) | `Fail` |

Changes versus Study E's seven: `afterResult` becomes the generic `postProcess` (per attempt, before validation) plus the generic `onFinalize` (once);
the two E proposed as LLM-only (`afterResult`, `onFinalize`) are now in `StepBase`. LLM-only list is therefore **six**:
`buildUserMessage, buildSystemMessage, configureRequest, parseResponse, onConversationCreated, onToolLimit`.
Not proposed: a generic `before`/replace-`execute` slot (that is `DelegatedStep`, `DelegatedStep.cs:12-14` `[V]`), `SwitchToNextFallback`, `Save/RestoreState`.
Demand evidence: of the `StepBase` hooks only `FinalizeAsync` (4 overrides) and `EnrichStartedEvent` (1, `ExecStep`) have users today; the Enrich slots are
cheap but have no consumer yet, so they can be built last.

### 2.5 Semantics

- **Around hooks** (`HookAround`): delegate gets `args` and `next`; not calling `next` = replace; before/after/wrap by calling it. Same as E 3.1.
- **`postProcess`**: runs per attempt, only for results without error, inside the `try` of the retry loop (`RetryMiddleware.cs:56-76`) so an exception takes the existing
  fatal path (`ErrorStepResult.FromException`, no retry, fallback consulted) `[V]`. The returned result must be assignable to `OutputType`
  (`StepBase.cs:41`) or the wrapper fails with a named error; it must preserve `NextSteps` unless it sets them deliberately.
  Runs before CASE 2/3, so validators see the post-processed result. Idempotent required (a fallback switch re-runs attempts, E 3.5).
- **`onFinalize`**: once, with the final result (success or error); exceptions are wrapped in `StepHookException(hook, step, inner)` and propagate (today they propagate too,
  `RetryMiddleware.cs:160,173` outside the try `[V]`). A step invoked without `RetryMiddleware` never finalizes (`BaseLlmStep.cs:243-247` comment per E), same limitation.
- **`enrich*`**: exceptions are **swallowed and logged** by the dispatcher, because `ObservabilityMiddleware` calls them outside its `try` (`:27-50` `[V]`) and an
  observability hook must not kill the step. This differs from the other hooks; document it.
- **Validators**: an exception inside a validator is not caught by the loop (`:104,132` outside the `try`). The dispatcher turns it into a `StepHookException`; the middleware
  catches that one type in the validation blocks, finalizes and returns `ErrorStepResult.FromException` (fatal, no retry). Small middleware change (internal class).
- **Cancellation**: `OperationCanceledException` passes through untouched.
- **Retries**: validators and result validators run on every attempt; their failure retries the **whole step** (`:122-129`). For deterministic steps
  (`ScriptStep`, `ExecStep`: the latter re-launches the process) that is expensive or unsafe; the designer should warn and recommend `maxRetries: 1`
  (`StepBase.cs:31` is already a YAML property) `[I]`.
- **Suspension**: results of a suspended step carry `SuspendInfo` (`IStepResult.cs:~17`); `[?]` whether a suspended result reaches CASE 2/3 or the validators. `ValidateArgs`
  and `PostProcessArgs` expose `IsSuspended` so a hook can skip it. Hooks are not persisted: the pipeline is rebuilt from YAML on resume (`[I]`, E 3.9) and names rebind to
  the *current* compiled unit; the unit's hash in the trust dialog is the guard. Hook state across a suspension must go to `Metadata`.

## 3. YAML binding

### 3.1 Syntax (flat keys, no unions)

```yaml
- id: Auditor_Errores
  type: LlmStep
  outputSchema: Finding                         # C# class of the unit, registered as a schema (4.3)
  validators: [Findings.NoDuplicates, "len-limit"]   # semantic, by IStepValidator.Id or unit method
  resultValidators: ["schema:Finding", Findings.Check]
  hooks:
    postProcess: Reports.AttachProvenance
    onFinalize: Notes.End
    onConversationCreated: Notes.Begin           # LLM-only slot
    onToolLimit: Auditor.FinalAnswer             # LLM-only slot
```

`hooks`, `validators`, `resultValidators` are **structural keys**: they apply to every `StepBase` step and are resolved by the factory, not by `Populate`. Therefore:
add them to `StepCatalog.StructuralKeys` (`StepCatalog.cs:23-24`) and to the per-step `structural` list for every step, not only `IJsonSchemaStep`
(`:113-119`); add the same keys to the factory's accepted-keys logic next to the schema keys (`YamlPipelineFactory.cs:330-342`). Non-`StepBase` steps reject them
with the same kind of message already used for schemas (`:334-339`). This also keeps them out of `ConfigurationModel`/the parameter schema.

Why the factory and not each step's `[OnDeserialized]` (as E proposed for `YamlLlmStep`, `YamlLlmStep.cs:240-245`): hooks are now on every step, so one binder avoids
N copies; `YamlLlmStep.ResolveTools` stays as is.

### 3.2 Registry

```csharp
public interface IHookRegistry
{
    bool TryGet(string name, HookKind kind, Type delegateType, out Delegate d);
    IReadOnlyCollection<HookInfo> List();               // for the designer
}
public enum HookKind { Hook, StepValidator, ResultValidator }
public sealed record HookInfo(string Name, HookKind Kind, Type DelegateType, string? Description, string Source /* "unit:pkg" | "di" | "host" */);
```

- Lives in `AITaskAgent.Abstractions` (no Roslyn, no Yaml dependency). Default instance registered with `TryAddSingleton` next to `SchemaRegistry`
  (`YamlServiceCollectionExtensions.cs:23` `[V]`), so the factory (which already holds an `IServiceProvider`, `YamlPipelineFactory.cs:101`) resolves it without changing its
  public constructor.
- Sources, in resolution order: (1) the compiled package unit registers each public static method it declares as `<Class>.<Method>` with its kind taken from the
  delegate type or an attribute (`[Hook]`, `[Validator]`, `[ResultValidator]`); (2) DI `IEnumerable<IStepValidator>` by `Id` (the pattern of `ResolveTools`,
  `YamlLlmStep.cs:214-223`), which finally gives `IStepValidator.Id` its documented meaning (`IStepValidator.cs:12-13`); (3) host `registry.Add(...)`.
  **Same name in two sources = build error**, not precedence (silent shadowing of a validator is worse than a refusal). `ValidatorTagAttribute` is not used.
- Built-in result validator `schema:<name>`: validates the result value against `SchemaRegistry` entry `<name>` with `JsonStepResult.Validate` (`JsonStepResult.cs:41`).

### 3.3 Static validation and order of resolution

At pipeline build (so also in the designer: `WorkflowCompiler` goes through `YamlPipelineFactory.CreateFromYaml`, `WorkflowCompiler.cs:197` `[V]`):

1. Step created and `Name` set (`YamlPipelineFactory.cs:296-318`).
2. Containers built (`:320-328`), then `inputSchema/outputSchema` assigned (`:330-342`), **then** hooks/validators bound (new), so a bound method can already see the schema.
3. For each key: step must be `StepBase`; slot must exist for the step's type (`StepBase` slots + `LlmStepHooks` if `BaseLlmStep`); name must resolve for that **kind**;
   `Delegate.CreateDelegate(slotType, method, throwOnBindFailure:false)` must succeed, else error "signature is X, slot expects Y".
4. Errors reuse the factory's wrapper (`Step 'x' (type 'T') could not be created: ...`, `:296-304`), listing available names of that kind.
5. When the unit failed to compile, the message is "unit has errors", not "unknown name". `[?]` depends on the not-yet-built unit extension.

Designer: `StepTypeDto` gets an additive `hookSlots` list (name, kind, delegate signature as text, description) generated from `StepHooks`/`LlmStepHooks` members;
`GET /api/hooks` lists `IHookRegistry.List()` per kind, like `GET /api/tools` (`StepCatalog.cs:165` per E). `x-editor` hints are free strings (E 3.10).

### 3.4 Suspension and resume

Same as 2.5: names rebind on rebuild; if the unit changed between suspend and resume the new code runs; a validator that depends on state of the first run must read it from
`Metadata`. `[?]` Not verified in code: whether the host rebuilds through `YamlPipelineFactory` on resume (E assumed it).

## 4. Result validation: how it attaches to an `IStepResult`

### 4.1 Options considered

| Option | Pros | Cons |
|---|---|---|
| A. Property on the result that `StepResult.ValidateAsync` consults (user's rule applied to the result) | validation travels with the response | **bypassed by `JsonStepResult`, `LlmJsonStepResult`, `LlmStringStepResult`, `CriticResult`** (they override without base) `[V]`; adapting four overrides leaves third-party ones broken |
| B. Delegate in the step (`Hooks.ValidateResult`) called from `StepBase.Validate` | trivial | it **is** CASE 3 (`:132`): semantic and structural collapse into one list, and `StepStartedEvent`/`StepValidationEvent.ValidationType` loses the distinction (`RetryMiddleware.cs:110-121` vs `:134-146`) |
| **C (recommended).** The step owns a list of *result validators*; they are **stamped on each result** by `StepBase` and executed by a **non-virtual** call in the CASE 2 slot | works for every result type, independent of overrides; keeps Structural vs Semantic; also usable by C# code that builds results by hand | one internal field on `StepResult`, one extra call in `RetryMiddleware` |

### 4.2 Option C in detail

- `StepResult` gains `internal List<IResultValidator> Attached` (not a property: not serialised; `PipelineContextSerializer` rebuilds results from stored JSON,
  `PipelineContextSerializer.cs:385-402`, and a delegate cannot be stored; whether the serializer reads properties by reflection is `[?]`, hence an internal field) and a public
  extension `result.AttachValidation(IResultValidator)` for C# code.
- `IResultValidator`: `Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken ct)` + `Id`. Delegate flavour
  `Func<ResultValidateArgs, Task<(bool,string?)>>` wrapped like validators. Defined in Abstractions; `IStepResult` itself is **not** changed.
- Stamping point: the `IStep.ExecuteAsync` wrapper of `StepBase` (`StepBase.cs:209-213`), after `PostProcessAsync`, for results without error: copies the step's `ResultValidators` into
  `Attached`. `[I]` `TypedStep` inherits this wrapper (its `IStep<TIn,TOut>` default implementation at `IStep.cs:77-85` is shadowed by the class implementation); if the compiler picked
  the default interface member instead, the stamp would be skipped for `TypedStep`: cheap to verify with one test, not verified here.
- Execution: `RetryMiddleware.cs:104` becomes `result.ValidateAsync(context)` **then**, if valid, `StepResultValidation.RunAttachedAsync(result, context, ct)`; same `StepValidationEvent`
  with `ValidationType = Structural`, same `result.Error` + retry path (`:122-129`). Non-virtual: no override can skip it.
- **No double validation with `Validate` (`:132`)** by construction: (1) the lists are disjoint and the binder checks the **kind** of each name (a `ResultValidator` cannot appear in `validators:`
  and vice-versa); (2) CASE 2 = "is the response itself well-formed" (inputs: result, context), CASE 3 = "is it acceptable for this step" (inputs: step, attempt, state); (3) `schema:Finding`
  runs once because `JsonStepResult` intrinsic schema check and the attached one are different checks only if the result has its own `Schema` too: the binder warns when `outputSchema: X` and
  `resultValidators: ["schema:X"]` name the same schema (redundant).
- Failure on a result with error never reaches validation (`:79-97`), so validators never need to handle `HasError`.
- Suspended results: `[?]`, see 2.5.

### 4.3 C# response types, end to end

Flow: `class` in the package unit -> JSON Schema -> `SchemaRegistry` -> YAML `outputSchema`/`inputSchema` -> NJsonSchema validation -> `JsonStepResult`/`LlmJsonStepResult`.

1. **Authoring** (in the unit, section 6 of the main document):
   ```csharp
   [ResponseType("Finding")]                 // optional name, default = class name
   public sealed class Finding
   {
       [Description("Short title")] public required string Title { get; init; }
       [Range(1,5)] public int Severity { get; init; }
       public List<string> Evidence { get; init; } = [];
   }
   public static class Findings
   {
       [ResultValidator] public static Task<(bool,string?)> Check(ResultValidateArgs a) { var f = a.Value.ToObject<Finding>()!; ... }
   }
   ```
2. **Schema generation**: after the unit compiles, the extension scans exported types with `[ResponseType]` and calls a **public** `ResponseSchemaBuilder.Build(Type)`,
   i.e. `StepResultFactory.BuildJsonSchema` (`StepResultFactory.cs:196-300`) promoted out of `private static`. Reasons over `TypeSchemaBuilder`:
   strict `additionalProperties:false` for providers with strict schema mode (`:207-211` comment), `ApplyValidationAttributes` (`:~262`), `[Description]`, defaults, `[ExcludeFromLlmSchema]`,
   nested types, `required` from `[Required]` or non-null `init` without default (`:~272-284`) `[V]`. It already is what produces `response_format` for typed LLM steps
   (`StepBase.Schema`, `StepBase.cs:100`), so the schema the model sees equals the schema the result is validated against. `TypeSchemaBuilder` (flat) types no nested class and does not set
   `additionalProperties:false` `[V]`; its `configuration:true` flavour requires `[Required]`/`required`, not nullability (`TypeSchemaBuilder.cs:62-65`), so required fields would be lost.
   Limits of the builder (`StepResultFactory`): class roots only (`:196-224` builds an object schema), no `$ref`/`definitions` (types are inlined, recursive types cut by `visitedTypes`),
   System.Text.Json attributes ignored (Newtonsoft only, `:~236`), enums as strings `[I]`. A recursive response type is therefore not supported (cut silently) and should produce a designer diagnostic.
   NJsonSchema `JsonSchema.FromType` (v11.6.1 is referenced everywhere, `*.csproj`) is the alternative if `$ref` is needed; not evaluated against strict-mode providers `[?]`.
3. **Registration**: `SchemaRegistry.Add("Finding", schema)` (`SchemaRegistry.cs:87`). A clash with a schema file of the same name throws (`:144-148`): the unit's names and the `schemas/` folder
   share one namespace; the error should name both sources (the message already carries the source, `:147`).
4. **Use from YAML**: `outputSchema: Finding` (`YamlPipelineFactory.cs:330-342`) on `LlmStep`/`SuspendStep`. For `LlmStep` the result becomes `LlmJsonStepResult`
   (`YamlLlmStep.cs:158-168`), `Schema` goes to the provider and the value is validated by `JsonStepResult.Validate` (`LlmJsonStepResult.cs:22`, `JsonStepResult.cs:41-63`), retried with the violation list as feedback.
5. **Typed access in C#**: the value is a `JToken` (`JsonStepResult : StepResult<JToken>`, `JsonStepResult.cs:16`), so hooks use `args.Result.Value.ToObject<Finding>()` (helper `args.As<Finding>()`).
   Property names are camelCase by the builder, Newtonsoft reads case-insensitively `[I]`. There is no compile-time typing across steps: that is the "poorly typed by design" of the YAML engine
   (`StepBase(name, inputType, outputType)`; `ScriptStep` `IStepResult -> JsonStepResult`, `ExecStep` `-> StepResult<string>`, `ScriptStep.cs:92`, `ExecStep.cs:90` `[V]`).
6. **Steps that are not `IJsonSchemaStep`** (`ScriptStep`, `ExecStep`, flow steps) cannot declare `outputSchema` (`YamlPipelineFactory.cs:334-339`). Two ways to type them with a C# class:
   (i) `resultValidators: ["schema:Finding"]` (recommended: no contract change), (ii) make `ScriptStep` implement `IJsonSchemaStep` and create `new JsonStepResult(this, value, OutputJsonSchema)`
   (one line at `ScriptStep.cs:171`, but it changes the step's public surface and its catalog entry). `ExecStep` returns text, so a C# class does not apply unless a validator parses it.
7. **Input side**: `inputSchema: Finding` on the first step is checked against the pipeline input (`YamlPipelineFactory.cs:56-64` `[V]`); for the rest it documents intent.

## 5. Impact

### 5.1 Public contract (additive vs not)

| Change | Additive? |
|---|---|
| New types in Abstractions: `StepHooks`, args, `HookAround`, `IHookRegistry`, `HookInfo`, `HookKind`, `IResultValidator`, attributes | yes |
| `StepBase.Hooks` (`[JsonIgnore]`), `ResultValidators` list, `PostProcessAsync` virtual | yes (a subclass already declaring `PostProcessAsync` gets a hiding warning, `[?]` none in repo: grep `PostProcessAsync` not run) |
| `StepBase.Validate` also fed by delegate validators; `AddValidator` unchanged | yes |
| `Enrichable` becomes `[JsonIgnore]` | **no for YAML/catalog** (key disappears; it never worked), yes for C# |
| `StepResult` internal field + `AttachValidation` extension; `IStepResult` untouched | yes |
| `RetryMiddleware` (internal) extra call + catch of `StepHookException` | internal; behaviour only with hooks |
| `BaseLlmStep.FinalizeAsync` calls `base`; `YamlLlmStep` Build* dispatch | behaviour-preserving when `Hooks == null` `[I]` |
| `StepResultFactory.BuildJsonSchema` becomes public façade | yes |
| `StepCatalog.StructuralKeys` + `structural` list + `StepTypeDto.hookSlots` | additive DTO; frontend consumption `[?]` |
| `YamlPipelineFactory` public ctor | unchanged (registry resolved from the provider) |
| `ScriptStep : IJsonSchemaStep` (optional alternative) | no (public surface/catalog) |

### 5.2 Tests

- Framework: `Tests/` has 288 `[Fact]/[Theory]` attributes `[V]` (grep count); I could not reproduce the user's 330 (Theory rows), `[?]`. With `Hooks == null` and empty lists the code path is
  today's, so none should change `[I]`. Most exposed: tests of `RetryMiddleware` order/events (new call in CASE 2), `BaseLlmStep` finalize/compaction tests (`FinalizeAsync` restructured: `LlmFallbackClassificationTests`,
  `StatelessTemplateLlmStepTests`, `ParallelBranchIsolationTests` per E), any catalog snapshot in `Samples/WorkflowDesigner.Core.Tests` that lists `enrichable` (not read, `[?]`).
- CodebaseAnalysis: its LLM steps derive from `StatelessTemplateLlmStep` and already call base in all overrides (table 2.3); `CustomStatelessTemplateLlmStepTests`, `NoteProtocolTests`, `ToolLoopGuardTests` unaffected until F1 moves.
  The 14 direct-`IStep` steps are untouched.
- New tests: R1 reflection test over hookable virtuals; binder (unknown name, wrong kind, wrong signature, duplicate name across sources); result-validation (stamped, runs once, retry, event type);
  exception conventions (2.5); `TypedStep` stamping check (4.2); parallel-branch re-entrancy.

### 5.3 Cost (S < 1 day, M 1-3 days, L > 3 days) and order

| # | Piece | Cost | Notes |
|---|---|---|---|
| 1 | `IHookRegistry`/`HookInfo`/`HookKind`, `DelegateValidator`, binder for `validators:` + DI `IStepValidator` by `Id`, `Enrichable [JsonIgnore]`, structural keys | M | finishes the half-built design (`StepBase.cs:81-85`); no LLM involved; unlocks C# validators from YAML |
| 2 | Result validators: `IResultValidator`, stamping in `StepBase`, call in `RetryMiddleware`, `schema:<name>`, `StepHookException` handling | M | the exception to the user's rule; needs the user's OK |
| 3 | `StepHooks` (`postProcess`, `onFinalize`, `enrich*`), `PostProcessAsync`, adapt `BaseLlmStep.FinalizeAsync`, bind-time warning (R3) | M | F2's end-of-invocation moment |
| 4 | `ResponseSchemaBuilder` public + registration from the unit into `SchemaRegistry` | S (builder) / depends on the unit (L, outside this study) | C# response types |
| 5 | LLM hooks: `onConversationCreated` (S), `buildUser/System` + `YamlLlmStep` adaptation (S), `configureRequest`, `parseResponse` (S each) | M total | R1 test lands here |
| 6 | F2 (note protocol): `onConversationCreated` + `onFinalize` + `tagHandlers` by name from DI (E 4.2) | S once 3 and 5 exist | |
| 7 | F1 (`onToolLimit`, final-answer mode in `BaseLlmStep`, `ToolLoopCutException` moved, `FinalAnswerRequests` moved) | M | unchanged from E 4.1, including the stateful-step bookmark caveat and the unchecked `tool_choice` provider support |
| 8 | Designer: catalog `hookSlots`, `x-editor: hooks`, `/api/hooks`, validation messages, retry warning for `ExecStep`/`ScriptStep` | M-L | frontend not read |

Recommended order: **1 -> 2 -> 3 -> 5 -> 6 -> 7**, with 4 as soon as the unit exists and 8 in parallel from 1. Rationale: 1 and 2 deliver the user's explicit asks (C# validators and result validation from YAML)
with the least risk; 3 and 5 are prerequisites of F2; F1 last because it changes fallback semantics.

## 6. Objections (concrete, not purism)

- **Override-over-hook silently disables a hook** when a user subclass overrides without `base`. Mitigated only by R1 (framework) and R3 (warning); it cannot be fully prevented. If this bites, Study E's call-site dispatch is the fix, and it can be
  adopted per hook later without changing YAML.
- **Result validation outside the virtual** (4.1-C) contradicts the literal rule "the virtual default calls the hook". It is the only way that covers `JsonStepResult`, i.e. the type every YAML step returns. I need the user's decision on it, with the argument above.
- **Validators on non-LLM steps retry the whole step** (`RetryMiddleware.cs:122-153`): a failing validator on an `ExecStep` re-runs the process up to `MaxRetries` (default 3). Needs a designer warning or `maxRetries: 1`.
- **Enrich hooks have no consumer in the repo** (one override, `ExecStep.cs:150`); building them first would add surface without demand.

## 7. What I could not check

- `[?]` Whether the frontend renders unknown structural keys/`hookSlots` (I read only the C# catalog).
- `[?]` Whether `Attribute.IsDefined(p, typeof(JsonIgnoreAttribute))` finds an attribute declared only on a base virtual property (decides whether `YamlLlmStep`'s re-declaration is still needed).
- `[?]` Whether a suspended result passes through CASE 2/3 and the validators (`AwaitingInputResult`/`SuspendStep` flow not read).
- `[?]` Whether `TypedStep` reaches the `StepBase.IStep.ExecuteAsync` wrapper or the `IStep<TIn,TOut>` default member (C# interface re-implementation rule, not run).
- `[?]` Whether `PipelineContextSerializer` serialises result properties by reflection (affects whether a delegate-carrying property would leak; the proposal uses an internal field).
- `[?]` Which framework tests assert on the `enrichable` key or on `FinalizeAsync` ordering; test bodies not read. The "330 tests" figure was not reproduced (288 attributes counted).
- `[?]` Strict-mode providers' treatment of the schemas `StepResultFactory` builds when the class is recursive; `tool_choice` support across `ILlmService` implementations (inherited from E).
- `[?]` The package-unit extension (section 6 of the main document) does not exist yet: registration of `[Hook]/[Validator]/[ResultValidator]` methods and diagnostics for "unit has errors" are designed against an interface I could not read.
- `[?]` Exact current line of `BaseLlmStep.ConfigureLlmRequest`/`ParseLlmResponseAsync` (taken from Study E, `:482`, `:499`); everything else cited above was read.
- `[I]` All design statements are proposals; no build or test was executed.


---

# Objections

Written by the implementers, in English; they do not reopen closed decisions. Each one says what was not built as studied and why.

## Phase A (hooks in the framework, 2026-10-19)

1. **`ToolLimitFinalAnswerPrompt` was not added to `BaseLlmStep`.** RETOMAR §0.2 says F1 is not approved as a property, and the CodebaseAnalysis sample's `CustomStatelessTemplateLlmStep` already declares a property of that name: adding it to the base would hide it silently (the study's own warning) and force a change in the sample, which is not to be touched. F1 exists only as the `onToolLimit` hook (`ToolLimitDecision.FinalAnswer(prompt)`). Moving the sample onto the hook (and deleting its copy) is a separate, approved step.
2. **`onToolLimit` runs inside `ExecuteAsync`, not inside `SwitchToNextFallback`**, because `SwitchToNextFallback` must not change. Study E §4.1 put it in the fallback path (new attempt 1 in «final-answer mode», `GetConversationContext` returning the stored conversation, own bookmark rules). The in-attempt version is simpler and keeps the bookmark/`Message` invariants by construction, but: (a) it does not reset `attempt` or fire `onConversationCreated` again, (b) when the cut was `MaxToolIterations` the cut turn's token usage is lost (the loop returns a failure without counters), (c) `tool_choice: none` stays in `state.Request` for later retries of the same invocation, (d) a provider that ignores `tool_choice` gets its tool calls dropped instead of a second tool loop. The study's `[?]` about providers other than OpenAI-compatible honouring `tool_choice` is still unverified.
3. **`tagHandlers: [names]` in YAML is not built.** It is cheap (a `TagHandlerNames` property resolved from `ITagHandler` services like `ToolNames`), but `YamlPipelineV2Tests.An_LlmStep_rejects_the_inherited_properties_that_do_nothing_in_YAML` pins that `tagHandlers` is rejected. A note protocol can already be attached with `onConversationCreated` + `onFinalize`; the handler instances still need C# (`TagHandlers` set by whoever builds the step). Decide whether to change that test.
4. **`YamlLlmStep.BuildUserMessageAsync/BuildSystemMessageAsync` do not call `base`** (the decision said they should). They call the protected dispatcher `DispatchUserMessageHook/DispatchSystemMessageHook` that the base default also uses, with the step's own rendering as `next`. A literal `base` call would give a hook `Prompt` (empty for a YAML step) as `next`. Same effect for the user: the hook is the default implementation of the virtual.
5. **`ValidateArgs` carries no `Attempt`, result validators get `CancellationToken.None`.** `IStepValidator.ValidateAsync`/`StepBase.Validate` have no attempt and `IStepResult.ValidateAsync(PipelineContext)` has no token. Changing those interfaces is a public contract change; a validator that needs the attempt can read `Metadata`.
6. **`CriticResult` (AdvancedLlmPipeline sample, plus the copy in PipelineVisualizer) overrides `ValidateAsync` without `base`**, so attached result validators are skipped for it. Not reachable from YAML; left alone because the samples are not part of this change. Any third-party result type has the same trap: documented in §14 of `architecture.md`.
7. **The R1 reflection test is a heuristic.** It reads IL for a call to a same-named method in a base type, to the dispatcher, or to the compiler's `<>n__` stub for `await base.X()`; the stub is accepted without checking what it forwards. It proves the reviewed overrides do not silently drop hooks, not that every future one calls base.
8. **Validators retry the whole step.** A failing validator on `ExecStep`/`ScriptStep` re-runs the process up to `MaxRetries` (default 3). Phase B should warn and suggest `maxRetries: 1`; nothing in the framework changes that.
9. **`onFinalize` exceptions do not escape the pipeline** as study F §2.5 expected: `Pipeline` turns what escapes `RetryMiddleware` into an error result whose `OriginalException` is the `StepHookException`. The step still ends as a failure naming the hook.
10. **A plain `StepHooks` assigned programmatically to an LLM step ignores the LLM slots** (`BaseLlmStep.LlmHooks` is `Hooks as LlmStepHooks`). YAML binding always builds the right subtype from `[HooksType]`.
11. **`PostProcess` result-type check only applies to a result the hook replaced.** Steps may legitimately return result types that are not their declared `OutputType` today (`NextSteps` flows, suspension), so the check is not applied to unmodified results.

12. **No hook is registered by the shipped host.** Phase B delivers the selectors, the endpoint and the validation, but `GET /api/hooks` is empty in production: the registry is filled by whoever calls `ConfigureEngineServices` (the host), an extension or, from phase C, the C# unit. The E2E used a scratch host outside the repo for that reason. Consequence: until phase C the section only shows its note and the names already written in a YAML. A «sample hooks» extension would fill it but is not asked for.
13. **`GET /api/hooks` is global, not per package.** Unit hooks (phase C) depend on the package's compiled unit, so the endpoint will need a per-package form (`/api/packages/{id}/hooks`) and the inspector must ask for the open package's list; the frontend currently asks once per workspace mount.
14. **The designer checks names and kinds, not signatures.** A hook registered with a delegate type that does not fit its slot is only reported by the factory (one error at a time, located by step). Showing the slot signature next to each option and filtering by compatible delegate type in the selector would avoid it, but needs the delegate type on the wire (today a formatted string); not done. Tuple element names (`IsValid`, `Error`) are lost in the signature text because reflection does not keep them.

## Phase C (C# unit per package, 2026-10-19)

15. **In-process C# can take the host down.** `Environment.Exit`, an endless loop or a thread left running affect the backend; cancellation is cooperative. Decided (study §8) and mitigated only by trust (`ICSharpStepPolicy`, 403 before running, code and hash shown). A worker process stays the safer option for packages from others.
16. **The trust gate is the same flag as `ExecStep` (`ExecTrusted`).** Confirming a package confirms both its commands and its C#; the dialog lists both, but there is no way to trust one and not the other.
17. **Validating loads the unit without trust.** Compiling executes nothing, but loading the assembly to bind hooks and build response-type schemas goes through reflection, which can instantiate custom attributes declared by the unit and read static metadata. A metadata-only path (symbols instead of a loaded assembly) would remove it; not built.
18. **The reference allowlist is not a sandbox.** It leaves out `System.Diagnostics.Process` and `System.Net.*`, but reflection (`typeof(object).Assembly`) reaches anything the process can.
19. **Typed parameters per step are not built** (study §6): a step reads what it needs through `StepCall`. YAML properties other than `code` are not validated against the method.
20. **Templates carry the code of a `CSharpStep` but not `csharp/shared.cs`**: a template inserted in another package may reference helpers that package does not have (a compile error, reported on validation).
21. **A schema the code generator cannot turn into a class is only a warning (`WFCS004`)**, so a step may fail to compile later with «type not found». Schemas with file `$ref`s are the likely case.
22. **Frontend:** the editor is a textarea (no highlighting or completion); diagnostics appear after `Compilar`/`Validar`, not while typing. One Playwright run in four read the «Resultado» tab before the result arrived (the run had completed: the events tab and the API showed it); not caused by C#, not investigated.

## Phase D (NuGet per package and per extension, 2026-10-19)

23. **The resolver is a simplification of NuGet's, and it can choose differently.** It picks the lowest version that satisfies every range and, on a diamond, the highest minimum; NuGet's «nearest wins» and its constraint solver (`NuGet.Resolver`) are not used. A package graph that NuGet resolves one way can resolve another way here (usually the same). The lock makes the outcome explicit and reproducible, but a `restore --force` against a feed that gained versions can change it.
24. **Running may download.** A trusted run (and every CLI run) fetches a locked package missing from the cache, by its pinned SHA-512, from the configured sources. It never resolves ranges, but it does touch the network (or the folder) while «running». The alternative, failing until someone runs `restore`, would make a package restored on one machine unusable on another without a manual step; with no source configured it fails with the reason.
25. **A package that is not trusted still shows its NuGet declarations in the trust dialog before anything is fetched**, but nothing stops a user from trusting a lock whose hashes they cannot verify: the hash proves the file is the one the lock names, not that the file is safe. Package signatures (`NuGet.Packaging` can verify them) are not checked, and the audit suppression for `System.Security.Cryptography.Pkcs` exists because of that library's dependency, not because the designer uses it.
26. **Authenticated feeds and the user's `nuget.config` are not supported.** Sources are plain URLs or folders; credentials and the user's NuGet configuration are never read (on purpose: they are secrets and per-user state), so a private feed cannot be used yet. A source with an unreachable host fails the restore with the host's message; there is no per-source timeout setting.
27. **Unloading the unit's load context does not guarantee that the library is released.** Libraries are loaded from bytes into the collectible context of each unit; a library that starts a thread, a timer or keeps a static event handler to a host object stays alive until the process ends (the same limit as objection 15). Native libraries loaded through `NativeLibrary.Load` are never unloaded by .NET at all.
28. **The shared set is decided by name, not by contract.** A package that depends on a *different major* of `Newtonsoft.Json` than the host's gets the host's one and a warning; there is no way to give one package its own copy, and a library that breaks on the host's version will break at run time, not at restore. Types shared by identity (`IStep`, `PipelineContext`) are only safe because `AITaskAgent.*` is always the host's.
29. **`CSharpStep` still cannot use a package that needs an assembly outside the compile allowlist transitively.** The compile references are the allowlist plus every assembly of the lock; a library whose public API exposes a type from an assembly the host has but the allowlist leaves out (`System.Net.Http`, for example) (inferred, not tried) would compile only if the code does not touch that type; the error would be the compiler's.
30. **The extension side is parsed but not wired.** `ExtensionNuget.Parse` and the resolver are ready for phase E; the dynamic loader must decide where an extension's lock lives (a file next to `extension.json` is the natural place) and use its own load context, because the package's unit context does not apply to extensions.

### Phase E: dynamic loading of extensions (2026-10-19)

31. **No hot unload, and a restart is how an extension changes.** The load context is not collectible (the catalog, the YAML reader and the step registry keep the extension's `Type`s for the host's lifetime), so replacing a DLL has no effect until the host restarts, and a second workspace opened with a different build of the same DLL loads a second copy that is never freed (the process cache is keyed by path and hash). Fine for a local backend and a CLI that runs one command; not for a long-lived multi-user service.
32. **Loading an extension executes it, and `<Workspace>/extensions` is discovered automatically.** Static constructors and `IExtension.Register` run when the host starts (or when a workspace is opened: the workspace host builds a new catalog), before any dialog can ask anything. A workspace received from someone else (a cloned repository, a shared folder, a zip unpacked there) with an `extensions/` folder therefore runs that DLL with the backend's permissions and secrets in memory the moment it is opened. Study §2.2 recommended this convention and the user accepted dynamic loading, so it is built, but the safer default would be opt-in per workspace (a one-time «this workspace has extensions: load them?» that remembers the hashes) or no auto-discovery of the workspace folder at all. `AutoDiscoverExtensions=false` is the switch today; the CLI has no equivalent flag (it only reads the folder when `--workspace` is given).
33. **The `Extensions` section of `config.json` is not masked, encrypted or part of the trust hash.** An extension's configuration can hold a secret (an API key) and it would be saved in clear and exported with the package; a foreign package can also change an extension's settings without the user being asked again, because the trust hash covers the extension's DLL but not its configuration. The decision was that an extension's configuration belongs to the extension; the secret handling of `Mcp` headers (`enc:v2:`) was not generalized to it.
34. **`min` is the version installed when the workflow was last saved**, which may be higher than what the package really needs (a package saved once with 2.0 of an extension will refuse 1.9, though it would run), and `sha256` is never written automatically. The author can lower `min` by hand in the manifest; nothing in the UI edits `requires.extensions`.
35. **An unknown tag cannot be tied to the extension that provides it** unless the package has exactly one requirement. With several, the message lists every missing extension. Mapping tags to extensions would need the extension's manifest to list its tags (it does not, to avoid a second source of truth) or the zip to record `type → extension` (it does not: it records only names).
36. **A package opened without its extension is editable**, not read-only (the round trip is lossless, see the deviations). The remaining risk is the editor: an unknown step has no parameter form, and the user can still delete or rename it. Derived requirements keep the entry of a missing extension, so saving does not forget it, but they also cannot be removed from the designer while the extension is not installed.
37. **The compatibility check is the integer `contract` only.** The assemblies of the framework have no version policy (every build is `1.0.0.0`), so an extension compiled against a different build of `AITaskAgent.Abstractions` than the host's is not detected at load: it fails when a member is missing (`MissingMethodException` inside `Register` or at run time), reported as `register-failed` or as a step error with the loader's message.
38. **The tag clash is detected by the text of the framework's exception** (`StepRegistry` says «is already registered»); a change of that message would turn `tag-conflict` into `register-failed`. A typed exception in the framework would remove the dependency; the framework was not touched.
39. **A slow or stuck extension blocks the host's start.** `Register` exceptions are caught; a static constructor that never returns, or a `Register` that blocks, is not (there is no timeout and no separate thread). Loading is synchronous inside the first `StepCatalog.Create`.
40. **The NuGet closure of an extension is materialized offline from the cache.** A machine that never ran `extension restore` (or `package restore`-style fetch) with network has no cache and the extension is reported `nuget-failed`; the cache folder is the CLI's default (`%LOCALAPPDATA%\WorkflowDesigner\data\nuget`) for the standalone commands and `<data>/nuget` of the workspace for the Web, so an extension restored with the CLI is not visible to a backend with another data folder unless `--nuget-cache` points to the same place.
41. **No sandbox, as study §4 said.** An extension reads the master key, every decrypted provider key and any file. The trust dialog says so and shows the DLL's hash; it cannot say what the code does. Out-of-process extensions remain the only real mitigation and are out of scope.

42. **Reading of «`proto`» (phase F).** The prompt glossed it as an extension of the prototype; study A says «add a `proto` integer to `ready` and to `run`», which is what was built (protocol version 2, worker and host refuse each other on a mismatch). If a prototype-level extension of the JavaScript base object (`Object.prototype`/utilities) was meant, it is not built: say so and it is a small bootstrap change, with the risk that every script then shares mutated built-ins.
43. **`nextStep` builds only steps made of properties.** The script's `nextStep` goes through `IStepRegistry.Create(tag, config, services)`, as `CSharpStep` does; the container keys of the YAML factory (`steps`, `cases`, `default`, `dependsOn`, `hooks`, `validators`) are not resolved, so a dynamic `GroupStep`/`LoopStep` does not get children. Building full definitions needs `AITaskAgent.Yaml`'s factory, which the Script extension does not reference; adding it would couple the extension to YAML.
44. **Dynamic steps are marked, not nested.** The run table shows a row whose id is not in the workflow as «dinámico» (indented) but cannot put it under the step that created it: events carry no parent step id. A `ParentStepId` in the step events is a framework change that nobody approved.
45. **The grant is per package request and host, not per step.** A step's `allow` can narrow what the package requests, never widen it; two ScriptSteps of the same package that need different functions share the package request. `file.exists` answers `false` (not an error) outside the roots or without roots, so a script cannot tell the two apart; `file.readText` does fail with «outside the folders». Contract §18.3 said «every call is an error without roots»: this is the deviation.
46. **`file.readText` is path-check then read.** The canonical path and the reparse-point check (symlinks/junctions anywhere under the root) happen before the read, so a link created in between wins (TOCTOU); hard links are not detected; case rules on Linux are the filesystem's. It is the same family of gaps as FileTools (RETOMAR §7). The default (no roots) is the safe configuration.
47. **Host functions run with the host's permissions and secrets in memory.** The worker is sandboxed, the function behind `host.x` is not: `config.get` reads only what `Script.Config` exposes, but a host that registers its own `IScriptHostFunction` hands the script whatever that function does. The trust dialog lists the requested functions and whether the host grants them; it cannot judge a custom one.
48. **Static syntax check costs a worker.** `ScriptSyntax.CheckAsync` starts one worker (~0.8 s) per distinct set of script texts; the designer caches the result by hash in memory, so repeating a validation of unchanged scripts is free, but every edit of a script or library in the editor costs one start on the next validation. A debounce in the frontend is advisable if validation runs automatically.
49. **Libraries compile at start, not lazily.** Every library in `uses` is compiled before the script runs (so a syntax error in one shows even if the script never `require`s it, under the name `lib:<name>`); only its execution is lazy, on the first `require`. Contract §18.2 said lazy compilation: a lazily compiled library would need one more round trip to the host or a second compile inside the worker.
50. **`Script.Config` is plain text in `config.json`.** Same as objection 33: the section is not masked or encrypted and not in the trust hash (it is read by `config.get` in a trusted run); a secret there travels in the zip. It is meant for non-secret switches.
51. **Native dates and time zones.** The worker runs with `InvariantGlobalization`, so IANA zone ids (`Europe/Madrid`) are not reliable on Windows and system ids (`Romance Standard Time`) not on Linux; `UTC` and fixed offsets (`+02:00`) always work. A DST-aware portable zone would need ICU in the worker.
52. **A pre-existing flaky test, not caused by this phase.** `ScriptIsolationTests.A_long_native_call_is_stopped_by_the_timeout_and_the_worker_is_gone` died once in a full run with exit code `-1073741819` (access violation: the worker's timer calls `engine.Interrupt()` during one long native regex) and passed in four other runs; the assertion expects the «500 ms» message, a crash is reported differently. The test classes of the Script extension now share a collection (`ScriptWorker`) because `A_missing_worker...` sets a process-wide environment variable; the suite runs sequentially (~30 s).
53. **Hints, not guarantees.** `host.x.y(` and `require('x')` mentions are found by regex, so dynamic access (`host['file']['readText']`, `require(name)`) is invisible to the warning; the guarantee is at run time (the worker defines only what `allow`/`uses` list). A script can also read the `__readNext` global that exists to return the queued steps: harmless (a read-only function over the queue).
54. **A race in the sample or the framework, found by the parity test and not caused by the variant.** With several auditors in parallel, the **original** pipeline sometimes fails one auditor with `Collection was modified; enumeration operation may not execute.` The stack goes through `JsonSerializerInternalWriter.SerializeDictionary → SerializeObject → SerializeList → List<T>.Enumerator.MoveNext`: a `List<T>` that one branch serializes while another mutates (the branches share `Metadata` and `StepResults`, and `ParallelStep` gives each a *shallow* copy of the input). It cost 1-2 of 9 reports in 6 of 10 combined runs of the test (never when the original ran alone in a fresh process), and the synthesis then says «1 auditor(es) fallaron». Which object it is was not located. The parity tests run with `MaxParallelAuditors = 1` (original) and `maxDegreeOfParallelism: 1` (package); **the package ships with 3**, so the same failure can hit it: a failed auditor degrades to a missing report, it does not stop the run. Not fixed: it is framework or original-sample code.
55. **Java enrichment does not work in the variant unless `JavaAnalyzer.java` is copied beside the host executable.** The original runner looks for it at `AppContext.BaseDirectory/Enrichment/Java/JavaAnalyzer.java` and `JavaAnalyzerOptions` has no path option (only the request type has `AnalyzerSourcePath`). Adding the option means editing the original sample, which this phase must not do. `.NET` enrichment works: `DotNetEnricher:CliPath` defaults to the CLI inside the extension folder (the folder is published to the extension by `ExtensionHost`, architecture §19.3).
56. **What the variant does not host** (study B §3.3, by design of the test): preparation by subsystem (`DomainDiscovery`, `CoveragePlanning`, `TimeBudget`: they need the factory's `AuditorStepRegistry` and the *same objects* as the report steps, impossible when the YAML factory builds them), mode B (needs F5 `ForEachStep`), the shared pass, the note protocol (`tagHandlers` by name, objection 3 of phase A), the verification and regeneration pipelines. The package is the factory's `Preparation:Mode = Off` pipeline. `CaInventory` is registered and tested alone but has no effect with `Mode = Off`.
57. **The synthesis stays one adapter, and it needs `ParallelResult.AddResult`, which is `internal`.** The original `ExecutiveSynthesizerStep` takes `SingleReportResult` branch results and does the digest, the LLM call, the write and the graph re-incorporation as one unit; splitting it into a digest step + a YAML `LlmStep` + a write step would mean copying ~300 lines. `CaSynthesis` rebuilds the results from the files the auditors' hook wrote and calls `AddResult` by reflection. A public way to build a `ParallelResult` (or a constructor taking the dictionary) would remove the reflection: framework change, not approved.
58. **Statics are contained, not removed** (study D3). `FileToolsConfiguration` is a private copy in the extension's load context (`FileTools.dll` is not in the shared set), so it does not collide with the designer's own file tools and `FileToolsScope.Apply`; two runs of the extension share it, plus `DesignerRun` and the original's `ClearAllPools`. `MaxConcurrentRuns = 1` is what keeps it correct. A consequence: when the package enables the designer's `FileTools`, the extension's `view_file`, `grep_search`… win the name clash (registered first) and use the extension's roots (the repository), not the designer's.
59. **What a YAML auditor does not do that `CustomStatelessTemplateLlmStep` did.** The per-call progress lines (`🤖 [LLM] Iniciando auditoría…`, `✅ [LLM] Completado…`) are not emitted (the designer shows the engine's own events); the role's `SystemPrompt` override and `Auditors:MaxToolIterations[ByRole]` are replaced by the fixed `systemPrompt`/`maxToolIterations` of the YAML; the timeouts are the YAML ones (`AnalysisTimeouts` is only read by the inner steps that read it themselves); a failed auditor keeps the original retry rules of the framework, not the sample's. The parity test lists the log lines only the original says and asserts they are exactly the `[LLM]` ones.
60. **Trust gates validation, and the editor logs a 403 until it is granted.** A package that uses a loaded extension asks for trust before **Validar** (the extension's code runs in the backend while the pipeline is built), and opening it makes the editor request `GET /packages/{id}/tools`, which answers `403 untrusted_package` (one red line in the browser console) until the user trusts the package. Harmless (the package opens, the dialog explains), but it is noise from phase E's design.
61. **The example's `requires.extensions` is written by hand.** The designer derives it when it saves a workflow from the Web; the example comes from `workspace-src/` through `package pack`, so the manifest was edited. `ExampleWorkspaceDeterminismTests` and the migration tests skip an example that needs an extension (`ExampleSources.NeedsExtension`): it only validates where `CodebaseAnalysis` is installed.
62. **`ToolCatalog` now lists the tools that extensions register as `ITool`** (source `Extension`), by building a throw-away engine collection once per catalog. Before, `tools: [name]` could name only file tools and MCP tools, so an extension could not offer tools to its YAML `LlmStep`s without the designer rejecting the name (`tool 'x' is not available`). Side effect: a tool registered by `ConfigureEngineServices` of the host is listed too.
