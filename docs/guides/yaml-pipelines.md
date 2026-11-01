# Declarative Pipelines in YAML (v2)

A pipeline can be written in YAML instead of C#. The engine reads the files, converts each document to `JObject`/`JToken`, creates each step with the `IStepRegistry` from its `type:` and its configuration, validates that configuration against the step description (`IStepRegistry.Describe`) and checks the whole graph before running anything. No C# type is generated or compiled: the inputs and outputs of declarative steps are JSON validated at runtime against a JSON Schema (NJsonSchema), so the engine, the telemetry and the events see a normal `IStepResult`, the `JsonStepResult`, whether the step was written in C# or declared here.

This version is **not compatible with the previous YAML** (`!LlmStep` tags, `stepId`, schemas compiled with Roslyn) and there is no migrator. A file with an application tag `!Algo` fails with an error that points to the new format. The list of changes is in [ARCHITECTURE-v2](../ARCHITECTURE-v2.md).

## Assemblies and Registry

The engine lives in `AITaskAgent.Yaml`, which depends only on `AITaskAgent.Core` and therefore does not know what an LLM is. The steps it can declare are provided by the registry, that is, by the extensions the application has added: the core registers `GroupStep`, `ParallelStep`, `LoopStep`, `BreakStep`, `SwitchStep`, `SuspendStep` and `ExecStep`; `AITaskAgent.Extensions.Llm` registers `LlmStep` and `AITaskAgent.Extensions.Script` registers `ScriptStep`; `AITaskAgent.Extensions.CSharp` registers `CSharpStep` (`AddCSharpExtension()`); a custom step comes in with a `[StepTag]` and an extension (see [Custom extensions](../extensibility/custom-extensions.md)).

```csharp
services.AddSingleton<IConfiguration>(configuration);
services.AddAITaskAgent();          // core + LLM (LlmStep); with only AddAITaskAgentCore() there are no LLM steps
services.AddScriptExtension();      // optional: ScriptStep (JavaScript)
services.AddAITaskAgentYaml();      // SchemaRegistry + YamlPipelineFactory + HookRegistry / IHookRegistry (singletons)

var provider = services.BuildServiceProvider();
provider.UseAITaskAgent();

await provider.GetRequiredService<SchemaRegistry>().LoadFromFolderAsync("schemas");
YamlPipeline pipeline = await provider
    .GetRequiredService<YamlPipelineFactory>()
    .CreateFromFolderAsync("pipelines");

IStepResult result = await pipeline.ExecuteAsync(input, cancellationToken: ct);
```

`CreateFromFolderAsync` concatenates all the `*.yaml` files in the folder tree, sorted by path, as documents separated by `---`; `CreateFromYaml(string)` accepts the text directly. The steps of all the documents are added up into a single pipeline whose name is the first non-empty `name`, and a document only accepts the keys `name` and `steps`, so any other key is an error. The schemas must be loaded before building the pipeline, because the factory resolves them by name when it creates each step.

## Grammar

A step is a map with a small set of structural keys, and everything else is the step configuration, which is flat and uses the names of its public properties in camelCase.

| Key | Where | Meaning |
|---|---|---|
| `id` | every step | Required. Unique across the **whole** document, nested ones included, and without `/` (the slash separates cache paths). It becomes the step's `Name` |
| `type` | every step | Registry tag (`LlmStep`, `GroupStep`...). Case-sensitive. It can be omitted only if the step declares `steps:`, and then it is an implicit `GroupStep` |
| `dependsOn` | top level only | Id or list of ids of other top-level steps. Defines the order through topological sorting. A non-existent id, a nested one or a cycle are errors |
| `inputSchema` / `outputSchema` | steps that implement `IJsonSchemaStep` (today `LlmStep` and `SuspendStep`) | Name of a schema in the `SchemaRegistry`. On another step type they are an error |
| remaining keys | depending on the type | Public properties with a setter on the step, validated by name and type against `Describe(type)`. An unknown or wrongly typed one fails with the list of valid ones |

Note: `dependsOn` is optional. Without it, the steps run in declaration order. If steps with and without `dependsOn` are mixed, the execution order may differ from the written one: `A`, `B` (`dependsOn: A`), `C` declared like this run as `A, C, B`, because topological sorting runs the steps without dependencies first (in declaration order) and then the ones that depend on them.

A public property with a setter whose type is `List<IStep>` or `Dictionary<string, List<IStep>>` is a **container property**, so its YAML value is built recursively into steps with the same registry and the same rules, with the only depth limit imposed by the YAML reading (see below). Today these are `steps` in `GroupStep` and `LoopStep`, `branches` in `ParallelStep`, and `cases` and `default` in `SwitchStep`.

The following example goes through the five flow-control constructs. The factory builds and sorts it (classify, route, refine, fan, wrap); the rules it illustrates are covered by `Tests/YamlPipelineV2Tests.cs`, which uses an LLM double (`ScriptedLlmService`, in `Tests/YamlTestKit.cs`) and does not call any model.

