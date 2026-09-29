import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ChatSession, ChatSessionSummaryDto } from '../api/types';
import { useChatTurn } from '../hooks/useChatTurn';
import { jsonResponse, mockFetch } from '../test/fixtures';
import { renderWithWorkspace } from '../test/renderWithWorkspace';
import { ChatPane } from './ChatPane';

vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn(async () => ({ svg: '<svg></svg>' })) } }));

// Llegan desordenadas: el modal las enseña de la más reciente a la más antigua.
const summaries: ChatSessionSummaryDto[] = [
  { id: 's-old', title: 'Colas de mensajes', updatedAt: '2026-09-20T09:00:00Z', turnCount: 3 },
  { id: 's-new', title: 'Errores críticos', updatedAt: '2026-09-27T18:00:00Z', turnCount: 1 },
];

const sessionOf = (s: ChatSessionSummaryDto): ChatSession => ({
  schemaVersion: 1,
  id: s.id,
  title: s.title,
  repositoryPath: 'C:\\repos\\miapp',
  createdAt: s.updatedAt,
  updatedAt: s.updatedAt,
  turns: [],
});

function Harness() {
  const [activeId, setActiveId] = useState<string | null>(null);
  const chat = useChatTurn({ enabled: true, activeId, onActiveChange: setActiveId });
  return <ChatPane chat={chat} analysisRunning={false} />;
}

afterEach(() => vi.unstubAllGlobals());

describe('ChatPane: conversaciones en un modal', () => {
  it('«Conversaciones» abre el modal ordenado por fecha; elegir una la abre y cierra el modal', async () => {
    const api = mockFetch({
      'GET /api/chat/sessions': () => jsonResponse(summaries),
      ...Object.fromEntries(summaries.map((s) => [`GET /api/chat/sessions/${s.id}`, () => jsonResponse(sessionOf(s))])),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);

    expect(screen.queryByRole('navigation', { name: 'Conversaciones' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Conversaciones' }));

    const list = await screen.findByRole('navigation', { name: 'Conversaciones' });
    await waitFor(() => expect(within(list).getAllByRole('listitem')).toHaveLength(2));
    const titles = within(list).getAllByRole('listitem').map((li) => li.querySelector('.session__title')?.textContent);
    expect(titles).toEqual(['Errores críticos', 'Colas de mensajes']);

    await user.click(within(list).getByRole('button', { name: /^Colas de mensajes/ }));
    expect(screen.queryByRole('navigation', { name: 'Conversaciones' })).not.toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Colas de mensajes' })).toBeInTheDocument();
    expect(api.calls.some((c) => c.url === '/api/chat/sessions/s-old')).toBe(true);
  });

  it('«Cerrar» cierra el modal sin cambiar de conversación', async () => {
    vi.stubGlobal('fetch', vi.fn(mockFetch({ 'GET /api/chat/sessions': () => jsonResponse(summaries) }).fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);

    await user.click(screen.getByRole('button', { name: 'Conversaciones' }));
    await screen.findByRole('navigation', { name: 'Conversaciones' });
    await user.click(screen.getByRole('button', { name: 'Cerrar' }));
    expect(screen.queryByRole('navigation', { name: 'Conversaciones' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Nueva conversación' })).toBeInTheDocument();
  });
});
