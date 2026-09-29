import { formatDuration } from '../format';
import { PHASES, PHASE_LABELS, type PhaseStatus, type RunState } from '../state/runReducer';

export const PHASE_STATUS_LABELS: Record<PhaseStatus, string> = {
  pending: 'Pendiente',
  running: 'En curso',
  succeeded: 'Correcta',
  failed: 'Con error',
  skipped: 'Omitida',
  cancelled: 'Cancelada',
};

export function PhaseStepper({ state, now }: { state: RunState; now: number }) {
  return (
    <ol className="phases" aria-label="Fases del análisis">
      {PHASES.map((phase) => {
        const p = state.phases[phase];
        const duration =
          p.durationMs ?? (p.status === 'running' && p.startedAt ? now - Date.parse(p.startedAt) : null);
        return (
          <li key={phase} className={`phase phase--${p.status}`} aria-current={p.status === 'running' ? 'step' : undefined}>
            <span className="phase__mark" aria-hidden="true" />
            <span className="phase__name">{PHASE_LABELS[phase]}</span>
            <span className="phase__meta">
              <span className="phase__status">{PHASE_STATUS_LABELS[p.status]}</span>
              {duration !== null && <span className="phase__time">{formatDuration(duration)}</span>}
            </span>
          </li>
        );
      })}
    </ol>
  );
}
