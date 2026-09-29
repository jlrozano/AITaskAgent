# Plan: analizador Java nativo y plugin Struts/JSP (primera tanda)

Spec: `docs/plans/2026-09-28-enriquecimiento-nativo-java-design.md` (D1-D15, criterios 1-10). La spec manda sobre este plan
salvo en lo que el plan fija porque la spec lo deja abierto (nombres de labels, aristas y propiedades; formato del fichero
intermedio; reglas de casado). Rama `consolidation`, sin commits: el usuario decide cuándo se commitea.

Las citas `fichero:línea` salen de tres agentes de investigación de solo lectura del 2026-09-28. Las marcadas `[V]` las he
releído yo; el resto son `[V]` del agente y se tratan como tales. Rutas relativas a `Samples/` salvo que se diga otra cosa.
`CORE` = `CodebaseAnalysis.Core`, `CT` = `CodebaseAnalysis.Core.Tests`, `WEB` = `CodebaseAnalysisWeb`, `WT` = `CodebaseAnalysisWeb.Tests`.

## Hechos del sistema en los que se apoya el plan

- `[V]` El pipeline es una lista fija de cinco pasos construida con `new` (`CORE/AnalysisPipelineFactory.cs:131-139`); no
  hay DI de pasos. Cada paso de primer nivel es `IStep<GraphStatusResult, GraphStatusResult>` salvo el primero.
- `[V]` La fase de la web sale del nombre del paso: `AnalysisEventMapper.PhaseOfStep` e `IsTopLevelStep`
  (`CORE/Progress/AnalysisEventMapper.cs:32-56`) y `PHASE_STEPS` en `WEB/ClientApp/src/state/runReducer.ts:18-24`.
- `[V]` En el cliente, `step.completed` de un paso de primer nivel cierra su fase y `step.started` la reabre
  (`runReducer.ts:299-321`): con dos pasos en `enrich` la fase parpadea a `succeeded` y `durationMs` se queda con el del
  segundo. Hay que corregirlo.
- `[V]` `BdpEnricherStep` va al fallback LLM si no hay `.sln` (`CORE/Steps/BdpEnricherStep.cs:84-91`) y se salta todo si
  `input.IsEnriched` (`:53-57`).
- Una excepción en un paso corta el pipeline (`Framework/Core/Execution/Pipeline.cs:272-279,406-410`); para avisar y seguir
  hay que capturar, `context.Report(..., Warning, ...)` y devolver `input` (patrón de `CORE/Steps/SemanticEnrichmentStep.cs:69-73`).
- `context.Report(source, level, message, channel)` es síncrono (`CORE/Progress/AnalysisProgress.cs:29-50`); niveles en
  `CORE/Progress/IAnalysisReporter.cs:4-10`.
- Configuración: `IConfiguration` desde `context.ServiceProvider` y *records* de opciones con `SectionName` y
  `FromConfiguration` (`CORE/Documents/DocumentOptions.cs:6-40`, uso en `CORE/Steps/SqlFacetExtractorStep.cs:14-17`).
  El fichero es `CORE/appsettings.analysis.json`; `AddAnalysisConfiguration` añade entorno, user secrets y línea de
  comandos (`CORE/Hosting/AnalysisConfiguration.cs:43-64`).
- Contenido a la salida: `None Update ... CopyToOutputDirectory` en `CORE/CodebaseAnalysis.Core.csproj:25-34`; llega a la
  web por la referencia de proyecto (comprobado en `WEB/bin/Debug/net10.0/`).
- SQLite con `Microsoft.Data.Sqlite` 10.0.1 (`CORE/CodebaseAnalysis.Core.csproj:11`). `GraphSchema.EnsureAsync`
  (`CORE/Support/GraphSchema.cs:12-118`) añade `file`, `line_start`, `line_end`, `project_id` y los índices únicos, y
  rellena `file`↔`file_path` y `line_*`↔`*_line`. `SqlFacetExtractorStep` lee la columna `file` y `line_start`: un nodo
  nuevo tiene que rellenar las dos parejas. El proyecto se resuelve con `GraphProject.ResolveAsync`
  (`CORE/Support/GraphProject.cs:13-22`); el nombre del proyecto de cbm no se guarda en el contexto.
