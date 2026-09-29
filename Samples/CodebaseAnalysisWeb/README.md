# Codebase Analysis Web

Aplicación web local para lanzar el análisis de `Samples/CodebaseAnalysis.Core/` sobre un repositorio, seguir su progreso en vivo, leer los informes y preguntar por el repositorio analizado a un chat con herramientas. El backend es ASP.NET Core con minimal APIs y Server-Sent Events, y la interfaz es una SPA de React con Vite en `ClientApp/`. El pipeline es el mismo que ejecuta la CLI (`Samples/CodebaseAnalysisPipeline/`, cuyo README describe los pasos, los informes y su configuración): la web no reimplementa nada del análisis, solo lo aloja, reenvía sus eventos y añade el chat.

La interfaz tiene una barra de repositorio (ruta, rutas recientes guardadas en el navegador y estado del grafo y de los informes) y dos pestañas cuya selección va en el hash de la URL: `#analisis` y `#asistente?file=<sel>&chat=<id>`, donde `<sel>` es `<fichero>`, `custom/<fichero>` o `doc/<id>`. Los enlaces antiguos `#informes/<sel>` y `#chat/<id>` se reescriben a ese formato al abrirlos. En **Análisis** se eligen los informes y la reindexación, y se ven las cinco fases, un carril por auditor con sus llamadas a herramientas y el log de eventos. **Asistente** reparte la pantalla en tres paneles (biblioteca, chat y visor) que se redimensionan arrastrando o con las flechas sobre el separador, y que se pliegan y despliegan con los botones de su barra; el último visible no se puede plegar, y tamaños y plegados se recuerdan en el navegador (`localStorage`). La biblioteca lista el `00` y los nueve informes con su semáforo, los informes propios que ha escrito el chat y los documentos que ha subido el usuario, y permite añadir cualquier fuente al grafo y a los embeddings. El chat guarda conversaciones por repositorio, que se eligen en un modal desde su cabecera, y muestra la respuesta en streaming junto con las herramientas que usa el modelo y un aviso por cada informe que escribe, con su «Deshacer»; «Abrir» en ese aviso lo enseña en el visor sin salir del chat. El visor muestra solo el fichero seleccionado, sin solapas: renderiza el Markdown y los diagramas Mermaid o abre el documento en su visor, y si estaba plegado se despliega al elegir un fichero.

## 1. Requisitos

- **.NET 10:** los proyectos compilan contra `net10.0`.
- **Node.js >= 22.22.2:** lo exige `engines` de `ClientApp/package.json` (jsdom 30, que usan los tests). Solo hace falta para compilar o desarrollar la SPA; el backend compila y sirve la API sin ella.
- **Los del pipeline:** `codebase-memory-mcp`, un proveedor LLM con clave y, según el repositorio, Microsoft Access, `BdpGraphEnricher.CLI` o un JDK 17 o superior para el analizador Java (no se instala ni se descarga; su carpeta va en `JavaAnalyzer:JdkPath`). El apartado 1 del README de la CLI los detalla.

## 2. Ejecución

### 2.1 Desarrollo

Backend y SPA se arrancan por separado. El backend escucha en `http://127.0.0.1:5180` (`appsettings.json` y el perfil de `Properties/launchSettings.json`), y el servidor de Vite en `http://localhost:5173` reenvía `/api` a ese puerto:

```powershell
dotnet run --project Samples/CodebaseAnalysisWeb/CodebaseAnalysisWeb.csproj

# en otra consola
cd Samples/CodebaseAnalysisWeb/ClientApp
npm ci
npm run dev
```

La interfaz se abre en `http://localhost:5173`. El proxy de `vite.config.ts` hace dos cosas además de reenviar: corta la petición al backend cuando el navegador cierra la suya, para que «Parar» en el chat llegue al servidor como cancelación del turno, y fuerza `Cache-Control: no-cache` y el envío inmediato de cabeceras en las respuestas `text/event-stream`, para que los eventos no se agrupen.

### 2.2 Release

`dotnet publish` ejecuta `npm ci` y `npm run build` en `ClientApp/` y publica la SPA compilada en `wwwroot/` junto al ejecutable, con `appsettings.analysis.json` y `templates/` copiados desde Core:

```powershell
dotnet publish Samples/CodebaseAnalysisWeb/CodebaseAnalysisWeb.csproj -c Release -o C:\ruta\publicacion
C:\ruta\publicacion\CodebaseAnalysisWeb.exe
```

La aplicación queda en `http://127.0.0.1:5180`, servida por el propio backend y sin proxy. Para compilar la SPA también en un `dotnet build` se pasa `-p:BuildClientApp=true`; la salida de Vite va a `Samples/CodebaseAnalysisWeb/wwwroot/`, que está en `.gitignore`. `-p:SkipClientApp=true` desactiva la compilación de la SPA en los dos casos, por ejemplo para publicar con un `wwwroot/` ya compilado. Sin Node en el `PATH`, el target falla con un mensaje que lo dice.

## 3. Configuración

La web carga dos ficheros. `appsettings.json`, el suyo, solo lleva `Urls`, `AllowedHosts` y niveles de log. La configuración del análisis es la de la CLI: `appsettings.analysis.json` junto al ejecutable, con las referencias `${VARIABLE}` expandidas y después, por este orden, variables de entorno, user secrets y línea de comandos, de modo que se imponen al JSON igual que en la CLI. Las dos aplicaciones comparten `UserSecretsId`, así que una clave guardada con `dotnet user-secrets` para la CLI vale también para la web.

