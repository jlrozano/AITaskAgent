// Guiones del servidor simulado de chat-protocol/1. Cada guion es una lista de pasos; el servidor añade `turnId` y `seq`.
// Se elige por palabra clave del mensaje (`texto`, `razonamiento`, `herramientas`, `agentes`, `lineal`, `informe`, `propuesta`, `pregunta`, `aprobacion`,
// `error`, `lento`, `desconocido`); sin palabra clave, `texto`. La respuesta a una pregunta (`answer`) usa `respuesta`.
import type { ServerEvent } from "../src/protocol.ts";

type Distribute<T> = T extends unknown ? Omit<T, "turnId" | "seq"> : never;
export type Draft = Distribute<ServerEvent>;
export interface Step {
  event?: Draft;
  /** Espera antes de emitir (ms). */
  delayMs?: number;
  /** El paso espera a que se cancele el turno (el servidor emite entonces `done {reason: "cancelled"}`). */
  hang?: true;
  /** Tipo no definido en chat-protocol/1: el cliente debe ignorarlo (compatibilidad hacia delante). */
  unknown?: Record<string, unknown>;
}

const start = (model = "mock-model"): Step => ({ event: { type: "turn.start", sessionId: "s1", task: "auto", provider: "mock", model, baseVersion: 1, autoApprove: false, learningEnabled: false } });
const usage = (i: number, o: number, cost?: number): Step => ({
  event: { type: "usage", provider: "mock", model: "mock-model", inputTokens: i, cachedInputTokens: 0, outputTokens: o, reasoningTokens: 0, turn: { requests: 1, inputTokens: i, cachedInputTokens: 0, outputTokens: o, reasoningTokens: 0 }, ...(cost !== undefined ? { estimatedCost: cost, currency: "USD" } : {}) },
});
const chunks = (s: string, n = 12): Step[] => Array.from({ length: Math.ceil(s.length / n) }, (_, i) => ({ event: { type: "text.delta" as const, text: s.slice(i * n, (i + 1) * n) } }));
const done = (reason = "completed"): Step => ({ event: { type: "done", reason, usage: { requests: 1, inputTokens: 100, cachedInputTokens: 0, outputTokens: 20, reasoningTokens: 0 }, steps: 1, toolCalls: 0, elapsedMs: 5 } });

export type Script = (req: { text?: string }) => Step[];