- Esquema de cbm (DDL literal del agente, sobre `~/.cache/codebase-memory-mcp/C-GeminiCli-AITask-AITaskAgent.db`):
  `nodes(id, project, label, name, qualified_name, file_path, start_line, end_line, properties, UNIQUE(project, qualified_name))`,
  `edges(id, project, source_id, target_id, type, properties, url_path_gen, UNIQUE(source_id, target_id, type))`,
  `projects(name, indexed_at, root_path)`, `node_vectors`, `nodes_fts` *contentless*. Una sola arista por
  `(source, target, type)`: los puntos de llamada van en las propiedades.
- Reglas de QN de cbm para Java (datos de la instantánea del 28/09 y fuente de cbm `src/pipeline/fqn.c:100-123`,
  `internal/cbm/helpers.c:748-773`): prefijo `<project>.<ruta relativa sin extensión con / → .>`; tipo
  `<prefijo>.<Outer>[.<Inner>]`; método `<QN del tipo>.<nombre>`; constructor = método con el nombre de la clase;
  métodos de interfaz y enum además como `Function` en `<prefijo>.<nombre>`; sobrecargas fundidas en un nodo que suele
  llevar la línea de la última declaración (2637 de 2683 en la muestra).
- Propiedades de cbm en Method: `signature` `"(Hashtable connections,long id)"`, `param_types` solo no primitivos,
  `return_type`, `docstring`, `parent_class`. 60 nodos tienen `properties` que no es JSON válido (saltos de línea en
  `base_classes`): todo SQL con `json_*` tiene que ir protegido con `json_valid`.
- `enriched_by` ya es convención (lista JSON, con `origin` y `enriched_at`) en lo que escribe BdpGraphEnricher;
  `enrichment_runs` no existe en ninguna BD ni en el código.
- Consumidores de rutas: `graph_domain` lee aristas `HANDLES` con `http_method` y `route_path`
  (`CORE/Tools/GraphDomainTool.cs:88-98`); `SqlFacetExtractorStep` lee `Route` con `http_verb`, `route_path` y
  `auth_required` (`:78-85,212-220`) y Route sin `HANDLES` entrante (`:154-162`).
- Tests: xUnit 2 sin `Skip` dinámico ni `SkippableFact` en ningún proyecto; los repos de prueba se generan en código
  (`CT/SampleFiles.cs:17-20`, `CT/TestSupport.cs:31-53` `TempDirectory`).
- `RepositoryPathFilter` excluye `.git`, `bin`, `obj`, `node_modules`, `.codebase` (`CORE/Support/RepositoryPathFilter.cs:10-13`);
  no `target` ni `build`.
- Máquina: no hay JDK instalado (solo JRE 8). Para las pruebas hay un Temurin 17.0.20.1 descomprimido en el scratch de
  esta sesión y `servlet-api-2.5.jar` y `jsp-api-2.1.jar` de Maven Central en `scratch/provided/`. El spike `javac` de la
  sesión anterior se conserva en `…/a379b943-…/scratchpad/javaspike/src/JavacSpike.java` y es la base del analizador.
- No hay BD de cbm del repo objetivo en disco: la aceptación reindexa con cbm (157 s medidos) y limpia después.

## Decisiones que fija el plan

- **P1. Nombre y posición del paso.** `LanguageEnricherStep` (`StepName = "LanguageEnricherStep"`), entre
  `DetectAndIndexGraphStep` y `BdpEnricherStep`, fase `enrich`. Ejecuta, por este orden, el analizador Java y el plugin
  Struts/JSP. Deja en el contexto `AnalysisContextKeys.DetectedLanguages`.
- **P2. Java sin C#.** `BdpEnricherStep` consulta `DetectedLanguages`: si hay Java y no hay C#, informa y devuelve
  `input` sin CLI ni fallback LLM. Con C#, o sin ninguno de los dos, su camino no cambia. Sale de la spec, §Selección
  («`.cs` mantiene el camino actual … si no hay ninguno, se mantiene el fallback») y del criterio 8 («termina con el grafo
  de cbm»).
