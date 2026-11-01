# @aitaskagent/chat

Chat reutilizable para React 19 que consume el protocolo `chat-protocol/1` (SSE). Es un componente genérico de AITaskAgent: no conoce a ningún anfitrión concreto; lo único que necesita de él es el adaptador `HostAdapter`. Paquete privado (`private: true`, licencia MIT): por ahora solo se consume desde este repositorio y no está publicado en npm.

- Contrato del protocolo: `schema/chat-protocol.schema.json`.
- Punto de entrada: `src/index.ts`. Estilos: `@aitaskagent/chat/chat.css`.
- Dependencias de ejecución: `react`/`react-dom` 19 (peer) y `streamdown` 2.7.0 (Markdown seguro). `@streamdown/mermaid` 1.0.3 es peer **opcional** (diagramas).

## Qué hay

| Pieza | Fichero | Para qué |
|---|---|---|
| `ChatClient` | `src/client.ts` | POST `{baseUrl}/messages` (SSE), `{baseUrl}/cancel` y reanudación `GET {baseUrl}/turns/{turnId}/events` con `Last-Event-ID`. |
| `chatReducer`, `ChatState` | `src/state.ts` | Estado puro: mensajes con partes (`text`, `thinking`, `tool`, `toolGroup`, `attachments`, `proposal`, `question`, `notice`, `error`, `usage`). |
| `useChat` | `src/useChat.ts` | Hook que une cliente, reductor y anfitrión (opción `transformEvent`): `send`, `setTransformEvent`, `answer`, `cancel`, `applyProposal`, `discardProposal`, `reset`. |
| `ChatView`, `MessageList` | `src/components/` | Vista completa (lista, cuadro de texto, Enviar/Detener) o solo la lista. |
| `createRenderers`, `defaultRenderers` | `src/registry.ts`, `src/renderers/` | Un renderizador por tipo de parte; se pueden sustituir o ampliar. |
| `defaultLabels` | `src/labels.ts` | Textos de la interfaz (español); se sobrescriben con `labels`. |
| `applyTransform`, `TransformEvent` | `src/transform.ts` | El hook previo `transformEvent` (ver más abajo). |
| `HostAdapter` | `src/host.ts` | El contrato con la aplicación que aloja el chat. |

## Integración mínima

```tsx
import { ChatClient, ChatView, useChat, type HostAdapter } from "@aitaskagent/chat";
import "@aitaskagent/chat/chat.css";

const client = new ChatClient({
  baseUrl: `/api/assistant/chat/sessions/${sessionId}`, // sin barra final
  headers: { /* autenticación del anfitrión, si la hay */ },
});

const host: HostAdapter = {
  readDocument: () => miDocumento(),            // opaco: va en MessageRequest.document
  readContext: () => ({ seleccion: [] }),       // opcional: MessageRequest.context
  setEditingLocked: (locked) => bloquearEdicion(locked), // true al enviar; false en done/error/cancelación
  applyProposal: async (p) => {                 // una sola entrada de deshacer
    aplicar(p.document /* o p.changes, ambos opcionales */);
    return { outcome: "applied", operations: p.changes?.length };
  },
  discardProposal: (p) => quitarResaltados(p),  // opcional
};

function Asistente() {
  const chat = useChat({ client, host, sessionId });
  return <ChatView chat={chat} toolbar={<MiSelectorDeModelo />} />;
}
```

`applyProposal` devuelve `{ outcome: "applied" | "conflict" | "failed", message? }`; el chat lo muestra y el anfitrión decide cómo informar al servidor del resultado.

## Reglas del protocolo que conviene respetar

- Un turno por vez. Si el modelo hace una pregunta (`question`), el turno se cierra; la respuesta es un mensaje nuevo (`answer` con el `callId` de la pregunta). Una respuesta que ya no está pendiente da `409 assistant.noPendingQuestion`; para «Reintentar» tras un fallo, reenvíe el texto como mensaje normal.
- La propuesta llega como documento completo (`proposal.document`) más `changes[]` para mostrarla; el servidor trabaja sobre su copia y el anfitrión bloquea la edición durante el turno.
- El anfitrión es quien crea la sesión del servidor y quien cambia de sesión si cambia el contexto (p. ej. otra conexión de datos: 409 `assistant.connectionChanged`).
- `retryTurn`/`canRetry` en el contexto de los renderizadores son opcionales: sin ellos no sale «Reintentar».

