/**
 * HTML subido (plan v2 §7.5, V2-D16): `<iframe sandbox="">` sin ningún permiso, sobre `/raw`, que además sale con
 * `Content-Security-Policy: sandbox`. Nunca se ejecuta un script suyo ni carga recursos remotos.
 */
export function HtmlViewer({ src, title }: { src: string; title: string }) {
  return (
    <div className="viewer viewer--html">
      <iframe className="viewer__frame viewer__frame--html" src={src} title={`HTML: ${title}`} sandbox="" referrerPolicy="no-referrer" />
    </div>
  );
}
