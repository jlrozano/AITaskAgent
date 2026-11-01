# Visual workflow designer: architecture and contracts

> **Approved change pending construction (2026-10-18):** the flat workspace of §2 and the single `environment.json` of
> §10.1 are replaced by per-workflow `.zip` packages with their own configuration, portable `enc:v2` encryption with a
> master password, and a library of configurations and step templates. Design in
> `Samples/WorkflowDesignerWeb/PACKAGES-AND-TEMPLATES-SPEC.md`; to resume, `Samples/WorkflowDesignerWeb/RETOMAR.md`.
> Until it is built, §2 and §10.1 describe what exists.

Working document for the build (2026-10-18). It fixes the decisions taken and the contracts between
`WorkflowDesigner.Core`, `WorkflowDesignerWeb` (backend), `WorkflowDesigner.Cli` and the frontend
(`WorkflowDesignerWeb/ClientApp`). The user's decisions are not reopened:

1. React + backend in `Samples/`, with a reusable Core, as in CodebaseAnalysis (Core + CLI + Web).
2. The node layout lives in a separate file from the YAML.
3. v1 scope: the steps registered in `IStepRegistry` (Group, Loop, Parallel, Break, Switch, LlmStep,
   ScriptStep) and the JavaScript step. `Framework/` (Abstractions, Core, Yaml, Extensions) is not touched.
4. Execution is observed through the pipeline events (SSE) and painted over the schema.
5. Besides the steps, providers, roles, schemas and prompts are configured: everything needed to run.

## 1. Projects

| Project | Contents |
|---|---|
| `Samples/WorkflowDesigner.Core` | No ASP.NET. Step catalog, document and YAML emission/reading, validation, on-disk workspace, environment (LLM), event-based execution. Reused by Web and CLI. |
| `Samples/WorkflowDesignerWeb` | `Microsoft.NET.Sdk.Web`, minimal APIs, SSE with replay, loopback only. Serves the compiled SPA. |
| `Samples/WorkflowDesignerWeb/ClientApp` | React 19 + TypeScript + Vite + `@xyflow/react`. |
| `Samples/WorkflowDesigner.Cli` | `validate`, `list-steps`, `run`, `suspended`, `resume`, `session`. |
| `Samples/WorkflowDesigner.Core.Tests` | xUnit for the Core. |

Backend port: `http://127.0.0.1:5190`. Vite in development: `5174` with a proxy of `/api` to `5190`
(5180 and 5173 belong to CodebaseAnalysis and may be occupied).

## 2. On-disk workspace

A folder (configurable, `WorkflowDesigner:Workspace`; by default `workspace/` next to the backend executable, or the one
passed by the CLI with `--workspace`):

```
workspace/
  environment.json          # configuración del motor y de las extensiones (proveedores, roles)
  schemas/<nombre>.json     # JSON Schemas; el nombre del fichero es el nombre que usa el YAML
  prompts/<nombre>.md       # plantillas de prompt (FileTemplateProvider solo carga .md)
  workflows/<nombre>.yaml   # el que ejecuta el motor
  workflows/<nombre>.layout.json
  sessions/<id>.json        # historial de las sesiones multi-turno (§11)
```

- `environment.json` has the shape of the `AITaskAgent:LlmProviders` section (`Providers`, `Roles`,
  `DefaultRole`, `FallbackProviders`). An `ApiKey` is either a `${VARIABLE}` reference (stored as is and expanded by the
  framework) or a key of its own, which the designer stores **encrypted** as `enc:v1:<base64>`: the current user's DPAPI
  on Windows; on other systems, AES-256-GCM with a random master key in `~/.config/WorkflowDesigner/secret.key` (readable
  only by the user). The API never returns the key or its encryption (sentinel `********`), and decryption happens only in
  memory, when building the engine for a validation or a run (also in the CLI). A plaintext key typed by hand is used and
  gets encrypted on the next save from the designer. Encryption depends on the OS user: an `environment.json` copied to
  another user or machine does not decrypt (a clear error asks for the key to be re-entered; it only blocks workflows with
  LLM steps).
- `*.layout.json`: `{ "version": 1, "positions": {}, "viewport": { "x": 0, "y": 0, "zoom": 1 }, "collapsed": ["<stepId>"], "direction": "TB" | "LR" }`.
  `positions` holds the position of each box the user has placed (`{ "<stepId>": { x, y } }`, relative to the parent container or lane,
  absolute at the first level; a box without an entry gets an automatic position), `direction` is optional (absent = `TB`) and is what
  «Vertical / Horizontal» stores (see «Frontend: compact boxes, free positions with snap on drop»). Since 2026-10-20 (evening) `positions`
  is used again; during the afternoon of the same day it was ignored (superseded decision, see that section).
- Workflow, schema and prompt names are `[A-Za-z0-9._-]+`; any other name, or a `..`, is rejected
  (it is browser input that ends up as disk paths).

## 3. YAML v2 semantics the designer must respect

Source: `docs/guides/yaml-pipelines.md` and `AITaskAgent.Yaml`.

- A document has only `name` and `steps`. Each step: `id` (unique across the whole document, no `/`), `type`
  (the step's tag), `dependsOn`, `inputSchema`, `outputSchema`, and the remaining keys are the step's public properties
  in camelCase.
- The designer **does not use `dependsOn`**: the order of the root steps is the order of the YAML list (a linear list),
  and inside a container too. The engine does not change: `dependsOn` is optional, only exists at the first level and,
  when it appears, defines the order by topological sort; without it, the execution order is the declaration order
  (`YamlPipelineFactory.ValidateAndOrder`). The Core keeps reading and emitting it because existing YAML files carry it.
- Mixed-order trap: mixing steps with and without `dependsOn` reorders the execution. `A`, `B(dependsOn: A)`, `C`
  declared like this run as `A, C, B` (Kahn: the ones without dependencies first, in declaration order).
  That is why validation warns (`warnings`) when a document carries any `dependsOn`.
- Without `type` but with `steps`, it is an implicit `GroupStep`.
- Containers: properties of type `List<IStep>` (`steps` in Group and Loop, `default` in Switch, and `branches` in
  Parallel, where each element is a branch: `AITaskAgent.Core/Steps/ParallelStep.cs:49`) or
  `Dictionary<string, List<IStep>>` (only `cases` in Switch).
- `null`/`~` means "not set". `inputSchema`/`outputSchema` are schema names from the workspace.

## 4. Document (DTO shared by Core, Web and frontend)

```ts
type JsonValue = string | number | boolean | null | JsonValue[] | { [k: string]: JsonValue };

interface WorkflowDocumentDto { name: string; steps: StepNodeDto[]; }

interface StepNodeDto {
  id: string;
  type?: string;                 // ausente = GroupStep implícito
  dependsOn?: string[];          // opcional, heredado: solo primer nivel; el diseñador no lo escribe (puede reordenar la ejecución)
  inputSchema?: string;
  outputSchema?: string;
  properties: Record<string, JsonValue>;               // propiedades que no son contenedor, tal como en el YAML
  containers?: Record<string, StepNodeDto[] | Record<string, StepNodeDto[]>>;  // por nombre de propiedad
}
```

Emitting the YAML of a document and reading it back gives the same document (round-trip by content: comments and the
style of hand-written YAML are not kept; key order is).

## 5. Catalog

`GET /api/catalog`

```ts
interface CatalogDto {
  steps: StepTypeDto[];
  plugins: PluginDto[];          // loaded plugins (§25.9); pending or failed ones: GET /api/plugins
  pluginProblems?: PluginProblemDto[];
}
interface StepTypeDto {
  tag: string;
  description: string;
  category: 'flow' | 'llm' | 'script' | 'other';
  parameters: JsonSchema;                    // IStepRegistry.Describe(tag).Parameters sin los contenedores
  containers: { name: string; kind: 'list' | 'map'; description?: string }[];
  structural: ('dependsOn' | 'inputSchema' | 'outputSchema')[];
}
```

## 6. REST API (camelCase JSON; everything under `/api`)

| Method and route | Body → response |
|---|---|
| `GET /catalog` | → `CatalogDto` |
| `GET /workflows` | → `{ name, updatedAt }[]` |
| `GET /workflows/{name}` | → `{ document, layout, yaml }` |
| `PUT /workflows/{name}` | `{ document, layout }` → `{ yaml }` (saves `.yaml` and `.layout.json`) |
| `DELETE /workflows/{name}` | → 204 |
| `POST /workflows/to-yaml` | `{ document }` → `{ yaml }` |
| `POST /workflows/from-yaml` | `{ yaml }` → `{ document }` or 422 with `{ errors }` |
| `POST /workflows/validate` | `{ document }` → `{ ok, errors: { message, line?, column?, stepId? }[], warnings: { message, stepId? }[] }` (`warnings` does not affect `ok`; today it only warns about `dependsOn`) |
| `GET /environment` | → the `environment.json` object; `ApiKey` as is if it is `${VAR}`, `"********"` if a key is stored (encrypted or typed by hand in plaintext) |
| `PUT /environment` | object → 204 (validates: role→provider→model must exist). `ApiKey`: `"********"` = keep the one of the provider with the same name (if there is none, e.g. after a rename: 422 asking for it to be re-entered); `${...}` = reference; `enc:v1:` = kept if it decrypts (otherwise 422); any other text = new key, encrypted |
| `GET /schemas`, `GET/PUT/DELETE /schemas/{name}` | list of names / JSON content |
| `GET /prompts`, `GET/PUT/DELETE /prompts/{name}` | list of names / `{ text }` |
| `POST /runs` | `{ workflow: string, input: JsonValue }` or `{ document, input }` → 202 `{ runId }` |
| `GET /runs/{id}` | → `{ runId, status, startedAt, finishedAt?, result?, error? }` |
| `GET /runs/{id}/events?after=<seq>` | SSE, `run` event, `id:` = `seq`, replay with `after` or `Last-Event-ID` |
| `POST /runs/{id}/cancel` | → 202 |

Errors: `{ "error": "mensaje" }` with 400/404/409/422. Loopback only, allowed `Host`, `CrossSiteGuard`,
CSP and mandatory JSON, as in `Samples/CodebaseAnalysisWeb/Hosting/WebSecurity.cs`.

**From P1b (packages) onwards, the `/workflows` routes (except `to-yaml` and `from-yaml`), `/environment`, `/schemas`, `/prompts` and
`/tools` in this table respond 410, and `POST /runs` and `POST /sessions` carry `package` (id) instead of `workflow`.** The current
routes, their DTOs and their error codes are in «Deviations» → «Web P1b» and in `Samples/WorkflowDesignerWeb/README.md`.

## 7. Execution events

```ts
interface RunEventDto {
  seq: number; runId: string; ts: string;
  type: 'run.started' | 'run.completed' | 'run.failed' | 'run.cancelled'
      | 'step.started' | 'step.completed' | 'step.progress' | 'step.timeout' | 'step.retry'
      | 'branch.started' | 'branch.completed' | 'llm.response' | 'tool.started' | 'tool.completed' | 'log';
  stepId?: string;            // id del step (el último segmento del StepPath; los ids son únicos)
  stepPath?: string;
  level?: 'info' | 'warning' | 'error';
  message?: string;
  data?: Record<string, JsonValue>;  // step.completed: success, durationMs, error; llm.response: model, promptTokens, completionTokens, costUsd
}
```

Each run has its own `EventChannel`; the mapper is generic (no domain phases or reports). Streaming chunks are
discarded and `StepResult` only copies figures.

## 8. Frontend: organization

Rule: no component of more than ~150 lines, nor one that mixes data, state and presentation. Layers that only depend
downward:

```
src/
  app/            App.tsx (shell y composición), providers.tsx, DesignerLayout (centro + panel lateral único)
  api/            client.ts, sse.ts, types.ts          (única capa que hace fetch)
  domain/         tipos y funciones puras: documentTree, documentToGraph, graphEdits, ids, layout   (sin React)
  state/          workflowReducer (con undo/redo), runReducer, contextos y hooks useWorkflow/useCatalog/useEnvironment/useRun
  features/
    canvas/       WorkflowCanvas, nodes/StepNode, nodes/ContainerNode, edges, CanvasToolbar
    palette/      StepPalette
    inspector/    Inspector, StepInspector (pestañas Propiedades/Hooks), grid/ (PropertyGrid, HooksGrid), fields/* (String, Number, Boolean, Enum, StringList, Json, Code)
    environment/  EnvironmentPanel (configuración del paquete abierto), ProvidersEditor, RolesEditor, SchemasEditor, PromptsEditor
    runs/         RunPanel, RunInputEditor, EventLog, useRunController
    packages/     PackageMenu (nuevo, abrir, guardar, guardar como, importar, exportar, renombrar, cerrar, borrar), PackagePrompts
                  (conflicto 409, recuperar, confianza), MigrationBanner, SecretsLock (candado y contraseña maestra), SecretsBanner
    workflows/    ValidationIndicator, YamlPreview
    library/      LibraryView (pestañas Configuraciones y Plantillas), ConfigLibraryActions (guardar como y aplicar configuración)
    templates/    TemplateDialogs (insertar una plantilla con parámetros y «Guardar como plantilla…»; los monta la app)
  shared/         ui/ (Button, Panel, Tabs, Field, Dialog), hooks/, code/ (CodeEditor: CodeMirror 6 cargado aparte, fuentes de autocompletado puras)
```

Designer layout: canvas with a bottom panel (Execution | YAML) and, on the right, **a single resizable side panel** with
two **tabs**, «Pasos» (`palette`) and «Inspector» (`inspector`), one visible at a time (both stay mounted; the inactive one is
`hidden`). This replaces, on 2026-10-19, the collapsible accordion sections of 2026-10-18. The whole column folds into a rail
with one vertical label per tab. The active tab, folding and width are stored in `localStorage` (`workflowDesigner.sidePanel.v1`,
optional); selecting a step (not adding one from the palette) switches to «Inspector». The composition lives in `app/`
(`DesignerLayout`, `SideColumn`, `useSidePanel`); the features do not know about it.

### 8.1 Inspector and multiple selection (2026-10-19)

- **Selection.** `useWorkflow().selectedIds` is everything selected (the primary `selection` included): the Ctrl/Cmd+click set
  (`multiSelection`), else the Shift+click sibling range (`rangeSelection`), else the primary alone. Ctrl/Cmd+click toggles a step
  (`actions.selectStepsToggle`); the box (Shift+drag on the background) replaces the set (`actions.selectSteps`, the last id is the
  primary). Edits prune the dead ids from the set but do not empty it.
- **Inspector with two inner tabs**, «Propiedades» and «Hooks» (`shared/ui/Tabs`, `aria-label` «Secciones del Inspector», id prefix
  `inspector`; the side tabs are «Secciones del panel lateral», prefix `side`). Only the active panel is mounted. «Propiedades» =
  header (`StepHeader`, `TemplateOriginNote`), validation errors, «Estructura» (inputSchema/outputSchema), `PropertyGrid`, the
  «Compilar» button with the C# diagnostics, and `ContainersSection`. «Hooks» = `HooksNotes` (empty server list, retries warning) +
  `HooksGrid`. `PropertyGrid` replaces `PropertyForm` and `HooksGrid` replaces `HooksSection` (both deleted); `PropertyRow` stays
  because `ParametersForm` (package parameters) uses it.
- **Grids** (`inspector/grid/`): generic, they know neither steps nor the document. `PropertyGrid(fields, values[], onChange(name,
  changes[{index,value}]), context, …)`: categories Obligatorias / Opcionales / Con valor por defecto / Otras, filter, draggable
  divider (the percentage is remembered in `workflowDesigner.inspectorSplit.v1`), description bar of the focused row (sticky at the
  bottom of the panel), bold = some step has a value different from its default, «Quitar», keyboard (↑↓ Home End, Enter, Esc).
  `HooksGrid(slots, registered, schemas, values[], onChange(changes[{index,patch}]), …)`: a row per slot whose select offers only the
  hooks with the slot's signature (text equality, `sameSignature`; delegate variance is not offered), and expandable `validators` /
  `resultValidators` rows. The pure part of multi-editing is `src/domain/commonFields.ts`.
- **Two columns, one line per row; complex properties in a dialog (2026-10-20).** The user compared the grids with the Delphi Object
  Inspector (`InspUnit.pas`: `EditStyle` `esSimple` / `esEllipsis` / `esPickList`, double click opens the editor) and asked for the same
  rule everywhere. **Simple** kinds are edited in the cell: `string` (one line), `number`, `boolean` (checkbox; double click on the row
  toggles it), `enum` and `role` (selects; double click on the row moves to the next value). **Dialog** kinds (`usesDialog`,
  `grid/summary.ts`): `text`, `code`, `prompt`, `stringList`, `intList`, `stringMap`, `tools`, `scriptNames`, `json`. Their cell shows a
  read-only **summary** (`3 tools`, first line of the code/prompt with «…», `{…} 4 claves`, `[a, b, +2]`, `«name» (plantilla)`; empty =
  dim «(vacío)» or the default marked «(por defecto)»; mixed = «(varios valores)») and a small **«…» button** (`aria-label` «Editar
  <name>», it is the `label for` target) that opens `PropertyDialog`; double click on the row does the same. The dialog (`EditDialog`,
  rendered in a portal on `body` so its keys and focus never reach the grid) reuses the field component of `FIELD_COMPONENTS` (or of
  `editors`) over a **copy** of the value: **Aceptar writes once** (one undo step, also with several steps), Cancelar / Esc / × discard,
  Aceptar is disabled while an editor holds an unwritable value (`aria-invalid`, e.g. malformed JSON, repeated map key). With differing
  values the dialog warns, starts empty and writes nothing unless something is touched. Keyboard: Enter / F2 on a row enter the cell
  editor of a simple row and open the dialog of a complex one; Esc reverts a simple row; ↑↓ Home End as before. When the package is
  not trusted the server gives no tools (`useTools().trustRequired`): `FieldContext.toolsTrust` makes the tools dialog show
  `TOOLS_TRUST_MESSAGE` with a «Confiar…» button (`ensureTrusted`) instead of «No hay tools», and the tools summary says so while empty.
  The «Línea N» button of the compiler diagnostics opens the `code` dialog and selects the line (the editor is no longer on screen).
- **Hooks rows (2026-10-20).** `HooksGrid` has the same two-column rows. A slot row = name | select of the hooks with the slot's
  signature | «…» | «Quitar»; **«…» and a double click on the row open `HookCodeDialog`** (`inspector/HookCodeDialog.tsx`, passed to the
  grid as `codeDialog`; Enter / F2 on a slot row still enter the select). `validators` / `resultValidators` are rows with a summary
  («2 validadores») + «…» that opens a dialog with the sortable list (a copy; Aceptar writes once); each name that comes from the
  package's unit (`source` `unit:*`) has a «Código» button that opens `HookCodeDialog` on its method.
- **`HookCodeDialog`.** Hooks are declared in `csharp/shared.cs` with `[Hook]`, `[Validator]`, `[ResultValidator]` and registered as
  `Class.Method` (§15.6). A slot with a hook of the package's unit (`source` starts with `unit:`) opens the editor of `shared.cs`
  (`CodeArea`) with the cursor and scroll on the method (`findHookLine`). A hook of another origin (host, extension, DI) shows its
  signature, origin and description read-only. A free slot (or a name no longer registered) gets a **stub**: method name
  `<IdDelStep>_<Slot>` (unique in the unit; `Steps` with several steps), inserted in `public static class Hooks` (or appended in a new one; `insertStub` skips
  strings/comments when finding the closing brace). **Since 2026-10-24 the stub is inserted when the dialog opens** (no preview, no «Crear stub» step: §14.7
  «The code dialog since 2026-10-24»); before, a read-only preview and «Crear stub en csharp/shared.cs» came first. «Aceptar y
  enlazar» saves `shared.cs` (`persisted(saveSharedCSharp)`, `useHooks().refresh()`, `refreshTrust()`) and binds `Hooks.<name>` to the
  slot (to every selected step). Errors of the unit (`useHooks().diagnostics` + `useValidation().remote`, file `csharp/shared.cs`) are
  listed in the dialog. Editing is locked, with a visible notice of the reason and the action on top of the dialog (§14.7), when no package is
  open, when it is read-only (`current.readOnly`) or, for C#, when it is not trusted (`required && !trusted`; «Confiar…» opens the trust dialog). An
  already trusted package stays trusted after the edit (`PUT /csharp/shared` records the new hash), so this never dead-ends.
- **How the stub is generated.** The server describes each slot structurally: `HookSlotDto` gained `returnType`, `parameters[{type,name}]`
  and `unitCompatible` (`Samples/WorkflowDesigner.Core/Engine/HookStubs.cs`, called from `StepCatalog`; backwards compatible, nothing in
  `AITaskAgent.*`). It reads the delegate of the `StepHooks` property by reflection (`Invoke` of the generic definition, with
  nullable annotations from `NullabilityInfoContext` and tuple names from `TupleElementNamesAttribute`) and writes C# keywords, tuples
  `(IStepResult? Result, string? Error)` and `global::Ns.Type` for namespaces the unit does not import (its global usings, mirrored
  from `CSharpUnitCompiler.Usings`). `HookSignatures.Describe` (the text) drops all of that, so it is not enough. A client that gets
  a server without these fields falls back to `shapeFromSignature` (Func / Action / HookAround from the text), see
  `src/domain/hookStub.ts`. The body is `return Task.CompletedTask;` for a `Task` method (since 2026-10-23 every around slot is written in its
  after form, `Task Name(TArgs args)`, §14.6), `return next();` only for an old `HookAround` shape a server without `signatures` describes, nothing for
  `void` and `throw new NotImplementedException();` otherwise. JavaScript stubs and validator stubs: §14.7.
  On insertion the client rewrites every `global::Ns.Type` of the stub as `Type` and adds the missing `using Ns;` after the usings at
  the top of `shared.cs` (`importNamespaces`, `missingUsings`; the preview shows them), so the body can also name those types
  (`ToolLimitDecision.FinalAnswer(...)`). A `using` only resolves names: the assembly must still be referenced by the unit.
- **Host references of the unit (LLM slots).** The types of the LLM slots (`LlmStepHooks`: `BuildMessageArgs`, `ToolLimitArgs`,
  `ToolLimitDecision`, `LlmRequest`...) live in `AITaskAgent.Extensions.Llm`, which the framework's allowlist (`UnitReferences.Allowed`)
  does not include. The designer adds it without touching the framework: `Samples/WorkflowDesigner.Core/Engine/UnitHostReferences.cs` is a
  closed list of host assemblies (today only that one; ASP.NET, `System.Diagnostics.Process`, `System.Net.*`, `System.Console` stay out),
  passed to every unit as `CSharpCompileOptions.AdditionalReferences` by `PackageUnits` (with or without NuGet packages; NuGet never
  resolves `AITaskAgent.*`, so they do not overlap), and `HookStubs` counts those assemblies as reachable, so `unitCompatible` is true
  for every slot of the built-in steps. The files are the host's own assemblies (`UnitHostReferences.Files()`). The unit's load context does not load them again: `UnitLoadContext.Load` returns null for any assembly the host declares shared (`CSharpCompileOptions.IsSharedAssembly`; default only `AITaskAgent.*`), so the reference falls through to the default context whatever its file name, and the hook binds (without that, `Delegate.CreateDelegate` returns null and the cast throws `InvalidCastException`). `PackageUnits` passes `SharedAssemblySet.HostWins` (shared by name **and** present in the host), the same rule the NuGet resolver and `IsolatedLoadContext` use. The set is the built-in one (`AITaskAgent.*`, `Newtonsoft.Json`, `NJsonSchema*`, `Microsoft.Extensions.*`, platform) plus `sharedAssemblies` of `settings.json` (`"Acme.*"`/`"Acme."` prefix, other = exact name), loaded once at start-up by the Web and the CLI via `SharedAssemblySet.Configure`; a change needs a restart. "Whatever the process has loaded" is deliberately not the criterion: it depends on load order and host, and would overrule a package's own version of a dependency. Tests: `HookStubCompileTests` (the stub of every slot,
  as written and with usings, compiles and binds; an LLM hook of the unit runs in a run; `Process`, `HttpClient`, `Console` still do
  not compile).
- **One step.** Header with id (rename), «Eliminar step», structure, containers. **Several steps** (`selectedIds.length > 1`):
  header «N steps seleccionados» (`MultiHeader`: ids and types) and, in the grids, the **intersection**: properties with the same
  name and field kind in every step (`commonFields` over `schemaToFields(type.parameters)` plus `unknownFields`, as the single
  form did) and the hook slots with the same name and signature (`commonSlots`; `validators` / `resultValidators` only if every
  type admits them). A cell whose values differ shows «(varios valores)» and is only edited on activation (it starts empty and
  writes to all). Rename, delete, structure, containers and `TemplateOriginNote` are single-step only. No selection: the
  workflow settings («Selecciona un step para editarlo.»).
- **One undo step per multiple edit.** New action `actions.updateSteps(patches: {id, patch}[])` (reducer `updateSteps`): applies
  the `StepPatch` of every step inside one `commit`, so it is one history entry, and it is atomic (if one patch fails nothing is
  applied and `lastEditError` carries the message). It is meant for property/hook patches: the reducer does not follow `patch.id`
  renames in the multiple selection for it. With one step the inspector keeps using `updateStep`.
- **Canvas fixes found in the browser check.** (1) The selection box also selects the edges between the boxed steps; the resulting
  `onEdgesChange` called `select(null)` and emptied the box result, so `onEdgesChange` is ignored while a box is in progress.
  (2) Shift+mouse-down extended the native TEXT selection of the page under the box: `preventDefault` on shift+mouse-down in the
  canvas. (3) After a box, xyflow's `nodesselection-rect` swallowed clicks on the selected steps: it is kept as a frame with
  `pointer-events: none` (dragging a selected node already moves the set).
- **Deferred:** double-click to create a C# stub, «add without replacing» in mixed hook lists, delegate variance in the signature
  filter (see `PENDIENTES.md`).

### 8.2 Code editor (2026-10-23)

Every place where code is edited uses one component, `shared/code/CodeEditor` (`language: 'csharp' | 'javascript' | 'json' | 'yaml' | 'text'`,
`value`/`onChange`, `readOnly`, `disabled`, `rows`, `placeholder`, `completions`, `diagnostics`, `jsonSchema`, plus the `id`/`aria-*` that `Field` gives).
`CodeArea` (the textarea of `fields/CodeField.tsx`) is gone; its Tab/Escape behaviour lives on as the fallback described below.

- **Library: CodeMirror 6**, not Monaco. Reasons: size (the editor chunk is 553 KB / 187 KB gzip with the five languages, autocomplete, lint and search; Monaco
  is several MB plus web workers that Vite must bundle and serve), no workers, theming by CSS variables (the editor theme and the `HighlightStyle` only use
  `tokens.css` variables, so light/dark follow `data-theme` and `prefers-color-scheme` with no code; new `--code-*` tokens for syntax colours in the three theme
  blocks), keyboard accessibility (Tab indents, **Escape then Tab within 2 s moves the focus out**, CodeMirror's own `tabFocusMode`; with the completion list
  open, Escape only closes the list and does not cancel the enclosing `<dialog>`), and it mounts in jsdom. Packages pinned exactly, each at least two weeks old
  on 2026-10-23 (`@codemirror/state` 6.7.6, `view` 6.43.14, `commands` 6.11.1, `language` 6.13.1, `autocomplete` 6.20.3, `lint` 6.9.7, `search` 6.7.2,
  `lang-javascript` 6.2.5, `lang-json` 6.0.2, `lang-yaml` 6.1.3, `legacy-modes` 6.5.5 for C# (`clike`), `@lezer/highlight` 1.2.5, `@lezer/common` 1.5.3; the
  transitive `@lezer/*` resolve to versions older than two weeks too).
- **Lazy and offline.** `CodeEditor` (main bundle) renders a monospaced textarea (Tab indents, Escape releases Tab: the old `CodeArea` behaviour) until
  `loadCodeMirror()` (`import('./CodeMirrorEditor')`, a separate chunk served from `wwwroot`, not preloaded) resolves; a failed load keeps the textarea. If the
  textarea had the focus, the caret moves to the editor when it mounts. Measured with `npm run build` into a temporary `outDir`: the CodeMirror chunk is
  553.45 KB (186.94 KB gzip), fetched the first time an editor opens; the initial JS went from 619 KB (one `index` chunk in the `wwwroot` of 2026-10-22) to about
  648 KB split into `index` 483 + `state` 76 + `hookStub` 75 + `jsx-runtime` 9 + `jsonSchema` 5 KB (Rollup split the shared modules once a second entry appeared;
  that baseline also lacks other uncommitted changes, so ~29 KB is an upper bound for the wrapper and the completion sources).
- **Contracts free of CodeMirror.** `shared/code/types.ts`: `CodeCompletionSource = ({ text, pos, explicit, signal }) => { from, to?, options, validFor?, filter? } |
  null` with `CodeCompletion { label, type, detail, info, snippet, apply, boost }`; `CodeDiagnostic { line, column?, endLine?, endColumn?, severity, message }`
  (1-based, what the server already returns). The sources are pure functions over text and caret (tested without a browser); consumers memoize them (CodeMirror
  identifies a source by identity):
  - **JavaScript** (`completion/scriptGlobals.ts`): exactly the globals the worker defines (`ScriptRunner.Bootstrap` and `WrapStep`): `input`, `context.get/set`,
    `log`, `console.log/info/warn/error/debug`, `hash.sha256/sha1/md5`, `base64.encode/decode`, `uuid.v4`, `date.format/parse`, `path.join/normalize/basename/
    dirname/extname`, `require` (the package's library names inside `require('…')`), `host.<ns>.<fn>` from `GET /api/script/catalog` (flagged when this host would
    not grant it), `nextStep`; each with a Spanish `info` saying what is a copy and what writes. A library (`scripts/<name>.js`) has no `input` and gets
    `module`/`exports`. `@codemirror/lang-javascript` adds keywords, snippets and local variables.
  - **C#** (`completion/csharpStatic.ts`, `csharpSource.ts`): keywords, a few snippets and, in a `CSharpStep` body, `call` with the members of `StepCall`; with a
    backend, `POST /api/code/complete` (Roslyn over the package's unit, `core-api.md` «Code editor») is asked at the start of a word or after a dot (aborted when
    the user keeps typing); after a dot the server's symbols win, elsewhere they are merged with the static words; a failure or `timedOut` leaves the static list.
    In `csharp/shared.cs` the list also offers `validator` / `resultValidator` (`GET /api/code/snippets`) and `hook <slot>` for every slot of the catalog: the stub
    of `domain/hookStub.ts`, which now starts with the server's `HookSlotDto.header` (the parameter header; single source `Engine/HookDocs.cs`, see «Deviations and
    backend of the code editor»); the header is also the `info` of those entries. `state/codeAssist.ts` (`useCSharpCompletion(stepId | null)`,
    `useTemplateCompletion`, `sharedSnippets`) wires them to the API, the open package and the catalog, and works without providers (static only).
  - **JSON** (`completion/jsonSchema.ts`): a tolerant cursor scanner gives the path and whether a key or a value is being written; keys come from `properties`
    (required first, with type and description), values from `enum`/`const`/`default`/boolean/null; `$ref` `#`, `#/definitions/…`, `#/$defs/…`, `items`,
    `additionalProperties`, `anyOf`/`oneOf` are followed. Inside a closed string (`"ti|"`, quotes auto-closed) the source filters itself and replaces the whole
    string (`filter: false`). Lint: syntax errors at their offset (a small parser with positions) and, with a schema, warnings for type, `required`, `enum` and
    `additionalProperties: false`. Schemas known today: the property schema of a `json` field (`FieldDescriptor.schema`, new), the run input (first step's
    `inputSchema`, `RunInputEditor`) and a draft-07 subset metaschema (`JSON_SCHEMA_META`) in the Schemas tab.
  - **Templates** (`completion/templates.ts`): only inside `{{ … }}`: `input`, `context` and `context.<key>` for the keys the open workflow writes or reads
    (`context.set/get('k')` in scripts, `call.Set/Get("k")` in C#, `{{ context.k }}` elsewhere; lowercased like `PromptTemplateResolver`). Free text highlights
    `{{ … }}` and does not auto-close quotes.
  - **YAML**: syntax errors from the error nodes of the Lezer tree.
- **Server diagnostics as marks**: the step's code errors of the last «Validar» (`codeLine`/`column`; C# compiler and `script_syntax`) through
  `FieldContext.code.diagnostics`; the unit's errors in the `shared.cs` editors; `scripts/<name>.js` errors in the library editor. The «Línea N» buttons
  (`CSharpDiagnostics`, now also for a ScriptStep's `script`) use `revealLine`, which moves the CodeMirror selection through a handle registry
  (`shared/code/registry.ts`, so the main bundle does not import CodeMirror) or, with the fallback, the textarea. **Since 2026-10-24** the hook and validator
  dialog also checks the text that is not saved yet («Comprobar», on demand and 1 s after typing stops, `POST /api/packages/{id}/code/check`) and marks its
  problems; a locked editor (`readOnly`/`disabled`) stays focusable and readable, and the dialog always says why it is locked and what unlocks it (§14.7).
- **Where it is used.** Inspector: `CodeField` (`script` of ScriptStep, `code` of CSharpStep, any `x-editor: code:*`; named by the property in the grid dialog),
  `JsonField`, `PromptField` (inline prompt with template completion), «C# del paquete» (`SharedCSharpSection`), `HookCodeDialog`, «Librerías JS». Configuration:
  Schemas (JSON + metaschema), Prompts (text + templates), a role's SystemPrompt, plugin prompts (read-only). Run: input (JSON + input schema), initial context
  metadata (JSON). «Importar YAML». **Left as they were:** `ResponseEditor` (JSON *or* plain text by design), descriptions, the chat message box, stop sequences
  and key/value editors (not code), and the read-only `YamlView` (a viewer with its own highlighter).
- **Language of a code step.** «Lenguaje: JavaScript | C#» (`CodeLanguageSwitch`, a radio group with arrow keys in «Propiedades» of one `ScriptStep` or `CSharpStep`,
  shown when the catalog has both). There is no `language` key: the language **is** the step type, already in the document and the YAML, kept by the `.layout`
  (positions are by id) and validated by the Core against the catalog. Switching is one `updateStep` patch (`domain/codeLanguage.ts`, one undo step): `type`
  changes, the code moves between `script` and `code` commented line by line under a minimal valid body (`return call.Ok(call.Input.Value);` / `return input;`),
  properties the target type does not declare are removed and listed, hooks, validators and schemas stay (both types derive from `StepBase`). Hooks stay C# only
  (`csharp/shared.cs`); validators and result validators can also be JavaScript functions of a library, `js:<library>.<function>` (§18.7, 2026-10-23).
- **Tests.** `shared/code/CodeEditor.test.tsx` mounts the real CodeMirror in jsdom (`Range.getClientRects` stubbed): label association, C# highlighting,
  external value changes, read-only, server diagnostics plus JSON syntax marks, `revealLine`, a custom source reaching the completion state, Tab indents but
  Escape+Tab does not trap the focus, and the fallback/load handover. `completion/completion.test.ts` covers the pure sources. Everywhere else
  `src/test/setup.ts` mocks `loadCodeMirror` with a promise that never resolves, so screen tests exercise the real fallback textarea (same label,
  `data-language`, Tab/Escape). Also `domain/codeLanguage.test.ts`, `Inspector.language.test.tsx`, `state/codeAssist.test.tsx` and the header in
  `hookStub.test.ts` / `Inspector.hookCode.test.tsx`.

Contracts between layers (all features consume them; `state/` defines them):

- `useWorkflow()` → `{ document, layout, selection, selectedIds, rangeSelection, dirty, errors, canUndo, canRedo, actions }`, with
  `actions`: `addStep(tag, parent?, index?)`, `insertSteps(steps, parent?, index?)` (templates: a single undo
  step), `selectRange(id)` (Shift+click: consecutive siblings), `removeStep(id)`, `updateStep(id, patch)`,
  `moveStep(id, newParent?, index?)`, `connect(from, to)` (reorders: `to` goes right after `from` in its list) /
  `disconnect(from, to)` (always rejected: edges are not deleted), `normalizeOrder()` (YAML with `dependsOn`),
  `setDirection('TB' | 'LR')`, `updateSteps(patches)` (several `updateStep` as one undo step), `select(id | null)`, `selectStepsToggle(id)`, `selectSteps(ids)`, `undo()`, `redo()`, `load(doc, layout)`, `markSaved()`.
- `usePackages()` → `{ list, current, dirty, prompt, open(nombre), create, importZip, save(), saveAs, rename, close, remove, reload, exportZip, exportYaml, ensureTrusted, … }`
  (P1d, §12): `current` is the open package (`{ id, name, editId, readOnly, trust }`) and `dirty` counts the document, the
  working copy and the drafts of the configuration panel. `useWorkflow().fileName` is the name of the open package.
- `useCatalog()` → `{ steps, byTag, plugins, pluginProblems, loading, error, … }` (`extensions` was replaced by `plugins`, 2026-10-21).
- `useEnvironment()` → `{ environment, schemas, prompts, save…, loading, error }`.
- `useRun()` → `{ status, stepStates: Record<stepId, { state: 'idle'|'running'|'suspended'|'ok'|'failed'; durationMs?; error?; tokens?; costUsd? }>, events, start(input, context?), attach(runId), cancel(), suspension, resumedFrom, suspended, resume(runId, response), discardSuspended(runId) }` (`status` includes `suspended`; see §10.3).
- `useTools()` → `{ tools, loading, error }` (`GET /api/tools`).

## 9. Working rules for whoever builds

- No `git commit`, `git push` or destructive operations. Do not touch `Framework/` (the `AITaskAgent.*` projects unless
  stated otherwise), nor `Designer/` or `Samples/PipelineVisualizer`.
- Restore with `dotnet restore <proyecto> --ignore-failed-sources -p:WarningsNotAsErrors=NU1801` and build/test with
  `--no-restore` (the source `C:\Asisa\NuGetLocal` does not exist on this machine).
- New code follows the style of its neighbours: C# with brief XML comments and names in English; frontend with strict
  TypeScript, reducers and hooks (as in `CodebaseAnalysisWeb/ClientApp`).
- Each piece comes with its tests (xUnit; Vitest on the frontend). If something in the contract cannot be met, it is
  noted in the "Deviations" section of this document and work goes on.
- Running tests that call a real LLM is prohibited. ScriptStep and LLM test doubles are enough.

## Deviations

(Each agent adds here what it could not meet as stated, with the reason.)

### Frontend: foundations (`ClientApp`, 2026-10-18)

- **`branches` of ParallelStep is a list, not a map.** §3 and §5 give it as `Dictionary<string, List<IStep>>`,
  but `ParallelStep.Branches` is `List<IStep>` (`AITaskAgent.Core/Steps/ParallelStep.cs:49`) and in the YAML each
  element is a branch. The frontend and the mock API treat it as `kind: 'list'`; the only map is `cases`.
- **Optional field `execution` in `ContainerSpecDto`** (`'sequential' | 'parallel'`). Without it the canvas cannot
  know that the elements of `branches` are not chained. If the backend does not emit it, the frontend assumes `sequential`
  (it would draw sequence edges between branches). Proposal for the catalog: `parallel` in `ParallelStep.branches`.
- **`x-editor` hint in `parameters`** (`'multiline' | 'template' | 'code:javascript'`), optional, so that the inspector
  picks an editor. It is a JSON Schema extension that the backend may or may not emit.
- **Tags with suffix**: the real tags are `GroupStep`, `LoopStep`, `ParallelStep`, `SwitchStep`, `BreakStep`,
  `LlmStep`, `ScriptStep` (`[StepTag]` attributes); the mock uses those.
- **Keys of `environment.json` in PascalCase** (`Providers`, `Roles`, `DefaultRole`, `FallbackProviders`, and inside
  `ServiceType`, `BaseUrl`, `ApiKey`, `Provider`, `Model`, `SystemPrompt`, `Inference`), as in the
  `AITaskAgent:LlmProviders` section. §6 says "JSON camelCase" for the API but "object of `environment.json`" for
  `/environment`; it has been read as the environment travelling as the file is. If the backend switches it to camelCase,
  `EnvironmentDto` in `src/api/types.ts` (and the `environment` feature) must change.
- **Layout positions relative to the canvas parent node** (as in xyflow): a step inside a container stores its position
  relative to the container, or to the lane (`lane`) if the container has several slots or a map. §2 did not spell this out.
- **Contracts of §8 extended, not changed**: `useWorkflow()` adds `fileName`, `lastEditError` and the actions
  `insertStep`, `setViewport`, `toggleCollapsed`, `setDirection`, `setName`, `addSlot`, `removeSlot`,
  `renameSlot`, `clearEditError`; `addStep` and `moveStep` accept one more optional `index` (the `position` parameter and the `setPosition`/`endGesture`/`autoLayout`
  actions of 2026-10-18..20 were removed on 2026-10-20: the layout is automatic), and `load` a
  third argument `{ fileName, dirty }`. `useEnvironment()` makes `save…` concrete as `saveEnvironment`,
  `saveSchema`/`deleteSchema`/`loadSchema`, `savePrompt`/`deletePrompt`/`loadPrompt`, and adds `roles` and `reload`.
  `useRun()` adds `runId`, `totals`, `error` and `reset`, and `status` includes `idle` and `starting`. There is one more hook,
  `useWorkflowFiles()` (list, open, save, new, delete), so that the menu and the Ctrl+S shortcut share logic.
- **`npm install` from scratch fails** on this machine with `Cannot read properties of null (reading 'edgesOut')`
  (Arborist bug with the optional peers of Vitest 5). It was solved by seeding the `package-lock.json` from
  `CodebaseAnalysisWeb/ClientApp` and letting npm add the new entries; the resulting lock now installs with `npm ci`.
- **`Samples/WorkflowDesignerWeb/wwwroot/` (output of `npm run build`) is not in `.gitignore`**: the root only
  ignores CodebaseAnalysisWeb's and this agent could not touch anything outside `ClientApp`. It has to be added.

### Core (`Samples/WorkflowDesigner.Core`, 2026-10-18)

Real API in `docs/designer/core-api.md`.

- **`branches` is a list** (confirmed by the factory's detection): the catalog gives it as `kind: 'list'` with
  `execution: 'parallel'`; the rest of the containers carry `execution: 'sequential'`. `x-editor` hint emitted with the
  frontend's values (`code:javascript` in `ScriptStep.script`, `template` in `userPrompt`/`systemPrompt`), not with
  `code`/`prompt`/`role`; no hint is emitted for `profileName`.
- **Prompts in `.md`, not `.txt`** (§2): the framework's `FileTemplateProvider` only loads `<nombre>.md`.
- **`stepId` = `StepName`, not the last segment of `StepPath`** (§7): the `StepPath` of step events is the
  path of the parent. The Core emits `stepPath` = parent path + `/` + id (format in `core-api.md`).
- **`environment.json` in PascalCase**, as is the `AITaskAgent:LlmProviders` section (confirmed: the Core passes it
  to the engine as that section). Literal keys masked as `********`.
- **`POST /runs` compiles before returning the `runId`**: a workflow that does not compile throws
  `WorkflowValidationException` (422 with localized `errors`) instead of creating a failed run.
- **Validation with one factory error per round**: the factory stops at the first one. The designer's own checks
  (ids, schemas, roles) do come out all together, and if any of them fails, nothing is compiled.
- **Emitted YAML**: it does not keep comments; `yes/no/on/off` are written without quotes because the engine's reader is
  YAML 1.2 core (`on:` of SwitchStep).
- **LLM services**: the Core registers only the `ServiceType` values it knows (`openai`, `openrouter`, `azure-openai`,
  `gemini`); others, through `WorkflowDesignerOptions.ConfigureEngineServices`. Tools registered as `ITool` through that
  route are not added to `AvailableTools` (`UseAITaskAgent` is not called, since it sets static members per provider).

### Frontend: canvas and palette (`features/canvas`, `features/palette`, 2026-10-18)

- **Dragging a step only changes its position** (decision of 2026-10-20 evening, replaces «automatic layout, snaps back» of the afternoon):
  while the drag lasts, the position lives in the canvas's local state (`useNodeDrag`: `DragOverride`), so the box and its arrows follow the
  cursor; on drop `positionsOnDrop` snaps it and `setPositions` stores it (one undo step). Order and container membership never change by
  dragging: that is done from the context menu or the inspector.
- **Nodes with `aria-label` = step id**: simple steps are `role="button"`; containers and lanes are
  `role="group"` (the container holds the fold button inside, which is no longer a control nested in another).
- **New pure module `src/domain/placement.ts`** (exported from `domain/index.ts`, without changing existing signatures):
  `absoluteBoxes`, `hostedSlot`, `dropTargetAt`, `resolvePaletteDrop` (2026-10-20: `graphSubtree`, `placeIn`, `avoidOverlap`,
  `insertionAmong`, `resolveNodeDrop` and `DropTarget.index/origin/min` were removed with the free positions).
- **Drop feedback**: only the host container/lane is highlighted (`is-drop-target`) while a palette piece is dragged over the canvas.
  The insertion line (`domain/dropPreview.ts`, `DropIndicator`) and its tests were removed on 2026-10-20: nothing is dropped «between»
  boxes any more (a palette piece goes to the end of the host's list; the root keeps «after the selected step»).
- **Container declared and absent from the document**: resolved in the integration (see the next section).
- `src/app/App.test.tsx` imports `features/canvas/test/setupCanvas.ts` (jsdom stubs for xyflow: `event.view`, which
  d3-drag needs and user-event leaves as null, and `DOMMatrixReadOnly`). The global setup has not been touched.

#### Canvas context menu and Alt+Arrow shortcuts (2026-10-20)

- **Right click on a step** (`CanvasContextMenu.tsx`, pieces in `contextMenuParts.tsx`): Subir / Bajar, «Mover a…» (submenu with the
  valid slots and their path: «Raíz», «sw › cases: x», «sw › default»), «Sacar del contenedor», Duplicar, Eliminar, and in containers
  Plegar / Desplegar, «Seleccionar contenido» and «Añadir paso aquí» (one entry per slot); «Insertar después de…» and the unchanged
  «Guardar como plantilla…». The step types come from the palette (`groupSteps`). With several steps selected only the template
  item is offered (the other actions would act on the clicked step alone). **Right click on the pane**: the layout direction («Layout vertical» / «Layout horizontal», the current one checked) and «Añadir paso…».
  There is no «Pegar»: the app has no step clipboard.
- **Logic in `src/domain/stepMenu.ts`** (pure, exported from `domain/index.ts`): `moveByOffset`, `moveDestinations` (excludes the
  step, its subtree and its current slot; includes list slots the catalog declares and the document lacks, which `moveStep` creates;
  map keys are never invented), `moveOutTarget`, `planDuplicate` (ids `<id>_<n>` unique in the whole document, subtree included, the
  copy carries no `dependsOn`). (`endOfSlotPosition` was removed on 2026-10-20: «Mover a…» always appends to the destination list and the layout places it.)
  Every menu entry is ONE state action (`moveStep`, `insertStep`, `addStep`, `removeStep`...), hence one history step.
- **Alt+ArrowUp / Alt+ArrowDown** (`useCanvasHandlers.onKeyDownCapture`): moves the selected step in its list; ignored with several steps
  selected or while typing in a field. It runs in the capture phase and stops propagation, otherwise xyflow would also move the box.
- **Reducer change**: `moveStep`/`addStep`/`insertStep` called WITHOUT position and with an index in the middle of a nested list now
  relayout that list (`relayoutSlot`, like `relayoutTopLevel` does for the root), so the boxes follow the new order. Drags always carry
  a position and are unaffected; the inspector's reorder arrows benefit too.
- **Delete** shares `useCanvasHandlers.requestDelete` with the Supr key (same confirmation for containers with children).


### Web backend and CLI (`WorkflowDesignerWeb`, `WorkflowDesigner.Cli`, 2026-10-18)

- **`GET /workflows/{name}` always returns `layout`**: without `.layout.json` (or if it is unreadable) an empty layout
  arrives (`version: 1`, no positions), because the frontend types it as mandatory. Nulls are omitted in all
  responses.
- **`PUT /workflows/{name}`**: `layout` is optional; if missing, the saved one is kept. It does not require the document
  to validate (work in progress is saved); validation is `POST /workflows/validate`.
- **Errors**: `{ "error": "mensaje" }` and, in the 422s, `errors` (localized objects in workflows; strings in
  `PUT /environment`). A schema that NJsonSchema does not accept is 422; an unreadable body, 400; a body that is not JSON, 415;
  one that is too large, 413 (also without `Content-Length`).
- **Mandatory JSON only if there is a body**: `POST /runs/{id}/cancel` carries no body (that is how the frontend client
  sends it) and therefore does not require `Content-Type`; `CrossSiteGuard` protects it.
- **`POST /runs/{id}/cancel`** answers 202 `{ runId, status }`, also if the run had already finished (idempotent);
  404 if it does not exist. The SSE ends (closes the connection) when the run ends; with a finished run and
  `after` at the end it returns 200 without events.
- **`environment.json`**: the Core requires providers with `BaseUrl`; the seeded example uses `${OPENAI_API_KEY}` and lists the
  model `gpt-4o-mini` in `Models`.
- **SPA**: the csproj compiles `ClientApp` only on `publish` (and with `-p:BuildClientApp=true`), not on any `Release`
  build, so that `dotnet test -c Release` never invokes npm. `.gitignore` already ignores `wwwroot/` and `node_modules/`.
- **CLI**: exit codes 0 ok, 1 validation failed, 2 usage/I/O, 3 run failed, 4 Ctrl+C, 5 `--timeout`, 70
  internal. A YAML file is run as a document (`RunRequest.Document`), after validating its own text so that the
  error lines are the file's lines. `--env` and the absence of a workspace are resolved with a temporary copy
  (deleted when finished); the CLI does not create folders in the current directory.

### Frontend: integration (`ClientApp`, 2026-10-18)

- **Navigation with pending changes**: `useHashRoute().navigate(route, { force? })` checks `confirmLeave()` and returns
  `false` if the user stays; external hash changes (back/forward, edited URL) are reverted with
  `replaceState`. `useWorkflowRoute` navigates with `force` (automatic sync). A change of workflow through the hash with
  the workflow unsaved asks for its own confirmation and, if cancelled, returns to the open one. The workflow is **not** registered in
  `useLeaveGuard` on purpose: the document lives in `WorkflowProvider` and survives the view change, so warning when going to
  Environment would be a false positive; opening/creating from the menu already has its own dialog. `useWorkflowMenu.useUnsavedGuard`
  duplicates the `beforeunload` of `useLeaveGuard` (no consequences; unify it if a single point is wanted).
- **Absent containers**: `insertStep`/`moveStep`/`removeStep` accept an optional `containerSpecs` (the reducer receives it
  from the catalog). With it, inserting or moving into a declared and absent container creates it empty; when a list container
  empties it is removed, unless it is the only one that declares the step (body of Group/Loop/Parallel) or it is a map. The catalog
  does not mark containers as mandatory: if `required` is added to `ContainerSpecDto`, the pruning rule must use it.
- **Origin of the edit notices**: `lastEditSource` (`'canvas' | 'inspector'`) and `inspectorActions` in `useWorkflow()`;
  the canvas shows only its own, and the inspector (`InspectorNotice`) shows its own. Changing the selection discards the notice.
- **`useRun().reconnecting`**: true while the SSE is cut and resumed (`onStatus` → `reconnecting`); the run summary
  shows «reconectando…».
- **`MAX_EVENTS` stays at 2000** (measured: 30,000 events, 153 ms with 2000 versus 4,753 ms with 20,000; each event copies
  the array). Raising it requires a chunked store.
- **Bundle**: the canvas (`@xyflow/react`), `YamlPreview`, the environment panel and the mock are loaded with dynamic imports
  (`React.lazy` / `import()`); the main chunk drops from 628 kB to 398 kB.

### Frontend: compact boxes, free positions with snap on drop (`features/canvas`, `domain/layout|geometry|freePlacement|placement`, 2026-10-20 evening)

**History of the decision (do not reopen without new data).** 2026-10-20 morning: free positions with long boxes; the user rejected the boxes
(elongated), the tangle of arrows when moving boxes (dropping reordered by geometry and arrows jumped) and a container that, when unfolded, hid
the element below instead of pushing it. 2026-10-20 afternoon: «layout 100 % automatic» (no stored positions, a dropped box went back to its
slot); built from a misreading of «ahora se colocan automáticamente». 2026-10-20 evening, the user clarified: «cada elemento se AJUSTA cuando
se suelta» and to put it elsewhere (= change the ORDER or the container) «a través del menú contextual o el inspector». **Current decision:
free positions with snap on drop; order and membership only by context menu or inspector.** The afternoon text («no free positions, snaps
back») is superseded.

- **Compact boxes** (`GRAPH_METRICS`, `domain/layout.ts`, unchanged from the afternoon): step 108×80 (icon on top, name below in 1-2 lines; the
  tag, the full name, `properties.description` and the run summary are the native `title`); folded container 230×40 (`collapsedHeight`); an
  unfolded container is as large as its content needs, min width 230; header 34, lane header 26, padding 14, `rankSep` 44, `nodeSep` 28.
- **Geometry is the saved position plus a derived push.** `layoutDocument(document, { collapsed, direction, positions }, { containerSpecs })`
  places every list of siblings (`LayoutBuilder.placeSiblings`): (1) a box with a saved position keeps it (`layout.positions[id]`, relative to
  its parent: the container or lane, whose content origin is `(padding, header + padding)`; inside a container it is clamped to that origin so
  it never sits under the header; the container grows to contain it); (2) if NO box of the list has a saved position, all are placed with dagre
  (so a new or opened workflow looks as before); (3) if only some have one, the ones without are placed with dagre among themselves in the free
  space that follows: below the lowest placed box in `TB`, right of the rightmost in `LR`, aligned with the last placed sibling that precedes
  them in the list; the placed ones are not touched. (4) `pushApart` (`domain/geometry.ts`) resolves overlaps: boxes are visited by main-axis
  coordinate (top first in `TB`, left first in `LR`) and one that overlaps an already fixed box moves past it (gap `PUSH_GAP` 20), in cascade.
  `StepBox.basePosition` is the position before the push, `position` the drawn one.
- **Unfold pushes, fold gives the room back (derived, never stored).** The push is a pure function of the current positions and sizes, so
  unfolding a container (or adding or removing children, which changes its size) shoves whatever it now overlaps down (`TB`) or right (`LR`),
  nested containers propagate to their parent, and folding restores exactly what the user had placed. A box that does not collide is never
  moved (one the user left far away stays). Tests: `domain/layout.free.test.ts` (both directions, cascade, nested, far box), `domain/geometry.test.ts`.
- **Drop** (`useNodeDrag` → `positionsOnDrop`, `domain/freePlacement.ts`): the box keeps the dropped position snapped to a 10 px grid counted from
  the content origin of its parent (`GRID`). If it would cover a sibling (or sit closer than `MIN_GAP` 8 px) it goes to the nearest free spot
  (`placeBox`: candidates glued to a side of each sibling at `DROP_GAP` 16, minimal distance to where it was dropped, never at the other end of the
  canvas). A container is a sibling like any other: **dropping over the body of an unfolded container does NOT put the box inside** (membership
  changes only by menu/inspector); the box is moved outside. Dropping does not reorder, does not change the container and does not relayout
  anything else; the only side effect is that the siblings without a saved position are PINNED where they are (`basePosition`, so the automatic
  placement of the missing ones does not make them jump). Not moving (a click) emits nothing. One undo step (`setPositions`).
- **Entry from the palette or a template** (`usePaletteDrop`, `at`): at the first level the new box is centred on the drop point, snapped and
  moved off its neighbours (`positionsOnInsert`, computed in the reducer with the real size of the new box; a template chains its root steps from
  the point in the layout direction); its place in the ORDER stays what it was (after the selected root step or at the end). Over an unfolded
  container or lane it goes to the end of that list with an automatic position. `addStep`/`insertStep`/`insertSteps` take an optional `at`
  (only honoured when `parent` is null); the pane menu «Añadir paso…» passes the click point likewise. Dropping into a container does not use the
  cursor position.
- **Reducer** (`state/workflowReducer.ts`): `setPositions(positions)` merges positions, never touches the document, no history entry if nothing
  changes; `arrange()` («Ordenar») empties `positions` (every box is recomputed with dagre, order untouched, one undo step); `setDirection(d)` saves
  `direction` AND empties `positions` (so pressing the current direction is «Ordenar»); `moveStep` to another list drops the step's saved position
  (it was relative to the old parent) and keeps it when only the index changes (up/down: the box stays, the arrows change); `removeStep` drops the
  positions of the subtree; renaming follows the id; `load` keeps the saved positions. The arrows are still the sequence edges of the list order
  (`seq:a->b`), never derived from geometry.
- **Direction** is persisted: `WorkflowLayoutDto.direction?: 'TB' | 'LR'` (absent = `TB`, `layoutDirection(layout)`), C# `WorkflowLayout.Direction`
  (`WorkspaceStore.cs`, kept by `LibraryStore.RelativeLayout`). UI: toolbar «Ordenar», «Vertical ↓» / «Horizontal →» (`aria-pressed` = current)
  and the same entries in the pane menu. Lanes of a Switch/Parallel are side by side in both directions. `GraphNode.basePosition` is not passed to xyflow.
- **Arrows** stay floating (`edges/floating.ts`, `edges/SequenceEdge.tsx`), four `source` handles per box, `ConnectionMode.Loose`.
  `floatingSides(source, target, direction)` is a stable rule: while the target lies beyond the source on the main axis (`TB`: below; `LR`: to the
  right) the arrow goes bottom→top (right→left), the reverse if it lies before it; only when the boxes overlap on the main axis (side by side) does
  it use the nearest side of the cross axis. It depends only on the rectangles, so it changes side only when a box crosses a boundary of the
  other, continuously, and it follows the box while dragging. Edges exist only between visible siblings.
- **Inspector** (`features/inspector/ChildList.tsx`): `WorkflowSettings` lists the root steps and `ContainersSection` the slots of a container;
  each row has up/down, **«Mover a…»** (`moveDestinations(document, id, containerSpecs)`) and remove, through `inspectorActions.moveStep`.
- **Tests**: `domain/geometry.test.ts`, `domain/layout.free.test.ts`, `domain/layout.auto.test.ts`, `domain/layout.direction.test.ts`,
  `state/workflowReducer.test.ts` («posiciones libres en el reductor»), `features/canvas/useNodeDrag.test.tsx` (stays where dropped, snapped to the
  grid, apart from a container without entering it, one undo step, no-op click), `CanvasBoxes.test.tsx`, `WorkflowCanvas.test.tsx`,
  `CanvasContextMenu.test.tsx`, `edges/floating.test.ts`. Checked in a real browser (SPA built to a temp folder, `?mock=1`, Playwright): drag and
  drop (stays, snaps, does not cover), drop over a container and over another box, unfold with elements below (pushed, in cascade) and fold
  (restored), «Mover a…» from the context menu and the inspector, palette drop at the root and over a container, `Horizontal`, `Ordenar`, undo.

### Frontend: order by the list, without `dependsOn` (`ClientApp`, 2026-10-18)

User decision (closed): the order of the root steps is the order of the YAML, as inside containers.
Reason: mixing steps with and without `dependsOn` reorders execution in a surprising way (`A`, `B(dependsOn: A)`, `C`
runs as `A, C, B`, due to the FIFO queue of `YamlPipelineFactory.ValidateAndOrder`).

- **Derived edges**: `documentToGraph` draws `sequence` edges (`seq:a->b`) between consecutive neighbours of the root and
  of each sequential slot; there are no `dependsOn` edges any more. They are selectable but never `deletable`.
- **§8 changed**: `connect(from, to)` moves `to` right after `from` in its list (same list; otherwise `EditError`).
  `disconnect` is kept in the contract as a typed rejection (`EditError` with «el orden se cambia moviendo el step») so that
  Supr on an edge gives a notice through the `lastEditError` channel. New action `normalizeOrder()` and new fields
  `legacyDependsOn` and `executionOrder` in `useWorkflow()`. Removed from the domain: `dependsTransitively`, `dependencyCandidates`,
  `dependencyEdges`, `dependencyEdgeId` and `insertionSlotFor` (replaced by `insertionPointFor`); `topologicalOrder` becomes
  `executionOrder`, which replicates the engine's rule (it used to be a «stable» topological sort that gave `A, B, C`).
- **Re-layout** (superseded on 2026-10-20: dragging only changes the position, see «Frontend: compact boxes, free positions with snap on drop»): dragging a step
  does not reorder; order is changed by connecting, with move up/down or «Mover a…» (context menu, inspector with no selection).
- **Insertion**: clicking or dropping from the palette onto the root adds right after the selected root step, or at the end; with a
  container selected, into its first slot (as before).
- **Legacy YAML**: `dependsOn` values are kept on save. The canvas shows a notice with the effective order and
  «Normalizar» (root set to the effective order and all `dependsOn` removed, nested ones included; one undo step; with a cycle it
  is rejected). The inspector shows `dependsOn` only as a read-only note; local validation still flags cycles and
  non-existent or nested ids. Renaming a step still updates the `dependsOn` that cite it.
- **Pending outside `ClientApp`**: the catalog still declares `structural: ['dependsOn']`; the frontend ignores it.

### Frontend: storage, tools and suspension (`ClientApp`, 2026-10-18, contract §10)

- **Environment**: two new tabs, «Almacenamiento» and «Herramientas» (`StorageEditor`, `ToolsEditor`). `environmentMapping.ts`
  remains the only module that knows the keys; `Storage` (all in memory) and `FileTools` (enabled, with no root or folders) are not
  written when they hold their default value, so as not to clutter the file. Accepted MCP transports: `Stdio`, `StreamableHttp`
  and `Sse` (verified in `McpIntegration/Configuration/McpServerConfig.cs`; the contract only cited `Stdio`). MCP environment
  variables and headers get the same treatment as `ApiKey` (sentinel `********`, `${VAR}` in plaintext). With more than one
  MCP server a warning is shown (only the first one counts) without blocking the save.
- **New state**: `useTools()` (`state/tools.tsx`, `GET /tools`, reloaded when the environment changes) and, in `useRun()`,
  `suspension`, `resumedFrom`, `suspended`, `resume(runId, respuesta)`, `discardSuspended(runId)` and `start(input, context?)`.
  Run status `suspended` (not counted as active: another run can be launched) and step state `suspended`: when `run.suspended`
  arrives, every step «en curso» becomes «suspendido» because the pipeline no longer runs.
- **`run.suspended` closes the SSE flow** of that `runId` (it is in `TERMINAL_RUN_EVENTS`); resuming opens a new runId with
  its own subscription. It is assumed that the server closes the stream after `run.suspended`; if it did not, the client would cut it itself.
- **«Descartar» (discard) a suspension has no endpoint** in §10.3: today it only hides it in the browser
  (`localStorage workflowDesigner.discardedSuspended.v1`) and it stays in `GET /runs/suspended`. For real deletion,
  `DELETE /api/runs/suspended/{runId}` is needed, and `discardSuspended` must change.
- **«Enlace con la anterior» (link to the previous one)**: the resumed run shows «Reanudada de `<runId>`» (from
  `run.resumed.data.resumedFrom`), but there is no navigation to the events of the previous run (the API only replays events of a runId to whoever subscribes).
- **Mock**: `SuspendStep` in the catalog (`message`, `marker`, `dataToReview`; `outputSchema` defines the response) and workflow
  `approval-flow` with schema `Verdict` (`approved` mandatory); the mock's tools come from the saved environment (FileTools if
  enabled + one `<servidor>_search` per MCP server).

### Sessions: backend (`WorkflowDesigner.Core/Sessions`, Web and CLI, 2026-10-18, contract §11)

- **`TurnDto` extended (additive)**: `inputTruncated?`/`outputTruncated?` (the saved value is the start of the text, or of the
  compact JSON, as text) and `warnings?` (memory keys that were not carried over, names only). `suspension` is the
  `SuspensionDto` of `RunInfo` (it also carries `stepPath`; `marker` may be missing).
- **`title` without turns**: the workflow name; the first `input` replaces it only if no `title` was given on creation.
- **A resumed attempt that fails or is cancelled** does not leave the turn `failed`: since `RunManager` keeps the suspended run, the
  turn goes back to `suspended` on it (its previous `runId` and `resumedFrom`) with the failure in `error`.
- **`DELETE /sessions/{id}`**: 409 if there is a turn in progress; with a suspended one, it discards its run. `?conversation=true`
  deletes in the store that **now** says `Storage.Conversation` (if it changed since the turns, the old one is not touched).
- **`durationMs`** = execution time of all the turn's attempts (without the wait in suspension); tokens and cost,
  sum of all attempts.
- **`SessionManager` must be resolved at startup**: it listens to the resumes and discards of `RunManager` (internal events);
  the backend does it in `Program.cs` and the CLI in `resume`, `suspended` and `session`.
- **Concurrency**: writes serialized per session within the process; backend and CLI on the same session at the same time = the
  last writer wins. A `running` turn from another live process is not touched; if that process dies, it becomes `failed` when read.
- **Example workflow**: `advanced-story` prepends the last story (`context.lastStory`) to the request in `ReviewStory`
  and `Questions` with a `ScriptStep` (`reviewContext`, `questionsContext`), and the classifier receives it in its system prompt
  (`{{context.lastStory ?? (none)}}`); `saveStory` deletes `draft1`/`draft2` from memory with `context.set(k, null)`.

### Frontend: sessions in the Result tab (`features/runs/session`, 2026-10-18, contract §11)

- **Default mode = Session**; «Ejecución única» (single run: JSON input, initial context, suspended runs) works as before. `RunPanel`
  accepts `initialMode` (optional, default `'session'`). In session mode the side column only carries the selector and the
  summary; the initial tab is Result (in single run, Events).
- **`useRun().attach(runId)`** (new, without changing existing signatures): hooks into a run created by the server with the same
  reducer and the same `launch` as `start`/`resume` (it replays the events and follows the new ones). It throws if a run is in progress.
- **The server is the source of truth**: after `startTurn` the session is requested and `attach` is called; when the followed
  run ends or suspends, the session is requested again (up to 8 attempts, in case the SSE arrives before the turn is saved). When a session
  with a `running` or `suspended` turn is opened, the canvas and Events hook into it. A resume is recognised by
  `run.resumedFrom` (the new run replaces the followed one).
- **Turns use the saved workflow** (`POST /sessions/{id}/turns` only carries `input`): with unsaved changes a warning is shown
  next to the message box. This is an inference from the contract, not checked against the backend.
- **Message**: if the first step's `inputSchema` has a single mandatory text property, a textarea is used and
  `{ <propiedad>: texto }` is sent (the turn list also shows only the text); otherwise, the usual JSON editor
  (`useRunInput`) with Ctrl+Enter. While the schema loads, the JSON editor is shown.
- **Deleting a session** offers «solo el historial» or «historial y conversación» (`?conversation=`); it is disabled only with a
  `running` turn (with a suspended one the backend discards it, §11 backend). The last session used is remembered per workflow in
  `localStorage` (`workflowDesigner.lastSession.<workflow>`).
- **«Ver eventos» (view events)** of a past turn hooks into its `runId` (SSE replay); it cannot be used while another run is active. For a
  resumed turn only the last attempt (current `runId`) can be reached.
- **Mock**: `createSession`/`startTurn`… in `api/mockSessions.ts`; the simulated memory is `lastInput` and `turns`, and the turn
  answers quoting the previous message. New workflow `support-chat` (schema `Message`, one text property). The entries of
  `GET /runs/suspended` carry `resumable`; `SuspendedRunDto.workflow` becomes optional.
- **`testTimeout: 15000`** in `vite.config.ts`: the `App` tests load the canvas and, with a busy machine, exceeded 5 s.
- Pending: no visual verification in a browser (jsdom only); no «copiar respuesta» (copy response) and no pagination of turns.

### Core P1a: packages (`Samples/WorkflowDesigner.Core`, 2026-10-18, contract §12)

Full API, typed errors and HTTP mapping in `core-api.md` §9. Here, what was decided where the specification does not
say so, and what departs from it.

- **What breaks for Web and CLI** (on purpose; they are other agents' work): `WorkspaceStore` and `EnvironmentStore` are no
  longer registered (each package has its own: `PackageSession.Workspace` / `.Config`); `RunRequest.Workflow` becomes
  `PackageId` / `PackageFile` / `PackageDirectory` + `Source`; `SessionManager.Create(packageId)`; `ToolCatalog` no longer receives the
  environment (`ListAsync(ConfigStore)`); `WorkflowValidator.Validate*Async` receives the package (`IPackageFolder` or an open id);
  `WorkspaceStore.ListWorkflows/DeleteWorkflow/WorkflowInfo/EnvironmentPath` disappear. `EnvironmentStore` remains as an
  `[Obsolete]` alias of `ConfigStore`.
- **`Open` does not open when there is something to recover**: it returns `Recoverable` and the UI chooses `Recover` or `Discard`
  (`RecoveryChoice`). A working copy is offered only if it comes from the same zip (same `id` and sha256) and **has changes**; one
  without changes is deleted on its own, also when the store is released (`PackageStore.Dispose` keeps only those with changes). The record
  of each copy is `<datos>/work/<editId>.json` (owner `pid:inicio`, source zip); a dead or null owner makes it orphaned.
- **`SaveAs` reassigns the session**: the original zip stays as it was on disk and the session starts editing the new one (like Office's
  «Guardar como»). **`Close` with changes and without `discard` throws** `PackageHasChangesException` (409).
- **`Save` without changes does not touch the zip** (nor `updatedAt`); the comparison ignores `manifest.updatedAt`. The 409 uses the sha256 of the
  zip, not the date.
- **The default `RunRequest` runs the working copy** (`Source = Working`); sessions and resumes use the saved zip. `PackageFile` and `PackageDirectory`
  (not serialized to JSON) are for the CLI: `--source-dir` and `run x.zip`, which extract to
  `<datos>/work/run-<runId>/` and always delete it (tested successfully: success, failure, suspension, cancellation and timeout).
- **Suspended runs v2**: `packageId`, `packageName` and `<runId>.package.zip` (snapshot with the YAML that ran). Resuming uses the
  YAML, schemas and prompts of the snapshot and the **saved** configuration of the package by `id` (if the package is
  open with unsaved configuration changes, they do not count). Without a snapshot (migrated): saved schemas and prompts of the
  package with the YAML from the registry. Without `packageId` (unmigrated v1): `resumable: false` and a 409 asking to migrate.
- **Orphan warning in validation** (`warnings`, RF-06): a schema or prompt of the package that no step uses. Additive, but
  an assertion of empty `warnings` in `Web.Tests` or `Cli.Tests` over a package with loose resources will stop holding.
- **RN-04 deviation**: the 100:1 compression ratio is checked only from 64 KiB uncompressed per entry (a small file cannot do harm, and
  repeated text compresses far above 100:1). The limit that really stops a zip bomb is that of actual uncompressed bytes (5 MiB per entry, 50 MiB in total).
- **Determinism across systems (RN-06)**: `ZipArchive` writes the host system into each record (0 on Windows, 3 elsewhere) and
  that would change the bytes; the writer fixes it to 3 (Unix) and fixes the attributes to 0644. What still depends on the runtime is
  the Deflate output: a zlib change between .NET versions would change the bytes of the versioned zips (P1e).
- **Relative `Storage`**: it now goes to `<datos>/packages/<id>/`. The example `environment.json` uses `data/contexts`, which ends up in
  `data/packages/<id>/data/contexts` after migrating (the migration copies the old folder there): when generating the example zips
  (P1e) it is better to leave it as `contexts`.
- **Not done in this step** (outside P1a): `enc:v2` encryption and export without keys (P2; `ISecretProtector` and `enc:v1` stay
  as they were, and `config.json` already keeps the `Secrets` section when saving); library and templates (P3; `PackageArchive` already
  reads and writes `kind: config` and `template`, and `migrate` creates `library/configs/default.zip`); the trust of §8 (hash of
  `Mcp` and `FileTools` in `trust.json`) belongs to Web, the Core does not store it.
- **Untested**: `File.Replace` over a zip opened by another process on Windows; writes from two threads to the same store
  (everything goes under a lock, with no concurrency test); renaming only the case in a case-sensitive file system (Linux);
  lock release if the process dies (tested with a lock from a non-existent pid, not by killing a process).

### CLI P1c: packages (`Samples/WorkflowDesigner.Cli`, 2026-10-18, contract §12)

Replaces what «Web backend and CLI» says about `--env`, the temporary copy of the workspace and `run <nombre>` over YAML. CLI
README updated; `Cli.Tests` 103/103 (previously 57), two consecutive runs.

- **The temporary folder of `run x.zip` is the Core's** (`<datos>/work/run-<id>/`, `core-api.md` §9.5), not `%TEMP%/workflow-designer-cli/`
  as spec §9.3 says: the Core already extracts it and always deletes it (success, failure, suspension, cancellation, timeout) before
  the run ends, and sweeps those left by a dead process after 24 h. The CLI only uses `%TEMP%/workflow-designer-cli/<guid>/`
  for its own things (loose YAML, `validate`'s disposable host), with `finally` and the same 24 h sweep at the start of a command.
- **`--data` and `--workspace`**: `--data` defaults to `<workspace>/data`, or `%LOCALAPPDATA%\WorkflowDesigner\data`. `PackageStore`
  requires a workspace: without `--workspace` it is `<datos>/workspace`. `run`, `suspended`, `resume`, `session` and `migrate` open the Core
  over that; `validate`, `package *` and `list-steps` do not touch any user folder (host in `%TEMP%` or `PackageArchive`
  directly).
- **Sessions and `resume` need the package in the workspace** (the Core resolves `PackageId` through the `workflows/` index, and
  `PackageFile` «sin sesiones ni índice» (without sessions or index)): `session new x.zip`, `session turn --package` and `resume --package` copy the zip,
  byte for byte and with its `id`, into `workflows/` (it is validated first; if the `id` already exists with other bytes, it is replaced with a note).
  It must be stated that **the CLI writes into the workspace's `workflows/`** in those three cases; this was not in the spec. `session turn` with a
  `--package` of another `id` is rejected before copying.
- **`run`**: `--source-dir <carpeta>` **replaces** the positional argument (spec §9.3 shows it next to `<paquete.zip>`; using both at once is a usage error). A loose YAML
  (`--config/--schemas/--prompts`, formerly `--env`) is dressed as a package folder in `%TEMP%` and runs as a
  `PackageDirectory`; its `id` comes from the hash of the YAML path so that its relative data and suspensions are stable across runs; plaintext keys in
  `--config` are encrypted (`enc:v1`) in the copy. `--env` answers with an error that names `--config`. There is no longer
  any YAML pre-validation: compile errors come from `StartAsync` with the same `fichero:línea:columna` (`paquete.zip!workflow.yaml` for a zip).
- **Exit codes**: the current ones. A zip or folder rejected (`InvalidPackageException`, `PackageTooLargeException`), `Duplicate…`,
  `NameConflict…` and `ReadOnly…` are 2; `package strip-secrets|rekey` are 2 with «no disponible hasta P2» (not available until P2). `migrate` with workflows
  that cannot be converted is 1 (the others are converted).
- **`--key`, `--key-file`, `WORKFLOW_DESIGNER_KEY`**: they are parsed (priority `--key` > file > variable, first line, empty is
  an error), `--key` warns on stderr, and they reach `CliHost.MasterKey`; **nothing in the Core uses them** until P2, and today there is no hook
  in `AddWorkflowDesigner` (P2 will have to add `MasterKeyProvider` and connect it there).
- **`package info`** shows Stdio with command and arguments, FileTools folders, `Storage`, the type of each key (`literal`,
  `variable`, `enc:v1`, `enc:v2`) and the **names** (never the values) of the MCP environment variables and headers; URLs, without
  query or user. `run` and `validate` print to stderr a warning for each Stdio server.
- **`package pack`** overwrites the zip at `-o` (atomic, RN-07); `package unpack` requires an empty or non-existent folder; `package new-id`
  rewrites in place unless `-o` is given. The analyzer accepts the `-o` aliases (`OptionSpec.Alias`).
- **`migrate`** opens the Core with `Workspace` = the folder; `PackageStore` creates `workflows/` and `data/work/` even with `--dry-run`
  (empty folders only; nothing else is touched).
- **Core defects seen, not touched**: `OpenFolder` (and therefore `run --source-dir`, `validate <carpeta>`) creates empty `schemas/` and
  `prompts/` in the user's folder; the listing of v1 suspended runs says `not resumable` without distinguishing lost context
  from a registry entry with no package.
- **Untested**: a real Ctrl+C against the process (the tests call `CliApp.RunAsync` with a cancelled token, which is what
  `Program` wires to the console), a CLI process killed by force (the 24 h sweep is tested with artificially made old folders),
  and `session`/`resume` with a workspace that another process has open (lock RN-08).

### Web P1b: packages (`Samples/WorkflowDesignerWeb`, 2026-10-18, contract §12)

Implements the routes of `PACKAGES-AND-TEMPLATES-SPEC.md` §9.2 except the library (`/library/*`, P3) over the API
of `core-api.md` §9, without touching the Core. The routes are summarized in `Samples/WorkflowDesignerWeb/README.md` (API section);
here, what the frontend (P1d) must know and what departs from the specification. Tests: `Samples/WorkflowDesignerWeb.Tests`
(`PackageEndpointTests`, `TrustTests` and the rest adapted to packages).

**Errors.** `{ "error", "code"?, "errors"?, ...extras }`. Mapping of the Core's exceptions (in `ApiPipeline.Map`):

| Exception | HTTP · `code` · extras |
|---|---|
| `InvalidPackageException` | 422 · the Core's `Code` (`entry-name`, `duplicate-entry`, `symlink`, `compression-ratio`, `missing-manifest`...) · `entry?` |
| `PackageTooLargeException` | 413 · `zip-size`, `too-many-entries`, `entry-size`, `total-size` · `entry?` |
| `PackageNotFoundException` / URL id that is not 32 hex | 404 · `package_not_found` |
| `PackageNotOpenException` | 404 · `package_not_open` |
| `DuplicatePackageIdException` | 409 · `duplicate-id` · `files[]` |
| `PackageNameConflictException` | 409 · `name_conflict` |
| `PackageConflictException` | 409 · `conflict` · `conflict` (`modified`\|`deleted`), `savedAt?` |
| `PackageHasChangesException` · `PackageOpenException` | 409 · `has_changes` · `package_open` |
| `PackageReadOnlyException` | 423 · `read_only` · `lockedBy?` |
| `InvalidWorkspaceNameException` | 400 (as before) |
| (trust, §8) | 403 · `untrusted_package` · `trust` (and 409 `trust_stale` when confirming with an old hash) |
| (old route) | 410 · `gone` · `replacement` |
| (not done yet) | 501 · `not_implemented` |

**Routes and DTO** (all camelCase; `PackageSummary` is the Core's: `{ id, name, file, updatedAt, description?, hasSecrets, open, readOnly?, problems[] }`):

- `GET /packages` → `PackageSummary[]` · `POST /packages { name, description? }` → 201 `PackageSummary` (`fromConfig` → 501 until P3).
- `POST /packages/import?name=` with the zip as body (`Content-Type: application/zip`, `application/x-zip-compressed` or
  `application/octet-stream`; any other type → 415) → 201 `PackageSummary`. Its own limit of 20 MiB (a larger `Content-Length` → 413 without
  reading; without `Content-Length`, it is cut when the limit is passed), `CrossSiteGuard` as with any POST. It is the only route with a non-JSON body.
- `GET /packages/{id}` → `{ package: PackageSummary, session?: PackageOpenResponse, trust: TrustDto }` (**added**: after reloading the
  page the client needs to know whether the package is still open and whether there are unsaved changes).
- `POST /packages/{id}/open` with an optional body `{ recovery?: "ask"|"recover"|"discard" }` (**added**; without it, `ask`) →
  `PackageOpenResponse = { editId?, readOnly, lockedBy?, recovered?, recoverable?, dirty?, trust? }`. With an orphaned copy that has
  unsaved changes and `ask`: `{ readOnly: false, recoverable: true }`, without `editId` (nothing was opened); it is repeated with `recover` or `discard`.
- `POST /packages/{id}/save { force? }` (optional body) → 200 `{ changed, package: PackageSummary }`; 409 `conflict`.
- `POST /packages/{id}/save-as { name }` → 201 `PackageSummary` with the **new id**; the edit session becomes the new package's
  (the client continues with the new id; the original stays as it was). The new package inherits the original's trust if it had it.
- `POST /packages/{id}/close { discard? }` (optional body) → 204; with changes and without `discard`, 409 `has_changes`.
- `POST /packages/{id}/rename { name }` → 200 `PackageSummary` (same `id`). `DELETE /packages/{id}` → 204 / 404.
- `POST /packages/new-id { name }` (**added**, not in §9.2: spec §4.5 asks for it for `duplicate-id`): `name` is the file name
  without `.zip`, because the id is ambiguous → 200 `PackageSummary` with the new id.
- `GET /packages/{id}/export?secrets=keep|strip` → the **saved** zip (download with `Content-Disposition`); unsaved changes of an
  open session are not included. `strip` → 501 until P2 (there is no `X-Required-Variables` or `?format=json` yet either); any other value → 400.
- `GET /packages/{id}/workflow` → `{ document, layout, yaml }` (empty layout if there is none) · `PUT` `{ document, layout? }` → `{ yaml }`.
  `GET /packages/{id}/yaml` → `workflow.yaml` of the working copy as a download (`text/yaml`, `<nombre>.yaml`).
- `GET|PUT /packages/{id}/config` → as `/environment`: masked keys, 204 on save, 422 with `errors` (strings).
- `GET /packages/{id}/schemas`, `GET|PUT|DELETE …/schemas/{n}`, `GET …/prompts`, `GET|PUT|DELETE …/prompts/{n}`: as before, within the package.
- `POST /packages/{id}/validate { document }` → `{ ok, errors, warnings }` (with the orphan warnings from the Core) · `GET /packages/{id}/tools`.
- `GET /packages/{id}/trust` → `TrustDto = { trusted, required, hash, mcp: { name, transportType, command?, arguments[], workingDirectory?, url? }[], fileToolsRoot?, fileToolsDirectories[] }` · `POST /packages/{id}/trust { hash? }` → `TrustDto` (optional body; with `hash`, if it no longer matches, 409 `trust_stale` with the current `trust`).
- `POST /runs { package, source?: "working"|"saved", document?, input, context? }` → 202 `{ runId }`. Without `package`, 400 (also if `workflow` arrives). `source` defaults to `working`: with the package not open, 404 `package_not_open`; `saved` runs the zip even if it is not open. `RunInfo.workflow` is the package's visible name and `RunInfo.packageId` its id.
- `POST /sessions { package, title? }` → 201 (id with invalid format, 404 `package_not_found`) · `GET /sessions?package=<id>` (`?workflow=` → 400
  with the warning). The rest is the same. `SessionDto.workflow` is the package's current name; `packageId`, `orphaned?`.
- `GET /secrets/status` → `{ unlocked: false, source: null, hasEncryptedSecrets }` · `POST /secrets/lock` → the same · `POST /secrets/unlock` → 501. Skeleton; P2 implements it.
- `POST /workspace/migrate { dryRun? }` → the Core's `MigrationReport` (`needed`, `dryRun`, `packages[]`, `configLibrary`, `warnings`, `errors`...).
  **The body is mandatory, and a missing `dryRun` counts as `true`**: nothing moves unless `false` is expressly requested.
- 410 Gone, with `{ error, code: "gone", replacement: "GET /api/packages/{id}/workflow" }` (new method and route, with literal `{id}` and `{name}`): `GET|POST /workflows`, `GET|PUT|DELETE /workflows/{name}`, `POST /workflows/validate`, `GET|PUT /environment`, `/schemas*`, `/prompts*` and `GET /tools`. `POST /workflows/to-yaml` and `from-yaml`, `/catalog`, `/runs/*` and `/sessions/*` remain.

**Trust (§8).** `PackageTrust` (`Hosting/PackageTrust.cs`): `required` is true if the configuration carries any `Mcp` server,
folders in `FileTools.AdditionalAllowedDirectories`, or a non-empty `FileTools.RootDirectory` (with `FileTools.Enabled = false` it does not
count); otherwise there is nothing to confirm and the package is trusted as is (so `hello-script` and packages without MCP run without
ceremony). The hash is the SHA-256 of those sections without secrets (MCP headers and environment variables do not enter it: what is shown to the
user is what gets confirmed, and changing a key does not ask for trust again). `<datos>/packages/<id>/trust.json` stores
`{ configHash, trustedAt }`. Trust is required in: `POST /runs` (the copy that runs: the working one or the zip), `GET …/tools`,
`POST …/validate` if any step carries a non-empty `tools`, and `POST /sessions/{id}/turns` (runs the saved zip). **Not** required
when resuming a suspended run (`POST /runs/{id}/resume`): the package had to be trusted in order to suspend (it has not been checked that a trusted
package cannot stop being trusted in between). `PUT …/config` on a trusted package records the new hash (the user edited it);
on an untrusted one it does not (editing does not clear other people's servers). The CLI does not ask (§8).

**Other decisions.**

- `WorkflowDesigner:DataDirectory` (appsettings; relative = against the content root) feeds `WorkflowDesignerOptions.DataDirectory`; empty = `<Workspace>/data`.
- `SampleWorkspace.EnsureCreated(root, reader?)` seeds when `root` does not exist or **has no file at all** (before: no entry; now it accounts for
  the Core creating an empty `workflows/`): `workflows/hello-script.zip`, `greet-llm.zip` and `ask-and-continue.zip` (new, with the schema `Approval`) and
  `library/configs/default.zip`, written with `PackageArchive`. Each zip carries a `config.json` with `Storage` `file` in `contexts` and `conversations` (relative, as
  the P1a note asks), `FileTools` with no extra folders and `Mcp: []`. The ids are random on each seeding (example zips with fixed ids are P1e). The repo's
  workspace (`workspace/`) remains in flat format: in Development nothing is seeded and `POST /workspace/migrate` would convert it; P1e converts it to zips.
- Body limits: 2 MiB (`MaxBodyKilobytes`) for JSON, 20 MiB only for `POST /packages/import` (with `IHttpMaxRequestBodySizeFeature` when the server allows it).
- A `..` name in a path (`/schemas/..`) is normalized by the HTTP client before sending and falls on the package path (`DELETE /packages/{id}/`): the server
  does not see it; the frontend must validate names before building the URL (`WorkspaceStore.ValidateName`).
- `manifest.updatedAt` has one-second resolution: two saves within the same second leave the manifest equal (the tests do not assume it changes).
- `DevelopmentWorkspaceTests` no longer lists workflows: it checks the workspace and a `dryRun` of the migration (the repo stays in flat format).

**Untested:** real LLM and MCP (`Stdio` included; the tests use a non-existent command and a URL that rejects the connection); concurrent writes
to the same package from two tabs; a real browser's `Sec-Fetch-Site` against the import; Linux/macOS; `IHttpMaxRequestBodySizeFeature` under Kestrel (the tests use
`TestServer`, where the `Content-Length` header is enough for the 413 and a body without length is cut on read); `recovery` after really killing the process (the test
releases the host, as `PackageStore.Dispose` does).

### Frontend P1d: packages (`ClientApp`, 2026-10-18, contract §12)

`src/api/client.ts` implements spec §9.2 as «Web P1b» left it (that subsection takes precedence where they differ); the mock
(`api/mock*.ts`) imitates it in full. What the UI does and what was decided where the contract does not say:

- **State**: `PackagesProvider` (`state/packages.tsx`, pure reducer `packagesReducer`) replaces `WorkflowFilesProvider` (deleted, with
  `useWorkflowFiles`). Provider order: Catalog → Workflow → Packages → Environment → Tools → Run. `useEnvironment()` and `useTools()`
  read the open package (`hasPackage`; without a package, `environment` is null and the view says «Abre un paquete»). Opening waits for the
  catalog to be loaded (the containers' layout needs it) and closes the previous package **after** opening the new one, with
  `close { discard: true }` (the caller already asked for confirmation if there were changes).
- **«Sin guardar» (`●`)**: document (`useWorkflow().dirty`) **or** working copy changed **or** drafts of the configuration panel. The
  working copy counts as changed after any write the server accepts (PUT of config, schema or prompt, or of the document on save): it does not
  compare content, so an identical PUT also marks it dirty. On opening, it takes part of `recovered` or `dirty` (if the server gives them). It is
  shown in the header title and in `document.title`. The warning when closing or reloading the tab uses `useLeaveGuard(..., { navigation: false })`:
  switching between «Diseñador» and «Configuración» does not ask (it is the same package); opening, creating, importing and closing another package does.
- **Save** (button and Ctrl+S): 1) sends the drafts of the configuration panel (`flush`, which validates and does the PUTs; if it fails,
  nothing is packed), 2) `PUT /packages/{id}/workflow` if the document has changes, 3) `POST /save`. The «Guardar» button of the configuration
  panel only applies to the working copy (the warning says «falta guardarlo»). A 409 with `conflict` opens the dialog («Sobrescribir» = `save { force }`,
  «Guardar como…», «Recargar» = `close discard` + open, «Cancelar»). `save()` returns `'saved' | 'conflict'` so as not to report success on a conflict.
- **Routes**: `#/designer/<nombre>` and `#/config/<nombre>` use the package **name**, not the id (it is resolved against `GET /packages`);
  `#/environment` is read as `#/config`. Without a package in the URL, the designer opens the first one in the list; the configuration does not.
  Renaming and «guardar como» update the URL.
- **Recover**: `open` with `recoverable` opens the dialog («Recuperar» / «Descartar» / «Cancelar») and repeats `open { recovery }`; cancelling
  opens nothing and returns the URL to what it was.
- **Trust (§8)**: read from `open.trust` (if missing, `GET /packages/{id}/trust`). It asks before running (`useRun().start`)
  and before launching a session turn if `trust.required && !trust.trusted`, showing `trust.mcp`, `fileToolsRoot` and
  `fileToolsDirectories`; «Confiar y ejecutar» does `POST /trust { hash }` with the hash shown. A 403 `untrusted_package` (the UI
  thought it was trusted) opens the same dialog with the `trust` from the error and retries once; the same when validating with tools. A 409
  `trust_stale` leaves the dialog open with the new `trust`. Tools are requested again when trust changes. It does not
  ask when resuming.
- **Migration**: on startup, `POST /workspace/migrate { dryRun: true }`; with `needed` a notice («Revisar y migrar…») appears with
  the report, and migrating calls with `dryRun: false`. A server without the route offers nothing. The body is always sent (a missing `dryRun`
  counts as a simulation).
- **Key lock (P2 skeleton)**: `GET /secrets/status` in the header, with an informational dialog; no password and no
  `POST /secrets/unlock` (501 today). If the route fails, nothing is shown.
- **Menu**: «Nuevo» (empty; `fromConfig` exists in the client, but the UI does not offer it until P3), «Abrir», «Guardar», «Guardar como» and, under
  «Más»: «Importar .zip» (asks for the name, proposed from the file, and checks the 20 MiB before uploading), «Exportar paquete» (the
  saved zip; warns if there are unsaved changes), «Exportar sin claves» (shows the server's message while it answers 501 and,
  with `X-Required-Variables`, the variables), «Exportar YAML» (first sends the document to the working copy and requests `GET …/yaml`),
  «Renombrar», «Cerrar» and «Borrar». «Duplicar» disappears («Guardar como» covers it). A package with `duplicate-id` offers «Asignar un id
  nuevo» (`POST /packages/new-id`) before it is opened.
- **Sessions and runs**: `POST /runs { package, source: 'working', document, input, context? }` with the editor's document;
  `POST /sessions { package }` and `GET /sessions?package=`. The last remembered session is stored by package **id**
  (`workflowDesigner.lastSession.<id>`); the remembered entry for the run is still keyed by name. The «los turnos usan lo
  guardado» warning now counts any package change (document, configuration, schemas and prompts).
- **Validation**: the server's `warnings` are shown apart from the errors (not counted in `ok`); the local validator detects an
  unknown step type using the catalog's tags (`validateDocument(doc, { knownTags })`).
- **Names**: `isValidResourceName` applies RN-01 in full (up to 100, no `..`, no trailing dot, no device names), and the
  HTTP client rejects locally schema or prompt names that the browser would normalize (`..`, `.`) before requesting.
- **Behaviour changes in the canvas and palette** (from `PENDIENTES.md`): with a nested step selected, the palette adds as its sibling
  right after it, inside its container (before, at the end of the root); a click on an edge moves focus to the canvas section
  (`tabIndex=-1`) so that Supr reaches it (cannot be tested in jsdom: it does not paint edges).
- **Duplicates unified**: `ConfirmDialog` moves to `shared/ui` (used by the menu and by canvas deletion), the duplicated `beforeunload`
  in `useWorkflowMenu` disappears (`useLeaveGuard` covers it) and `downloadText` moves to `shared/download.ts` alongside
  `downloadBlob`.
- **Mock**: `createMockApi()` returns the API plus `controls` (`touchSaved`, `setUntrusted`, `setDuplicateIds`) and accepts `untrusted`,
  `recoverable`, `locked`, `duplicateIds`, `legacyWorkspace` and `secrets`; `mockPackageId(nombre)` gives the stable id of each seeded package.
  In tests, `src/test/OpenPackage.tsx` opens a package.

**Contract assumptions the backend must meet** (all taken from «Web P1b»; if they change, the client breaks at these points):
`open` without a body or with `{ recovery }` and response `{ editId?, readOnly, lockedBy?, recovered?, recoverable?, dirty?, trust? }`;
`save` with 409 `{ code: 'conflict', conflict, savedAt? }` and 423 when read-only; `close { discard }` with 409 if there are changes; errors
`{ error, code?, … }`; 403 `untrusted_package` with `trust` on `POST /runs`, `GET …/tools`, validating with tools and turns;
`POST …/trust` accepts `{ hash }` and returns `TrustDto` (409 `trust_stale` with `trust`); `GET …/export?secrets=` returns the zip with
`Content-Disposition`; `GET …/yaml` returns text (or `{ yaml }`); `POST /workspace/migrate` with an explicit `dryRun`. If `open` does not
carry `trust`, the client requests `GET …/trust` and, if that fails, does not ask (the server decides).

**Not tested in a real browser** (jsdom with the mock API only): the native `beforeunload` dialog, the real download of a zip and of
YAML (`URL.createObjectURL`), uploading a real zip, focus after clicking an edge, the look of the «Más» menu and of
the header with everything new (it may be tight in narrow windows), `recovery` after killing the backend, the 409 with a really modified zip,
and trust against the real backend.

### Frontend P2/P3a: master password and configuration library (`ClientApp`, 2026-10-18, contract §12)

`client.ts` and the mock follow specification §9.2 and, where it differs, «Web P2/P3» (that subsection takes precedence). `npm run typecheck` clean; Vitest: 540 pass and 1 is skipped (the usual one,
`EnvironmentPanel.realEnvironment`, with no real `environment.json`); previously 406. The new work has not been seen in a real browser. Decisions where the contract is silent:

- **Password (`state/secrets.tsx`, `SecretsProvider`, between `ApiProvider` and `CatalogProvider`)**: it keeps the `GET /secrets/status` result (with `firstTime`), the dialog and `withUnlock(op)`: it runs the operation, and on 423 `secrets_locked` it opens the dialog, waits
  and **repeats once**; if the user cancels, it rejects with the original error (the server's notice). **The password does not live in the provider**: it is typed into `UnlockForm` (the form's local state), sent with `unlock()`, and cleared when
  the server answers, good or bad (to retry it must be typed again); the dialog unmounts the form when it closes. Not in `localStorage`, not in cookies, not in logs; the field is `type=password` with `autoComplete` `current-password` / `new-password`.
  `requestUnlock` only waits if a host is mounted (`SecretsLock` is registered while the server has the route): without one it returns `false` instead of hanging.
- **Where the 423 is handled**: running (`useRun().start`), creating a session and sending a turn, saving and «guardar como» of the package, `PUT /config` of the package and of a library configuration, and «guardar como configuración». **Validating**: the Core returns
  «Claves bloqueadas…» as an error in the result (not 423); `ValidationIndicator` offers «Desbloquear claves…» when `locked` and some error starts with that text (**fragile: it is detected by the text**, as the CLI does; a `code` in
  `WorkflowError` would fix it) and also on a real 423. `wrong_master_key` on unlock appears on the form itself; on a save, as the server's message.
- **Lock and notice**: the label comes from the real state («Claves: desbloqueadas / bloqueadas / sin contraseña»). With `firstTime` the dialog asks for the password twice, warns that losing it forces all keys to be re-entered, and requires that both match.
  When unlocked, the dialog offers «Bloquear» (`POST /secrets/lock`) and warns that, if the password came from the server, it returns on restart. «Claves bloqueadas · Desbloquear» (`SecretsBanner`, not modal, `role=region`) appears with `hasEncryptedSecrets && !unlocked`. The state
  is read again when another package is opened and after saving.
- **Save warnings** (`warnings` from `POST /save` and from `PUT /config`, package or library): `warning` toast (12 s instead of 4). `api.savePackage` returns `{ warnings }` and `api.saveConfig` / `saveLibraryConfig` return `string[]`; the client handles `204` and `{ warnings }`.
- **Export without keys**: no contract change (`X-Required-Variables`); if the package carries no variables, a notice says so. A library configuration is also exported without keys (`export?secrets=strip`) with the same variables dialog.
- **Library** (`features/library`, `#/library/configs|templates/<nombre>`): `HashRoute` gains `library?: { kind, name }` and the `library` view (the URL does not name a package; the open one stays open and `usePackageRoute` does not rewrite the hash in it). Tabs
  «Configuraciones» (list with «Claves», editor, import, export with or without keys, rename, delete) and **«Plantillas»: `TemplatesTab` is a placeholder («próximamente» (coming soon)) with the signature `{ name, onSelect }`** for P3b; the client already accepts `kind: 'templates'` in
  `renameLibraryItem`, `importLibraryItem` and `exportLibraryItem`. **The editor is the Configuration one**: `EnvironmentPanel` gained `library?: { name }` and `extraActions?`, and the source is switched by `EnvironmentSourceProvider` (new in `state/environment.tsx`);
  in library mode there are no schemas or prompts, the open package is not marked «Sin guardar», and the notice is «guardada en la biblioteca» (saved in the library). Since features do not import each other, **the app passes the panel** (`<LibraryView ConfigPanel={EnvironmentPanel} />`) and the buttons
  (`<EnvironmentPanel extraActions={<ConfigLibraryActions />} />`). Renaming and deleting first ask if the editor has unsaved changes (`confirmLeave`).
- **Guardar como configuración / Aplicar configuración…** (Configuration view): before copying, the panel's drafts are sent (`usePackages().flushDrafts`, new, alongside `draftsDirty`). «Guardar como» with or without keys; **`POST /library/configs` with `strip` does not list the variables**
  (Web P2/P3), so the UI reads them from the saved configuration: the `${VAR}` of `ApiKey` and of MCP headers and environment variables (`referencedVariables`; it includes the references that were already there, which the recipient must also define). «Aplicar» chooses a configuration and mode
  (**Reemplazar** by default, **Combinar**), goes through a confirmation and shows `conflicts[]`, `reenter[]` and `warnings[]`; it leaves the package unsaved. **Applying does not record trust** on the server: after applying, `usePackages().refreshTrust()` (new) is called
  and a package with MCP or folders asks for «Confiar» again. «Nuevo desde configuración…» is under «Más» (`usePackages().create(name, fromConfig)`).
- **Moved to `shared/` and `domain/`** so as not to import between features: `NameDialog` (generic, with `validate`), `VariablesDialog`, `formatUpdatedAt` and `validateName` (the ones in `features/packages` are thin wrappers).
- **Mock** (`mockLibrary.ts`, `mockConfig.ts`): three seeded configurations (`default` without keys, `con-claves`, `otro-equipo` with a key «de otro equipo» that appears in `reenter`); `createMockApi({ secrets: { unlocked, source, hasEncryptedSecrets, firstTime?, password? } })` with unlock/lock holding state and 422
  `wrong_master_key` (`secret_tampered` is not simulated); locked with encrypted keys, running and opening a session return 423 only if the workflow has an `LlmStep` (case 18), validating returns the error, and saving a new literal key without a password returns 423 (`RequireMasterKeyForNewSecrets`).
  `exportPackage('strip')` and the configuration export list `WD_<SERVIDOR>_<CLAVE>`. The key rules (masking, sentinel, validation) moved from `mock.ts` to `mockConfig.ts` and are used by packages and library. **Effect on the existing mock**: the example MCP server carries a token,
  so all seeded packages have `hasSecrets`.
- **Catalog (`PENDIENTES.md`)**: `schemaToFields` no longer marks as mandatory (`*`) a property with a default value even if the schema lists it in `required` (test in `schemaToFields.test.ts`). The other «Catalog» items (`structural: ['dependsOn']`, `required` of `BreakStep`, empty descriptions)
  belong to the Core or the framework; of «Sessions», nothing is verifiable in jsdom (narrow window and message box, CSS and browser; the message in English, Core; repeated titles, not decided which datum distinguishes them; orphaned sessions, no route lists them).

**Contract assumptions** (from «Web P2/P3»; if they change, the client breaks at these points): `GET /secrets/status` with `firstTime`; `POST /secrets/unlock { password }` and `lock` return the state (or 204: the client asks) and 422 `wrong_master_key`; `POST /packages/{id}/save` with
`warnings`; `PUT …/config` and `PUT /library/configs/{n}` with 204 or `{ warnings }`; `GET /library/configs/{n}` masked; `POST /library/{kind}/{n}/rename { name }`; `POST /library/configs { name, fromPackage, secrets }` (the open package); `…/apply { package, mode }` with `{ conflicts, reenter, warnings }`;
`import?name=` and `export` (`?secrets=strip` on configurations, with `X-Required-Variables`); the errors `{ error, code }` with `secrets_locked` (423), `wrong_master_key` (422), `library_name_conflict` (409) and `library_item_not_found` (404). **Not used**: `overwrite`, `style=empty`, `format=json`, `GET /packages/{id}/template-origins`, or anything about templates.

**Not tested in a real browser** (jsdom with the mock API only): the dialog with the browser's password manager (which should not offer to save it), focus when opening and closing the password dialog, the real download of a zip exported without keys, uploading a configuration, the look of the header with
«Biblioteca» and the lock (it was already tight below ≈1100 px) and of the locked-keys notice, the library list with many configurations, the configuration editor inside the library in narrow windows (two columns), and everything against the real backend.

### Core P2 and P3: portable encryption, library and templates (`Samples/WorkflowDesigner.Core`, 2026-10-18, contract §12)

Full API, errors and their HTTP mapping in `core-api.md` §10. Here, what was decided where the specification does not say so, and what departs from it.
`Core.Tests` 351/351 (previously 294), `Web.Tests` 211/211, `Cli.Tests` 103/103. `Web.Tests` failed in 2 of 7 full runs, always in the cleanup of
`PackageEndpointTests.A_working_copy_with_unsaved_changes_is_offered…` (`UnauthorizedAccessException` when deleting the test folder; the test only catches `IOException`), and passes 6/6 alone and 3/3 in the last full runs: it was not checked whether this happened before P2/P3.

- **Without a password, a new secret is still encrypted as `enc:v1:` with a warning** (spec §9.2 and §5.3 ask for 423 if «saving requires encrypting and is locked»). That is what `ConfigStore.Save` does by
  default, so as not to break the Web and CLI tests that save keys with the user's protector; with `WorkflowDesignerOptions.RequireMasterKeyForNewSecrets = true` it throws
  `SecretsLockedException` (423 `secrets_locked`). **Web must enable it** once it carries the unlock dialog (and decide whether the CLI does too). Already saved `enc:v1:` values are migrated to v2 when the package is saved with the
  password unlocked (`PackageStore.Save` calls `ConfigStore.Reseal`), and so are v2 values with another salt; without a password they are saved as they are, with the warning in `SaveResult.Warnings`. `Reseal` never blocks
  a save: a value that does not decrypt is left and reported; a password that does not open the package's verifier leaves everything as it is, with a warning.
- **`ISecretProtector` remains the user's protector (read-only for v1 and write fallback)** and the password is a separate service, `MasterKeyProvider` (`TryAdd`; `Options.MasterKey` or `WORKFLOW_DESIGNER_KEY`; the
  configuration wins over the variable because that is what the CLI resolves with `--key`/`--key-file`). `Lock()` forgets the password even if it came from the configuration or the variable (until restart). `ConfigStore` gained
  a constructor with `MasterKeyProvider` and `requireMasterKey`; the earlier ones remain (without a password: v2 is read as locked).
- **Iterations**: those of new values are always 600,000 (`PasswordSecretProtector.DefaultIterations`); there is no user setting. Tests use 1,000 with `internal` constructors
  (`MasterKeyProvider(configured, environment, iterations)`, `RekeyRequest.Iterations`). Added beyond the spec: when **reading**, a value with iterations outside 1,000..10,000,000 is treated as malformed
  (tampered), so that a foreign package cannot force derivation for minutes.
- **Which salt governs**: that of the configuration's `Secrets.Verifier` (the verifier carries its own salt and iterations; it is not renewed when the default is raised). A package without a verifier and with v2 values
  from another origin re-encrypts them with a new salt on save (if they decrypt with the password) and creates the verifier. `Secrets.Verifier` is returned as is by `ConfigStore.Read()` (decision of P1a, with a test): it is not a key.
- **`RunManager.StartAsync` no longer wraps** `SecretsLockedException` or `WrongMasterKeyException` in `WorkflowValidationException`: they propagate typed so that Web answers 423/422 with its `code` (spec §5.3). The
  «valor alterado» cases and unreadable `enc:v1` remain `WorkflowValidationException` (422). **Validation** still returns everything as a result error. A workflow without LLM steps reads the raw configuration: it does not
  decrypt anything (case 18), which also removes the key derivation from those validations and runs.
- **Messages**: the new v2 messages are in Spanish («Claves bloqueadas…», «Contraseña maestra incorrecta para este paquete.», «El valor de el proveedor 'X' está alterado…»); the `enc:v1` ones keep their English (there are Web and CLI tests).
- **Package index version 2**: `index.json` stores the verifier of each zip; a version 1 index is discarded and rebuilt on startup.
- **Export without keys**: names `WD_<SERVIDOR>_<CLAVE>` with suffix `_2`, `_3` if two secrets would give the same name; `Empty` leaves it empty and lists what was blanked in `Blanked`. `ExportedPackage` and `SaveResult` gained optional properties (they do not break Web or CLI).
  The CLI's `package rekey` and `strip-secrets` are based on `PackageSecrets.RekeyFile` / `Strip` (the CLI still answers «no disponible hasta P2» (not available until P2) for them).
- **Library** (`LibraryStore`): configurations are editable by opening the zip in a temporary folder under `<datos>/work/lib-*` and going through `ConfigStore` (same masking and encryption rules), which is always deleted.
  Applying writes the `config.json` of the working copy without validating against the structure (problems come out as `warnings` to be fixed in the editor); `Merge` does not count identical values as a conflict and
  fallbacks are joined without repetition. The v2 `reenter` with a locked password cannot be computed: a notice with the number of keys is given instead.
- **Templates** — points where a decision was made: (1) `x-editor` and the rest of the schema keys of `parameters` are stored as they are; (2) `inputSchema` and `outputSchema` accept parameters (text mode), `id` and `type` do not;
  (3) only the schemas and prompts that some step names are copied (a loose resource of the template is not copied); (4) an inserted value is not tested against the engine: validation against the package is done by `WorkflowValidator` on the
  resulting document (the template Core only warns about role, tool, step type and schema); (5) the tools warning requires the caller to pass `availableTools` (listing them starts MCP, which is not done here); (6) the `sha256` of
  `manifest.templates` is a hash of the normalized content and not the zip's sha, so that renaming a template does not mark it as changed; (7) `manifest.templates[]` carries `templateId` in addition to what the spec has; (8) `CreateTemplateFromSelectionAsync` escapes any literal `${{` in the source steps to `$${{`, so that inserting without values reproduces the selection as is (there is a test); (9) context keys, prompts and
  `maxIterations` are the only candidates (those of the spec); a parameterized `maxIterations` is written as `"${{ params.x }}"` and becomes an integer again on insertion.
- **RF-25 (editing a template's content)**: resolved at API level with `GetTemplateAsync` + `SaveTemplateAsync` (the editor in template mode reads the template as a workflow plus resources and replaces it). A working-copy editing session
  **has not been built** for templates (`PackageSession` of `kind: template`): it is not needed for this and it was not proven that the canvas can be opened over that DTO; it stays as pending for Web/frontend.
- **Untested**: real DPAPI between two users or computers (the «otro equipo» (other machine) is simulated with another `ISecretProtector`); 600,000 iterations only in a round-trip test (the derivation was not measured); `MasterKeyProvider`
  with several threads asking for the same key at once (everything is under a lock, with no concurrency test); a template with `ParallelStep` or `SwitchStep` saved from a selection (tested with Switch on insertion and with Loop on saving);
  importing templates with huge `schemas/` (they share the package limits); `Rekey` of a package opened by another process.
- **For the next phase**: Web (`/api/secrets/*` over `SecretsManager`, `423 secrets_locked` on run, save and export, `export?secrets=strip` with `X-Required-Variables`, `fromConfig` in `POST /packages`,
  `/api/library/*` over `LibraryStore`, mapping of §10.1 and §10.2/§10.3 of `core-api.md`, `RequireMasterKeyForNewSecrets = true`, `WorkflowDesigner:MasterKey`); CLI (`CliHost` passes `key.Reveal()` to `Options.MasterKey`, `package rekey|strip-secrets`,
  `library list|import|export`, exit codes 2 for `secrets_locked` and `wrong_master_key`); frontend (lock and unlock dialog with double password the first time according to `firstTime`, notices from `SaveResult.Warnings` and from `warnings` when saving the configuration,
  Library view, insertion form with `schemaToFields`, «Guardar como plantilla» with the candidates from `SuggestTemplateParameters`, «la plantilla ha cambiado» state with `TemplateOrigins`).

### CLI P2/P3: master password, rekey, strip-secrets and library (`Samples/WorkflowDesigner.Cli`, 2026-10-18, contract §12)

CLI README updated; `Cli.Tests` 132/132 (previously 103; the test «not available until P2» was removed), two consecutive runs. Cases 13, 14, 15, 17, 18, 22, 23, 24, 26 and 27 of the spec through the CLI; case 22 inserts
the template twice in two cases of a Switch and **runs the resulting YAML without `--workspace`** (without a library).

- **Password**: `CliHost` sets `Options.MasterKey = key?.Reveal()` before `AddWorkflowDesigner` (the `WORKFLOW_DESIGNER_KEY` variable is already weighed by `MasterKey.Resolve`; if there is no `--key`/`--key-file`, the Core reads it itself).
  The `--key`/`--key-file` options are also accepted in `package rekey --from-v1` and in every `library` command. `MasterKey.EnvironmentOverride` (AsyncLocal) is the test hook for the variable.
- **Exit codes** (`CliApp`, a single `catch` of `ApiKeyDecryptionException`): `Locked` and `WrongMasterKey` = 2 with the §5.3 text in the CLI («falta la contraseña maestra (WORKFLOW_DESIGNER_KEY, --key-file o --key)…» (master password missing ...),
  which replaces the Core's message that refers to the interface lock); `Tampered` and `Undecryptable` = 1 with the Core's message. `validate` receives the lock as a result error without a code; the CLI
  distinguishes it **by the text** («Claves bloqueadas», «Contraseña maestra incorrecta») to give 2; if the Core changes those messages, `validate` will start giving 1 (there is a test). Asking the Core for a `WorkflowError.Code`
  would fix it.
- **`package rekey`**: `--from-v1` (password from `--key`/`--key-file`/variable) or `--old-key-file` + `--new-key-file`; mixing them, or giving only one half, is a usage error. Without `-o` it uses `PackageSecrets.RekeyFile`;
  with `-o`, `Rekey` and `WriteFile` to another zip. A non-empty `Failed` (an `enc:v1` from another user) = exit code 1, lists `Proveedor 'X'` on stderr and leaves that value as it was. The iterations are always the Core's 600,000 (`RekeyRequest.Iterations` is `internal`): each `rekey` in the tests pays one derivation, no problem (~0.3 s).
- **`package strip-secrets <zip|carpeta> -o <zip> [--empty]`**: `-o` is mandatory and must differ from the source (the spec does not say whether it should overwrite; not destroying keys by accident was chosen). Stdout is only the
  variables, one per line; the summary and `emptied: …` go to stderr. «Exportar con o sin claves desde `pack`/`export-yaml`» (Export with or without keys from `pack`/`export-yaml`): **not applicable** (there are no keys to touch in `export-yaml`; `pack` starts from a folder that
  `strip-secrets` already accepts as a source).
- **`library list|import|export`** over `--workspace` (mandatory; creates `library/` and `data/` in it, as `migrate` does). `export configs … --strip [--empty]` added (the spec does not ask for it; an exported configuration carries the encrypted keys). Also added: `library template show` (parameters, requirements) and, without the spec §9.3 naming them, `library template instantiate`, `library config save` and `library config apply`.
- **Where `instantiate` inserts**: at the end of the root unless `--after <stepId>` (right after that step, in the list that contains it) or `--into <stepId>[/<contenedor>[/<caso>]]` (at the end of that container; `switch/cases/a`,
  `switch/default`, `grupo`). Parameters: `--param k=v` repeatable (new `OptionSpec.Repeatable`; the other options still reject repetitions) and `--params-json`; the value is typed with the template's schema
  (`string` = text as is; the rest is read as JSON; without a type, JSON if it parses). A value that does not fit (the case `n: "3"` of 24) is the Core's `TemplateParameterException` = exit code 2 and writes nothing. After inserting, the package's
  workflow is validated and the problems come out as warnings on stderr (§7.3).
- **How it edits a zip** (`PackageEditing.EditAsync`): it validates the zip, copies it to a temporary workspace, `PackageStore.Open` → session, the change, `PackageStore.Save` (re-encrypts with the package's salt if there is a password; never writes
  a plaintext key) and rewrites the original with `PackageArchive.WriteFile` only if `Changed`, restoring `manifest.name` (Save makes it equal to the temporary file name). **`LibraryStore.InstantiateAsync`/`ApplyConfig` require
  the package to be open in the `PackageStore` that the `LibraryStore` was built with**: the CLI builds another `LibraryStore` with `Workspace` = the user's and the temporary `PackageStore` (the normal host's one gave
  `PackageNotOpenException`). The RN-08 lock of the original zip is not checked: if the backend has it open, the designer will see a 409 when saving.
- **Effects of saving under the Core's rules** (documented in the README): with a password, the package's `enc:v1` values become v2; the YAML is emitted again with `YamlEmitter` (the comments of the original YAML are lost);
  without a previous layout no `layout.json` is created, and with one the new steps go below the existing ones (only if the template brings a layout).
- **Core defects noted by the previous agent**: (1) `OpenFolder` creates `schemas/` and `prompts/` in the user's folder: **solved from the CLI** for `validate <carpeta>` and `run --source-dir` (they work on a
  temporary copy; a test compares the full tree); the defect remains in the Core. (2) The listing of v1 suspended runs says `not resumable` without distinguishing lost context from a registry entry with no package: **it cannot be solved from the CLI**
  (the CLI only sees the Core's boolean); it is noted.
- **Untested**: `library` against a workspace that another process has open (RN-08 lock); an `instantiate` with a template that carries a `ParallelStep` or with `--into` a Parallel branch (only Switch, implicit Group and root tested); `library config apply` with a password different from the library's (the `enc:v2` values are «a reintroducir» (to be re-entered) only when there is no password to check them; there is no test of the `reenter` notice); the real
  `WORKFLOW_DESIGNER_KEY` variable of the process (the tests use the AsyncLocal and assume the real one is not defined); Ctrl+C during `rekey` or `instantiate`.

### Web P2/P3: encryption, library and templates (`Samples/WorkflowDesignerWeb`, 2026-10-18, contract §12)

Routes from `PACKAGES-AND-TEMPLATES-SPEC.md` §9.2 that were missing, over `SecretsManager`, `LibraryStore` and `PackageStore` (API in `core-api.md` §10), without touching the Core. Summary
in the `WorkflowDesignerWeb` README. `Web.Tests` 238/238 (previously 211), two consecutive runs. What the frontend must know and what departs from the specification:

**Host.** `WorkflowDesigner:MasterKey` → `WorkflowDesignerOptions.MasterKey` (source `configuration`; the `WORKFLOW_DESIGNER_KEY` variable is the source `environment`) and
`WorkflowDesigner:RequireMasterKeyForNewSecrets` (**default `true`**, deviation 1 of Core P2). With `true`, saving a new key without a password is 423 `secrets_locked` and
writes nothing; with `false` the Core encrypts it as `enc:v1:` and warns (Web tests use `false` except the encryption ones, so as not to unlock in each one).

**New errors** (`ApiPipeline.Map`, before the generic mapping; no message carries a password, value or ciphertext):

| Exception | HTTP · `code` · extras |
|---|---|
| `SecretsLockedException` | 423 · `secrets_locked` |
| `WrongMasterKeyException` | 422 · `wrong_master_key` |
| `ApiKeyDecryptionException` | 422 · `secret_tampered` \| `secret_undecryptable` (the `Code` of `Reason`) · `provider?` |
| `LibraryItemNotFoundException` | 404 · `library_item_not_found` |
| `LibraryNameConflictException` | 409 · `library_name_conflict` |
| `TemplateParameterException` | 422 · `invalid_parameters` · `errors: [{ parameter, message }]` |
| `InvalidTemplateException` | 422 · `invalid_template` · `errors: string[]` |
| `InvalidTemplateSelectionException` | 422 · `invalid_selection` |
| unknown `{kind}` in `/library/{kind}` | 404 · `library_kind_unknown` |

A zip in the library with a broken template comes in as `InvalidPackageException` (422 `invalid-template`, `wrong-kind`, `not-a-zip`...). **Core limitation:** `RunManager.StartAsync` wraps «valor alterado» (altered value) and unreadable `enc:v1` in
`WorkflowValidationException` (422 with `errors[]`, **without** `code`), so `POST /runs` does not return `secret_tampered` as a code; the message does come through («El valor de el proveedor 'p' está alterado…»). `secrets_locked` and `wrong_master_key`
do arrive typed (also in `POST /sessions/{id}/turns`). Validation (`POST …/validate`) returns those cases as `errors` with `ok: false`, as before.

**Encryption.**

- `GET /secrets/status` → `{ unlocked, source: "environment"|"configuration"|"session"|null, hasEncryptedSecrets, firstTime }` (`firstTime`: the workspace does not know any verifier; the dialog asks for the password twice, the server receives one).
- `POST /secrets/unlock { password }` → the same state. Empty or missing `password` → 400; with known verifiers, a password that opens none → 422 `wrong_master_key` and nothing changes. `POST /secrets/lock` → state (also forgets the configured one until restart).
  The password lives only in the process memory; it appears in no response or log (there are tests that search for it in responses, SSE events and all logs).
- `POST /packages/{id}/save` → `{ changed, package, warnings: string[] }` (**`warnings` added**: keys that were not re-encrypted to v2 for lack of a password or because they did not decrypt). Saving is never blocked by this.
- `PUT /packages/{id}/config` → **204 without notices, 200 `{ warnings: string[], changed }` with notices** (e.g. a key that stays `enc:v1` with `RequireMasterKeyForNewSecrets=false`). The 204 is kept so as not to break the P1d client; the client must handle `undefined | { warnings }`. 423 `secrets_locked` and 422 `wrong_master_key` as above.
- `GET /packages/{id}/export?secrets=keep|strip&style=variables|empty&format=zip|json`: `strip` removes all keys (§5.6) and **does not need a password**; header `X-Required-Variables` (comma-separated list, empty if none) and, with `format=json`,
  body `{ fileName, contentBase64, requiredVariables, blanked }`. `style` and `format` are **added** to §9.2. It still exports the saved zip, not the working copy.
- `POST /packages { name, description?, fromConfig? }`: `fromConfig` = name of a configuration (copy with its keys and verifier; 404 `library_item_not_found`, nothing is written). The resulting package inherits the trust corresponding to its `config.json` (nothing is recorded: if it carries MCP or folders, it asks for trust).

**Library** (`{kind}` = `configs` | `templates`; `LibraryItemSummary` = `{ kind, name, file, id?, description?, updatedAt, hasSecrets, steps?, parameters?[], requires?, sha256?, problems[] }`; an unreadable zip comes out with `id` null and `problems: ["invalid: …"]`):

| Route | Effect |
|---|---|
| `GET /library/{kind}` | `LibraryItemSummary[]` |
| `DELETE /library/{kind}/{n}` | 204 · 404 |
| `POST /library/{kind}/{n}/rename { name }` | 200 summary · 409 if the name exists. **Added** (§9.2 only names it for templates; RF-14 asks for it for configurations). No package changes |
| `POST /library/{kind}/import?name=` | zip as body (`application/zip` \| `x-zip-compressed` \| `octet-stream`; 20 MiB; other type 415) → 201 summary; wrong type 422 `wrong-kind`; repeated id in the folder → new id; repeated name 409 |
| `GET /library/{kind}/{n}/export` | zip. For `configs`, `?secrets=strip` (+ `style`, `format=json`) as with a package; for `templates`, `secrets=strip` → 400. **`strip` added**; the response of `POST /library/configs` with `strip` does **not** list the variables (the Core does not return them; they are obtained by exporting with `strip`) |
| `GET /library/configs/{n}` | the **masked** configuration (same shape as `GET /packages/{id}/config`) |
| `PUT /library/configs/{n}` | body = configuration → 204, or 200 `{ warnings, changed }`; 422 `errors[]` if not valid; 423/422 for encryption as with a package |
| `POST /library/configs` | `{ name, fromPackage, secrets?: "keep"\|"strip", style?: "variables"\|"empty", description?, overwrite? }` → 201 summary (the package must be open: 404 if not; it starts from its working copy). `overwrite` keeps the `id` |
| `POST /library/configs/{n}/apply` | `{ package, mode?: "replace"\|"merge" }` → `{ mode, conflicts: [{ kind, name }], reenter: string[], warnings: string[] }`. The package is left with unsaved changes. It does not record trust: applying a configuration with MCP/folders leaves the package untrusted until `POST …/trust` |
| `GET /library/templates/{n}` | `{ manifest, template: { name, description?, parameters?, steps }, layout?, parameters?, requires: { roles, roleParameters, schemas, prompts, tools }, schemas: string[], prompts: string[], sha256, warnings[] }` |
| `POST /library/templates/suggest` | **New route** (§9.2 does not have it): `{ package, stepIds[], document? }` → `{ stepIds[], candidates: [{ key, kind: role\|prompt\|maxIterations\|contextKey, stepId?, property?, value, suggestedName, selected }], schemas[], prompts[], warnings[] }`; 422 `invalid_selection` if it is not a step or consecutive siblings. `document` = the one being edited (it may have unsaved steps) |
| `POST /library/templates` | `{ name, description?, fromPackage, stepIds[], parameters: [{ key, name, description? }], document?, overwrite? }` → 201 summary (the `key`s are those of `candidates`); 409 if it exists and there is no `overwrite`; 422 `invalid_template` with `errors[]` (reserved parameter name, a key that is not a candidate...) |
| `PUT /library/templates/{n}` | `{ description?, parameters?, steps, layout?, schemas: { nombre: texto }, prompts: { nombre: texto } }` → 200 summary. **Replaces all the content**: whatever is not sent is lost. RF-24 and RF-25 are resolved this way (there is no template editing session) |
| `POST /library/templates/{n}/instantiate` | `{ package, params?, prefix?, existingIds?, x?, y? }` → `{ steps[], stepIds[], layout, resources: { schemas[], prompts[], reused[], renamed{} }, warnings[] }`; the package must be open (404 `package_not_open`); 422 `invalid_parameters`; 400 if the prefix is not valid. Writes schemas and prompts to the working copy and notes `manifest.templates`; the frontend inserts `steps` (one undo step). `renamed` uses keys `schemas/<viejo>` and `prompts/<viejo>` |
| `GET /packages/{id}/template-origins` | **New route**: `[{ template, templateId?, sha256?, insertedAt?, stepIds[], status: "unchanged"\|"changed"\|"missing" }]` — the state «la plantilla ha cambiado desde la inserción» (template changed since insertion) (case 29). On the working copy if the package is open, and otherwise on the saved zip |

`instantiate` and tools: only if the template names `tools` and the package is trusted, the backend lists the tools with the package's configuration (`ToolCatalog`) and passes them as `availableTools` (a warning for each tool that does not exist). With an untrusted package **nothing is listed** (listing starts MCP servers)
and «No se han comprobado las tools de la plantilla: el paquete no es de confianza…» (the template's tools have not been checked: the package is not trusted…) is added to `warnings`. The remaining warnings (role the package does not have, unknown step type, non-existent schema, empty optional) are the Core's.

Body guards: `POST /library/{configs|templates}/import` shares with `POST /packages/import` the 20 MiB limit and the `application/zip` type (`ApiPipeline.IsImportPath`).

**Untested / not done.** Real DPAPI between two users; race of two tabs unlocking at the same time; `Sec-Fetch-Site` real on library uploads; `instantiate` with a real MCP (it is only tested that with an untrusted package nothing is listed, and FileTools with a trusted one); a template with `ParallelStep`/`SwitchStep` saved from a selection over HTTP;
templates with `layout` (the tests do not set positions, they only check that `layout` comes back); `POST /library/templates` does not create from scratch (only from a selection, as §9.2); no attempt limit on `unlock` (loopback only; each attempt costs one PBKDF2 derivation of 600,000 iterations).
`PackageEndpointTests.A_working_copy_with_unsaved_changes_is_offered_after_a_restart_and_can_be_recovered`: the final cleanup retries (up to 10 times) on `IOException` and `UnauthorizedAccessException`; it only swallows the last failure to delete the temporary folder, never an assertion.

### Frontend P3b: step templates (`ClientApp`, 2026-10-18, contract §12)

`client.ts` and the mock follow specification §6.3 and §7 and, where it differs, «Web P2/P3» (that subsection takes precedence). `npm run typecheck` clean; Vitest: 651 pass and 1 is skipped (the usual one); previously 540. None of this has been seen in a
real browser or against the real backend: jsdom with the mock API only. Decisions where the contract is silent, and assumptions:

- **Who opens what** (features do not import each other): `state/templateUi.tsx` (`TemplateUiProvider`, at the very top of `AppProviders`) holds «insertar» (`requestInsert(plantilla, destino?)`) and «guardar como plantilla» (`requestSave(stepIds)`), and a counter
  `templatesRevision` that rereads the template list and the origins when the library changes. The palette, the Library view and the canvas request; `features/templates` (`TemplateDialogs`, mounted in `App` next to `PackagePrompts`) draws the dialogs.
  The parameters form is `ParametersForm` from `features/inspector` (new in its `index.ts`): the app passes it to `TemplateDialogs`, just as `ConfigPanel` goes to the library. The preview likewise: `ReadOnlyWorkflowCanvas` from `features/canvas` is passed to `LibraryView`
  (`TemplatePreview`, `lazy` in `App`).
- **Insert** (`state/templateInsert.ts`, `useTemplateInsert`): `POST …/instantiate` with `existingIds` = the ids of the document being edited (with unsaved steps) and `x, y` only if it was dropped with the cursor; with what comes back, `useWorkflow().actions.insertSteps` (new reducer action:
  all steps in order from the index, or none if any id already exists, and **a single history step**). Then `noteChange()` (the package is left with changes: the server wrote schemas, prompts and `manifest.templates` into the working copy), rereads the schemas and prompts of the
  package (`useEnvironment().reload`) and notifies origins and list. The server's warnings (non-existent role or tool, empty optional, tools unchecked for an untrusted package) come out as `warning` toasts after inserting; they never block. Undo reverts the document and leaves the copied resources in place
  (as the specification asks, case 30).
- **Layout** (superseded on 2026-10-20: the layout is automatic and the positions the server returns are ignored; the text below is the original design): the positions the server returns are applied (corner at `x, y`). **When clicking (no position), the ones of the inserted roots are discarded** so the canvas places them after the rest (as `addStep` does); those of nested steps, relative to their parent, are kept. Inserting into the middle of the root re-lays out the whole root (`inOrder`, an existing rule). With a position (drag), `placeIn` gives the position relative to the slot's host, which is what is sent as `x, y`.
- **Target** (`domain/templates.ts`): the dialog offers «tras el step seleccionado» (after the selected step) (in its list, nested or not), «Primer nivel, al final» (root level, at the end) and the end of **each slot of each container**: Switch cases, `default`, Parallel branches, Loop and Group bodies, including those the catalog declares
  and the document does not have (the reducer creates the slot with `ensureSlot`). By default, the one from the palette rule (`insertionPointFor`) or, if it was dropped with the cursor, «Donde lo soltaste» (where you dropped it) (slot + index + position). Dragging a template uses its own MIME
  (`TEMPLATE_NAME_MIME` in `shared/dnd.ts`); the canvas does not insert on its own: it asks for the dialog.
- **Form** (`schemaToFields`, which gains `x-editor: role|prompt` without needing the step's tag → `RoleField`/`PromptField` with the roles and prompts of the open package): the mandatory ones (`required` without `default`) with no value, or with empty text, block «Insertar»
  (`missingRequiredParameters`); optional instance prefix with the Core's rules (`^[A-Za-z_][A-Za-z0-9_.-]*$`, up to 60); a 422 `invalid_parameters` is shown under the form (`parameterErrors(error)` reads `errors: [{ parameter, message }]`, which `ApiError.errors` types as
  `ValidationErrorDto`) and does not close the dialog. A read-only package does not allow inserting (it also disables the palette).
- **Shift+click / `rangeSelection`**: the reducer gains `rangeSelection: string[]` (anchor `selection` included; empty with a simple selection) and the action `selectRange(id)`: consecutive siblings of the same list between the anchor and `id` (`siblingRange`); if they are not siblings it sets `lastEditError`
  (canvas notice) and changes nothing. Any action that changes the document, `select` or undo/redo empties it; the view (`setViewport`) does not. On the canvas, xyflow reports its own selection **before** `onNodeClick`, which would move the anchor: while the key is held
  (`onMouseDownCapture`/`onClickCapture` in the section) its selection changes are ignored. `multiSelectionKeyCode` stays `null`: the multi-selection is ours, not xyflow's (no selection rectangle). The range's steps are painted selected (`CanvasDecorations.range`).
- **Save as template**: a «Guardar como plantilla…» button on the canvas toolbar (active with a selection) and a context menu (right click; `CanvasContextMenu`, `role=menu`, Escape or click outside close it; if the step is in the range it saves the range, otherwise that step). The dialog asks
  `POST /library/templates/suggest` with the document being edited, shows the candidates (**only the roles are ticked**, as the server says), the name and description of each ticked parameter (same rules as the Core, duplicates included) and what the template would carry (schemas, prompts, warnings), and
  creates with `POST /library/templates` sending `stepIds` = the ones the server resolved. A 409 `library_name_conflict` offers «Reemplazar» (`overwrite`); a 422 `invalid_template` shows `errors`; a 422 `invalid_selection` is stated in the dialog.
- **Origin** (`state/templateOrigins.tsx`, `TemplateOriginsProvider`): `GET /packages/{id}/template-origins` when the package is opened and each time `templatesRevision` changes; the inspector shows «Insertada desde «X». Es una copia…» and, depending on `status`, «(la plantilla ha cambiado desde entonces)» or «(la plantilla ya
  no está en la biblioteca)» (`TemplateOriginNote`). A failure of the route is not shown: it is informative.
- **«Plantillas» tab** (`features/library`): list with description, number of steps, parameters and required roles/tools; detail with a parameter table, requirements (`RequirementsList`, new in `shared/ui`), what it carries (schemas and prompts), notices and a **preview** in `ReadOnlyWorkflowCanvas`
  (read-only canvas with its own `WorkflowProvider`: the template's layout and autolayout where it is missing; nodes do not receive the pointer). Insert into the open package, edit, rename, delete, import (20 MiB, name proposed from the file) and export (`deleteLibraryItem` is new and generic).
- **RF-25 limited**: editing the **content** (steps) is not done on the canvas; it is limited to description and parameters (fixed name, type `string|integer|number|boolean`, description, default, mandatory, `role|prompt` selector, add and remove). Reasons: the Core has no template editing session (note of «Core P2 and P3»); the inspector does not accept `${{ params.x }}` in numeric fields; and the palette, the inspector and validation speak about the open package, not the template. Steps and layout are forwarded as they are. **Data loss if not warned**: `PUT /library/templates/{n}`
  replaces EVERYTHING, and `GET` does not return the content of the schemas and prompts that the template carries (only their names), so **saving a template with schemas or prompts discards them**; the dialog requires ticking «Entiendo que guardar descarta…» (I understand that saving discards…). The fix at the root is in the backend (`GET` with content,
  or a `PUT` that keeps what is not sent). Removing a parameter that the steps reference is rejected by the server (422) and the dialog says so.
- **Mock** (`api/mockTemplates.ts` and `mockTemplateEngine.ts`): three seeded templates (`ConCuentoActual` with a role and a prompt via `x-editor`, its own schema and prompt, and layout; `repetir-hasta`, with Loop, `maxIterations: ${{ params.n }}` and nested layout; `solo-script`, with no parameters or layout) and the behaviours of
  cases 22-29: id remapping with prefix and `_n`, full value with type and an absent optional that removes the property, prompts with the value substituted and `_2` if it differs, reused schemas or `_2`, role warning, tools unchecked with an untrusted package, shifted layout, `manifest.templates` in the working copy,
  candidates (`role` ticked; `prompt`, `maxIterations`, `contextKey` unticked) and `unchanged|changed|missing` by content hash (renaming does not change the hash). `PackageContent` gains `templates?` and enters the signature of «copia de trabajo cambiada» (working copy changed).
- **Canvas (from `PENDIENTES.md`)**: a `SwitchStep` with `cases: {}` and no `default` had a single slot (the declared `default`) and `LayoutBuilder.measure` treated it as a single body with no lane (no «default» label); now a single slot is painted without a lane only if the step declares no more containers.
  Tested in jsdom (the lane exists and accepts a drop); the appearance, no.

**Contract assumptions** (from «Web P2/P3» and `core-api.md` §10.3; if they change, the client breaks at these points): `GET /library/templates` with `steps`, `parameters` (names), `requires` and `sha256`; `GET /library/templates/{n}` with `template.steps`/`template.parameters`/`layout?`/`requires`/`schemas`/`prompts`/`warnings`; the `steps` of the template and of
`instantiate` have the shape of `StepNodeDto` (properties and containers as in the document), and `layout.positions` has the shape of the workflow's layout; `instantiate` returns `layout` with the roots already shifted to `(x, y)` and the nested ones relative; `suggest` and `POST /library/templates` accept `document`;
errors `{ error, code, errors }` with `errors` as `[{ parameter, message }]` on `invalid_parameters` and `string[]` on `invalid_template`; `GET /packages/{id}/template-origins` with the package open. **Not used**: `?secrets=` on templates (400), editing the content of a template.

**Not tested in a real browser** (jsdom with the mock API only): Shift+click and right click with a real mouse (jsdom delivers the events, but xyflow decides the selection with its own pointer events); dragging a template from the palette (only the drop logic is tested, with a fake `DataTransfer`);
the visual placement of what is inserted (positions, overlaps with other nodes, root re-laid out when inserting in the middle); the preview (size, view fitting, look in dark theme; in jsdom nodes are not measured); the context menu at the window edges; the insert dialog with many parameters or with a long prompt;
the palette with many templates; and the whole previous contract against the real backend.

### Plugins phase 1: framework and NugetLoader (2026-10-21)

- The contract and its deviations are in §25 («Plugins: unified loading (contract)», «Deviations of plugins phase 1»); §17 and §21-§24 are the former model, reduced to a note (phase 6).

## 10. Storage, suspension and tools (contract added 2026-10-18)

Decided by the user: the step that suspends is generic (`SuspendStep`: whoever answers can be a
person or another system); the context and the conversation are stored wherever the configuration says; FileTools and
MCP are ENABLED; FileTools is confined to the folder the process runs from and to those the user adds,
with environment variables.

### 10.1 New sections of `environment.json` (PascalCase, `${VAR}` and environment variables in all texts)

```json
"Storage": {
  "Context":      { "Type": "memory | file", "Directory": "" },
  "Conversation": { "Type": "memory | file | yaml", "Directory": "" }
},
"FileTools": { "Enabled": true, "RootDirectory": "", "AdditionalAllowedDirectories": [ "${HOME}/docs" ] },
"Mcp": [ { "Name": "x", "TransportType": "Stdio", "Command": "", "Arguments": [], "WorkingDirectory": "",
           "EnvironmentVariables": { "K": "v" }, "Url": "", "Headers": { "Authorization": "..." }, "TimeoutSeconds": 30 } ]
```

- `Storage` binds to the framework's `AITaskAgent:Storage` (default: memory). If it is `file`, the
  directory is mandatory; relative = against the workspace.
- An empty `FileTools.RootDirectory` = the working directory of the process running (backend or CLI). Only
  that root and the additional folders are allowed; `..` and links that leave them are rejected.
- The values of `Mcp[].Headers` and `Mcp[].EnvironmentVariables` are SECRETS: they are treated as `ApiKey`
  (encrypted on disk, the API returns `********`, `********` in the PUT = unchanged, `${VAR}` stays in plaintext).
- Framework limit: only the first MCP server registered in a collection takes effect (`McpExtension` registers
  with TryAdd). The designer must warn if there are several, or resolve it if possible.
  **Resolved in the Core** (2026-10-18): it does not use `McpExtension`; it connects each server with its own `McpToolProvider`, so
  all of them take effect. Valid transports: `Stdio`, `StreamableHttp`, `Sse` (case-insensitive); `Name` must be unique, case-insensitive.
  Details (expansion of `${VAR}`, `${HOME}`, relative root) in `core-api.md` §7.

### 10.2 Tools

- `GET /api/tools` → `{ name, description, source: 'FileTools' | 'Mcp:<server>' | 'Plugin:<id>' | 'Host', parameters: JsonSchema, qualifiedName }[]`
  (the ones that remain active with the current environment). **Qualified names (2026-10-22):** every tool has a second name
  `<source>:<name>` (`FileTools:write_to_file`, `Plugin:CodebaseAnalysis:view_file`, `Mcp:<server>:<tool>`, `Host:<name>`), returned as
  `qualifiedName`, accepted in `tools: [...]` (ignoring case, trimmed) and never ambiguous, so it is also the way out of `tool_ambiguous`. The name the
  model sees (`ITool.Name`) never changes (providers reject `:`). A tool whose bare name another tool wins (the plugin's `view_file` over
  FileTools') is listed twice: the winner under its bare `name`, the loser as an extra entry whose `name` is its qualified name (so a picker offers it).
- `LlmStep` accepts `tools: [nombres]` in YAML (new property of the step; the internal `Tools` property stays
  hidden). A non-existent name is a validation error with the name and the list of available ones. The
  catalog marks that property with the hint `x-editor: 'tools'`.
- Implemented: tools are only built for a workflow whose `LlmStep` names some of them (listing an MCP server starts or calls it). Servers that do not
  respond do not appear in `GET /api/tools` (they are logged), and their reason is added to the validation error for a missing name. For repeated names across sources, the first one wins
  (FileTools, then the servers in order) and validation warns about it in `warnings` (the text names the qualified name that still reaches the loser).

### 10.3 Initial context, suspension and resumption

- `POST /api/runs` also accepts `context?: { metadata?: { [k: string]: JsonValue }, conversationId?: string }`:
  initial metadata and a previous conversation (loaded from the `Storage.Conversation` store by id; if it
  does not exist, an empty one is created with that id).
- Run states: `suspended` is added. New events: `run.suspended` (`data`: `stepId`, `stepPath`,
  `marker`, `message`, `dataToReview`, `rejectionReason?`) and `run.resumed`.
- On suspension, the designer saves the context with the `Storage.Context` store and a record of the
  suspension in the workspace (`runs/suspended/<runId>.json`: workflow, snapshot of the YAML that ran, step path, marker, message, dataToReview, date).
  Resuming ALWAYS uses the YAML snapshot, not the current workflow (which may have changed).
- `GET /api/runs/suspended` → `{ runId, workflow, stepId, marker, message, dataToReview, suspendedAt }[]`
  (persisted: survives restarts if `Storage.Context` is `file`; with `memory` the record is kept but
  the context is not, and resuming gives 409 with a clear message).
- `POST /api/runs/{runId}/resume` with `{ response: JsonValue }` → 202 `{ runId }` with a NEW runId and
  `resumedFrom` in `run.resumed`. If the response does not meet the `outputSchema` of the `SuspendStep`, the step suspends again
  with `rejectionReason` (`run.suspended` event again).
- `DELETE /api/runs/suspended/{runId}` → 204: discards the suspended run (deletes the record and the saved context); 404 if there is none
  with that id; 409 if that run is in progress or being resumed.
  `POST /api/runs/{runId}/cancel` on a suspended run does the same and answers 202 `{ status: 'cancelled' }`.
- Additionally implemented (additive): `run.suspended` is the **last** event of that run, and the SSE closes after emitting it (as after
  `run.completed`); resuming creates another run, whose second event (after `run.started`) is `run.resumed` with `data.resumedFrom`.
  `GET /runs/{id}` adds `suspension` (`stepId`, `stepPath`, `marker`, `message`,
  `dataToReview`, `rejectionReason?`) and `resumedFrom`; it also answers for a suspended run that only exists on disk (after a restart).
  `GET /runs/suspended` adds `stepPath`, `rejectionReason`, `resumedFrom` and `resumable` (false = context lost; resuming would give 409).
  The conversation of `conversationId` is saved when the run ends in any state except `cancelled`; with
  `Storage.Conversation = memory` it lives in the process (shared among runs until restart).
- CLI: `run --context fichero.json --conversation id`, `suspended` (list) and `resume <runId> --answer-json '…'`.
  Also implemented: `suspended --discard <runId>`, `resume ... --answer <fichero.json>` and exit code 6
  (suspended run).
- Suspending inside `ParallelStep` branches remains a documented limitation of the framework.

## 11. Sessions: multi-turn conversation in the Result tab (contract added 2026-10-18)

Decided by the user: the run allows CONTINUING the interaction (like a chat), and the final answers accumulate in the
«Resultado» tab. Between one turn and the next, two things pass through: (1) the previous conversation, via
`conversationId` against the `Storage.Conversation` store, and (2) what the scripts store with `context.set` (the session's
«memoria» (memory)). The history is saved in the workspace, `sessions/<id>.json`, not in the browser.
The same `PipelineContext` is not reused between turns (fast-forwarding would serve the steps already done from the cache): each turn is a new run with a new
context, `conversationId` and memory metadata.

```ts
interface SessionSummaryDto { id: string; workflow: string; title: string; turns: number; updatedAt: string; }
interface SessionDto extends SessionSummaryDto {
  conversationId: string;               // p. ej. "sess-<id>"
  memory: Record<string, JsonValue>;    // claves de context.set arrastradas entre turnos
  createdAt: string;
  items: TurnDto[];
}
interface TurnDto {
  n: number; runId: string;             // runId del último intento (una reanudación lo sustituye; resumedFrom guarda el anterior)
  status: 'running' | 'completed' | 'failed' | 'cancelled' | 'suspended';
  input: JsonValue; output?: JsonValue; error?: string;
  startedAt: string; finishedAt?: string; durationMs?: number;
  promptTokens?: number; completionTokens?: number; costUsd?: number;
  suspension?: { stepId: string; marker: string; message?: string; dataToReview?: JsonValue; rejectionReason?: string };
  resumedFrom?: string;
}
```

| Method and route | Body → response |
|---|---|
| `GET /sessions?workflow=` | → `SessionSummaryDto[]` (most recent first) |
| `POST /sessions` | `{ workflow, title? }` → 201 `SessionDto` (new conversationId) |
| `GET /sessions/{id}` | → `SessionDto` |
| `DELETE /sessions/{id}?conversation=true\|false` | → 204 (deletes the history and, if requested, its conversation) |
| `POST /sessions/{id}/turns` | `{ input: JsonValue }` → 202 `{ n, runId }`; 409 if there is a turn in progress or an unresolved suspended one |
| `POST /runs/{runId}/resume` | if the run belongs to a session, it updates the turn (new `runId`, `resumedFrom`) |
| `DELETE /runs/suspended/{runId}` | if it belongs to a session, the turn becomes `cancelled` |

- The turn completes on the SERVER (closing the browser does not lose it): when it finishes, it saves `output`, errors,
  tokens and cost, and updates the session's memory and the conversation. Events are followed through the SSE of
  `/runs/{runId}/events`.
- `title` by default: the start of the first `input` (60 characters). Saved output and `input` are trimmed to a
  reasonable maximum per turn (with an indicator), documented.
- Session names `[A-Za-z0-9._-]+`; atomic write; a `sessions/` folder ignored by git in the example workspace.
- CLI: `session new <workflow>`, `session list`, `session show <id>`, `session turn <id> --input-json '…'`.
- Implemented (backend, 2026-10-18; detail in `core-api.md` §8): which keys form the memory (all those of `Metadata` except `__*`,
  `TokensUsed`, `CostUsd`, `null` and anything that is not JSON; limit 256 KiB), limits on `input`/`output`
  (32,000 characters), recovery after restart, and what is not met as stated: see «Sessions: backend» in
  Deviations.

## 12. Packages, portable encryption, library and templates (contract added 2026-10-18)

Current contract: `Samples/WorkflowDesignerWeb/PACKAGES-AND-TEMPLATES-SPEC.md` §4-§9 (zip format, RN-01..08,
working copy, REST API §9.2, CLI §9.3) and §5-§7 (encryption, library, templates). It replaces §2 (flat workspace) and the
`environment.json` of §10.1 (it becomes the package's `config.json`). The routes, DTOs and error codes of the specification are the
names used by backend, CLI and frontend; where an agent has to decide an unwritten detail, it notes it in
«Deviations» (its subsection) and in `core-api.md`. Build order: P1a Core → (P1b Web ∥ P1c CLI ∥ P1d Frontend) →
P2/P3 Core → (Web ∥ CLI ∥ Frontend) → P1e examples → E2E → P4.

## 13. Contract changes after the E2E verification (2026-10-18)

Details and the full list of findings are in `Samples/WorkflowDesignerWeb/E2E-FINDINGS.md`; these are the changes that affect contracts.

- **`WorkflowError.Code`** (Core): optional machine-readable code, serialized as `code` and omitted when null (`secrets_locked`,
  `wrong_master_key`). The CLI (`validate`) and the frontend decide on the code, never on the message text.
- **`PUT /api/library/templates/{n}`**: `schemas` and `prompts` are optional; when they are not sent the existing resources of the template
  are preserved. The frontend no longer sends them.
- **`default-role-unknown`** is only a warning when the configuration has no roles (a new package starts with `DefaultRole` and no roles).
- **`PackageStore.OpenFolder`** no longer creates `schemas/` or `prompts/` in a folder it does not own; `migrate --dry-run` leaves no empty folders.
- **Canvas**: `fitView` is capped at 100 % zoom; a step dropped or dragged at first level is moved below the lowest first-level step
  to avoid overlaps; at ≈760 px the run panel columns stack.


### 13.1 Automatic partial save to the zip (2026-10-18)

Reason (real bug): the user changed the API keys in Configuration, did not press «Save package», and session turns (which run the saved zip)
failed with 401 because the zip still had the old `${…}` variables.

- **`PackageStore.SaveEntries(session, entries, delete?, force)`** (Core): builds the new zip from the current zip on disk, replacing only the
  named entries with the working copy's bytes (an entry the working copy no longer has is removed; `delete` forces removals). Same
  guarantees as `Save`: atomic write and RN-01..05 validation (`PackageArchive.WriteFile`), determinism, 409 against `OriginSha256`
  (`force` skips it), 423 for a read-only session, `config.json` resealed to `enc:v2` with warnings, `manifest.updatedAt` refreshed (the
  manifest's other fields come from the saved zip). Nothing is written when the entries already equal the saved ones. Afterwards only those
  entries' hashes are moved in the session's saved fingerprint, so `HasChanges` still reports whatever was not written. `manifest.json` cannot
  be named.
- **Web**: `PUT config`, `PUT workflow` (`workflow.yaml` + `layout.json`), `PUT/DELETE schemas/{n}` and `PUT/DELETE prompts/{n}` write the
  working copy and then call `SaveEntries` for their own entries. Responses are unchanged (204, or 200 `{warnings, changed, savedToPackage}` for
  the configuration). If the zip changed from outside the working copy keeps the edit but the answer is the same 409 `conflict` as saving; 423
  for read-only. Not auto-saved (they stay «unsaved» until Save package): applying a library configuration and inserting a template, because
  they replace whole contents in one go and the user confirms them first.
- **Frontend**: `usePackages().persisted(op)` wraps those writes; on a 409 `conflict` it marks the package pending and opens the conflict
  dialog (the same one as Save package). A successful write no longer marks the package as changed (`noteChange` stays for apply-config and
  templates). `●` / «Sin guardar» / «los turnos usan lo guardado» now mean: canvas edits not yet sent, panel drafts not yet sent, or a write
  that failed (409). The canvas document is still sent with the existing flow (Save, Ctrl+S, export, save as): one PUT per save, never per
  keystroke. Ctrl+S remains the forced full save.
- **Replaces** the «Save» semantics of specification §4.3 / RF-02: the working copy no longer accumulates unsaved changes. What is left: a
  recovered orphan working copy only exists when something stayed pending (409, apply-config, template, or a crash between the working-copy
  write and the zip write); «close without saving / discard» drops that pending content; the 409 is now also raised by every edit, not only
  by Save package. Open question for the user: whether the canvas document should also autosave (debounced) and whether apply-config /
  template insertion should persist by themselves.

### 13.2 Time limits, the real error, provider types and model listing (2026-10-18)

Reason (real case): an `outliner` step (an `LlmStep` on a free OpenRouter model) failed at 60.036 s with «The operation was canceled.».
The framework applies `DefaultStepTimeout = 1 min` to every step (`AITaskAgent.Core/Configuration/TimeoutOptions.cs`), and the error neither said
that it was a timeout nor which limit: `RetryMiddleware` turned the cancellation into an error result before `TimeoutMiddleware` recognised it.

**1. `Timeouts` section of `config.json`** (PascalCase, whole seconds, 1..86400; edited in Configuration → «Tiempos»):

```json
"Timeouts": { "DefaultWorkflowTimeout": 1800, "DefaultStepTimeout": 300, "Steps": { "LlmStep": 600, "ScriptStep": 20 } }
```

- **Decision, defaults** (when the section or a field is absent; shown in the UI and published in `GET /api/catalog` → `timeouts`):
  workflow **30 min**, step **5 min**. The framework's own defaults (10 min and 1 min) cut any slow model call. A step type with no entry of
  its own uses the step default, so an `LlmStep` has 5 min unless configured.
- **Decision, how it reaches the framework.** `DefaultWorkflowTimeout` → `AITaskAgent:Timeouts:DefaultPipelineTimeout` and `DefaultStepTimeout` →
  `AITaskAgent:Timeouts:DefaultStepTimeout` (`EngineServices.BuildConfiguration`), so the framework's `Pipeline` enforces both. In addition
  `RunManager` owns the run's limit with its own `CancellationTokenSource` (the smaller of `DefaultWorkflowTimeout` and the host's
  `WorkflowDesigner:RunTimeoutSeconds`), started before the pipeline's: when it has fired the run fails with
  `The workflow exceeded its limit of N s (Timeouts.DefaultWorkflowTimeout)` and `errorType` `WorkflowTimeout` (the framework's own pipeline timeout
  only cancels the step in flight, which reports a bare cancellation, so the cause had to be known outside it). A user cancellation is still `cancelled`.
- **Decision, per type, without touching the framework.** After compiling, `TimeoutSettings.Apply` walks the compiled steps (nested ones through
  `ContainerDetection`, the same rule as the YAML factory) and sets `StepBase.Timeout` on the steps that have none: the `Steps[tag]` entry if
  there is one. A `timeout` written in the YAML always wins. **Container steps** (group, loop, parallel, switch) with no entry get the *workflow*
  limit: their time is the sum of their children's, so the 5 min step default would have cut a group of three LLM steps (a framework behaviour:
  `DefaultStepTimeout` also applies to containers).
- Validation: `EnvironmentSections.ValidateTimeouts` (422 on save with the path, e.g. `Timeouts.Steps.LlmStep must be a whole number of seconds
  between 1 and 86400.`). A tag that no registered step has is not an error on the server (the package may come from another build); the editor
  warns. The section is a designer section (`EnvironmentSections.Names`): it is not part of `AITaskAgent:LlmProviders`; applying a library
  configuration in Merge mode copies it only when the package has none.
- Other timers: `RequestTimeoutSeconds` (per attempt, 600 s by default for the OpenAI-compatible service) and `StreamingInactivityTimeoutSeconds`
  (120 s) of `Inference` (provider or role; the second one is new in the editor), MCP `TimeoutSeconds` (1-600, 30) and the step's own `timeoutMs`
  (`ScriptStep`, 5000) already existed and stay where they are; the tab lists them. Not offered: `TimeoutOptions.DefaultToolTimeout` (no code
  reads it) and `StreamingActivityTimeout` (obsolete, not read).

**2. Real error.**
- **Framework change (minimal, approved):** `AITaskAgent.Core/Execution/Middlewares/TimeoutMiddleware.cs`. When the step's timer fired and the
  caller's token was not cancelled, any error result that comes back (an `OperationCanceledException` wrapped by `RetryMiddleware`, or the plain message
  a step such as `ScriptStep` returns after catching the cancellation) is replaced by
  `ErrorStepResult.FromException(step, new TimeoutException("Step <name> exceeded its limit of <n> s"))`, and the `StepTimeoutEvent` is emitted
  (before, only an exception escaping `RetryMiddleware` produced it, which never happened). The framework already kept the exception type in
  `IStepResult.Error.OriginalException` (`IStepError`), so nothing else was needed to know the type; the test is `Tests/StepTimeoutReportTests.cs`
  (292 framework tests pass: the 289 plus 3).
- **`errorType`** (`Execution/ErrorTypes.cs`), derived from `Error.OriginalException` (and its inner exceptions): `Timeout`, `WorkflowTimeout`,
  `Cancelled`, `LlmHttp<status>` (`LlmHttp401`: from `HttpRequestException.StatusCode`, a `StatusCode` property, or the `HTTP 401: ...` text the
  OpenAI service writes), `LlmProvider`, `ToolIterationLimit`, `Validation` (an error without exception: validation of the output or input, or a
  plain error message) and `Exception:<TypeName>`.
- Where it is serialized: `data.errorType` of `step.completed` (only when it failed; `Exception` when the step threw past the middlewares and has no
  result), of `step.timeout` (`Timeout`) and of `run.failed`; `errorType` of `RunInfo` (`GET /runs/{id}`, the CLI's final JSON) and of a failed session
  turn (`TurnDto.errorType`). Absent when there is no error. The frontend shows it next to the message (Result tab, step table, session turn).
- The `step.timeout` message is now `<step>: exceeded its limit of <n> s`.

**3. Provider type as a drop-down.** `GET /api/catalog` gains `llm.serviceTypes: [{ name, listsModels }]` from `EngineServices.KnownLlmServices` (`openai`,
`openrouter`, `azure-openai`, `gemini`), not from the frontend. An empty `ServiceType` still means «the provider's name»; an unknown stored value is
shown as «<x> (desconocido)», kept, and warned about (the engine resolves a type by exact name or by a registered name that contains it, ignoring
case; the editor uses the same rule).

**4. Models by query.** `POST /api/packages/{id}/providers/{name}/models` (no body) → `{ models: [{ name, displayName? }], source: "api" | "cache" }`.
- `ProviderModelCatalog` (Core) reads the package's **saved** configuration, decrypts the key in memory (423 `secrets_locked` / 422 `wrong_master_key`
  exactly as a run), asks `GET {BaseUrl}/models` with `Authorization: Bearer` (`openai`, `openrouter`: OpenAI, OpenRouter, Groq...) or
  `GET {BaseUrl}/v1beta/models` with `x-goog-api-key` for `gemini` (models that support `generateContent`, up to 5 pages). `azure-openai` and unknown
  types: 422 `models_not_supported` (add them by hand). The key is never returned, logged or placed in an error; provider error bodies are not
  forwarded (only the HTTP status); redirects are not followed; each call has its own 15 s limit; answers are cached for 1 minute keyed by a hash of
  (service, URL, key). Errors: 422 `models_not_supported` / `models_no_key` / `models_bad_config` (undefined `${VAR}`, BaseUrl that is not http(s)),
  502 `models_unauthorized` (provider 401/403) / `models_unreachable` (network, timeout) / `models_failed`, 404 for a provider that is not in the saved
  configuration. Trusted packages only (`PackageTrust`, like `/tools`): the key, or the variable a `${VAR}` names, is sent to the provider's BaseUrl.
- Frontend: «Actualizar modelos» in the provider editor (disabled while the configuration has unsaved changes, because the server uses the saved key)
  merges the answer into `Models` without losing anything (existing rows with their prices, models written by hand, and models that a role or
  fallback of that provider uses, so a valid configuration does not become invalid: the engine requires a role's model to be in the list). After
  saving, a provider that is new or whose service, BaseUrl or key changed, that declares no models and whose service lists them, is queried and
  saved again automatically; a failure is a warning and manual entry still works. In Roles (and fallbacks) the model is chosen with a filterable
  combobox (`shared/ui/Combobox`, keyboard: arrows, Home/End, Enter, Esc) from the provider's list; a stored value that is not in the list is kept
  and flagged (the save validation still refuses it, as the engine does); a provider with no declared models keeps the free-text field.

### 13.3 The `timeout` of a step is a duration in the YAML and whole seconds in the document (2026-10-18)

Reason (real case): `advanced-story` had `timeout: 3200` on its `outliner` step and failed with «Step 'outliner' (type 'LlmStep') (line 16, column 11)
could not be created: Could not cast or convert from System.Int64 to System.TimeSpan». A step's `timeout` is `StepBase.Timeout` (`TimeSpan?`); the YAML v2
factory converts through JSON and a bare integer does not convert. The catalog published `timeout` as an untyped field (`"timeout": {}`), so the
inspector let any number through without a warning.

- **Decision, units.** In the designer the step's `timeout` is **whole seconds** (an integer ≥ 1; what the user means by a plain number). In the document
  (`StepNode.Properties["timeout"]`) it is that integer; in the YAML it is the duration the framework converts, the invariant `TimeSpan.ToString("c")`
  (`00:53:20`, `1.00:00:00` from 24 h). The conversion is `Documents/StepTimeout` (`TryGetSeconds`, `Format`, `TryParseDuration`) and happens in
  `YamlEmitter` (seconds → duration) and `YamlReader` (duration → seconds), for every step type, containers included. Round trip of the YAML is kept.
- **Catalog.** `StepCatalog` replaces the untyped `timeout` of every step type with `{ "type": "integer", "minimum": 1, "description": "Límite de tiempo del
  step, en segundos (entero, mínimo 1). Vacío: usa el de la pestaña «Tiempos».", "x-editor": "seconds" }`. The existing number field of the inspector
  renders it (integer, ≥ 1, empty removes the property, which then takes the limit of the «Tiempos» tab); no new control was needed.
- **Reading.** `YamlReader` accepts a duration (`hh:mm:ss`, `d.hh:mm:ss`, plain or quoted; no fractions of a second, no zero or negative) and an
  **integer**, which is what the designer itself wrote before this change and is read as **seconds**. The integer case adds a warning to
  `WorkflowReadResult.Warnings` («timeout numérico interpretado como segundos; se reescribirá como duración») and `WorkflowReadResult.Yaml` is the read text
  with that scalar rewritten **in place** (`timeout: 3200` → `timeout: 00:53:20`; no line moves, so line maps and error positions stay valid). Anything else
  (`soon`, `0`, `-5`, `1.5`, a quoted `"3200"`, a list) is kept as read: the reader does not refuse the package, the validator reports it.
- **Validation.** `WorkflowCompiler.PreCheck` reports a `timeout` that is not a whole number of seconds ≥ 1 as an error of the step, on the line of its `timeout`
  key, **before** the factory builds the step (so the framework's «could not be created» never appears for it). The warning of a numeric timeout reaches
  `ValidationResult.Warnings` by the YAML paths (`ValidateYamlAsync`, `ValidatePackageAsync`: CLI `validate`) and, for the editor (which validates the
  document, not the file), `ValidateAsync(package, document)` re-reads the package's saved `workflow.yaml` and adds the same warning for the steps the
  document still has: it stands until the workflow is saved, which writes the duration. Opening a package with `timeout: 3200` and saving it fixes it.
- **Execution.** The engine always receives the duration: a run of a document emits it; a run of a package's own `workflow.yaml` (CLI `run x.zip`,
  `source: saved`, resume) compiles `WorkflowReadResult.Yaml`. The warning is also the first `log` event (level `warning`, `data.source` `workflow`) of the
  run. A package with `timeout: 3200` therefore opens, validates with the warning and runs with a limit of 3200 s. The suspension snapshot stores the
  rewritten text.
- **Interaction with `Timeouts` by type.** None: `TimeoutSettings.Apply` only sets `StepBase.Timeout` on steps whose value is still null, so the step's own
  `timeout` wins over `Timeouts.Steps[tag]` and over the container default (test `The_timeout_of_the_step_wins_over_the_limit_of_its_type`). The workflow limit
  still bounds the whole run.
- **Not done.** The column of a `timeout` error is not reported (the line map keeps lines only). The web editor shows the legacy warning only when it
  validates a package whose saved file still has the number; a document pasted as YAML into the editor is read by the same reader but its warning is not shown.
  The user's `workspace/workflows/advanced-story.zip` was not touched.

### 13.4 `ExecStep` in the designer: category `system`, trust to run, policy and CLI notices (2026-10-19)

The framework has the step `ExecStep` (`AITaskAgent.Core/Steps/ExecStep.cs`, `docs/guides/yaml-pipelines.md`): it runs an external `.exe`/`.bat`/`.cmd` (not a
shell) and its result is the text of stdout. A package that carries one runs a program with the user of the backend or the CLI, so it is treated like the MCP
`Stdio` servers: shown, confirmed once, and checked again when it changes. The framework was not touched; everything below is in `Samples/`.

- **Catalog.** `StepCategories.System = "system"`. `StepCatalog.CategoryOf` has a rule **per type** before the one per assembly (`SystemSteps`, today `ExecStep`;
  a future step of that kind goes in that set, and in `ExecCommands` if a package must be trusted to run it). The designer adds to the schema what the framework's
  does not say: `inheritEnvironment` as `enum: ["minimal", "all"]` with `default: "minimal"`, `args` as `array` of `string` and `okExitCodes` as `array` of
  `integer` (the framework publishes arrays with no type), and `x-literal: true` on `executable` (it takes no templates). `env` already arrives as `object` with
  `additionalProperties: { type: string }`.
- **Inspector.** `schemaToFields` adds two kinds: `intList` (a list of integers, committed only when every item is an integer) and `stringMap` (any `object` whose
  `additionalProperties` is a string and has no properties of its own: name/value rows, a repeated or empty name is an error and does not reach the document). Both
  are reusable by any step. A field with `x-literal` shows a permanent note («no admite plantillas») and a stronger alert when the value contains `{{`. The step's
  description is already in the header. The palette and the canvas know `system` (label «Sistema», token `--cat-system`, a gear mark in the palette); a category the
  frontend does not know still falls into «Otros».
- **Trust.** `PackageTrust` now reads the **effective workflow** too (`WorkflowDocument.AllSteps()` through `ExecCommands.Find`, which reaches nested steps and the
  ones inserted from templates). With one `ExecStep` the package is `required`, so a run (working copy, saved zip, session turn) is a 403 `untrusted_package` until
  confirmed; **validating does not need trust** unless it also names tools (that gate is the old one and still uses the whole `trusted`). `TrustDto.exec` is a list of
  `ExecCommand`: `stepId`, `executable` (literal), `args[{ text, dynamic }]` (`dynamic` when the text has `{{`), `dynamicArgumentCount`, `workingDirectory?` and
  `workingDirectoryDynamic`, `envNames` (sorted; **never the values**) and `inheritsAllEnvironment`. `stdin` is not exposed. It is absent when there is none.
- **Hash.** Without commands the hash is exactly the old one (stored `trust.json` files stay valid). With commands it also covers, per command and in a stable order
  (moving a step changes nothing), executable, the text of each argument, the working directory, the env **names** and the inheritance; not env values, not stdin.
  A changed command, a new or renamed variable, another template, or `inheritEnvironment: all` ask again; a changed env value does not.
- **Edits by the user keep the trust, as with the config.** `PUT /packages/{id}/workflow` (and `PUT …/config`, `save-as`) re-records the hash after the write **only if the
  package was trusted before it**: an imported package stays untrusted however it is edited. Consequence worth knowing: a package that was trusted because it had
  nothing to confirm (created here, or imported without servers, folders or commands) and gets an `ExecStep` through the editor is trusted at once; that is the user's
  own act, like adding an MCP server today.
- **A run that carries a document is judged by that document.** The editor sends the workflow on screen (`document`) when it runs, saved or not, so
  `PackageTrust.ForRun(id, source, document)` takes the commands from it, not from the working copy. `POST /packages/{id}/trust` accepts `{ hash?, document? }`
  for the same reason: the frontend sends the editor's document, the hash is compared with the one computed for it, and a hash computed for something else is a 409
  `trust_stale` carrying the current `trust` (the dialog then shows the commands that were not on the list the user saw).
  Known edge: after confirming an unsaved document, `GET …/trust` describes the working copy, which may differ until the workflow is saved; running again with
  that document needs nothing further.
- **Policy (defense in depth).** `Engine/DesignerExecPolicy` (internal) is the `IExecStepPolicy` every run registers, **after** `ConfigureEngineServices` so a host
  cannot replace it with a permissive one. How trust reaches it without Core knowing Web: `RunRequest.ExecTrusted` (`[JsonIgnore]`, **false by default**, so a
  client cannot set it from the body), plus an optional `execTrusted` on `RunManager.ResumeAsync` and `SessionManager.StartTurnAsync`. The Web host sets it after its
  403 check (a resume: only while the package is trusted now); the CLI always sets it (whoever launches decides). Without it every `ExecStep` fails with
  `ExecStepException` («the package is not trusted for execution in this run»). Confinement, minimum and by default: the working directory must be inside
  `FileTools.RootDirectory` (empty = the process's working directory) or one of `AdditionalAllowedDirectories`; with `FileTools.Enabled = false` only the process's
  directory remains; a step with no `workingDirectory` runs in the root; a relative one is taken against it. Outside is denied (also `..`, and a sibling that only
  shares the prefix). It checks the declared path: links and junctions are not resolved, and the command itself can still reach whatever its user can: it is not a
  sandbox. A package that declares no folders is therefore confined to the backend's working directory, which is what an empty `RootDirectory` means for FileTools.
- **CLI.** `package info` prints one `exec:` line per command (step, executable, arguments with how many are dynamic, working directory, env names, inheritance;
  `exec` array in `--json`) and the same warnings. `run` and `validate` print one stderr line per command, without secrets and without asking:
  `warning: this package runs the external command 'x' (step 'go': 3 argument(s), 1 dynamic; sets A, B; inherits the whole environment)`.
- **Result tab.** The error of a failed command is shown as it comes (`errorType` `Exception:ExecStepException`): the message already carries the exit code and
  the last 2000 characters of stderr, and the `<pre>` keeps the line breaks.
- **Not done.** No sandbox (a command can read secrets in the files its user can read, and `inheritEnvironment: all` gives it the backend's environment, keys
  included; the dialog says so). The trust gate of the tools listing and of validation with tools still uses the whole package's trust, so an untrusted package with
  commands and tools asks before listing tools even though the commands would not run. Env values are shown in the inspector as plain text.

### 13.5 The workspace lives outside the repository and can be opened from the application (2026-10-19)

**Why.** The development workspace was `Samples/WorkflowDesignerWeb/workspace`, inside the repository, and the user's encrypted keys (`enc:v2`) ended up in a versioned zip.
Decision: a user's workspace, which may hold keys, is never inside the repository, and the application gets an option to open a workspace, so nothing travels to the
repository by mistake. The example workspaces without keys are added to the repository by hand, when pushing. Only `Samples/` changed; the framework was not touched.

- **Location and settings.** The default workspace is `%USERPROFILE%\WorkflowDesigner\workspaces\default` (`~/WorkflowDesigner/workspaces/default` on Linux and macOS). The user
  settings are `%LOCALAPPDATA%\WorkflowDesigner\settings.json` (`~/.local/share/WorkflowDesigner/settings.json`) with `{ "lastWorkspace": "...", "recent": ["..."] }` (at most 10, the
  most recent first, no duplicates; read tolerantly, written atomically; a relative path in the file is ignored). `Workspace/UserSettings.cs` (`UserSettingsStore`) and
  `Workspace/WorkspaceLocations.cs` hold this; both take every path as a parameter, so no test reads or writes the real profile.
- **Order at start-up.** The explicit option `WorkflowDesigner:Workspace` (command line, environment variable or an appsettings the user writes; relative = against the content root) >
  `lastWorkspace` (only if that folder still exists) > the default location. The folder that results is created and seeded with `SampleWorkspace` (no keys: `${OPENAI_API_KEY}`) when it
  has no files, as before. `appsettings.Development.json` no longer names a workspace. Two keys exist for hosts and tests: `WorkflowDesigner:SettingsFile` and
  `WorkflowDesigner:DefaultWorkspace`. An explicit workspace is **not** recorded in the settings (a one-off override must not change what the next plain start opens); one that comes
  from `lastWorkspace` or the default is recorded at start, and every workspace opened from the application is.
- **`WorkflowDesigner:DataDirectory`** belongs to the workspace of start-up: a workspace opened later keeps `<workspace>/data` (a shared data folder would mix the sessions and
  suspended runs of two workspaces). Opening the start-up workspace again brings the setting back.
- **A replaceable container (the design decision).** Everything bound to the workspace root (`PackageStore`, `LibraryStore`, `RunManager`, `SessionManager`, `WorkspaceMigration`, the catalog,
  validator, `ToolCatalog`, `SecretsManager`, trust) lives in a **child container** owned by `WorkspaceHost` (Core). `OpenAsync(path)` builds the container of the new workspace first
  (nothing changes if that fails), then swaps it and disposes the old one (which releases its package locks and cancels nothing: callers check first). What is not bound to a root is
  created once and shared by all containers: the secret protector and the `MasterKeyProvider`, so **the master password that was unlocked stays unlocked** (the verifiers belong to
  each package and library; a workspace whose packages were sealed with another password simply reports the usual 422 `wrong_master_key` / locked keys).
  The options in use are cloned (`WorkflowDesignerOptions.CloneFor`), so what a host changed in them (`ConfigureEngineServices`...) carries over.
  The CLI and the Core tests do not switch and keep `AddWorkflowDesigner` as it was.
- **How the web resolves services per request.** The endpoints are not touched: they keep asking for `PackageStore`, `RunManager`... as parameters. Those types are registered in the backend's
  container as transients that read the host's current container (for start-up code and tests), and a middleware (`UseWorkspaceServices`) gives each request a thin
  `IServiceProvider` that answers the bound types from the host **at that moment** and everything else from the request scope. This is deliberate: resolving them through the request
  scope would dispose a store or a run manager at the end of the request. The list of bound types is one array (`WorkspaceServices.Bound`); a new service that depends on the root must be
  added there.
- **API.** `GET /api/workspace` -> `{ path, name, isDefault, isSample, source: explicit|last|default|opened, gitRepository?, dataDirectory, defaultPath, recent: [{ path, name, exists, current }] }`.
  `isSample` is true while the workspace holds only the seeded packages. `gitRepository` is the folder with a `.git` (directory or file) at or above the workspace, else absent.
  `POST /api/workspace/open { path, create?, discard? }` -> the same body. Same guards as the rest: loopback only and `CrossSiteGuard`.
  - **Validation (400, `code`).** `invalid_path` (empty, not absolute, device path, drive root, the profile folder or other well-known user folders, a system folder, a file, missing folder, missing
    parent when creating), `nested_workspace` (inside the open workspace or around it; the same folder is a no-op), `not_a_workspace` (opening a folder without `workflows`, `library` or `data`),
    `not_empty` (creating in a folder with entries). `create` needs a new or empty folder whose parent exists: **nothing is written outside the new workspace**, and it is seeded with the examples.
  - **Conflicts (409).** `runs_active` while a run is starting or executing (suspended runs wait on disk and do not count); `unsaved_changes` with `packages: [{ id, name }]` when an edit
    session has changes that are not in the zip (the working copy autosaves each edit, so this is what a failed save or a conflict left pending; the canvas and drafts are the client's).
    `discard: true` confirms. On success the edit sessions of the old workspace are closed (`discard`: the working copies are deleted), the new container replaces the old and the
    workspace is recorded. If building the new workspace fails after the sessions were closed, the old workspace stays open.
  - One switch at a time. A request that was already running against the old container when the switch happens is not interrupted (single-user local tool; the check for runs is the guard).
  - The migration of the flat format and its banner work per workspace: the frontend asks again after a switch.
- **Frontend.** The header has a «Workspace» control: name and abbreviated path (full path in the tooltip, a warning mark inside a repository) that opens the «Abrir workspace» dialog: a path
  field (the browser cannot browse the file system; the server validates), the recent list (click to open; the open one and those that no longer exist are disabled), «Abrir» and
  «Crear aquí», and a permanent alert when the workspace is inside a git repository («este workspace está dentro de un repositorio: las claves cifradas podrían subirse»).
  `WorkspaceProvider` (just under `ApiProvider`) holds the info and re-mounts everything below it with a new `key` after a switch, so packages, runs, sessions, trust and the route start
  clean (the URL goes back to `#/designer`). Unsent changes on screen are confirmed before calling the server, and a 409 `unsaved_changes` lists the packages and asks again with `discard`.
  The mock implements the same routes, validations and 409s (`api/mockWorkspace.ts`).
- **Not done.** The seed always brings the three examples (a fully empty new workspace is not offered). No folder browser. A workspace opened from the repository's `Samples/WorkflowDesignerWeb/workspace`
  is allowed (the dialog warns that it is inside a repository). The CLI does not read the user settings: it always takes `--workspace`.

## 14. Step hooks, validators and result validators (contract added 2026-10-19, phase A)

Built in the framework (not in the designer). Phase B (designer integration) consumes what is described under «Designer contract». Study: `STUDY-CODEBASE-ANALYSIS-IN-DESIGNER.md`, study F (design) and «Objections» (what was not built as studied).

### 14.1 Types

| Type | Project / namespace | What it is |
|---|---|---|
| `IHookRegistry`, `HookRegistry` (default, `Add(name, kind, delegate, description?, source?)`), `HookInfo`, `HookKind` (`Hook`, `StepValidator`, `ResultValidator`) | `AITaskAgent.Abstractions` / `AITaskAgent.Core.Hooks` | Named delegates a YAML step references. A name is unique across kinds and sources: registering it twice throws. `TryGet(name, kind, delegateType, out d)` rebinds with delegate variance; `List()` feeds diagnostics and the designer. Registered as singleton (`HookRegistry` and `IHookRegistry`) by `AddAITaskAgentYaml()`. |
| `IResultValidator`, `StepHookException` | `AITaskAgent.Abstractions` / `AITaskAgent.Core.Hooks` | Validator of the response itself; wrapper the retry middleware recognises. |
| `StepResult.ResultValidators` / `AttachResultValidators(...)` | `AITaskAgent.Abstractions` | Validators stamped on a result by its step. `StepResult.ValidateAsync` (virtual) runs them after the result's own check. Never serialized. |
| `StepHooks` (record), argument records (`PostProcessArgs`, `FinalizeArgs`, `EnrichActivityArgs`, `EnrichStartedArgs`, `EnrichCompletedArgs`, `ValidateArgs`, `ResultValidateArgs`), `HookAround<TArgs,T>`, `HookVirtualAttribute`, `HooksTypeAttribute`, `HookSlots`, `HookSlot`, `HookDispatch`, `DelegateValidator`, `DelegateResultValidator`, `SchemaResultValidator`, `ResponseSchemaBuilder` | `AITaskAgent.Core` / `AITaskAgent.Core.Hooks` (`ResponseSchemaBuilder` in `AITaskAgent.Core.Execution`) | Hook slots and their dispatch. They live in Core because the args carry `StepBase`. |
| `LlmStepHooks : StepHooks` + args (`BuildMessageArgs`, `ConfigureRequestArgs`, `ParseResponseArgs`, `SelectConversationArgs` (2026-10-24), `ConversationCreatedArgs`, `ToolLimitArgs`, `ToolLimitReason`, `ToolLimitDecision`) | `AITaskAgent.Extensions.Llm` / `AITaskAgent.LLM.Steps` | LLM-only slots. `BaseLlmStep` declares `[HooksType(typeof(LlmStepHooks))]`; `YamlLlmStep` inherits it. |
| `IHookOutcome<T>`, `HookForms` (`After`, `AfterTypeOf`, `AfterAsAround`), `IHookData<TSelf>`, `HookDataMember` / `HookDataMember<TArgs>`, `HookDataProperty`, `HookData` (`MembersOf`, `Read`, `Write`, `ToJson`, `ToText`, `WithValue`, `Convert`), `IHookFactory`; `HookSlot.AfterType` / `AcceptedTypes` / `ArgsType`, `HookSlots.Bound` | `AITaskAgent.Core` / `AITaskAgent.Core.Hooks` | Added 2026-10-23: the after form of the around slots (§14.6) and the data view a hook in another language receives (§18.8). |

`StepBase` gains `Hooks` (`StepHooks?`, `[JsonIgnore]`), `ResultValidators` (getter only), `AddResultValidator`, and the virtual `PostProcessAsync`. `Enrichable` is `[JsonIgnore]` now (the `enrichable` YAML key never worked and disappears from the catalog). `StepResultFactory.BuildJsonSchema` stays internal; its public face is `ResponseSchemaBuilder.Build(Type)` (strict `additionalProperties: false`, `[Description]`, validation attributes, defaults, `required`, `[ExcludeFromLlmSchema]`; no `$ref`, recursive types are cut silently).

### 14.2 YAML keys

Three structural keys, valid on every step that derives from `StepBase` (rejected with a message on any other step type), resolved by `YamlPipelineFactory` after `inputSchema`/`outputSchema` and before `dependsOn`:

```yaml
- id: Auditor_Errores
  type: LlmStep
  outputSchema: Finding
  validators: [Findings.NoDuplicates]          # semantic: IStepValidator (registry kind StepValidator, or an IStepValidator service by Id)
  resultValidators: ["schema:Finding", Findings.Check]   # the response's own contract (registry kind ResultValidator, IResultValidator service, or the built-in schema:<registered schema>)
  hooks:                                       # slot -> registered hook name
    postProcess: Reports.AttachProvenance
    onFinalize: Notes.End
    onConversationCreated: Notes.Begin         # LLM-only slot
    onToolLimit: Auditor.FinalAnswer           # LLM-only slot
```

Errors are build errors that name the step, the key and (when useful) the available names: unknown name, name of another kind («'X' is a step validator, not a hook»), wrong signature (both signatures are shown), unknown slot (valid slots are listed; LLM slots are not valid on a non-LLM step), the same validator name in the registry and in DI («defined more than once»), unknown or unregistered `schema:` name. A `resultValidators: ["schema:X"]` that repeats the step's own `outputSchema: X` logs a warning (redundant).

### 14.3 Slots

Delegate types are exact (a compatible delegate is rebound with variance). `Signature` is the text shown in the designer.

| Slot | Delegate | Dispatch | Notes |
|---|---|---|---|
| `postProcess` | `Func<PostProcessArgs, Task>` (after form, writable `args.Result`) or `HookAround<PostProcessArgs, IStepResult>` | virtual `StepBase.PostProcessAsync`, called from the explicit `IStep.ExecuteAsync` after `ExecuteAsync` | per attempt, only for results without error, before validation; the step's own value is the unchanged result (`args.Result` / `next()`); a replaced result must be assignable to the output type (otherwise `StepHookException`, fatal) |
| `onFinalize` | `Func<FinalizeArgs, Task>` | notification, explicit `IStep.FinalizeAsync` (after the virtual) | once, final result (success or error); an exception ends the invocation as an error result carrying a `StepHookException` |
| `enrichActivityBefore`, `enrichActivityAfter` | `Action<EnrichActivityArgs>` | virtual `EnrichActivityBefore/After` (after `Enrichable`) | exceptions are logged and swallowed; JavaScript since 2026-10-24 (§18.8, synchronous slots) |
| `enrichStarted` | `Func<EnrichStartedArgs, StepStartedEvent>` | virtual `EnrichStartedEvent` | exceptions swallowed, the base event is used; JavaScript since 2026-10-24 |
| `enrichCompleted` | `Func<EnrichCompletedArgs, IStepCompletedEvent>` | virtual `EnrichCompletedEvent` | same |
| `buildUserMessage`, `buildSystemMessage` (LLM) | `Func<BuildMessageArgs, Task>` (after form, writable `args.Message`) or `HookAround<BuildMessageArgs, string?>` | virtual `BuildUserMessageAsync/BuildSystemMessageAsync`; `YamlLlmStep` feeds its own rendering to the same dispatcher (`DispatchUserMessageHook/DispatchSystemMessageHook`) | the step's own message (`Prompt`, or the rendered `userPrompt`/`systemPrompt`) is `args.Message` / `next()`; once per invocation round (retries reuse it) |
| `configureRequest` (LLM) | `Func<ConfigureRequestArgs, LlmRequest>` | virtual `ConfigureLlmRequest` | before the JSON response format is applied; JavaScript since 2026-10-24 |
| `parseResponse` (LLM) | `Func<ParseResponseArgs, Task>` (after form, writable `args.Result` and `args.Error`) or `HookAround<ParseResponseArgs, (IStepResult? Result, string? Error)>` | virtual `ParseLlmResponseAsync` | the built-in parsing is `args.Result`/`args.Error` / `next()`; an `Error` (or a null result) retries the step with it as feedback |
| `selectConversation` (LLM, 2026-10-24) | `Func<SelectConversationArgs, Task>` (writable `args.UseHistory`) | decision at the call site in `ExecuteAsync` (private `SelectConversationAsync`), first attempt of each round, right after the virtual `GetConversationContext` | the virtual's choice is the default (`UseHistory` = it returned the context's conversation: `true` for `BaseLlmStep`, `UseConversationHistory` for `YamlLlmStep`, `false` for `StatelessTemplateLlmStep`); `true` = the context's conversation, `false` = a fresh `ConversationContext(history.MaxTokens, history.TokenCounter)`; unchanged = the virtual's own object; before the round's bookmark and `onConversationCreated`; must be idempotent (runs again on a fallback round); an exception is a `StepHookException` (fatal, no retry, no fallback, no model call) |
| `onConversationCreated` (LLM) | `Func<ConversationCreatedArgs, Task>` | notification at the call site (first attempt of each round) | must be idempotent (also fires again after a provider fallback); the only C#-only slot (§18.8) |
| `onToolLimit` (LLM) | `Func<ToolLimitArgs, Task<ToolLimitDecision>>` | decision at the call site in `ExecuteAsync`, right after the tool loop | see 14.4 |

Validators are not slots: `validators` fill the existing `StepBase.Validate` list (`DelegateValidator` wraps a registry delegate; first failure short-circuits; failure = `Semantic` validation event + retry of the whole step), `resultValidators` fill `StepBase.ResultValidators` (stamped on every result, executed by `StepResult.ValidateAsync` in the structural slot: `Structural` validation event + retry). Result types that override `ValidateAsync` must call `base` after their own check; the framework's `JsonStepResult`, `LlmJsonStepResult` and `LlmStringStepResult` do (`LlmIntentionResult` already did). An exception thrown by a validator is wrapped in `StepHookException` and ends the step as a fatal error (no retry) after finalizing.

### 14.4 Precedence rules

1. **A hook is the default implementation of its virtual method**, one layer above the built-in behaviour. The most derived override runs first; an override that does not call `base` replaces the hook too (inheritance wins over the instance delegate).
2. **R1.** A framework step that overrides a hookable virtual must call `base` or the dispatcher the base uses (`YamlLlmStep.BuildUserMessageAsync/BuildSystemMessageAsync` call the dispatcher with their own rendering because the base default, `Prompt`, is not their behaviour). `HookReflectionTests` scans the framework assemblies and fails on a new override that does neither.
3. **R2.** Notification hooks (`onFinalize`, `onConversationCreated`) and the call-site decisions (`onToolLimit`, `selectConversation`) fire from the call site and cannot be bypassed by an override. `selectConversation` is deliberately **not** dispatched inside the virtual `GetConversationContext` (the general rule of §14.6): three steps override that virtual without calling `base` (`YamlLlmStep`, `StatelessTemplateLlmStep`, and the sample's `CustomStatelessTemplateLlmStep`), so a dispatch inside it would be skipped by each of them; it runs right after the virtual, which keeps the rule's meaning (the step's own value is already computed and is the default) for every subclass. Known edge: `StatelessTemplateLlmStep` saves its own conversation for a suspension (`SaveStateAsync`); a hook that switches it to the shared history does not change what it saves.
4. **R3.** At build time the binder logs a warning when the step's most derived declaration of a hook's virtual is an override outside the framework assemblies («runs only if … calls base»). Whether it calls base cannot be known without IL, so it is a warning, not an error.
5. **`onToolLimit`.** When the tool loop ends without an answer (`ToolLimitReason.IterationLimit`: `ToolIterationLimitExceededException`; `LoopCut`: repeat detector or `ToolLoopStopRequestedException`), the hook returns `ToolLimitDecision.Fail` (today's behaviour) or `FinalAnswer(prompt)`: the prompt is added as a user message and the model is called once more, in the same attempt, with `tool_choice: none` (tool definitions stay). The invocation's bookmark and `state.Message` are untouched, so `FinalizeAsync` rewinds the tool traffic and the final prompt and writes the original turn's `[user, assistant]` pair, also in a stateful step (shared conversation). The usage of the cut turn is added to the final one. A model that still asks for tools in that turn has the calls dropped. `SwitchToNextFallback` is not involved and unchanged. Without the hook nothing changes. There is no `ToolLimitFinalAnswerPrompt` property.
6. Hooks are immutable (`StepHooks` is a record replaced as a whole); delegates must be re-entrant and keep state in the `PipelineContext`, because one step instance serves concurrent runs and parallel branches.

### 14.5 Designer contract (for phase B)

- `StepTypeDto.hookSlots` (additive, `null`-safe): `[{ "name": "postProcess", "signature": "Func<PostProcessArgs, Task>", "description": "...", "signatures": ["Func<PostProcessArgs, Task>", "HookAround<PostProcessArgs, IStepResult>"], "languages": ["csharp", "javascript"], "help": "hook.postProcess", … }]` (since 2026-10-23 `signature` is the form the designer writes, the after form of an around slot; every new field is listed in §14.7), built by `HookSlots.For(type)`; empty for a step that does not derive from `StepBase`. `StepTypeDto.structural` now lists `hooks`, `validators`, `resultValidators` for every `StepBase` step; `StepCatalog.StructuralKeys` includes them.
- `GET /api/hooks` (to build in phase B, like `GET /api/tools`): `{ "hooks": [{ "name", "kind": "hook"|"stepValidator"|"resultValidator", "signature", "description", "source" }] }` from `IHookRegistry.List()` (+ `IStepValidator` services as `stepValidator`, source `di`), sorted by name. Result validators `schema:<name>` for every schema of the package are offered by the designer itself.
- The document model must keep `hooks` (map slot → name), `validators` and `resultValidators` (lists of names) through the YAML reader/emitter round trip, validate names against the registry (the compiler already does, through `YamlPipelineFactory`), and warn that a validator on `ExecStep`/`ScriptStep` re-runs the whole step per attempt (recommend `maxRetries: 1`).

### 14.6 The after form of the around slots (2026-10-23, contract)

Decided by the user (do not reopen; memory `project_hooks_no_next_2026_10_23`): a hook runs **at the end of the concrete virtual method**, with the method's
parameters and the step's own value already computed; whoever inherits calls `base.Method()` where it needs to, knowing the hook runs there (not exposed in the
designer). In the designer a hook has **no `next`**: it receives `args` with a writable member for the outcome and returns nothing.

- **Slots concerned** (the `HookAround<TArgs, T>` properties of the framework, verified by `HookAfterFormTests.The_around_slots_expose_their_after_form…`):
  `postProcess` (`args.Result`), `buildUserMessage` and `buildSystemMessage` (`args.Message`), `parseResponse` (`args.Result`, `args.Error`). The other slots
  were never around slots and keep their delegate.
- **Contract (additive; nothing existing changes):** `IHookOutcome<T> { T Outcome { get; set; } }`, implemented (explicitly) by the four argument records;
  `PostProcessArgs.Result` gains a public setter (it was `init`); `BuildMessageArgs.Message`, `ParseResponseArgs.Result` and `ParseResponseArgs.Error` are new
  settable properties (null in an around hook). `HookForms.After<TArgs, T>(Func<TArgs, Task>)` turns an after-form hook into the slot's `HookAround`:
  `args.Outcome = await next(); await hook(args); return args.Outcome;`. `HookSlot.AfterType` (`Func<TArgs, Task>` when the slot's `TArgs` is
  `IHookOutcome<T>`), `HookSlot.AcceptedTypes` and `HookSlot.Signature` (now the after form's text when there is one).
- **Binding.** `HookBinder` takes a registered hook of **either** form, by signature: the property's `HookAround`, or `Func<TArgs, Task>` adapted with
  `HookForms.AfterAsAround`. The after form must return exactly `Task` (delegate variance would also let a `Task<T>` method through and its value would be
  ignored: it is refused). A name of neither form is a build error that names both: «the signature of 'X' is …, but slot 'postProcess' expects
  Func<PostProcessArgs, Task> or HookAround<PostProcessArgs, IStepResult>». `HookAround` stays supported (CodebaseAnalysis's `ReportPostProcess` and every
  existing package hook keep working; the R1/R3 rules and `HookReflectionTests` are unchanged because the dispatch did not move).
- **Semantics kept.** The virtual methods still dispatch through `HookDispatch.AroundAsync` with their built-in behaviour as `next`, so the order (postProcess
  once per successful attempt, before result validators and validators; build messages once per invocation round; parseResponse per response) and the exception
  rules (a hook exception is a `StepHookException`, fatal, no retry, no provider fallback; `parseResponse` with `Error` or a null result retries) are the same for
  both forms. A replaced `postProcess` result is still checked against the output type. Tests: `Tests/HookAfterFormTests.cs` (both forms, exceptions, retries,
  type check, R3 with an after-form hook, LLM slots), `StepHooksTests`/`LlmHooksTests` (around form, unchanged).

### 14.7 Hooks and validators in the designer (2026-10-23)

- **Stubs.** C#: `Task Name(TArgs args)` with `return Task.CompletedTask;` for an around slot (no `next`); a validator stub (C# `[Validator]`/`[ResultValidator]`
  method from `GET /api/code/snippets`, or a JavaScript `exports.<fn> = function (args) { return true; }`) is offered by «Crear stub» in the `validators` /
  `resultValidators` list dialog. JavaScript: `exports.<fn> = function (args) { }` added to a package library (`scripts/<lib>.js`, an existing one or a new one)
  and bound as `js:<lib>.<fn>` (§18.8).
- **The comment of every stub is only the structure of `args`** (one single source, `Samples/WorkflowDesigner.Core/Engine/HookDocs.cs`): C# from reflection
  over the arguments record (`// args: PostProcessArgs` then `//   Result: IStepResult — lectura/escritura`, one line per public member, methods as
  `As<T>(): T? — método`); JavaScript from the framework's data view (`HookData.MembersOf`) plus `vars` (`//   result: JSON — lectura/escritura`, object members
  inline `{ name: string }`). `HookDocsTests` checks both against the types by reflection. `JsValidatorDocs` is now a thin wrapper over it.
- **`HookSlotDto` additions** (all additive, after `header`): `signatures` (every form the engine binds), `languages` (`["csharp"]` or
  `["csharp","javascript"]`), `jsUnsupported` (why a slot is C#-only), `jsHeader`, `jsMembers` (`JsMemberDto` list, the completion of `args.`), `help`
  (topic). `CodeSnippetDto.help`. `CSharpCompletionItem.Documentation` / `.Container` (AITaskAgent.Extensions.CSharp, additive; the summary of the symbol's XML
  documentation, read from `<assembly>.xml` beside each reference by a lazy provider in `UnitReferences`) and `CodeCompletionItemDto.info` (Web): the designer
  replaces it with the Spanish meaning of `HookDocs.Meanings` for the members of hook and validator arguments.
- **Language switch.** Each slot row has a C# | JS radio group (JS disabled with the reason for a C#-only slot); the list dialog of `validators` /
  `resultValidators` has its own, which filters «Añadir…» and decides the stub's language. No YAML key: the name is the choice (`js:…` is JavaScript), a free
  slot starts in C#. The code dialog opens in the chosen language (`HookCodeDialog.tsx`: `CSharpCodeDialog` / `JsCodeDialog`).
- **Help.** `Samples/WorkflowDesigner.Core/Help/*.md` (embedded resources `Help/<topic>.md`; topics `hooks`, `validator`, `resultValidator` and
  `hook.<slot>` for the 13 slots; `hook.selectConversation` since 2026-10-24), Spanish with identifiers in English: when it runs, order relative to retries/validation/middleware, every member, what
  happens on an exception or when the writable member is left alone, what JavaScript can do, and an example in C# and JavaScript (C#-only slots say why).
  `HelpTopics.Read(topic)` only serves names of the embedded set (`^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)?$`). `GET /api/help` → `{ topics }`,
  `GET /api/help/{topic}` → `{ topic, markdown }` or 404 `help_topic_not_found`. The dialog's «Ayuda» button (and F1 inside the dialog) shows it in a panel,
  rendered by `src/shared/markdown` (own minimal renderer, no dependency: headings, paragraphs, nested lists, fenced code, quotes, tables, emphasis, code
  spans, links; no raw HTML, every text is a React text node; a link is a link only for `http(s):`, `mailto:` or `#anchor`), loaded lazily
  (`React.lazy` in `HookHelp.tsx`).
- **Completion after `.`** C#: Roslyn as before, with the documentation above in the popup; `args.`, `args.Result.`, nested members and the unit's types
  (`CSharpCompletionTests.Nested_members_complete_after_each_dot`). JavaScript (`scriptGlobals.ts`): `args.` from `jsMembers` (a hook) or the validator's
  members, nested objects (`args.step.`, `args.response.`), the step's `outputSchema`/`inputSchema` for `args.result.`/`args.input.` (properties, `[n].` into
  array items: `args.result.lines[0].`), the members of a string, number or array by type, `JSON`/`Math`/`Object`/`Array`/`Number`/`String`/`Date`,
  `console.`, `host.<object>.<method>` and `require('…')`; each item shows its type and meaning.
- **Editor load failure.** `CodeEditor` used to fall back silently to the plain text area when the CodeMirror chunk failed to load (a stale `wwwroot` was
  the likely cause of «no editor features» on 2026-10-23); now it shows «El editor avanzado no se pudo cargar…» with «Reintentar» and logs the reason with
  `console.error`.

#### The code dialog since 2026-10-24 (editable from the start, docked help, «Comprobar»)

Reported by the user after restarting Vite and the backend (so not a stale build): «the windows of the hooks where the code is shown do not let me modify the
content». Reproduced in a real browser (Playwright on a scratch Vite with `?mock=1` and on a scratch backend with a temporary workspace): the CodeMirror editor
accepts typing whenever it is not locked; what did not accept it was (1) the **stub preview** shown before «Crear stub» — a read-only CodeMirror that takes the
focus and the caret but ignores keys, with no message; (2) the C# editor of a package that is **not trusted**, locked with a notice that was easy to miss; and (3) a
real bug: reopening a slot bound to `Hooks.X` when the unit did not register it (the unit does not compile, or the hooks list was not refreshed) showed the preview
of a **new** stub instead of the code of `X`, because the editor only opened for a name the server listed with `source: unit:*`. A package created in the designer
is trusted (nothing to confirm) and stays trusted after `PUT …/csharp/shared` (checked against the scratch backend), so the backend was not the cause.

- **Editable from the start.** A free slot or validator opens the **whole** `csharp/shared.cs` (or JavaScript library) with the stub already inserted in the working
  text, unsaved and editable, the caret on it. The name field («Nombre del método», «Nombre de la función») renames the inserted declaration in place
  (`renameMethod` / `renameJsFunction` in `domain/hookStub.ts`: the declaration only; invalid, clashing or no-longer-declared names are explained and keep Aceptar
  disabled). JavaScript: the function goes to the first library of the package, or to a new `hooks`; the library selector moves it while the text is the freshly
  inserted one. A slot whose stub is already in the unit under the suggested base name (`<IdDelStep>_<Slot>` plus an optional number, same argument type:
  `findSlotStub`; JavaScript `findJsSlotStub`) opens that method and binds it, without inserting a duplicate. A bound name the unit **declares** opens its code even
  when it is not registered (`findHookLine` on the text). Accept saves when there are changes and binds what was created, as before; Cancel discards it all. The
  read-only preview remains only when no package is open (there is nowhere to insert the stub).
- **Locked editor: reason and action, never silent** (`HookCodeParts.tsx`, `codeLockOf` + `CodeLockNotice`, `role="alert"` on top of the dialog): no package →
  «No hay ningún paquete abierto… Abre o crea un paquete en el selector de la barra superior»; read-only → who holds it and «ciérralo en el otro proceso y vuelve a
  abrir el paquete» (no button: there is no verified way to get an editable copy of a locked session, `save-as` from a read-only session was not checked); C# of an
  untrusted package → «Confiar…», wired to `usePackages().ensureTrusted()`; once trusted the editor unlocks in place with the stub already there. JavaScript needs no
  trust (unchanged).
- **Wider dialog with the help docked.** `EditDialog className="hook-dialog"` (`features/inspector/hookCode.css`): `min(1500px, 96vw)` wide, `min(80vh, 900px)` tall,
  code on the left and the Markdown help (`HookHelp`, same `GET /api/help/{topic}` topic as before) on the right, each scrolling on its own, visible by default.
  «Ayuda» hides/shows it and the choice is remembered in `localStorage` key `wd.hookCode.help` (read and written inside try/catch); F1 shows it and moves the focus
  to it. Reading typography for the docked Markdown (larger size and line height, headings, lists, mono code blocks, tables) only with `tokens.css` variables, so
  light and dark follow the theme. Below 900 px the panes stack and the dialog body scrolls.
- **«Comprobar».** A button in both dialogs and an automatic check 1 s after typing stops (`useCodeCheck`, a newer check aborts the previous one) call
  `POST /api/packages/{id}/code/check` (core-api «Code editor») with the editor's text: green «Sin errores», or the list of problems where each one with a line is a
  button that moves the caret there; the same problems are marks in the editor while the text is the one checked (a changed text says so). It never saves.
- **JS switch of a slot row.** When JavaScript is not available for a slot, «solo C#» is written next to the C# | JS switch (`pgrid__lang-why`); the whole reason
  is in its title, in the title of the disabled JS chip and in the description bar (the full server text inline crushed the select in a narrow inspector, seen in
  the browser). Against a fresh backend the switch is enabled for `postProcess`, `onFinalize`, `buildUserMessage`,
  `buildSystemMessage`, `parseResponse` and `onToolLimit` of an LlmStep (checked in the browser). Since 2026-10-24 it is enabled for every slot but
  `onConversationCreated` (Vitest `Inspector.hookCode.test.tsx` «selectConversation y los slots síncronos»; not re-checked in a browser).

### Framework: hooks (phase A, 2026-10-19, contract §14)

- **Where the types live.** `IHookRegistry`/`HookRegistry`/`HookInfo`/`HookKind`, `IResultValidator` and `StepHookException` are in `AITaskAgent.Abstractions` (`StepResult` needs them); `StepHooks`, the args, `HookSlots` and the delegate validators are in `AITaskAgent.Core` (the args carry `StepBase`); `LlmStepHooks` is in `AITaskAgent.Extensions.Llm`. The Yaml project does not reference the LLM extension, so the binder is generic: a step type declares its hooks record with `[HooksType]` and `HookSlots.For(type)` enumerates the slots by reflection.
- **Result validators** are attached to each result by `StepBase` (replace, not append, so a passthrough step does not inherit an upstream list) and run by the virtual default of `StepResult.ValidateAsync`. `RetryMiddleware` has no non-virtual call; its only change is catching `StepHookException` around the two validation calls (fatal, after finalizing). `LlmStringStepResult` was also changed to call `base` (it is what an `LlmStep` without `outputSchema` returns). `CriticResult` of the AdvancedLlmPipeline sample (and the copy in PipelineVisualizer) still overrides without `base`: not YAML-usable, left alone.
- **`ValidateArgs` has no `Attempt`** (neither `IStepValidator` nor `StepBase.Validate` carry it) and result validators run with `CancellationToken.None` (`IStepResult.ValidateAsync` has no token). Both are interface limits, not changed.
- **`YamlLlmStep.BuildUserMessageAsync/BuildSystemMessageAsync` do not call `base`**: they call the same protected dispatcher the base default uses, with their own rendering as `next`. Calling `base` would hand the hook `Prompt` as `next`, which a YAML step does not use. `BaseLlmStep.FinalizeAsync` now `await`s `base.FinalizeAsync` on every exit (async); `onFinalize` itself is dispatched from the explicit `IStep.FinalizeAsync`.
- **`onToolLimit` is implemented in `ExecuteAsync` (`HandleToolLimitAsync`), not in `SwitchToNextFallback`**, which is untouched as requested. Consequences: the final answer is a second call inside the same attempt (no fallback round, no `attempt` reset); the final request keeps `tool_choice: none` in `state.Request`, so a later retry of the same invocation also answers without tools; when the cut was the iteration limit the failed turn's token usage is not available (the loop returned a failure), so only the final call's is reported.
- **F2:** `onConversationCreated` + `onFinalize` are done (a note protocol can move to them). **`tagHandlers` by name from DI is not built:** a pinned test (`YamlPipelineV2Tests.An_LlmStep_rejects_the_inherited_properties_that_do_nothing_in_YAML`) states that `tagHandlers` is rejected in YAML; changing it is a public-contract change. See «Objections».
- **Catalog tests changed:** `CatalogTests.Serializes_to_the_contract_shape` (adds `hookSlots` as last property) and `Structural_keys_are_not_parameters` (the `structural` lists now include the three keys). Frontend: `structural.includes(...)` only, so unknown keys are harmless.
- **Not in phase A:** `GET /api/hooks`, the document model/reader/emitter support for the three keys, the inspector (done in phase B, next subsection). Registration of unit methods in the registry (phase C).

### Designer: hooks, validators and result validators (phase B, 2026-10-19, contract §14.5)

- **Document.** `StepNode` gains `hooks` (`OrderedDictionary<string,string>`, slot → name), `validators` and `resultValidators` (`List<string>`), all optional and omitted when empty, next to `inputSchema`/`outputSchema` (the DTO is `StepNodeDto` in the frontend). `YamlReader` reads the three keys out of `properties` (a malformed value is a located error: «'hooks' must be a mapping…», «'validators' must be a list of names»); `YamlEmitter` writes them after `outputSchema` and before the properties, so a document round-trips byte for byte; `TemplateEngine.Clone` copies them (names are not templates: `${{ params }}` does not apply to them).
- **`HookCatalog` (Core, `Engine/HookCatalog.cs`).** `List()` builds a throw-away engine collection (the one of the catalog, with `ConfigureEngineServices`) and returns the `IHookRegistry` entries plus every `IStepValidator` service not already registered by name (`source: di`), sorted by name, as `HookInfoDto { name, kind, signature, description, source }` (`kind`: `hook`, `stepValidator`, `resultValidator`). It is a singleton of the Core and one of `WorkspaceServices.Bound` (so the endpoint gets the open workspace's). Signatures are text built by `HookSignatures.Describe` (C# keywords and tuples: `Func<ValidateArgs, Task<(bool, string)>>`; the tuple element names do not survive reflection). The catalog's `hookSlots` use the same formatter. **A host registers hooks in `WorkflowDesignerOptions.ConfigureEngineServices`** (replace the `HookRegistry`/`IHookRegistry` singletons with its own registry, or add `IStepValidator` services): validation, runs and `GET /api/hooks` all build their engine collection through it.
- **`GET /api/hooks`** (`CatalogEndpoints`): `{ "hooks": [HookInfoDto] }`, global to the workspace (not per package yet: unit hooks of phase C will need `GET /api/packages/{id}/hooks`). Empty when nothing is registered, which is the case for the shipped host until phase C or an extension registers some.
- **Validation (`Validation/HookChecks.cs`).** After `PreCheck`, when a step uses any of the keys: every problem at once and located on the key's line, instead of the factory's first one: unknown name («'X' is not a registered step validator. Available: …»), a name of another kind («'X' is a step validator, not a hook»), unknown slot («'nope' is not a hook slot of 'ScriptStep'. Valid slots: …»), a step type that does not accept the key, and `schema:<Name>` naming a schema that the package does not have. The signature of each delegate is still checked by the factory when the pipeline is built (it is the only one that knows the step instance), so a wrong signature arrives as the factory's error. Warning (never an error): `ExecStep`/`ScriptStep` with a validator or result validator **and an explicit `maxRetries` above 1** («repite el step entero en cada intento… se recomienda maxRetries: 1»). Both steps default to `MaxRetries = 1` (`ExecStep.cs:93`, `ScriptStep.cs:95`), so without an explicit value the remark would be false and is not emitted.
- **CLI.** `validate` already prints warnings and errors; the new lines come for free. `list-steps` shows `hook slots: …` per step (and `hookSlots` in `--json`); new `list-hooks [--json]` (what the engine itself offers: nothing, unless the CLI is linked to a host that registers some).
- **Frontend.** `state/hooks.tsx` (`HooksProvider`/`useHooks`, under `ToolsProvider`; `useHooks` does not throw without a provider, so isolated inspector tests keep working), `features/inspector/HooksSection.tsx`: a «Hooks» section in the step inspector with `validators` and `resultValidators` as ordered lists (add from a select filtered by kind, ↑ ↓ ×, `schema:<Name>` for each schema of the package in `resultValidators`), the slots of the step type (bound ones on top as selects, the rest inside «Slots (n)») and the retry warning. A name the server no longer has shows «(no registrado)» and is kept until removed. `domain/graphEdits.updateStep` patches the three keys (`null`, `[]` or `{}` remove them); the mock API lists six demo hooks and gives every step the slots; `mockYaml` emits the keys.
- **E2E (real browser, 2026-10-19).** Playwright against the real backend on 5191 with a scratch host (`E2eHost`, outside the repo) that registers four test hooks in `ConfigureEngineServices` through the `Microsoft.Extensions.Hosting` diagnostic listener: 14/14 checks — section visible, options filtered by kind, assign validator + result validator + `onFinalize`, live YAML tab, Validar without errors, Guardar, the zip's YAML carries the three keys, reload keeps them, a run with `{"name":"bad"}` fails with the validator's message (`run.failed`, step «fallido») and `{"name":"Ana"}` completes.
- **Not done / limits.** No hook is registered in the shipped host, so the selectors are empty in production until phase C (unit methods) or an extension registers some; the section says so. `GET /api/hooks` is not per package. The designer does not validate the delegate signature itself (only the factory does). The canvas does not mark steps that carry hooks. Hooks are not shown in the Environment/Configuration view.

## 15. C# unit per package (contract added 2026-10-19, phase C)

Decisions: study §5, §6 and §8 (rows «C# response types», «schema classes», «deployment»). NuGet for the unit is phase D; the extension already accepts extra references (`CSharpCompileOptions.AdditionalReferences`) so D plugs in without changing this contract. Everything below lives in `AITaskAgent.Extensions.CSharp` (Roslyn and `NJsonSchema.CodeGeneration.CSharp` only here; Core and Yaml untouched) and in the shared designer Core, so the CLI and the Web behave the same.

### 15.1 Package format

- New optional entry **`csharp/shared.cs`** (workflow packages only; UTF-8; 5 MiB like any entry): declarations shared by every C# step of the package (classes, helpers, hooks, validators, response types). `PackageArchive` whitelists it (`Classify` rank 7, `FromDirectory`, `ExtractTo`), `WorkspaceStore` reads and writes it (`ReadSharedCSharp`/`SaveSharedCSharp`/`DeleteSharedCSharp`), `PackageStore.SaveEntries` saves it like any entry.
- New manifest field **`csharp`**: `{ "shared": "csharp/shared.cs" }`, written by `PackageArchive.Write` exactly when the entry exists and removed when it does not (informative; the entry is the authority and the trust looks at entries, never at the manifest). A package without C# is byte-identical to before.
- The step's own code is **not** an entry: it is the `code` property of a `type: CSharpStep` step in `workflow.yaml` (a block scalar, like `script:` of `ScriptStep`).

### 15.2 The step and the generated unit

```yaml
- id: Normalize
  type: CSharpStep
  code: |
    var name = call.InputAs<string>() ?? "";
    return call.Ok(new { name = Strings.Title(name) });   # Strings: a class of csharp/shared.cs
```

- `CSharpStep` (`[StepTag("CSharpStep")]`, category `csharp`, `x-editor: code:csharp` on `code`, `MaxRetries = 1`) derives from `StepBase` (input and output `IStepResult`) and is the delegated step of the unit: it holds, per step id, a delegate to the matching static method. It is **not** a subclass of `DelegatedStep<,>`: that class carries no `CancellationToken` and, closed over `IStepResult`, makes the two `ExecuteAsync` overloads of `TypedStep` collide (see «Deviations»).
- One compilation per package: four syntax trees. `generated/Usings.g.cs` (global usings: `System`, `System.Linq`, `System.Collections.Generic`, `System.Threading`, `System.Threading.Tasks`, `Newtonsoft.Json`, `Newtonsoft.Json.Linq`, `AITaskAgent.Core.Abstractions`, `AITaskAgent.Core.Models`, `AITaskAgent.Core.Hooks`, `AITaskAgent.CSharp`, `Workflow`, and one alias per schema class), `generated/Schemas.g.cs` (classes of the package schemas), `csharp/shared.cs` (verbatim, its own file path in diagnostics) and `generated/Steps.g.cs`:

```csharp
namespace Workflow;
public static partial class Steps
{
    [StepMethod("Normalize")]
    public static async Task<IStepResult> Normalize(StepCall call)
    {
#line 14 "workflow.yaml"
        var name = ...;      // the body as written, line for line
#line default
    }
}
```

- Method name: the step id with every character that is not a letter or digit replaced by `_` (prefixed `_` when it starts with a digit); a clash gets a numeric suffix. `Steps` is `partial` so `shared.cs` can add to it.
- **`StepCall`** (public, in `AITaskAgent.CSharp`): `Step`, `Input` (`IStepResult`), `Context` (`PipelineContext`), `Attempt`, `LastResult`, `Services`, `CancellationToken`; `InputAs<T>()`, `Get<T>(key)`/`Set(key, value)` (context metadata), `Ok(value = null)` (a `JsonStepResult`), `Ok(value, nextSteps)`, `Fail(message, exception = null)`, `CreateStep(tag, id, config = null)` (through the `IStepRegistry` of the run, to build `NextSteps`), `Log(message)`. The body must return an `IStepResult`. An exception becomes an error result naming the step.
- **Execution is in process** with the full API (decision of study §8); a `NextSteps` list on the returned result is honoured by the pipeline as for any step.

### 15.3 Hooks, validators, response types and schema classes

- Attributes of the extension (`AITaskAgent.CSharp`), usable in `shared.cs` on `public static` methods of public classes: `[Hook]`, `[Validator]` (signature `Task<(bool, string?)> M(ValidateArgs)`), `[ResultValidator]` (same with `ResultValidateArgs`), each with an optional `Description`. Registry name: `Class.Method`. They are registered in the engine's `IHookRegistry` with **source `unit:<package name>`** through a decorator (`AddCSharpUnit`), so the host's own registry keeps working and a clash between a unit name and another source is the usual «defined more than once» build error. A `[Hook]` may have any signature; the binder rebinds it to the slot it is named under (variance rules of `HookRegistry.TryGet`).
- `[ResponseType]` / `[ResponseType("Name")]` on a public class of `shared.cs`: after loading, `ResponseSchemaBuilder.Build(type)` gives its schema and the designer registers it in the run's `SchemaRegistry` under that name (default: the class name), so `outputSchema: Name` and `schema:Name` work. A class used that way and a package schema file of the same name is an **error** (`WFCS005`), as is any type declared in `shared.cs` with the simple name of a schema class (`WFCS002`): a name defined both ways does not compile.
- Schema-first: for every `schemas/<name>.json` of a package that has C# content one class is generated with `NJsonSchema.CodeGeneration.CSharp` (Newtonsoft attributes, POCO, in namespace `Workflow.Schemas.<Name>` and reachable as `<Name>` through a global alias). A schema the generator cannot turn into a class is a warning (`WFCS004`), not an error.
- The unit's response-type names are valid in `PreCheck` (schema existence) and the hooks of the unit are valid in `HookChecks`: the designer compiles the unit before them.

### 15.4 Compilation, cache, load

- `CSharpUnitCompiler.CompileAsync(CSharpUnitSources, ct)` returns `UnitCompilation { Success, Diagnostics, Unit?, Hash, FromCache, Elapsed }`. The key is a SHA-256 of the sources (steps, `FirstLine`s, shared, schemas), the compiler version and the reference set. The emitted DLL and PDB are cached in memory and, when `CSharpCompileOptions.CacheDirectory` is set (the designer uses `<data>/csharp-cache`), on disk (`<hash>.dll`, `.pdb`, `.json` with the warnings), so a new process (the CLI) does not compile again.
- References are an allowlist (not the whole runtime): `System.Private.CoreLib`, `System.Runtime`, collections, `System.Linq`, `System.Text.*`, `System.Threading*`, `System.ComponentModel*`, `System.Memory`, `System.IO.*` facades that the host's trusted platform assemblies provide, `netstandard`, `Newtonsoft.Json`, and `AITaskAgent.Abstractions/Core/Extensions.CSharp`, `NJsonSchema`. No `System.Diagnostics.Process`, `System.Net.*`: it stops honest mistakes, **not malice** (reflection), as the study says.
- Each `CompileAsync` returns a **new** `CSharpUnit` over its own collectible `AssemblyLoadContext` (static state of user code is per run). `CSharpUnit` is `IDisposable`: disposing unloads the context; the designer registers it through a factory so the run's service provider disposes it.
- Diagnostics are `CSharpDiagnostic { Id, Severity, Message, File, Line, Column, StepId, CodeLine }`; for a step body `File` is `workflow.yaml`, `Line` the YAML line (through `#line`), `CodeLine` the line inside the body, `StepId` the step. A diagnostic outside the body (a missing `return` is reported at the method end) is reported at the step's `code:` line. In the designer they become `WorkflowError` (`message`, `line`, `column` (inside the body), `stepId`, **new optional `file`**: `csharp/shared.cs` for the shared file, `null` for the YAML).

### 15.5 Trust, policy and CLI

- `TrustDto.csharp` (new, optional, absent when the package has no C#): `{ steps: [{ stepId, lineCount, sha256, code }], shared?: { lineCount, sha256, code } }`. A package with any C# is `required` (like a command): a run is a 403 `untrusted_package` until confirmed; **validating does not need trust** (compiling executes nothing). The hash covers, in stable order, each step id and the SHA-256 of its code and the SHA-256 of `shared.cs`; without C# the hash is unchanged. Edits made by the user in the designer keep a trusted package trusted (same rule as commands), including `PUT /api/packages/{id}/csharp/shared`.
- Policy: `ICSharpStepPolicy` (extension) is consulted by `CSharpStep` when registered; the designer registers `DesignerCSharpPolicy(execTrusted)` after the host's services, the same flag as `ExecStep` (`RunRequest.ExecTrusted`, false by default). Without a policy (framework use) the step runs.
- CLI: `run`/`validate` print one stderr line per C# step and one for `shared.cs` (`warning: this package runs C# code in process (step 'x': N lines, sha256 …)`), `package info` lists them (`csharp` in `--json`); it never asks. The CLI runs the zip through the same Core compiler and cache.

### 15.6 Web and frontend contract

- `GET /api/packages/{id}/csharp/shared` → `{ "text": "..." | null }` (null when the package has none); `PUT` with `{ "text": "..." }` writes and saves the entry (an empty text deletes it); both on the open package.
- `GET /api/packages/{id}/hooks` → `{ "hooks": [HookInfoDto], "diagnostics": [WorkflowError] }`: the engine's hooks plus the ones of the unit of the working copy (shared file and schemas only: step bodies do not declare hooks); empty `diagnostics` when it compiled. `GET /api/hooks` stays global.
- `StepCategories.CSharp = "csharp"`; catalog hint `x-editor: code:csharp`. `POST /api/packages/{id}/validate` returns the unit's diagnostics as errors/warnings with `stepId` and `line`; the inspector shows the ones of the selected step under the code field with their line inside the code.

### Deviations of phase C (2026-10-19)

- **`CSharpStep` is not a `DelegatedStep` subclass** (the plan said «replaced by a `DelegatedStep`»): the delegate of `DelegatedStep` carries no `CancellationToken` and, closed over `IStepResult`, the two `ExecuteAsync` overloads of `TypedStep` collide. `CSharpStep : StepBase` binds the generated method lazily on its first execution (by step id) from the `ICSharpUnit` of the run; a missing method or unit is a step error.
- **Typed YAML parameters are not bound to method parameters** (study §6 proposed `Task<IStepResult> Step(<properties>, input, context, ct)`): the method is `Steps.<Id>(StepCall call)`; `StepCall` carries input, context, attempt, last result, services and token. A step with parameters reads them from `call.Input`/context for now.
- **Manifest field `csharp`** is written by `PackageArchive.Write` only (not by the working copy's `manifest.json`), so a working copy never looks dirty because of it.
- **`TrustDto.csharp`** is serialized as `csharp` (an explicit `JsonPropertyName`; the camelCase default would give `cSharp`).
- **Validation compiles and loads the unit** (needed to know hooks and response types) without trust. Nothing of the author's code is executed (no step, hook or static constructor is invoked), but reflection over the unit's types can instantiate custom attributes the unit defines: see objection 17 in the study.
- **Remote validation result is shared state in the frontend** (`ValidationProvider`): the header indicator and the inspector of a C# step read the same result; `Compilar` calls the same validation.
- **`PackageHooksDto.responseTypes`** (not in the first contract): names of `[ResponseType]` classes, offered in `schema:` selectors.
- **Measured (xunit host, small unit with a shared file):** first compile in a process about 0.9 s, memory cache hit about 20 ms, disk cache hit in a new process about 100 ms. Roslyn assemblies were already shipped by `FileTools`; `NJsonSchema.CodeGeneration.CSharp` adds about 115 KB.
- Phase D (NuGet) is not started; `CSharpCompileOptions.AdditionalReferences` is where its resolved assemblies plug in.

## 16. NuGet packages per workflow package (contract added 2026-10-19, phase D; the per-extension part was replaced by plugins, §25)

Decisions: study §7 and §8 (row «NuGet in C#»). One resolver, in the shared designer Core (`WorkflowDesigner.Core.Nuget`), used by the CLI and the Web; the C# extension stays free of NuGet and only receives file paths (`CSharpCompileOptions.AdditionalReferences`, `NativeLibraryDirectories`).

### 16.1 Declaration

- Package: manifest field **`requires`** = `{ "nuget": [ { "id": "Humanizer.Core", "version": "2.14.1" } ] }` (informative to old readers: unknown fields are kept by `Extra`). (Phase D also put the same list in a `nuget` section of an extension's `extension.json`; that section and `ExtensionNuget.Parse` are gone: a folder plugin ships its dependencies, a NuGet plugin is resolved by `PluginLoader`, §25.2, §25.7.)
- `version` is a NuGet version or range as written by NuGet (`1.2.3` means «1.2.3 or higher», `[1.2.3]` exact, `[1.0,2.0)`). **Floating versions (`1.*`, `*`) are rejected** at declaration time.
- Only workflow packages; entries are unique by id (case-insensitive).

### 16.2 Lock file

- New optional package entry **`nuget.lock.json`** (workflow packages, rank 8, 5 MiB limit like any entry). `PackageArchive.Classify`/`FromDirectory`/`ExtractTo` whitelist it; `WorkspaceStore.ReadNugetLock/SaveNugetLock/DeleteNugetLock` read it from the working copy root; `PackageStore.SaveEntries` with this entry also copies `manifest.requires` from the working copy to the zip's manifest.
- Shape: `{ "version": 1, "targetFramework": "net10.0", "requiresHash": "<sha256 of the canonical requirements>", "packages": [ { "id", "version" (exact), "hash": "sha512-<base64 of the .nupkg>", "direct": true|false, "dependencies": ["id/version", …] } ], "provided": [ { "id", "version", "hostAssembly" } ] }`. `provided` lists packages left out because the host supplies them (shared set).
- **Run time never resolves**: a run or a validation only materializes the lock (the exact versions, hash-checked). A lock whose `requiresHash` differs from the manifest is *stale* (warning in validation; a run refuses it with the message «lock out of date: restore»). A missing lock with requirements is *not restored* (error).

### 16.3 Resolver (`NugetResolver`)

- `RestoreAsync(requirements, existingLock?, options, ct)` → `NugetRestoreResult { Lock, Environment, Notes (info/warnings), Conflicts }`. Uses `NuGet.Protocol` (`FindPackageByIdResource` over each source) and `NuGet.Packaging`/`NuGet.Frameworks`: lowest version satisfying the range, **only the dependency group of the nearest target framework to `net10.0`** (not every group, unlike BRMS), highest minimum wins on a diamond, error when no version satisfies two ranges. It is a simplified NuGet algorithm, not `NuGet.Resolver`: see «Deviations».
- `MaterializeAsync(lock, options, ct)` → `NugetEnvironment { References (dll paths, the nearest TFM of each package's `lib/`), NativeDirectories (`runtimes/<rid>/native` of the current RID), Packages, Notes }`; works **offline from the cache**; downloads a missing package only when `options.AllowNetwork` and a source has it; always checks the `.nupkg` SHA-512 against the lock and refuses a mismatch.
- Shared set (**host wins**): a package whose id is in the shared set and which the host process can load by that name is not downloaded; an assembly of the shared set (`AITaskAgent.*`, `Newtonsoft.Json`, `NJsonSchema*`, `Microsoft.Extensions.*`, `System.*`, `Microsoft.NETCore.*`, `NETStandard.Library`, `Microsoft.Win32.*`, `Microsoft.CSharp`) is never loaded from a package. A requested major version above the host's is reported as a conflict (the host's version is used).
- Native libraries: `runtimes/<rid>/native/*` for the running RID (and its fallbacks) are extracted and passed as `NativeLibraryDirectories`; the unit's load context probes them in `LoadUnmanagedDll`.

### 16.4 Cache, sources, trust

- Cache: **`<data>/nuget/packages/<id lower>/<version>/`** with the `.nupkg`, `content/` (the extracted `lib` and native files) and `ok.json` (hash); never `~/.nuget`. Shared by all packages (content addressed by id/version and verified by hash), so two packages can load different versions: every load context reads its own files.
- Sources: `WorkflowDesignerOptions.NuGetSources` (folder paths or `https://…/index.json` URLs), filled by `NugetSourceConfig.Resolve` from, in order: the host's explicit list (`WorkflowDesigner:NuGetSources`, `--nuget-source`), the variable `WORKFLOW_DESIGNER_NUGET_SOURCES` (`;`-separated) and, for the web backend, `nugetSources` of the user's `settings.json`. **Never** in the zip. No sources = offline. `GET /api/nuget/sources` lists them. The cache folder is `WorkflowDesignerOptions.NuGetCacheDirectory` or `<data>/nuget` (`--nuget-cache` in the CLI).
- Trust: a package with requirements is `required` (like commands and C#): `TrustDto.nuget` = `[ { id, version, hash?, direct } ]` (from the lock; the declared range when no lock) and the trust hash covers it (in stable order); without requirements the hash is unchanged. **Restoring (any network or cache write) needs a trusted package** (403 `untrusted_package`); validating never touches the network.

### 16.5 Web, CLI, frontend

- `GET /api/packages/{id}/nuget` → `{ requires: [{id,version}], lock: { packages, provided, stale: bool } | null, status: "none"|"notRestored"|"stale"|"ready", sources: [...] }`; `PUT` with `{ requires: [...] }` saves the declaration (and keeps the zip's manifest in step); `POST /api/packages/{id}/nuget/restore` (trust required) → the same body plus `notes`. Error codes: 400 `invalid_nuget_requirement`, 403 `untrusted_package`, 502 `nuget_restore_failed` (message names the id and the sources tried).
- CLI: `package restore <zip|folder> [-o out.zip] [--nuget-source …]… [--nuget-cache dir] [--force]` (the only command that resolves ranges: it writes the lock into the zip, or `nuget.lock.json` into the folder), `package info` shows requirements, status and the lock (`--json`: `nuget { status, requires, packages }`), `validate` reads the cache and never fetches, `run` fetches a missing locked package by its pinned hash when a source is configured; both print one stderr line per package (id, version, hash prefix).
- Frontend: section «NuGet» of the package (list id/version, «Restaurar», status, hashes), texts in Spanish; the trust dialog lists the packages with hashes.

### Deviations of phase D (2026-10-19)

- **Simplified resolver, not `NuGet.Resolver`.** The contract says «official NuGet client libraries»: sources, package reading and framework selection are `NuGet.Protocol`, `NuGet.Packaging` and `NuGet.Frameworks` (7.3.1, already in the local cache); the version choice is `NugetResolver`'s own loop: the lowest version that satisfies every range that mentions the package, repeated until nothing changes, the highest minimum winning on a diamond, an error naming the clashing ranges when none fits. It does not implement NuGet's «nearest wins» rule, floating versions, or `NuGet.Resolver`'s constraint solver (which needs every version of every package in memory). Packages that become unreachable after a version change are pruned afterwards; constraints that unreachable packages contributed before are not undone (a corner case: another `restore --force` settles it).
- **`AllowNetwork` means «may read the configured sources at all»** (folders and URLs alike), not only «may use the network». With it false (validation, and a package opened without trust) only the cache is read, so validating never writes to the cache or touches a source. A run passes `RunRequest.ExecTrusted` (the host already checked trust, the CLI always sets it): a trusted run may therefore fetch a locked package that is missing from the cache, **by its pinned SHA-512**; it never resolves ranges.
- **Sources: web backend = `WorkflowDesigner:NuGetSources`, then `WORKFLOW_DESIGNER_NUGET_SOURCES` (`;`-separated), then `nugetSources` of the user's `settings.json`; CLI = `--nuget-source` (repeatable, `run`, `package restore`) then the environment variable.** The CLI does not read the user settings file (the tests must never read the real one). `NugetSourceConfig.Resolve` is the single place that merges them.
- **`WorkflowDesignerOptions.NuGetCacheDirectory` (not in the first contract).** A host whose data folder is throwaway (the CLI validating a zip uses a temporary one) needs a cache that lasts: `--nuget-cache <dir>`, defaulting to `<data>/nuget` for a persistent host and to `%LOCALAPPDATA%\WorkflowDesigner\data\nuget` for a temporary one.
- **The lock travels with the declarations.** `PackageStore.SaveEntries` copies `manifest.requires` from the working copy to the zip when `nuget.lock.json` is among the saved entries; `PackageStore.SaveNuget(session, requires, lock)` is the one call that does both (a null lock removes it). The manifest is otherwise never saved on its own.
- **Trust.** A package with declarations is `required`; the hash adds `|nuget:` plus the sorted id, exact version and SHA-512 (the declared range, without hash, when there is no lock), and is unchanged for a package without them. `PUT …/nuget` and `POST …/nuget/restore` re-record the hash when the package was trusted (the user, or the designer producing the lock for a package they trusted, made the change), exactly as `PUT …/csharp/shared` does. `POST …/nuget/restore` is a 403 `untrusted_package` before anything is downloaded.
- **`POST …/nuget/restore` is not always a resolution.** When the lock already matches the declarations (and `force` is not set) it only materializes the lock, fetching what is missing by hash (`resolved: false`); declarations without a matching lock are resolved again. The CLI's `package restore` behaves the same.
- **Shared set = host wins, by name.** `AITaskAgent.*`, `Newtonsoft.Json`, `NJsonSchema*`, `Microsoft.Extensions.*`, `System.*`, `Microsoft.NETCore.*`, `Microsoft.Win32.*`, `Microsoft.CSharp`, `NETStandard.Library`: a package of that set which the host process can load by that name is left out of the lock (`provided`) and never downloaded; an assembly of that set inside another package is never loaded from it. A requested major version above the host's is a warning, not an error. The load context of the unit (`UnitLoadContext`) resolves only the assemblies the host handed it, so anything else falls through to the default context, which is how the shared set is the host's by construction.
- **One load context per unit, not per package version.** The context is the collectible `AssemblyLoadContext` of each compiled unit (`CSharpUnit`), created on every run and disposed with it; two packages (or two runs) can therefore load different versions of a library side by side. Assemblies are loaded from bytes, so the cache files stay free. Whether an unloaded context is really collected depends on the author's code leaving no thread or timer alive (objection 15).
- **Native libraries:** `runtimes/<rid>/native` of the running identifier (then its OS and `unix`) is extracted to `<cache>/packages/<id>/<version>/content/native` and probed by `UnitLoadContext.LoadUnmanagedDll`; managed `runtimes/<rid>/lib/<tfm>` overrides `lib/<tfm>` of the same file name. A test extracts the right folder for `win-x64` and `linux-x64`; no native library was actually loaded.
- **No `NuGet.Resolver`, no `NuGet.Configuration` files:** `nuget.config` is not read, so the user's own NuGet setup (and its credentials) is never used; a source that needs authentication is not supported yet.
- **Audit warning.** `NuGet.Packaging` depends on `System.Security.Cryptography.Pkcs`; the advisory feed flags 8.0.1, 10.0.0 and 10.0.11 here (`NU1902/NU1903` are errors in this repository). The two advisories are suppressed in `Samples/WorkflowDesigner.NuGetAudit.props`, imported by the designer Core, CLI, Web and their three test projects only. The designer never verifies package signatures, it checks each `.nupkg` against the SHA-512 of its own lock.
- **Extensions (`extension.json`) — replaced by plugins (2026-10-21).** Phase D's `nuget` section of `extension.json` and `ExtensionNuget.Parse` no longer exist; `PluginLoader` calls `NugetResolver` for a NuGet plugin with its own lock (`<data>/plugins/plugin-locks/…`, §25.6) and the shared cache.
- **Frontend.** `NugetSection` in the workflow settings (below «C# del paquete»), the trust dialog lists the packages with the start of their hash. `Restaurar` saves pending edits first, refreshes the trust, asks for trust when the package is not trusted, and shows the server's message (502 names the package). The mock simulates a lock deterministically (the first version of the range, a fake hash; ids starting with `Ghost` or `NotFound` fail).
- **Measured / counted.** Designer Core 520/522 (+32 NuGet tests; the 2 failures are the known `ExampleWorkspaceDeterminismTests`), Web 296/296 (+9), CLI 160/160 (+8), CSharp extension 32/32, Vitest 806/806 (+17). The test feed is a folder of `.nupkg` built by the tests (`Samples/TestShared/NugetTestFeed.cs`, linked into the three projects): no network was touched. Real NuGet.org or an authenticated feed were **not** exercised.

## 17. Dynamic loading of extensions (phase E, 2026-10-19) — superseded by §25 (2026-10-21)

**Superseded by §25 (2026-10-21).** Phase E loaded «extensions»: a folder with `extension.json` and one `IExtension`, read once at start and trusted at host level. Plugins (§25) replaced that model; the designer did not load `IExtension` classes for a while (2026-10-21) and does again since 2026-10-22: a plugin is driven by its one `IExtension` (§25.3a). The phase E loader, the `extension.json` manifest and the host-level trust are still gone. This section keeps what still holds and the table of renames; the phase E contract text (roots, `ExtensionLoadContext`, `ExtensionHost`, `ExtensionProblem` codes, `ExtensionDto`, `requires.extensions`, `|ext:` trust, `/api/extensions`, `extension …` commands) and its «Deviations» were removed because they describe code that no longer exists (git history has them).

### 17.1 What still holds from phase E

- **The framework loads nothing**: `AITaskAgent.Core` has no loader. The loader is `AITaskAgent.NugetLoader` (`PluginLoader`, §25.7); the designer Core wraps it in `PluginService` (§25.9), and the CLI and the Web share that service.
- **One non-collectible load context per plugin; the host wins** for `SharedAssemblySet` (`AITaskAgent.*`, `Newtonsoft.Json`, `NJsonSchema*`, `Microsoft.Extensions.*`, `System.*`…); the plugin's own assemblies are loaded from bytes, so the files are not locked and the hash that was approved is the hash of what was loaded (§25.4, §25.5). The context is now named `plugin:<nuget|folder>:<id>/<version>`.
- **A plugin that fails never takes the host down** and registers nothing; problems are reported with a code (`PluginProblemDto`, `LoadReportDto`).
- **Opening a package without its plugin does not make it read-only, and it round-trips.** An unknown step is read as a leaf whose properties (for an unknown container, also its children) are plain data and the emitter writes them back unchanged; validation and run fail instead: `plugin_missing` when `requires.plugins` names a plugin that is not installed or not loaded, else `step_unknown` (the framework's wording `unknown type '<tag>'` and the step's line are kept). With several requirements the message lists the plugins that are not installed: which plugin owns an unknown tag is not known. Comments and formatting of the YAML are lost on save, as always.
- **Removing a loaded plugin, or loading another version of it, needs a restart**; adding one is live (§25.5).
- **The package never carries the plugin**: `manifest.requires.plugins` names it by id (and optionally `origin`, `min`, `sha`); the DLLs live in the application, not in the zip. A plugin's steps are category `other` in the catalog.

### 17.2 Renames (extension and step package → plugin)

Everything below also applies to §21-§24, which point here.

| Before (§17, §21-§24) | Now |
|---|---|
| «extension» (folder + `IExtension`) and «step package» (NuGet) | **plugin**: `origin` `nuget` or `folder`; scope `explicit`, `workspace` or `install` |
| `<root>/<Name>/extension.json` (`name`, `assembly`, `contract`, `extensionType`, `nuget`) | `plugin.json` `{ id, version?, description?, contract, assemblies? }` (§25.2); `extension.json` is still read, as a `plugin.json`, with the warning `legacy-manifest`, and its `nuget` section is ignored |
| roots `<Workspace>/extensions`, `<install dir>/extensions` | `<Workspace>/plugins`, `<install dir>/plugins`; `<Workspace>/extensions` is still searched as a legacy root (§25.9) |
| `ExtensionDirectories`, `AutoDiscoverExtensions`, `WORKFLOW_DESIGNER_EXTENSIONS`, `WorkflowDesigner:ExtensionDirectories`, `--extensions` | `PluginDirectories`, `AutoDiscoverPlugins`, `WORKFLOW_DESIGNER_PLUGINS`, `WorkflowDesigner:PluginDirectories`, `--plugins` (no alias: the old names are usage errors) |
| `ExtensionHost`, `ExtensionLoadContext`, `ExtensionInfo`, `ExtensionChecks`, `StepPackageService`, `NugetStepLoader`, `NugetStepLoaderOptions` | `PluginService` (designer Core), `PluginLoader`, `PluginLoaderOptions`, `IsolatedLoadContext`, `PluginRequirements` |
| `steps-packages.json`, `<data>/step-packages/<id>/<version>/nuget.lock.json` | `<data>/plugins/plugins.json`, `<data>/plugins/plugin-locks/<id>/<version>/nuget.lock.json`; the old file is migrated once and renamed `steps-packages.json.migrated` (§25.6) |
| load context `nuget-steps:<id>/<version>` and the extension's one | `plugin:<origin>:<id>/<version>` |
| step without `[StepTag]` (steps packages): class name, `tagged: false`; extensions: not registered | `Type.FullName`; never dropped; `StepTypeDto.tagged` is gone |
| `Extensions.<Name>` of `config.json`, `AITaskAgent:Extensions:<Name>`, `IExtension.ConfigurationModel()` | `Plugins.<id>`, `AITaskAgent:Plugins:<id>`, `IExtension.ConfigurationType`/`ConfigurationModel()`/`ConfigurationDefaults`/`RootSections` (`IPluginConfiguration` stays for a plugin without an extension; §25.1, §25.9); migrated in memory when a file is read |
| the `IExtension` class of an extension folder | the plugin's entry point again (§25.3a): its `Register` registers everything and its `Info()` declares it; a plugin without one loads by type discovery |
| `requires.extensions: [{ name, min?, sha256? }]`; codes `extension_missing`, `extension_version`; trust fragment `\|ext:` | `requires.plugins: [{ id, origin?, min?, sha? }]`; `plugin_missing`, `plugin_version`; `\|plugin:` (§25.9) |
| `ExtensionRequirements`, `ExtensionUse`, `PackageStore.DeclareExtensions` | `PluginRequirements`, `PluginUse`, `PackageStore.DeclarePlugins` |
| `GET /api/extensions`; `GET`/`POST`/`DELETE /api/step-packages` | `/api/plugins` (§25.10); they answer 404/405 now |
| `CatalogDto.extensions`/`extensionProblems`; `StepTypeDto.extension`/`nuget`/`tagged`; `TrustDto.extensions` | `plugins`/`pluginProblems`; `StepTypeDto.plugin { id, version, origin }`; `TrustDto.plugins` |
| CLI `extension list\|info\|verify\|restore` and `steps install\|list\|remove` | the `plugin` group (§25.11); exit code 7 stays «needs approval» |
| trust: extensions had none of their own (host-level); only step packages asked | every plugin of either origin goes through the two-step trust before anything is loaded (§25.4) |
| «Paquetes instalados» / «Extensiones» / «Paquetes NuGet» | «Plugins instalados» (§24) |

## 18. Extensible JavaScript (contract added 2026-10-19, phase F)

Decisions: study A (all of it), study §3 point 3 and §5 point 2 (declarative next steps), RETOMAR §0.3. The worker keeps its shape (one process per execution, JSON in and out, bare V8, no .NET object exposed). **Reading of «`proto`»:** study A's own wording is «add a `proto` integer to `ready` and to `run`» (protocol version); it is implemented as that, not as a prototype extension. Everything below lives in `AITaskAgent.Extensions.Script` (+ `.Worker`) and in the shared designer Core, so the CLI and the Web behave the same.

### 18.1 Protocol (version 2; superseded by version 3, §18.6)

- `ready` carries `proto` (integer, `ScriptProtocol.Version = 2`); `run` carries `proto` too. A worker that answers another version (or none) is a clear step error («worker speaks protocol X, host Y»); the worker refuses a `run` whose `proto` differs.
- `run` gains: `modules` (JSON text of `{ name: source }`: only the libraries the step declares in `uses`), `allow` (JSON text of the host functions the step may call, already intersected with what the host grants), `maxCalls`, `maxCallBytes`, `mode` (`run` | `check`). `check` carries `scripts` (JSON text of `[ { id, script } ]`) and answers `result.json = [ { id, error? } ]` after **compiling** each one (no execution): one process for all the scripts of a package.
- New worker→host message `call` (`fn`, `args` JSON text); the host answers with `value` carrying `ok`, and `json` or `error` (the existing `get` reply, `{json}`, is untouched). `get`/`set`/`log` stay as they are. `result` gains `nextSteps` (JSON text of an array of step definitions, or absent).
- Limits: `maxCalls` default 100, ceiling 10 000; `maxCallBytes` (arguments + replies, per run) default 1 MiB, ceiling 8 MiB: counted by the worker and, authoritatively, by the host (an over-budget call is answered with an error; the YAML cannot raise the ceilings). Host function time is inside `timeoutMs`.

### 18.2 What the script sees

- Existing: `log`, `console`, `context.get/set`, `input`. New (all frozen):
  - **`require(name)`**: closed. Only the names in the step's `uses` (CommonJS wrapper `(module, exports, require)`, cached per run, cycles get the partial `exports`); anything else throws `module 'x' is not declared in 'uses'`. A library is compiled lazily on first `require`, under its own document name (`lib:<name>`) so errors name it.
  - **Native library** (worker, .NET delegates, pure functions, no I/O): `hash.sha256/sha1/md5(text) → hex`, `base64.encode/decode(text)` (UTF-8), `uuid.v4()`, `date.format(iso|ms, format, tz?)` and `date.parse(text, format, tz?)` (invariant culture, IANA/Windows zone ids), `path.join/basename/dirname/extname/normalize` (pure string logic, `/`).
  - **`host.<ns>.<fn>(args)`**: exists only for the functions in `allow`; each call is one `call` round trip; a host error is a JS `Error` (catchable); `args` and the reply are JSON values.
  - **`nextStep(definition)`**: queues a step `{ type, id, ...properties }` (same shape as YAML v2). On success the host builds each one with `IStepRegistry.Create(tag, config, services)`, names it with `id`, and puts them in the result's `NextSteps` (at most 50; an unknown `type`, a missing `id` or a duplicate `id` is a step error naming the index). The queue is discarded when the script fails.

### 18.3 Host side (framework)

- `IScriptHostFunction { Name, Description, ArgsSchema?, InvokeAsync(JToken args, ScriptCallContext ctx, CancellationToken) }`; `ScriptCallContext { StepName, Context (PipelineContext), Services }`. `IScriptLibrary { Name, Description?, Source }`. Both are DI services (`AddScriptHostFunction`, `AddScriptLibrary`); `IScriptCatalog` (default `ScriptCatalog`) lists them.
- **Default deny.** A host function runs only when it is (1) declared in the step's `allow`, (2) known to the catalog, and (3) granted by the host: `ScriptHostOptions.GrantedFunctions` (empty by default). The step can narrow, never widen; the manifest of a package cannot widen either (it only *requests*, the host decides). Without `ScriptHostOptions` registered nothing is granted.
- Built-ins (registered by `ScriptExtension.Register`, always listed, granted only by the host): `config.get(name)` (reads `ScriptHostOptions.Config`, read-only, string values), `file.readText(path)` and `file.exists(path)` under `ScriptHostOptions.FileRoots` (canonical full path; `..`, reparse points, UNC and device paths refused; text only, `MaxFileBytes` 1 MiB; no roots = every call is an error).
- `ScriptStep` new properties: `uses` (list of library names), `allow` (list of host function names), `maxCalls`, `maxCallKb`. All optional; a step without them behaves exactly as before.
- Static validation (`ScriptChecks`, public): names in `uses` exist in the catalog; names in `allow` exist (error) and are granted (warning); a `host.x.y(` mention of a function not in `allow` is a **warning** (approximate by regex; the hard guarantee is that the worker only defines what `allow` lists); a `require('x')` literal not in `uses` is a warning; `ScriptSyntax.CheckAsync` compiles scripts and libraries through one worker (since 2026-10-23 the same bundle a run compiles, §18.6).

### 18.4 Package format and trust (designer Core)

- Optional package entries **`scripts/<name>.js`** (workflow packages, UTF-8, 5 MiB each; `<name>` = `[A-Za-z0-9_.-]+` without the extension) = the package's libraries. Manifest field **`scripts`**: `[ { "name", "file", "sha256" } ]` written by `PackageArchive.Write` exactly when entries exist (informative; the entries are the authority). Manifest `requires` gains **`hostFunctions`**: `["file.readText", ...]` = what the package **asks** for. A package without libraries and requests is byte-identical to before.
- Host side (never in the zip): `WorkflowDesignerOptions.ScriptFileRoots` (`WorkflowDesigner:ScriptFileRoots`, CLI `--script-root`, repeatable), `ScriptConfig` (the `Script` section of the package `config.json`, string values), and the set of functions the designer is willing to grant at all (`ScriptGrantableFunctions`, default `config.get`, `file.readText`, `file.exists`).
- **Grant = request ∩ grantable ∩ trusted run.** An untrusted run (or a validation) grants nothing; a trusted run grants what the package requests and the host allows. Libraries alone (pure JS in the sandbox) need no trust; a package with `requires.hostFunctions` is `required` (403 `untrusted_package` until confirmed). `TrustDto.scripts` = `{ libraries: [ { name, lineCount, sha256, code } ], hostFunctions: [ { name, description, granted } ] }`; the trust hash adds `|js:` plus each library name and SHA-256 and the sorted requested functions, and is unchanged for a package without them.

### 18.5 Web, CLI, frontend

- `GET /api/script/catalog` → `{ hostFunctions: [ { name, description, argsSchema, grantable } ], libraries: [ { name, description } ] }` (global: built-ins and what the host registered). Since 2026-10-23 (§18.7) the host functions also carry `object` (the methods of the plugins' script objects, always `grantable`) and `libraries` lists the host's and the plugins' libraries with `source`.
- `GET /api/packages/{id}/scripts` → `{ libraries: [ { name, text, sha256 } ], hostFunctions: [string] }`; `PUT /api/packages/{id}/scripts/{name}` `{ text }` (empty text deletes it; 400 `invalid_script_name`); `DELETE /api/packages/{id}/scripts/{name}`; `PUT /api/packages/{id}/script-host` `{ hostFunctions: [string] }` (the request list; unknown names 400 `unknown_host_function`). Editing keeps a trusted package trusted only when it asks for no host function (otherwise the hash changes and trust is asked again).
- `POST /api/packages/{id}/validate` adds the `ScriptChecks`/syntax diagnostics (with `stepId`, `line` and `file` = `scripts/<name>.js` for libraries).
- CLI: `package info` (libraries, requested functions, `--json` `scripts`), `run`/`validate` print one stderr line per library and per granted host function, `--script-root <dir>` (repeatable) on `run`.
- Frontend (Spanish, minimal): «Librerías JS» (list, textarea editor per library, add/delete), «Funciones del host» (checklist from the catalog), `uses`/`allow` of a ScriptStep as name selectors, trust dialog section. The run view shows events of step ids the canvas does not know as children (indented rows) of the step that emitted them.

### 18.6 One compile per run (protocol 3, 2026-10-23)

Decided by the user (BRMS style, do not reopen): at the start of each pipeline execution all the JavaScript of the workflow is joined and compiled once; each step then calls its own function; compiled per execution, never cached between executions.

- **Where the run starts.** Framework contract addition (`AITaskAgent.Abstractions/Abstractions/IPipelineRunHook.cs`): `IPipelineRunHook.RunStartingAsync(steps, context, ct) → IAsyncDisposable?` and `IPipelineRunHookProvider { RunHook }`. `Pipeline.ExecuteAsync` (`AITaskAgent.Core/Execution/PipelineRunHooks.cs`) walks the steps known before the run (top level; Group, Parallel, Loop, declarative Switch cases and default), groups them by the hook they name, calls each hook once with `(path, step)` pairs, and disposes what they return in its `finally` (success, error, cancellation, suspension). A hook that throws fails the run before any step runs. `ScriptStep` implements the provider explicitly (`ScriptRunHook.Instance`); nothing else in the framework knows about scripts.
- **Bundle** (`Protocol/ScriptBundle.cs`, compiled into host and worker): line 1 `globalThis.__bundle = { steps, libs }`, then one line-aligned piece per script (`__bundle.steps["<step path>"] = function (input, context) {<script>\n};`) and per used library (`__bundle.libs["<name>"] = function (module, exports, require) {…}`). The function name is the step's path in the tree (deterministic; `#2` added on a duplicate). V8 positions `workflow-bundle:L:C` are mapped back to `<step id>:<line>:<col>` of the step's own script (columns on line 1 as the old stand-alone wrapper reported them), the inferred `globalThis.__bundle.steps.` prefix and the glue frames are dropped.
- **Protocol 3** (`ScriptProtocol.Version = 3`): host→worker `load` (`pieces` JSON, `heapMb` = the largest of the run, `check`) answered by `loaded` (`ok`, `errors` = `[ { id, error? } ]`, `error`); `run` (`fn`, `name`, `input`, `timeoutMs`, `heapMb`, `stackKb`, `uses`, `allow`, `maxCalls`, `maxCallBytes`); `result` gains `recycle`. The worker serves calls until stdin closes. `mode`/`check`/`scripts`/`modules`/`script` of version 2 are gone.
- **Compile.** `V8Runtime.Compile` of the bundle, once. On a syntax error each piece is compiled alone to report every error under its own id and line. Then the compiled bundle runs once in a throwaway context (10 s cap) and every function's `Function.prototype.toString` must equal the text it was wrapped in, and there must be exactly as many functions as pieces: a script that closes its function early is refused (its id, and the victim's).
- **Call.** Each call: full GC, baseline heap info, soft cap = committed baseline + the step's `maxHeapMb` (ClearScript's monitor counts committed memory, which V8 does not give back at once; an absolute cap tripped on earlier calls and left the runtime blocked, measured), fresh context (`runtime.CreateScriptEngine`), run the compiled bundle and the compiled bootstrap (only the step's `uses` reach `require`), call, check `used - baseline > cap`, cap reset to 0 (which also lifts ClearScript's block after a late violation). Time, heap or engine failures answer `recycle: true` and the host kills that worker; the next call of the run starts a new one from the pieces the host kept.
- **Host.** `ScriptRunSession` (pieces, one `ScriptWorker`, a gate that serializes calls), `ScriptRunSessions` (keyed by the context's `Metadata` instance, which branch clones share; a nested run stacks), `ScriptWorker` (start fail-closed under the job object/watchdog, `load`, `run`, kill). A step not in the run's bundle (a `nextStep` child, a step executed directly) runs alone: a session with a bundle of one, disposed after the call. A worker that cannot be started at run start does not fail the run: each step reports it when it runs, as before.
- **Designer.** `ScriptSyntax.CheckAsync` sends the same pieces with `check: true`, so the validation and the run compile the same text and report the same messages and lines (`ScriptRunBundleTests.Validation_and_run_compile_the_same_bundle_and_report_the_same_error`). `PackageScripts.CheckAsync` is unchanged.
- **Costs and limits.** Parallel branches with scripts are serialized (no JavaScript in parallel); a hang holds the others at most its `timeoutMs` + 2 s. The process memory limit and the `ArrayBuffer` cap are those of the largest `maxHeapMb` of the run. A run that only reaches its scripts in a branch never taken still pays the start-up and the compile.

### 18.7 JavaScript validators and script objects (protocol 4, 2026-10-23)

Decided by the user (do not reopen): validators and result validators can be written in JavaScript, BRMS style, and run in the **existing per-run
worker**, not in the host process. The measurement that decided it (memory `project_script_bundle_per_run_2026_10_23`, ClearScript 7.5.0): V8 in process is
not safe in this server (`new Array(5e8).fill(1.5)` is a fatal OOM that kills the process; `for(;;) JSON.parse(bigText)` ignores `Interrupt()`; a host call
cannot be interrupted inside .NET; `e.hostException`, a delegate's `.Target`, `gc`, `EngineInternal` stay open). ScriptStep is unchanged. JS hooks came in the
next round (§18.8, protocol 5).

- **Declaration.** The existing YAML keys, with the name `js:<library>.<function>` (`validators: [js:orders.checkTotal]`, `resultValidators:
  [js:orders.wellFormed]`). The library is what comes before the last dot (a library name may contain dots); the function is a JavaScript identifier the
  library exports (`exports.checkTotal = function (args) {…}`). Libraries come from the run's `IScriptCatalog`: the host's (`AddScriptLibrary`), the
  package's `scripts/<name>.js` (designer) and each plugin's `js/<name>.js` (below). Framework contract addition (`AITaskAgent.Core/Hooks/ValidatorFactory.cs`):
  `IValidatorFactory { Prefix; CreateValidator(name, StepBase); CreateResultValidator(name, StepBase) }`, registered in DI; `HookBinder` asks the factory whose
  prefix starts a name before the hook registry (a factory's `InvalidOperationException` becomes «Step 'x' (type 'T'): validator 'name': …»). The JavaScript
  extension registers `JsValidatorFactory` (prefix `js:`): a malformed name or a library the catalog does not know is a **build error**; whether the library
  exports the function is checked when the run starts and by the designer.
- **Run.** `JsValidator` (one class for both keys) implements `IPipelineRunHookProvider` with the ScriptStep's hook. Framework change
  (`PipelineRunHooks.StartAsync`, documented in `IPipelineRunHook`): a validator or result validator of a `StepBase` may name a run hook too; the step is passed
  once per hook. `ScriptRunHook` adds the library of every `js:` validator of the known steps, and the libraries it `require`s with a literal name
  (`ScriptLibraryClosure`), to the run's bundle; a run **without ScriptSteps** but with JS validators starts the worker all the same (same lifetime: released at
  the end, also on failure, cancellation or suspension; rebuilt from the kept pieces after a kill). The `load` also asks the worker for the functions those
  libraries export (each library runs once in a throwaway context, 10 s and the run's heap cap, no host function, logs dropped); a validator whose function is
  missing, or whose library throws while loading, fails the run before any step runs: «A JavaScript validator of this run names a function its library does not
  export, so no step was run: step 'p', validator 'js:lib.nope': the library 'lib' has no function 'nope' (it exports: a, b).» A compile error of the library is
  the existing «does not compile» error with the library's own line (`lib:<name>:<line>:<col>`). A validator of a step the run could not see (a `NextSteps`
  child, a validator called outside a run) gets a worker of its own for the call.
- **Protocol 4** (`ScriptProtocol.Version = 4`; host and worker refuse each other otherwise): `load.exports` (JSON array of library names) answered by
  `loaded.exports` = `{ "<lib>": { "functions": [..] } | { "error": ".." } }`; `run.call` = `step` (default) or `validate`, with `fn` = the library, `export` = the
  function and `input` = the payload `{ result, keys, step? }`; the `result.json` of a `validate` call is the envelope `{ type, value?, patch }` (below).
- **The facades** (`ScriptRunner.Bootstrap`, `makeArgs`). The function receives one argument, `args`, a read-only `Proxy` whose members are:
  `args.result` (the result's `Value` as JSON, **read-only**), `args.vars` (the pipeline variables, **read/write**) and, for a step validator only, `args.step`
  (`{ name }`, **read-only**); `Object.keys(args)` is exactly that. This mirrors the C# contract: `ValidateArgs(Step, Result, Context, CancellationToken)` and
  `ResultValidateArgs(Result, Context, CancellationToken)`, neither of which can replace the result (`IStepValidator`/`IResultValidator` return
  `(bool IsValid, string? Error)`), so the result is read-only for both; `Context.Metadata` is writable in C#, so `args.vars` is writable here. Every facade is a
  `Proxy` over plain JSON values of the call's own context (no .NET object is ever handed to V8): property access, nesting, `in`, `Object.keys`, `for…in`,
  `for…of`, spread, `Object.assign`, the array methods and `JSON.stringify` work as on plain data; `Object.getPrototypeOf` gives the context's own
  `Object.prototype`/`Array.prototype` (harmless: the same the script already has). A write, delete, `defineProperty`, `setPrototypeOf` or freeze of a read-only
  facade throws `TypeError: args.result.lines[0].qty is read-only.` (the property's path). `args.vars` reads a variable from the host the first time it is used
  (`get`, only JSON-like values: `JToken`, string, bool, numbers, dates, guids; anything else does not exist for the script); a written value is JSON-cloned (a
  function or `undefined` throws `TypeError … only takes JSON values`).
- **Patch format.** The writes to `args.vars` come back as `[ { "op": "set", "path": [..], "value": .. } | { "op": "delete", "path": [..] } ]` in order;
  `path[0]` is the variable, the rest walks into its JSON value (property names, array indices). Rules that keep it simple and exact: a value is the final state
  of what was written; a write anywhere under an array records the **outermost array whole** (indices move with `push`/`shift`); a write into an object that was
  replaced or deleted after it was read is not recorded (it is no longer reachable). The host (`VariablePatch.Apply`) applies it after the call to
  `PipelineContext.Metadata`: a one-element path sets or removes the variable; a longer one changes the stored `JToken` **in place**. The patch is applied only
  when the function returned a valid shape.
- **Return mapping** (same contract as C#): `true` → valid; `false` → `(false, "JavaScript validator 'js:x.y' rejected the result.")`; a string → valid when it is
  empty or blank, otherwise `(false, text)` (so `return errors.join('; ')` works); `{ isValid: bool, error? }` → `(isValid, error)` (a missing error gets the
  default text). Anything else (`undefined`, a number, a `Promise`: the function is synchronous) → `StepHookException`. A JavaScript exception, a broken limit
  (time, heap: the worker is killed and the next call starts another) or a dead worker → `StepHookException` too: the step ends as a fatal error without retry,
  exactly what an exception of a C# validator does (§14.3). A rejection retries the step like any invalid answer.
- **Environment of the function.** `console.*` and `log` go to the logger of the step's type, prefixed `[js:x.y of <step>]`; `require` (its library's closure),
  the native library and `host.<object>.<method>` of the script objects (below) exist; `input`, `context` and `nextStep` do not. Each call runs in a fresh V8
  context: neither globals nor a library's module state survive from one call to the next.
- **Limits.** `JsValidatorOptions` (DI; `services.ConfigureJsValidators(...)`): `TimeoutMs` 2000, `MaxHeapMb` 64, `MaxStackKb` 512, `MaxCalls` 100, `MaxCallKb`
  1024 per call, with the ScriptStep's ranges and ceilings. The run's worker is sized for the largest heap cap of its steps and validators.
- **Script objects.** Abstractions addition: `IExtensionBuilder.AddScriptObject(name, Type, factory?, description?)`, whose default body registers a
  `RegisteredScriptObject(Name, Type, Factory, Description)` in `builder.Services` (so every builder supports it and a plugin's captured services carry it to the
  engine); typed forms in the extension: `IExtensionBuilder.AddScriptObject<T>(…)` (`ScriptExtensionBuilderExtensions`) and `IServiceCollection.AddScriptObject<T>`.
  `ScriptObjects.Functions` turns each into host functions `<name>.<method>` (`ScriptObjectFunction`, public): the **public instance methods** of the type,
  inherited ones included, in camelCase; not those of `object`, `Dispose`/`DisposeAsync`, property/event accessors, operators, generic methods, methods with
  `ref`/`out`/pointer or delegate parameters or a delegate return; at most one data parameter (the script's argument, `JToken.ToObject`), plus an optional
  `CancellationToken` and `ScriptCallContext` the host fills; `Task`/`ValueTask` results are awaited; an overload (two methods with one script name) is an error.
  No property navigation, no delegates: the call is the existing JSON round trip of `host.ns.fn` (counted calls and bytes). The instance is created once per
  service provider. **Registering the object is its grant**: a `ScriptObjectFunction` needs no `ScriptHostOptions.GrantedFunctions` entry; a ScriptStep still
  lists it in `allow`; a JS validator may call every script object method and nothing else. `ExtensionCapture.ActualInfo` reports them (kind
  `ExtensionKinds.ScriptObject`), so an extension declares them in `Info()` (`new ExtensionEntry(ExtensionKinds.ScriptObject, "inventory", typeof(Inventory).FullName)`)
  to avoid `info-mismatch`.
- **Plugin libraries.** `ScriptLibraryFolder.Load(folder, owner)` reads `<PluginContext.Folder>/js/*.js` (top level, name = file name, `[A-Za-z0-9_.-]`, at most
  1 MiB each, strict UTF-8). For a `.nupkg` plugin shaped as a folder the files ship as `plugin/js/<name>.js`. The designer reads each loaded plugin's folder
  once per loaded version (`Plugins/PluginScripts.cs`) and registers the libraries in every engine collection (`PluginWiring.ApplyContributions`); the files are
  part of the trust hash (folder tree hash; entries of the verified `.nupkg`, a changed extracted file is `content-mismatch` on the next load,
  `PluginNupkgFolderTests.The_js_libraries_of_a_package_land_in_the_plugin_folder_and_a_changed_one_stops_the_reload`); a file changed after approval is not read
  again until the plugin is reloaded (same limit as the executables of §25.4). Kind `ExtensionKinds.ScriptLibrary` names them on the plugin's card; it is the
  host's listing, never part of `Info()`/`ActualInfo`. A library name that two sources share is a catalog error (validation `script_catalog`; at run time the
  catalog cannot be built).
- **Designer.** `PackageScripts.EngineCatalog(options, package)` is the catalog a run gets (built-ins, host, plugins, package). Validation (`PackageScripts.CheckAsync`,
  on the line of the key): `js_validator_name` (malformed), `js_validator_library_unknown`, `js_validator_function` (from the same worker listing a run uses:
  `ScriptLibraryExports.Problem`, so validation and run say the same words), and the existing `script_syntax` for the libraries a run would bundle (a plugin's or
  the host's is reported without a file). `HookChecks` skips `js:` names. `GET /api/packages/{id}/hooks` adds `scriptFunctions: [ { name, library, function, source } ]`
  (every function of every library, `source` = `package` | `plugin:<id>` | `host`) and `scriptProblems`; the inspector's validator lists offer them. `GET
  /api/code/snippets` adds `jsValidator` and `jsResultValidator` (`language: "javascript"`, `members`): an exported function whose header is
  `Engine/JsValidatorDocs.cs`, the single description of the facades (what each member is, read-only or read/write, what to return), also the source of the
  editor's `args.` completion; `ScriptValidatorTests` pins `Object.keys(args)` so the docs and the worker cannot drift. `GET /api/script/catalog` lists the script
  object methods (`object`, `grantable: true`) and the host's and plugins' libraries (`source`). The plugin card lists `scriptObjects` and `scriptLibraries`.
- **Cost (measured 2026-10-23, Debug build of the tests, `ScriptValidatorTests.Measure_the_cost_of_a_validation_with_and_without_the_facades`).** Per
  validation, from the difference between 201 and 1 loop iterations (start-up and compile cancel out), each iteration an echo step plus the JavaScript: **2.2–2.6 ms
  with the facades** against **2.0–2.3 ms for the same check as a ScriptStep over its plain `input`** (no Proxy): the facades add ≈ 0.2–0.3 ms per call over the
  fresh-context cost both pay. The fixed cost of a run with JavaScript is the existing ≈ 0.4 s.

### 18.8 JavaScript hooks (protocol 5, 2026-10-23)

Decided by the user (do not reopen): hooks can be written in JavaScript with the same facades and patch as the validators (§18.7), BRMS style; only what has
behaviour stays out.

- **Declaration.** The existing `hooks:` syntax with the validator's name form: `hooks: { postProcess: js:orders.stamp }` (library = before the last dot).
  Framework addition: `IHookFactory { Prefix; CreateHook(name, StepBase, HookSlot) }` (`AITaskAgent.Core.Hooks`, DI), asked by `HookBinder` before the
  registry; its errors carry the YAML location: «Step 'out' (type 'JsonOut'): hook 'postProcess' (line 7, column 20) 'js:nolib.f': the JavaScript library
  'nolib' is not known to this host. Libraries: lib.». The extension registers `JsHookFactory` (prefix `js:`). A malformed name, an unknown library or a
  C#-only slot is a build error; whether the library exports the function is checked when the run starts («A JavaScript validator or hook of this run names a
  function its library does not export, so no step was run: step 'out', hook 'postProcess' 'js:lib.missing': …») and by the designer with the same words.
- **Which slots.** A slot takes JavaScript when its arguments have a **data view** (`IHookData<TSelf>`, static `DataMembers`) and its delegate has a
  known form: asynchronous, an around slot (after form), `Func<TArgs, Task>` (notification) or `Func<TArgs, Task<T>>` whose args are `IHookOutcome<T>`
  (decision); since 2026-10-24 also synchronous, `Action<TArgs>` or `Func<TArgs, T>` whose args are `IHookOutcome<T>` (transform, see «Synchronous slots»).
  Result (decided by the user on 2026-10-24: every slot but one): `postProcess`, `onFinalize`, `enrichActivityBefore`, `enrichActivityAfter`,
  `enrichStarted`, `enrichCompleted`, `buildUserMessage`, `buildSystemMessage`, `configureRequest`, `parseResponse`, `selectConversation`, `onToolLimit`.
  **C#-only**: `onConversationCreated` only (`JsHookSlots.Unsupported(slot)`: «its arguments (ConversationCreatedArgs) are objects with behaviour»): its
  argument is the `ConversationContext` the step will use, which a hook changes in place through its methods (messages, bookmarks); a JSON copy and a patch
  cannot reproduce that faithfully. Deciding the conversation from JavaScript is `selectConversation`'s job. Checked by
  `ScriptHookTests.Every_slot_but_onConversationCreated_takes_javascript` and `HookDocsTests.Only_onConversationCreated_is_csharp_only_and_says_why`.
- **The data view** (`HookData`, Core; one list per arguments record, the single source of the facades, the stub header and the completion):

  | Args | Members (writable in bold) |
  |---|---|
  | `PostProcessArgs` | `step {name}`, `attempt`, `input`, **`result`** (the value; written back in place with `HookData.WithValue`, which keeps the result's type, error, metrics and validators), `isSuspended` |
  | `FinalizeArgs` | `step {name}`, `result` (null on error), `hasError`, `error` |
  | `BuildMessageArgs` | `step {name}`, `input`, **`message`** |
  | `ParseResponseArgs` | `step {name}`, `response {content, finishReason, model, tokensUsed}`, **`result`** (a written value is built like the built-in parsing: `BaseLlmStep.ResultFromJson`, internal), **`error`** |
  | `ToolLimitArgs` | `step {name}`, `reason`, `message`, `maxToolIterations`, **`finalAnswerPrompt`** (text = `FinalAnswer(text)`, null = `Fail`; through the explicit `IHookOutcome<ToolLimitDecision>`) |
  | `EnrichActivityArgs` (2026-10-24) | `step {name}`, `activity {traceId, spanId, parentSpanId, operationName}` (null without an activity), **`displayName`** (`Activity.DisplayName`; null/empty = the operation name), **`tags`** (`{ name: value }`; a tag whose JSON did not change keeps its original object, a new/changed one is set with `SetTag` as a scalar or as the compact JSON of an array/object, a missing or null one is removed), `input`, `result` (null before execution or on error). Without an activity the writes are ignored. |
  | `EnrichStartedArgs` (2026-10-24) | `step {name}`, `event {eventType, stepName, stepPath, correlationId, timestamp, attemptNumber}`, **`additionalData`** (object or null; an unchanged entry keeps its original object, a null entry is removed), **`suppressFromUser`**, `input`. The event is rebuilt as `BaseEvent with { … }` through the explicit `IHookOutcome<StepStartedEvent>` (a private field, no new public C# member). |
  | `EnrichCompletedArgs` (2026-10-24) | `step {name}`, `event {eventType, stepName, stepPath, correlationId, timestamp, success, durationMs, errorMessage}`, **`additionalData`**, **`suppressFromUser`**, `result` (null on error), `hasError`. Rebuilt with `with` only when the event is the framework's `StepCompletedEvent`; any other `IStepCompletedEvent` is read-only (a write throws «args.additionalData cannot be written: the completed event is a X, which the framework cannot rebuild»), and its `success`/`stepPath` read null. |
  | `ConfigureRequestArgs` (2026-10-24) | `step {name}`, `messages` (the request's conversation as data), `tools` (names), **`options`** (the whole `LlmInferenceSettings.Options`; the written object replaces them), **`model`** (not empty), **`systemPrompt`**, **`temperature`**, **`topP`**, **`maxTokens`** (number or null). Each write builds a new request (`with`; the copy constructor clones the options, so the original is never touched) through the explicit `IHookOutcome<LlmRequest>`. Written back in the view's order, so the typed members win over the same key written in `options`. |
  | `SelectConversationArgs` (2026-10-24) | `step {name}`, `attempt`, `input`, `history` (the shared conversation's messages: `role`, `content`, and when present `name`, `toolCallId`, `toolCalls [{id, name, arguments}]`; `Message.Metadata`/`Native` are not exposed), **`useHistory`** (boolean only). C#: `History` is `IReadOnlyList<Message>` (immutable records), `UseHistory` a public settable `bool`. |
  | `ValidateArgs` / `ResultValidateArgs` | `result`, `step {name}` / `result` (documentation of §18.7's facades; the validator payload is unchanged) |

  Plus `args.vars` for every one. Objects with behaviour (`StepBase`, `Activity`, `CancellationToken`, `IServiceProvider`, `ConversationContext`,
  `LlmRequest`) are never members: only data read from them, and writes applied back by the host.
- **The patch is applied in the view's order** (2026-10-24): `JsHook.Apply` writes the touched members back in the order of `DataMembers`, not of the
  writes (it was the order of the first write before; no existing view had overlapping members, so nothing observable changed).
- **Synchronous slots (2026-10-24).** `enrich*` and `configureRequest` are synchronous delegates; `JsHook.NotifySync` / `TransformSync` call
  `RunBlocking`: the data view is read and the payload built on the caller's thread; only `JsFunctionCall.InvokeAsync` runs on the thread pool
  (`Task.Run`, so no `SynchronizationContext` is captured and nothing it awaits needs the blocked thread; the caller holds no lock: the run session's gate is
  taken and released inside the call); the caller blocks on `Task.Wait` bounded by `SyncWaitMs = JsValidatorOptions.TimeoutMs + 2 s (kill grace) + 10 s
  (SyncSlackMs: queueing behind another serialized call of the run and a respawn)`, with a linked `CancellationTokenSource` that cancels the call at that
  bound (the worker client kills the worker, the next call starts another) and a 1 s backstop after which the call is abandoned. The patch is applied back
  on the caller's thread, so an abandoned call's late answer is never applied. A timeout of the call itself (the worker's `timeoutMs`), a killed or dead
  worker, a JavaScript error or a write that does not fit is a `StepHookException`, handled by the slot's own rule: `StepBase.RunEnrichHook` logs and
  swallows it (the base event is used), `configureRequest` fails the step (fatal, no retry). Cost: the waiting thread is a blocked thread-pool thread
  (bounded). Tests (`ScriptSyncHookTests`, 9): tags/display name applied to a real `Activity` and ids read-only; events rebuilt; request changed with the
  typed members winning over `options`; a runaway hook killed at its 500 ms limit fails in seconds; a worker that blows its heap fails; the bounded wait
  (test seam `JsHook.SyncWaitOverrideMs`) cancels and fails; a call from a thread whose `SynchronizationContext` never runs anything completes (no
  deadlock); in a run, a failing `enrichActivityBefore` is ignored and the next hooks run on a respawned worker (2 workers started, 0 left).
- **`selectConversation` in JavaScript** is an ordinary asynchronous notification-form slot (`Func<TArgs, Task>`): `args.useHistory` is its writable
  member, so `NotifyAsync` + the patch is enough (`ScriptSyncHookTests.A_selectConversation_hook_reads_the_history…`).
- **Run.** `JsHook` (internal) is the target of the slot's delegate (built by reflection with the slot's own delegate type), implements
  `IPipelineRunHookProvider` with the ScriptStep's hook, and `PipelineRunHooks` now also looks at the targets of a step's hooks (`HookSlots.Bound`), so a run
  whose only JavaScript is a hook starts the worker (`ScriptHookTests.A_run_whose_only_javascript_is_a_hook…`). Same `JsValidatorOptions` limits per call,
  same kill and respawn (`ScriptHookTests` runaway test: 2 workers started, the next hook works). Shared code: `JsFunctionCall` (payload `keys`, allow list,
  bridge, bundled session or a worker of its own) for validators and hooks.
- **Protocol 5** (unchanged on 2026-10-24: the synchronous slots and `selectConversation` use the same `hook` call; only the host waits differently). `run.call = hook`, payload `{ fields, writable, keys }`, envelope `{ type, patch, fields }`: `patch` is the `args.vars` patch of §18.7,
  `fields` the patch of the writable members (same format, `path[0]` is the member). The facade (`makeArgs` in `ScriptRunner.Bootstrap`, hook branch): one
  getter per field; a read-only member throws `TypeError: args.input.x is read-only.`; a writable one can be changed inside (`args.result.total = 5`) or
  reassigned (`args.message = '…'`); `delete args.result` throws (assign null). The host applies `fields` to a copy of the values it sent
  (`VariablePatch.Apply` over a dictionary) and writes each touched member back through `HookData.Write`, then applies `patch` to the variables.
- **Return and errors.** The function returns nothing: any other return (`type` other than `undefined`; a `Promise` is named) is a `StepHookException`
  («it returned object; a JavaScript hook returns nothing and changes args.result (or args.vars).»). A JavaScript error, a broken limit, a value that does not
  convert (`args.result = 'x'` on a numeric result) or a dead worker is a `StepHookException` too, handled as an exception of a C# hook of the slot (fatal, no
  retry). The exception names the function (`Hook 'js:bad.throws' of step 'out' failed: …`).
- **Designer.** `PackageScripts.CheckAsync` validates `hooks:` `js:` names with the validators' listing (codes `js_hook_name`, `js_hook_library_unknown`,
  `js_hook_slot` for a C#-only slot, `js_hook_function`; on the line of `hooks:`); `HookChecks` skips them. The slot dropdown offers the
  `scriptFunctions` in JavaScript mode. Tests: `ScriptHookTests` (19), `JsHookDesignerTests` (4).

### Deviations of phase F (2026-10-19)

- **«`proto`» is the protocol version** (study A's wording), not a prototype extension: see objection 42.
- **Library names follow the workspace rule** (`WorkspaceStore.IsValidName`: letters, digits, `.`, `_`, `-`), the one schemas and prompts use, not the narrower text of §18.4; the entry is `scripts/<name>.js` with rank 9 in `PackageArchive.Classify` and a `scripts` folder in `FromDirectory`/`ExtractTo`. The manifest `scripts` list is rewritten by `PackageArchive.Write` (`SyncScriptsField`) only when it changes, so a package without libraries stays byte-identical.
- **`PackageStore.SaveEntries` also copies `manifest.requires` when a `scripts/…` entry is saved**, and `PackageStore.SaveScriptHost` (the request list, `PUT …/script-host`) saves the workflow entry with it: the request travels with the same mechanism as the NuGet lock. `ManifestRequires.IsEmpty` replaces the three ad-hoc «is anything left» checks.
- **Trust.** Libraries alone need no trust (sandboxed JavaScript); a package that asks for host functions is `required`. The hash adds `|js:` plus the sorted libraries (with hashes) and requests **only when the package asks the host**, so a package without a request keeps the hash it had. `TrustDto.scripts` lists the libraries (code and hash) and each request with `granted` (whether this host would grant it to a trusted run).
- **Grant = request ∩ `ScriptGrantableFunctions` ∩ trusted run**, computed in `PackageScripts.For` and registered after the host's own services (`EngineExtras.Scripts`; it replaces a `ScriptHostOptions` the host registered, so a host cannot widen a package by accident). The `Script` section of `config.json` is reserved (`EnvironmentSections.Script`, `{ "Script": { "Config": { "k": "v" } } }`) and feeds `config.get`.
- **Libraries compile at start**, not lazily (objection 49); only their execution is lazy. **`file.exists` is `false`, not an error, outside the roots** (objection 45).
- **`BuiltInHostFunctions` is public** (the designer lists them without building a provider); the shipped Web host registers no library of its own, so `GET /api/script/catalog` answers `libraries: []` today.
- **`uses`/`allow`/`maxCalls`/`maxCallKb`** are properties of `ScriptStep` (catalog hints `x-editor: script-libraries|script-host`); a name in `uses` the catalog does not know and a function in `allow` the host does not know are validation **errors** (`script_library_unknown`, `script_host_function_unknown`); a function not requested by the package or not grantable is a **warning** (`script_host_function_not_granted`) and a failure at run time.
- **Syntax diagnostics** are `WorkflowError`s with code `script_syntax`: a step's carries `stepId`, the YAML line of the failing line (first line of the block plus the line inside the script) and `codeLine`; a library's carries `file = scripts/<name>.js`.
- **Frontend.** `ScriptsProvider`/`useScripts` (state), «Librerías JS» and «Funciones del host» in the workflow settings (`ScriptsSection`), `ScriptNamesField` for `uses`/`allow` (a name the catalog lacks is kept and marked «no disponible»; a function the host would not grant is flagged), a «JavaScript» section in the trust dialog, and a «dinámico» marker for run-table rows whose id is not in the workflow. No browser E2E was run for this phase (the plan asks for it for B, C and G).
- **Measured / counted.** `AITaskAgent.Extensions.Script.Tests` 91/91 (51 before, +40 in `ScriptExtensibilityTests`, one existing test was told about the protocol version, two lines of the «no host globals» theory changed), designer Core 572/574 (+12 `ScriptLibrariesTests`; the 2 failures are the known `ExampleWorkspaceDeterminismTests`), Web 311/311 (+8), CLI 179/179 (+6), Vitest 832/832 (+17), framework `Tests` 388/388 (untouched). Three existing tests used `scripts/x.js` as the example of a hostile entry and now use `scripts/x.exe`.

## 19. CodebaseAnalysis hosted in the designer (contract added 2026-10-19, phase G)

Decisions: study §3, §8 (rows «Office scope», «D-2 rest», «D-4», «D-5», F4, F5/F6 deferred, `FileToolsConfiguration` static, `TemplateLlmStep` untouched), study B (inventory, YAML expressibility), study D3 (global state). **This is a complex test of the extension mechanism, not a replacement:** `Samples/CodebaseAnalysis.Core` and its tests are not modified (its 1533 tests stay as they were); the variant is a new project, `Samples/CodebaseAnalysis.Designer` (folder plugin id `CodebaseAnalysis` since plugins phase 5; it was an extension of that name), that references the sample and adds adapters, tools, hooks and glue. Office/Access ingestion of the analyzed repository is not part of the variant: no step or option of the package enables it (the original `DetectAndIndexGraphStep` still contains it and is run as it is, for repositories with such files; the variant's repositories are code).

### 19.1 The package pipeline (mode A, the factory's `Preparation:Mode = Off`)

`workspace-src/codebase-analysis` (zip `workspace/workflows/codebase-analysis.zip`, no keys, `requires.plugins: [{ id: CodebaseAnalysis }]`, configuration section `Plugins.CodebaseAnalysis`). Step ids are the ones of `AnalysisPipelineFactory` (`AnalysisEventMapper` and the web progress depend on them):

| Order | id | type | What it is |
|---|---|---|---|
| 1 | `Init` | `CaInit` | New: builds `AnalysisPipelineInput` from the run input and the configuration (`AnalysisRunOptions.BuildInput`), seeds the context keys of `AnalysisRunner.CreateContext` plus `KnowledgePaths`, points the file tools at the repository (`DesignerRun.Begin`, what `AnalysisToolRegistration.Register` does for them) |
| 2 | `DetectAndIndexGraphStep` | `CaDetect` | Adapter over the original step |
| 3 | `LanguageEnricherStep` | `CaLanguageEnrich` | Adapter |
| 4 | `DotNetEnricherStep` | `CaDotNetEnrich` | Adapter; builds the original `DotNetEnricherStep` with its `SemanticEnrichmentStep` fallback and the role `Enricher` |
| 5 | `SqlFacetExtractorStep` | `CaFacets` | Adapter |
| 6 | `ParallelReportsExecution` | `ParallelStep`, `failFast: false` | Nine branches `Auditor_<ReportType>`: **plain YAML `LlmStep`** with `profileName` (the role of `AnalysisPipelineInput.GetRoleName`), `userPrompt`/`systemPrompt` (the sample's template names), `useConversationHistory: false`, the seven auditor tools, `maxToolIterations`, and `hooks: { onToolLimit: CodebaseAnalysis.AuditorToolLimit, postProcess: CodebaseAnalysis.ReportPostProcess }` |
| 7 | `ExecutiveSynthesizerStep` | `CaSynthesis` | Adapter that turns the YAML auditors' results into the `SingleReportResult`s the original takes and runs it (the synthesis LLM call stays inside it) |

Not in the package: `RepositoryInventoryStep`/`DomainDiscoveryStep`/`CoveragePlanningStep`/`TimeBudgetStep`/`SharedPassStep` and mode B (`SubsystemInvestigationReportStep`): study B §3.3 says they need F5/F6 or the factory's object graph (`AuditorStepRegistry`). `CaInventory` is registered and tested alone; the others are listed as not hosted (objections 54-58).

### 19.2 What the plugin contributes (plugins phase 5)

No registration code: `plugin.json` (`{ "id": "CodebaseAnalysis", "version": "1.0.0", "contract": 1, "assemblies": ["CodebaseAnalysis.Designer.dll"] }`) names the only assembly the loader scans (§25.2, §25.3), and the engine wires what it finds (§25.9). `CodebaseAnalysis.Core.dll`, the plugin's copy of `FileTools.dll` and the other DLLs of the published folder are private dependencies, never scanned. Contributions of `CodebaseAnalysis.Designer.dll`:

- **Steps**: the seven adapters (`CaAdapterStep<TIn,TOut> : StepBase`, abstract, skipped as `not-concrete`; original created per execution, `MaxRetries = 1`, typed results travel between steps as in the factory), by their `[StepTag]`: `CaInit`, `CaDetect`, `CaLanguageEnrich`, `CaDotNetEnrich`, `CaFacets`, `CaInventory`, `CaSynthesis`.
- **Tools**: `CodebaseAnalysisTools : IToolSource` creates the sample's 14 graph, file and document tools (`graph_map`, `graph_health`, `graph_domain`, `graph_flows`, `graph_search`, `graph_neighbors`, `view_file`, `view_file_outline`, `view_code_item`, `list_dir`, `find_by_name`, `grep_search`, `list_documents`, `read_document`), not the process-wide `AvailableTools.Instance`; listed as `Plugin:CodebaseAnalysis`. The ones named like FileTools win over FileTools with an informative notice (phase 3); the package keeps `FileTools.Enabled: false`.
- **Hooks**: `CodebaseAnalysisHooks`, `public static` methods with `[Hook(Name = …)]`: `CodebaseAnalysis.AuditorToolLimit` (`OnAuditorToolLimit`), `CodebaseAnalysis.VerifierToolLimit` (`OnVerifierToolLimit`), `CodebaseAnalysis.ReportPostProcess` (`PostProcess`); source `plugin:CodebaseAnalysis`. The names are the package's contract and did not change.
- **Templates**: `CodebaseAnalysisTemplates : ITemplateSource` returns `AnalysisTemplateProvider(<plugin folder>/templates)` (else `templates/` beside the host); the engine chains the package's `prompts/` first, so a name only the sample knows is rendered by the sample's provider and the 45 templates and `@[file.md]` includes are used as they are, the input flattened exactly as in the sample.
- **Configuration**: `CodebaseAnalysisConfiguration : IPluginConfiguration` (below).

### 19.3 Configuration and the plugin's folder

- **Configuration (decision D-3; `CodebaseAnalysisConfiguration : IPluginConfiguration` since plugins phase 5):** `Plugins.CodebaseAnalysis` of the package's `config.json` (exposed by the engine as `AITaskAgent:Plugins:CodebaseAnalysis:*`) mirrors the sections of `appsettings.analysis.json`. `RootSections` = `CodebaseAnalysis`, `JavaAnalyzer`, `DotNetEnricher`, `CodebaseMemory`; the engine composes (`PluginWiring.Compose`, §25.9) the plugin's `Defaults` (`CodebaseAnalysis:Preparation:Mode=Off`, `CodebaseAnalysis:SharedPass:Enabled=false`, and `DotNetEnricher:CliPath` = `dotnet-enricher/DotNetEnricher(.exe|.dll)` or `DotNetEnricher(.exe|.dll)` inside the plugin folder when the file is there) < the engine configuration < the four sections lifted to the root, so `AnalysisTimeouts.FromServices`, `PreparationOptions.FromServices`, `JavaAnalyzerOptions.FromConfiguration`… read the same keys as in the sample. `Schema()` is the hand-written schema of the four sections (open objects). Roles are the package's `Roles` (`LlmRoleFactory.RequiredRoles`: `Enricher`, nine auditors, `ExecutiveSynthesizer`, `ReportVerifier`).
- **The plugin's folder:** a plugin is loaded from bytes, so it has no `Assembly.Location`; the folder reaches it as `PluginContext.Folder` (in `Defaults` and in the template source), which replaced `AppContext.SetData("WorkflowDesigner.ExtensionFolder:<Name>")`. Statically referenced (`AddPlugin`, tests) it is the assembly's folder.
- The Java analyzer source (`JavaAnalyzer.java`) is still searched beside the host executable by the original runner, which has no option for it: Java enrichment needs the file copied there (objection 55).

### 19.4 Global state (study D3)

`MaxConcurrentRuns = 1` stays. The plugin keeps the sample's statics but contains them: the file tools read the plugin's own copy of `FileTools.dll` (`FileTools` is not in the shared set, so its `FileToolsConfiguration` is private to the plugin's load context and separate from the one `FileToolsScope.Apply` sets for the designer's own tools); `CaInit` sets it for the run. `DesignerRun` (process-wide, set by `CaInit`) gives the code-item locator the database and repository. `SqliteConnection.ClearAllPools()` is called by the original `DetectAndIndexGraphStep` and stays: with one run at a time it is harmless. Two runs over the same repository share the SQLite DB and `.codebase` by design (D-5).

### 19.5 Hooks

`onToolLimit` replaces `CustomStatelessTemplateLlmStep`'s final-answer attempt (F1) with the phase A hook (second call in the same attempt, `tool_choice: none`; see objections 1-2 of the phase A report). `postProcess` is F3: the report clean-up, critical-findings reconciliation, provenance and the **single write point** of the individual reports, at the end of each auditor, so reports survive a failed synthesis exactly as in the original.

### 19.6 Parity test

`CodebaseAnalysis.Designer.Tests`: the original `AnalysisRunner.RunAsync` (Off) and the package run by the designer's `RunManager`, over the same small repository and the same pre-built enriched graph, with a deterministic LLM double: same step ids and order for the deterministic steps and for the nine auditors, same report files, same report content up to the provenance keys, same calls to the model. What is not equal is listed in «Deviations of phase G».

### Deviations of phase G (2026-10-19)

- **Changes outside the new project** (replaced by plugins, 2026-10-21: `ExtensionHost`, the `AppContext` folder lookup and the `Extension` tool source are gone, see §19.2, §19.3 and §25.9) (all in the designer Core, none in the framework nor in the original sample): `ExtensionHost` publishes each extension's folder with `AppContext.SetData("WorkflowDesigner.ExtensionFolder:<Name>", folder)` (§19.3); `ToolCatalog(loggerFactory, options)` lists the `ITool` services that extensions register (source `Extension`; §19.2, objection 62), because the designer's pre-check rejected a `tools: [...]` name it did not know; `InternalsVisibleTo CodebaseAnalysis.Designer.Tests` in the Core; `AITaskAgent.slnx` has the two new projects (the `.sln` is untouched).
- **Auditors are plain `LlmStep`s as the study asked, the synthesis is not.** The nine `Auditor_*` are YAML `LlmStep`s with the hooks `onToolLimit` and `postProcess`; the synthesis is the adapter `CaSynthesis` around the original step (objection 57). `postProcess` returns the model's result unchanged (the output type of a YAML `LlmStep` is `LlmStringStepResult`, sealed, so it cannot be a `SingleReportResult`); the processed report goes to disk and `CaSynthesis` reads it back.
- **`failFast: false` replaces `TolerantReportsParallelStep`**, which kept the aggregate error when every branch failed; `CaSynthesis` restores that rule (it fails with the reasons when no auditor produced a report).
- **Roles, prompts and limits are the package's, not the factory's:** the nine role names and the template prefixes are written in the YAML from `AnalysisPipelineInput.GetRoleName/GetTemplatePrefix`; a role's own `SystemPrompt`, `MaxToolIterations` per role and the `CodebaseAnalysis:Timeouts` reach only what the inner steps read themselves (objection 59).
- **What is the same, measured by `CodebaseAnalysis.Designer.Tests`** (the original `AnalysisRunner.RunAsync` with `Preparation:Mode = Off` against the package run by `RunManager`, same repository, same enriched graph, deterministic double): the same ten report files with the same content up to the provenance keys (`generated_at`, `model`, `provider`, `system_sha`, `prompt_sha`, `graph`, `app`), the same step ids in the same order (`Init` is the designer's addition), the nine `Auditor_*` names `AnalysisEventMapper` parses, ten report-written notifications, the same progress lines of the deterministic steps (the designer repeats none and adds none; the original's `[LLM]` per-call lines are missing, as listed), and the same number of model calls (nine auditors and the synthesis; the enricher is not called because the graph is already enriched). **Parallelism is 1 in the comparison** because of objection 54.
- **Tests:** `CodebaseAnalysis.Designer.Tests` 6/6 (stable in 7 consecutive runs after fixing the parallelism); designer Core 574/576 (the 2 known `ExampleWorkspaceDeterminismTests` failures caused by the user's `advanced-story.zip`; +2 passing tests for the new example), Web 311/311, CLI 179/179, the original `CodebaseAnalysis.Core.Tests` 1533/1533 (unchanged). Vitest and the framework `Tests` were not run: neither the frontend nor the framework changed.
- **Browser check (phase G, before plugins; the folder is now `plugins/CodebaseAnalysis` and needs approval) (Playwright, own host on 5191, scratch workspace with `extensions/CodebaseAnalysis` from `dotnet publish`, the model double registered by a scratch host through `ConfigureEngineServices`): 20/20.** The extension is loaded from the workspace folder (7 steps, no problems, configuration schema published); the canvas shows the package; the inspector of an auditor shows its two hooks; `GET /api/hooks` lists the three extension hooks; after the trust dialog (names the extension, its folder, its hash and the steps that use it) **Validar** compiles the package; the run ends `run.completed` with the ids of the factory, the nine auditors, ten `analysis.report_written` events and ten report files with `analysis-meta` and provenance (`model=fake-model`, `prompt_sha=…`); `GET /packages/{id}/tools` lists the extension's tools; the extension panel shows it. On the first run the editor also logged one `403 untrusted_package` (objection 60). The check ran with `maxDegreeOfParallelism: 1` in the scratch copy of the package (objection 54).
- **Not done:** the hosted preparation, mode B, shared pass, notes, verification and regeneration (objection 56); Java enrichment without copying `JavaAnalyzer.java` (55); a real LLM, `cbm` indexing and the `DotNetEnricher` CLI were not run (the graph was prepared; no process, no network, no cost); `CaInventory` is only tested alone.

### Import a configuration from another package (2026-10-19, after phase G)

The user asked to reuse providers and roles of another zip without typing them again. What already existed: «Guardar como configuración…» (package → library, keys kept encrypted or stripped) and «Aplicar configuración…» (library → open package, `replace` or `merge`, `POST /library/configs/{n}/apply`), plus `POST /packages { fromConfig }`. What was missing was the direct path from **another package of the workspace**, without the library in between. Added:

- **Core:** `LibraryStore.ApplyConfigFromPackage(sourcePackageId, session, mode = Merge)` reads the saved `config.json` of the other package (`PackageStore.ReadSaved`) and `LibraryStore.ApplyConfigObject(JsonObject, session, mode)` is the body shared with `ApplyConfig` (same merge/replace rules, same `reenter` and warnings; nothing is written to disk until «Guardar»). The same package as source is `InvalidPackageException("same-package")`.
- **Web:** `POST /api/library/configs/from-package { package, fromPackage, mode? }` → `ApplyConfigResultDto` as apply (mode default `merge`; 400 for a missing field or a wrong mode; 404 for an unknown source; the target must be open, `package_not_open` otherwise).
- **CLI:** `library config import --source <other.zip> --package <package.zip> [--mode merge|replace] --workspace <dir>`; exit code 2 for usage errors.
- **Frontend:** «Aplicar configuración…» has a new first choice «De dónde viene»: «Biblioteca» (as before) or «Otro paquete» (select of the other saved packages of the workspace; `combinar` by default); `api.importConfigFromPackage`, mock `pkgs.savedContent`.
- **Keys:** copied as stored (`${VAR}` as text, `enc:v2:` values unchanged, never plaintext, nothing printed). Encrypted values open with the same master password; those that do not decrypt come back in `reenter`; they are re-sealed to the package's salt at the next save (as with apply).
- **Merge rules (unchanged):** providers, roles, fallbacks and MCP servers the package lacks are added; same name = the package's is kept and listed in `conflicts`; `Storage`, `FileTools`, `Timeouts` and `DefaultRole` are copied only if the package has none. Consequence for the CodebaseAnalysis package: its roles keep pointing to `OpenAiBase`; after importing another provider, change the `Provider` of the roles (or name the imported provider `OpenAiBase`).
- **Tests:** Web `Import_from_another_package_merges_its_saved_configuration_without_the_library`, CLI `Config_import_takes_the_configuration_of_another_package_zip_without_the_library_and_never_prints_keys`, Vitest «Otro paquete importa la configuración…».
- **Java analyzer, objection 55 (workaround, original sample untouched):** `WorkflowDesignerWeb.csproj` and `WorkflowDesigner.Cli.csproj` copy `CodebaseAnalysis.Core/Enrichment/Java/JavaAnalyzer.java` to `<output>/Enrichment/Java/` on every build (the original runner looks for it at `AppContext.BaseDirectory/Enrichment/Java/JavaAnalyzer.java`). A `dotnet run` build of the Web project therefore finds it.


## 20. Import by sections and the application's NuGet settings (contract added 2026-10-19)

Two requests of the user after phase G: (1) import only some parts of another configuration (for example only the providers or only the roles) with a form of checkboxes; (2) a screen to configure where NuGet packages are downloaded from and where they are cached, as BRMS did.

### 20.1 Importing only some sections of a configuration

- **Section ids** (`ConfigSections`, `Samples/WorkflowDesigner.Core/Library/ConfigSections.cs`): `providers` (`Providers` and `FallbackProviders`), `roles` (`Roles` and `DefaultRole`), `mcp`, `timeouts`, `storage`, `filetools`, and `plugins:<id>` (a plugin's own section under `Plugins`, §25.9; it was `extensions:<Name>`, still accepted as `plugins:<Name>`). `Describe(config)` lists only the sections the configuration has, with `{ id, label, count, names }`; `Normalize(ids)` lower-cases, removes repeats and throws `ArgumentException` for an unknown id or an empty list. `Secrets` (the verifier) and `Script` are not importable on their own.
- **Core:** `LibraryStore.ApplyConfig`, `ApplyConfigFromPackage` and `ApplyConfigObject` take `IEnumerable<string>? sections = null`. **`null` = the whole configuration, exactly the behaviour before sections existed.** With sections the package's configuration is the base and only the selected sections change: `merge` uses the existing merge rules over the selected sections (plus plugin sections: added when missing, `conflicts` entry of kind `plugin` (before: `extension`) when a different one exists); `replace` takes the source's value for each selected section the source has and leaves the rest of the package alone (so «Reemplazar» with a subset is not «make the package equal to the source»; with every section checked the UI sends no `sections`, which is the old, whole replacement). `ConfigApplyResult.sections` echoes what was applied (null = all).
- **Roles without their providers:** when roles are imported, a warning (not an error) names the providers they point to that the package does not have after the import («Los roles importados apuntan a proveedores que el paquete no tiene: …»). Only roles that were added or replaced are checked.
- **Keys:** nothing changes: `enc:v2:` values travel as stored, never in plaintext; the `reenter` list and the warnings of the existing apply cover them. Only the slots of imported providers and MCP servers are checked.
- **API:** `sections: [...]` in the bodies of `POST /api/library/configs/from-package` and `POST /api/library/configs/{name}/apply`; unknown or empty (`[]`) → 400 and nothing changes. Preview for the form: `GET /api/library/configs/{name}/sections` and `GET /api/library/configs/from-package/{id}/sections` → `[{ id, label, count, names }]` (404 for an unknown item or package).
- **CLI:** `library config import … --sections providers,roles,plugins:Name`, `library config apply … --sections …`, and `library config import --source <zip> --list-sections` (prints `id<TAB>count<TAB>names`, needs no `--package`). Exit code 2 for an unknown section.
- **Frontend:** «Aplicar configuración…» shows a «Qué importar» group of checkboxes (all checked at first, «Todo»/«Nada», the names of the items under each) for both origins; with none checked it cannot continue; the confirmation lists the chosen sections; the result lists plugin conflicts. A server without the preview route imports everything as before. Mock: `describeSections`, `applyConfig(..., sections)`.

### 20.2 NuGet sources and cache folder of the application

- **What exists since phase D (§16.4):** the sources were `WorkflowDesigner:NuGetSources`, `--nuget-source`, `WORKFLOW_DESIGNER_NUGET_SOURCES` and a plain list `nugetSources` of `settings.json`; the cache was `<data>/nuget` or `--nuget-cache`. There was no screen, and the CLI did not read `settings.json`.
- **Settings file** (`%LOCALAPPDATA%\WorkflowDesigner\settings.json`, `UserSettings`): new `nuget: { sources: [{ name, source, enabled }], cacheDirectory }`. The old `nugetSources` list is still read (each entry is an enabled source named after itself) and is folded into `nuget.sources` the first time the screen saves. Disabled entries stay in the file and are not used.
- **Precedence:** sources keep the union order host (`WorkflowDesigner:NuGetSources`, `--nuget-source`) → environment variable → enabled entries of the settings file. The cache folder is the first of: `WorkflowDesigner:NuGetCacheDirectory` (new key) or `--nuget-cache` → `nuget.cacheDirectory` of the settings file → `<data>/nuget` (the CLI's default when it has no workspace: `%LOCALAPPDATA%\WorkflowDesigner\data\nuget`).
- **CLI:** reads the same file (`CliSettings`: `WORKFLOW_DESIGNER_SETTINGS` or the default path; it never writes it). Tests point `CliSettings.PathOverride` at a file of their own so a developer's settings never leak in.
- **Validation** (`NugetUserConfig.Validate`, every problem is returned at once): a name and a source are required, names (case-insensitive) and sources are unique, only absolute folders or `https://` URLs, **no `http://`, no user, password or query in a URL (feeds with authentication are not supported: no credential is stored anywhere)**, the cache folder must be absolute.
- **API** (`Api/NugetSettingsEndpoints.cs`; it replaces the old `GET /api/nuget/sources`, whose `sources` field is kept): `GET /api/nuget/sources` → `{ sources, entries: [{ name, source, enabled, origin: settings|appsettings|env, editable }], cacheDirectory, cacheOrigin: appsettings|settings|default, cacheSetting, settingsFile }`; `PUT /api/nuget/sources { sources: [{ name, source, enabled }], cacheDirectory }` → the same DTO (400 `invalid_nuget_settings` with `problems[]`; `settings_not_writable`); `POST /api/nuget/sources/test { source }` → `{ ok, kind: folder|url, message, packages }` (a folder must exist and reports its `.nupkg` count; an `https` URL gets one GET with a 5 s timeout, no credentials, nothing written); `GET /api/nuget/cache` → `{ directory, exists, files, packages, bytes }` of the cache in use.
- **Applied live:** saving writes `settings.json` and updates the options of the open workspace (`WorkspaceHost.Options`, which the next workspace opened clones), so a restore uses the new sources and cache at once. Plugins already loaded keep the closure they read at load time; the NuGet settings of the plugin service are read when the workspace's service is created (a new workspace or a restart).
- **Frontend:** «Ajustes» button in the header → dialog «Ajustes de la aplicación» → «Orígenes NuGet»: the sources of the host or the environment read-only with their origin, the editable ones (active checkbox, name, folder or URL, «Probar», «Quitar»), «Añadir origen», «Carpeta de caché» (read-only when the host fixes it), «Ver tamaño», «Guardar ajustes» and the problems of the server.
- **Limits:** no authenticated feeds; no `nuget.config` is read; the URL probe makes an outbound request to what the user typed (the backend is loopback-only and cross-site guarded); changing the cache folder does not move what was already downloaded.

## 21. Step packages loaded at run time (`NugetStepLoader`, 2026-10-20) — superseded by §25 (2026-10-21)

**Superseded by §25 (2026-10-21).** `NugetStepLoader` became `PluginLoader` (§25.7) with two origins (NuGet and folder); `steps-packages.json` became `plugins.json` (§25.6); the discovery of «every public concrete `IStep`» became the metadata scan of §25.3 (tag = `[StepTag]` or `Type.FullName`, no `tagged:false`, no assembly loaded before approval); `StepsChanged` is `PluginsChanged`. The names are in the table of §17.2. The API listing, the file formats, the skip reasons and the «Deviations and decisions» of the first version were removed: §25.5-§25.7 and `Deviations of plugins phase 1` replace them. `AITaskAgent.NugetLoader/README.md` is the usage guide.

What of the 2026-10-20 design survives unchanged in the NuGet origin of `PluginLoader.InstallAsync(id, version)` and is not repeated in §25:

1. The id and the version (a version or a range, never floating) are checked as in §16.1 (`NugetRequirement.Problem`) → `invalid-package`, nothing written.
2. A lock made for exactly this request (`requiresHash`) is reused and only materialized (offline works); failing that, when the installed version satisfies the request its own lock is reused, so the exact version shown by a pending request approves a package installed with a range; otherwise `NugetResolver.RestoreAsync` resolves it (needs `AllowNetwork` and a source). The cached `.nupkg` files are checked against the lock (`hash-mismatch`) and every extracted DLL and native library must be byte-identical to a file of its `.nupkg` (`content-mismatch`).
3. For the same version with a different closure, the new lock is written only after approval, so a refused install never leaves the installed plugin with a lock its approval does not cover. If another version of the plugin is loaded in this process, the new one is approved, recorded and reported `restart-required`.
4. Trust is asked unless `plugins.json` already approved this exact version, `.nupkg` hash and closure hash; not approved → nothing loaded, nothing recorded. Approved but nothing loadable (`no-assemblies`, `load-failed`) → not recorded.
5. `LoadInstalledAsync` never reads a source: lock → `.nupkg` hash → cache → content check → trust (asked only when the approved hashes differ, `Changed`) → load. A reload that changes nothing writes nothing; a failed load keeps the `steps` of the last one that worked. `RemoveAsync` removes the entry and its locks, keeps the cache, and the types stay registered until the restart.

## 22. Step packages in the designer (backend, 2026-10-20) — superseded by §25 (2026-10-21)

**Superseded by §25 (2026-10-21).** `StepPackageService` and `ExtensionHost` were replaced by `PluginService` (§25.9: operations, DTOs, engine wiring, static plugins); the REST routes `GET`/`POST`/`DELETE /api/step-packages` and `GET /api/extensions` by `/api/plugins…` (§25.10); `ApprovalTrustPolicy` works unchanged on the new request fields (§25.9). Renames: §17.2. The service signature, the route table, the response examples, the error list and the «Deviations and decisions» of 2026-10-20 were removed. What still holds and §25 does not say in one place:

- **`StepCatalog` is swapped whole.** It keeps one immutable state (`CatalogDto`, tag → `StepTypeInfo`, revision) behind a volatile reference; `Refresh()` rebuilds it under a lock with the logic of its creation and swaps it, so the singletons that hold the catalog (`YamlReader`, `PackageStore`, `LibraryStore`, `WorkflowValidator`, `RunManager`, `PackageTrust`, the endpoints) see it without changing reference. `PluginsChanged` calls it (§25.9). `Snapshot()` returns a catalog frozen on the current state: `YamlReader.Read`, `WorkflowValidator` and the preparation of a run take one at the start, so an operation never mixes two states. Registries never remove a tag, so a refreshed catalog only adds. A registered step type that cannot be described is left out and listed in `CatalogDto.pluginProblems` with code `step-describe-failed` (tag as `step`, assembly as `folder`).
- **Each workspace has its own plugin service and install folder** (`<data>/plugins`), created over its options; opening another workspace creates a new service and loads that workspace's plugins before building its container. The types of the previous workspace stay in the process (contexts are not collectible) but are in neither the registry, the catalog nor the runs of the new one; reopening a workspace loads its plugins again in new contexts (memory grows with each reopening). The local repository `<data>/nuget-repo` is per workspace too.
- **Plugin types reach every engine collection** (catalog, validation, run, hook and tool listings) after `ConfigureEngineServices`; a tag the collection already has keeps its type. A dedicated internal `options.Plugins`, reset by `CloneFor`, is used instead of chaining `ConfigureEngineServices` because `WorkspaceHost` clones the options in use and a chained delegate would carry one workspace's plugins into the next.
- **The two steps of the approval** (`ApprovalTrustPolicy`): the first call is refused and the request is remembered as shown (listed for 30 minutes); the second approves only when it carries the `Sha512` (and optionally the closure digest) of a request this backend showed less than 30 minutes ago, once. A hash learnt elsewhere is refused and shown. A plugin whose content changed since it was approved is refused at start-up and listed as pending. **What it protects against:** installs nobody meant and cross-site requests (`CrossSiteGuard`, loopback only). It does not prove that a person is in front of the screen: the API has no authentication and `CrossSiteGuard` lets through requests without `Sec-Fetch-Site`, so any process of the same user can make the 202 and then the 200. Such a process can already run code as that user.
- **Installing while a run is in progress is allowed**: a run uses the collection built when it started and the catalog only gains tags.
- **Limits**: the shown requests live in memory (a restarted backend shows the request again: 409 `trust_stale`, then 200); the NuGet settings are read when the workspace's service is created; no sandbox: an approved plugin runs in the host process with its permissions; the two-step approval does not authenticate the user.

## 23. Step packages in the CLI (2026-10-20) — superseded by §25.11 (2026-10-21)

**Superseded by §25 (2026-10-21).** `steps install|list|remove` and `extension list|info|verify|restore` are the single `plugin` group of §25.11 (`plugin install|list|info|trust|remove|rescan|verify|repo …`); `--extensions` is `--plugins`; the exit code **7** («needs approval: nothing was loaded or recorded; repeat with `--trust`») is kept, and 0/1/2 mean what they meant. Renames: §17.2. The command table and the deviations of 2026-10-20 were removed. What §25.11 does not repeat and still holds:

- A CLI command is one process: «install» means «recorded for the next command» and «restart» means «the next command». Nothing is loaded live.
- `run`, `resume`, `session turn`, `validate`, `list-steps` and `package info` create the plugin service with `NeverTrust` before the catalog is built and load what is recorded from the cache (no network, no sources); a plugin that changed is reported on stderr (`warning: plugin not loaded (<code>)`, with the approving command) and not loaded, and the workflow fails on the unknown step tag. `list-steps --strict` exits 1 on such problems. `--trust` exists only on `plugin install`.
- `--trust` approves exactly the plugin being installed (`InstallTrust`, armed only while that install runs), never `AlwaysTrust`.
- `<data>` is `--data`, else `<workspace>/data` with `--workspace`, else `%LOCALAPPDATA%\WorkflowDesigner\data`; `validate`, `list-steps` and `package info` take `--data` too. Commands that only read leave the install and repository folders byte-for-byte as they were (a test checks it). `session` and `resume` have no `--nuget-cache`: they use `<data>/nuget`.
- Two CLI processes over one data folder keep each other's entries (`plugins.json.lock`); two installs of the same plugin at the same time end with the last write. There is no sandbox.

## 24. «Paquetes instalados» → «Plugins instalados»: one place for plugins (2026-10-20, rewritten 2026-10-21)

Problem it solved: the two global registries (extensions and NuGet step packages) were tabs of the per-package Environment view, which says «Abre un paquete» when no package is open. The header view `#/installed` needs no open package; with plugins it is one list (phase 4, «Deviations of plugins phase 4»): `features/plugins` (`PluginsView`, `PluginCard`, `PluginTrustDialog`, `PluginLoadResult`, `AddPluginButton`/`AddPluginDialog`, `RepositoryList`, `WhereInstalled`) and `state/plugins.tsx`; the route and the header button stay, now «Plugins instalados (N ⚠)» (⚠: a plugin in error, pending or needing a restart, or a registry problem that belongs to no plugin).

- Each plugin is a card with an origin badge (Carpeta / NuGet), its state (`loaded`, `pending-trust`, `error`, `restart-required`), folder, hash, the steps with their tag, tools, hooks, templates and configuration (schema with secret fields flagged), warnings and notes; install by id/version, «Añadir plugin…» (`.nupkg` files or a folder, §25.8, §25.10), approve (the two-step trust dialog lists hashes, assemblies and contributions) and remove. «Dónde se instalan» shows the roots, install, cache and repository folders from `GET /api/plugins`, and the `dotnet publish` command of the CodebaseAnalysis plugin.
- The palette marks steps of a plugin with a «Plugin» badge (the id as tooltip) and the inspector links the step to the view; the old «Extensión» / «NuGet» badges are gone.
- Data comes from `GET /api/plugins` (§25.10) and `CatalogDto.plugins` / `StepTypeDto.plugin`; the view edits no plugin configuration.
- **Not done**: the class name of a step is not shown (`StepTypeDto` has no type name); there are no per-package plugins (a plugin is global to the application, per workspace folder).

## 25. Plugins: unified loading (contract)

Phase 1 of the unification of «extension» and «NuGet step package» into one concept, **plugin** (2026-10-21; decisions: the plugin spec of
that day and memory `project_unify_step_packages_2026_10_21`). This section is what the framework and `AITaskAgent.NugetLoader` implement
now, and what the designer (service §25.9, REST §25.10, CLI §25.11, frontend §24) runs on; §17 and §21-§24 are the former model, reduced to a note
and the table of renames (§17.2). **Since 2026-10-22 a plugin is driven by one class that implements `IExtension`** (decision of the user, after type
discovery alone gave too many problems): its `Register` registers everything the plugin needs and its second method `Info()` declares what it is going to
register (§25.3a). The same `IExtension` is the framework's own DI composition (Llm, Script, FileTools, Mcp, Office, CSharp). Type discovery (§25.3) stays as
the trust preview and as the fallback for a plugin that has no `IExtension`.

### 25.1 Contracts in the framework

| Contract | Assembly / namespace | Shape |
|---|---|---|
| `[Hook(Name?, Description?)]`, `[Validator(Description?)]`, `[ResultValidator(Description?)]`, `[ResponseType(name?)]` | `AITaskAgent.Abstractions`, `AITaskAgent.Core.Hooks` (moved from `AITaskAgent.Extensions.CSharp`) | On `public static` methods / public classes. Hook name = `Name` or `Class.Method` (nested `Outer.Inner.Method`, no namespace); validators always `Class.Method`. The C# unit honours `Name` too. |
| `IExtension` (+ `Info()`, `ConfigurationDefaults(PluginContext)`, `RootSections`) | `AITaskAgent.Abstractions`, `AITaskAgent.Core.Abstractions` | `string Name`; `void Register(IExtensionBuilder)`; `Type? ConfigurationType` and `JsonSchema? ConfigurationModel()` (default: `TypeSchemaBuilder` over the type); **new** default members: `ExtensionInfo Info()` (empty by default), `IReadOnlyDictionary<string,string?> ConfigurationDefaults(PluginContext)` and `IReadOnlyList<string> RootSections` (what `IPluginConfiguration.Defaults/RootSections` give; same key rules). For a plugin, one public concrete class with a public parameterless constructor. |
| `ExtensionInfo`, `ExtensionEntry`, `ExtensionKinds` | `AITaskAgent.Abstractions` | Plain data: `ExtensionInfo.Entries: [ExtensionEntry(Kind, Name, Type?, Description?)]`, kinds `step`, `tool`, `toolSource`, `hook`, `validator`, `resultValidator`, `responseType`, `templateSource`, `configuration`, `rootSection`, and (2026-10-23, §18.7) `scriptObject` (named, reported by `ActualInfo`) and `scriptLibrary` (named; the host's listing of a plugin's `js/`, never in `ActualInfo`). `ExtensionInfo.Difference(declared, actual)` compares by name for named kinds and by class for the rest. No `ITool` here: classes are named by `FullName`. |
| `IExtensionBuilder` (new members) | same | `Plugin` (`PluginContext?`: id, version, folder while a host runs it; null in a plain collection), `AddHook(name, kind, MethodInfo, description?)`, `AddHooksFromAssembly(Assembly)`, `AddResponseType(name, Type)`, `AddResponseTypesFromAssembly(Assembly)`, `AddToolType(Type)`, `AddToolInstance(object)`, `AddToolSourceInstance(object)`, `AddTemplateSourceInstance(object)`; default bodies throw `NotSupportedException` (a builder that cannot wire something says so). Typed forms in `AITaskAgent.Extensions.Llm` (`ExtensionBuilderToolExtensions`: `AddTool<T>()`, `AddTool(ITool)`, `AddToolSource(IToolSource)`, `AddTemplates(ITemplateSource)`, `AddToolsFromAssembly`, `AddTemplatesFromAssembly`, `AddAllFromAssembly`). **2026-10-23 (§18.7):** `AddScriptObject(name, Type, factory?, description?)`, whose default body adds a `RegisteredScriptObject` to `Services` (every builder supports it); typed form `AddScriptObject<T>` in `AITaskAgent.Extensions.Script`. |
| `ExtensionDiscovery` / `AssemblyDiscovery` | Abstractions / `AITaskAgent.Extensions.Llm` | The reusable discovery helpers: `ExtensionDiscovery.TaggedSteps/Hooks/ResponseTypes/Describe(assembly)` (what Abstractions sees), `AssemblyDiscovery.Describe(assembly)` (adds tools, tool sources and template sources). An extension that registers with `AddAllFromAssembly` can return `AssemblyDiscovery.Describe(assembly)` from `Info()`; one that registers by hand writes its `ExtensionInfo` by hand. |
| `ExtensionCapture` | `AITaskAgent.Abstractions` | An `IExtensionBuilder` that registers nowhere and keeps what `Register` asked for (`CapturedSteps`, `Hooks`, `ResponseTypes`, `ToolTypes`, `ToolInstances`, `ToolSources`, `TemplateSources`, `Services`); `ActualInfo(extension)` reports it as an `ExtensionInfo`. The loader and the host-referenced plugins use it. |
| `IPluginConfiguration` | `AITaskAgent.Abstractions`, `AITaskAgent.Core.Abstractions` | **Fallback since 2026-10-22**: only for a plugin without an `IExtension` (an extension-driven plugin declares its configuration through the members above, which lose nothing: schema, defaults, root sections and `x-secret` marking are the same code paths). `Type? OptionsType`; `JsonSchema? Schema()` (default: `TypeSchemaBuilder.Build(OptionsType, configuration: true)`); `IReadOnlyDictionary<string,string?> Defaults(PluginContext)` (default empty; keys relative to the plugin's section, or to the root for a key of a root section); `IReadOnlyList<string> RootSections` (default empty). At most one per plugin, public parameterless constructor. `PluginConfigurationRules.Problem(section)`: a root section is a top-level name and never `AITaskAgent`. |
| `PluginContext(Id, Version, Folder, IConfiguration Section)` | `AITaskAgent.Abstractions`, `AITaskAgent.Core.Abstractions` | Replaces `AppContext.SetData("WorkflowDesigner.ExtensionFolder:<Name>")`. `Folder`: the folder plugin, the extracted `plugin/` folder of a NuGet plugin shaped as a folder (§25.2), or the extracted `lib` folder of another NuGet plugin. |
| `IToolSource { IEnumerable<ITool> Create(PluginContext) }` | `AITaskAgent.Extensions.Llm`, `AITaskAgent.LLM.Tools.Abstractions` | Tools with arguments or dependencies. A public concrete `ITool` with a public parameterless constructor is discovered on its own. |
| `ITemplateSource { ITemplateProvider? Create(PluginContext) }` | `AITaskAgent.Core`, `AITaskAgent.Support.Template` | Chained after the workflow package's own `prompts/`. |

`AITaskAgent.Abstractions` now references `Microsoft.Extensions.Configuration.Abstractions` (for `PluginContext.Section`). The C# unit's
cache key changed (`csu2`), so every cached unit compiles once again.

### 25.2 Plugin, manifest, own assemblies

- **NuGet plugin**: a package resolved by `NugetResolver` (§16) into the private cache; its nuspec is the manifest; its **own assemblies** are
  the direct package's `lib/<nearest tfm>` DLLs (never its dependencies).
- **NuGet plugin shaped as a folder** (2026-10-22): a package with the entry `plugin/plugin.json`. The resolver extracts every `plugin/**` entry
  with its relative path to `<cache>/packages/<id>/<version>/content/plugin` (entries that would leave it are refused, the extraction caps apply);
  the loader then treats that folder as a folder plugin's: `plugin.json` (`assemblies`, or the folder rules below) names the own assemblies,
  `PluginContext.Folder` is that folder (content files and private executables are there), and the other DLLs at its top level are private
  dependencies beside the closure of the nuspec's `dependencies` (downloaded from the configured sources like any NuGet dependency; the host's
  shared set is not). The manifest's `id` must be the package id (`id-mismatch`); the nuspec's version wins over the manifest's. Every file of
  `plugin/` is checked against the verified `.nupkg` (a file added, missing or changed in the cache is `content-mismatch`) and its SHA-512 is
  what the load context accepts. A `*.deps.json` should not be shipped there (its packages would not be beside the main assembly).
  `Samples/CodebaseAnalysis.Designer/pack-plugin.ps1` builds such a package.
- **Folder plugin**: a folder used in place (nothing is copied), usually a `dotnet publish` output. Optional `plugin.json`:
  `{ "id": "CodebaseAnalysis", "version": "1.0.0"?, "description": "…"?, "contract": 1, "assemblies": ["CodebaseAnalysis.Designer.dll"]? }`.
  `id` (a NuGet-style id) and an integer `contract` ≥ 1 are required; `contract` above `PluginFolder.SupportedContract` (1) is
  `contract-too-new`; `assemblies` are file names inside the folder (no `..`, no rooted path, `.dll`). Without `plugin.json`, a legacy
  `extension.json` is read as one (`name` → `id`, `assembly` → `assemblies`, warning `legacy-manifest`; its `nuget` section is ignored:
  a folder ships its dependencies). Without either: the own assembly is `<folder name>.dll`, else the only DLL at the top (several DLLs:
  `no-assemblies`); the id is the caller's (`InstallFolderAsync(folder, id)`) or the folder name; contract 0. A manifest id that is not the
  caller's id is `id-mismatch`. Version: the manifest's, else `[AssemblyInformationalVersion]` (without `+metadata`) or the assembly version
  of the first own assembly (read from metadata), else `0.0.0`, normalized. UNC and device paths (`\\…`, `\\?\…`) are refused (`invalid-plugin`).
- Only the own assemblies are scanned for contributions (a published folder carries `FileTools.dll` and others whose types must not appear
  as the plugin's).
- **Entry point** (2026-10-22): the one public concrete `IExtension` of the own assemblies, or the class `plugin.json` names with `"extension": "Full.Type.Name"`
  (a legacy `extension.json` names it with `extensionType`). The manifest `contract` stays 1: `extension` is an optional addition. Several `IExtension` classes
  and no `extension` is the stopping problem `multiple-extensions`; a name that is not one of them, `extension-not-found`; a plugin whose only `IExtension`
  cannot be created (not public, abstract, generic, no public parameterless constructor), `invalid-extension`. All three are found from metadata, before trust.

### 25.3 Discovery without side effects

`PluginScanner.Scan(ownAssemblies, dependencies)` reads **metadata only** (`System.Reflection.Metadata` over the bytes): no `Assembly.Load`
of the plugin, no load context, no attribute constructor, before approval (a test compares `AssemblyLoadContext.All` and the loaded
assemblies before and after). Base types and interfaces are followed through the plugin's own and dependency files (metadata) and, for
shared assemblies (`SharedAssemblySet.HostWins`), through the host's copy; a type whose base or interface is in an assembly nobody has is
reported (`type-load-failed`) and left out. Result `PluginContributions` (JSON camelCase, part of the trust request):

| Field | What |
|---|---|
| `steps: [{ tag, type, tagged }]` | Public, concrete, non-generic `IStep`; `tag` = `[StepTag]` or `Type.FullName` (`tagged: false`); never dropped. Only a concrete, non-generic class that is not public goes to `LoadReport.Skipped` (`not-public`); abstract classes, interfaces and open generics are bases, never reported. |
| `tools: [{ type }]` | Public concrete `ITool` with a public parameterless constructor. |
| `toolSources`, `templateSources: [{ type }]`; `configuration: { type } \| null` | `IToolSource`, `ITemplateSource`, `IPluginConfiguration`: public, concrete, parameterless constructor (else `invalid-contribution`, ignored); two configurations = `multiple-configurations`. |
| `hooks: [{ name, kind: Hook\|StepValidator\|ResultValidator, type, method, description }]` | `public static`, non-generic methods of public classes (else `invalid-hook`, ignored). |
| `responseTypes: [{ name, type }]` | `[ResponseType]` public, non-abstract, non-generic classes (else `invalid-response-type`). |
| `extensions: [type]`, `extension: { type } \| null` | Every public concrete `IExtension` with a public parameterless constructor, and the entry point chosen among them (§25.2, §25.3a); an `IExtension` that cannot be created next to a valid one is ignored with the warning `ignored-extension`. |
| `assemblies: [file]` | The own assemblies scanned. |

A tag, hook name or response type name declared twice inside the plugin is `collision`.

**This scan is the trust preview** (2026-10-22): it is what the user sees and approves. For a plugin that has an `IExtension` it is a prediction, not the
registration: what `Register` really registers is only known after approval (§25.3a).

### 25.3a The entry point: `IExtension`, `Info()` and `info-mismatch` (2026-10-22)

**Decision (the user's, not reopened):** a plugin is driven by a class that implements `IExtension` and registers everything it needs (steps, tools, hooks,
validators, response types, templates, configuration); `IExtension.Info()` returns what it is going to register, by discovery or by hand; `[StepTag]`,
`[Hook]`, `[Validator]`, `[ResultValidator]` and `[ResponseType]` stay so that `AddStepsFromAssembly`, `AddHooksFromAssembly` and
`AddResponseTypesFromAssembly` register by scan without names. The qualified tool names (§25.9) and the read-only plugin prompts (§25.10) stay.

**Security (decided by the assistant, delegated; the objection was told to the user): nothing of the plugin runs before approval.**
- The trust dialog and `TrustRequest.Contributions` show **what the metadata scan (§25.3) sees** plus the **entry point**: `contributions.extension.type`, found by
  metadata too. That list is a preview; it is not what `Register` will do.
- `Info()`, the constructor and `Register` run **after** approval, inside the plugin's load context (`PluginExtensionRunner.Run`, called by
  `PluginLoader.LoadCore`). A test compiles a plugin whose constructor, `Info()` and `Register` write marker files and asserts that none exists, no
  assembly of the plugin is loaded and `AssemblyLoadContext.All` is unchanged after a refused install, and that the three exist after an approved one
  (`PluginExtensionTests.Nothing_of_the_plugin_runs_before_approval_and_the_extension_runs_only_after`).
- A refused plugin after approval (collision, `Accept`) has already run its constructor, `Info()` and `Register` (known limit, as for attribute and tool
  constructors in §25.5).

**`Info()` is the plugin's declaration and is not trusted.** After loading, the loader runs `Register` against an `ExtensionCapture` (nothing is registered
anywhere; the step registry it reads is the loader's) and compares `Info()` with what was really registered (`ExtensionCapture.ActualInfo`, which adds
`ConfigurationType` and `RootSections`): `ExtensionInfo.Difference` → problem **`info-mismatch`** (a warning; the message names the items declared and not
registered, and registered and not declared). **The real registration is what the designer uses.** If `Info()` throws: `info-failed` (warning) and the
declaration is taken as empty. `LoadedPlugin.Extension` (`PluginExtension`) keeps the instance, `Declared`, `Registered`, `Missing`, `Extra` and the
instances `Register` handed over (tool instances, tool sources, template sources, the services it added).

**Selection and failure.** Exactly one `IExtension` in the own assemblies drives the plugin; more than one is the stopping problem `multiple-extensions`,
decided from metadata **before trust**; `plugin.json` may name the class (`"extension": "Full.Type.Name"`; `extension-not-found` when it is not there).
If the constructor or `Register` throws, or registers a hook or response type name twice, or a step tag twice: `extension-failed`, nothing of the plugin is
registered and it is not recorded. **A plugin with no `IExtension` keeps loading by the type discovery of §25.3** (everything that worked before still does).
For an extension-driven plugin the lists in `LoadedPlugin` (`Steps`, `Tools`, `ToolSources`, `Hooks`, `ResponseTypes`, `TemplateSources`) are the real
registration; `Configuration` is null (the extension declares it). Collisions (§25.5) are checked before trust on the scan's preview and again, after
loading, on the real registration.

**What `Register` may do and what the host does with it.** `builder.Plugin` gives the id, the exact version and the folder (its `Section` is empty: the
engine composes the real one later, per engine). Steps go to the loader's registry after the collision checks; hooks, response types, template sources and
tool sources/instances are returned in `LoadedPlugin` and wired per engine collection by `PluginWiring` (§25.9): a tool source is created once and its
`Create(PluginContext)` is called per engine, a tool class is created per engine, a tool instance is shared. `builder.Services` are the plugin's own
service descriptors, copied into each engine collection as they are (an instance is shared; do not register the framework's services there).
**Configuration:** `ConfigurationType` / `ConfigurationModel()` (schema; `x-secret` is marked from `[PasswordPropertyText]`/`[DataType(Password)]` as before),
`ConfigurationDefaults(PluginContext)` and `RootSections` replace `IPluginConfiguration`; nothing it gave is lost, so a plugin that has an extension
ignores any `IPluginConfiguration` class it also contains.

### 25.4 Trust

Nothing is loaded before `ITrustPolicy.ApproveAsync(TrustRequest)` approves, unless `plugins.json` already approved the same content.
`TrustRequest(Id, Version, Sha512, Dependencies, Assemblies, Kind) { NativeLibraries, Origin, Folder, Contributions }`:

- NuGet: `Sha512` of the locked `.nupkg`, `Dependencies` the closure with hashes, `Assemblies` every managed DLL of the closure (and
  every DLL of a `plugin/` folder, as `plugin/<relative path>`), `NativeLibraries` the `runtimes/<rid>/native` files; approval also remembers the closure hash (`approvedClosure`).
- Folder: `Sha512` = **tree hash** (`PluginFolder.Hash`): lines `<relative path with />\t<lowercase hex SHA-512 of the file>\n` for **every**
  file below the folder (links not followed, at most `MaxFolderFiles` = 20 000), sorted ordinally, SHA-512 of the UTF-8 text as
  `sha512-<base64>`; `Assemblies` every `*.dll` of the tree; `Dependencies` empty. Any added, removed, renamed or changed file asks again
  (`Changed`). The own assemblies are loaded from bytes checked against the tree; the folder's load context loads a private dependency only
  when it is a file of the approved tree whose bytes still have the approved hash (otherwise not loaded / `FileLoadException`).
- `Kind`: `Install`, `Update` (another version), `Changed` (same version, other content). `AlwaysTrust`, `NeverTrust` as before; the
  designer's two-step `ApprovalTrustPolicy` works unchanged on the new fields.
- **After approval** (audit fixes): the load context re-checks what it reads later, for both origins. NuGet: the SHA-512 of every assembly
  and native library the cache check read (`ContentProblem`) is kept; a dependency is loaded from bytes with that hash and a native library
  is hashed through a handle that denies writes and loaded before it is closed; a file not in the approved set is not loaded from the
  context, a changed one is a `FileLoadException` whose `Data["plugin-problem"]` is `content-changed` (the loader turns it into the stopping
  problem `content-changed` while it loads). Folder: the same with the tree's hashes (native libraries included). **Limit**: executables a
  plugin starts by itself (CodebaseAnalysis's `dotnet-enricher/`) are part of the tree hash at approval time but nothing re-checks them when the
  plugin runs them.

### 25.5 Load, collisions, restart

- One non-collectible `IsolatedLoadContext` per plugin, named **`plugin:<nuget|folder>:<id>/<version>`** (`PluginIdentity.ContextName`;
  `PluginIdentity.Of(type)` reads it back), host wins by `SharedAssemblySet`.
- **All or nothing**: a step tag, hook name or response type name that another owner already has (the host's registry, or another plugin of
  the loader) stops the plugin whole with `collision`, whose message names both owners (`plugin 'B' 1.0.0 (folder)`, `plugin 'A' 2.0.0
  (nuget)` or `the host (<assembly>)`). Checked from metadata **before trust** (nothing is asked or loaded) and again after loading; then
  `PluginLoaderOptions.Accept(LoadedPlugin)` lets the host refuse it too (tool names: the loader never instantiates a tool). A refused plugin
  registers nothing and is not recorded (its assemblies stay loaded in their context: contexts are not collectible).
- `restart-required`: another version (or other content) of a loaded plugin is approved and recorded for the next start; tags owned by
  another version of the same id are not a collision. Removing a loaded plugin needs a restart. Such an update goes through the same rules
  as a first install against the **other** owners (audit fixes): step tags, hook names and response types of other plugins stop it before
  trust (`collision`), and after approval it is loaded into a load context of its own and offered to `Accept` (tool names) without
  registering anything (that context stays in the process); a refused update is not recorded. The step tags of an update recorded for the
  next start are taken too: another plugin that declares one is a `collision` now («recorded to load at the next start»), not at that start.
- After approval each step type is described once (`IStepRegistry.Describe`); one that throws is skipped (`type-load-failed`), the rest load.
  Contributions are resolved by metadata token in the loaded assemblies (the same bytes that were scanned). Steps go to the loader's
  `IStepRegistry`; everything else is returned in `LoadedPlugin` for the host (nothing instantiated): `Tools`, `ToolSources`, `Hooks`
  (`PluginHook(Name, Kind, MethodInfo, Description)`, `CreateDelegate()`), `ResponseTypes`, `TemplateSources`, `Configuration`.
  `PluginRegistration.AddHooks(HookRegistry, LoadedPlugin)` registers the hooks with source **`plugin:<id>`**.
- **Extension-driven plugins** (§25.3a): the lists above are filled from what `Register` registered into the capturing builder, not from the scan; the
  tag/hook/response collision check, the `Describe` of each step and `Accept` run on that real registration (a probe of an update runs `Register` too, in its
  own load context). `LoadedPlugin.Extension` carries the instance and the declaration comparison.
- States for the service (phase 2), from a `LoadReport`: `Plugin != null` → `loaded`; problem `not-approved` → `pending-trust`;
  `RestartRequired` → `restart-required`; any other stopping code → `error(code)`.

### 25.6 `plugins.json`

`<InstallDirectory>/plugins.json` (every write atomic, under `plugins.json.lock`, re-read before applying the change; a reload that changes
nothing writes nothing; an unreadable file is copied to `plugins.json.corrupt-<utc>` and reported `store-corrupt`):

```json
{ "version": 1, "plugins": [
  { "id": "Acme.Steps", "origin": "nuget", "version": "1.2.0", "requested": "[1.0,2.0)", "sha512": "sha512-…", "approvedSha512": "sha512-…",
    "approvedClosure": "<sha256 hex>", "installedAtUtc": "…", "steps": ["AcmeTag"] },
  { "id": "CodebaseAnalysis", "origin": "folder", "version": "1.0.0", "folder": "C:\\…\\CodebaseAnalysis", "sha512": "sha512-<tree>",
    "approvedSha512": "sha512-<tree>", "description": "…", "installedAtUtc": "…", "steps": ["…"] } ] }
```

Locks of NuGet plugins: `<InstallDirectory>/plugin-locks/<id lower>/<version lower>/nuget.lock.json` (§16.2 format). **Migration**: when
`plugins.json` does not exist and `<LegacyStepPackagesDirectory or InstallDirectory>/steps-packages.json` does, it is converted once
(origin `nuget`), its locks are copied from `<legacy>/<id>/<version>/` to `plugin-locks/`, and the old file is renamed
`steps-packages.json.migrated`; `PluginLoader.StoreNotes` says so. An id is unique across origins (`duplicate-id`).

### 25.7 Public API of `AITaskAgent.NugetLoader`

- `PluginLoader(PluginLoaderOptions options, IStepRegistry registry, ITrustPolicy trust, ILogger? logger = null)`:
  `Task<LoadReport> InstallAsync(string id, string version, CancellationToken)` (NuGet), `Task<LoadReport> InstallFolderAsync(string folder,
  string? id = null, CancellationToken)`, `Task<IReadOnlyList<LoadReport>> LoadInstalledAsync(CancellationToken)`, `Task<RemoveResult>
  RemoveAsync(string id, CancellationToken)`, `static FolderInspection InspectFolder(string folder, string? id = null, int maxFiles = 20000)`,
  `LoadProblem? ContentConflict(string id, string version, string sha512)` (audit fixes: `package-conflict` when the entry, lock or cache has other bytes);
  `Installed: IReadOnlyList<InstalledPlugin>`, `Loaded: IReadOnlyList<LoadedPlugin>`, `StoreProblems`, `StoreNotes`, `Options`,
  `RegistryPath`; `event EventHandler<LoadReport> PluginsChanged`.
- `PluginLoaderOptions { Sources, CacheDirectory (required), InstallDirectory (required), LegacyStepPackagesDirectory?, AllowNetwork = false,
  AllowRemote = true, SourcesOptionName?, TargetFramework = "net10.0", MaxFolderFiles = 20000, MaxExtractedEntryBytes = 256 MiB, MaxExtractedPackageBytes = 1 GiB, Accept? }`.
- `LoadReport { Id, Version, Origin, Folder?, Approved, Recorded, Registered: [RegisteredStep(Tag, Type, Tagged)], Skipped: [SkippedStep(Type,
  Reason, Message)], Problems: [LoadProblem(Code, Message)], Notes, Contributions?, Plugin?: LoadedPlugin, RestartRequired }`.
- `InstalledPlugin(Id, Version, Sha512, ApprovedSha512, InstalledAtUtc, Steps) { Origin, Folder?, Requested?, Description? }`;
  `RemoveResult(Id, Removed, RestartRequired, Message)`; `LoadedPlugin(Id, Version, Origin, Folder, Assemblies, Steps, Tools, ToolSources,
  Hooks, ResponseTypes, TemplateSources, Configuration) { Description, HookSource }`; `PluginIdentity(Origin, Id, Version)` with
  `ContextName`, `Of(Type|Assembly)`, `Parse`; `PluginOrigin { Nuget, Folder }` (JSON `nuget`/`folder`).
- `PluginScanner.Scan(own, dependencies) → PluginScanResult(Contributions, Problems)`, `PluginScanner.AssemblyVersionOf(path)`;
  `PluginFolder.ReadManifest(folder, id?) → (PluginManifest?, Problems)`, `PluginFolder.Hash(folder, maxFiles) → PluginFolderTree(Folder,
  Sha512, Files) { HashOf(path) }`, `PluginFolder.IsNetworkPath`.
- `LocalNupkgRepository(string folder, NupkgLimits? limits = null, string targetFramework = "net10.0")`: `Task<NupkgAddResult>
  AddAsync(Stream)`, `AddFileAsync(path)`, `Task<IReadOnlyList<NupkgFolderItem>> AddFolderAsync(folder)`, `NupkgListing List()`,
  `bool Remove(id, version)`, `static NupkgInspection Inspect(path, limits?, tfm)`, `static string FileNameOf(id, version)`, `Folder`.
  `NupkgInspection(Id, Version, Sha512, Size, Dependencies: [NupkgDependency(Id, Range, AvailableIn?: "local-repository" | "host")], Path)`, `NupkgAddResult(Package,
  AlreadyPresent)`, `NupkgException.Code`: `invalid_nupkg`, `package_conflict`, `too_large`, `invalid_path`. `NupkgLimits { MaxBytes = 100 MiB,
  MaxNuspecBytes = 1 MiB, MaxEntries = 20000, MaxFilesPerFolder = 200 }`.
- Additive (2026-10-22, entry point): `PluginScanner.Scan(own, dependencies, extensionType?)`; `PluginContributions.Extensions` / `Extension` (replace
  `LegacyExtensions`); `PluginManifest.Extension`; `LoadedPlugin.Extension: PluginExtension?(Instance, TypeName, Declared, Registered, Missing, Extra,
  ToolInstances, ToolSourceInstances, TemplateSourceInstances, Services) { Matches }`; `PluginExtensionRunner.Run(IExtension, PluginContext, IStepRegistry?,
  List<LoadProblem>, Action<Exception>?, ILogger?) → PluginExtensionRun?(Extension, Capture, Contributions)` (also used by hosts that reference a plugin
  assembly). Codes: stopping `multiple-extensions`, `extension-not-found`, `invalid-extension`, `extension-failed`; warnings `info-mismatch`, `info-failed`,
  `ignored-extension` (`legacy-extension` is gone).
- Additive (2026-10-22): `NugetMaterializedPackage.PluginDirectory` (the extracted `plugin/` of a package with `plugin/plugin.json`, else null),
  `NugetResolver.PluginEntryPath(root, relative)` (where an entry below `plugin/` is extracted; null when it would leave `root`),
  `PluginLoader.NotFoundHint` (appended to `nuget-not-found` while installing).
- `NugetOptions` gains `AllowRemote = true` (with `AllowNetwork`, folder sources are usable and `http(s)` ones only when `AllowRemote`; never
  makes a source usable when `AllowNetwork` is false; only remote sources with it off = `offline` «remote sources are off») and the extraction
  caps `MaxExtractedEntryBytes = 256 MiB`, `MaxExtractedPackageBytes = 1 GiB`, `MaxPackageEntries = 20000` (bytes really written are counted;
  past a cap `NugetException.Code = "too_large"`, loader `nuget-too-large`, nothing extracted).

### 25.8 Local repository of `.nupkg` files

Validation before anything is copied: size (streamed to a temporary file of the repository, cut past `MaxBytes`), `PK\x03\x04` signature,
entry count, exactly one `.nuspec` at the root, the nuspec decompressed with a byte cap and parsed with `DtdProcessing.Prohibit` (a DTD is
`invalid_nupkg`), id and version from `PackageArchiveReader.GetIdentity()` (never the client's file name), id checked with
`NugetRequirement.Problem`, destination `id.version.nupkg` lower case with the normalized version and checked to be inside the folder,
moved atomically. Same id, version and bytes = `AlreadyPresent` (idempotent); other bytes = `package_conflict` (replacing a version is not
supported). Dependencies of the nearest framework group are reported with `availableIn: "local-repository"` when the repository has a
satisfying version, and `"host"` when the host supplies that package (`SharedAssemblySet.HostProvides`: never downloaded; 2026-10-22). Adding registers and loads nothing; the loader installs from the folder as from any source (offline with
`AllowRemote = false`); a dependency no source has fails with `nuget-not-found` naming it.

### 25.9 The designer's plugin service (phase 2, `WorkflowDesigner.Core.Plugins`)

One mutable service per workspace options object replaces `ExtensionHost` (§17) and `StepPackageService` (§22). Web, CLI and frontend
(phases 3-4) are written against this.

**Creation and hosting.** `PluginService.Create(WorkflowDesignerOptions options, ITrustPolicy trust, ILogger? logger = null)` attaches the
service to the options (internal `options.Plugins`, reset by `CloneFor`; a second call throws); `PluginService.Of(options)` returns it or null
(no service = no plugins). It seeds its own `StepRegistry` with the steps of an engine collection of those options, so a plugin tag that the
engine or the host has is a `collision` naming `the host`. `services.AddPlugins(options, trust, logger?)` + `await provider.LoadPluginsAsync()`
for a fixed host; `WorkspaceHost.PluginTrust` (`Func<WorkflowDesignerOptions, ITrustPolicy>`) and `PluginLogger` for a host that switches
workspaces (`OpenAsync` creates the service and awaits `LoadAsync` before building the container). The `StepCatalog` singleton attaches
itself (`PluginsChanged` → `Refresh()`).

**Folders and sources.** `LoaderOptions` = `PluginLoaderOptions { InstallDirectory = <data>/plugins, LegacyStepPackagesDirectory =
<data>/step-packages (its steps-packages.json is migrated once, §25.6), CacheDirectory = the designer's NuGet cache, Sources = [<data>/nuget-repo,
.. options.NuGetSources], AllowNetwork = true, AllowRemote = any https source, Accept = the tool-name rule }`. The local repository is a source
of the plugins only: `options.NuGetSources` (used by the package `requires.nuget` of §16) never gets it. `Offline` = no remote source.
Folder roots (`Roots: [PluginRootDto { path, origin: explicit|workspace|install, exists }]`, in this order): `options.PluginDirectories`
(`WorkflowDesigner:PluginDirectories`, `--plugins`, `WORKFLOW_DESIGNER_PLUGINS` through `PluginDirectoryConfig.Resolve`, UNC and device
paths left out), then, unless `options.AutoDiscoverPlugins = false`, `<Workspace>/plugins`, `<Workspace>/extensions` (the legacy root, phase 3; scope
`workspace`, `extension.json` is read as `plugin.json` with warning `legacy-manifest`) and `<install dir>/plugins`. A root is itself a
plugin when it has `plugin.json`/`extension.json`, else each subfolder with a manifest or a DLL is one.

**Operations** (problems are reported, never thrown, except cancellation):
- `Task<IReadOnlyList<LoadReport>> LoadAsync(ct)`: reloads `plugins.json` (folders and cache only, never a source) then `RescanAsync`.
- `Task<IReadOnlyList<LoadReport>> RescanAsync(ct)`: every folder of the roots that `plugins.json` does not record goes through the trust
  policy (`ApprovalTrustPolicy`/`NeverTrust`: a pending request read from metadata, nothing loaded; `AlwaysTrust`: loaded live).
- `Task<PluginInstallResult> InstallAsync(id, version, PluginApproval? approval = null, ct)` (NuGet) and
  `InstallFolderAsync(folder, id? = null, PluginApproval? approval = null, ct)` (approve or install a folder) → `PluginInstallResult(LoadReport
  Report, PendingTrust? Pending, bool Installed)`; `Installed` = approved and `Recorded`. Two steps exactly as the approval of §22: `PluginApproval(Sha512,
  Closure?)`, `PluginTrust.ClosureOf(TrustRequest)`, `PluginTrust.CurrentApproval`, `ApprovalTrustPolicy(TimeProvider?)` (30 min,
  `ShownLifetime`; the shown key now includes the origin). For a folder `Sha512` is the tree hash.
- `Task<RemoveResult> RemoveAsync(id, ct)`; `Task<PluginListDto> ListAsync(ct)` / `PluginListDto Describe()`; `DescribeLoaded()`;
  `FindLoaded(id)`, `FindInstalled(id)`, `ScopeOf(folder)`; `Installed`, `Loaded`, `StoreProblems`, `Revision`, `ResponseTypeNames`;
  `event EventHandler<LoadReport> PluginsChanged`; `Attach(StepCatalog)`.
- Local repository (`Repository`: the loader's `LocalNupkgRepository` over `<data>/nuget-repo`; uploading registers and loads nothing):
  `Task<AddNupkgResultDto> AddNupkgAsync(Stream, ct)`, `AddNupkgFileAsync(path, ct)`, `Task<IReadOnlyList<AddNupkgFolderItemDto>>
  AddNupkgFolderAsync(folder, ct)`, `NupkgRepositoryDto ListRepository()`, `bool RemoveFromRepository(id, version)`; errors are
  `NupkgException.Code` (`invalid_nupkg`, `package_conflict`, `too_large`, `invalid_path`) and `ArgumentException` for an invalid id/version.
- States (`PluginStates`): `loaded`; `pending-trust` (problem `not-approved`); `restart-required` (another version or content recorded, or
  removed while loaded); `error` (the first stopping problem, or `not-loaded` for a recorded plugin this process never loaded). Warning codes
  (the plugin loads): `info-mismatch`, `info-failed`, `ignored-extension`, `legacy-manifest`, `invalid-hook`, `invalid-response-type`, `invalid-contribution`, `type-load-failed`.

**DTOs (JSON camelCase).**
- `PluginListDto { plugins: [PluginDto], problems: [PluginProblemDto], pending: [TrustRequestDto], roots: [PluginRootDto], installDirectory,
  cacheDirectory, repositoryDirectory, sources: [string] (repository first), offline, restartRequired }`. `plugins` = the recorded ones plus
  folders found by a rescan (or installs) that are not recorded (pending or failed).
- `PluginDto { id, version, origin: nuget|folder, state, scope: explicit|workspace|install|null, folder?, sha512?, installedAtUtc?, description?,
  error?: { code, message }, loadedVersion?, steps: [{ tag, type }], tools: [name] (loaded only), hooks: [{ name, kind: hook|validator|resultValidator,
  description? }], responseTypes: [name], hasTemplates, configurationSchema? (loaded only; secret fields carry "x-secret": true),
  rootSections: [name], warnings: [{ code, message }], notes: [string], extension?: { type, declared: [{ kind, name, type?, description? }], registered: [...],
  missing: [...], extra: [...] } }`. `extension` is the plugin's `IExtension`: before approval only `type` (read from metadata; `declared`/`registered` empty,
  because nothing of the plugin has run); once loaded, what `Info()` declared, what `Register` registered and the difference (also as the warning `info-mismatch`).
  Absent for a plugin without an `IExtension`. The plugins view shows the entry point and an «Info() y registro» section; `TrustContributionsDto` has
  `extensions` and `extension` instead of `legacyExtensions`.
- `PluginProblemDto { id?, version?, code, message, folder?, step? }` (also `step-describe-failed` from the catalog: `step` = tag, `folder` =
  assembly). `TrustRequestDto { id, version, sha512, closure, dependencies: [{ id, version, sha512 }], assemblies, kind: install|update|changed,
  askedAtUtc, origin, folder?, contributions? (the loader's PluginContributions, §25.3), nativeLibraries? }`.
- `LoadReportDto { id, version, origin, folder?, approved, recorded, registered: [{ tag, type }], skipped: [{ type, reason, message }],
  problems: [{ code, message }], notes, restartRequired }` (no `tagged`).
- `AddNupkgResultDto { id, version, sha512, size, alreadyPresent, dependencies: [{ id, range, availableIn? }] }`,
  `AddNupkgFolderItemDto { file, result?, error? }`, `NupkgRepositoryDto { directory, packages: [{ id, version, sha512, size, dependencies }],
  problems }`.
- Catalog: `CatalogDto { steps, plugins: [PluginDto] (loaded ones), llm, timeouts, pluginProblems: [PluginProblemDto] }` (`extensions` and
  `extensionProblems` are gone, built-in IExtensions are not listed); `StepTypeDto.plugin: { id, version, origin }` (absent for the engine's
  and the host's steps; `extension`, `nuget`, `tagged` are gone). Trust: `TrustDto.plugins: [PluginTrustItem { id, version, origin, scope?,
  folder?, sha512?, steps: [stepId] }]` (Web, `PluginTrustItem.From(PluginUse)`).

**Engine wiring** (`EngineServices.Create`, every collection: catalog, validation, run, hooks, tools). After `AddAITaskAgentYaml` and before
the designer's tool set and `ConfigureEngineServices`: configuration composed by the engine (`PluginWiring.Compose`: each plugin's
`Defaults(ctx)` (keys relative to `AITaskAgent:Plugins:<id>`, or to the root for a key of one of its root sections) < the collection's
configuration (with `Plugins` of `config.json` as `AITaskAgent:Plugins:<id>`) < each plugin's `RootSections` lifted from
`AITaskAgent:Plugins:<id>:<section>` to `<section>`; a root section `PluginConfigurationRules.Problem` rejects (`AITaskAgent`, a path) is
ignored and logged); hooks on the collection's `HookRegistry` with source `plugin:<id>` (a name taken is logged and skipped); templates:
`ChainedTemplateProvider` = the package's `prompts/` first, then each plugin's `ITemplateSource` (loader order: by id); tools: one instance of
each `ITool`/`IToolSource` tool built with the collection's composed configuration, registered before the tool set so a step resolves the
plugin's instance; response types: `PluginSchemaSet` read by `WorkflowCompiler.LoadSchemasAsync` (a name the package already has is an
error), and `ResponseTypeNames` valid in `PreCheck`/`HookChecks`. After `ConfigureEngineServices`: the plugins' step types (a tag the
collection has keeps its type). **Source of truth (2026-10-22):** `PluginParts.From(LoadedPlugin)` reads the real registration of the plugin's extension
(`LoadedPlugin.Extension`): tool classes (created per engine), tool instances, `IToolSource` instances (their `Create(PluginContext)` per engine, with the
`Plugin:<id>:<name>` aliases and the `tool_ambiguous` rules unchanged), template-source instances, hooks, response types, the services the extension added,
and its configuration through `ExtensionConfiguration` (an `IPluginConfiguration` over the extension's `ConfigurationModel()`, `ConfigurationDefaults`,
`RootSections`). A plugin without an extension is wired from its discovered types as before.

**Qualified tool names (2026-10-22).** Framework: `ToolAlias(Name, Tool)` (`AITaskAgent.LLM.Tools.Abstractions`) is a service that gives a tool
another lookup key; `YamlLlmStep.ResolveTools` adds every `ToolAlias` after the `ITool` services and the `ITools` registry with `TryAdd`, so a bare name
always has priority. Designer: `PluginWiring` registers `Plugin:<id>:<name>` next to each plugin tool instance; `EngineToolSet.Register` registers
`FileTools:<name>`, `Mcp:<server>:<name>` and `Host:<name>` (the host's real instance) for every tool, **including the ones `Deduplicate` dropped**
(`EngineToolSet.Shadowed`). `EngineToolSet.Names` (what `CheckToolNames` compares against) holds the winners' bare names plus the qualified name of
every tool; `Ambiguous` is keyed by bare name, so a qualified name is never `tool_ambiguous`. `ToolInfoDto.qualifiedName` is always filled and the shadowed
tools are extra listing entries named by their qualified name.

**Tools.** `ToolCatalog` lists, in order, the plugins' tools (`Plugin:<id>`) and the host's `ITool` services (`Host`), FileTools, MCP;
first name wins. (The qualified-names paragraph above applies to every case below.) A plugin tool with the name of a FileTools or MCP tool is listed as the plugin's, the listing has a problem,
and `EngineToolSet.Ambiguous` makes a step whose `tools:` names it a validation error with code **`tool_ambiguous`** (the run refuses as for
any validation error). **Phase 3 changes the FileTools half:** a plugin tool with the name of a FileTools tool wins with an informative problem only
(not ambiguous); the MCP half is as described. Between two plugins the later one is refused whole by `Accept` (`collision`, naming both). The set no longer
registers copies of plugin/host tools in the run's collection (only FileTools and MCP).

**Static plugins.** `services.AddPlugin(Assembly assembly, string id, string? folder = null)` (namespace `WorkflowDesigner.Core.Plugins`,
`PluginServiceCollectionExtensions`): for hosts and tests that reference a plugin project (`ConfigureEngineServices`); reflection over that
assembly only with the scanner's rules (steps strictly: a tag of another type throws), then the same wiring; **an assembly with exactly one public concrete
`IExtension` is driven by it** (`PluginExtensionRunner`, no loader, no trust), several is an error. Its tools are listed as
`Plugin:<id>`; its steps have no `StepTypeDto.plugin` and it is not in `CatalogDto.plugins`.

**Packages.** `manifest.requires.plugins: [PluginRequirement { id, origin?, min?, sha? }]`; reading a manifest turns `requires.extensions:
[{ name, min?, sha256? }]` into plugin entries (`name` → `id`, `min` kept, `sha256` dropped; an entry `plugins` has wins) and never writes
`extensions` again. `PluginRequirements.Derive(document, catalog, existing)` / `Of(manifest)` / `Check(document, map, catalog, requirements)`
(codes `plugin_missing`, `plugin_version`, `step_unknown`; a different `sha` is a warning) / `AtLeast`; `PackageStore.DeclarePlugins(session,
requires)` (was `DeclareExtensions`). `PluginUse.Find(document, catalog)` → `PluginUse(Id, Version, Origin, Folder?, Sha512?, Scope?,
StepIds)`. Trust hash: `|ext:` → `PluginRequirements.TrustFragment(uses, config)` = `|plugin:` + JSON of `[{ Id, Origin, Sha512, Config }]`
sorted by id, `Config` = SHA-256 of the package's `Plugins.<id>` section with secret values replaced (the fields the loaded plugin's schema marks `x-secret`, plus any `enc:`/`********` value; never by name, «Deviations of plugins phase 6», audit fixes); empty without
plugins (the hash of such a package is unchanged; a package that uses a plugin asks for trust once, as accepted).

**Configuration migration.** `config.json` `Extensions.<Name>` → `Plugins.<id>` in memory (`PluginConfigMigration.Migrate`, an entry
`Plugins` already has wins) when `ConfigStore` reads the file and when it saves (so the next save writes `Plugins`), when a library or another
package's configuration is applied, in `ConfigSections.Describe` and in the trust fragment. `EnvironmentSections.Plugins` (new) and
`LegacyExtensions` are reserved names (never an LLM provider). `ConfigSections`: ids `plugins:<id>` (label «Plugin <id>»), `extensions:<Name>`
accepted as `plugins:<Name>`; the merge conflict kind is `plugin`. No alias at run time: the engine only has `AITaskAgent:Plugins:<id>`.

**Options.** `PluginDirectories`, `AutoDiscoverPlugins`, internal `Plugins` replace `ExtensionDirectories`, `AutoDiscoverExtensions`,
`ExtensionHostCache` and `StepPackages`; `MaxPluginUploadBytes` (phase 3) is the cap of a `.nupkg` for the local repository. `NugetUserConfig.Validate` refuses UNC (`\\server\share`) and device (`\\?\`, `\\.\`) paths for
sources and the cache folder.

### 25.10 Web routes of the plugins (phase 3, `WorkflowDesignerWeb`)

Everything under `/api`, camelCase JSON; the mutating routes go through the same `CrossSiteGuard` as every other POST/DELETE (`Sec-Fetch-Site: cross-site` → 403
before anything runs). `/api/extensions` and `/api/step-packages` (GET, POST, DELETE) no longer exist (404/405). `/api/packages/...` (the workflow zips) is untouched.
The frontend is written against this section.

| Route | Request | Answers |
|---|---|---|
| `GET /api/plugins` | | 200 `PluginListDto` (§25.9). |
| `POST /api/plugins` | `{ id, version }` (NuGet) **or** `{ folder, id? }` (folder plugin); second step adds `approvedSha512` (+ optional `approvedClosure`) | 202 `PluginInstallResponse { installed:false, restartRequired:false, report: LoadReportDto, pending: TrustRequestDto }` (nothing loaded or recorded); 200 `{ installed:true, restartRequired, report }`; 400 `invalid_plugin`; 409 `trust_stale`; 422 `plugin_install_failed` or `folder_not_allowed`. |
| `DELETE /api/plugins/{id}` | | 200 `{ id, removed:true, restartRequired, message }`; 404 `plugin_not_found`; 400 `invalid_plugin`. |
| `POST /api/plugins/rescan` | none | 200 `{ reports: [LoadReportDto], list: PluginListDto }`: one report per folder of the roots that `plugins.json` does not record (they stay `pending-trust`; nothing is loaded). |
| `POST /api/plugins/files` | the raw `.nupkg` as the body (`Content-Type: application/octet-stream`, `application/zip` or `application/x-zip-compressed`) | 201 `AddNupkgResultDto { id, version, sha512, size, alreadyPresent, dependencies:[{ id, range, availableIn? }] }` (also when `alreadyPresent`: the same bytes are idempotent); 400 `invalid_nupkg`; 409 `package_conflict`; 413; 415. |
| `GET /api/plugins/repo` | | 200 `NupkgRepositoryDto { directory, packages:[{ id, version, sha512, size, dependencies }], problems:[{ code, message }] }`. |
| `DELETE /api/plugins/repo/{id}/{version}` | | 200 `{ id, version, removed:true }`; 404 `repo_package_not_found`; 400 `invalid_plugin`. |
| `GET /api/plugins/{id}/prompts` | | 200 `[name]`: the prompts of a **loaded** plugin, sorted (empty when it has none); 404 `plugin_not_found` when it is not loaded; 400 `invalid_plugin`. Read-only. |
| `GET /api/plugins/{id}/prompts/{name}` | | 200 `{ text }` (the raw file, not rendered); 404 when the plugin or the prompt does not exist; 400 for an invalid name (`..`, separators, reserved device names...). There is no `PUT`/`DELETE`/`POST`: the answer is 404/405. |

**Plugin prompts (2026-10-22).** `ITemplateProvider` cannot enumerate, so the designer reads the plugin's `templates/` folder (`<LoadedPlugin.Folder>/templates/*.md`) by
convention, only for a plugin that has a template source (`PluginPrompts.List/Read`, same name rules and containment as the package's prompts:
`WorkspaceStore.ValidateName` + `ResolvePath`, and a link is not followed). A source that keeps its templates elsewhere is not listed (it still
renders); a static plugin (`services.AddPlugin`) is not «loaded» for this purpose. The frontend lists them in the Prompts tab under «De plugins»
(grouped by plugin, «Plugin» badge, a package prompt with the same name is marked as shadowing it because the package's `prompts/` come first in the chain),
opens them in a read-only text area and offers them in the role form's SystemPrompt picker. Nothing in the UI saves, renames or deletes them, and no
validation compares a role's SystemPrompt with the prompt names.

**Install and approval (`POST /api/plugins`).** `id` is checked as a NuGet id, `version` as `NugetRequirement` (a version or range, never floating), `approvedSha512` as
`sha512-<base64>` and `approvedClosure` as `sha256-<hex>`; a `folder` request must not carry `version`. First call: 202 with `pending` (`TrustRequestDto`: `id`, exact
`version`, `sha512` of the `.nupkg` or of the folder's tree, `closure`, `dependencies`, `assemblies`, `kind` `install|update|changed`, `origin`, `folder`, `contributions`,
`nativeLibraries`) and nothing loaded. Second call with the `sha512` (and `closure`) the user saw: 200 and the plugin is live (steps in `GET /api/catalog`, no restart) unless
another version or other content was already loaded (`restartRequired: true`). Another hash, or a request this backend did not show (expired after 30 min, backend restarted):
409 `trust_stale` with the current `pending` and `report`. What the loader cannot install (not found in any source, hash mismatch, a collision with the engine or with another
plugin, an own assembly that is not a .NET assembly…): 422 `plugin_install_failed` with `report.problems[0]` (`code`, `message`). Installs are serialized per backend. **`folder`
must be a plugin root or inside one** (`roots` of `GET /api/plugins`: `PluginDirectories`, `<workspace>/plugins`, `<workspace>/extensions`, `<install dir>/plugins`), as
`pending[].folder` and `plugins[].folder` already are; anything else (another path, `..`, a UNC or device path) is 422 `folder_not_allowed` and nothing is read. A browser cannot
name an arbitrary path anyway: the command line installs those (`plugin install --folder`).

**Files (`POST /api/plugins/files`).** The body is streamed to a temporary file of the local repository `<data>/nuget-repo` with a cap of
`WorkflowDesigner:MaxPluginUploadBytes` (default 100 MiB, `WorkflowDesignerOptions.MaxPluginUploadBytes`; bytes really read, not the declared length): a `Content-Length` over the
cap is a 413 before reading, a chunked body is cut at the cap (`IHttpMaxRequestBodySizeFeature` is set for this route only, as for the zip import) and also 413. Content types: only the three
above; multipart, JSON or anything else is 415. Before copying: `PK\x03\x04` signature, at most 20 000 entries, exactly one `.nuspec` at the root, the nuspec decompressed with a byte
cap and parsed with DTDs prohibited, id and version read with `PackageArchiveReader.GetIdentity()` (never the client's file name), id checked with `NugetRequirement.Problem`, version with
`NuGetVersion`, destination `id.version.nupkg` in lower case with the normalized version and checked to be inside the folder, moved atomically. All of those are 400 `invalid_nupkg`
(a decompression bomb in the nuspec too: it is within the byte cap on the wire and invalid once read; only «over the cap» is 413). Same id and version with the same bytes: 201 with
`alreadyPresent: true`; other bytes: 409 `package_conflict` (replacing a version is not supported; delete it first). Also 409 `package_conflict` (the file is not kept)
when the installed plugin, its lock or the NuGet cache have that id and version with other bytes: an install would load that copy, not the upload (`PluginLoader.ContentConflict`). `dependencies` are those of the framework group nearest to
`net10.0`, each with `availableIn: "local-repository"` when the repository has a satisfying version and nothing otherwise («upload this one too», unless another source has it).
**Uploading registers, records and loads nothing**: the plugin appears in `GET /api/plugins/repo` and the install is the two-step `POST /api/plugins` (`{ id, version }`), which
reads the repository as a source (so it works with no remote source). The frontend uploads a folder as one request per file (200 files at most per import: a limit of the dialog, not of the server).

**Error bodies.** `{ "error": message, "code": code, ...extra }` as everywhere (`ApiError`): `invalid_plugin` (+ `problems: [string]` on `POST`), `trust_stale` (+ `pending`,
`report`), `plugin_install_failed` (+ `report`), `folder_not_allowed`, `plugin_not_found`, `repo_package_not_found`, and from the nupkg validation `invalid_nupkg`,
`package_conflict`, `too_large` (a 413 raised inside the repository when the stream passes the cap without a declared length, same status as the early 413 without `code`).

**Configuration of the web layer.** `WorkflowDesigner:PluginDirectories`, `WorkflowDesigner:AutoDiscoverPlugins`, `WorkflowDesigner:MaxPluginUploadBytes`
(`WebLimits.MaxPluginUploadBytes` for the early rejection and `WorkflowDesignerOptions.MaxPluginUploadBytes` for the repository, read from the same key). `WorkspaceHost.PluginTrust`
is `ApprovalTrustPolicy`; `PackageTrust` uses `PluginUse`/`PluginRequirements.TrustFragment` and `TrustDto.plugins`.

### 25.11 The `plugin` group of the CLI (phase 3, `WorkflowDesigner.Cli`)

One group replaces `extension list|info|verify|restore` and `steps install|list|remove`; `--plugins <dir>` (repeatable) replaces `--extensions` on `validate`, `run`, `resume`,
`list-steps`, `package info` and the `plugin` actions that read the roots; there is no alias (`--extensions`, `extension` and `steps` are usage errors, exit 2). `--json` as in the rest
of the CLI. Every action that opens the host (all but `verify`, and `info` of a folder or a `.nupkg`) takes `--data <dir>` and `--workspace <dir>` (data folder: `--data`, else `<workspace>/data`, else `%LOCALAPPDATA%/WorkflowDesigner/data`; the local repository
is `<data>/nuget-repo` and the recorded plugins `<data>/plugins`, also without workspace).

| Command | What it does | Exit |
|---|---|---|
| `plugin list [--plugins …] [--nuget-cache …]` | Loads what is recorded (never from a source), looks at the roots (nothing is approved), prints plugins, roots, repository, sources, problems and pending requests with the command that approves each; `--json` = `PluginListDto`. | 0; 1 when a recorded plugin cannot load (a folder never approved is not a failure). |
| `plugin info <id \| folder \| file.nupkg>` | Folder: manifest, tree hash, contributions (read from metadata). `.nupkg`: id, version, sha512, size, dependencies. Id: the plugin as listed (steps, tools, hooks, response types, settings, warnings). | 0; 1 when a folder or package is not loadable; 2 for an id that is neither a path nor a known plugin. |
| `plugin install <id> [--version v] [--trust] [--nuget-source …] [--nuget-cache …]` | NuGet. Without `--version`, the highest version of the id in the local repository. | 0 installed; **7** without `--trust` (shows the request, loads and records nothing); 1 failed (`install failed (code)` on stderr); 2 usage (floating version, bad id, no version known). |
| `plugin install --file <x.nupkg> [--trust]` | Id and version from the nuspec; the file is copied to the local repository first (with or without `--trust`), then as above. | as above; 2 for a file that is not a package or conflicts with the repository (`invalid_nupkg`, `package_conflict`, `too_large`). |
| `plugin install --folder <dir> [--id id] [--trust]` | Approves and records a folder plugin (any path; the tree is hashed and used in place). | as above. |
| `plugin trust <id \| folder>` | The approval itself, without `--trust`: prints what it approved (hashes). An id is looked up among the folders of the roots. Only that folder is approved. | 0; 2 when no folder matches. |
| `plugin remove <id>` | The next command stops loading it. | 0; 1 when it is not installed. |
| `plugin rescan [--plugins …]` | Lists only the folders of the roots that were never approved (`--json`: `{ roots, found, reports, pending, problems }`). | 0; 1 when a found folder cannot be read. |
| `plugin verify <folder \| file.nupkg>` | The author's check; problems on stderr. | 0 valid; 1 not valid; 2 path does not exist. |
| `plugin repo add <file \| folder>` | Validates and copies; installs nothing. A folder is imported file by file (at most 200). | 0; 1 when a file of a folder was refused; 2 for a single file refused. |
| `plugin repo list` | Packages with their dependencies and which are not in the repository. | 0; 1 if there are files that are not valid packages. |
| `plugin repo remove <id> --version v` | Removes one file; the installed plugin is not touched. | 0; 1 when it is not there; 2 for an invalid id or version. |

`--trust` approves exactly the plugin being installed (`InstallTrust`, armed only while that install runs; the reload of what is already installed that happens first never gets approved
by it). Read-only commands (`list`, `info`, `rescan`, `verify`, `repo list`, and the loading done by `validate`, `run`, `list-steps`, `package info`) write nothing: install and repository
folders are byte-identical before and after (test). `run` and `validate` announce each plugin a package uses on stderr; a recorded plugin that does not load (changed content, cache not matching
its lock, collision) is a `warning: plugin not loaded (code)` with the approving command. Exit codes: 0/1/2 as before and 7 = needs approval.

### 25.12 Migration guide (extensions and step packages → plugins, 2026-10-21)

**An extension author** (a class `: IExtension` loaded from a folder with `extension.json`). **Since 2026-10-22 the class is the plugin's entry point again** (§25.3a): keep it (or write one), ship it in an assembly named by `plugin.json`, and let `Register` add what the plugin brings. The 2026-10-21 advice «delete the `IExtension` class» no longer applies: a plugin without one still loads by type discovery (§25.3), but then the only way to register a tool with arguments is an `IToolSource` found by scan.

| `IExtension.Register` / `Info()` | What it does |
|---|---|
| `builder.AddStep<T>("Tag")`, `builder.AddStepsFromAssembly(asm)` | the steps; an explicit tag, else `[StepTag]`, else `Type.FullName` (`AddStepsFromAssembly` only takes the tagged ones) |
| `builder.AddTool<T>()` / `AddTool(ITool)` / `AddToolSource(IToolSource)` / `AddToolsFromAssembly(asm)` (`AITaskAgent.Extensions.Llm`) | the tools: a class (one instance per engine), an instance (shared), or a source built by you (with its constructor arguments) whose `Create(PluginContext)` runs per engine; listed as `Plugin:<id>` with the qualified `Plugin:<id>:<name>` |
| `builder.AddHook(name, kind, method)`, `AddHooksFromAssembly(asm)`, `AddResponseTypesFromAssembly(asm)` | `public static` methods marked `[Hook(Name = "…")]`, `[Validator]`, `[ResultValidator]`, and `[ResponseType]` classes (`AITaskAgent.Core.Hooks`, in `AITaskAgent.Abstractions`); hooks with source `plugin:<id>` |
| `builder.AddTemplates(ITemplateSource)` | the templates; the package's `prompts/` wins, then the plugins' sources (ordered by plugin id) |
| `builder.AddAllFromAssembly(asm)` | all of the above by scan of one assembly; then `Info()` can be `AssemblyDiscovery.Describe(asm)` |
| `ConfigurationType`, `ConfigurationModel()`, `ConfigurationDefaults(PluginContext)`, `RootSections` | what `IPluginConfiguration` did (schema, defaults, root sections, `x-secret`); the engine composes the configuration and reads the user's values under `AITaskAgent:Plugins:<id>`; `builder.Plugin.Folder` / `PluginContext.Folder` replace `AppContext.SetData("WorkflowDesigner.ExtensionFolder:<Name>")` |
| `Info()` | what you declare you will register; by discovery or by hand; compared with the real registration after loading (`info-mismatch`, a warning) |
| `extension.json` (`name`, `assembly`, `nuget`, `extensionType`) | `plugin.json` `{ id, version?, description?, contract: 1, assemblies?, extension? }` (`extension` only to choose among several `IExtension` classes); ship the dependencies in the folder (`dotnet publish`): a folder plugin has no `nuget` section |

**Moving a discovery-only plugin to an extension** (`Samples/ExamplePlugin` and `Samples/CodebaseAnalysis.Designer` were moved on 2026-10-22): add a class `: IExtension` whose `Register` calls `AddStepsFromAssembly`/`AddAllFromAssembly`, move `IPluginConfiguration` into `ConfigurationType`/`ConfigurationModel()`/`ConfigurationDefaults`/`RootSections`, and return `Info()`. Nothing else changes: attributes, tool sources and template sources stay as they are.

What a plugin cannot do in this version (nothing in the engine reads it): replace or decorate `ILlmService`, `IExecStepPolicy`, `ICSharpStepPolicy`, `IConfiguration` or other services, `ICodeItemLocator`, `IScriptHostFunction`, `IScriptLibrary`. Check the result with `workflow-designer plugin verify <folder|file.nupkg>` and `plugin info <folder>`; both read metadata only. Examples: `Samples/ExamplePlugin` (small) and `Samples/CodebaseAnalysis.Designer` (large, with a private copy of `FileTools.dll`; §19).

**A NuGet step-package author.** The package installs as it was, now as a plugin (`plugin install <id>`). Two visible differences: a step without `[StepTag]` is registered under `Type.FullName` (it was the class name), so a workflow written against the old tag needs `[StepTag("ClassName")]`; and the package's tools, hooks, response types, templates and configuration are now discovered too. The recorded installs are migrated once from `steps-packages.json` with their approvals (§25.6), but the next reload registers the new tags.

**A user.**
- Folders in `<workspace>/extensions/<Name>` are still found (legacy root, warning `legacy-manifest` when they carry `extension.json`) and show as **pending trust**: nothing is loaded until you approve them in «Plugins instalados» or with `plugin trust <folder>`. Moving them to `<workspace>/plugins/` removes the warning; republishing CodebaseAnalysis needs `-o <workspace>/plugins/CodebaseAnalysis`.
- Approving is needed once per folder and again after any change of any file of it (tree hash). Extensions used to be trusted at host level, so this is new for them.
- Workflow zips that used an extension read `requires.extensions` as `requires.plugins` and `Extensions.<Name>` of `config.json` as `Plugins.<id>`; both are written in the new form on the next save. The trust hash of such a zip changed (`|ext:` → `|plugin:`), so it asks for trust **once more**. `workspace/workflows/codebase-analysis.zip` was repacked already.
- Settings and flags with the old names are gone, without alias: `WorkflowDesigner:ExtensionDirectories` → `PluginDirectories`, `WorkflowDesigner:AutoDiscoverExtensions` → `AutoDiscoverPlugins`, `WORKFLOW_DESIGNER_EXTENSIONS` → `WORKFLOW_DESIGNER_PLUGINS`, `--extensions` → `--plugins`, `extension …` and `steps …` → `plugin …`. REST clients: `/api/extensions` and `/api/step-packages` → `/api/plugins`.
- **Plugin secrets** (since the audit fixes of 2026-10-21): the fields a loaded plugin's schema marks `x-secret` (format `password`: `[PasswordPropertyText]` or `[DataType(DataType.Password)]` on its options class, or `x-secret` in a hand-written `Schema()`) are encrypted in `config.json` like API keys, masked (`********`) when read and replaced by `${WD_<ID>_<KEY>}` in an export without keys; a name alone (`ApiKey`, `Token`…) no longer makes a field secret, so a plugin author must declare it. A value saved in clear before the plugin was loaded is encrypted on the next save.
- A backend or CLI started before the migration keeps its old loaded state: restart it (the backend without `--no-build`, since the Core changed).

### Deviations of plugins phase 1 (2026-10-21)

- **`load-failed` from the scan stops the plugin before trust**: an own assembly that is not a readable .NET assembly is reported and nothing
  is asked (before: approved, then `load-failed`). A recorded plugin in that state keeps its entry unchanged.
- **Tool-name collisions are the host's**, through `PluginLoaderOptions.Accept`: a tool's name is an instance property and the loader
  instantiates nothing. The designer's plugin service (phase 2) does it.
- **`RegisteredStep.Tagged` stays** in the loader's report (whether `[StepTag]` was declared); the DTO decision (`tagged` disappears) is the
  designer's.
- **Folder plugins have no `nuget` section** (§17's `extension.json` `nuget` + `nuget.lock.json`): the folder ships its dependencies.
- **`plugin-locks/` subfolder** (the old layout put locks at `<install>/<id>/<version>/`): with `InstallDirectory = <data>` the id folders
  would mix with `nuget/`, `csharp-cache/` and the rest.
- **`invalid-contribution`, `invalid-hook`, `invalid-response-type` are warnings** (the item is ignored, the plugin loads); `collision`,
  `multiple-configurations`, `load-failed`, `invalid-plugin` stop it.
- **The designer is adapted only to compile** (phase 2 replaces it): `StepPackageService` uses `PluginLoader`/`PluginLoaderOptions`/
  `InstalledPlugin`/`PluginsChanged`, and `StepPackageOrigin.Of` reads `plugin:nuget:` contexts through `PluginIdentity`. It still sets
  `InstallDirectory = <data>/step-packages`, so `plugins.json` lands there (migrated from the `steps-packages.json` beside it) and
  `AllowRemote` stays at its default (true). Tests that still assert the old behaviour (file name, class-name tags, per-step tag clash):
  Core 1 (`StepPackageTests.A_tag_of_the_engine_is_a_reported_clash_and_an_untagged_step_is_marked`), CLI 5 (`StepsCommandTests`),
  Web 1 (`StepPackageEndpointTests.Install_is_202_…`).
- **Tests**: `AITaskAgent.NugetLoader.Tests` 97/97 (was 59: `PluginLoaderTests` ported from `NugetStepLoaderTests`, one test rewritten and +1, `PluginFolderTests`
  19, `LocalNupkgRepositoryTests` 18), `AITaskAgent.Extensions.CSharp.Tests` 33/33 (+1, `[Hook(Name)]`), framework `Tests` 394/394 (+6,
  `PluginContractsTests`). No network.

### Deviations of plugins phase 2 (2026-10-21)

- **Folder roots are `<Workspace>/plugins` and `<install dir>/plugins`**, not the former `extensions/` folders: a workspace with
  `extensions/CodebaseAnalysis` must move (or point `PluginDirectories` at) it. Every folder now needs approval (rescan → pending), also an
  explicit one, as the spec says («nada se carga antes de aprobar»).
- **Local repository first in the source list** (`[<data>/nuget-repo, ..NuGetSources]`): with the same id and version in a feed, the uploaded
  file wins. The repository folder is created by the first upload, not by the service.
- **`tool_ambiguous` applies to FileTools and MCP alike** (spec §2): a plugin tool shadowing a FileTools tool is listed and run as the
  plugin's, and a step naming it fails validation. CodebaseAnalysis ships `FileTools.Enabled: false`, so its package is not affected; a
  user who enables FileTools in that package gets `tool_ambiguous` on the auditors (objection: an exemption for FileTools would avoid it).
- **Template chain order is by plugin id**, not by the order of `requires.plugins` (the engine collection does not know the manifest).
- **`CatalogDto.plugins` lists the loaded plugins only**; pending and failed ones are in `GET /api/plugins` (`PluginListDto`). Built-in
  IExtensions (LLM, Script, C#) and their `ConfigurationModel()` are no longer in the catalog.
- **Plugin secrets are marked, not encrypted** (superseded by the audit fixes of «Deviations of plugins phase 6»: no name rule, encrypted and masked): `configurationSchema` carries `x-secret: true` (by name: api key, password, secret, token,
  connection string, credential; or `format: password`) and the trust hash ignores their values, but `ConfigStore` neither masks nor
  encrypts values under `Plugins` (they are stored as written; `${VAR}` is the way). Pending.
- **Static plugins (`AddPlugin`) are registered inside `ConfigureEngineServices`**, after the designer's tool set: a FileTools tool of the
  same name wins there in a run (CodebaseAnalysis has FileTools off).
- **Temporary in other projects** (phases 3 and 5 replace them): Web `StepPackageEndpoints` (`/api/step-packages`) and `/api/extensions`
  serve `PluginService` (`PluginListDto`), `PackageTrust` uses `PluginUse`/`TrustFragment`/`TrustDto.plugins`, `Program.cs` reads
  `PluginDirectories`/`AutoDiscoverPlugins`; CLI `steps …` and `extension …` run on `PluginService`/`PluginLoader.InspectFolder` (`extension
  restore` is a usage error), `--extensions` fills `PluginDirectories`; `CodebaseAnalysis.Designer/DesignerConfiguration.SectionPath` reads
  `AITaskAgent:Plugins:CodebaseAnalysis`; the CLI and Web test files that named the removed types compile against the new ones.
- **Tests**: designer Core 578/578 (`PluginServiceTests` 20 and `PluginEngineTests` 20 cases replace `ExtensionLoadingTests` and
  `StepPackageTests`; `ConfigSectionsTests`, `CatalogTests`, `ExampleWorkspaceDeterminismTests` adapted), `AITaskAgent.NugetLoader.Tests` 97/97,
  `CodebaseAnalysis.Designer.Tests` 6/6; CLI 178/198 and Web 318/332, the failures being the extension/step-package suites phase 3 rewrites.
  The fixture is `Samples/TestShared/PluginTestFixture.cs` (was `ExtensionTestFixture`). No network.

### Deviations of plugins phase 3 (2026-10-21)

- **Web: the shape of the install answers is the old one, with new names.** `PluginInstallResponse` is `StepPackageInstallResponse` (`installed`, `restartRequired`, `report`, `pending`); the codes
  `invalid_step_package`, `step_package_failed`, `step_package_not_found` are now `invalid_plugin`, `plugin_install_failed`, `plugin_not_found`. `POST /api/plugins/rescan` answers
  `{ reports, list }` (the spec did not fix a shape); `POST /api/plugins/files` answers 201 also for `alreadyPresent`.
- **Web: a folder can only be approved from inside a plugin root** (422 `folder_not_allowed`): the spec said «aprobar/instalar carpeta» without saying from where. The folders the UI can show
  (`pending[].folder`, `plugins[].folder`) are always in a root, a browser cannot type a path it did not get from the backend, and the two-step trust still applies, but accepting an
  arbitrary path from a request would let any script that gets past loopback and `Sec-Fetch-Site` point the backend at a folder it chooses. The CLI installs any path.
- **Web: only the size cap is a 413.** A nuspec that is a decompression bomb is within the cap on the wire and is refused once read: 400 `invalid_nupkg` (the nuspec has its own byte cap, the zip
  is never extracted). A body over `MaxPluginUploadBytes` is 413 declared or chunked (no `code` when the web layer cuts it, `code: "too_large"` when the repository does).
- **`WorkflowDesignerOptions.MaxPluginUploadBytes`** (new, default 100 MiB, key `WorkflowDesigner:MaxPluginUploadBytes`): `PluginService` passes it to the repository as `NupkgLimits.MaxBytes`;
  `WebLimits.MaxPluginUploadBytes` is the web layer's copy for the early 413 and the request body limit of the route.
- **Core: plugin vs FileTools is no longer `tool_ambiguous`** (decision of the user, replaces the second bullet of phase 2): the plugin's tool wins the listing and the run and the tool list carries an
  informative problem («the plugin's is listed and used»); CodebaseAnalysis carries its own copy of FileTools on purpose (§19.4). Plugin vs an MCP server of the package stays `tool_ambiguous`
  (`ToolCatalog.Deduplicate`, now `internal`, has the unit test). Consequence to know: a step that names `view_file` in a package with FileTools enabled and a plugin that offers `view_file`
  runs the plugin's copy without any validation error.
- **Core: the legacy root `<workspace>/extensions` is searched** (scope `workspace`, between `<workspace>/plugins` and `<install dir>/plugins`; `AutoDiscoverPlugins = false` turns it off with the others):
  a folder there with `extension.json` is read as `plugin.json` (warning `legacy-manifest`) and waits for approval like any folder, so the user's published CodebaseAnalysis shows up as pending
  without moving it by hand. It replaces the first bullet of phase 2 (there, only `plugins/`).
- **CLI: `plugin install <id>` without `--version`** takes the highest version of that id in the local repository (usage error when there is none): the spec wrote `[--version v]`, and a floating
  «latest» is not accepted by the loader. `--file` copies the package into the local repository before the trust question, so a command that exits 7 has written a file there (and nothing
  else); say so in the output. `plugin trust` approves without `--trust` (the command is the consent; it prints the hashes it approved). `plugin install --folder`/`trust` accept any existing path
  (the CLI is the user's own process).
- **CLI keys that changed**: `package info --json` has `plugins { used: [{ id, version, folder, sha512, origin, steps }], requires: [{ id, min, sha, status, installed }] }` (was `extensions` with `name`);
  `list-steps --json` has `plugin { id, version, origin }` (was `extension: name`); text lines say `plugins:`/`plugin 'X'`. The `Offline` flag of `plugin list` is true with only folder sources.
- **Not temporary any more**: all `TEMPORARY (plugins phase 2)` patches of Web and CLI are gone (`StepPackageEndpoints`, `/api/extensions`, `ExtensionCommand`, `StepsCommand`,
  `StepPackageSupport`, the key names). Still open elsewhere: `CodebaseAnalysis.Designer/DesignerConfiguration.SectionPath` (phase 5), `Samples/ExampleExtension` still ships an `IExtension`
  (phase 5), the ClientApp still called `/api/extensions` and `/api/step-packages` when this was written (phase 4 replaced them: it now calls only `/api/plugins…`), and the READMEs of `WorkflowDesignerWeb`
  (`workspace/README.md`, `PENDIENTES.md`, `RETOMAR.md`) still describe the old model (phase 6).
- **Tests**: designer Core 580/580 (+2: legacy root, `Deduplicate`; the FileTools test now asserts the new behaviour), CLI 204/204 (`PluginCommandTests`, 35 cases, replaces `ExtensionCommandTests` and
  `StepsCommandTests` and adds `--file`, `repo`, `trust`, `rescan`, the legacy root and the read-only check), Web 362/362 (`PluginEndpointTests.cs` with `PluginFolderEndpointTests` and
  `PluginNugetEndpointTests` replaces `ExtensionEndpointTests` and `StepPackageEndpointTests`; `PluginFileEndpointTests.cs` is new: not a zip, bad ids, DTD and bomb, cap declared and chunked,
  conflict, idempotence, missing dependency, nothing registered or loaded, multipart/JSON 415, cross-site 403, install from the repository, delete), `CodebaseAnalysis.Designer.Tests` 6/6. No network.

### Deviations of plugins phase 4 (2026-10-21)

- **One view, one state**: `features/plugins` (`PluginsView`, `PluginCard`, `PluginTrustDialog`, `PluginLoadResult`, `AddPluginButton`/`AddPluginDialog`, `RepositoryList`,
  `WhereInstalled`) and `state/plugins.tsx` (`PluginsProvider`, `usePlugins`, `useInstalledSummary`) replace `features/environment/installed`, `stepPackages`, `ExtensionsViewer` and
  `state/stepPackages.tsx`. Route `#/installed` and the header button stay («Plugins instalados (N ⚠)»: ⚠ when a plugin is in error, pending or needs a restart, or a registry problem
  belongs to no plugin). `AppProviders` has `PluginsProvider` where `StepPackagesProvider` was.
- **API client**: `getPlugins`, `installPlugin` (202 → `pending`, 409 `trust_stale` → `stale`, 422 `plugin_install_failed` → `failed`, 400 `invalid_plugin` throws), `removePlugin`,
  `rescanPlugins` (the `{ reports, list }` answer is ignored; the list is requested again), `addPluginFile` (raw `application/octet-stream` body, one file per request),
  `getPluginRepository`, `removeFromPluginRepository`. `getExtensions`, `/step-packages` and every `Extension*`/`StepPackage*` type are gone. `InstallPluginRequest.origin` is only
  read by the simulator: the HTTP client does not send it (the server tells NuGet from folder by `version`/`folder`).
- **Catalog**: `CatalogDto.plugins`/`pluginProblems` and `StepTypeDto.plugin`; the `tagged:false` note of the inspector header is replaced by a «Plugin» badge with link; the palette badge
  «Plugin» (id as tooltip) replaces «Extensión»/«NuGet». The old «Integrada» listing of built-in `IExtension`s is gone (the catalog no longer has them). `Config.Extensions` became
  `Config.Plugins`, library section ids `plugins:<id>`, conflict kind `plugin`; `TrustDto.extensions` became `plugins` (`TrustPluginDto`).
- **Where the UI is stricter or different from the spec**: a folder of more than 200 `.nupkg` uploads none (all-or-nothing, so no dependency is left loose); a pending plugin never shows its
  `not-approved` marker as an error; Settings → Orígenes NuGet probes every enabled folder source with the existing `POST /nuget/sources/test` to warn about a missing one (no new field);
  installing from Settings closes it and hands the request to the view through `shared/pluginInstall.ts` (the view owns the trust flow). `features/plugins/index.ts` exports only
  `AddPluginButton`; `App` lazy-loads `PluginsView` by its module path so the view stays in its own chunk.
- **Secrets**: `SchemaTree` masks defaults and flags `x-secret` / `format: password` fields; the view edits no plugin configuration.
- **`?mock=1`** starts with the demo state of `mockPlugins.ts` (`plugins: { demo: true }`: CodebaseAnalysis and Acme.Steps loaded, Plain.Steps needing a restart, ExamplePlugin pending,
  Broken.Tools in error, two `.nupkg` in the repository); tests use the empty default.
- **Tests**: ClientApp Vitest 1188/1189 in 125 files (the one failure of the last full run is `templates.test` «desde la paleta», a known flake that passes alone); `tsc -b --noEmit` clean. Browser (Playwright, `vite build` to a temporary folder, `?mock=1`, own server on 5191): list, states, sections, menu, file and folder upload,
  trust dialogs of both origins, repository, Settings and dark theme, screenshots reviewed. Not seen against the real backend.

### Deviations of plugins phase 5 (2026-10-21)

- **CodebaseAnalysis is a folder plugin** (§19.2, §19.3): `CodebaseAnalysisDesignerExtension.cs` and `ServiceDecoration.cs` are gone; `extension.json` → `plugin.json`
  (`assemblies: ["CodebaseAnalysis.Designer.dll"]`, copied by the csproj); `DesignerConfiguration` → `CodebaseAnalysisConfiguration : IPluginConfiguration` (the phase 2 patch
  `DesignerConfiguration.SectionPath` and the `AppContext` folder lookup are gone); `DesignerTools` → `CodebaseAnalysisTools : IToolSource`; `DesignerHooks` →
  `CodebaseAnalysisHooks` (`[Hook(Name = …)]` on `OnAuditorToolLimit`, `OnVerifierToolLimit`, `PostProcess`; the const names and the hook names are unchanged);
  `DesignerTemplates` (`CompositeTemplateProvider`) → `CodebaseAnalysisTemplates : ITemplateSource`. `Steps/*`, `DesignerRun` and `RunCodeItemLocator` are unchanged (the
  `[Description]` of `CaInit` still says «extension configuration»).
- **What the engine composes is narrower than the old bridge**: the bridge lifted the whole extension section to the root; the engine lifts only the four `RootSections`, so a key
  of `Plugins.CodebaseAnalysis` outside them stays under `AITaskAgent:Plugins:CodebaseAnalysis` (the original steps read none). `Schema()` is the former hand-written schema
  (`OptionsType` is null). A hook name that is already registered is now logged and skipped by the engine (the old code skipped it silently). The sample's template provider is
  built when the collection's `ITemplateProvider` is resolved (it was lazy); `templates/` beside the host stays as a fallback.
- **Publish command** (the one verified, into a scratch folder outside the repository): `dotnet publish Samples/CodebaseAnalysis.Designer -c Release --no-restore -o
  <scratch>/plugins-ws/plugins/CodebaseAnalysis`; for a workspace, `-o <workspace>/plugins/CodebaseAnalysis`. The folder has 354 files (`templates/`, `dotnet-enricher/`,
  `Enrichment/`, `CodebaseAnalysis.Core.dll`, `FileTools.dll`, `OfficeTools.dll`, Sqlite, Roslyn…; also a copy of `AITaskAgent.Abstractions.dll`, never loaded from there because the
  host wins, but part of the tree hash). `publish` without `OutDir` also leaves `bin/Release` in the referenced projects.
- **Checked on that real folder**: CLI `plugin verify` valid (0); `plugin info <folder> --json`: contributions read from metadata with `assemblies: ["CodebaseAnalysis.Designer.dll"]`
  only, seven `Ca*` steps (`CaAdapterStep<TIn,TOut>` skipped as `not-concrete`), no tool classes, one tool source, three hooks, one template source, the configuration, no
  `legacyExtensions`, no problems; `plugin install --folder` without `--trust` exit 7 and nothing recorded, with `--trust` exit 0; `plugin list --json`: `loaded`, `folder`,
  `explicit`, 7 steps, the 14 tools, 3 hooks, templates, the four root sections and the four schema properties, no warnings; `plugin info CodebaseAnalysis`; `list-steps`
  marks the seven as `(plugin CodebaseAnalysis)`; `validate codebase-analysis.zip --data <that data>` valid (0) while with an empty data folder it fails (1: `plugin_missing`
  on the six adapter steps, unknown tools and hooks on the auditors); `package info --json` shows the plugin used and `requires` `present`. `CodebaseAnalysis.Designer.Tests`
  with `CODEBASE_ANALYSIS_PLUGIN_FOLDER` pointing at that folder: 8/8, twice.
- **`FolderPluginTests`** (new, `CodebaseAnalysis.Designer.Tests`): the folder through the designer's `PluginService` with `ApprovalTrustPolicy` (pending first, contributions from
  metadata, nothing loaded; then approved), the plugin's load context `plugin:folder:CodebaseAnalysis/1.0.0` (not the test's copy of the assembly), steps, tool source, 14 tools
  listed as `Plugin:CodebaseAnalysis`, hooks with source `plugin:CodebaseAnalysis`, templates, schema, the package validating, and a full run of the package on the folder plugin
  with the model double (completed, ten reports with `analysis-meta`, ten model calls). Without the variable the folder is assembled from the test output (`plugin.json`,
  `CodebaseAnalysis.Designer.dll`, `CodebaseAnalysis.Core.dll`, `FileTools.dll`, `templates/`); the other dependencies then come from the test's default context.
- **`ParityTests`**: `services.AddPlugin(typeof(CodebaseAnalysisConfiguration).Assembly, "CodebaseAnalysis")` instead of `AddExtension`; the front-half test sets
  `Plugins.CodebaseAnalysis.CodebaseAnalysis.Preparation.Mode` (was `Extensions…`), which exercises the lifting of a root section.
- **`Samples/ExampleExtension` → `Samples/ExamplePlugin`** (project, folder, namespace and DLL `ExamplePlugin`; `AITaskAgent.slnx` and the three designer test projects reference
  it): no `IExtension`; `ExampleConfiguration : IPluginConfiguration` (`OptionsType = ExampleOptions`); `UppercaseStep` reads `AITaskAgent:Plugins:Example:Suffix`
  (`UppercaseStep.SuffixKey`); `plugin.json` (`id: Example`). `PluginTestFixture`, `PluginCommandTests`, `PluginEndpointTests` and `PluginServiceTests` name `ExamplePlugin.dll`. The
  Core test of the legacy folder (`A_legacy_extension_folder_loads_…`) now compiles its own `IExtension` variant with Roslyn (the example no longer has one); a new Core test
  checks the example's schema and that `Plugins.Example.Suffix` reaches the step.
- **Workflow zips**: only `workspace-src/codebase-analysis` declared `requires.extensions` / `Extensions`: its `manifest.json` has `requires.plugins: [{ "id": "CodebaseAnalysis" }]`,
  `config.json` `Plugins.CodebaseAnalysis`, and the description and README speak of the plugin; `workspace/workflows/codebase-analysis.zip` was repacked with `workflow-designer
  package pack` (the other zips have no such section and are unchanged). Inspected: the only key-like value is `${OPENAI_API_KEY}`; no `enc:` value.
- **Tests**: `CodebaseAnalysis.Designer.Tests` 8/8 (6 parity + 2 folder), designer Core 581/581 (+1), CLI 204/204, `AITaskAgent.NugetLoader.Tests` 97/97. **Not run**: Web
  (`WorkflowDesignerWeb.Tests`: only the project reference and three `ExamplePlugin.dll` strings changed; not built because the frontend phase was working on that project) and
  the browser E2E of the spec's phase 5; `CodebaseAnalysis.Core.Tests` was not touched. Documentation left for phase 6: done, see «Deviations of plugins phase 6» (only `RESULTS-2026-10-19.md` stays as it was).

### Deviations of plugins phase 6 (documentation, 2026-10-21)

- **§17 and §21-§24 are not rewritten as contracts: each is a «Superseded by §25» note** with what still holds (§17.1, §21 install sequence, §22 catalog swap, workspaces and the two-step approval, §23 CLI process model, §24 the single view) and the table of renames (§17.2). Their route tables, DTO lists, file formats, problem codes and «Deviations» of 2026-10-19/20 were deleted because they describe code that no longer exists; git history has them. §16 (phase D), §19 (phase G) and §20 keep their history, with a marker where a name changed (`extensions:<Name>` → `plugins:<id>`, conflict kind `extension` → `plugin`, `ExtensionNuget`).
- **§25.12 is the migration guide**; the user-facing steps in it are the ones that follow from the code and the deviations above, not a tested upgrade of a real user workspace (`%USERPROFILE%\WorkflowDesigner` was not touched). Two statements there are inferred from the loader's behaviour and were not run: that a CodebaseAnalysis folder published by the old model (an `IExtension` registering its hooks) does not validate its package, and that two folders with the same plugin id clash.
- **Documentation updated:** this file, `core-api.md` («Plugins» replaces «Dynamic extensions»; catalog and NuGet paragraphs), `Samples/WorkflowDesignerWeb/workspace/README.md`, `Samples/WorkflowDesignerWeb/RETOMAR.md` §0 (new entry and markers), `PENDIENTES.md` (new «Plugins» section and markers), `docs/extensibility/custom-extensions.md` (the designer loads plugins, not `IExtension`), `AITaskAgent.Extensions.CSharp/README.md` (attributes now in Abstractions, `[Hook(Name)]`), `docs/designer/STUDY-CODEBASE-ANALYSIS-IN-DESIGNER.md` (note at the top, text untouched). Already correct when checked (phases 1-5 wrote them): `AITaskAgent.NugetLoader/README.md`, `Samples/CodebaseAnalysis.Designer/README.md`, `Samples/ExamplePlugin/README.md`, `Samples/WorkflowDesigner.Cli/README.md`, `Samples/WorkflowDesignerWeb/README.md`.
- **Not changed on purpose:** the framework documents that describe `IExtension`/`AddExtension` as DI composition (`README.md`, `AITaskAgent.Core/README.md`, `docs/ARCHITECTURE-v2.md`, `docs/getting-started/architecture.md`, `docs/api-reference/*`, `docs/guides/yaml-pipelines.md`, `docs/informe-reestructuracion-v2.md`, `docs/RETOMAR.md`) because it is still true; `Samples/WorkflowDesignerWeb/RESULTS-2026-10-18.md`, `RESULTS-2026-10-19.md`, `PACKAGES-AND-TEMPLATES-SPEC.md`, `REUSABLE-FLOWS-ANALYSIS.md` (dated records); `docs/assets/diagrams/yaml-schema-compilation.excalidraw` (it has no mention of `IExtension`).
- **Verification pass fixes (2026-10-21):** (1) a plugin removed while loaded stays in `GET /api/plugins` as `restart-required` with `error: { code: "removed", message: "Se quitó; desaparecerá al reiniciar." }` (no `installedAtUtc`, so no «Quitar») until the restart; the list `restartRequired` was already true. (2) `invalid_nupkg` messages of an upload speak of «the uploaded file» (a file added by name, as the CLI does, keeps its own name); a read error on the temporary file is a generic «could not be read». (3) Additive: `PluginRootDto.legacy` (bool) marks `<workspace>/extensions`. (4) `LoadReport.Skipped` no longer carries `not-concrete` or `generic` (see §25.3). (5) UI only: «Añadir plugin» shows «Ya instalado» instead of «Instalar…» for a NuGet plugin loaded at that version. (6) The web backend logs each tool-listing problem once per process (Warning, or Information for the informative FileTools one) and repeats at Debug; `/tools` and `pluginProblems` still carry all of them. (7) Additive: `NugetMaterializedPackage.Description` is read from the nuspec of the direct package and fills `PluginDto.description` of NuGet plugins.
- **Audit fixes (2026-10-21, after the independent audit of the plugin system).** Each one has its tests (loader `PluginLoaderTests`/`PluginFolderTests`/`LocalNupkgRepositoryTests`, designer Core `PluginServiceTests`/`PluginEngineTests`, CLI `PluginCommandTests`, Web `PluginFileEndpointTests`, framework `TypeSchemaBuilderTests`, ClientApp `AddPlugin.test.tsx`).
  (A1) **Updates go through the collision rules**: an update of a loaded plugin (another version, or other content; NuGet and folder) is checked against the other owners' step tags, hook names and response types before trust, loaded aside after approval and offered to `Accept` (tool names), and only then recorded (§25.5). Before, only the scan's own problems were checked and the clash fell at the next start on whichever plugin loaded second by id. The step tags of an update recorded for the next start count as taken. Cost: one more load context per approved update, kept until the restart.
  (A2) **A `.nupkg` with the id and version of something the loader would reuse is refused**: `PluginLoader.ContentConflict(id, version, sha512)` (new, public) names the installed entry, the lock or the cached `.nupkg` of that id and version with another hash; `PluginService.AddNupkgAsync`/`AddNupkgFileAsync`/`AddNupkgFolderAsync` turn it into `package_conflict` (Web 409, CLI exit 2) and take the file they added out again. `plugin install --file --trust` approves only a request whose `sha512` is the file's (`InstallTrust.ForFile`) and fails (exit 1) when what was or would be installed is other content; the UI compares the uploaded `sha512` with the one of the request before offering trust (`usePluginFlow.start(request, expectedSha512)`, `AddPluginDialog.onInstall(id, version, sha512?)`, `PluginInstallRequest.sha512?`). Before, the upload succeeded and the install loaded the copy the cache or the lock already had.
  (M1) **Plugin secrets are secrets**: the `x-secret` fields of each loaded plugin (`PluginSecretFields`, published by `PluginService` after a load) are `SecretSlot`s of `config.json` (`Plugins.<id>` and the former `Extensions.<id>`): encrypted on save (`enc:v1:`, or `enc:v2:` with the master password unlocked), masked on read, decrypted for the engine (also when MCP secrets are left encrypted), replaced by `${WD_<ID>_<KEY>}` in an export without keys, checked when a library is applied; an `enc:` value or a `********` under `Plugins` is a secret even when its plugin is not loaded. A field of a plugin that is not loaded is left as it is. Paths follow nested `properties` only (not arrays, maps or `$ref`). Static plugins (`services.AddPlugin`) do not publish their fields.
  (M2) **Only the schema decides what is secret**: `PluginWiring.IsSecretName` and its regex are gone (they matched `MaxTokens`, `TokenEndpoint`, `TokenizerPath` and missed `Key`, `Pat`); `x-secret` = already `x-secret` in the schema or format `password`, and `TypeSchemaBuilder` (configuration flavour) gives format `password` to a string with `[PasswordPropertyText]` or `[DataType(DataType.Password)]`. The trust fragment masks exactly those fields (and `enc:`/`********` values), so a shared zip that changes `TokenEndpoint` asks for trust again. Plugins without a schema have no secret fields (no name heuristic, not even as a UI hint: they show no configuration at all).
  (M3) **Checks after approval** (§25.4): `IsolatedLoadContext.Named(…, IReadOnlyDictionary<string,string> approvedFiles)` (new overload) and the tree check now cover `Load` of NuGet dependencies and `LoadUnmanagedDll` (both origins); `content-changed` in `Exception.Data[IsolatedLoadContext.ProblemKey]` and as a stopping problem while loading. Executables a plugin starts are outside (limit in §25.4 and PENDIENTES).
  (M4) **The cache check has the extraction caps**: `ContentProblem` decompresses only entries whose file name is one of the extracted files, each and all together within `PluginLoaderOptions.MaxExtractedEntryBytes` / `MaxExtractedPackageBytes` (new, default 256 MiB / 1 GiB, also passed to the resolver), bytes really read; past them `nuget-too-large`. Before, every `.dll` entry was hashed with no cap.
  (M5) **A legacy folder that duplicates an installed id is ignored**: a rescan skips a folder of `<workspace>/extensions` whose id is installed from another folder (or from NuGet) and reports it as the warning `legacy-duplicate` (`PluginService.LegacyDuplicateCode`) in `problems` and in that plugin's `notes`; it can still be approved by naming it. Before, every rescan offered it as an update and approving it pointed the plugin back at the old folder. Covered with a temporary workspace equivalent to the user's (the real one was not read).
  Low: (a) installing again a folder plugin removed while loaded, with the same content, records it without `restart-required` (it asks trust as an install); (b) `LocalNupkgRepository` keeps the identity of each file by path, size and write time for the availability of dependencies (an import of N files no longer inspects N² of them; a listing always inspects again); (c) UNC and device paths are left out of the NuGet sources by `NugetSourceConfig.Resolve` (warning on stderr, or the new `refused` callback) and by `PluginService.Create` (logged), and the resolver never counts them as local folders when remote sources are off; (d) the phase 3 note about the ClientApp calling `/api/extensions` is corrected (phase 4 removed those calls).

### Deviations of the plugin folder inside a `.nupkg` (2026-10-22)

- **Why** (request of the user): CodebaseAnalysis could only be installed as a published folder of 354 files; he wants one `.nupkg` through «Añadir plugin… →
  Archivo(s) .nupkg…» and `plugin install --file`, and, as in his BRMS, the third-party libraries downloaded from the configured NuGet sources instead of
  embedded. Before this change a NuGet plugin's own assemblies were every DLL of `lib/<tfm>` and nothing else was extracted (`NugetResolver.Cache.ExtractTo`:
  only `lib/<tfm>/*.dll`, flattened, and `runtimes/<rid>/native`), so putting `CodebaseAnalysis.Core.dll` and `FileTools.dll` in `lib/` would have scanned
  them as the plugin's (their original steps and tools), and `templates/` and `dotnet-enricher/` would not have existed for `PluginContext.Folder`.
- **Chosen: the folder inside the package** (§25.2): `plugin/plugin.json` turns `plugin/` into a folder plugin's folder with the same manifest rules, while the
  nuspec's `dependencies` remain NuGet dependencies resolved, hashed (trust request and lock) and loaded as before. One code path for both origins'
  manifest; no new file format. **Discarded**: (a) `plugin.json` at the root with everything in `lib/` (the DLLs of a private executable such as
  `dotnet-enricher/` would be lib items, flattened by name with the plugin's own copies, and the content files would still not be extracted); (b) one
  `.nupkg` per own project (Designer, CodebaseAnalysis.Core, FileTools, OfficeTools, OpenAILLmService) as dependencies of each other (five files to upload or
  publish to a feed, and `lib/` of a dependency is still not a place for templates or the enricher); (c) contentFiles (NuGet's `contentFiles/any/any/…` is
  a build-time concept: the loader would need the same extraction anyway, under a less obvious name).
- **Packing**: `Samples/CodebaseAnalysis.Designer/pack-plugin.ps1` (publish, stage, generated nuspec, `dotnet pack --no-build -p:NuspecFile=…`) → `bin/nupkg/`
  (git-ignored). Own = the Designer and the project references that are not `AITaskAgent.*`; dependencies = their PackageReferences from
  `obj/project.assets.json` (highest version per id), with `exclude="Build,Analyzers"`. Not in `plugin/`: third-party DLLs, `runtimes/`, satellite folders,
  `*.deps.json`, `*.pdb`, `*.xml`. Result: 28 MB, 235 files under `plugin/` (27 MB of it the `dotnet-enricher/` CLI, a separate process that ships whole).
- **Upload dialog and errors**: `availableIn: "host"` (above); the dialog says that dependencies missing from the local repository will be downloaded from the
  configured sources at install (with no source: upload them or configure one); `nuget-not-found` while installing ends with `PluginLoader.NotFoundHint`, and
  the UI's text for the code says the same in Spanish.
- **Verified for real**: CLI `plugin install --file … --trust` against a folder feed (12.3 s) and against nuget.org with an empty cache (17.9 s): `loaded`, 7
  `Ca*` steps, 14 tools, 3 hooks, templates, schema, no warnings or problems, only `CodebaseAnalysis.Designer.dll` scanned; the same closure (22 packages,
  83 MB of `.nupkg`, identical lock hashes) from both sources; `validate codebase-analysis.zip` valid; without a source: exit 1 `nuget-not-found` naming
  `DocumentFormat.OpenXml`. Browser (Playwright) against a backend on 5191 with a temporary workspace and nuget.org: upload, trust dialog with the 18
  downloaded dependencies and their hashes, plugin `loaded` (NuGet), seven palette entries with the «Plugin» badge, the package validating.
- **Tests**: `AITaskAgent.NugetLoader.Tests` 115/115 (`PluginNupkgFolderTests` 5 new, `LocalNupkgRepositoryTests` +1), designer Core 585/585, CLI 206/206,
  Web 364/364, `CodebaseAnalysis.Designer.Tests` 10/10 (`NupkgPluginTests` 2 new; also 10/10 with the real package and the folder feed), ClientApp plugins
  suites 49/49 and `tsc -b --noEmit` clean. Open points: `PENDIENTES.md` «CodebaseAnalysis as one `.nupkg`».

### Deviations of the IExtension-driven plugins (2026-10-22)

- **Why** (decision of the user, not reopened): type discovery alone gave too many problems (FileTools tools shadowed by the plugin's, and so on), so a plugin goes
  back to being driven by one class that implements `IExtension` and registers everything it needs; `IExtension` gets `Info()`, which declares what it will register;
  the attributes `[StepTag]`, `[Hook]`, `[Validator]`, `[ResultValidator]`, `[ResponseType]` stay; the qualified tool names and the plugin prompts view stay (§25.3a).
- **Decided by the assistant (delegated; the user was told the objection)**: (1) nothing of the plugin runs before approval: the trust dialog keeps the metadata
  scan and gains the entry point found by metadata; `Info()` and `Register` run after approval. (2) `Info()` is not trusted: the loader compares it with the real
  registration (`info-mismatch`, a warning naming the items) and the real registration is what the designer uses. (3) One `IExtension` drives the plugin; several =
  stopping `multiple-extensions` from metadata before trust; none = type discovery as before. (4) `plugin.json` may say `"extension": "Full.Type.Name"`; manifest
  `contract` stays 1 (the field is optional, nothing existing changes shape). (5) The framework's own extensions return a minimal but accurate `Info()`;
  `IExtension.Info()` has a default (empty); the discovery helpers are `ExtensionDiscovery` (Abstractions) and `AssemblyDiscovery` (Extensions.Llm).
- **`IPluginConfiguration`: kept, as the fallback.** The extension declares its configuration with `ConfigurationType`/`ConfigurationModel()` (which already
  existed) plus two new default members, `ConfigurationDefaults(PluginContext)` and `RootSections`, so schema, defaults, root sections and `x-secret` marking lose
  nothing (the designer reads them through `ExtensionConfiguration`, an `IPluginConfiguration` over the extension, so there is one code path). A plugin with an
  extension ignores any `IPluginConfiguration` class it also has; a plugin without one still uses it.
- **Chosen beyond the brief**: (a) `ExtensionInfo` is one list of `ExtensionEntry(kind, name, type, description)` with `ExtensionKinds`, not a list per kind (fewer types,
  the DTO and the comparison are the same loop); the comparison key is the name for step, hook, validator, response type and root section and the class for the rest
  (a tool created by a tool source is not compared, only the source). (b) The builder members in Abstractions take types and objects (`AddToolType(Type)`,
  `AddToolInstance(object)`, `AddToolSourceInstance(object)`, `AddTemplateSourceInstance(object)`) because Abstractions knows neither `ITool` nor `ITemplateSource`; the
  typed forms are extension methods in `AITaskAgent.Extensions.Llm` (`AddTool<T>`, `AddTool(ITool)`, `AddToolSource`, `AddTemplates`, `AddToolsFromAssembly`,
  `AddTemplatesFromAssembly`, `AddAllFromAssembly`); their default bodies throw `NotSupportedException`, so the test builders and any third-party builder still compile.
  (c) The framework's service-collection builder (`AddExtension`) wires hooks into the collection's `HookRegistry` (source `di`), tools and tool sources under their
  `ITool`/`IToolSource` interface (found by name: Core cannot reference them), templates as `ITemplateSource`, response types as `RegisteredResponseType` singletons.
  (d) `ExtensionCapture` (the builder that registers nowhere) and `PluginExtensionRunner.Run` are public: the loader and `services.AddPlugin(assembly, id)` (a
  host-referenced assembly with one `IExtension` is driven by it) share them. (e) The services a plugin's `Register` adds to `builder.Services` are copied as
  descriptors into each engine collection. (f) New loader codes: stopping `extension-not-found`, `invalid-extension`, `extension-failed`; warnings `info-failed`,
  `ignored-extension`; `legacy-extension` is gone and `legacyExtensions` became `extensions`/`extension` in the contributions JSON. (g) The pre-trust collision check
  uses the scan's preview; the real registration is checked again after loading (all or nothing, as before). (h) The legacy `extension.json` key `extensionType` is read
  as `extension`. (i) The CLI prints the entry point (`plugin info`, trust request) and, for an installed plugin, `Info: N declared, M registered, K different`.
  (j) The plugins view got a «Punto de entrada» fact, an «Info() y registro» section (declared vs registered, difference marked) and Spanish texts for the new codes;
  the trust dialog shows the entry point and says that its code runs only after approval; the mock API simulates an extension per plugin.
- **Migrated**: `Samples/CodebaseAnalysis.Designer` (`CodebaseAnalysisExtension`: `AddAllFromAssembly`, `Info()` = `AssemblyDiscovery.Describe` + root sections;
  `CodebaseAnalysisConfiguration` is now a static helper) and `Samples/ExamplePlugin` (`ExampleExtension`). The Designer plugin was repacked
  (`pwsh Samples/CodebaseAnalysis.Designer/pack-plugin.ps1`, same version 1.0.0, 28 MB) and `NupkgPluginTests` ran against that real package with a folder feed
  (`CODEBASE_ANALYSIS_PLUGIN_NUPKG`, `CODEBASE_ANALYSIS_PLUGIN_FEED` = the user's global NuGet packages folder, read-only): loaded, `Info()` matches, no warnings.
  **A version cannot be replaced in the local repository** (`package_conflict`): if the previous 1.0.0 of this package was already uploaded, delete it from the
  repository first or bump the version in `plugin.json` and `-Version`.
- **Not changed**: `wwwroot` (not rebuilt: `npm run build` is needed for the backend on 5190; the Vite on 5174 serves the sources, and the `src` files were touched at
  the end), the user's workspace, zips and servers.
- **Tests** (run serially, 2026-10-22): `AITaskAgent.NugetLoader.Tests` 124/124 (+9, `PluginExtensionTests`: one extension, mismatch, two extensions before trust,
  named by the manifest, named but missing, none = discovery, nothing runs before approval with marker files and load contexts compared, `Register` throws, `Info()`
  throws); framework `Tests` 408/408 (+13, `ExtensionInfoTests`: default `Info()`, `Difference`, discovery helpers, capture, the service-collection builder, a builder that
  cannot wire things); `Extensions.CSharp.Tests` 34/34 (+1); designer Core 594/594 (+4 `PluginExtensionEngineTests`: the engine wires what `Register` registered, the
  mismatch in the DTO, a pending plugin shows only its entry point, a host-referenced assembly; one test rewritten in `PluginServiceTests`); CLI 206/206; Web 366/366;
  `CodebaseAnalysis.Designer.Tests` 11/11 (also with the real package and a feed); `CodebaseAnalysis.Core.Tests` 1533/1533; `tsc -b --noEmit` clean; Vitest 1209/1209
  (128 files; `PluginExtension.test.tsx` is new: the card, the mismatch, the pending plugin, the trust dialog, the mock). No real LLM, no MCP, no network. Open points:
  `PENDIENTES.md` «Plugins driven by an `IExtension`».

### Deviations and backend of the code editor (2026-10-23)

- **Framework contract addition (additive, `AITaskAgent.Extensions.CSharp`).** New file `Compilation/CSharpCompletion.cs`: `public sealed record CSharpCompletionRequest(string Text,
  int Caret, string? StepId = null)`, `public sealed record CSharpCompletionItem(string Label, string Kind, string? Detail = null, int Boost = 0)`, `public sealed record
  CSharpCompletionList(int From, int To, IReadOnlyList<CSharpCompletionItem> Items)` (with `static Empty(int caret)`), and on `CSharpUnitCompiler` (now `partial`)
  `public const int MaxCompletionItems = 300` and `public Task<CSharpCompletionList> CompleteAsync(CSharpUnitSources context, CSharpCompletionRequest request,
  CancellationToken ct = default)`. Nothing existing changed its signature. The compilation is built by one private `CreateCompilationAsync` shared with
  `CompileCoreAsync` (references, usings, schema classes, shared file, generated `Steps` wrapper), so what the editor binds against and what is emitted cannot drift; the
  editor's variant keeps a body's trailing empty lines and an empty shared file. Completion never emits, loads or touches the disk cache. It uses the `SemanticModel` only
  (no `Microsoft.CodeAnalysis.Features`/`Workspaces`): after a dot (`x.`, `x?.`, `Ns.`) the left side is resolved (namespace or type → its members, static ones for a type;
  a value → `LookupSymbols(position, type, includeReducedExtensionMethods: true)` without statics); otherwise `LookupSymbols(position)` filtered by the typed prefix
  (case-insensitive), and with nothing typed only locals, parameters and members of the containing type. Constructors, operators, accessors, unspeakable names and
  `Workflow.WorkflowNamespaceAnchor` are dropped; overloads are grouped. `UnitReferences.FromFiles` now caches a `MetadataReference` per file, size and write time (a
  completion per keystroke would otherwise reread every NuGet assembly).
- **Designer Core.** `PackageUnits` factors the compiler creation (`CompilerAsync`: host references, the package's locked NuGet packages, `SharedAssemblySet.HostWins`;
  `Plain` without packages) and adds `CompleteAsync(package?, request)`, used by the new public `CodeCompletion` (`Engine/CodeCompletion.cs`: `CompleteCSharpAsync`,
  `Snippets()`, `CodeSnippetDto`), registered as a Core singleton and in `WorkspaceServices.Bound`. With a package whose NuGet packages cannot be materialized the completion
  falls back to the host references instead of failing.
- **One source for the parameter headers: `Engine/HookDocs.cs`.** Structure by reflection (the delegate's `Invoke` and the validator shape; each arguments record's public
  instance properties and its own public methods such as `As<T>()`; read-only = no public setter or `init`), meanings from one table written from the framework's source
  (`Meanings` by (type, member), `ArgsMeanings` by type, `SlotMeanings` for `next` and the return by (hooks type, slot), `ValidatorReturns`) and two fixed lines for
  `IStepResult` and `PipelineContext` the first time each appears. `HookSlotDto` gained `header` (additive, last property; `HookStubs.Describe` fills it), and
  `GET /api/code/snippets` returns the validator and result validator skeletons built with the same header. Header text is Spanish (shown to the user). `HookStubs`
  now names the parameter of an `Action<T>` slot (`enrichActivityBefore/After`) `args` instead of the BCL's `obj`, like the `Func` slots.
- **Web.** `Api/CodeEndpoints.cs` (`MapCodeEndpoints` next to the others, so the cross-site guard and `ApiPipeline` apply): `POST /api/code/complete` and
  `GET /api/code/snippets` as described in `core-api.md` («Code editor»). Decided differently: the 5 s timeout is configurable
  (`WorkflowDesigner:CodeCompletionTimeoutMilliseconds`, default 5000) so the timeout path is testable; the concurrency slot (2) is released when the work really ends, not
  when the request gives up on it; a zero `boost` is left out of the JSON.
- **Tests** (run serially, 2026-10-23): `AITaskAgent.Extensions.CSharp.Tests` 48/48 (+14, `CSharpCompletionTests`); designer Core 619/619 (+25:
  `HookDocsTests` for every slot of the built-in steps and both validator kinds, the table's completeness, read-only vs read-write, the snippets compiled by the real
  compiler; `HookStubCompileTests` now compiles every stub with its header; `CatalogTests` checks the slot's shape with `header` last); Web 376/376 (one run had a flaky `LibraryConfigEndpointTests.Apply_replace...` 500 that passed alone and on the full rerun) (+10,
  `CodeEndpointTests`). CLI not touched. No real LLM, no MCP, no network.
