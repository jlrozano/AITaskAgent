import { useState } from 'react';
import type { ChatApi } from '../hooks/useChatTurn';
import { selectedModelId, sessionTitle } from '../state/chatReducer';
import { useWorkspace } from '../state/workspace';
import { ChatComposer } from './ChatComposer';
import { ChatModelSelect } from './ChatModelSelect';
import { ChatReadRoots } from './ChatReadRoots';
import { ChatSessionsDialog } from './ChatSessionsDialog';
import { ChatThread } from './ChatThread';
import { StatusBanner } from './StatusBanner';

interface Props {
  chat: ChatApi;
  analysisRunning: boolean;
  /** V2-B: «Abrir» de un chip de informe: lo enseña el visor de al lado. */
  onOpenReport?: (id: string) => void;
  /** V2-B: «Deshacer» o «Borrar» de un chip: la biblioteca tiene que recargarse. */
  onReportChanged?: () => void;
}

/** Panel central de Asistente. Las conversaciones se eligen en un modal desde la cabecera. */
export function ChatPane({ chat, analysisRunning, onOpenReport, onReportChanged }: Props) {
  const { workspace } = useWorkspace();
  const { state } = chat;
  const hasWorkspace = !!workspace?.repositoryPath;
  const [sessionsOpen, setSessionsOpen] = useState(false);

  if (!hasWorkspace) {
    return (
      <div className="empty">
        <h2>No hay repositorio abierto</h2>
        <p>El chat responde sobre el repositorio abierto. Ábrelo en la barra superior.</p>
      </div>
    );
  }

  // El turno en curso puede ser de otra sesión (se cambió por el hash o «atrás»): solo se pinta en la suya (P2-3).
  const live = state.live && state.live.sessionId === state.activeId ? state.live : null;
  const liveElsewhere = state.live !== null && live === null ? state.live : null;
  const disabledReason = analysisRunning ? 'analysis_running' : liveElsewhere ? 'turn_elsewhere' : null;

  return (
    <section className="chat__main" aria-label={state.session ? sessionTitle(state.session.title) : 'Nueva conversación'}>
      <header className="chat__head">
        <h2 className="chat__title">{state.session ? sessionTitle(state.session.title) : 'Nueva conversación'}</h2>
        <button
          type="button"
          className="btn btn--ghost btn--small"
          aria-haspopup="dialog"
          onClick={() => setSessionsOpen(true)}
        >
          Conversaciones
        </button>
        <button type="button" className="btn btn--ghost btn--small" onClick={() => chat.select(null)} disabled={!state.activeId}>
          Nueva
        </button>
      </header>
      <ChatReadRoots />
      <ChatSessionsDialog
        open={sessionsOpen}
        onClose={() => setSessionsOpen(false)}
        sessions={state.sessions}
        loaded={state.sessionsLoaded}
        activeId={state.activeId}
        liveId={state.live?.sessionId ?? null}
        onSelect={chat.select}
        onDelete={(id) => void chat.remove(id)}
      />
      {analysisRunning && (
        <StatusBanner tone="warning" title="Chat en pausa mientras corre el análisis">
          El análisis reescribe el grafo y los informes que usa el asistente. Podrás preguntar cuando termine.
        </StatusBanner>
      )}
      {!analysisRunning && workspace && !workspace.hasGraph && (
        <StatusBanner tone="info" title="Este repositorio no tiene grafo">
          Sin análisis previo el asistente no puede buscar en el código indexado; sí puede leer los informes que haya.
        </StatusBanner>
      )}
      {liveElsewhere && (
        <StatusBanner
          tone="info"
          title="Hay una respuesta en curso en otra conversación"
          action={
            liveElsewhere.sessionId ? (
              <button type="button" className="btn btn--small" onClick={() => chat.select(liveElsewhere.sessionId)}>
                Ir a ella
              </button>
            ) : undefined
          }
        >
          Podrás preguntar aquí cuando termine.
        </StatusBanner>
      )}
      {state.error && (
        <StatusBanner tone="error" onDismiss={chat.dismissError}>
          {state.error}
        </StatusBanner>
      )}
      <ChatThread session={state.session} live={live} models={state.models} onOpenReport={onOpenReport} onReportChanged={onReportChanged} />
      <ChatComposer
        busy={live !== null}
        disabledReason={disabledReason}
        onSend={(text) => void chat.send(text)}
        onStop={chat.stop}
        modelPicker={
          <ChatModelSelect
            models={state.models}
            value={selectedModelId(state)}
            disabled={state.live !== null}
            onChange={(id) => void chat.chooseModel(id)}
          />
        }
      />
    </section>
  );
}
