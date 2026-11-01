import { useEffect, useMemo, useRef, useState } from "react";
import { Streamdown, type MermaidErrorComponentProps } from "streamdown";
import { MermaidError, safeComponents, safeRehypePlugins, safeRemarkPlugins } from "../markdown.tsx";
import type { RendererProps } from "../registry.ts";
import type { TextPart as Part } from "../state.ts";

/** Luminancia aproximada de un `rgb()/rgba()` calculado; null si no se puede leer. */
function isLightColor(css: string): boolean | null {
  const m = /rgba?\(\s*(\d+)[ ,]+(\d+)[ ,]+(\d+)/.exec(css);
  if (!m) return null;
  return (0.2126 * +m[1]! + 0.7152 * +m[2]! + 0.0722 * +m[3]!) / 255 > 0.5;
}

/** Texto del asistente como markdown (Streamdown, saneado); el del usuario, como texto plano. */
export function TextPart({ part, message, ctx }: RendererProps<Part>) {
  const md = ctx.markdown;
  const ref = useRef<HTMLDivElement>(null);
  const [autoTheme, setAutoTheme] = useState<"default" | "dark">("default");
  const assistant = message.role === "assistant";
  // Tema del anfitrión: el texto hereda `--atc-fg`/`color`; si es claro, el fondo es oscuro y Mermaid usa su tema oscuro.
  useEffect(() => {
    if (!assistant || !ref.current || typeof getComputedStyle !== "function") return;
    const light = isLightColor(getComputedStyle(ref.current).color);
    if (light !== null) setAutoTheme(light ? "dark" : "default");
  });
  const theme = md?.mermaidTheme && md.mermaidTheme !== "auto" ? md.mermaidTheme : autoTheme;
  const rehypePlugins = useMemo(() => safeRehypePlugins(md?.rehypePlugins), [md?.rehypePlugins]);
  const components = useMemo(() => safeComponents(md?.components), [md?.components]);
  const remarkPlugins = useMemo(() => safeRemarkPlugins(), []);
  const label = ctx.labels.mermaidError;
  const mermaid = useMemo(
    () => ({ config: { securityLevel: "strict", theme }, errorComponent: (p: MermaidErrorComponentProps) => <MermaidError {...p} label={label} /> }),
    [theme, label],
  );
  if (!assistant) return <p className="atc-text atc-text-user">{part.text}</p>;
  // `isAnimating`: el turno sigue en curso y esta es la última parte del mensaje (la que está recibiendo texto).
  const last = message.parts[message.parts.length - 1];
  const isAnimating = message.status === "streaming" && last?.id === part.id;
  return (
    <div className="atc-text atc-md" ref={ref} data-streaming={isAnimating ? "true" : undefined}>
      <Streamdown
        isAnimating={isAnimating}
        plugins={md?.plugins}
        components={components}
        rehypePlugins={rehypePlugins}
        remarkPlugins={remarkPlugins}
        mermaid={mermaid}
        controls={false}
        linkSafety={{ enabled: false }}
      >
        {part.text}
      </Streamdown>
    </div>
  );
}
