// @vitest-environment jsdom
// hook `transformEvent` (modifica, descarta, undefined, lanza, reanudación, `done` protegido), filtros de visualización
// `showThinking` / `showToolCalls` (solos y combinados con el hook) y textos de presentación `display` (inicio → fin, respaldo).
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { ChatClient, ChatView, MessageList, applyTransform, chatReducer, defaultLabels, initialState, useChat, type ChatMessage, type ChatState, type RendererContext, type ServerEvent, type TransformEvent } from "../src/index.ts";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

type Ev = Record<string, unknown>;
const ev = (seq: number, e: Ev): ServerEvent => ({ turnId: "t1", seq, ...e }) as ServerEvent;
const turn = (...events: Ev[]): ServerEvent[] => [ev(0, { type: "turn.start" }), ...events.map((e, i) => ev(i + 1, e)), ev(events.length + 1, { type: "done", reason: "completed" })];

function sse(events: ServerEvent[], keepOpen = false): Response {
  const enc = new TextEncoder();
  return new Response(
    new ReadableStream({
      start(c) {
        for (const e of events) c.enqueue(enc.encode(`event: ${e.type}\nid: ${e.turnId}:${e.seq}\ndata: ${JSON.stringify(e)}\n\n`));
        if (!keepOpen) c.close();
      },
    }),
    { status: 200, headers: { "content-type": "text/event-stream" } },
  );
}

/** fetch falso: cada llamada consume la siguiente respuesta; guarda las URL. */
function fakeFetch(...responses: Array<() => Response>) {
  const urls: string[] = [];
  const f = vi.fn(async (url: string | URL | Request) => {
    urls.push(String(url));
    const next = responses.shift();
    if (!next) throw new Error("sin más respuestas");
    return next();
  });
  return { f: f as unknown as typeof fetch, urls };
}

const req = { type: "message" as const, text: "hola" };
async function collect(client: ChatClient, hook?: TransformEvent): Promise<ServerEvent[]> {
  const out: ServerEvent[] = [];
  await client.send(req, (e) => out.push(e), hook ? { transformEvent: hook } : {});
  return out;
}

describe("applyTransform", () => {
  const notice = ev(1, { type: "notice", code: "c", message: "hola" });

  it("recibe una copia profunda: mutarla y devolver undefined no cambia nada; devolver el evento modificado sí", () => {
    const seen: ServerEvent[] = [];
    const mutate: TransformEvent = (e) => {
      seen.push(e);
      (e as { message: string }).message = "mutado";
      return undefined;
    };
    const r1 = applyTransform(mutate, notice);
    expect(r1).toBe(notice);
    expect((notice as { message: string }).message).toBe("hola");
    expect(seen[0]).not.toBe(notice);
    expect(seen[0]).toEqual({ ...notice, message: "mutado" });
    const r2 = applyTransform((e) => ({ ...e, message: "nuevo" }) as ServerEvent, notice);
    expect(r2).toMatchObject({ message: "nuevo" });
    expect((notice as { message: string }).message).toBe("hola");
  });

  it("null descarta; sin hook pasa tal cual", () => {
    expect(applyTransform(() => null, notice)).toBeNull();
    expect(applyTransform(undefined, notice)).toBe(notice);
  });

  it("si el hook lanza, el evento pasa intacto y se registra el error", () => {
    const err = vi.spyOn(console, "error").mockImplementation(() => undefined);
    expect(applyTransform(() => { throw new Error("boom"); }, notice)).toBe(notice);
    expect(err).toHaveBeenCalledOnce();
  });

  it("un resultado que no es un evento del protocolo se ignora (pasa el original)", () => {
    const err = vi.spyOn(console, "error").mockImplementation(() => undefined);
    expect(applyTransform((() => ({ foo: 1 })) as unknown as TransformEvent, notice)).toBe(notice);
    expect(applyTransform((() => "x") as unknown as TransformEvent, notice)).toBe(notice);
    expect(err).toHaveBeenCalledTimes(2);
  });

  it("`done` está protegido: ni se descarta ni cambia de tipo (aviso en consola); uno modificado sigue siendo `done`", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    const done = ev(9, { type: "done", reason: "completed" });
    expect(applyTransform(() => null, done)).toBe(done);
    expect(applyTransform(() => notice, done)).toBe(done);
    expect(warn).toHaveBeenCalledTimes(2);
    expect(applyTransform((e) => ({ ...e, reason: "otro" }) as ServerEvent, done)).toMatchObject({ type: "done", reason: "otro" });
  });

  it("descartar `error` o `turn.start` se permite pero avisa", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    expect(applyTransform(() => null, ev(1, { type: "error", code: "x", message: "m" }))).toBeNull();
    expect(applyTransform(() => null, ev(0, { type: "turn.start" }))).toBeNull();
    expect(warn).toHaveBeenCalledTimes(2);
  });
});