## Línea de tiempo e inspector de herramienta

Campos aditivos de `chat-protocol/1` que el reductor conserva:

- `tool.completed.output` (JSON, o string si no lo era) y `outputTruncated: true` (el servidor recorta a 16384 caracteres) pasan a `ToolPart.output` / `ToolPart.outputTruncated`. `tool.started.stepId` / `tool.completed.stepId` pasan a `ToolPart.stepId`.
- `step.started` (`stepId`, `name`, `label?`, `kind: llm|tool|step`, `attempt >= 1`, `detail?`) y `step.completed` (`stepId`, `ok`, `elapsedMs?`, `errorCode?`, `summary?` y, solo en `llm`, `model?`, `inputTokens?`, `outputTokens?`, `costUsd?`) crean y cierran la parte `agent` (`AgentPart`: `id` = `stepId`, `status: running|ok|error`, `attempt`, `detail`, `elapsedMs`, `summary`, `errorCode`, `model`, `inputTokens`, `outputTokens`, `costUsd`). Un reintento (mismo `stepId`, `attempt` mayor) reabre el nodo. Al llegar `done`, los pasos y herramientas que seguían en curso pasan a `error`.
- **Línea de tiempo** (`MessageList` + `toolGroup`): con algún paso (`step.*`) en el turno sale un panel plegable (abierto mientras corre, plegado al terminar; si el usuario lo toca, manda él) con una lista ordenada de nodos, **también con un único nodo**. Las herramientas con el `stepId` de un nodo cuelgan de él (plegables dentro del nodo); las demás se pintan sueltas. Sin `step.*` ni `stepId` todo sale como antes (panel plano «N pasos»). `ToolGroupPart` trae `entries` y `agentHeaders` para quien sustituya el renderizador.
- **Sin vocabulario de clases**: la interfaz no pinta «Agente», «Herramienta» ni «Paso» como etiqueta. Cada nodo se titula con su propia descripción (`display`, ver más abajo; respaldo `label ?? name`). Se conservan dos iconos SVG distintos como apoyo visual (destello = paso del flujo, `kind: llm|step`; llave = llamada a herramienta, `kind: tool` y las herramientas), con `role="img"` y nombre accesible neutro sobreescribible: `labels.iconStep` («Paso») y `labels.iconCall` («Llamada»). `classAgent`, `classStep`, `classTool`, `countAgent(s)`, `countStep(s)` y `countTool(s)` siguen aceptándose en `labels` (compilan) pero **no se pintan**.
- **Nodo**: icono de estado (en curso con arco que gira, quieto con `prefers-reduced-motion`; correcto; error), icono de tipo, título (`display`; mientras corre el de `*.started`, al terminar el de `*.completed`, que lo sustituye; sin `display`, `label ?? name`), «intento n», duración (cronómetro vivo mientras corre), `detail` en gris debajo (también en curso), el **chip de modelo, tokens y coste** en el nodo que lo traiga (`step.completed.model|inputTokens|outputTokens|costUsd`) y el resultado (`summary`) al terminar. Si todos los pasos `llm` traen tokens, la línea `usage` suelta del turno no se repite; si alguno no, se conserva.
- **Cabecera del panel** (sin palabras de clase): en curso, «En curso: <título del nodo actual>» (la herramienta activa gana al paso); al terminar, «Completado · N acciones · duración» (N = nodos visibles, pasos y herramientas; duración = suma de los nodos de primer nivel; los ceros se omiten) y, con fallos, «· n con error». Etiquetas `done`, `countAction`, `countActions`, `nowRunning` (todas sobreescribibles; `toolsToggle` queda `@deprecated`, sin uso).
- **Secuencia lineal dentro de la caja del paso**: cada paso pinta, tras su cabecera y `detail`, en orden de llegada, el razonamiento (atenuado; abierto si es corto, plegado si pasa de 280 caracteres), las filas de herramienta (con el botón-ojo si `toolInspector`) y el texto del modelo (markdown); el `summary` cierra la caja. No hay bloque plegable «Llamadas». Los eventos de texto/razonamiento no traen `stepId`: un paso posee las partes `thinking`/`text`/`tool` posteriores a él y anteriores a su cierre, y el reductor anota ese cierre en el campo opcional `AgentPart.endIndex` (`parts.length` en `step.completed`, o al cerrar el turno con el paso abierto); mientras corre absorbe todo, con pasos anidados gana el último abierto, y las herramientas con `stepId` conocido siguen en su caja aunque lleguen tras el cierre. Sin `endIndex` un paso terminado solo posee sus herramientas. El texto posterior al último cierre sale como mensaje bajo el panel; si algún paso trae texto, el panel no se pliega al terminar. `showThinking`/`showToolCalls` se aplican dentro de la caja. Mock: palabra `lineal`.
- **Aviso `phase` heredado**: un `notice` con `code: "phase"` se pinta como un nodo ya terminado, con el nombre sin guiones bajos, en vez de una línea cruda. Lo correcto es emitir `step.*` con `kind: "step"`.
- Renderizador `agent` (`AgentPart`, un nodo de paso; recibe `part.tools`) registrado en `defaultRenderers`; se sustituye con `createRenderers(defaultRenderers, { agent: Mio })`. `MessageList` pone el registro en uso en `RendererContext.renderers` para que `toolGroup` lo encuentre.
- **`toolInspector`** (boolean, por defecto `false`) en `ChatView`, `MessageList` y `RendererContext`. Activa: la fila de herramienta muestra solo estado, título (su `display` si lo hay, si no etiqueta/nombre), duración (y `errorCode` si falló), sin argumentos en línea ni `summary` (el resumen solo se ve en la modal); el nombre es un botón con aspecto de enlace y además sale un botón solo de icono, y los dos abren la misma modal. Es **opt-in por proyecto**: sin activarlo la fila queda como siempre (argumentos y resumen). El botón de icono (`labels.toolInspect`, como `aria-label` y `title`) que abre una modal (`role="dialog"`, `aria-modal`, foco atrapado, Esc y clic en el fondo cierran, devuelve el foco, scroll vertical) con las secciones «Entrada» y «Salida». Visor JSON propio (sin dependencias): objetos y arrays plegables, escalares coloreados con las variables `--atc-json-key|str|num|bool|null|punct` y `--atc-modal-bg|fg|border` (claro por defecto; oscuro con `prefers-color-scheme` o `data-theme="dark"` en el documento), cadenas largas con salto, botón copiar, texto plano si no es JSON. Salida en curso: «en curso…»; herramienta fallida: resumen y código de error; `outputTruncated`: aviso. La modal se monta en `document.body` (portal). Desactivada, el comportamiento es el de antes. Etiquetas nuevas, todas sobreescribibles: `attempt`, `toolInspect`, `toolInspectorTitle`, `toolTechName`, `toolDisplayStart`, `toolDisplayEnd`, `toolInput`, `toolOutput`, `toolNoInput`, `toolNoOutput`, `toolOutputPending`, `toolOutputTruncated`, `toolFailed`, `copy`, `copied`, `close`, `jsonExpand`, `jsonCollapse`, `jsonKeys`, `jsonItems`.
 Con `display`, el enlace y la fila llevan el texto del `display`; el nombre técnico queda en el `title` de la fila y, en la modal, en una sección «Nombre técnico» junto a los `display` de inicio y de fin («Al empezar» / «Al terminar»), encima de Entrada y Salida.

