# Auditoría de la aplicación de análisis de código (2026-09-29)

Auditoría externa de solo lectura sobre `Samples/CodebaseAnalysis.Core`, `Samples/CodebaseAnalysisPipeline`,
`Samples/CodebaseAnalysisWeb`, sus tests y lo que usan del resto del repositorio, en el estado del disco de la rama
`consolidation` (casi todo sin commitear). Las rutas son relativas al repositorio salvo que se diga otra cosa; `CORE`
abrevia `Samples/CodebaseAnalysis.Core` y `WEB` abrevia `Samples/CodebaseAnalysisWeb`.

Cada afirmación de hecho lleva su nivel de evidencia: `[V]` verificado con la línea citada, `[I]` inferido y `[?]` no
sabido. El razonamiento y las recomendaciones no se marcan. Para comprobar el estado se ejecutaron hoy los tests de Core
y de la web y se leyeron, sin modificarlos, los logs y las sesiones de chat de `TestAgenteAnalisis`.

## 1. Veredicto

La aplicación es hoy un pipeline de nueve auditores LLM sobre un grafo SQLite de cbm, con una web local que añade chat
con herramientas, documentos, embeddings y versiones de informes, y desde el 28/09 un analizador Java propio que corrige
el grafo con una fidelidad muy alta (99,996 % de llamadas resueltas según el estado del proyecto). La ingeniería de
cada pieza es buena y está bien probada en lo que se puede probar sin LLM, pero la arquitectura no entrega lo que se le
pide en el caso que ahora importa, porque la cadena de valor está cortada entre el grafo y los informes. Los tres
problemas que más importan son estos:

1. **El enriquecimiento no llega a los informes.** Ninguna faceta ni herramienta lee `CALLS`, `INHERITS`, `IMPLEMENTS`,
   los nodos `Jsp` ni sus marcas de sospecha, y las facetas dependen de propiedades (`fan_in`, `hotspot`, `loc_total`)
   que nadie escribe para Java. La tanda Java mejoró el grafo y dejó los informes de Java casi igual.
2. **La capa «determinista» no es fiable como fuente de hechos.** Presenta lo desconocido como negativo (clases sin
   `fan_in` como «cero uso entrante», rutas sin dato de autenticación como «sin validación»), acepta métricas inventadas
   por el LLM del enriquecimiento como si fueran medidas y contiene defectos cableados del repositorio de prueba, que es
   a la vez el único banco con el que se juzga la calidad. No hay forma de saber si un cambio de prompt mejora o empeora.
3. **Los límites de tiempo siguen gobernados por valores ocultos.** R1 arregló el timeout de 10 minutos del pipeline
   de análisis, pero el chat sigue cortado por el timeout de paso por defecto del framework (60 s), y la causa física
   del fallo del 28/09, una llamada HTTP colgada sin plazo propio, no se ha tratado: ahora se espera más antes de fallar.

## 2. Puntos fuertes

El contrato de escritura sobre la copia del grafo (D8 y P12) es lo mejor del diseño: nunca se toca la BD de cbm, todo
lo escrito lleva `enriched_by`, cada pasada borra su salida anterior y deja una fila en `enrichment_runs`, de modo que el
enriquecimiento es repetible y auditable arista a arista. Las decisiones del analizador Java se tomaron midiendo
(JavaParser, JDT y `javac` sobre el repositorio real, con tiempos, memoria y fallos) y no por preferencia, y el
analizador degrada con aviso y fila `skipped` o `failed` en lugar de tumbar el pipeline.

La idea de extraer facetas por SQL antes de llamar al LLM es correcta, igual que la corrección determinista de
`critical` contra la matriz de severidad y el escáner de secretos que enmascara los valores. La estrategia de «redactar
sin herramientas al agotar el tope» vive en el auditor y no en el framework, que es el sitio adecuado, y los errores
tipados (`LlmProviderException`, `ToolIterationLimitExceededException`) dejaron el fallback con una semántica limpia.

Para una herramienta local, la postura de seguridad de la web está por encima de lo habitual: solo loopback,
`AllowedHosts`, guarda `Sec-Fetch-Site`, CSP en `index.html` y visores en sandbox. La cancelación mata árboles de
procesos, y el token propio de C6 distingue el timeout de la cancelación sin comparar tiempos. Los tests se ejecutan
sin red ni procesos externos y son rápidos: hoy pasan 384 de Core (2 omitidos por falta de JDK) y 109 de la web `[V]`
(ejecución de hoy), y el cliente tiene 184 casos Vitest `[V]` (recuento con `grep` sobre `ClientApp/src/**/*.test.*`).
La documentación es precisa y cita `fichero:línea`, lo que ha hecho esta auditoría bastante más barata.

## 3. Mal diseñado o mal hecho

### H-1. El grafo enriquecido no lo consume nadie (alta)

**Evidencia.** Ni `SqlFacetExtractorStep` ni ninguna herramienta de `CORE/Tools` consulta aristas `CALLS` o `INHERITS`
`[V]` (búsqueda de `CALLS|INHERITS` sin resultados en `CORE/Steps/SqlFacetExtractorStep.cs` y `CORE/Tools/*.cs`). Las
herramientas del grafo solo recorren `HANDLES` (`CORE/Tools/GraphDomainTool.cs:96-97`), `FLOW_NEXT`
(`CORE/Tools/GraphFlowsTool.cs:136-137`) y `PROJECT_DEPENDS_ON` (`CORE/Tools/GraphMapTool.cs:82-85`) `[V]`, e
`IMPLEMENTS` solo aparece en la faceta `DiResolutions`, limitada a 50 filas (`CORE/Steps/SqlFacetExtractorStep.cs:190-195`)
`[V]`. `graph_search` busca nodos por etiqueta y patrón de nombre y no devuelve vecinos
(`CORE/Tools/GraphSearchTool.cs:96-111`) `[V]`. Nada lee los nodos `Jsp`, `suspicious`, `FORWARDS_TO`, `SUBMITS_TO` o
`INCLUDES` `[V]` (búsqueda sin resultados en `CORE/Steps`, `CORE/Tools`, `CORE/Chat` y `CORE/templates`). La conexión
MCP de cbm, que sí trae trazado de llamadas, está desactivada porque «duplica `graph_search`»
(`Samples/CodebaseAnalysisPipeline/Program.cs:75-78`) `[V]`, cuando en realidad cubre lo que `graph_search` no tiene.

**Coste real.** El objetivo declarado de la tanda Java era «detectar código de negocio o de servidor metido en las
pantallas y enlazar los flujos desde la pantalla donde parten» (`docs/plans/2026-09-28-enriquecimiento-nativo-java-design.md:14-16`)
`[V]`, y ninguno de los dos resultados llega a un informe ni al chat salvo que el modelo adivine la etiqueta `Jsp` y la
pida por `graph_search`. Los criterios de aceptación de esa tanda miden el grafo y ninguno mide un informe
(`…-design.md:175-197`) `[V]`, y la aceptación se hizo con un arnés que ejecuta solo `LanguageEnricherStep`
(`docs/plans/2026-09-28-enriquecimiento-nativo-java-plan.md:287-289`) `[V]`. No consta ninguna ejecución completa del
pipeline sobre `delta/Online` `[I]` (ni el estado ni los planes la registran).

**Qué haría.** Antes de seguir con Roslyn, Jasper o nodos `Flow`, cerrar la cadena: una herramienta `graph_neighbors`
(llamantes, llamados, implementaciones y jerarquía de un nodo, con límite) para auditores y chat, y dos facetas
nuevas, las JSP sospechosas con sus motivos y las acciones Struts con sus forwards, repartidas entre errores, deuda,
arquitectura y flujos.

### H-2. Las facetas presentan lo desconocido como negativo y dependen de propiedades que nadie escribe (alta)

**Evidencia.** `ZeroFaninClasses` selecciona clases con `fan_in = 0` **o** `fan_in IS NULL`
(`CORE/Steps/SqlFacetExtractorStep.cs:164-170`) `[V]`, y la plantilla la presenta como «Clases, Tablas o Módulos con
Cero Uso Entrante (`fan_in = 0`)» (`CORE/templates/report_codigomuerto_prompt.md:22`) `[V]`. En Java nadie escribe
`fan_in`: solo aparece fuera de las facetas en `OfficeTools/Steps/IngestOfficeArtifactsStep.cs` `[V]` (búsqueda de
`fan_in|hotspot|loc_total` en `CORE/Enrichment` sin resultados). El auditor de código muerto recibe, por tanto, 20
clases cualesquiera etiquetadas como sin uso `[I]`. `UnvalidatedRoutes` hace lo mismo con `auth_required IS NULL`
(`:212-220`) `[V]`, y la plantilla de errores la titula «Rutas, Consultas o Puntos de Entrada sin Validación o con
Manejo Inseguro» (`CORE/templates/report_errores_prompt.md:12-14`) `[V]`; el plugin Struts no escribe `auth_required`
(`docs/plans/2026-09-28-enriquecimiento-nativo-java-plan.md:117-120`) `[V]`, así que 40 de las 1484 acciones pasan al
auditor de seguridad como «sin validación» `[I]`.

Además, las facetas con `LIMIT` no ordenan por nada relevante: `RoutesSummary` toma 50 rutas sin `ORDER BY`
(`:78-85`), `DomainEntitiesSummary` 50 clases sin orden (`:94-101`) `[V]`, lo que en un repositorio de 8420 ficheros
es una muestra arbitraria por orden de inserción `[I]`. Un error SQL se convierte en `[] /* Nota faceta SQL … */`
(`:563-566`) `[V]`, que el auditor puede leer como conjunto vacío. `_common_rules.md` dice que un vacío no prueba una
ausencia (`CORE/templates/_common_rules.md:22-24`) `[V]`, pero esa regla no protege
contra un conjunto que llega lleno y mal etiquetado.

**Coste real.** Es la vía más directa a alucinaciones con apariencia de rigor: el auditor cita datos del prompt, el
dato es falso y el informe lo presenta como hallazgo con identificador. En código muerto y seguridad, los dos informes
cuyos hallazgos se actúan, el error cae justo donde más daño hace.

**Qué haría.** Tratar `NULL` como desconocido en todas las facetas (filtrar o marcar `"fan_in": "unknown"`), derivar
`fan_in`/`fan_out` y una LOC aproximada por SQL a partir de las aristas y las líneas, que ahora sí son correctas para
Java, y ordenar cada `LIMIT` por una métrica declarada en la propia faceta.

### H-3. El enriquecimiento por LLM escribe en el grafo métricas inventadas (alta)

