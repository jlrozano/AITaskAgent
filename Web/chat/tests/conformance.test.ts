// Conformidad con chat-protocol/1: (a) cada evento del mock cumple el esquema y el orden garantizado; (b) el cliente
// y el reductor procesan todos los guiones (texto, razonamiento, herramientas, propuesta, pregunta, error, cancelación,
// eventos desconocidos, reanudación); (c) los cuerpos POST que envía el cliente cumplen el esquema.
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { ChatClient } from "../src/client.ts";
import type { ServerEvent } from "../src/protocol.ts";
import { chatReducer, initialState, type ChatState } from "../src/state.ts";
import { scripts } from "../mock-server/scripts.ts";
import { startChatMock, type ChatMock } from "../mock-server/server.ts";
import { asEvents, errorsOf, isFuture, readTurn, validateClientRequest, validateServerEvent } from "./kit.ts";

let mock: ChatMock;
beforeAll(async () => {
  mock = await startChatMock();
});
afterAll(() => mock.close());

const KEYWORD: Record<string, string> = { respuesta: "texto" };

async function runClient(base: string, text: string, opts: Partial<ConstructorParameters<typeof ChatClient>[0]> = {}) {
  const client = new ChatClient({ baseUrl: base, reconnectDelayMs: 5, ...opts });
  let state: ChatState = chatReducer(initialState, { type: "assistant.begin", id: "a1" });
  const events: ServerEvent[] = [];
  await client.send({ type: "message", text }, (event) => {
    events.push(event);
    state = chatReducer(state, { type: "event", event });
  });
  return { state, events, client };
}

describe("(a) los eventos del servidor cumplen el esquema", () => {
  for (const name of Object.keys(scripts)) {
    it(`guion «${name}»`, async () => {
      const raw = await readTurn(mock.url, { type: "message", text: KEYWORD[name] ?? name });
      expect(raw.length).toBeGreaterThan(1);
      const events = asEvents(raw);
      for (const e of events) {
        const ok = validateServerEvent(e);
        expect(ok, `${(e as { type: string }).type}: ${errorsOf(validateServerEvent)}`).toBe(true);
      }
      // Cabecera SSE coherente: `event` = type, `id` = turnId:seq.
      for (const r of raw.filter((x) => !isFuture(x))) {
        const d = r.data as ServerEvent;
        expect(r.name).toBe(d.type);
        expect(r.id).toBe(`${d.turnId}:${d.seq}`);
      }
      // Orden: turn.start primero, done último (y único), seq creciente desde 0 sin repetir.
      expect(events[0]!.type).toBe("turn.start");
      expect(events.at(-1)!.type).toBe("done");
      expect(events.filter((e) => e.type === "done")).toHaveLength(1);
      const seqs = raw.map((r) => (r.data as ServerEvent).seq);
      expect(seqs).toEqual(seqs.map((_, i) => i));
      // `error` va seguido de `done`.
      const iErr = events.findIndex((e) => e.type === "error");
      if (iErr >= 0) expect(events[iErr + 1]!.type).toBe("done");
    });
  }

  it("rechaza lo que no cumple el protocolo", () => {
    expect(validateServerEvent({ type: "text.delta", turnId: "t1", seq: 0 })).toBe(false); // falta text
    expect(validateServerEvent({ type: "done", turnId: "t1", seq: -1, reason: "completed" })).toBe(false);
    expect(validateServerEvent({ type: "question", turnId: "t1", seq: 0, callId: "c", question: "?", kind: "otra" })).toBe(false);
    expect(validateServerEvent({ type: "tool.completed", turnId: "t1", seq: 0, callId: "c", name: "x" })).toBe(false); // falta ok
    expect(validateServerEvent({ type: "proposal", turnId: "t1", seq: 0 })).toBe(false); // faltan operations
  });
  it("acepta propiedades nuevas (compatibilidad hacia delante) y rechaza tipos desconocidos como evento definido", () => {
    expect(validateServerEvent({ type: "text.delta", turnId: "t1", seq: 0, text: "x", nueva: 1 })).toBe(true);
    expect(validateServerEvent({ type: "futuro.evento", turnId: "t1", seq: 0 })).toBe(false);
  });
});