- **P3. Condición de ejecución.** Cada enriquecedor se salta si `enrichment_runs` tiene una fila `ok` con el mismo
  enriquecedor, versión, huella de la indexación y huella de configuración. Huella de la indexación:
  `projects.name|projects.indexed_at` de la copia. Huella de configuración: SHA-256 de las opciones que cambian el
  resultado (classpath *provided*, raíces de test, codificación).
- **P4. Carpeta de trabajo.** `<directorio de la BD>/enrichment/java/` (petición, resultado y log). Con la BD por defecto
  cae en `.codebase/`, que ya se excluye del grafo; en la aceptación la BD va al scratch y no se escribe en `C:\BBVA`.
- **P5. Formato intermedio (D11).** Petición en texto `clave=valor` UTF-8 (listas con la clave repetida); resultado en
  JSON Lines UTF-8, un registro por línea con `"k"`. Progreso por stdout con líneas `@@progress …`. Contrato completo
  abajo; versión `"schema":1` en el registro `summary`.
- **P6. Codificación.** Por fichero: UTF-8 si los bytes son UTF-8 válido (sin BOM o con él), y si no la codificación por
  defecto. La codificación por defecto es `JavaAnalyzer:DefaultEncoding`; si está vacía, la
  `project.build.sourceEncoding` del `pom.xml` raíz; si no hay, UTF-8. La misma regla sirve para las JSP cuando no
  declaran `pageEncoding`/`charset`. En Java se implementa con un `SimpleJavaFileObject` propio que devuelve el texto ya
  decodificado. Los ficheros que `javac` cargue por `-sourcepath` en modo lotes usan la codificación por defecto (solo
  aportan símbolos).
- **P7. Casado de declaraciones.** Tipo: `Class`/`Interface`/`Enum` por QN. Método: `Method` por QN; si no, `Function`
  en `<prefijo>.<nombre>` (rareza de cbm en interfaces y enums). Declaraciones dentro de clases anónimas o locales no se
  emiten (fuera de alcance); sus llamadas se atribuyen al método con nombre que las contiene.
- **P8. Sobrecargas (D7).** En un grupo de declaraciones con el mismo QN conserva el QN la que contiene el `start_line`
  del nodo de cbm; si ninguna, la de inicio más cercano. Las demás se crean con QN `<QN>(<tipos simples borrados>)`,
  p. ej. `…consultar(Hashtable,long)`, y si dos colisionan, con tipos completos. Llevan `overload_of` = QN de cbm, y
  una arista `DEFINES_METHOD` desde su tipo. El nodo conservado corrige `start_line`/`end_line` a su declaración.
- **P9. Declaraciones sin nodo en cbm.** Se crean como nodos nuevos (con `DEFINES_METHOD` desde su tipo) si su QN está
  libre, para que las llamadas no se pierdan; si choca con otro label, cuentan como no casadas.
- **P10. Aristas.** Se borran todas las `CALLS` cuyo origen es un nodo de un fichero analizado (cualquier label) y las
  `INHERITS`/`IMPLEMENTS` que salen de sus tipos. Se insertan: `CALLS` método→método (y constructor) de proyecto, con
  `{"enriched_by":["JavaAnalyzer"],"lines":[…],"count":n,"kinds":["call","new"]}`; las llamadas fuera de un método
  (inicializadores) salen del tipo con `"context":"initializer"`. `INHERITS` clase→superclase e interfaz→interfaz
  extendida, `IMPLEMENTS` clase→interfaz, solo con destino de proyecto; los destinos del JDK o de jars quedan en
  propiedades del tipo (`super_fqn`, `interfaces_fqn`). Llamada a un método de interfaz → al método de la interfaz.
- **P11. Propiedades de método** (json_patch sobre el nodo): `signature` `"BeanAccion consultar(Hashtable connections, long id)"`,
  `param_types` FQN borrados con primitivos, `return_type_fqn`, `accessibility` (`public|protected|private|package`),
  `is_static`, `is_abstract`, `is_constructor`, `annotations` (nombres simples), `docstring` (Javadoc decodificado),
  `cyclomatic` (1 + if, for, foreach, while, do, case no default, catch, ?:, &&, ||), `enriched_by:["JavaAnalyzer"]`.
  De tipo: `fqn`, `kind`, `accessibility`, `is_abstract`, `annotations`, `docstring`, `super_fqn`, `interfaces_fqn`.
  Si `properties` no es JSON válido se envuelve: `json_patch(json_object('cbm_raw_properties', properties), @p)`.
