import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ChatSession, ChatTurn } from '../api/types';
import { useChatTurn } from '../hooks/useChatTurn';
import { chatModels, chatStream, controlledStream, jsonResponse, mockFetch, sse, sseResponse } from '../test/fixtures';
import { renderWithWorkspace } from '../test/renderWithWorkspace';
import { ChatPane } from './ChatPane';
import { ChatThread } from './ChatThread';

vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn(async () => ({ svg: '<svg></svg>' })) } }));

const session: ChatSession = {
  schemaVersion: 1,
  id: '20260927-101500-k3f9',
  title: null,
  repositoryPath: 'C:\\repos\\miapp',
  createdAt: '2026-09-27T10:15:00Z',
  updatedAt: '2026-09-27T10:15:00Z',
  turns: [],
};

function Harness({ analysisRunning = false }: { analysisRunning?: boolean }) {
  const [activeId, setActiveId] = useState<string | null>(null);
  const chat = useChatTurn({ enabled: true, activeId, onActiveChange: setActiveId });
  return <ChatPane chat={chat} analysisRunning={analysisRunning} />;
}

const messagesPath = `/api/chat/sessions/${session.id}/messages`;

afterEach(() => {
  vi.unstubAllGlobals();
});

function setup() {
  const stream = controlledStream();
  const api = mockFetch({
    'GET /api/chat/sessions': () => jsonResponse([]),
    'POST /api/chat/sessions': () => jsonResponse(session, 201),
    [`GET /api/chat/sessions/${session.id}`]: () => jsonResponse(session),
    [`POST ${messagesPath}`]: () => sseResponse(stream.stream),
  });
  vi.stubGlobal('fetch', vi.fn(api.fn));
  return { api, stream };
}

describe('Chat en streaming', () => {
  it('un stream falso pinta los deltas y los chips de tools, y termina con el turno persistido', async () => {
    const { api, stream } = setup();
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);

    await user.type(screen.getByLabelText('Pregunta'), '¿Qué informes hay y cuál es el peor?{Enter}');

    // Crea la sesión (no había ninguna abierta) y envía la pregunta con cuerpo JSON.
    await waitFor(() => expect(api.calls.some((c) => c.url === messagesPath)).toBe(true));
    const createCall = api.calls.find((c) => c.method === 'POST' && c.url === '/api/chat/sessions')!;
    expect(createCall.body).toEqual({});
    const post = api.calls.find((c) => c.url === messagesPath)!;
    expect(post.body).toEqual({ message: '¿Qué informes hay y cuál es el peor?' });
    expect(post.headers['Content-Type']).toBe('application/json');

    // Chunks partidos a propósito a mitad de evento.
    const text = chatStream.slice(0, 3).map((e) => sse('chat', e)).join('');
    stream.push(text.slice(0, 40));
    stream.push(text.slice(40));
    expect(await screen.findByText('Voy a mirar los informes.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Parar' })).toBeInTheDocument();

    stream.push(chatStream.slice(3, 5).map((e) => sse('chat', e)).join(''));
    expect(await screen.findByText('list_reports')).toBeInTheDocument();
    expect(screen.getByText('Razonamiento intermedio')).toBeInTheDocument();
    await waitFor(() => expect(document.querySelector('.tool-chip')).toHaveAttribute('data-status', 'running'));

    stream.push(chatStream.slice(5, 8).map((e) => sse('chat', e)).join(''));
    await waitFor(() => expect(document.querySelector('.tool-chip')).toHaveAttribute('data-status', 'ok'));
    expect(await screen.findByText('05 Errores y bugs')).toBeInTheDocument();

    stream.push(sse('chat', chatStream[8]));
    stream.close();
    await waitFor(() => expect(screen.queryByText('Razonamiento intermedio')).not.toBeInTheDocument());
    expect(screen.getByText(/1\.?834 tokens/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Enviar' })).toBeInTheDocument();
    // La sesión aparece en la lista con el título de la primera pregunta.
    expect(screen.getAllByText('¿Qué informes hay y cuál es el peor?').length).toBeGreaterThanOrEqual(2);
  });

  it('«Parar» aborta el fetch del turno y lo marca como parado', async () => {
    const { api, stream } = setup();
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);
    await user.type(screen.getByLabelText('Pregunta'), 'Pregunta larga{Enter}');
    await waitFor(() => expect(api.calls.some((c) => c.url === messagesPath)).toBe(true));
    stream.push(sse('chat', chatStream[0]) + sse('chat', chatStream[1]));
    expect(await screen.findByText('Voy a mirar')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Parar' }));

    const post = api.calls.find((c) => c.url === messagesPath)!;
    expect(post.signal?.aborted).toBe(true);
    await waitFor(() => expect(stream.cancelled).toBe(true));
    expect(await screen.findByText('Respuesta parada por el usuario.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Enviar' })).toBeInTheDocument();
  });

  it('un 409 del servidor se muestra como aviso y no deja turno a medias', async () => {
    const api = mockFetch({
      'GET /api/chat/sessions': () => jsonResponse([]),
      'POST /api/chat/sessions': () => jsonResponse(session, 201),
      [`GET /api/chat/sessions/${session.id}`]: () => jsonResponse(session),
      [`POST ${messagesPath}`]: () =>
        jsonResponse({ error: 'chat_turn_running', message: 'Ya hay una respuesta en curso en esta conversación.' }, 409),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);
    await user.type(screen.getByLabelText('Pregunta'), 'Hola{Enter}');
    // chat_turn_running usa el texto del cliente: el del servidor es genérico («Hay un turno de chat en curso.»).
    expect(await screen.findByRole('alert')).toHaveTextContent(/Hay una respuesta del chat en curso/);
    expect(screen.queryByRole('button', { name: 'Parar' })).not.toBeInTheDocument();
  });

  it('con un análisis en curso bloquea el compositor y lo explica', () => {
    setup();
    renderWithWorkspace(<Harness analysisRunning />);
    expect(screen.getByText('Chat en pausa mientras corre el análisis')).toBeInTheDocument();
    expect(screen.getByLabelText('Pregunta')).toBeDisabled();
  });

  it('Mayús+Enter hace salto de línea sin enviar', async () => {
    const { api } = setup();
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);
    const box = screen.getByLabelText('Pregunta');
    await user.type(box, 'línea 1{Shift>}{Enter}{/Shift}línea 2');
    expect(box).toHaveValue('línea 1\nlínea 2');
    expect(api.calls.some((c) => c.method === 'POST')).toBe(false);
  });
});

