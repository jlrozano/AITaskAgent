import { formatDuration } from '../format';
import { mainArgument } from '../state/runReducer';

export type ToolChipStatus = 'pending' | 'running' | 'ok' | 'error' | 'unknown';

interface Props {
  name: string;
  arguments?: string | null;
  status: ToolChipStatus;
  durationMs?: number | null;
  error?: string | null;
}

const STATUS_TEXT: Record<ToolChipStatus, string> = {
  pending: 'en cola',
  running: 'ejecutando',
  ok: 'correcta',
  error: 'con error',
  unknown: '',
};

/** Una tool call del agente: nombre, argumento principal, duración y resultado. */
export function ToolCallChip({ name, arguments: args, status, durationMs, error }: Props) {
  const arg = mainArgument(args);
  return (
    <span className={`tool-chip tool-chip--${status}`} title={error ?? args ?? undefined} data-status={status}>
      <span className="tool-chip__icon" aria-hidden="true" />
      <span className="tool-chip__name">{name}</span>
      {arg && <span className="tool-chip__arg">{arg}</span>}
      {durationMs != null && <span className="tool-chip__dur">{formatDuration(durationMs)}</span>}
      {status === 'error' ? (
        <span className="tool-chip__err">error</span>
      ) : (
        STATUS_TEXT[status] && <span className="visually-hidden">, {STATUS_TEXT[status]}</span>
      )}
    </span>
  );
}