- **P12. Marca y limpieza (D8).** Nodos creados: `origin:"enricher"`, `enriched_by:[<enriquecedor>]`,
  `enriched_at`. Aristas escritas: `enriched_by:[<enriquecedor>]`. Al empezar, cada enriquecedor borra sus nodos creados
  (con sus aristas) y sus aristas; las propiedades parcheadas se reescriben. Todo en una transacción. Toda consulta
  sobre `enriched_by` usa `json_valid(properties) AND EXISTS (SELECT 1 FROM json_each(properties,'$.enriched_by') WHERE value=@e)`.
- **P13. Retirada de tests (D15).** Raíces: `testSourceDirectory` del `pom.xml` raíz, `src/test/java` si existe y
  `JavaAnalyzer:TestRoots`. Se borran los nodos cuyo `file_path` es la raíz o cuelga de ella, sus aristas en los dos
  sentidos y sus `node_vectors`. `nodes_fts` no se toca (es *contentless*; la app no lo consulta, `graph_search` usa `LIKE`).
- **P14. Plugin Struts/JSP.** Enriquecedor `StrutsJspPlugin`, señal: algún `struts-config*.xml`. Nombres:
  - `Route` por acción: QN `__route__ANY__<path>` (sin prefijo, como cbm y BdpGraphEnricher), `name` = `path`,
    `file`/`file_path` = el `struts-config`, línea de `<action>`; propiedades `framework:"struts"`, `route_path`,
    `http_verb:"ANY"`, `http_method:"ANY"`, `action_type`, `form_bean`, `form_type`, `scope`, `input`, `parameter`,
    `config_file`.
  - `HANDLES` clase de acción → `Route`, con `http_method` y `route_path` (lo que lee `graph_domain`). La clase se busca
    por `label='Class'` y `file_path` terminado en `/<paquete>/<Simple>.java` y `name=<Simple>`.
  - `Jsp`: QN `<project>.<ruta sin extensión con puntos>.__jsp__`, `name` = ruta relativa, líneas 1..n; propiedades
    `scriptlet_count`, `scriptlet_lines`, `expression_count`, `declaration_count`, `page_imports`, `taglibs`, `has_sql`,
    `session_access`, `suspicious`, `suspicious_reasons`, `encoding`.
  - `TilesDefinition`: QN `<project>.__tiles__.<name>`, propiedades `template`, `extends`, `puts`, `config_file`.
  - Aristas `FORWARDS_TO` (Route → Jsp | TilesDefinition; `forward_name`, `redirect`; el `input` sale como forward
    `input`), `SUBMITS_TO` (Jsp → Route desde `<html:form action>`), `INCLUDES` (Jsp → Jsp desde `<%@ include file %>`,
    `<jsp:include page>` y `<tiles:insert page>` con `.jsp`), `RENDERS` (TilesDefinition → Jsp, `role`
    `template`|`put:<nombre>`), `EXTENDS_DEFINITION` (TilesDefinition → TilesDefinition).
  - Rutas de contexto (`/pages/x.jsp`) contra las raíces web: carpetas que contienen `WEB-INF`. XML con
    `DtdProcessing.Ignore` y `XmlResolver = null` (sin red).
  - Sospechosa: algún `page import` que casa con `StrutsJsp:SuspiciousImportPatterns` (por defecto `java.sql`,
    `javax.sql`, `(?i)(^|\.)dao(\.|$)|DAO\b`, `(?i)\.(negocio|business|service)\.`) o SQL en scriptlets
    (`SELECT … FROM`, `INSERT INTO`, `UPDATE … SET`, `DELETE FROM`, sin distinguir mayúsculas).
  - Quedan fuera: forwards globales como aristas (se cuentan en la pasada), `web.xml`, DWR, Java de los scriptlets.
- **P15. Tests con JDK.** Atributo `JdkFactAttribute : FactAttribute` que pone `Skip` con el motivo si no encuentra JDK
  (`CODEBASE_ANALYSIS_TEST_JDK` o `JavaAnalyzer:JdkPath`); así xUnit 2 los muestra como omitidos, sin paquetes nuevos.
