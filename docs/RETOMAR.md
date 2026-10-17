# Retomar (2026-10-17)

Único fichero de retomada del proyecto: solo lo pendiente, el contexto mínimo y el prompt de arranque. Lo que se cierra sale de aquí.

## 1. Estado del repo

- El trabajo de la pasada compartida, el anti-bucle, el enriquecedor C# y los nodos del LLM en el grafo entró en el commit `5feaa92` (rama `consolidation`) [V: `git log`]. El documento de retomada anterior decía «todo sin commitear»; ya no es cierto.
- Los arreglos de la auditoría de documentación (2026-10-17) están commiteados en `consolidation`: documentación y diagramas revisados contra el código, carpetas `docs/plans`, `project_notes`, `experimentos` y `auditoria` retiradas (los datos que usan los tests viven en `Samples/CodebaseAnalysis.Core.Tests/Fixtures/`), y ~25 defectos corregidos o documentados. Sin commitear: `AITaskAgent.sln` y `.claude/settings.local.json`.
- Tests de referencia al cerrar la tanda [V]: `AITaskAgent.Tests` 183 (+2 omitidos que necesitan el V8 nativo), `CodebaseAnalysis.Core.Tests` 1533, `DotNetEnricher.Tests` 81, `CodebaseAnalysisWeb.Tests` 148, cliente Vitest 277, `OpenAILLmService.Tests` 118, `GeminiLlmService.Tests` 12.
- Aplazado con la limitación escrita en el código y en `docs/guides/human-in-the-loop.md`: suspensión de una rama de `ParallelStep` (no se propaga) y estado de instancia de `StatelessTemplateLlmStep`; arreglarlo exige tocar `IResume`. Sigue abierto el `StepTimeoutEvent` que no sale porque `RetryMiddleware` captura la cancelación. Decisiones en la memoria `project_defect_fixes_2026_10_17`.

## 2. Pasada compartida: pendiente

Desactivada por defecto (`CodebaseAnalysis:SharedPass:Enabled`). Pruebas reales sobre la copia `C:\Users\juan.rozano\m\fw2` de `m\fw`.

| Ejecución | Hallazgos | Severidad | Coste lector |
|---|---|---|---|
| Experimento Claude (referencia de recall) | 187 (182 confirmados) | P0 1, P1 7, P2 72, P3 102 | n/a |
| GPT-SOL razonamiento bajo (CLI, 16-oct) | 101 (97) | P1 3, P2 45, P3 49 | 1,63 USD; total 1,93 USD, 15,7 min |
| GPT-SOL razonamiento medio (web, 16-oct) | 114 (113) | P1 5, P2 53, P3 55 | 1,95 USD, ~17 min |
| GPT-SOL razonamiento alto (web, 17-oct) | 128 (127) | P1 8, P2 58, P3 61 | lector 2,34 USD; total 2,41 USD; ~8-10 min |

Registros del experimento y de GPT-SOL medio/alto: `Samples/CodebaseAnalysis.Core.Tests/Fixtures/shared-pass-2026-10-16/` (`registry.json`, `findings_all.json`, `out/`, `results/_shared_pass_registry_medium.json` y `_high.json`, `scripts/compare_registries.py`, README del experimento). Es la única copia en el repo del registro medio (el alto también está en `fw2\reports`, el bajo se sobrescribió) [I]. Comparar: `python <fixtures>/scripts/compare_registries.py <registro> [referencia...]`.

Comparación del razonamiento alto: `CloneForBranch` y la suspensión de rama de `ParallelStep` ya salen como P1 (REG-005 y REG-007 del registro alto). `ConvertOrThrow` (cultura) no sale en ningún nivel como P1 (solo REG-052, P2, sobre `LlmHelpers.cs:26`). La inclusión `@[ruta]` baja a P2 frente al P0 de Claude. 126 de las 182 entradas confirmadas de Claude tienen equivalente por ubicación (medio: 116).

**Los 8 P0/P1 del experimento de Claude que el usuario debe juzgar** (IDs del `registry.json` del experimento):
1. REG-001 P0 (seguridad): `@[ruta]` se expande tras la interpolación y sin directorio base en el camino de DI por defecto, `Support/Template/TinyTemplateRendered.cs:109`.
2. REG-002 P1: `ConvertOrThrow` usa `Parse` con la cultura actual, `LLM/Support/LlmHelpers.cs:147`.
3. REG-003 P1: `CloneForBranch` descarta la ruta actual; ramas paralelas en un `LoopStep` reutilizan resultado cacheado, `Core/Models/PipelineContext.cs:153`. **Arreglado el 2026-10-17** (la rama conserva el prefijo del padre; `Tests/BranchPathCacheTests.cs`).
4. REG-004 P1: `ParallelStep` no propaga la suspensión de una rama, `Core/Steps/ParallelStep.cs:114`. **Confirmado y aplazado** (limitación escrita).
5. REG-005 P1: la fusión multi-fichero `---` no fusiona pasos, `YAML/Execution/YamlPipelineFactory.cs:66`. **Arreglado el 2026-10-17** (lectura en bucle de documentos; reproducido antes el fallo con dos `.yaml`).
6. REG-006 P1: el DAG YAML se valida por aristas pero se ejecuta como cadena lineal, `YAML/Execution/YamlPipelineFactory.cs:200`.
7. REG-007 P1: nombres de plantilla por defecto terminan en `.md` y el proveedor añade otro `.md`, `LLM/Steps/TemplateLlmStep.cs:36`. **Arreglado el 2026-10-17** (`FileTemplateProvider` tolera el `.md`; si la plantilla no existe y parece un fichero, error claro).
8. REG-008 P1: includes recursivos sin límite de profundidad ni detección de ciclos, `Support/Template/TinyTemplateRendered.cs:268`.

