import { useEffect, useMemo, useRef } from "react";
import { defaultLabels } from "../labels.ts";
import { defaultRenderers } from "../renderers/index.ts";
import type { NoticeRenderers, Renderer, RendererContext, RendererRegistry } from "../registry.ts";
import type { AgentNodePart, AgentPart, ChatMessage, FlowItem, GroupEntry, NoticePart, Part, ToolGroupPart, ToolPart } from "../state.ts";

export interface MessageListProps {
  messages: ChatMessage[];
  ctx: RendererContext;
  renderers?: RendererRegistry;
  /** Desplaza al final cuando cambian los mensajes (por defecto sí). El anfitrión que ya sigue el final por su cuenta lo apaga. */
  autoScroll?: boolean;
  /** Renderizador por `code` de aviso (`notice`); el que no coincida usa `renderers.notice`. */
  noticeRenderers?: NoticeRenderers;
  /** Botón de icono que abre una modal con la entrada y la salida de cada herramienta, en vez de los argumentos en línea (por defecto no). Gana a `ctx.toolInspector`. */
  toolInspector?: boolean;
  /** Filtros de presentación, posteriores a `transformEvent` e independientes de él; por defecto true. Gana a `ctx`. `showThinking=false` no pinta las partes `thinking`; `showToolCalls=false` oculta los nodos de tool (y su recuento). El estado conserva los datos. */
  showThinking?: boolean;
  showToolCalls?: boolean;
}

type Item = Part | ToolGroupPart | AgentNodePart;

/** «Borrador_Extraer» → «Borrador Extraer». */
const prettify = (s: string) => s.replace(/_+/g, " ").trim();

/**
 * Las herramientas seguidas de un mensaje pasan a un solo elemento `toolGroup` (si el registro lo trae). Cada paso (agente)
 * recoge, en orden cronológico, su secuencia lineal (`flow`): las partes `thinking`, `text` y `tool` que llegaron después de él
 * y antes de su cierre (`endIndex`; sin cierre, un paso en curso lo absorbe todo), y las herramientas con su `stepId`, vengan
 * donde vengan. Con pasos anidados gana el último abierto (siguen planos). Lo que no pertenece a ningún paso (herramientas
 * sueltas, texto antes del primer paso o tras el último cierre) sale como siempre. Con algún agente o paso en el mensaje la
 * línea de tiempo siempre lleva cabeceras (`agentHeaders`), también con un único nodo. Un aviso heredado `phase` se pinta
 * como un nodo ya terminado. Si todos los pasos con modelo traen sus tokens (`step.completed`), la línea `usage` suelta del
 * turno sobra y no se pinta. Sin agentes ni pasos, todo sale como antes.
 */