**Evidencia.** `SemanticEnrichmentStep` vuelca el JSON del modelo tal cual: `properties` se copia con `GetRawText()`
(`CORE/Steps/SemanticEnrichmentStep.cs:227,290`), se inserta con `ON CONFLICT … json_patch` sobre nodos existentes
(`:235-238`) y actualiza propiedades por `qualified_name` sin filtrar por proyecto (`:298-300`) `[V]`. La marca
`origin: "ai_enricher"` solo está en el ejemplo del prompt y el código no la impone
(`CORE/templates/enricher_prompt.md:16-33`) `[V]`, y ese mismo ejemplo sugiere `"cyclomatic": 5` y `8`
(`:31,52`) `[V]`. Después el paso marca el grafo como enriquecido (`SemanticEnrichmentStep.cs:81-82`) `[V]`, y en la
siguiente ejecución la presencia de cualquier `cyclomatic` basta para darlo por enriquecido
(`CORE/Steps/DetectAndIndexGraphStep.cs:440-446`) `[V]`.

**Coste real.** Una complejidad ciclomática que el modelo estimó leyendo 15 ficheros de 4000 caracteres
(`SemanticEnrichmentStep.cs:120`) `[V]` entra en `HighCyclomaticMethods` y los auditores la reciben como dato del grafo.
El mismo mecanismo que el análisis vende como «facetas deterministas» lava así salida del LLM, y el grafo queda marcado
como enriquecido hasta un `ForceReindex`. Afecta a los repositorios sin enriquecedor nativo (Python, TypeScript, C#
sin `.sln`); en Java no corre por P2 (`CORE/Steps/BdpEnricherStep.cs:42-44`) `[V]`.

**Qué haría.** Imponer en código `origin` y `enriched_by`, prohibir que el LLM escriba propiedades numéricas que
las facetas tratan como métricas, y que las facetas excluyan o marquen como «estimado» lo que tiene `origin = ai_enricher`.
Con el valor que aporta hoy (rutas y registros DI de 15 ficheros), retirarlo tampoco sería una pérdida grave.

### H-4. Timeouts: el chat se corta a los 60 s y una llamada colgada no tiene plazo propio (alta)

**Evidencia del chat.** El paso del turno se construye sin `Timeout` (`CORE/Chat/ChatAgent.cs:218-222`) `[V]`, y
`TimeoutMiddleware` aplica entonces el valor por defecto del motor (`Framework/Core/Execution/Middlewares/TimeoutMiddleware.cs:30`)
`[V]`, que con `AddAITaskAgent` es `TimeoutOptions.DefaultStepTimeout`, 1 minuto
(`Framework/Configuration/TimeoutOptions.cs:12`, `Framework/Configuration/ServiceCollectionExtensions.cs:50,58`) `[V]`.
Ni `appsettings.analysis.json` ni el `appsettings.json` de la web fijan `AITaskAgent:Timeouts` `[V]`. El
`TurnTimeoutMinutes` de 5 minutos se pasa como timeout de pipeline (`ChatAgent.cs:225`) `[V]` y no llega a actuar. Los
datos cuadran: las 16 respuestas guardadas en `TestAgenteAnalisis/.codebase/chat/` terminan todas en menos de 60 s y la
más larga dura 59 150 ms (`20260928-135051-hai3.json`, turno `t-0004`) `[V]`. Que un turno más largo falle con un
mensaje opaco del tipo «The operation was canceled.» es `[I]`: `RetryMiddleware` captura la excepción antes que el
filtro de `TimeoutMiddleware` (`Framework/Core/Execution/Middlewares/RetryMiddleware.cs:50-52`) `[V]`, el mismo síntoma
que C6 corrigió solo para el análisis.

**Evidencia de la llamada colgada.** El 28/09 el último auditor que terminó lo hizo a las 20:21:44 y `Auditor_CodeSmells`
se canceló a las 20:29:04 sin haber recibido respuesta (`TestAgenteAnalisis/logs/analysis-pipeline-20260928.log:311-312`)
`[V]`. El cliente HTTP tiene 30 minutos de timeout (`OpenAILLmService/OpenAILlmService.cs:124`) `[V]`, los reintentos
son 3 con 2 s y 4 s de espera sin mirar `Retry-After` (`:66,278-302`) `[V]`, y `DefaultToolTimeout` y
`StreamingActivityTimeout` existen en `TimeoutOptions` pero ningún código los lee `[V]` (búsqueda en `Framework/`).
`ParallelStep` lanza las ramas con `Parallel.ForEachAsync` sin grado de paralelismo (`Framework/Core/Steps/ParallelStep.cs:52`)
`[V]`, que por defecto es el número de procesadores `[I]`: nueve llamadas simultáneas al mismo despliegue.

**Coste real.** El chat, que es la función interactiva principal, tiene un techo real de un minuto que contradice su
configuración, y en repositorios grandes las preguntas con varias herramientas lo superarán. En el análisis, R1 y R2
han cambiado «falla a los 10 minutos» por «espera hasta 15 minutos por auditor y falla», porque un timeout de paso no
se reintenta (`TimeoutMiddleware` envuelve a `RetryMiddleware`, `Framework/Core/Execution/Pipeline.cs:17-18`) `[V]`.

**Qué haría.** Poner `Timeout` explícito al paso del chat desde `ChatOptions`, fijar en la configuración del sample un
`AITaskAgent:Timeouts:DefaultStepTimeout` razonable y añadir un test que falle si algún paso construido por la factoría
o el chat queda con `Timeout` nulo. En el proveedor, un plazo por petición (del orden de 3 a 5 minutos, configurable)
con reintento, respeto de `Retry-After` y un tope de concurrencia de auditores. Es la única manera de que un cuelgue
cueste minutos y no un informe.

### H-5. `ClaimAuditor` comprueba cobertura de lectura, y `view_code_item` la falsea (media)

**Evidencia.** El estado `Verified` significa que las líneas citadas se leyeron en el turno (`CORE/Chat/ClaimAuditor.cs:339-357`)
`[V]`; no se compara el contenido de la afirmación con esas líneas. `view_code_item` cuenta como fichero entero leído a
partir de su argumento `File` (`:403-406`) `[V]`, aunque la herramienta solo devuelve los nodos pedidos con su
`Location` (`FileTools/Tools/ViewCodeItemTool.cs:111`) `[V]`. Los resultados de error solo se descartan si empiezan por
`Error` (`ClaimAuditor.cs:375`) `[V]`, y `view_code_item` devuelve `TOOL_ERROR:` o `TOOL_CALL_ERROR:`
(`ViewCodeItemTool.cs:65,73,87,126`) `[V]`, así que una llamada fallida también marca el fichero como leído. La
herramienta parsea siempre como C# (`:94`) `[V]`, de modo que en VBA o Java es fácil que falle. El test de errores
solo cubre el prefijo `Error` de `view_file` (`CORE.Tests/ClaimAuditorTests.cs:148-156`) `[V]`. La evidencia se extrae
además por expresiones regulares sobre el texto de salida de FileTools (`ClaimAuditor.cs:487-495`) `[V]`, un contrato
implícito entre proyectos.

**Coste real.** La insignia V es la señal de confianza del producto, y un falso «verificado» es peor que un «sin
comprobar». El nombre ya sobrevende lo que se comprueba: «citado y leído» sería el término exacto.

**Qué haría.** Tomar de `view_code_item` los rangos de `Location`, descartar los prefijos `TOOL_ERROR` y
`TOOL_CALL_ERROR`, y añadir una comprobación léxica barata (los identificadores en código de la afirmación aparecen en
las líneas citadas) que degrade a «parcial» cuando no casan.

### H-6. Defectos del repositorio de prueba cableados en OfficeTools (media)

**Evidencia.** `IngestOfficeArtifactsStep` añade `detected_issues` buscando `nocout`, `sfFECALT_Fin` y `FECCON`
(`OfficeTools/Steps/IngestOfficeArtifactsStep.cs:786-792`) `[V]`, nombres de `TestAgenteAnalisis` según el propio estado
(`docs/project_notes/codebase-analysis-pipeline-estado.md:94`) `[V]`. Esas propiedades alimentan
`CriticalHotspotsExceptions` y `SuspectPatterns` (`CORE/Steps/SqlFacetExtractorStep.cs:198-232`) `[V]`.

**Coste y corrección.** El repositorio con el que se juzga la calidad de los informes recibe las respuestas precargadas, así
que cualquier conclusión sobre la calidad del auditor de errores en ese repositorio está contaminada, y viola además
la regla de genericidad que el propio proyecto se impone. La corrección es borrar hoy las dos reglas con nombres
concretos y dejar las genéricas (`On Error Resume Next`, `LIKE` mal entrecomillado), porque cuestan una línea cada una
y su coste de oportunidad es el banco de pruebas entero.

### H-7. «Regenerar» actúa sobre otro repositorio y reapunta el sandbox (media)

**Evidencia.** Cambiar de repositorio no limpia la última ejecución (`WEB/Workspace/WorkspaceState.cs:175-190`) `[V]`,
`Regenerate` no compara el repositorio de la ejecución con el abierto (`WEB/Analysis/AnalysisRunManager.cs:119-165`)
`[V]` y el cliente, que sí detecta la discrepancia y la avisa, mantiene el botón (`WEB/ClientApp/src/components/AnalysisTab.tsx:40-41,108-111`)
`[V]`. La regeneración llama a `AnalysisToolRegistration.Register(run.Input)` (`AnalysisRunManager.cs:390`) `[V]`, que
fija `FileToolsConfiguration.RootDirectory` y las herramientas del grafo en el repositorio antiguo
(`CORE/Hosting/AnalysisToolRegistration.cs:21-30`) `[V]`.

**Coste y corrección.** Tras abrir B y regenerar un informe de A, el chat de B lee código y grafo de A hasta el
siguiente análisis o cambio de repositorio `[I]`, sin ningún aviso, porque el cerrojo común protege la concurrencia y
no la identidad del repositorio. Basta con responder 409 en el servidor cuando el repositorio no coincide y ocultar el
botón en el cliente cuando `otherRepo` es cierto.

### H-8. La lectura de fuentes ignora la codificación que el analizador Java sí respeta (media)

**Evidencia.** El analizador decide la codificación fichero a fichero (P6) y `/api/source` cae a Latin-1 si no es UTF-8
válido (`WEB/Api/SourceEndpoints.cs:56-68`) `[V]`, pero `view_file`, `grep_search`, el escáner de secretos, el código de
`graph_search` y los fragmentos de las facetas leen con la codificación por defecto de .NET
(`FileTools/Tools/ViewFileTool.cs:89`, `FileTools/Tools/GrepSearchTool.cs:165`, `CORE/Support/HardcodedSecretScanner.cs:63`,
`CORE/Tools/GraphSearchTool.cs:168`, `CORE/Steps/SqlFacetExtractorStep.cs:589`) `[V]`.

**Coste y corrección.** En el repositorio objetivo, en ISO-8859-1, auditores y chat ven U+FFFD en cada tilde y un
`grep_search` de una palabra acentuada no encuentra nada `[I]`, de modo que el modelo puede reportar como ausente algo
que existe. La política correcta ya está escrita en `SourceEncoding`; falta un único lector de texto que la aplique y
que usen las cinco rutas.