```yaml
# One file = one document. Several files or several "---" documents form ONE pipeline.
name: DocumentPipeline
steps:
  - id: classify
    type: LlmStep
    inputSchema: DocumentInput          # file DocumentInput.json (case-insensitive, ".json" optional)
    outputSchema: Classification        # the response is requested and validated against this schema; if it does not comply, retry with the errors
    profileName: default                # LlmEnvironment role; without it, DefaultRole. A non-existent role fails at build time
    useConversationHistory: false
    maxRetries: 2                       # the public properties inherited from StepBase also apply
    systemPrompt: classifier            # template name (ITemplateProvider) or inline text
    userPrompt: |                       # literal block: line breaks, quotes, {, }, \, :, # and emoji arrive intact
      Document: {{input.content}}
      Return "type" and "confidence".

  - id: route
    type: SwitchStep
    dependsOn: [classify]
    on: input.type                      # template expression (without {{ }} it is wrapped automatically); it is not JavaScript
    cases:                              # key = expected value; compares without regard to case or surrounding spaces, and two keys that differ only in that are an error
      invoice:
        - id: extract_invoice
          type: LlmStep
          userPrompt: >                 # folded block: lines are joined with spaces
            Extract amount, vendor
            and date.
      report:
        - id: inner                     # containers can be nested
          type: SwitchStep
          on: "{{ input.confidence }}"
          cases:
            "1": [ { id: sure, type: LlmStep, userPrompt: "Summarize" } ]
          default: [ { id: unsure, type: LlmStep, userPrompt: "Ask for review" } ]
    default:                            # no default and no matching case: error at execution time
      - { id: other, type: LlmStep, userPrompt: "Describe it" }

  - id: refine
    type: LoopStep
    dependsOn: [route]
    maxIterations: 5                    # cap on iterations (50 by default)
    outputIterationToMetadata: iteration  # {{context.iteration}} = current iteration, from 1 to n
    steps:                              # sequential; the output of one iteration feeds the next
      - id: improve
        type: LlmStep
        outputSchema: Review            # e.g. { "done": boolean, ... }
        userPrompt: "Iteration {{context.iteration}}: improve it"
      - id: stop
        type: BreakStep
        when: input.done                # exits if true (empty, false, 0, no and null are false); without 'when' it always exits

  - id: fan
    type: ParallelStep
    dependsOn: [refine]
    maxDegreeOfParallelism: 2
    failFast: true                      # default; false = a failed branch does not fail the step (see below)
    branches:                           # each branch is a step; the result is a ParallelResult per branch id
      - { id: tone, type: LlmStep, userPrompt: "Check tone" }
      - id: facts                       # multi-step branch: without 'type' and with 'steps' = implicit GroupStep
        steps:
          - { id: facts1, type: LlmStep, userPrompt: "List claims" }
          - { id: facts2, type: LlmStep, userPrompt: "Verify them" }

  - id: wrap
    type: GroupStep                     # explicit sequence; also valid without 'type'
    dependsOn: [fan]
    steps:
      - { id: final, type: LlmStep, userPrompt: "Write the final answer" }
```

Values are interpreted with the rules of the YAML 1.2 core schema, so quoted scalars and the `|` and `>` blocks are always literal text, while plain ones are `null` (`~` or empty), `true`/`false`, integers, decimals or, if none of these match, text. A number or boolean where the property expects text is accepted with its original text (`on: 1` arrives as `"1"`); an enum accepts the matching text case-insensitively; and `null` (`~`, `null` or empty) means "not set" for any type, integers and booleans included: the property is omitted from the configuration and the step keeps its default value, so `prop: ~` no longer assigns null and cannot be used to clear a property that has a default.

Reading the YAML has a budget so that a hostile or careless document cannot exhaust the machine. The maximum depth is 64 levels (about 20 levels of step nesting), a document cannot expand to more than 200,000 nodes (each use of an alias counts again, so a few cascading aliases exhaust it), and an alias that contains itself is rejected as a cycle. All three cases are errors at build time, with the YAML line and column, and none of them is configurable.

### Nested Flow Control

`GroupStep` runs its `steps` in sequence within the same context, while `ParallelStep` runs its `branches` at the same time on branches of the context (`CloneForBranch`: its own conversation and path, shared `Metadata` and `StepResults`), and gives each branch a shallow copy of the input, so two branches whose first step schedules children over it do not write to the same `NextSteps`. `LoopStep` repeats its `steps` until `maxIterations` or until a `BreakStep` exits, and `outputIterationToMetadata` publishes the current iteration in `context`. `SwitchStep` (the `DeclarativeSwitchStep` class) evaluates `on` with the template engine, over a `{ input, context }` model like the one used by the prompts, and runs the steps of the matching case or those of `default`. `GroupStep` passes the result of its last step on to the next step.

`ParallelStep.failFast` (default `true`, the behaviour the step has always had) decides what a failed branch means for the step itself. With `true`, if any branch fails the `ParallelResult` carries an aggregate error and the pipeline stops there. With `failFast: false` the step returns **no** aggregate error, not even when every branch failed: the error of each failed branch stays in its own entry of the `ParallelResult` (`Value["id"].HasError` / `.Error`) and in its `BranchCompletedEvent` (`Success = false`, `ErrorMessage`), and whoever reads the result decides what to do with it. In both modes a failing branch does not cancel the others.