- **P16. Web.** La fase `enrich` agrupa los dos pasos: se abre con el primer `step.started` y se cierra con el
  `step.completed` de `BdpEnricherStep` (último paso de la fase); la duración se calcula de `startedAt` a `finishedAt`.

## Contrato del fichero intermedio (P5)

Petición (`request.txt`): `root=`, `out=`, `file=<rel>` (repetida), `classpath=<abs>` (repetida, jars ya expandidos por
.NET), `defaultEncoding=`, `batchSize=`, `maxParallelBatches=`, `sourceRoot=<abs>` (solo en lotes; la calcula Java si falta).

Resultado (`java-analysis.jsonl`), claves cortas y rutas relativas con `/`:

```
{"k":"file","path":"src/a/B.java","encoding":"ISO-8859-1","errors":0}
{"k":"type","path":…,"chain":["B","Inner"],"fqn":"a.B.Inner","kind":"class|interface|enum|annotation","line":10,"end":40,
 "access":"public","abstract":false,"annotations":["Deprecated"],"doc":"…",
 "super":{"fqn":"a.Base","origin":"project|jdk|jar|unresolved"},"interfaces":[{"fqn":…,"origin":…}]}
{"k":"method","path":…,"chain":["B"],"ownerFqn":"a.B","name":"consultar","ctor":false,"line":27,"end":27,
 "key":"a.B#consultar(java.util.Hashtable,long)","params":["java.util.Hashtable","long"],"paramSimple":["Hashtable","long"],
 "paramNames":["connections","id"],"ret":"a.BeanAccion","retSimple":"BeanAccion","access":"public","static":false,
 "abstract":true,"annotations":[],"doc":null,"cyclomatic":1}
{"k":"call","path":…,"line":889,"from":"<key del método o null>","fromType":"a.B","to":"<key del destino>",
 "origin":"project|jdk|jar|unresolved","kind":"call|new","reason":null|"null-element|error-owner|<kind>"}
{"k":"diag","path":…,"line":12,"msg":"package javax.servlet does not exist"}
{"k":"summary","schema":1,"analyzerVersion":"1","javaVersion":"17.0.20.1","files":8420,"parseMs":…,"analyzeMs":…,
 "scanMs":…,"heapPeakMb":…,"calls":{"project":…,"jdk":…,"jar":…,"unresolved":…},"unresolvedByReason":{…},
 "missingPackages":["javax.servlet",…],"diagnostics":{"error":n,"warning":n}}
```

`key` = `ownerFqn#nombre(FQN borrados separados por coma)`, calculado con `Types.erasure` sobre el `ExecutableElement`
tanto en la declaración como en el destino de la llamada, de modo que casan por igualdad. `origin` se clasifica como en el
spike (`JavacSpike.java:139-160`): fuente del proyecto → `project`, `ct.sym`/`jrt:` → `jdk`, jar → `jar`. Las llamadas
`this(...)`/`super(...)` son `kind:"call"` hacia el constructor. `@@progress phase=<parse|analyze|scan> done=<n> total=<n>`
por stdout; cualquier otra línea es log.

## Tareas

Orden de ejecución: T1 ‖ T2, después T3, T4, T5 y T6. Cada tarea la hace un agente; los tests .NET se ejecutan con
`-p:OutDir=<scratch>/<tarea>` por si el backend del usuario tiene las DLL bloqueadas.

### T1. Base .NET: opciones, detección, raíces de test y `enrichment_runs` (agente A)

Ficheros nuevos en `CORE/Enrichment/`:
- `JavaAnalyzerOptions.cs`: *record* `SectionName = "JavaAnalyzer"` con `Enabled` (true), `JdkPath` (""), `JvmOptions`
  (`["-Xmx8g","-XX:+UseG1GC"]`), `BatchSize` (0), `MaxParallelBatches` (1), `ProvidedClasspath` ([]), `TestRoots` ([]),
  `DefaultEncoding` (""), `TimeoutSeconds` (900); `FromConfiguration` como `DocumentOptions`.
