import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ChatSession, ChatSessionSummaryDto } from '../api/types';
import { useChatTurn } from '../hooks/useChatTurn';
import { chatModels, chatStream, controlledStream, jsonResponse, mockFetch, sse, sseResponse } from '../test/fixtures';
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

describe('ChatPane: selector de modelo', () => {
  const session: ChatSession = {
    schemaVersion: 1,
    id: 's-1',
    title: 'Con modelo',
    modelId: 'gpt-6-sol',
    createdAt: '2026-09-27T10:00:00Z',
    updatedAt: '2026-09-27T10:00:00Z',
    turns: [],
  };

  function ModelHarness({ initial = null }: { initial?: string | null }) {
    const [activeId, setActiveId] = useState<string | null>(initial);
    const chat = useChatTurn({ enabled: true, activeId, onActiveChange: setActiveId });
    return <ChatPane chat={chat} analysisRunning={false} />;
  }

  const routes = (extra: Parameters<typeof mockFetch>[0] = {}) =>
    mockFetch({
      'GET /api/chat/sessions': () => jsonResponse([]),
      'GET /api/chat/models': () => jsonResponse(chatModels),
      'GET /api/chat/sessions/s-1': () => jsonResponse(session),
      ...extra,
    });

  it('lista los modelos con el de por defecto elegido en una conversación nueva', async () => {
    vi.stubGlobal('fetch', vi.fn(routes().fn));
    renderWithWorkspace(<ModelHarness />);

    const select = await screen.findByRole('combobox', { name: 'Modelo del asistente' });
    await waitFor(() => expect(within(select).getAllByRole('option')).toHaveLength(2));
    expect(select).toHaveValue('gpt-6-sol');
    expect(within(select).getByRole('option', { name: 'GPT-5.4 · sin streaming' })).toBeInTheDocument();
  });

  it('elegir un modelo antes de preguntar lo manda al crear la sesión', async () => {
    const created: ChatSession = { ...session, modelId: 'gpt-5.4', title: null };
    const stream = controlledStream();
    const api = routes({
      'POST /api/chat/sessions': () => jsonResponse(created, 201),
      'POST /api/chat/sessions/s-1/messages': () => sseResponse(stream.stream),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<ModelHarness />);

    const select = await screen.findByRole('combobox', { name: 'Modelo del asistente' });
    await waitFor(() => expect(within(select).getAllByRole('option')).toHaveLength(2));
    await user.selectOptions(select, 'gpt-5.4');
    expect(select).toHaveValue('gpt-5.4');
    await user.type(screen.getByRole('textbox', { name: 'Pregunta' }), 'hola{Enter}');

    await waitFor(() => expect(api.calls.some((c) => c.method === 'POST' && c.url === '/api/chat/sessions')).toBe(true));
    expect(api.calls.find((c) => c.method === 'POST' && c.url === '/api/chat/sessions')!.body).toMatchObject({ modelId: 'gpt-5.4' });
    stream.close();
  });

  it('cambiar de modelo en una sesión abierta lo guarda en el servidor', async () => {
    const api = routes({
      'PUT /api/chat/sessions/s-1/model': () => jsonResponse({ ...session, modelId: 'gpt-5.4' }),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<ModelHarness initial="s-1" />);

    const select = await screen.findByRole('combobox', { name: 'Modelo del asistente' });
    await waitFor(() => expect(select).toHaveValue('gpt-6-sol'));
    await user.selectOptions(select, 'gpt-5.4');

    await waitFor(() => expect(api.calls.some((c) => c.method === 'PUT')).toBe(true));
    expect(api.calls.find((c) => c.method === 'PUT')!.body).toEqual({ modelId: 'gpt-5.4' });
    await waitFor(() => expect(select).toHaveValue('gpt-5.4'));
  });

  it('una sesión con un modelo retirado muestra el de por defecto', async () => {
    vi.stubGlobal('fetch', vi.fn(routes({ 'GET /api/chat/sessions/s-1': () => jsonResponse({ ...session, modelId: 'retirado' }) }).fn));
    renderWithWorkspace(<ModelHarness initial="s-1" />);

    const select = await screen.findByRole('combobox', { name: 'Modelo del asistente' });
    await waitFor(() => expect(select).toHaveValue('gpt-6-sol'));
  });

  it('se deshabilita mientras hay un turno en curso', async () => {
    const stream = controlledStream();
    vi.stubGlobal('fetch', vi.fn(routes({ 'POST /api/chat/sessions/s-1/messages': () => sseResponse(stream.stream) }).fn));
    const user = userEvent.setup();
    renderWithWorkspace(<ModelHarness initial="s-1" />);

    const select = await screen.findByRole('combobox', { name: 'Modelo del asistente' });
    await waitFor(() => expect(select).toBeEnabled());
    await user.type(screen.getByRole('textbox', { name: 'Pregunta' }), 'hola{Enter}');
    await waitFor(() => expect(select).toBeDisabled());

    stream.push(chatStream.map((e) => sse('chat', e)).join(''));
    stream.close();
    await waitFor(() => expect(select).toBeEnabled());
  });

  it('sin endpoint de modelos (404) no hay selector ni error', async () => {
    vi.stubGlobal('fetch', vi.fn(mockFetch({ 'GET /api/chat/sessions': () => jsonResponse([]) }).fn));
    renderWithWorkspace(<ModelHarness />);

    await screen.findByRole('heading', { name: 'Nueva conversación' });
    expect(screen.queryByRole('combobox', { name: 'Modelo del asistente' })).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