| Clave | Por defecto | Efecto |
|---|---|---|
| `Urls` | `http://127.0.0.1:5180` | Dirección de escucha. Solo se admite loopback (apartado 7). |
| `CodebaseAnalysis:DatabasePath`, `OutputReportsPath` | `.codebase/codebase.db`, `reports` | Relativas al repositorio abierto, con la misma regla que la CLI. No se cambian por petición. |
| `CodebaseAnalysis:RequestedReports` | los nueve | Informes preseleccionados en la interfaz (`defaultReports` de `/api/meta`). |
| `LlmConfiguration:Roles:ChatAssistant` | declarado en `appsettings.analysis.json` | Rol del chat: plantilla `chat_system`, `MaxOutputTokens` 32768, `UseStreaming: true`. Si no se declara, se crea desde `DefaultProvider` y con streaming activado. |
| `LlmConfiguration:Roles:ChatAssistant:StreamingInactivityTimeoutSeconds` | 300 en `appsettings.analysis.json`; sin la clave, 120 (o el del proveedor, si lo fija) | Espera máxima de cada trozo del stream del chat; 0 la quita. Con Azure y `gpt-5.4`, que entregan la respuesta entera al final, una respuesta que tarde más en generarse corta el turno con un error que nombra la clave: en ese caso hay que subirla. Sin streaming manda `RequestTimeoutSeconds` (600). Los dos plazos se explican en el apartado 3.6 del README de la CLI. |
| `CodebaseAnalysis:Chat:MaxToolIterations` | 12 | Iteraciones de herramientas por turno. |
| `CodebaseAnalysis:Chat:MaxHistoryTurns` | 20 | Turnos completados que se reenvían al modelo como contexto. |
| `CodebaseAnalysis:Chat:TurnTimeoutMinutes` | 5 | Plazo de un turno de chat; 0 lo quita. Al superarlo, el turno falla con un error que nombra esta clave. |
| `CodebaseAnalysis:Documents:MaxDocuments`, `MaxTotalMegabytes` | 300, 500 | Documentos subidos y megas por repositorio; al superarlos, 409 `document_limit_reached`. |
| `CodebaseAnalysis:Documents:VisionRole` | `ChatAssistant` | Rol con el que «Describir» y `describe_image` describen imágenes; sin rol o sin clave, la función queda desactivada con el motivo. |
| `CodebaseAnalysis:Documents:IncludeInAnalysisByDefault`, `AnalysisIndexMaxChars` | `true`, 8000 | Valor inicial de `useInAnalysis` de un documento subido y tamaño del índice de documentos que reciben los auditores. |
| `CodebaseAnalysis:Embeddings:Enabled`, `Provider`, `Model` | `true`, `AzureOpenAI`, `text-embedding-3-small` | Proveedor y modelo (en Azure, el nombre del despliegue) de los embeddings; sin clave, desactivados con el motivo. |
| `CodebaseAnalysis:Embeddings:Dimensions`, `BatchSize`, `MaxChunkChars`, `ChunkOverlapChars` | `null`, 32, 1800, 200 | Dimensiones pedidas (sin valor, las del modelo: 1536), trozos por petición, tamaño de trozo y solape. |
| `CodebaseAnalysis:Timeouts:*` | 240, 15, 20, 60, 15, 15 | Límites en minutos del análisis y de la regeneración, 0 sin límite: `PipelineMinutes`, `DetectMinutes`, `EnrichMinutes`, `ReportsMinutes`, `AuditorMinutes` y `SynthesisMinutes`. Los explica el apartado 3.6 del README de la CLI. |
| `CodebaseAnalysis:Reports:MaxParallelAuditors` | 0 | Auditores a la vez en el análisis y en la regeneración; 0 no pone tope propio (uno por procesador). |
| `JavaAnalyzer:*` | sin JDK ni jars *provided* |Analizador Java: `JdkPath`, `JvmOptions`, `BatchSize`, `MaxParallelBatches`, `ProvidedClasspath`, `TestRoots`, `DefaultEncoding`, `TimeoutSeconds`, `StepTimeoutSeconds`. El significado de cada clave está en el apartado 4 del README de la CLI. |
| `StrutsJsp:*` | activado | Plugin Struts/JSP: `Enabled`, `SuspiciousImportPatterns` (imports que marcan una JSP por sí solos) y `BusinessImportPatterns` (los que solo la marcan si los usa un scriptlet). |
| `CodebaseAnalysis:Web:SettingsPath` | `%LOCALAPPDATA%\CodebaseAnalysisWeb\settings.json` | Fichero donde se recuerda el último repositorio abierto. |
| `CodebaseAnalysis:Web:SseKeepAliveSeconds` | 15 | Intervalo del comentario `: keep-alive` en los dos streams SSE. |
| `CodebaseAnalysis:Web:MaxEventsPerRun` | 50000 | Eventos que se guardan por análisis; al superarlo se descartan los más antiguos y el replay empieza con `log.truncated`. |

Las claves `CodebaseAnalysis:Web:*` no aparecen en ningún JSON y se fijan con cualquiera de las fuentes anteriores, por ejemplo `--CodebaseAnalysis:Web:SettingsPath C:\ruta\settings.json`. El último repositorio se guarda en `%LOCALAPPDATA%` porque es un dato por usuario que tiene que sobrevivir a recompilar y republicar, que vacían la carpeta del ejecutable. Al arrancar, la web lo reabre si la ruta sigue existiendo; si no existe, el fichero está dañado o la apertura falla, arranca sin repositorio y lo avisa en el log. La web ignora `McpServers:CodebaseMemory:ConnectMcp`: ni los auditores ni el chat usan las herramientas MCP.

El log va a la consola del servidor y, durante un análisis, también a `<repositorio>/logs/analysis-pipeline-<fecha>.log`, igual que en la CLI.

