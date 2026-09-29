// Estado del chat: sesiones, sesión abierta y turno en streaming (plan §4.4, §7). Función pura.

import type { ChatSession, ChatSessionSummaryDto, ChatStreamEvent, ChatTurn, ChatTurnArtifact, ReportChange } from '../api/types';

export type LiveToolStatus = 'pending' | 'running' | 'ok' | 'error';

export interface LiveTool {
  key: number;
  id: string | null;
  name: string;
  arguments: string | null;
  status: LiveToolStatus;
  durationMs: number | null;
  error: string | null;
  /** Número de segmentos de texto que había cuando apareció la llamada: se pinta detrás de ellos. */
  afterSegment: number;
}

export interface LiveSegment {
  text: string;
  /** Cerrado por `tool.calls`: es razonamiento intermedio, no la respuesta final. */
  closed: boolean;
}

export interface LiveTurn {
  /** Sesión a la que pertenece el turno (null mientras se crea la sesión). Los eventos del stream son de ella. */
  sessionId: string | null;
  question: string;
  turnId: string | null;
  askedAt: string | null;
  segments: LiveSegment[];
  thinking: string;
  tools: LiveTool[];
  /** V2-B: informes que ha escrito el turno (`report.changed`), en orden. */
  reports: ReportChange[];
  status: 'sending' | 'streaming';
}

export interface ChatState {
  sessions: ChatSessionSummaryDto[];
  sessionsLoaded: boolean;
  activeId: string | null;
  session: ChatSession | null;
  live: LiveTurn | null;
  error: string | null;
  /**
   * Turnos terminados mientras se veía otra sesión. Se funden al cargar su sesión: la lectura del servidor puede
   * haber salido antes de que el turno se persistiera (P2-3).
   */
  detached: { sessionId: string; turn: ChatTurn }[];
}

export type ChatAction =
  | { type: 'sessions/loaded'; sessions: ChatSessionSummaryDto[] }
  | { type: 'session/select'; id: string | null }
  | { type: 'session/loaded'; session: ChatSession }
  | { type: 'session/deleted'; id: string }
  /** `sessionId` omitido = la sesión abierta. */
  | { type: 'turn/submit'; question: string; sessionId?: string | null }
  /** Con `sessionId`, se ignora si el turno en curso es de otra sesión. */
  | { type: 'turn/event'; event: ChatStreamEvent; sessionId?: string | null }
  | { type: 'turn/aborted'; sessionId?: string | null }
  | { type: 'turn/error'; message: string }
  | { type: 'error'; message: string | null };

export const initialChatState: ChatState = {
  sessions: [],
  sessionsLoaded: false,
  activeId: null,
  session: null,
  live: null,
  error: null,
  detached: [],
};

export const USER_STOPPED_MESSAGE = 'Parado por el usuario.';

let toolKey = 0;
const nextKey = () => ++toolKey;

function appendText(live: LiveTurn, text: string): LiveTurn {
  const segments = [...live.segments];
  const last = segments[segments.length - 1];
  if (!last || last.closed) segments.push({ text, closed: false });
  else segments[segments.length - 1] = { ...last, text: last.text + text };
  return { ...live, segments, status: 'streaming' };
}

function patchFirstTool(
  tools: LiveTool[],
  name: string,
  from: LiveToolStatus[],
  patch: Partial<LiveTool>,
  fallback: () => LiveTool,
): LiveTool[] {
  for (const status of from) {
    const index = tools.findIndex((t) => t.name === name && t.status === status);
    if (index >= 0) return tools.map((t, i) => (i === index ? { ...t, ...patch } : t));
  }
  return [...tools, fallback()];
}

/** Texto final visible del turno en curso: el segmento abierto (si lo hay). */
export function openSegmentText(live: LiveTurn): string {
  const last = live.segments[live.segments.length - 1];
  return last && !last.closed ? last.text : '';
}

function finishLocally(state: ChatState, status: 'failed' | 'cancelled', error: string | null, turnId: string | null): ChatState {
  const live = state.live;
  if (!live) return state;
  const text = openSegmentText(live);
  const turn: ChatTurn = {
    id: turnId || live.turnId || `local-${nextKey()}`,
    askedAt: live.askedAt ?? new Date().toISOString(),
    question: live.question,
    answer: text || null,
    status,
    error,
    toolCalls: live.tools.map((t) => ({
      name: t.name,
      arguments: t.arguments,
      success: t.status === 'ok' ? true : t.status === 'error' ? false : null,
      durationMs: t.durationMs,
    })),
    // Lo escrito queda escrito aunque el turno falle: el chip con «Deshacer» tiene que seguir visible.
    artifacts: live.reports.length > 0 ? live.reports.map(toArtifact) : null,
  };
  return settle(state, turn);
}

