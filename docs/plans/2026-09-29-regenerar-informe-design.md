# Regenerar un informe desde el progreso y timeouts configurables del análisis

Estado: diseño y plan del 2026-09-29, escritos sin sesión de preguntas porque el usuario pidió seguir sin consultar
(«Sigue con todo lo que te pido, sin preguntar nada»). Las decisiones que tomé en su lugar están en el Decision Log para
que las revise. Ámbito: `Samples/CodebaseAnalysis.Core` y `Samples/CodebaseAnalysisWeb`.

## Propósito

El usuario pidió dos cosas: que los límites de tiempo dependan del proyecto y no sean fijos, y un botón en la pantalla de
progreso para volver a generar un informe que ha fallado sin repetir los demás pasos.

El fallo que lo motivó es el run `1ce275643d68` de TestAgenteAnalisis (28/09 a las 20:19). En él, `Auditor_CodeSmells` se
cortó esperando la respuesta del LLM a los 600 s exactos del arranque. Lo cortó el timeout global del pipeline del
framework, que son 10 minutos por defecto:
- el valor por defecto está en `Framework/Configuration/TimeoutOptions.cs:9`;
- se aplica al `IPipeline` *singleton* en `Framework/Configuration/ServiceCollectionExtensions.cs:46-61`;
- está en el log `TestAgenteAnalisis/logs/analysis-pipeline-20260928.log:312,374-386`.

Al ser una excepción y no una rama con error, la tolerancia de `TolerantReportsParallelStep` no llegó a actuar. El run
quedó `failed`, sin resumen ejecutivo y con `failedReports` vacío. Con el repositorio Java objetivo (cbm, el analizador
Java y nueve informes), el análisis pasaría siempre de esos 10 minutos.

## Hechos del sistema

- Los timeouts de los pasos del análisis están fijos en el código:
  - `DetectAndIndexGraphStep`, 15 min;
  - `BdpEnricherStep`, 20 min;
  - el paralelo de informes, 20 min (`AnalysisPipelineFactory.cs` al crear `TolerantReportsParallelStep`);
  - cada auditor, 15 min (`CreateReportStep`).
- La sección `PipelineExecution` de `appsettings.analysis.json` (`RetryAttempts`, `StepTimeoutMinutes`) no la lee ningún
  código.
- `IPipeline.ExecuteAsync` admite un `pipelineTimeout` que sustituye al valor por defecto en esa ejecución
  (`Framework/Core/Execution/IPipeline.cs:31`).
- Las facetas (`ReportFacetInput`) solo viven en memoria (`SqlFacetExtractorStep.cs:440`). Los informes se escriben en
  cuanto termina su auditor (`CustomStatelessTemplateLlmStep.cs:112-130`). La síntesis ya admite ejecuciones parciales y
  lee de disco los informes que no ha generado la ejecución (`ExecutiveSynthesizerStep.cs:127-155`).
- La web conserva solo la última ejecución y sus eventos en memoria (`AnalysisRunManager.cs:38`,
  `AnalysisEventLog.cs:16-19`). Un análisis nuevo reinicia la vista (`useAnalysisRun.ts:35-69`).
- El servidor manda `failedReports` como nombres de fichero (`ExecutiveSynthesizerStep.cs:132`) y el cliente los compara
  con tipos (`runReducer.ts:476-488`). Con datos reales, la marca por servidor no se dispara nunca.
- `DetectAndIndexGraphStep` retira del grafo los informes del catálogo (`ReincorporateKnowledgeAsync`, `:109,127-141`) y
  `CodebaseAnalysisPipeline` los reincorpora en su `finally`.

## Decision Log

