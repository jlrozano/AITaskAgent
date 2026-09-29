import { useState } from 'react';
import type { MetaDto } from '../api/types';
import { formatDuration, formatNumber } from '../format';
import { canRegenerate, type AuditorState, type AuditorStatus, type RunState } from '../state/runReducer';
import { HealthBadge } from './HealthBadge';

export const AUDITOR_STATUS_LABELS: Record<AuditorStatus, string> = {
  pending: 'En espera',
  running: 'Trabajando',
  succeeded: 'Terminado',
  failed: 'Con error',
  cancelled: 'Cancelado',
  skipped: 'No ejecutado',
};

const ms = (iso: string | null) => (iso ? Date.parse(iso) : NaN);

/** Ventana común de los carriles: del primer auditor que arranca al último que termina (o ahora). */
function laneWindow(auditors: AuditorState[], running: boolean, now: number): [number, number] | null {
  const starts = auditors.map((a) => ms(a.startedAt)).filter(Number.isFinite);
  if (starts.length === 0) return null;
  const start = Math.min(...starts);
  const ends = auditors.map((a) => ms(a.finishedAt)).filter(Number.isFinite);
  const anyRunning = auditors.some((a) => a.status === 'running');
  const end = anyRunning || (running && ends.length < auditors.length) ? now : Math.max(start + 1, ...ends);
  return [start, Math.max(end, start + 1000)];
}

interface Props {
  state: RunState;
  meta: MetaDto | null;
  now: number;
  /** Pide regenerar esos informes. Sin él no hay botón (p. ej. si el servidor ya no tiene la ejecución, R7). */
  onRegenerate?: (reports: string[], synthesis: boolean) => void;
  /** Informes cuya regeneración está arrancando: su botón queda desactivado. */
  regenerating?: string[] | null;
  /** Error del servidor al pedir la regeneración, en el carril de esos informes. */
  regenerateError?: { reports: string[]; message: string } | null;
}

/**
 * Un carril por auditor: los auditores corren en paralelo y cada marca del carril es una tool call en su instante.
 * La barra va de su arranque a su final sobre una escala de tiempo común. Con la ejecución terminada, los carriles
 * fallidos o cancelados ofrecen «Regenerar», que vuelve a generar solo ese informe.
 */
export function AuditorGrid({ state, meta, now, onRegenerate, regenerating = null, regenerateError = null }: Props) {
  const [synthesis, setSynthesis] = useState(true);
  const auditors = state.auditorOrder.map((r) => state.auditors[r]);
  if (auditors.length === 0) return null;
  const regenerable = new Set(onRegenerate ? auditors.filter((a) => canRegenerate(state, a.report)).map((a) => a.report) : []);
  const busy = regenerating !== null;
  const titles = new Map(meta?.reports.map((r) => [r.type, r.title]) ?? []);
  const running = state.status === 'running';
  const window = laneWindow(auditors, running, now);
  const pos = (t: number) => (window ? ((t - window[0]) / (window[1] - window[0])) * 100 : 0);

  return (
    <section className="lanes" aria-labelledby="lanes-title">
      <header className="lanes__head">
        <h2 id="lanes-title" className="panel-title">
          Auditores en paralelo
        </h2>
        {window && <span className="lanes__scale">escala {formatDuration(window[1] - window[0])}</span>}
      </header>
      {regenerable.size > 0 && (
        <div className="lanes__regenerate">
          <label className="check check--inline">
            <input type="checkbox" checked={synthesis} disabled={busy} onChange={(e) => setSynthesis(e.target.checked)} />
            <span className="check__label">Actualizar también el resumen ejecutivo</span>
          </label>
        </div>
      )}
      <div className="lane lane--header" aria-hidden="true">
        <span>Auditor</span>
        <span>Actividad · cada marca es una tool call</span>
        <span className="lane__stats lane__stats--header">
          <span>Tiempo</span>
          <span>Tools</span>
          <span>Reintentos</span>
          <span>Tokens</span>
          <span>Semáforo</span>
        </span>
      </div>
      <ul className="lanes__list">
        {auditors.map((a) => {
          const title = titles.get(a.report) ?? a.report;
          const started = ms(a.startedAt);
          const finished = ms(a.finishedAt);
          const elapsed = a.durationMs ?? (Number.isFinite(started) ? (Number.isFinite(finished) ? finished : now) - started : null);
          const barStart = Number.isFinite(started) ? pos(started) : 0;
          const barEnd = Number.isFinite(finished) ? pos(finished) : a.status === 'running' ? pos(now) : barStart;
          return (
            <li key={a.report} className={`lane lane--${a.status}`}>
              <div className="lane__who">
                <span className="lane__status-dot" aria-hidden="true" />
                <span className="lane__title">{title}</span>
                <span className="lane__status">
                  {AUDITOR_STATUS_LABELS[a.status]}
                  {a.activeTool && <span className="lane__tool"> · {a.activeTool}</span>}
                </span>
              </div>
              <div className="lane__track" aria-hidden="true">
                {Number.isFinite(started) && (
                  <span className="lane__bar" style={{ left: `${barStart}%`, width: `${Math.max(barEnd - barStart, 0.6)}%` }} />
                )}
                {a.toolTimes.map((t, i) => (
                  <span key={i} className="lane__tick" style={{ left: `${pos(Date.parse(t))}%` }} />
                ))}
              </div>
              <dl className="lane__stats">
                <div>
                  <dt className="visually-hidden">Tiempo</dt>
                  <dd>{formatDuration(elapsed)}</dd>
                </div>
                <div>
                  <dt className="visually-hidden">Tools</dt>
                  <dd>
                    {a.toolCalls}
                    {a.toolErrors > 0 && <span className="lane__errors"> ({a.toolErrors} con error)</span>}
                  </dd>
                </div>
                <div>
                  <dt className="visually-hidden">Reintentos</dt>
                  <dd className={a.retries > 0 ? 'text-warning' : undefined}>{a.retries}</dd>
                </div>
                <div>
                  <dt className="visually-hidden">Tokens</dt>
                  <dd>{formatNumber(a.tokens)}</dd>
                </div>
                <div className="lane__health">
                  <dt className="visually-hidden">Semáforo</dt>
                  <dd>{a.health ? <HealthBadge health={a.health} /> : <span className="muted">—</span>}</dd>
                </div>
              </dl>
              {(a.truncated || a.error) && (
                <p className={`lane__note${a.error ? ' lane__note--error' : ''}`}>
                  {a.error ?? 'Informe cortado: la respuesta agotó MaxOutputTokens.'}
                </p>
              )}
              {regenerable.has(a.report) && (
                <div className="lane__actions">
                  <button
                    type="button"
                    className="btn btn--small"
                    disabled={busy}
                    aria-label={`Regenerar ${title}`}
                    onClick={() => onRegenerate?.([a.report], synthesis)}
                  >
                    {regenerating?.includes(a.report) ? 'Regenerando…' : 'Regenerar'}
                  </button>
                  {regenerateError?.reports.includes(a.report) && (
                    <span className="lane__note lane__note--error" role="alert">
                      {regenerateError.message}
                    </span>
                  )}
                </div>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}
