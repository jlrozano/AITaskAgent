/**
 * PDF con el visor nativo del navegador (plan v2 §7.5): `<iframe>` sin `sandbox`, porque Chrome no pinta su visor en
 * un contexto aislado. Si no carga (headless, visor desactivado), quedan «Abrir en una pestaña nueva» y la pestaña
 * «Texto extraído».
 */
export function PdfViewer({ src, title }: { src: string; title: string }) {
  return (
    <div className="viewer viewer--pdf">
      <p className="viewer__bar">
        <a className="btn btn--small" href={src} target="_blank" rel="noopener noreferrer">
          Abrir en una pestaña nueva
        </a>
        <span className="hint">Si el PDF no se ve aquí, ábrelo en una pestaña o mira «Texto extraído».</span>
      </p>
      <iframe className="viewer__frame" src={src} title={`PDF: ${title}`} />
    </div>
  );
}