| # | Decisión | Por qué | Coste aceptado |
|---|---|---|---|
| R1 | El análisis usa su propio timeout de pipeline, `CodebaseAnalysis:Timeouts:PipelineMinutes`, con 240 por defecto y 0 para no poner límite. Se pasa como `pipelineTimeout` a `IPipeline.ExecuteAsync` | Es la causa del fallo real. El timeout global del framework también lo usan el chat y otros pipelines, así que no se toca | Un análisis colgado tarda más en cortarse |
| R2 | Los timeouts fijos del análisis pasan a `CodebaseAnalysis:Timeouts`: `DetectMinutes` 15, `EnrichMinutes` 20, `ReportsMinutes` 60, `AuditorMinutes` 15 y `SynthesisMinutes` 15, todos con 0 = sin límite. `JavaAnalyzer:TimeoutSeconds`/`StepTimeoutSeconds` siguen en su sección. La sección `PipelineExecution`, que nadie lee, se sustituye | «Habrá proyectos y proyectos». El paralelo pasa de 20 a 60 min porque con nueve auditores concurrentes y reintentos 20 min no dan margen | Más configuración que documentar |
| R3 | «Regenerar» continúa la misma ejecución, con el mismo `runId`, en lugar de crear una nueva | La pantalla conserva el estado de los demás informes y el usuario ve el reintento en su sitio | El estado de la ejecución puede volver de terminal a `running` |
| R4 | La regeneración ejecuta un pipeline reducido sobre el grafo que ya existe: `PrepareRegenerationStep` → `SqlFacetExtractorStep` → informes pedidos → síntesis (opcional). No hay detección, indexación ni enriquecimiento | Es lo que pide el usuario. Las facetas se recalculan porque son SQL de unos segundos y así no quedan obsoletas si el grafo cambió | Los informes regenerados se basan en el grafo de ahora, que puede no ser el de la ejecución original |
| R5 | La síntesis se incluye por defecto, con una casilla «Actualizar también el resumen ejecutivo» | El resumen se construye con todos los informes: sin él quedaría desfasado o, como en el fallo real, no existiría | Una llamada más al LLM |
| R6 | El botón sale en cada carril de auditor con estado `failed` cuando la ejecución no está en curso. No depende de `failedReports`, porque en el fallo real venía vacío | Es donde el usuario ve el error | No hay botón para un informe correcto (queda fuera) |
| R7 | Solo se puede regenerar la última ejecución que el servidor tiene en memoria. Tras reiniciar el servidor no hay botón | La web no persiste ejecuciones y persistirlas es otro alcance | Hay que relanzar el análisis |
| R8 | `failedReports` se corrige en el cliente: el reducer acepta nombres de fichero y tipos | Cambiar el contrato del servidor rompería el banner, que muestra ficheros | Una tabla tipo→fichero en el cliente |

## Diseño

**Core.** `AnalysisPipelineFactory.CreateRegenerationPipeline(templates, roles, input, synthesis, sp)` compone los pasos
de R4.

`PrepareRegenerationStep` (`IStep<AnalysisPipelineInput, GraphStatusResult>`, fase `facets`):
- deja en el contexto `PipelineInput`, `CbmDbPath` y `KnowledgePaths`, como `DetectAndIndexGraphStep.cs:36-38`;
- falla con un mensaje claro si no existe la BD o no tiene grafo estructural;
- aplica `GraphSchema.EnsureAsync`;
- retira del grafo los informes del catálogo con el mismo código que la detección. Se reincorporan en el `finally` del
  pipeline.

`AnalysisRunner.RegenerateAsync(...)` hace de gemelo de `RunAsync`. Los timeouts salen de un *record*
`AnalysisTimeouts.FromConfiguration`.

**Web.** `POST /api/analysis/{runId}/regenerate` con `{ reports: ["CodeSmells"], synthesis: true }`. Respuestas:
- 202 con el estado;
- 404 si la ejecución no existe;
- 409 si hay una ejecución en curso, un turno de chat o una operación de conocimiento, igual que al lanzar;
- 400 si un informe es desconocido o la lista está vacía.

`AnalysisRunManager.Regenerate`:
- vuelve a poner la ejecución en `running`;
- emite `run.regeneration_started` con `{ reports, synthesis }` y sigue con el mismo registro de eventos y `seq`;
- al terminar emite otro `run.completed` con el estado recalculado. `failedReports` es el de antes, menos los regenerados
  que han salido bien, más los nuevos fallos.

`IAnalysisWorkflow` gana `RegenerateAsync`, y `FakeAnalysisWorkflow` lo implementa para los tests.

**Cliente.**
- `api.regenerateReports(runId, reports, synthesis)`.
- `useAnalysisRun.regenerate` sigue con el mismo SSE y no hace `reset`.
- `runReducer` trata `run.regeneration_started`:
  - estado `running`;
  - los auditores pedidos pasan a `pending`, sin error;
  - las fases `facets`, `reports` y, si procede, `synthesis` pasan a `pending`;
  - el resto se conserva.
- `AuditorGrid` pinta el botón «Regenerar» (`btn btn--small`) en los carriles `failed` cuando no hay ejecución en curso.
  El botón se desactiva mientras arranca la regeneración y muestra el error del servidor si lo hay.
- La casilla del resumen va encima de la rejilla y solo se ve si hay algún carril que se pueda regenerar.

## Criterios de aceptación

1. Sin configuración, un análisis no se corta a los 10 minutos: el timeout del pipeline es 240 min. Con
   `CodebaseAnalysis:Timeouts:PipelineMinutes=1` se corta al minuto.