### H-9. La deriva de los QN de cbm no se detecta (media)

**Evidencia.** D3 ancla el analizador en los QN de cbm replicando «sus rarezas» (`…-design.md:46`) `[V]`, P9 crea como
nodos nuevos las declaraciones que no casan si el QN está libre (`…-plan.md:95-96`) `[V]`, la huella de indexación es
`projects.name|indexed_at` sin versión de cbm (`CORE/Enrichment/EnrichmentRuns.cs:114-126`) `[V]` y el resumen que se
informa al usuario no incluye la proporción de tipos o métodos casados frente a creados
(`CORE/Steps/LanguageEnricherStep.cs:242-283`) `[V]`; esos recuentos solo quedan en `details` de la fila.

**Coste y corrección.** Si una versión nueva de cbm cambia cómo deriva los QN, la pasada «sale bien» y duplica miles
de nodos en paralelo a los de cbm, sin error ni aviso `[I]`, porque P9 convierte cada fallo de casado en un nodo nuevo.
Guardar la versión de cbm en la huella y avisar (o marcar `failed`) cuando los nodos creados superen un porcentaje
pequeño de las declaraciones convierte esa deriva en un fallo visible.

### H-10. Ingesta de Office marca el grafo como enriquecido (media)

**Evidencia.** Cualquier `.docx` en el repositorio activa la ingesta de Office y fija `isEnriched = true`
(`CORE/Steps/DetectAndIndexGraphStep.cs:93-101`) `[V]`, y con esa marca `BdpEnricherStep` se salta tanto el CLI de
Roslyn como el fallback (`CORE/Steps/BdpEnricherStep.cs:66`) `[V]`. El README lo documenta como comportamiento.

**Coste y corrección.** Un repositorio .NET con un documento Word en `docs/` pierde todas las métricas de Roslyn (ciclomática,
acoplamiento, DI) y sus facetas llegan vacías `[I]`, porque la marca mezcla dos conceptos, «hay artefactos Office» y
«el código está enriquecido». La condición que ya usa el analizador Java, una fila `ok` en `enrichment_runs` para el
enriquecedor concreto, es la que debería sustituir a `IsEnriched`.

### H-11. Menores (baja)

- **ForceReindex destruye antes de construir:** mueve BD e informes a `backups/` antes de indexar
  (`CORE/Steps/DetectAndIndexGraphStep.cs:41-44,52-63`) `[V]`; si el indexador falla, la web se queda sin grafo ni
  informes visibles. Las copias no rotan (`:167`) `[V]`: `TestAgenteAnalisis` acumula 14 con 191 MB `[V]` (`du` de hoy),
  y en el repositorio Java cada una pesaría 1,3 GB `[I]`. El JSONL de 744 MB se queda en `.codebase/enrichment/java/`
  tras aplicarlo `[I]` (no hay borrado en `JavaAnalyzerRunner` ni en el paso).
- **Esquema duplicado de cbm:** `GraphSchema.EnsureAsync` añade `file`/`line_start` junto a `file_path`/`start_line`
  y los rellena con seis `UPDATE` de tabla completa en cada ejecución y cada regeneración (`CORE/Support/GraphSchema.cs:105-119`)
  `[V]`. Dos columnas para un dato obligan a todos los escritores a mantener las dos parejas.
- **Fases por nombre de paso en tres sitios:** `AnalysisEventMapper` (`CORE/Progress/AnalysisEventMapper.cs:31-56`),
  `runReducer` (`WEB/ClientApp/src/state/runReducer.ts:23-31`) y `FakeAnalysisWorkflow` `[V]`, aunque el DTO ya lleva
  `Phase` (`CORE/Progress/AnalysisEventDto.cs:19`) `[V]`. Añadir un paso de primer nivel exige tocar los tres.
- **Rol silencioso:** `ResolveRole` devuelve el primer rol configurado si falta el pedido
  (`CORE/AnalysisPipelineFactory.cs:165-172`) `[V]`; hoy lo tapa `RequiredRoles`, pero un auditor puede acabar con el
  prompt de sistema de otro rol sin aviso.
- **Estado en campos de instancia:** `CustomStatelessTemplateLlmStep` guarda `_conversation` y `_finalAnswerMode` en
  campos (`CORE/Steps/CustomStatelessTemplateLlmStep.cs:32-33`) `[V]`, justo lo que `BaseLlmStep` prohíbe por
  concurrencia (`Framework/LLM/Steps/BaseLlmStep.cs:117-124`) `[V]`. Hoy funciona porque la factoría crea pasos por
  ejecución; es una trampa para quien los reutilice.
- **Secretos en logs:** se registra el prefijo de 4 caracteres de la clave (`CORE/Hosting/LlmRoleFactory.cs:62`,
  `OpenAILLmService/OpenAILlmService.cs:110-113`) `[V]`, y con `--debug` se vuelca el cuerpo de cada petición, con
  código y fragmentos, en `<repositorio>/logs` (README de la CLI, §2) `[V]`. El endpoint real de Azure está en el JSON
  del sample (`CORE/appsettings.analysis.json:99-100`) `[V]`.
- **La salida se escribe dentro del repositorio analizado:** `logs/` es fijo (`CORE/Hosting/AnalysisRunOptions.cs:112`)
  `[V]` y `reports/` y `.codebase/` lo son por defecto; en un repositorio de cliente eso ensucia `git status` y
  facilita commitear informes.

## 4. Puntos de mejora

**Evaluación.** No hay arnés de evaluación: los tests usan servicios LLM falsos y la web un workflow falso
(`WEB.Tests/FakeAnalysisWorkflow.cs:28`) `[V]`, y la calidad se juzga leyendo informes y logs
(`docs/project_notes/codebase-analysis-pipeline-estado.md:42,109`) `[V]`. Sin un conjunto fijo de repositorios con
hallazgos conocidos y comprobaciones automáticas no hay manera de decir si un cambio de plantilla mejora o empeora, y
con temperatura 0,1-0,2 sobre un modelo de razonamiento dos ejecuciones iguales ya difieren `[I]`. El arnés mínimo útil
es determinista y barato: que cada `ruta:línea` citada exista, que los identificadores sean únicos, que cada P0/P1
apunte a una línea real, y una lista de hallazgos esperados por repositorio contra la que medir cobertura y falsos
positivos. El juez LLM puede venir después.

**Procedencia de cada informe.** La línea `analysis-meta` solo lleva `health` y `critical`
(`Samples/CodebaseAnalysisPipeline/README.md:105-111`) `[V]`. Añadir modelo, SHA de las plantillas, huella del grafo y
fecha permitiría comparar ejecuciones, que es la condición previa de la evaluación.

**Coste por informe.** En la única ejecución con log de depuración, los nueve auditores y la síntesis suman unos 545 000
tokens sobre un repositorio de 262 nodos, y el de arquitectura solo consume 231 535 (225 689 de prompt)
(`TestAgenteAnalisis/logs/analysis-pipeline-20260927.log:36329`) `[V]`; los tokens se acumulan entre iteraciones de
herramientas (`Framework/LLM/Steps/BaseLlmStep.LlmInvocation.cs:166-180`) `[V]`. Ese volumen lo explica el reenvío del
historial completo en cada iteración, no las facetas, y crecerá con el repositorio `[I]`. El análisis no agrega ni
muestra el coste por ejecución, aunque los eventos llevan `tokens` y `costUsd` (`CORE/Progress/AnalysisEventMapper.cs:193-194`)
`[V]`. Las facetas se serializan indentadas (`CORE/Steps/SqlFacetExtractorStep.cs:560`) `[V]`, que es un 20-30 % de
tokens sin información `[I]`.

**Nueve informes solapados.** Errores y APIs comparten secretos y rutas, deuda, code smells y arquitectura comparten
complejidad y acoplamiento, y la síntesis vuelve a resumirlo todo. El valor del LLM está en priorizar y explicar, y el
de las tablas en el SQL; cinco informes con tablas generadas por código y comentario del LLM costarían la mitad y se
contradirían menos.

**Regeneración desacoplada de la ejecución.** El pipeline de regeneración solo necesita el grafo y los informes en
disco (`CORE/Steps/PrepareRegenerationStep.cs:43-59`) `[V]`, así que atarlo a la ejecución en memoria (R3, R7) es una
restricción artificial. Regenerar cualquier informe desde la biblioteca, también uno correcto, es además la herramienta
natural para iterar plantillas.

**Configuración.** Hay límites de tiempo en `CodebaseAnalysis:Timeouts` (minutos), `JavaAnalyzer` (segundos),
`CodebaseAnalysis:Chat` (minutos), cinco minutos fijos en facetas y preparación, 30 s fijos en la extracción de
documentos y el `AITaskAgent:Timeouts` del framework, que no aparece en ningún JSON y es el que manda en el chat. Una
sección única, en una sola unidad, y un volcado al arrancar de los límites efectivos de cada paso evitarían la clase
de fallo del 28/09.

**Alcance de V2.** Documentos (12 familias), embeddings, versiones de informes y conocimiento suman unas 4200 líneas de
C# en Core (`Documents` 1606, `Knowledge` 1848, `Reports` 719) `[V]` (recuento con `wc`), más que los pasos que
producen los informes (3050) `[V]`. Los embeddings solo indexan documentos e informes, no código `[V]`
(`CORE/Knowledge/Embeddings/EmbeddingStore.cs:154-162` busca en `chunks` de fuentes indexadas desde la web). Es
superficie que mantener sin evidencia de uso; congelarla hasta que el núcleo de informes esté evaluado sería lo sensato.

**Rendimiento en el repositorio grande.** El análisis recorre el repositorio en disco al menos cinco veces (copia de
seguridad, Office, Markdown, `RepositoryScan` y ecosistema) `[I]`, y las facetas hacen unas treinta consultas con
`json_extract` sobre la tabla entera sin índices `[V]` (`CORE/Steps/SqlFacetExtractorStep.cs:68-414`). Con 1,3 GB de BD,
el límite fijo de 5 minutos para facetas no se ha probado nunca `[?]`, igual que la memoria del proceso .NET al aplicar
el JSONL de 744 MB `[?]`.

**Tests con JDK.** Los dos tests extremo a extremo del analizador Java se omiten en esta máquina (ejecución de hoy)
`[V]`, así que ningún test ejercita hoy el camino Java completo. Un JDK portable en `tools/` o una variable en CI lo
resolvería sin tocar el diseño de P15.

## 5. Revisión de decisiones

### 5.1 Enriquecimiento nativo Java (D1-D15 y Change Log)

