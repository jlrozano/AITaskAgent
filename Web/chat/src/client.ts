// Cliente de chat-protocol/1: POST + flujo SSE, reanudación simple y cancelación. Sin estado de interfaz.
//
// Transporte:
//   POST {baseUrl}/messages               cuerpo MessageRequest | AnswerRequest → 200 text/event-stream
//   POST {baseUrl}/cancel                 cuerpo CancelRequest                  → 202
//   GET  {baseUrl}/turns/{turnId}/events  con `Last-Event-ID: <turnId>:<seq>`   → reanuda (opcional en el servidor)
import { isServerEvent, type AnswerRequest, type CancelRequest, type MessageRequest, type ServerEvent } from "./protocol.ts";
import { parseSse } from "./sse.ts";
import { applyTransform, type TransformEvent } from "./transform.ts";

export interface ChatClientOptions {
  /** URL base sin barra final, p. ej. `/api/assistant/sessions/s1`. */
  baseUrl: string;
  /** Cabeceras fijas (autenticación del anfitrión). */
  headers?: Record<string, string>;
  fetch?: typeof fetch;
  /** Reintentos de reanudación si el flujo se corta antes de `done` (por defecto 2). */
  maxReconnects?: number;
  reconnectDelayMs?: number;
  /** Tras pedir la cancelación se espera el `done` del servidor; pasado este plazo se aborta la lectura (por defecto 2000 ms). */
  cancelGraceMs?: number;
  /** Hook previo a la entrega de cada evento del servidor (también los reanudados); ver `TransformEvent`. */
  transformEvent?: TransformEvent;
}

export class ChatClient {
  private readonly o: ChatClientOptions & { maxReconnects: number; reconnectDelayMs: number; cancelGraceMs: number };
  private controller: AbortController | null = null;
  private turnId: string | undefined;

  constructor(options: ChatClientOptions) {
    this.o = { maxReconnects: 2, reconnectDelayMs: 300, cancelGraceMs: 2000, ...options };
  }

  private get f(): typeof fetch {
    return this.o.fetch ?? globalThis.fetch.bind(globalThis);
  }

  get busy(): boolean {
    return this.controller !== null;
  }

