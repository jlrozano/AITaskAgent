import { afterEach, describe, expect, it, vi } from 'vitest';
import { jsonResponse, mockFetch, runningStatus } from '../test/fixtures';
import { ApiError, api, apiErrorText, errorMessage } from './client';

afterEach(() => vi.unstubAllGlobals());

async function rejection(promise: Promise<unknown>): Promise<ApiError> {
  try {
    await promise;
  } catch (e) {
    if (e instanceof ApiError) return e;
    throw e;
  }
  throw new Error('La llamada no falló');
}

describe('errores de la API (códigos post-auditoría del backend)', () => {
  it('409 chat_turn_running en POST /api/analysis: código y texto del cliente, no el genérico del servidor', async () => {
    const f = mockFetch({
      'POST /api/analysis': () => jsonResponse({ error: 'chat_turn_running', message: 'Hay un turno de chat en curso.' }, 409),
    });
    vi.stubGlobal('fetch', vi.fn(f.fn));
    const e = await rejection(api.startAnalysis({ forceReindex: false, reports: [] }));
    expect(e.status).toBe(409);
    expect(e.code).toBe('chat_turn_running');
    expect(e.message).toMatch(/^Hay una respuesta del chat en curso/);
    expect(errorMessage(e)).toBe(e.message);
  });

  it('400 workspace_not_writable en PUT /api/workspace: explica qué hacer y conserva el detalle del servidor', async () => {
    const detail = "No se puede escribir en 'C:\\repo\\.codebase\\chat': Access to the path is denied.";
    const f = mockFetch({ 'PUT /api/workspace': () => jsonResponse({ error: 'workspace_not_writable', message: detail }, 400) });
    vi.stubGlobal('fetch', vi.fn(f.fn));
    const e = await rejection(api.openWorkspace('C:\\repo'));
    expect(e.code).toBe('workspace_not_writable');
    expect(e.message).toMatch(/^No se puede abrir este repositorio: la aplicación necesita escribir en su carpeta \.codebase/);
    expect(e.message).toContain(`(${detail})`);
  });

  it('503 report_unavailable en GET /api/reports/{f}: transitorio, invita a reintentar', async () => {
    const f = mockFetch({
      'GET /api/reports/05_errores_y_bugs.md': () =>
        jsonResponse({ error: 'report_unavailable', message: 'The process cannot access the file.' }, 503),
    });
    vi.stubGlobal('fetch', vi.fn(f.fn));
    const e = await rejection(api.getReportContent('05_errores_y_bugs.md'));
    expect(e.status).toBe(503);
    expect(e.code).toBe('report_unavailable');
    expect(e.message).toMatch(/Vuelve a intentarlo en unos segundos\. \(The process cannot access the file\.\)$/);
  });

  it.each([400, 415])('%i invalid_request: mensaje del cliente con el detalle del servidor', async (status) => {
    const f = mockFetch({
      'POST /api/analysis': () => jsonResponse({ error: 'invalid_request', message: 'El cuerpo tiene que ser JSON.' }, status),
    });
    vi.stubGlobal('fetch', vi.fn(f.fn));
    const e = await rejection(api.startAnalysis({ forceReindex: false, reports: [] }));
    expect(e.status).toBe(status);
    expect(e.code).toBe('invalid_request');
    expect(e.message).toBe(
      'El servidor no ha entendido la petición. Recarga la página; si se repite, es un fallo de la aplicación. (El cuerpo tiene que ser JSON.)',
    );
  });

  it('los códigos con texto concreto del servidor lo conservan, y analysis_running mantiene el runId', async () => {
    const f = mockFetch({
      'POST /api/analysis': () =>
        jsonResponse({ error: 'analysis_running', message: 'Hay un análisis en curso.', runId: runningStatus.runId }, 409),
    });
    vi.stubGlobal('fetch', vi.fn(f.fn));
    const e = await rejection(api.startAnalysis({ forceReindex: false, reports: [] }));
    expect(e.message).toBe('Hay un análisis en curso.');
    expect(e.body?.runId).toBe(runningStatus.runId);
  });

  it('sin mensaje del servidor cae al del cliente, y un código desconocido usa el del servidor o el estado', () => {
    expect(apiErrorText('report_unavailable', '', 503)).toMatch(/Vuelve a intentarlo en unos segundos\.$/);
    expect(apiErrorText('invalid_path', null, 400)).toBe('La ruta no es válida: tiene que ser absoluta y el directorio debe existir.');
    expect(apiErrorText('codigo_futuro', 'Texto del servidor', 400)).toBe('Texto del servidor');
    expect(apiErrorText('http_502', null, 502)).toBe('El servidor respondió 502.');
  });
});
