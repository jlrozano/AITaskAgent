// Markdown del asistente (contenido NO confiable, sale de un modelo) con Streamdown: tolera bloques sin cerrar mientras
// llega el texto y recibe `isAnimating`. El saneado es el de Streamdown (rehype-raw → rehype-sanitize con el esquema por
// defecto de GitHub → rehype-harden); aquí solo se endurece: sin imágenes remotas y sin protocolos fuera de http(s)/mailto.
// Mermaid, Shiki, KaTeX… son plugins opcionales (`plugins`), nunca dependencias del kit (ver mermaid.ts).
import type { ReactNode } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { Streamdown, defaultRehypePlugins, defaultRemarkPlugins, type Components, type MermaidErrorComponentProps, type PluginConfig, type StreamdownProps } from "streamdown";

export type MarkdownPlugins = PluginConfig;
export type MarkdownComponents = Components;
export type MarkdownRehypePlugins = NonNullable<StreamdownProps["rehypePlugins"]>;

/** Lo que el anfitrión puede añadir al renderizador `text` sin sustituirlo (se pasa en `ChatView.markdown` o `RendererContext.markdown`). */
export interface MarkdownOptions {
  /** Plugins de Streamdown (`mermaid`, `code`, `math`, `cjk`, `renderers`). Sin `mermaid`, el bloque ```mermaid se queda como código. */
  plugins?: MarkdownPlugins;
  /** Componentes que sustituyen a elementos (`a`, `code`, `table`…); ganan a los del kit. */
  components?: MarkdownComponents;
  /** Plugins rehype EXTRA: se ejecutan después del saneado del kit (no lo sustituyen). Son responsables de lo que generan. */
  rehypePlugins?: MarkdownRehypePlugins;
  /** Tema de Mermaid: «auto» (por defecto) lo deduce del color de texto heredado del chat (`--atc-fg`/`color`). */
  mermaidTheme?: "auto" | "default" | "dark" | "neutral" | "forest";
}

type Pluggable = MarkdownRehypePlugins[number];
type HNode = { type: string; value?: string; tagName?: string; children?: HNode[] };

// Saneado: el esquema de rehype-sanitize que trae Streamdown (el de GitHub: sin script/iframe/style ni on*, enlaces solo
// http(s)/mailto/relativos) SIN `img` (el texto del modelo no debe provocar peticiones a terceros al pintarse). El HTML
// crudo no se interpreta: se muestra como texto (`htmlAsText`), igual que hacía el renderizador anterior.
function strictSanitize(): Pluggable {
  const base = (defaultRehypePlugins as Record<string, Pluggable>).sanitize as [unknown, { tagNames?: string[] }];
  const schema = base[1];
  return [base[0], { ...schema, tagNames: (schema.tagNames ?? []).filter((t) => t !== "img") }] as unknown as Pluggable;
}

/** El saneado del kit y detrás los rehype del anfitrión. NUNCA se pasan los del anfitrión solos: reemplazarían el saneado. */
export function safeRehypePlugins(extra: MarkdownRehypePlugins = []): MarkdownRehypePlugins {
  return [strictSanitize(), ...extra];
}

/** Remark: los nodos `html` (en bloque o en línea) pasan a texto, para que se vean literales en vez de interpretarse. */
function htmlAsText() {
  const walk = (n: HNode): void => {
    if (n.type === "html") n.type = "text";
    n.children?.forEach(walk);
  };
  return (tree: HNode) => walk(tree);
}

/** Remark de Streamdown (gfm…) y el del kit. */
export function safeRemarkPlugins(): NonNullable<StreamdownProps["remarkPlugins"]> {
  return [...Object.values(defaultRemarkPlugins), htmlAsText] as NonNullable<StreamdownProps["remarkPlugins"]>;
}

// Streamdown pinta `strong` como <span class="font-semibold"> y los enlaces con rel="noreferrer": clases de Tailwind que el kit
// no trae. Se devuelven a elementos semánticos (negrita real; enlaces http(s)/mailto en pestaña nueva con rel seguro).
const Strong = ({ children }: { children?: ReactNode }) => <strong>{children}</strong>;
const Anchor = ({ href, children, title }: { href?: string; children?: ReactNode; title?: string }) => {
  const external = /^(?:https?:|mailto:)/i.test(href ?? "");
  return (
    <a href={href} title={title} {...(external ? { target: "_blank", rel: "noopener noreferrer nofollow" } : {})}>
      {children}
    </a>
  );
};

/** Componentes del kit y encima los del anfitrión (ganan los del anfitrión). */
export function safeComponents(extra: MarkdownComponents = {}): MarkdownComponents {
  return { strong: Strong, a: Anchor, ...extra } as MarkdownComponents;
}

/** Error de diagrama = bloque de código con el mensaje y el código fuente; nunca un hueco. */
export function MermaidError({ chart, error, label }: MermaidErrorComponentProps & { label: string }) {
  return (
    <div className="atc-mermaid-error" role="alert" data-testid="mermaid-error">
      <p className="atc-mermaid-error-msg">{label}: {error}</p>
      <pre><code className="language-mermaid">{chart}</code></pre>
    </div>
  );
}

/**
 * Markdown (contenido no confiable) a HTML saneado, síncrono: la misma tubería que el renderizador `text` (saneado del kit,
 * HTML crudo como texto, sin imágenes, sin controles) renderizada con `react-dom/server`. Para pintar con
 * `dangerouslySetInnerHTML` fuera del chat. Sin Mermaid: el bloque ```mermaid sale como código. Restituye el export que
 * se retiró con la migración a Streamdown, con la misma firma `(md: string) => string`; `options` es aditivo.
 */
export function renderMarkdown(md: string, options: Pick<MarkdownOptions, "components" | "rehypePlugins"> = {}): string {
  return renderToStaticMarkup(
    <div className="atc-md">
      <Streamdown mode="static" controls={false} linkSafety={{ enabled: false }} components={safeComponents(options.components)} rehypePlugins={safeRehypePlugins(options.rehypePlugins)} remarkPlugins={safeRemarkPlugins()}>
        {md}
      </Streamdown>
    </div>,
  );
}
