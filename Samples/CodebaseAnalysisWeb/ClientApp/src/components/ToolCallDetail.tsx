import { useEffect, useId, useRef, useState } from 'react';
import { formatBytes, formatDuration, formatTime } from '../format';
import type { ToolChipStatus } from './ToolCallChip';

/** Lo que se enseña de una llamada a herramienta, venga de un carril del análisis o del chat. */
export interface ToolCallView {
  name: string;
  /** Step que la lanzó (`Auditor_CodeSmells`, `Investigation_…`); null en el chat. */
  step?: string | null;
  startedAt?: string | null;
  durationMs?: number | null;
  status: ToolChipStatus;
  error?: string | null;
  arguments?: string | null;
  argumentsTruncated?: boolean;
  output?: string | null;
  outputTruncated?: boolean;
  outputBytes?: number | null;
  outputDropped?: boolean;
  /** Por qué no hay salida cuando no es un fallo ni un descarte (p. ej. un turno de chat guardado). */
  outputNote?: string | null;
}

const STATUS_LABELS: Record<ToolChipStatus, string> = {
  pending: 'En cola',
  running: 'Ejecutando',
  ok: 'Correcta',
  error: 'Con error',
  unknown: 'Sin resultado',
};

/** El texto con sangría si es JSON válido; si no (o viene truncado), tal cual. */
export function prettyJson(text: string): string {
  const trimmed = text.trim();
  if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) return text;
  try {
    return JSON.stringify(JSON.parse(trimmed), null, 2);
  } catch {
    return text;
  }
}

function CopyButton({ text, label }: { text: string; label: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle');
  useEffect(() => {
    if (state === 'idle') return;
    const timer = window.setTimeout(() => setState('idle'), 1800);
    return () => window.clearTimeout(timer);
  }, [state]);
  return (
    <button
      type="button"
      className="btn btn--ghost btn--small"
      aria-label={label}
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text);
          setState('copied');
        } catch {
          setState('failed');
        }
      }}
    >
      {state === 'copied' ? 'Copiado' : state === 'failed' ? 'No se pudo copiar' : 'Copiar'}
    </button>
  );
}

function Block({ title, text, copyLabel, notice }: { title: string; text: string | null; copyLabel: string; notice?: string | null }) {
  const id = useId();
  return (
    <section className="tool-detail__block" aria-labelledby={id}>
      <header className="tool-detail__block-head">
        <h3 id={id} className="tool-detail__block-title">
          {title}
        </h3>
        {text !== null && text !== '' && <CopyButton text={text} label={copyLabel} />}
      </header>
      {notice && <p className="tool-detail__notice">{notice}</p>}
      {text !== null && text !== '' && (
        <pre className="tool-detail__pre" tabIndex={0}>
          {text}
        </pre>
      )}
    </section>
  );
}

function outputNotice(call: ToolCallView, output: string | null): string | null {
  if (call.outputTruncated && call.outputBytes != null && output !== null) {
    return `Salida truncada: se muestran ${formatBytes(new TextEncoder().encode(call.output ?? '').length)} de ${formatBytes(call.outputBytes)}.`;
  }
  if (call.outputDropped) return 'La salida ya no está disponible: se descartó para acotar la memoria del registro (solo se guardan las más recientes).';
  if (output !== null) return null;
  if (call.status === 'running' || call.status === 'pending') return 'La herramienta todavía no ha terminado.';
  if (call.status === 'error') return 'La herramienta falló: no hay salida.';
  return call.outputNote ?? 'El servidor no envió la salida de esta llamada.';
}

interface Props {
  call: ToolCallView | null;
  onClose: () => void;
  /** Posición en la lista de llamadas del carril o del turno, con «Anterior» y «Siguiente». */
  position?: { index: number; total: number; onPrev: () => void; onNext: () => void };
}

/**
 * Detalle de una llamada a herramienta en un `<dialog>` modal (el navegador atrapa el foco y cierra con Esc): nombre, paso, hora y
 * duración, la entrada (JSON con sangría) y la salida (texto; JSON con sangría si lo es), cada una con «Copiar».
 */
export function ToolCallDetail({ call, onClose, position }: Props) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const open = call !== null;

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    else if (!open && dialog.open) dialog.close();
  }, [open]);

  const input = call?.arguments ? prettyJson(call.arguments) : null;
  const output = call?.output != null ? prettyJson(call.output) : null;

  return (
    <dialog
      ref={ref}
      className="tool-detail"
      aria-labelledby={titleId}
      onClose={onClose}
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      {call && (
        <div className="tool-detail__body">
          <header className="tool-detail__head">
            <h2 id={titleId} className="tool-detail__title">
              <span className={`tool-detail__dot tool-detail__dot--${call.status}`} aria-hidden="true" />
              {call.name}
            </h2>
            <span className={`tool-detail__status tool-detail__status--${call.status}`}>{STATUS_LABELS[call.status]}</span>
            <button type="button" className="btn btn--ghost btn--small tool-detail__close" onClick={onClose} aria-label="Cerrar">
              ×
            </button>
          </header>
          <dl className="tool-detail__meta">
            {call.step && (
              <div>
                <dt>Paso</dt>
                <dd className="tool-detail__mono">{call.step}</dd>
              </div>
            )}
            {call.startedAt && (
              <div>
                <dt>Hora</dt>
                <dd>
                  <time dateTime={call.startedAt}>{formatTime(call.startedAt)}</time>
                </dd>
              </div>
            )}
            <div>
              <dt>Duración</dt>
              <dd>{formatDuration(call.durationMs)}</dd>
            </div>
            {call.outputBytes != null && (
              <div>
                <dt>Salida</dt>
                <dd>{formatBytes(call.outputBytes)}</dd>
              </div>
            )}
          </dl>
          {call.error && (
            <p className="tool-detail__error" role="alert">
              {call.error}
            </p>
          )}
          <Block
            title="Entrada"
            text={input}
            copyLabel="Copiar la entrada"
            notice={!input ? 'Sin argumentos registrados.' : call.argumentsTruncated ? 'Argumentos truncados.' : null}
          />
          <Block title="Salida" text={output} copyLabel="Copiar la salida" notice={outputNotice(call, output)} />
          {position && position.total > 1 && (
            <footer className="tool-detail__nav">
              <button type="button" className="btn btn--small" onClick={position.onPrev} disabled={position.index <= 0}>
                Anterior
              </button>
              <span className="tool-detail__pos">
                {position.index + 1} de {position.total}
              </span>
              <button type="button" className="btn btn--small" onClick={position.onNext} disabled={position.index >= position.total - 1}>
                Siguiente
              </button>
            </footer>
          )}
        </div>
      )}
    </dialog>
  );
}
