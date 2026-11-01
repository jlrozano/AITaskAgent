// @vitest-environment jsdom
// reductor (step/agent, output, stepId, truncado), agrupación por agente en MessageList e inspector de herramienta
// (modal accesible, visor JSON plegable, texto no JSON, truncado, en curso, error y opción desactivada = comportamiento anterior).
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MessageList, renderMarkdown, chatReducer, createRenderers, defaultLabels, defaultRenderers, initialState, isServerEvent, type ChatMessage, type AgentNodePart, type ChatState, type RendererContext, type RendererProps, type ServerEvent } from "../src/index.ts";

afterEach(cleanup);

const ctx = (over: Partial<RendererContext> = {}): RendererContext => ({ labels: defaultLabels, busy: false, applyProposal: vi.fn(), discardProposal: vi.fn(), answerQuestion: vi.fn(), ...over });
const asst = (parts: ChatMessage["parts"]): ChatMessage => ({ id: "m1", role: "assistant", status: "done", parts });
const show = (parts: ChatMessage["parts"], c = ctx(), props: { toolInspector?: boolean } = {}) => render(<MessageList messages={[asst(parts)]} ctx={c} {...props} />);

function run(events: Array<Record<string, unknown>>): ChatState {
  let s = chatReducer(initialState, { type: "assistant.begin", id: "a" });
  events.forEach((e, seq) => {
    s = chatReducer(s, { type: "event", event: { turnId: "t1", seq, ...e } as ServerEvent });
  });
  return s;
}

describe("reductor: agentes, salida y stepId", () => {
  it("step.started/completed crean y cierran la parte agent; tool conserva output, outputTruncated y stepId", () => {
    const s = run([
      { type: "step.started", stepId: "s1", name: "plan", label: "Planificando", kind: "llm", attempt: 1 },
      { type: "tool.started", callId: "c1", name: "q", input: { a: 1 }, stepId: "s1" },
      { type: "tool.completed", callId: "c1", name: "q", ok: true, output: { rows: [1, 2] }, stepId: "s1" },
      { type: "tool.started", callId: "c2", name: "r", stepId: "s1" },
      { type: "tool.completed", callId: "c2", name: "r", ok: true, output: "texto", outputTruncated: true, stepId: "s1" },
      { type: "step.completed", stepId: "s1", ok: false, elapsedMs: 33, errorCode: "x.y", summary: "mal" },
    ]);
    const parts = s.messages[0]!.parts;
    expect(parts.filter((p) => p.type === "agent")).toEqual([{ type: "agent", id: "s1", name: "plan", label: "Planificando", kind: "llm", attempt: 1, status: "error", elapsedMs: 33, errorCode: "x.y", summary: "mal", endIndex: 3 }]);
    expect(parts.filter((p) => p.type === "tool")).toMatchObject([
      { id: "c1", status: "ok", input: { a: 1 }, output: { rows: [1, 2] }, stepId: "s1" },
      { id: "c2", output: "texto", outputTruncated: true, stepId: "s1" },
    ]);
    expect((parts.find((p) => p.id === "c1") as { outputTruncated?: boolean }).outputTruncated).toBeUndefined();
  });

  it("un reintento (mismo stepId, attempt 2) reabre el agente; done cierra los agentes que seguían en curso; un step.completed sin started no falla", () => {
    const s = run([
      { type: "step.started", stepId: "s1", name: "p", kind: "step", attempt: 1 },
      { type: "step.completed", stepId: "s1", ok: false, elapsedMs: 5 },
      { type: "step.started", stepId: "s1", name: "p", kind: "step", attempt: 2 },
      { type: "step.completed", stepId: "huerfano", ok: true, elapsedMs: 1 },
      { type: "step.started", stepId: "s3", name: "q", kind: "tool", attempt: 1 },
      { type: "done", reason: "completed" },
    ]);
    const agents = s.messages[0]!.parts.filter((p) => p.type === "agent");
    expect(agents).toMatchObject([{ id: "s1", attempt: 2, status: "error" }, { id: "huerfano", status: "ok" }, { id: "s3", status: "error" }]);
    expect((agents[0] as { elapsedMs?: number }).elapsedMs).toBeUndefined();
  });

  it("los eventos desconocidos se siguen ignorando", () => {
    expect(isServerEvent({ type: "step.futuro", turnId: "t1", seq: 0, stepId: "z" })).toBe(false);
    expect(isServerEvent({ type: "step.started", turnId: "t1", seq: 0, stepId: "z" })).toBe(true);
    expect(isServerEvent({ type: "step.completed", turnId: "t1", seq: 0, stepId: "z" })).toBe(true);
  });
});