La fase «Enriquecimiento» agrupa dos steps, `LanguageEnricherStep` (analizador Java y plugin Struts/JSP) y `BdpEnricherStep`: la abre el primero, solo el `step.completed` correcto del segundo la da por terminada, el fallo de cualquiera de los dos la marca fallida y su duración va del inicio de la fase a su cierre, de modo que no aparece como terminada entre un step y otro. Los mensajes del analizador salen en el log de eventos de esa fase, con nivel de aviso cuando falta el JDK («⚠️ Analizador Java: … Se continúa con el grafo de cbm.»), faltan paquetes en el classpath o hay ficheros con errores de compilación, y la salida cruda del proceso Java va por el canal `indexer`, como la del indexador. Sus ficheros de trabajo quedan en `<carpeta de la BD>/enrichment/java/`, y cómo se repite el enriquecimiento lo explica el apartado 3.2 del README de la CLI.

## 4. Modelo de uso

El proceso trabaja sobre **un único repositorio activo**, porque la raíz del sandbox de FileTools y el registro de herramientas son estáticos de proceso. Abrirlo (`PUT /api/workspace`) exige una ruta absoluta a un directorio que exista, crea `<carpeta de la BD>/chat/` y comprueba que se puede escribir en ella antes de cambiar nada, registra las herramientas y deja el sandbox de FileTools solo en ese repositorio. Si la escritura falla, la respuesta es 400 `workspace_not_writable` y el repositorio anterior sigue activo.

Solo corre un análisis a la vez, y análisis y chat se excluyen, porque el análisis sobrescribe la BD del grafo y mueve los informes que un turno de chat puede estar leyendo. De ahí los 409 cruzados: lanzar un análisis con un turno de chat en curso da `chat_turn_running`, enviar una pregunta con un análisis en curso da `analysis_running`, y cambiar de repositorio con cualquiera de los dos en curso da el código correspondiente. Cada sesión de chat admite un turno a la vez.

Las operaciones de conocimiento (subir, describir o borrar un documento, escribir en el grafo o en los embeddings, restaurar una versión o borrar un informe propio) se apuntan en un contador bajo el mismo cerrojo mientras duran, y la matriz completa queda así:

| Operación | Con análisis en curso | Con turno de chat en curso | Con otra operación de conocimiento |
|---|---|---|---|
| Subir, renombrar, cambiar `useInAnalysis` o borrar un documento que no está en el grafo | permitida | permitida | permitida |
| Añadir o quitar del grafo; borrar un documento que está en el grafo | 409 `analysis_running` | 409 `chat_turn_running` | 409 `knowledge_operation_running` |
| Indexar o quitar embeddings de un documento o de un informe propio | permitida | permitida | 409 `knowledge_operation_running` |
| Indexar embeddings de un informe del catálogo | 409 `analysis_running` | permitida | 409 `knowledge_operation_running` |
| Restaurar una versión, borrar un informe propio | 409 `analysis_running` | 409 `chat_turn_running` | permitida |
| Describir una imagen | permitida | permitida | 409 `knowledge_operation_running` |
| Lanzar un análisis, enviar una pregunta, cambiar de repositorio | — | — | 409 `knowledge_operation_running` |

Las escrituras de grafo y embeddings del proceso, incluidas las que hace el chat al reingestar un informe y las del pipeline, pasan además por un único semáforo (`KnowledgeGraphIngestor.WriteGate`), y dentro de él se vuelve a comprobar que la fuente sigue existiendo, de modo que borrar un documento mientras otra petición lo indexa no deja restos en ninguno de los dos. La llamada de red de embeddings queda fuera del semáforo.

El análisis corre desacoplado de la petición HTTP, así que cerrar la pestaña no lo para. El servidor guarda en memoria los eventos de la última ejecución, en curso o terminada, hasta que empieza otra: una pestaña recargada o una segunda pestaña reconstruyen el estado reproduciendo esos eventos desde el principio y siguen en vivo. Tras reiniciar el servidor esa lista se pierde, aunque los informes y el log siguen en disco. Cancelar detiene el pipeline, termina los procesos externos con su árbol y deja los informes ya escritos; parar el servidor cancela también el análisis en curso.

Con la ejecución terminada, el carril de un auditor que ha fallado o se ha cancelado muestra «Regenerar», y encima de los carriles aparece la casilla «Actualizar también el resumen ejecutivo», marcada por defecto. El botón no depende de `failedReports`, que llega vacío cuando falla el pipeline entero, y sale también en los cancelados para poder regenerar tras cancelar a mitad de los informes, porque la cancelación los deja como cancelados y no como fallidos. Pulsarlo llama a `POST /api/analysis/{runId}/regenerate`, que ejecuta el pipeline reducido del apartado 3.7 del README de la CLI (preparación, facetas, ese auditor y, con la casilla, la síntesis) sobre el grafo existente, sin detección, indexado ni enriquecimiento. No es una ejecución nueva: la misma vuelve a `running` con el mismo `runId`, emite `run.regeneration_started` con `{ reports, synthesis }` y sigue en el mismo log de eventos, con el `seq` a continuación, hasta un segundo `run.completed`. La interfaz no se reinicia: los carriles pedidos vuelven a «En espera», las fases de facetas, auditores y, si procede, resumen vuelven a pendientes, y el resto conserva su estado. El `failedReports` del nuevo `run.completed` es el de antes, menos los regenerados que han escrito su informe, más los que no lo han escrito, más los pedidos en la ejecución original que no tienen `report.written` en toda la ejecución, la original y sus regeneraciones; si se cancela, los regenerados que no llegaron a escribirse solo entran por esta última regla. Así, tras cancelar un análisis de dos informes y regenerar solo uno, la ejecución termina `completed_with_failures` con el otro en la lista, y no `succeeded`. El aviso de fin es verde solo sin informes pendientes: con fallos es de aviso, como la cancelación, y lista los ficheros que faltan («Informes sin generar: …»); rojo queda para la ejecución fallida. El botón se desactiva mientras arranca la regeneración y, si el servidor la rechaza, el error sale en el carril. Solo se puede regenerar la última ejecución que el servidor tiene en memoria: tras reiniciarlo no hay ejecución ni botón, y hay que lanzar el análisis. Tampoco se regenera una ejecución de otro repositorio: cambiar de repositorio no la borra, pero el botón desaparece y el servidor responde 409 `run_from_other_workspace`, porque regenerarla reapuntaría las herramientas del chat al repositorio de la ejecución.

