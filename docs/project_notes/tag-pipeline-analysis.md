# Pipeline de tags: análisis, requisito de paridad de canales y bugs

Fecha: 2026-09-04 · Rama: `consolidation` · Estado del working tree: sin commitear, incluye cambios míos de esta sesión que este informe recomienda revertir en parte.

> **Nota de vigencia (2026-09-09):** las secciones 2 a 5 describen el diseño tal como estaba durante
> el análisis del 04-09, con los nombres de entonces: `IStreamingTagHandler`, `StreamingTagHandlerBase`,
> `BaseLlmStep.StreamingHandlers`. El 07-09 (sección 6) ese contrato se renombró: el vigente es
> `ITagHandler` / `TagHandlerBase` / `BaseLlmStep.TagHandlers` (`Framework/LLM/Streaming/ITagHandler.cs`,
> `TagHandlerBase.cs`; los dos ficheros viejos ya no existen). Se deja el cuerpo sin reescribir para no
> perder el rastro histórico del análisis; para el estado actual del contrato, ir a la sección 6.

## 0. Resumen

- El framework ya modela los dos canales (contenido y razonamiento) de forma uniforme para todos los proveedores: `LlmResponse.Content` / `LlmResponse.Reasoning` y `LlmStreamChunk.IsThinking`. La paridad de canales que pides es barata de implementar **técnicamente**: hay un único punto de decisión, `BaseLlmStep.InvokeLlmAsync`.
- Lo que no es barato es la **semántica**: el canal de razonamiento contiene borradores. Tres casos reales de esta semana lo prueban, uno de ellos destruyó un archivo de 10 KB con un esqueleto sintácticamente perfecto. Ninguna validación del tag lo distingue de una acción; sí lo distinguen señales de la respuesta completa (tool call concurrente, duplicado en contenido, orden).
- El problema estructural de fondo es que hoy hay **dos implementaciones distintas** de "detectar y ejecutar un tag" (una regex para batch, una máquina de estados para streaming) con reglas distintas de atributos, comillas y errores. La paridad stream/no-stream pasa por dejar una sola.
- Implementado (decisión del usuario): un único escáner para los dos transportes y los dos canales; cada tag se procesa completo al cerrar, venga de donde venga. El trade-off (los borradores del razonamiento también se ejecutan) queda registrado como D2.
- Aparte del requisito, hay 16 hallazgos en el framework (4 de severidad alta: fuga de `FileStream` con tag sin cerrar, excepción de handler que mata el turno en streaming, ventana deslizante ignorada por el servicio OpenAI, y la causa real de los 101 K tokens de prompt) y 4 en el sample.

## 1. Alcance y método

Leído completo: `Framework/LLM/Streaming/*`, `Framework/LLM/Steps/BaseLlmStep.cs` y `.LlmInvocation.cs`, modelos (`LlmResponse`, `LlmStreamChunk`, `LlmResult`, `LlmRequest`, `Message`), `ConversationContext` y `MessageHistory`, `RetryMiddleware`, `StepBase`, `TemplateLlmStep`/`StatelessTemplateLlmStep`/`YamlLlmStep`, `OpenAILlmService` y su carpeta `Normalization`, la parte de razonamiento y streaming de `GeminiLlmService`, `ToolCallXmlParser`, `LlmHelpers`, `FileTools/Streaming/WriteFileTagHandler.cs`, `WriteToFileTool.cs`, `Samples/FileToolsTestApp/Program.cs` y la documentación en `docs/` que menciona tags.

Contrastado con dos logs reales de `FileToolsTestApp` contra `nvidia/nemotron-3-super-120b-a12b:free` vía OpenRouter: la sesión de la mañana (`UseStreaming: false`, 13:47–13:57) y la de la tarde (`UseStreaming: true`, 15:23–15:26).

## 2. Cómo funciona hoy

### 2.1 Registro e instrucción al modelo

`BaseLlmStep.StreamingHandlers` (`BaseLlmStep.cs:92`) es la lista de `IStreamingTagHandler`. En `BuildLlmRequestAsync` (`:310-313`) se inyecta al system prompt el bloque `## IN-TEXT STREAMING ACTIONS (NOT TOOLS)` con el `GetInstructions()` de cada handler. Es el único mecanismo por el que el modelo sabe que los tags existen. El único handler real del repositorio es `WriteFileTagHandler`; el único consumidor es `FileToolsTestApp`.