P1 nuevos del registro alto, ya verificados contra el código el 2026-10-17: logging de argumentos de tools y de respuestas LLM (REG-001/002) es decisión de diseño, no se toca; cancelación convertida en error (REG-003) es real pero P2 y sigue abierta; esquema que omite `[Range]` en enteros y `[Required]` (REG-004) y `GetParameters` con el indexador de `ParallelResult` (REG-006) **arreglados**; fallback sin reconstruir la solicitud (REG-008) **era un falso positivo** (tras el fallback `attempt` vuelve a 1 y `ExecuteAsync` reconstruye `state.Request`, `RetryMiddleware.cs:59`, `BaseLlmStep.cs:307`).

Pendiente técnico:
1. Taxonomía cerrada de categorías en lector y consolidador (hoy 95 distintas).
2. Revisión visual en navegador del panel «Pasada compartida» y del detalle de tool (`ToolCallDetail`): solo probados con jsdom.
3. Juez: su tope de llamadas no es exacto con tandas en paralelo (el del lector sí).
4. Idea para `ConvertOrThrow`: semilla determinista en el enriquecedor (Parse/ToString sensibles a la cultura, regla tipo CA1305) en lugar de depender del LLM.
5. Fases siguientes, con OK del usuario: informes de errores, deuda y seguridad que consuman el registro; funcional y arquitectura desde contratos; prueba real con GPT-SOL con tope (≤ 50 USD); recall en `csharp-simple` y A/B carpeta-prosa frente a lote estructurado; incrementalidad por hash; enriquecedor Java con JDK 17 (solo hay JRE 1.8).
6. Informe Markdown del registro para el visor (`reports\custom\`): no hecho.
7. Heredado: borrar la copia `m\fw` lo ejecuta el usuario (`rm -rf /c/Users/juan.rozano/m/fw` y `delete_project` de cbm).

### Cómo se lanza la pasada
No hace falta reindexar `fw2`: su grafo ya está enriquecido con `EnricherVersion` 5. Antes hay que parar el proceso viejo de `CodebaseAnalysisWeb`, que bloquea las DLL.

```powershell
dotnet run --project Samples/CodebaseAnalysisWeb/CodebaseAnalysisWeb.csproj -- --CodebaseAnalysis:SharedPass:Enabled true
```

En la web se abre `C:\Users\juan.rozano\m\fw2` y se elige solo el informe `deadcode` (con los diez cuesta más; los de modo B lanzan investigaciones por subsistema).

Por CLI, solo con flags documentados en `Samples/CodebaseAnalysisPipeline/README.md` (un `--help` lanzó una vez un análisis real de ~13-14 USD):

```powershell
dotnet run --project Samples/CodebaseAnalysisPipeline/Samples.CodebaseAnalysisPipeline.csproj --no-build -- --repo "C:\Users\juan.rozano\m\fw2" --reports deadcode --CodebaseAnalysis:SharedPass:Enabled true --CodebaseAnalysis:SharedPass:MaxCostPerRunUsd 25
```

Topes por defecto: 2 USD por lote, 10 por etapa, 25 por ejecución, 1000 llamadas, 50 M de tokens, 90 min. Resultados: `fw2\reports\_shared_pass_registry.json`, grafo `fw2\.codebase\codebase.db` (nodos `Finding`, `Anchor`, `Run`, contratos `llm_*`) y log en `fw2\logs\`.

### Compilar en esta máquina
El origen NuGet `C:\Asisa\NuGetLocal` no existe: `dotnet restore --ignore-failed-sources -p:WarningsNotAsErrors=NU1801` y compilar/testear con `--no-restore`. `McpIntegration.Tests` y `FileTools.Tests` no corren aquí.

## 3. Cómo se prueba (web y tests)

- El backend del usuario ocupa el 5180 y bloquea las DLL: compilar con `-p:OutDir=<scratch>` y levantar una segunda instancia: `ASPNETCORE_ENVIRONMENT=Development dotnet <scratch>/CodebaseAnalysisWeb.dll --urls http://127.0.0.1:5181 --contentRoot C:\GeminiCli\AITask\AITaskAgent\Samples\CodebaseAnalysisWeb --CodebaseAnalysis:Web:SettingsPath=<scratch>\web-settings.json`. Sin `SettingsPath` se pisa `%LOCALAPPDATA%\CodebaseAnalysisWeb\settings.json`. `--contentRoot` debe ser absoluto (uno relativo se resuelve contra la carpeta de la DLL).
- Para servir una SPA compilada en el scratch: `--webroot <scratch>\spa --staticWebAssets=<scratch>\no-existe.json` (en Development, el manifiesto de static web assets sirve el `wwwroot` del repo por encima de `--webroot`).
- Parar la instancia por puerto (`Get-NetTCPConnection -LocalPort 5181`), nunca filtrando por línea de comandos.
- Tests .NET: `dotnet test <proyecto> -p:OutDir=<scratch>` mientras el backend del usuario esté vivo. `SampleConfigurationTests.Every_role_names_a_configured_provider_and_a_model` falla con `OutDir` fuera del repo (busca `AITaskAgent.slnx` subiendo desde los binarios, `Tests/SampleConfigurationTests.cs:29-35`); no es regresión.
- No crear sesiones de chat ni ficheros dentro de `C:\BBVA\...`; para el chat, usar `C:\Users\juan.rozano\Desktop\TestAgenteAnalisis`. cbm sobre el repo Java no escribe en él (`persistence` false por defecto); limpiar con `codebase-memory-mcp.exe cli delete_project {"project":"<nombre>"}`.
- JRE 1.8.0_503 sin `javac` ni Maven; hay python.

## 4. Pendientes menores y objeciones abiertas

- Ideas pendientes de `/brainstorm`: catálogo de dominios barato, documento funcional por dominio, proveedor sobre Responses API, reducir coste, y **selección de subsistemas por el usuario** (`Preparation:IncludeSubsystems`, sin implementar).
- Analizador Java, incidencias menores: 27 declaraciones sin nodo (su QN lo ocupa un `Field` de cbm), `Function` gemelos de métodos de interfaz sin parchear, un `struts-config` con XML inválido (`parametrizacion/markUp`).
- Pendientes de decisión: valores por defecto de tiempos del framework; fusión de informes (ver memoria `project_report_catalog_decisions`).
- Tanda 5 de Roslyn/javac aplazada: nulabilidad (S05), clones y analizadores estándar (constantes ya hecha).
- Defectos de la corrida 3 del 16-oct sin resolver: informe 09 con secciones duplicadas (documento emitido dos veces) y citas con carpeta equivocada (`YAML/Execution/SchemaCompiler.cs` real en `YAML/Compilation/`). Comprobar si la pasada compartida los dejó obsoletos [?].
- Auditoría de samples, sin decidir: migrar `AdvancedLlmPipeline` a `AddAITaskAgent()` + `IOptions<LlmEnvironment>`, credenciales de desarrollo en claro, JSON muerto de `GeminiTestApp`.
- Objeción D1 (sistema de tags): `FinalizeAsync` descarta el tráfico intermedio de tools al cerrar el turno; es intencional, pero entre turnos el modelo solo recuerda lo que diga su respuesta final. Dejarlo como decisión escrita.
- Objeción D2 (decisión del usuario, 2026-09-04): un tag se procesa al cerrar, venga del razonamiento o del contenido. Objeción: impide las reglas R2 (última ocurrencia) y R3 (conflicto con tool call concurrente); un esqueleto bien formado en el razonamiento puede volver a sobrescribir el fichero. Trade-off asumido a cambio de un mecanismo único.

## 5. PENDIENTE DE DECISIÓN DEL USUARIO: plan de flujo de análisis (2026-10-06)

Solo hechos, sin decisión por mi parte:
- La cabecera del plan `flujo-analisis-plan-final` decía «pendiente del adelante; nada implementado», pero su §8 registra «adelante con condiciones»: (1) reemplazar mensajes con `MessageHistory.ReplaceMessage` en el framework (opción b); (2) OK al cambio de contrato de T1 (`cached_tokens` en el resultado y precio de caché en el proveedor); (3) el protocolo de notas va también al modo A, desde T2, con interruptor de configuración del sample; (4) tope de 30 USD para la medida real (T6).
- El código ya tiene `MessageHistory.ReplaceMessage` (`Framework/LLM/Conversation/MessageHistory.cs:74`) y `CachedInputPricePerMillion` (`Framework/LLM/Configuration/LlmProvider.cs:25,51`) [V].
- No he comprobado qué tareas T1-T7 están hechas ni si T6 (medida real) se ejecutó [?]. Los documentos de retomada anteriores seguían diciendo «sin adelante».
- Decisión que falta: dar el plan por aprobado y cerrado, o confirmar qué parte queda por hacer.

## 6. Prompt de arranque (sesión limpia)

> Retomamos el trabajo del proyecto AITaskAgent en `C:\GeminiCli\AITask\AITaskAgent` (rama `consolidation`). Lee `docs/RETOMAR.md` entero y la memoria `project_shared_pass_design_2026_10_16`. Primera tarea: decirme qué P0/P1 de la sección 2 sigo sin tener juzgados por ti y proponer el orden de las fases siguientes. No lances análisis reales sin mi OK y un tope. Responde en español, breve, con `[V]/[I]/[?]` y TL;DR final.
