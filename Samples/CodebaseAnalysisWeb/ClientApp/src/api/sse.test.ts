import { describe, expect, it, vi } from 'vitest';
import { SseParser, connectResumableSse, readSseStream, type SseMessage } from './sse';
import { controlledStream, sseResponse, streamOf } from '../test/fixtures';

function collect() {
  const messages: SseMessage[] = [];
  const comments: string[] = [];
  const parser = new SseParser((m) => messages.push(m), (c) => comments.push(c));
  return { parser, messages, comments };
}

describe('entorno de test', () => {
  it('jsdom de Vitest expone fetch, Response, ReadableStream y TextDecoder de Node', () => {
    expect(typeof fetch).toBe('function');
    expect(typeof Response).toBe('function');
    expect(typeof ReadableStream).toBe('function');
    expect(typeof TextDecoder).toBe('function');
    expect(new Response('x').body).toBeInstanceOf(ReadableStream);
  });
});

describe('SseParser', () => {
  it('despacha un evento del análisis con id, nombre y data (formato §4.2)', () => {
    const { parser, messages } = collect();
    parser.push('id: 57\nevent: analysis\ndata: {"seq":57,"type":"tool.completed"}\n\n');
    expect(messages).toEqual([{ id: '57', event: 'analysis', data: '{"seq":57,"type":"tool.completed"}' }]);
  });

  it('reconstruye eventos partidos en cualquier punto entre chunks', () => {
    const text = 'id: 1\nevent: analysis\ndata: {"a":1}\n\nid: 2\nevent: analysis\ndata: {"b":2}\n\n';
    for (let cut = 1; cut < text.length; cut++) {
      const { parser, messages } = collect();
      parser.push(text.slice(0, cut));
      parser.push(text.slice(cut));
      expect(messages.map((m) => [m.id, m.data]), `corte en ${cut}`).toEqual([
        ['1', '{"a":1}'],
        ['2', '{"b":2}'],
      ]);
    }
  });

  it('acepta CRLF partido entre chunks y CR solo', () => {
    const { parser, messages } = collect();
    parser.push('event: chat\r');
    parser.push('\ndata: x\r');
    parser.push('\n\r');
    parser.push('\n');
    parser.push('data: y\r\r');
    parser.end();
    expect(messages.map((m) => [m.event, m.data])).toEqual([
      ['chat', 'x'],
      ['message', 'y'],
    ]);
  });

  it('entrega los comentarios (keep-alive) sin generar eventos', () => {
    const { parser, messages, comments } = collect();
    parser.push(': keep-alive\n\n');
    expect(messages).toHaveLength(0);
    expect(comments).toEqual(['keep-alive']);
  });

  it('une varias líneas data con salto de línea y quita solo un espacio inicial', () => {
    const { parser, messages } = collect();
    parser.push('data:  uno\ndata:dos\n\n');
    expect(messages[0].data).toBe(' uno\ndos');
  });

  it('el id persiste en los eventos siguientes sin id propio', () => {
    const { parser, messages } = collect();
    parser.push('id: 7\ndata: a\n\ndata: b\n\n');
    expect(messages.map((m) => m.id)).toEqual(['7', '7']);
    expect(parser.lastEventId).toBe('7');
  });

  it('descarta un evento incompleto al final del stream', () => {
    const { parser, messages } = collect();
    parser.push('data: completo\n\ndata: a medias');
    parser.end();
    expect(messages.map((m) => m.data)).toEqual(['completo']);
  });

  it('decodifica UTF-8 aunque un carácter multibyte quede partido entre chunks', async () => {
    const bytes = new TextEncoder().encode('data: análisis ✓\n\n');
    const cut = bytes.indexOf(0xc3) + 1; // parte la «á»
    const body = new ReadableStream<Uint8Array>({
      start(c) {
        c.enqueue(bytes.slice(0, cut));
        c.enqueue(bytes.slice(cut));
        c.close();
      },
    });
    const { parser, messages } = collect();
    await readSseStream(body, parser);
    expect(messages[0].data).toBe('análisis ✓');
  });
});

describe('readSseStream', () => {
  it('al abortar la señal cancela el lector y rechaza con AbortError', async () => {
    const ctl = controlledStream();
    const { parser, messages } = collect();
    const abort = new AbortController();
    const reading = readSseStream(ctl.stream, parser, abort.signal);
    ctl.push('data: uno\n\n');
    await vi.waitFor(() => expect(messages).toHaveLength(1));
    abort.abort();
    await expect(reading).rejects.toMatchObject({ name: 'AbortError' });
    expect(ctl.cancelled).toBe(true);
  });
});

