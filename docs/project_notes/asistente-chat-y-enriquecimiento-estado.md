# Asistente web: vista de tres paneles, chat auditado y enriquecimiento nativo (estado 2026-09-28, cierre de sesión)

Rama `consolidation`, **nada commiteado**. Este documento recoge solo lo de la sesión del 2026-09-28 sobre la web del
análisis de código; el estado general del pipeline sigue en `codebase-analysis-pipeline-estado.md`.

## Hecho y verificado

**Vista Asistente (SPA).** Las pestañas pasan de Análisis / Informes / Chat a **Análisis / Asistente**. Asistente tiene tres
paneles redimensionables y plegables (react-resizable-panels 4.14.1): biblioteca (la `LibraryList` de siempre), chat y
visor del fichero seleccionado, sin solapas.
- Hash `#asistente?file=<sel>&chat=<id>`; `#informes/…` y `#chat/…` se reescriben al formato nuevo.
- Conversaciones en un modal (`ChatSessionsDialog`) desde la cabecera del chat.
- Ficheros: `ClientApp/src/components/AssistantTab.tsx` (antes `ReportsTab`), `ChatPane.tsx` (antes `ChatTab`),
  `ChatSessionsDialog.tsx`, `hooks/useHashTab.ts`. `src/test/setup.ts` lleva stubs de jsdom (ResizeObserver,
  showModal y los rectángulos de paneles fuera de la pantalla).
- Verificado en Chrome headless a 1440×900 y 1024×768 (19/19): plegar, arrastrar, teclado, persistencia y enlaces
  antiguos. En jsdom no se puede probar el plegado.

**Chat acotado, con acceso al código y afirmaciones auditadas.**
- `templates/chat_system.md`: sección *Alcance* (solo el repositorio; lo demás se rechaza sin tools) y marcas
  `[V]` (código leído en el turno, con `ruta:línea`), `[D]` (informe o documento, con `nombre › sección`) e `[I]`
  (inferido). Las propuestas, criterios y objetivos no se marcan.
- Tools nuevas del chat: `list_dir`, `find_by_name`, `grep_search` (FileTools, solo lectura), registradas en
  `Hosting/AnalysisToolRegistration.cs`. `graph_search` gana `include_code`: lee el código del fichero actual por el
  rango de líneas del nodo; si no se puede leer, usa la copia de `properties.code` avisando.
- `Chat/ClaimAuditor.cs` (+ `TurnEvidence`): la evidencia sale de los resultados de las tools del turno. `ChatTurn.Audit`
  es aditivo y se persiste. Lo no respaldado queda `unchecked` con su motivo; nunca se rechaza una respuesta.
- `ChatAgent.AuditedChatStep` sobrescribe `FinalizeAsync` para guardar los mensajes del turno **antes** de que
  `BaseLlmStep` compacte la conversación (`Framework/LLM/Steps/BaseLlmStep.cs:233-250`), que borra el tráfico de tools.
- `GET /api/source?path=` (`Api/SourceEndpoints.cs`): solo texto, dentro del repo, sin `.git` ni enlaces,
  UTF-8 o Latin-1, 2 MB como máximo.
- UI: `ChatAnswer.tsx`, `claims.ts` (plugin de remark) y `viewers/SourceViewer.tsx` (highlight.js 11.11.2, ya
  dependencia directa). Las marcas se pintan como insignias y las referencias abren el visor
  (`file=src/<ruta>:<ini>-<fin>` o el id del informe o del documento).
- Probado con Azure (gpt-5.4) sobre TestAgenteAnalisis: rechaza «teorema de Pitágoras»; en «¿Qué hace
  ExecProcediment?» da 10/10 `[V]` comprobadas; en el plan de refactorización, 19 `[V]` y 5 `[D]` comprobadas y 2
  `unchecked` legítimas.
- Tests: Core 257, Web 99, Vitest 174 en verde. README del sample actualizado (§4, §8, §9).

## Hecho el 2026-09-29 (sin commitear)

