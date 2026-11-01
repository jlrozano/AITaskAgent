# Report on the v2 restructuring (2026-10-17)

Done on the `restructure-v2` branch (295 files changed, +2505 −2285 according to `git diff consolidation --shortstat -M`). **Integrated on 2026-10-17** into `consolidation` (commit `23403ac`, not pushed); the branch and the worktree were deleted.

**User decisions on §6 (2026-10-17):** 1 delete `IPipelineContext` · 2 accepted · 3 `dependsOn` stays · 4 clean up `YamlLlmStep` · 5 yes, with hard ceilings · 6 isolate `ScriptStep` in a separate process · 7 redraw · 8 CodebaseAnalysis is migrated in `consolidation` · the JSON serialization failure is discarded. **Done on 2026-10-17 (not committed): 1, 4, 5 and 6**; 2 and 3 required no changes; 7 (redraw) and 8 (migrate CodebaseAnalysis) remain. Below, §2, §4 and §5 keep the text of the original delivery; where something changed, a note marked «Updated» says so.

## 1. Result

The full plan was carried out, in an order different from the one proposed (see §3): decoupled context, five new projects, a single step registry with extensions, YAML v2 without Roslyn, `Script` extension with ClearScript, documentation, an independent review and a round of fixes.

Final state, checked by me after all the changes (build `dotnet build AITaskAgent.slnx`, 0 errors; tests `--no-build` per project):

| Suite | F0 (before) | Final |
|---|---|---|
| Tests | 183 + 2 skipped | 238 |
| GeminiLlmService.Tests | 12 | 12 |
| OpenAILLmService.Tests | 118 | 118 |
| OfficeTools.Tests | 32 | 32 |
| CodebaseAnalysis.Core.Tests | 1533 | 1532 + 1 flaky (see §5) |
| CodebaseAnalysisWeb.Tests | 148 | 148 |
| DotNetEnricher.Tests | 81 | 81 |
| Extensions.Script.Tests (new) | — | 36 |

Prohibited and not executed: any test with a real LLM. `McpIntegration.Tests` and `FileTools.Tests` do not run on this machine (as in F0).

## 2. Resulting Architecture

`Abstractions ← Core ← Extensions.Llm`; `Yaml` depends only on Core; `Extensions.Script` depends only on Core (ClearScript lives only there). FileTools, OfficeTools and McpIntegration are registered through `IExtension`. Dependencies and the "before/now" table are in `docs/ARCHITECTURE-v2.md`; the YAML v2 grammar is in `docs/guides/yaml-pipelines.md`; how to write an extension is in `docs/extensibility/custom-extensions.md`.

## 3. Deviations from What Was Agreed

1. **Phase order inverted.** Projects cannot be split while `Core` depends on `LLM` (a cycle): first the context was decoupled and only then was the split done.
2. **`IPipelineContext` exists but no signature uses it.** The signatures of `IStep`, middlewares and `IPipeline` still receive the base class `PipelineContext` (moved to Abstractions, `HttpContext` model). Changing them touched ~520 occurrences in 182 files, with casts inside the engine. This is the most relevant deviation from "interface + inheritance": **your decision** (§6). **Updated 2026-10-17: the interface was deleted** (`IPipelineContext.cs`, its `: IPipelineContext` and its explicit implementation of `CloneForBranch`); the `PipelineContext` class and inheritance remain.
3. **`ParallelStep` changes:** each branch receives a copy of the input (`StepResult.CopyForBranch`). It fixed a previous bug (two branches shared the `NextSteps` of the same object), but it was not requested and it changes the semantics for branches that modify their input [I: nobody in the repo depends on that].
4. **Public contract changes:** `StepManager` (static), `[YamlStepTag]`, `IYamlStep`, `SchemaCompiler`, `StepBase.InputSchema/OutputSchema` and `PipelineContextFactory.Create(conversation)` (now `LlmPipelineContextFactory.Create`) disappear. `StepBase.Name` becomes `protected internal set`. `StepBase.Schema` is `virtual`.
5. **`StepRegistry.Describe` only takes properties with a public setter** (`StepManager` also included protected and private ones).
6. **Null = "not set"** in step configuration (`~`, `null`, empty keep the default). Previously, on reference types, it assigned null.
7. **`when: ""` in `BreakStep` fails at construction** (previously it broke the loop on the first pass).
8. **`SchemaCompilerJsValidationTests` was deleted** (3 tests, 2 skipped). JS validation of schemas was not reimplemented: the JS now lives in `ScriptStep`.
9. **`LlmExtension` registers 0 steps by scanning** except `YamlLlmStep`: the others are open generics or need constructor arguments, and the registration cannot create them.
10. **New types not requested:** `LlmJsonStepResult` (so as not to lose `AssistantMessage` or metrics), `DeclarativeSwitchStep` (`[StepTag("SwitchStep")]`), `SchemaRegistry`, `JsonStepResult` (in Core).

## 4. Assumptions Taken Without Asking

- YAML v2 is flat (`id`, `type` and properties next to them), as in BRMS; no `with:`.
- `dependsOn` only at the first level; nested content is sequential inside its container. Ids are unique across the whole document, children included.
- A multi-step branch without `type` and with `steps:` is an implicit `GroupStep`.
- `ScriptStep` contract: the body of `function(input, context)`, returning with an explicit `return`; `timeoutMs` defaults to 5000; heap 64 MB and stack fixed (not configurable from YAML; **updated 2026-10-17:** `maxHeapMb` from 8 to 128 and `maxStackKb` from 64 to 2048, with those hard ceilings, and each execution in its own process); `context.get/set` over `PipelineContext.Metadata`, JSON values only.
- YAML aliases: maximum depth 64, cap of 200,000 nodes, cycles rejected.
- The simple samples compile and start up to the LLM call; CodebaseAnalysis only received minimal compilation patches (3 lines and a test helper), it was not migrated.
- The demo schemas carry `additionalProperties: false`.