describe('connectResumableSse', () => {
  const noSleep = () => Promise.resolve();

  it('reconecta tras un corte reanudando desde el último id con after y Last-Event-ID', async () => {
    const urls: string[] = [];
    const headers: (Record<string, string> | undefined)[] = [];
    const responses = [
      // Primera conexión: dos eventos y el servidor corta sin run.completed.
      () => sseResponse(streamOf(['id: 1\nevent: analysis\ndata: {"seq":1}\n\n', 'id: 2\nevent: analysis\ndata: {"seq":2}\n\n'])),
      // Segunda: sigue desde 3 y termina.
      () => sseResponse(streamOf(['id: 3\nevent: analysis\ndata: {"seq":3,"type":"run.completed"}\n\n'])),
    ];
    const fetchImpl = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      urls.push(String(input));
      headers.push(init?.headers as Record<string, string> | undefined);
      return responses[urls.length - 1]();
    });
    const received: string[] = [];
    let complete = false;
    const statuses: string[] = [];
    await connectResumableSse({
      url: (last) => `/api/analysis/r1/events?after=${last ?? 0}`,
      onMessage: (m) => {
        received.push(m.data);
        if (m.data.includes('run.completed')) complete = true;
      },
      isComplete: () => complete,
      onStatus: (s) => statuses.push(s),
      signal: new AbortController().signal,
      fetchImpl: fetchImpl as unknown as typeof fetch,
      sleep: noSleep,
    });
    expect(urls).toEqual(['/api/analysis/r1/events?after=0', '/api/analysis/r1/events?after=2']);
    expect(headers[0]?.['Last-Event-ID']).toBeUndefined();
    expect(headers[1]?.['Last-Event-ID']).toBe('2');
    expect(received).toHaveLength(3);
    expect(statuses[0]).toBe('connecting');
    expect(statuses).toContain('reconnecting');
    expect(statuses.at(-1)).toBe('closed');
  });

  it('no reconecta cuando el stream termina tras el evento final', async () => {
    const fetchImpl = vi.fn(async () =>
      sseResponse(streamOf(['id: 1\nevent: analysis\ndata: {"type":"run.completed"}\n\n'])),
    );
    let complete = false;
    await connectResumableSse({
      url: () => '/e',
      onMessage: () => {
        complete = true;
      },
      isComplete: () => complete,
      signal: new AbortController().signal,
      fetchImpl: fetchImpl as unknown as typeof fetch,
      sleep: noSleep,
    });
    expect(fetchImpl).toHaveBeenCalledTimes(1);
  });

  it('reintenta con espera exponencial ante errores de red y 5xx, con tope', async () => {
    const delays: number[] = [];
    let calls = 0;
    const fetchImpl = vi.fn(async () => {
      calls++;
      if (calls <= 5) {
        if (calls % 2) throw new TypeError('Failed to fetch');
        return new Response('boom', { status: 503 });
      }
      return sseResponse(streamOf(['data: fin\n\n']));
    });
    let complete = false;
    await connectResumableSse({
      url: () => '/e',
      onMessage: () => {
        complete = true;
      },
      isComplete: () => complete,
      signal: new AbortController().signal,
      fetchImpl: fetchImpl as unknown as typeof fetch,
      sleep: async (ms) => {
        delays.push(ms);
      },
      initialDelayMs: 1000,
      maxDelayMs: 4000,
    });
    expect(delays).toEqual([1000, 2000, 4000, 4000, 4000]);
  });

  it('un 404 es definitivo: informa failed y no reintenta', async () => {
    const fetchImpl = vi.fn(async () => new Response('{"error":"not_found"}', { status: 404 }));
    const statuses: string[] = [];
    await connectResumableSse({
      url: () => '/e',
      onMessage: () => undefined,
      isComplete: () => false,
      onStatus: (s) => statuses.push(s),
      signal: new AbortController().signal,
      fetchImpl: fetchImpl as unknown as typeof fetch,
      sleep: noSleep,
    });
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect(statuses).toEqual(['connecting', 'failed']);
  });

  it('abortar la señal detiene la conexión sin más intentos', async () => {
    const ctl = controlledStream();
    const abort = new AbortController();
    const fetchImpl = vi.fn(async () => sseResponse(ctl.stream));
    const done = connectResumableSse({
      url: () => '/e',
      onMessage: () => undefined,
      isComplete: () => false,
      signal: abort.signal,
      fetchImpl: fetchImpl as unknown as typeof fetch,
      sleep: noSleep,
    });
    await vi.waitFor(() => expect(fetchImpl).toHaveBeenCalledTimes(1));
    abort.abort();
    await done;
    expect(fetchImpl).toHaveBeenCalledTimes(1);
  });
});
