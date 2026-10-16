# DotNetEnricher

Enriquecedor Roslyn del grafo SQLite de `codebase-memory-mcp` (cbm) para soluciones .NET. Carga la solución con `MSBuildWorkspace`, compila sus proyectos C# y escribe en la copia del grafo del análisis lo que cbm no sabe sacar del código: registros de DI, rutas HTTP, flujos, contratos, complejidad ciclomática, métricas, código muerto, acoplamiento y proyectos. Lo lanza `DotNetEnricherStep` (`CodebaseAnalysis.Core`), en la fase `enrich`, después del enriquecimiento nativo por lenguaje y antes de las facetas.

Es una copia genérica y revisada del enriquecedor C# de BdpGraphEnricher: se ha quitado todo lo propio de aquella aplicación (su proyecto `.Bdp`, el servidor MCP, la vectorización, la consolidación de ADR y el enlace con la documentación de `specs/`) y se han corregido los fallos que se describen más abajo.

## Por qué es un proceso aparte

La app lo lanza como proceso, igual que al analizador Java, y no lo carga como librería. Hay tres motivos:

1. Un MSBuild colgado en la carga de la solución no se puede cortar desde dentro del host web: el timeout del paso solo deja de esperar. Con un proceso aparte, `DotNetEnricher:TimeoutSeconds` mata el proceso y todo su árbol.
2. `MSBuildLocator` tiene que registrarse antes de que se cargue ningún tipo de MSBuild, y en un proceso que ya ha cargado otras cosas (el backend web, los tests) no se controla qué se ha cargado antes.
3. La solución compilada entera (compilaciones y modelos semánticos) se quedaría en la memoria del backend. Al salir el proceso se libera toda.

## Uso

```
DotNetEnricher enrich --solution <.sln|.slnx> --db <copia del grafo> [--repo-root <raíz>] [--project <proyecto de cbm>]
                      [--config-fingerprint <huella>] [--verbose]
                      [--restore auto|always|never] [--restore-timeout <s>] [--cache-dir <carpeta>]
```

- `--repo-root` es la raíz del repositorio analizado, la que usó cbm para el `file_path` y los qualified_names. La app la pasa siempre. Sin ella se toma la carpeta con `.git` más cercana por encima de la solución, o la de la solución si no hay ninguna. El original usaba siempre la carpeta de la solución, y con la solución en `src/` los QN no casaban.
- `--project` es el proyecto de cbm. Por defecto se toma el mayoritario en la tabla `nodes`, con la misma lógica que `GraphProject.ResolveAsync` de la app. La app ya no lo pasa: antes lo deducía del nombre de la carpeta de la solución, no casaba con el de cbm, fallaban todos los patches y se creaba un segundo proyecto en el grafo.
- `--verbose` muestra el progreso de cada enriquecedor. Los avisos de carga de la solución (`WorkspaceFailed`) salen siempre, porque un proyecto que no carga sus documentos es la causa habitual de un enriquecimiento vacío.

- `--restore` (`auto` por defecto; también `siempre`/`nunca`), `--restore-timeout` (300 s) y `--cache-dir` controlan la restauración de paquetes NuGet (sección siguiente). La app los pasa desde `DotNetEnricher:Restore`, `DotNetEnricher:RestoreTimeoutSeconds` y `<carpeta de la BD>/enrichment/dotnet-restore`.

La BD tiene que ser la copia del análisis: el CLI se niega a escribir en la caché de cbm (`~/.cache/codebase-memory-mcp`).

### Restauración de paquetes (sin escribir en el repositorio)

Sin `project.assets.json` Roslyn no resuelve los paquetes NuGet (p. ej. `SqlConnection`) y se pierden las reglas que dependen de sus tipos (`undisposed-resource`). Antes de cargar la solución, el CLI ejecuta `dotnet restore` (lo hace el CLI y no el paso de la app porque ya corre fuera de proceso: su timeout mata el árbol de `dotnet restore` y tiene el MSBuild localizado):