- `StrutsJspOptions.cs`: `SectionName = "StrutsJsp"`, `Enabled` (true), `SuspiciousImportPatterns` (P14).
- `RepositoryScan.cs`: recorrido único del repo con `RepositoryPathFilter` más `target`, `build`, `.gradle`, `.idea`,
  `out` y las raíces de test; devuelve `.java`, `.cs`, `.jar`, `struts-config*.xml`, XML de Tiles (`<tiles-definitions`),
  `.jsp`/`.jspf`, raíces web (carpetas con `WEB-INF`) y `DetectedLanguages` (`Java`, `CSharp`).
- `TestRoots.cs`: resolución de P13 a partir del `pom.xml` raíz (`build/testSourceDirectory`, normalizado sin `./`).
- `SourceEncoding.cs`: regla P6 (`project.build.sourceEncoding`, validación UTF-8 estricta con `UTF8Encoding(false, true)`).
- `EnrichmentRuns.cs`: DDL `CREATE TABLE IF NOT EXISTS enrichment_runs(id INTEGER PRIMARY KEY AUTOINCREMENT, enricher TEXT NOT NULL,
  enricher_version TEXT NOT NULL, tool_version TEXT, index_fingerprint TEXT NOT NULL, config_fingerprint TEXT NOT NULL,
  started_at TEXT NOT NULL, duration_ms INTEGER NOT NULL, status TEXT NOT NULL, files_analyzed INTEGER, files_unmatched INTEGER,
  nodes_created INTEGER, nodes_updated INTEGER, nodes_removed INTEGER, cbm_edges_replaced INTEGER, edges_created INTEGER,
  unresolved TEXT, diagnostics TEXT, details TEXT, message TEXT)`; `IndexFingerprintAsync`, `HasSuccessfulRunAsync`,
  `InsertAsync`. `status` ∈ `ok|failed|skipped`.
- `EnrichmentMarks.cs`: helpers SQL de P12 (predicado `enriched_by`, borrado de salida previa, borrado de nodos con aristas
  y vectores).
- `AnalysisContextKeys.DetectedLanguages` en `CORE/Progress/AnalysisContextKeys.cs`.
- `CORE/appsettings.analysis.json`: secciones `JavaAnalyzer` y `StrutsJsp` con los valores por defecto (sin jars ni JDK).

Tests (`CT/Enrichment/…`): detección con y sin `.java`/`.cs` (criterio 1), exclusiones y raíces de test del `pom.xml`,
regla de codificación (Latin-1, UTF-8, UTF-8 con BOM), `enrichment_runs` (crea, inserta, `HasSuccessfulRunAsync` por
huellas), borrado de salida previa con un nodo de JSON inválido presente. Fuente: `CT/TestSupport.cs:31-53`,
`CORE/Support/GraphSchema.cs:12`.

Verificación: `dotnet test CT -p:OutDir=<scratch>/t1` en verde.

### T2. `JavaAnalyzer.java` (agente B, en paralelo con T1; no toca .NET)

Fichero `CORE/Enrichment/Java/JavaAnalyzer.java`, un solo fichero, Java 17, sin dependencias, partiendo de
`JavacSpike.java`:
- Lee `request.txt` (P5); ficheros con `SimpleJavaFileObject` que decodifica según P6; opciones
  `--release 8 -proc:none -implicit:none -Xmaxerrs 1000000 -nowarn -classpath …` (y `-sourcepath` en lotes).
- `parse()`, `analyze()`, recorrido con `TreePathScanner`: emite `file`, `type`, `method`, `call` (invocaciones y `new`,
  sin constantes de enum; `this`/`super` como llamada al constructor), `diag` (errores) y `summary` según el contrato.
  `chain` sigue el anidamiento de tipos con nombre; tipos y métodos dentro de clases anónimas o locales no se emiten.
- Lotes: `batchSize > 0` parte la lista; `maxParallelBatches > 1` los lanza en un `ExecutorService`; `sourceRoot` por
  `package` si no viene.
- `missingPackages` sale de los diagnósticos `package X does not exist`; `unresolvedByReason` de `classifyExec`.
- Progreso `@@progress` cada 500 ficheros en el recorrido; código de salida 0 si terminó (aunque haya diagnósticos), 1 si
  falló la petición, 2 si `javac` no pudo arrancar.
- Se ejecuta como `java <JvmOptions> --add-exports jdk.compiler/com.sun.tools.javac.code=ALL-UNNAMED JavaAnalyzer.java --request <ruta>`
  (*source launcher*, D13); el agente confirma qué `--add-exports` exige.