**Analizador Java nativo y plugin Struts/JSP.** Spec `docs/plans/2026-09-28-enriquecimiento-nativo-java-design.md`
(con su Change Log) y plan `docs/plans/2026-09-28-enriquecimiento-nativo-java-plan.md` (P1-P16). Paso
`Steps/LanguageEnricherStep.cs`, código en `Enrichment/` (Java en `Enrichment/Java/`, web en `Enrichment/Web/`).
Aceptación sobre `delta/Online`, sin escribir en `C:\BBVA`, con los 10 criterios cumplidos:
- 99,996 % de llamadas resueltas y 0 `CALLS` de cbm en los ficheros analizados;
- los dos casos del criterio 3 correctos y 5488 sobrecargas creadas (solo `src/`);
- 1736 JSP y 1484 acciones Struts, 26 JSP sospechosas con la regla afinada;
- idempotente, y la BD de `~/.cache` no cambia;
- paso completo en unos 109 s tras la optimización de la escritura.

Configuración nueva: secciones `JavaAnalyzer` y `StrutsJsp` de `appsettings.analysis.json`. Instalación en la máquina
(2026-09-29):
- Temurin 17.0.20.1 en `%LOCALAPPDATA%\Programs\jdk-17.0.20.1+1`;
- `servlet-api-2.5` y `jsp-api-2.1` en `%LOCALAPPDATA%\Programs\java-provided`;
- configuración en los user secrets de la app (id `3e60cd17-…`): `JavaAnalyzer:JdkPath` y
  `JavaAnalyzer:ProvidedClasspath:0`.

En el repo no hay ninguna ruta de máquina. Los tests con JDK siguen necesitando `CODEBASE_ANALYSIS_TEST_JDK`.

**Timeouts configurables y «Regenerar informe».** Diseño `docs/plans/2026-09-29-regenerar-informe-design.md`,
decidido sin preguntas por petición del usuario. Hay que revisar R1-R8 con él.
- Causa del informe fallido del 28/09: el timeout global de 10 min del framework.
- `CodebaseAnalysis:Timeouts`: pipeline a 240 min por defecto, y 0 = sin límite.
- Botón «Regenerar» en los carriles fallidos o cancelados. Continúa la misma ejecución y solo ejecuta la preparación,
  las facetas, el informe y, opcionalmente, el resumen ejecutivo.

**Decidido el 2026-09-29 tras la auditoría (`docs/auditoria/2026-09-29-auditoria-app-analisis-codigo.md`, H-1).**
El chat veía el grafo solo a través de `graph_search`, que devuelve nodos sin propiedades ni aristas. Se decide:
- `graph_search` devuelve un subconjunto de `properties`;
- una tool nueva, `graph_neighbors`, da los vecinos por `CALLS`, `INHERITS`, `IMPLEMENTS`, `HANDLES` y las aristas
  Struts/JSP;
- `graph_domain` pasa a las tools del chat.

Los auditores de informes quedan para después, porque necesitan que sus plantillas les digan cómo usarlas.
Implementado el mismo día (`Tools/GraphNeighborsTool.cs`, `GraphToolsTests`; Core 397/397 con JDK), sin probar aún
con un LLM real ni sobre el grafo de `delta/Online`.

**Decidido el 2026-09-29 sobre la fiabilidad del grafo en los informes.**
- Regla de uso: el grafo sirve para la estructura. Las aristas resueltas (`enriched_by`) son fiables; las heurísticas
  de cbm, solo pistas.
- Code smells, bugs, seguridad y deuda: el grafo y las facetas dicen dónde mirar, pero el veredicto exige leer el
  código.
- Código muerto: antes de afirmarlo hay que cruzar con `HANDLES`, las JSP y la configuración.
- `graph_neighbors` muestra de qué tipo es cada arista, y los auditores reciben las tools del grafo (tanda K).

**Propuesto y pendiente del OK del usuario:**
- (a) Fusionar informes: 02 Code smells con 08 Deuda técnica, y 06 Arquitectura con 09 DI. Pasa de 9 a 7 auditores.
  Cambia el contrato de `ReportType`, los ficheros y la UI. La fusión 01+04 se decidirá después de ver los informes
  reales.
- (b) Detectores deterministas en `JavaAnalyzer`, con unas diez reglas tipo PMD que llegan como facetas y que el LLM
  tría leyendo el código.
- (c) Medir en el análisis completo los tokens por auditor y cuánto código repiten entre ellos, antes de plantear
  exploración compartida, caché de prompts o tope de concurrencia.
