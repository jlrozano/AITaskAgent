# Plan: preparación de datos e investigación por subsistema

Estado: **pendiente de aprobación (gate)**, 2026-10-04. Spec: `docs/plans/2026-10-04-preparacion-informes-por-subsistema-design.md`
(léela antes de ejecutar: define el porqué de cada decisión). Nada de esto está implementado ni editado.
`[V]` leído en código o en la base de datos con su `fichero:línea`; `[I]` inferido.

Rutas: **Core** = `Samples/CodebaseAnalysis.Core`, **Tests** = `Samples/CodebaseAnalysis.Core.Tests`. Pruebas con
`dotnet test Samples/CodebaseAnalysis.Core.Tests --no-restore -p:OutDir=<scratch>` (el backend del usuario bloquea las DLL
del repo; las notas de retomada lo recogen). El vault `_VAULT/` no está montado: la fuente de cada tarea es el código leído.

## Reglas de ejecución (de CLAUDE.md y de las decisiones vigentes)

- Test primero y verlo fallar. Un commit por tarea solo cuando el usuario lo pida; nunca `push`.
- Genericidad: ningún nombre del repositorio de prueba (Notificaciones incluida) en código, plantillas ni configuración.
- Sin añadir opciones a `Framework/` para este sample; se usan los puntos de extensión (`StepBase.Timeout` ya es `set`).
- No fallar duro: un subsistema que falla es aviso y «no cubierto», no un error del pipeline.
- Texto plano entre pasos; sin JSON entre auditores.

## Hallazgos del plan que la spec no recogía (a confirmar con el usuario)

1. **Interruptor `CodebaseAnalysis:Preparation:Mode` (`Subsystems` por defecto, `Off`)**. Sin él no hay forma de medir A8 «frente a la
   ejecución actual» con el mismo código. Con `Off` el pipeline queda como hoy.
2. **Sin subsistemas elegidos** (grafo diminuto, como el del test offline), el modo B degrada a un auditor normal, sin
   investigaciones. Hace falta para que `OfflinePipelineTests` (que espera 9 informes con un LLM de mentira,
   `OfflinePipelineTests.cs:21-100` `[V]`) siga pasando.
3. **Rol del LLM que nombra los subsistemas:** se reutiliza `FunctionalAuditor`, para no obligar a configurar un rol nuevo en
   `LlmRoleFactory.RequiredRoles`.
4. **Fase de progreso:** los pasos 5-8 se asignan a la fase `Facets` existente (`AnalysisEventMapper.cs:40-49` `[V]`) para no
   tocar la SPA; la rejilla sigue mostrando un carril por informe (pregunta abierta 5 de la spec, aplazada).
5. **Plantillas sin condicionales:** el motor solo interpola `{{ prop }}` y `{{ include }}` `[V]`
   (`Framework/Support/Template/TinyTemplateRendered.cs:61-100`); un placeholder vacío se pinta vacío y deja un warning. Los
   prompts deben redactarse para tolerar el bloque vacío.

## Mapa de ficheros

Nuevos en Core: carpeta `Preparation/` (`PreparationOptions`, `RepositoryInventory*`, `DomainMap*`, `CoveragePlan*`,
`TimeBudget*`, `AuditorStepRegistry`, `PreparationArtifacts`), pasos en `Steps/` (`RepositoryInventoryStep`, `DomainDiscoveryStep`,
`CoveragePlanningStep`, `TimeBudgetStep`, `SubsystemInvestigationReportStep`) y plantillas en `templates/`.
Modificados: `AnalysisPipelineFactory.cs`, `Models/ReportFacetInput.cs`, `Progress/AnalysisContextKeys.cs`,
`Progress/AnalysisEventMapper.cs`, `Progress/LlmUsageTracker.cs`, `Reports/ReportValidator.cs`, `Steps/ExecutiveSynthesizerStep.cs`,
`appsettings.analysis.json` y las plantillas `report_*_prompt.md`. Tests nuevos en Tests con el nombre de cada clase.

---

## Fase 0: entorno

### T0. Compilar y medir la línea base
- **Problema `[V]`:** `NuGet.config` lista `C:\Asisa\NuGetLocal`, que no existe, y `dotnet build` falla con NU1301 en el restore.
  `Samples/CodebaseAnalysis.Core.Tests/obj/project.assets.json` existe, así que `--no-restore` debería compilar.
- **Pasos:** `dotnet build Samples/CodebaseAnalysis.Core.Tests --no-restore -p:OutDir=<scratch>` y `dotnet test` igual. Si el
  proyecto necesita paquetes que no están restaurados, **parar y preguntar** (opciones: restaurar con
  `--source https://api.nuget.org/v3/index.json`, o crear la carpeta vacía); no tocar `NuGet.config` por cuenta propia.