Un turno de chat es un paso LLM con prompt literal (el texto del usuario no pasa por el motor de plantillas) y dieciséis herramientas. Diez son de solo lectura sobre el grafo, el código y los informes: `graph_search`, `graph_neighbors`, `graph_domain`, `view_file`, `view_code_item`, `list_dir`, `find_by_name`, `grep_search`, `list_reports` y `read_report`, que también lista y lee los informes propios. Las tres de búsqueda son las de FileTools, con el mismo sandbox, y sirven para localizar ficheros y líneas en vez de adivinar rutas. `graph_search` devuelve, tras la tabla de nodos, las propiedades útiles de cada uno que las tenga (firma, tipos, visibilidad, `cyclomatic`, `overload_of`, `fqn`, `super_fqn`, `interfaces_fqn`, las de rutas y acciones de framework, los indicadores de JSP y el docstring truncado a 300 caracteres), y lo dice cuando las `properties` de un nodo no son JSON válido; `include_properties: false` las quita. Con `include_code: true` añade el código de hasta cinco nodos (150 líneas cada uno) leído del fichero actual con el rango de líneas del nodo. Solo si el fichero no se puede leer, devuelve la copia de `properties.code` del grafo, avisando de que puede estar desfasada. `graph_neighbors` da los vecinos de un nodo, por su Qualified Name exacto o un fragmento, agrupados por tipo de arista y dirección (`out`, `in` o `both`): por defecto `CALLS`, `INHERITS`, `IMPLEMENTS`, `DEFINES_METHOD`, `HANDLES`, las aristas Struts/JSP (`FORWARDS_TO`, `SUBMITS_TO`, `INCLUDES`, `RENDERS`, `EXTENDS_DEFINITION`) y las del analizador Java para tipos y campos (`USES_TYPE`, `READS`, `WRITES`), hasta 50 vecinos (`limit`, máximo 200) y avisando si trunca. De cada `CALLS` enseña las líneas de la llamada en el fichero que llama; de `USES_TYPE`, los contextos del uso (`param`, `return`, `local`, `cast`, `new_implicit`…); de `READS` y `WRITES`, el acceso; de los forwards, su nombre, y de `RENDERS`, el rol. Con `direction: in` sobre una clase o un campo dice dónde se usa, y con `line` solo devuelve las aristas que tienen esa línea en el fichero de su origen, para preguntar qué referencia hay en la línea N de un método. Si el fragmento casa con varios nodos lista los candidatos y no mezcla vecinos, y una llamada a un método de interfaz lleva una nota para pedir las `IMPLEMENTS` de la interfaz. `graph_domain` da las rutas con su clase (`routes`), los registros de inyección de dependencias (`di`) y los atributos de código (`attributes`). `create_report` y `update_report` escriben informes (apartado 5), `list_documents`, `read_document` y `describe_image` trabajan sobre los documentos subidos (apartado 6) y `semantic_search` busca por embeddings en documentos e informes indexados (apartado 7). El prompt de sistema es `templates/chat_system.md` más cuatro bloques de contexto concatenados en C#: el catálogo de informes con semáforo y fecha, los documentos aportados (hasta 40, con su estado en el grafo y en los embeddings), el estado del grafo y el `00_resumen_ejecutivo.md` truncado a 12000 caracteres. Se concatenan y no se interpolan porque el renderer de plantillas resuelve los `@[fichero]` después de interpolar, y un informe que contuviera `@[x]` se leería como un include. Si el cliente aborta la petición, el turno se guarda como `cancelled` y conserva como respuesta el texto que el modelo había emitido desde su última llamada a herramientas.

El prompt limita el chat al repositorio abierto: análisis, refactorizaciones, planes y código nuevo para él. Un concepto general solo se explica en relación con este código, y cualquier otra petición se rechaza en una frase, sin herramientas. Es una regla del prompt, no un filtro del servidor.

Cada hecho sobre el sistema empieza con una marca:

- `[V]` verificado en el código en ese turno, con una referencia `ruta:línea` o `ruta:ini-fin`;
- `[D]` sacado de un informe o documento aportado, con `nombre › sección`;
- `[I]` inferido, sin referencia.

Las propuestas (planes, recomendaciones, código nuevo, criterios) no se marcan. Al completar el turno, `ClaimAuditor` contrasta cada marca con lo que leyeron de verdad sus herramientas. La evidencia sale de sus resultados:

- las líneas que enseñó `view_file`;
- las coincidencias de `grep_search`;
- los tramos de `graph_search` con `include_code` (la tabla y las propiedades de `graph_search`, `graph_neighbors` y `graph_domain` son del grafo y no cuentan como código leído, aunque citen `ruta:línea`: el prompt pide abrir esa línea con `view_file` para marcar `[V]`, o marcar `[I]` diciendo que sale del grafo);
- de `view_code_item`, solo el tramo `Location` de cada nodo que devuelve, no el fichero entero;
- los informes y documentos de `read_report` y `read_document`.

Una marca que no se respalda queda `unchecked` con el motivo: no se leyó en este turno, se leyó solo en parte, esas líneas no existen, el fichero no existe o falta la referencia. Nunca se rechaza la respuesta. Como `BaseLlmStep` compacta la conversación al par pregunta/respuesta al terminar, el step del chat guarda los mensajes del turno justo antes. En la interfaz, las marcas son insignias (V, D, I, o «?» si están sin comprobar, con el motivo al pasar el ratón), cada referencia abre su código o su informe en el visor y cada respuesta lleva un resumen por estado al pie.

