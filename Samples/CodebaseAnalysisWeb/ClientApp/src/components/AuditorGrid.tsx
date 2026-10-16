import { useState } from 'react';
import type { MetaDto } from '../api/types';
import { formatDuration, formatNumber, formatTime } from '../format';
import {
  canRegenerate,
  canUndoVerification,
  canVerify,
  type AuditorState,
  type AuditorStatus,
  type RunState,
  type ToolCallRecord,
  type VerificationState,
} from '../state/runReducer';
import { HealthBadge } from './HealthBadge';
import { ToolCallDetail, type ToolCallView } from './ToolCallDetail';

/** El detalle de una llamada de un carril; sin fin, sigue en curso (o la ejecución se cortó antes de que terminara). */
export function toolCallView(c: ToolCallRecord): ToolCallView {
  return {
    name: c.tool,
    step: c.source,
    startedAt: c.startedAt,
    durationMs: c.durationMs,
    status: c.finishedAt === null ? 'running' : c.success === false ? 'error' : 'ok',
    error: c.error,
    arguments: c.arguments,
    argumentsTruncated: c.argumentsTruncated,
    output: c.output,
    outputTruncated: c.outputTruncated,
    outputBytes: c.outputBytes,
    outputDropped: c.outputDropped,
  };
}

export const AUDITOR_STATUS_LABELS: Record<AuditorStatus, string> = {
  pending: 'En espera',
  running: 'Trabajando',
  succeeded: 'Terminado',
  failed: 'Con error',
  cancelled: 'Cancelado',
  skipped: 'No ejecutado',
};

const ms = (iso: string | null) => (iso ? Date.parse(iso) : NaN);

/** Texto del resultado de la verificación de un carril; null si no se ha verificado. */
function verificationNote(v: VerificationState): string | null {
  switch (v.status) {
    case 'running':
      return v.activeTool ? `Verificando… · ${v.activeTool}` : 'Verificando…';
    case 'verified':
      return 'Verificado: sin correcciones.';
    case 'corrected':
      return `Verificado: ${v.changes} corrección(es). El apartado «Cambios de la verificación» del informe las lista.`;
    case 'warnings':
      return v.changes > 0
        ? `Verificado con ${v.changes} corrección(es), pero quedan avisos de validación en el informe.`
        : 'Verificado, pero quedan avisos de validación en el informe.';
    case 'failed':
      return `No se pudo verificar: ${v.message ?? 'sin detalle'}. El informe sigue como estaba.`;
    default:
      return null;
  }
}

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
  /** Pide verificar esos informes contra el código. Sin él no hay botón (mismos motivos que `onRegenerate`). */
  onVerify?: (reports: string[], synthesis: boolean) => void;
  /** Informes cuya verificación está arrancando: sus botones quedan desactivados. */
  verifying?: string[] | null;
  /** Error del servidor al pedir la verificación o al deshacerla, en el carril de ese informe. */
  verifyError?: { reports: string[]; message: string } | null;
  /** Restaura la versión anterior a la última verificación del informe («Deshacer verificación»). */
  onUndoVerification?: (report: string) => void;
}

/**
 * Un carril por auditor: los auditores corren en paralelo y cada marca del carril es una tool call en su instante.
 * La barra va de su arranque a su final sobre una escala de tiempo común. Con la ejecución terminada, los carriles
 * fallidos o cancelados ofrecen «Regenerar», que vuelve a generar solo ese informe.
 */
