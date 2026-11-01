// @vitest-environment jsdom
// Markdown con Streamdown: saneado de salida de modelo, Mermaid opcional (válido, inválido, sin plugin, incompleto) y
// las peticiones aditivas (input de herramienta, options de useChat, avisos por code).
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, renderHook, screen, waitFor } from "@testing-library/react";
import { createMermaidPlugin } from "@streamdown/mermaid";
import { MessageList, chatReducer, defaultLabels, initialState, useChat, type ChatClient, type ChatMessage, type MarkdownOptions, type RendererContext, type ServerEvent } from "../src/index.ts";

// jsdom no trae observadores; el diagrama se pinta al entrar en pantalla, así que el simulado «ve» todo al instante.
beforeAll(() => {
  const g = globalThis as Record<string, unknown>;
  g.IntersectionObserver = class {
    cb: (e: unknown[]) => void;
    constructor(cb: (e: unknown[]) => void) {
      this.cb = cb;
    }
    observe(el: Element) {
      this.cb([{ isIntersecting: true, target: el }]);
    }
    takeRecords() {
      return [{ isIntersecting: true }];
    }
    unobserve() {}
    disconnect() {}
  };
  g.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
});
afterEach(cleanup);

const ctx = (markdown?: MarkdownOptions, over: Partial<RendererContext> = {}): RendererContext => ({ labels: defaultLabels, busy: false, applyProposal: vi.fn(), discardProposal: vi.fn(), answerQuestion: vi.fn(), ...(markdown ? { markdown } : {}), ...over });
const asst = (text: string, status: ChatMessage["status"] = "done"): ChatMessage => ({ id: "m", role: "assistant", status, parts: [{ type: "text", id: "t", text }] });
const show = (text: string, markdown?: MarkdownOptions, status: ChatMessage["status"] = "done") => render(<MessageList messages={[asst(text, status)]} ctx={ctx(markdown)} />);

/** Plugin Mermaid simulado: registra las configuraciones y fabrica un svg, o falla si el texto lo pide. */
function fakeMermaid() {
  const configs: Array<Record<string, unknown> | undefined> = [];
  const render = vi.fn(async (_id: string, src: string) => {
    if (src.includes("INVALIDO")) throw new Error("Parse error on line 2");
    return { svg: `<svg data-testid="fake-svg" xmlns="http://www.w3.org/2000/svg" width="10" height="10"><text>ok</text></svg>` };
  });
  const plugin = {
    name: "mermaid" as const,
    type: "diagram" as const,
    language: "mermaid",
    getMermaid: (c?: Record<string, unknown>) => {
      configs.push(c);
      return { initialize: vi.fn(), render };
    },
  };
  return { plugin, render, configs };
}