function toArtifact(change: ReportChange): ChatTurnArtifact {
  return { type: 'report', ...change };
}

/** Artefactos del turno: los que persistió el servidor y, si no trae ninguno, los que llegaron por el stream. */
function withLiveArtifacts(turn: ChatTurn, live: LiveTurn): ChatTurn {
  if ((turn.artifacts?.length ?? 0) > 0 || live.reports.length === 0) return turn;
  return { ...turn, artifacts: live.reports.map(toArtifact) };
}

/** Informes que escribió un turno persistido (solo los de `type: 'report'`, el único que existe hoy). */
export function turnReportChanges(turn: ChatTurn): ChatTurnArtifact[] {
  return (turn.artifacts ?? []).filter((a) => a?.type === 'report');
}

/**
 * Cierra el turno en curso con `turn`: se añade a la sesión abierta solo si es la suya. Si se está viendo otra, se
 * guarda aparte para fundirlo cuando se vuelva a ella y se actualiza su resumen en la lista.
 */
function settle(state: ChatState, turn: ChatTurn): ChatState {
  const owner = state.live ? state.live.sessionId : (state.session?.id ?? null);
  if (state.session && (owner === null || owner === state.session.id)) {
    const session = appendTurn(state.session, turn);
    return { ...state, live: null, session, sessions: touchSummary(state.sessions, session) };
  }
  if (owner === null) return { ...state, live: null };
  // Un id local no existe en el servidor: fundirlo duplicaría el turno que sí guardó con su id.
  const detached = turn.id.startsWith('local-') ? state.detached : [...state.detached, { sessionId: owner, turn }];
  return { ...state, live: null, detached, sessions: touchSummaryOf(state.sessions, owner, turn) };
}

function touchSummaryOf(sessions: ChatSessionSummaryDto[], id: string, turn: ChatTurn): ChatSessionSummaryDto[] {
  const current = sessions.find((s) => s.id === id);
  if (!current) return sessions;
  const summary: ChatSessionSummaryDto = {
    ...current,
    title: current.title || turn.question.slice(0, 80),
    updatedAt: turn.askedAt > current.updatedAt ? turn.askedAt : current.updatedAt,
    turnCount: (current.turnCount ?? 0) + 1,
  };
  return [summary, ...sessions.filter((s) => s.id !== id)];
}

function appendTurn(session: ChatSession | null, turn: ChatTurn): ChatSession | null {
  if (!session) return session;
  const turns = session.turns.some((t) => t.id === turn.id)
    ? session.turns.map((t) => (t.id === turn.id ? turn : t))
    : [...session.turns, turn];
  return {
    ...session,
    title: session.title || turn.question.slice(0, 80),
    updatedAt: turn.askedAt > session.updatedAt ? turn.askedAt : session.updatedAt,
    turns,
  };
}

function applyStreamEvent(state: ChatState, event: ChatStreamEvent): ChatState {
  const live = state.live;
  switch (event.type) {
    case 'turn.started':
      if (!live) return state;
      return { ...state, live: { ...live, turnId: event.turnId, askedAt: event.askedAt, status: 'streaming' } };
    case 'delta':
      if (!live) return state;
      return { ...state, live: appendText(live, event.text) };
    case 'thinking':
      if (!live) return state;
      return { ...state, live: { ...live, thinking: live.thinking + event.text, status: 'streaming' } };
    case 'tool.calls': {
      if (!live) return state;
      const segments = live.segments.map((s) => ({ ...s, closed: true }));
      const tools = [
        ...live.tools,
        ...event.calls.map<LiveTool>((c) => ({
          key: nextKey(),
          id: c.id ?? null,
          name: c.name,
          arguments: c.arguments ?? null,
          status: 'pending',
          durationMs: null,
          error: null,
          afterSegment: segments.length,
        })),
      ];
      return { ...state, live: { ...live, segments, tools, status: 'streaming' } };
    }
    case 'tool.started': {
      if (!live) return state;
      const tools = patchFirstTool(live.tools, event.name, ['pending'], { status: 'running' }, () => ({
        key: nextKey(),
        id: null,
        name: event.name,
        arguments: null,
        status: 'running',
        durationMs: null,
        error: null,
        afterSegment: live.segments.length,
      }));
      return { ...state, live: { ...live, tools } };
    }
    case 'tool.completed': {
      if (!live) return state;
      const patch: Partial<LiveTool> = {
        status: event.success ? 'ok' : 'error',
        durationMs: event.durationMs ?? null,
        error: event.error ?? null,
      };
      const tools = patchFirstTool(live.tools, event.name, ['running', 'pending'], patch, () => ({
        key: nextKey(),
        id: null,
        name: event.name,
        arguments: null,
        durationMs: null,
        error: null,
        afterSegment: live.segments.length,
        ...patch,
        status: patch.status as LiveToolStatus,
      }));
      return { ...state, live: { ...live, tools } };
    }
    case 'report.changed': {
      if (!live) return state;
      const { type: _type, ...change } = event;
      return { ...state, live: { ...live, reports: [...live.reports, change] } };
    }
    case 'turn.completed':
      if (!live) return state;
      return settle(state, withLiveArtifacts(event.turn, live));
    case 'turn.failed':
      return finishLocally(state, event.status, event.error, event.turnId);
    default:
      return state;
  }
}