- `auto`: solo si algún `.csproj` no tiene `obj/project.assets.json`; si los tienen todos, se usan sin restaurar. `always` restaura siempre; `never` nunca.
- **No escribe en el repositorio analizado.** La salida de NuGet (`MSBuildProjectExtensionsPath`) y también `obj/` y `bin/` (`BaseIntermediateOutputPath`, `BaseOutputPath`: la carga con `MSBuildWorkspace` escribe ahí `AssemblyInfo.cs` y `GlobalUsings.g.cs`) se redirigen a la caché (`pkg/`, `obj/`, `bin/` bajo `--cache-dir`, una carpeta por proyecto) con dos ficheros `.props` que se importan al principio y al final de `Microsoft.Common.props` (`CustomBefore/AfterMicrosoftCommonProps`), y las mismas propiedades se pasan a `MSBuildWorkspace.Create`. Límite conocido: un `BaseIntermediateOutputPath` propio del repositorio solo se pisa en la importación final.
- Sin acceso a los feeds, o si se agota `--restore-timeout`, el CLI deja un aviso (stderr) y sigue sin los paquetes que falten; no aborta. Si la restauración dejó resultados parciales y el repositorio no tiene assets propios, se usan.

### Códigos de salida

| Código | Significado |
|---|---|
| 0 | Correcto. |
| 2 | Correcto con avisos: algún enriquecedor no encontró en el grafo más del 5 % de los nodos que buscaba. |
| 1 | Argumentos o entradas que no sirven: no existe la solución o la BD, la solución no está dentro de la raíz o el grafo no tiene nodos. |
| 3 | La solución no se pudo cargar o no tiene ningún proyecto C# con código. |
| 4 | Fallo durante el enriquecimiento. La transacción se deshace y el grafo queda como estaba. |

El original devolvía 0 aunque faltaran la solución o la BD. La app trata 0 y 2 como éxito; con cualquier otro código, o si se agota el tiempo, avisa y sigue con el enriquecimiento por LLM (`SemanticEnrichmentStep`), salvo con `DotNetEnricher:Required`.

## Escritura en el grafo

Toda la pasada va por una sola conexión y una sola transacción, con el mismo patrón que el enriquecedor Java de la app:

- La conexión se abre con `EnrichmentMarks.OpenForBulkWriteAsync` (caché de páginas grande, sin fsync). El original abría una conexión por operación y confirmaba cada una por separado, así que un fallo a mitad dejaba el grafo a medio enriquecer.
- Al empezar se borra la salida de la pasada anterior de cada enriquecedor: los nodos que creó (`origin = enricher` y su nombre en `enriched_by`) y las aristas con su marca. Repetir la pasada no duplica nada.
- Al terminar se registra la pasada en `enrichment_runs` (`enricher = DotNetEnricher`, huella de la indexación de cbm, huella de configuración, recuentos, avisos de carga y detalle por enriquecedor). `DotNetEnricherStep` se salta la pasada si ya hay una correcta sobre la misma indexación y configuración.
- Un parche suma su marca a `enriched_by` en lugar de sustituir la lista, y no cambia el `origin` del nodo. Antes un parche marcaba como `origin = enricher` interfaces de cbm, que la limpieza por marca habría borrado como propias.
- Procedencia uniforme (`ProvenanceStamp`): `GraphRepository` sella todo nodo parcheado o creado y toda arista escrita con `enriched_by` (el enriquecedor que escribe, que fija la pasada antes de cada uno), `enriched_at` (el inicio de la pasada, salvo que el enriquecedor ponga el suyo) y `enricher_version`; las aristas, además, con `origin = enricher` si no traen otro (las exactas llevan `compiler`). Antes los métodos no llevaban ninguna marca (0 de 431 en `Framework`; ahora 431). No pasan por ahí las actualizaciones con SQL directo: `language` por extensión (`ExtensionPointEnricher`) y los `json_remove` de limpieza.

