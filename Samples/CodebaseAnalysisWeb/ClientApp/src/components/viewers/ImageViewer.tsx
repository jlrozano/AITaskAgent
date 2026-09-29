import type { ReactNode } from 'react';

/** Imagen o SVG con `<img>` sobre `/raw` (plan v2 §7.5): un SVG dentro de `<img>` no ejecuta scripts. */
export function ImageViewer({ src, title, actions }: { src: string; title: string; actions?: ReactNode }) {
  return (
    <div className="viewer viewer--image">
      {actions && <p className="viewer__bar">{actions}</p>}
      <div className="viewer__canvas">
        <img className="viewer__img" src={src} alt={title} />
      </div>
    </div>
  );
}
