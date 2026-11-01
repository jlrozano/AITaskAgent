// @vitest-environment jsdom
// Línea de tiempo de agentes IA, pasos y herramientas: vocabulario, iconos por clase, recuento sin ceros, «en curso»,
// anidamiento, resultado de paso, usage sin duplicar, compatibilidad sin step.* y aviso `phase` heredado.
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MessageList, chatReducer, defaultLabels, initialState, type ChatMessage, type ChatState, type RendererContext, type ServerEvent } from "../src/index.ts";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const ctx = (over: Partial<RendererContext> = {}): RendererContext => ({ labels: defaultLabels, busy: false, applyProposal: vi.fn(), discardProposal: vi.fn(), answerQuestion: vi.fn(), ...over });
const asst = (parts: ChatMessage["parts"]): ChatMessage => ({ id: "m1", role: "assistant", status: "done", parts });
const show = (parts: ChatMessage["parts"], c = ctx()) => render(<MessageList messages={[asst(parts)]} ctx={c} />);
const summary = () => screen.getByTestId("steps-summary").textContent;

function run(events: Array<Record<string, unknown>>): ChatState {
  let s = chatReducer(initialState, { type: "assistant.begin", id: "a" });
  events.forEach((e, seq) => {
    s = chatReducer(s, { type: "event", event: { turnId: "t1", seq, ...e } as ServerEvent });
  });
  return s;
}

const tool = (id: string, stepId?: string, extra: Record<string, unknown> = {}): ChatMessage["parts"][number] => ({ type: "tool", id, name: `t_${id}`, status: "ok", ...(stepId ? { stepId } : {}), ...extra }) as never;
const node = (id: string, kind: "llm" | "step" | "tool", extra: Record<string, unknown> = {}): ChatMessage["parts"][number] => ({ type: "agent", id, name: `n_${id}`, kind, attempt: 1, status: "ok", ...extra }) as never;

describe("reductor: detail, modelo, tokens y coste", () => {
  it("step.started.detail y los campos de step.completed llegan a la parte agent", () => {
    const s = run([
      { type: "step.started", stepId: "s1", name: "plan", kind: "llm", attempt: 1, detail: "Decide qué consultar" },
      { type: "step.completed", stepId: "s1", ok: true, elapsedMs: 900, model: "gpt-5", inputTokens: 1200, outputTokens: 340, costUsd: 0.0123, summary: "listo" },
    ]);
    expect(s.messages[0]!.parts[0]).toEqual({ type: "agent", id: "s1", name: "plan", kind: "llm", attempt: 1, status: "ok", detail: "Decide qué consultar", elapsedMs: 900, summary: "listo", model: "gpt-5", inputTokens: 1200, outputTokens: 340, costUsd: 0.0123, endIndex: 1 });
  });

  it("sin los campos nuevos, la parte no los lleva; un 0 de tokens sí se conserva", () => {
    const s = run([
      { type: "step.started", stepId: "s1", name: "p", kind: "step", attempt: 1 },
      { type: "step.completed", stepId: "s1", ok: true, inputTokens: 0, outputTokens: 0, costUsd: 0 },
    ]);
    const a = s.messages[0]!.parts[0] as unknown as Record<string, unknown>;
    expect(a.detail).toBeUndefined();
    expect(a.model).toBeUndefined();
    expect([a.inputTokens, a.outputTokens, a.costUsd]).toEqual([0, 0, 0]);
  });
});

