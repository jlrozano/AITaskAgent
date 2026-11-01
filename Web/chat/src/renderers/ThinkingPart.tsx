import { useState } from "react";
import type { RendererProps } from "../registry.ts";
import type { ThinkingPart as Part } from "../state.ts";

/** Razonamiento plegable. Suelto, plegado por defecto; dentro de un paso (`defaultOpen` si es corto) va abierto y atenuado. */
export function ThinkingBlock({ text, label, defaultOpen = false, className = "" }: { text: string; label: string; defaultOpen?: boolean; className?: string }) {
  // El estado inicial se decide al montar: un razonamiento que crece en streaming no se pliega solo.
  const [open, setOpen] = useState(defaultOpen);
  return (
    <details className={`atc-thinking${className ? ` ${className}` : ""}`} open={open} onToggle={(e) => setOpen((e.currentTarget as HTMLDetailsElement).open)}>
      <summary>{label}</summary>
      <p>{text}</p>
    </details>
  );
}

/** Razonamiento plegado por defecto. */
export function ThinkingPart({ part, ctx }: RendererProps<Part>) {
  return <ThinkingBlock text={part.text} label={ctx.labels.thinking} />;
}