The exit condition of a loop is not a field of `LoopStep` but a `BreakStep` with `when`, which returns the input unchanged (and the loop continues) while the expression is false. An empty or blank `when` is a construction error, because previously it did not amount to a false condition but instead always broke on the first iteration; without `when`, or with `when: ~`, the step always breaks.

## Schemas

A schema is a JSON Schema file whose name is the file name without its extension (`schemas/Classification.json` is called `Classification`), with `title` playing no part. The `SchemaRegistry` loads them with `LoadFromFolderAsync(folder)` (subfolders included; relative `$ref` values are resolved against the file) or one at a time with `AddAsync(name, json)` and `Add(name, JsonSchema)`; two schemas with the same name are an error, even if they come from different folders. The lookup ignores case, so it accepts the name with or without `.json`. Schemas are optional: `GroupStep`, `LoopStep` and `SwitchStep` do not accept them, an `LlmStep` may declare one, both or neither, and a `SuspendStep` uses only `outputSchema`, to validate the response with which it resumes.

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "properties": {
    "type":       { "type": "string" },
    "confidence": { "type": "number" }
  },
  "required": ["type", "confidence"],
  "additionalProperties": false
}
```

The `outputSchema` of an `LlmStep` does three things at once: it goes to the provider as `response_format` (or it is injected into the system prompt if the model does not support JSON), it turns the step result into an `LlmJsonStepResult` (an `LlmStepResult<JToken>` that keeps the assistant message and the metrics), and it validates the response with `JsonStepResult.Validate`. Validation goes through the structural validation step of `RetryMiddleware`: a failure retries with the list of violations (path and type) as feedback and, once the attempts are exhausted, returns an error with that list. Non-LLM steps return a `JsonStepResult`, which carries its own `Schema` per instance and validates in the same way.

`inputSchema` is only checked in two places: against the pipeline input, when the first step declares it (a failure returns an `ErrorStepResult` without running any step), and in the compatibility between steps with `dependsOn`, where, if both ends declare a schema, the `outputSchema` of the step depended on and the `inputSchema` of the dependent step must have the same name. An intermediate step does not validate its input when it runs, and the sample schemas carry `"additionalProperties": false` because providers with strict JSON Schema require it.

## Execution and Input

`YamlPipeline.ExecuteAsync(input, context?, ct)` passes through as-is an input that is already an `IStepResult`, and wraps any other input (a `JObject`, a POCO, a string) in a `JsonStepResult`, where a string is JSON text that is not parsed. LLM steps require an `LlmPipelineContext`, which with `AddAITaskAgent()` is already the default context, and with a base one the pipeline fails before running anything, with a message that names the step. The check also walks the steps of each case and of a `SwitchStep`'s `default`, without waiting for the selector to choose a branch. Since the pipeline input and the output of each step with a schema are JSON, `{{input.field}}` in the prompts accesses their properties.

If the context passed contains a suspended execution (`PipelineResume.IsSuspended(context)`), the call resumes it: the input is the response to the step that suspended and is not validated against the `inputSchema` of the first step, and the steps that already finished are served from the context cache. The context can also come pre-built, with initial `Metadata` and a previous conversation (`LlmPipelineContextFactory.CreateForConversationAsync`); see [Human-in-the-Loop](human-in-the-loop.md).

## `SuspendStep`: Requesting More Information

Suspends the pipeline so that someone (a person or another system calling the flow) provides information, and on resume returns the response as its result, so that the next step reads it with `{{input...}}`. It works at the top level and inside `GroupStep`, `LoopStep` and `SwitchStep`, but not inside a `ParallelStep` branch, because that suspension is not propagated (known limitation).

```yaml
  - id: ask
    type: SuspendStep
    dependsOn: [draft]
    message: "Do you approve order {{input.orderId}}?"   # template text over { input, context }
    dataToReview: input.order        # path: keeps the JSON; other text is rendered; without it, the whole input
    marker: awaiting-approval        # SuspendInfo of the result; default awaiting-input
    outputSchema: Approval           # optional: the response must comply with it
```

The first execution returns an `AwaitingInputResult` (`SuspendInfo`, `Message`, `DataToReview`). On resume, a `JToken` or object response remains JSON and a string is text. If it does not comply with the `outputSchema`, the step suspends again with the errors in `RejectionReason` instead of failing. An empty `marker` is a construction error. Resumption, the persistent context store and their limits are described in [Human-in-the-Loop](human-in-the-loop.md).

## `ExecStep`: Running an External Executable

`ExecStep` (in the core, so every host that calls `AddAITaskAgentCore()` has it) runs an external executable or batch file (`.exe`, `.bat`, `.cmd`) **without a shell** and returns the text of its standard output as its result. It is not an interactive shell and nothing interprets the command line. Whoever writes the flow takes the risk, as when writing C# and running something: the command runs with the privileges of the host process.

```yaml
  - id: build
    type: ExecStep
    executable: dotnet                 # literal: templates are refused here
    args: ["build", "-c", "{{ input.config }}", "--nologo"]   # each item rendered on its own, and it is ONE argument
    workingDirectory: C:\work\proj     # template allowed; default: the host process's
    env:                               # values accept templates
      DOTNET_CLI_TELEMETRY_OPTOUT: "1"
    inheritEnvironment: minimal        # minimal (default) | all
    stdin: "{{ input.text }}"          # optional; written, then stdin is closed
    timeoutMs: 60000                   # default 60000
    okExitCodes: [0]                   # default [0]
    maxOutputChars: 1000000            # per stream (stdout, stderr); default 1000000
    encoding: utf-8                    # default utf-8; also windows-1252, ibm850...