Verificación del agente (en el scratch, sin escribir en `C:\BBVA`):
1. Repo de prueba pequeño en el scratch (el mismo que describe T3) → registros esperados a mano.
2. Repo objetivo `C:\BBVA\Fuentes\cli-bbva-clan\TEDT\Construccion\delta\Online`, `src/` completo, con los jars de
   `WebContent/WEB-INF/lib` y `scratch/provided/*.jar`: tiempo, memoria, % de llamadas resueltas (esperado ≈ 47 fallos de
   1.066.814, criterio 2), `missingPackages`, y casado de QN contra `…/a379b943-…/scratchpad/cbmjava/nodes.pkl` con un script
   Python como `javaspike/cbm_match.py` (esperado 100 % tipos, 99,96 % métodos). Recuento de declaraciones y QN sobrecargados
   en `src/` (la spec da 9626 y 4105 con `test/` incluido).
3. Comprobar en la salida `Accion.java:889` → `geo.DAO.acciones.DAOAcciones#consultar(java.util.Hashtable,long)` y
   `DAOExtraccionEventosOracleImpl.java:2100` → `…Usuario#getCodigoUsuario()` (criterio 3).

### T3. Lector del resultado y escritura en el grafo (agente A, tras T1)

Ficheros en `CORE/Enrichment/Java/`:
- `JavaAnalysisRecords.cs`: *records* del contrato y `JavaAnalysisReader` (JSON Lines con `System.Text.Json`, en streaming).
- `JavaGraphWriter.cs`: aplica P7-P13 sobre la copia en una transacción: borra salida previa (P12), retira tests (P13),
  mapea `fqn`→QN de tipo y `key`→id de nodo, casa y crea (P7-P9), parchea propiedades (P11), sustituye aristas (P10) y
  devuelve un `EnrichmentOutcome` con los recuentos de `enrichment_runs`. Los nodos nuevos rellenan `project`,
  `project_id=1`, `file_path`/`file`, `start_line`/`line_start`, `end_line`/`line_end`.
- Nunca abre `~/.cache/codebase-memory-mcp` (comprobación de ruta y test).

Tests (`CT/Enrichment/JavaGraphWriterTests.cs`) sin JDK, con un grafo sintético al estilo cbm (`GraphSchema.EnsureAsync`
+ inserts con QN de cbm, un `CALLS` equivocado, una sobrecarga fundida, un nodo bajo `test/`, un nodo con JSON inválido,
un `Function` de interfaz) y un resultado sintético:
sobrecarga conserva QN por línea y crea la otra con sufijo y `overload_of`; `CALLS` de cbm sustituidas por las resueltas;
`INHERITS`/`IMPLEMENTS`; llamada a interfaz apunta al método de interfaz; tests retirados; segunda pasada deja los mismos
recuentos (criterio 6); propiedades del JSON inválido envueltas; fila de `enrichment_runs` completa (criterio 7).

Verificación: `dotnet test CT -p:OutDir=<scratch>/t3`.

### T4. Plugin Struts/JSP (agente C)

Ficheros en `CORE/Enrichment/Web/`: `StrutsConfigParser.cs`, `TilesDefinitionsParser.cs`, `JspScanner.cs` (directivas,
scriptlets `<% %>`, expresiones `<%= %>`, declaraciones `<%! %>`, comentarios `<%-- --%>` descartados, `html:form`,
includes, taglibs, SQL, sesión), `StrutsJspWriter.cs` (P12, P14, fila en `enrichment_runs`).

Tests (`CT/Enrichment/StrutsJspTests.cs`) con ficheros generados: acción con forwards e `input`, formulario, include,
definición de Tiles con `extends` y `put`, JSP con scriptlet con SQL e import de DAO → sospechosa; JSP limpia → no
sospechosa; idempotencia; `HANDLES` desde la clase de acción del grafo sintético.

### T5. Proceso, paso, pipeline y web (agente D)

