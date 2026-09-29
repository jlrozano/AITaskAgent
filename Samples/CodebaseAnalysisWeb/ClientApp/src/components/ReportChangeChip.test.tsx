import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ChatSession, ChatStreamEvent } from '../api/types';
import { chatReducer, initialChatState } from '../state/chatReducer';
import { completedTurn, jsonResponse, mockFetch, reportCreated, reportUpdated } from '../test/fixtures';
import { renderWithWorkspace } from '../test/renderWithWorkspace';
import { ChatThread } from './ChatThread';
import { ReportChangeChip } from './ReportChangeChip';

vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn(async () => ({ svg: '<svg></svg>' })) } }));

const session: ChatSession = {
  schemaVersion: 1,
  id: '20260927-101500-k3f9',
  title: 'Colas',
  createdAt: '2026-09-27T10:15:00Z',
  updatedAt: '2026-09-27T10:15:00Z',
  turns: [],
};

afterEach(() => vi.unstubAllGlobals());

describe('ReportChangeChip (V2-B)', () => {
  it('«Deshacer» restaura previousVersionId, avisa para recargar y deja el chip como deshecho', async () => {
    const api = mockFetch({
      'POST /api/reports/05_errores_y_bugs.md/versions/v0001/restore': () => jsonResponse({}),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const onChanged = vi.fn();
    const onOpen = vi.fn();
    const user = userEvent.setup();
    render(<ReportChangeChip change={reportUpdated} onChanged={onChanged} onOpen={onOpen} />);

    expect(screen.getByText('Informe modificado:')).toBeInTheDocument();
    expect(screen.getByText(/embeddings desactualizados/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Abrir' }));
    expect(onOpen).toHaveBeenCalledWith('05_errores_y_bugs.md');

    await user.click(screen.getByRole('button', { name: 'Deshacer' }));
    expect(await screen.findByText('Deshecho')).toBeInTheDocument();
    expect(api.calls[0].body).toEqual({});
    expect(onChanged).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Deshacer' })).not.toBeInTheDocument();
  });

  it('un informe creado ofrece «Borrar» (con confirmación) en lugar de «Deshacer»', async () => {
    const api = mockFetch({ 'DELETE /api/reports/custom/estudio-de-colas.md': () => new Response(null, { status: 204 }) });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    render(<ReportChangeChip change={reportCreated} onOpen={() => undefined} />);
    expect(screen.getByText('Informe guardado:')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Deshacer' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Borrar' }));
    expect(api.calls).toHaveLength(0);
    await user.click(screen.getByRole('button', { name: 'Sí, borrar' }));
    expect(await screen.findByText('Borrado')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Abrir' })).not.toBeInTheDocument();
  });

  it('un artefacto persistido con undoneByVersionId dice «Deshecho» o «Borrado» con la versión y no ofrece la acción', () => {
    const { unmount } = render(
      <ReportChangeChip change={{ ...reportUpdated, undoneByVersionId: 'v0003' }} onOpen={() => undefined} />,
    );
    expect(screen.getByText('Deshecho · v0003')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Deshacer' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Abrir' })).toBeInTheDocument();
    expect(screen.getByText('Informe modificado:').closest('.report-chip')).toHaveClass('report-chip--done');
    unmount();

    render(<ReportChangeChip change={{ ...reportCreated, undoneByVersionId: 'v0002' }} onOpen={() => undefined} />);
    expect(screen.getByText('Borrado · v0002')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Borrar' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Abrir' })).not.toBeInTheDocument();
  });

  it('un cambio de un propio borrado después dice «Borrado» y no ofrece «Abrir» aunque tuviera «Deshacer»', () => {
    const edited = { ...reportCreated, action: 'updated' as const, versionId: 'v0002', previousVersionId: 'v0001' };
    const { unmount } = render(
      <ReportChangeChip change={{ ...edited, undoneByVersionId: 'v0003', undoneAction: 'deleted' }} onOpen={() => undefined} />,
    );
    expect(screen.getByText('Borrado · v0003')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Abrir' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Deshacer' })).not.toBeInTheDocument();
    unmount();

    render(<ReportChangeChip change={{ ...edited, undoneByVersionId: 'v0003', undoneAction: 'restored' }} onOpen={() => undefined} />);
    expect(screen.getByText('Deshecho · v0003')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Abrir' })).toBeInTheDocument();
  });

  it('un 409 al deshacer se explica como aviso y el botón sigue disponible', async () => {
    const api = mockFetch({
      'POST /api/reports/05_errores_y_bugs.md/versions/v0001/restore': () =>
        jsonResponse({ error: 'analysis_running', message: 'Hay un análisis en curso: espera a que termine.' }, 409),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    render(<ReportChangeChip change={reportUpdated} />);
    await user.click(screen.getByRole('button', { name: 'Deshacer' }));
    const notice = await screen.findByText('Hay un análisis en curso: espera a que termine.');
    expect(notice).toHaveClass('report-chip__error--warning');
    expect(screen.getByRole('button', { name: 'Deshacer' })).toBeEnabled();
  });
});

