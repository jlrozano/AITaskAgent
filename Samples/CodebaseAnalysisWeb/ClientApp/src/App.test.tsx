import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ChatSession } from './api/types';
import { App } from './App';
import { completedTurn, controlledStream, jsonResponse, mockFetch, sse, sseResponse } from './test/fixtures';
import { renderWithWorkspace } from './test/renderWithWorkspace';

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

afterEach(() => vi.unstubAllGlobals());

describe('App: bloqueo cruzado chat ↔ análisis', () => {
  it('mientras el chat responde, «Lanzar análisis» está deshabilitado y lo explica; al terminar se desbloquea', async () => {
    const stream = controlledStream();
    const api = mockFetch({
      'GET /api/analysis/current': () => new Response(null, { status: 204 }),
      'GET /api/chat/sessions': () => jsonResponse([]),
      'POST /api/chat/sessions': () => jsonResponse(session, 201),
      [`GET /api/chat/sessions/${session.id}`]: () => jsonResponse(session),
      [`POST /api/chat/sessions/${session.id}/messages`]: () => sseResponse(stream.stream),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    window.location.hash = '#asistente';
    const user = userEvent.setup();
    renderWithWorkspace(<App />);

    await user.type(await screen.findByLabelText('Pregunta'), 'Hola{Enter}');
    await waitFor(() => expect(api.calls.some((c) => c.url.endsWith('/messages'))).toBe(true));
    stream.push(sse('chat', { type: 'turn.started', turnId: 't-0001', askedAt: '2026-09-27T10:15:02Z' }));
    stream.push(sse('chat', { type: 'delta', text: 'Pensando la respuesta.' }));
    expect(await screen.findByText('Pensando la respuesta.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Parar' })).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Análisis' }));
    const launch = screen.getByRole('button', { name: 'Lanzar análisis' });
    expect(launch).toBeDisabled();
    expect(screen.getByText(/El chat está respondiendo\. Podrás lanzar el análisis cuando termine/)).toBeInTheDocument();

    stream.push(sse('chat', { type: 'turn.completed', turn: { ...completedTurn, question: 'Hola' } }));
    stream.close();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Lanzar análisis' })).toBeEnabled());
    expect(screen.queryByText(/El chat está respondiendo/)).not.toBeInTheDocument();
    expect(api.calls.some((c) => c.method === 'POST' && c.url === '/api/analysis')).toBe(false);
  });
});