- **Aceptación:** número de tests de Core en verde registrado como línea base (las notas dicen 623 con JDK, `[I]` sin comprobar).
- **Fuente:** `NuGet.config`, `docs/project_notes/asistente-chat-y-enriquecimiento-estado.md` («Cómo se prueba»).

## Fase 1: configuración, modelos y artefactos

### T1. `PreparationOptions` y `TimeBudgetOptions`
- **Ficheros:** `Core/Preparation/PreparationOptions.cs`, `Core/Preparation/TimeBudgetOptions.cs`, `Core/appsettings.analysis.json`;
  test `Tests/PreparationOptionsTests.cs`.
- **Contenido:** secciones `CodebaseAnalysis:Preparation` (`Mode`, `MaxSubsystemsPerReport` = 8, `MaxParallelInvestigations` = 4,
  `VocabularyMergeThreshold`, `MinSubsystemFiles`, pesos de puntuación, `StopTokens`) y `CodebaseAnalysis:TimeBudget` (base, `k`,
  mínimo y máximo de la investigación, la consolidación y el auditor en modo A). Mismo patrón que `AnalysisTimeouts`:
  `FromConfiguration` y `FromServices`, con 0 o negativo = sin límite y valores inválidos = defecto con aviso.
- **Test primero:** defectos, lectura desde `TestConfig.FromPairs`, valores inválidos, `Mode` desconocido cae a `Subsystems`.
- **Aceptación:** respalda D9 (K = 8, concurrencia 4).
- **Fuente:** `Hosting/AnalysisTimeouts.cs` (patrón completo), `Tests/TestSupport.cs:14-23` (`TestConfig`).

### T2. Modelos de los artefactos y su E/S
- **Ficheros:** `Core/Preparation/PreparationModels.cs` (`RepositoryInventory`, `DomainMap`, `Subsystem`, `CoveragePlan`,
  `ReportCoverage`, `TimeBudgetPlan`), `Core/Preparation/PreparationArtifacts.cs`, claves nuevas en
  `Core/Progress/AnalysisContextKeys.cs`; test `Tests/PreparationArtifactsTests.cs`.
- **Contenido:** registros inmutables, listas ordenadas, serialización estable (misma entrada = mismos bytes salvo
  `generated_at`). `PreparationArtifacts` escribe y lee `reports/_repository_inventory.json`, `_domain_map.json`,
  `_coverage_plan.json` y `_time_budget.json`, y expone la huella del grafo con la que se calcularon (para T24).
- **Test primero:** ida y vuelta, orden estable, lectura de un fichero ausente o corrupto devuelve null con aviso.
- **Fuente:** `Progress/AnalysisContextKeys.cs` (convención de claves), `Reports/RunMetaFile.cs` (patrón de fichero `_*.json`).

## Fase 2: `RepositoryInventoryStep` (A1)

### T3. Clasificador de source sets
- **Ficheros:** `Core/Preparation/SourceSetClassifier.cs`; test `Tests/SourceSetClassifierTests.cs`.
- **Contenido:** función pura ruta → `production | test | qa | debug | generated | samples | unknown`, por segmentos de ruta y
  convenciones (`src/test`, `Tests`, `*.Tests`, `qa`, `debug`, `generated`, `target`, `obj`, `samples`...). Sin evidencia
  devuelve `unknown`, nunca se adivina. Reutiliza `RepositoryPathFilter.IsExcluded` para las carpetas de herramientas.
- **Test primero:** tabla de rutas (Java Maven, .NET, mixtas) y un caso `unknown`.
- **Fuente:** `Support/RepositoryPathFilter.cs:8-20`, `Enrichment/RepositoryScan.cs:55-80` (`TestRoots`, carpetas de build).

### T4. Contador de LOC de producción
- **Ficheros:** `Core/Preparation/ProductionLineCounter.cs`; test `Tests/ProductionLineCounterTests.cs`.
- **Contenido:** líneas físicas por fichero de producción para `.java`, `.cs`, `.jsp`, `.js`, `.ts` (lista configurable), con
  lectura tolerante de codificación (UTF-8 o Latin-1) y orden ordinal. No se apoya en el grafo: los nodos `Class` de `Online`
  llevan `"lines":0` `[V]` (consulta a `~/.cache/codebase-memory-mcp/C-BBVA-…-Online-src.db`).