describe("ChatClient con transformEvent", () => {
  it("modifica, descarta y deja pasar; recibe copias", async () => {
    const { f } = fakeFetch(() => sse(turn({ type: "notice", code: "a", message: "uno" }, { type: "notice", code: "b", message: "dos" }, { type: "text.delta", text: "hola" })));
    const client = new ChatClient({ baseUrl: "/x", fetch: f });
    const out = await collect(client, (e) => (e.type === "notice" ? (e.code === "a" ? { ...e, message: "UNO" } : null) : undefined));
    expect(out.map((e) => e.type)).toEqual(["turn.start", "notice", "text.delta", "done"]);
    expect(out[1]).toMatchObject({ code: "a", message: "UNO" });
  });

  it("la opción del constructor funciona y la de `send` gana", async () => {
    const mk = () => fakeFetch(() => sse(turn({ type: "notice", code: "a", message: "m" })));
    const a = new ChatClient({ baseUrl: "/x", fetch: mk().f, transformEvent: () => null });
    expect((await collect(a)).map((e) => e.type)).toEqual(["done"]); // el hook descarta todo salvo `done` (protegido)
    const b = new ChatClient({ baseUrl: "/x", fetch: mk().f, transformEvent: () => null });
    expect((await collect(b, () => undefined)).map((e) => e.type)).toEqual(["turn.start", "notice", "done"]);
  });

  it("también se aplica a los eventos reanudados (GET .../events)", async () => {
    const all = turn({ type: "notice", code: "a", message: "uno" }, { type: "notice", code: "b", message: "dos" });
    const { f, urls } = fakeFetch(
      () => sse(all.slice(0, 2)), // turn.start + primer aviso; corte sin done
      () => sse(all.slice(2)),
    );
    const client = new ChatClient({ baseUrl: "/x", fetch: f, reconnectDelayMs: 1 });
    const seen: string[] = [];
    const out = await collect(client, (e) => {
      seen.push(`${e.type}:${e.seq}`);
      return e.type === "notice" ? { ...e, message: (e as { message: string }).message.toUpperCase() } : undefined;
    });
    expect(urls[1]).toContain("/turns/t1/events");
    expect(out.filter((e) => e.type === "notice").map((e) => (e as { message: string }).message)).toEqual(["UNO", "DOS"]);
    expect(seen).toEqual(["turn.start:0", "notice:1", "notice:2", "done:3"]);
  });

  it("un `done` descartado por el hook no deja el turno abierto: llega igualmente", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => undefined);
    const { f } = fakeFetch(() => sse(turn({ type: "text.delta", text: "x" })));
    const client = new ChatClient({ baseUrl: "/x", fetch: f });
    const out = await collect(client, () => null);
    expect(out.map((e) => e.type)).toEqual(["done"]);
  });

  it("los eventos sintéticos del cliente (error de conexión, done) no pasan por el hook", async () => {
    const hook = vi.fn(() => null);
    const f = (async () => new Response("boom", { status: 400 })) as unknown as typeof fetch;
    const client = new ChatClient({ baseUrl: "/x", fetch: f });
    const out = await collect(client, hook);
    expect(out.map((e) => e.type)).toEqual(["error", "done"]);
    expect(hook).not.toHaveBeenCalled();
  });

  it("un hook que lanza no rompe el turno", async () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    const { f } = fakeFetch(() => sse(turn({ type: "text.delta", text: "x" })));
    const out = await collect(new ChatClient({ baseUrl: "/x", fetch: f }), () => { throw new Error("x"); });
    expect(out.map((e) => e.type)).toEqual(["turn.start", "text.delta", "done"]);
  });
});