### 2.2 Los dos canales llegan ya normalizados

| Proveedor | Cómo separa razonamiento de contenido | Dónde acaba |
|---|---|---|
| OpenAI-compatible, campo nativo (`reasoning` / `reasoning_content`) | `OpenAILlmService.ProcessChoice:343` (batch) y `:504-517` (stream) | `Reasoning` / `IsThinking = true` |
| OpenAI-compatible, `<think>…</think>` dentro de `content` | `ThinkTagReasoningExtractor` vía `OpenAiPluginRegistry` (`:357`, `:537`) | ídem |
| Gemini, `Part.Thought == true` | `ExtractReasoningContent:652`, `ProcessStreamChunk:728` | ídem |

Consecuencia: el framework no necesita saber de dónde vino el razonamiento. `BaseLlmStep` recibe siempre `Content` + `Reasoning` (batch) o deltas con `IsThinking` (stream). Ese es el punto de apoyo para la paridad.

### 2.3 Dos rutas de ejecución, dos implementaciones

**Batch** — `InvokeLlmAsync:428-461` → `ProcessTagsNonStreamingAsync:776-835`. Regex `<(\w+)([^>]*)>(.*?)</\1>` sobre el texto completo, `ParseAttributes` con `(\w+)\s*=\s*["']([^"']*)["']`, invoca `OnCompleteTagAsync`, sustituye el tag por el placeholder, captura excepciones y las convierte en texto `[Error processing tag …]`.

**Streaming** — `InvokeLlmAsync:463-640` → `StreamingTagParser.ProcessChunkAsync`. Máquina de estados carácter a carácter (`Normal → PotentialTag → InTag → PotentialClose`), atributos con `(\w+)="([^"]*)"`, invoca `OnTagStartAsync` / `OnContentAsync` / `OnTagEndAsync` según llegan los deltas. Solo se alimenta con deltas `!IsThinking` (`:502-508`); los deltas de razonamiento van directos a `reasoningBuilders` (`:488-500`). No captura excepciones. No tiene método de cierre.

Las dos rutas divergen en: qué comillas aceptan, si el fallo de un tag mata el turno o produce un placeholder, si un tag sin cerrar se recupera, y qué pasa con el canal de razonamiento.

### 2.4 Matriz de estado

Columna = modo de transporte; fila = canal donde el modelo escribe el tag. "Antes" es el código anterior a esta sesión; "ahora" es el diseño implementado (sección 4), sin commitear.

| | Batch (`UseStreaming: false`) | Streaming (`UseStreaming: true`) |
|---|---|---|
| Tag en **contenido** | Antes: ejecuta (regex); error → texto de error. **Ahora**: escáner único; error → placeholder de error en el sitio + `TagCompletedEvent(Success=false)`. | Antes: ejecuta en vivo (parser); error → excepción, turno muerto; tag sin cerrar → `FileStream` abierto. **Ahora**: mismo escáner; el tag se procesa completo al cerrar; error → placeholder; tag sin cerrar → texto plano, nada abierto. |
| Tag en **razonamiento** | Antes: ignorado. **Ahora**: mismo escáner, mismo resultado que en contenido; la referencia se añade al contenido para el historial. | Antes: ignorado. **Ahora**: ídem; el placeholder se emite en el evento thinking y la referencia se añade al contenido. |

### 2.5 Qué se persiste en la conversación

Dentro del turno, `InvokeLlmWithToolsAsync:141` añade `assistant` (+ tool calls) y `ExecuteToolCallsAsync` añade los `tool`. Al final, `BaseLlmStep.ExecuteAsync:269` añade el `assistant` final con el contenido ya procesado (placeholders en lugar de cuerpos de tag).

Cuando el step corre **dentro del pipeline**, `RetryMiddleware` llama a `FinalizeAsync` (`RetryMiddleware.cs:64,80,151,164`) y `BaseLlmStep.FinalizeAsync:147-165` restaura el bookmark y reescribe la cola como un único par `[user, assistant(final)]`. Todo el tráfico intermedio de tools **desaparece** de la conversación persistida. Una referencia de tag que viviera en un `assistant` intermedio se pierde también.