- **Test primero:** repo temporal con producción y test, fichero Latin-1, fichero vacío, enlaces ignorados.
- **Fuente:** patrón de lectura UTF-8/Latin-1 en `FileTools/Tools/ViewFileTool.cs` y `FileTools/Tools/GrepSearchTool.cs` (aviso de Latin-1, tanda A de las notas).

### T5. Lectura del grafo para el inventario
- **Ficheros:** `Core/Preparation/InventoryGraphReader.cs`; test `Tests/InventoryGraphReaderTests.cs` con `CbmTestGraph`.
- **Contenido:** entradas (nodos con `is_entry_point`, aristas `HANDLES` de Struts, métodos `main`), tablas referenciadas por
  DAO, estado de enriquecimiento por lenguaje (`enrichment_runs`, que no existe en un índice sin enriquecer: devuelve «sin
  enriquecer»), y `cbm_match` de `enrichment_runs.details`. Todas las consultas sobre `properties` con `json_valid`
  (60 nodos de `Online` tienen JSON inválido y `json_extract` lanza `malformed JSON` `[V]`).
- **Test primero:** grafo sin `enrichment_runs`, con nodos de JSON inválido, con `HANDLES`.
- **Fuente:** `Enrichment/Java/JavaGraphWriter.cs:812-829` (`cbm_match`), `Steps/DotNetEnricherStep.cs` (`ReadRunsAsync`), `Tests/Enrichment/CbmTestGraph.cs:25-100`.

### T6. `RepositoryInventoryStep`
- **Ficheros:** `Core/Steps/RepositoryInventoryStep.cs`; test `Tests/RepositoryInventoryStepTests.cs`.
- **Contenido:** `IStep<ReportFacetInput, ReportFacetInput>` (devuelve el mismo `ReportFacetInput`, porque la cadena de tipos
  es `SqlFacetExtractorStep → ReportFacetInput → paralelo de informes` `[V]`, `SqlFacetExtractorStep.cs:12`). Une T3-T5, escribe
  el artefacto, deja la clave de contexto y pone la versión compacta en `ReportFacetInput.RepositoryInventory`. Si falla algo,
  aviso y se devuelve el input intacto.
- **Test primero (A1):** dos ejecuciones sobre el mismo grafo y repositorio dan JSON idéntico salvo `generated_at`; lenguaje sin
  enriquecedor declarado «sin enriquecer»; un fallo interno no rompe el paso.
- **Fuente:** `Steps/SqlFacetExtractorStep.cs:29-72` (contrato de paso y de contexto), `Steps/LanguageEnricherStep.cs:77` (`DetectedLanguages`).

## Fase 3: `DomainDiscoveryStep` (A2)

### T7. Grafo agregado a nivel de tipo
- **Ficheros:** `Core/Preparation/TypeGraphBuilder.cs`; test `Tests/TypeGraphBuilderTests.cs`.
- **Contenido:** nodos = clases, interfaces y enums de producción; aristas ponderadas agregando `CALLS` de método a método hasta el
  tipo que lo define (vía `DEFINES_METHOD`, `[V]` presente en `Online`: 148 382 aristas) y sumando `USES_TYPE`, `READS`/`WRITES`,
  `INHERITS`, `IMPLEMENTS` cuando existan. Identificadores y aristas ordenados de forma ordinal.
- **Test primero:** grafo sintético con métodos y `DEFINES_METHOD`; sin `USES_TYPE` (índice sin enriquecer) también funciona.
- **Fuente:** consulta a la base de `Online` (tipos de arista y recuentos), `Tests/Enrichment/CbmTestGraph.cs`.

### T8. Detección de comunidades determinista
- **Ficheros:** `Core/Preparation/CommunityDetector.cs`; test `Tests/CommunityDetectorTests.cs`.
- **Contenido:** propagación de etiquetas o Louvain con semilla fija y recorrido en orden ordinal, sin `Dictionary` de orden no
  definido. Entrada: T7; salida: comunidad por tipo.
- **Test primero (A2):** dos cliques unidos por un puente quedan separados; el resultado es idéntico en dos ejecuciones y al
  permutar el orden de inserción.
- **Fuente:** decisión D11 de la spec (cbm no trae `community_id`: 0 nodos en `Online`, `[V]`; las facetas lo leen en `SqlFacetExtractorStep.cs:450-457`).

### T9. Vocabulario y fusión de comunidades
- **Ficheros:** `Core/Preparation/NameTokenizer.cs`, `Core/Preparation/VocabularyMerger.cs`; test `Tests/VocabularyMergerTests.cs`.
- **Contenido:** tokenizador de nombres (camelCase, PascalCase, `_`, `.`, números) con la lista `StopTokens` de configuración
  (`impl`, `dao`, `action`, `util`... por defecto, configurable); término dominante por comunidad por frecuencia y rareza; fusión
  de comunidades que comparten un término dominante por encima de `VocabularyMergeThreshold`.
