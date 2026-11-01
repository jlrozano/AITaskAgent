// Servidor simulado de chat-protocol/1 (node:http, sin dependencias). `npm run mock` (puerto CHAT_MOCK_PORT, 7182 por defecto).
//   POST /messages               MessageRequest | AnswerRequest → text/event-stream
//   POST /cancel                 CancelRequest                  → 202
//   GET  /turns/{id}/events      reanuda con `Last-Event-ID`
// Los turnos se producen aparte de las conexiones: si una conexión cae, el turno sigue y se puede reanudar.
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import type { ClientRequest, ServerEvent } from "../src/protocol.ts";
import { pickScript, scripts, type Draft } from "./scripts.ts";

interface Turn {
  id: string;
  events: ServerEvent[];
  finished: boolean;
  cancelled: boolean;
  waiters: (() => void)[];
}

export interface MockOptions {
  port?: number;
  /** Corta la primera conexión de cada turno tras N eventos (para probar la reanudación). */
  dropAfterEvents?: number;
}

export interface ChatMock {
  url: string;
  /** Cuerpos recibidos, en orden. */
  requests: ClientRequest[];
  close(): Promise<void>;
}

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

export async function startChatMock(opts: MockOptions = {}): Promise<ChatMock> {
  const turns = new Map<string, Turn>();
  const requests: ClientRequest[] = [];
  let counter = 0;
  const sockets = new Set<ServerResponse>();

  const notify = (t: Turn) => t.waiters.splice(0).forEach((w) => w());

  async function produce(t: Turn, scriptName: string, req: { text?: string }): Promise<void> {
    const emit = (e: Draft) => {
      t.events.push({ ...e, turnId: t.id, seq: t.events.length } as ServerEvent);
      notify(t);
    };
    for (const step of scripts[scriptName]!(req)) {
      if (step.delayMs) await sleep(step.delayMs);
      if (t.cancelled) break;
      if (step.hang) {
        // Espera a la cancelación; sin ella, sigue al cabo de 1 s (el guion `lento` acaba con `done`).
        await Promise.race([new Promise<void>((r) => t.waiters.push(r)), sleep(1000)]);
        if (t.cancelled) break;
        continue;
      }
      if (step.unknown) {
        // Evento fuera del protocolo, con la misma numeración: el cliente debe ignorarlo.
        t.events.push({ ...step.unknown, turnId: t.id, seq: t.events.length } as unknown as ServerEvent);
        notify(t);
        continue;
      }
      if (step.event) emit(step.event);
    }
    if (t.cancelled) emit({ type: "done", reason: "cancelled" });
    t.finished = true;
    notify(t);
  }

  async function stream(res: ServerResponse, t: Turn, from: number, dropAfter?: number): Promise<void> {
    res.writeHead(200, { "Content-Type": "text/event-stream; charset=utf-8", "Cache-Control": "no-cache", "X-Accel-Buffering": "no" });
    sockets.add(res);
    res.on("close", () => sockets.delete(res));
    let i = from;
    let written = 0;
    for (;;) {
      while (i < t.events.length) {
        const e = t.events[i]!;
        // Los eventos fuera del protocolo salen con un nombre propio; el cliente se guía por `data.type`.
        res.write(`event: ${e.type}\nid: ${t.id}:${e.seq}\ndata: ${JSON.stringify(e)}\n\n`);
        i++;
        written++;
        if (dropAfter !== undefined && written >= dropAfter && !t.finished) {
          res.destroy();
          return;
        }
      }
      if (t.finished || res.destroyed) break;
      await new Promise<void>((r) => t.waiters.push(r));
    }
    res.end();
  }

  async function body(req: IncomingMessage): Promise<unknown> {
    const chunks: Buffer[] = [];
    for await (const c of req) chunks.push(c as Buffer);
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
  }

  const json = (res: ServerResponse, status: number, v: unknown) => {
    res.writeHead(status, { "Content-Type": "application/json", "Access-Control-Allow-Origin": "*" }).end(JSON.stringify(v));
  };

  const server: Server = createServer(async (req, res) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    if (req.method === "OPTIONS") {
      res.writeHead(204, { "Access-Control-Allow-Origin": "*", "Access-Control-Allow-Methods": "GET,POST,OPTIONS", "Access-Control-Allow-Headers": "Content-Type, Last-Event-ID" }).end();
      return;
    }
    try {
      if (req.method === "POST" && url.pathname === "/messages") {
        const b = (await body(req)) as ClientRequest;
        requests.push(b);
        if (b.type !== "message" && b.type !== "answer") return json(res, 400, { code: "request.invalid", message: "Se esperaba message o answer." });
        const text = b.type === "message" ? b.text : b.approved !== undefined ? `approved=${b.approved}` : b.answers ? `answers=${b.answers.map((a) => a.join("+")).join("|")}` : `text=${b.text ?? ""}`;
        const t: Turn = { id: `t${++counter}`, events: [], finished: false, cancelled: false, waiters: [] };
        turns.set(t.id, t);
        void produce(t, b.type === "answer" ? "respuesta" : pickScript(b.text), { text });
        return void (await stream(res, t, 0, opts.dropAfterEvents));
      }
      if (req.method === "POST" && url.pathname === "/cancel") {
        const b = (await body(req)) as ClientRequest;
        requests.push(b);
        const id = b.type === "cancel" && b.turnId ? b.turnId : [...turns.keys()].at(-1);
        const t = id ? turns.get(id) : undefined;
        if (t && !t.finished) {
          t.cancelled = true;
          notify(t);
        }
        return json(res, 202, { cancelled: !!t && t.cancelled, ...(id ? { turnId: id } : {}) });
      }
      const m = /^\/turns\/([^/]+)\/events$/.exec(url.pathname);
      if (req.method === "GET" && m) {
        const t = turns.get(decodeURIComponent(m[1]!));
        if (!t) return json(res, 404, { code: "turn.notFound", message: "Turno desconocido." });
        const last = /:(\d+)$/.exec(String(req.headers["last-event-id"] ?? ""));
        return void (await stream(res, t, last ? Number(last[1]) + 1 : 0));
      }
      json(res, 404, { code: "not.found", message: url.pathname });
    } catch (e) {
      if (!res.headersSent) json(res, 400, { code: "request.invalid", message: e instanceof Error ? e.message : "Cuerpo no válido." });
    }
  });

  await new Promise<void>((r) => server.listen(opts.port ?? 0, "127.0.0.1", r));
  const port = (server.address() as AddressInfo).port;
  return {
    url: `http://127.0.0.1:${port}`,
    requests,
    close: () =>
      new Promise<void>((r) => {
        sockets.forEach((s) => s.destroy());
        server.close(() => r());
      }),
  };
}

if (import.meta.url === `file:///${process.argv[1]?.replace(/\\/g, "/")}` || process.argv[1]?.endsWith("mock-server/server.ts") || process.argv[1]?.endsWith("mock-server\\server.ts")) {
  const mock = await startChatMock({ port: Number(process.env.CHAT_MOCK_PORT ?? 7182) });
  console.log(`chat-protocol/1 mock en ${mock.url} (guiones: ${Object.keys(scripts).join(", ")})`);
}
