# Retomar (2026-10-17): pasada compartida, grafo completo y anti-bucle

Documento de retomada para una sesión limpia. Todo está **sin commitear** en `consolidation` (≈290 ficheros modificados y ≈140 sin seguimiento entre esta y las tandas anteriores). Sustituye en parte a `2026-10-16-bucles-de-herramientas-y-calidad-informes-retomar.md`: su tarea principal, el bucle de herramientas, está hecha.

## 1. Qué hay hecho (todo con tests verdes)

Tests de referencia: `CodebaseAnalysis.Core.Tests` 1515, `DotNetEnricher.Tests` 81, `CodebaseAnalysisWeb.Tests` 148, cliente Vitest 271, `OpenAILLmService.Tests` 118. `McpIntegration.Tests` y `FileTools.Tests` no se ejecutan en esta máquina porque el origen NuGet `C:\Asisa\NuGetLocal` no existe; hay que usar `dotnet restore --ignore-failed-sources -p:WarningsNotAsErrors=NU1801` y compilar con `--no-restore`.

- **Anti-bucle** (plan `docs/plans/2026-10-16-grafo-completo-antibucle-pasada-compartida-plan.md`, paquete A y correcciones):
  - Detector por ventana de 10 iteraciones en `Framework/LLM/Steps/BaseLlmStep.LlmInvocation.cs`, con **progreso por tags**: un turno en el que se ejecutó un tag no cuenta repetición. Antes cortaba investigaciones que releían tras anotar; 30 de 30 cortes del 16-oct eran falsos positivos.
  - Recordatorio y retención de notas pendientes en `Samples/CodebaseAnalysis.Core/Notes/`.
  - Un corte lleva a un último turno sin herramientas.
  - `Auditors:MaxToolIterations` pasa a 100.
  - El Framework gana `ToolLoopStopRequestedException` y la marca `ToolLoopStopMessage` (aceptadas por el usuario).
- **Enriquecedor C#** (`Samples/DotNetEnricher`, `EnricherVersion` 5): CALLS exactas que sustituyen a las de cbm, THROWS, constructores, APIs sensibles, concurrencia, hashes y procedencia (`enriched_by`, etc.). **Java (B2) no está hecho:** solo hay JRE 1.8 y hace falta un JDK 17.
- **Nodos del LLM en el grafo** (`Enrichment/AnalysisGraph/LlmRegistryGraphWriter.cs`): `Finding`, `Anchor`, `Summary`, `Batch` y `Run`, más las vistas `graph_domain` `findings` y `llm_contracts`.
- **Pasada compartida** (`Samples/CodebaseAnalysis.Core/SharedPass/`, `Steps/SharedPassStep.cs`, plantillas `templates/shared_pass_*.md`), **desactivada por defecto** (`CodebaseAnalysis:SharedPass:Enabled`):
  - un lote por subsistema del mapa de dominios, leído con 4 workers en paralelo y sin esperar por niveles;
  - validación de anclas y de cobertura, consolidación con rúbrica y juez;
  - registro en el grafo y en `reports/_shared_pass_registry.json`.
  - Roles `SharedPassReader` (gpt-6-sol, **reasoning_effort alto**, temperatura 1 porque gpt-6-sol con razonamiento no admite otra) y `SharedPassJudge` (medio).
- **Web:** panel «Pasada compartida» (evento `sharedpass.progress`) y detalle de entrada y salida de cada tool al hacer clic (`ToolCallDetail`). Para ello `LlmTool` rellena `AdditionalData["parameters"/"result"]` (aceptado por el usuario). **No se ha visto en un navegador**, solo con jsdom.
- **Revisión independiente** hecha y corregida. Rechazado el P2-12: `INSTANCE_OF` exige partir los patrones en entradas y cambia el contrato del registro.

## 2. Pruebas reales (copia `C:\Users\juan.rozano\m\fw2` de `m\fw`; `m\fw` y sus informes de GPT-SOL no se tocan)

| Ejecución | Hallazgos | Severidad | Coste lector | Notas |
|---|---|---|---|---|
| Experimento Claude (agentes, `docs/experimentos/2026-10-16-pasada-compartida/`) | 187 (182 confirmados) | P0 1, P1 7, P2 72, P3 102 | n/a | referencia de recall |
| GPT-SOL, razonamiento bajo (CLI, 16-oct) | 101 (97 conf.) | P1 3, P2 45, P3 49 | 1,63 USD; total 1,93 USD, 15,7 min | registro sobrescrito |
| GPT-SOL, razonamiento medio (web, 16-oct) | 114 (113 conf.) | P1 5, P2 53, P3 55 | 1,95 USD, ~17 min | copia en `docs/experimentos/2026-10-16-pasada-compartida/results/_shared_pass_registry_medium.json` |
| GPT-SOL, razonamiento **alto** (web, 17-oct) | 128 (127 conf.) | P1 8, P2 58, P3 61 | lector 2,34 USD; total 2,41 USD; ~8-10 min en paralelo | copia en `results/_shared_pass_registry_high.json` |