- **Test primero (A2):** repositorio sintético cuya capacidad comparte un término en dos ramas distantes del árbol de
  paquetes queda en **un** subsistema; dos capacidades distintas con un término genérico compartido siguen separadas. Ningún
  término del repositorio real en el código.
- **Fuente:** caso real `[V]`: 258 clases con «notific» en la ruta bajo `…/oficinas/notificacionOficinas/` y `…/actions/multiRol/notificaciones/`.

### T10. Clusters estructurales, asignación de ficheros e identificadores
- **Ficheros:** `Core/Preparation/StructuralClusterer.cs`, `Core/Preparation/DomainMapBuilder.cs`; test `Tests/DomainMapBuilderTests.cs`.
- **Contenido:** siembra por carpeta, acciones y JSP de Struts del mismo path y tablas de los mismos DAO; combina con T8-T9;
  asigna cada fichero de producción a un subsistema o a `unassigned`; identificador estable derivado de la carpeta raíz o del
  hash ordenado de sus ficheros; subsistemas bajo `MinSubsystemFiles` se absorben en el más cercano o en `unassigned`.
- **Test primero (A2):** cada fichero de producción tiene exactamente una asignación; mismos ids en dos ejecuciones; grafo pobre
  (sin referencias) cae a la estructura y lo declara.
- **Fuente:** spec §4.2; `Enrichment/Web/` (Struts), `Steps/SqlFacetExtractorStep.cs` (tablas).

### T11. Nombrado por LLM con validación en código
- **Ficheros:** `Core/Steps/DomainNamingLlm.cs` (o dentro de `DomainDiscoveryStep`), plantillas `templates/domain_naming_prompt.md` y
  `domain_naming_system.md`; test `Tests/DomainNamingTests.cs`.
- **Contenido:** sub-step `CustomStatelessTemplateLlmStep<…, LlmStringStepResult>` con el rol `FunctionalAuditor`, invocado sin
  middleware (patrón de `VerifyReportStep.cs:83`). Entrada: resumen compacto por cluster. Salida: texto parseado a nombre,
  descripción y prioridad por id. Validación: todo cluster conserva su asignación; los que el LLM omita o devuelva mal usan un
  nombre derivado de la carpeta o el término dominante; un fallo del LLM es un aviso y el mapa se emite con nombres derivados.
- **Test primero (A2):** `CannedLlmService` que devuelve menos subsistemas, texto basura y error; en los tres, el mapa tiene todos
  los clusters.
- **Fuente:** `Steps/VerifyReportStep.cs:75-83`, `Tests/OfflinePipelineTests.cs:55-60` (LLM falso), `Support/AnalysisTemplateProvider.cs`.

### T12. `DomainDiscoveryStep`
- **Ficheros:** `Core/Steps/DomainDiscoveryStep.cs`; test `Tests/DomainDiscoveryStepTests.cs`.
- **Contenido:** une T7-T11 con el inventario del contexto (LOC por subsistema), escribe `_domain_map.json`, clave de contexto y
  `ReportFacetInput.DomainMap` compacto. Aviso, no error, si falta el inventario.
- **Test primero:** de grafo a artefacto con LLM falso; mapa vacío ante grafo vacío.
- **Fuente:** T6 (mismo contrato de paso).

## Fase 4: `CoveragePlanningStep` (A3)

### T13. Puntuación, selección y preguntas por informe
- **Ficheros:** `Core/Preparation/CoveragePlanner.cs`, plantillas `templates/_investigation_questions_funcional.md`, `_flujos.md`,
  `_errores.md`, `_arquitectura.md`; test `Tests/CoveragePlannerTests.cs`.
- **Contenido:** puntuación determinista con pesos configurables (centralidad, entradas, tablas, estados o enums, integraciones,
  riesgo de uso dinámico); exclusión de test, qa y generado; selección de los K primeros por informe más una cola larga
  muestreada con un orden estable (no aleatorio); lista de excluidos con motivo; preguntas leídas de plantilla (editables, no
  escritas en código) y evidencia mínima por subsistema (entradas, flujos completos, caminos alternativos, un contraejemplo).
- **Test primero (A3):** como mucho K elegidos, todo excluido con motivo, salida idéntica en dos ejecuciones, K se respeta con
  cientos de subsistemas.