| Decisión | Veredicto | Motivo |
|---|---|---|
| D1 lenguaje y plugins de framework separados | Mantener | Separación correcta y barata. |
| D2 selección por ficheros en disco | Mantener | Señal fiable; el coste aceptado es real y pequeño. |
| D3 anclarse en los QN de cbm | Revisar | Parte de su motivo no existe: la app nunca lee `node_vectors` ni `nodes_fts` (solo los borra, `CORE/Enrichment/EnrichmentMarks.cs:138-139`) `[V]`. Conservar nodos de ficheros no Java sí vale; mantener con la alarma de deriva de H-9. |
| D4 `javac` 17 con `--release 8` | Mantener | Elegido midiendo las alternativas. |
| D5, D6 configuración de JVM y un solo lote | Mantener | Razonables y medidos. |
| D7 sobrecargas como nodos | Mantener | Coherente; `overload_of` hoy no lo consume nadie. |
| D8 contrato de escritura | Mantener | Lo mejor del diseño. |
| D9 JSP estructurales | Mantener el alcance, revisar la entrega | El análisis se hace y su resultado no llega a ningún informe (H-1). |
| D10 Java antes que C# | Mantener | Sigue el caso objetivo. |
| D11 fichero intermedio y escritura en .NET | Mantener | Contrato en un sitio; borrar o comprimir el JSONL tras aplicarlo. |
| D12, D14 JDK y jars *provided* por configuración | Mantener | Sin configuración no hay análisis Java, pero degrada con aviso. |
| D13 *source launcher* | Mantener | A 964 líneas está en el límite que la propia decisión fija; compilar y cachear si crece. |
| D15 retirar el código de test | Mantener | Decisión del usuario con coste conocido; marcar `is_test` habría sido reversible. |
| Change Log 1 (regla de JSP sospechosa afinada) | Mantener | Bajar de 787 a 26 JSP marcadas es el ajuste correcto y está medido. |
| Change Log 2 y 3 (recuento solo de `src/`, sin fila si se omite) | Mantener | Consecuencias directas de D15 y P3. |

### 5.2 Plan (P1-P16)

| Decisión | Veredicto | Motivo |
|---|---|---|
| P1, P2 paso nuevo y Java sin fallback LLM | Mantener | P2 evita además el problema de H-3 en Java. |
| P3 condición de ejecución por huellas | Revisar | La versión del enriquecedor es una constante manual (`CORE/Enrichment/Java/JavaGraphWriter.cs:93`) `[V]`: editar `JavaAnalyzer.java` sin subirla hace que la pasada se omita. Usar el SHA del fuente y la versión de cbm. |
| P4, P5 carpeta de trabajo y formato | Mantener | Limpiar el resultado tras aplicarlo. |
| P6 codificación por fichero | Mantener y extender | Aplicarla también a las herramientas y facetas (H-8). |
| P7, P8, P10-P13 casado, sobrecargas, aristas, propiedades, marcas y retirada de tests | Mantener | Bien especificados y probados sin JDK. |
| P9 crear nodos para declaraciones sin casar | Revisar | Compensa en silencio lo que debería alarmar (H-9). |
| P14 nombres del plugin Struts/JSP | Mantener, con corrección | Los nombres están bien; `Route` sin `auth_required` cae en la trampa de H-2. |
| P15 `JdkFact` | Revisar | Correcto como mecanismo, pero hoy deja el camino Java sin test ejecutado. |
| P16 fase `enrich` con dos pasos | Mantener | Cumple; el problema de fondo es el acoplamiento por nombre (H-11). |

### 5.3 Regeneración (R1-R8, tomadas sin consultar, y C1-C7)

| Decisión | Veredicto | Motivo |
|---|---|---|
| R1 timeout propio del análisis | Mantener, ampliar | Arregla el síntoma del análisis; la misma clase de fallo sigue en el chat y en las llamadas colgadas (H-4). |
| R2 límites en configuración | Mantener, consolidar | Correcto; unificar unidades y sitios (§4). |
| R3 regenerar dentro de la misma ejecución | Revisar | Introduce la vuelta de terminal a `running`, dos `run.completed` y el cálculo de C7, que salió con un fallo en la verificación; y provoca H-7. Una regeneración independiente de la ejecución sería más simple. |
| R4 pipeline reducido sobre el grafo existente | Mantener | Es lo pedido y es barato. |
| R5 síntesis por defecto | Mantener | Sin ella el `00` queda incoherente. |
| R6 botón solo en carriles fallidos (con C1, también cancelados) | Revisar | Excluye regenerar un informe correcto, que es el caso de uso al iterar plantillas. |
| R7 solo la última ejecución en memoria | Revisar | La regeneración no necesita la ejecución (§4); desacoplarla elimina la restricción sin persistir nada. |
| R8 el cliente acepta ficheros y tipos en `failedReports` | Revertir | Es un parche sobre un contrato incoherente: el servidor debería mandar tipos (o ambos campos) y el cliente mapear para mostrar. Coste bajo. |
| C1-C5 | Mantener | Correcciones razonables de implementación. |
| C6 token propio y mensaje de timeout | Mantener | Bien razonado; replicarlo en el chat. |
| C7 `failedReports` acumulado | Mantener mientras exista R3 | Es complejidad derivada de R3. |

## 6. Recomendaciones priorizadas

### Ahora

| # | Acción | Esfuerzo | Riesgo de no hacerla |
|---|---|---|---|
| 1 | `Timeout` explícito en el paso del chat, `DefaultStepTimeout` fijado en la configuración y test que falle con pasos sin `Timeout` (H-4) | S | Preguntas largas que fallan con un mensaje opaco. |
| 2 | Quitar de OfficeTools las reglas con nombres del repositorio de prueba (H-6) | S | Seguir evaluando la calidad sobre un banco con respuestas precargadas. |
| 3 | Facetas: `NULL` como desconocido y `ORDER BY` en cada `LIMIT` (H-2) | S | Hallazgos de código muerto y seguridad inventados con apariencia de dato. |
| 4 | Comprobar repositorio en `Regenerate` y ocultar el botón con `otherRepo` (H-7) | S | Chat leyendo otro repositorio sin aviso. |
| 5 | `ClaimAuditor`: rangos de `view_code_item` y prefijos `TOOL_ERROR` (H-5) | S | Insignias V falsas. |
| 6 | Enriquecimiento LLM: marcas impuestas en código y sin métricas numéricas (H-3) | S | Métricas inventadas tratadas como medidas. |

### Siguiente

| # | Acción | Esfuerzo | Riesgo de no hacerla |
|---|---|---|---|
| 7 | `fan_in`/`fan_out`/LOC derivados por SQL de `CALLS` y líneas, y herramienta `graph_neighbors` (H-1, H-2) | M | El analizador Java sigue sin mejorar ningún informe. |
| 8 | Facetas de JSP sospechosas y acciones Struts con forwards (H-1) | M | El objetivo de D9 queda sin entregar. |
| 9 | Plazo por petición LLM con reintento, `Retry-After` y tope de concurrencia (H-4) | M | Un cuelgue del proveedor sigue costando un informe. |
| 10 | Primera ejecución completa sobre `delta/Online`, juzgada por los informes (H-1) | M | Seguir optimizando el grafo sin saber qué produce la app en el caso objetivo. |
| 11 | Lector de texto único con la regla de codificación (H-8) | M | Tildes rotas y búsquedas vacías en repositorios Latin-1. |
| 12 | Procedencia en `analysis-meta` (modelo, SHA de plantillas, huella del grafo) | S | Imposible comparar ejecuciones. |

### Después

| # | Acción | Esfuerzo | Riesgo de no hacerla |
|---|---|---|---|
| 13 | Arnés de evaluación determinista sobre tres repositorios fijos, con hallazgos esperados | L | Los cambios de prompt se siguen decidiendo por impresión. |
| 14 | Regeneración independiente de la ejecución, también de informes correctos (R3, R6, R7) | M | Máquina de estados frágil y regeneración imposible tras reiniciar. |
| 15 | Alarma de deriva de QN y versión de cbm en la huella (H-9) | S | Grafo duplicado en silencio tras actualizar cbm. |
| 16 | Consolidar configuración de tiempos, reducir y fusionar informes, rotar copias de seguridad (§4, H-11) | M | Coste por análisis creciente y más fallos de configuración. |
| 17 | En el framework: valores por defecto de tiempos pensados para agentes LLM y borrar las opciones que nadie lee | S | Cada app nueva repite el fallo del 28/09. |

## TL;DR

El grafo Java quedó excelente, pero los informes no lo usan y la parte «determinista» a veces da por hechos datos que
no existen o que inventó el propio LLM. Antes de seguir enriqueciendo: arreglar el corte de 60 s del chat, que las
facetas no conviertan «no sé» en «no hay», y conectar las llamadas y las JSP con los informes; después, medir con un
banco de pruebas limpio.

## Seguimiento (2026-09-29)

Estado de los hallazgos H-4, H-5, H-6, H-7, H-9 y H-10 tras la tanda de correcciones del mismo día, sobre el disco sin
commitear. Ninguno se ha descartado. Suites al terminar: Core 426 con JDK (397 antes), Web 110 (109), OfficeTools 32,
OpenAILLmService 66, Vitest 198 y `typecheck` limpio.

**H-4, corregido en el chat y en `Retry-After`; pendiente el plazo por petición.** El turno ya no hereda
`DefaultStepTimeout`: `ChatAgent` aplica `TurnTimeout` con su propio token, igual que `CodebaseAnalysisPipeline` con
`PipelineMinutes` (C6), y al paso y al motor les pasa `Timeout.InfiniteTimeSpan`, nunca null
(`CORE/Chat/ChatAgent.cs:217-221,242,246`). Así el error distingue el timeout de la cancelación por el token que salta y
nombra la clave: «El turno superó el límite de N min (CodebaseAnalysis:Chat:TurnTimeoutMinutes); súbelo o pon 0 para
quitarlo.» (`:87`, `:288-294`). `TurnTimeoutMinutes` a 0 o negativo quita el límite (`:58`). No se ha dado
`Timeout = TurnTimeout` al paso, como pedía la recomendación, porque con el límite en el paso `RetryMiddleware` se queda la
cancelación y el turno falla otra vez con «The operation was canceled.». Tests: `ChatAgentTests.cs:142` (un motor con
`DefaultStepTimeout` de 200 ms no corta un turno de 600 ms), `:159` (el turno que supera el límite falla con ese texto) y
`:189` (0 sin límite), más `RegenerationTests.cs:136`, que recorre los pasos de primer nivel y las ramas del pipeline
completo, del de regeneración con síntesis y del turno del chat y falla si alguno tiene `Timeout` null. Los sub-steps LLM
del enriquecimiento y de la síntesis quedan fuera porque se invocan sin middleware. En el proveedor, el reintento respeta
`retry-after-ms` (Azure) o `Retry-After`, acotado al `MaxDelay` de la política (`OpenAILLmService/OpenAILlmService.cs:319-340,378`),
con `OpenAILLmService.Tests/RetryAfterTests.cs`. Queda sin hacer el plazo por petición y el tope de concurrencia de
auditores, porque tocan el framework y la configuración (más de tres ficheros): un `CancellationTokenSource` por intento en
`InvokeAsync`, con plazo configurable (3-5 min) y el vencimiento convertido en fallo reintentable; en streaming, un plazo
de inactividad que se reinicie con cada chunk (el `StreamingActivityTimeout` que nadie lee); y un
`MaxDegreeOfParallelism` en `ParallelStep`. Tampoco se ha fijado `AITaskAgent:Timeouts:DefaultStepTimeout` en la
configuración del sample: con el test de arriba ningún paso llega a usarlo.

