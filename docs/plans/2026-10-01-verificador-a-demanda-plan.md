# Plan: verificador de informes a demanda

Estado: **implementado el 2026-10-01 (sin commitear)**, el paso 7 (plantillas V6/V7) se hizo después, el mismo día (ver «Paso 7 hecho» al final); ver «Desviaciones
y mediciones reales» al final. Original: pendiente de aprobación (gate). Adapta `2026-09-30-verificador-informes-plan.md` a la decisión del usuario
(2026-10-01): el verificador pasa de cada rama del pipeline a un botón por informe, junto al de rehacer (con `gpt-6-sol` y
los datos del compilador quedan pocas falsedades). `[V]` leído en código, `[I]` inferido.

## Hallazgos que cambian el plan

- **«Regenerar» solo se ofrece hoy en carriles fallidos o cancelados** [V] `ClientApp/src/state/runReducer.ts:727-730`
  (`canRegenerate`) y `components/AuditorGrid.tsx:50,143-150`. En un informe bien generado no hay botón de rehacer al que
  acompañar: «Verificar» tiene que nacer en carriles `succeeded`. Si además se quiere rehacer un informe sano, es abrir
  `canRegenerate` (decisión abierta D1).
- **Los dos casos de falsedad conocida ya no existen** en `after-reports`: el 03 de C# habla ahora de «registro de DI sin
  consumidor identificado» (CMD-004, `csharp-simple/03_codigo_muerto.md:16`, con la cautela en el texto), y el 06 de Java
  dice «cuatro acciones» (`java-simple/06_arquitectura_acoplamiento.md:55`) cuando `struts-config.xml` declara 4
  `<action>` (grep en `eval-repos/java-simple/WebContent/WEB-INF/struts-config.xml`) y hay 4 clases `*Action`. Habrá que
  sembrarlas (ver «Verificación real»).
- **La versión previa y el «deshacer» ya existen**: `ReportWriter.Update` guarda como `baseline` el contenido que no esté
  en el historial antes de escribir (`Reports/ReportWriter.cs:114-147,191-197`), conserva `analysis-meta`
  (`KeepAnalysisMeta`, `:227`) y `Restore` deshace (`:149`); la web ya expone `POST .../versions/{versionId}/restore`
  (`Api/ReportsEndpoints.cs:102-131`). Guardar el original y deshacer salen sin construir nada.
- **`CompleteRun` ya revalida todos los informes y reescribe `_validation.json`** al terminar cualquier ejecución
  [V] `Analysis/AnalysisRunManager.cs:450-452`, y `RestoreRetiredCatalogReportsAsync` reincorpora al grafo los informes
  desde disco en el `finally` del pipeline (`AnalysisPipelineFactory.cs:347-377`): el informe corregido vuelve al grafo solo.
- **El camino de rehacer, de punta a punta** [V]: botón (`AuditorGrid.tsx:147`) → `useAnalysisRun.regenerate`
  (`hooks/useAnalysisRun.ts:145-160`) → `POST /api/analysis/{runId}/regenerate` (`AnalysisEndpoints.cs:42-48`) →
  `AnalysisRunManager.Regenerate` (`:120-175`: mismas reglas de exclusión, `run.BeginRegeneration()`, evento
  `run.regeneration_started`, tarea de fondo) → `ExecuteRegenerationAsync` (`:318-376`) → pipeline
  `PrepareRegenerationStep → SqlFacetExtractorStep → ParallelStep de auditores → síntesis opcional`
  (`AnalysisPipelineFactory.cs:136-160`) → `CompleteRun` (`:430`, `_run_meta.json`, validación, `run.completed`).
## Qué se conserva y qué se descarta del plan anterior

