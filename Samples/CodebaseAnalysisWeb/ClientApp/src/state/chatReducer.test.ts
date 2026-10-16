import { describe, expect, it } from 'vitest';
import type { ChatSession, ChatStreamEvent } from '../api/types';
import { chatModels, chatStream, completedTurn } from '../test/fixtures';
import { chatReducer, initialChatState, openSegmentText, selectedModelId, USER_STOPPED_MESSAGE, type ChatState } from './chatReducer';

const session: ChatSession = {
  schemaVersion: 1,
  id: '20260927-101500-k3f9',
  title: null,
  repositoryPath: 'C:\\repos\\miapp',
  createdAt: '2026-09-27T10:15:00Z',
  updatedAt: '2026-09-27T10:15:00Z',
  turns: [],
};

function opened(): ChatState {
  let s = chatReducer(initialChatState, { type: 'session/select', id: session.id });
  s = chatReducer(s, { type: 'session/loaded', session });
  return chatReducer(s, { type: 'turn/submit', question: completedTurn.question });
}

const apply = (state: ChatState, events: ChatStreamEvent[]) =>
  events.reduce((s, event) => chatReducer(s, { type: 'turn/event', event }), state);

describe('chatReducer', () => {
  it('acumula deltas en el segmento abierto', () => {
    const s = apply(opened(), chatStream.slice(0, 3));
    expect(s.live?.turnId).toBe('t-0001');
    expect(s.live?.status).toBe('streaming');
    expect(openSegmentText(s.live!)).toBe('Voy a mirar los informes.');
  });

  it('tool.calls cierra el segmento como razonamiento intermedio y registra las llamadas', () => {
    const s = apply(opened(), chatStream.slice(0, 4));
    expect(s.live?.segments).toEqual([{ text: 'Voy a mirar los informes.', closed: true }]);
    expect(openSegmentText(s.live!)).toBe('');
    expect(s.live?.tools).toMatchObject([{ name: 'list_reports', arguments: '{}', status: 'pending', afterSegment: 1 }]);
  });

  it('tool.started/completed actualizan la llamada y el texto posterior abre un segmento nuevo', () => {
    const s = apply(opened(), chatStream.slice(0, 8));
    expect(s.live?.tools).toMatchObject([{ name: 'list_reports', status: 'ok', durationMs: 12 }]);
    expect(s.live?.segments).toHaveLength(2);
    expect(openSegmentText(s.live!)).toBe('El peor es **05 Errores y bugs** (rojo).');
  });

  it('turn.completed sustituye el turno en curso por el turno persistido', () => {
    const s = apply(opened(), chatStream);
    expect(s.live).toBeNull();
    expect(s.session?.turns).toEqual([completedTurn]);
    // Sin título, la sesión toma los primeros 80 caracteres de la primera pregunta (§4.5).
    expect(s.session?.title).toBe(completedTurn.question);
    expect(s.sessions[0]).toMatchObject({ id: session.id, turnCount: 1 });
  });

  it('turn.failed guarda el turno con su estado y error y conserva el texto parcial', () => {
    const s = apply(opened(), [
      ...chatStream.slice(0, 3),
      { type: 'turn.failed', turnId: 't-0001', status: 'failed', error: 'HTTP 500 del proveedor' },
    ]);
    expect(s.live).toBeNull();
    expect(s.session?.turns).toHaveLength(1);
    expect(s.session?.turns[0]).toMatchObject({
      id: 't-0001',
      status: 'failed',
      error: 'HTTP 500 del proveedor',
      answer: 'Voy a mirar los informes.',
      question: completedTurn.question,
    });
  });

  it('turn.failed con status cancelled queda como cancelado', () => {
    const s = apply(opened(), [chatStream[0], { type: 'turn.failed', turnId: 't-0001', status: 'cancelled', error: null }]);
    expect(s.session?.turns[0]).toMatchObject({ status: 'cancelled', answer: null });
  });

  it('parar desde el cliente cierra el turno como cancelado por el usuario', () => {
    const s = chatReducer(apply(opened(), chatStream.slice(0, 2)), { type: 'turn/aborted' });
    expect(s.live).toBeNull();
    expect(s.session?.turns[0]).toMatchObject({ status: 'cancelled', error: USER_STOPPED_MESSAGE, answer: 'Voy a mirar ' });
  });

  it('un rechazo antes del stream (409) quita el turno en curso y deja el error', () => {
    const s = chatReducer(opened(), { type: 'turn/error', message: 'Hay un análisis en curso.' });
    expect(s.live).toBeNull();
    expect(s.error).toBe('Hay un análisis en curso.');
    expect(s.session?.turns).toHaveLength(0);
  });

  it('un único delta (rol sin streaming) se muestra igual', () => {
    const s = apply(opened(), [chatStream[0], { type: 'delta', text: 'Respuesta completa.' }]);
    expect(openSegmentText(s.live!)).toBe('Respuesta completa.');
  });

  it('borrar la sesión abierta la cierra', () => {
    const s = chatReducer(apply(opened(), chatStream), { type: 'session/deleted', id: session.id });
    expect(s.activeId).toBeNull();
    expect(s.session).toBeNull();
    expect(s.sessions).toHaveLength(0);
  });

  it('turn.failed de respaldo con turnId null (F2-9) cierra el turno con el id que ya se conocía', () => {
    const s = apply(opened(), [chatStream[0], { type: 'turn.failed', turnId: null, status: 'failed', error: 'Error interno' }]);
    expect(s.live).toBeNull();
    expect(s.session?.turns[0]).toMatchObject({ id: 't-0001', status: 'failed', error: 'Error interno' });
  });

  it('turn.failed con turnId null antes de turn.started usa un id local', () => {
    const s = apply(opened(), [{ type: 'turn.failed', turnId: null, status: 'failed', error: 'Error interno' }]);
    expect(s.session?.turns[0].id).toMatch(/^local-/);
  });

  it('ordena las sesiones por última actualización', () => {
    const s = chatReducer(initialChatState, {
      type: 'sessions/loaded',
      sessions: [
        { id: 'a', title: 'A', updatedAt: '2026-09-27T09:00:00Z' },
        { id: 'b', title: 'B', updatedAt: '2026-09-27T11:00:00Z' },
      ],
    });
    expect(s.sessions.map((x) => x.id)).toEqual(['b', 'a']);
  });
});