Faltan, en bajo y en medio, dos P1 del experimento de Claude: `ConvertOrThrow` con cultura (`LLM/Support/LlmHelpers.cs:147`) y `CloneForBranch`, que pierde la ruta (`Core/Models/PipelineContext.cs:153`). Las categorías salen sin taxonomía cerrada (84 distintas).

Para comparar un registro con otro o con el de Claude: `python docs/experimentos/2026-10-16-pasada-compartida/scripts/compare_registries.py <registro> [referencia...]`.

## 3. Pendiente

1. **Resultado del razonamiento alto (ya comparado el 17-oct; copia en `results/_shared_pass_registry_high.json`).** `CloneForBranch` y la suspensión de rama de `ParallelStep` ya salen como P1 (REG-005 y REG-007). `ConvertOrThrow` (cultura) sigue sin salir en ningún nivel (solo REG-052, P2, sobre `LlmHelpers.cs:26`). La inclusión `@[ruta]` baja a P2 (REG-010 y REG-011) frente al P0 de Claude y el P1 del medio. 126 de las 182 entradas confirmadas de Claude tienen equivalente por ubicación (medio: 116). P1 nuevos que el autor debe verificar: logging de argumentos de tools (REG-001) y de respuestas LLM (REG-002), cancelación convertida en error (REG-003), esquema que omite restricciones (REG-004), `GetParameters` con el indexador de `ParallelResult` (REG-006) y fallback sin reconstruir la solicitud (REG-008). Idea para `ConvertOrThrow`: semilla determinista en el enriquecedor (Parse/ToString sensible a la cultura, regla tipo CA1305) en lugar de depender del LLM. Categorías: 95 distintas.
2. **Taxonomía cerrada de categorías** en el lector y el consolidador.
3. **Revisión visual** del panel y del detalle de tool en el navegador.
4. **Juez:** su tope de llamadas no es exacto con tandas en paralelo (el del lector sí).
5. **Siguientes fases del plan**, con OK del usuario:
   - que los informes de errores, deuda y seguridad consuman el registro;
   - funcional y arquitectura desde contratos;
   - prueba real con GPT-SOL con tope (≤ 50 USD);
   - recall en `csharp-simple` y A/B carpeta-prosa frente a lote estructurado;
   - incrementalidad por hash;
   - enriquecedor Java con JDK 17.
6. **Heredado:** borrar la copia `m\fw` (lo ejecuta el usuario: `! rm -rf /c/Users/juan.rozano/m/fw` y el `delete_project` de cbm; yo no puedo), el plan `docs/plans/2026-10-06-flujo-analisis-plan-final.md` sigue sin «adelante», y nada está commiteado.

## 4. Cómo se lanza la pasada

Reindexar **no** hace falta: el grafo de `fw2` ya está enriquecido con la versión 5 y el código no cambia. `--force-reindex` lo regeneraría desde cero, necesita `codebase-memory-mcp` y no aporta nada.

Backend, desde la raíz del repo (antes hay que parar el proceso viejo de `CodebaseAnalysisWeb`, que bloquea las DLL):

```powershell
dotnet run --project Samples/CodebaseAnalysisWeb/CodebaseAnalysisWeb.csproj -- --CodebaseAnalysis:SharedPass:Enabled true
```

En la web se abre `C:\Users\juan.rozano\m\fw2` y se elige solo el informe `deadcode`: con los diez cuesta más y los de modo B lanzan investigaciones por subsistema.

Por CLI: `dotnet run --project Samples/CodebaseAnalysisPipeline/Samples.CodebaseAnalysisPipeline.csproj --no-build -- --repo "C:\Users\juan.rozano\m\fw2" --reports deadcode --CodebaseAnalysis:SharedPass:Enabled true --CodebaseAnalysis:SharedPass:MaxCostPerRunUsd 25`. Solo flags documentados en `Samples/CodebaseAnalysisPipeline/README.md`: un `--help` lanzó una vez un análisis real.

Topes por defecto: 2 USD por lote, 10 por etapa, 25 por ejecución, 1000 llamadas, 50 M de tokens y 90 minutos.

Resultados: `fw2\reports\_shared_pass_registry.json`, el grafo `fw2\.codebase\codebase.db` (nodos `Finding`, `Anchor`, `Run` y contratos `llm_*`) y el log en `fw2\logs\`. Un informe en Markdown del registro para el visor (`reports\custom\`) **no está hecho**.

## 5. Prompt de arranque para la sesión limpia

> Retomamos el trabajo de la pasada compartida en `C:\GeminiCli\AITask\AITaskAgent` (rama `consolidation`, todo sin commitear). Lee `docs/project_notes/2026-10-17-pasada-compartida-retomar.md` entero y la memoria `project_shared_pass_design_2026_10_16`. Primera tarea: comparar el registro de la ejecución con razonamiento alto (`C:\Users\juan.rozano\m\fw2\reports\_shared_pass_registry.json`) con el medio (`docs/experimentos/2026-10-16-pasada-compartida/results/_shared_pass_registry_medium.json`) y con el de Claude, usando `scripts/compare_registries.py`, y decir si aparecen `ConvertOrThrow` y `CloneForBranch`. No lances análisis reales sin mi OK y un tope. Responde en español, breve, con `[V]/[I]/[?]` y TL;DR final.