Cuando el step se invoca **directo** (`FileToolsTestApp/Program.cs:381`), `FinalizeAsync` no corre nunca. La conversación acumula sin límite todo lo intermedio, turno tras turno. Es la causa de los 101 778 tokens de prompt de la sesión de la mañana, no el modelo.

### 2.6 Interacción con tools

- `ExecuteToolCallsAsync:322-359`: si el modelo llama como tool un nombre que es un tag registrado, se le responde con `GetInstructions()` (original) o con "already ran" si el tag se ejecutó en esa misma respuesta (mío).
- `IsStuckRepeatingToolCalls:197-238`: corta el bucle cuando el mismo conjunto de llamadas se repite; umbral 1 para mutantes (incluye `write_file`, que al no ser tool no es `IsReadOnly`).
- `XmlToolCallNormalizer` (servicio OpenAI) convierte a tool call cualquier par XML cuyo nombre coincida con un tool disponible **antes** de que `BaseLlmStep` vea el contenido.

## 3. El requisito: tags iguales en `think` y `content`, en stream y no stream

### 3.1 Lo que sí es cierto

La unificación técnica es pequeña. Los dos canales llegan normalizados (2.2); el sitio donde decidir es uno (`InvokeLlmAsync`); las dos implementaciones de escaneo se pueden fusionar en una porque el batch es un caso particular del streaming (un único delta).

### 3.2 Lo que bloquea: el razonamiento contiene borradores

Tres casos reales, los tres con el mismo modelo:

1. **Sesión mañana, iteración 12** (batch, log `13:xx`, líneas 12976-13010 del log de entonces): razonamiento con el tag `<write_file path="analisis.md" summary="…">` y 40 líneas de contenido real; `Content` vacío; además una tool call `write_file`. Un tag completo y legítimo en el canal equivocado.
2. **Sesión tarde, turno 1** (streaming, log línea 7395): razonamiento con `<write_file path="analysis.md" summary="Informe de análisis de código">\n[content]\n</write_file>`; `Content` vacío; además tool call `write_file`. Sintácticamente perfecto, cuerpo = la palabra `[content]`. Mi código lo ejecutó: el informe de 10 KB pasó a tener 14 bytes.
3. **Sesión tarde, turno 2** (streaming, líneas 9162-9213): razonamiento con `<write` … sin `path`; `Content` vacío. Mi código lo ejecutó, el handler lanzó, y el texto `[Error processing tag …]` sustituyó la respuesta.

Del caso 2 se sigue la conclusión dura: **validar el tag no basta**. Tenía `path` y `summary` correctos. Lo que era borrador era el cuerpo, y no hay criterio sintáctico para eso.

### 3.3 Lo que sí distingue un borrador

No el tag en sí, sino la **respuesta completa** en la que aparece:

- En los casos 1 y 2 la respuesta traía además una **tool call con el mismo nombre**. El modelo no se decidió entre los dos mecanismos. Eso es una señal de conflicto, no de acción.
- Si el mismo tag (misma identidad: nombre + atributo clave, `path`) aparece también en el contenido, el del contenido es el definitivo.
- Si aparece varias veces en el razonamiento, la última es la candidata; las anteriores son iteraciones.
- El caso 3 lo descarta una validación de atributos requeridos, que sí es sintáctica.

### 3.4 Lo que ningún diseño arregla

Esqueleto en el razonamiento + tool call + ningún tag real en ninguna parte (caso 2 exacto). La única respuesta correcta es **no ejecutar nada** y devolver al tool call las instrucciones del tag. Es lo que hacía el código original. Hay que decirlo: para ese caso, la paridad de canales no aporta; el modelo no emitió la acción.

## 4. Diseño implementado: un escáner, la variable es el transporte

Decisión del usuario (2026-09-04): no se distingue razonamiento de contenido. Es un texto en el que hay que detectar tags y procesarlas. Lo que sí cambia es el transporte: en batch se recibe el texto completo; en streaming, al abrir un tag se deja de emitir, se acumula hasta el cierre, se procesa y se sigue emitiendo. (El diseño anterior de este informe —dos políticas por canal y reglas de commit al cierre de la respuesta— queda descartado; la objeción que lo motivaba se registra como D2 en la sección 5.)