- **Fuente:** spec §4.3; `Hosting/AnalysisRunOptions.cs` (patrón de configuración por informe), `Models/AnalysisPipelineInput.cs:82-122`.

### T14. `CoveragePlanningStep`
- **Ficheros:** `Core/Steps/CoveragePlanningStep.cs`; test `Tests/CoveragePlanningStepTests.cs`.
- **Contenido:** lee inventario y mapa del contexto, escribe `_coverage_plan.json`, clave de contexto y
  `ReportFacetInput.CoveragePlan` (para el modo A).
- **Test primero:** paso con contexto completo; sin mapa, plan vacío con aviso.

## Fase 5: `TimeBudgetStep` (A4)

### T15. `AuditorStepRegistry` y `Timeout` mutable
- **Ficheros:** `Core/Preparation/AuditorStepRegistry.cs`; test `Tests/AuditorStepRegistryTests.cs`.
- **Contenido:** objeto por pipeline (no estático) que la factoría crea y pasa por constructor a `TimeBudgetStep` y a los
  envoltorios. Registra por `ReportType` el paso del informe (`IStep` con `Timeout` escribible). Para los pasos que ya heredan de
  `StepBase` (`BaseLlmStep.cs:45`, `StepBase.cs:69`) basta asignar; el envoltorio de T21 declara `Timeout { get; set; }`.
- **Test primero:** registrar, sobrescribir `Timeout` de un `StepBase` y comprobar que `TimeoutMiddleware` lo usa
  (`TimeoutMiddleware.cs:30`).
- **Fuente:** `Framework/Core/Steps/StepBase.cs:67-69`, `Framework/Core/Execution/Middlewares/TimeoutMiddleware.cs:30`.

### T16. Calculadora de presupuesto
- **Ficheros:** `Core/Preparation/TimeBudgetCalculator.cs`; test `Tests/TimeBudgetCalculatorTests.cs`.
- **Contenido:** `clamp(base + k · LOC, mín, máx)` para investigación, consolidación y auditor en modo A; conciliación con el techo
  global `AnalysisTimeouts.Pipeline` (0 = sin límite) dividiendo por la concurrencia; si lo supera, reduce K empezando por los
  subsistemas de menor puntuación y devuelve los avisos con el recorte.
- **Test primero (A4):** valores en los extremos, techo que obliga a recortar (orden de recorte comprobado), sin techo no recorta,
  coeficientes de configuración.
- **Fuente:** `Hosting/AnalysisTimeouts.cs:12-70` (`PipelineMinutes` = 240, 0 = sin límite).

### T17. `TimeBudgetStep`
- **Ficheros:** `Core/Steps/TimeBudgetStep.cs`; test `Tests/TimeBudgetStepTests.cs`.
- **Contenido:** lee plan y mapa del contexto, calcula con T16, escribe `Timeout` en cada paso del registro y rellena la tabla
  `subsistema → TimeSpan` de los envoltorios, escribe `_time_budget.json` y los avisos de recorte.
- **Test primero (A4):** tras el paso, los `Timeout` son los calculados; un paso de prueba que duerme más que su `Timeout`
  asignado por el paso termina por timeout en el motor real; con presupuesto por encima del techo hay aviso y K reducido.
- **Fuente:** T15-T16.

## Fase 6: informes

### T18. Campos nuevos en `ReportFacetInput`
- **Ficheros:** `Core/Models/ReportFacetInput.cs`; test en `Tests/FacetExtractionTests.cs` o uno nuevo.
- **Contenido:** propiedades de texto con defecto `"[]"`/`"{}"`/`""`: `RepositoryInventory`, `DomainMap`, `CoveragePlan`,
  `InvestigationNotes`, `CoverageSummary`; método `CloneForReport()` (la clase es un `StepResult` plano de propiedades `[V]`,
  `ReportFacetInput.cs:7`; el envoltorio necesita una copia por informe porque las ramas comparten el input).
- **Test primero:** la copia no comparte estado y mantiene todas las facetas.
- **Fuente:** `Models/ReportFacetInput.cs:100-140`.

### T19. Nombres de paso, eventos y consumo por informe
- **Ficheros:** `Core/AnalysisPipelineFactory.cs`, `Core/Progress/AnalysisEventMapper.cs`, `Core/Progress/LlmUsageTracker.cs`; tests
  `Tests/AnalysisEventMapperTests.cs`, `Tests/LlmUsageTests.cs`.