describe('chatReducer: el turno va ligado a su sesión (P2-3)', () => {
  const A: ChatSession = { ...session, id: 'sesion-a', title: 'A' };
  const B: ChatSession = { ...session, id: 'sesion-b', title: 'B', turns: [] };
  const turnA = { ...completedTurn, id: 't-a' };

  function turnInA(): ChatState {
    let s = chatReducer(initialChatState, { type: 'sessions/loaded', sessions: [A, B].map(({ id, title, updatedAt }) => ({ id, title, updatedAt })) });
    s = chatReducer(s, { type: 'session/select', id: A.id });
    s = chatReducer(s, { type: 'session/loaded', session: A });
    s = chatReducer(s, { type: 'turn/submit', question: turnA.question, sessionId: A.id });
    return apply2(s, A.id, chatStream.slice(0, 3));
  }
  const apply2 = (state: ChatState, sessionId: string, events: ChatStreamEvent[]) =>
    events.reduce((s, event) => chatReducer(s, { type: 'turn/event', sessionId, event }), state);
  const toB = (s: ChatState) => chatReducer(chatReducer(s, { type: 'session/select', id: B.id }), { type: 'session/loaded', session: B });

  it('cambiar a B a mitad del turno de A no lo pinta en B', () => {
    let s = toB(turnInA());
    // El turno sigue vivo, pero es de A.
    expect(s.live?.sessionId).toBe(A.id);
    s = apply2(s, A.id, [...chatStream.slice(3, 8), { type: 'turn.completed', turn: turnA }]);
    expect(s.live).toBeNull();
    expect(s.session?.id).toBe(B.id);
    expect(s.session?.turns).toEqual([]);
    // La lista refleja que A tiene un turno más.
    expect(s.sessions.find((x) => x.id === A.id)).toMatchObject({ turnCount: 1 });
  });

  it('al volver a A se ve el turno, aunque A se haya leído del servidor antes de persistirlo', () => {
    let s = apply2(toB(turnInA()), A.id, [{ type: 'turn.completed', turn: turnA }]);
    s = chatReducer(s, { type: 'session/select', id: A.id });
    s = chatReducer(s, { type: 'session/loaded', session: A }); // lectura sin el turno
    expect(s.session?.turns).toEqual([turnA]);
    expect(s.detached).toEqual([]);
  });

  it('si el servidor ya trae el turno, no se duplica', () => {
    let s = apply2(toB(turnInA()), A.id, [{ type: 'turn.completed', turn: turnA }]);
    s = chatReducer(s, { type: 'session/select', id: A.id });
    s = chatReducer(s, { type: 'session/loaded', session: { ...A, turns: [turnA] } });
    expect(s.session?.turns).toEqual([turnA]);
  });

  it('volver a A mientras el turno sigue en curso lo muestra y lo cierra en A', () => {
    let s = toB(turnInA());
    s = chatReducer(s, { type: 'session/select', id: A.id });
    s = chatReducer(s, { type: 'session/loaded', session: A });
    expect(s.live?.sessionId).toBe(A.id);
    expect(openSegmentText(s.live!)).toBe('Voy a mirar los informes.');
    s = apply2(s, A.id, [{ type: 'turn.completed', turn: turnA }]);
    expect(s.session?.turns).toEqual([turnA]);
  });

  it('un turno parado (sin turnId) en otra sesión no se pinta en la abierta', () => {
    let s = toB(chatReducer(chatReducer(initialChatState, { type: 'session/select', id: A.id }), { type: 'session/loaded', session: A }));
    s = chatReducer(s, { type: 'session/select', id: A.id });
    s = chatReducer(s, { type: 'turn/submit', question: 'x', sessionId: A.id });
    s = toB(s);
    s = chatReducer(s, { type: 'turn/aborted', sessionId: A.id });
    expect(s.live).toBeNull();
    expect(s.session?.turns).toEqual([]);
  });

  it('ignora eventos de un turno de otra sesión', () => {
    const s = turnInA();
    expect(chatReducer(s, { type: 'turn/event', sessionId: B.id, event: { type: 'delta', text: 'ajeno' } })).toBe(s);
  });
});