| Del plan 2026-09-30 | Ahora |
|---|---|
| V1/V1b: `GroupStep[Auditor, Preparación, Verificador]` por rama, y partir `LanguageEnricherStep` + `SwitchStep` | **Se descarta.** No se toca la estructura del pipeline de análisis; V1b era independiente del verificador y queda como idea aparte. |
| V2: auditor devuelve texto plano y no escribe el fichero | **Se descarta.** El auditor sigue igual y escribe su informe. |
| V3: preparación (validador, citas, fragmentos, neutralizar `@[`) | **Se conserva**, sobre el informe ya en disco. Se pierde «lo que el auditor leyó» (no hay captura, el auditor ya terminó): ya no se puede marcar «citado sin haberlo leído». |
| V4: rol `ReportVerifier`, tools, salida = informe corregido + «Cambios de la verificación» | **Se conserva** (decisión cerrada). |
| V5: cierre (escribir, `analysis-meta`, revalidar, `report.written`, sin bloquear) | **Se conserva**, escribiendo con `ReportWriter.Update` (versión previa incluida). |
| V6 (citas en todos los informes), V7 (IDs `NN-CAT-nnn`) | **Se conservan**, ahora con más peso: el verificador solo puede comprobar lo que el informe cita. |
| V8: síntesis sobre informes verificados | **Cambia**: ver «Síntesis». |
| V9: regeneración pasa por el verificador | **Se descarta.** Verificar y rehacer son acciones separadas. |
| V10: `VerifierMinutes` (10 min) | **Se conserva.** |
| V11: estados del carril | **Cambia**: es una acción sobre un carril ya `succeeded`. |
| F1, F2 (framework) | **Se conservan**, sin cambios. |
| Índice de evidencias generado por el auditor | **Abierto (D3)**: exigirlo toca todas las plantillas de auditor y el informe visible. |
## Síntesis (00)
La síntesis se hizo con los informes sin verificar. Verificar el 06 no la actualiza sola. Propuesta: **no se rehace
automáticamente** (la verificación toca pocas líneas y rehacer la síntesis cuesta una llamada grande), pero el endpoint
acepta `synthesis` como el de regenerar (`RegenerateReportsRequest.Synthesis`, `AnalysisRunManager.cs:23`), la UI reutiliza
la casilla «Actualizar también el resumen ejecutivo» (`AuditorGrid.tsx:66-70`) y, si hay correcciones sin reflejar, muestra
el aviso «el resumen ejecutivo puede estar desfasado». Decisión abierta D2.

## Flujo

1. **Botón** «Verificar» en carriles `succeeded` con la ejecución terminada (`canVerify`, análogo a `canRegenerate`).
   Mismo estado `busy` que regenerar: una acción a la vez.
2. **Endpoint** `POST /api/analysis/{runId}/verify`, cuerpo `{ reports, synthesis }`, 202 con el `RunStatusDto`. Se
   extrae de `Regenerate` lo común (búsqueda de la ejecución, parseo de informes, exclusiones, `BeginRegeneration`) a un
   método privado y `Verify` solo cambia el evento (`run.verification_started`), la tarea y los mensajes de
   `run.completed`. Mantiene las limitaciones R3/R6/R7: solo la ejecución en memoria del servidor [V] `:120-135`.
3. **Pipeline** `CreateVerificationPipeline` (en `AnalysisPipelineFactory`): `PrepareRegenerationStep` (reutilizado tal
   cual: comprueba grafo, esquema y retira los informes del grafo, que vuelven en el `finally`) → un `ParallelStep`
   tolerante con una rama por informe (`GroupStep[Preparación, Verificador, Cierre]`, como proponía V1b pero solo aquí) →
   síntesis opcional. No hace falta `SqlFacetExtractorStep`: el verificador no consume facetas.
   - **Preparación** (`DelegatedStep`, sin LLM): lee el informe, lo valida con `ReportValidator` (hoy opera sobre ficheros,
     `ReportValidator.cs:96`; el contenido en memoria sigue siendo la tarea 2 del plan anterior), extrae citas
     `ruta:línea` con ±N líneas de código, neutraliza `@[` y arma **un único texto** con tres secciones (borrador, fallos,
     evidencias) para `{{ input }}`, como está decidido.
   - **Verificador**: `CustomStatelessTemplateLlmStep` con rol `ReportVerifier` (modelo `gpt-6-sol`), conversación nueva,
     tools `view_file`, `view_code_item`, `grep_search`, `graph_neighbors` (de `AuditorToolNames`,
     `AnalysisPipelineFactory.cs:250-253`). Devuelve el informe completo con «Cambios de la verificación».
   - **Cierre** (código): restaura los `@[`, valida el resultado (misma lógica de la preparación), y escribe con
     `ReportWriter.Update(modo replace)`; el original queda como `baseline` y la nueva como `updated`. `analysis-meta`
     conserva su procedencia y añade `verification=ok|corrected(n)|warnings(n)`. Si el verificador falla o agota el
     tiempo, **no se toca el fichero** (sin sustituir el informe bueno por un borrador peor) y el carril se marca con aviso.
   - Emite `report.verified` con `{ report, outcome, changes, previousVersionId, versionId }`.