**H-5, corregido.** Los resultados que empiezan por `Error`, `TOOL_ERROR` o `TOOL_CALL_ERROR` (tras blancos) no son
evidencia (`CORE/Chat/ClaimAuditor.cs:455-462`), y `view_code_item` ya no cuenta el fichero entero: aporta solo el tramo
`Location: fichero:ini-fin` de cada nodo devuelto (`:405-412`, `:513-515`). Tests: `ClaimAuditorTests.cs:179` (cuatro
formatos de error, ninguno da `Verified`) y `:190` (se verifica lo que cae en el nodo devuelto y no el resto del fichero).
La comprobación léxica que proponía el hallazgo no se ha hecho.

**H-6, corregido.** Fuera las dos reglas con nombres del repositorio de prueba (`nocout` y `sfFECALT_Fin`/`FECCON`);
quedan las genéricas de `On Error Resume Next` y `LIKE` mal entrecomillado (`OfficeTools/Steps/IngestOfficeArtifactsStep.cs:781-790`).
La regla de `nocout` no se ha generalizado: comprobar la ortografía de una palabra clave concreta sigue siendo una regla
sacada del caso de prueba. `CreateProcedureProps` pasa a `internal` para el test `OfficeTools.Tests/IngestOfficeArtifactsStepTests.cs`.
De paso se ha cambiado el ejemplo `CGAR_Filtro` de la plantilla `CORE/templates/_mermaid_guidelines.md:8` por
`Pedido_Filtro`, porque `CGAR` es un módulo del repositorio de prueba, y los mismos ejemplos en comentarios de
`ClaimAuditor.cs` y `ClientApp/src/components/claims.ts:22`. Los tests de Vitest que usan `CGAR` como cadena de ejemplo se
han dejado.

**H-7, corregido.** `Regenerate` responde 409 `run_from_other_workspace` (con `runId` y la ruta de la ejecución en el
mensaje) si el repositorio abierto no es el de la ejecución, comparando rutas completas sin barra final y sin distinguir
mayúsculas, y 409 `no_workspace` si no hay ninguno abierto (`WEB/Analysis/AnalysisRunManager.cs:140-147,177-179`,
`WEB/Api/ApiErrors.cs:58,86-89`). El cliente no pasa `onRegenerate` a los carriles cuando `otherRepo` es cierto
(`WEB/ClientApp/src/components/AnalysisTab.tsx:110-115`) y conoce el código nuevo (`api/types.ts`, `api/client.ts`); si
el 409 llega igual (otra pestaña), sale en el carril con el texto del servidor. Tests: `CodebaseAnalysisWeb.Tests/RegenerationEndpointsTests.cs:81`
y `AnalysisTab.test.tsx:90`. El bundle de `wwwroot/` no se ha regenerado: hace falta `npm run build` para verlo en la web.

**H-9, corregido el aviso; pendiente la versión de cbm en la huella.** `JavaGraphStats` calcula la proporción de tipos y
de métodos (sin las sobrecargas, que P8 crea siempre) creados por no casar con cbm, y marca deriva por encima del 5 %
(`CORE/Enrichment/Java/JavaGraphWriter.cs:78-97,120`). La fila `ok` guarda `details.cbm_match` con las dos proporciones,
el umbral y `drift_suspected` (`:415-421`), y el paso emite un aviso que termina en «Los nombres de cbm no casan con los
del analizador: ¿ha cambiado la versión de cbm?» (`CORE/Steps/LanguageEnricherStep.cs:256-257,284-296`). La pasada sigue
terminando `ok`. Tests: `Enrichment/JavaGraphWriterTests.cs:436` (el fixture no da deriva), `:454` (con los QN de cbm
renombrados, todo se crea, `details` marca la deriva y sale el aviso) y `:492` (el umbral y la exclusión de sobrecargas).
No se ha añadido la versión de cbm a la huella de indexación.

**H-10, corregido.** La ingesta de Office ya no pone `isEnriched = true` (`CORE/Steps/DetectAndIndexGraphStep.cs:99-100`),
y `CheckIfEnrichedAsync` no cuenta las propiedades de los nodos con `origin` de Office ni las de los marcados por el
analizador Java (`:436-460`): los procedimientos VBA traen `cyclomatic` y hacían lo mismo en la segunda ejecución, y el
`cyclomatic` de Java dejaba sin Roslyn un repositorio mixto Java y C#. Como consecuencia, un repositorio solo con Access
llegaba ahora al fallback del LLM sin nada que enviarle; `SemanticEnrichmentStep` no llama al modelo si no encuentra
ficheros de rutas, controladores o DI (`CORE/Steps/SemanticEnrichmentStep.cs:53-59`). Tests: `EnrichedGraphMarkTests.cs:51`
(qué marca el grafo como enriquecido), `:61` (un `.docx` no lo marca) y `:81` (sin ficheros no hay llamada al modelo). El
README de la CLI está actualizado; la condición que proponía el hallazgo, una fila `ok` en `enrichment_runs`, no sirve
para Roslyn, porque `BdpGraphEnricher` es un CLI externo que no escribe esa tabla.

**H-1, H-2 y H-3 (tanda K, mismo día).** Suites al terminar: Core 438 con JDK (426 antes) y Web 110. No se ha fusionado
ningún informe ni tocado el catálogo (`ReportType`, ficheros, plantillas por informe), que siguen pendientes del OK.

**H-1, corregido en la parte de los informes.** Los auditores tienen `graph_neighbors` y `graph_domain` además de
`graph_search` (`CORE/AnalysisPipelineFactory.cs:238-241,259`); `graph_map`, `graph_health` y `graph_flows` no, porque
ninguna plantilla las usa. `graph_neighbors` dice la procedencia de cada arista en una columna nueva: `resuelta · <enriquecedor>`
si lleva `enriched_by`, `estimada por LLM` si solo la marcó el fallback, `heurística cbm` con su `strategy` y su `confianza`
si cbm las da, y `estructura cbm` para la pertenencia (`CORE/Tools/GraphNeighborsTool.cs:43-52,341-389`). La cabecera cuenta
todas las aristas pedidas por procedencia y avisa si alguna no está resuelta (`:394-426`). `graph_search` sigue igual. La
regla de fiabilidad decidida está en `CORE/templates/_graph_rules.md:1-6`, que incluyen las nueve plantillas `report_*_system.md`
de auditor y no la de la síntesis, que no tiene herramientas; las de código muerto, arquitectura, funcional, flujos, APIs y
deuda dicen en qué paso usar `graph_neighbors` (p. ej. `report_codigomuerto_prompt.md:38`). Las facetas nuevas del material
Java/Struts están en H-2. Tests: `GraphToolsTests.cs:225` (procedencia, confianza, aviso y aristas del LLM), el `[JdkFact]`
de `GraphToolsTests` (las CALLS del analizador salen resueltas), `FacetExtractionTests.cs:315,326` (lista de tools y regla
en los nueve sistemas, no en la síntesis) y `:349` (cada rama de auditor del pipeline construido recibe esas tools). Queda
sin hacer la ejecución completa sobre `delta/Online` juzgada por los informes, y no se ha probado con un LLM real.

**H-2, corregido.** `ZeroFaninClasses` solo lista tipos con `fan_in` conocido e igual a 0, sin los que atienden una ruta ni
los que tienen subtipos, y con `fan_in` calculado solo si el tipo declara constructor, porque instanciarlo con el implícito
no deja arista (`CORE/Steps/SqlFacetExtractorStep.cs:186-204`). `UnvalidatedRoutes` solo toma `auth_required = false` (`:271-281`),
y el resumen y la matriz de rutas escriben `desconocido` cuando falta el dato (`:91-102`, `:377-384`). Cada `LIMIT` lleva un
`ORDER BY` determinista, por la métrica de la faceta y después por nombre. Un error SQL llega como «Faceta no disponible por un
error SQL …: no es un conjunto vacío» (`:792-793`). Cuando faltan las propiedades se calculan en tablas temporales de la
conexión (`:646-733`): `fan_in`/`fan_out` de métodos y tipos por `CALLS` resueltas (con `enriched_by` que no sea el fallback
del LLM, y solo en nodos marcados por el mismo enriquecedor), `method_count` por `DEFINES_METHOD`, `cyclomatic_total` como suma
si todos los métodos la tienen y la LOC por rango de líneas; cada fila dice de dónde sale el valor en `*_source`. Las
consultas que leían propiedades que nadie escribe usan ya lo calculado (`HighCyclomaticMethods`, `BloatedClasses`,
`CouplingMetrics`, `ComplexClasses`, `HotspotsRanking`, `CreationalCandidates`, `DomainEntitiesSummary`), el verbo de una ruta ya
no se inventa como `QUERY` y `entry_method` y `di_lifetime` quedan en null si nadie los escribe. Facetas nuevas: `SuspiciousJsps`
(`:283-299`; informes 05, 06 y 08), `StrutsActionsWithoutHandler` (`:206-223`; 03 y 07), `UnresolvedForwards` de
`enrichment_runs.details` (`:246`, `:743-778`; 04 y 05) y `TypeHierarchy` (`:351-364`; 06). Las facetas se serializan sin
escapar tildes (`:781-789`). Tests: `FacetExtractionTests.cs:61` (NULL), `:85` (orden), `:109` y `:172` (métricas calculadas,
heurísticas y llamadas internas que no cuentan, exclusiones de la faceta de cero), `:228` y `:274` (Struts/JSP) y `:286`
(`[JdkFact]`, acoplamiento calculado de las CALLS del analizador). La propuesta de `_common_rules.md` de marcar un vacío como
«no evaluable» sigue igual. No se ha medido el tiempo de las tablas temporales sobre la BD de 1,3 GB.