## 5. Informes propios y versiones

El chat escribe informes con dos herramientas, y solo cuando el usuario lo pide de forma explícita, que es lo que dice `chat_system.md`; lo que sí es determinista es que toda escritura queda versionada, se ve en el hilo y está limitada a cinco por turno, contando también los intentos fallidos. `create_report { title, content, note? }` crea `reports/custom/<slug>.md`, con el slug sacado del título (minúsculas, sin tildes, `[a-z0-9-]`, 60 caracteres) y desambiguado con `-2`, `-3` frente a los ficheros existentes y también frente a los historiales de propios ya borrados, para no mezclar las versiones de dos informes. `update_report { file_name, mode, heading?, content, note }` modifica un informe del catálogo o un propio con `replace`, `replace_section` o `append_section`; en un informe del catálogo conserva la línea `analysis-meta` si el contenido nuevo no la trae, y un contenido idéntico al actual es `invalid_request` y no crea versión. Los límites son 100 informes propios por repositorio (`report_limit_reached`) y 300 000 caracteres por informe (`report_too_large`). El pipeline no toca `reports/custom/` y la síntesis no lo lee, así que un informe propio no entra en el `00`.

Cada estado se guarda en `.codebase/report-history/<catalog|custom>/<fichero>/`, con `history.json` y una instantánea `vNNNN.md` por versión (acción, origen `pipeline`, `chat` con sesión y turno, o `ui`, nota, tamaño y SHA-256), hasta 50 por informe. Antes de modificar un informe del catálogo cuyo contenido no está en el historial, porque lo regeneró el pipeline, se guarda como versión `baseline`. «Deshacer» restaura `previousVersionId` y crea a su vez una versión `restored`; borrar un propio, que solo se hace desde la interfaz, guarda una versión `deleted`, lo quita del grafo y de los embeddings y conserva el historial.

Cada escritura del chat emite `report.changed` en el SSE del turno, detrás del `tool.completed` de su herramienta, y queda en el turno persistido como artefacto, que es lo que pinta el aviso «Informe guardado» o «Informe modificado» con «Abrir» y «Deshacer» o «Borrar». Al restaurar o borrar, la web marca los artefactos afectados con `undoneByVersionId` y `undoneAction` (`restored` o `deleted`), de modo que al recargar el aviso dice «Deshecho» o «Borrado» con la versión y un informe borrado no ofrece «Abrir». Si el informe estaba en el grafo, se reingesta en el acto; si esa reingesta falla, el cambio ya está hecho, así que el evento y el artefacto salen igual y el fallo llega al modelo como aviso.

## 6. Documentos aportados

Los documentos se suben de uno en uno a `POST /api/documents`, con los bytes en crudo, `Content-Type: application/octet-stream` y el nombre en `X-File-Name` codificado con `encodeURIComponent`, desde el selector o arrastrándolos a la pestaña Informes. Cada uno queda en `.codebase/documents/<id>/` con `original.bin`, `content.md` (el texto que lee el LLM) y `document.json`, sin manifiesto global. La validación se hace por contenido y no por lo que declara el navegador: firmas de PDF e imágenes, UTF-8 estricto sin NUL para las familias de texto, estructura y límites anti-bomba del ZIP de Office (200 MB descomprimidos y 10 000 entradas), y rechazo de un SHA-256 repetido con 409 `duplicate_document` y el `documentId` existente. Si la extracción falla o supera su plazo de 30 s, el documento se guarda igual con `extraction.status = failed` y el motivo, porque el visor del original puede servir.

| Familia | Extensiones | Máximo | Visor | Qué contiene `content.md` |
|---|---|---|---|---|
| `markdown` | `.md`, `.markdown` | 5 MB | Markdown | El texto, con secciones por encabezados |
| `text` | `.txt`, `.log` | 5 MB | Texto | El texto, con secciones de unos 4000 caracteres |
| `code` | `.cs`, `.vb`, `.ts`, `.py`, `.java`, `.sql`, `.ps1` y otras 33 | 5 MB | Código resaltado | Un bloque cercado con su lenguaje |
| `structured` | `.json`, `.yaml`, `.yml`, `.xml` | 5 MB | Código resaltado | JSON reindentado si es válido; XML sin DTD |
| `csv` | `.csv`, `.tsv` | 10 MB | Tabla | Tabla con 5000 filas y 50 columnas como máximo |
| `html` | `.html`, `.htm` | 5 MB | `<iframe sandbox="">` sobre el original y texto extraído | Markdown sin `script`, `style`, `iframe`, `object`, `embed` ni `template` |
| `pdf` | `.pdf` | 25 MB | Visor del navegador y texto extraído | `## Página N`, hasta 1000 páginas; sin texto, `empty` (no hay OCR) |
| `docx` | `.docx` | 25 MB | Markdown | Encabezados, listas y tablas |
| `xlsx` | `.xlsx` | 25 MB | Markdown | `## Hoja: <nombre>` y una tabla por hoja, 1000 filas y 50 columnas, leída fila a fila |
| `pptx` | `.pptx` | 25 MB | Markdown | `## Diapositiva N — <título>`, el texto de las formas y las notas |
| `image` | `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp` | 10 MB | Imagen | Vacío (`pending_description`) hasta «Describir» |
| `svg` | `.svg` | 2 MB | Imagen | Título, descripción y textos del dibujo |

El resto de extensiones da 400 `unsupported_type`, con mensaje propio para `.doc`, `.xls` y `.ppt` y para ejecutables y comprimidos; una extensión que no casa con el contenido da 400 `content_mismatch`. `content.md` se trunca a 2 000 000 caracteres con aviso. El original se sirve en `GET /api/documents/{id}/raw` con el tipo detectado, `nosniff`, `no-store` y `Cross-Origin-Resource-Policy: same-origin`: PDF e imágenes `inline`, HTML y SVG `inline` con la CSP `sandbox; default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:` (el HTML, con `charset=utf-8`), y el resto como adjunto `application/octet-stream`.