Antes de enriquecer, la pasada retira del grafo lo que cbm indexó de `bin/`, `obj/` y los ficheros generados (`*.g.cs`, `*.Designer.cs`, `*.AssemblyInfo.cs`, `TemporaryGeneratedFile_*`…; `GeneratedArtifacts`, también enlazado): sin ello los totales, métricas, ciclos y comunidades cuentan salida de compilación.

El nombre y la versión de la pasada (`DotNetEnricherIdentity`), las marcas (`EnrichmentMarks`), el registro (`EnrichmentRuns`) y la resolución del proyecto (`GraphProject`) son ficheros de `CodebaseAnalysis.Core` enlazados en este proyecto. No se referencia Core porque traería al proceso el Framework y el cliente del LLM, y esos ficheros solo dependen de `Microsoft.Data.Sqlite`.

## Enriquecedores

Se ejecutan por este orden:

| Enriquecedor | Qué escribe |
|---|---|
| `SemanticMethodEnricher` | `is_async`, `is_abstract`, `is_virtual`, `is_override`, `is_static`, `accessibility` y `return_type_fqn` en los métodos; nodos `Interface` que cbm no creó; aristas `IMPLEMENTS` a todas las interfaces del tipo (las heredadas llevan `is_inherited`) y `OVERRIDES` al método base. |
| `AttributeEnricher` | `attributes` (nombre, argumentos) en tipos y métodos, por ejemplo `[Obsolete]`. |
| `CallEnricher` | Aristas `CALLS` exactas desde las referencias de Roslyn en lugar de las de cbm (sección «Llamadas y excepciones»). Va antes de `ProjectEnricher` y `GraphAnalyticsEnricher`, que las leen. |
| `DiEnricher` | Nodos `DiRegistration` (`_di_.{servicio}->{implementación}`) con `consumer_count` (quién consume el servicio, por referencias de Roslyn), aristas `DI_RESOLVES` y `di_lifetime`/`di_registered_as` en la implementación. |
| `RouteEnricher` | Nodos `Route` (`__route__{VERBO}__{ruta}`) de controllers y Minimal APIs, con `MapGroup` anidados, `auth_required` (explícito `false` si no se declara nada y no hay protección global) y contrato; aristas `HANDLES` desde el método que atiende la ruta. Una lambda como handler deja la ruta huérfana. |
| `FieldEnricher` | Nodos `Field` con `field_name`, `type_fqn`, `accessibility`, `is_static`, `is_final` y aristas `DEFINES`, `READS` y `WRITES` exactas, con la forma del analizador Java (las consume `unused_fields`). |
| `ExtensionPointEnricher` | `language` por extensión; `is_extension_point`/`is_extension_impl` y aristas `EXTENDS_POINT` y `REGISTERED_VIA`. |
| `IssueEnricher` | `detected_issues` e `issue_rules` por método: diagnósticos del compilador y reglas de sintaxis/operaciones (`async void`, catch que traga, `.Result`/`.Wait()`, `IDisposable` sin liberar, captive dependency). |
| `ThrowsEnricher` | `throws`, `rethrows`, `rethrow_loses_stack`, `swallows_exceptions` y `swallowed_catches` por método, y aristas `THROWS` exactas (sección «Llamadas y excepciones»). |
| `ProjectEnricher` | Nodos `CsProject` (`_proj_.{nombre}`) con Ca/Ce/`instability`, rol, `project_references` y `packages` (lo que declara el `.csproj`), aristas `PROJECT_DEPENDS_ON` y `BELONGS_TO_PROJECT`. |
| `TypeRoleEnricher` | `type_role` (`contract`: interfaz o clase abstracta pública; `implementation`; `infrastructure`: estáticas, puntos de entrada `Main`, derivadas de clases de `Microsoft.*` y abstracciones no públicas; `model`: enums, excepciones, atributos y tipos solo de datos), `is_public` y `cs_project` en cada tipo; en cada `CsProject`, `abstractness`, `distance` y `type_roles`. Las reglas son estructurales y están en `TypeRoleEnricher.Classify`. |
| `CommentEnricher` | `docstring` completo (XML doc o comentario anterior), `todo_markers`, `commented_code` y `encoding` del fichero, con la trivia de Roslyn. |
| `ConstantEnricher` | `hardcoded_secrets` (`[{kind, name, line, preview_masked}]`, siempre con el valor enmascarado) y `reflection_targets` (`string[]` con el nombre de tipo de `Type.GetType`, `Assembly.GetType`/`CreateInstance` y `Activator.CreateInstance(ensamblado, tipo)`, con `*` por cada tramo no constante), resueltos con `IOperation.ConstantValue`. `ReflectionTargetSuppressor` mantiene vivas las clases que un patrón puede nombrar. |
| `SensitiveApiEnricher` | `sensitive_apis` (`[{category, api, line}]`) y `sensitive_categories` por método, resueltas por símbolo (sección «APIs sensibles, concurrencia y huellas»). |
| `ConcurrencyEnricher` | `concurrency_markers` (`[{kind, line, api?}]`) y `concurrency_kinds` por método; `is_static_mutable` e `is_thread_safe_type` en los nodos `Field`. |
| `ConfigOptionsEnricher` | Clases `*Options`/`*Config`/`*Settings`/`*Configuration`, su sección y aristas `OPTIONS_CONFIGURES`. |
| `FlowEnricher` | Nodos `Flow` desde `BackgroundService` y handlers HTTP, aristas `FLOW_ENTRY` y `FLOW_NEXT` con orden, condición y camino. Los puntos de entrada y la resolución de pasos son extensibles (`IFlowEntryProvider`, `IStepResolver`). |
| `ContractEnricher` | `public_props`, `values` de enums, sobrecargas, `doc_params`/`doc_returns`/`exceptions` y `doc_coverage` por proyecto. |
| `CyclomaticEnricher` | `cyclomatic` de McCabe (no es el `complexity` de cbm). |
| `HashEnricher` | `body_hash` y `contract_hash` por método, `type_hash` por tipo (SHA-256, deterministas). |
| `MetricsEnricher` | `fan_in`, `fan_out` (exactos, de las referencias de Roslyn), `hotspot`; agregados por clase; percentiles por proyecto. |
| `ReachabilityEnricher` | `is_reachable`, `dead_code_candidate`/`_confidence`/`_reason` (con métodos privados sin referencias dentro de clases vivas), `unused_registration` y `unused_config`. Los tests se tratan como raíces vivas (`TestConventionSuppressor`, extensible con `IDeadCodeSuppressor`). |
| `GraphAnalyticsEnricher` | Acoplamiento por clase, ciclos (`in_cycle`), violaciones de capas (`layer_violation`) y comunidades. |