**H-3, corregido.** El código impone las marcas del fallback: `origin: "ai_enricher"`, `enriched_by: ["SemanticEnricherAgent"]`
y `enriched_at` en los nodos creados, y `enriched_by` en las aristas y en las propiedades completadas de nodos existentes,
con `EnrichmentMarks` como en P12 (`CORE/Steps/SemanticEnrichmentStep.cs:18,252-380`). Solo pasan las propiedades de una lista
blanca semántica (`:29-40`); las métricas y las marcas que proponga el modelo se descartan con un aviso que las nombra (`:113-120`).
Una arista que ya existe no se toca (`:348`), las actualizaciones se limitan al proyecto (`:408`) y antes de escribir se
retira la salida de la pasada anterior (`:269-280`), porque el fallback ya no deja el grafo marcado: el paso no pone
`IsEnriched` (`:113`) y `CheckIfEnrichedAsync` no cuenta los nodos con `origin = ai_enricher` (`CORE/Steps/DetectAndIndexGraphStep.cs:446-459`).
El ejemplo de `enricher_prompt.md` ya no lleva `cyclomatic` ni marcas de origen, y `enricher_system.md:13-16` dice que no se
escriben. Tests: `EnrichedGraphMarkTests.cs:158` (marcas, filtro, aviso, arista existente, grafo sin marcar y segunda pasada sin
acumular) y `:47`, que antes esperaba que un nodo `ai_enricher` marcara el grafo y ahora espera lo contrario. Consecuencia: en un
repositorio sin enriquecedor nativo el fallback llama al modelo en cada análisis, no solo en el primero. Las propiedades que
un fallback anterior escribió sin marca en nodos de cbm no se pueden distinguir y siguen hasta un `ForceReindex`.

**H-8, H-4 (plazo por petición y concurrencia) y el fallback de H-3 (tanda L, mismo día).** Suites al terminar: Core 453
con JDK (438 antes), Web 111 (110), FileTools 65, OpenAILLmService 79 (66), framework 107 de 108 (el fallo es el conocido de
`SampleConfigurationTests` con `OutDir` fuera del repositorio), OfficeTools 32, GeminiLlmService 12 y McpIntegration 20.
Vitest no se ha tocado. Ninguna llamada real a un LLM.

**H-8, corregido.** Comprobado antes con tests: con el código anterior, seis de los siete casos Latin-1 de
`FileTools.Tests/SourceEncodingToolTests.cs` fallaban (U+FFFD en `view_file`, `view_code_item` y el esquema, `grep_search`
sin resultados y `replace_file_content` reescribiendo la tilde). El lector único es `TextFileEncoding`, en FileTools
(`FileTools/TextFileEncoding.cs:17`): manda el BOM (UTF-8, UTF-16 o UTF-32); si no hay, UTF-8 cuando los bytes lo son; si no,
`FileToolsConfiguration.DefaultEncoding` (`FileTools/FileToolsConfiguration.cs:53`), que vale Latin-1 salvo que el anfitrión
fije otra, y no UTF-8, porque un UTF-8 de reserva vuelve a dejar U+FFFD. Lo usan `view_file` (`FileTools/Tools/ViewFileTool.cs:90`),
`grep_search` (`GrepSearchTool.cs:170`), `view_code_item` (`ViewCodeItemTool.cs:93`), `view_file_outline` (`ViewFileOutlineTool.cs:89`)
y las dos de edición a través de `FileToolsHelper.ReadWithEncodingAsync` (`FileToolsHelper.cs:23`), que devuelve la
codificación con la que reescribir el fichero, así que editar un fichero Latin-1 lo deja en Latin-1. `grep_search` no usa
ripgrep sino expresiones regulares de .NET sobre el texto ya decodificado, de modo que no hay limitación de `--encoding`. En la
app, `SourceEncoding.Decode` e `IsValidUtf8` delegan en ese lector (`CORE/Enrichment/SourceEncoding.cs:79,86`), y
`ResolveReadFallback` (`:94`) da la codificación de las lecturas: la de `JavaAnalyzer:DefaultEncoding` o el `pom.xml`, salvo que
sea UTF-8 o no haya ninguna, y entonces Latin-1. `AnalysisToolRegistration` la fija en FileTools al registrar las tools
(`CORE/Hosting/AnalysisToolRegistration.cs:37`) con la clave que `BuildInput` copia en `AnalysisPipelineInput.SourceDefaultEncoding`
(`CORE/Hosting/AnalysisRunOptions.cs:127`), y la web la pasa también al abrir un repositorio (`WEB/Workspace/WorkspaceState.cs:183`).
Usan el lector el código de `graph_search` con `include_code` (`CORE/Tools/GraphSearchTool.cs:305`), `/api/source`
(`WEB/Api/SourceEndpoints.cs:58`) y, aunque la tanda no los pedía, las otras dos rutas del hallazgo: el escáner de secretos
(`CORE/Support/HardcodedSecretScanner.cs:63`) y los fragmentos de las facetas (`CORE/Steps/SqlFacetExtractorStep.cs:908`). Tests:
`FileTools.Tests/SourceEncodingToolTests.cs:53-190` (Latin-1 y UTF-8 con y sin BOM en las cinco tools, búsqueda con tilde y
sin distinguir mayúsculas, edición que conserva los bytes, windows-1252 configurado, reglas de `Decode` y cortes de línea),
`CodebaseAnalysis.Core.Tests/SourceEncodingAppTests.cs:45` (resolución con configuración, `pom.xml`, UTF-8 y nombre inválido),
`:54` (el registro fija la codificación) y `:69` (`include_code` con Latin-1), y `CodebaseAnalysisWeb.Tests/SourceEndpointsTests.cs:62`
(windows-1252 declarado en el `pom.xml`). Límite: al reescribir en Latin-1 un carácter que no existe en él, como un emoji o el
euro, sale `?`.

**H-4, hecho el plazo por petición y el tope de concurrencia.** Dos opciones nuevas en la fachada `LlmInferenceSettings`,
`RequestTimeoutSeconds` y `StreamingInactivityTimeoutSeconds` (`Framework/LLM/Configuration/LlmInferenceSettings.cs:93,104`).
Viajan en `Options`, así que `BaseLlmStep` ya las fusiona del proveedor y del rol sin tocar nada más; null deja el valor del
servicio y 0 quita el límite. `OpenAILlmService` aplica por defecto 10 min por intento sin streaming y 2 min de inactividad con
streaming (`OpenAILLmService/OpenAILlmService.cs:145,151`), no las reenvía al proveedor (`:204`) y deja el cliente sin su timeout
fijo de 30 min (`:122`). Cada intento tiene su token enlazado al del llamante (`:358`); si vence, el intento es un
`TimeoutException` reintentable que nombra la clave («The request to model '…' got no response within the per-attempt limit
of N s (RequestTimeoutSeconds); raise it or set 0 to remove it.», `:382`), y una cancelación del llamante sigue propagándose
como cancelación, porque el catch de transporte ya no se traga ningún `OperationCanceledException` (`:466`). En streaming el
plazo se arma para la cabecera y para cada línea y se desarma mientras el llamante procesa lo recibido (`:592-617`, `:726`); al
vencer lanza un `TimeoutException` que `BaseLlmStep` convierte en fallo reintentable del proveedor. El manejador HTTP es un punto
de extensión (`CreateHttpMessageHandler`, `:136`), que es el que usan los tests. `TimeoutOptions.StreamingActivityTimeout`, que
nadie leía y valía 30 s, queda `[Obsolete]` apuntando a la opción nueva; no se borra para no romper configuración ni código
(`Framework/Configuration/TimeoutOptions.cs:23`). La app lee `Inference:RequestTimeoutSeconds` e `Inference:StreamingInactivityTimeoutSeconds`
del proveedor y las mismas claves, sin prefijo, del rol (`CORE/Hosting/LlmRoleFactory.cs:76-77,147-148`). Tests:
`OpenAILLmService.Tests/RequestTimeoutTests.cs:131-252`, con un manejador falso: valores por defecto, 0 y valores fuera de
rango, claves fuera del cuerpo, intento colgado reintentado y agotado, reintento que acaba bien, cancelación del llamante,
stream que se para, cabecera que no llega, plazo que se reinicia con cada trozo y cancelación con el stream en silencio. Más
`CodebaseAnalysis.Core.Tests/LlmRoleFactoryTests.cs:220`. `GeminiLlmService` no comparte el patrón: el SDK Google.GenAI 0.10
no acepta `CancellationToken` en `GenerateContentAsync` ni en el streaming y solo expone un `HttpOptions.Timeout` por cliente,
así que no se ha tocado; ignora las dos opciones y avisa de ellas como de cualquier opción que no reconoce.

`ParallelStep` tiene `MaxDegreeOfParallelism` (`Framework/Core/Steps/ParallelStep.cs:50`); con 0 o menos usa las mismas opciones
que la sobrecarga sin `ParallelOptions` que tenía, el planificador por defecto y una rama por procesador (`:56`, `:71`), así que
sin configurar el comportamiento no cambia. La app lo toma de `CodebaseAnalysis:Reports:MaxParallelAuditors`, 0 por defecto y
con aviso si no es un entero ≥ 0 (`CORE/Hosting/AnalysisRunOptions.cs:132`, `CORE/Models/AnalysisPipelineInput.cs:45`), y la
factoría lo aplica a `TolerantReportsParallelStep` en el análisis y en la regeneración (`CORE/AnalysisPipelineFactory.cs:201`).
Tests: `Tests/ParallelStepConcurrencyTests.cs:61,74,86`, `CodebaseAnalysis.Core.Tests/RegenerationTests.cs:130` y
`AnalysisRunOptionsTests.cs:179`. Quedan dos consecuencias por decidir con datos. Con los valores por defecto, un intento
colgado consume 10 de los 15 min de `AuditorMinutes` y al reintento le quedan 5. Y con Azure y `gpt-5.4` en streaming, que
entregan la respuesta entera al final (estado del 28/09), una respuesta del chat que tarde más de 2 min corta por inactividad.
No se ha cambiado ninguna de las dos en `appsettings.analysis.json`.

**El fallback de H-3 ya no se repite en cada análisis.** `SemanticEnrichmentStep` registra su pasada en `enrichment_runs`
(enriquecedor `SemanticEnricherAgent`, versión 1, `CORE/Steps/SemanticEnrichmentStep.cs:28`), con los recuentos en la fila `ok`
(`:159`) y una fila `failed` si la respuesta viene vacía o no es JSON (`:142,173`). Antes de llamar al modelo se salta, con un
mensaje, si hay una fila `ok` con esa versión, la misma huella de indexación y la misma huella de lo que envía, que es el
lenguaje, los frameworks y el lote de ficheros (`:99-101`), igual que P3. `ForceReindex` o una reindexación de cbm, que cambia
`indexed_at`, lo repiten; sin tabla `projects` no hay huella y se ejecuta siempre, como antes. El grafo sigue sin marcarse como
enriquecido, así que Roslyn se vuelve a intentar en cada ejecución. Test: `CodebaseAnalysis.Core.Tests/EnrichedGraphMarkTests.cs:235`,
con `CannedLlmService`: la segunda ejecución no llama al modelo y la salida anterior sigue en el grafo, y una reindexación, otros
ficheros o `ForceReindex` vuelven a llamarlo.

