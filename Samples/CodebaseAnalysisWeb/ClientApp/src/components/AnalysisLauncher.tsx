import { useEffect, useId, useState, type FormEvent } from 'react';
import type { MetaDto, StartAnalysisRequest } from '../api/types';

interface Props {
  meta: MetaDto | null;
  /** Motivo por el que no se puede lanzar (sin workspace...), o null. */
  blockedReason: string | null;
  running: boolean;
  starting: boolean;
  cancelling: boolean;
  onStart: (request: StartAnalysisRequest) => void;
  onCancel: () => void;
}

function initialSelection(meta: MetaDto | null): Set<string> {
  if (!meta) return new Set();
  const known = new Set(meta.reports.map((r) => r.type));
  const defaults = meta.defaultReports.filter((t) => known.has(t));
  return new Set(defaults.length > 0 ? defaults : meta.reports.map((r) => r.type));
}

export function AnalysisLauncher({ meta, blockedReason, running, starting, cancelling, onStart, onCancel }: Props) {
  const [forceReindex, setForceReindex] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(() => initialSelection(meta));
  const [touched, setTouched] = useState(false);
  const warningId = useId();
  const reportsLegendId = useId();

  // La meta puede llegar después del primer render.
  useEffect(() => {
    if (!touched) setSelected(initialSelection(meta));
  }, [meta, touched]);

  const reports = meta?.reports ?? [];
  const toggle = (type: string) => {
    setTouched(true);
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(type)) next.delete(type);
      else next.add(type);
      return next;
    });
  };
  const setAll = (all: boolean) => {
    setTouched(true);
    setSelected(new Set(all ? reports.map((r) => r.type) : []));
  };

  const nothingSelected = selected.size === 0;
  const canStart = !blockedReason && !running && !starting && !nothingSelected && reports.length > 0;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!canStart) return;
    // Orden del catálogo, con el nombre del enum (lo acepta ParseReportType, §4.1).
    onStart({ forceReindex, reports: reports.filter((r) => selected.has(r.type)).map((r) => r.type) });
  };

  return (
    <form className="launcher" onSubmit={submit} aria-label="Lanzar análisis">
      <h2 className="panel-title">Nuevo análisis</h2>

      <fieldset className="launcher__reports" disabled={running} aria-describedby={reportsLegendId}>
        <legend>Informes</legend>
        <div className="launcher__bulk">
          <button type="button" className="btn btn--link" onClick={() => setAll(true)}>
            Todos
          </button>
          <span aria-hidden="true">·</span>
          <button type="button" className="btn btn--link" onClick={() => setAll(false)}>
            Ninguno
          </button>
          <span className="launcher__count" id={reportsLegendId}>
            {selected.size} de {reports.length}
          </span>
        </div>
        {reports.length === 0 && <p className="muted">Cargando catálogo de informes…</p>}
        <ul className="checklist">
          {reports.map((r) => (
            <li key={r.type}>
              <label className="check">
                <input type="checkbox" checked={selected.has(r.type)} onChange={() => toggle(r.type)} />
                <span className="check__file">{r.fileName.slice(0, 2)}</span>
                <span className="check__label">{r.title}</span>
              </label>
            </li>
          ))}
        </ul>
      </fieldset>

      <div className="launcher__option">
        <label className="check">
          <input
            type="checkbox"
            checked={forceReindex}
            disabled={running}
            onChange={(e) => setForceReindex(e.target.checked)}
            aria-describedby={warningId}
          />
          <span className="check__label">Forzar reindexación</span>
        </label>
        <p id={warningId} className={`hint${forceReindex ? ' hint--warning' : ''}`}>
          Vuelve a indexar el repositorio y mueve la base de datos y los informes de la ejecución anterior a{' '}
          <code>backups/</code>.
        </p>
      </div>

      {blockedReason && <p className="hint hint--warning">{blockedReason}</p>}
      {!blockedReason && nothingSelected && !running && <p className="hint">Marca al menos un informe.</p>}

      <div className="launcher__actions">
        <button type="submit" className="btn btn--primary" disabled={!canStart}>
          {starting ? 'Lanzando…' : 'Lanzar análisis'}
        </button>
        {running && (
          <button type="button" className="btn btn--danger" onClick={onCancel} disabled={cancelling}>
            {cancelling ? 'Cancelando…' : 'Cancelar análisis'}
          </button>
        )}
      </div>
    </form>
  );
}