```

Arguments are passed with `ProcessStartInfo.ArgumentList`: a rendered value with spaces, quotes, `&`, `|` or `;` stays one argument, so a template in `args` cannot inject shell commands. A value that starts with `-` can still be read as an option by the target program (argument injection), which the step cannot prevent. A bare name such as `dotnet` is looked up in `PATH`.

**Batch files.** On Windows a `.bat` or `.cmd` is run as `cmd.exe /d /s /c` with every argument double-quoted. Inside quotes `cmd` treats `& | < > ^ ( )` literally, but it expands `%var%` and cannot escape a `"`, so an argument (or the executable path) that contains `"`, `%`, a line break or NUL is refused with an `ExecStepException` (`InvalidConfiguration`) instead of being escaped. What the batch file does with its own arguments (`call`, an unquoted `%1`...) is outside the step: do not give untrusted text to a batch file.

**Environment.** With `inheritEnvironment: minimal` (the default) the process receives only what it needs to run (`PATH`, `PATHEXT`, `SystemRoot`, `SystemDrive`, `windir`, `ComSpec`, `TEMP`, `TMP` on Windows; `PATH`, `HOME`, `TMPDIR`, `LANG`, `LC_ALL` elsewhere) plus `env`. `all` inherits the whole environment of the host, secrets included. The values of `env` and the text of `stdin` are never written to events, logs, error messages or the policy request.

**Result.** The text of stdout exactly as the program wrote it (a `StepResult<string>`): the trailing line break is **not** removed, because no step of the framework trims it. stderr is not part of the result; it is kept for the error message.

**Errors.** Every failure carries an exception, so `RetryMiddleware` does not run the command again (`maxRetries` is 1 by default; raising it re-runs the command). An exit code outside `okExitCodes`, a command the policy denies, a process that cannot start, an invalid property or output over `maxOutputChars` are an `ExecStepException` (`Kind`: `NonZeroExit`, `PolicyDenied`, `StartFailed`, `InvalidConfiguration`, `OutputLimit`; a host that classifies errors by exception type sees `Exception:ExecStepException`). For a non-zero exit it carries `ExitCode` and the last 2000 characters of stderr. A timeout (`timeoutMs`, or the `timeout` of the step in the pipeline, whichever comes first) is a `TimeoutException` and a cancellation an `OperationCanceledException`; in both, and when the output limit is hit, the process tree is killed first.

**Killing the tree.** On Windows the process runs in its own job object (`KILL_ON_JOB_CLOSE`), so grandchildren, orphans included, die with it on timeout, cancellation and output limit, and also when the step ends normally: a child left running in the background does not outlive the step. Only a process spawned between the start and the assignment to the job can escape. On Linux and macOS only `Process.Kill(entireProcessTree: true)` is used: it kills the descendants that exist at that moment, so a grandchild whose parent already exited survives, and background children survive a normal end. This limitation is not covered by the tests (they run on Windows).

**Policy.** A host can register an `IExecStepPolicy` in DI. Before starting the process the step calls `EvaluateAsync(ExecRequest, ct)` with the rendered command (executable, arguments, working directory, names of the environment variables, whether the whole environment is inherited, whether it is a batch file) and the policy returns `Allow()`, `AllowIn(directory)` (the command runs in that directory, to confine it) or `Deny(reason)`. A policy that throws denies. Without a registered policy everything is allowed.

## The `LlmStep` Step

It is the only LLM step that YAML knows about, because the others (`TemplateLlmStep<,>`, `IntentionAnalyzerStep<>`, `RouterStep`) are generic or need constructor arguments and cannot be created from a registry; its own keys are the following.

| Key | Meaning |
|---|---|
| `profileName` | Role of `LlmEnvironment`; without it, the `DefaultRole`. The role is resolved when the registry finishes populating the step, so a non-existent role fails when the pipeline is **built**, with the list of available roles |
| `systemPrompt` / `userPrompt` | Prompt text, resolved as described below. Without `systemPrompt` the role's is used; without `userPrompt` the input is sent serialized as JSON |
| `useConversationHistory` | `false` starts with a clean conversation (default `true`) |
| `tools` | List of tool names offered to the model (`tools: [view_file, list_dir]`). They are looked up, case-insensitively, among the `ITool` instances registered in the `IServiceProvider` with which the pipeline is built, then in its `ITools`, and finally among the `ToolAlias` services (in that order, `YamlLlmStep.ResolveTools`); the bare name wins over an alias of the same name. The framework itself creates no `Source:name` aliases: a qualified name such as `Plugin:x:view_file` only exists when the host registers a `ToolAlias` for it (the workflow designer does, in `Samples/WorkflowDesigner.Core/Engine/EngineTools.cs`). Never in a static registry of another provider. A name that does not exist throws an `InvalidOperationException` when **building**, listing the available ones. Without `tools` (or empty) the role's tools are used, as before |
| `inputSchema` / `outputSchema` | See the schemas section |

`LlmStep` also inherits `maxRetries`, `timeout` and `maxToolIterations` from `BaseLlmStep` and `StepBase`, and those work. The ones that are useless in YAML (`prompt`, which would be ignored because the message is `userPrompt`, and the live `Tools` property, `tagHandlers` and `enrichable`, which are live objects; the `tools` key is the list of names above, `YamlLlmStep.ToolNames`) are hidden with `[JsonIgnore]`: writing them gives the unknown-property error with the list of valid ones.

### Prompt Templates

Both prompts go through `PromptTemplateResolver`, the same one used by `TemplateLlmStep`, with the `{ input, context }` model (`input` is the output of the previous step, `context` is the pipeline metadata). If there is no `ITemplateProvider` in DI, the text is rendered inline with `TinyTemplateRendered` (`{{input.content}}`, `{{context.key}}`, filters such as `| json`, and inclusions `@[relative/path.md]` resolved against the working directory). If there is one, the whole value is passed to `ITemplateProvider.Render` as a template **name** (with `FileTemplateProvider`, `<folder>/<value>.md`); if no template exists with that name, the text is returned **unrendered**, placeholders included. There is no `inputData` variable: each step receives the output of the previous one and there is no access by name to the results of other steps.

## `ScriptStep`: JavaScript declarable

`ScriptStep` (extension `AITaskAgent.Extensions.Script`, which depends only on Core; V8 lives in its worker process) runs JavaScript without an LLM, as the declarable counterpart of `DelegatedStep`. It is registered with `services.AddScriptExtension()` and declared with `type: ScriptStep`.

```yaml
- id: totals
  type: ScriptStep
  timeoutMs: 2000                  # optional, 5000 by default; <= 0 is an error
  maxHeapMb: 32                    # optional, 64 by default; from 8 to 128 (more is an error)
  maxStackKb: 256                  # optional, 512 by default; from 64 to 2048 (more is an error)
  script: |
    const subtotal = input.lines.reduce((s, l) => s + l.qty * l.unitPrice, 0);
    const tax = Math.round(subtotal * input.taxRate * 100) / 100;
    log("customer", input.customer);
    return { customer: input.customer, subtotal, tax, total: subtotal + tax };