- (d) **Requisito del usuario (2026-09-29): el propio workflow tiene que hacer este tipo de razonamiento**, es decir,
  detectar que un grupo de candidatos sigue un patrón de uso dinámico y verificarlo antes de afirmarlo. Propuesta en tres
  capas genéricas: faceta de puntos de instanciación dinámica y de clases nombradas en XML; candidatos agrupados por
  patrón de paquete o sufijo; y una regla en la plantilla del auditor, que además recibe `grep_search`. Pendiente del OK
  para planificarlo. Caso que lo motivó, instanciación por reflexión: en `delta/Online`, unos 400 de los 817 `ZeroFaninClasses` son DAO creados con
  `Class.forName` a partir de un nombre compuesto (`UtilidadesDAO.java:202-206` de la copia). La propuesta genérica es
  que el analizador detecte `Class.forName` y `newInstance` con argumento no literal y que la faceta marque como dudosos
  los tipos de los paquetes afectados. Filtrar la factoría concreta haría la app específica de un repo.

**Corregido tras la prueba M (2026-09-29, hecho).** Discrepancias que encontró la guía
`docs/guides/codebase-analysis-flujo-y-politica-de-informes.md` (sección 2.13, donde quedan como corregidas):
- Hecho: `graph_domain` da «❔ Desconocida» sin `auth_required`, en lugar de «🔓 No» (resto de H-2).
- Hecho: `obsolete_elements` lee también `annotations`, así que el `@Deprecated` del analizador llega al 08.
- Hecho: `graph_neighbors` etiqueta «ingesta Office» las `CALLS` de Office, reconocidas por el `origin` de su nodo de
  origen, en lugar de «heurística cbm».
- Hecho: `report_deuda_prompt.md` pide la matriz esfuerzo/impacto como `flowchart` con un `subgraph` por cuadrante, sin
  `quadrantChart`.
- Hecho: `_common_rules.md` va solo en la plantilla de prompt de cada auditor y de la síntesis.
- Hecho: las plantillas ya no dicen «SQL o Sesión» en `suspicious_jsps`.
- Hecho: erratas del README de la web (`view_code_item` y los 300 s de inactividad del chat).

Además, en la misma tanda:
- `json_valid` en `graph_domain`, `graph_health`, `graph_map` y `graph_flows`.
- El aviso de deriva de cbm (H-9) separa los ficheros que cbm no indexó, con la pista del MAX_PATH, de los nombres que no
  casan, y guarda los dos recuentos en `details.cbm_match`.
- La consola de la CLI lleva hora y origen, paso o auditor, también en «Executing Tool».
- El índice por label de `GraphSchema` tiene nombre propio: el de antes chocaba con el `idx_nodes_label (project, label)`
  de cbm y no se creaba. Medido después sobre la BD real con referencias: 45,9 s con él y 46,7 s sin él, así que no es
  lo que cuesta; 24 s son de `PrepareDerivedMetricsAsync`.

**Incidencias abiertas tras la prueba M:**
- Azure no tiene deployments en el recurso (`404 DeploymentNotFound` en los nueve auditores): bloquea todo lo de la
  prueba que exige LLM, es decir, tiempos y tokens por auditor, lectura repetida entre auditores y calidad de los informes.
- Hay que repetir la prueba con la copia en una ruta corta, `C:\Users\juan.rozano\m\Online` (decidido). En la ruta
  temporal larga, cbm no indexó los ficheros que llegaban al MAX_PATH, y el analizador Java encontró 1364 ficheros más de
  los que tenía cbm.
- `LlmProvider.CalculateCost` devuelve null porque no hay precios configurados, así que `costUsd` no sirve para medir.
- La copia `C:\Users\juan.rozano\m\Online` se conserva para repetir la prueba con LLM; el proyecto de cbm se borró al
  cerrar la tarea 4 y hay que reindexar antes.
- Falsos positivos de `ZeroFaninClasses` en el repo objetivo: 400 de 817 son DAO que `UtilidadesDAO` instancia por
  nombre compuesto. Descartarlos exige saber de esa factoría, que es del proyecto y no genérica; sin decidir.

Matiz para la propuesta (a): en Java, 06 y 09 comparten menos evidencia de la que se suponía.

**Hecho el 2026-09-29: referencias de tipos y campos** (`docs/plans/2026-09-29-referencias-tipos-campos-plan.md`, los 7
criterios cumplidos). El grafo Java sabe dónde se declara lo que usa cada método:
- `USES_TYPE` de método, campo o tipo a tipo, con `lines`, `count` y `contexts`, y `READS`/`WRITES` a campos, que
  sustituyen las `USAGE` de cbm en los `.java`; los campos casan con el `Field` de cbm o se crean.