## 5. Risks and Debt

- **Residual P1, `ScriptStep`.** [V, `ScriptStep.cs:37-56`] A large reservation made by a single V8 builtin cannot be rejected before it completes: with `new Array(1e8)` ~4 GB of working set and 22 s were observed, and not even the timeout cuts it off. The cases that previously aborted the process (3e7, a 1 GB `ArrayBuffer`) now produce a step error. Only isolating the script in another process closes the risk. **Untrusted scripts must not be executed**; this is stated in the documentation. **Updated 2026-10-17: closed by isolation.** Each execution runs in `AITaskAgent.Extensions.Script.Worker`, with a process memory cap (job object on Windows, watchdog on other platforms) and termination on timeout or cancellation; the case of three chained `new Array(3e7).fill(...)` gives a step error and the host goes from 20 to 21 MB. The limits in the worker process remain the usual ones plus the process one; what no longer exists is a risk to the host. Cost: ~0.8 s per execution on this machine. The Linux path has not been run.
- **Flaky test.** `FacetExtractionTests.Unknown_fan_in_and_auth_are_not_presented_as_zero_or_unprotected` failed once in the full Core.Tests suite (2 min 30 s) and passed 2 out of 2 in isolation, and the agents' runs gave 1533/1533. It seems to depend on load [I]; it is not in the code that was touched, not investigated.
- **Designer** does not restore (NU1605, NJsonSchema versions) and is not in the `.slnx`. It already failed on `consolidation`.
- **OpenAI strict mode** requires all properties in `required`, and the sample schemas have optional ones: not checked with a real provider.
- **`InternalsVisibleTo`** couples the assemblies (Abstractions→Core/Llm, Core→Llm/Yaml, etc.); no member was made public.
- **`IExtension.ConfigurationModel()`** has no consumers: it stays as an unused hook point, documented as such.
- **Docs not reviewed:** `docs/guides/steps.md`, `docs/getting-started/architecture.md`, `docs/Funcional.md` and `docs/functional_en.md` still cite Switch, `When` or `RequiredContext` and were not checked against the latest fixes. `new-project.md` says each command «was run against this repo» and, after changing paths, it is no longer literal. A code block in `interfaces.md` was split by a note.
- **Diagram `yaml-schema-compilation`** (`.excalidraw` and `.png`) redrawn for the v2 flow (merge, JToken, `Describe`/`Create`, `SchemaRegistry`, topological order); no document references it yet.
- **Git CRLF→LF warnings** in several edited .cs files: line endings should be unified before committing.
- **The JSON serialization failure** (long multi-line strings with special characters) has not been touched: there is still no reproducible case. v2 does not fix it.
- **CodebaseAnalysis (Core, Pipeline, Web, Tests)** is left for its phase: it compiles, but it has not been tested with a real LLM nor migrated to the extensions.

## 6. Questions for You

1. Do we leave `IPipelineContext` as it is (unused), delete it, or change the signatures to the interface despite the cost (~520 occurrences)? I recommend deleting it if you will not want contexts that do not inherit from `PipelineContext`. **Resolved: deleted.**
2. Do you accept the per-branch copy in `ParallelStep` and the "null = not set" semantics? **Resolved: accepted.**
3. Do we also validate `inputSchema` in intermediate YAML steps? Today it is only checked at the pipeline input and in the `dependsOn` crossing. **Resolved: not touched; `dependsOn` stays** (it orders the first-level steps and checks that the `outputSchema` of one matches the `inputSchema` of the one it depends on).
4. Do we hide in `YamlLlmStep` the inherited properties that are useless in YAML (`prompt`, `tools`, `enrichable`)? **Resolved: hidden** (`prompt`, `tools`, `tagHandlers` and `enrichable` are no longer accepted in a `LlmStep`: unknown property error with the list of valid ones; `maxRetries`, `timeout` and `maxToolIterations` keep working).
5. Do I expose `maxHeapMb` and `maxStackKb` in the YAML of `ScriptStep`? With the residual risk, I would not do it until the process isolation is decided. **Resolved: exposed, with hard ceilings of 128 MB and 2048 KB, together with the isolation.**
6. Do we isolate `ScriptStep` in a separate process (closes P1) or leave it only for trusted scripts? **Resolved: isolated** (one worker process per execution).
7. Do I delete or redraw the v1 diagram? **Decided: redraw (pending).**
8. How do we integrate: you review the diff and I commit to `restructure-v2` (`refactor:`/`feat:` convention, several commits per phase), and the CodebaseAnalysis migration goes in a following branch? **Resolved: one commit (`23403ac`) merged into `consolidation`; CodebaseAnalysis is migrated in `consolidation` (pending).**

## 7. How to Reproduce

```
cd C:\GeminiCli\AITask\AITaskAgent
dotnet restore AITaskAgent.slnx --ignore-failed-sources -p:WarningsNotAsErrors=NU1801
dotnet build AITaskAgent.slnx --no-restore
dotnet test Tests --no-build
```

The logs per phase (with `file:line` evidence) are in `C:\Users\juan.rozano\AppData\Local\Temp\claude\C--GeminiCli-AITask-AITaskAgent\f514d024-e59c-4f77-86b3-23a2ef927574\scratchpad\restructure\`: `log-F0.md` … `log-F5.md`, `log-F4a.md`, `log-F4b.md`, `log-review.md`, `log-fixes.md`, `log-docs-patch.md`.