```tsx
<ChatView chat={chat} toolInspector />
```

La «Salida» de la modal solo aparece si el servidor emite `tool.completed.output`; sin él, la modal dice «Sin salida». Una herramienta en curso sin entrada abre igualmente la modal, indicando «en curso».

Qué emitir desde un servidor AITask para aprovecharlo: `step.started` (con `detail`: qué hace el paso) y `step.completed` por cada paso del flujo, con `kind: "llm"` para los pasos que llaman al modelo (más `model`, `inputTokens`, `outputTokens`, `costUsd` al completar) y `kind: "step"` para las fases deterministas (con `summary` = su resultado) (mismo `stepId`; reintento = mismo `stepId` con `attempt` mayor), `stepId` del paso abierto más reciente en `tool.started`/`tool.completed`, y `output` (con `outputTruncated: true` si se recorta) en `tool.completed`. Sin ninguno de ellos el chat funciona igual que antes. Pruebas: `tests/agents.test.tsx`, `tests/transform.test.tsx` y los guiones `agentes` e `informe` (pasos con modelo y deterministas, con herramientas) del mock (`mock-server/scripts.ts`) y `tests/timeline.test.tsx`.

### `transformEvent`: hook previo al reductor

```ts
type TransformEvent = (event: ServerEvent) => ServerEvent | null | undefined;
```