### 4.1 Un único componente de escaneo — `StreamingTagParser`

Se conserva el nombre de clase (API pública) pero es el único escáner para los dos transportes:

- `ProcessChunkAsync(delta)` devuelve el texto purgado: fuera de tag pasa tal cual; al detectar la apertura de un tag registrado deja de devolver texto y acumula el cuerpo; al cierre invoca `OnCompleteTagAsync(attrs, body)` **una vez, con el tag completo**, y devuelve el placeholder en su sitio.
- `FlushAsync()` al final del texto: si quedó un tag abierto, su texto vuelve como texto plano y el handler no se ejecuta. Como nada se abre al ver la apertura, no queda ningún recurso colgado.
- Excepción del handler: capturada; placeholder `[Error processing tag X: msg]` en el sitio, log de error, y el turno sigue. El `TagCompletedEvent(Success=false)` lo emite `StreamingTagHandlerBase.OnCompleteTagAsync`.
- Una gramática de atributos: `(\w+)="([^"]*)"` (solo comillas dobles, como documenta `GetInstructions()`). Dentro de un valor entre comillas cualquier carácter es válido (puntos, barras, espacios).
- Expone `ExecutedTags` (para "already ran") y `CompletedTagReferences` (placeholders de tags ejecutados con éxito, en orden).
- Los métodos incrementales de `IStreamingTagHandler` (`OnTagStart/Content/End`) quedan sin uso por el framework. La interfaz no se toca (contrato público); su limpieza es trabajo aparte.

### 4.2 Batch (`InvokeLlmAsync`, rama `!UseStreaming`)

Un escáner por candidato. `ProcessChunkAsync(Reasoning)` + `FlushAsync()` → razonamiento purgado; `ProcessChunkAsync(Content)` + `FlushAsync()` → contenido purgado. Los eventos thinking/content se emiten con los textos purgados. Las referencias producidas mientras se escaneaba el razonamiento se añaden al contenido: el razonamiento no se persiste, la referencia sí. `_tagsExecutedInLastResponse` se alimenta de `ExecutedTags`.

### 4.3 Streaming (`InvokeLlmAsync`, rama `UseStreaming`)

Un escáner **por choice** (antes había uno compartido para todos). Cada chunk con `Delta` pasa por el escáner en orden de llegada, da igual `IsThinking`; lo devuelto se emite con el `IsThinking` del chunk y se acumula en el builder de su canal. Dentro de un tag no se emite nada; el placeholder sale donde el tag cerró. Un tag no puede cruzar de canal: cuando `IsThinking` cambia entre chunks, se hace `FlushAsync()` y lo pendiente sale como texto del canal anterior. Al terminar el stream, `FlushAsync()` por choice. Las referencias producidas durante chunks thinking se añaden al contenido final.

### 4.4 Historial

Da igual el canal ni el transporte: al historial va el contenido purgado (placeholder en lugar del cuerpo) más las referencias de los tags que estaban en el razonamiento. El razonamiento en sí no se persiste. Sigue pendiente (D1): la referencia de un tag ejecutado en una iteración intermedia se pierde al pasar por `FinalizeAsync`.

### 4.5 Qué se eliminó y qué se mantuvo

- Eliminado: `ProcessTagsNonStreamingAsync`, `_tagRegex`/`_attrRegex`, `ParseAttributes`, `AppendReasoningTagReferencesAsync`, `keepSurroundingText`, y el warning "Executed N tag(s) found in the reasoning channel".
- Mantenido: `AddAssistantMessageWithToolCalls(toolCalls, content)`, la rama "already ran" en `ExecuteToolCallsAsync`, `IsStuckRepeatingToolCalls`, el placeholder con path/tamaño e `InvariantCulture`.
- Tests: `Tests/ReasoningChannelTagTests.cs` (6) sigue válido —su semántica coincide con la decisión: el tag del razonamiento se ejecuta— con el resumen actualizado; nuevo `FileTools.Tests/TagScannerParityTests.cs` (10): misma respuesta por batch y streaming → mismos archivos, mismo contenido, mismas referencias; tag partido en chunks de 1/3/11 caracteres (incluido dentro del `path` con punto y dentro del cierre); tag sin cerrar → texto plano, ningún archivo, ningún `TagStartedEvent`; handler que lanza → placeholder de error, el turno no muere, `TagCompletedEvent(Success=false)`; markup ajeno dentro del cuerpo pertenece al archivo. Suite completa: 181 tests en verde.