- **Contenido:** el envoltorio y la consolidación usan `Auditor_<tipo>` (mismo carril, mismo reparto de tokens); las
  investigaciones usan `Investigation_<tipo>_<id>` con `TryParseInvestigationStepName`; el mapeador y el contador de consumo
  atribuyen `Investigation_*` al informe; los pasos 5-8 se mapean a la fase `Facets` y entran en `IsTopLevelStep`.
- **Test primero:** el nombre se parsea y vuelve; el consumo de una investigación suma al informe; los pasos nuevos emiten eventos
  de fase `facets`.
- **Fuente:** `AnalysisPipelineFactory.cs:30-50` (patrón `Auditor_`/`Verify_`/`Verifier_`), `Progress/AnalysisEventMapper.cs:40-80`,
  `Progress/LlmUsageTracker.cs:100-115`.

### T20. Plantillas de investigación (revisión del usuario)
- **Ficheros:** `templates/report_funcional_investigacion_prompt.md`, `report_flujos_…`, `report_errores_…`, `report_arquitectura_…`;
  test `Tests/InvestigationTemplatesTests.cs`.
- **Contenido:** misma system prompt que el auditor; prompt nuevo por informe con brief del subsistema, preguntas, evidencia
  mínima y salida en **texto plano con citas `ruta:línea`**, sin JSON. Redactadas en español genérico, sin nombres de repositorio.
  Se incluyen `_common_rules.md` y `_graph_rules.md` como en las plantillas actuales.
- **Test primero:** cada plantilla se renderiza con un input de prueba sin placeholders sin resolver (F1 deja warning, se
  comprueba que no hay ninguno) y sin términos de un repositorio concreto.
- **Aviso:** la calidad real del texto no se prueba sin LLM; **el usuario debe revisarlas** antes de la medición A8.
- **Fuente:** `templates/report_funcional_prompt.md` (estructura y `@[_common_rules.md]`), motor en `TinyTemplateRendered.cs:61-100`.

### T21. `SubsystemInvestigationReportStep`
- **Ficheros:** `Core/Steps/SubsystemInvestigationReportStep.cs`; test `Tests/SubsystemInvestigationReportStepTests.cs`.
- **Contenido:** `IStep<ReportFacetInput, SingleReportResult>` con `Name = Auditor_<tipo>` y `Timeout { get; set; }`. Por cada subsistema
  elegido, una investigación `CustomStatelessTemplateLlmStep<SubsystemInvestigationInput, LlmStringStepResult>` con el
  rol y las tools del auditor, invocada con `ExecuteStepAsync` y un token propio (tabla de T17), paralelismo con
  `SemaphoreSlim(MaxParallelInvestigations)`. Después arma `InvestigationNotes` y `CoverageSummary` en una copia del input (T18) y
  llama al auditor existente (consolidación), que escribe el informe (`CustomStatelessTemplateLlmStep.cs`, único punto de
  escritura). Sin subsistemas elegidos, llama directamente al auditor (hallazgo 2). Fallo de una investigación: aviso y «no
  cubierto». Fallan todas: error del paso, como `TolerantReportsParallelStep.cs:34-38`.
- **Test primero (A5):** N subsistemas = N investigaciones y 1 consolidación; una investigación falla y el informe se escribe con
  ese subsistema en «no cubierto»; todas fallan y hay error; una supera su límite y se cancela solo ella; cero subsistemas
  equivale a un auditor normal; respeta la concurrencia máxima.
- **Fuente:** `Steps/VerifyReportStep.cs:75-125` (patrón completo), `Steps/CustomStatelessTemplateLlmStep.cs:17-175`,
  `Steps/TolerantReportsParallelStep.cs`.

### T22. Plantillas de consolidación y de modo A
- **Ficheros:** `templates/report_{funcional,flujos,errores,arquitectura}_prompt.md` (modo B), `report_{codesmells,codigomuerto,apis,deuda,di}_prompt.md` (modo A);
  test ampliado de `Tests/InvestigationTemplatesTests.cs`.
- **Contenido:** modo B incorpora `{{ investigation_notes }}` y `{{ coverage_summary }}` con la instrucción de redactar el
  apartado **Cobertura** y de tolerar el bloque vacío (no hay condicionales); modo A incorpora el inventario, el mapa y el plan
  como contexto y el protocolo de mínimos.
- **Test primero:** con el input por defecto (bloques vacíos) la plantilla renderiza sin errores y sin dejar `{{`.
- **Fuente:** hallazgo 5 del plan; `templates/report_*_prompt.md`.

### T23. Cableado en la factoría
- **Ficheros:** `Core/AnalysisPipelineFactory.cs`; tests `Tests/OfflinePipelineTests.cs` (sin cambios de comportamiento) y
  `Tests/PreparationPipelineTests.cs` (nuevo).