describe('ChatThread', () => {
  it('pinta los turnos guardados con sus tools, y los fallidos con su error', () => {
    renderWithWorkspace(
      <ChatThread
        live={null}
        session={{
          ...session,
          turns: [
            {
              id: 't-1',
              askedAt: '2026-09-27T10:15:02Z',
              question: '¿Dónde se valida?',
              answer: 'En `OrderValidator.cs:12`.',
              status: 'completed',
              error: null,
              toolCalls: [{ name: 'view_file', arguments: '{"path":"src/OrderValidator.cs"}', success: true, durationMs: 12 }],
            },
            {
              id: 't-2',
              askedAt: '2026-09-27T10:16:00Z',
              question: '¿Y la otra?',
              answer: null,
              status: 'failed',
              error: 'HTTP 500',
              finishReason: null,
            },
          ],
        }}
      />,
    );
    expect(screen.getByText('view_file')).toBeInTheDocument();
    expect(screen.getByText('src/OrderValidator.cs')).toBeInTheDocument();
    expect(screen.getByText('OrderValidator.cs:12')).toBeInTheDocument();
    expect(screen.getByText('La respuesta falló: HTTP 500')).toBeInTheDocument();
  });
});

describe('ChatThread: modelo de cada respuesta', () => {
  const turn = (extra: Partial<ChatTurn>): ChatTurn => ({
    id: 't-1',
    askedAt: '2026-09-27T10:15:02Z',
    question: 'q',
    answer: 'r',
    status: 'completed',
    error: null,
    model: 'gpt-5.4',
    tokens: 100,
    ...extra,
  });

  it('enseña la etiqueta del modelo del catálogo en cada respuesta', () => {
    renderWithWorkspace(
      <ChatThread
        live={null}
        models={chatModels.models}
        session={{ ...session, turns: [turn({ modelId: 'gpt-5.4' }), turn({ id: 't-2', modelId: 'gpt-6-sol', model: 'gpt-6-sol' })] }}
      />,
    );
    expect(screen.getByText(/^GPT-5\.4 · 100 tokens/)).toBeInTheDocument();
    expect(screen.getByText(/^GPT-6 Sol · 100 tokens/)).toBeInTheDocument();
  });

  it('un modelo retirado del catálogo, o un turno antiguo sin modelId, enseñan el deployment', () => {
    renderWithWorkspace(
      <ChatThread live={null} models={chatModels.models} session={{ ...session, turns: [turn({ modelId: 'retirado', model: 'gpt-4o' }), turn({ id: 't-2' })] }} />,
    );
    expect(screen.getByText(/^gpt-4o · 100 tokens/)).toBeInTheDocument();
    expect(screen.getByText(/^gpt-5\.4 · 100 tokens/)).toBeInTheDocument();
  });
});