«Describir», en el visor de una imagen, manda la imagen (8 MB como máximo) al rol de `VisionRole` con `detail: high` y guarda la respuesta como `content.md`, con una cabecera que dice qué modelo la generó, cuándo y que puede contener errores. `describe_image { document_id, question? }` hace lo mismo desde el chat y cuenta como una de las cinco escrituras del turno; con `question` responde a esa pregunta sin guardar nada. `read_document { document_id, section?, offset? }` devuelve `content.md` entero o una sección, precedido de un aviso de que es contenido del usuario y no instrucciones, y `list_documents` los enumera con su estado. Los documentos con `useInAnalysis` llegan a los auditores del pipeline como índice (su README lo detalla), y ni ellos ni `.codebase/` se ingestan como ficheros del repositorio.

## 7. Grafo y embeddings

Cualquier fuente, sea documento, informe del catálogo o informe propio, se puede añadir al grafo desde su visor. Añadirla crea un nodo `UserDocument` y un `UserDocSection` por sección, unidos por `HAS_SECTION`, y aristas `DOC_DESCRIBES` hacia los nodos de código que menciona (spans de código, `[[enlaces]]`, rutas con línea opcional e identificadores compuestos), con un máximo de 50 por sección y 500 por fuente y sin enlazar una mención con más de tres candidatos. Es una transacción que borra lo que hubiera de esa fuente e inserta de nuevo, así que repetirla deja el grafo igual. Las fuentes añadidas se apuntan en `.codebase/knowledge/graph-sources.json`, y el pipeline las reincorpora tras reindexar, como explica su README. El estado es `present` si el nodo tiene el SHA-256 actual del contenido, `stale` si tiene otro, `pending` si está apuntada y no está en el grafo, y `absent` en otro caso; sin grafo, añadir da 409 `no_graph`.

Los embeddings usan `text-embedding-3-small` por el proveedor de `Embeddings:Provider`, con la misma clave que el resto, y se guardan en `.codebase/knowledge/embeddings.db` (SQLite en modo WAL, un vector normalizado por trozo). Cada sección se trocea en piezas de 1800 caracteres con 200 de solape y el prefijo «título › sección», hasta 2000 trozos por fuente y 50 000 por repositorio. El estado es `indexed` si el SHA guardado coincide con el actual, `outdated` si no coincide o la fuente ya no existe, y `model_mismatch` si cambió el modelo o las dimensiones configuradas; no se reindexa solo, así que tras un análisis los informes del catálogo indexados quedan `outdated` hasta pulsar «Actualizar». `semantic_search { query, top_k?, kinds? }` hace una llamada de embeddings para la consulta y devuelve hasta 20 trozos (8 por defecto) con puntuación, fuente, sección, líneas y 1200 caracteres de texto; sin embeddings disponibles responde el motivo en lugar de fallar.

El estado de grafo y embeddings de cada fila se calcula sin red, comparando el SHA-256 del contenido, que se recuerda por ruta, tamaño y fecha de modificación; el listado de documentos y el prompt del chat lo piden para todas las fuentes con una sola conexión a cada BD.

## 8. API

Todas las rutas cuelgan de `/api`. Los errores responden `{ "error": "<código>", "message": "<texto>" }`, con `runId` añadido en los 409 `analysis_running` y `not_running`. Los POST y PUT exigen `Content-Type: application/json`, incluido el cancel, que lleva `{}`. Los payloads, el modelo de eventos y el formato del historial están en `docs/project_notes/codebase-analysis-web-plan.md` (§4 y las secciones de decisiones del final, que mandan cuando discrepan).

