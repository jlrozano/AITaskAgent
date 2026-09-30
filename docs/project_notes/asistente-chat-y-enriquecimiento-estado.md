# App de análisis de código: estado y retomada (actualizado el 2026-09-30, cierre de sesión)

Rama `consolidation`, **todo commiteado y sin push**. Fuera quedan `.claude/settings.local.json` y el `AITaskAgent.sln`
antiguo sin versionar. La sección «Para retomar» es lo único que hace falta leer para seguir; el resto es historial.

## Para retomar (2026-09-30)

**Dónde estamos.** El pipeline completo funciona de extremo a extremo con Azure `gpt-5.4`, que volvió a responder el
2026-09-30 a las 13:49 tras el `404 DeploymentNotFound` del 29/09.
- **Primera prueba real:** dos repos pequeños con fallos sembrados.
  - Repos: `C:\Users\juan.rozano\eval-repos\java-simple` (Struts, 25 `.java`) y `…\csharp-simple` (ASP.NET, 24 `.cs`).
  - Listas de respuestas: `…\eval-repos\answers\*.expected-findings.json`.
  - Evidencias (logs, `_validation.json`): `…\eval-repos\results\2026-09-30\`.
  - Los repos de evaluación no están versionados; conviene decidir si se versionan.
- **Resultado de la prueba:**

  | | Java | C# |
  |---|---|---|
  | Tiempo | 144 s | 160 s |
  | Tokens | 613.000 | 669.000 |
  | Citas válidas | 98 % | 99 % |
  | Sembrados encontrados | 16/18 a mano (10/18 según el comparador) | 18/22 |

  Las trampas de reflexión no salen como código muerto. El analizador aportó hallazgos reales no sembrados: IDOR,
  `TrustServerCertificate` y falta de `AddAuthentication`.
- **Fallos vistos en la prueba:**
  - Afirmaciones falsas:
    - el 05 de Java dice que `findByStatus` cierra en `finally`, y es la fuga sembrada;
    - el 06 cita `jsonExporterImplImpl`;
    - el 05 de C# afirma que falta el rollback, y lo hay.
  - Los bugs de lógica de Java salen en el 02 y el 08, no en el 05.
  - Nadie detecta los métodos privados muertos, los campos solo escritos en C# ni los registros DI que nunca se resuelven.

**Siguiente, en este orden** (propuesto al usuario el 2026-09-30; falta que elija por dónde empezar):
1. **Bug de `BdpEnricherStep`.**
   - Pasa como `--project` el nombre de la carpeta de la solución y no el proyecto de cbm (`BdpEnricherStep.cs:107-112`).
     BdpGraphEnricher falla con FK y el pipeline aborta (`:221`) en vez de caer al fallback LLM.
   - En la prueba se sorteó con `--BdpEnricher:ProjectName`.
   - Arreglo: `GraphProject.ResolveAsync` y degradar al fallback si falla.
2. **Ruido del validador y del comparador.**
   - Citas a directorios y a URLs `.do` que se dan por rotas (`ReportValidator.cs:817-838`, solo mira `File.Exists`).
   - Hallazgos de informes sin tabla ni IDs (el 06) que el comparador no lee.
   - Opción de buscar un esperado en cualquier informe.
   - Soporte de `notExpected` para medir falsos positivos.
3. **Verificador de informes.** Plan cerrado con el usuario, pendiente de su «adelante»:
   `docs/plans/2026-09-30-verificador-informes-plan.md`.
   - Estructura: detección de lenguaje → `SwitchStep` a los enriquecedores → facetas.
   - Cada rama de informe es un `GroupStep[Auditor*, Preparación, Verificador*]`. El verificador corrige contra el código
     y cierra el informe con «Cambios de la verificación».
   - Los IDs pasan a `NN-CAT-nnn` y todos los informes citan.
   - Framework: F1 (warning por placeholder sin resolver) y F2 (`@[ruta]` relativa al directorio de trabajo).
   - Los `@[` del borrador se neutralizan en la preparación.
   - Estimación: 2-2,5 h de agentes, en versión comprimida.
   - Las tres afirmaciones falsas de la prueba son su caso de prueba real.
4. **Facetas que faltan:** métodos privados sin llamadas, campos solo escritos en C# (Roslyn) y registros DI nunca
   resueltos.
5. **Análisis completo del repo Java grande** sobre `C:\Users\juan.rozano\m\Online`. Hay que reindexar con cbm antes.
   Estimación: ~20-25 min y 3-4 M tokens de entrada; subir `AuditorMinutes` a 25-30 para la primera vez.

**Decisiones del usuario vigentes** (detalle en la memoria `project_report_catalog_decisions`):
- no fusionar informes todavía;
- IDs únicos con prefijo por informe;
- verificación en cada rama;
- entrada del verificador como un único texto por `{{ input }}` (el placeholder es `{{ input }}`, no `{{ value }}`:
  `PromptTemplateResolver.cs:49-53`);
- la síntesis se hace sobre los informes verificados;
- anotado para después: enriquecedores JSP y React/TS, y un paso que cree pasos según lo detectado.

**Pendiente de decisión del usuario:**
- la fusión de informes. Dato de la prueba: las fronteras entre 02, 05 y 08 están difusas;
- R3/R6/R7/R8;
- los valores por defecto de tiempos del framework;
- las columnas duplicadas del esquema;
- (b) detectores tipo PMD;
- versionar los repos de evaluación;
- la tarifa de Azure en `LlmConfiguration:Providers:<p>:Models`, para que salga el coste.

**Entorno.**
- JDK 17 en `%LOCALAPPDATA%\Programs\jdk-17.0.20.1+1` y jars *provided* en `…\java-provided`. Los dos van en los user
  secrets (`JavaAnalyzer:JdkPath`, `ProvidedClasspath:0`), y los tests `[JdkFact]` ya lo encuentran solos.
- BdpGraphEnricher.CLI está instalado y se usó en la prueba C#.
- La SPA está compilada en `wwwroot`. Para ver la UI nueva hay que reiniciar el backend del 5180.
- `Samples/TextToTemplateDemo` es la demo, sin LLM, de cómo llega el texto de un paso a la plantilla del siguiente.

## Hecho y verificado (sesión del 2026-09-28)

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

## Historial: hecho el 2026-09-29 (ya commiteado)

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

En el repo no hay ninguna ruta de máquina. Desde la tanda A, los tests con JDK lo encuentran sin `CODEBASE_ANALYSIS_TEST_JDK`.

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
  patrón de paquete o sufijo; y una regla en la plantilla del auditor, que además recibe `grep_search`. **Capas 2 y 3 y la faceta de instanciación, hechas en la tanda C (2026-09-29)**; la capa 1 en el
  analizador sigue aparcada. Caso que lo motivó, instanciación por reflexión: en `delta/Online`, unos 400 de los 817 `ZeroFaninClasses` son DAO creados con
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
  nombre compuesto. Desde la tanda C se tratan de forma genérica (grupos, faceta de instanciación y regla del 03), sin
  medir aún sobre el grafo enriquecido ni con LLM.

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
- **B. Coste y trazabilidad — hecha el 2026-09-29** (detalle y tests en «Seguimiento» de la auditoría; Core 529 con JDK,
  Web 113, framework 112 de 113 con el fallo conocido de `OutDir`, OpenAILLmService 80, Vitest 204). Toca el framework en lo
  mínimo: el evento final de cada llamada lleva tokens de entrada y salida y coste. Clave nueva:
  `LlmConfiguration:Providers:<p>:Models` (precios), a `null` en el sample hasta tener la tarifa del contrato:
  - facetas en JSON compacto (un 21 % menos de caracteres en el grafo sintético);
  - consumo y coste por ejecución y por auditor en `run.completed`, en el resumen de la CLI, en `reports/_run_meta.json` y
    en `ProgressSummary`;
  - procedencia en `analysis-meta` (modelo, servicio, SHA de plantillas, huella del grafo, filas de `enrichment_runs`,
    fecha y versión);
  - volcado de los límites de tiempo efectivos al arrancar el análisis y la web.
- **C. Auditores — hecha el 2026-09-29** (detalle y tests en «Seguimiento» de la auditoría; Core 540 con JDK, Web 113,
  FileTools 76, Vitest 204). Sin LLM y sin tocar `JavaAnalyzer.java`:
  - `grep_search` en las tools de los auditores, sin `.git`/`bin`/`obj`/`node_modules`, la carpeta de la BD ni la de informes,
    sin binarios, con orden ordinal y aviso `Truncated` al llegar a 50;
  - (d) capa 2: `ZeroFaninGroups` y `UnreadFieldGroups`, candidatos agrupados por prefijo o sufijo CamelCase o directorio
    (desde 10), fuera de las listas;
  - (d) faceta genérica `DynamicInstantiationSites` (regex sobre `.java`/`.cs` y clases nombradas en XML) y cruce
    `likely_dynamic` con evidencia, también por la pieza fija de un nombre compuesto (`+ "Impl"`);
  - (d) capa 3: reglas 5 y 6 de `_graph_rules.md`, y el 03 da como «dudoso por instanciación dinámica» el grupo cuya factoría
    verifica con `grep_search`; el 02 recibe los grupos de campos;
  - `view_code_item` en Java: punto de extensión `ICodeItemLocator` en FileTools, localizador por grafo y, sin grafo, por llaves.
  Sobre la copia del repositorio objetivo, el recorrido encuentra los `Class.forName` de la factoría de DAO; falta ver qué
  grupo sale sobre su grafo enriquecido (hay que reindexar) y, con LLM, si el auditor sigue la regla.
- **D. Streaming — hecha el 2026-09-29** (detalle en «Seguimiento» de la auditoría; OpenAILLmService 99, Core 540 con JDK,
  Web 113). Solo `OpenAILLmService`; framework y `GeminiLlmService` sin tocar:
  - un stream que se cierra sin `finish_reason` (fin del cuerpo, `[DONE]` sin chunk final o error de lectura) cierra cada
    choice abierto como lo haría su `finish_reason`: vacía el lector de `<think>`, el prefijo retenido y el buffer de tool
    calls embebidas, y emite el chunk final con `FinishReason.Other` y `RawFinishReason`
    `stream_closed_without_finish_reason`, con aviso en log. Antes el consumidor lo daba por `Stop`;
  - una tool call embebida con el JSON cortado se descarta con aviso; las nativas se reenvían según llegan y no se pueden
    retirar, así que solo se avisa de las que quedan con los argumentos cortados;
  - una cancelación del llamante a mitad de stream lanza `OperationCanceledException` y no vacía nada. Antes el bucle salía
    sin excepción;
  - `hasYieldedText` pasa a ser por choice (`textYieldedChoices`), y el `Flush` del lector de `<think>` deja como reasoning un
    cierre a medias (`</thi`), que antes salía como content.
- **E. Evaluación — hecha el 2026-09-30** (detalle y tests en «Seguimiento» de la auditoría, recomendación 13; Core 569 con
  JDK, Web 116, Vitest 206). Sin LLM:
  - `ReportValidator` comprueba citas `ruta:línea` (también por sufijo), citas `nombre › sección`, identificadores
    `PREFIJO-NNN`, P0/P1 con ubicación, nombres del grafo, procedencia en `analysis-meta` y tipos Mermaid, y escribe
    `reports/_validation.json`;
  - corre al final del análisis (CLI) y de cada ejecución no cancelada (web: `run.completed` y celda «Citas válidas»), y a
    demanda con `--validate-reports` y `GET /api/reports/validation`;
  - `expected-findings.json`: formato, plantilla vacía en `CORE/evaluation/` y comparador (`--expected`). **Sin listas
    rellenas: las decide el usuario**, igual que los repositorios fijos de evaluación;
  - primera medida sobre TestAgenteAnalisis (guía, apartado 1.15): 164/164 citas válidas y ninguna procedencia (informes
    anteriores a la tanda B); el 01 y el 04 no citan código, y `SEC-001`-`003` y `ARC-001`/`002` chocan entre informes.

Siguen fuera, pendientes de decisión del usuario:
- (a) fusionar informes;
- R3/R6/R7/R8 (validadas; la auditoría pide revisarlas);
- los valores por defecto de tiempos del framework;
- las columnas duplicadas del esquema.

**Diseñado el 2026-09-30, pendiente del gate: verificación de informes.** Plan en
`docs/plans/2026-09-30-verificador-informes-plan.md`.
- Pipeline: detección de lenguaje → `SwitchStep` hacia los enriquecedores → facetas.
- Cada rama de informe es un `GroupStep` con auditor*, preparación y verificador*. El verificador corrige contra el código
  y lista los cambios.
- Los IDs pasan a `NN-CAT-nnn` y todos los informes citan el código.
- Arreglos del framework: F1 (aviso al no resolver un placeholder) y F2 (base de `@[ruta]`).
- Anotado para después: enriquecedores JSP y React/TS, y un paso que cree pasos según lo detectado.

## Historial: pendientes anteriores (lo vigente está en «Para retomar»)

1. **Validadas por el usuario el 2026-09-29** las decisiones R1-R8 y C1-C7 de la regeneración y el Change Log de la spec
   Java. Sigue en pie una objeción escrita en la auditoría (R3/R6/R7: la regeneración depende de la ejecución en memoria
   y no rehace informes correctos); su daño práctico, H-7, está corregido. Todo commiteado.
   Incidencias abiertas del analizador Java, menores:
   - 914 docstrings heredados de cbm con U+FFFD, en comentarios `//`;
   - 27 declaraciones sin nodo porque su QN lo ocupa un `Field` de cbm;
   - los `Function` gemelos de métodos de interfaz no se parchean;
   - un `struts-config` con XML inválido (`parametrizacion/markUp`).
2. **Streaming del chat con Azure — aparcado.** El arreglo del proveedor está commiteado (`39c72f4` y anteriores:
   `ClassifyContentPrefix`/`RouteContent` en `OpenAILLmService/OpenAILlmService.cs`, tests en
   `OpenAILLmService.Tests/StreamingEmbeddedToolCallPrefixTests.cs`, y el cierre sin `finish_reason` de la tanda D). Lo que queda es de Azure: con Chat
   Completions, `gpt-5.4`/`gpt-5-mini` de este recurso sueltan la respuesta entera al final (filtro de contenido sobre
   la completion completa, medido fuera de la app); `gpt-4.1-mini` y la Responses API sí gotean. El usuario no tiene
   acceso a la configuración de Azure y probará con otro proveedor. Si hiciera falta Azure: proveedor sobre la
   Responses API (contrato nuevo, pasa por `/brainstorm`). Corregido en la tanda D (2026-09-29) lo que era del código: el
   stream que se cierra sin `finish_reason` ya no pierde `embeddedToolCallBuffers` ni el `PendingBuffer` de `<think>`, y
   `hasYieldedText` es por choice.
3. **Después del analizador Java**, en este orden (fuera de la primera tanda según la spec): corregir los consumidores
   que leen propiedades que nadie escribe (`SqlFacetExtractorStep.cs:82,90,108,118,128,339`: `http_verb`,
   `entry_method`, `cyclomatic`, `loc_total`, `fan_in` en Class, `di_lifetime`); portar C# a Roslyn en proceso, con
   paridad contra la BD de este repo enriquecida por BdpGraphEnricher; JSP con Jasper; nodos `Flow` end to end;
   enriquecedor LLM para repos sin lenguaje soportado. (`view_code_item` para Java ya está hecho, en la tanda C, y los
   consumidores de propiedades ya no inventan datos, desde la tanda K.)

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

> Retomamos la app de análisis de código. Lee la sección «Para retomar» de
> `docs/project_notes/asistente-chat-y-enriquecimiento-estado.md` y la memoria `project_report_catalog_decisions`. Dime
> en tres líneas dónde estamos y pregúntame por cuál de los cinco puntos de «Siguiente» empezamos. No toques código
> hasta que te lo diga. Antes de ejecutar el plan del verificador, espera mi «adelante». Usa agentes para lo largo y lee
> tú lo que esté en uno o dos ficheros. Pon la hora al principio de cada mensaje de avance.