describe('Chat: cambiar de sesión con un turno en curso (P2-3)', () => {
  const A: ChatSession = { ...session, id: 'sesion-a', title: 'Sesión A' };
  const B: ChatSession = { ...session, id: 'sesion-b', title: 'Sesión B' };
  const turnA = { ...chatStream[8], turn: { ...(chatStream[8] as { turn: ChatTurn }).turn, id: 't-a' } };

  // El hash manda la sesión abierta; el botón simula «atrás» o editar el hash a mano.
  function HashHarness() {
    const [activeId, setActiveId] = useState<string | null>(A.id);
    const chat = useChatTurn({ enabled: true, activeId, onActiveChange: setActiveId });
    return (
      <>
        <button type="button" onClick={() => setActiveId(B.id)}>
          hash B
        </button>
        <button type="button" onClick={() => setActiveId(A.id)}>
          hash A
        </button>
        <ChatPane chat={chat} analysisRunning={false} />
      </>
    );
  }

  it('el turno de A no se pinta en B y al volver a A se ve persistido', async () => {
    const stream = controlledStream();
    let persisted = false;
    const api = mockFetch({
      'GET /api/chat/sessions': () =>
        jsonResponse([A, B].map(({ id, title, updatedAt }) => ({ id, title, updatedAt, turnCount: 0 }))),
      [`GET /api/chat/sessions/${A.id}`]: () => jsonResponse(persisted ? { ...A, turns: [turnA.turn] } : A),
      [`GET /api/chat/sessions/${B.id}`]: () => jsonResponse(B),
      [`POST /api/chat/sessions/${A.id}/messages`]: () => sseResponse(stream.stream),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<HashHarness />);
    expect(await screen.findByRole('heading', { name: 'Sesión A' })).toBeInTheDocument();

    await user.type(screen.getByLabelText('Pregunta'), '¿Qué informes hay y cuál es el peor?{Enter}');
    await waitFor(() => expect(api.calls.some((c) => c.url === `/api/chat/sessions/${A.id}/messages`)).toBe(true));
    stream.push(chatStream.slice(0, 3).map((e) => sse('chat', e)).join(''));
    expect(await screen.findByText('Voy a mirar los informes.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'hash B' }));
    expect(await screen.findByRole('heading', { name: 'Sesión B' })).toBeInTheDocument();
    // En B no se ve el turno de A, y se avisa de que hay uno en curso en otra conversación.
    expect(screen.queryByText('Voy a mirar los informes.')).not.toBeInTheDocument();
    expect(screen.getByText('Hay una respuesta en curso en otra conversación')).toBeInTheDocument();
    expect(screen.getByLabelText('Pregunta')).toBeDisabled();

    stream.push(chatStream.slice(3, 8).map((e) => sse('chat', e)).join(''));
    persisted = true;
    stream.push(sse('chat', turnA));
    stream.close();
    await waitFor(() => expect(screen.queryByText('Hay una respuesta en curso en otra conversación')).not.toBeInTheDocument());
    expect(screen.queryByText('05 Errores y bugs')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Pregunta')).toBeEnabled();

    await user.click(screen.getByRole('button', { name: 'hash A' }));
    expect(await screen.findByRole('heading', { name: 'Sesión A' })).toBeInTheDocument();
    expect(await screen.findByText('05 Errores y bugs')).toBeInTheDocument();
    expect(screen.getAllByRole('article')).toHaveLength(1);
  });
});

describe('ChatThread: coste (D8)', () => {
  const turn = (costUsd: number | null | undefined): ChatTurn => ({
    id: `t-${String(costUsd)}`,
    askedAt: '2026-09-27T10:15:02Z',
    question: 'Pregunta',
    answer: 'Respuesta',
    status: 'completed',
    error: null,
    model: 'gpt-5.4',
    tokens: 1200,
    costUsd,
    durationMs: 2100,
  });

  it.each([0, null, undefined])('con coste %s no muestra el coste', (costUsd) => {
    renderWithWorkspace(<ChatThread live={null} session={{ ...session, turns: [turn(costUsd)] }} />);
    const meta = document.querySelector('.turn__meta')!;
    expect(meta.textContent).toContain('tokens');
    expect(meta.textContent).not.toContain('$');
  });

  it('con coste positivo lo muestra, y por debajo de una milésima no lo redondea a cero', () => {
    renderWithWorkspace(<ChatThread live={null} session={{ ...session, turns: [turn(0.041), { ...turn(0.0004), id: 't-2' }] }} />);
    const metas = [...document.querySelectorAll('.turn__meta')].map((m) => m.textContent);
    expect(metas[0]).toContain('0,041 $');
    expect(metas[1]).toContain('< 0,001 $');
  });
});

describe('ChatThread: estructura del scroll (V2-A)', () => {
  it('la región viva y los turnos quedan dentro del contenedor con scroll del hilo', () => {
    const turns: ChatTurn[] = Array.from({ length: 3 }, (_, i) => ({
      id: `t-${i}`,
      askedAt: '2026-09-27T10:15:02Z',
      question: `Pregunta ${i}`,
      answer: 'Respuesta',
      status: 'completed',
      error: null,
    }));
    renderWithWorkspace(<ChatThread live={null} session={{ ...session, turns }} />);
    const status = screen.getByRole('status');
    const thread = status.closest('.thread');
    expect(thread).not.toBeNull();
    expect(status).toHaveClass('visually-hidden');
    expect(thread!.querySelectorAll('.turn')).toHaveLength(3);
  });
});
