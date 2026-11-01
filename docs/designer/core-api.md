# WorkflowDesigner.Core: API for Web and CLI

Summary of what `Samples/WorkflowDesigner.Core` exposes (2026-10-18) for anyone building `WorkflowDesignerWeb` and
`WorkflowDesigner.Cli` on top of it. The general contract remains in `architecture.md`; this file describes what the Core
actually does, with signatures, exceptions and formats. Reference tests: `Samples/WorkflowDesigner.Core.Tests`.

**Packages (P1a, 2026-10-18):** the flat workspace (`environment.json`, `workflows/*.yaml`...) no longer exists in the Core.
Each workflow is a `.zip` package with its own configuration; the package API is in **§9** and replaces what §1, §5, §6, §7 and §8
say about the workspace and `environment.json` (each section marks where it changes). `config.json` is the former
`environment.json`, with the same shape. Portable encryption (`enc:v2:`, master password), the configuration library and step
templates (P2 and P3): **§10**.

## 1. Registration

```csharp
services.AddWorkflowDesigner(new WorkflowDesignerOptions
{
    Workspace = "...",              // por defecto <AppContext.BaseDirectory>/workspace; contiene workflows/<n>.zip y library/
    DataDirectory = null,           // datos de ejecución (§9.1); null = <Workspace>/data
    SuspendedRunRetention = null,   // null = los registros de suspendidas no caducan; un TimeSpan los purga al crear el RunManager
    MaxConcurrentRuns = 1,          // una más => RunLimitExceededException
    MaxRetainedRuns = 20,           // ejecuciones terminadas que se recuerdan (estado, resultado, eventos)
    MaxEventsPerRun = 20_000,       // tope del log de eventos de cada ejecución
    RunTimeout = null,              // null = timeout de pipeline por defecto del framework
    ConfigureEngineServices = s => { /* extensiones, logging, un ILlmService propio... */ }
});
```

Registers as singletons: `WorkflowDesignerOptions`, `StepCatalog`, `YamlReader`,
`ISecretProtector` (with `TryAdd`: if the host or a test registers its own before, that one is used; by default
`SecretProtectors.ForCurrentUser()`; from P2 it only reads `enc:v1:`), `MasterKeyProvider` (`TryAdd`, §10.1), `PackageStore` (it is `IDisposable`: disposing the container releases the locks and leaves
working copies with changes so they can be recovered), `LibraryStore` and `SecretsManager` (§10), `ToolCatalog`, `WorkflowValidator`, `RunManager` (this one is
`IAsyncDisposable`: disposing the container cancels and waits for runs in progress), `SessionManager` and
`WorkspaceMigration`. There are **no longer** global `WorkspaceStore` or `EnvironmentStore`: each package has its own
(`PackageSession.Workspace` and `.Config`, §9.3). `YamlEmitter` is static. The host does **not** need `AddAITaskAgent`: the Core builds
its own engine collection for the catalog, for each validation and for each run (`AddAITaskAgent` +
`AddScriptExtension` + `AddAITaskAgentYaml` + workspace environment + `ConfigureEngineServices` last).

A host that has to **change workspace while it runs** (the web backend, architecture §13.5) does not call `AddWorkflowDesigner` itself: it creates a `WorkspaceHost` (§12) and asks it for the services.

Serialization: the DTOs carry explicit `[JsonPropertyName]` (camelCase) and use `System.Text.Json`
(`JsonObject`/`JsonNode` for JSON values). `WorkflowJson.Options` (Web defaults, nulls omitted) serves the
CLI; the minimal APIs can use their default options.

### What must be in the output directory