- `CORE/Enrichment/Java/JavaAnalyzerRunner.cs`: resuelve `java(.exe)` bajo `JdkPath/bin`, lee `JdkPath/release`
  (`JAVA_VERSION`, exige ≥ 17), compone el classpath (jars del repo + `ProvidedClasspath`, carpetas expandidas), escribe la
  petición, lanza con `ArgumentList`, lee stdout por eventos (progreso a `context.Report`, canal `indexer` para el log
  crudo), aplica `TimeoutSeconds` con `ProcessCancellation.WaitForExitOrKillAsync` (`CORE/Support/ProcessCancellation.cs:17`).
- `CORE/Steps/LanguageEnricherStep.cs`: P1-P3; mensajes de lenguajes detectados, JDK encontrado o no, progreso, % de
  llamadas resueltas, paquetes que faltan, ficheros con errores. Cualquier fallo → aviso, fila `failed`/`skipped` y
  devuelve `input` (criterio 8). Devuelve `GraphStatusResult` con recuentos nuevos.
- `CORE/CodebaseAnalysis.Core.csproj`: `None Include="Enrichment\Java\JavaAnalyzer.java"` con `PreserveNewest`.
- `CORE/AnalysisPipelineFactory.cs:131-139`: insertar el paso tras `detectStep`.
- `CORE/Steps/BdpEnricherStep.cs`: P2.
- `CORE/Progress/AnalysisEventMapper.cs:32-56`: `LanguageEnricherStep.StepName => Enrich` y en `IsTopLevelStep`.
- Cliente: `runReducer.ts` (`PHASE_STEPS`, P16), `test/fixtures.ts`, `runReducer.test.ts`, `EventLog.test.tsx` si hace falta.
- `WT/FakeAnalysisWorkflow.cs:26-38,124-137`: emitir el paso nuevo.
- Tests: `CT/AnalysisEventMapperTests.cs` (`InlineData` nuevo), `CT/Enrichment/LanguageEnricherStepTests.cs`
  (sin JDK → aviso y grafo intacto; sin `.java` → no hace nada; con JDK `[JdkFact]` → repo de prueba de T3 extremo a
  extremo sobre grafo sintético, criterio 10), test de P2 en `BdpEnricherStep`, Vitest de P16.
- Documentación: `CORE`/`WEB` README (configuración `JavaAnalyzer` y `StrutsJsp`, fase `enrich`), `WEB/README.md:5`.

Verificación: `dotnet test` de CT y WT, `npm test` en `WEB/ClientApp`, `npx tsc --noEmit`.

### T6. Aceptación sobre el repo objetivo y en la web (agente E)

Sin escribir en `C:\BBVA`: cbm con `persistence` por defecto (solo `~/.cache`), copia de la BD al scratch, arnés de consola en
el scratch que referencia Core y ejecuta `LanguageEnricherStep` con `DatabasePath` en el scratch, `JdkPath` y
`ProvidedClasspath` del scratch. Comprobaciones con Python en solo lectura:
criterios 2 (≥ 99,9 % y solo `CALLS` resueltas en ficheros analizados), 3, 4 (nodos de sobrecarga = declaraciones − QN
en `src/`, comparado con los 5521 de la spec), 5 (JSP y acciones con sus aristas, sospechosas), 6 (dos pasadas, mismos
recuentos; SHA-256 de la BD de `~/.cache` igual antes y después), 7 (fila completa; ningún nodo bajo `test/`), 9 (< 5 min).
Criterio 8 y mensajes en la web: segunda instancia en `127.0.0.1:5181` (receta de
`docs/project_notes/asistente-chat-y-enriquecimiento-estado.md:72-76`) sobre una copia del repo de prueba en el scratch,
con y sin `JdkPath`; se leen los eventos de la API y se cancela tras la fase `enrich`. Al terminar: `delete_project` de cbm
para el repo objetivo y parada por puerto.

## Riesgos

- `--add-exports` y el *source launcher*: si el launcher no pasa las opciones al compilador en memoria, habría que usar
  reflexión o `--add-opens`; T2 lo comprueba el primero.
- El recuento de sobrecargas de la spec incluye `test/`; con D15 el número de `src/` será menor. Se informa de los dos.
- `nodes_fts` queda con filas de nodos retirados (P13); no afecta a la app, sí a búsquedas FTS directas sobre la copia.
- La memoria de `-Xmx8g` por defecto en máquinas con poca RAM; se ajusta por configuración (D5).
