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