```

The text of `script` is the **body** of `function (input, context)`: the value of the `return` is the result of the step, and a final expression without `return` is not returned (if there is no `return`, the result is `null`). Only values representable in JSON survive. Inside the script three things are available.

- **`input`:** the value of the previous step as a JavaScript object (it goes through JSON, a string remains a string).
- **`context.get(key)` / `context.set(key, value)`:** reading and writing the pipeline variables (`PipelineContext.Metadata`). Only JSON values are exchanged: `get` returns `undefined` for a missing key and for arbitrary .NET objects, and `set` stores a `JToken`.
- **`log(...)` and `console.log/info/warn/error/debug(...)`:** write to the step's `ILogger` at the corresponding level; anything that is not a string is logged as JSON, truncated to 8 KB.

Four more things exist and are all closed by default (nothing a script can name that the step did not declare).

- **`require(name)` and `uses: [name, ...]`:** a CommonJS `require` over the libraries the step lists in `uses` (the libraries are registered in the host's `IScriptCatalog`; the designer takes them from the package's `scripts/<name>.js`). `require` of anything else throws `module 'x' is not declared in 'uses'`.
- **A native library, with no I/O:** `hash.sha256/sha1/md5(text)`, `base64.encode/decode(text)`, `uuid.v4()`, `date.format(value, format, tz?)` / `date.parse(text, format, tz?)` (invariant culture; `tz` is `UTC`, an offset such as `+02:00` or a system time zone id), and `path.join/basename/dirname/extname/normalize` (pure string logic).
- **`host.<ns>.<fn>(args)` and `allow: [ns.fn, ...]`:** host functions run in the host process through the pipe. The methods of the **script objects** an extension registers (`builder.AddScriptObject<T>("name")`, architecture §18.7) are host functions too, `host.name.method(args)`, granted by their registration (they still go in `allow`). **Default deny:** a function exists in the script only if the step lists it in `allow` **and** the host grants it (`ScriptHostOptions.GrantedFunctions`, empty by default); asking for one that is not granted fails the step before the script starts. The built-ins are `config.get(name)`, `file.readText(path)` and `file.exists(path)` (the file ones only under `ScriptHostOptions.FileRoots`, UTF-8 text, at most 1 MiB; no roots, no files). `maxCalls` (100, at most 10 000) and `maxCallKb` (1024, at most 8192) bound the calls per run; a host error is a catchable JavaScript `Error`.
- **`nextStep({ type, id, ...properties })`:** queues a step (the shape of YAML v2); when the script succeeds the host builds each one with the step registry and returns them as `NextSteps` (at most 50; a missing or repeated `id` or an unknown `type` fails the step). Steps with children (`GroupStep`, `LoopStep`, ...) are not supported this way.

**One compile per run (BRMS style).** When a pipeline run starts, the scripts of all its ScriptSteps whose place is known in advance (top level, and the children of `GroupStep`, `ParallelStep`, `LoopStep` and `SwitchStep`, every case and the default) and the libraries they use are joined in one bundle, each script as a function named after the step's path, and compiled **once** in one worker process for the run (`AITaskAgent.Extensions.Script.Worker`). A syntax error fails the run **before any step runs**, with an `InvalidOperationException` that names each failing step and the line of its own script (`ScriptStep 'second': script error: SyntaxError: ... at second:2:9`); a script that closes its function early and declares code of its own is refused the same way. Each execution of a step is then a call of its function. The bundle is built again on every run and never cached between runs (the workflow may change); the worker is killed when the run ends, also on error, cancellation or suspension (a resumed run compiles again). A step the run could not know in advance (a `nextStep` child) gets a worker of its own for that call. The designer's syntax check compiles the same bundle, so validation and run report the same errors.

On isolation and limits: **each call runs in a fresh V8 context** of the run's worker: no global, prototype change or input mutation of one step, loop iteration or parallel branch is seen by the next, and the input is a new object every time. Calls of one run are serialized (one V8 isolate), so parallel branches do not run JavaScript at the same time. The host process never loads V8 nor retains the script's memory. Inside the worker the engine is bare: no type or .NET object is exposed (`AddHostType` and `AddHostObject` are not used) and V8 on its own has no access to files, network or processes, so `System`, `process` and `module` do not exist, and `require` and `host` exist only as closed objects (see above). `context.get/set` and `log` cross to the host as JSON over the worker's standard input and output.

The limits are these, from softest to hardest. They apply **per call**: a call that breaks one (time, heap, a killed or crashed worker) kills the run's worker, and the next call starts a new one from the same scripts. A script that hangs holds the other script calls of the run at most its own `timeoutMs` plus 2 s.

- **Time (`timeoutMs`, 5000 by default):** the worker interrupts the engine and names the line. If V8 cannot be interrupted (a single long native call), the host kills the process two seconds later. Cancellation of the token kills the worker; a token that is already cancelled when starting returns `the execution was cancelled.` without starting anything.
- **V8 heap (`maxHeapMb`, 64 by default, from 8 to 128) and stack (`maxStackKb`, 512 by default, from 64 to 2048):** the heap is checked during execution and again when it finishes, measured from the runtime's baseline after a full GC (what earlier calls committed is not charged to this one); there is also an `ArrayBuffer` cap, the largest `maxHeapMb` of the run (exceeding it is a catchable `RangeError` inside the script). The **ceilings** (128 MB and 2048 KB) are hard: an out-of-range value is rejected at pipeline build time, it is not clamped, and no YAML can raise them.
- **Whole-process memory:** `maxHeapMb × 4 + maxHeapMb + 192 MB` with the largest `maxHeapMb` of the run (512 MB with the default value, 832 MB at most). On Windows the operating system enforces it with a *job object*, which also prevents the worker from creating processes and kills it if the host dies. On other platforms the host checks the worker's private memory every 25 ms and kills it if it exceeds the limit, so a single native reservation can overshoot the cap by whatever it reserves between two checks. A worker killed for memory is an error result with the step id (`the script ran out of memory...`); it cannot bring down the host.

The step does not retry (`MaxRetries = 1`) because a script that fails will fail the same way the second time. What this isolation does not cover: a script can spend CPU up to its `timeoutMs` and write up to 8 KB per log line; there is no per-process CPU limit. **Cost:** one process per run: the startup of .NET and V8 and the compile are paid once, when the run starts (about 0.4 s on the development machine), and each call then costs about 1-1.5 ms plus the script itself (measured 2026-10-23, trivial script: 20 steps in a run 0.40 s, 100 steps 0.51 s, a loop of 100 iterations 0.51 s; before, one process per execution, 6.8 s, 33.3 s and 33.5 s). A step executed on its own, outside a pipeline, still pays the whole startup (about 0.35 s). **Deployment:** the application that runs the step must carry `AITaskAgent.Extensions.Script.Worker.dll` (with its `.runtimeconfig.json` and dependencies) next to its binaries, which the reference to `AITaskAgent.Extensions.Script` takes care of; the environment variable `AITASKAGENT_SCRIPT_WORKER` changes where it is looked for, and if it is missing the error says so. The limits depend on the platform's native V8 (`win-x64` and `linux-x64` are referenced; on any other platform there is no engine); the Linux path, with the memory watchdog instead of the *job object*, has not been run. The complete example, with a nested `GroupStep` and no LLM, is in `Samples/YamlPipelineDemo/examples/script-invoice.yaml` and runs as a test in `AITaskAgent.Extensions.Script.Tests/ScriptYamlPipelineTests.cs`. It is not in `pipelines/` because `CreateFromFolderAsync` would merge everything in that folder into the main pipeline.

## Display Text on Any Step

Every step accepts a `display` key with the templates of the text its start and end events carry in
`Display` (`StepStartedEvent.Display`, `StepCompletedEvent.Display`). Both are optional; any other key is an error.

```yaml
- id: extract
  type: LlmStep
  userPrompt: "..."
  display:
    start: "Extracting data from {{ input.fileName }}"
    end: "{{ output.rows }} rows in {{ durationMs }} ms"
