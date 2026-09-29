import { useCallback, useEffect, useId, useRef, useState, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { useTheme } from '../state/theme';

type Mermaid = (typeof import('mermaid'))['default'];

let mermaidModule: Promise<Mermaid> | null = null;
// Import dinámico: mermaid pesa y solo hace falta en el visor (§8 F3).
function loadMermaid(): Promise<Mermaid> {
  mermaidModule ??= import('mermaid').then((m) => m.default);
  return mermaidModule;
}

// mermaid.render usa estado global: se serializan los renders para que no se pisen.
let queue: Promise<unknown> = Promise.resolve();
function enqueue<T>(task: () => Promise<T>): Promise<T> {
  const run = queue.then(task, task);
  queue = run.catch(() => undefined);
  return run;
}

/** Tamaño natural del diagrama (el `viewBox` que escribe mermaid), o null si no lo trae. */
export function naturalSize(svg: string): { width: number; height: number } | null {
  const m = /viewBox="\s*-?[\d.]+[\s,]+-?[\d.]+[\s,]+([\d.]+)[\s,]+([\d.]+)\s*"/.exec(svg);
  if (!m) return null;
  const width = Number(m[1]);
  const height = Number(m[2]);
  return width > 0 && height > 0 ? { width, height } : null;
}

type Fit = 'natural' | 'fit';

function FitToggle({ fit, onChange }: { fit: Fit; onChange: (fit: Fit) => void }) {
  return (
    <button type="button" className="btn btn--ghost btn--small" onClick={() => onChange(fit === 'fit' ? 'natural' : 'fit')}>
      {fit === 'fit' ? 'Tamaño real' : 'Ajustar al ancho'}
    </button>
  );
}

/** Diagrama a pantalla completa: capa fija sobre la página, Escape o «Cerrar» la cierran y el foco vuelve al botón. */
function FullScreen({ svg, style, onClose }: { svg: string; style: CSSProperties; onClose: () => void }) {
  const [fit, setFit] = useState<Fit>('natural');
  const closeRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    closeRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);
  return createPortal(
    <div className="mermaid-overlay" role="dialog" aria-modal="true" aria-label="Diagrama a pantalla completa">
      <div className="mermaid__bar">
        <FitToggle fit={fit} onChange={setFit} />
        <button ref={closeRef} type="button" className="btn btn--small" onClick={onClose}>
          Cerrar
        </button>
      </div>
      <div
        className={`mermaid__canvas mermaid--${fit}`}
        style={style}
        tabIndex={0}
        role="img"
        aria-label="Diagrama"
        dangerouslySetInnerHTML={{ __html: svg }}
      />
    </div>,
    document.body,
  );
}

/**
 * Diagrama mermaid. Por defecto a tamaño natural con scroll horizontal propio: escalado al ancho, un diagrama ancho
 * quedaba ilegible (D3). Se puede ajustar al ancho o abrir a pantalla completa.
 */
export function MermaidDiagram({ code }: { code: string }) {
  const { resolved } = useTheme();
  const rawId = useId();
  const id = `mmd-${rawId.replace(/[^a-zA-Z0-9]/g, '')}`;
  const [svg, setSvg] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [fit, setFit] = useState<Fit>('natural');
  const [full, setFull] = useState(false);
  const closeFull = useCallback(() => setFull(false), []);
  const openRef = useRef<HTMLButtonElement>(null);
  const wasFull = useRef(false);

  // Al cerrar la pantalla completa, el foco vuelve al botón que la abrió.
  useEffect(() => {
    if (wasFull.current && !full) openRef.current?.focus();
    wasFull.current = full;
  }, [full]);

  useEffect(() => {
    let cancelled = false;
    setError(null);
    enqueue(async () => {
      const mermaid = await loadMermaid();
      mermaid.initialize({
        startOnLoad: false,
        securityLevel: 'strict',
        theme: resolved === 'dark' ? 'dark' : 'neutral',
        fontFamily: 'inherit',
      });
      try {
        return await mermaid.render(id, code);
      } finally {
        // En error mermaid deja su SVG de error colgando del body.
        document.getElementById(`d${id}`)?.remove();
      }
    }).then(
      (result) => {
        if (!cancelled) setSvg(result.svg);
      },
      (e: unknown) => {
        if (cancelled) return;
        setSvg(null);
        setError(e instanceof Error ? e.message : String(e));
      },
    );
    return () => {
      cancelled = true;
    };
  }, [code, id, resolved]);

  if (error) {
    return (
      <figure className="mermaid mermaid--error" role="group" aria-label="Diagrama con error de sintaxis">
        <figcaption>El diagrama no se puede dibujar: {error}</figcaption>
        <pre>
          <code>{code}</code>
        </pre>
      </figure>
    );
  }
  if (!svg) {
    return (
      <figure className="mermaid mermaid--loading" aria-busy="true">
        <figcaption>Dibujando diagrama…</figcaption>
      </figure>
    );
  }
  const size = naturalSize(svg);
  const style = (size ? { '--mmd-w': `${size.width}px` } : {}) as CSSProperties;
  // El SVG sale de mermaid con securityLevel 'strict' (saneado por mermaid). Mientras está a pantalla completa no se
  // pinta aquí: dos copias repetirían los id de los que dependen sus estilos y marcadores.
  return (
    <figure className={`mermaid mermaid--${fit}`} role="group" aria-label="Diagrama" style={style}>
      <div className="mermaid__bar">
        <FitToggle fit={fit} onChange={setFit} />
        <button ref={openRef} type="button" className="btn btn--ghost btn--small" onClick={() => setFull(true)}>
          Pantalla completa
        </button>
      </div>
      {full ? (
        <p className="mermaid__placeholder">Diagrama abierto a pantalla completa.</p>
      ) : (
        <div className="mermaid__canvas" tabIndex={0} role="img" aria-label="Diagrama" dangerouslySetInnerHTML={{ __html: svg }} />
      )}
      {full && <FullScreen svg={svg} style={style} onClose={closeFull} />}
    </figure>
  );
}