Las propiedades y aristas que consume la app (`SqlFacetExtractorStep`) salen igual que con el original, con las diferencias de la sección siguiente.

## Cambios respecto al original

- **Proyectos C#.** Salen de la solución cargada, y cada nodo va al proyecto de la carpeta que contiene su `file_path`. El original los infería del segundo segmento del qualified_name: con los `.csproj` bajo `src/` salía un proyecto «src», y con un proyecto de cbm con puntos en el nombre (el de una ruta `C:\Users\nombre.apellido\…`) salía un único proyecto con un trozo del nombre. El rol sale primero de la solución (proyecto de test, ejecutable) y después del nombre; los proyectos de test llevan el rol `test`, fuera de las reglas de capas. Los percentiles y `doc_coverage` por proyecto siguen el mismo criterio.
- **Filtro de exclusión.** Las reglas de cbm (`bin`, `obj`, `temp`, `.gitignore`…) se aplican a la ruta relativa al repositorio. Con la absoluta, un repositorio bajo `%TEMP%` quedaba excluido entero y no se enriquecía nada.
- **Rutas.** `DiRegistration` y `ConfigClass` guardan el `file_path` relativo, como cbm; el original guardaba la ruta absoluta.
- **Carga de la solución.** Un proyecto multi-target se compila una vez. Si no queda ningún proyecto C# con código, la salida es 3 en lugar de seguir con un enriquecimiento vacío.
- **Sin lo de BDP.** Fuera los patrones BRMS/BDP/Dashboard de la inferencia de rol, la convención `*ShortCircuit*` de `FlowEnricher` (todos los `catch` son el camino `error`) y el tipo `source_adapter` de `ExtensionPointEnricher` (ahora `adapter`).
- **Roslyn 5.** `Microsoft.CodeAnalysis` 5.0.0, la versión que usan el Framework y FileTools, con `Workspaces.MSBuild` 5.0.0 y `Microsoft.Build.Locator`.