describe("nodos: clase, iconos y etiquetas", () => {
  it("cada clase lleva su icono SVG distinto y su etiqueta de texto (sin depender del color)", () => {
    show([node("a", "llm"), node("p", "step"), node("t", "tool"), tool("x", "a")]);
    const kinds = (id: string) => screen.getByTestId(`agent-${id}`).querySelector(".atc-node-icon")?.getAttribute("data-icon");
    // llm y step son el mismo tipo de nodo (destello); `tool` lleva la llave. Ninguna palabra de clase en pantalla.
    expect([kinds("a"), kinds("p"), kinds("t")]).toEqual(["agent", "agent", "tool"]);
    expect(screen.queryAllByTestId("node-class")).toHaveLength(0);
    expect(screen.getByTestId("agent-a").textContent).not.toMatch(/Agente|Herramienta|Paso/);
    // El icono lleva nombre accesible neutro (sobreescribible).
    expect(screen.getByTestId("agent-a").querySelector(".atc-node-icon")!.getAttribute("aria-label")).toBe("Paso");
    expect(screen.getByTestId("agent-t").querySelector(".atc-node-icon")!.getAttribute("aria-label")).toBe("Llamada");
    // La herramienta anidada: llave y «Herramienta».
    const nested = screen.getByTestId("step-t_x");
    expect(nested.querySelector(".atc-node-icon")?.getAttribute("data-icon")).toBe("tool");
    expect(within(nested).queryByTestId("node-class")).toBeNull();
    // Paso y llamada tienen iconos de forma distinta.
    const shapes = ["a", "t"].map((id) => screen.getByTestId(`agent-${id}`).querySelector(".atc-node-icon")!.innerHTML);
    expect(new Set(shapes).size).toBe(2);
  });

  it("las etiquetas neutras se sobreescriben con labels; las de clase heredadas se ignoran", () => {
    show([node("a", "llm"), node("p", "step")], ctx({ labels: { ...defaultLabels, iconStep: "Step", done: "Done", countActions: "actions", classAgent: "AI agent", classStep: "Step", countAgent: "AI agent", countStep: "step" } }));
    expect(screen.getByTestId("agent-a").querySelector(".atc-node-icon")!.getAttribute("aria-label")).toBe("Step");
    expect(screen.queryAllByTestId("node-class")).toHaveLength(0);
    expect(summary()).toBe("Done · 2 actions");
  });

  it("estado: en curso (spinner), ok y error con su icono", () => {
    show([node("a", "llm", { status: "running" }), node("b", "step"), node("c", "step", { status: "error", errorCode: "x.y" })]);
    const st = (id: string) => screen.getByTestId(`agent-${id}`).querySelector(".atc-status-icon")!;
    expect(st("a").getAttribute("data-icon")).toBe("running");
    expect(st("a").getAttribute("class")).toContain("atc-spin");
    expect(st("b").getAttribute("data-icon")).toBe("ok");
    expect(st("c").getAttribute("data-icon")).toBe("error");
    expect(screen.getByTestId("agent-c").textContent).toContain("x.y");
  });

  it("detail en gris debajo (también en curso), resultado al terminar y no mientras corre", () => {
    show([node("a", "step", { status: "running", detail: "Extrae el borrador", summary: "parcial" }), node("b", "step", { detail: "Valida", summary: "3 bandas" })]);
    expect(within(screen.getByTestId("agent-a")).getByTestId("node-detail").textContent).toBe("Extrae el borrador");
    expect(within(screen.getByTestId("agent-a")).queryByTestId("node-summary")).toBeNull();
    expect(within(screen.getByTestId("agent-b")).getByTestId("node-summary").textContent).toBe("3 bandas");
  });

  it("agente IA: modelo, tokens y coste en la misma fila; un paso no los muestra", () => {
    show([node("a", "llm", { model: "gpt-5", inputTokens: 1200, outputTokens: 340, costUsd: 0.0123 }), node("p", "step")]);
    const meta = within(screen.getByTestId("agent-a")).getByTestId("node-meta").textContent!;
    expect(meta).toMatch(/gpt-5 · 1\.?200 entrada · 340 salida · 0\.0123 USD/);
    expect(within(screen.getByTestId("agent-p")).queryByTestId("node-meta")).toBeNull();
  });

  it("cronómetro vivo mientras corre y duración fija al terminar", () => {
    vi.useFakeTimers();
    show([node("a", "llm", { status: "running" }), node("b", "step", { elapsedMs: 1500 })]);
    expect(within(screen.getByTestId("agent-a")).getByTestId("node-time").textContent).toBe("0 s");
    act(() => {
      vi.advanceTimersByTime(3200);
    });
    expect(within(screen.getByTestId("agent-a")).getByTestId("node-time").textContent).toBe("3 s");
    expect(within(screen.getByTestId("agent-b")).getByTestId("node-time").textContent).toBe("1,5 s");
  });
});

