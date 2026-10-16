import { formatDuration } from '../format';
import { mainArgument } from '../state/runReducer';

export type ToolChipStatus = 'pending' | 'running' | 'ok' | 'error' | 'unknown';

interface Props {
  name: string;
  arguments?: string | null;
  status: ToolChipStatus;
  durationMs?: number | null;
  error?: string | null;
  /** Abre el detalle de la llamada: con él, el chip es un botón. */
  onOpen?: () => void;
}

const STATUS_TEXT: Record<ToolChipStatus, string> = {
  pending: 'en cola',
  running: 'ejecutando',
  ok: 'correcta',
  error: 'con error',
  unknown: '',
};

/** Una tool call del agente: nombre, argumento principal, duración y resultado. Con `onOpen`, un botón que abre su detalle. */
export function ToolCallChip({ name, arguments: args, status, durationMs, error, onOpen }: Props) {
  const arg = mainArgument(args);
  const content = (
    <>
      <span className="tool-chip__icon" aria-hidden="true" />
      <span className="tool-chip__name">{name}</span>
      {arg && <span className="tool-chip__arg">{arg}</span>}
      {durationMs != null && <span className="tool-chip__dur">{formatDuration(durationMs)}</span>}
      {status === 'error' ? (
        <span className="tool-chip__err">error</span>
      ) : (
        STATUS_TEXT[status] && <span className="visually-hidden">, {STATUS_TEXT[status]}</span>
      )}
    </>
  );
  if (onOpen) {
    return (
      <button
        type="button"
        className={`tool-chip tool-chip--${status} tool-chip--button`}
        title={error ?? args ?? undefined}
        data-status={status}
        aria-haspopup="dialog"
        onClick={onOpen}
      >
        {content}
        <span className="visually-hidden"> (ver detalle)</span>
      </button>
    );
  }
  return (
    <span className={`tool-chip tool-chip--${status}`} title={error ?? args ?? undefined} data-status={status}>
      {content}
    </span>
  );
}