- **Contenido:** `CreatePipeline` inserta los pasos 5-8 entre `SqlFacetExtractorStep` y el paralelo de informes
  (`AnalysisPipelineFactory.cs:146-151`); `CreateReportsStep` construye el envoltorio para los informes en modo B cuando
  `Mode = Subsystems` y registra todo en el registro; con `Mode = Off` no se insertan pasos y el pipeline queda como hoy.
  `CreateRegenerationPipeline` (`:166-190`) carga los artefactos del disco si la huella del grafo coincide y, si faltan o
  no coinciden, ejecuta los pasos 5-8 tras `SqlFacetExtractorStep`. `CreateVerificationPipeline` no cambia.
- **Test primero:** `OfflinePipelineTests` sigue dando 9 informes con `Mode = Subsystems` (grafo de un nodo, degradación de T21);
  nuevo test con un grafo de dos subsistemas y LLM falso: existen los cuatro `_*.json`, los informes en modo B tienen «Cobertura»;
  con `Mode = Off` no se crea ningún artefacto; la regeneración reutiliza los artefactos.
- **Fuente:** `AnalysisPipelineFactory.cs:102-190`, `Hosting/AnalysisRunner.cs:54-55,87-88`, `Tests/OfflinePipelineTests.cs:21-100`.

### T24. Validador de cobertura (A6)
- **Ficheros:** `Core/Reports/ReportValidator.cs`, `Core/Reports/ReportValidation.cs`; test `Tests/ReportContentValidatorTests.cs`.
- **Contenido:** nuevo `ReportValidationChecks.Coverage`: un informe en modo B debe llevar el apartado «Cobertura» y cada
  subsistema elegido en `_coverage_plan.json` debe aparecer; sin plan en disco (modo `Off`), no se comprueba.
- **Test primero:** informe sin apartado, con un subsistema ausente, completo, y sin plan.
- **Fuente:** `Reports/ReportValidator.cs` (comprobaciones actuales), `Steps/VerifyReportStep.cs:62-66` (uso del validador).

### T25. Síntesis con cobertura
- **Ficheros:** `Core/Steps/ExecutiveSynthesizerStep.cs`, `templates/report_sintesis_prompt.md`; test del paso de síntesis existente.
- **Contenido:** la síntesis recibe un resumen de cobertura por informe (de `_coverage_plan.json` y los apartados «Cobertura»)
  y no presenta como completo un análisis con subsistemas no cubiertos.
- **Test primero:** con un informe con subsistemas «no cubiertos», la entrada de la síntesis los menciona.
- **Fuente:** `Steps/ExecutiveSynthesizerStep.cs:100-115` (lectura de fallos por auditor).

### T26. Documentación
- **Ficheros:** el README del sample de análisis (no hay `README.md` en `Core` ni en `CodebaseAnalysisPipeline` `[V]`: localizar el que describe el pipeline, p. ej. el de `CodebaseAnalysisWeb`), `docs/guides/codebase-analysis-flujo-y-politica-de-informes.md`,
  `docs/project_notes/asistente-chat-y-enriquecimiento-estado.md`.
- **Contenido:** pipeline nuevo, claves de configuración, artefactos `_*.json`, modos A y B, el interruptor `Mode`. Solo lo
  implementado; sin describir lo aplazado como existente.

## Fase 6b: aplicación web (`Samples/CodebaseAnalysisWeb`) y repositorios de prueba

La app a modificar y probar es `Samples/CodebaseAnalysisWeb` (referencia a Core: `CodebaseAnalysisWeb.csproj:19`; ejecuta análisis,
regeneración y verificación por `AnalysisRunner`, `Analysis/PipelineAnalysisWorkflow.cs:24,39,54` `[V]`). Su configuración (roles, proveedores,
claves) sale del mismo JSON de Core más variables de entorno, user secrets y línea de comandos (`Program.cs:25`): **no se tocan los datos de
conexión del LLM**, solo se añaden claves nuevas con defecto. Las versiones previas de informe (`baseline`, «Deshacer») no cambian.

### W1. Tests de la web
- Ejecutar `Samples/CodebaseAnalysisWeb.Tests` completo antes y después; arreglar lo que rompan los pasos nuevos (`RegenerationEndpointsTests`,
  `AnalysisEndpointsTests`, `FakeAnalysisWorkflow`). Test nuevo: un análisis por la web con LLM falso deja los artefactos `_*.json` y los informes en modo B
  con «Cobertura», y la regeneración de un informe en modo B reutiliza los artefactos.