describe("cabecera del panel", () => {
  it("«Completado · N acciones» sin ceros ni palabras de clase", () => {
    show([node("a", "llm"), node("b", "llm"), node("p1", "step"), node("p2", "step"), node("p3", "step"), node("p4", "step"), tool("x", "a"), tool("y", "a"), tool("z", "b")]);
    expect(summary()).toBe("Completado · 9 acciones");
    cleanup();
    show([node("p1", "step"), node("p2", "step")]);
    expect(summary()).toBe("Completado · 2 acciones");
    cleanup();
    show([node("a", "llm", { elapsedMs: 2500 }), tool("x", "a")]);
    expect(summary()).toBe("Completado · 2 acciones · 2,5 s");
  });

  it("con errores añade «· n con error» (un nodo fallido por una herramienta fallida cuenta una vez)", () => {
    show([node("a", "llm", { status: "error" }), tool("x", "a", { status: "error" }), node("p", "step", { status: "error" })]);
    expect(summary()).toBe("Completado · 3 acciones · 2 con error");
  });

  it("en curso: «En curso: <nodo actual>» (la herramienta activa gana al agente) y el panel va abierto; al terminar, plegado", () => {
    show([node("a", "llm", { status: "running", label: "Planificando" }), tool("x", "a", { status: "running", label: "Buscando" })]);
    expect(summary()).toBe("En curso: Buscando");
    expect(screen.getByTestId("steps-panel").getAttribute("data-open")).toBe("true");
    cleanup();
    show([node("a", "llm", { status: "running", label: "Planificando" })]);
    expect(summary()).toBe("En curso: Planificando");
    cleanup();
    show([node("a", "llm"), tool("x", "a")]);
    expect(screen.getByTestId("steps-panel").getAttribute("data-open")).toBe("false");
  });

  it("si el usuario lo pliega a mano mientras corre, manda él", () => {
    const { rerender } = show([node("a", "llm", { status: "running" })]);
    fireEvent.click(screen.getByTestId("steps-toggle"));
    expect(screen.getByTestId("steps-panel").getAttribute("data-open")).toBe("false");
    rerender(<MessageList messages={[asst([node("a", "llm")])]} ctx={ctx()} />);
    expect(screen.getByTestId("steps-panel").getAttribute("data-open")).toBe("false");
  });
});

describe("anidamiento y orden", () => {
  it("las herramientas con stepId cuelgan de su agente, seguidas y sin bloque plegable «Llamadas»", () => {
    show([node("a", "llm"), tool("x", "a"), tool("y", "a"), node("p", "step")]);
    const a = screen.getByTestId("agent-a");
    expect(within(a).getByTestId("step-t_x")).toBeTruthy();
    expect(within(a).getByTestId("step-t_y")).toBeTruthy();
    expect(within(screen.getByTestId("agent-p")).queryByTestId("step-t_x")).toBeNull();
    expect(screen.queryByTestId("node-tools-toggle")).toBeNull();
    expect(a.textContent).not.toContain("Llamadas");
  });

  it("los nodos salen en el orden en que llegaron", () => {
    show([node("p1", "step"), node("a", "llm"), node("p2", "step")]);
    const ids = Array.from(document.querySelectorAll("[data-testid^='agent-']")).map((e) => e.getAttribute("data-testid"));
    expect(ids).toEqual(["agent-p1", "agent-a", "agent-p2"]);
  });
});