4. **UI**: el carril pasa a «verificando» (fase propia en el mapeo de `runReducer.ts`), luego «verificado», «corregido (n)»
   o «con avisos». En el visor del informe, «Cambios de la verificación» ya sale en el markdown; junto al carril, botón
   **«Deshacer verificación»** que llama a `restore` con `previousVersionId`: coste bajo, todo el camino existe.
5. **Origen de la versión**: usar `ReportVersionOrigin.Pipeline` con nota «Verificación» (sin tocar el contrato de
   `ReportVersionDto.origin`). Un origen `verifier` propio sería un cambio de contrato: D4.

## Concurrencia

Una ejecución a la vez, la misma regla que regenerar: verificar mientras se rehace otro informe, o el mismo, da 409
`AnalysisRunning` [V] `AnalysisRunManager.cs:138-139`; también con un turno de chat o una operación de conocimiento
(`:150-154`), porque se escribe el informe y se retira del grafo. Varios informes en una petición corren en paralelo bajo
`MaxParallelAuditors`. El `WriteLock` de `ReportWriter` serializa las escrituras (`:70`). Cancelar funciona como hoy
(`run.Cancellation`). **Coste de la regla**: verificar los 9 informes uno a uno son 9 ejecuciones seriadas; por eso el
endpoint admite lista.

## Coste, tokens y tiempos [I]

- Por informe, 20.000-40.000 tokens (estimación del plan anterior, sin medir): informe de ~6-8k tokens como entrada más
  evidencias, varias vueltas de tools y un informe completo de salida. Con `gpt-6-sol` a 2/10 USD por millón
  [V] `CodebaseAnalysis.Core/appsettings.analysis.json:115-118`: del orden de 0,10-0,25 USD y 2-4 minutos por informe.
- `AnalysisTimeouts` gana `VerifierMinutes` (10 por defecto; 0 = sin límite), siguiendo el patrón de los demás
  (`Hosting/AnalysisTimeouts.cs:25-47,62-67`). El pipeline reusa `PipelineMinutes`.
- El rol `ReportVerifier` **no** entra en `LlmRoleFactory.RequiredRoles` (`Hosting/LlmRoleFactory.cs:21-26`) para no romper
  configuraciones existentes: `ResolveRole` lanza si falta (`AnalysisPipelineFactory.cs:171-179`), así que el error sale al
  pulsar, con el nombre del rol y la sección donde declararlo. Decisión abierta D5 si se prefiere obligatorio.

## Pasos de implementación

Cada uno termina en commit tras aprobación; 1-2 son independientes.
1. **Framework F1/F2**: `TinyTemplateRendered` y `PromptTemplateResolver` (`Framework/`), tests en `Tests/`.
2. **Validador sobre texto**: `ReportValidator` acepta contenido en memoria; reglas de IDs `NN-CAT-nnn` con unicidad
   global y de citas por informe. Tests en `CodebaseAnalysis.Core.Tests`.
3. **Pasos y pipeline**: `Steps/PrepareVerificationStep.cs`, `ReportVerifier` en `appsettings.analysis.json`,
   plantillas `report_verifier_system.md`/`_prompt.md`, `Steps/CloseVerificationStep.cs`,
   `CreateVerificationPipeline`, `VerifierMinutes`. Test de extremo a extremo con LLM enlatado (cita rota + hallazgo
   contradicho → informe corregido, `baseline` + `updated` en el historial, `verification=corrected(2)`; fallo del
   verificador → fichero intacto).
