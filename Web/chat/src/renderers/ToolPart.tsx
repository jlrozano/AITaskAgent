import { useRef, useState } from "react";
import type { Labels } from "../labels.ts";
import type { RendererProps } from "../registry.ts";
import type { ToolPart as Part } from "../state.ts";
import { ClassIcon, StatusIcon, liveText, nodeTitle, useLiveElapsed } from "./NodeIcons.tsx";
import { ToolInspector } from "./ToolInspector.tsx";

/** «1,2 s» o «340 ms». */
export function durationText(ms: number): string {
  return ms >= 1000 ? `${(ms / 1000).toFixed(1).replace(".", ",")} s` : `${Math.round(ms)} ms`;
}

/** Argumentos de la llamada en una línea y truncados (el texto completo queda en `title`). */
export function inputText(input: unknown, max = 80): string | undefined {
  if (input === undefined || input === null) return undefined;
  let t: string;
  try {
    t = typeof input === "string" ? input : JSON.stringify(input);
  } catch {
    return undefined;
  }
  if (!t || t === "{}") return undefined;
  return t.length > max ? `${t.slice(0, max - 1)}…` : t;
}

/** Una línea por herramienta: icono de estado, título (`display` de inicio/fin, o etiqueta/nombre; el nombre técnico queda en `title`), resumen, código de error y duración. */
export function ToolRow({ part, labels, inspector = false, tagged = false }: { part: Part; labels: Labels; /** `toolInspector`: sin argumentos en línea; botón de icono que abre la modal con entrada y salida. */ inspector?: boolean; /** Dentro de una línea de tiempo con agentes: icono de estado y de tipo (llave), sin etiqueta de clase. */ tagged?: boolean }) {
  const icon = part.status === "running" ? "…" : part.status === "ok" ? "✓" : "✗";
  const live = useLiveElapsed(tagged && part.status === "running");
  const title = nodeTitle(part);
  const args = inspector ? undefined : inputText(part.input);
  const [open, setOpen] = useState(false);
  const button = useRef<HTMLButtonElement>(null);
  return (
    <div className={`atc-tool atc-tool-${part.status}`} data-status={part.status} data-testid={`step-${part.name}`} {...(tagged ? { "data-tagged": "" } : {})}>
      {tagged ? (
        <>
          <span className="atc-tool-icon" aria-hidden="true"><StatusIcon status={part.status} /></span>
          <ClassIcon kind="tool" label={labels.iconCall} />
        </>
      ) : (
        <>
          <span className="atc-tool-icon" aria-hidden="true">{icon}</span>{" "}
        </>
      )}
      {inspector ? (
        <button type="button" className="atc-tool-name atc-tool-link" aria-haspopup="dialog" data-testid="tool-name-link" title={part.name} onClick={() => setOpen(true)}>{title}</button>
      ) : (
        <span className="atc-tool-name" title={part.name}>{title}</span>
      )}{" "}
      {args ? <code className="atc-tool-args" title={typeof part.input === "string" ? part.input : JSON.stringify(part.input)}>{args}</code> : null}{" "}
      {part.status === "running" ? <span className="atc-tool-state">{labels.toolRunning}</span> : null}
      {part.summary && !inspector ? <span className="atc-tool-summary">{part.summary}</span> : null}
      {part.status === "error" && part.errorCode ? <code className="atc-tool-code">{part.errorCode}</code> : null}
      {part.elapsedMs !== undefined || live !== undefined ? <span className="atc-tool-time">{live !== undefined ? liveText(live) : durationText(part.elapsedMs!)}</span> : null}
      {inspector ? (
        <button ref={button} type="button" className="atc-tool-inspect" aria-label={labels.toolInspect} title={labels.toolInspect} aria-haspopup="dialog" data-testid="tool-inspect" onClick={() => setOpen(true)}>
          <svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true" focusable="false">
            <path d="M1.5 8s2.3-4.5 6.5-4.5S14.5 8 14.5 8 12.2 12.5 8 12.5 1.5 8 1.5 8z" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" />
            <circle cx="8" cy="8" r="1.9" fill="none" stroke="currentColor" strokeWidth="1.3" />
          </svg>
        </button>
      ) : null}
      {inspector && open ? <ToolInspector part={part} labels={labels} onClose={() => setOpen(false)} /> : null}
    </div>
  );
}

export function ToolPart({ part, ctx }: RendererProps<Part>) {
  return <ToolRow part={part} labels={ctx.labels} inspector={ctx.toolInspector === true} />;
}