- `graph_neighbors` las incluye por defecto y filtra por `line`; el `fan_in` de los tipos cuenta las `USES_TYPE`;
  faceta nueva `UnreadFields` para 02 y 03, y `ZeroFaninClasses` sin el requisito del constructor declarado y sin los
  tipos que Struts instancia por reflexión.
- Sobre `C:\Users\juan.rozano\m\Online`: 253 753 `USES_TYPE`, 121 659 `READS` y 56 250 `WRITES`; el 99,996 % de las
  referencias del proyecto resueltas; el 95,85 % de los campos casados; dos pasadas idénticas.
- Tiempo del paso, 1,39 veces el de la versión sin referencias (mediana 166,6 s frente a 119,4 s en 6 pares alternos,
  con la máquina cargada; 1,07 en el par más tranquilo), y JSONL de 1003 MB (1,35 veces). Facetas en 45,9 s.
- `ZeroFaninClasses` da 817 tipos, y 400 son DAO `*Oracle*Impl` que la factoría instancia por `Class.forName`: falsos
  positivos. `UnreadFields` da 154 campos y los 5 revisados son verdaderos.

**Decidido el 2026-09-29: lo de Java queda aparcado hasta que vuelva Azure** (el recurso responde 404
`DeploymentNotFound` desde antes de las 13:08; a las 01:07 funcionaba). Aparcado:
- la prueba completa con informes y el chat real;
- la medición limpia del criterio 5;
- (b) detectores tipo PMD y (d) capa 1 en el analizador;
- las incidencias menores del analizador.

Mientras tanto se sigue con el código que no depende del LLM, por tandas:
- **A. Robustez — hecha el 2026-09-29** (detalle y tests en la sección «Seguimiento» de la auditoría; Core 509 con JDK,
  Web 112, FileTools 68, OpenAILLmService 80). Claves nuevas: `CodebaseAnalysis:LogsPath`, `CodebaseAnalysis:Backups:Keep`
  y `JavaAnalyzer:KeepIntermediate`. La versión de cbm entra en la huella, pero no cambia cuándo se repite un
  enriquecimiento, porque `indexed_at` ya cambia en cada indexación: solo deja constancia de con qué cbm se hizo:
  - la versión de cbm en la huella;
  - `ForceReindex` seguro, con rotación de copias y borrado del JSONL;
  - `ResolveRole` sin caída silenciosa;
  - sin estado en campos de `CustomStatelessTemplateLlmStep`;
  - sin prefijo de clave en los logs y `logs/` configurable;
  - aviso de Latin-1 en FileTools;
  - `[JdkFact]` que encuentre el JDK instalado.
- **B. Coste y trazabilidad:**
  - facetas en JSON compacto;
  - precios y coste por ejecución;
  - procedencia en `analysis-meta`;
  - volcado de los límites de tiempo efectivos.
- **C. Auditores:** `grep_search` y las capas 2-3 de (d), y `view_code_item` para Java.
- **D. Streaming:** buffers perdidos sin `finish_reason` y `hasYieldedText` por choice.
- **E. Evaluación:** validador determinista de informes.

Siguen fuera, pendientes de decisión del usuario:
- (a) fusionar informes;
- R3/R6/R7/R8 (validadas; la auditoría pide revisarlas);
- los valores por defecto de tiempos del framework;
- las columnas duplicadas del esquema.

## Pendiente (en orden)

1. **Validadas por el usuario el 2026-09-29** las decisiones R1-R8 y C1-C7 de la regeneración y el Change Log de la spec
   Java. Sigue en pie una objeción escrita en la auditoría (R3/R6/R7: la regeneración depende de la ejecución en memoria
   y no rehace informes correctos); su daño práctico, H-7, se corrige. Queda pendiente commitear.
   Para ver la SPA nueva en el backend del 5180 hace falta `npm run build` de `ClientApp` y reiniciar ese backend.
   Incidencias abiertas del analizador Java, menores:
   - 914 docstrings heredados de cbm con U+FFFD, en comentarios `//`;
   - 27 declaraciones sin nodo porque su QN lo ocupa un `Field` de cbm;
   - los `Function` gemelos de métodos de interfaz no se parchean;
   - un `struts-config` con XML inválido (`parametrizacion/markUp`).