describe("markdown: saneado (el texto viene de un modelo)", () => {
  it("<script>, onerror, iframe, svg onload y estilos no llegan al DOM", () => {
    const { container } = show('a <script>window.__x=1</script> <img src=x onerror="window.__x=2"> <svg onload="window.__x=3"></svg> <iframe src="//e.test"></iframe> <b style="color:red" onclick="x()">b</b>');
    expect(container.querySelector("script, img, svg, iframe, object, embed")).toBeNull();
    for (const el of Array.from(container.querySelectorAll("*"))) {
      for (const a of Array.from(el.attributes)) expect(a.name.startsWith("on"), `${el.tagName}.${a.name}`).toBe(false);
    }
    expect(container.querySelector("[style]:not([data-streamdown])")).toBeNull();
    expect(container.querySelector("b")).toBeNull(); // el HTML crudo se muestra como texto, no se interpreta
    expect(container.textContent).toContain("<script>");
    expect((window as unknown as { __x?: number }).__x).toBeUndefined();
  });

  it("enlaces: javascript: y data: bloqueados; https con rel seguro; imágenes remotas bloqueadas", () => {
    const { container } = show("[m](javascript:alert(1)) <a href=\"javascript:alert(2)\">z</a> [d](data:text/html,x) [ok](https://x.test) [v](vbscript:x) [e](JaVa&#x73;cript:alert(3)) ![i](https://evil.test/a.png)");
    for (const el of Array.from(container.querySelectorAll("[href], [src]"))) {
      const u = (el.getAttribute("href") ?? el.getAttribute("src") ?? "").trim().toLowerCase();
      expect(u.startsWith("javascript:") || u.startsWith("data:") || u.startsWith("vbscript:"), u).toBe(false);
    }
    expect(container.querySelector("img")).toBeNull();
    expect(container.innerHTML).not.toContain("evil.test");
    const links = Array.from(container.querySelectorAll("a[href]"));
    expect(links.map((x) => x.getAttribute("href"))).toEqual(["https://x.test"]);
    expect(links[0]!.getAttribute("rel")).toContain("noopener");
    expect(links[0]!.getAttribute("target")).toBe("_blank");
  });

  it("un rehype del anfitrión se AÑADE tras el saneado: no lo sustituye", () => {
    type N = { type?: string; tagName?: string; properties?: Record<string, unknown>; children?: N[] };
    const marcar = () => (tree: N) => {
      const walk = (n: N) => {
        if (n.type === "element" && n.tagName === "p") n.properties = { ...n.properties, "data-host": "1" };
        (n.children ?? []).forEach(walk);
      };
      walk(tree);
    };
    const { container } = show("hola <script>x</script> [m](javascript:alert(1))", { rehypePlugins: [marcar] });
    expect(container.querySelector("p[data-host='1']")).not.toBeNull();
    expect(container.querySelector("script")).toBeNull();
    expect(container.querySelector("a[href^='javascript' i]")).toBeNull();
  });

  it("components del anfitrión sustituyen elementos", () => {
    const { container } = show("ver **negrita**", { components: { strong: ({ children }) => <em data-host="strong">{children}</em> } });
    expect(container.querySelector("em[data-host='strong']")?.textContent).toBe("negrita");
  });
});

describe("markdown: Mermaid como plugin opcional", () => {
  it("sin plugin: el bloque mermaid queda como código", () => {
    const { container } = show("```mermaid\ngraph TD\nA-->B\n```");
    expect(container.querySelector("[data-streamdown='mermaid-block']")).toBeNull();
    const code = container.querySelector("[data-streamdown='code-block']");
    expect(code).not.toBeNull();
    expect(code!.textContent).toContain("graph TD");
    expect(code!.textContent).toContain("A-->B");
  });

  it("diagrama válido: se pinta el svg y Mermaid recibe securityLevel strict", async () => {
    const { plugin, render: r, configs } = fakeMermaid();
    const { container } = show("```mermaid\ngraph TD\nA-->B\n```", { plugins: { mermaid: plugin } });
    await waitFor(() => expect(container.querySelector("svg[data-testid='fake-svg']")).not.toBeNull());
    expect(r).toHaveBeenCalled();
    expect(configs.length).toBeGreaterThan(0);
    for (const c of configs) expect(c?.securityLevel).toBe("strict");
  });

  it("tema: mermaidTheme se traslada a la configuración de Mermaid", async () => {
    const { plugin, configs } = fakeMermaid();
    show("```mermaid\ngraph TD\nA-->B\n```", { plugins: { mermaid: plugin }, mermaidTheme: "dark" });
    await waitFor(() => expect(configs.some((c) => c?.theme === "dark")).toBe(true));
  });

  it("diagrama inválido: bloque de código con el mensaje de error y el fuente, nunca un hueco", async () => {
    const { plugin } = fakeMermaid();
    const { container } = show("```mermaid\ngraph TD\nINVALIDO-->\n```", { plugins: { mermaid: plugin } });
    const err = await screen.findByTestId("mermaid-error");
    expect(err.textContent).toContain("Diagrama no válido");
    expect(err.textContent).toContain("Parse error on line 2");
    expect(err.querySelector("pre code")?.textContent).toContain("INVALIDO");
    expect(container.querySelector("svg")).toBeNull();
  });

  it("diagrama inválido con el Mermaid real (@streamdown/mermaid): error visible con el fuente", async () => {
    const { container } = show("```mermaid\ngraph TD\nA--> [[[ B ((\n```", { plugins: { mermaid: createMermaidPlugin() } });
    const err = await screen.findByTestId("mermaid-error", {}, { timeout: 10000 });
    expect(err.querySelector("pre code")?.textContent).toContain("A--> [[[ B ((");
    expect(container.querySelector("svg")).toBeNull();
  }, 15000);

  it("bloque incompleto en streaming: no se pinta ni da error; al cerrarse se pinta", async () => {
    const { plugin, render: r } = fakeMermaid();
    const md = { plugins: { mermaid: plugin } };
    const parcial = "texto\n\n```mermaid\ngraph TD\nA-->B";
    const { container, rerender } = show(parcial, md, "streaming");
    await new Promise((res) => setTimeout(res, 300));
    expect(r).not.toHaveBeenCalled();
    expect(screen.queryByTestId("mermaid-error")).toBeNull();
    expect(container.textContent).toContain("texto"); // el texto previo sigue visible; el diagrama espera a cerrarse
    expect(container.querySelector("[data-streamdown='mermaid-block']")).not.toBeNull(); // hueco reservado, sin error
    expect(container.querySelector(".atc-md")?.getAttribute("data-streaming")).toBe("true");
    rerender(<MessageList messages={[asst(`${parcial}\n\`\`\`\n`, "done")]} ctx={ctx(md)} />);
    await waitFor(() => expect(container.querySelector("svg[data-testid='fake-svg']")).not.toBeNull());
    expect(container.querySelector(".atc-md")?.getAttribute("data-streaming")).toBeNull();
  });
});