describe('chatReducer: modelo del asistente', () => {
  const withModels = () =>
    chatReducer(initialChatState, { type: 'models/loaded', models: chatModels.models, defaultId: chatModels.defaultId });

  it('sin catálogo no hay modelo elegido', () => {
    expect(selectedModelId(initialChatState)).toBeNull();
  });

  it('sin sesión se elige el de por defecto, o el pendiente que haya marcado el usuario', () => {
    let s = withModels();
    expect(selectedModelId(s)).toBe('gpt-6-sol');
    s = chatReducer(s, { type: 'model/pending', modelId: 'gpt-5.4' });
    expect(selectedModelId(s)).toBe('gpt-5.4');
  });

  it('una sesión abierta manda sobre el pendiente; sin campo, o con un modelo retirado, cae al de por defecto', () => {
    let s = chatReducer(withModels(), { type: 'model/pending', modelId: 'gpt-5.4' });
    s = chatReducer(s, { type: 'session/select', id: session.id });
    s = chatReducer(s, { type: 'session/loaded', session: { ...session, modelId: 'gpt-6-sol' } });
    expect(selectedModelId(s)).toBe('gpt-6-sol');

    expect(selectedModelId({ ...s, session: { ...session } })).toBe('gpt-6-sol');
    expect(selectedModelId({ ...s, session: { ...session, modelId: 'retirado' } })).toBe('gpt-6-sol');
  });

  it('session/model cambia el modelo de la sesión abierta sin tocar sus turnos, y se ignora para otra sesión', () => {
    let s = chatReducer(withModels(), { type: 'session/select', id: session.id });
    s = chatReducer(s, { type: 'session/loaded', session: { ...session, turns: [completedTurn] } });

    const changed = chatReducer(s, { type: 'session/model', id: session.id, modelId: 'gpt-5.4' });
    expect(selectedModelId(changed)).toBe('gpt-5.4');
    expect(changed.session?.turns).toEqual([completedTurn]);
    expect(chatReducer(s, { type: 'session/model', id: 'otra', modelId: 'gpt-5.4' })).toBe(s);
  });
});
