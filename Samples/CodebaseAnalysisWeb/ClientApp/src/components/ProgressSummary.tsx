import type { CSSProperties } from 'react';
import type { RunStatus } from '../api/types';
import type { SseConnectionStatus } from '../api/sse';
import { formatCost, formatDuration, formatNumber, formatTime } from '../format';
import { progressOf, type RunState } from '../state/runReducer';

export const RUN_STATUS_LABELS: Record<RunStatus, string> = {
  running: 'En curso',
  succeeded: 'Terminado',
  completed_with_failures: 'Terminado con fallos',
  failed: 'Fallido',
  cancelled: 'Cancelado',
};

const CONNECTION_LABELS: Record<SseConnectionStatus | 'idle', string> = {
  idle: '',
  connecting: 'Conectando…',
  open: 'En directo',
  reconnecting: 'Reconectando…',
  closed: '',
  failed: 'Sin conexión con la ejecución',
};

interface Props {
  state: RunState;
  connection: SseConnectionStatus | 'idle';
  now: number;
}

export function ProgressSummary({ state, connection, now }: Props) {
  const { done, total, ratio } = progressOf(state);
  const started = state.startedAt ? Date.parse(state.startedAt) : NaN;
  const finished = state.finishedAt ? Date.parse(state.finishedAt) : NaN;
  const elapsed = state.durationMs ?? (Number.isFinite(started) ? (Number.isFinite(finished) ? finished : now) - started : null);
  const percent = Math.round(ratio * 100);
  const status = state.status;
  // Consumo LLM: tokens siempre que haya llamadas; el coste solo si alguna tiene precio (sin precios no es 0, es desconocido).
  const usage = state.usage;
  const usageDetail = usage
    ? `${formatNumber(usage.promptTokens)} de entrada · ${formatNumber(usage.completionTokens)} de salida · ${formatNumber(usage.calls)} ${usage.calls === 1 ? 'llamada' : 'llamadas'}`
    : '';

  return (
    <section
      className={`summary summary--${status ?? 'none'}`}
      aria-label="Resumen de la ejecución"
      style={{ '--summary-cells': 4 + (usage ? 1 : 0) + (usage?.costUsd != null ? 1 : 0) } as CSSProperties}
    >
      <div className="summary__status">
        <span className="summary__label">Estado</span>
        <strong aria-live="polite">{status ? RUN_STATUS_LABELS[status] : '—'}</strong>
        {CONNECTION_LABELS[connection] && (
          <span className={`live-dot live-dot--${connection}`}>{CONNECTION_LABELS[connection]}</span>
        )}
      </div>
      <div className="summary__cell">
        <span className="summary__label">Inicio</span>
        <span className="num">{formatTime(state.startedAt)}</span>
      </div>
      <div className="summary__cell">
        <span className="summary__label">Transcurrido</span>
        <span className="num">{formatDuration(elapsed)}</span>
      </div>
      <div className="summary__cell">
        <span className="summary__label">Avisos</span>
        <span className={`num${state.warnings > 0 ? ' text-warning' : ''}`}>{state.warnings}</span>
      </div>
      <div className="summary__cell">
        <span className="summary__label">Errores</span>
        <span className={`num${state.errors > 0 ? ' text-error' : ''}`}>{state.errors}</span>
      </div>
      {usage && (
        <div className="summary__cell">
          <span className="summary__label">Tokens</span>
          <span className="num">{formatNumber(usage.totalTokens)}</span>
          <span className="summary__detail">{usageDetail}</span>
        </div>
      )}
      {usage && usage.costUsd !== null && (
        <div className="summary__cell">
          <span className="summary__label">Coste</span>
          <span className="num">
            {formatCost(usage.costUsd)}
            {usage.unpricedCalls > 0 ? ' (mínimo)' : ''}
          </span>
        </div>
      )}
      <div className="summary__progress">
        <span className="summary__label" id="progress-label">
          Progreso · {done}/{total}
        </span>
        <div
          className="progress"
          role="progressbar"
          aria-labelledby="progress-label"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={percent}
        >
          <span className="progress__fill" style={{ width: `${percent}%` }} />
        </div>
      </div>
    </section>
  );
}