### W2. SPA y eventos
- Comprobar que los eventos `Investigation_<tipo>_<id>` y los pasos 5-8 no rompen `runReducer`/`AuditorGrid` (un carril por informe; los eventos de
  investigación suman al carril del informe). Tests de Vitest en `ClientApp/src/state/runReducer.test.ts` y `AuditorGrid.test.tsx`. Si hace falta
  mostrar el progreso por subsistema, es un cambio mínimo y se anota; la SPA compilada en `wwwroot` se regenera con el `npm run build` del propio proyecto.

### W3. Configuración y README de la web
- Las claves `CodebaseAnalysis:Preparation` y `CodebaseAnalysis:TimeBudget` en `Core/appsettings.analysis.json` (T1) llegan a la web sin más; documentarlas
  en `Samples/CodebaseAnalysisWeb/README.md` junto con el interruptor `Mode`, los artefactos y los modos A/B.

### W4. Repositorios de prueba con elementos comprobables
- Crear dos aplicaciones pequeñas fuera del repo (`C:\Users\juan.rozano\eval-repos\subsystems-java` y `subsystems-csharp`, no versionadas, como los demás repos de
  evaluación `[V]`, `docs/project_notes/...` «Para retomar»), cada una con **tres capacidades de negocio genéricas** (p. ej. facturación, recordatorios de pago y
  catálogo), una de ellas repartida en **dos ramas distantes del árbol de paquetes** (UI/acciones y dominio) con vocabulario común, y con código de
  test/qa excluible. Un fichero `answers/<repo>.subsystems.json` con la respuesta esperada (subsistemas, a qué fichero pertenece cada clase). Ningún nombre del
  repositorio real en ellas.
- Criterio: el mapa de dominios (`_domain_map.json`) de cada una reproduce las tres capacidades, con la repartida en un único subsistema, y el plan elige y
  excluye lo esperado. Se comprueba indexando con cbm de verdad (sin LLM, o con LLM falso en el nombrado).

## Fase 7: verificación real (con el usuario)

Requieren LLM real y tienen coste (las notas dan unos 2,4-3 USD por repositorio pequeño `[V]`): **se piden antes de lanzarlas.**

### T27. No regresión (A7) y genericidad (A9)
- `java-simple` y `csharp-simple` con `Mode = Off` y `Mode = Subsystems`; comparar con `--expected`: citas válidas ≥ 97,7-100 %
  y sembrados 17/18 (Java) y 19/22 (C#) a mano. `grep` de nombres del repositorio de prueba en `templates/`, `appsettings.analysis.json`
  y `Preparation/`; ejecución sobre una copia del sample C# sin cambios de código.
- **Fuente:** tabla de `gpt-6-sol` en `asistente-chat-y-enriquecimiento-estado.md`, `eval-repos/answers/`.

### T28. Medición sobre `Online` (A8)
- Reindexar con cbm la copia de ruta corta `C:\Users\juan.rozano\m\Online` (el proyecto de cbm se borró, hay que reindexar), enriquecer,
  y ejecutar con `Off` (línea base) y `Subsystems`. Registrar tiempos, tokens y coste; comprobar que Notificaciones sale como
  un único subsistema en `_domain_map.json` y que el 01 la trata con reglas, estados, eventos y flujos, contrastado por el usuario.
  Calibrar `k`, base, `VocabularyMergeThreshold` y `MinSubsystemFiles` con los datos y dejar los valores en configuración.

## Orden y dependencias

T0 → T1 → T2 → (T3, T4, T5) → T6 → (T7 → T8 → T9 → T10 → T11) → T12 → T13 → T14 → (T15, T16) → T17 → T18 → T19 → (T20, T21) → T22 → T23 → (T24, T25) → (W1, W2, W3, W4) → T26 → T27 → T28.
Cada tarea de la 1 a la 25 deja el repositorio compilando y los tests en verde; ninguna cambia el comportamiento con `Mode = Off`.

## Riesgos que el plan no elimina

- **Coste:** 01, 04, 05 y 06 investigan cada subsistema por separado (D7); K = 8 y concurrencia 4 son los valores aceptados, y el coste real
  solo se conoce en T28.
- **Calidad del agrupamiento:** el umbral de vocabulario y el tamaño mínimo se calibran en T28; hasta entonces son estimaciones.
- **Calidad de las plantillas de investigación (T20):** no verificable sin LLM; depende de la revisión del usuario.
- **Detección determinista de comunidades sobre ~8 000 tipos:** rendimiento no medido `[I]`; si pasa de decenas de segundos se
  optimiza en T8 antes de seguir.