2. Todos los timeouts de R2 salen de la configuración y 0 los desactiva; hay tests.
3. En una ejecución terminada con un auditor `failed`, su carril muestra «Regenerar». Al pulsarlo, solo se ejecutan la
   preparación, las facetas, ese auditor y (si está marcada) la síntesis. Los demás carriles conservan su estado, el
   informe se escribe en `reports/` y el carril pasa a `succeeded`.
4. Con una ejecución en curso, el endpoint devuelve 409 y el botón no aparece.
5. Tras una ejecución cancelada durante los informes, regenerar un auditor cancelado funciona igual.
6. `failedReports` con nombres de fichero marca los carriles correctos en el cliente.
7. Pasan las pruebas de la web (.NET y Vitest) y una prueba real en una segunda instancia sobre TestAgenteAnalisis:
   se cancela en mitad de los informes, se regenera uno con síntesis y se comprueban los ficheros escritos y los eventos.

## Plan

1. **Agente G (implementación).** R1-R8 con sus tests:
   - Core: `AnalysisTimeouts`, factoría, `PrepareRegenerationStep`, `AnalysisRunner.RegenerateAsync`, mapper de fases
     (`PrepareRegenerationStep` en `facets` y como paso de primer nivel), `appsettings.analysis.json` y README;
   - Web: endpoint, manager, workflow, fake y tests;
   - Cliente: api, hook, reducer, AuditorGrid, fixtures y tests.
2. **Agente H (verificación real).** Criterio 7 sobre TestAgenteAnalisis en el puerto 5181. Al terminar, `npm run build`
   de la SPA en un `--webroot` del scratch, sin tocar el `wwwroot` del repo, que sirve el backend del usuario.

## Change Log

Cambios sobre el diseño de arriba. Los C1-C5 los aplicó el agente G al implementarlo; C6 y C7 salen de la verificación
real del agente H y los aplicó el agente I.

| # | Cambio | Por qué |
|---|---|---|
| C1 | El botón «Regenerar» sale también en los carriles `cancelled`, no solo en los `failed` (corrige R6) | La cancelación deja los auditores interrumpidos en `cancelled`, y sin esto el criterio 5 no tenía botón |
| C2 | `SynthesisMinutes` vale 15 por defecto y no 10 (R2 ya lo recoge) | Lo decidí en R2 para igualarlo al de un auditor: la síntesis es una sola llamada al LLM con los nueve informes de entrada, y con 10 min un modelo lento se queda sin margen. El código tenía 10 fijos |
| C3 | La lista vacía da 400 `invalid_request`; un informe desconocido sigue dando 400 `unknown_report` | Pedir cero informes es una petición mal formada, no un nombre que no existe |
| C4 | Un informe cuenta como regenerado cuando llega su `report.written`, no por el resultado del pipeline | Es lo mismo que ve el cliente, y el paralelo tolerante no dice qué rama falló si no fallan todas |
| C5 | La CLI no ofrece la regeneración; solo `AnalysisRunner.RegenerateAsync` y la web | La CLI no conserva una ejecución anterior sobre la que continuar (R3, R7) |
| C6 | El límite de `PipelineMinutes` lo aplica `CodebaseAnalysisPipeline` con un token propio y al motor le llega `Timeout.InfiniteTimeSpan` (corrige R1, que lo pasaba como `pipelineTimeout`). Si vence sin que el llamante haya cancelado, el resultado es un error con el texto «El análisis superó el límite de N min (CodebaseAnalysis:Timeouts:PipelineMinutes); súbelo o pon 0 para quitarlo.», que también se emite como `context.Report` de nivel Error en el momento del corte. Vale igual para la regeneración, que usa el mismo wrapper | Con el timeout en el motor, el run terminaba `failed` con «A task was canceled.», el mismo síntoma opaco del fallo del 28/09. Con el token en el wrapper, el timeout se distingue de la cancelación del usuario por qué token ha saltado, sin comparar tiempos, y el framework no cambia. El aviso se emite al vencer porque, al volver del motor, este ya ha cerrado el canal de eventos |
| C7 | Al terminar una regeneración, `failedReports` incluye además los informes pedidos en la ejecución original que no tienen `report.written` en toda la ejecución, la original y sus regeneraciones, y el estado es `completed_with_failures` si queda alguno. El aviso de fin del cliente es de aviso, no de error, cuando termina con fallos, y lista los ficheros que faltan pasando los tipos por la tabla de R8 | Tras cancelar un análisis de dos informes (`failedReports` vacío) y regenerar uno bien, el segundo `run.completed` salía `succeeded` y el aviso verde ocultaba que el otro seguía sin informe |