**H-9, H-11 y los tests con JDK (tanda A, robustez, mismo día).** Suites al terminar: Core 509 con JDK y sin
`CODEBASE_ANALYSIS_TEST_JDK` (486 antes; ningún `[JdkFact]` omitido), Web 112 (111), FileTools 68 (65) y OpenAILLmService 80
(79). El framework no se ha tocado. Ninguna llamada real a un LLM. Quedan fuera las dos incidencias de H-11 que no eran de
esta tanda: el esquema duplicado de cbm y las fases por nombre de paso en tres sitios.

**H-9, hecha la versión de cbm en la huella.** La BD de cbm no guarda la versión que la produjo (ni tabla ni `user_version`,
comprobado en la de este repositorio), y `codebase-memory-mcp --version` sí la da (`codebase-memory-mcp 0.6.1`).
`CodebaseMemoryLocator.GetVersionAsync` la lee con un plazo de 30 s y null si no responde (`CORE/Support/CodebaseMemoryLocator.cs:77`),
`DetectAndIndexGraphStep` la pide antes de indexar y la apunta tras copiar la BD en la tabla propia `cbm_index_versions`, por
`(project, indexed_at)` (`CORE/Enrichment/EnrichmentRuns.cs:151`), y la huella pasa a `projects.name|indexed_at|cbm=<versión>`
(`:123`). Una indexación que no hizo el pipeline, con otro `indexed_at`, queda sin versión en lugar de heredar la anterior, y
sin versión legible se avisa y la huella queda como antes. Alcance real: como `ForceReindex` sustituye la BD entera y cbm
cambia `indexed_at` al indexar, la versión no cambia cuándo se repite un enriquecimiento en ningún caso que se haya
identificado `[I]`; lo que añade es que cada fila de `enrichment_runs` diga con qué cbm se indexó el grafo que enriqueció. La
deriva de QN la sigue detectando el aviso de `JavaGraphStats`. Tests: `Enrichment/EnrichmentFoundationTests.cs:353` (la huella
lleva la versión, otra versión deja de casar con la pasada `ok` y otro `indexed_at` la pierde) y `:387` (lectura de la línea
de versión).

**H-11, corregido lo de esta tanda.**
- *ForceReindex*: la BD y los informes se copian a `backups/<fecha>/` y siguen en su sitio mientras se indexa; si el indexador
  falla o se cancela, la BD se restaura desde la copia y el paso falla con su error; solo si termina bien salen los informes
  anteriores (`CORE/Steps/DetectAndIndexGraphStep.cs:63-79`, `:187`, `:303`, `:326`). Antes de copiar la BD nueva se borran
  el `-wal`, el `-shm` y el `-journal` de la anterior, que ahora siguen allí (`:449`). Las carpetas `access_export` se siguen
  moviendo, porque cbm las indexaría como código y la ingesta de Office las regenera, y vuelven si la indexación falla. Las copias
  rotan con `CodebaseAnalysis:Backups:Keep` (5; 0 sin límite; `:262`, `CORE/Hosting/AnalysisRunOptions.cs:157`). Tests:
  `ForceReindexBackupTests.cs:81` (un indexador falso que estropea la BD y sale con error deja la BD y el informe anteriores),
  `:120` (copia sin mover), `:141` (rotación) y `:172` (la clave), y `ReportWriteTests.cs:217` adaptado.
- *JSONL*: `java-analysis.jsonl` se borra tras aplicarlo bien, salvo con `JavaAnalyzer:KeepIntermediate` (`false`;
  `CORE/Steps/LanguageEnricherStep.cs:193,214`). Tests: `Enrichment/LanguageEnricherStepTests.cs:385` y `:394`, los dos `[JdkFact]`.
- *Rol silencioso*: `ResolveRole` falla nombrando el rol que falta y los recibidos (`CORE/AnalysisPipelineFactory.cs:171`). No
  rompe configuraciones válidas porque la CLI y la web construyen los roles con `LlmRoleFactory.BuildRoles`, que crea los
  `RequiredRoles` que falten. Tests: `LlmRoleFactoryTests.cs:254` y `:269`.
- *Estado en campos*: la conversación y el modo de respuesta final de `CustomStatelessTemplateLlmStep` viven por
  `PipelineContext` en una `ConditionalWeakTable` (`CORE/Steps/CustomStatelessTemplateLlmStep.cs:37,46`); `PipelineContext.StepState`,
  el que usa `BaseLlmStep`, es interno del framework. Test: `CustomStatelessTemplateLlmStepTests.cs:111`, el mismo paso en dos
  ejecuciones intercaladas con la segunda arrancando mientras la primera está en su intento final; con el código anterior falla.
  `StatelessTemplateLlmStep` del framework sigue guardando `_currentConversation` en un campo para la suspensión, fuera de esta tanda.
- *Secretos en logs*: ni la CLI ni el proveedor registran ya el prefijo de la clave, solo su longitud (`CORE/Hosting/LlmRoleFactory.cs:63`,
  `OpenAILLmService/OpenAILlmService.cs:114`). Tests: `LlmRoleFactoryTests.cs:98` y `OpenAILLmService.Tests/ApiKeyLoggingTests.cs:60`.
  El endpoint de Azure sigue en el JSON del sample.
- *Salida en el repositorio*: `CodebaseAnalysis:LogsPath` (`logs`, relativa al repositorio o absoluta; `CORE/Hosting/AnalysisRunOptions.cs:101`)
  lo usan la CLI (`CLI/Program.cs:38`) y la web (`WEB/Workspace/WorkspaceState.cs:166`), y con `Debug` o `Trace` las dos avisan
  al arrancar de que el log guardará el cuerpo de las peticiones (`AnalysisRunOptions.cs:112`, `CLI/Program.cs:63`,
  `WEB/Program.cs:93`). `reports/` y `.codebase/` ya eran configurables. Tests: `AnalysisRunOptionsTests.cs:62` y `:83`, y
  `CodebaseAnalysisWeb.Tests/WorkspaceAndHostTests.cs:64` (el log de la ejecución cae en la carpeta configurada y no en el repositorio).

**Límite de H-8, corregido.** Al reescribir un fichero, `replace_file_content` y `multi_replace_file_content` comprueban antes
que su codificación admite todo el contenido; si no, devuelven un error que nombra los caracteres (`'€' (U+20AC)`) y no escriben
nada (`FileTools/Tools/FileToolsHelper.cs:33`, `ReplaceFileContentTool.cs:157`, `MultiReplaceFileContentTool.cs:213`).
`write_to_file` y la etiqueta `write_file` escriben siempre UTF-8 y no tienen el problema. Tests: `FileTools.Tests/SourceEncodingToolTests.cs:125,152,173`.

**Tests con JDK (§4), corregido.** `[JdkFact]` busca además `JavaAnalyzer:JdkPath` en los user secrets de la app y el primer
JDK 17 o superior de `%LOCALAPPDATA%\Programs\jdk-*` (`CodebaseAnalysis.Core.Tests/Enrichment/JdkFactAttribute.cs:70,83`); en
esta máquina ya no se omite ninguno sin la variable de entorno.

**§4 (coste, procedencia y configuración) y recomendación 12 (tanda B, mismo día).** Suites al terminar: Core 529 con JDK
(509 antes), Web 113 (112), framework 112 de 113 (107 de 108; el fallo es el conocido de `SampleConfigurationTests` con
`OutDir` fuera del repositorio), OpenAILLmService 80, Vitest 204 (198) y `typecheck` limpio. Ninguna llamada real a un LLM.
Los valores por defecto de tiempos del framework (recomendación 17) no se han tocado.

**Facetas en JSON compacto, hecho.** `FacetJsonOptions` pasa a `WriteIndented = false` y conserva
`UnsafeRelaxedJsonEscaping` (`CORE/Steps/SqlFacetExtractorStep.cs:856`), y la faceta de secretos, que se serializaba
sangrada y escapando las tildes, usa las mismas opciones (`CORE/Support/HardcodedSecretScanner.cs:103`). Medido sobre el grafo
sintético con el plugin Struts/JSP: 27 facetas no vacías, 21 122 caracteres compactas frente a 26 807 sangradas, un 21,2 % menos
(`FacetExtractionTests.cs`, `The_facets_are_compact_json`, que además comprueba que cada faceta es su propia forma compacta).
Ninguna plantilla depende del formato, porque todas envuelven la faceta en un bloque `json`, y ningún test comparaba texto
sangrado. El ahorro sobre el repositorio grande no se ha medido; los `code_snippet` no se comprimen y pesan más allí.

**Coste por ejecución, hecho, con los precios sin rellenar.** La premisa de partida no era exacta: `llm.response` llevaba
`tokens` pero no `costUsd`; lo que llevaba `costUsd` era `step.completed`, que venía de sumar las iteraciones de tools con un
`?? 0`, así que un modelo sin precio aparecía con coste 0 en cuanto había una iteración. Y el reparto entrada/salida no viajaba
en ningún evento. Se ha tocado el framework en lo mínimo: el evento final de la opción principal lleva `PromptTokens`,
`CompletionTokens` y `CostUsd` (`Framework/Observability/Events/LlmResponseEvent.cs:43-52`), también cuando la respuesta viene
vacía, que es el caso caro de un modelo de razonamiento que gasta `MaxTokens` pensando (`Framework/LLM/Steps/BaseLlmStep.LlmInvocation.cs:794`);
el coste acumulado queda null si ninguna iteración tiene precio; y el precio se busca también por el modelo pedido cuando el
servicio responde con el nombre versionado, como hace Azure (`:825`). Tests: `Tests/LlmCallUsageEventTests.cs`. En la app,
`LlmUsageTracker` suma llamadas, tokens y coste por paso y en total (`CORE/Progress/LlmUsageTracker.cs:89-129`); la web lo manda
en el `usage` de `run.completed`, acumulado con las regeneraciones (`WEB/Analysis/AnalysisRunManager.cs:391`, `:481`), la CLI lo
imprime al terminar, también si se cancela o falla (`Samples/CodebaseAnalysisPipeline/Program.cs:213`, `:222`), y los dos lo
escriben en `reports/_run_meta.json` (`CORE/Reports/RunMetaFile.cs:46`). Se ha elegido un fichero y no el `analysis-meta` del
`00` porque el consumo es de la ejecución: una regeneración sin síntesis no reescribe el `00` y la síntesis no puede contar su
propia llamada antes de escribirlo. La SPA lo suma en vivo de los `llm.response` y lo enseña en `ProgressSummary`, con el
coste solo si alguna llamada tiene precio (`ProgressSummary.test.tsx`, `runReducer.test.ts`). Los precios: el framework ya los
admite en `LlmProvider.Models` (`LlmModelSettings`), pero la app construye sus proveedores desde `LlmConfiguration` y no los
leía; ahora lee `LlmConfiguration:Providers:<p>:Models` con esa misma forma (`CORE/Hosting/LlmRoleFactory.cs:99`). El JSON del
sample declara `gpt-5.4` con los precios a `null`, porque no se conoce la tarifa del contrato de Azure, y al arrancar se avisa de
que no hay precio. Tests: `CodebaseAnalysis.Core.Tests/LlmUsageTests.cs` y `CodebaseAnalysisWeb.Tests/AnalysisEndpointsTests.cs`
(`Run_completed_and_the_run_meta_file_carry_the_llm_usage`). Limitación: una respuesta con varias opciones solo cuenta el uso
en la principal, y la app pide siempre una.

