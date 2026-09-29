import type { Health } from '../api/types';

const LABELS: Record<Health, string> = { Green: 'Verde', Amber: 'Ámbar', Red: 'Rojo', Unknown: 'Sin semáforo' };

export function healthLabel(health: Health | null | undefined): string {
  return LABELS[health ?? 'Unknown'] ?? LABELS.Unknown;
}

/** Semáforo del informe (línea `analysis-meta`). El color nunca va solo: siempre acompaña el texto. */
export function HealthBadge({ health, compact = false }: { health: Health | null | undefined; compact?: boolean }) {
  const h: Health = health && health in LABELS ? health : 'Unknown';
  return (
    <span className={`health health--${h.toLowerCase()}${compact ? ' health--compact' : ''}`} title={`Semáforo: ${LABELS[h]}`}>
      <span className="health__dot" aria-hidden="true" />
      <span className={compact ? 'visually-hidden' : undefined}>{LABELS[h]}</span>
    </span>
  );
}