describe("(b) el cliente procesa los guiones", () => {
  it("texto: concatena y cierra", async () => {
    const { state, events } = await runClient(mock.url, "texto");
    const m = state.messages[0]!;
    expect(m.status).toBe("done");
    expect(m.model).toBe("mock-model");
    expect(m.parts[0]).toMatchObject({ type: "text", text: expect.stringContaining("**soy**") });
    expect(m.parts.filter((p) => p.type === "text")).toHaveLength(1);
    expect(m.parts.find((p) => p.type === "usage")).toMatchObject({ counts: { inputTokens: 100, outputTokens: 20 }, estimatedCost: 0.0012 });
    expect(events.at(-1)!.type).toBe("done");
    expect(state.busy).toBe(false);
  });

  it("razonamiento: parte aparte de la respuesta", async () => {
    const { state } = await runClient(mock.url, "razonamiento");
    const parts = state.messages[0]!.parts;
    expect(parts[0]).toMatchObject({ type: "thinking", text: "Primero miro el esquema. Luego decido la consulta." });
    expect(parts[1]!.type).toBe("text");
  });

  it("herramientas: línea por llamada con su resultado y el error", async () => {
    const { state } = await runClient(mock.url, "herramientas");
    const tools = state.messages[0]!.parts.filter((p) => p.type === "tool");
    expect(tools).toMatchObject([
      { id: "call_1", name: "search_schema", status: "ok", summary: "3 objetos", label: "Buscando en el esquema" },
      { id: "call_2", name: "validate_sql", status: "error", errorCode: "sql.rejected" },
    ]);
    expect(state.messages[0]!.parts.some((p) => p.type === "notice" && p.code === "sampleRows.sent")).toBe(true);
  });

  it("agentes: partes agent con sus herramientas (output, truncado y stepId) y valida el esquema de los eventos nuevos", async () => {
    const { state, events } = await runClient(mock.url, "agentes");
    expect(events.map((e) => e.type)).toEqual(expect.arrayContaining(["step.started", "step.completed"]));
    const parts = state.messages[0]!.parts;
    expect(parts.filter((p) => p.type === "agent")).toMatchObject([
      { id: "s1", name: "plan", label: "Planificando", kind: "llm", attempt: 1, status: "ok", elapsedMs: 40, summary: "esquema localizado", detail: "Decide qué tablas consultar", model: "mock-model", inputTokens: 1200, outputTokens: 340, costUsd: 0.0123 },
      { id: "s2", kind: "llm", attempt: 2, status: "error", errorCode: "sql.rejected" },
      { id: "s3", kind: "step", status: "ok", summary: "plantilla compilada" },
    ]);
    expect(parts.filter((p) => p.type === "tool")).toMatchObject([
      { id: "call_1", stepId: "s1", output: { total: 2 } },
      { id: "call_2", stepId: "s2", output: "texto plano que no es JSON", outputTruncated: true },
    ]);
  });

  it("informe: 3 pasos deterministas y 2 agentes IA con herramientas, todo conforme al esquema", async () => {
    const { state } = await runClient(mock.url, "informe");
    const parts = state.messages[0]!.parts;
    const nodes = parts.filter((p) => p.type === "agent");
    expect(nodes.map((n) => (n as { kind: string }).kind)).toEqual(["step", "llm", "step", "llm", "step"]);
    expect(nodes.every((n) => (n as { status: string }).status === "ok")).toBe(true);
    expect(parts.filter((p) => p.type === "tool").map((t) => (t as { stepId?: string }).stepId)).toEqual(["a1", "a1", "a2"]);
  });

  it("agentes: el esquema acepta detail y los campos de modelo, tokens y coste", () => {
    const base = { turnId: "t1", seq: 0 };
    expect(validateServerEvent({ ...base, type: "step.started", stepId: "s", name: "n", kind: "step", attempt: 1, detail: "qué hace" })).toBe(true);
    expect(validateServerEvent({ ...base, type: "step.completed", stepId: "s", ok: true, model: "m", inputTokens: 1, outputTokens: 2, costUsd: 0.1 })).toBe(true);
  });

  it("agentes: el esquema acepta y exige los campos de step.started/completed", () => {
    const base = { turnId: "t1", seq: 0 };
    expect(validateServerEvent({ ...base, type: "step.started", stepId: "s", name: "n", kind: "llm", attempt: 1 })).toBe(true);
    expect(validateServerEvent({ ...base, type: "step.started", stepId: "s", name: "n", kind: "otro", attempt: 1 })).toBe(false);
    expect(validateServerEvent({ ...base, type: "step.started", stepId: "s", name: "n", kind: "llm", attempt: 0 })).toBe(false);
    expect(validateServerEvent({ ...base, type: "step.completed", stepId: "s", ok: true, elapsedMs: 3 })).toBe(true);
    expect(validateServerEvent({ ...base, type: "step.completed", stepId: "s", ok: true })).toBe(true);
    expect(validateServerEvent({ ...base, type: "step.completed", stepId: "s", elapsedMs: 3 })).toBe(false);
    expect(validateServerEvent({ ...base, type: "tool.completed", callId: "c", name: "x", ok: true, output: { a: 1 }, outputTruncated: true, stepId: "s" })).toBe(true);
  });

  it("propuesta: queda pendiente con sus diferencias", async () => {
    const { state } = await runClient(mock.url, "propuesta");
    const p = state.messages[0]!.parts.find((x) => x.type === "proposal");
    expect(p).toMatchObject({ status: "pending", proposal: { summary: "Total en negrita y a la derecha" } });
    expect(p?.type === "proposal" && p.proposal.changes).toHaveLength(3);
  });

  it("pregunta: cierra el turno y la respuesta entra como mensaje nuevo (answer)", async () => {
    const first = await runClient(mock.url, "pregunta");
    const q = first.state.messages[0]!.parts.find((p) => p.type === "question");
    expect(q).toMatchObject({ question: { callId: "ask_1", kind: "choice" } });
    expect(first.events.at(-1)).toMatchObject({ type: "done", reason: "question" });
    expect(first.state.busy).toBe(false);

    const client = new ChatClient({ baseUrl: mock.url });
    const events: ServerEvent[] = [];
    await client.send({ type: "answer", callId: "ask_1", answers: [["emisión"]] }, (e) => events.push(e));
    const text = events.filter((e) => e.type === "text.delta").map((e) => (e as { text: string }).text).join("");
    expect(text).toBe("Recibido: answers=emisión");
    expect(events.at(-1)).toMatchObject({ type: "done", reason: "completed" });
  });

  it("aprobación: approved llega al servidor", async () => {
    const client = new ChatClient({ baseUrl: mock.url });
    const events: ServerEvent[] = [];
    await client.send({ type: "answer", callId: "ask_2", approved: false }, (e) => events.push(e));
    expect(events.filter((e) => e.type === "text.delta").map((e) => (e as { text: string }).text).join("")).toBe("Recibido: approved=false");
  });

  it("error: parte de error y mensaje en estado error", async () => {
    const { state } = await runClient(mock.url, "error");
    const m = state.messages[0]!;
    expect(m.status).toBe("error");
    expect(m.parts.find((p) => p.type === "error")).toMatchObject({ code: "assistant.providerUnavailable", retryable: true });
    expect(state.busy).toBe(false);
  });

  it("cancelación: POST /cancel y done «cancelled»", async () => {
    const client = new ChatClient({ baseUrl: mock.url });
    let state: ChatState = chatReducer(initialState, { type: "assistant.begin", id: "a1" });
    const run = client.send({ type: "message", text: "lento" }, (event) => {
      state = chatReducer(state, { type: "event", event });
      if (event.type === "text.delta" && event.text === "trozo2 ") void client.cancel();
    });
    await run;
    expect(state.messages[0]!.status).toBe("cancelled");
    expect(state.busy).toBe(false);
    expect(mock.requests.some((r) => r.type === "cancel")).toBe(true);
    expect(state.messages[0]!.parts.filter((p) => p.type === "text")[0]!).toMatchObject({ text: expect.not.stringContaining("trozo30") });
  });

  it("cancelación sin servidor que conteste: se corta y cierra como cancelado", async () => {
    // fetch que nunca cierra el flujo y no atiende /cancel
    const neverEnding = (async (url: string | URL | Request) => {
      if (String(url).endsWith("/cancel")) throw new Error("sin red");
      const body = new ReadableStream<Uint8Array>({ start: (c) => c.enqueue(new TextEncoder().encode(`event: turn.start\nid: t9:0\ndata: ${JSON.stringify({ type: "turn.start", turnId: "t9", seq: 0 })}\n\n`)) });
      return new Response(body, { status: 200, headers: { "Content-Type": "text/event-stream" } });
    }) as typeof fetch;
    const client = new ChatClient({ baseUrl: "http://x", fetch: neverEnding, cancelGraceMs: 20 });
    const events: ServerEvent[] = [];
    await client.send({ type: "message", text: "hola" }, (e) => {
      events.push(e);
      if (e.type === "turn.start") void client.cancel();
    });
    expect(events.at(-1)).toMatchObject({ type: "done", reason: "cancelled", turnId: "t9" });
  });

  it("eventos desconocidos y propiedades nuevas se ignoran", async () => {
    const { state, events } = await runClient(mock.url, "desconocido");
    expect(events.map((e) => e.type)).toEqual(["turn.start", "text.delta", "done"]);
    expect(state.messages[0]!.parts[0]).toMatchObject({ type: "text", text: "ignoré lo que no conozco" });
  });

  it("reanuda el flujo cortado con Last-Event-ID sin duplicar ni perder eventos", async () => {
    const flaky = await startChatMock({ dropAfterEvents: 3 });
    try {
      const { state, events } = await runClient(flaky.url, "texto");
      const seqs = events.map((e) => e.seq);
      expect(seqs).toEqual(seqs.map((_, i) => i));
      expect(state.messages[0]!.status).toBe("done");
      expect(state.messages[0]!.parts[0]).toMatchObject({ text: "Hola, **soy** el asistente simulado.\n\n- primero\n- segundo\n" });
    } finally {
      await flaky.close();
    }
  });

  it("sin servidor: error connection.lost + done, sin lanzar", async () => {
    const down = await startChatMock();
    const url = down.url;
    await down.close();
    const { state, events } = await runClient(url, "texto", { maxReconnects: 1 });
    expect(events.map((e) => e.type)).toEqual(["error", "done"]);
    expect(events[0]).toMatchObject({ code: "connection.lost", retryable: true });
    expect(state.messages[0]!.status).toBe("error");
  });

  it("HTTP 4xx: error sin reintentar", async () => {
    const f = (async () => new Response("{}", { status: 413 })) as typeof fetch;
    const client = new ChatClient({ baseUrl: "http://x", fetch: f });
    const events: ServerEvent[] = [];
    await client.send({ type: "message", text: "x" }, (e) => events.push(e));
    expect(events[0]).toMatchObject({ type: "error", code: "http.413", retryable: false });
  });

  it("un turno a la vez", async () => {
    const client = new ChatClient({ baseUrl: mock.url });
    const first = client.send({ type: "message", text: "lento" }, () => undefined);
    await expect(client.send({ type: "message", text: "x" }, () => undefined)).rejects.toThrow();
    await client.cancel();
    await first;
  });
});

describe("(c) los POST del cliente cumplen el esquema", () => {
  it("todo lo que recibió el mock es un ClientRequest válido", () => {
    expect(mock.requests.length).toBeGreaterThan(5);
    for (const r of mock.requests) expect(validateClientRequest(r), `${JSON.stringify(r)}: ${errorsOf(validateClientRequest)}`).toBe(true);
  });
  it("rechaza cuerpos que no cumplen", () => {
    expect(validateClientRequest({ type: "message", text: "" })).toBe(false);
    expect(validateClientRequest({ type: "answer", callId: "c" })).toBe(false); // sin approved, answers ni text
    expect(validateClientRequest({ type: "answer", callId: "c", approved: true })).toBe(true);
    expect(validateClientRequest({ type: "cancel" })).toBe(true);
    expect(validateClientRequest({ type: "otro" })).toBe(false);
  });
});