function run(events: Array<Record<string, unknown>>): ChatState {
  let s = chatReducer(initialState, { type: "assistant.begin", id: "a" });
  events.forEach((e, seq) => {
    s = chatReducer(s, { type: "event", event: { turnId: "t1", seq, ...e } as ServerEvent });
  });
  return s;
}
const ctx = (over: Partial<RendererContext> = {}): RendererContext => ({ labels: defaultLabels, busy: false, applyProposal: vi.fn(), discardProposal: vi.fn(), answerQuestion: vi.fn(), ...over });
const asst = (parts: ChatMessage["parts"]): ChatMessage => ({ id: "m1", role: "assistant", status: "done", parts });

describe("filtros de visualización", () => {
  const parts = (): ChatMessage["parts"] => [
    { type: "thinking", id: "th", text: "pensando fuerte" },
    { type: "agent", id: "s1", name: "plan", kind: "llm", attempt: 1, status: "ok", elapsedMs: 10 },
    { type: "tool", id: "c1", name: "search", status: "ok", stepId: "s1" },
    { type: "tool", id: "c2", name: "suelta", status: "ok" },
    { type: "agent", id: "s2", name: "compila", kind: "step", attempt: 1, status: "ok", elapsedMs: 5 },
    { type: "text", id: "tx", text: "respuesta" },
  ];
  const show = (p: { showThinking?: boolean; showToolCalls?: boolean } = {}) => render(<MessageList messages={[asst(parts())]} ctx={ctx()} {...p} />);
  const summary = () => screen.getByTestId("steps-summary").textContent;

  it("por defecto se ve todo", () => {
    show();
    expect(screen.getByText("pensando fuerte")).toBeTruthy();
    expect(screen.getByTestId("step-search")).toBeTruthy();
    expect(summary()).toBe("Completado · 4 acciones · 15 ms");
  });

  it("showThinking=false oculta el razonamiento y nada más", () => {
    show({ showThinking: false });
    expect(screen.queryByText("pensando fuerte")).toBeNull();
    expect(screen.getByTestId("step-search")).toBeTruthy();
    expect(screen.getByText("respuesta")).toBeTruthy();
  });

  it("showToolCalls=false oculta los nodos de tool y su recuento; los pasos siguen", () => {
    show({ showToolCalls: false });
    expect(screen.queryByTestId("step-search")).toBeNull();
    expect(screen.queryByTestId("step-suelta")).toBeNull();
    expect(screen.getByTestId("agent-s1")).toBeTruthy();
    expect(screen.getByTestId("agent-s2")).toBeTruthy();
    expect(screen.queryByTestId("node-tools-toggle")).toBeNull();
    expect(summary()).toBe("Completado · 2 acciones · 15 ms");
  });

  it("un paso `kind: tool` también cuenta como nodo de tool", () => {
    render(<MessageList messages={[asst([{ type: "agent", id: "k", name: "k", kind: "tool", attempt: 1, status: "ok" }, { type: "agent", id: "s", name: "s", kind: "step", attempt: 1, status: "ok" }])]} ctx={ctx()} showToolCalls={false} />);
    expect(screen.queryByTestId("agent-k")).toBeNull();
    expect(screen.getByTestId("agent-s")).toBeTruthy();
  });

  it("el estado del reductor conserva los datos aunque se oculten", () => {
    const s = run([{ type: "thinking.delta", text: "x" }, { type: "tool.started", callId: "c", name: "q" }]);
    expect(s.messages[0]!.parts.map((p) => p.type)).toEqual(["thinking", "tool"]);
    render(<MessageList messages={s.messages} ctx={ctx()} showThinking={false} showToolCalls={false} />);
    expect(screen.queryByTestId("step-q")).toBeNull();
  });

  it("los filtros también se leen de ctx y la prop los gana", () => {
    render(<MessageList messages={[asst(parts())]} ctx={ctx({ showThinking: false })} />);
    expect(screen.queryByText("pensando fuerte")).toBeNull();
    cleanup();
    render(<MessageList messages={[asst(parts())]} ctx={ctx({ showThinking: false })} showThinking />);
    expect(screen.getByText("pensando fuerte")).toBeTruthy();
  });
});

/** ChatView real con ChatClient y un servidor falso. */
function Harness({ client, hook, optionHook, ...view }: { client: ChatClient; hook?: TransformEvent; optionHook?: TransformEvent; showThinking?: boolean; showToolCalls?: boolean }) {
  const chat = useChat({ client, ...(optionHook ? { transformEvent: optionHook } : {}) });
  return <ChatView chat={chat} {...(hook ? { transformEvent: hook } : {})} {...view} />;
}
async function say(text = "hola") {
  fireEvent.change(screen.getByRole("textbox"), { target: { value: text } });
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Enviar" }));
  });
}

