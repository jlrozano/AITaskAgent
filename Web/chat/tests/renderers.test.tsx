// @vitest-environment jsdom
// Renderizadores por tipo de elemento (DOM), registro extensible y ChatView + useChat con anfitrión y servidor simulado.
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { ChatClient, ChatView, MessageList, createRenderers, defaultLabels, defaultRenderers, useChat, type ChatMessage, type HostAdapter, type RendererContext } from "../src/index.ts";
import { startChatMock, type ChatMock } from "../mock-server/server.ts";

afterEach(cleanup);

const ctx = (over: Partial<RendererContext> = {}): RendererContext => ({ labels: defaultLabels, busy: false, applyProposal: vi.fn(), discardProposal: vi.fn(), answerQuestion: vi.fn(), ...over });
const asst = (parts: ChatMessage["parts"]): ChatMessage => ({ id: "m1", role: "assistant", status: "done", parts });
const show = (parts: ChatMessage["parts"], c = ctx()) => render(<MessageList messages={[asst(parts)]} ctx={c} />);

describe("renderizadores", () => {
  it("texto: markdown saneado (sin scripts, enlaces seguros, HTML crudo escapado)", () => {
    const { container } = show([{ type: "text", id: "t", text: "**negrita** y [web](https://x.test) <script>alert(1)</script> [mal](javascript:alert(1))" }]);
    expect(container.querySelector("strong")?.textContent).toBe("negrita");
    expect(container.querySelector("script")).toBeNull();
    const a = container.querySelector("a[href='https://x.test']");
    expect(a?.getAttribute("rel")).toContain("noopener");
    expect(container.innerHTML).not.toContain("javascript:");
    expect(container.textContent).toContain("<script>");
  });

  it("texto del usuario: plano, sin interpretar markdown", () => {
    const { container } = render(<MessageList messages={[{ id: "u", role: "user", status: "done", parts: [{ type: "text", id: "t", text: "**no**" }] }]} ctx={ctx()} />);
    expect(container.querySelector("strong")).toBeNull();
    expect(container.textContent).toBe("**no**");
  });

  it("razonamiento: plegado por defecto", () => {
    const { container } = show([{ type: "thinking", id: "r", text: "pienso" }]);
    const d = container.querySelector("details")!;
    expect(d.open).toBe(false);
    expect(d.textContent).toContain("Razonamiento");
    expect(d.textContent).toContain("pienso");
  });

  it("herramienta: estado, resumen y código de error", () => {
    const { container } = show([
      { type: "tool", id: "a", name: "search_schema", label: "Buscando", status: "running" },
      { type: "tool", id: "b", name: "validate_sql", status: "error", summary: "no", errorCode: "sql.rejected", elapsedMs: 4.2 },
    ]);
    const [run, err] = Array.from(container.querySelectorAll(".atc-tool"));
    expect(run!.getAttribute("data-status")).toBe("running");
    expect(run!.textContent).toContain("Buscando");
    expect(run!.textContent).toContain("en curso");
    expect(err!.textContent).toContain("validate_sql");
    expect(err!.textContent).toContain("sql.rejected");
    expect(err!.textContent).toContain("4 ms");
  });

  it("pasos: varias herramientas seguidas en un panel plegable con estado, resumen y duración; abierto en curso y plegado al terminar", () => {
    const parts: ChatMessage["parts"] = [
      { type: "tool", id: "a", name: "search_schema", label: "Buscando", status: "running" },
      { type: "tool", id: "b", name: "validate_sql", status: "error", summary: "no", errorCode: "sql.rejected", elapsedMs: 1234 },
      { type: "text", id: "t", text: "fin" },
      { type: "tool", id: "c", name: "otra", status: "ok", elapsedMs: 9 },
    ];
    const { container, rerender } = show(parts);
    const panels = screen.getAllByTestId("steps-panel");
    expect(panels).toHaveLength(2); // las herramientas separadas por texto forman dos grupos
    expect(panels[0]!.getAttribute("data-open")).toBe("true");
    expect(screen.getAllByTestId("steps-summary")[0]!.textContent).toBe("2 pasos… Buscando");
    expect(container.textContent).toContain("1,2 s");
    expect(panels[1]!.getAttribute("data-open")).toBe("false");
    expect(screen.getAllByTestId("steps-summary")[1]!.textContent).toBe("1 paso");
    // Termina el turno: el primero se pliega solo y resume los errores.
    rerender(<MessageList messages={[asst([{ ...(parts[0] as object), status: "ok" } as never, parts[1]!])]} ctx={ctx()} />);
    expect(screen.getByTestId("steps-panel").getAttribute("data-open")).toBe("false");
    expect(screen.getByTestId("steps-summary").textContent).toBe("2 pasos · 1 con error");
    // El usuario lo abre a mano: manda él.
    fireEvent.click(screen.getByTestId("steps-toggle"));
    expect(screen.getByTestId("steps-panel").getAttribute("data-open")).toBe("true");
    expect(screen.getByTestId("step-validate_sql").textContent).toContain("sql.rejected");
  });

  it("adjuntos del usuario y botón Reintentar del error (solo con retryTurn y canRetry)", () => {
    const retry = vi.fn();
    const msg: ChatMessage = { id: "m9", role: "assistant", status: "error", parts: [{ type: "error", id: "e", code: "x", message: "Falló", retryable: true }] };
    const user: ChatMessage = { id: "u1", role: "user", status: "done", parts: [{ type: "text", id: "t", text: "mira" }, { type: "attachments", id: "a", items: [{ id: "f1", name: "factura.pdf", kind: "pdf" }] }] };
    const { rerender } = render(<MessageList messages={[user, msg]} ctx={ctx()} />);
    expect(screen.getByText("factura.pdf")).toBeTruthy();
    expect(screen.queryByTestId("msg-retry")).toBeNull();
    rerender(<MessageList messages={[user, msg]} ctx={ctx({ retryTurn: retry, canRetry: (id) => id === "m9" })} />);
    fireEvent.click(screen.getByTestId("msg-retry"));
    expect(retry).toHaveBeenCalledWith("m9");
    rerender(<MessageList messages={[user, msg]} ctx={ctx({ retryTurn: retry, canRetry: () => false })} />);
    expect(screen.queryByTestId("msg-retry")).toBeNull();
  });

  const proposal = (status: "pending" | "applied" | "conflict" = "pending"): ChatMessage["parts"] => [
    {
      type: "proposal",
      id: "p",
      status,
      proposal: { type: "proposal", turnId: "t1", seq: 3, summary: "Cambios", operations: [{ tool: "set_properties", label: "Negrita" }], changes: [{ kind: "changed", target: "c1", label: "Total", before: "a", after: "b" }, { kind: "removed", target: "c2" }] },
    },
  ];

  it("propuesta: diferencias y Aplicar/Descartar", () => {
    const c = ctx();
    const { container } = show(proposal(), c);
    expect(container.querySelector("h4")?.textContent).toBe("Cambios");
    expect(container.querySelector(".atc-diff-changed del")?.textContent).toBe("a");
    expect(container.querySelector(".atc-diff-changed ins")?.textContent).toBe("b");
    expect(container.querySelector(".atc-diff-removed")?.textContent).toContain("c2");
    fireEvent.click(screen.getByText("Aplicar"));
    expect(c.applyProposal).toHaveBeenCalledWith("p");
    fireEvent.click(screen.getByText("Descartar"));
    expect(c.discardProposal).toHaveBeenCalledWith("p");
  });

  it("propuesta: sin diferencias usa las operaciones; aplicada y en conflicto no ofrecen botones", () => {
    const parts = proposal("applied");
    (parts[0] as { proposal: { changes?: unknown } }).proposal.changes = undefined;
    const { container } = show(parts);
    expect(container.querySelector(".atc-ops li")?.textContent).toBe("Negrita");
    expect(container.querySelector("button")).toBeNull();
    expect(container.querySelector(".atc-status")?.textContent).toBe("Aplicada");
    cleanup();
    expect(show(proposal("conflict")).container.querySelector(".atc-status")?.textContent).toContain("Conflicto");
  });

  it("pregunta de aprobación: Aprobar/Rechazar", () => {
    const c = ctx();
    show([{ type: "question", id: "q", question: { type: "question", turnId: "t1", seq: 1, callId: "k", question: "¿Borro?", kind: "approval" } }], c);
    fireEvent.click(screen.getByText("Rechazar"));
    expect(c.answerQuestion).toHaveBeenCalledWith("q", { approved: false }, "Rechazar");
  });

  it("pregunta con opciones: selección única responde al pulsar; múltiple espera a Responder", () => {
    const c = ctx();
    const q = (multi: boolean) => [{ type: "question" as const, id: "q", question: { type: "question" as const, turnId: "t1", seq: 1, callId: "k", question: "¿Cuál?", kind: "choice" as const, multiSelect: multi, options: ["a", "b"] } }];
    show(q(false), c);
    fireEvent.click(screen.getByLabelText("b"));
    expect(c.answerQuestion).toHaveBeenCalledWith("q", { answers: [["b"]] }, "b");
    cleanup();
    const c2 = ctx();
    show(q(true), c2);
    expect((screen.getByText("Responder") as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByLabelText("a"));
    fireEvent.click(screen.getByLabelText("b"));
    fireEvent.click(screen.getByText("Responder"));
    expect(c2.answerQuestion).toHaveBeenCalledWith("q", { answers: [["a", "b"]] }, "a, b");
  });

  it("pregunta de texto libre y pregunta ya respondida", () => {
    const c = ctx();
    const part = { type: "question" as const, id: "q", question: { type: "question" as const, turnId: "t1", seq: 1, callId: "k", question: "¿Nombre?", kind: "clarification" as const } };
    show([part], c);
    fireEvent.change(screen.getByLabelText("¿Nombre?"), { target: { value: "Ana" } });
    fireEvent.click(screen.getByText("Responder"));
    expect(c.answerQuestion).toHaveBeenCalledWith("q", { text: "Ana" }, "Ana");
    cleanup();
    const { container } = show([{ ...part, answered: "Ana" }]);
    expect(container.querySelector("button")).toBeNull();
    expect(container.textContent).toContain("Ana");
  });

  it("uso/coste, aviso y error", () => {
    const { container } = show([
      { type: "usage", id: "usage", model: "m", counts: { requests: 1, inputTokens: 1500, cachedInputTokens: 0, outputTokens: 20, reasoningTokens: 0 }, estimatedCost: 0.0012, currency: "USD" },
      { type: "notice", id: "n", code: "retrying", message: "Reintentando" },
      { type: "error", id: "e", code: "x", message: "Falló", retryable: true },
    ]);
    expect(container.querySelector(".atc-usage")?.textContent).toMatch(/1\.?500 entrada · 20 salida · 0\.0012 USD/);
    expect(container.querySelector(".atc-notice")?.textContent).toBe("Reintentando");
    expect(container.querySelector("[role=alert]")?.textContent).toContain("Falló");
    expect(container.querySelector("[role=alert]")?.textContent).toContain("reintentar");
  });

  it("registro extensible: sustituye un tipo y no pinta los que no conoce", () => {
    const renderers = createRenderers(defaultRenderers, { toolGroup: undefined, tool: ({ part }: { part: { name: string } }) => <b data-testid="custom">{part.name}</b> });
    const { container } = render(<MessageList messages={[asst([{ type: "tool", id: "a", name: "x", status: "ok" }, { type: "futuro", id: "f" } as never])]} ctx={ctx()} renderers={renderers} />);
    expect(screen.getByTestId("custom").textContent).toBe("x");
    expect(container.querySelectorAll("article > *")).toHaveLength(1);
  });
});

describe("ChatView + useChat + anfitrión", () => {
  let mock: ChatMock;
  beforeAll(async () => {
    mock = await startChatMock();
  });
  afterAll(() => mock.close());

  function App({ host }: { host: HostAdapter }) {
    const client = useClient();
    const chat = useChat({ client, host, sessionId: "s1" });
    return <ChatView chat={chat} />;
  }
  const clients = new Map<string, ChatClient>();
  function useClient() {
    if (!clients.has(mock.url)) clients.set(mock.url, new ChatClient({ baseUrl: mock.url, reconnectDelayMs: 5 }));
    return clients.get(mock.url)!;
  }
  const write = async (text: string) => {
    fireEvent.change(screen.getByLabelText(defaultLabels.placeholder), { target: { value: text } });
    fireEvent.click(screen.getByText("Enviar"));
  };

  it("envía documento y contexto, bloquea y libera la edición y aplica la propuesta", async () => {
    const lock = vi.fn();
    const host: HostAdapter = {
      readDocument: () => ({ bundleFormat: "app-bundle/1" }),
      readContext: () => ({ selection: ["c1"] }),
      setEditingLocked: lock,
      applyProposal: vi.fn(async () => ({ outcome: "applied" as const, operations: 1 })),
    };
    render(<App host={host} />);
    await write("propuesta por favor");
    await waitFor(() => screen.getByText("Aplicar"));
    await waitFor(() => expect(lock).toHaveBeenLastCalledWith(false));
    expect(lock.mock.calls.map((c) => c[0])).toEqual([true, false]);
    const sent = mock.requests.at(-1)!;
    expect(sent).toMatchObject({ type: "message", sessionId: "s1", document: { bundleFormat: "app-bundle/1" }, context: { selection: ["c1"] } });
    fireEvent.click(screen.getByText("Aplicar"));
    await waitFor(() => expect(screen.getByText("Aplicada")).toBeTruthy());
    expect(host.applyProposal).toHaveBeenCalledTimes(1);
  });

  it("pregunta: la respuesta lleva el documento vivo y el contexto del anfitrión (turno nuevo)", async () => {
    const host: HostAdapter = { readDocument: () => ({ bundleFormat: "app-bundle/1" }), readContext: () => ({ view: "bands" }), setEditingLocked: () => undefined, applyProposal: () => ({ outcome: "applied" }) };
    render(<App host={host} />);
    await write("pregunta");
    const option = await screen.findByLabelText(/vencimiento/);
    await waitFor(() => expect((option as HTMLInputElement).disabled).toBe(false));
    fireEvent.click(option);
    await waitFor(() => expect(screen.getByText(/Recibido: answers=vencimiento/)).toBeTruthy());
    expect(mock.requests.at(-1)).toMatchObject({ type: "answer", document: { bundleFormat: "app-bundle/1" }, context: { view: "bands" } });
  });

  it("pregunta: pulsar una opción envía la respuesta como turno nuevo", async () => {
    const host: HostAdapter = { setEditingLocked: () => undefined, applyProposal: () => ({ outcome: "applied" }) };
    render(<App host={host} />);
    await write("pregunta");
    const option = await screen.findByLabelText(/vencimiento/);
    await waitFor(() => expect((option as HTMLInputElement).disabled).toBe(false));
    fireEvent.click(option);
    await waitFor(() => expect(screen.getByText(/Recibido: answers=vencimiento/)).toBeTruthy());
    expect(mock.requests.at(-1)).toMatchObject({ type: "answer", callId: "ask_1", answers: [["vencimiento"]] });
  });

  it("Detener cancela el turno en curso", async () => {
    const host: HostAdapter = { setEditingLocked: () => undefined, applyProposal: () => ({ outcome: "applied" }) };
    render(<App host={host} />);
    await write("lento");
    fireEvent.click(await screen.findByText("Detener"));
    await waitFor(() => expect(screen.getByText("Cancelado")).toBeTruthy());
    await waitFor(() => expect(screen.getByText("Enviar")).toBeTruthy());
  });
});