export const scripts: Record<string, Script> = {
  texto: () => [start(), ...chunks("Hola, **soy** el asistente simulado.\n\n- primero\n- segundo\n"), usage(100, 20, 0.0012), done()],

  razonamiento: () => [
    start(),
    { event: { type: "thinking.delta", text: "Primero miro el esquema. " } },
    { event: { type: "thinking.delta", text: "Luego decido la consulta." } },
    ...chunks("Listo, ya lo he pensado."),
    usage(80, 30),
    done(),
  ],

  herramientas: () => [
    start(),
    ...chunks("Voy a mirar el esquema. "),
    { event: { type: "tool.started", callId: "call_1", name: "search_schema", label: "Buscando en el esquema", where: "server", input: { query: "facturas" } } },
    { event: { type: "tool.completed", callId: "call_1", name: "search_schema", ok: true, summary: "3 objetos", elapsedMs: 12 } },
    { event: { type: "tool.started", callId: "call_2", name: "validate_sql", where: "server" } },
    { event: { type: "tool.completed", callId: "call_2", name: "validate_sql", ok: false, summary: "SELECT * no permitido", elapsedMs: 4, errorCode: "sql.rejected" } },
    { event: { type: "notice", code: "sampleRows.sent", message: "Se enviaron filas de muestra al proveedor." } },
    ...chunks("Encontré 3 tablas."),
    usage(500, 60, 0.004),
    done(),
  ],

  propuesta: () => [
    start(),
    ...chunks("He preparado los cambios."),
    {
      event: {
        type: "proposal",
        proposalId: "p1",
        baseVersion: 1,
        revision: 2,
        summary: "Total en negrita y a la derecha",
        operations: [{ callId: "call_1", tool: "set_properties", label: "Negrita en Total", changed: ["c12"] }],
        changes: [
          { kind: "changed", target: "c12", label: "Total", before: "normal", after: "negrita" },
          { kind: "added", target: "c13", label: "Línea de total" },
          { kind: "removed", target: "c4", label: "Rótulo antiguo" },
        ],
        validation: { valid: true, errors: 0, warnings: 1, scriptErrors: 0 },
      },
    },
    usage(300, 40),
    done(),
  ],

  pregunta: () => [
    start(),
    ...chunks("Necesito un dato."),
    { event: { type: "question", callId: "ask_1", question: "¿Qué fecha usamos?", kind: "choice", options: ["emisión", "vencimiento"], header: "Fecha", questions: [{ question: "¿Qué fecha usamos?", options: [{ label: "emisión", recommended: true, description: "fecha de la factura" }, { label: "vencimiento" }] }] } },
    done("question"),
  ],

  aprobacion: () => [
    start(),
    { event: { type: "question", callId: "ask_2", question: "¿Borro 8 controles?", kind: "approval", options: [], stage: "plan" } },
    done("question"),
  ],

  respuesta: (req) => [start(), ...chunks(`Recibido: ${req.text ?? ""}`), usage(60, 10), done()],

  error: () => [
    start(),
    ...chunks("Empiezo… "),
    { event: { type: "error", code: "assistant.providerUnavailable", message: "El proveedor no responde.", retryable: true } },
    done("error"),
  ],

  // Cierre antes de tiempo si el cliente cancela: ver `hang` en server.ts.
  lento: () => [start(), ...Array.from({ length: 40 }, (_, i): Step => ({ delayMs: 25, event: { type: "text.delta", text: `trozo${i} ` } })), { hang: true }, done()],

  // Línea de tiempo: dos agentes IA (el segundo reintentado y fallido), herramientas con salida JSON y un paso propio.
  agentes: () => [
    start(),
    { event: { type: "step.started", stepId: "s1", name: "plan", label: "Planificando", kind: "llm", attempt: 1, detail: "Decide qué tablas consultar", display: "Decidiendo qué tablas consultar para «facturas»" } },
    { event: { type: "tool.started", callId: "call_1", name: "search_schema", label: "Buscando en el esquema", where: "server", input: { query: "facturas", limit: 5 }, stepId: "s1", display: "Buscando «facturas» en el esquema" } },
    { event: { type: "tool.completed", callId: "call_1", name: "search_schema", ok: true, summary: "2 objetos", elapsedMs: 12, output: { objects: [{ name: "FACTURA", kind: "table" }, { name: "CLIENTE", kind: "table" }], total: 2 }, stepId: "s1", display: "Encontradas 2 tablas para «facturas»" } },
    { event: { type: "step.completed", stepId: "s1", ok: true, elapsedMs: 40, summary: "esquema localizado", model: "mock-model", inputTokens: 1200, outputTokens: 340, costUsd: 0.0123, display: "Plan listo: FACTURA y CLIENTE" } },
    { event: { type: "step.started", stepId: "s2", name: "sql", label: "Redactando SQL", kind: "llm", attempt: 2, detail: "Escribe y valida la consulta" } },
    { event: { type: "tool.started", callId: "call_2", name: "validate_sql", input: { sql: "SELECT * FROM FACTURA" }, stepId: "s2" } },
    { event: { type: "tool.completed", callId: "call_2", name: "validate_sql", ok: false, summary: "SELECT * no permitido", elapsedMs: 4, errorCode: "sql.rejected", output: "texto plano que no es JSON", outputTruncated: true, stepId: "s2" } },
    { event: { type: "step.completed", stepId: "s2", ok: false, elapsedMs: 9, errorCode: "sql.rejected", summary: "rechazado", model: "mock-model", inputTokens: 800, outputTokens: 60 } },
    { event: { type: "step.started", stepId: "s3", name: "compile", label: "Compilar plantilla", kind: "step", attempt: 1, detail: "Comprueba que la plantilla compila" } },
    { event: { type: "step.completed", stepId: "s3", ok: true, elapsedMs: 3, summary: "plantilla compilada" } },
    ...chunks("Hecho."),
    usage(2000, 400),
    done(),
  ],

  // Secuencia lineal: un paso LLM con razonamiento, 3 herramientas, más razonamiento y un texto con pregunta, todo dentro de la caja del paso.
  lineal: () => [
    start(),
    { event: { type: "step.started", stepId: "l1", name: "analiza", label: "Analizando la petición", kind: "llm", attempt: 1, detail: "Decide qué consultar y propone el siguiente paso" } },
    { event: { type: "thinking.delta", text: "El usuario pide un informe de ventas. " } },
    { event: { type: "thinking.delta", text: "Primero miro qué tablas hay, luego describo VENTA y cuento filas." } },
    { event: { type: "tool.started", callId: "l_1", name: "search_schema", label: "Buscando en el esquema", where: "server", input: { query: "ventas" }, stepId: "l1" } },
    { delayMs: 120, event: { type: "tool.completed", callId: "l_1", name: "search_schema", ok: true, summary: "4 objetos", elapsedMs: 120, output: { total: 4 }, stepId: "l1" } },
    { event: { type: "tool.started", callId: "l_2", name: "describe_table", label: "Describiendo VENTA", where: "server", input: { table: "VENTA" }, stepId: "l1" } },
    { delayMs: 120, event: { type: "tool.completed", callId: "l_2", name: "describe_table", ok: true, summary: "9 columnas", elapsedMs: 120, output: { columns: 9 }, stepId: "l1" } },
    { event: { type: "tool.started", callId: "l_3", name: "count_rows", label: "Contando filas", where: "server", input: { table: "VENTA" }, stepId: "l1" } },
    { delayMs: 120, event: { type: "tool.completed", callId: "l_3", name: "count_rows", ok: true, summary: "12.408 filas", elapsedMs: 120, output: { rows: 12408 }, stepId: "l1" } },
    { event: { type: "thinking.delta", text: "Tengo lo necesario; falta saber el criterio de agrupación antes de diseñar." } },
    ...chunks("He encontrado la tabla **VENTA** (9 columnas, 12.408 filas).\n\n¿Quieres agrupar el informe por cliente o por mes?"),
    { event: { type: "step.completed", stepId: "l1", ok: true, elapsedMs: 480, summary: "pendiente de tu respuesta", model: "mock-model", inputTokens: 1800, outputTokens: 220, costUsd: 0.0031 } },
    usage(1800, 220, 0.0031),
    done(),
  ],

  // Flujo de «informe nuevo»: 3 pasos deterministas (kind "step") y 2 agentes IA con herramientas. Con pausas para ver «En curso».
  informe: () => [
    start(),
    { event: { type: "step.started", stepId: "p1", name: "Borrador_Extraer", label: "Extraer borrador", kind: "step", attempt: 1, detail: "Lee la petición y extrae el borrador del informe" } },
    { delayMs: 150, event: { type: "step.completed", stepId: "p1", ok: true, elapsedMs: 150, summary: "borrador con 3 bandas y 12 campos" } },
    { event: { type: "step.started", stepId: "a1", name: "disenar", label: "Diseñar el informe", kind: "llm", attempt: 1, detail: "Elige tablas y campos de cada banda", display: "Diseñando las bandas del informe" } },
    { event: { type: "tool.started", callId: "call_1", name: "search_schema", label: "Buscando en el esquema", where: "server", input: { query: "ventas" }, stepId: "a1" } },
    { delayMs: 150, event: { type: "tool.completed", callId: "call_1", name: "search_schema", ok: true, summary: "4 objetos", elapsedMs: 150, output: { total: 4 }, stepId: "a1" } },
    { event: { type: "tool.started", callId: "call_2", name: "describe_table", label: "Describiendo VENTA", where: "server", input: { table: "VENTA" }, stepId: "a1" } },
    { delayMs: 900, event: { type: "tool.completed", callId: "call_2", name: "describe_table", ok: true, summary: "9 columnas", elapsedMs: 900, output: { columns: 9 }, stepId: "a1" } },
    { event: { type: "step.completed", stepId: "a1", ok: true, elapsedMs: 1100, summary: "diseño de 3 bandas", model: "mock-model", inputTokens: 2400, outputTokens: 610, costUsd: 0.0041, display: "Informe diseñado en 3 bandas" } },
    { event: { type: "step.started", stepId: "p2", name: "Borrador_Validar", label: "Validar SQL", kind: "step", attempt: 1, detail: "Ejecuta las consultas contra la base en solo lectura" } },
    { delayMs: 100, event: { type: "step.completed", stepId: "p2", ok: true, elapsedMs: 100, summary: "3 consultas válidas" } },
    { event: { type: "step.started", stepId: "a2", name: "redactar", label: "Redactar la plantilla", kind: "llm", attempt: 1, detail: "Escribe la plantilla final con los controles" } },
    { event: { type: "tool.started", callId: "call_3", name: "set_properties", label: "Aplicando propiedades", where: "server", input: { band: "detail" }, stepId: "a2" } },
    { delayMs: 150, event: { type: "tool.completed", callId: "call_3", name: "set_properties", ok: true, summary: "12 controles", elapsedMs: 150, output: { changed: 12 }, stepId: "a2" } },
    { delayMs: 100, event: { type: "step.completed", stepId: "a2", ok: true, elapsedMs: 300, summary: "plantilla redactada", model: "mock-model", inputTokens: 3100, outputTokens: 880, costUsd: 0.0062 } },
    { event: { type: "step.started", stepId: "p3", name: "Compilar", label: "Compilar plantilla", kind: "step", attempt: 1, detail: "Comprueba que la plantilla compila" } },
    { delayMs: 50, event: { type: "step.completed", stepId: "p3", ok: true, elapsedMs: 50, summary: "plantilla compilada sin errores" } },
    ...chunks("Informe creado."),
    usage(5500, 1490, 0.0103),
    done(),
  ],
  desconocido: () => [
    start(),
    { unknown: { type: "futuro.evento", dato: 1 } },
    { event: { type: "text.delta", text: "ignoré lo que no conozco", ...({ campoNuevo: true } as object) } },
    done(),
  ],
};

/** Elige el guion por palabra clave del mensaje. */
export function pickScript(text: string): string {
  const t = text.toLowerCase();
  for (const key of ["agentes", "lineal", "informe", "razonamiento", "herramientas", "propuesta", "aprobacion", "pregunta", "error", "lento", "desconocido", "texto"]) {
    if (t.includes(key)) return key;
  }
  return "texto";
}
