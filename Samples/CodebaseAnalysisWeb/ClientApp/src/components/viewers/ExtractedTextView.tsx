import { MarkdownView } from '../MarkdownView';

interface Props {
  text: string | null;
  error?: string | null;
  /** `pre`: el visor `text` (preformateado). `markdown`: `content.md` pintado, como en la pestaña «Texto extraído». */
  mode: 'pre' | 'markdown';
  label: string;
  emptyText?: string;
}

/** `content.md` del documento: lo que lee el asistente (plan v2 §7.2, §9). */
export function ExtractedTextView({ text, error, mode, label, emptyText = 'No hay texto extraído.' }: Props) {
  if (error) return <p className="muted">{error}</p>;
  if (text === null) return <p className="muted">Cargando texto…</p>;
  if (!text.trim()) return <p className="muted">{emptyText}</p>;
  if (mode === 'pre')
    return (
      <pre className="viewer__pre" aria-label={label} tabIndex={0}>
        {text}
      </pre>
    );
  return <MarkdownView source={text} toc label={label} />;
}