El nombrado de comunidades usa las IDF de `token_vectors`, que crea cbm al indexar, con el tokenizador de la vectorización original (`NameTokenizer`). La vectorización en sí no se copia porque la app ya tiene sus embeddings.

## Referencias exactas de Roslyn

`ReferenceIndex` recorre una vez todos los árboles con el modelo semántico (un `GetSymbolInfo` por nombre, más los `new`, inicializadores de constructor, atributos e indexadores) y guarda las referencias a símbolos del código fuente: quién, a qué, en qué línea, y si es una lectura, una escritura, la lista de bases de un tipo o un argumento de una llamada de registro de DI. Hace lo que `SymbolFinder.FindReferencesAsync` por cada símbolo, sin recargar la solución; por eso el CLI no conserva el `Solution`, basta con las compilaciones. Lo comparten `CallEnricher`, `DiEnricher`, `FieldEnricher`, `MetricsEnricher` y `ReachabilityEnricher`. `GraphSymbols` lo cruza con los nodos de cbm por qualified_name.

- **Código muerto.** Las aristas entre métodos y tipos C# salen del índice, no de CALLS/USAGE de cbm (que solo se siguen desde nodos que Roslyn no resolvió). Lo que nombra el código de nivel superior (`Program.cs`) o un miembro sin nodo es raíz. Un método privado (ordinario, sin atributos, que no sea explícito de interfaz, manejador de eventos ni `Main`) solo vive si lo referencia código vivo. Un servicio registrado en DI con `consumer_count = 0` ya no es raíz; sí lo son los de `AddHostedService` y los de servicios externos (los resuelve el framework).
- **`unused_registration`.** `consumer_count = 0`: nadie nombra el tipo del servicio salvo la llamada de registro y la lista de bases de las implementaciones. Cuentan los parámetros de constructor, `GetService<T>`/`GetRequiredService<T>`, `[FromServices]`, `IEnumerable<T>` y las factorías. Sin `consumer_count` (servicio externo, hosted service) no se marca.
- **`fan_in`/`fan_out`.** `fan_in`: miembros distintos que referencian al método, o a lo que implementa o sobrescribe, sin contarse a sí mismo. `fan_out`: símbolos del código fuente distintos que su firma y su cuerpo nombran.
- **Campos.** `READS`/`WRITES` por (origen, campo) con `lines` y `count`; `+=`, `++` y `ref` dan las dos; `out` y el destino de una asignación, `WRITES`; el inicializador del campo, `WRITES` desde el tipo; `nameof` no cuenta.

## Llamadas y excepciones

`CallEnricher` escribe una arista `CALLS` por (origen, método llamado) con `exact = true`, `origin = compiler`, `callee` (el método con su firma), `line` (la primera), `lines`, `count` y `kinds`: `invocation`, `creation` (`new`, al constructor con nodo), `constructor-initializer` (`: base(..)`/`: this(..)`) y `delegate` (el método nombrado como delegado). La llamada por interfaz o a un miembro virtual va al símbolo declarado, con `dispatch = interface`/`virtual`; las implementaciones las unen `IMPLEMENTS`/`OVERRIDES`. Si el origen no es un método con nodo lleva `context` (`property`, `field-initializer`, `event`, `member` o `top-level`, este desde el nodo `Module` o `File` del fichero, como cbm). No cuentan los atributos, `nameof` ni los métodos sin nodo (de metadatos, accessors, funciones locales). Sin los paquetes NuGet restaurados una llamada con un argumento sin tipo no resuelve: cuenta si queda un único candidato o si todas las sobrecargas candidatas caen en el mismo nodo de cbm (que es por nombre), y entonces la arista lleva `resolution = candidate`.