| Método y ruta | Éxito | Errores |
|---|---|---|
| `GET /api/meta` | 200: catálogo de informes, fichero del `00`, informes por defecto, carpeta de los propios, límites y tipos de documento con sus máximos, y disponibilidad de «Describir» y de los embeddings con el motivo | — |
| `GET /api/workspace` | 200 (`repositoryPath: null` si no hay repositorio) | — |
| `PUT /api/workspace` `{ repositoryPath }` | 200 | 400 `invalid_path`, `workspace_not_writable` · 409 `analysis_running`, `chat_turn_running` |
| `POST /api/analysis` `{ forceReindex, reports[] }` | 202 | 400 `unknown_report` · 409 `no_workspace`, `analysis_running`, `chat_turn_running` |
| `GET /api/analysis/current` | 200 (última ejecución) · 204 si no hubo ninguna | — |
| `GET /api/analysis/{runId}/events?after=<seq>` | 200 SSE | 404 |
| `POST /api/analysis/{runId}/cancel` | 202 | 404 · 409 `not_running` |
| `POST /api/analysis/{runId}/regenerate` `{ reports[], synthesis }` | 202 (la misma ejecución, otra vez en curso) | 400 `unknown_report`, `invalid_request` (lista vacía) · 404 (no es la última ejecución) · 409 `analysis_running`, `chat_turn_running`, `knowledge_operation_running`, `run_from_other_workspace` (la ejecución es de otro repositorio que el abierto) |
| `GET /api/reports` | 200 | 409 `no_workspace` |
| `GET /api/reports/{fileName}` | 200 `text/markdown` | 404 (fuera del catálogo o inexistente) · 409 `no_workspace` · 503 `report_unavailable` |
| `GET /api/chat/sessions` | 200 (`[]` sin repositorio) | — |
| `POST /api/chat/sessions` `{ title? }` | 201 | 409 `no_workspace` |
| `GET /api/chat/sessions/{id}` | 200 | 404 (también si el fichero está dañado) |
| `DELETE /api/chat/sessions/{id}` | 204 | 404 · 409 `chat_turn_running` |
| `POST /api/chat/sessions/{id}/messages` `{ message }` | 200 SSE | 400 `empty_message`, `message_too_long` (más de 8000 caracteres) · 404 · 409 `no_workspace`, `analysis_running`, `chat_turn_running`, `knowledge_operation_running` |
| `GET /api/source?path=<ruta>` | 200 `{ path, text, totalLines, sizeBytes }`: fichero de texto del repositorio, UTF-8 o, si no lo es, la codificación que declara el repositorio (`JavaAnalyzer:DefaultEncoding` o el `pom.xml`) o Latin-1, con la misma regla que `view_file` y `grep_search` | 400 `invalid_path` (fuera del repositorio), `unsupported_type` (binario) · 404 (inexistente, bajo `.git` o con un enlace simbólico o unión en la ruta) · 409 `no_workspace` · 413 `file_too_large` (más de 2 MB) |
| `GET /api/reports/custom` | 200: solo los propios | 409 `no_workspace` |
| `GET /api/reports/custom/{fileName}` | 200 `text/markdown` | 404 · 409 `no_workspace` · 503 `report_unavailable` |
| `DELETE /api/reports/custom/{fileName}` | 204 | 404 · 409 `analysis_running`, `chat_turn_running` |
| `GET /api/reports/{fileName}/versions`, `GET /api/reports/custom/{fileName}/versions` | 200 (la más reciente primero, con `isCurrent`) | 404 |
| `GET …/versions/{versionId}` | 200 `text/markdown` | 404 `version_not_found` |
| `POST …/versions/{versionId}/restore` `{}` | 200: el informe tras restaurar | 404 · 404 `version_not_found` · 409 `analysis_running`, `chat_turn_running` |
| `GET /api/documents` | 200 (`[]` sin repositorio) | — |
| `POST /api/documents` (bytes, `application/octet-stream`, `X-File-Name`) | 201 | 400 `invalid_file_name`, `unsupported_type`, `content_mismatch`, `empty_file` · 403 `cross_site_request` · 409 `no_workspace`, `document_limit_reached`, `duplicate_document` · 413 `file_too_large` · 415 `invalid_request` |
| `GET /api/documents/{id}`, `GET …/raw`, `GET …/text` | 200: detalle con secciones, original o `content.md` | 404 |
| `PATCH /api/documents/{id}` `{ title?, useInAnalysis? }` | 200 | 400 `invalid_request` · 404 |
| `DELETE /api/documents/{id}` | 204 | 404 · 409 si está en el grafo (fila 2 de la matriz) |
| `POST /api/documents/{id}/describe` `{}` | 200 | 400 `not_an_image` · 404 · 409 `image_description_unavailable`, `knowledge_operation_running` · 502 `llm_error` |
| `GET /api/knowledge` | 200: fuentes en el grafo y resumen de embeddings | — |
| `POST /api/knowledge/graph` `{ source: { kind, id } }` | 200: estado de la fuente | 400 · 404 · 409 `no_workspace`, `no_graph`, `analysis_running`, `chat_turn_running`, `knowledge_operation_running` |
| `DELETE /api/knowledge/graph/{kind}/{id}` | 200 | 400 · 404 · 409 como el anterior salvo `no_graph` |
| `POST /api/knowledge/embeddings` `{ source: { kind, id } }` | 200 | 404 · 409 `embeddings_disabled`, `analysis_running` (solo catálogo), `knowledge_operation_running` · 502 `embedding_provider_error` |
| `DELETE /api/knowledge/embeddings/{kind}/{id}` | 200 | 404 · 409 `knowledge_operation_running` |

`kind` es `document`, `report` o `custom`. Quitar del grafo o de los embeddings una fuente cuyo fichero ya no existe responde 200 y limpia los restos. El 413 lleva `{error, message}` por los tres caminos que lo producen: un `Content-Length` mayor que 25 MiB, el máximo de la familia al leer el cuerpo y el corte de Kestrel por el límite de la ruta, que es el único que actúa sobre un cuerpo en chunked. El resto de rutas nuevas con cuerpo admiten 1 MB.

Además, en cualquier ruta, un cuerpo que no es JSON válido da 400 `invalid_request`, un `Content-Type` que no es JSON da 415 `invalid_request` y una ruta `/api` desconocida da 404 `not_found`. `reports` vacío o ausente equivale a no pasar `--reports` a la CLI; a diferencia de ella, que avisa y descarta un nombre desconocido, la API responde 400. Solo se listan y sirven los diez nombres del catálogo, porque la carpeta de informes puede contener Markdown ajeno.

El SSE del análisis emite `event: analysis` con `id: <seq>`, monótono y sin huecos desde 1, y termina tras `run.completed`. Si después se regenera un informe, el log de la ejecución se reabre y sigue con el `seq` siguiente, empezando por `run.regeneration_started`; el cliente se vuelve a suscribir con `after=<último seq>`, y un replay desde 0 trae las dos partes con sus dos `run.completed`.Reproduce los eventos con `seq` mayor que `after` o, si falta, que la cabecera `Last-Event-ID`, y después sigue en vivo. El del chat es la propia respuesta del `POST .../messages`, emite `event: chat` sin `id` (un turno cortado no se reanuda) y cierra con `turn.completed` o `turn.failed`. Los dos envían `: keep-alive` periódicamente.

## 9. Historial de chat

Cada sesión es un JSON en `<carpeta de DatabasePath>/chat/<id>.json`, que con la configuración por defecto es `<repositorio>/.codebase/chat/`. La ubicación cumple tres condiciones: sobrevive a `--force-reindex`, que solo mueve a `backups/` la BD, las exportaciones de Office y los informes; queda fuera del grafo, porque el pipeline retira los nodos bajo la carpeta de la BD; y no se mezcla con `reports/`, cuyo listado la trataría como salida del pipeline. El id tiene la forma `yyyyMMdd-HHmmss-xxxx`, se valida contra `^[a-z0-9-]{1,64}$` y la ruta resultante tiene que quedar dentro de la carpeta. La escritura es atómica, a un temporal y después `File.Move`.