describe('chips de informe en el hilo (V2-B)', () => {
  const opened = () => {
    let s = chatReducer(initialChatState, { type: 'session/select', id: session.id });
    s = chatReducer(s, { type: 'session/loaded', session });
    return chatReducer(s, { type: 'turn/submit', question: 'Guarda un estudio de colas' });
  };
  const apply = (events: ChatStreamEvent[]) => events.reduce((s, event) => chatReducer(s, { type: 'turn/event', event }), opened());

  it('report.changed se pinta en el turno en curso, con «Deshacer» bloqueado hasta que termine', () => {
    const state = apply([
      { type: 'turn.started', turnId: 't-0001', askedAt: '2026-09-27T10:15:02Z' },
      { type: 'tool.calls', calls: [{ name: 'update_report', arguments: '{"file_name":"05_errores_y_bugs.md"}' }] },
      { type: 'tool.completed', name: 'update_report', success: true },
      { type: 'report.changed', ...reportUpdated },
    ]);
    expect(state.live?.reports).toEqual([reportUpdated]);
    renderWithWorkspace(<ChatThread session={state.session} live={state.live} />);
    const undo = screen.getByRole('button', { name: 'Deshacer' });
    expect(undo).toBeDisabled();
    expect(undo).toHaveAttribute('title', 'Podrás deshacerlo cuando termine la respuesta.');
  });

  it('al completar, el turno lleva los artefactos (del servidor o, si no trae, los del stream) y el chip sigue', () => {
    const state = apply([
      { type: 'turn.started', turnId: 't-0001', askedAt: '2026-09-27T10:15:02Z' },
      { type: 'report.changed', ...reportCreated },
      { type: 'turn.completed', turn: { ...completedTurn, artifacts: null } },
    ]);
    expect(state.live).toBeNull();
    expect(state.session?.turns[0].artifacts).toEqual([{ type: 'report', ...reportCreated }]);

    const onOpenReport = vi.fn();
    renderWithWorkspace(<ChatThread session={state.session} live={null} onOpenReport={onOpenReport} />);
    expect(screen.getByText('Estudio de colas')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Borrar' })).toBeEnabled();
  });

  it('una sesión recargada con artifacts pinta sus chips; un turno fallido conserva lo escrito', () => {
    const loaded = chatReducer(chatReducer(initialChatState, { type: 'session/select', id: session.id }), {
      type: 'session/loaded',
      session: { ...session, turns: [{ ...completedTurn, artifacts: [{ type: 'report', ...reportUpdated }] }] },
    });
    const { unmount } = renderWithWorkspace(<ChatThread session={loaded.session} live={null} />);
    expect(screen.getByRole('button', { name: 'Deshacer' })).toBeEnabled();
    unmount();

    const failed = apply([
      { type: 'turn.started', turnId: 't-0002', askedAt: '2026-09-27T10:16:00Z' },
      { type: 'report.changed', ...reportUpdated },
      { type: 'turn.failed', turnId: 't-0002', status: 'failed', error: 'boom' },
    ]);
    expect(failed.session?.turns[0].artifacts).toEqual([{ type: 'report', ...reportUpdated }]);
  });
});
