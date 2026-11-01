// Analizador de text/event-stream (WHATWG HTML §9.2) sobre fetch (EventSource no admite POST ni cabeceras). Robusto ante
// trozos que cortan líneas, CRLF/CR/LF, BOM, comentarios (`: ping`) y varias líneas `data:`. Copia acotada del analizador
// del diseñador: este paquete no depende del diseñador.
export interface SseEvent {
  event: string;
  id?: string;
  /** `data` interpretado como JSON (o el texto tal cual si no lo es). */
  data: unknown;
}

export class SseParser {
  private buffer = "";
  private event = "";
  private data: string[] = [];
  private id: string | undefined;
  private first = true;

  push(chunk: string): SseEvent[] {
    if (this.first && chunk.length > 0) {
      this.first = false;
      if (chunk.charCodeAt(0) === 0xfeff) chunk = chunk.slice(1);
    }
    this.buffer += chunk;
    const out: SseEvent[] = [];
    for (;;) {
      const m = /\r\n|\r|\n/.exec(this.buffer);
      if (!m) break;
      if (m[0] === "\r" && m.index === this.buffer.length - 1) break; // posible primera mitad de CRLF
      const line = this.buffer.slice(0, m.index);
      this.buffer = this.buffer.slice(m.index + m[0].length);
      const ev = this.line(line);
      if (ev) out.push(ev);
    }
    return out;
  }

  end(): SseEvent[] {
    const out: SseEvent[] = [];
    if (this.buffer.length > 0) {
      const ev = this.line(this.buffer.replace(/\r$/, ""));
      this.buffer = "";
      if (ev) out.push(ev);
    }
    const last = this.dispatch();
    if (last) out.push(last);
    return out;
  }

  private line(line: string): SseEvent | null {
    if (line === "") return this.dispatch();
    if (line.startsWith(":")) return null;
    const colon = line.indexOf(":");
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);
    if (field === "event") this.event = value;
    else if (field === "data") this.data.push(value);
    else if (field === "id" && !value.includes("\0")) this.id = value;
    return null;
  }

  private dispatch(): SseEvent | null {
    if (this.data.length === 0) {
      this.event = "";
      return null;
    }
    const raw = this.data.join("\n");
    let data: unknown = raw;
    try {
      data = JSON.parse(raw);
    } catch {
      /* texto plano */
    }
    const ev: SseEvent = { event: this.event || "message", data, ...(this.id !== undefined ? { id: this.id } : {}) };
    this.event = "";
    this.data = [];
    return ev;
  }
}

/** Lee un cuerpo SSE como iterador asíncrono; abortar la señal cancela la lectura. */
export async function* parseSse(body: ReadableStream<Uint8Array>, signal?: AbortSignal): AsyncGenerator<SseEvent> {
  const reader = body.getReader();
  const decoder = new TextDecoder("utf-8");
  const parser = new SseParser();
  const onAbort = () => void reader.cancel().catch(() => undefined);
  signal?.addEventListener("abort", onAbort, { once: true });
  try {
    for (;;) {
      if (signal?.aborted) throw new DOMException("Cancelado", "AbortError");
      const { done, value } = await reader.read();
      if (done) break;
      for (const ev of parser.push(decoder.decode(value, { stream: true }))) yield ev;
    }
    if (signal?.aborted) throw new DOMException("Cancelado", "AbortError");
    for (const ev of parser.push(decoder.decode())) yield ev;
    for (const ev of parser.end()) yield ev;
  } finally {
    signal?.removeEventListener("abort", onAbort);
    reader.releaseLock?.();
  }
}
