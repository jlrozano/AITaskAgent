import { afterEach, describe, expect, it, vi } from 'vitest';
import { jsonResponse, mockFetch } from '../test/fixtures';
import { ApiError, api, apiErrorText, reportPath, reportRefFromId, reportSource } from './client';

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

describe('rutas de V2 (plan v2 §5.1)', () => {
  it('informes propios y del catálogo: ruta, versiones, restauración y borrado', async () => {
    const summary = { fileName: 'estudio.md', kind: 'custom', id: 'custom/estudio.md' };
    const f = mockFetch({
      'GET /api/reports/custom/estudio.md/versions': () => jsonResponse([]),
      'GET /api/reports/05_errores_y_bugs.md/versions/v0002': () => new Response('# v2', { status: 200 }),
      'POST /api/reports/custom/estudio.md/versions/v0001/restore': () => jsonResponse(summary),
      'DELETE /api/reports/custom/estudio.md': () => new Response(null, { status: 204 }),
    });
    vi.stubGlobal('fetch', vi.fn(f.fn));

    await api.getReportVersions(reportRefFromId('custom/estudio.md'));
    expect(await api.getReportVersionContent(reportRefFromId('05_errores_y_bugs.md'), 'v0002')).toBe('# v2');
    expect(await api.restoreReportVersion({ kind: 'custom', fileName: 'estudio.md' }, 'v0001')).toEqual(summary);
    await api.deleteCustomReport('estudio.md');

    const restore = f.calls.find((c) => c.method === 'POST')!;
    expect(restore.body).toEqual({});
    expect(restore.headers['Content-Type']).toBe('application/json');
    expect(f.calls.map((c) => c.method + ' ' + c.url)).toEqual([
      'GET /api/reports/custom/estudio.md/versions',
      'GET /api/reports/05_errores_y_bugs.md/versions/v0002',
      'POST /api/reports/custom/estudio.md/versions/v0001/restore',
      'DELETE /api/reports/custom/estudio.md',
    ]);
  });

  it('la fuente de conocimiento de un informe es report o custom con el nombre del fichero', () => {
    expect(reportSource({ kind: 'catalog', fileName: '05_errores_y_bugs.md' })).toEqual({
      kind: 'report',
      id: '05_errores_y_bugs.md',
    });
    expect(reportSource(reportRefFromId('custom/estudio.md'))).toEqual({ kind: 'custom', id: 'estudio.md' });
    expect(reportPath({ kind: 'catalog', fileName: '05 x.md' })).toBe('/api/reports/05%20x.md');
  });

  it('grafo y embeddings: POST con { source } y DELETE por tipo e id', async () => {
    const status = { source: { kind: 'document', id: 'd-1' } };
    const f = mockFetch({
      'POST /api/knowledge/graph': () => jsonResponse(status),
      'DELETE /api/knowledge/graph/custom/estudio.md': () => jsonResponse(status),
      'POST /api/knowledge/embeddings': () => jsonResponse(status),
      'DELETE /api/knowledge/embeddings/document/d-1': () => jsonResponse(status),
    });
    vi.stubGlobal('fetch', vi.fn(f.fn));

    await api.addToGraph({ kind: 'document', id: 'd-1' });
    await api.removeFromGraph({ kind: 'custom', id: 'estudio.md' });
    await api.indexEmbeddings({ kind: 'report', id: '05_errores_y_bugs.md' });
    await api.removeEmbeddings({ kind: 'document', id: 'd-1' });

    expect(f.calls[0].body).toEqual({ source: { kind: 'document', id: 'd-1' } });
    expect(f.calls[2].body).toEqual({ source: { kind: 'report', id: '05_errores_y_bugs.md' } });
    expect(f.calls.map((c) => c.method)).toEqual(['POST', 'DELETE', 'POST', 'DELETE']);
  });

  it('documentos: PATCH con JSON y la URL del original', async () => {
    const f = mockFetch({ 'PATCH /api/documents/d-1': () => jsonResponse({ id: 'd-1' }) });
    vi.stubGlobal('fetch', vi.fn(f.fn));

    await api.patchDocument('d-1', { useInAnalysis: false });

    expect(f.calls[0].body).toEqual({ useInAnalysis: false });
    expect(api.documentRawUrl('d-1')).toBe('/api/documents/d-1/raw');
  });

  it('códigos de V2: knowledge_operation_running con el texto del cliente, llm_error con el detalle del servidor', async () => {
    const f = mockFetch({
      'POST /api/knowledge/graph': () => jsonResponse({ error: 'knowledge_operation_running', message: 'x' }, 409),
      'POST /api/documents/d-1/describe': () => jsonResponse({ error: 'llm_error', message: 'timeout' }, 502),
    });
    vi.stubGlobal('fetch', vi.fn(f.fn));

    const busy = await rejection(api.addToGraph({ kind: 'document', id: 'd-1' }));
    const llm = await rejection(api.describeDocument('d-1'));

    expect(busy.message).toMatch(/^Hay una operación de grafo, embeddings o documentos en curso/);
    expect(llm.message).toBe('El modelo ha fallado al responder. Vuelve a intentarlo más tarde. (timeout)');
    expect(apiErrorText('content_mismatch', 'Declarado .pdf, detectado ejecutable', 400)).toBe(
      'Declarado .pdf, detectado ejecutable',
    );
  });
});
