import { useState } from 'react';
import type { ChatSessionSummaryDto } from '../api/types';
import { formatDateTime } from '../format';
import { sessionTitle } from '../state/chatReducer';

interface Props {
  sessions: ChatSessionSummaryDto[];
  loaded: boolean;
  activeId: string | null;
  /** Sesión con un turno en curso: no se puede borrar (el servidor respondería 409). */
  liveId: string | null;
  onSelect: (id: string | null) => void;
  onDelete: (id: string) => void;
}

/**
 * Lista de conversaciones. Se puede cambiar de conversación con una respuesta en curso: el turno sigue ligado a la
 * suya y se ve al volver (P2-3).
 */
export function ChatSessions({ sessions, loaded, activeId, liveId, onSelect, onDelete }: Props) {
  const [confirming, setConfirming] = useState<string | null>(null);
  return (
    <nav className="sessions" aria-label="Conversaciones">
      <div className="sessions__head">
        <h2 className="panel-title">Conversaciones</h2>
        <button type="button" className="btn btn--small" onClick={() => onSelect(null)}>
          Nueva conversación
        </button>
      </div>
      {!loaded && <p className="muted">Cargando…</p>}
      {loaded && sessions.length === 0 && <p className="muted">Aún no hay conversaciones en este repositorio.</p>}
      <ul>
        {sessions.map((s) => {
          const active = s.id === activeId;
          return (
            <li key={s.id} className={`session${active ? ' session--active' : ''}`}>
              <button
                type="button"
                className="session__open"
                aria-current={active ? 'page' : undefined}
                onClick={() => onSelect(s.id)}
              >
                <span className="session__title">{sessionTitle(s.title)}</span>
                <span className="session__meta">
                  <time dateTime={s.updatedAt}>{formatDateTime(s.updatedAt)}</time>
                  {s.turnCount != null && <span>{s.turnCount === 1 ? '1 pregunta' : `${s.turnCount} preguntas`}</span>}
                </span>
              </button>
              {confirming === s.id ? (
                <span className="session__confirm">
                  <button
                    type="button"
                    className="btn btn--danger btn--small"
                    onClick={() => {
                      setConfirming(null);
                      onDelete(s.id);
                    }}
                  >
                    Borrar
                  </button>
                  <button type="button" className="btn btn--ghost btn--small" onClick={() => setConfirming(null)}>
                    No
                  </button>
                </span>
              ) : (
                <button
                  type="button"
                  className="btn btn--ghost btn--small session__delete"
                  onClick={() => setConfirming(s.id)}
                  disabled={s.id === liveId}
                  aria-label={`Borrar «${sessionTitle(s.title)}»`}
                >
                  Borrar
                </button>
              )}
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
