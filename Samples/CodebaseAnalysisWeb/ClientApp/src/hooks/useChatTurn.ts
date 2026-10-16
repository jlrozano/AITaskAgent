import { useCallback, useEffect, useReducer, useRef } from 'react';
import { ApiError, api, errorMessage } from '../api/client';
import { isAbortError } from '../api/sse';
import { chatReducer, initialChatState, selectedModelId, type ChatState } from '../state/chatReducer';

export interface ChatApi {
  state: ChatState;
  busy: boolean;
  loadSessions: () => Promise<void>;
  select: (id: string | null) => void;
  remove: (id: string) => Promise<void>;
  send: (question: string) => Promise<void>;
  /** Elige el modelo que responde: el de la sesión abierta o, sin sesión, el de la conversación nueva. */
  chooseModel: (modelId: string) => Promise<void>;
  stop: () => void;
  dismissError: () => void;
}

/**
 * Sesiones de chat y turno en streaming (§4.4). La sesión se crea al enviar la primera pregunta si no hay ninguna
 * abierta, para no dejar sesiones vacías. «Parar» aborta el fetch; el servidor ve RequestAborted y guarda el turno
 * como `cancelled` (§6.1.9).
 */
export function useChatTurn(options: {
  enabled: boolean;
  activeId: string | null;
  onActiveChange: (id: string | null) => void;
  /** V2-B: el turno ha escrito un informe (`report.changed`); el listado de Informes tiene que recargarse. */
  onReportChanged?: () => void;
}): ChatApi {
  const { enabled, activeId, onActiveChange } = options;
  const [state, dispatch] = useReducer(chatReducer, initialChatState);
  const abortRef = useRef<AbortController | null>(null);
  const stateRef = useRef(state);
  stateRef.current = state;
  const onActiveRef = useRef(onActiveChange);
  onActiveRef.current = onActiveChange;
  const onReportChangedRef = useRef(options.onReportChanged);
  onReportChangedRef.current = options.onReportChanged;

  const loadSessions = useCallback(async () => {
    try {
      dispatch({ type: 'sessions/loaded', sessions: await api.listChatSessions() });
    } catch (e) {
      dispatch({ type: 'error', message: errorMessage(e) });
    }
  }, []);

  useEffect(() => {
    if (enabled) void loadSessions();
  }, [enabled, loadSessions]);

  // Un servidor sin selector (404) o un fallo aquí no es un error del chat: simplemente no hay selector.
  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    api.listChatModels().then(
      (r) => {
        if (!cancelled) dispatch({ type: 'models/loaded', models: r.models, defaultId: r.defaultId });
      },
      () => {},
    );
    return () => {
      cancelled = true;
    };
  }, [enabled]);

  // La sesión abierta la manda el hash (`chat=<id>` en `#asistente`).
  useEffect(() => {
    if (!enabled) return;
    dispatch({ type: 'session/select', id: activeId });
    if (!activeId) return;
    if (stateRef.current.session?.id === activeId) return;
    let cancelled = false;
    api.getChatSession(activeId).then(
      (session) => {
        if (!cancelled) dispatch({ type: 'session/loaded', session });
      },
      (e) => {
        if (cancelled) return;
        const message =
          e instanceof ApiError && e.status === 404 ? 'La conversación ya no existe.' : errorMessage(e);
        dispatch({ type: 'error', message });
      },
    );
    return () => {
      cancelled = true;
    };
  }, [enabled, activeId]);

  const select = useCallback((id: string | null) => onActiveRef.current(id), []);

  const remove = useCallback(async (id: string) => {
    try {
      await api.deleteChatSession(id);
      dispatch({ type: 'session/deleted', id });
      if (stateRef.current.activeId === id) onActiveRef.current(null);
    } catch (e) {
      dispatch({ type: 'error', message: errorMessage(e) });
    }
  }, []);

  const send = useCallback(async (question: string) => {
    const text = question.trim();
    if (!text || stateRef.current.live) return;
    let boundSession: string | null = stateRef.current.activeId;
    dispatch({ type: 'turn/submit', question: text, sessionId: boundSession });
    const controller = new AbortController();
    abortRef.current = controller;
    try {
      let sessionId = stateRef.current.activeId;
      if (!sessionId) {
        const session = await api.createChatSession(undefined, selectedModelId(stateRef.current));
        sessionId = session.id;
        dispatch({ type: 'session/select', id: session.id });
        dispatch({ type: 'session/loaded', session });
        dispatch({ type: 'turn/submit', question: text, sessionId: session.id });
        onActiveRef.current(session.id);
      }
      boundSession = sessionId;
      if (controller.signal.aborted) throw new DOMException('abort', 'AbortError');
      let finished = false;
      let turnId: string | null = null;
      // Cada evento lleva la sesión del turno: si mientras tanto se abre otra (hash, atrás), no se pinta en ella (P2-3).
      await api.sendChatMessage(
        sessionId,
        text,
        (event) => {
          if (event.type === 'turn.started') turnId = event.turnId;
          if (event.type === 'turn.completed' || event.type === 'turn.failed') finished = true;
          dispatch({ type: 'turn/event', event, sessionId: boundSession });
          if (event.type === 'report.changed') onReportChangedRef.current?.();
        },
        controller.signal,
      );
      // Stream terminado sin turn.completed/turn.failed (servidor caído a mitad): se cierra como fallido.
      if (!finished) {
        dispatch({
          type: 'turn/event',
          sessionId: boundSession,
          event: { type: 'turn.failed', turnId, status: 'failed', error: 'El stream se cortó antes de terminar la respuesta.' },
        });
      }
    } catch (e) {
      if (isAbortError(e) || controller.signal.aborted) {
        dispatch({ type: 'turn/aborted', sessionId: boundSession });
      } else {
        dispatch({ type: 'turn/error', message: errorMessage(e) });
      }
    } finally {
      if (abortRef.current === controller) abortRef.current = null;
    }
  }, []);

  const chooseModel = useCallback(async (modelId: string) => {
    const current = stateRef.current;
    if (current.live) return;
    const sessionId = current.session?.id ?? null;
    if (!sessionId) {
      dispatch({ type: 'model/pending', modelId });
      return;
    }
    try {
      const session = await api.setChatSessionModel(sessionId, modelId);
      dispatch({ type: 'session/model', id: sessionId, modelId: session.modelId ?? modelId });
    } catch (e) {
      dispatch({ type: 'error', message: errorMessage(e) });
    }
  }, []);

  const stop = useCallback(() => {
    abortRef.current?.abort();
  }, []);

  useEffect(() => () => abortRef.current?.abort(), []);

  const dismissError = useCallback(() => dispatch({ type: 'error', message: null }), []);

  return { state, busy: state.live !== null, loadSessions, select, remove, send, chooseModel, stop, dismissError };
}