## 5. Bugs y mejoras (independientes del requisito)

Severidad: **A** = pérdida de datos, recurso colgado o turno muerto · **M** = comportamiento incorrecto o inconsistente · **B** = calidad/documentación.

### Framework

| # | Sev | Dónde | Qué |
|---|---|---|---|
| B1 | A · **resuelto** | `StreamingTagParser` (sin método de cierre); `LlmInvocation.cs` (nada tras el bucle) | Si el stream terminaba con un tag abierto, `_activeContext` no se disponía: el `FileStream` de `WriteFileContext` quedaba abierto (archivo bloqueado en Windows), sin placeholder ni `TagCompletedEvent`. Resuelto por 4.1: el handler solo se invoca con el tag completo, nada se abre en la apertura, y `FlushAsync()` devuelve el texto pendiente. Test: `A_tag_left_open_comes_back_as_plain_text_and_creates_no_file`. |
| B2 | A · **resuelto** | `LlmInvocation.cs` (antes `:507`); `StreamingTagHandlerBase.OnTagStartAsync:26-56` | En streaming, una excepción del handler salía de `ProcessChunkAsync` sin captura → `RetryMiddleware` la trataba como fatal → turno muerto. Resuelto por 4.1: captura en el escáner, placeholder de error en el sitio. Test: `A_handler_failure_leaves_an_error_placeholder_and_the_turn_survives`. Queda abierto el detalle de `OnTagStartAsync` (evento started sin completed si `Internal…` lanza), que ya no está en el camino del framework. |
| B3 | A | `OpenAILlmService.BuildRequestBody:143` vs `GeminiLlmService.BuildContents:394` | El servicio OpenAI envía `History.Messages` completo e ignora `UseSlidingWindow`/`SlidingWindowMaxTokens`; Gemini sí los aplica. El log de trace (`LlmInvocation.cs:411`) usa la ventana, así que **el log no muestra lo que se envía**. Nota: aplicar la ventana ingenua (`keepFirstN: 2` + recientes por tokens) a una secuencia `assistant(tool_calls)` / `tool` puede cortar el par y provocar 400 en OpenAI. |
| B4 | A | `Samples/FileToolsTestApp/Program.cs:381`; `BaseLlmStep.FinalizeAsync` | Invocar `step.ExecuteAsync` directo salta `FinalizeAsync`: la conversación nunca se compacta a `[user, assistant]`, acumula todo el tráfico de tools entre turnos y `_conversation`/`_request`/`_initialBookmark` nunca se limpian. Origen de los 101 K tokens de prompt. El framework debería o bien exponer una forma de finalizar fuera del pipeline, o bien no depender del middleware para ello. |
| B5 | M | `LlmInvocation.cs:182-185` | `_lastToolCallKeys` y `_consecutiveIdenticalToolCalls` no se resetean nunca (ni en `FinalizeAsync`): el contador de repeticiones cruza turnos. |
| B6 | M | `IsStuckRepeatingToolCalls:217`; `docs/guides/tools-advanced.md:306-308` | Docs: "Iteration 2 → loop cut immediately". Código: `<= threshold` permite una repetición y corta en la tercera llamada idéntica. Uno de los dos está mal. |
| B7 | M · **resuelto** | antes `LlmInvocation.cs` (regex) vs `StreamingTagParser.cs` | Batch usaba `["']([^"']*)["']` (rompía con apóstrofe, aceptaba comillas simples), `[^>]*` (rompía con `>` en atributo) y `(.*?)</\1>`; streaming solo comillas dobles. Dos gramáticas para el mismo tag. Resuelto por 4.1: la regex de batch desaparece; una sola gramática. |
| B8 | M | Ambas rutas | El cuerpo conserva el salto de línea tras `>`: todo archivo escrito empieza con una línea vacía (observado en `analysis.md`). |
| B9 | M · **resuelto en la práctica** | `WriteFileTagHandler.cs:82` | Streaming abría `FileMode.Create` al ver la apertura del tag: truncaba el destino antes de recibir un byte. Con 4.1 el framework ya no invoca `OnTagStartAsync`; el archivo se escribe de una vez en `OnCompleteTagAsync`. El código de `InternalOnTagStartAsync` sigue existiendo pero no está en el camino; eliminarlo es parte de la limpieza del contrato. |
| B10 | M | `WriteFileTagHandler` (ctor `baseDirectory`) | Ignora `FileToolsConfiguration.RootDirectory` y `AllowedDirectories`: `/setMainPath` no afecta al tag y el sandbox de directorios permitidos no se aplica a las escrituras por tag. |
| B11 | M | `XmlToolCallNormalizer.Normalize:20-25` | Tags y tools comparten espacio de nombres a nivel de proveedor: un tag cuyo nombre coincida con un tool (o difiera solo en `-`/`_`) se convierte en tool call y se borra del contenido antes de llegar al parser de tags. |
| B12 | B | `OpenAILlmService.LooksLikeEmbeddedToolCall:723` | Contenido que empieza por `[` o `{` se retiene hasta `finish_reason`: una respuesta que arranque con "[Nota]" pierde el streaming. |
| B13 | B | `ExecuteToolCallsAsync:313` vs `IsStuckRepeatingToolCalls:213` | Búsqueda de tool por nombre case-sensitive en un sitio, `OrdinalIgnoreCase` en el otro. |
| B14 | B | `StreamingTagHandlerBase:36,134` | `TagStartedEvent.StepName` es `"StreamingTag"`/`"BatchTag"`, no el step real: los eventos de tag no se pueden correlacionar con el step que los produjo. |
| B15 | B · **resuelto (2026-09-09)** | `docs/api-reference/observability-namespace.md` (sección "Artifact Generation Events") | Documentaba un bloque de instrucciones y un placeholder `[File: path]` que ya no coincidían con `GetInstructions()` ni con el placeholder actual. Corregido: ejemplo alineado con el texto real de `WriteFileTagHandler.GetInstructions()` y el placeholder `[write_file: path (size) — summary]`. |
| B16 | B | `BaseLlmStep.EnrichSystemPromptWithStreamingTags:538` | `GetInstructions()` completo va en cada request; en bucles de tools largos se multiplica. Candidato a resumir tras la primera iteración. |
| B17 | M | `EventChannel.Subscribe:141-149` vs `DisposeAsync:206-231` | **Resuelto.** `HandlerSubscription.Dispose()` quitaba el consumer task de `_backgroundLoops`, así que `DisposeAsync` no lo esperaba: un suscriptor que cerraba su suscripción y luego el canal podía perder los últimos eventos en buffer. Lo destapó `TagScannerParityTests` como flaky (1 fallo en la suite completa, 3/3 verde aislado). Fix: la tarea se queda en la lista; el writer se completa igual. |