  /**
   * Envía un mensaje o una respuesta y entrega cada evento a `onEvent` hasta `done`. Si el flujo se corta sin `done`,
   * intenta reanudar; si no puede, entrega un `error` (`connection.lost`) y un `done` sintéticos, de modo que el
   * consumidor siempre ve el cierre del turno. No lanza por fallos de red.
   */
  async send(request: MessageRequest | AnswerRequest, onEvent: (e: ServerEvent) => void, sendOptions: { transformEvent?: TransformEvent } = {}): Promise<void> {
    if (this.controller) throw new Error("Ya hay un turno en curso.");
    // El hook de la llamada gana al del constructor. Solo se aplica a eventos del servidor, no a los sintéticos del cliente.
    const hook = sendOptions.transformEvent ?? this.o.transformEvent;
    const controller = (this.controller = new AbortController());
    this.turnId = undefined;
    let lastSeq = -1;
    let finished = false;
    // `seq`, `turnId` y `done` se siguen sobre el evento ORIGINAL; el hook solo decide qué ve el consumidor.
    const deliver = (e: ServerEvent, synthetic = false) => {
      if (e.seq <= lastSeq) return; // duplicado tras reanudar
      lastSeq = e.seq;
      this.turnId = e.turnId;
      if (e.type === "done") finished = true;
      const out = synthetic ? e : applyTransform(hook, e);
      if (out) onEvent(out);
    };
    const deliverSynthetic = (e: ServerEvent) => deliver(e, true);
    const pump = async (res: Response): Promise<void> => {
      if (!res.ok || !res.body) throw new HttpError(res.status, await safeText(res));
      for await (const ev of parseSse(res.body, controller.signal)) {
        // Eventos de tipo desconocido o no JSON: se ignoran (compatibilidad hacia delante, §8.19).
        if (isServerEvent(ev.data)) deliver(ev.data);
      }
    };
    try {
      let attempt = 0;
      let resume = false;
      while (!finished) {
        try {
          const res = resume
            ? await this.f(`${this.o.baseUrl}/turns/${encodeURIComponent(this.turnId ?? "")}/events`, { headers: { Accept: "text/event-stream", "Last-Event-ID": `${this.turnId}:${lastSeq}`, ...this.o.headers }, signal: controller.signal })
            : await this.f(`${this.o.baseUrl}/messages`, { method: "POST", headers: { "Content-Type": "application/json", Accept: "text/event-stream", ...this.o.headers }, body: JSON.stringify(request), signal: controller.signal });
          await pump(res);
          if (!finished) throw new Error("El flujo terminó sin `done`.");
        } catch (e) {
          if (controller.signal.aborted) throw e;
          const retriable = !(e instanceof HttpError) || e.status >= 500;
          // Sin turnId no hay nada que reanudar (y reenviar el POST duplicaría el mensaje).
          if (finished || !retriable || this.turnId === undefined || attempt >= this.o.maxReconnects) {
            this.fail(e, lastSeq, deliverSynthetic);
            break;
          }
          attempt++;
          resume = true;
          await delay(this.o.reconnectDelayMs * attempt, controller.signal);
        }
      }
    } catch {
      // Aborto (cancelación sin respuesta del servidor): se cierra el turno como cancelado.
      if (!finished) deliverSynthetic({ type: "done", turnId: this.turnId ?? "t0", seq: lastSeq + 1, reason: "cancelled" });
    } finally {
      this.controller = null;
    }
  }

  /** Pide la cancelación al servidor; el `done {reason: "cancelled"}` llega por el flujo abierto. */
  async cancel(sessionId?: string): Promise<void> {
    const controller = this.controller;
    if (!controller) return;
    const body: CancelRequest = { type: "cancel", ...(sessionId ? { sessionId } : {}), ...(this.turnId ? { turnId: this.turnId } : {}) };
    const timer = setTimeout(() => controller.abort(), this.o.cancelGraceMs);
    controller.signal.addEventListener("abort", () => clearTimeout(timer), { once: true });
    try {
      await this.f(`${this.o.baseUrl}/cancel`, { method: "POST", headers: { "Content-Type": "application/json", ...this.o.headers }, body: JSON.stringify(body) });
    } catch {
      controller.abort(); // sin servidor al que pedírselo: se corta aquí
    }
  }

  /** Corta la lectura sin avisar al servidor (cierre de la vista). */
  abort(): void {
    this.controller?.abort();
  }

  private fail(e: unknown, lastSeq: number, deliver: (e: ServerEvent) => void): void {
    const turnId = this.turnId ?? "t0";
    const http = e instanceof HttpError;
    deliver({
      type: "error",
      turnId,
      seq: lastSeq + 1,
      code: http ? `http.${e.status}` : "connection.lost",
      message: http ? `El servidor respondió ${e.status}${e.body ? `: ${e.body}` : ""}` : "Se perdió la conexión con el asistente.",
      retryable: !http || e.status >= 500,
    });
    deliver({ type: "done", turnId, seq: lastSeq + 2, reason: "error" });
  }
}

class HttpError extends Error {
  readonly status: number;
  readonly body: string;
  constructor(status: number, body: string) {
    super(`HTTP ${status}`);
    this.status = status;
    this.body = body;
  }
}

async function safeText(res: Response): Promise<string> {
  try {
    return (await res.text()).slice(0, 300);
  } catch {
    return "";
  }
}

function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const t = setTimeout(resolve, ms);
    signal.addEventListener("abort", () => {
      clearTimeout(t);
      reject(new DOMException("Cancelado", "AbortError"));
    }, { once: true });
  });
}