describe("ChatView y useChat con transformEvent + filtros", () => {
  const events = () => turn({ type: "thinking.delta", text: "razono" }, { type: "tool.started", callId: "c1", name: "search", input: { q: 1 } }, { type: "tool.completed", callId: "c1", name: "search", ok: true, summary: "bien" }, { type: "notice", code: "n", message: "secreto" }, { type: "text.delta", text: "listo" });
  const mk = () => new ChatClient({ baseUrl: "/x", fetch: fakeFetch(() => sse(events())).f });

  it("la prop transformEvent de ChatView reescribe y descarta antes del reductor", async () => {
    const hook: TransformEvent = (e) => (e.type === "notice" ? null : e.type === "tool.completed" ? { ...e, summary: "reescrito" } : undefined);
    render(<Harness client={mk()} hook={hook} />);
    await say();
    await waitFor(() => expect(screen.getByText("listo")).toBeTruthy());
    expect(screen.queryByText("secreto")).toBeNull();
    expect(screen.getByText("reescrito")).toBeTruthy();
    expect(screen.queryByText("bien")).toBeNull();
  });

  it("la opción transformEvent de useChat funciona sin la prop", async () => {
    render(<Harness client={mk()} optionHook={(e) => (e.type === "notice" ? null : undefined)} />);
    await say();
    await waitFor(() => expect(screen.getByText("listo")).toBeTruthy());
    expect(screen.queryByText("secreto")).toBeNull();
  });

  it("los filtros son posteriores al hook e independientes: el hook convierte el razonamiento en texto y showThinking=false no lo oculta", async () => {
    const hook: TransformEvent = (e) => (e.type === "thinking.delta" ? { ...e, type: "text.delta" } : undefined);
    render(<Harness client={mk()} hook={hook} showThinking={false} showToolCalls={false} />);
    await say();
    await waitFor(() => expect(screen.getByText("razono")).toBeTruthy());
    expect(screen.queryByTestId("step-search")).toBeNull();
  });

  it("sin hook, showThinking=false y showToolCalls=false ocultan razonamiento y herramientas", async () => {
    render(<Harness client={mk()} showThinking={false} showToolCalls={false} />);
    await say();
    await waitFor(() => expect(screen.getByText("listo")).toBeTruthy());
    expect(screen.queryByText("razono")).toBeNull();
    expect(screen.queryByTestId("step-search")).toBeNull();
    expect(screen.getByText("secreto")).toBeTruthy();
  });

  it("un hook que descarta `done` deja el chat estable (el turno se cierra y se puede volver a escribir)", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => undefined);
    render(<Harness client={mk()} hook={(e) => (e.type === "done" ? null : undefined)} />);
    await say();
    await waitFor(() => expect(screen.getByRole("button", { name: "Enviar" })).toBeTruthy());
    expect(screen.getByText("listo")).toBeTruthy();
  });
});

