import { useEffect, useRef, useState } from "react";
import type { RendererProps } from "../registry.ts";
import type { ToolGroupPart as Group } from "../state.ts";
import { AgentPart } from "./AgentPart.tsx";
import { nodeTitle } from "./NodeIcons.tsx";
import { ToolRow, durationText } from "./ToolPart.tsx";

/**
 * Pasos de herramientas de un turno en un panel plegable: abierto mientras alguno corre y plegado solo al terminar (si el
 * usuario lo abre o lo cierra a mano, manda él). Cada paso: estado, nombre, resumen y duración. Si el grupo trae agentes
 * (`entries`) y hay que mostrar su cabecera (`agentHeaders`), cada agente se pinta con el renderizador `agent` del registro,
 * con sus herramientas anidadas; si no, las herramientas salen planas como siempre.
 */
export function ToolGroupPart({ part, message, ctx }: RendererProps<Group>) {
  const agents = part.entries?.filter((e): e is Extract<typeof e, { kind: "agent" }> => e.kind === "agent").map((e) => e.agent) ?? [];
  const headers = part.agentHeaders === true && agents.length > 0;
  const running = part.tools.some((t) => t.status === "running") || agents.some((a) => a.status === "running");
  // Si algún paso trae texto del modelo (la respuesta), el panel no se pliega al terminar: la respuesta vive dentro de la caja.
  const hasText = agents.some((a) => a.flow?.some((f) => f.type === "text"));
  const keepOpen = running || hasText;
  const [open, setOpen] = useState(keepOpen);
  const manual = useRef(false);
  useEffect(() => {
    if (!manual.current) setOpen(keepOpen);
  }, [keepOpen]);
  const n = part.tools.length;
  const errors = part.tools.filter((t) => t.status === "error").length + (headers ? agents.filter((a) => a.status === "error" && !a.tools?.some((t) => t.status === "error")).length : 0);
  const now = [...part.tools].reverse().find((t) => t.status === "running") ?? (headers ? [...agents].reverse().find((a) => a.status === "running") : undefined);
  const l = ctx.labels;
  let summary: string;
  if (headers) {
    // Sin palabras de clase: en curso, el título del nodo actual; al terminar, «Completado · N acciones · duración»
    // (N cuenta nodos: pasos y llamadas; sin ceros) y los errores.
    const actions = n + agents.length;
    const total = (part.entries ?? []).reduce((acc, e) => acc + ((e.kind === "agent" ? e.agent.elapsedMs : e.tool.elapsedMs) ?? 0), 0);
    const bits = [l.done, actions > 0 ? `${actions} ${actions === 1 ? l.countAction : l.countActions}` : "", total > 0 ? durationText(total) : ""].filter(Boolean);
    if (errors > 0) bits.push(`${errors} ${l.stepsWithErrors}`);
    summary = running ? `${l.nowRunning}${now ? `: ${nodeTitle(now)}` : "…"}` : bits.join(" · ");
  } else {
    const count = `${n} ${n === 1 ? l.step : l.steps}`;
    summary = running ? `${count}…${now ? ` ${nodeTitle(now)}` : ""}` : `${count}${errors > 0 ? ` · ${errors} ${l.stepsWithErrors}` : ""}`;
  }
  const inspector = ctx.toolInspector === true;
  const AgentR = ctx.renderers?.agent ?? AgentPart;
  const bodyId = `atc-steps-${part.id}`;
  return (
    <div className={`atc-steps${open ? " is-open" : ""}${running ? " is-running" : ""}`} data-testid="steps-panel" data-open={open ? "true" : "false"}>
      <button
        type="button"
        className="atc-steps-toggle"
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={() => {
          manual.current = true;
          setOpen((o) => !o);
        }}
        data-testid="steps-toggle"
      >
        <span aria-hidden="true">{open ? "▾" : "▸"}</span> <span className="atc-steps-summary" data-testid="steps-summary">{summary}</span>
      </button>
      <div id={bodyId} className="atc-steps-body" hidden={!open}>
        {headers
          ? part.entries!.map((e) => (e.kind === "agent" ? <AgentR key={`agent-${e.agent.id}`} part={e.agent} message={message} ctx={ctx} /> : <ToolRow key={e.tool.id} part={e.tool} labels={l} inspector={inspector} tagged />))
          : part.tools.map((t) => <ToolRow key={t.id} part={t} labels={l} inspector={inspector} />)}
      </div>
    </div>
  );
}
