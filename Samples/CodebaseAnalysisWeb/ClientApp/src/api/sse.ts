// Cliente SSE sobre fetch + ReadableStream (plan §7, D6).
// EventSource no sirve: el chat es un POST y hace falta controlar la reconexión con `after`.

export interface SseMessage {
  /** Nombre del evento (`event:`); "message" si no se indicó. */
  event: string;
  /** Líneas `data:` unidas con "\n". */
  data: string;
  /** Último `id:` visto en el stream al despachar este mensaje (persiste entre mensajes, como en la especificación). */
  id: string | null;
}

type MessageHandler = (message: SseMessage) => void;
type CommentHandler = (comment: string) => void;

/**
 * Parser incremental de `text/event-stream` según la especificación WHATWG: líneas terminadas en CRLF, LF o CR,
 * comentarios con `:`, campos `id`/`event`/`data`/`retry`, despacho en línea vacía. Admite que un evento, una línea o
 * un CRLF queden partidos entre dos chunks.
 */
export class SseParser {
  lastEventId: string | null = null;
  retryMs: number | null = null;

  private buffer = '';
  private dataLines: string[] = [];
  private hasData = false;
  private eventType = '';
  private first = true;

  constructor(
    private readonly onMessage: MessageHandler,
    private readonly onComment?: CommentHandler,
  ) {}

  push(chunk: string): void {
    if (this.first && chunk.length > 0) {
      this.first = false;
      if (chunk.charCodeAt(0) === 0xfeff) chunk = chunk.slice(1);
    }
    this.buffer += chunk;
    const buf = this.buffer;
    let pos = 0;
    while (pos < buf.length) {
      const lf = buf.indexOf('\n', pos);
      const cr = buf.indexOf('\r', pos);
      if (lf === -1 && cr === -1) break;
      let end: number;
      let next: number;
      if (cr !== -1 && (lf === -1 || cr < lf)) {
        // Un CR al final del buffer puede ser la mitad de un CRLF: se espera al siguiente chunk.
        if (cr === buf.length - 1) break;
        end = cr;
        next = buf.charCodeAt(cr + 1) === 10 ? cr + 2 : cr + 1;
      } else {
        end = lf;
        next = lf + 1;
      }
      this.processLine(buf.slice(pos, end));
      pos = next;
    }
    this.buffer = buf.slice(pos);
  }

  /** Fin del stream: un evento sin línea vacía final se descarta (especificación). */
  end(): void {
    if (this.buffer.endsWith('\r')) this.processLine(this.buffer.slice(0, -1));
    this.buffer = '';
    this.resetEvent();
  }

  private processLine(line: string): void {
    if (line === '') {
      this.dispatch();
      return;
    }
    if (line.startsWith(':')) {
      this.onComment?.(line.slice(1).trimStart());
      return;
    }
    const colon = line.indexOf(':');
    let field: string;
    let value: string;
    if (colon === -1) {
      field = line;
      value = '';
    } else {
      field = line.slice(0, colon);
      value = line.slice(colon + 1);
      if (value.startsWith(' ')) value = value.slice(1);
    }
    switch (field) {
      case 'data':
        this.dataLines.push(value);
        this.hasData = true;
        break;
      case 'event':
        this.eventType = value;
        break;
      case 'id':
        if (!value.includes('\0')) this.lastEventId = value;
        break;
      case 'retry':
        if (/^\d+$/.test(value)) this.retryMs = Number(value);
        break;
      default:
        break; // campo desconocido: se ignora
    }
  }

  private dispatch(): void {
    if (this.hasData) {
      this.onMessage({ event: this.eventType || 'message', data: this.dataLines.join('\n'), id: this.lastEventId });
    }
    this.resetEvent();
  }

  private resetEvent(): void {
    this.dataLines = [];
    this.hasData = false;
    this.eventType = '';
  }
}

function abortError(): Error {
  return new DOMException('La petición se ha cancelado.', 'AbortError');
}

export function isAbortError(error: unknown): boolean {
  return (error instanceof DOMException || error instanceof Error) && error.name === 'AbortError';
}