**Recomendación 12, hecha.** Tras cada auditor y antes de escribir el `00`, el pipeline añade a la línea `analysis-meta`
`generated_at`, `model` y `provider` del rol, `response_model` si el servicio dice que respondió otro, `system_sha` y `prompt_sha`,
`graph` (la huella de `EnrichmentRuns.IndexFingerprintAsync`), `enrichment` (la última fila `ok` de cada enriquecedor con esa
huella) y `app` (la `InformationalVersion`, con el commit en el que se compiló), y `fallback=true` en el `00` de respaldo
(`CORE/Steps/CustomStatelessTemplateLlmStep.cs:139`, `CORE/Steps/ExecutiveSynthesizerStep.cs:257-260`,
`CORE/Support/ReportProvenance.cs:51-120`). Los SHA son de cada plantilla renderizada sin datos: con las inclusiones resueltas y las
variables vacías. El fichero crudo no cambia cuando cambia una inclusión, y el texto con las facetas cambia con cada grafo, que
ya identifican `graph` y `enrichment`. `ReportMarkdown.SetMetaValues` sustituye las claves que el modelo haya escrito con esos
nombres y neutraliza `;` y `-->` en los valores (`CORE/Support/ReportMarkdown.cs:115`). Lectores de la línea revisados:
`SingleReportResult` (listado de la web, síntesis y semáforo), `SetCriticalInMeta`, `ReportWriter.KeepAnalysisMeta` (ediciones
desde el chat) y el `MarkdownView` de la SPA, que la oculta; todos parten por `;` y por el primer `=` e ignoran las claves
desconocidas, así que no ha habido que cambiar ninguno. Tests: `ReportProvenanceTests.cs` (cada lector, SHA que sigue a las
inclusiones y no a los datos, todas las plantillas reales con SHA distinto, huella y filas), `OfflinePipelineTests.cs` (la línea
de los diez ficheros del pipeline completo) y `MarkdownView.test.tsx`. Límite: una edición posterior desde el chat conserva la
procedencia de la última generación; el cambio queda en el historial de versiones.

**Volcado de límites efectivos (§4, «Configuración»), hecho.** Al arrancar cada análisis y cada regeneración, `AnalysisRunner`
emite una línea por grupo: `CodebaseAnalysis:Timeouts` con los dos fijos de 5 min, `JavaAnalyzer`, los plazos LLM de los roles
del análisis agrupados por valor y con su origen (`[rol]`, `[proveedor]` o `[servicio]`) y `AITaskAgent:Timeouts`
(`CORE/Hosting/EffectiveLimits.cs:23-39`, `CORE/Hosting/AnalysisRunner.cs:102`). La web vuelca al arrancar los del chat
(`WEB/Hosting/AnalysisHost.cs:27`). En la CLI los mensajes del reporter solo van a la consola, así que las líneas de límites se
escriben además en el fichero de log con una propiedad que las excluye de la consola (`Samples/CodebaseAnalysisPipeline/Program.cs:173`);
el resto de mensajes del pipeline sigue sin llegar al fichero de la CLI, como antes. Tests: `EffectiveLimitsTests.cs` y
`OfflinePipelineTests.cs`. No se ha unificado la configuración de tiempos en una sección (recomendación 16).

**Auditores: `grep_search`, código muerto por patrón y `view_code_item` en Java (tanda C, mismo día).** Suites al terminar:
Core 540 con JDK (529 antes), Web 113, FileTools 76 (68) y Vitest 204. Ninguna llamada real a un LLM. Es la propuesta (d),
capas 2 y 3; la capa 1 en el analizador sigue aparcada y `JavaAnalyzer.java` no se ha tocado.

**`grep_search` para los auditores, hecho.** Entra en `AnalysisPipelineFactory.AuditorToolNames`
(`CORE/AnalysisPipelineFactory.cs:251-254`); `find_by_name` y `list_dir` no, porque `grep_search` con `Includes` ya localiza
ficheros y cada tool más es esquema en cada llamada. Revisada para un repositorio grande: recorría también `.git`, la carpeta de
la BD y la de informes (un sufijo buscado en la raíz encontraba el informe anterior) y los binarios, devolvía líneas enteras de
ficheros minificados y no decía si había cortado. Ahora no entra en `.git`, `bin`, `obj` ni `node_modules`, ni en
`FileToolsConfiguration.SearchExcludedDirectories`, que la app fija con la carpeta de la BD y la de informes
(`CORE/Hosting/AnalysisToolRegistration.cs:44-51`); salta binarios y ficheros de más de 1 MB, recorre en orden ordinal, corta
las líneas a 300 caracteres y, al llegar a las 50 coincidencias, añade un último elemento `Truncated` sin `Filename`, que
`ClaimAuditor` ignora (`FileTools/Tools/GrepSearchTool.cs:37-50`, `:177`, `:233-241`). La decodificación de H-8 no cambia.
Tests: `FileTools.Tests/ToolBehaviorTests.cs` y, sobre las ramas del pipeline, `AuditorToolsTests`.

**Capa 2, candidatos agrupados por patrón, hecha.** `ZeroFaninClasses` y `UnreadFields` leen todos los candidatos y
`DeadCodeGrouping` los agrupa con un algoritmo determinista (tokens CamelCase, prefijo o sufijo de tokens más frecuente desde
10 candidatos, alargado mientras conserve el 80 %, y directorio para el resto). Los grupos van en `ZeroFaninGroups` y
`UnreadFieldGroups` con patrón, recuento y tres ejemplos, y no se repiten en las listas (`CORE/Steps/SqlFacetExtractorStep.cs:209-251`,
`:1002-1039`, `CORE/Support/DeadCodeGrouping.cs:81-120`). Tests: `DeadCodePatternTests.cs`.

**Faceta de instanciación dinámica, hecha.** `DynamicInstantiationSites` recorre el `RepositoryScan` con expresiones regulares
sobre el texto sin comentarios (Java: `Class.forName`, `newInstance`, `getDeclaredConstructor`, `ServiceLoader.load`; C#:
`Activator.CreateInstance`, `GetType`, `Assembly.Load*`; siempre con argumento no literal) y los XML que no son de build
(`class=`, `type=`, `<…-class>`), y el cruce marca `likely_dynamic` con su `evidence` (`CORE/Support/DynamicInstantiationScanner.cs:63-104`,
`CORE/Support/DeadCodeGrouping.cs:218-298`). Sobre la copia del repositorio objetivo, sin grafo: 52 sitios y 2409 clases
nombradas, 5,5 s con caché y 44 s en frío; entre los sitios, los `Class.forName` de la factoría de DAO
(`UtilidadesDAO.java:206`, `:481`). Esa factoría compone el nombre con variables y un literal final `"Impl"` (`:202`), así
que el sufijo del grupo no aparece entero: se añadió la regla de la pieza fija (un literal junto al sitio que es final de
tokens del sufijo) al comprobarlo. No se ha podido comprobar el grupo que sale sobre el grafo enriquecido de ese repositorio,
porque su proyecto de cbm se borró. Tests: `DeadCodePatternTests.cs` (Java, C# y XML sintéticos).

**Capa 3, regla en las plantillas, hecha.** `_graph_rules.md` explica `grep_search` y dice que un grupo grande con patrón común
es señal de uso dinámico (reglas 5 y 6); el 03 recibe los grupos y los sitios y la instrucción de buscar la factoría con
`grep_search`, leerla y, si la instanciación es plausible, dar el grupo como «dudoso por instanciación dinámica» con la
evidencia (`CORE/templates/report_codigomuerto_prompt.md:57`, `report_codigomuerto_system.md:10`); el 02 recibe los grupos de
campos con la advertencia de reflexión y serialización. Test: `The_dead_code_prompts_carry_the_groups_the_dynamic_sites_and_the_rule_to_verify_them`.
Sin LLM no se sabe si los auditores la siguen.

**`view_code_item` para Java, hecho.** FileTools tiene un punto de extensión, `ICodeItemLocator`, sin dependencia de Core: la
tool pregunta a los que recibe (por constructor o registrados en el contenedor) y al final a Roslyn, así que sin localizadores
se comporta como antes (`FileTools/CodeItems/ICodeItemLocator.cs`, `FileTools/Tools/ViewCodeItemTool.cs:34`, `:116-122`).
La app registra `GraphCodeItemLocator`, que toma el rango del nodo por `file_path` y QN (el de `javac` en Java, campos incluidos)
y elige sobrecarga por `param_types`, y detrás `JavaCodeItemLocator`, que cuenta llaves sin comentarios ni cadenas; los `.cs`
siguen con Roslyn (`CORE/Hosting/AnalysisToolRegistration.cs:41-42`). Cada declaración mostrada conserva la cabecera
`=== … ===` / `Location:` que lee `ClaimAuditor` (H-5); una sobrecarga ambigua se lista sin código ni esa cabecera. Tests:
`FileTools.Tests/CodeItemLocatorTests.cs` y `CodebaseAnalysis.Core.Tests/JavaCodeItemLocatorTests.cs`. Límites: el localizador
por llaves no reconoce campos ni métodos de clases anónimas, y los genéricos se comparan por su nombre escrito (`T`) y no por
el borrado que usa el grafo (`Object`).

**Streaming del proveedor OpenAI (tanda D, mismo día).** No era un hallazgo de esta auditoría, sino los dos defectos anotados
en el punto de streaming del documento de estado; se registra aquí para seguir la serie. Suites al terminar: OpenAILLmService 99
(80 antes), Core 540 con JDK y Web 113; framework sin tocar. Un stream que se cierra sin `finish_reason` cierra ahora cada
choice abierto con el mismo código que su `finish_reason` (`CloseChoice`, `OpenAILLmService/OpenAILlmService.cs:734`, llamado en
`:1006` y `:1026`), con `FinishReason.Other` y `RawFinishReason` `stream_closed_without_finish_reason` (`:581`) en lugar del
`Stop` que ponía el consumidor por defecto (`Framework/LLM/Steps/BaseLlmStep.LlmInvocation.cs:669`). Una tool call embebida
con el JSON cortado se descarta con aviso (`:790`, `IsIncompleteToolCall` en `:1094`), y una cancelación del llamante lanza
en lugar de vaciar (`:1013`). `hasYieldedText` pasa a ser por choice (`:675`). Tests: `StreamingAbnormalEndTests.cs` y
`ThinkTagReasoningExtractorTests.Flush_keeps_an_incomplete_close_tag_at_stream_end_as_reasoning`. Lo que depende de Azure
(Responses API) sigue aparcado.