const tool = (id: string, stepId?: string, extra: Partial<ChatMessage["parts"][number]> = {}): ChatMessage["parts"][number] =>
  ({ type: "tool", id, name: `t_${id}`, status: "ok", ...(stepId ? { stepId } : {}), ...extra }) as never;
const agent = (id: string, extra: Record<string, unknown> = {}): ChatMessage["parts"][number] => ({ type: "agent", id, name: `ag_${id}`, kind: "llm", attempt: 1, status: "ok", elapsedMs: 40, ...extra }) as never;

describe("agrupación por agente", () => {
  it("dos agentes: una cabecera por agente (etiqueta, intentos, duración) con sus herramientas anidadas, y las sueltas aparte", () => {
    show([agent("s1", { label: "Planificando" }), tool("a", "s1"), tool("loose"), agent("s2", { attempt: 2, status: "error", elapsedMs: 1500, errorCode: "sql.rejected" }), tool("b", "s2")]);
    expect(screen.getAllByTestId("steps-panel")).toHaveLength(1);
    expect(screen.getByTestId("steps-summary").textContent).toBe("Completado · 5 acciones · 1,5 s · 1 con error");
    const a1 = screen.getByTestId("agent-s1");
    expect(a1.textContent).toContain("Planificando");
    expect(within(a1).getByTestId("step-t_a")).toBeTruthy();
    expect(within(a1).queryByTestId("step-t_b")).toBeNull();
    const a2 = screen.getByTestId("agent-s2");
    expect(a2.textContent).toContain("ag_s2");
    expect(within(a2).getByTestId("agent-attempt").textContent).toBe("intento 2");
    expect(a2.textContent).toContain("1,5 s");
    expect(a2.textContent).toContain("sql.rejected");
    expect(within(a2).getByTestId("step-t_b")).toBeTruthy();
    // La suelta no cuelga de ningún agente.
    expect(screen.getByTestId("step-t_loose").closest("[data-testid^='agent-']")).toBeNull();
  });

  it("un único agente también lleva cabecera (la línea de tiempo siempre es visible)", () => {
    show([agent("s1"), tool("a", "s1"), tool("b", "s1")]);
    expect(screen.getByTestId("agent-s1")).toBeTruthy();
    expect(screen.getByTestId("steps-summary").textContent).toBe("Completado · 3 acciones · 40 ms");
    expect(screen.getByTestId("step-t_a")).toBeTruthy();
    expect(screen.getByTestId("step-t_b")).toBeTruthy();
  });

  it("un agente reintentado, fallido o sin herramientas se pinta igual: nodo con cabecera", () => {
    show([agent("s1", { attempt: 3 }), tool("a", "s1")]);
    expect(screen.getByTestId("agent-s1").textContent).toContain("intento 3");
    cleanup();
    expect(show([agent("s1")]).container.querySelector(".atc-steps")).not.toBeNull();
    cleanup();
    show([agent("s1", { status: "error" })]);
    expect(screen.getByTestId("agent-s1")).toBeTruthy();
  });

  it("herramientas sin stepId (o con uno desconocido) se pintan como antes: un panel plano", () => {
    show([tool("a"), tool("b", "no-existe")]);
    expect(screen.getByTestId("steps-summary").textContent).toBe("2 pasos");
    expect(document.querySelector(".atc-agent")).toBeNull();
  });

  it("el renderizador agent es sustituible con createRenderers", () => {
    const Mine = ({ part }: RendererProps<AgentNodePart>) => <b data-testid="mi-agente">{part.name}</b>;
    const renderers = createRenderers(defaultRenderers, { agent: Mine });
    render(<MessageList messages={[asst([agent("s1"), agent("s2"), tool("a", "s1")])]} ctx={ctx()} renderers={renderers} />);
    expect(screen.getAllByTestId("mi-agente").map((e) => e.textContent)).toEqual(["ag_s1", "ag_s2"]);
  });

  it("agente en curso: el panel va abierto y resume el agente activo", () => {
    show([agent("s1"), agent("s2", { status: "running", label: "Pensando", elapsedMs: undefined })]);
    expect(screen.getByTestId("steps-panel").getAttribute("data-open")).toBe("true");
    expect(screen.getByTestId("steps-summary").textContent).toBe("En curso: Pensando");
  });
});