/**
 * Lee un cuerpo SSE hasta el final y lo pasa por el parser. Si `signal` se aborta, cancela el lector
 * (también cuando el `fetch` es un doble de test que no conoce la señal) y rechaza con AbortError.
 */
export async function readSseStream(
  body: ReadableStream<Uint8Array>,
  parser: SseParser,
  signal?: AbortSignal,
): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder('utf-8');
  const onAbort = () => {
    reader.cancel().catch(() => undefined);
  };
  if (signal?.aborted) {
    onAbort();
    throw abortError();
  }
  signal?.addEventListener('abort', onAbort, { once: true });
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (signal?.aborted) throw abortError();
      if (done) break;
      parser.push(decoder.decode(value, { stream: true }));
    }
    parser.push(decoder.decode());
    parser.end();
  } finally {
    signal?.removeEventListener('abort', onAbort);
  }
}

export type SseConnectionStatus = 'connecting' | 'open' | 'reconnecting' | 'closed' | 'failed';

export interface ResumableSseOptions {
  /** URL a pedir, con el último id recibido (null en la primera conexión si no se indicó otro). */
  url: (lastEventId: string | null) => string;
  onMessage: MessageHandler;
  /** Si devuelve true al acabar un stream, no se reconecta (el servidor cierra tras `run.completed`). */
  isComplete: () => boolean;
  onStatus?: (status: SseConnectionStatus, detail?: { httpStatus?: number; retryInMs?: number }) => void;
  signal: AbortSignal;
  initialLastEventId?: string | null;
  initialDelayMs?: number;
  maxDelayMs?: number;
  fetchImpl?: typeof fetch;
  sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
}

function defaultSleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(done, ms);
    function done() {
      clearTimeout(timer);
      signal.removeEventListener('abort', done);
      resolve();
    }
    signal.addEventListener('abort', done, { once: true });
  });
}

/**
 * GET SSE con reconexión exponencial (1 s → 15 s por defecto). Cada reconexión reanuda desde el último `id`
 * recibido, que viaja en la URL (`after`, lo construye `url`) y en la cabecera `Last-Event-ID`. Un 4xx es
 * definitivo (p. ej. 404: la ejecución ya no existe); un 5xx, un corte de red o un cierre prematuro reintentan.
 */
export async function connectResumableSse(options: ResumableSseOptions): Promise<void> {
  const {
    url,
    onMessage,
    isComplete,
    onStatus,
    signal,
    initialDelayMs = 1000,
    maxDelayMs = 15000,
    fetchImpl = (...args) => fetch(...args),
    sleep = defaultSleep,
  } = options;
  let lastEventId = options.initialLastEventId ?? null;
  let delay = initialDelayMs;
  let attempt = 0;

  while (!signal.aborted) {
    if (attempt === 0) onStatus?.('connecting');
    attempt++;
    try {
      const headers: Record<string, string> = { Accept: 'text/event-stream' };
      if (lastEventId !== null) headers['Last-Event-ID'] = lastEventId;
      const response = await fetchImpl(url(lastEventId), { method: 'GET', headers, signal, cache: 'no-store' });
      if (!response.ok) {
        if (response.status >= 400 && response.status < 500) {
          onStatus?.('failed', { httpStatus: response.status });
          return;
        }
        throw new Error(`HTTP ${response.status}`);
      }
      if (!response.body) throw new Error('Respuesta SSE sin cuerpo');
      onStatus?.('open');
      const parser = new SseParser((message) => {
        if (message.id !== null) lastEventId = message.id;
        delay = initialDelayMs; // llegan datos: la siguiente caída vuelve a empezar por el retardo mínimo
        onMessage(message);
      });
      parser.lastEventId = lastEventId;
      await readSseStream(response.body, parser, signal);
      lastEventId = parser.lastEventId;
    } catch (error) {
      if (signal.aborted || isAbortError(error)) break;
    }
    if (isComplete()) {
      onStatus?.('closed');
      return;
    }
    if (signal.aborted) break;
    onStatus?.('reconnecting', { retryInMs: delay });
    await sleep(delay, signal);
    delay = Math.min(delay * 2, maxDelayMs);
  }
  onStatus?.('closed');
}
