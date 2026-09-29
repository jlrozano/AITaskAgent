import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useAnalysisRun } from '../hooks/useAnalysisRun';
import { jsonResponse, mockFetch, runningStatus, sse, sseResponse, streamOf, successfulRun } from '../test/fixtures';
import { renderWithWorkspace } from '../test/renderWithWorkspace';
import { AnalysisTab } from './AnalysisTab';

function Harness({ chatBusy = false }: { chatBusy?: boolean }) {
  const run = useAnalysisRun();
  return <AnalysisTab run={run} onOpenReports={() => undefined} chatBusy={chatBusy} />;
}

const eventsPath = `/api/analysis/${runningStatus.runId}/events`;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('AnalysisLauncher', () => {
  it('la selección de informes y la reindexación producen el cuerpo esperado del POST', async () => {
    const api = mockFetch({
      'GET /api/analysis/current': () => new Response(null, { status: 204 }),
      'POST /api/analysis': () => jsonResponse(runningStatus, 202),
      [`GET ${eventsPath}`]: () => sseResponse(streamOf(successfulRun.map((e) => sse('analysis', e, e.seq)))),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);

    const form = screen.getByRole('form', { name: 'Lanzar análisis' });
    // Por defecto vienen marcados los defaultReports de /api/meta.
    expect(within(form).getByText('4 de 4')).toBeInTheDocument();
    await user.click(within(form).getByRole('button', { name: 'Ninguno' }));
    await user.click(within(form).getByLabelText(/Inyección de dependencias/));
    await user.click(within(form).getByLabelText(/Code smells/));
    await user.click(within(form).getByLabelText(/Forzar reindexación/));
    await user.click(within(form).getByRole('button', { name: 'Lanzar análisis' }));

    await waitFor(() => expect(api.calls.some((c) => c.method === 'POST')).toBe(true));
    const post = api.calls.find((c) => c.method === 'POST')!;
    expect(post.url).toBe('/api/analysis');
    expect(post.headers['Content-Type']).toBe('application/json');
    // En el orden del catálogo, con el nombre del enum.
    expect(post.body).toEqual({ forceReindex: true, reports: ['CodeSmells', 'InyeccionDependenciasExtensiones'] });

    // Tras el 202 se suscribe al SSE desde after=0 y reconstruye el estado.
    await waitFor(() => expect(api.calls.some((c) => c.url === `${eventsPath}?after=0`)).toBe(true));
    expect(await screen.findByText('Análisis terminado')).toBeInTheDocument();
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '100');
  });

  it('sin informes marcados no deja lanzar', async () => {
    const api = mockFetch({ 'GET /api/analysis/current': () => new Response(null, { status: 204 }) });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);
    await user.click(screen.getByRole('button', { name: 'Ninguno' }));
    expect(screen.getByRole('button', { name: 'Lanzar análisis' })).toBeDisabled();
    expect(screen.getByText('Marca al menos un informe.')).toBeInTheDocument();
  });

  it('un 409 analysis_running se comunica y engancha la ejecución que ya corre', async () => {
    let currentCalls = 0;
    const api = mockFetch({
      'GET /api/analysis/current': () => (++currentCalls === 1 ? new Response(null, { status: 204 }) : jsonResponse(runningStatus)),
      'POST /api/analysis': () =>
        jsonResponse({ error: 'analysis_running', message: 'Ya hay un análisis en curso.', runId: runningStatus.runId }, 409),
      [`GET ${eventsPath}`]: () => sseResponse(streamOf([sse('analysis', successfulRun[0], 1)])),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);
    await user.click(screen.getByRole('button', { name: 'Lanzar análisis' }));
    expect(await screen.findByText('Ya hay un análisis en curso.')).toBeInTheDocument();
    await waitFor(() => expect(api.calls.some((c) => c.url.startsWith(eventsPath))).toBe(true));
  });

  it('con un turno de chat en curso no deja lanzar, lo explica y se desbloquea al terminar', () => {
    const api = mockFetch({ 'GET /api/analysis/current': () => new Response(null, { status: 204 }) });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const { rerender } = renderWithWorkspace(<Harness chatBusy />);
    expect(screen.getByRole('button', { name: 'Lanzar análisis' })).toBeDisabled();
    expect(screen.getByText(/El chat está respondiendo\. Podrás lanzar el análisis cuando termine/)).toBeInTheDocument();
    rerender(<Harness chatBusy={false} />);
    expect(screen.getByRole('button', { name: 'Lanzar análisis' })).toBeEnabled();
    expect(screen.queryByText(/El chat está respondiendo/)).not.toBeInTheDocument();
  });

  it('un 409 chat_turn_running (turno de otra pestaña) se avisa y no engancha ninguna ejecución', async () => {
    const api = mockFetch({
      'GET /api/analysis/current': () => new Response(null, { status: 204 }),
      'POST /api/analysis': () => jsonResponse({ error: 'chat_turn_running', message: 'Hay un turno de chat en curso.' }, 409),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);
    await user.click(screen.getByRole('button', { name: 'Lanzar análisis' }));
    const banner = (await screen.findByText('El chat está respondiendo')).closest('.banner')!;
    expect(banner).toHaveClass('banner--warning');
    expect(banner).toHaveTextContent(/Hay una respuesta del chat en curso .*Espera a que termine o párala\./);
    expect(api.calls.filter((c) => c.url === '/api/analysis/current')).toHaveLength(1);
    expect(api.calls.some((c) => c.url.includes('/events'))).toBe(false);
    expect(screen.getByRole('button', { name: 'Lanzar análisis' })).toBeEnabled();
  });

  it('un 400 invalid_request se muestra como error con el detalle del servidor', async () => {
    const api = mockFetch({
      'GET /api/analysis/current': () => new Response(null, { status: 204 }),
      'POST /api/analysis': () =>
        jsonResponse({ error: 'invalid_request', message: 'El cuerpo de la petición no es JSON válido.' }, 400),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);
    await user.click(screen.getByRole('button', { name: 'Lanzar análisis' }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Petición rechazada por el servidor');
    expect(alert).toHaveTextContent('(El cuerpo de la petición no es JSON válido.)');
  });

  it('sin repositorio abierto explica por qué no se puede lanzar', () => {
    const api = mockFetch({ 'GET /api/analysis/current': () => new Response(null, { status: 204 }) });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    renderWithWorkspace(<Harness />, { workspace: null });
    expect(screen.getByText(/Abre un repositorio en la barra superior/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Lanzar análisis' })).toBeDisabled();
  });
});