### Sample `FileToolsTestApp`

| # | Dónde | Qué |
|---|---|---|
| S1 | `Program.cs:118-119` | `AddSingleton<ILlmService, OpenAILlmService>()` + `AddSingleton<OpenAILlmService>()` = dos instancias, dos `HttpClient`. |
| S2 | `Program.cs:159-160` | El listener filtra `&& !resp.IsThinking`: el razonamiento no se imprime nunca. Añadir un case. |
| S3 | `Program.cs:256` | `MaxToolIterations = 500`. |
| S4 | `Program.cs:48-51`, `:38-41` | `catch {}` al borrar logs; detección de `projectDir` por `Contains("bin")`. Ya señalados por el informe del modelo; correctos. |

### Decisiones de diseño registradas (no son bugs)

D1. `FinalizeAsync` descarta el tráfico intermedio de tools al cerrar el turno. Es intencional ("canonical [user / assistant] pair") pero implica que, entre turnos, el modelo no recuerda qué tools llamó ni qué tags ejecutó salvo lo que diga su respuesta final. Conviene dejarlo escrito como decisión.

D2. **Un tag se procesa al cerrar, venga del razonamiento o del contenido** (decisión del usuario, 2026-09-04). Objeción registrada: procesar al cierre del tag impide las reglas R2 (última ocurrencia) y R3 (conflicto con tool call concurrente) del diseño anterior, porque al cerrar el tag aún no se sabe qué viene después. En consecuencia, el esqueleto `<write_file path="analysis.md" summary="Informe de análisis de código">[content]</write_file>` que el modelo escribió en su razonamiento en el turno 1 de la tarde (log 15:24, línea 7395) **volverá a sobrescribir el archivo**: está bien formado, tiene `path`, y la única validación compatible con este diseño (atributos requeridos, cuerpo no vacío) lo deja pasar. Se asume el trade-off a cambio de un mecanismo único y sin distinción de canal.