```

The model is `step`, `input`, `context`, `data` and, at the end, `output`, `success`, `error` and `durationMs`
(details and limits in [event-system](event-system.md#display-text)). YAML has no per-tool key: tools are
registered in DI, so give them templates through their `DisplayStart` / `DisplayEnd` properties or an
`IDisplayTextProvider`.

## Hooks and Validators on Any Step

Every step that derives from `StepBase` accepts three structural keys that name code registered in the host (an `IHookRegistry`, registered by `AddAITaskAgentYaml()`; DI services for validators):

```yaml
- id: Auditor
  type: LlmStep
  outputSchema: Finding
  validators: [Findings.NoDuplicates]               # semantic validation of the result (IStepValidator)
  resultValidators: ["schema:Finding", Findings.Check]   # the response's own contract; schema:<name> uses a registered schema
  hooks:
    postProcess: Reports.AttachProvenance          # after each successful attempt, before validation
    onFinalize: Notes.End                          # once, after the last attempt
    onToolLimit: Auditor.FinalAnswer               # LlmStep only: what to do when the tool loop ends without an answer
```

A hook is the default implementation of the step's virtual method, so an override that does not call `base` replaces it (a warning is logged when the binder sees such an override outside the framework). Slots and delegate signatures: `docs/designer/architecture.md` §14.

**Hooks run at the end of the step's method (since 2026-10-23).** For the slots that compute a value (`postProcess`, `buildUserMessage`, `buildSystemMessage`, `parseResponse`) a hook is written as `Task Name(TArgs args)`: the step computes its own value first, puts it in the writable member of `args` (`args.Result`, `args.Message`, `args.Result`/`args.Error`) and the hook changes it or leaves it. There is no `next` to call. (This is the form the YAML binder and JavaScript use; in C# the `StepHooks` slot properties are still typed `HookAround<TArgs, T>`, with `next`, and the no-`next` form is adapted to them by `HookForms.After`.) The older form `HookAround<TArgs, T>` (`(args, next) => …`) is still bound by its signature, so existing hooks keep working (architecture §14.6).

```csharp
[Hook(Description = "Adds the total")]
public static Task Order_PostProcess(PostProcessArgs args)
{
    if (args.Result.Value is JObject order)
    {
        order["total"] = order["lines"]!.Sum(l => l.Value<decimal>("amount"));
    }

    return Task.CompletedTask;
}
```

A failing validator or result validator retries the step like any invalid answer (for `ExecStep`/`ScriptStep` that re-runs the work: set `maxRetries: 1`); an exception thrown by a hook ends the step with an error that names the hook. A wrong name, kind or signature is a build error that lists what is available.

**Validators in JavaScript** (`AITaskAgent.Extensions.Script`, architecture §18.7). A validator or result validator can be a function a JavaScript library exports, named `js:<library>.<function>` (the library is what comes before the last dot):

```yaml
- id: order
  type: LlmStep
  validators: [js:orders.checkTotal]
  resultValidators: [js:orders.wellFormed, "schema:Order"]