Se pasa como prop de `ChatView` (`transformEvent`), como opción de `useChat` (`useChat({ client, transformEvent })`) o de `ChatClient` (constructor, o tercer parámetro de `client.send(req, onEvent, { transformEvent })`). Precedencia: la prop de `ChatView` > la opción de `useChat` > la del `ChatClient`; en un turno se aplica **un** hook (el más prioritario), no se encadenan. Semántica exacta:

- Recibe una **copia profunda** (`structuredClone`) del mismo JSON que envió el servidor, antes del reductor. Mutarla no altera el original.
- Devuelve un evento (modificado o no): ese es el que llega al reductor. Devuelve `null`: el evento se descarta y el chat no presenta nada de él. Devuelve `undefined`: **sin cambios** (pasa el original, aunque hayas mutado la copia: para cambiar algo, devuelve el evento).
- Se aplica a **todos** los eventos del servidor, incluidos los reanudados (`GET .../turns/{id}/events`) y los del mock. No se aplica a los eventos sintéticos que fabrica el propio cliente (`error` `connection.lost`/`http.*` y su `done`).
- Si el hook **lanza**, el evento pasa intacto y el error se registra con `console.error`; nunca rompe el turno. Un resultado que no es un evento del protocolo (sin `type` conocido) se trata igual.
- Protecciones: **`done` no se puede descartar ni cambiar de tipo** (pasa el original y `console.warn`; sí se puede modificar, p. ej. su `reason`, siempre que siga siendo `done`), porque sin él el turno queda «en curso». Descartar `error` o `turn.start` se permite pero avisa con `console.warn` (el primero oculta un fallo real; el segundo deja el mensaje sin `turnId` ni modelo): **`turn.start` y `done` no deberían descartarse**. El seguimiento de `seq`, la deduplicación y la reanudación usan el evento original, así que un hook que cambie `seq`/`turnId` no rompe la reanudación.

```tsx
const hook: TransformEvent = (e) => {
  if (e.type === "notice" && e.code === "debug") return null; // quitar un evento
  if (e.type === "step.completed" && e.summary) return { ...e, summary: e.summary.replace(/\s+/g, " ") }; // reescribir un summary
  if (e.type === "notice" && e.code === "phase") return { ...e, message: "Fase: " + e.message }; // reescribir un notice
  return undefined; // sin cambios
};
<ChatView chat={chat} transformEvent={hook} />
```

(`ChatView` registra el hook en `chat.setTransformEvent`; con `MessageList` suelto, pásalo a `useChat` o al cliente.)

### Filtros de visualización: `showThinking` y `showToolCalls`

Props (por defecto `true`) de `ChatView` y `MessageList`, también en `RendererContext` (la prop gana al contexto). Son **posteriores** al hook e **independientes** de él: el hook decide qué entra en el estado; el filtro, qué se pinta (si el hook convierte un `thinking.delta` en `text.delta`, `showThinking=false` ya no lo oculta). El estado del reductor conserva todos los datos.