## 6. Estado y trabajo pendiente

Hecho (2026-09-04, sin commitear): sección 4 completa (escáner único, ambos transportes, ambos canales); B1, B2, B7 resueltos; B9 fuera del camino. 181 tests en verde.

Hecho (2026-09-07, sin commitear) — segunda pasada tras la revisión completa del sistema:

- Contrato limpio: `IStreamingTagHandler` → `ITagHandler` (`TagName`, `GetInstructions`, `OnCompleteTagAsync`); `StreamingTagHandlerBase` → `TagHandlerBase`; `BaseLlmStep.StreamingHandlers` → `TagHandlers`. Borrados `ITagContext`, `StreamingTagContextBase`, `WriteFileContext` y la mitad incremental de `WriteFileTagHandler`. Cierra B9 y el resto de B2.
- Observabilidad: `TagStartedEvent` lo emite el escáner al reconocer la apertura (antes salía pegado al completed, dentro del handler); `TagCompletedEvent` lleva `path` y `bytesWritten` (antes salía vacío porque el handler recibía `tagContext: null`); un tag sin cerrar emite `TagCompletedEvent(Success=false)`; `StepName` es `context.CurrentStepPath` (cierra B14).
- Solo el candidato primario ejecuta tags (batch y streaming): los alternativos de `n > 1` pasan sin escanear. Antes se ejecutaban los efectos secundarios de todos.
- Gramática: se recorta un único salto de línea pegado a `>` y a `</` (cierra B8); `<` seguido de algo que no puede empezar un nombre de tag (espacio, `=`, dígito) vuelve como texto en el acto, sin retener la salida en streaming.
- B3: `OpenAILlmService.BuildRequestBody` aplica `GetMessagesForRequest` como Gemini; `BaseLlmStep.BuildLlmRequestAsync` copia `AITaskAgent:Conversation.UseSlidingWindow/SlidingWindowMaxTokens` a la request (antes las opciones se leían pero no se usaban); la ventana nunca empieza en un mensaje `tool` huérfano.
- B4: `FileToolsTestApp` llama `FinalizeAsync` tras cada turno (documentado en `BaseLlmStep.FinalizeAsync`); `SlidingWindowMaxTokens` 120 000 en su `appsettings.json`.
- B5: `_lastToolCallKeys`/`_consecutiveIdenticalToolCalls` se resetean al empezar cada turno.
- S4: el sample ya no borra `logs/`; crea `trace_yyyyMMdd_HHmmss.log` por arranque.
- `GetInstructions()` y el bloque del system prompt reescritos con ejemplo completo y par WRONG/RIGHT explícito.
- El segundo `StreamingTagParser` (servicio OpenAI, extractor de `<think>`) pasa a `ReasoningTagStreamParser`.
- Retirada la lección "no tratar el razonamiento como fuente de acciones ejecutables" (contradecía D2; decisión del usuario 2026-09-07).

Pendiente:

1. **Híbrido tag/tool** (aplazado por el usuario a otra sesión): registrar cada `ITagHandler` también como `ToolDefinition` y enrutar la tool call al mismo `OnCompleteTagAsync`, con deduplicación por identidad (`write_file` + `path`). Decidir qué pasa con `WriteToFileTool` (esquema `TargetFile/CodeContent` vs `path/content`). Dato empírico que falta: qué argumentos manda el modelo cuando llama `write_file` como tool (los logs que lo tenían se borraron; el próximo arranque lo conserva y lo logea a nivel Debug en `LlmInvocation.cs`, "→ iteration N call:").
2. B10, B11, S1-S3; B6, B12, B13, B15, B16 y D1 como documentación.
3. Probar en runtime con `FileToolsTestApp` en ambos modos (`UseStreaming` true/false) contra el modelo real: los tests cubren el mecanismo, no la reacción del modelo.