Las `CALLS` de cbm (resueltas por nombre: `strategy` `unique_name`, `suffix_match`, `field_type_hint`…) que salen de nodos de los ficheros C# analizados se borran antes; las de otros lenguajes y las que llevan `enriched_by` no se tocan. El total va a `cbm_edges_replaced` de `enrichment_runs`, el desglose por tipo a `details.cbm_edges_replaced`, y en `details.enricher_details.calls` quedan `exact`, `cbm_replaced`, `cbm_with_equivalent` (mismo origen y destino que una exacta) y `cbm_without_equivalent`. Medido en `csharp-simple` sin restaurar: 47 exactas, 45 de cbm sustituidas, 35 con equivalente y 10 sin él (todas eran llamadas por interfaz que cbm llevaba a una implementación, a veces la equivocada). En el grafo de `Framework`: 401 exactas, 387 sustituidas, 292 con equivalente.

`ThrowsEnricher`, en cada método o constructor con cuerpo: `throws` (tipos completos de `throw new X`, `throw e`, `?? throw` y `throw;`, que toma el tipo del `catch`; no los de lambdas), `rethrows` (`throw;` o `throw e` con la variable del catch), `rethrow_loses_stack` (`throw e`), `swallows_exceptions` y `swallowed_catches` (`[{line, type, kind}]`: `empty` e `inert` son la regla `empty-catch` de `IssueEnricher`, que sigue en `detected_issues` sin repetirse; `log-only` es un catch genérico que solo llama a algo o devuelve; `generic`, uno genérico que hace otra cosa sin relanzar; el de una cancelación no cuenta). Las aristas `THROWS` (`exact`, `origin = compiler`, `lines`, `kinds`) van al tipo de excepción cuando tiene nodo, y sustituyen a las de cbm igual que las `CALLS`.

**Constructores, tipos anidados e implementaciones explícitas.** `GraphMethods` dice qué métodos tienen nodo en cbm (ordinarios, constructores declarados con el nombre de la clase e implementaciones explícitas con el nombre del miembro) y lo usan todos los enriquecedores de métodos: antes `SemanticMethodEnricher`, `ContractEnricher` y `AttributeEnricher` se saltaban los constructores, y todos ellos las implementaciones explícitas (en `Framework`, 36 de 431 métodos sin `accessibility`: 30 constructores, 4 métodos de tipos anidados y 2 implementaciones explícitas; ahora 0). Los constructores llevan `is_constructor` y no `return_type_fqn`; las explícitas, `is_explicit_interface_impl` (y cuentan como polimórficas para el código muerto), salvo la que comparte nodo con un método ordinario del mismo nombre, que no le pisa las propiedades. `SymbolQn` nombra los tipos anidados con su cadena (`{fichero}.Outer.Inner`), como cbm: con solo `Inner` fallaban los patches de sus métodos (los `unresolved` de `enrichment_runs`).

## APIs sensibles, concurrencia y huellas

`SensitiveApiEnricher` mira cada llamada y cada `new` del cuerpo de los métodos y constructores con nodo (lambdas y funciones locales incluidas) y la clasifica por el tipo que declara el método, sus bases y sus interfaces, nunca por el texto: una clase propia llamada `File` no cuenta, y `SqlCommand.ExecuteReader` cuenta por `DbCommand`. Categorías: `file-io` (`File`, `Directory`, `FileInfo`/`DirectoryInfo`, `new FileStream`, `new StreamReader/StreamWriter` sobre una ruta), `process` (`Process.Start`/`Kill`/`GetProcesses*`), `network` (`HttpClient`, `Socket`, `TcpClient`/`UdpClient`/`TcpListener`, `WebClient`, `WebRequest.Create`, `Dns`), `deserialization` (`BinaryFormatter` y los demás formateadores inseguros, YamlDotNet `Deserialize`, y Newtonsoft solo con `TypeNameHandling` distinto de `None`: la asignación y las llamadas `Deserialize*` del mismo método), `reflection` (`Activator.CreateInstance`, `Type.GetType`, `Type.InvokeMember`, `Assembly.Load*`, `MethodBase.Invoke`), `dynamic-code` (`Expression.Compile`, `System.Reflection.Emit`, `Compilation.Emit`, `CSharpScript`, `CodeDomProvider.CompileAssembly*`), `environment` (`Environment.Get/SetEnvironmentVariable(s)`, `ExpandEnvironmentVariables`) y `sql` (`DbCommand`/`IDbCommand` `Execute*`, Dapper `Query*`/`Execute*`, EF Core `FromSql*`/`ExecuteSql*`/`SqlQuery*`). `api` es `{tipo}.{método}` o `new {tipo}`. Sin los paquetes restaurados, una llamada que no resuelve cuenta si todos los candidatos son del mismo tipo y nombre. El código fuera de un método con nodo (accessors, inicializadores) no se atribuye.