```

The function (`exports.checkTotal = function (args) { ... }`) runs in the run's script worker, never in the host process, with the ScriptStep's isolation and per-call limits (`JsValidatorOptions`: 2000 ms, 64 MB by default). It receives `args.result` (the result's value, read-only), `args.vars` (the pipeline variables, read/write: the writes are applied to the context after the call) and, in `validators` only, `args.step` (`{ name }`). It returns `true`, `false`, an error text (empty = valid) or `{ isValid, error }`, like a C# validator; an exception or a broken limit ends the step without retry. The library must be known to the host (a build error otherwise) and must export the function (checked when the run starts, before any step runs). A run with JavaScript validators and no ScriptStep starts the worker all the same.

**Hooks in JavaScript** (architecture §18.8). A slot can name a JavaScript function too, with the same `js:<library>.<function>` form:

```yaml
- id: order
  type: LlmStep
  hooks:
    postProcess: js:orders.stamp
    buildUserMessage: js:orders.withLanguage
```

```javascript
exports.stamp = function (args) {
  args.result.total = args.result.lines.reduce(function (s, l) { return s + l.amount; }, 0);
  args.vars.lastTotal = args.result.total;
};
```

The function receives the data of the slot's arguments (`args.step.name`, `args.input`, `args.result`, `args.attempt`…, plus `args.vars`) and returns nothing: what it changes in the writable members (`args.result`, `args.message`, `args.error`, `args.finalAnswerPrompt`) is applied to the real arguments after the call; a read-only member throws a `TypeError` naming it. Since 2026-10-24 every slot takes JavaScript except `onConversationCreated`, whose argument is the conversation object itself (naming a `js:` function there is a build error). The synchronous slots (`enrichActivityBefore`, `enrichActivityAfter`, `enrichStarted`, `enrichCompleted`, `configureRequest`) wait for the worker with a bounded wait; they see the activity's ids and tags (`args.tags`, `args.displayName` writable), the event (`args.additionalData`, `args.suppressFromUser` writable) or the request (`args.options`, `args.model`, `args.systemPrompt`, `args.temperature`, `args.topP`, `args.maxTokens` writable; `args.messages`, `args.tools` read-only). Same worker, limits and errors as the JavaScript validators; an `enrich*` failure is logged and ignored, as for a C# hook.

**`selectConversation`** (LLM steps, 2026-10-24) decides whether the round uses the shared conversation or a fresh empty one: `args.useHistory` (C# `args.UseHistory`) starts with the step's own choice (`useConversationHistory`, default true) and the hook changes it; `args.history` is the shared history as data (read-only), with `args.input` and `args.vars`. It runs on the first attempt of every round, also after a provider fallback, so it must be idempotent; an exception ends the step without retry:

```yaml
hooks:
  selectConversation: js:chat.pick      # exports.pick = function (args) { args.useHistory = !!args.input.followUp; };