Cada turno guarda pregunta, respuesta, estado, modelo, tokens, coste, duración, motivo de fin, las herramientas llamadas con sus argumentos truncados a 2000 caracteres, pero no sus resultados, los informes que escribió (`artifacts`, apartado 5) y, si se completó, su auditoría (`audit`: las afirmaciones con su estado y las referencias con el suyo, apartado 4). Solo los turnos `completed` vuelven al modelo como contexto, como pares pregunta y respuesta; los `failed` y `cancelled` se guardan para la interfaz.

## 10. Seguridad

La web no tiene autenticación y, a través de las herramientas, lee el disco del repositorio y gasta la clave del LLM, así que se limita a la máquina local. El arranque aborta si `Urls`, `ASPNETCORE_URLS`, `--urls` o algún `Kestrel:Endpoints:*:Url` tienen una dirección que no es loopback, o si solo hay `HTTP_PORTS`/`HTTPS_PORTS`, que escuchan en todas las interfaces. `AllowedHosts` es `localhost;127.0.0.1;[::1]`, lo que rechaza con 400 una petición con otro `Host` y cierra el paso al DNS rebinding. No hay CORS, y como todo POST y PUT exige JSON, una petición desde otro origen necesita un preflight que no se concede; la subida tampoco es una petición simple, porque exige `application/octet-stream` y la cabecera `X-File-Name`, y no admite `multipart/form-data`. Además, cualquier petición `/api` que no sea `GET` o `HEAD` y llegue con `Sec-Fetch-Site` distinto de `same-origin` o `none` recibe 403 `cross_site_request`.

`index.html`, tanto en `/` como en cualquier ruta que caiga en el fallback de la SPA, se sirve con `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; frame-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'self'`, más `nosniff` y `Cache-Control: no-cache`. `'unsafe-inline'` en estilos es lo que necesitan los SVG de Mermaid, `frame-src 'self'` deja cargar los visores de PDF y HTML sobre `/raw` y los assets compilados no usan `eval`. Ni los assets ni la API llevan esa cabecera, y el original de un HTML o un SVG lleva la suya con `sandbox` (apartado 6). El servidor de Vite sirve su propio `index.html` y solo reenvía `/api`, así que en desarrollo la política no se aplica.

## 11. Tests

Los tests de .NET no necesitan proveedor LLM ni indexador: usan un servicio LLM falso que hace streaming con llamadas a herramientas y, en la web, un workflow falso que emite eventos del framework y escribe informes. El proyecto de la web desactiva el paralelismo, porque el registro de herramientas y el sandbox de FileTools son estáticos, y fija `CodebaseAnalysis:Web:SettingsPath` a un fichero temporal para no tocar los ajustes reales.

```powershell
dotnet test Samples/CodebaseAnalysis.Core.Tests/CodebaseAnalysis.Core.Tests.csproj
dotnet test Samples/CodebaseAnalysisWeb.Tests/CodebaseAnalysisWeb.Tests.csproj
# o la solución entera
dotnet test AITaskAgent.slnx
```

Los tests del cliente usan Vitest con Testing Library y jsdom. `npm test` arranca Vitest en modo observación; para una sola pasada se añade `--run`:

```powershell
cd Samples/CodebaseAnalysisWeb/ClientApp
npm ci
npm run typecheck
npm test -- --run
```

## 12. Limitaciones conocidas

- **Un repositorio por proceso:** la raíz de FileTools y el registro de herramientas (`AvailableTools`) son globales. Cambiar de repositorio los reasigna, y por eso se bloquea mientras corre algo.
- **Salida de OfficeTools:** la ingesta de Access y Word escribe en la consola y no por el canal de eventos, así que va a la consola del servidor y no aparece en el log de la interfaz. Los mensajes del pipeline antes y después de la ingesta sí llegan.
- **Ritmo del chat:** con Azure y gpt-5.4 los deltas llegan en ráfagas de 150-400 ms tras varios segundos de silencio. No lo introduce la web: la misma petición directa a Azure muestra el mismo patrón, que corresponde al razonamiento del modelo y al troceado del proveedor, y el servidor reenvía cada chunk en cuanto llega.
- **Eventos en memoria:** solo se guarda la última ejecución y se pierde al reiniciar el servidor. Por lo mismo, solo se puede regenerar un informe de esa ejecución.
- **Regeneración sobre el grafo de ahora:** un informe regenerado se basa en el grafo actual, que puede no ser el de la ejecución original, y un carril que terminó bien no tiene botón para regenerarlo.
- **Sin OCR:** un PDF escaneado queda `empty`, y una imagen solo tiene texto después de «Describir», cuyo coste no entra en los tokens del turno.
- **Imágenes remotas:** el Markdown de un documento o de un informe solo pinta imágenes `data:` o de `/api`; una remota se muestra como enlace, y en el HTML en sandbox no se carga.
- **Informe bloqueado por otro proceso:** mientras dura el bloqueo, el listado lo muestra con semáforo `Unknown`, porque no puede leer la línea `analysis-meta`, y el semáforo no se actualiza tras «Reintentar» hasta el siguiente refresco del listado. El detalle del aviso repite «No se puede leer el informe … ahora», y abrirlo por enlace directo llegó a producir tres 503.
- **Sin verificar:** la desactivación de «Seguir al final» del log al cambiar de pestaña, observada una vez, no se reprodujo, y se endureció el criterio sin poder confirmar la causa. El resto (cancelación con el indexador vivo, replay con `Last-Event-ID`, proxy de Vite) está verificado en la sección «Cierre de F4» del plan.