`ConcurrencyEnricher` escribe en el método `concurrency_markers` con `kind`: `lock` (la sentencia y `Monitor.Enter/TryEnter`), `Interlocked`, `async-void` (el método, una función local o una lambda `async` convertida a un delegado `void`, como `list.ForEach(async x => …)`), `fire-and-forget` (una sentencia que descarta una `Task`/`ValueTask`, también `_ = …`), `blocking-wait` (`.Result`, `.Wait()`/`WaitAll`/`WaitAny`, `GetAwaiter().GetResult()`), `Task.Run` (y `TaskFactory.StartNew`), `Parallel`, `thread` (`new Thread`, `ThreadPool.QueueUserWorkItem`) y `writes-static-mutable` (asigna un campo estático reasignable, por las escrituras exactas de `ReferenceIndex`, o modifica una colección estática no concurrente con `Add`/`Remove`/`Clear`… o por índice). En los nodos `Field`: `is_static_mutable` (estático y reasignable, o estático `readonly` de un array o una colección no concurrente) e `is_thread_safe_type` (`string`, `System.Collections.Concurrent`/`Immutable`/`Frozen`, primitivas de `System.Threading`, `Lazy`, canales). `blocking-wait` y `async-void` son los mismos hechos que las reglas `blocking-async` y `async-void` de `IssueEnricher`, que siguen siendo los hallazgos (aquí sin la excepción de los manejadores de eventos).

`HashEnricher` calcula SHA-256 sobre los tokens de Roslyn sin trivia y sobre firmas del compilador, en orden fijo, así que dan lo mismo en cada pasada, en cada máquina y con otro formato o comentarios: `body_hash` (cuerpo, cuerpo de expresión e inicializador `: base/this(..)`), `contract_hash` (firma completa con accesibilidad, modificadores, parámetros con nombre y valores por defecto, más `throws` de `ThrowsEnricher`; también en los métodos sin cuerpo) y `type_hash` (cabecera del tipo con bases, interfaces y atributos, y las huellas ordenadas de sus miembros; un tipo anidado aporta la suya). Las sobrecargas, que comparten nodo, se combinan ordenadas por firma. Medido en `Framework` (sin restaurar): 21 métodos con `sensitive_apis` (13 `file-io`, 4 `dynamic-code`, 3 `reflection`, 1 `environment`), 27 con `concurrency_markers` (19 con `lock`), 4 campos estáticos mutables de 201, `body_hash` en 375 de 431 métodos, `contract_hash` en 431 y `type_hash` en 206 tipos; dos pasadas seguidas dan las mismas huellas.

## Patrones de error y comentarios

`detected_issues` es una lista de textos `"{regla}: {descripción} (línea N)"` en el nodo del método (o del campo, o del tipo); `issue_rules`, las reglas sin repetir: `null-deref` (CS8602), `unused-field` (CS0414/CS0169, en el nodo Field), `unawaited-task` (CS4014), `unreachable-code` (CS0162), `obsolete-api` (CS0618/CS0612), `async-void`, `empty-catch`, `blocking-async`, `undisposed-resource` y `captive-dependency`. Las reglas que dependen de tipos de paquetes NuGet (`undisposed-resource` sobre `SqlConnection`…) necesitan la solución restaurada: sin ella el tipo no resuelve y Roslyn no sabe que es `IDisposable`.