export function AuditorGrid({
  state,
  meta,
  now,
  onRegenerate,
  regenerating = null,
  regenerateError = null,
  onVerify,
  verifying = null,
  verifyError = null,
  onUndoVerification,
}: Props) {
  const [synthesis, setSynthesis] = useState(true);
  // Al verificar, el resumen ejecutivo no se rehace por defecto (D2): la verificación toca pocas líneas.
  const [verifySynthesis, setVerifySynthesis] = useState(false);
  // La llamada abierta en el detalle: se lee del estado a cada render, así que la salida aparece en cuanto llega su `tool.completed`.
  const [openCall, setOpenCall] = useState<{ report: string; index: number } | null>(null);
  const auditors = state.auditorOrder.map((r) => state.auditors[r]);
  if (auditors.length === 0) return null;
  const regenerable = new Set(onRegenerate ? auditors.filter((a) => canRegenerate(state, a.report)).map((a) => a.report) : []);
  const verifiable = new Set(onVerify ? auditors.filter((a) => canVerify(state, a.report)).map((a) => a.report) : []);
  const busy = regenerating !== null || verifying !== null;
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
      {(regenerable.size > 0 || verifiable.size > 0) && (
        <div className="lanes__regenerate">
          {regenerable.size > 0 && (
            <label className="check check--inline">
              <input type="checkbox" checked={synthesis} disabled={busy} onChange={(e) => setSynthesis(e.target.checked)} />
              <span className="check__label">Actualizar también el resumen ejecutivo</span>
            </label>
          )}
          {verifiable.size > 0 && (
            <label className="check check--inline">
              <input type="checkbox" checked={verifySynthesis} disabled={busy} onChange={(e) => setVerifySynthesis(e.target.checked)} />
              <span className="check__label">Regenerar el resumen ejecutivo tras verificar</span>
            </label>
          )}
        </div>
      )}
      {state.synthesisStale && state.status !== 'running' && (
        <p className="lanes__stale" role="status">
          El resumen ejecutivo puede estar desfasado: se corrigieron informes al verificarlos y no se ha vuelto a generar.
        </p>
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
                {a.toolTimes.map((t, i) => {
                  const call = a.calls[i];
                  if (!call) return <span key={i} className="lane__tick" style={{ left: `${pos(Date.parse(t))}%` }} />;
                  // Fuera del orden de tabulación (puede haber cientos): el teclado entra por el número de tools.
                  return (
                    <button
                      key={call.key}
                      type="button"
                      tabIndex={-1}
                      className={`lane__tick lane__tick--button${call.success === false ? ' lane__tick--error' : ''}`}
                      style={{ left: `${pos(Date.parse(t))}%` }}
                      title={`${call.tool} · ${formatTime(call.startedAt)}`}
                      onClick={() => setOpenCall({ report: a.report, index: i })}
                    />
                  );
                })}
              </div>
              <dl className="lane__stats">
                <div>
                  <dt className="visually-hidden">Tiempo</dt>
                  <dd>{formatDuration(elapsed)}</dd>
                </div>
                <div>
                  <dt className="visually-hidden">Tools</dt>
                  <dd>
                    {a.calls.length > 0 ? (
                      <button
                        type="button"
                        className="lane__calls"
                        aria-haspopup="dialog"
                        aria-label={`Ver las ${a.toolCalls} llamadas a herramientas de ${title}`}
                        onClick={() => setOpenCall({ report: a.report, index: a.calls.length - 1 })}
                      >
                        {a.toolCalls}
                      </button>
                    ) : (
                      a.toolCalls
                    )}
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
              {verificationNote(a.verification) && (
                <p className={`lane__note lane__note--verify lane__note--verify-${a.verification.status}`}>{verificationNote(a.verification)}</p>
              )}
              {(regenerable.has(a.report) || verifiable.has(a.report) || canUndoVerification(state, a.report)) && (
                <div className="lane__actions">
                  {regenerable.has(a.report) && (
                    <button
                      type="button"
                      className="btn btn--small"
                      disabled={busy}
                      aria-label={`Regenerar ${title}`}
                      onClick={() => onRegenerate?.([a.report], synthesis)}
                    >
                      {regenerating?.includes(a.report) ? 'Regenerando…' : 'Regenerar'}
                    </button>
                  )}
                  {verifiable.has(a.report) && (
                    <button
                      type="button"
                      className="btn btn--small"
                      disabled={busy}
                      aria-label={`Verificar ${title}`}
                      onClick={() => onVerify?.([a.report], verifySynthesis)}
                    >
                      {verifying?.includes(a.report) ? 'Verificando…' : 'Verificar'}
                    </button>
                  )}
                  {onUndoVerification && canUndoVerification(state, a.report) && (
                    <button
                      type="button"
                      className="btn btn--small"
                      disabled={busy}
                      aria-label={`Deshacer verificación de ${title}`}
                      onClick={() => onUndoVerification(a.report)}
                    >
                      Deshacer verificación
                    </button>
                  )}
                  {regenerateError?.reports.includes(a.report) && (
                    <span className="lane__note lane__note--error" role="alert">
                      {regenerateError.message}
                    </span>
                  )}
                  {verifyError?.reports.includes(a.report) && (
                    <span className="lane__note lane__note--error" role="alert">
                      {verifyError.message}
                    </span>
                  )}
                </div>
              )}
            </li>
          );
        })}
      </ul>
      <OpenCallDetail state={state} open={openCall} onChange={setOpenCall} />
    </section>
  );
}

function OpenCallDetail({
  state,
  open,
  onChange,
}: {
  state: RunState;
  open: { report: string; index: number } | null;
  onChange: (open: { report: string; index: number } | null) => void;
}) {
  const calls = open ? (state.auditors[open.report]?.calls ?? []) : [];
  const call = open ? calls[open.index] : undefined;
  return (
    <ToolCallDetail
      call={call ? toolCallView(call) : null}
      onClose={() => onChange(null)}
      position={
        open
          ? {
              index: open.index,
              total: calls.length,
              onPrev: () => onChange({ ...open, index: Math.max(0, open.index - 1) }),
              onNext: () => onChange({ ...open, index: Math.min(calls.length - 1, open.index + 1) }),
            }
          : undefined
      }
    />
  );
}