2. **Streaming del chat con Azure — aparcado.** El arreglo del proveedor está hecho y sin commitear
   (`ClassifyContentPrefix`/`RouteContent` en `OpenAILLmService/OpenAILlmService.cs`, tests en
   `OpenAILLmService.Tests/StreamingEmbeddedToolCallPrefixTests.cs`, 57/57). Lo que queda es de Azure: con Chat
   Completions, `gpt-5.4`/`gpt-5-mini` de este recurso sueltan la respuesta entera al final (filtro de contenido sobre
   la completion completa, medido fuera de la app); `gpt-4.1-mini` y la Responses API sí gotean. El usuario no tiene
   acceso a la configuración de Azure y probará con otro proveedor. Si hiciera falta Azure: proveedor sobre la
   Responses API (contrato nuevo, pasa por `/brainstorm`). Sin tocar: si el stream se cierra sin `finish_reason` se
   pierden `embeddedToolCallBuffers` y el `PendingBuffer` de `<think>`; `hasYieldedText` es global y no por choice.
3. **Después del analizador Java**, en este orden (fuera de la primera tanda según la spec): corregir los consumidores
   que leen propiedades que nadie escribe (`SqlFacetExtractorStep.cs:82,90,108,118,128,339`: `http_verb`,
   `entry_method`, `cyclomatic`, `loc_total`, `fan_in` en Class, `di_lifetime`); portar C# a Roslyn en proceso, con
   paridad contra la BD de este repo enriquecida por BdpGraphEnricher; JSP con Jasper; nodos `Flow` end to end;
   `view_code_item` para Java (`FileTools/Tools/ViewCodeItemTool.cs:94` solo entiende C#); enriquecedor LLM para
   repos sin lenguaje soportado.

## Cómo se prueba (para repetirlo)

- El backend del usuario ocupa el puerto 5180 y bloquea las DLL: compilar con `-p:OutDir=<scratch>` y levantar una
  segunda instancia con `ASPNETCORE_ENVIRONMENT=Development dotnet <scratch>/CodebaseAnalysisWeb.dll --urls
  http://127.0.0.1:5181 --contentRoot C:\GeminiCli\AITask\AITaskAgent\Samples\CodebaseAnalysisWeb --CodebaseAnalysis:Web:SettingsPath=<scratch>\web-settings.json`
  (sin el último argumento pisa `%LOCALAPPDATA%\CodebaseAnalysisWeb\settings.json`). `--contentRoot` tiene que ser
  absoluto: uno relativo se resuelve contra la carpeta de la DLL. Para servir una SPA compilada en el scratch, añade
  `--webroot <scratch>\spa --staticWebAssets=<scratch>\no-existe.json`: en Development, el manifiesto de *static web
  assets* de la salida sirve el `wwwroot` del repo por encima de `--webroot`. Pararla **por puerto**
  (`Get-NetTCPConnection -LocalPort 5181`), nunca filtrando por línea de comandos.
- Tests .NET con `dotnet test <proyecto> -p:OutDir=<scratch>` mientras el backend del usuario esté vivo. El test
  `SampleConfigurationTests.Every_role_names_a_configured_provider_and_a_model` falla con `OutDir` fuera del repo
  (busca `AITaskAgent.slnx` subiendo desde los binarios, `Tests/SampleConfigurationTests.cs:29-35`); no es regresión.
- No crear sesiones de chat ni ficheros dentro de `C:\BBVA\...`; para probar el chat, usar
  `C:\Users\juan.rozano\Desktop\TestAgenteAnalisis`. cbm se puede ejecutar sobre el repo Java sin que escriba en él
  (`persistence` es `false` por defecto); para limpiarlo, `codebase-memory-mcp.exe cli delete_project {"project":"<nombre>"}`.
- En la máquina: el sistema tiene JRE 1.8.0_503 sin `javac` y sin Maven; el JDK es el que se configure. Hay python.

## Prompt de arranque

> Retomamos la app de análisis de código. Lee `docs/project_notes/asistente-chat-y-enriquecimiento-estado.md` entero y
> preséntame para revisar las decisiones R1-R8 de `docs/plans/2026-09-29-regenerar-informe-design.md` y el Change Log de
> `docs/plans/2026-09-28-enriquecimiento-nativo-java-design.md`, que se tomaron sin preguntarme. No commitees hasta que
> las confirme.
