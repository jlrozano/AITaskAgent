import { useState } from 'react';
import { ApiError, api, errorMessage } from '../api/client';
import type { ChatTurnArtifact, ReportChange } from '../api/types';

interface Props {
  /** Del stream (`ReportChange`) o del turno persistido, que puede traer `undoneByVersionId`. */
  change: ReportChange & Pick<ChatTurnArtifact, 'undoneByVersionId' | 'undoneAction'>;
  /** Enseña el informe en el visor de Asistente (`file=<id>` en el hash). */
  onOpen?: (id: string) => void;
  /** Tras deshacer o borrar: el listado de Informes tiene que recargarse. */
  onChanged?: () => void;
  /** Mientras el turno sigue en curso, restaurar daría 409 `chat_turn_running`: se ofrece al terminar. */
  disabledReason?: string | null;
}

type Outcome = { kind: 'undone' | 'deleted' } | { kind: 'error'; message: string; conflict: boolean } | null;

/**
 * Un informe que ha escrito el chat (V2-B, plan v2 §6.5): «Abrir» y «Deshacer» (restaurar `previousVersionId`) o, si
 * el turno lo creó, «Borrar». El resultado de pulsar vive en el chip; al recargar la sesión lo dice el servidor con
 * `undoneByVersionId` y `undoneAction`, y entonces el chip muestra «Deshecho» o «Borrado» con esa versión en lugar de
 * la acción. Un informe borrado no ofrece «Abrir».
 */
export function ReportChangeChip({ change, onOpen, onChanged, disabledReason = null }: Props) {
  const [busy, setBusy] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>(null);
  const created = change.action === 'created';
  const canUndo = change.previousVersionId !== null;
  const canDelete = !canUndo && change.kind === 'custom';
  const ref = { kind: change.kind, fileName: change.fileName };
  // Deshecho antes de recargar: restaurar deja la versión `restored` y borrar, la `deleted` (decisión B-11). Sin
  // `undoneAction` (turnos anteriores a ese campo) se deduce de la acción que ofrecía el chip.
  const undoneAction = change.undoneAction ?? (canUndo ? 'restored' : 'deleted');
  const undoneBefore = change.undoneByVersionId ? (undoneAction === 'deleted' ? 'deleted' : 'undone') : null;
  const doneKind = outcome?.kind === 'undone' || outcome?.kind === 'deleted' ? outcome.kind : undoneBefore;
  const done = doneKind !== null;
  const doneVersion = outcome === null && change.undoneByVersionId ? ` · ${change.undoneByVersionId}` : '';

  const run = async (action: () => Promise<unknown>, kind: 'undone' | 'deleted') => {
    setBusy(true);
    try {
      await action();
      setOutcome({ kind });
      onChanged?.();
    } catch (e) {
      setOutcome({ kind: 'error', message: errorMessage(e), conflict: e instanceof ApiError && e.status === 409 });
    } finally {
      setBusy(false);
    }
  };

  const undo = () => void run(() => api.restoreReportVersion(ref, change.previousVersionId as string), 'undone');
  const remove = () => {
    setConfirmDelete(false);
    void run(() => api.deleteCustomReport(change.fileName), 'deleted');
  };

  const notes = [
    change.graphRefreshed ? 'actualizado en el grafo' : null,
    change.embeddingsOutdated ? 'embeddings desactualizados' : null,
  ].filter(Boolean);

  return (
    <div className={`report-chip${done ? ' report-chip--done' : ''}`} data-action={change.action}>
      <span className="report-chip__icon" aria-hidden="true" />
      <span className="report-chip__text">
        <span className="report-chip__label">{created ? 'Informe guardado' : 'Informe modificado'}:</span>{' '}
        <strong className="report-chip__title">{change.title || change.fileName}</strong>
        <span className="report-chip__meta">
          {change.kind === 'custom' ? `custom/${change.fileName}` : change.fileName} · {change.versionId}
          {notes.length > 0 && ` · ${notes.join(' · ')}`}
        </span>
      </span>
      <span className="report-chip__actions">
        {doneKind === 'undone' && <span className="report-chip__done">Deshecho{doneVersion}</span>}
        {doneKind === 'deleted' && <span className="report-chip__done">Borrado{doneVersion}</span>}
        {doneKind !== 'deleted' && onOpen && (
          <button type="button" className="btn btn--small" onClick={() => onOpen(change.id)}>
            Abrir
          </button>
        )}
        {!done && canUndo && (
          <button
            type="button"
            className="btn btn--small"
            onClick={undo}
            disabled={busy || !!disabledReason}
            title={disabledReason ?? `Restaura la versión ${change.previousVersionId}`}
          >
            {busy ? 'Deshaciendo…' : 'Deshacer'}
          </button>
        )}
        {!done && canDelete && !confirmDelete && (
          <button
            type="button"
            className="btn btn--small btn--danger"
            onClick={() => setConfirmDelete(true)}
            disabled={busy || !!disabledReason}
            title={disabledReason ?? 'Borra el informe propio que ha creado el chat'}
          >
            {busy ? 'Borrando…' : 'Borrar'}
          </button>
        )}
        {!done && canDelete && confirmDelete && (
          <>
            <button type="button" className="btn btn--small btn--danger" onClick={remove}>
              Sí, borrar
            </button>
            <button type="button" className="btn btn--small btn--ghost" onClick={() => setConfirmDelete(false)}>
              No
            </button>
          </>
        )}
      </span>
      {outcome?.kind === 'error' && (
        <p className={`report-chip__error${outcome.conflict ? ' report-chip__error--warning' : ''}`} role={outcome.conflict ? 'status' : 'alert'}>
          {outcome.message}
        </p>
      )}
    </div>
  );
}