describe("display", () => {
  it("el reductor guarda displayStart/displayEnd de pasos y herramientas; sin display, no los lleva", () => {
    const s = run([
      { type: "step.started", stepId: "s1", name: "plan", kind: "llm", attempt: 1, display: "Planificando" },
      { type: "tool.started", callId: "c1", name: "q", stepId: "s1", display: "Consultando" },
      { type: "tool.completed", callId: "c1", name: "q", ok: true, stepId: "s1", display: "Consultado: 3 filas" },
      { type: "step.completed", stepId: "s1", ok: true, display: "Plan listo" },
      { type: "step.started", stepId: "s2", name: "otro", kind: "step", attempt: 1 },
      { type: "step.completed", stepId: "s2", ok: true },
    ]);
    const p = s.messages[0]!.parts;
    expect(p.find((x) => x.id === "s1")).toMatchObject({ displayStart: "Planificando", displayEnd: "Plan listo" });
    expect(p.find((x) => x.id === "c1")).toMatchObject({ displayStart: "Consultando", displayEnd: "Consultado: 3 filas" });
    const s2 = p.find((x) => x.id === "s2") as unknown as Record<string, unknown>;
    expect(s2.displayStart).toBeUndefined();
    expect(s2.displayEnd).toBeUndefined();
  });

  const agentRun = (running: boolean) =>
    run([
      { type: "step.started", stepId: "s1", name: "plan", label: "Etiqueta", kind: "llm", attempt: 1, detail: "detalle del paso", display: "Planificando X" },
      { type: "tool.started", callId: "c1", name: "search_schema", label: "Buscar", stepId: "s1", display: "Buscando en el esquema" },
      ...(running ? [] : [{ type: "tool.completed", callId: "c1", name: "search_schema", ok: true, stepId: "s1", display: "Encontradas 2 tablas", summary: "2 objetos" }, { type: "step.completed", stepId: "s1", ok: true, display: "Plan listo", model: "m", inputTokens: 10, outputTokens: 5 }]),
    ]);

  it("mientras corre: título = display de inicio (con respaldo al nombre); al terminar: el del fin lo sustituye", () => {
    const { rerender } = render(<MessageList messages={agentRun(true).messages} ctx={ctx()} />);
    const node = screen.getByTestId("agent-s1");
    expect(node.querySelector(".atc-agent-name")!.textContent).toBe("Planificando X");
    expect(within(node).getByTestId("step-search_schema").textContent).toContain("Buscando en el esquema");
    expect(within(node).getByTestId("step-search_schema").querySelector(".atc-tool-name")!.getAttribute("title")).toBe("search_schema");
    expect(screen.getByTestId("steps-summary").textContent).toBe("En curso: Buscando en el esquema");
    rerender(<MessageList messages={agentRun(false).messages} ctx={ctx()} />);
    const done = screen.getByTestId("agent-s1");
    expect(done.querySelector(".atc-agent-name")!.textContent).toBe("Plan listo");
    expect(done.textContent).not.toContain("Planificando X");
    expect(within(done).getByTestId("step-search_schema").textContent).toContain("Encontradas 2 tablas");
    expect(within(done).getByTestId("step-search_schema").textContent).not.toContain("Buscando en el esquema");
    // El chip de modelo sigue y `detail` sigue debajo.
    expect(within(done).getByTestId("node-meta").textContent).toContain("m");
    expect(within(done).getByTestId("node-detail").textContent).toBe("detalle del paso");
  });

  it("sin display: título = label ?? name, como hasta ahora", () => {
    render(<MessageList messages={run([{ type: "step.started", stepId: "s", name: "plan", label: "Planificando", kind: "llm", attempt: 1 }, { type: "tool.started", callId: "c", name: "q", stepId: "s" }]).messages} ctx={ctx()} />);
    expect(screen.getByTestId("agent-s").querySelector(".atc-agent-name")!.textContent).toBe("Planificando");
    expect(screen.getByTestId("step-q").querySelector(".atc-tool-name")!.textContent).toBe("q");
  });

  it("si solo hay display de inicio, se conserva al terminar", () => {
    const s = run([{ type: "tool.started", callId: "c", name: "q", display: "Consultando" }, { type: "tool.completed", callId: "c", name: "q", ok: true }]);
    render(<MessageList messages={s.messages} ctx={ctx()} />);
    expect(screen.getByTestId("step-q").textContent).toContain("Consultando");
  });

  it("inspector: el enlace es el display; la modal trae el nombre técnico y ambos displays junto a Entrada/Salida", () => {
    render(<MessageList messages={agentRun(false).messages} ctx={ctx()} toolInspector />);
    const link = screen.getByTestId("tool-name-link");
    expect(link.textContent).toBe("Encontradas 2 tablas");
    expect(link.getAttribute("title")).toBe("search_schema");
    fireEvent.click(link);
    const modal = screen.getByTestId("tool-inspector");
    expect(within(modal).getByTestId("tool-tech-name").textContent).toBe("search_schema");
    expect(within(modal).getByTestId("tool-display-start").textContent).toBe("Buscando en el esquema");
    expect(within(modal).getByTestId("tool-display-end").textContent).toBe("Encontradas 2 tablas");
    expect(within(modal).getByTestId("tool-input")).toBeTruthy();
    expect(within(modal).getByTestId("tool-output")).toBeTruthy();
  });

  it("el recuento no menciona clases y los textos de clase no se pintan", () => {
    render(<MessageList messages={agentRun(false).messages} ctx={ctx()} />);
    expect(screen.getByTestId("steps-summary").textContent).toMatch(/^Completado · 2 acciones/);
    expect(document.body.textContent).not.toMatch(/Agente|Herramienta/);
  });
});