const inspectable = (over: Record<string, unknown> = {}) =>
  tool("a", undefined, { label: "Buscando", input: { query: "facturas", opts: { limit: 5, nested: { flags: [true, null] } } }, output: { objects: [{ name: "FACTURA" }], total: 2 }, ...over });

describe("inspector de herramienta", () => {
  it("desactivado (por defecto): argumentos en línea y sin botón", () => {
    show([inspectable()]);
    expect(screen.queryByTestId("tool-inspect")).toBeNull();
    expect(document.querySelector(".atc-tool-args")?.textContent).toContain("facturas");
  });

  it("activado: sin argumentos en línea y un botón solo de icono con aria-label y title", () => {
    show([inspectable()], ctx(), { toolInspector: true });
    expect(document.querySelector(".atc-tool-args")).toBeNull();
    const b = screen.getByTestId("tool-inspect");
    expect(b.getAttribute("aria-label")).toBe(defaultLabels.toolInspect);
    expect(b.getAttribute("title")).toBe(defaultLabels.toolInspect);
    expect(b.textContent).toBe("");
    expect(b.querySelector("svg")).toBeTruthy();
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("también se activa por ctx.toolInspector y las etiquetas son sobreescribibles", () => {
    show([inspectable()], ctx({ toolInspector: true, labels: { ...defaultLabels, toolInspect: "Inspect" } }));
    expect(screen.getByTestId("tool-inspect").getAttribute("aria-label")).toBe("Inspect");
  });

  it("abre la modal (dialog, aria-modal, Entrada y Salida), foco dentro, Esc cierra y devuelve el foco", () => {
    show([inspectable()], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("steps-toggle")); // el panel plegado oculta el botón (hidden): no sería enfocable
    const b = screen.getByTestId("tool-inspect");
    b.focus();
    fireEvent.click(b);
    const d = screen.getByRole("dialog");
    expect(d.getAttribute("aria-modal")).toBe("true");
    expect(d.contains(document.activeElement)).toBe(true);
    expect(within(d).getByRole("heading", { name: /Buscando/ })).toBeTruthy();
    expect(within(screen.getByTestId("tool-input")).getByRole("heading", { name: "Entrada" })).toBeTruthy();
    expect(within(screen.getByTestId("tool-output")).getByRole("heading", { name: "Salida" })).toBeTruthy();
    expect(screen.getByTestId("tool-input-json").textContent).toContain("facturas");
    expect(screen.getByTestId("tool-output-json").textContent).toContain("FACTURA");
    fireEvent.keyDown(document.activeElement ?? document.body, { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.activeElement).toBe(b);
  });

  it("el botón Cerrar y el clic en el fondo cierran; el clic dentro no", () => {
    show([inspectable()], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("tool-inspect"));
    fireEvent.mouseDown(screen.getByRole("dialog"));
    expect(screen.getByRole("dialog")).toBeTruthy();
    fireEvent.mouseDown(screen.getByTestId("tool-inspector-backdrop"));
    expect(screen.queryByRole("dialog")).toBeNull();
    fireEvent.click(screen.getByTestId("tool-inspect"));
    fireEvent.click(screen.getByTestId("tool-inspector-close"));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("el foco queda atrapado: Tab desde el último pasa al primero y Mayús+Tab desde el primero al último", () => {
    show([inspectable()], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("tool-inspect"));
    const d = screen.getByRole("dialog");
    const items = Array.from(d.querySelectorAll<HTMLElement>("button"));
    const first = items[0]!;
    const last = items[items.length - 1]!;
    last.focus();
    fireEvent.keyDown(last, { key: "Tab" });
    expect(document.activeElement).toBe(first);
    fireEvent.keyDown(first, { key: "Tab", shiftKey: true });
    expect(document.activeElement).toBe(last);
    // Foco fuera del diálogo (p. ej. el contenedor): Tab lo devuelve dentro.
    (document.activeElement as HTMLElement).blur();
    fireEvent.keyDown(document.body, { key: "Tab" });
    expect(d.contains(document.activeElement)).toBe(true);
  });

  it("visor JSON: pliega y despliega objetos y arrays; colorea los escalares por clase", () => {
    show([inspectable()], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("tool-inspect"));
    const input = screen.getByTestId("tool-input-json");
    expect(input.querySelector(".atc-json-str")?.textContent).toBe('"facturas"');
    expect(input.querySelector(".atc-json-num")?.textContent).toBe("5");
    const toggles = within(input).getAllByTestId("json-toggle");
    // Raíz, `opts` y `nested` abiertos; `flags` (profundidad 3) plegado: se despliega a mano.
    expect(input.textContent).not.toContain("true");
    const flags = toggles.find((t) => t.getAttribute("aria-label")?.endsWith("flags"))!;
    expect(flags.getAttribute("aria-label")).toBe("Desplegar flags");
    fireEvent.click(flags);
    expect(input.textContent).toContain("true");
    expect(input.querySelector(".atc-json-bool")?.textContent).toBe("true");
    expect(input.querySelector(".atc-json-null")?.textContent).toBe("null");
    // Plegar la raíz oculta todo y resume (claves).
    fireEvent.click(toggles[0]!);
    expect(input.textContent).not.toContain("facturas");
    expect(input.textContent).toContain("2 claves");
    fireEvent.click(toggles[0]!);
    expect(input.textContent).toContain("facturas");
  });

  it("copiar: escribe el JSON formateado en el portapapeles", async () => {
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    show([inspectable({ output: { a: 1 } })], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("tool-inspect"));
    fireEvent.click(within(screen.getByTestId("tool-output")).getByTestId("json-copy"));
    expect(writeText).toHaveBeenCalledWith('{\n  "a": 1\n}');
  });

  it("salida que no es JSON: texto con salto de línea; un string con JSON de objeto se pinta como árbol", () => {
    show([inspectable({ output: "SELECT 1\nFROM dual — no es JSON {" })], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("tool-inspect"));
    const out = screen.getByTestId("tool-output-json");
    expect(out.getAttribute("data-kind")).toBe("text");
    expect(out.querySelector("pre")?.textContent).toBe("SELECT 1\nFROM dual — no es JSON {");
    cleanup();
    show([inspectable({ output: '{"k": [1]}' })], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("tool-inspect"));
    expect(screen.getByTestId("tool-output-json").getAttribute("data-kind")).toBe("json");
  });

  it("salida truncada: avisa", () => {
    show([inspectable({ output: "abc", outputTruncated: true })], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("tool-inspect"));
    expect(screen.getByTestId("tool-truncated").textContent).toContain("16384");
  });

  it("en curso: dice «en curso» y no hay salida; al terminar, la modal abierta se actualiza", () => {
    const running = inspectable({ status: "running", output: undefined });
    const { rerender } = show([running], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("tool-inspect"));
    expect(screen.getByTestId("tool-output-pending").textContent).toBe("en curso…");
    expect(screen.queryByTestId("tool-output-json")).toBeNull();
    rerender(<MessageList messages={[asst([inspectable()])]} ctx={ctx()} toolInspector />);
    expect(screen.queryByTestId("tool-output-pending")).toBeNull();
    expect(screen.getByTestId("tool-output-json").textContent).toContain("FACTURA");
  });

  it("herramienta fallida: muestra el error (resumen y código), con o sin salida", () => {
    show([inspectable({ status: "error", summary: "SELECT * no permitido", errorCode: "sql.rejected", output: undefined })], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("tool-inspect"));
    const e = screen.getByTestId("tool-error");
    expect(e.textContent).toContain("SELECT * no permitido");
    expect(e.textContent).toContain("sql.rejected");
    expect(screen.queryByText("Sin salida.")).toBeNull();
  });

  it("sin entrada ni salida: lo dice", () => {
    show([tool("a")], ctx(), { toolInspector: true });
    fireEvent.click(screen.getByTestId("tool-inspect"));
    expect(screen.getByText("Sin entrada.")).toBeTruthy();
    expect(screen.getByText("Sin salida.")).toBeTruthy();
  });

  it("también en las herramientas anidadas de un agente", () => {
    show([agent("s1"), agent("s2"), tool("a", "s1", { output: { ok: 1 } })], ctx(), { toolInspector: true });
    fireEvent.click(within(screen.getByTestId("agent-s1")).getByTestId("tool-inspect"));
    expect(screen.getByTestId("tool-output-json").textContent).toContain("ok");
  });
});

describe("renderMarkdown (export restituido)", () => {
  it("devuelve HTML saneado de forma síncrona", () => {
    const html = renderMarkdown("**negrita** [web](https://x.test) <script>alert(1)</script> [mal](javascript:alert(1)) ![i](https://x.test/a.png)");
    expect(html).toContain("<strong>negrita</strong>");
    expect(html).toContain('href="https://x.test"');
    expect(html).not.toContain("<script");
    expect(html).not.toContain("javascript:");
    expect(html).not.toContain("<img");
  });
});
