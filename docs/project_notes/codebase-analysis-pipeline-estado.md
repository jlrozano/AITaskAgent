# CodebaseAnalysisPipeline y CodebaseAnalysisWeb: estado (2026-09-28)

> **Trabajo posterior del mismo día** (vista Asistente de tres paneles, chat acotado con marcas `[V]`/`[D]`/`[I]` auditadas,
> visor de código, streaming roto pendiente y el rediseño del enriquecimiento nativo C#/Java): ver
> `asistente-chat-y-enriquecimiento-estado.md`. Las cifras de tests de abajo son anteriores a ese trabajo.

Rama `consolidation`, **sin nada commiteado**. El pipeline vive en una librería, `Samples/CodebaseAnalysis.Core/`, que usan dos ejecutables: la CLI (`Samples/CodebaseAnalysisPipeline/`) y una aplicación web local (`Samples/CodebaseAnalysisWeb/`) con progreso en vivo, visor de informes y chat sobre el repositorio analizado. Sobre esa base está implementada la V2 de la web: el chat escribe informes propios y modifica los del análisis con versiones y «Deshacer», el usuario sube documentos de doce familias con visor y extracción de texto, y cualquier fuente se puede añadir al grafo y a unos embeddings con `text-embedding-3-small`. La V2 pasó su verificación de extremo a extremo contra Azure y después una auditoría cuyos catorce hallazgos están corregidos; el último, el tope de tools de los auditores, se rehízo el 2026-09-28 sin opciones nuevas en el framework (plan v2, «PA-3 revisada»), de modo que lo pendiente se reduce a agrupar los commits, a un tema abierto de OfficeTools y a lo que sigue sin verificar (último apartado). La descripción funcional está en `Samples/CodebaseAnalysisPipeline/README.md` y `Samples/CodebaseAnalysisWeb/README.md`; el diseño de la web está en `docs/project_notes/codebase-analysis-web-plan.md` (v1) y `docs/project_notes/codebase-analysis-web-plan-v2.md` (V2), cuyas secciones finales («Cierre de V2-E» y «Correcciones post-auditoría V2») mandan.

## Qué hay sin commitear

| Ruta | Estado git | Contenido |
|---|---|---|
| `Samples/CodebaseAnalysis.Core/` | `??` | Pipeline, configuración y roles, progreso por eventos, agente de chat, informes propios y versiones, documentos, grafo y embeddings, plantillas y `appsettings.analysis.json` |
| `Samples/CodebaseAnalysisPipeline/` | `??` | CLI (`Program.cs`, csproj) y README del pipeline |
| `Samples/CodebaseAnalysisWeb/` | `??` | Backend ASP.NET Core, SPA en `ClientApp/` y README |
| `Samples/CodebaseAnalysis.Core.Tests/`, `Samples/CodebaseAnalysisWeb.Tests/` | `??` | Tests de la librería y de la web |
| `OfficeTools/`, `OfficeTools.Tests/` | `??` | Ingesta de Access y Word en el grafo |
| `Framework/LLM/Conversation/MessageHistory.cs` | `MM` | Ventana deslizante segura con grupos de tools |
| `Framework/LLM/Conversation/Context/ConversationContext.cs` | `MM` | `UseSlidingWindow=false` envía el historial completo |
| `Framework/LLM/Steps/BaseLlmStep.cs`, `BaseLlmStep.LlmInvocation.cs` | ` M` | Entre otros cambios anteriores de la rama, los errores tipados `LlmProviderException` y `ToolIterationLimitExceededException` y que `SwitchToNextFallback` solo cambie de proveedor ante el primero (plan v2, PA-3 revisada) |
| `Framework/LLM/Models/LlmProviderException.cs`, `ToolIterationLimitExceededException.cs`, `Tests/LlmFallbackClassificationTests.cs` | `??` | Las dos excepciones y sus 5 tests |
| `Tests/MessageHistorySlidingWindowTests.cs` | `??` | 10 tests de la ventana |
| `docs/guides/configuration.md` | ` M` | Semántica nueva de la ventana |
| `AITaskAgent.slnx` | ` M` | OfficeTools, OfficeTools.Tests y los cinco proyectos del sample |
| `.gitignore` | ` M` | Solo la línea `Samples/CodebaseAnalysisWeb/wwwroot/` |
| `docs/project_notes/codebase-analysis-web-plan.md`, `codebase-analysis-web-plan-v2.md` y este documento | `??` | Planes de la web y estado |

Los dos ficheros `MM` del framework tienen además cambios staged de antes de este trabajo, y los de `BaseLlmStep` mezclan la clasificación de errores con cambios previos de la rama, así que hay que revisarlos con `git diff` y `git diff --cached` antes de agrupar commits. En `docs/project_notes/` hay otros dos ficheros sin seguimiento (`sample-configuration-audit.md`, `tag-pipeline-analysis.md`) que no son de este trabajo, igual que el resto de modificados de `git status` (FileTools, Gemini, OpenAI, McpIntegration, otros samples). En la raíz hay una carpeta `.codebase/` sin seguimiento, con un `codebase.db` de 17 MB, cuyo origen no está establecido y que no debe entrar en ningún commit, mientras que `ClientApp/node_modules/` y `wwwroot/` ya están ignorados.

## Cómo verificar

Los tests de .NET no necesitan proveedor LLM ni indexador y se ejecutan con `dotnet test AITaskAgent.slnx`, que tras las correcciones post-auditoría da **605 sin fallos**: Core 250, Web 91, Framework 109, FileTools 54, OpenAI 40, OfficeTools 29, MCP 20 y Gemini 12, con el build de la solución sin avisos. Uno de los de la web arranca Kestrel real en un puerto efímero para probar el 413 de un cuerpo en chunked. Los 3 tests de integración de Office se omiten salvo que `OFFICETOOLS_SAMPLE_DIR` apunte a una carpeta con un `.accdb` y documentos `.docx`. Los del cliente se ejecutan en `Samples/CodebaseAnalysisWeb/ClientApp` con `npm ci`, `npm run typecheck`, `npm test -- --run` (**153 tests en 20 ficheros**) y `npm run build`.

El caso principal es Access/VBA, en `C:\Users\juan.rozano\Desktop\TestAgenteAnalisis`:

```powershell
dotnet build Samples/CodebaseAnalysisPipeline/Samples.CodebaseAnalysisPipeline.csproj
dotnet run --project Samples/CodebaseAnalysisPipeline/Samples.CodebaseAnalysisPipeline.csproj --no-build -- `
    --repo "C:\Users\juan.rozano\Desktop\TestAgenteAnalisis" --force-reindex true
```

La salida se juzga con sus `reports/` y `logs/`, contrastados con `.codebase/access_export`, nunca con restos de la carpeta del sample. En la última ejecución completa salieron 262 nodos (261 de Office más el nodo `Project`), los 9 informes y el `00`, con código de salida 0, y los candidatos deterministas a código muerto fueron `Consulta2`, `Errores de pegado` y `MFiles1`; esas cifras son anteriores a la extracción y no se han repetido con los nueve informes. Tras las correcciones post-auditoría de v1 se ejecutó dos veces con `--reports errores`: las dos clasifican la contraseña de `MMapNetWorkDrive.bas:6` como P0 (`SEC-001`) sin reproducir su valor, `critical` cuadra con la matriz y el `00` tiene un solo H1.

El segundo repositorio, para comprobar que el pipeline es genérico, es una copia del propio sample fuera del árbol, porque el pipeline escribe `.codebase/`, `reports/` y `logs/` en el repositorio analizado. El pipeline no está en `Samples/CodebaseAnalysisPipeline/`, que solo contiene la CLI, así que hay que copiar también `Samples/CodebaseAnalysis.Core/`. No se usan repositorios ajenos, como `poc_notificaciones`. Desde la raíz del repositorio, en Git Bash:

```bash
dest="$TMP/repo_sample"
for p in CodebaseAnalysisPipeline CodebaseAnalysis.Core; do
  mkdir -p "$dest/$p"
  tar -C "Samples/$p" --exclude=bin --exclude=obj --exclude=.codebase --exclude=reports --exclude=logs -cf - . \
    | tar -C "$dest/$p" -xf -
done
```

Lo esperado es el ecosistema «C# (.NET)», enriquecimiento por LLM sin errores (la copia no tiene `.sln`) y 10 informes sin menciones a VBA/Access salvo las que describen la ingesta Office del propio código, sin menciones a `UserSecretsId` ni a su GUID. En una segunda pasada con `--force-reindex`, el log debe mostrar «Retirados del grafo N nodos de la salida del propio pipeline» y, si la web marcó fuentes para el grafo, los mensajes «🧩 Reincorporados al grafo …» de la detección y de la síntesis. La última ejecución real sobre esa copia (2026-09-28) fue `--reports flujos` con `--CodebaseAnalysis:Auditors:MaxToolIterationsByRole:FlowsAuditor 2`, para forzar el camino de PA-3 revisada: el log muestra «agotó su tope de herramientas tras 6,8s: se redacta con lo consultado» y «Redactando … sin más herramientas», el `04` sale completo (21,6 KB, `health=Amber`), sin cambio de proveedor, y el proceso termina con código 0.

La web se verifica con la build publicada, sobre la misma copia:

```powershell
dotnet publish Samples/CodebaseAnalysisWeb/CodebaseAnalysisWeb.csproj -c Release -o $env:TEMP\cawpub
& "$env:TEMP\cawpub\CodebaseAnalysisWeb.exe"
```

En `http://127.0.0.1:5180` se abre la copia como repositorio, se lanza un análisis y se prueba el chat. La verificación de v1 (plan v1, «Cierre de F4») cubrió análisis, replay, cancelación, chat con persistencia y proxy de Vite; la de V2 (plan v2, «Cierre de V2-E») cubrió, contra Azure y con Chrome headless, la subida de todas las familias, los visores de PDF y HTML con sus cabeceras, «Describir», grafo y embeddings desde el visor, `create_report`, `update_report` con «Deshacer», `semantic_search`, un análisis con «Forzar reindexación» que reincorpora las fuentes marcadas, y la ausencia de scroll de página con 51 documentos y 41 informes. Tras la auditoría se comprobó además, con la build de Release y Chrome headless, que la CSP de `index.html` no produce ninguna violación con Mermaid, el visor de PDF y el HTML en sandbox.

## Decisiones tomadas

Las decisiones del pipeline siguen vigentes, empezando por que `MaxOutputTokens` vale 65536 en todos los roles de análisis, porque gpt-5.4 descuenta de ese límite también el razonamiento y Azure rechaza más de 128000, y el del chat vale 32768. La ventana deslizante está activa con `SlidingWindowMaxTokens` = 262144: conserva siempre los primeros mensajes, los `tool` que les siguen y el último grupo, nunca envía un `tool` huérfano, y con `UseSlidingWindow=false` envía el historial completo en lugar de recortar a 8192 tokens como hacía antes. Un auditor fallido no para la síntesis, que lo declara, y el proceso sale con código 1; si fallan todos, el pipeline se detiene. Cada rol usa el proveedor que nombra, la conexión MCP es opcional (`ConnectMcp: false`) y el grafo usa el `project` que informa el indexador, con la salida del propio pipeline retirada tras indexar.

La extracción, la web y la V2 añadieron estas:

- **Errores tipados en `Framework/`:** `BaseLlmStep` envuelve todo fallo del proveedor, devuelto o lanzado, en `LlmProviderException`, y el tope de tools agotado en `ToolIterationLimitExceededException`. Su `SwitchToNextFallback` solo cambia de motor ante el primero; antes cambiaba ante cualquier error con excepción y repetía un bucle de tools en cada fallback. La estrategia de los auditores al agotar el tope (repetir sobre la misma conversación, sin herramientas, pidiendo el informe) vive en `CustomStatelessTemplateLlmStep` y no en el framework, porque un informe parcial es una decisión de dominio. El tope es de 12 iteraciones (16 para `FlowsAuditor`), configurable por rol en `CodebaseAnalysis:Auditors`. Sustituye a la opción `FinalAnswerOnToolLimit` de la primera versión de PA-3, que se retiró. El resto (streaming con tools, eventos, mensajes de dominio como `StepProgressEvent`) no necesitó tocar el framework.
- **Un repositorio activo por proceso y un análisis a la vez**, con 409 cruzados entre análisis, chat y operaciones de conocimiento según la matriz del apartado 4 del README de la web, porque el sandbox de FileTools y el registro de tools son estáticos y el análisis sobrescribe la BD y mueve los informes.
- **Todo lo nuevo en `<repositorio>/.codebase/`:** historial de chat, `report-history/`, `documents/` y `knowledge/` sobreviven a `--force-reindex` y quedan fuera del grafo, de la ingesta Markdown y de la de Office; los informes propios van en `reports/custom/`, que el pipeline no toca.
- **Escrituras del chat versionadas y limitadas**, cinco por turno contando `describe_image` sin pregunta, con «Deshacer» como restauración que crea a su vez una versión.
- **Fuentes del grafo apuntadas en un registro** y reincorporadas por el propio pipeline tras reindexar, también cuando el run no llega a la síntesis.
- **Estado de grafo y embeddings sin red**, comparando el SHA-256 del contenido, recordado por ruta, tamaño y fecha.
- **SSE escrito a mano sobre `fetch`**, con replay por `seq` y estado del análisis derivado en el cliente con un reductor puro.
- **Solo loopback**, `AllowedHosts` restringido, cuerpos JSON u `octet-stream` obligatorios, `CrossSiteGuard`, sin CORS y con CSP en `index.html`, porque la web no tiene autenticación.
- **Último repositorio en `%LOCALAPPDATA%\CodebaseAnalysisWeb\settings.json`**, porque recompilar o republicar vacía la carpeta del ejecutable.
- **Ctrl+C en la CLI:** el primero cancela el pipeline y sale con 130 conservando los informes escritos; el segundo termina el proceso.
- **Secretos y severidad:** la faceta `HardcodedSecrets` descarta identificadores cuyo último segmento es de referencia (`Id`, `Path`, `Url`…), el informe de errores sigue una rúbrica P0-P3 en la que un secreto real en claro es siempre P0, y `critical` se recalcula desde la matriz de severidad y se corrige si el modelo no cuadra.
- **LOC por rango de líneas** cuando el indexador no da `loc_total`, como en C#, que antes sumaba 0.

El porqué detallado de cada una, y las decisiones menores de implementación, están en las secciones finales de los dos planes.

## Límites conocidos

Los del pipeline están en el apartado 7 del README de la CLI: facetas vacías cuando el indexador no genera ciertos labels, uso de herramientas a criterio del modelo, cumplimiento no absoluto de las reglas de redacción, detección de secretos por patrón y LOC aproximadas en C#. Los de la web están en el apartado 12 de su README: un repositorio por proceso, la salida de OfficeTools solo en la consola del servidor, deltas del chat en ráfagas por el propio Azure, eventos del análisis solo en memoria, sin OCR y sin imágenes remotas en los visores.

## Pendiente

- **Commits:** decidir cómo agruparlos, separando lo de este trabajo del resto de cambios de la rama y revisando antes los ficheros `MM` y los de `BaseLlmStep`.
- **Tema abierto, sin corregir:** `OfficeTools/Steps/IngestOfficeArtifactsStep.cs:786-792` detecta defectos buscando nombres de un repositorio concreto (`nocout`, `sfFECALT_Fin`, `FECCON`), lo que va contra la regla de que el pipeline sea genérico. Hay que decidir si se quita, se generaliza o se deja como heurística del caso de prueba.
- **Sin verificar en real:** el intento final sin herramientas con los topes por defecto (12 y 16), que solo se observó forzando el tope a 2; que el `<script>` de un HTML en sandbox no llegue a la API, que se apoya en la CSP `sandbox` y en `sandbox=""` sin haberse visto en el log; el Ctrl+C real de la CLI; y la desactivación de «Seguir al final» en el log de la web, que no se reprodujo y se endureció sin confirmar la causa.
- **Cambio de comportamiento del framework a revisar al commitear:** `BaseLlmStep.SwitchToNextFallback` ya no cambia de proveedor ante errores que no sean `LlmProviderException` (plantilla, parseo, cancelación, tope de tools). Los 605 tests pasan, pero no se ha comprobado si algún otro sample dependía del comportamiento anterior.
- **Decisión a revisar:** borrar un documento que no está en el grafo cuenta como operación de conocimiento pero se permite con otra en curso (plan v2, PA-1), como dice la matriz; si se prefiere un 409, es cambiar la operación en `DocumentsEndpoints`.
- **Observaciones menores abiertas del smoke de v1**, todas con un informe bloqueado por otro proceso: el semáforo no se actualiza tras «Reintentar» hasta el siguiente refresco del listado; el detalle de `report_unavailable` repite «No se puede leer el informe … ahora»; y abrir el informe por enlace directo produjo tres 503.

## Prompt de arranque

```text
Retomamos el análisis de código en la rama consolidation: Samples/CodebaseAnalysis.Core (librería),
Samples/CodebaseAnalysisPipeline (CLI) y Samples/CodebaseAnalysisWeb (web local, con la V2: informes propios,
documentos, grafo y embeddings). Lee primero docs/project_notes/codebase-analysis-pipeline-estado.md y después los
README de la CLI y de la web; el diseño y las decisiones están en docs/project_notes/codebase-analysis-web-plan.md y
codebase-analysis-web-plan-v2.md, cuyas secciones finales mandan. No hay nada commiteado. Debe valer para cualquier
proyecto: nada del caso de prueba en prompts, heurísticas ni configuración. La calidad se juzga con los informes y el
log del repositorio analizado. Antes de tocar el framework, usa sus puntos de extensión (métodos virtuales,
SwitchToNextFallback, errores tipados). Empieza por <lo que toque: p. ej. revisar el diff y proponer cómo agrupar los
commits, o decidir qué hacer con los nombres concretos de IngestOfficeArtifactsStep>.
```