`auth_required = false` con `auth_inferred = true` marca la ruta sin autorización declarada (atributo, cadena de `RequireAuthorization`, grupo, `[Authorize]` heredado). `[AllowAnonymous]` sigue siendo un `false` declarado, sin `auth_inferred`. No se infiere si la solución tiene una `FallbackPolicy`, un `AuthorizeFilter` global o `RequireAuthorization` sobre `MapControllers`/`MapRazorPages`, ni en un grupo que llega por parámetro, ni en `MapHealthChecks`.

`CommentEnricher` escribe `todo_markers` (`[{kind, line, text}]`, solo TODO/FIXME/HACK en mayúsculas), `commented_code` (`[{start_line, end_line, lines, preview}]`: comentarios que parsean como C# sin errores y hacen algo; una racha de código dentro de un bloque con prosa también cuenta), `docstring` y `docstring_source` (`xml` o `comment`).

## Cómo lo encuentra la app

`CodebaseAnalysis.Core` referencia este proyecto sin su ensamblado (`ReferenceOutputAssembly=false`, `PrivateAssets=all`): se compila con Core, y su salida viaja como contenido a `dotnet-enricher/` en la carpeta de cada ejecutable que referencia Core (el runner CLI, la web y los tests), también al publicar. Si todo se compila en un `OutDir` común (`-p:OutDir=...`), el CLI ya está en la carpeta de la app y no se copia. `DotNetEnricherStep` lo busca en los dos sitios, o en `DotNetEnricher:CliPath` si se configura. No hay que instalar nada aparte.

## Configuración (`DotNetEnricher` en `appsettings.analysis.json`)

| Clave | Por defecto | |
|---|---|---|
| `Enabled` | `true` | Con `false` se pasa directamente al enriquecimiento por LLM. |
| `Required` | `false` | Si el CLI no está, falla o se agota el tiempo, el análisis se para en lugar de seguir con el LLM. |
| `CliPath` | vacío | El CLI (`.exe` o `.dll`), si no es el que viaja con la app. |
| `SolutionPath` | vacío | Relativa al repositorio. Por defecto, el primer `.slnx`/`.sln` de la raíz o de sus subcarpetas inmediatas. |
| `ProjectName` | vacío | Proyecto de cbm; por defecto, el mayoritario en el grafo. |
| `TimeoutSeconds` | `900` | Límite del proceso; al superarlo se mata. 0 = sin límite. Tiene que quedar por debajo de `CodebaseAnalysis:Timeouts:EnrichMinutes`, que cubre también el fallback. |

## Tests

`Samples/DotNetEnricher.Tests` prueba los enriquecedores que consume la app (DI, rutas, flujos, alcanzabilidad, ciclomática, proyectos, métodos, `IMPLEMENTS`, referencias exactas, campos, patrones de error, comentarios, llamadas, excepciones, constructores, APIs sensibles, concurrencia, huellas y procedencia) sobre soluciones pequeñas compiladas sin MSBuild y un grafo con el esquema de cbm, más la transacción, el registro de pasadas y los códigos de salida.

El test de paridad (`[ParityFact]`) indexa una copia de `eval-repos/csharp-simple` con cbm, la enriquece con el BdpGraphEnricher.CLI instalado y con este CLI, y compara nodos, aristas y propiedades admitiendo solo las diferencias buscadas (`IntendedProperties` en `ParityTests`, cada una con su motivo). Se salta si falta cbm, el CLI original o el repositorio; se pueden indicar con `DOTNETENRICHER_PARITY_CBM`, `DOTNETENRICHER_PARITY_BDP_CLI` y `DOTNETENRICHER_PARITY_REPO`. La copia va a `%LOCALAPPDATA%\DotNetEnricherParity`, no a `%TEMP%`, porque el original no enriquece nada bajo una carpeta `Temp`.