```

## Common Errors

The factory refuses to build a pipeline that it knows is wrong, instead of discovering it three LLM calls later. The errors include the YAML line and column when the node keeps them.

| Symptom | Cause |
|---|---|
| `Invalid YAML at line N...` or an `!Algo` tag | Malformed YAML, or v1 format (application tags do not exist) |
| `Unknown key 'x' in a pipeline document. Valid keys: name, steps.` | Document key not accepted |
| `has no 'id'` / `Duplicate step id` / `must not contain '/'` | Missing, repeated (nested ones included) or slashed ids |
| `has unknown type 'X'. Available types: ...` | `type` that no `IExtension` registered; `AddAITaskAgent()`, `AddScriptExtension()` or your own extension is missing |
| `has unknown property 'x'. Valid properties: ...` | Key that is not structural, a container, or a property of the step (names are case-sensitive) |
| `property 'x' expects integer but got string ...` | Value with the wrong type for the property |
| `'dependsOn' is only allowed on top-level steps` | `dependsOn` inside a container; its steps go in declaration order |
| `depends on 'x' which does not exist` / `Cycle detected in pipeline DAG` | `dependsOn` points to a non-existent or nested id, or forms a cycle |
| `Schema mismatch: step 'a' outputs schema 'X' but step 'b' expects input schema 'Y'` | Two dependent steps that declare schemas with different names |
| `inputSchema 'X' is not registered. Registered schemas: ...` | Schema not loaded into the `SchemaRegistry` before building |
| `does not accept 'inputSchema'/'outputSchema'` | The step type does not implement `IJsonSchemaStep` |
| `could not be created: LLM role 'x' is not configured. Available roles: ...` | `profileName` that does not exist in `LlmEnvironment` |
| `could not be created: Tool(s) 'x' not available. Available tools: ...` | A `tools` name that is not registered in the provider |
| `requires a LlmPipelineContext` | Pipeline with LLM steps run with a base context, also when the step is in a case or in a `SwitchStep`'s `default` |
| `YAML alias cycle` / `expands to more than 200000 nodes` / `nested deeper than 64 levels` | Recursive alias, cascading expansion or nesting that exceeds the reading budget |
| `repeats the entry 'x'` in `cases` | Two `cases` keys that are equal without regard to case or surrounding spaces |
| `'when' is empty` | `BreakStep` with an empty or blank `when` |
| `ExecStep 'id': ...` (`ExecStepException`) | Exit code not in `okExitCodes` (with the tail of stderr), command denied by the policy, process that cannot start, invalid property (`executable` with a template, `timeoutMs` <= 0, unknown `encoding`, batch argument with `"` or `%`) or output over `maxOutputChars`; a timeout or cancellation is a `TimeoutException` / `OperationCanceledException` instead |
| `The JavaScript of this run does not compile, so no step was run: ScriptStep 'id': script error: SyntaxError ...` (`InvalidOperationException` from `ExecuteAsync`) | A ScriptStep (or a library it uses) does not compile, or a script closes its function early; checked when the run starts, before any step runs; the location (`id:line:col`) is counted from the first line of that step's `script` |
| `ScriptStep 'id' failed: ...` | Exception, timeout, memory or recursion error in the script, a syntax error in a step the run could not compile in advance (`nextStep`, a step executed directly), or a worker that does not start or dies; includes the line when V8 reports it, counted from the first line of `script` |
| `could not be created: ... 'maxHeapMb' must be between 8 and 128` | `maxHeapMb` or `maxStackKb` out of range: the ceilings cannot be raised |
| `could not be created: ... 'inheritEnvironment' must be 'minimal' or 'all'` | `ExecStep.inheritEnvironment` with another value |

A practical warning: multi-line texts with special characters (`:`, `#`, `{`, `}`, `\`, quotes, emoji) must be written as a `|` or `>` block; inside double quotes, `\` starts YAML escape sequences.

## No Secrets in YAML

A step names a role, never a key or an endpoint. Credentials live in the configuration and are resolved through `LlmEnvironment`, so a pipeline file can be versioned and shared across environments. See [Configuration](configuration.md).

## See Also

- [Configuration](configuration.md): providers, roles and fallbacks.
- [LLM Steps](llm-steps.md): the equivalent in code.
- [Custom extensions](../extensibility/custom-extensions.md): how to make a custom step declarable.
- `Samples/YamlPipelineDemo`: three-step pipeline in v2 format.