describe("usage del turno", () => {
  const usage = { type: "usage", id: "usage", counts: { requests: 1, inputTokens: 5, cachedInputTokens: 0, outputTokens: 2, reasoningTokens: 0 } } as never;
  it("no se repite si todos los agentes IA traen sus tokens", () => {
    const { container } = show([node("a", "llm", { inputTokens: 5, outputTokens: 2 }), node("p", "step"), usage]);
    expect(container.querySelector(".atc-usage")).toBeNull();
  });
  it("sigue saliendo si un agente IA no trae tokens o no hay agentes IA", () => {
    expect(show([node("a", "llm", { inputTokens: 5 }), node("b", "llm"), usage]).container.querySelector(".atc-usage")).not.toBeNull();
    cleanup();
    expect(show([node("p", "step"), usage]).container.querySelector(".atc-usage")).not.toBeNull();
  });
});

describe("compatibilidad", () => {
  it("sin step.* ni stepId: panel plano de siempre, «N pasos» y sin etiquetas de clase", () => {
    show([tool("a"), tool("b", undefined, { status: "error" })]);
    expect(summary()).toBe("2 pasos · 1 con error");
    expect(document.querySelector(".atc-node, [data-testid=node-class]")).toBeNull();
    expect(screen.getByTestId("step-t_a").textContent).toContain("✓");
  });

  it("aviso `phase` heredado: nodo ya terminado con el nombre prettificado, no línea cruda", () => {
    const { container } = show([{ type: "notice", id: "n1", code: "phase", message: "Borrador_Extraer" }, { type: "notice", id: "n2", code: "phase", message: "Borrador_Validar_Informe" }]);
    expect(container.querySelector(".atc-notice")).toBeNull();
    const nodes = container.querySelectorAll("[data-kind=step]");
    expect(nodes).toHaveLength(2);
    expect(nodes[0]!.textContent).toContain("Borrador Extraer");
    expect(nodes[0]!.textContent).not.toContain("_");
    expect(summary()).toBe("Completado · 2 acciones");
  });

  it("otros avisos siguen siendo líneas", () => {
    const { container } = show([{ type: "notice", id: "n1", code: "retrying", message: "Reintentando" }]);
    expect(container.querySelector(".atc-notice")?.textContent).toBe("Reintentando");
  });
});

describe("toolInspector: fila de herramienta", () => {
  const insp = (parts: ChatMessage["parts"]) => render(<MessageList messages={[asst(parts)]} ctx={ctx()} toolInspector />);
  const full = (extra: Record<string, unknown> = {}) => tool("a", undefined, { label: "Buscando", summary: "3 objetos", elapsedMs: 12, input: { q: "x" }, output: { n: 3 }, ...extra });

  it("solo estado, nombre (enlace), duración y errorCode: sin argumentos ni summary; el summary va en la modal", () => {
    insp([full({ status: "error", errorCode: "sql.rejected" }), full({ id: "b" })]);
    const row = screen.getAllByTestId("step-t_a")[0]!;
    expect(row.textContent).toContain("Buscando");
    expect(row.textContent).toContain("12 ms");
    expect(row.textContent).toContain("sql.rejected");
    expect(row.textContent).not.toContain("3 objetos");
    expect(row.querySelector(".atc-tool-args")).toBeNull();
    expect(row.querySelector(".atc-tool-summary")).toBeNull();
  });

  it("el nombre abre la misma modal que el botón de icono", () => {
    insp([full()]);
    fireEvent.click(screen.getByTestId("steps-toggle"));
    const link = screen.getByTestId("tool-name-link");
    expect(link.tagName).toBe("BUTTON");
    expect(link.textContent).toBe("Buscando");
    fireEvent.click(link);
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(screen.getByTestId("tool-summary").textContent).toBe("3 objetos");
    expect(screen.getByTestId("tool-output-json").textContent).toContain("3");
    fireEvent.click(screen.getByTestId("tool-inspector-close"));
    expect(screen.queryByRole("dialog")).toBeNull();
    fireEvent.click(screen.getByTestId("tool-inspect"));
    expect(screen.getByRole("dialog")).toBeTruthy();
  });

  it("en curso y sin entrada: el enlace abre la modal indicando «en curso»", () => {
    insp([tool("a", undefined, { status: "running", summary: "parcial" })]);
    fireEvent.click(screen.getByTestId("tool-name-link"));
    expect(screen.getByTestId("tool-output-pending").textContent).toBe("en curso…");
    expect(screen.getByText("Sin entrada.")).toBeTruthy();
    expect(document.querySelector(".atc-tool-summary")).toBeNull();
  });

  it("sin toolInspector la fila queda como siempre: argumentos y summary, sin enlace", () => {
    show([full()]);
    expect(screen.queryByTestId("tool-name-link")).toBeNull();
    expect(screen.getByTestId("step-t_a").textContent).toContain("3 objetos");
    expect(document.querySelector(".atc-tool-args")?.textContent).toContain("x");
  });
});