function touchSummary(sessions: ChatSessionSummaryDto[], session: ChatSession | null): ChatSessionSummaryDto[] {
  if (!session) return sessions;
  const summary: ChatSessionSummaryDto = {
    id: session.id,
    title: session.title,
    updatedAt: session.updatedAt,
    createdAt: session.createdAt,
    turnCount: session.turns.length,
  };
  const rest = sessions.filter((s) => s.id !== session.id);
  return [summary, ...rest];
}

export function chatReducer(state: ChatState, action: ChatAction): ChatState {
  switch (action.type) {
    case 'sessions/loaded':
      return { ...state, sessions: sortSessions(action.sessions), sessionsLoaded: true };
    case 'session/select':
      if (action.id === state.activeId) return state;
      // El turno en curso no se toca: sigue siendo de su sesión y sus eventos siguen llegando (P2-3).
      return {
        ...state,
        activeId: action.id,
        session: state.session && state.session.id === action.id ? state.session : null,
        error: null,
      };
    case 'session/loaded': {
      if (state.activeId !== null && action.session.id !== state.activeId) return state;
      let session = action.session;
      const pending = state.detached.filter((d) => d.sessionId === session.id);
      for (const { turn } of pending) {
        if (!session.turns.some((t) => t.id === turn.id)) session = appendTurn(session, turn) ?? session;
      }
      const detached = pending.length > 0 ? state.detached.filter((d) => d.sessionId !== session.id) : state.detached;
      const sessions = state.sessions.some((s) => s.id === session.id) ? state.sessions : touchSummary(state.sessions, session);
      return { ...state, activeId: session.id, session, sessions, detached };
    }
    case 'session/deleted':
      return {
        ...state,
        sessions: state.sessions.filter((s) => s.id !== action.id),
        ...(state.activeId === action.id ? { activeId: null, session: null } : {}),
        live: state.live?.sessionId === action.id ? null : state.live,
        detached: state.detached.filter((d) => d.sessionId !== action.id),
      };
    case 'turn/submit':
      return {
        ...state,
        error: null,
        live: {
          sessionId: action.sessionId !== undefined ? action.sessionId : state.activeId,
          question: action.question,
          turnId: null,
          askedAt: null,
          segments: [],
          thinking: '',
          tools: [],
          reports: [],
          status: 'sending',
        },
      };
    case 'turn/event':
      if (!belongsToLive(state, action.sessionId)) return state;
      return applyStreamEvent(state, action.event);
    case 'turn/aborted':
      if (!belongsToLive(state, action.sessionId)) return state;
      return finishLocally(state, 'cancelled', USER_STOPPED_MESSAGE, null);
    case 'turn/error':
      // El servidor rechazó el turno antes de abrir el stream (409, 400...): no se ha guardado nada.
      return { ...state, live: null, error: action.message };
    case 'error':
      return { ...state, error: action.message };
    default:
      return state;
  }
}

/** Una acción sin `sessionId` es del turno en curso; con él, solo si coincide con la sesión de ese turno. */
function belongsToLive(state: ChatState, sessionId: string | null | undefined): boolean {
  if (sessionId === undefined || !state.live) return true;
  return state.live.sessionId === null || state.live.sessionId === sessionId;
}

function sortSessions(sessions: ChatSessionSummaryDto[]): ChatSessionSummaryDto[] {
  return [...sessions].sort((a, b) => (a.updatedAt < b.updatedAt ? 1 : a.updatedAt > b.updatedAt ? -1 : 0));
}

export function sessionTitle(title: string | null | undefined): string {
  return title && title.trim() ? title : 'Conversación sin título';
}