- `showThinking=false`: no se pintan las partes `thinking`.
- `showToolCalls=false`: se ocultan los nodos de herramienta (las sueltas, las anidadas y los pasos `kind: "tool"`) y desaparecen del recuento «N acciones»; los pasos (`kind: llm|step`) siguen.

### `display`: texto de presentación con plantilla

Campo opcional (`string`) en `step.started`, `step.completed`, `tool.started` y `tool.completed`. El reductor lo guarda como `displayStart` (de `*.started`) y `displayEnd` (de `*.completed`) en `AgentPart`/`ToolPart`. Presentación: el título del nodo es el `display` del inicio mientras corre y el del fin al terminar (**el del fin sustituye al del inicio**; si solo hubo el del inicio, se conserva); sin `display` el título es `label ?? name` y `detail`/`summary` se pintan como siempre. En una herramienta con `display`, la fila y el enlace del inspector son ese texto; el nombre técnico queda en el `title` de la fila y en la modal. **Qué emitir desde el servidor**: el framework renderiza plantillas (`DisplayStart`/`DisplayEnd` del paso o la herramienta, motor `TinyTemplateRendered`) y rellena `display` en los cuatro eventos; sin plantilla, no se emite el campo (no `""`). Es texto plano de una línea, sin markdown.



## Integración con AITask (servidor)

El chat no habla con AITask directamente: habla `chat-protocol/1` por SSE con cualquier servidor. Un servidor sobre AITaskAgent traduce los eventos del canal de ejecución (texto, herramientas, pasos, uso, pregunta, fin) a eventos `chat-protocol/1`. Pasos para montarlo:

1. **Contenedor de AITask.** `services.AddAITaskAgent()` y `sp.UseAITaskAgent()`. AITask guarda estado en estáticos: un solo contenedor por proceso (dos se pisan). La configuración nativa es la sección `AITaskAgent` de `IConfiguration`.
2. **Rol del modelo.** Un `LlmRole` (proveedor, modelo, prompt de sistema, inferencia), construido a mano o declarado en `AITaskAgent:LlmProviders`.
3. **Herramientas.** Cada herramienta deriva de `LlmTool` (no de `ITool` a secas: solo así AITask emite `ToolStarted/Completed`).
4. **Flujo.** Un paso `LlmStep` con herramientas y topes. El texto del usuario va como `Prompt` literal (`BaseLlmStep` no renderiza plantillas).
5. **Mapeo al protocolo.** Un mapeador asigna a cada evento `type`, `turnId` y `seq` crecientes. Orden garantizado: `turn.start`, mezcla de `text.delta`, `thinking.delta`, `step.*`, `tool.*`, `usage`, `notice`, y un cierre `(question?, proposal?, done)` o `(error, done)`.
6. **Endpoints.** `POST …/messages` (SSE), `POST …/cancel` y `GET …/turns/{turnId}/events` (reanudación con `Last-Event-ID`, con un búfer de eventos con `seq`).

Implementación de referencia en este repositorio: `Samples/CodebaseAnalysis.Core/Chat/Protocol/` (mapeador) y `Samples/CodebaseAnalysisWeb/Api/ChatProtocolEndpoints.cs` (endpoints); cliente de referencia: `Samples/CodebaseAnalysisWeb/ClientApp/src/components/KnowledgeChat.tsx`. Los eventos reales se validan contra `schema/chat-protocol.schema.json`.

## Personalizar

```tsx
import { createRenderers, defaultRenderers } from "@aitaskagent/chat";

const renderers = createRenderers({ ...defaultRenderers, proposal: MiPropuesta }); // clave = Part.type (más `toolGroup`)
<ChatView chat={chat} renderers={renderers} labels={{ /* textos propios */ }} />
```

El ejemplo completo es `Samples/CodebaseAnalysisWeb/ClientApp/src/components/` (`KnowledgeChat.tsx`, `knowledgeRenderers.tsx`).

## Markdown y Mermaid

