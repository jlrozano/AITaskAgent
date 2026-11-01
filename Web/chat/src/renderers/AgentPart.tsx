import type { Labels } from "../labels.ts";
import type { RendererProps } from "../registry.ts";
import type { AgentNodePart } from "../state.ts";
import { ClassIcon, StatusIcon, liveText, nodeTitle, useLiveElapsed, type NodeClass } from "./NodeIcons.tsx";
import { TextPart } from "./TextPart.tsx";
import { ThinkingBlock } from "./ThinkingPart.tsx";
import { ToolRow, durationText } from "./ToolPart.tsx";

/** Razonamiento de hasta este largo (caracteres) se ve abierto dentro del paso; el más largo, plegado. */
const SHORT_THINKING = 280;

const nf = new Intl.NumberFormat("es-ES");

/** Clase de un nodo: todo paso del flujo es un agente (`llm` o `step`); `tool` es una herramienta lanzada como paso. */
export function nodeClass(kind: AgentNodePart["kind"]): NodeClass {
  return kind === "tool" ? "tool" : "agent";
}

/** «gpt-5 · 1.200 entrada · 340 salida · 0,0123 USD»: solo lo que trae el paso (`step.completed`). */
export function agentMeta(part: AgentNodePart, l: Labels): string | undefined {
  const bits: string[] = [];
  if (part.model) bits.push(part.model);
  if (part.inputTokens !== undefined) bits.push(`${nf.format(part.inputTokens)} ${l.tokensIn}`);
  if (part.outputTokens !== undefined) bits.push(`${nf.format(part.outputTokens)} ${l.tokensOut}`);
  if (part.costUsd !== undefined) bits.push(`${part.costUsd.toFixed(4)} USD`);
  return bits.length > 0 ? bits.join(" · ") : undefined;
}

/**
 * Nodo de la línea de tiempo (agente o herramienta lanzada como paso): icono de estado, icono de su tipo (sin
 * etiqueta de clase), título (`display` o `label ?? name`), «intento n» si se reintentó, duración (cronómetro vivo mientras corre), `detail` en gris debajo, el chip de modelo/tokens/coste de un agente con modelo y el resultado
 * (`summary`, salvo que ya lo diga el `display` del fin) al terminar. Entre la cabecera y el resultado, la secuencia lineal del
 * paso (`flow`, en orden de llegada): razonamiento atenuado, filas de herramienta y texto del modelo. `MessageList` la pinta dentro del panel de pasos (`toolGroup`); sustituible con
 * `createRenderers(defaultRenderers, { agent: … })`.
 */
export function AgentPart({ part, message, ctx }: RendererProps<AgentNodePart>) {
  const l = ctx.labels;
  const cls = nodeClass(part.kind);
  const running = part.status === "running";
  const live = useLiveElapsed(running, part.attempt);
  const meta = agentMeta(part, l);
  const flow = part.flow ?? part.tools ?? [];
  const TextR = ctx.renderers?.text ?? TextPart;
  return (
    <div className={`atc-node atc-agent atc-agent-${part.status}`} data-status={part.status} data-kind={part.kind} data-class={cls} data-testid={`agent-${part.id}`}>
      <div className="atc-agent-head atc-node-head">
        <span className="atc-tool-icon" aria-hidden="true"><StatusIcon status={part.status} /></span>
        <ClassIcon kind={cls} label={cls === "tool" ? l.iconCall : l.iconStep} />
        <span className="atc-agent-name">{nodeTitle(part)}</span>
        {part.attempt > 1 ? <span className="atc-agent-attempt" data-testid="agent-attempt">{l.attempt} {part.attempt}</span> : null}
        {running ? <span className="atc-tool-state atc-sr">{l.toolRunning}</span> : null}
        {part.status === "error" && part.errorCode ? <code className="atc-tool-code">{part.errorCode}</code> : null}
        {part.elapsedMs !== undefined || live !== undefined ? <span className="atc-tool-time atc-node-time" data-testid="node-time">{live !== undefined ? liveText(live) : durationText(part.elapsedMs!)}</span> : null}
      </div>
      {part.detail ? <div className="atc-node-detail" data-testid="node-detail">{part.detail}</div> : null}
      {meta ? <div className="atc-node-meta atc-node-chip" data-testid="node-meta">{meta}</div> : null}
      {flow.length > 0 ? (
        <div className="atc-node-flow" data-testid="node-flow">
          {flow.map((it) =>
            it.type === "tool" ? (
              <ToolRow key={it.id} part={it} labels={l} inspector={ctx.toolInspector === true} tagged />
            ) : it.type === "thinking" ? (
              <div key={it.id} className="atc-flow-item" data-testid="flow-thinking">
                <ThinkingBlock text={it.text} label={l.thinking} defaultOpen={it.text.length <= SHORT_THINKING} className="atc-flow-thinking" />
              </div>
            ) : (
              <div key={it.id} className="atc-flow-item atc-flow-text" data-testid="flow-text">
                <TextR part={it} message={message} ctx={ctx} />
              </div>
            ),
          )}
        </div>
      ) : null}
      {part.summary && !running ? <div className="atc-node-result" data-testid="node-summary">{part.summary}</div> : null}
    </div>
  );
}