4. **Manager y endpoint**: `IAnalysisWorkflow.VerifyAsync` y `PipelineAnalysisWorkflow`, `AnalysisRunManager.Verify`
   (refactor de lo común con `Regenerate`), `POST /verify`, `RunMetaFile.KindVerification`, mensajes de `run.completed`,
   `FakeAnalysisWorkflow`. Tests en `CodebaseAnalysisWeb.Tests` (análogos a `RegenerationEndpointsTests`: 404, 400, 409
   por cada exclusión, 202 y secuencia de eventos).
5. **Eventos**: `AnalysisEventDto`/`AnalysisEventMapper` (`run.verification_started`, `report.verified`) y tests en
   `AnalysisEventMapperTests`.
6. **SPA**: `api/types.ts` y `client.ts` (`verifyReports`), `useAnalysisRun.verify`/`verifying`/`verifyError`,
   `runReducer.ts` (estado de verificación del carril, `canVerify`, mapeo de fase), `AuditorGrid.tsx` (botones Verificar y
   Deshacer), `fixtures.ts`. Vitest: reducer (secuencia verificación completa, con avisos, fallida), `AuditorGrid.test.tsx`
   (botón solo en `succeeded` y con ejecución terminada; deshabilitado con `busy`; error visible).
7. **Plantillas (V6/V7)**: `_common_rules.md` y plantillas por informe (citas e IDs con prefijo).
8. **Documentación**: guía de la app, READMEs, diagrama del flujo (verificación como proceso aparte), documento de estado.

## Verificación real

Con LLM real y `gpt-6-sol`, tras el paso 4 (hay Azure; antes del paso 7 solo con informes ya generados):

1. Copia de `after-reports/java-simple` y `csharp-simple` a una carpeta de trabajo. **Sembrar** falsedades comprobables
   en el 06 de Java (cambiar «cuatro acciones» por «tres», `06_arquitectura_acoplamiento.md:55`; una cita a una línea que
   no existe) y en el 03 de C# (cambiar CMD-004 a «sin ningún registro de DI para `IInvoiceService`»,
   contradicho por `Program.cs:19`). Las originales ya son correctas, así que son su propio control negativo.
2. Verificar los 4. Esperado: las sembradas corregidas y listadas con motivo en «Cambios de la verificación»; en los
   originales, cero o muy pocos cambios y ningún hallazgo esperado retirado (contra `answers/*.expected-findings.json`).
3. Comprobar `_validation.json` antes y después (citas válidas no baja), el historial (`baseline` + `updated`) y que
   «Deshacer» restaura el byte a byte (SHA-256 del `baseline`).
4. Medir tokens, coste y tiempo reales (`run.completed.usage`) y sustituir las estimaciones de este documento.
## Esfuerzo
Orden de 5-6 días de trabajo con agentes: pasos 1-2 (1 d), 3 (1,5 d), 4-5 (1 d), 6 (1 d), 7-8 (0,5 d), verificación real
y ajuste de prompts (1 d, depende de cuánto se desvíe el verificador).
## Decisiones cerradas (usuario, 2026-10-01: aceptadas las recomendaciones y dado el «adelante»)
- **D1.** «Regenerar» se abre también a carriles correctos; «Verificar» va al lado.
- **D2.** La síntesis no se rehace sola tras verificar: casilla opcional con aviso de desfase.
- **D3.** Sin índice de evidencias del auditor: el verificador usa las citas del propio informe.
- **D4.** Origen de la versión: `Pipeline` + nota, sin tocar el contrato.
- **D5.** `ReportVerifier` obligatorio en `RequiredRoles`, con `gpt-6-sol`.

## Desviaciones y mediciones reales (2026-10-01)

Desviaciones respecto del plan:

- **Un solo paso por informe**, no `GroupStep[Preparación, Verificador, Cierre]`: `VerifyReportStep` hace las tres cosas y
  llama al sub-step LLM sin middleware, como la síntesis, con su propio límite (`VerifierMinutes`). Evita pasar estado entre
  pasos por el contexto compartido de las ramas.
- **Respuesta corta `SIN CAMBIOS`**: el verificador no devuelve el informe entero si no corrige nada (ahorra la salida
  de ~8k tokens por informe sano). Con cero cambios solo se estampa `verification=ok` en `analysis-meta`.
- **Bloque `<recuentos>`** en la entrada: lista las cifras que el borrador afirma («las tres acciones»). Sin él, el modelo (sin
  razonamiento) pasó por alto el recuento sembrado («tres acciones» en vez de cuatro) en 3 de 5 pasadas; con él, en la
  pasada final lo corrigió. Son pocas pasadas: no mide una tasa.