El texto del asistente se pinta con [Streamdown](https://github.com/vercel/streamdown) (sin renderizador propio): tolera bloques sin cerrar mientras llega el texto y recibe `isAnimating` (turno en curso y última parte del mensaje). El anfitrión añade lo suyo **sin sustituir** el renderizador `text`:

```tsx
import { loadMermaidPlugin } from "@aitaskagent/chat/mermaid"; // opcional: requiere instalar @streamdown/mermaid
const [mermaid, setMermaid] = useState<DiagramPlugin | null>(null);
useEffect(() => { void loadMermaidPlugin().then(setMermaid); }, []);   // null si el paquete no está
<ChatView chat={chat} markdown={{ plugins: mermaid ? { mermaid } : undefined, components: { /* a, code… */ }, rehypePlugins: [miCita] }} />
```

- `markdown` (`MarkdownOptions`) también va en `RendererContext.markdown` si se usa `MessageList` directamente. Campos: `plugins` (`mermaid`, `code`, `math`, `cjk`, `renderers`), `components`, `rehypePlugins` y `mermaidTheme` (`auto` por defecto: se deduce del color de texto heredado del chat).
- **Mermaid opcional**: `@streamdown/mermaid` es `peerDependenciesMeta.optional`; `index.ts` no lo importa (solo `@aitaskagent/chat/mermaid`, con `import()` diferido). Sin plugin, el bloque ```mermaid se queda como bloque de código. Diagrama inválido: bloque de código con el mensaje (`Diagrama no válido: …`, etiqueta `mermaidError`) y el fuente; nunca un hueco. Bloque sin cerrar en streaming: no se pinta hasta cerrarse.
- **Seguridad** (el texto viene de un modelo): el HTML crudo se muestra como texto, no se interpreta; el saneado es el `rehype-sanitize` de Streamdown (esquema de GitHub) **sin `img`**; `javascript:`/`data:` no llegan al DOM; enlaces http(s)/mailto con `rel="noopener noreferrer nofollow"`; Mermaid con `securityLevel: "strict"` forzado. Tus `rehypePlugins` se ejecutan **después** del saneado (no lo reemplazan, a diferencia de pasarlos directos a Streamdown) y responden de lo que generan. Pruebas: `tests/markdown.test.tsx`.
- Estilos: Streamdown usa clases de Tailwind que el kit no incluye; `chat.css` da estilos mínimos con las variables `--atc-*` (`--atc-fg`, `--atc-border`, `--atc-bg-code`, `--atc-link`, `--atc-error`). Controles de copiar/descargar desactivados.
- `renderMarkdown(md, { components?, rehypePlugins? }): string` está **reexportado** con la firma de siempre: HTML saneado y síncrono con la misma tubería que el renderizador `text` (Streamdown con `react-dom/server`; sin Mermaid, sin imágenes, HTML crudo como texto). `marked`/`dompurify` siguen retirados. Prueba: `tests/agents.test.tsx`.

## Peticiones opcionales aceptadas (INSTRUCCIONES-OTRO-AGENTE-CHAT.md)

- `ToolPart.input` se conserva (`tool.started.input`) y el chip muestra los argumentos truncados (`.atc-tool-args`, texto completo en `title`).
- `UseChatOptions.options` se reenvía en `MessageRequest.options` y `AnswerRequest.options` (`send` y `answer`).
- `noticeRenderers` (`ChatView`/`MessageList`): `{ [code]: Renderer<NoticePart> }`; el aviso cuyo `code` no esté usa `renderers.notice`.
- `react`/`react-dom` siguen como peer y fuera del bundle (también `streamdown`).
- Consumo como fuente: en vitest/Vite del anfitrión, `streamdown` debe resolverse con la misma copia de React (en el diseñador: `resolve.dedupe` y `test.server.deps.inline: [/streamdown/]`).

## Desarrollo y pruebas

```
npm run mock        # servidor simulado con un guion por caso (mock-server/)
npm run typecheck
npm test            # vitest: reductor, renderizadores y conformidad con el esquema (Ajv)
npm run build
```

`tests/conformance.test.ts` valida con Ajv cada evento del servidor simulado y cada cuerpo que envía el cliente contra `schema/chat-protocol.schema.json`.