describe("peticiones aditivas", () => {
  it("ToolPart conserva input (reductor) y el chip lo muestra truncado", () => {
    const ev = (e: unknown) => ({ type: "event" as const, event: e as ServerEvent });
    let s = chatReducer(initialState, { type: "assistant.begin", id: "a" });
    s = chatReducer(s, ev({ type: "tool.started", turnId: "t", seq: 1, callId: "c1", name: "search_schema", input: { q: "x".repeat(200) } }));
    const tool = s.messages[0]!.parts[0]!;
    expect(tool.type === "tool" && (tool.input as { q: string }).q.length).toBe(200);
    const { container } = render(<MessageList messages={s.messages} ctx={ctx()} />);
    const args = container.querySelector(".atc-tool-args")!;
    expect(args.textContent!.length).toBeLessThanOrEqual(80);
    expect(args.textContent).toContain("…");
    expect(args.getAttribute("title")).toContain("x".repeat(200));
  });

  it("useChat reenvía options en message y en answer", async () => {
    const send = vi.fn(async (_req: unknown, on: (e: ServerEvent) => void) => {
      on({ type: "question", turnId: "t", seq: 1, callId: "q1", question: "¿?" } as unknown as ServerEvent);
      on({ type: "done", turnId: "t", seq: 2, reason: "question" } as unknown as ServerEvent);
    });
    const client = { send, abort: vi.fn(), cancel: vi.fn() } as unknown as ChatClient;
    const { result } = renderHook(() => useChat({ client, sessionId: "s1", options: { role: "analista" } }));
    await act(async () => {
      await result.current.send("hola");
    });
    expect(send.mock.calls[0]![0]).toMatchObject({ type: "message", options: { role: "analista" } });
    const q = result.current.state.messages.flatMap((m) => m.parts).find((p) => p.type === "question")!;
    await act(async () => {
      await result.current.answer(q.id, { text: "sí" }, "sí");
    });
    expect(send.mock.calls[1]![0]).toMatchObject({ type: "answer", callId: "q1", options: { role: "analista" } });
  });

  it("useChat sin options: no añade el campo", async () => {
    const send = vi.fn(async (_req: unknown, _on: (e: ServerEvent) => void) => {});
    const client = { send, abort: vi.fn(), cancel: vi.fn() } as unknown as ChatClient;
    const { result } = renderHook(() => useChat({ client }));
    await act(async () => {
      await result.current.send("hola");
    });
    expect(send.mock.calls[0]![0]).not.toHaveProperty("options");
  });

  it("avisos: renderizador por code; el resto usa notice", () => {
    const msg: ChatMessage = {
      id: "m",
      role: "assistant",
      status: "done",
      parts: [
        { type: "notice", id: "n1", code: "audit.x", message: "uno" },
        { type: "notice", id: "n2", code: "otro", message: "dos" },
      ],
    };
    const { container } = render(<MessageList messages={[msg]} ctx={ctx()} noticeRenderers={{ "audit.x": ({ part }) => <aside data-testid="audit">{part.message}!</aside> }} />);
    expect(screen.getByTestId("audit").textContent).toBe("uno!");
    expect(container.querySelector("p.atc-notice")?.textContent).toBe("dos");
  });
});