function grouped(all: Part[], group: boolean, showThinking: boolean, showToolCalls: boolean): Item[] {
  // Filtros de presentación: ocultan sin tocar el estado. Un paso `kind: "tool"` es un nodo de tool.
  const hidden = (p: Part) => (p.type === "thinking" && !showThinking) || (!showToolCalls && (p.type === "tool" || (p.type === "agent" && p.kind === "tool")));
  if (!group) return all.filter((p) => !hidden(p));
  // Los índices (`endIndex`) son de `message.parts`: la propiedad se calcula antes de filtrar.
  const parts: Part[] = all.map((p) => (p.type === "notice" && p.code === "phase" ? { type: "agent", id: `phase-${p.id}`, name: prettify(p.message), kind: "step", attempt: 1, status: "ok" } : p));
  const agentAt: Array<[number, AgentPart]> = [];
  parts.forEach((p, i) => {
    if (p.type === "agent") agentAt.push([i, p]);
  });
  const ids = new Set(agentAt.map(([, a]) => a.id));
  const flows = new Map<string, FlowItem[]>(agentAt.map(([, a]) => [a.id, []]));
  // Paso abierto en la posición j: el último iniciado antes de j que sigue en curso (sin cierre) o cuyo cierre es posterior.
  // Los pasos `kind: "tool"` son un nodo de herramienta, no contenedores.
  const openAt = (j: number): AgentPart | undefined => {
    for (let k = agentAt.length - 1; k >= 0; k--) {
      const [i0, a] = agentAt[k]!;
      if (i0 >= j || a.kind === "tool") continue;
      if (a.endIndex !== undefined ? j < a.endIndex : a.status === "running") return a;
    }
    return undefined;
  };
  const ownerOf = new Map<number, string>();
  parts.forEach((p, j) => {
    if (p.type !== "thinking" && p.type !== "text" && p.type !== "tool") return;
    const owner = p.type === "tool" && p.stepId !== undefined && ids.has(p.stepId) ? p.stepId : openAt(j)?.id;
    if (owner === undefined) return;
    ownerOf.set(j, owner);
    if (!hidden(p)) flows.get(owner)!.push(p);
  });
  const headers = agentAt.length > 0;
  const visibleAgents = agentAt.map(([, a]) => a).filter((a) => !hidden(a));
  const llm = visibleAgents.filter((a) => a.kind === "llm");
  const usageInAgents = llm.length > 0 && llm.every((a) => a.inputTokens !== undefined || a.outputTokens !== undefined);
  const out: Item[] = [];
  parts.forEach((p, j) => {
    if (hidden(p)) return;
    const last = out[out.length - 1];
    if (ownerOf.has(j) || (p.type === "usage" && usageInAgents)) return;
    if (p.type !== "tool" && p.type !== "agent") {
      out.push(p);
      return;
    }
    const flow = p.type === "agent" ? flows.get(p.id)! : [];
    const entry: GroupEntry = p.type === "agent" ? { kind: "agent", agent: { ...p, tools: flow.filter((f): f is ToolPart => f.type === "tool"), flow } } : { kind: "tool", tool: p };
    const tools = entry.kind === "agent" ? entry.agent.tools! : [entry.tool];
    if (last?.type === "toolGroup") out[out.length - 1] = { ...last, tools: [...last.tools, ...tools], entries: [...last.entries!, entry] };
    else out.push({ type: "toolGroup", id: `tools-${p.id}`, tools: [...tools], entries: [entry], agentHeaders: headers });
  });
  // Sin agentes, el grupo es el de siempre (sin `entries`); sin herramientas ni cabeceras, no pinta nada.
  return out.flatMap((it): Item[] => {
    if (it.type !== "toolGroup") return [it];
    if (!it.entries!.some((e) => e.kind === "agent")) return [{ type: "toolGroup", id: it.id, tools: it.tools }];
    return it.agentHeaders || it.tools.length > 0 ? [it] : [];
  });
}

/** Lista de mensajes: cada parte se pinta con el renderizador de su tipo (registro extensible). */
export function MessageList({ messages, ctx, renderers = defaultRenderers, autoScroll = true, noticeRenderers, toolInspector, showThinking, showToolCalls }: MessageListProps) {
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (autoScroll) end.current?.scrollIntoView?.({ block: "end" });
  }, [messages, autoScroll]);
  const c = useMemo<RendererContext>(() => ({ ...ctx, renderers, ...(toolInspector !== undefined ? { toolInspector } : {}), ...(showThinking !== undefined ? { showThinking } : {}), ...(showToolCalls !== undefined ? { showToolCalls } : {}) }), [ctx, renderers, toolInspector, showThinking, showToolCalls]);
  if (messages.length === 0) return <p className="atc-empty">{(ctx.labels ?? defaultLabels).empty}</p>;
  return (
    <div className="atc-messages" role="log" aria-live="polite">
      {messages.map((m) => (
        <article key={m.id} className={`atc-message atc-${m.role}`} data-status={m.status}>
          {grouped(m.parts, renderers.toolGroup !== undefined, c.showThinking !== false, c.showToolCalls !== false).map((part) => {
            const byCode = part.type === "notice" ? noticeRenderers?.[(part as NoticePart).code] : undefined;
            // eslint-disable-next-line @typescript-eslint/no-explicit-any
            const R: Renderer<any> | undefined = byCode ?? renderers[part.type];
            return R ? <R key={part.id} part={part} message={m} ctx={c} /> : null;
          })}
          {m.status === "cancelled" ? <p className="atc-cancelled">{ctx.labels.cancelled}</p> : null}
        </article>
      ))}
      <div ref={end} />
    </div>
  );
}