Nothing to copy by hand. The Core's csproj references `AITaskAgent.Extensions.Script`, which in turn references the
worker: `AITaskAgent.Extensions.Script.Worker.dll` (+ `.runtimeconfig.json`, `.deps.json` and ClearScript) arrives on its own in the
`bin` of any project that references the Core (verified in the tests' `bin`). If it is published with a different layout, the
variable `AITASKAGENT_SCRIPT_WORKER` points to the worker dll.

## 2. Catalog — `StepCatalog`

- `CatalogDto Catalog { get; }` → `{ steps: StepTypeDto[], plugins: PluginDto[], pluginProblems?, llm?, timeouts? }` (`plugins` = the loaded plugins, architecture §25.9; the built-in `IExtension`s are no longer listed).
- `StepTypeDto`: `tag` (the registry one, not renamed: `GroupStep`, `LoopStep`, `ParallelStep`, `BreakStep`,
  `SwitchStep`, `LlmStep`, `ScriptStep`, `ExecStep`), `description`, `category` (`flow` = Core steps, `llm`, `script`,
  `other`, per assembly; `system` for the steps that act on the host machine, a rule per type: `ExecStep`), `parameters` (JSON Schema from `IStepRegistry.Describe` **without** containers nor
  `id/type/dependsOn/inputSchema/outputSchema`; `required` cleaned the same way), `containers`, `structural`
  (`dependsOn` always; `inputSchema`/`outputSchema` only if the type implements `IJsonSchemaStep`, today only `LlmStep`).
- `ContainerDto`: `name`, `kind` (`list` | `map`), `description?`, `execution` (`parallel` for ParallelStep's `branches`, `sequential` the rest). Real containers: Group/Loop `steps` (list), Parallel `branches` (**list**:
  each element is a branch), Switch `cases` (map) and `default` (list).
- `x-editor` hint inside `parameters.properties`: `code:javascript` on `ScriptStep.script`, `template` on
  `LlmStep.userPrompt`/`systemPrompt` (the values the frontend uses).
- `ExecStep` (architecture §13.4): the catalog adds `enum` and `default` to `inheritEnvironment`, `type: array` with `items` to `args` (string) and `okExitCodes`
  (integer), and `x-literal: true` to `executable` (no templates). `env` is `object` with `additionalProperties: { type: string }`.
- Plugins: `StepTypeDto.plugin { id, version, origin }` names the owner of a step the engine does not own; a plugin's steps are category `other`.
- Also: `StepCatalog.Create(options)`, `Find(tag?)`/`TryGet(tag)` → `StepTypeInfo(Tag, Type, Containers,
  ParameterTypes)`, `StructuralKeys`.

## 3. Document and YAML

DTO from §4 of the architecture: `WorkflowDocument { Name, Steps }`, `StepNode { Id, Type?, DependsOn?, InputSchema?,
OutputSchema?, Properties: JsonObject, Containers?: OrderedDictionary<string, StepContainer> }`. `StepContainer` is
`Steps` (list) **or** `Map` (`OrderedDictionary<string, List<StepNode>>`); in JSON it is an array or an object
(custom converter). `Type == null` = implicit GroupStep.

- `YamlReader.Read(string yaml) → WorkflowReadResult(Document, LineMap, Warnings?, Yaml?)`. Throws `WorkflowYamlException`
  (`FormatException`) with `Errors: WorkflowError[]` (1-based line and column): malformed YAML, a root that is not a
  mapping, root keys other than `name`/`steps`, steps without `id`, `dependsOn`/schemas with a wrong shape, `!X` tags.
  Scalars follow the engine's rules (YAML 1.2 core). A plain number or boolean in a property that the step
  declares as text is stored as text (`script: 1.10` → `"1.10"`), which is what the engine would pass to it. Several
  documents (`---`) are merged as in the factory. Comments are not preserved.
  A step's `timeout` is read as whole seconds (`StepTimeout`): from a duration (`hh:mm:ss`, `d.hh:mm:ss`) or from an integer, which also adds a warning to
  `Warnings` and is rewritten in place in `Yaml` (the text to compile; `YamlFor(original)` returns it or the original). A value that is neither stays as read for the
  validator. `YamlEmitter` writes seconds as `TimeSpan.ToString("c")`. See architecture §13.3.
- `YamlEmitter.Emit(doc) → string` and `EmitWithMap(doc) → EmittedYaml(Yaml, LineMap)`. Key order: `id`, `type`,
  `dependsOn`, `inputSchema`, `outputSchema`, properties (document order), containers. Multiline as a `|`/`|-`/`|+` block when it fits exactly; otherwise double quotes with escapes. Scalar lists inline if they fit in
  100 columns. Guarantees (with tests, including 400 random texts): `Read(Emit(doc)) == doc` and
  `Emit(Read(Emit(doc))) == Emit(doc)`.
- `YamlLineMap`: `StepAt(line) → stepId?` (the innermost step), `LineOf(stepId, key?)`, `LineOf(StepNode, key?)`,
  `Spans`.
- `WorkflowError(Message, Line?, Column?, StepId?)` → `{ message, line?, column?, stepId? }`.

API composition: `GET /workflows/{name}` = `store.ReadWorkflow` + `reader.Read` + `store.ReadLayout`;
`PUT` = `YamlEmitter.Emit(document)` + `store.SaveWorkflow(name, yaml, layout)`; `from-yaml` = `reader.Read` (422 with
`ex.Errors`); `to-yaml` = `YamlEmitter.Emit`.

## 4. Validation — `WorkflowValidator`

- `Task<ValidationResult> ValidateAsync(IPackageFolder | string packageId, WorkflowDocument)`: lines refer to the YAML from `YamlEmitter.Emit(doc)`. Since P1a validation is always done **against a package** (its schemas, prompts and configuration): signatures in §9.4.
- `Task<ValidationResult> ValidateYamlAsync(IPackageFolder | string packageId, string yaml)`: lines of the text itself (for the CLI). Unreadable YAML
  comes back as a result with errors, not as an exception.
- `ValidationResult(Ok, Errors, Warnings)` → `{ ok, errors, warnings }`. It does not throw for workflow errors; it does throw for I/O.
- `Warnings` (`WorkflowWarning(Message, StepId?)` → `{ message, stepId? }`) is additive and never changes `ok` or `errors`:
  there is one warning for each step with a non-empty `dependsOn` («dependsOn puede reordenar la ejecución respecto al orden
  escrito (p. ej. A, B(dependsOn A), C se ejecuta A, C, B)» (Spanish UI/message text), also when there are errors. `dependsOn` is optional and
  inherited: the designer writes linear workflows (YAML order) and the Core keeps reading, emitting,
  round-tripping and validating them. Mixing steps with and without `dependsOn` reorders execution (`A, B(dependsOn A), C`
  runs `A, C, B`; pinned by `DependsOnOrderTests`).

Order: (1) own checks, returned all together, and then **no** compilation: empty, repeated or `/`-containing ids;
`inputSchema`/`outputSchema` not found in `workspace/schemas`; `LlmStep` role (`profileName` or the
`DefaultRole`) not present in `environment.json`; and, if there is any LlmStep, the environment errors. (2) Compilation
with the real `YamlPipelineFactory` in a disposable `ServiceProvider`, with the workspace schemas and environment; the LLM services are stand-ins with the `Name` of each `ServiceType` that fail if called (an LLM is never called).
The factory stops at the first error: one error per round. The `stepId` comes from the line map; if the message carries no line, from the first `Step 'x'` in the message (and that step's line, or its `dependsOn`'s line for dependency errors).

## 5. Package folder — `WorkspaceStore` and `ConfigStore` (P1a: formerly `EnvironmentStore`)

`WorkspaceStore(root, readOnly = false)` is the view of **a folder with the structure of an extracted package** (a working
copy, or the folder where a package was extracted to run it): `Root`, `WorkflowPath`, `LayoutPath`, `ConfigPath`,
`SchemasDirectory`, `PromptsDirectory`, `ReadOnly`. It knows nothing about zips (that is `PackageStore`, §9). With `ReadOnly`, every write throws `PackageReadOnlyException`.

| Method | Notes |
|---|---|
| `WorkflowExists()`, `ReadWorkflow() → string` | `workflow.yaml`; `FileNotFoundException` if it does not exist |
| `ReadLayout() → WorkflowLayout?` | `layout.json`; null if missing or not valid JSON |
| `SaveWorkflow(yaml, layout?)` | layout null = the existing one is not touched |
| `ListSchemas`, `ReadSchema`, `SaveSchemaAsync(name, json)`, `DeleteSchema` | `SaveSchemaAsync` requires a JSON object that NJsonSchema accepts; otherwise `ArgumentException` |
| `ListPrompts`, `ReadPrompt`, `SavePrompt(name, text)`, `DeletePrompt` | **`.md`** files |
| `static IsValidName`, `static ValidateName` | |

(`ListWorkflows`, `DeleteWorkflow`, `WorkflowInfo`, `WorkflowsDirectory` and `EnvironmentPath` are gone: listing and
deleting belong to `PackageStore`.)

Names: `[A-Za-z0-9._-]+`, max. 100, no `..`, no trailing `.`, no Windows device names (`CON`,
`NUL.txt`, `COM1`, `LPT9.x`...), and the final path must stay inside its folder. Otherwise:
`InvalidWorkspaceNameException` (`ArgumentException`, 400). Atomic writes (temporary file + `File.Move`), UTF-8 without BOM.
`WorkflowLayout { version, positions: { id: {x,y} }, viewport: {x,y,zoom}, collapsed: [] }`.

`ConfigStore(workspace, protector)` (and `ConfigStore(workspace)`, with the current user's protector) reads and writes the
`config.json` of the folder. `EnvironmentStore` remains as an `[Obsolete]` class that inherits from `ConfigStore` (temporary alias). New statics: `ConfigStore.DefaultConfig()` (no providers or roles). The **`Secrets`** section
(master password verifier, P2) is preserved as is on save: `Save` ignores the one sent by the client and keeps the
stored one; `Read` returns it and the engine does not see it (`EnvironmentSections.Names` includes it). What follows in this section
(format, `Read`, `Save`, `enc:v1:` encryption) holds equally with `config.json` instead of `environment.json`:

- Format of `environment.json`: **PascalCase**, the exact shape of the `AITaskAgent:LlmProviders` section
  (`Providers`, `Roles`, `DefaultRole`, `FallbackProviders`; inside: `ServiceType`, `BaseUrl`, `ApiKey`,
  `ApiVersion`, `Models[{Name, ...}]`, `Inference`, `Provider`, `Model`, `SystemPrompt`, `Fallbacks`). The Core passes it to the engine
  as that section, so the framework expands `${VAR}`. Keys are looked up case-insensitively.
- `JsonObject Read()`: every stored key (encrypted `enc:v1:` or plain text written by hand) is replaced by `EnvironmentStore.Mask`
  (`"********"`); `${VAR}` values and empty ones are not. It decrypts nothing. No file: `{ Providers: {}, Roles: {}, DefaultRole:
  "default" }`. Broken JSON: `InvalidDataException` (with line and position, without quoting file text).
- `void Save(JsonObject)`: per provider, `ApiKey == Mask` keeps the stored value of the provider with the same name (if
  it was plain text, it encrypts it); `${VAR}` is written as is; `enc:v1:` is kept if it decrypts; any other text is
  encrypted (`enc:v1:<base64>`). It validates and writes atomically (readable UTF-8, accents not escaped).
  `EnvironmentValidationException` (`ArgumentException`, with `Errors: string[]`) if: a provider without `BaseUrl`, a role without
  `Provider`/`Model`, a role or fallback naming a non-existent provider, a model the provider does not list (only if it lists `Models`),
  a non-existent `DefaultRole` when there are roles, a masked key without a stored key with that name (a renamed provider: the message asks to re-enter it), an `ApiKey` that is not text, or an `enc:v1:` that does not decrypt. It does not check that the `ServiceType` exists (that fails at run time).
- `Storage`, `FileTools` and `Mcp` sections (architecture §10.1, detail in §7): `Save` validates them
  (`EnvironmentSections.Validate`) and treats the values of `Mcp[].Headers` and `Mcp[].EnvironmentVariables` like
  `ApiKey` (encrypted `enc:v1:`, `Read` returns `********`, `********` keeps the server's stored value with the same
  `Name` and the same key —if there is none, 422 asking to re-enter it—, `${VAR}` and empty stay as they are). An
  `environment.json` without those sections is still valid: in-memory stores, FileTools enabled over the working directory, no MCP. The engine receives in `AITaskAgent:LlmProviders` the environment **without** those three sections.
- `internal ReadForEngine(bool includeMcpSecrets = true)`: the environment with the keys decrypted, **in memory only**, for the engine of a validation
  or a run (`WorkflowValidator` and `RunManager` use it through `WorkflowCompiler.EngineEnvironment`). A key that does not decrypt throws `ApiKeyDecryptionException` (clear message, without the value), which only matters if the workflow
  has LLM steps: validation returns it as an error and `RunManager.StartAsync` as `WorkflowValidationException`.
- Statics: `Validate(env) → string[]`, `DefaultRole(env)`, `RoleNames(env)`, `IsVariable(value)`,
  `EncryptLiteralKeys(env, protector)` (encrypts in place the plain-text keys of a copy; the CLI uses it with `--env`).

Encryption (`Workspace/SecretProtection.cs`): `ISecretProtector { byte[] Protect(byte[]); byte[] Unprotect(byte[]); }`;
`DpapiSecretProtector` (Windows, `CurrentUser`, the application's own entropy); `AesGcmSecretProtector` (AES-256-GCM,
`nonce(12)|tag(16)|ciphertext`, random nonce per value; `FromKeyFile(path)` creates the 256-bit key on first use
with `0600` permissions where the OS supports them); `SecretProtectors.ForCurrentUser()` picks DPAPI on Windows and otherwise
`AesGcmSecretProtector.FromKeyFile(SecretProtectors.DefaultKeyFile)` (`~/.config/WorkflowDesigner/secret.key`);
`ApiKeySecrets.Encrypt/Decrypt/IsEncrypted` and `Prefix = "enc:v1:"`. What it protects: the file on disk, workspace copies, the repository and API responses. What it does not: a process running as the same OS user can decrypt (DPAPI and the master key belong to that user), and in memory the key is in clear text during the run.

## 6. Execution — `RunManager`

```csharp
Task<string> StartAsync(RunRequest request, CancellationToken ct = default); // { PackageId | PackageFile | PackageDirectory, Source, Document?, Input: JsonNode?, Context? } — §9.5
Task<string> ResumeAsync(string runId, JsonNode? response, CancellationToken ct = default); // ejecución nueva (§7)
IReadOnlyList<SuspendedRunDto> ListSuspended();  // de disco, más recientes primero
bool DiscardSuspended(string runId);             // false si no hay suspendida con ese id; 409 si está en curso
RunInfo? GetRun(string runId);                 // null = desconocida o ya olvidada
IReadOnlyList<RunInfo> ListRuns();             // más recientes primero
RunEventLog? GetEvents(string runId);
bool Cancel(string runId);                     // false si no existe o ya terminó
Task<RunInfo> WaitAsync(string runId, CancellationToken ct = default); // KeyNotFoundException si no existe
```

`StartAsync` compiles **before** returning the `runId` and throws: `ArgumentException` (not exactly one of package/zip/folder given, or an invalid `context`), `PackageNotFoundException` (404), `PackageNotOpenException` (404, `Working` of a package without an edit session), `InvalidPackageException` (422; `PackageTooLargeException` 413), `WorkflowYamlException` (unreadable workflow YAML), `WorkflowValidationException` (`InvalidOperationException` with localized `Errors`, the same checks as validation) and `RunLimitExceededException` (`InvalidOperationException`, 409). Suggested mapping:
400 / 404 / 404 / 422-413 / 422 / 422 / 409. Details of which copy of the package each `RunRequest` executes: §9.5.

`RunInfo` → `{ runId, workflow?, packageId?, status: running|completed|failed|cancelled|suspended, startedAt, finishedAt?, result?,
error?, suspension?, resumedFrom? }` (`workflow` is the package's visible name when the run started). `Cancel` on a suspended run discards it (record and context) and returns true.
`result` is the value of the last step as JSON (an LlmStep without `outputSchema` gives a string). The `inputSchema` of the
first step is validated against `input`; if it does not match, the run ends `failed` without executing anything.

Each run (with the configuration, schemas and prompts **of its package**): its own `ServiceProvider` with the LLM services of the environment's `ServiceType`s that the Core knows
(`openai`, `openrouter`, `azure-openai`, `gemini`, with the same matching rule as the framework: equal, or a
name that contains it); any other is added through `ConfigureEngineServices`. `FileTemplateProvider` over
`<package>/prompts` without cache (a `systemPrompt: greeter` loads `greeter.md`, verified by test; what happens with a
name that does not exist is decided by the framework's `PromptTemplateResolver`, not the Core). Workspace schemas loaded into its `SchemaRegistry`. `LlmPipelineContext` created by the Core
with the run's channel (the static `PipelineContextFactory` is not used).

### Events — `RunEventLog`

- `Subscribe(long after = 0) → (IReadOnlyList<RunEventDto> Replay, RunEventReader Live)`: what is stored with `seq > after` and a `ChannelReader` with what arrives later; no gaps or duplicates between both. With the run finished, `Live.Reader` is already completed. `Dispose()` of the reader to unsubscribe.
- `Snapshot(after)`, `LastSeq`, `IsCompleted`. If `MaxEventsPerRun` was exceeded and the client asks for dropped events,
  the replay starts with a `log` event of level `warning` with `data.droppedUpToSeq` (its `seq` is the last dropped one).
- SSE: `id:` = `seq`, `event: run`, `data:` = the serialized DTO; `Last-Event-ID` or `after` → `Subscribe(after)`.

`RunEventDto` → `{ seq, runId, ts, type, stepId?, stepPath?, level?, message?, data? }`; `data` scalars only:

| type | origin | data |
|---|---|---|
| `run.started` | the Core, at start (seq 1) | `workflow`, `name` |
| `run.completed` / `run.failed` / `run.cancelled` | the Core, last event | `success`, `durationMs`, `error`, `errorType` (`run.failed`) |
| `step.started` | `StepStartedEvent` attempt 1 | — |
| `step.retry` | `StepStartedEvent` attempt > 1 | `attempt` |
| `step.completed` | `StepCompletedEvent` (level `error` if it fails) | `success`, `durationMs`, `error`, `errorType` (if it fails); if LLM `model`, `tokens`, `costUsd` |
| `step.timeout` | `StepTimeoutEvent` | `configuredTimeoutMs`, `errorType` (`Timeout`) |
| `step.progress` | `StepProgressEvent` | `eventType`, `current`, `total` |
| `branch.started` / `branch.completed` | ParallelStep events | `parallelId`; on finish `success`, `durationMs`, `error` |
| `llm.response` | final `LlmResponseEvent` (streaming chunks are discarded) | `model`, `provider`, `finishReason`, `tokens`, `promptTokens`, `completionTokens`, `costUsd`, `toolCalls`, `contentLength` |
| `tool.started` / `tool.completed` | tool events | `tool`; on finish `success`, `durationMs`, `error` |
| `log` | `log()`/`console.*` of a ScriptStep (`data.source = "script"`), `StepValidationEvent`, `LoopExhaustedEvent` and any other framework event (`data.eventType`) | |

`PipelineStarted/CompletedEvent` are discarded (the `run.*` events replace them). No event copies the value of a
result.

### Actual format of `StepPath` (verified in `Pipeline`, `ObservabilityMiddleware`, `LoopStep`, `ParallelStep` and by test)

The `StepPath` of the framework's step events is the path of the **parent**, not of the step: `""` at the first level,
`group` inside a group, `loop/Iter_2` inside the second iteration, `fan/left` inside branch `left` of
`fan`; the step id is `StepName`. That is why the Core gives `stepId = StepName` and `stepPath = parent + "/" + id`
(`prepare/totals`, `loop/Iter_1/count`, `fan/left/left`: the branch repeats its id because the framework adds
`{parallel}/{branch}` and then runs the branch step inside). `branch.*` carry `stepId` = the branch and
`stepPath` = `fan/left`. `llm.response` and `tool.*` carry no path (`stepPath` absent), only `stepId`.

Added events: `run.suspended` (last event of the run; `data`: `stepId`, `stepPath`, `marker`, `message`,
`dataToReview`, `rejectionReason`, `durationMs`; the SSE closes after it) and `run.resumed` (second event of a
resumed run; `data`: `resumedFrom`, `stepId`). Tool warnings (an MCP server that did not respond, an omitted folder, a
repeated name) arrive as `log` events of level `warning` with `data.source = "tools"`; a failure to save the conversation,
with `data.source = "conversation"`.

## 7. Extended environment, tools and suspension (2026-10-18, architecture §10)

### `environment.json` sections (`EnvironmentSections`, `Engine/EngineSettings.cs`)

- `Storage.Context.Type` ∈ `memory|file`, `Storage.Conversation.Type` ∈ `memory|file|yaml` (case-insensitive); `file`/`yaml` require `Directory`. A relative directory resolves against **`<data>/packages/<id>/`** (P1a; before, against the workspace); `${VAR}` is expanded. With
  `memory`, the Core uses its **own stores, shared by all runs of the process** (each run has its own
  `ServiceProvider` and the framework's `InMemory*` would die with it): contexts are saved as documents of
  `PipelineContextSerializer` and restored onto the context of the run that resumes. With `file`, the Core passes the
  section to the framework as `AITaskAgent:Storage` with absolute paths.
- `FileTools`: `Enabled` (default true), `RootDirectory` (empty = the process working directory; relative = against that
  directory), `AdditionalAllowedDirectories` (absolute after expansion; a relative one or one with an undefined variable is omitted with a warning). `${HOME}` falls back to the user profile if `HOME` is not defined (Windows).
- `Mcp[]`: `Name` (unique, case-insensitive), `TransportType` ∈ `Stdio|StreamableHttp|Sse` (case-insensitive), `Command` (Stdio) or `Url` (HTTP), `Arguments`, `WorkingDirectory`, `EnvironmentVariables`, `Headers`,
  `TimeoutSeconds` (1-600, default 30). All texts expand `${VAR}`; a server with an undefined variable is not started (warning). Undefined variables are not an error on save: they depend on the machine that runs it.

### Hooks — `HookCatalog`

`HookCatalog(options).List()` returns the named hooks and validators a step can bind (`IHookRegistry` entries plus `IStepValidator` services) as `HookInfoDto` (`name`, `kind` = `hook` | `stepValidator` | `resultValidator`, `signature`, `description`, `source`), sorted by name; a host fills the registry in `WorkflowDesignerOptions.ConfigureEngineServices`. `ListForAsync(package)` adds (2026-10-23, architecture §18.7) `scriptFunctions: [ { name: "js:<lib>.<fn>", library, function, source: "package" | "plugin:<id>" | "host" } ]` and `scriptProblems` (`PackageScripts.FunctionsAsync`: the worker lists what each library of a run of the package exports; cached by the libraries' text). `StepNode` carries `hooks`, `validators` and `resultValidators` (reader, emitter and templates keep them); `WorkflowValidator` reports unknown names, wrong kinds and unknown slots all at once and warns about a validator on `ExecStep`/`ScriptStep` with `maxRetries` > 1. See `architecture.md` §14. Since 2026-10-23 a slot may name `js:<lib>.<fn>` (architecture §18.8): the same `scriptFunctions` are offered for the slots that take JavaScript (`HookSlotDto.languages`), and the validation reports `js_hook_name`, `js_hook_library_unknown`, `js_hook_slot` (C#-only slot) and `js_hook_function` on the line of `hooks:`. Since 2026-10-24 the only C#-only slot is `onConversationCreated` (the synchronous `enrich*` and `configureRequest` take JavaScript), and `LlmStep` has the new slot `selectConversation` (`Func<SelectConversationArgs, Task>`, help topic `hook.selectConversation`); both come out of the catalog's `hookSlots` with no change to the DTOs (architecture §14.3, §18.8).

### Tools — `ToolCatalog`

- `Task<ToolListing> ListAsync(ct)` → `ToolListing(Tools: ToolInfoDto[], Problems: string[])`; `GET /api/tools` returns
  `Tools` (`{ name, description, source: 'FileTools' | 'Mcp:<server>' | 'Plugin:<id>' | 'Host', parameters, qualifiedName }`) and logs `Problems`.
  Every tool also answers to `qualifiedName` = `<source>:<name>` in `tools: [...]` (architecture §10.2, §25.9); a tool another one shadows by bare name is an extra entry whose `name`
  is its qualified name. In the framework the alias is a `ToolAlias(Name, Tool)` service read by `YamlLlmStep.ResolveTools`.
- Each MCP server connects with its own `McpToolProvider` (not `McpExtension`, whose `TryAdd` only keeps the first):
  **every server takes effect**. A server's listing is cached for 60 s (`ToolCatalog.ListingTtl`) keyed by a hash
  of its configuration; a run always connects its own and disconnects them when it finishes.
- Only a workflow whose `LlmStep` has `tools` builds tools. Validation: stand-ins with the definition (never executed); run: the live tools. Both are registered in the engine collection as `ITool` and with an `ITools`
  of its own in that collection (it replaces the static `AvailableTools.Instance`). The step resolves the names against that
  provider (framework: `YamlLlmStep.ResolveTools`).
- Validation: every name in `tools` must exist (case-insensitive); the error lists the available ones and the reason for the missing ones (a server that did not respond). Repeated names across sources: the first wins and a `warning` is emitted.
- **FileTools concurrency limit**: `FileToolsConfiguration` is static in the framework. Each run that uses tools applies its root and folders when it starts (`FileToolsScope.Apply`, under a lock: clears the additional ones and sets root and folders). Two simultaneous runs share the last applied: with `MaxConcurrentRuns > 1` and an `environment.json` changed between them, the first would start using the second's roots. With the default value
  (1) this does not happen. It affects the whole process.

### What access FileTools and MCP grant

- FileTools reads, searches and **writes** (`write_to_file`, `replace_file_content`, `multi_replace_file_content`) inside the root
  and the additional folders, with the permissions of the process user. It rejects with `UnauthorizedAccessException`
  (verified in `ToolsTests`): relative `..` that escape, absolute paths outside, the sibling folder with the same prefix
  (`root2`), writing or listing outside, and a junction inside the root that points outside.
- Known framework gaps (not fixed): checking and access are not atomic (a link changed between them, TOCTOU); a **hard link** inside the root to a file outside is not detected; path comparison ignores case also on file systems that distinguish it (Linux). A broad additional folder (the entire drive, `${HOME}`) gives access to everything it contains.
- MCP `Stdio` **starts a process** (`Command` + `Arguments`) with the user of the backend or the CLI, without the confinement of FileTools. An HTTP server receives the tool calls and the configured headers. They are only started or called when listing tools (`GET /api/tools`), validating or running a workflow that names tools.

### Initial context, suspension and resume — `RunManager`

- `RunRequest.Context: RunContextRequest { Metadata: JsonObject?, ConversationId: string? }`. `conversationId` is a workspace name (`[A-Za-z0-9._-]+`, no `..`); otherwise `ArgumentException` (400). Metadata keys that are empty or start with `__` (reserved by the framework) fail the same way. The conversation is loaded with
  `LlmPipelineContextFactory.CreateForConversationAsync` and saved when it ends in any state except `cancelled`.
- Suspension: a result with `SuspendInfo` leaves the run `suspended`; the context is saved with the run's `IPipelineContextStorage` (key `CorrelationId`) and the record in `runs/suspended/<runId>.json`
  (atomic write; `runId` = 32 hex, any other id never becomes a file name). The record stores the YAML snapshot, the suspension, the `CorrelationId`, the type and the folder of the context store (resume looks for it **where it was saved**, even if the environment has changed) and the `conversationId`. A context that cannot be serialized leaves the run `failed` with the reason.
- P1a: the record (`<data>/runs/suspended/<runId>.json`, version 2) carries `packageId` and `packageName`, and next to it
  goes `<runId>.package.zip`, the snapshot of the package that ran (with the YAML that ran). Resume uses from the snapshot the YAML, schemas and prompts, and the **current configuration** of the package by `id` (the one in the saved zip), or the snapshot's if the package no longer exists. A record without a snapshot (migrated) uses the package's saved schemas and prompts with the record's YAML. A v1 record without `packageId` (unmigrated workspace) comes out with `resumable: false`, and resuming it is a `SuspendedRunConflictException` asking to migrate. `SuspendedRunDto` adds `packageId`, `packageName` (current name) and `packageMissing`. `RunManager.PurgeSuspended(TimeSpan olderThan) → int` deletes the record, snapshot and context of the oldest ones (also when creating the manager with `SuspendedRunRetention`).
- `ResumeAsync`: `KeyNotFoundException` (404) if there is no record; `SuspendedRunConflictException` (409) if the context is gone (memory after a restart), it is already resuming, or the record has no package; compiles the snapshot with the current configuration (`WorkflowValidationException`, 422). On completion it deletes the previous record and the context; if it suspends again (a response that does not meet the `outputSchema`, with `rejectionReason`) it deletes the previous record and writes its own; if it fails or is cancelled, the previous record is kept and it can be resumed again.
- Tests: `SuspendResumeTests`, `ToolsTests`, `EnvironmentSectionsTests` and the `ask-and-continue` one in
  `AdvancedStoryWorkspaceTests`.

## 8. Sessions — `SessionManager` and `SessionStore` (2026-10-18, architecture §11)

```csharp
SessionDto Create(string packageId, string? title = null);         // PackageNotFoundException (404) / DuplicatePackageIdException (409)
IReadOnlyList<SessionSummaryDto> List(string? packageId = null);    // más recientes primero (updatedAt); orphaned = el paquete ya no existe
SessionDto Get(string id);                                          // KeyNotFoundException; resuelve turnos huérfanos
Task DeleteAsync(string id, bool deleteConversation, CancellationToken ct = default); // SessionConflictException si hay un turno en curso
Task<TurnStartedDto> StartTurnAsync(string id, JsonNode? input, CancellationToken ct = default); // { n, runId }
Task<SessionDto> WaitTurnAsync(string id, int n, CancellationToken ct = default);  // hasta que el turno deja `running` y está escrito
Task WaitWrittenAsync(string runId, CancellationToken ct = default);               // hasta que el turno de ese run está escrito
```

Registered as a singleton by `AddWorkflowDesigner`. **The host must resolve it at startup** (the backend does it in
`Program.cs`; the CLI in `resume` and `suspended`): it subscribes to internal `RunManager` events (`Resumed`,
`Discarded`) so that a resume or discard done through `/runs` or the CLI updates the turn. Session ids and names:
`WorkspaceStore.ValidateName` (`InvalidWorkspaceNameException`, 400). `SessionConflictException`
(`InvalidOperationException`) is 409.

- **Turn** = `RunManager.StartAsync(new RunRequest { PackageId, Source = Saved, Input, Context = { ConversationId = "sess-<id>", Metadata =
  memory } })`: the package is extracted from the **saved** zip at that moment (not from the working copy); new context on each turn.
  P1a: `SessionDto` and `SessionSummaryDto` add `packageId` and `orphaned` (`workflow` becomes the package's current visible name; a deleted package leaves orphaned sessions, which can be read but do not accept turns: `PackageNotFoundException`). Whatever `StartAsync` throws (422, 404, 409 from `MaxConcurrentRuns`) leaves no turn. Its own 409 if the last turn is `running` or `suspended`.
- **End of turn on the server**: a task per run waits on `RunManager.WaitOutcomeAsync` (internal) and writes `status`,
  `output` (the run's `result`), `error`, `finishedAt`, `durationMs` (execution time of all attempts; the
  wait while suspended does not count), `promptTokens`, `completionTokens` and `costUsd` (sum of the `llm.response` events of all
  attempts; `RunEventLog.Usage` counts them on arrival, also when the event cap discards them) and, only if
  `completed`, the memory. Failed or cancelled: the memory does not change. The conversation is saved by `RunManager` as in
  any run with a `conversationId` (except `cancelled`).
- **Memory** (`SessionMemory.Extract`): the keys of the final context's `Metadata` except those starting with `__`
  (suspension state of `PipelineResume`), `TokensUsed` and `CostUsd` (written by `BaseLlmStep.UpdateContextMetrics`
  on each call), and values that are not JSON or are `null` (`context.set("k", null)` removes the key). The
  `StepResults` (the fast-forward cache) are another dictionary and never enter. Cap `MaxSessionMemoryBytes`
  (256 KiB, UTF-8 of the JSON): if exceeded, the largest keys go first and the turn says so in `warnings`.
- **Suspension**: turn `suspended` with `suspension` (the one from `RunInfo`). When its run is resumed (by any route), the turn
  goes back to `running` with the new `runId` and `resumedFrom`; on completion it completes as usual. If the resumed attempt fails or
  is cancelled and the suspended run remains (the `RunManager` keeps it), the turn returns to `suspended` over it with the failure
  in `error`. Discarding it (`DELETE /runs/suspended`, `cancel`, `suspended --discard`) leaves the turn `cancelled`. Deleting
  the session discards its suspended run.
- **Recovery** (on read): a `running` turn whose owning process (`pid` + start time, saved in the file) no longer
  exists becomes `failed` («Interrupted by a restart…», English message); one from another live process (the CLI while the backend reads) is not touched. A `suspended` one whose run is no longer suspended becomes `cancelled`; one with a record stays resumable after a restart if `Storage.Context` is `file`.
- **Deleting the conversation** (`RunManager.DeleteConversation`, internal): in the store that **currently** says
  `Storage.Conversation`: `memory` (the Core's own store, `SharedConversationStore`, which can delete), `file`
  (`<dir>/<id>.json`) or `yaml` (`<dir>/session-<id>.yaml`), with the file names of `FileConversationStorage` and
  `YamlFileConversationStorage` replicated (the framework's `IConversationStorage` has no delete).

`SessionStore(dataDirectory)`: `<data>/sessions/<id>.json` with atomic writes (`WorkspaceStore.WriteAtomic`); the file
wraps the `SessionDto` with `autoTitle` and `owners` (these are not exposed by the API). Statics: `DefaultTitle(input)` (the first
text of the input, spaces collapsed, 60 characters followed by «…»), `Truncate(value, maxChars)` (`MaxTurnValueChars`,
32,000 by default: the stored value becomes cut text and `inputTruncated`/`outputTruncated` = true), `Summary`.

Known limits: writes are serialized per session within the process; two processes writing the same session at the same
time = the last one wins. A turn is only completed by the process that started it: if that process dies, the turn is `failed` when read.
Tests: `SessionTests`, `AdvancedStoryWorkspaceTests` (two turns of the story machine), `SessionEndpointTests`
(Web) and `SessionCommandTests` (CLI).

## 9. Packages (P1a, 2026-10-18; specification `PACKAGES-AND-TEMPLATES-SPEC.md` §4-§11)

Namespace `WorkflowDesigner.Core.Packages`. Everything in this section is registered by `AddWorkflowDesigner`
(§1); reference tests: `PackageArchiveTests`, `PackageStoreTests`, `PackageRunTests`, `WorkspaceMigrationTests`.

### 9.1 Where each thing lives

```
<Workspace>/workflows/<nombre>.zip      los paquetes; el nombre del fichero (sin .zip) es el nombre visible
<Workspace>/workflows/.<nombre>.zip.lock   lock de un paquete abierto (RN-08)
<Workspace>/library/configs/default.zip     la crea `migrate` (P3 la usa)
<datos>/                                    WorkflowDesignerOptions.DataDirectory, por defecto <Workspace>/data
  packages/index.json                       caché del índice (se reconstruye recorriendo workflows/*.zip)
  packages/<id>/                            destino de un `Storage` relativo (contexts/, conversations/...)
  sessions/<sessionId>.json                 sesiones (con packageId)
  runs/suspended/<runId>.json + .package.zip  suspendidas v2 + instantánea del paquete
  work/<editId>/ + work/<editId>.json       copias de trabajo y su ficha (quién la tiene, de qué zip)
  work/run-<runId>/                         extracción de un paquete para una ejecución; se borra al terminar
```

The package `id` is `manifest.id` (32 lowercase hex). Renaming or moving the zip does not change it. The path of a
working copy never leaves the API (`PackageSession.WorkDirectory` is `internal`).

### 9.2 `PackageArchive`, `PackageContents`, `PackageManifest` (RN-01..07)

```csharp
static class PackageArchive {
  PackageContents Read(string path | Stream | byte[], YamlReader? reader = null, PackageLimits? limits = null);
  byte[] Write(PackageContents, YamlReader? = null, PackageLimits? = null);          // determinista (RN-06)
  void WriteFile(string path, PackageContents, YamlReader? = null, PackageLimits? = null, Action<string>? afterValidated = null); // atómico (RN-07)
  void Validate(PackageContents, YamlReader? = null, PackageLimits? = null);
  void ExtractTo(PackageContents, string directory);            // comprueba cada ruta; nunca ExtractToDirectory
  PackageContents FromDirectory(string directory);              // una copia de trabajo o `package unpack` → contenido
  string Sha256Of(string path | byte[]);                        // hex en minúscula
}
sealed class PackageContents { PackageManifest Manifest; IReadOnlyDictionary<string, byte[]> Entries; SchemaNames; PromptNames;
  Set/SetText/Remove/Has/TryGet/TryGetText; SetManifest; Fingerprint(); SameAs(other); }
sealed record PackageLimits { MaxZipBytes=20 MiB; MaxTotalBytes=50 MiB; MaxEntries=500; MaxEntryBytes=5 MiB; MaxRatio=100; RatioFromBytes=64 KiB }
sealed class PackageManifest { Format, FormatVersion, Kind, Id, Name, Description?, CreatedAt, UpdatedAt, Generator?, Templates? (JsonArray), Extra;
  static Create(name, kind = "workflow", description = null); ToBytes(); static Parse(json); Clone(); }
static class PackageFormat { Format, CurrentVersion = 1, KindWorkflow/KindConfig/KindTemplate, NewId(), IsValidId(id) }
```

- The entries of §4.1 of the specification are accepted according to `manifest.kind` (`workflow`: `manifest.json`, `workflow.yaml`,
  `layout.json?`, `config.json`, `schemas/<n>.json`, `prompts/<n>.md`, `README.md?`; `config`: manifest and `config.json`;
  `template`: `template.yaml`, `layout.json?`, schemas, prompts, README). Schema and prompt names with
  `WorkspaceStore.ValidateName`; lowercase extension. Empty folder entries `schemas/` and `prompts/` are tolerated.
- `Read` counts the bytes **actually** decompressed and stops when the limit is passed; the compression ratio (100:1) is checked
  only from 64 KiB decompressed (documented deviation). With `reader`, `workflow.yaml` must be read with the Core's `YamlReader`; without it, it is only required to exist. `config.json` must be a JSON object and each schema a JSON object.
- `Write`: order manifest, workflow (or template), layout, config, schemas, prompts, README (each group by ordinal name), time
  1980-01-01, optimal Deflate, file attributes 0644 and «version made by» = Unix in all records (so the zip does not
  depend on the operating system), each entry's bytes as they are. Same content → same bytes.
- `WriteFile` writes `.<name>.<guid>.tmp` next to the target, rereads and validates it, calls `afterValidated` (test hooks) and
  moves it with `File.Replace`/`File.Move`; a failure leaves the previous zip intact and no temporaries.

Errors (all in `WorkflowDesigner.Core.Packages`, suggested HTTP mapping):

| Exception | HTTP | When |
|---|---|---|
| `InvalidPackageException` (`Code`, `Entry`) | 422 | RN-01, 02, 03, 05 and format: codes `not-a-zip`, `entry-name`, `entry-not-allowed`, `duplicate-entry`, `symlink`, `encrypted-entry`, `compression-ratio`, `missing-manifest` (the message suggests `migrate` if there is a loose `.yaml`), `invalid-manifest`, `newer-version`, `wrong-kind`, `missing-entry`, `invalid-workflow`, `invalid-config`, `invalid-entry`. The message names the entry and never contains data from the zip. |
| `PackageTooLargeException` (inherits from the above) | 413 | RN-04: `zip-size`, `too-many-entries`, `entry-size`, `total-size` |
| `PackageNotFoundException` (`FileNotFoundException`) | 404 | no package with that id |
| `PackageNotOpenException` | 404 | the operation needs an edit session and there is none |
| `DuplicatePackageIdException` (`Files`) | 409 | two zips with the same id; resolved with `AssignNewId` |
| `PackageNameConflictException` | 409 | a package with that name already exists (create, import, save as, rename) |
| `PackageConflictException` (`Conflict` = `modified`\|`deleted`, `SavedAt`) | 409 | saving with the zip changed or deleted from outside; `force` overwrites it |
| `PackageHasChangesException` | 409 | closing with changes without `discard` |
| `PackageOpenException` | 409 | `AssignNewId` on an open package |
| `PackageReadOnlyException` (`LockedBy`) | 423 | read-only session (another process holds the lock) or package locked when renaming/deleting |
| `InvalidWorkspaceNameException` | 400 | invalid package, schema or prompt name |

### 9.3 `PackageStore`

```csharp
string WorkspaceDirectory, WorkflowsDirectory, DataDirectory, IndexPath;   string DataDirectoryOf(string packageId);

IReadOnlyList<PackageSummary> List();                    // recorre workflows/*.zip; solo relee los zips cambiados
PackageSummary Get(string packageId);  bool Exists(string packageId);
PackageSummary Create(string name, string? description = null, JsonObject? config = null);
PackageSummary Import(Stream zip, string? name = null);  // valida todo antes de escribir; id nuevo si el suyo ya existe
ExportedPackage Export(string packageId);                // (FileName, Content): el zip guardado

OpenResult Open(string packageId, RecoveryChoice recovery = Ask);   // → Session | Recoverable
PackageSession? GetOpen(string packageId);  PackageSession RequireOpen(string packageId);
PackageSession? GetByEditId(string editId);  IReadOnlyList<PackageSession> OpenSessions();
bool HasChanges(PackageSession session);                 // copia de trabajo ≠ zip (sin contar manifest.updatedAt)
SaveResult Save(PackageSession session, bool force = false);        // → (Package, Changed)
SaveResult SaveEntries(PackageSession session, IReadOnlyCollection<string> entries,
                     IReadOnlyCollection<string>? delete = null, bool force = false);  // guardado parcial: solo esas entradas
PackageSummary SaveAs(PackageSession session, string name);         // id nuevo; la sesión pasa a editar el nuevo
void Close(PackageSession session, bool discard = false);
PackageSummary Rename(string packageId, string newName);            // fichero y manifest.name; el id no cambia
bool Delete(string packageId);                                      // cierra y descarta la sesión; false si no existe
PackageSummary AssignNewId(string name);                            // por nombre de fichero (el id es ambiguo)

PackageContents ReadSaved(string packageId);
ExtractedPackage ExtractSaved(string packageId, string directory);  // IDisposable: borra la carpeta
ExtractedPackage ExtractFile(string zipPath, string directory);     // un zip cualquiera (CLI); valida RN-01..05
ExtractedPackage OpenFolder(string directory);                      // carpeta con la estructura del §4.1; no la borra
```

- **`PackageSummary`** (`GET /packages`): `{ id?, name, file, updatedAt, description?, hasSecrets, open, readOnly?, problems[] }`.
  A zip that is not a package comes out with `id` null and `problems: ["invalid: <reason>"]`; two with the same id, `duplicate-id` on both;
  one locked by another process, `locked: pid N on HOST`. `file` is the file name, never a path.
- **`PackageSession`** (implements `IPackageFolder`): `EditId`, `PackageId`, `PackageName`, `Workspace` (`WorkspaceStore`),
  `Config` (`ConfigStore`), `Manifest`, `ReadOnly`, `LockedBy`, `Recovered`, `OriginSha256`, `SavedAt`. Opening the same package twice
  returns the same session. After `SaveAs`, `PackageId` and `PackageName` change on the same object; after `Rename`, `PackageName`.
- **`OpenResult`**: `{ Session?, Recoverable, Recovered }`. With an orphaned working copy of the same zip (same `id` and same
  origin sha256, and with unsaved changes), `Open(id)` returns `Recoverable = true` **without opening anything**; the UI offers
  recovering it (`Open(id, Recover)` → `Session.Recovered`) or discarding it (`Open(id, Discard)`). A copy without changes is deleted by itself.
  Through the UI: `POST /packages/{id}/open` → `{ editId, readOnly, recovered?, recoverable? }` (without `editId` if `recoverable`).
- **Locks (RN-08)**: `Open` takes `.<name>.zip.lock` (`pid:start`, host, store instance). If a live process (or one on another host) holds it,
  the session is read-only (`ReadOnly`, `LockedBy`) and every write —`WorkspaceStore`, `ConfigStore`,
  `Save`, `Rename`— throws `PackageReadOnlyException`: **Web must return 423 and the UI must warn**. A lock from a dead process
  is ignored and replaced. `PackageStore.Dispose()` releases the locks.
- **`Save`**: if the zip's sha256 is no longer the session's → `PackageConflictException`; without changes it does not write (the zip and its
  `updatedAt` stay identical, `Changed = false`); with changes it sets `manifest.updatedAt`, packs (RN-06/07) and rereads. A
  `workflow.yaml` that the `YamlReader` does not read (does not happen if it was saved with `YamlEmitter`) is an `InvalidPackageException` and the zip
  stays as it was. Saving when encryption is needed and it is locked: see §10.1 (`RequireMasterKeyForNewSecrets`, 423 `secrets_locked`); `Save` re-encrypts the secrets before packing and returns `SaveResult.Warnings`.
- **`SaveEntries`** (2026-10-18): writes into the zip only the named entries (e.g. `config.json`, `workflow.yaml`, `schemas/<n>.json`),
  taking them from the working copy (absent there = removed; `delete` forces removals) and every other entry from the zip on disk. Same
  guarantees as `Save` (atomic RN-07, RN-01..05, determinism RN-06, `PackageConflictException` against the base sha256, `PackageReadOnlyException`,
  resealing of `config.json` with `Warnings`, `manifest.updatedAt`); no write when the entries are already equal (`Changed = false`). The session's
  base sha256, `SavedAt` and the fingerprint of those entries are refreshed, so `HasChanges` keeps reporting what is still unsaved. The manifest
  cannot be named (`ArgumentException`). The Web calls it after each config, workflow, schema and prompt write.
- **`Import`**: reads the whole zip before touching `workflows/`; `kind` must be `workflow`; the name comes from `name` or the
  manifest.
- **`Rename`/`Delete`/`AssignNewId`** take the zip's lock while they work. Deleting a package does not delete its sessions or its
  data: the sessions become `orphaned`.
- When the store is created, `work/run-*` extractions older than 24 h and orphaned copies older than 30 days are deleted.

### 9.4 Validation and tools per package

```csharp
interface IPackageFolder { string PackageId; string PackageName; WorkspaceStore Workspace; ConfigStore Config; }  // PackageSession y ExtractedPackage

WorkflowValidator:
  Task<ValidationResult> ValidateAsync(string packageId, WorkflowDocument)       // el paquete debe estar abierto
  Task<ValidationResult> ValidateYamlAsync(string packageId, string yaml)
  Task<ValidationResult> ValidateAsync(IPackageFolder, WorkflowDocument)
  Task<ValidationResult> ValidateYamlAsync(IPackageFolder, string yaml)
  Task<ValidationResult> ValidatePackageAsync(IPackageFolder)                     // su propio workflow.yaml (CLI validate)
ToolCatalog:  new ToolCatalog(ILoggerFactory? = null);  Task<ToolListing> ListAsync(ConfigStore config, ct)
```

Schemas, roles and tools are checked against **the package**. `warnings` adds (additive, does not change `ok`) a
warning for each schema or prompt of the package that no step uses («orphan», RF-06); a prompt counts as used if the
`systemPrompt` or `userPrompt` of some step is exactly its name.

### 9.5 Executions per package

```csharp
record RunRequest { string? PackageId; PackageSource Source = Working; string? PackageFile; string? PackageDirectory;
                    WorkflowDocument? Document; JsonNode? Input; RunContextRequest? Context;
                    bool ExecTrusted = false; }   // JSON: package, source, document, input, context (ExecTrusted is [JsonIgnore])
enum PackageSource { Working, Saved }                                                           // JSON "working" | "saved"
```

**exactly one** of `PackageId`, `PackageFile` and `PackageDirectory` is given:

| Input | What runs |
|---|---|
| `PackageId` + `Working` | the session's working copy, open (with unsaved edits); `PackageNotOpenException` if it is not open |
| `PackageId` + `Saved` | the saved zip, extracted to `<data>/work/run-<runId>/` and deleted when it finishes (also if it fails, is cancelled, expires or suspends, or if startup throws) |
| `PackageFile` | any zip (CLI `run x.zip`): validated (RN-01..05) and extracted the same way; no sessions or index |
| `PackageDirectory` | a folder with the structure of §4.1 (`--source-dir`), as is and not deleted |

`Document` replaces `workflow.yaml` (unsaved edits), with the package's schemas, prompts and configuration. The
relative `Storage` in `config.json` goes to `<data>/packages/<id>/` with the manifest's `id`, also with `PackageFile`.
`RunInfo` and the `run.started` events carry the package name in `workflow` and `RunInfo.packageId`. Session turns
run `Saved`. `FileTools` still has no root of its own per run: the `MaxConcurrentRuns > 1` warning of §7 applies
the same way (more likely with per-package configuration).

**External commands (`ExecStep`, architecture §13.4).** `RunRequest.ExecTrusted` (never read from JSON, false by default), the optional `execTrusted` of
`RunManager.ResumeAsync(runId, response, ct, execTrusted)` and of `SessionManager.StartTurnAsync(id, input, ct, execTrusted)` say that whoever started the run
confirmed that the package may run external programs. Every run registers `Engine/DesignerExecPolicy` as its `IExecStepPolicy` (after `ConfigureEngineServices`):
without that flag it denies every command (`ExecStepException`, `errorType` `Exception:ExecStepException`), and it confines `workingDirectory` to
`FileTools.RootDirectory` (empty = the process's directory) and `AdditionalAllowedDirectories` (an unset directory runs in the root; outside is denied; links are not
resolved). The web host sets the flag after its trust check, the CLI always. `Catalog/ExecCommands.Find(document, catalog)` lists a workflow's `ExecStep`s (nested
ones included) as `ExecCommand { StepId, Executable, Args[{ Text, Dynamic }], WorkingDirectory?, WorkingDirectoryDynamic, EnvNames, InheritsAllEnvironment }`: names of
the variables, never their values, and no `stdin`. `PackageTrust` (Web) and the CLI both use it.

### 9.6 `WorkspaceMigration` (specification §11)

```csharp
bool IsLegacy();                              // hay environment.json o workflows/*.yaml
MigrationReport Run(bool dryRun = false);     // idempotente
record MigrationReport { DryRun, Needed, LegacyDirectory?, Packages[] (name, id, file, created, schemas, prompts, missingSchemas),
                         ConfigLibrary?, Sessions, SuspendedRuns, EncryptedKeysPending, Warnings[], Errors[] }
```

- Validates and builds everything in memory before writing (`dryRun` stops there). For each `workflows/<n>.yaml`: `workflows/<n>.zip`
  with a new id, the YAML, `<n>.layout.json` as `layout.json`, a copy of `environment.json` as `config.json` (or the empty one) and
  the schemas and prompts it references (schemas via `inputSchema`/`outputSchema`; prompts when `systemPrompt`/`userPrompt` is
  exactly the name of a `.md`). References to non-existent schemas: `missingSchemas` and a warning, without blocking. If a zip with that name already
  exists it is not touched (warning).
- `environment.json` → `library/configs/default.zip` (`kind: config`). The `enc:v1:` keys are copied as they are
  (`EncryptedKeysPending`) and, if there is an unlocked master password (P2), they are re-encrypted to `enc:v2:`.
- A relative `Storage.*.Directory` changes meaning (workspace → package data folder): the old folder is **copied** to `<data>/packages/<id>/<dir>` (the original is not touched) and there is a warning.
- Sessions and suspended-run records receive `packageId` by the name of their workflow and move to `<data>/sessions` and
  `<data>/runs/suspended` (the records, upgraded to version 2 without snapshot); a session of a workflow without a package is orphaned
  with a warning.
- The originals (`.yaml`, `.layout.json`, `environment.json`, `schemas/`, `prompts/`, sessions, records) are **moved** to
  `legacy/<yyyyMMdd-HHmmss>/` with their relative path. If a workflow cannot be converted (unreadable YAML) it goes to `Errors`, the
  others are converted, and what depends on all of them (sessions, suspended runs, `environment.json`, `schemas/`, `prompts/`) stays
  where it is until the migration is repeated. An `environment.json` that is not JSON blocks everything. A second run with nothing pending
  returns `Needed = false` and touches nothing.
- Web (`POST /workspace/migrate { dryRun }`) and CLI (`migrate --workspace <dir> [--dry-run]`) must only call this; the
  CLI needs its own `WorkflowDesignerOptions` with `Workspace` = that folder.

## 10. Portable encryption, library and templates (P2 and P3, 2026-10-18; specification §5, §6 and §7)

Reference tests: `SecretsV2Tests` (cases 13-18), `LibraryConfigTests` (RF-10..14) and `TemplateTests` (cases 22-30). The
decisions that depart from the specification are in `architecture.md`, under «Deviations», subsection «Core P2 and P3».

### 10.1 Master password and `enc:v2:` (`Workspace/`)

```csharp
// Registro: AddWorkflowDesigner añade, con TryAdd, MasterKeyProvider (toma WorkflowDesignerOptions.MasterKey o WORKFLOW_DESIGNER_KEY),
// y siempre LibraryStore y SecretsManager. ISecretProtector sigue siendo el protector del usuario (solo lee enc:v1).
class WorkflowDesignerOptions { string? MasterKey; bool RequireMasterKeyForNewSecrets; }   // la segunda, false por defecto

sealed class MasterKeyProvider {
  const string EnvironmentVariable = "WORKFLOW_DESIGNER_KEY"; const string FromEnvironment/FromConfiguration/FromSession;   // "environment" | "configuration" | "session"
  MasterKeyProvider(string? configuredPassword = null);   // configuración > variable de entorno; bloqueado si no hay ninguna
  static MasterKeyProvider ForPassword(string password);   // desbloqueado, fuente "session" (rekey, tests)
  bool IsUnlocked; string? Source; int Iterations;          // Iterations = 600 000 (no configurable por el usuario)
  void Unlock(string password, IEnumerable<string>? verifiers = null);  // valida contra los verificadores; WrongMasterKeyException si no abre ninguno; sin verificadores acepta cualquiera
  void Lock();                                              // olvida la contraseña y las claves derivadas, venga de donde venga
}
sealed class SecretsManager(MasterKeyProvider, PackageStore, LibraryStore) {   // lo que usan /api/secrets/*
  SecretsStatus Status();            // { unlocked, source, hasEncryptedSecrets, firstTime }  (firstTime = ningún verificador en paquetes ni biblioteca)
  void Unlock(string password);      // keys.Unlock(password, KnownVerifiers())
  void Lock();
  IReadOnlyList<string> KnownVerifiers();   // Secrets.Verifier de los zips guardados (índice) y de las configuraciones de la biblioteca
}
sealed class PasswordSecretProtector : ISecretProtector { const int DefaultIterations = 600_000; MinIterations = 1_000; MaxIterations = 10_000_000; SaltSize = 16;
  PasswordSecretProtector(MasterKeyProvider keys, byte[] salt, int iterations); static byte[] NewSalt(); string Format(byte[] payload); }
static class ApiKeySecrets { const string Prefix = "enc:v1:", PrefixV2 = "enc:v2:";
  IsEncrypted(v) /*v1 o v2*/; IsV1; IsV2; Encrypt(ISecretProtector, text) /*v1*/; EncryptV2(PasswordSecretProtector, text);
  Decrypt(ISecretProtector, provider, value); Decrypt(ISecretProtector legacy, MasterKeyProvider? keys, provider, value) /*despacha por prefijo*/;
  DecryptV2(MasterKeyProvider?, provider, subject, value); TryParseV2(value, out salt, out iterations, out payload); }
static class ConfigSecrets { string? GetVerifier(JsonObject?); SetVerifier(JsonObject, string); Remove(JsonObject); }   // sección Secrets.Verifier
```

- **Format**: `enc:v2:<saltB64url>.<iterations>.<base64(nonce[12] | tag[16] | ciphertext)>`, AES-256-GCM, key PBKDF2-HMAC-SHA256 with
  16 bytes of salt, associated data `WorkflowDesigner.Secret.v2`. One salt per configuration: the one in `Secrets.Verifier` (encryption of
  `workflow-designer-verifier`), created when the first v2 value is written. `MasterKeyProvider` caches the derived key by
  (salt, iterations) until `Lock()`. When reading, iterations outside 1,000..10,000,000 count as a malformed value (a foreign
  folder cannot impose minutes of derivation).
- **`ConfigStore`** (`PackageSession.Config`, `ExtractedPackage.Config`): `Save(JsonObject)` keeps the same signature;
  `SaveWithReport(JsonObject) → ConfigSaveResult(Warnings, Changed)` is the same with warnings. With the master password unlocked, new
  values are written `enc:v2:`, `enc:v1:` values that decrypt move to v2, and v2 values with another salt are re-encrypted to the package's; without the password, the new value is encrypted `enc:v1:` (for this user) and a warning is raised, except with
  `RequireMasterKeyForNewSecrets` (then `SecretsLockedException`). The stored verifier takes precedence: with a different unlocked password, saving something that must be encrypted throws `WrongMasterKeyException` and writes nothing. `Reseal() → ConfigSaveResult` brings the stored content up to date (it never blocks: a value that does not decrypt is left and a warning is raised); `PackageStore.Save` calls it before packing, and
  `SaveResult.Warnings` (new, optional) carries its warnings. `Mcp[].Headers` and `Mcp[].EnvironmentVariables` behave the same as `ApiKey`.
  `Read()` keeps returning `********` for secrets and the `Secrets` section as is (the verifier is not a key).
- **Errors** (all derive from `ApiKeyDecryptionException`, with `Provider`, `Reason: SecretFailure` and `Code`; no message carries
  the value, its ciphertext or the password; the v2 ones are in Spanish):

| Exception | `Code` | Expected HTTP | When |
|---|---|---|---|
| `SecretsLockedException` | `secrets_locked` | 423 | an `enc:v2:` is needed and there is no password. Validation: returns it as an error («Claves bloqueadas…», Spanish UI/message text); `RunManager.StartAsync` **propagates** it typed (and `SessionManager` with it) |
| `WrongMasterKeyException` | `wrong_master_key` | 422 | the package's verifier does not decrypt with the unlocked password; also `Unlock` if it opens no known verifier |
| `ApiKeyDecryptionException` (`Reason = Tampered`) | `secret_tampered` | 422 | the verifier opens but the value does not: «El valor de el proveedor 'X' está alterado o se cifró con otra contraseña» (Spanish UI/message text). Validation: error; `StartAsync`: `WorkflowValidationException` (422) |
| `ApiKeyDecryptionException` (`Reason = Undecryptable`) | `secret_undecryptable` | 422 | an `enc:v1:` from another user or machine (message in English, as until now) |
| `EnvironmentValidationException` | — | 422 `errors[]` | as until now; now it also carries decryption failures on save |

  Case 18: a workflow without LLM steps (and therefore without tools) reads the configuration without decrypting anything: it needs no password and pays no derivation cost.
- **Export without keys and rekey** (`Packages/PackageSecrets.cs`):

```csharp
PackageStore.Export(string packageId, SecretsExportMode secrets, StripStyle style = Variables) → ExportedPackage(FileName, Content, RequiredVariables?, Blanked?)
// Keep = el zip guardado tal cual. Strip: cada secreto (literal o cifrado) → ${WD_<SERVIDOR>_<CLAVE>} (mayúsculas, no alfanuméricos a '_';
// proveedor: WD_<PROVEEDOR>_APIKEY; MCP: WD_<SERVIDOR>_<CABECERA|VARIABLE>; choques → sufijo _2, _3) o "" con StripStyle.Empty (Blanked lista qué se vació).
// ${VAR} y valores vacíos no se tocan; la sección Secrets se elimina. RequiredVariables ordenadas, sin repetir.
static PackageSecrets.Strip(PackageContents, StripStyle) → StripResult(RequiredVariables, Blanked, Replaced)   // en sitio sobre el contenido (CLI strip-secrets)
static PackageSecrets.StripConfig(JsonObject, StripStyle)                                                     // sobre un config.json (biblioteca)
PackageStore.Rekey(string packageId, RekeyRequest) → RekeyReport(Changed, Rekeyed, Failed[])                  // zip guardado; PackageOpenException si está abierto aquí; PackageReadOnlyException si lo tiene otro proceso
static PackageSecrets.RekeyFile(string zipPath, RekeyRequest, ISecretProtector legacy, YamlReader? = null)    // un zip cualquiera (CLI package rekey)
record RekeyRequest { string? OldPassword; required string NewPassword; }   // OldPassword null = los v2 ya están con NewPassword (--from-v1)
```

  `Rekey` reads the v1 values with the user's protector, the v2 ones with the old password, encrypts everything with the new one and a new salt, and writes
  a new verifier; what does not decrypt (v1 from another user) is left as is and appears in `Failed` (`Provider 'X'`). With v2 and an
  old password that does not open the verifier: `WrongMasterKeyException` without changing anything.
- **v1 migration of the workspace**: `WorkspaceMigration` re-encrypts to v2 when converting if there is an unlocked password (otherwise `EncryptedKeysPending` as before).
- **Where the password is hooked in**: Web, `WorkflowDesigner:MasterKey` → `WorkflowDesignerOptions.MasterKey`; CLI, in `CliHost`:
  `Options.MasterKey = key?.Reveal()` before `AddWorkflowDesigner` (it wins over `WORKFLOW_DESIGNER_KEY`; `--key > --key-file > variable` is already resolved by `MasterKey.Resolve`).
  A host that wants its own `MasterKeyProvider` registers it first (`TryAdd`).

### 10.2 Library (`Library/LibraryStore`)

`library/configs/<n>.zip` (entries `manifest.json` and `config.json`, `kind: config`) and `library/templates/<n>.zip` (`kind: template`),
with the package reader and writer (RN-01..07). Names per `WorkspaceStore.ValidateName`. `LibraryKind { Config, Template }`
(`kind.Segment()` = `configs`|`templates` for the path; `LibraryKinds.TryParse`).

```csharp
IReadOnlyList<LibraryItemSummary> List(LibraryKind);  LibraryItemSummary Get(kind, name);  bool Exists(kind, name);
ExportedPackage Export(kind, name);                                  // GET /library/{kind}/{n}/export
LibraryItemSummary Import(kind, Stream zip, string? name = null);    // valida todo antes de escribir; kind distinto → InvalidPackageException("wrong-kind"); plantilla inválida → "invalid-template"; id repetido en la carpeta → id nuevo
LibraryItemSummary Rename(kind, name, newName);  bool Delete(kind, name);   // no tocan ningún paquete
// Configuraciones
LibraryItemSummary SaveConfigFromPackage(string name, IPackageFolder package, SecretsExportMode secrets, StripStyle style = Variables, string? description = null, bool overwrite = false);
JsonObject ReadConfig(string name);                                  // enmascarada, como la de un paquete
ConfigSaveResult SaveConfig(string name, JsonObject config);         // las mismas reglas que ConfigStore.SaveWithReport (máscara, cifrado, validación)
ConfigApplyResult ApplyConfig(string name, PackageSession package, ApplyMode mode = Replace);   // → { mode, conflicts[{kind,name}], reenter[], warnings[] }
PackageSummary CreatePackage(string packageName, string configName, string? description = null);  // POST /packages { name, fromConfig }
// Secretos de la biblioteca (para SecretsManager)
IReadOnlyList<string> KnownVerifiers();  bool HasEncryptedSecrets();
```

- **Save from package** starts from the working copy (what the user sees). `Keep` re-encrypts it to v2 in memory if there is a password (it does not touch the copy);
  `Strip` applies §5.6. Name taken → `LibraryNameConflictException` unless `overwrite` (which keeps the `id`).
- **Apply**: writes the working copy's `config.json` as is (without blocking on validation: the result's problems come out in `warnings`) and leaves it
  unsaved. `Replace`: the package's becomes the library's (with its `Secrets`). `Merge`: adds providers, roles, fallbacks and MCP servers
  that are missing (case-insensitive); what both have is kept from the package and listed in `conflicts` (if identical it is not a conflict);
  `Storage` and `FileTools` only if the package does not have the section; `DefaultRole` if the package has none or had no roles. `reenter`: what was copied and does not decrypt (v1 from another user; v2 if there is a password and it does not open the library's verifier or the value does not decrypt); with the password locked the v2 values cannot be checked and this is said in `warnings`. v2 values with the library's salt are re-encrypted to the package's on save (if it is the same password).
- Errors: `LibraryItemNotFoundException` (404 `library_item_not_found`), `LibraryNameConflictException` (409 `library_name_conflict`),
  `InvalidPackageException` / `PackageTooLargeException` (422 / 413, as for packages), `InvalidWorkspaceNameException` (400),
  `PackageReadOnlyException` (423 `read_only`), `EnvironmentValidationException` (422 `errors[]`).
- `LibraryItemSummary` (JSON camelCase): `{ kind, name, file, id?, description?, updatedAt, hasSecrets, steps?, parameters?[], requires?, sha256?, problems[] }`; templates
  carry `steps` (with nested ones), `parameters` (names), `requires` and `sha256`; an unreadable zip comes out with `id` null and `problems: ["invalid: <reason>"]`.

### 10.3 Templates (`Library/LibraryStore`, `Library/TemplateEngine`)

`template.yaml` (§7.1): `template`, `description`, `parameters` (JSON Schema), `steps`. Reader `YamlReader.ReadTemplate(yaml) → TemplateReadResult(Template, LineMap)`
(`name:` is an unknown key; in a workflow `template:` is too) and writer `YamlEmitter.EmitTemplate(TemplateDocument)` / `EmitTemplateWithMap`, with the same
round-trip guarantees as workflows. `TemplateDocument { Name, Description?, Parameters?, Steps }` (JSON camelCase).

```csharp
Task<TemplateDetail> GetTemplateAsync(string name);                   // { manifest, template, layout?, parameters?, requires, schemas[], prompts[], sha256, warnings[] }
Task<LibraryItemSummary> CreateTemplateAsync(string name, TemplateContent content);   // desde cero; LibraryNameConflictException si existe
Task<LibraryItemSummary> SaveTemplateAsync(string name, TemplateContent content);     // PUT /library/templates/{n}: metadatos y contenido (RF-24, RF-25); conserva id y createdAt
class TemplateContent { string? Description; JsonObject? Parameters; List<StepNode> Steps; WorkflowLayout? Layout; Dictionary<string,string> Schemas; Dictionary<string,string> Prompts; }  // reemplaza todo el contenido
TemplateSelectionInfo SuggestTemplateParameters(PackageSession, IReadOnlyList<string> stepIds, WorkflowDocument? document = null);   // → { stepIds[], candidates[], schemas[], prompts[], warnings[] }
Task<LibraryItemSummary> CreateTemplateFromSelectionAsync(PackageSession, CreateTemplateRequest);   // { name, description?, stepIds[], parameters[{key,name,description?}], document?, overwrite }
Task<InstantiateResult> InstantiateAsync(string templateName, PackageSession target, InstantiateRequest request, IReadOnlyCollection<string>? availableTools = null);
IReadOnlyList<TemplateOrigin> TemplateOrigins(PackageManifest manifest);   // { template, templateId?, sha256?, insertedAt?, stepIds[], status: unchanged|changed|missing }
```

- **Validation** (on save, on import and before inserting; `InvalidTemplateException` with `Errors[]`): ids non-empty, without `/` and unique; no `dependsOn`; id and `type` are not parameters; parameter names `[A-Za-z_][A-Za-z0-9_]*` and different from `id`, `type` and `steps`; `parameters` an `object` schema that NJsonSchema reads, with `required` ⊆ declared;
  every `${{ params.x }}` in steps, container map keys and prompts names a declared parameter; any other `${{` is an error; `$${{` is the literal text. Warnings: unused parameters and schemas that are named but not included. At least one step.
- **Derived requirements** (`TemplateRequirements { roles, roleParameters, schemas, prompts, tools }`): literal `profileName`s; parameters `x-editor: role`; literal `inputSchema`/`outputSchema`;
  `systemPrompt`/`userPrompt` equal to a `.md` of the template; literal `tools`.
- **Insert** (`InstantiateRequest { params?, prefix?, existingIds?, x?, y? }` → `InstantiateResult { steps[], stepIds[], layout, resources { schemas[], prompts[], reused[], renamed{} }, warnings[] }`):
  validates the values with NJsonSchema after filling in the `default`s (an unknown parameter is always an error) → `TemplateParameterException { Errors[{parameter,message}] }` (422 `invalid_parameters`);
  substitutes in the tree (a value that is exactly one reference → JSON value with its type, and if the optional one has neither value nor default the property disappears; inside a text → invariant text, compact JSON
  for lists and objects, and an absent optional gives empty text with a warning); remaps ids (`prefix_id`, then the lowest free `_n ≥ 2` against `existingIds` ∪ the ids of the working copy's `workflow.yaml`); copies or reuses only the schemas and prompts
  that the steps name and the template carries (same name and same content: reuse; other content: `name_2`, `_3`… first free one or one with the same content; references are rewritten; `renamed` uses keys `schemas/<old>` and `prompts/<old>`);
  writes those resources and an entry in `manifest.templates` (`{ template, templateId, sha256, insertedAt, stepIds }`) in the working copy, and returns the steps so the frontend can insert them (a single undo step there). `layout`: the template's positions with the new ids, the
  top-left corner at `(x, y)` (0, 0 by default). Warnings (non-blocking): a role the package does not have (listing the ones it has), a tool not in `availableTools` (if given), an unknown step type, a schema that does not exist.
  An invalid value or a broken template writes nothing. `PackageReadOnlyException` (423) if the session is read-only; an invalid prefix → `ArgumentException` (400).
- **Save selection** (§7.6): `SuggestTemplateParameters` resolves the selection (one step, and goes up with its subtree; or several consecutive siblings in a list; otherwise `InvalidTemplateSelectionException`, 422) and proposes candidates
  `{ key, kind, stepId?, property?, value, suggestedName, selected }`: `role` (`profileName`, `selected: true`), `prompt` (`systemPrompt`/`userPrompt` that are a `.md` of the package), `maxIterations` and `contextKey` (literal keys of `context.set("…")`/`context.get("…")`
  in `script`, by regular expression: a computed key is not found). `CreateTemplateFromSelectionAsync` copies the steps (without `dependsOn`, with literal `${{` escaped to `$${{`), replaces each chosen candidate with `${{ params.name }}` and declares it
  with its current value as `default` (`x-editor: role|prompt`, `integer` with `minimum: 1` for `maxIterations`), copies the referenced schemas and prompts from the package, saves the layout relative to the top-left corner and validates the result. Inserting without values reproduces the original selection.
- **Origin** (§7.7): `TemplateOrigins` compares `manifest.templates` with the library by `templateId` (and otherwise by name). The `sha256` is a hash of the content (not of the zip): all entries except `manifest.json`, with `template.yaml` normalized (without the name, canonical emission), so renaming does
  not count as a change, but editing description, steps, parameters, layout, schemas or prompts does. It is informative: nothing reads it to run.
- Errors: `InvalidTemplateException` (422 `invalid_template`, `errors[]`), `TemplateParameterException` (422 `invalid_parameters`, `errors[]`), `InvalidTemplateSelectionException` (422 `invalid_selection`), plus those of §10.2.

## 11. Time limits, error types and provider models (2026-10-18, architecture §13.2)

- **`Engine/TimeoutSettings`** (internal): `From(environment)` reads `Timeouts` (`DefaultWorkflowTimeout`, `DefaultStepTimeout`, `Steps`, whole seconds; defaults
  1800 and 300 — `DefaultWorkflowSeconds`, `DefaultStepSeconds`, `MaxSeconds = 86400`); `Apply(steps, catalog)` sets `StepBase.Timeout` on the compiled steps that
  have none (`Steps[tag]`; container types with no entry get the workflow limit; a YAML `timeout` wins). `EngineServices` passes the workflow and step limits to the
  framework as `AITaskAgent:Timeouts`. `EnvironmentSections.Timeouts` / `ValidateTimeouts` validate the section (it is one of `EnvironmentSections.Names`).
  `StepCatalog.TagOf(Type)` gives the tag a step class is registered under.
- **`RunManager`** owns the workflow limit: `min(DefaultWorkflowTimeout, WorkflowDesignerOptions.RunTimeout)` with its own `CancellationTokenSource`; when it has fired the failed
  run reports `WorkflowTimeout`.
- **`Execution/ErrorTypes`**: `Of(IStepError?)` / `Of(Exception)` → `Timeout`, `WorkflowTimeout`, `Cancelled`, `LlmHttp<status>`, `LlmProvider`, `ToolIterationLimit`,
  `Validation`, `Exception:<TypeName>`. `RunInfo.ErrorType`, `TurnDto.ErrorType`, `data.errorType` of `step.completed` (on failure), `step.timeout` and `run.failed`.
- **`CatalogDto`** gains `Llm` (`ServiceTypeDto { Name, ListsModels }[]`) and `Timeouts` (`TimeoutDefaultsDto`).
- **`Engine/ProviderModelCatalog`** (public, registered with `TryAddSingleton`; tests pass an `HttpMessageHandler`): `ListAsync(ConfigStore, providerName)` →
  `ProviderModelsResult { Models: [{ Name, DisplayName? }], Source: api|cache }`; `ProviderModelsException.Code` ∈ `models_not_supported`, `models_no_key`,
  `models_bad_config`, `models_unauthorized`, `models_unreachable`, `models_failed`; `Supports(serviceType)`. Never returns, logs or quotes the key.

## 12. Workspace host and user settings (2026-10-19, architecture §13.5)

- **`WorkspaceHost(template, protector, keys, configure?)`** (`WorkspaceHost.cs`, `IAsyncDisposable`): the Core of the open workspace as a replaceable child container.
  `await host.OpenAsync(workspace, dataDirectory?)` builds the container (`AddWorkflowDesigner` over `template.CloneFor(workspace, dataDirectory)`, with the shared `protector` and `keys`
  pre-registered and `configure` applied first, for the host's own services), resolves `PackageStore` and `SessionManager` (so it follows resumes), swaps and disposes the previous container;
  when building fails nothing changes. `Services` / `Get<T>()` read the current one (`InvalidOperationException` before the first open), `Options` and `Workspace` describe it, `Keys` is the shared
  `MasterKeyProvider`. It does not check that nothing is running: use `RunManager.HasActiveRuns()` (true while a run is starting or executing; suspended runs do not count) and
  `PackageStore.OpenSessions()` / `HasChanges(session)` before switching.
- **`WorkflowDesignerOptions.CloneFor(workspace, dataDirectory)`**: a shallow copy with the two paths replaced.
- **`UserSettingsStore(path)`** (`Workspace/UserSettings.cs`): `Load()` (empty on a missing or unreadable file; relative paths are dropped), `RecordOpened(path)` (last + recent, at most
  `MaxRecent` = 10, no duplicates by file-system comparison; false when it could not write), `DefaultPath()` (`%LOCALAPPDATA%\WorkflowDesigner\settings.json`), `SamePath`, `Normalize`.
- **`WorkspaceLocations`** (`Workspace/WorkspaceLocations.cs`): `DefaultWorkspace()` (`%USERPROFILE%\WorkflowDesigner\workspaces\default`), `Resolve(explicit, settings, default)` -> `WorkspaceResolution(Path, Source)`
  (`Explicit` > `LastOpened` if the folder exists > `Default`), `FindGitRepository(path)` (a `.git` directory or file at or above it), `ValidateOpen(path, current, create)` (returns the
  normalized path or throws `WorkspaceOpenException` with `Code` `invalid_path`, `nested_workspace`, `not_a_workspace` or `not_empty`; it never writes) and `IsInside`.

## C# unit (architecture §15)

`PackageFormat.SharedCSharpEntry` (`csharp/shared.cs`), `WorkspaceStore.ReadSharedCSharp/SaveSharedCSharp/DeleteSharedCSharp`, `PackageManifest.CSharp`. `CSharpCode.Find(document, catalog, shared)` returns the `PackageCSharp` (steps' code, shared file, line counts, SHA-256) used by trust and the CLI. `WorkflowValidator` and `RunManager` compile the unit first (`PackageUnits`, internal; cache `<data>/csharp-cache`): errors become `WorkflowError` with `line`, `column`, `stepId`, `codeLine` or `file`; the unit's hooks and `[ResponseType]` names are known to the checks, and the run registers the unit (hooks `unit:<package>`, response-type schemas) in its engine collection, whose provider disposes it. `HookCatalog.ListForAsync(package)` returns `PackageHooksDto { hooks, diagnostics, responseTypes }`. `RunRequest.ExecTrusted` also gates `CSharpStep` (`DesignerCSharpPolicy`).

## Code editor (architecture §8.1, 2026-10-23)

`CodeCompletion` (`WorkflowDesigner.Core.Engine`, singleton of the Core and one of `WorkspaceServices.Bound`): `CompleteCSharpAsync(package?, CSharpCompletionRequest, ct)` completes C#
at the caret through `PackageUnits.CompleteAsync`, which builds the compiler exactly as the unit's compilation does (host references `UnitHostReferences`, the package's
locked NuGet packages with `allowNetwork: false`, `SharedAssemblySet.HostWins`) and calls the framework's `CSharpUnitCompiler.CompleteAsync` with the package's
`csharp/shared.cs` and schemas; without a package, or when its NuGet packages cannot be materialized, only the host references. Nothing is emitted or cached.
`CodeCompletion.Snippets()` returns the validator skeletons as `CodeSnippetDto { name, kind, description, header, code, language, members?, help }`.
`HookSlotDto.header` (catalog, additive) is the comment block a C# method for the slot starts with and `HookSlotDto.jsHeader` the one of a JavaScript function; they and
the snippets' headers come from `HookDocs` (internal, the single source since 2026-10-23: the structure of `args` only, C# by reflection, JavaScript from the
framework's `HookData`). Since 2026-10-23 the C# items carry the documentation of their symbol: `CompleteCSharpAsync` replaces it with the short Spanish meaning of
`HookDocs.Meanings` for the members of a hook's or validator's arguments (`CSharpCompletionItem.Container` names the declaring type). `HelpTopics` (public, static)
lists and reads the embedded help (`Help/*.md`, architecture §14.7).

**`POST /api/code/complete`** — body `{ "language": "csharp", "text": string, "caret": int, "packageId"?: string|null, "stepId"?: string|null }` (read with the
global body cap). With `stepId`, `text` is the body of that `CSharpStep` (a method that receives `StepCall call`); without it, `text` is `csharp/shared.cs` (it
replaces the package's). Answer 200 `{ "from": int, "to": int, "items": [ { "label", "kind", "detail"?, "boost"?, "info"? } ] }` (`info`, 2026-10-23: what the
symbol is, in one line: the Spanish meaning of a hook argument member or the summary of its XML documentation): `from`..`to` is the identifier being
typed (offsets of the request's text, CRLF included); `kind` ∈ `variable`, `property`, `method`, `class`, `interface`, `enum`, `type`, `namespace`, `constant`;
`boost` 3 locals/parameters, 2 members of the containing type, 1 members after a dot, -1 namespaces (absent = 0); overloads are one item (`detail` = first signature
+ ` (+N overloads)`); at most 300 items; symbols only (keywords and snippets are the client's). With nothing typed only locals, parameters and members of the containing
type come. Errors: `language` other than `csharp` → 400 `unsupported_language` (JavaScript, JSON and YAML complete in the client); `text` missing or `caret` outside
`[0, text.length]` → 400; `text` over 256 Ki characters → 413; `packageId` not open or unknown → the usual 404 `package_not_open` / `package_not_found`.
Timeout 5 s (`WorkflowDesigner:CodeCompletionTimeoutMilliseconds`), at most 2 completions at once (waiting counts against the timeout): past it → 200
`{ "from": caret, "to": caret, "items": [], "timedOut": true }`.

**`GET /api/code/snippets`** — `{ "snippets": [ { "name": "validator", "kind": "stepValidator", "description", "header", "code" }, { "name": "resultValidator",
"kind": "resultValidator", ... } ] }`. `code` is a whole method to paste in a public static class of `csharp/shared.cs` (unindented): `header`, `[Validator(Description =
"...")]` or `[ResultValidator(...)]`, `public static Task<(bool IsValid, string? Error)> Nombre(ValidateArgs args)` (or `ResultValidateArgs`) and a body that compiles;
`Nombre` is the placeholder name. Since 2026-10-23 (architecture §18.7) every snippet carries `language` (`csharp` for those two) and two more come with
`language: "javascript"`: `jsValidator` (kind `stepValidator`) and `jsResultValidator` (kind `resultValidator`), whose `code` is `header` +
`exports.Nombre = function (args) {\n  return true;\n};` for a library, and whose `members: [ { path, type, access, meaning, resultValidator } ]` describe
`args.result`, `args.step`, `args.step.name`, `args.vars` (source `HookDocs.JsMembers` over the framework's data view; `meaning` is one line). Every snippet has
`help` (`validator` / `resultValidator`).

**Header format** (since 2026-10-23, decided by the user: only the structure; Spanish access words, `//` lines joined with `\n`, no trailing newline). C#:
`// args: <ArgsType>` then one line per public member `//   <Member>: <type> — solo lectura|lectura/escritura|método` (read-only = no public setter or `init`;
a method as `As<T>(): T?`). JavaScript: `// args` then `//   <member>: <type> — solo lectura|lectura/escritura` (`JSON`, `string`, `number`, `boolean`,
`string | null`, an object as `{ name: string }`) and last `//   vars: objeto — lectura/escritura`. Nothing else: the meaning of each member is in the completion
popup and the detailed help in `GET /api/help/{topic}`.

**`POST /api/packages/{id}/code/check`** (2026-10-24, additive; the «Comprobar» of the hook and validator dialogs, architecture §14.7) — body
`{ "language": "csharp" | "javascript", "text": string, "library"?: string, "function"?: string }` (read with the global body cap). It checks the editor's text **without
saving it**: nothing is written to the package, the working copy or the compile cache, and none of the author's code runs in the backend process.
- `csharp`: `text` is compiled as the package's `csharp/shared.cs` together with its schemas and locked NuGet packages (never downloaded), by the same Roslyn
  compilation a run uses, through the new framework method `CSharpUnitCompiler.CheckAsync(sources, ct)` (`AITaskAgent.Extensions.CSharp`, additive): it emits into
  memory and drops the bytes, so it never loads the assembly and never caches it in memory or on disk. Step bodies (`CSharpStep`) are not part of the check.
- `javascript`: `text` is the library `library` (an existing one, which it replaces for the check, or a new one), joined with the other libraries of the run's
  catalog in one bundle and compiled in a **script worker** (never in this process); the worker then lists what the library exports by loading it in a throwaway
  context with the run's limits (`ScriptSyntax.CheckAsync`, the listing validation already used). Another library that does not compile is a warning with its
  `file`, and the checked one is listed again without it. With `function`, a library that does not export it is an error (`js_function`).
Answer 200 `{ "ok": bool, "diagnostics": [ { "severity": "error"|"warning", "message", "line"?, "column"?, "file"?, "code"? } ], "exports"?: [string] }`:
`line`/`column` (1-based) are positions **in the checked text**; a problem elsewhere carries `file` instead (`schema:<name>`, `scripts/<other>.js`) and no position;
`code` is the compiler id (`CS0103`, `WFCS001`…), `script_syntax`, `js_load` (the library threw while loading) or `js_function`; `ok` ignores warnings; `exports`
(JavaScript only) is the sorted list of exported functions when the library loaded. Errors: package not open or unknown → the usual 404; `text` missing → 400;
`language` other than those two → 400 `unsupported_language`; `javascript` without a valid `library` name → 400 `invalid_script_name`; `text` over 256 Ki characters →
413; a cross-site request → 403 (the same guards as every `/api` route). At most 2 checks at once (apart from completions); timeout 20 s
(`WorkflowDesigner:CodeCheckTimeoutMilliseconds`): past it → 200 `{ "timedOut": true }`. It needs no trust (compiling and the sandboxed listing run none of the author's
code in the host, as validation). Core: `CodeCompletion.CheckCSharpAsync(package, text, ct)` and `CheckJavaScriptAsync(package, library, text, function?, ct)` →
`CodeCheckResult { Ok, Diagnostics: CodeCheckDiagnostic[], Exports? }` (`Engine/CodeCheck.cs`, public, additive).

**`GET /api/help`** — `{ "topics": [ "hook.buildSystemMessage", …, "hooks", "resultValidator", "validator" ] }` (sorted). **`GET /api/help/{topic}`** — `{ "topic",
"markdown" }` (Spanish Markdown, the embedded `Help/<topic>.md`), or 404 `help_topic_not_found` for any other name (a path is never a topic).

## Plugins (architecture §25; they replace the dynamic extensions of §17 and the step packages of §21-§22)

`WorkflowDesigner.Core.Plugins`. A plugin is a NuGet package or a folder of DLLs (`plugin.json` + a `dotnet publish` output) that brings steps, tools, hooks, templates and settings; the framework contracts it implements are in architecture §25.1 and the loader is `AITaskAgent.NugetLoader` (`PluginLoader`). **Since 2026-10-22 a plugin is driven by one `IExtension`** (§25.3a): its `Register` registers everything, its `Info()` declares it, nothing of it runs before approval, and the loader compares the declaration with the real registration (`info-mismatch`); a plugin without an `IExtension` loads by type discovery as before.

- **`PluginService`**: one mutable service per workspace options object. `PluginService.Create(options, trust, logger?)` (a second call on the same options throws; internal `options.Plugins`, reset by `CloneFor`), `PluginService.Of(options)` (null = no plugins), `RepositoryDirectory(options)` (`<data>/nuget-repo`). Hosts: `services.AddPlugins(options, trust, logger?)` + `await provider.LoadPluginsAsync()`, or `WorkspaceHost.PluginTrust` (`Func<WorkflowDesignerOptions, ITrustPolicy>`) / `PluginLogger` for a host that switches workspaces (`OpenAsync` awaits the load before building the container). The `StepCatalog` attaches itself (`PluginsChanged` → `Refresh()`).
- **Operations** (problems are reported, not thrown): `LoadAsync` (reloads `plugins.json` from folders and cache, never a source, then rescans), `RescanAsync` (folders of the roots that `plugins.json` does not record go through the trust policy: pending, nothing loaded), `InstallAsync(id, version, PluginApproval?)` (NuGet), `InstallFolderAsync(folder, id?, PluginApproval?)`, `RemoveAsync(id)`, `ListAsync()` / `Describe()` → `PluginListDto`, `FindLoaded(id)`, `FindInstalled(id)`, `ScopeOf(folder)`, `Installed`, `Loaded`, `StoreProblems`, `Revision`, `ResponseTypeNames`, `event PluginsChanged`. `PluginInstallResult(Report, Pending, Installed)`; `PluginApproval(Sha512, Closure?)`; `ApprovalTrustPolicy(TimeProvider?)` is the two-step policy of the web backend (a request is shown, then approved by its hash within 30 minutes), `PluginTrust.ClosureOf(request)` its digest.
- **Local repository** (`<data>/nuget-repo`, a NuGet source of the plugins only; adding registers and loads nothing): `AddNupkgAsync(Stream)`, `AddNupkgFileAsync(path)`, `AddNupkgFolderAsync(folder)`, `ListRepository()`, `RemoveFromRepository(id, version)`; errors are `NupkgException.Code` (`invalid_nupkg`, `package_conflict`, `too_large`, `invalid_path`).
- **Roots** (`PluginRootDto { path, origin: explicit|workspace|install, exists }`, in this order): `WorkflowDesignerOptions.PluginDirectories` (`WORKFLOW_DESIGNER_PLUGINS` merged by `PluginDirectoryConfig.Resolve`), then, unless `AutoDiscoverPlugins = false`, `<Workspace>/plugins`, `<Workspace>/extensions` (legacy: `extension.json` is read as `plugin.json`, warning `legacy-manifest`) and `<install dir>/plugins`. `MaxPluginUploadBytes` caps a `.nupkg` for the repository.
- **States** (`PluginStates`): `loaded`, `pending-trust`, `restart-required`, `error`. `PluginDto` carries steps, tools, hooks, response types, `hasTemplates`, `configurationSchema` (secret fields flagged `x-secret`), `rootSections`, warnings and notes; `CatalogDto.plugins` lists the loaded ones, `pluginProblems` the problems; `StepTypeDto.plugin { id, version, origin }` names the owner of a step.
- **Engine wiring** (`PluginWiring`, `EngineServices.Create`): the configuration is composed (`Plugins.<id>` of `config.json` as `AITaskAgent:Plugins:<id>`; the plugin's `Defaults` below it, its `RootSections` lifted to the root above it), hooks are registered with source `plugin:<id>`, templates chain after the package's `prompts/`, tools are listed as `Plugin:<id>` (`ToolCatalog`; a plugin tool with the name of a FileTools tool wins with an informative problem, with the name of an MCP tool of the package it is `tool_ambiguous`), response types reach the schema compiler, and the plugins' step types are added after `ConfigureEngineServices`. `services.AddPlugin(assembly, id, folder?)` (`PluginServiceCollectionExtensions`) wires a plugin project that a host or a test references directly (an assembly with one `IExtension` is driven by it). `PluginParts.From(LoadedPlugin)` is the one seam: it reads `LoadedPlugin.Extension` (instances handed to the capturing builder, the services it added, its configuration as `ExtensionConfiguration`) or, without an extension, the discovered types. `PluginDto.extension` (`PluginExtensionDto { type, declared, registered, missing, extra }`) shows the declaration and any difference; `info-mismatch`, `info-failed` and `ignored-extension` are warning codes.
- **Plugin prompts** (read-only): `PluginPrompts.List(LoadedPlugin)` → names of `<plugin folder>/templates/*.md`; `PluginPrompts.Read(LoadedPlugin, name)` → text (`InvalidWorkspaceNameException` 400, `FileNotFoundException` 404). Only for a plugin with a template source; routes `GET /api/plugins/{id}/prompts[/{name}]` (architecture §25.10).
- **Packages**: `ManifestRequires.Plugins` (`PluginRequirement(Id, Origin, Min, Sha)`; a manifest with `requires.extensions` is read as `plugins` and never written back), `PluginRequirements.Derive/Of/Check` (errors `plugin_missing`, `plugin_version`, `step_unknown`; a different `sha` is a warning; `TrustFragment` = `|plugin:…`), `PluginUse.Find(document, catalog)` (what the trust dialog and the CLI list), `PackageStore.DeclarePlugins(session, requires)` (saving `workflow.yaml` also saves `manifest.requires`). `PluginConfigMigration.Migrate` turns `Extensions.<Name>` of `config.json` into `Plugins.<id>` in memory (written on the next save); `EnvironmentSections.Plugins` and `LegacyExtensions` are reserved names.
- **Removed with the migration**: `ExtensionHost`, `ExtensionLoadContext`, `ExtensionDirectoryConfig`, `ExtensionRequirements`, `ExtensionUse`, `ExtensionNuget`, `StepPackageService`, `StepCatalog.Extensions`, `ExtensionDto`, `WorkflowDesignerOptions.ExtensionDirectories`/`AutoDiscoverExtensions`/`ExtensionHostCache`/`StepPackages`, `PackageStore.DeclareExtensions`.

## NuGet per package (architecture §16)

`PackageManifest.Requires.Nuget` (`NugetRequirement(Id, Version)`: a NuGet version or range, floating versions refused by `Problem()`), `PackageFormat.NugetLockEntry` (`nuget.lock.json`), `WorkspaceStore.ReadNugetLock/SaveNugetLock/DeleteNugetLock`, `PackageStore.SaveNuget(session, requires, lock)`. `NugetResolver.RestoreAsync(requirements, options)` resolves into a `NugetLock` (exact versions, SHA-512 of each `.nupkg`, packages the host supplies) and `MaterializeAsync(lock, options)` returns the `NugetEnvironment` (assembly paths, native folders, notes) from the private cache, fetching by hash when `options.AllowNetwork` and a source has the package. `PackageNuget` (registered singleton) is what the hosts use: `Describe(package)` → `PackageNugetState` (`None`, `NotRestored`, `Stale`, `Ready`), `RestoreAsync(requires, lockText, force)`, `EnvironmentAsync(package, allowNetwork)`, `TrustItems(requires, lockText)`. `PackageUnits` passes the environment to `CSharpCompileOptions.AdditionalReferences`/`NativeLibraryDirectories`; a package whose packages cannot be materialized is a unit error (`NuGet: …`), not an exception. Options: `NuGetSources`, `NuGetCacheDirectory`; `NugetSourceConfig.Resolve(explicit, userSettings)` merges the sources (enabled entries of `UserSettings.NuGet.Sources` and the old `NuGetSources` list). `NugetUserConfig` (`Validate`, `Describe`, `ResolveCache`, `InspectCache`, `ProbeAsync`) backs the settings screen (architecture §20.2) and `UserSettingsStore.SaveNuGet` writes it. Importing part of a configuration: `ConfigSections` (`Describe`, `Normalize`) and the `sections` argument of `LibraryStore.ApplyConfig`/`ApplyConfigFromPackage`/`ApplyConfigObject`, `DescribeConfigSections`, `DescribePackageConfigSections` (architecture §20.1).

## JavaScript of a package (architecture §18)

`PackageScripts` (`WorkflowDesigner.Core.Engine`): `Describe(options, package | libraries + requested)` → `PackageScriptInfo { Libraries, HostFunctions }` (what trust and
the CLI show; `Canonical()` feeds the trust hash), `Requested(manifest)`, `HostFunctions()` (the built-ins, sorted). `WorkspaceStore.ListScripts/ReadScript/SaveScript/DeleteScript`
(`scripts/<name>.js`), `PackageStore.SaveScriptHost(session, names)` (the request, in `manifest.requires.hostFunctions`), `PackageManifest.Scripts` and
`ManifestRequires.HostFunctions`. `WorkflowDesignerOptions.ScriptFileRoots` and `ScriptGrantableFunctions` are host settings (never in a package).
`WorkflowValidator` adds `script_syntax`, `script_library_unknown`, `script_host_function_unknown` errors and `script_host_function_not_granted`/`script_host_call_not_allowed`/
`script_require_not_declared` warnings; the grant of a run is `PackageScripts.For(options, package, environment, trusted)`.

JavaScript validators and script objects (2026-10-23, architecture §18.7): `PackageScripts.EngineCatalog(options, package?)` → `(IScriptCatalog, Problem?)` (the
catalog a run gets: built-ins, the host's and the plugins' libraries and script objects, the package's libraries); `PackageScripts.Catalog(options)` →
`(ScriptHostFunctionInfo[], ScriptLibraryInfo[])` (what `GET /api/script/catalog` answers: host functions with `object` for script object methods, libraries with
`source`); `PackageScripts.FunctionsAsync(options, package?)` → `(ScriptFunctionDto[], problems)`. New validation codes, located on the line of `validators` /
`resultValidators`: `js_validator_name`, `js_validator_library_unknown`, `js_validator_function`, plus `script_catalog` (two libraries or functions with one name);
a plugin's or the host's library that does not compile is a `script_syntax` error without `file`. `PluginDto` gains `scriptObjects: [ { name, type, methods,
description } ]` and `scriptLibraries: [name]` (loaded plugins; warnings `script-object`, `script-library`).
