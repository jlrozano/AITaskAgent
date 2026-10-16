import { useId } from 'react';
import { formatCost, formatDuration, formatNumber } from '../format';
import type { SharedPassBatchStatus, SharedPassPhase, SharedPassState } from '../state/runReducer';

export const SHARED_PASS_PHASE_LABELS: Record<SharedPassPhase, string> = {
  reading: 'Leyendo lotes',
  consolidating: 'Consolidando',
  judging: 'Juez revisando',
  saving: 'Guardando',
  done: 'Terminada',
  cut: 'Terminada con corte',
  failed: 'Fallida',
};

export const SHARED_PASS_BATCH_LABELS: Record<SharedPassBatchStatus, string> = {
  pending: 'Pendiente',
  reading: 'Leyendo',
  retrying: 'Reintentando',
  complete: 'Completo',
  partial: 'Con huecos',
  failed: 'Fallido',
  notRead: 'No leído',
};

const TERMINAL: readonly SharedPassPhase[] = ['done', 'cut', 'failed'];
const SEVERITIES = ['P0', 'P1', 'P2', 'P3'];

/** Un contador con su tope: «valor / tope» y una barra; sin tope, solo el valor. */
function Meter({ label, value, max, text }: { label: string; value: number; max: number | null; text: string }) {
  const id = useId();
  const ratio = max && max > 0 ? Math.min(1, value / max) : null;
  return (
    <div className="sharedpass__meter">
      <span className="summary__label" id={id}>
        {label}
      </span>
      <span className={`num${ratio !== null && ratio >= 0.9 ? ' text-warning' : ''}`}>{text}</span>
      {ratio !== null && (
        <div
          className="progress progress--thin"
          role="progressbar"
          aria-labelledby={id}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.round(ratio * 100)}
        >
          <span className="progress__fill" style={{ width: `${ratio * 100}%` }} />
        </div>
      )}
    </div>
  );
}

interface Props {
  pass: SharedPassState | null;
  /** La ejecución sigue en curso: el tiempo avanza con `now` mientras la pasada no termina. */
  running: boolean;
  now: number;
}

/**
 * La pasada compartida (`sharedpass.progress`): fase, lotes con su estado, coste y tiempo frente a sus topes y, al terminar, los
 * registros por severidad. Solo aparece si la ejecución emite esos eventos.
 */
export function SharedPassPanel({ pass, running, now }: Props) {
  const titleId = useId();
  if (!pass) return null;
  const { totals } = pass;
  const terminal = TERMINAL.includes(pass.phase);
  const started = pass.startedAt ? Date.parse(pass.startedAt) : NaN;
  const elapsed = !terminal && running && Number.isFinite(started) ? Math.max(totals.elapsedMs, now - started) : totals.elapsedMs;
  const maxMs = totals.maxMinutes != null ? totals.maxMinutes * 60_000 : null;

  return (
    <section className={`sharedpass sharedpass--${pass.phase}`} aria-labelledby={titleId}>
      <header className="sharedpass__head">
        <h2 id={titleId} className="panel-title">
          Pasada compartida
        </h2>
        <span className="sharedpass__phase" aria-live="polite">
          {SHARED_PASS_PHASE_LABELS[pass.phase]}
          {pass.phase === 'judging' && pass.judge && ` · ${pass.judge.tandasDone}/${pass.judge.tandas} tandas`}
        </span>
      </header>

      <div className="sharedpass__meters">
        <Meter
          label="Lotes"
          value={totals.batchesDone}
          max={totals.batchesTotal}
          text={`${formatNumber(totals.batchesDone)} / ${formatNumber(totals.batchesTotal)}`}
        />
        <Meter
          label={totals.unpricedCalls > 0 ? 'Coste (mínimo)' : 'Coste'}
          value={totals.costUsd}
          max={totals.maxCostUsd}
          text={totals.maxCostUsd != null ? `${formatCost(totals.costUsd)} / ${formatCost(totals.maxCostUsd)}` : formatCost(totals.costUsd)}
        />
        <Meter
          label="Tiempo"
          value={elapsed}
          max={maxMs}
          text={maxMs != null ? `${formatDuration(elapsed)} / ${formatDuration(maxMs)}` : formatDuration(elapsed)}
        />
        <Meter
          label="Llamadas"
          value={totals.calls}
          max={totals.maxCalls}
          text={totals.maxCalls != null ? `${formatNumber(totals.calls)} / ${formatNumber(totals.maxCalls)}` : formatNumber(totals.calls)}
        />
        <div className="sharedpass__meter">
          <span className="summary__label">Tokens</span>
          <span className="num">{formatNumber(totals.tokens)}</span>
        </div>
      </div>

      {pass.stopReason && (
        <p className="sharedpass__note sharedpass__note--warning" role="status">
          Cortada: {pass.stopReason}. Lo no leído queda como hueco de cobertura y lo no juzgado, con la rúbrica.
        </p>
      )}
      {pass.error && (
        <p className="sharedpass__note sharedpass__note--error" role="alert">
          La pasada falló: {pass.error}. Los informes siguen sin su registro.
        </p>
      )}

      {pass.severities && (
        <dl className="sharedpass__severities" aria-label="Registros por severidad">
          {SEVERITIES.map((s) => (
            <div key={s} className={`sharedpass__sev sharedpass__sev--${s.toLowerCase()}`}>
              <dt>{s}</dt>
              <dd>{formatNumber(pass.severities?.[s] ?? 0)}</dd>
            </div>
          ))}
          {pass.entries != null && (
            <div className="sharedpass__sev">
              <dt>Registros</dt>
              <dd>
                {formatNumber(pass.entries)}
                {pass.rejected ? <span className="muted"> ({formatNumber(pass.rejected)} rechazados)</span> : null}
              </dd>
            </div>
          )}
        </dl>
      )}

      {pass.batches.length > 0 && (
        <ul className="sharedpass__batches" aria-label="Lotes">
          {pass.batches.map((b) => (
            <li key={b.id} className={`sharedpass__batch sharedpass__batch--${b.status}`}>
              <span className="sharedpass__batch-dot" aria-hidden="true" />
              <span className="sharedpass__batch-id">{b.id}</span>
              <span className="sharedpass__batch-status">{SHARED_PASS_BATCH_LABELS[b.status]}</span>
              <span className="sharedpass__batch-meta">
                {formatNumber(b.files)} fich. · ~{formatNumber(b.estimatedTokens)} tok.
                {b.calls > 0 && ` · ${formatNumber(b.calls)} llam. · ${formatCost(b.costUsd)}`}
                {b.findings != null && ` · ${formatNumber(b.findings)} hallazgos`}
                {b.gaps ? ` · ${formatNumber(b.gaps)} huecos` : ''}
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