- **Cambios declarados que no se escriben** se toman como sin cambios (el modelo listaba correcciones sin aplicarlas).
- **El apartado «Cambios de la verificación» sale de la validación determinista** (completa y por texto): sus citas erróneas e
  IDs repetidos eran falsos fallos.
- **Paso 2 recortado**: `ReportValidator.CreateContentValidator` valida texto y se reutiliza para varios informes; **no** se
  han añadido las reglas de IDs `NN-CAT-nnn` ni la de citas por informe, que van con el paso 7.
- **Paso 7 (V6/V7) (hecho después, ver «Paso 7 hecho»)**: toca `report_codesmells_prompt.md`, que está en edición por otra tarea, y cambia el formato de
  los IDs que lee el validador.
- **Plantillas de auditor**: dos tests de `DocumentTests` y uno de `EffectiveLimitsTests` se ajustaron (el verificador no es
  un auditor; la línea de límites gana «verificador»).
- **F1**: el warning de placeholder sin resolver sale por `ILogger` opcional de `TinyTemplateRendered.Render`; la ruta de
  `AnalysisTemplateRenderer` (plantillas de la app) no pasa logger y no lo emite. **F2**: `PromptTemplateResolver.Resolve`
  pasa `baseDirectory: null` (directorio de trabajo) en lugar de `CurrentStepPath`.

Mediciones con `gpt-6-sol`, `reasoning_effort: none`, sobre copias de `after-reports` de `java-simple` y `csharp-simple`
(evidencias en `C:\Users\juan.rozano\eval-repos\results\2026-10-01-verifier\`):

- Los nueve informes de un repositorio, en paralelo: 26-27 s de pared, 34-36 llamadas, 0,77-0,84 M tokens (0,75-0,82 M de
  entrada), 1,68-1,88 USD. Por informe: 6-27 s, 3-7 llamadas, ~85-95 k tokens, ~0,19-0,21 USD.
  La estimación del plan (20-40 k tokens, 2-4 min) se quedó corta en tokens (las vueltas de tools releen el borrador) y larga
  en tiempo.
- Siembras corregidas en la pasada final: 7 de 7 (4 Java, 3 C#).

## Paso 7 hecho (V6/V7, 2026-10-01)

- **V7, IDs únicos**: `_common_rules.md` exige `NN-CAT-nnn` (`05-SEC-002`) y cada `report_*_prompt.md` dice su número de informe. El
  validador lee los dos formatos (el anterior no avisa), avisa si el número no es el del fichero o si un informe mezcla ambos, y las
  colisiones entre informes salen como aviso en el resumen. Una línea en negrita que repite un ID ya definido por un encabezado (el
  plan de acción) deja de contar como segunda definición. `ReportMarkdown` cuenta los P0/P1 también con el prefijo.
- **V6, citas**: `_common_rules.md` exige `ruta:línea` en cada afirmación (también 01 y 04, con una instrucción propia). Nueva
  comprobación `citations_required` (aviso, nunca falla el run): informe sin ninguna cita u hallazgos sin cita; el `00` queda fuera
  y el verificador no la recibe entre sus fallos. Los totales (`ReportsWithoutCitations`, `FindingsUncited`) salen en la línea de
  totales del resumen.
- **Sin cambios**: el comparador de esperados no depende del formato del ID (casa por fichero, rango y palabras clave), ni la
  síntesis, el chat o la SPA (no leen IDs).
- **Medición** con `gpt-6-sol` (`C:/Users/juan.rozano/eval-repos/results/2026-10-01-step7/`): IDs repetidos entre informes 10 → 0 en
  Java y 13 → 0 en C#; citas de código 384 → 602 (Java) y 388 → 553 (C#), todas válidas; esperados 18/18 (Java) y 22/22 (C#), con S05
  y SEED-12a. Las dos trampas que el comparador marca (T11, T16) son informes que dicen «conservar»; no son falsos positivos. Coste y
  tiempo sin cambios apreciables (2,85 USD y 77 s en Java, 2,42 USD y 76 s en C#).
