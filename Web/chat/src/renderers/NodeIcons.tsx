import { useEffect, useRef, useState } from "react";

/** Clase de un nodo de la línea de tiempo: agente (todo paso del flujo, con o sin modelo) o herramienta. */
export type NodeClass = "agent" | "tool";

const base = { viewBox: "0 0 16 16", width: 14, height: 14, "aria-hidden": true, focusable: "false", fill: "none", stroke: "currentColor", strokeWidth: 1.3, strokeLinecap: "round", strokeLinejoin: "round" } as const;

/** Icono de la clase del nodo: destello (agente) o llave (herramienta). Distinto por forma, no por color. */
export function ClassIcon({ kind, label }: { kind: NodeClass; /** Nombre accesible (sin palabra de clase visible). */ label?: string }) {
  return (
    <svg {...base} {...(label ? { "aria-hidden": false, role: "img", "aria-label": label } : {})} className={`atc-node-icon atc-node-icon-${kind}`} data-icon={kind}>
      {kind === "agent" ? (
        <>
          <path d="M6.5 2.5l1.3 3.4 3.4 1.3-3.4 1.3-1.3 3.4-1.3-3.4-3.4-1.3 3.4-1.3z" />
          <path d="M12 9.5l.6 1.6 1.6.6-1.6.6-.6 1.6-.6-1.6-1.6-.6 1.6-.6z" />
        </>
      ) : (
        <path d="M10.6 2a3.4 3.4 0 0 0-3.2 4.5L2 11.9 4.1 14l5.4-5.4A3.4 3.4 0 0 0 14 5.4l-2.1 2.1-1.9-.4-.4-1.9L11.7 3.1A3.4 3.4 0 0 0 10.6 2z" />
      )}
    </svg>
  );
}

/** Estado: en curso (arco que gira; sin animación con `prefers-reduced-motion`), correcto o error. */
export function StatusIcon({ status }: { status: "running" | "ok" | "error" }) {
  return (
    <svg {...base} className={`atc-status-icon atc-status-${status}${status === "running" ? " atc-spin" : ""}`} data-icon={status}>
      {status === "running" ? <path d="M8 2a6 6 0 1 1-6 6" /> : status === "ok" ? <path d="M3 8.5l3.2 3.2L13 4.8" /> : <path d="M4 4l8 8M12 4l-8 8" />}
    </svg>
  );
}

/** Cronómetro vivo: milisegundos desde que el nodo empezó a correr (`undefined` si no corre). Se reinicia con `key` (el intento). */
export function useLiveElapsed(running: boolean, key: unknown = 0): number | undefined {
  const start = useRef(0);
  const [now, setNow] = useState(0);
  if (running && start.current === 0) start.current = Date.now();
  useEffect(() => {
    if (!running) {
      start.current = 0;
      return;
    }
    start.current = Date.now();
    setNow(Date.now());
    const id = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(id);
  }, [running, key]);
  return running ? Math.max(0, (now || Date.now()) - start.current) : undefined;
}

/** Cronómetro en segundos enteros: «3 s». */
export function liveText(ms: number): string {
  return `${Math.floor(ms / 1000)} s`;
}

/**
 * Título de un nodo: su `display` (el del inicio mientras corre; al terminar el del fin, que lo sustituye) y, sin
 * `display`, `label ?? name`. Sustituye a cualquier etiqueta de clase.
 */
export function nodeTitle(part: { status: string; label?: string; name: string; displayStart?: string; displayEnd?: string }): string {
  return (part.status === "running" ? part.displayStart : (part.displayEnd ?? part.displayStart)) ?? part.label ?? part.name;
}