describe("secuencia lineal dentro de la caja del paso", () => {
  const th = (id: string, text: string): ChatMessage["parts"][number] => ({ type: "thinking", id, text });
  const tx = (id: string, text: string): ChatMessage["parts"][number] => ({ type: "text", id, text });
  /** Orden de pintado dentro del flow: «T» razonamiento, «tool:x», «X» texto. */
  const order = (id: string) => Array.from(screen.getByTestId(`agent-${id}`).querySelector("[data-testid=node-flow]")!.children).map((e) => (e.getAttribute("data-testid") === "flow-thinking" ? "T" : e.getAttribute("data-testid") === "flow-text" ? "X" : e.getAttribute("data-testid")));

  const real = (): ChatMessage["parts"] => [
    node("a", "llm", { endIndex: 8, model: "gpt-5", inputTokens: 10, outputTokens: 5, summary: "hecho" }),
    th("t1", "Voy a consultar"),
    tool("x", "a"),
    tool("y", "a"),
    tool("z", "a"),
    th("t2", "Ya tengo los datos"),
    tx("tx1", "¿Quieres que aplique el cambio?"),
    tool("w", "a"),
  ];

  it("(a) razonamiento, herramientas, razonamiento y texto, en orden, dentro de la misma caja", () => {
    const { container } = show(real());
    expect(order("a")).toEqual(["T", "step-t_x", "step-t_y", "step-t_z", "T", "X", "step-t_w"]);
    // Nada suelto fuera de la caja: ni razonamiento ni texto ni herramientas.
    const outside = Array.from(container.querySelectorAll(".atc-thinking, .atc-md, [data-testid^=step-]")).filter((e) => !screen.getByTestId("agent-a").contains(e));
    expect(outside).toHaveLength(0);
    expect(screen.getByTestId("agent-a").textContent).toContain("¿Quieres que aplique el cambio?");
    // El resumen y el chip se mantienen; el resumen cierra la caja.
    expect(screen.getByTestId("agent-a").lastElementChild!.getAttribute("data-testid")).toBe("node-summary");
    expect(screen.getByTestId("node-meta")).toBeTruthy();
    // El panel no se pliega al terminar si hay respuesta dentro.
    expect(screen.getByTestId("steps-panel").getAttribute("data-open")).toBe("true");
  });

  it("razonamiento corto abierto; largo, plegado", () => {
    show([node("a", "llm", { endIndex: 4 }), th("t1", "corto"), tool("x", "a"), th("t2", "x".repeat(500))]);
    const d = Array.from(document.querySelectorAll<HTMLDetailsElement>("details.atc-flow-thinking"));
    expect(d.map((e) => e.open)).toEqual([true, false]);
  });

  it("(b) las herramientas con stepId que llegan tras el cierre siguen en su caja; el texto posterior queda fuera", () => {
    const { container } = show([node("a", "llm", { endIndex: 2 }), th("t1", "pienso"), tx("fin", "respuesta final"), tool("late", "a")]);
    expect(order("a")).toEqual(["T", "step-t_late"]);
    expect(screen.getByTestId("agent-a").textContent).not.toContain("respuesta final");
    expect(container.querySelector(".atc-md")!.textContent).toContain("respuesta final");
    expect(screen.getByTestId("steps-panel").contains(container.querySelector(".atc-md"))).toBe(false);
  });

  it("(c) showThinking=false y showToolCalls=false dentro de la caja; la cabecera sigue", () => {
    const m = [asst(real())];
    const r = render(<MessageList messages={m} ctx={ctx()} showThinking={false} />);
    expect(order("a")).toEqual(["step-t_x", "step-t_y", "step-t_z", "X", "step-t_w"]);
    r.rerender(<MessageList messages={m} ctx={ctx()} showToolCalls={false} />);
    expect(order("a")).toEqual(["T", "T", "X"]);
    r.rerender(<MessageList messages={m} ctx={ctx()} showThinking={false} showToolCalls={false} />);
    expect(order("a")).toEqual(["X"]);
    const only = [asst([node("a", "llm", { endIndex: 3 }), th("t1", "x"), tool("x", "a")])];
    r.rerender(<MessageList messages={only} ctx={ctx()} showThinking={false} showToolCalls={false} />);
    expect(screen.getByTestId("agent-a")).toBeTruthy();
    expect(screen.queryByTestId("node-flow")).toBeNull();
  });

  it("(d) mensaje sin pasos: texto, razonamiento y panel plano de herramientas como antes", () => {
    const { container } = show([th("t1", "pienso"), tool("a"), tx("tx1", "hola")]);
    expect(container.querySelector(".atc-node, [data-testid=node-flow]")).toBeNull();
    expect(summary()).toBe("1 paso");
    expect(container.querySelector("details.atc-thinking")!.hasAttribute("open")).toBe(false);
    expect(container.querySelector(".atc-md")!.textContent).toBe("hola");
  });

  it("(e) un paso en curso absorbe lo que llega; al cerrar, lo posterior queda fuera (reductor real)", () => {
    const events = [
      { type: "step.started", stepId: "s1", name: "plan", kind: "llm", attempt: 1 },
      { type: "thinking.delta", text: "pie" },
      { type: "thinking.delta", text: "nso" },
      { type: "tool.started", callId: "c1", name: "q" },
      { type: "tool.completed", callId: "c1", name: "q", ok: true },
      { type: "thinking.delta", text: "listo" },
      { type: "text.delta", text: "¿Seguimos?" },
    ];
    let s = run(events);
    const view = render(<MessageList messages={s.messages} ctx={ctx()} />);
    expect(order("s1")).toEqual(["T", "step-q", "T", "X"]);
    expect(screen.getByTestId("agent-s1").getAttribute("data-status")).toBe("running");
    view.unmount();
    s = run([...events, { type: "step.completed", stepId: "s1", ok: true }, { type: "text.delta", text: "Fin." }]);
    // El texto posterior al cierre no se funde con el de dentro del paso.
    expect(s.messages[0]!.parts.filter((p) => p.type === "text")).toHaveLength(2);
    render(<MessageList messages={s.messages} ctx={ctx()} />);
    expect(order("s1")).toEqual(["T", "step-q", "T", "X"]);
    expect(screen.getByTestId("agent-s1").textContent).not.toContain("Fin.");
    expect(screen.getByText("Fin.")).toBeTruthy();
  });

  it("done cierra un paso abierto con su endIndex", () => {
    const s = run([{ type: "step.started", stepId: "s1", name: "p", kind: "llm", attempt: 1 }, { type: "text.delta", text: "a" }, { type: "done", reason: "cancelled" }]);
    expect((s.messages[0]!.parts[0] as { endIndex?: number }).endIndex).toBe(2);
  });
});
