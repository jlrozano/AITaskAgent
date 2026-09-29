import { memo, useMemo, useRef, type ReactNode } from 'react';
import Markdown, { defaultUrlTransform, type Components, type Options, type UrlTransform } from 'react-markdown';
import rehypeHighlight from 'rehype-highlight';
import remarkGfm from 'remark-gfm';
import { MermaidDiagram } from './MermaidDiagram';

interface HastNode {
  type: string;
  tagName?: string;
  value?: string;
  properties?: Record<string, unknown>;
  children?: HastNode[];
}

function hastText(node: HastNode | undefined): string {
  if (!node) return '';
  if (node.type === 'text') return node.value ?? '';
  return (node.children ?? []).map(hastText).join('');
}

function mermaidSource(pre: HastNode | undefined): string | null {
  const code = pre?.children?.find((c) => c.type === 'element' && c.tagName === 'code');
  const cls = code?.properties?.className;
  if (Array.isArray(cls) && cls.includes('language-mermaid')) return hastText(code).replace(/\n$/, '');
  return null;
}

/** Encabezados `##` fuera de bloques de código, con el Markdown inline quitado. */
export function extractH2(source: string): string[] {
  const headings: string[] = [];
  let fence: string | null = null;
  for (const line of source.split(/\r?\n/)) {
    const f = /^\s{0,3}(`{3,}|~{3,})/.exec(line);
    if (f) {
      if (fence === null) fence = f[1][0];
      else if (f[1][0] === fence) fence = null;
      continue;
    }
    if (fence !== null) continue;
    const m = /^\s{0,3}##(?!#)\s+(.+?)\s*#*\s*$/.exec(line);
    if (m) {
      headings.push(
        m[1]
          .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
          .replace(/[`*_~]/g, '')
          .trim(),
      );
    }
  }
  return headings;
}

const components: Components = {
  pre({ node, children, ...rest }) {
    const mermaid = mermaidSource(node as HastNode | undefined);
    if (mermaid !== null) return <MermaidDiagram code={mermaid} />;
    return <pre {...rest}>{children}</pre>;
  },
  table({ node: _node, children, ...rest }) {
    return (
      <div className="md-table-wrap" tabIndex={0} role="region" aria-label="Tabla">
        <table {...rest}>{children}</table>
      </div>
    );
  },
  // Plan v2 §7.5: solo se pintan imágenes `data:` y rutas propias de la API. Una remota en un Markdown subido o en un
  // informe sale como enlace, para que abrir un documento no avise a un servidor ajeno.
  img({ node: _node, src, alt, ...rest }) {
    const url = typeof src === 'string' ? src : '';
    if (/^data:image\//i.test(url) || url.startsWith('/api/')) return <img src={url} alt={alt ?? ''} {...rest} />;
    return (
      <a href={url} className="md-img-link" target="_blank" rel="noopener noreferrer" title="Imagen externa: no se carga">
        {alt ? `Imagen: ${alt}` : 'Imagen externa'}
      </a>
    );
  },
  a({ node: _node, href, children, ...rest }) {
    const external = !!href && /^https?:\/\//i.test(href);
    return (
      <a href={href} {...rest} {...(external ? { target: '_blank', rel: 'noopener noreferrer' } : {})}>
        {children}
      </a>
    );
  },
};

const remarkPlugins = [remarkGfm];
// La transformación por defecto quita `data:`; en `src` de una imagen se deja pasar `data:image/…`, que en <img> no
// ejecuta nada (§7.5). El resto de URLs siguen la regla por defecto.
const urlTransform: UrlTransform = (url, key) =>
  key === 'src' && /^data:image\//i.test(url) ? url : defaultUrlTransform(url);
// Resaltado de los bloques con lenguaje (V2-C): los colores son variables del tema en styles.css (`--hl-*`). mermaid
// se deja como texto porque lo dibuja MermaidDiagram; un lenguaje que highlight.js no conoce se queda sin resaltar.
const rehypePlugins: NonNullable<Parameters<typeof Markdown>[0]['rehypePlugins']> = [[rehypeHighlight, { plainText: ['mermaid'] }]];

interface Props {
  source: string;
  /** Muestra el índice lateral con los `##`. */
  toc?: boolean;
  className?: string;
  label?: string;
  /** Plugins de remark detrás de GFM (p. ej. las marcas del chat). Referencia estable: cambiarla vuelve a pintar todo. */
  plugins?: Options['remarkPlugins'];
  /** Componentes que sustituyen a los de serie. Referencia estable, como `plugins`. */
  overrides?: Components;
}

/**
 * Markdown GFM con diagramas mermaid. El HTML crudo se descarta con `skipHtml`: sin él, react-markdown 10 lo
 * muestra escapado como texto y el comentario `analysis-meta` de los informes aparecería (§7).
 */
export const MarkdownView = memo(function MarkdownView({ source, toc = false, className, label, plugins, overrides }: Props) {
  const articleRef = useRef<HTMLDivElement>(null);
  const headings = useMemo(() => (toc ? extractH2(source) : []), [source, toc]);
  const allPlugins = useMemo(() => (plugins?.length ? [...remarkPlugins, ...plugins] : remarkPlugins), [plugins]);
  const allComponents = useMemo(() => (overrides ? { ...components, ...overrides } : components), [overrides]);

  const goTo = (index: number) => {
    const target = articleRef.current?.querySelectorAll('h2')[index];
    if (!target) return;
    target.scrollIntoView({ behavior: 'smooth', block: 'start' });
    (target as HTMLElement).setAttribute('tabindex', '-1');
    (target as HTMLElement).focus({ preventScroll: true });
  };

  let tocNode: ReactNode = null;
  if (toc && headings.length > 1) {
    tocNode = (
      <nav className="md-toc" aria-label="Índice del informe">
        <p className="md-toc__title">En este informe</p>
        <ol>
          {headings.map((h, i) => (
            <li key={`${i}-${h}`}>
              <button type="button" className="md-toc__link" onClick={() => goTo(i)}>
                {h}
              </button>
            </li>
          ))}
        </ol>
      </nav>
    );
  }

  return (
    <div className={`md-layout${tocNode ? ' md-layout--toc' : ''}${className ? ` ${className}` : ''}`}>
      <div className="md" ref={articleRef} aria-label={label}>
        <Markdown remarkPlugins={allPlugins} rehypePlugins={rehypePlugins} components={allComponents} urlTransform={urlTransform} skipHtml>
          {source}
        </Markdown>
      </div>
      {tocNode}
    </div>
  );
});
