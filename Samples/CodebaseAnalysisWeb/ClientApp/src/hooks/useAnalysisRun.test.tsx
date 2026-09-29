import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AnalysisEventDto, RunStatusDto } from '../api/types';
import {
  cancelledDuringReports,
  jsonResponse,
  mockFetch,
  regenerationAfter,
  RUN_ID,
  runningStatus,
  sse,
  sseResponse,
  streamOf,
} from '../test/fixtures';
import { useAnalysisRun } from './useAnalysisRun';

// Se cuentan las acciones que llegan al reductor: medir solo tiempo sería frágil con los tests en paralelo.
const reducerActions = vi.hoisted(() => ({ count: 0 }));
vi.mock('../state/runReducer', async (importActual) => {
  const actual = await importActual<typeof import('../state/runReducer')>();
  return {
    ...actual,
    runReducer: (...args: Parameters<typeof actual.runReducer>) => {
      reducerActions.count++;
      return actual.runReducer(...args);
    },
  };
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function replayChunks(count: number): string[] {
  const t0 = Date.parse('2026-09-27T10:15:00Z');
  const text: string[] = [];
  for (let seq = 1; seq <= count; seq++) {
    const last = seq === count;
    const event: AnalysisEventDto = {
      seq,
      runId: RUN_ID,
      ts: new Date(t0 + seq).toISOString(),
      type: last ? 'run.completed' : 'log',
      source: last ? null : 'DetectAndIndexGraphStep',
      report: null,
      phase: last ? null : 'detect',
      level: last ? 'success' : 'info',
      message: last ? 'Análisis terminado' : `   [indexador] línea ${seq}`,
      data: last
        ? { status: 'succeeded', exitCode: 0, durationMs: 1000, failedReports: [], executiveReportPath: null, error: null }
        : { channel: 'indexer' },
    };
    text.push(sse('analysis', event, seq));
  }
  // Trozos de ~64 KB, como los entrega la red.
  const joined = text.join('');
  const chunks: string[] = [];
  for (let i = 0; i < joined.length; i += 65536) chunks.push(joined.slice(i, i + 65536));
  return chunks;
}

describe('useAnalysisRun: replay grande (P3-12)', () => {
  it('reproduce 50 000 eventos (el tope del servidor) en tiempo razonable y avisa una vez del final', async () => {
    const count = 50_000;
    const chunks = replayChunks(count);
    const api = mockFetch({
      'GET /api/analysis/current': () => jsonResponse({ ...runningStatus, lastSeq: count }),
      [`GET /api/analysis/${RUN_ID}/events`]: () => sseResponse(streamOf(chunks)),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const onFinished = vi.fn();

    reducerActions.count = 0;
    const t0 = performance.now();
    const { result } = renderHook(() => useAnalysisRun(onFinished));
    await waitFor(() => expect(result.current.state.lastSeq).toBe(count), { timeout: 15000 });
    const elapsed = performance.now() - t0;

    expect(result.current.state.log).toHaveLength(count);
    expect(result.current.state.status).toBe('succeeded');
    expect(onFinished).toHaveBeenCalledTimes(1);
    // Una acción por trozo del stream (~150), no una por evento: con una por evento eran 50 000 acciones y 5,5 s en
    // esta máquina; en lote, 0,5 s en solitario. React puede invocar el reductor dos veces por acción.
    expect(reducerActions.count).toBeLessThan(1000);
    expect(elapsed).toBeLessThan(10000);
  }, 20000);
});

describe('useAnalysisRun: regenerar informes (R3)', () => {
  const lastSeq = cancelledDuringReports.length;
  const cancelledStatus: RunStatusDto = {
    ...runningStatus,
    status: 'cancelled',
    finishedAt: '2026-09-27T10:16:00Z',
    lastSeq,
    error: 'Análisis cancelado.',
  };
  const regeneration = regenerationAfter(cancelledDuringReports, ['CodeSmells']);

  /** Servidor falso: el SSE sirve los eventos con seq > after; los de la regeneración, solo tras el POST. */
  function server(regenerate: () => Response) {
    let regenerated = false;
    return mockFetch({
      'GET /api/analysis/current': () => jsonResponse(cancelledStatus),
      [`GET /api/analysis/${RUN_ID}/events`]: (call) => {
        const after = Number(new URL(call.url, 'http://localhost').searchParams.get('after') ?? 0);
        const events = (regenerated ? [...cancelledDuringReports, ...regeneration] : cancelledDuringReports).filter((e) => e.seq > after);
        return sseResponse(streamOf(events.map((e) => sse('analysis', e, e.seq))));
      },
      [`POST /api/analysis/${RUN_ID}/regenerate`]: () => {
        const response = regenerate();
        regenerated = response.ok;
        return response;
      },
    });
  }

  it('sigue la misma ejecución desde el último seq, sin reset: los demás carriles y el registro se conservan', async () => {
    const api = server(() => jsonResponse({ ...cancelledStatus, status: 'running', finishedAt: null, error: null }, 202));
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const onFinished = vi.fn();
    const { result } = renderHook(() => useAnalysisRun(onFinished));
    await waitFor(() => expect(result.current.state.lastSeq).toBe(lastSeq));
    expect(result.current.state.auditors.InyeccionDependenciasExtensiones.status).toBe('cancelled');

    await act(() => result.current.regenerate(['CodeSmells'], true));
    await waitFor(() => expect(result.current.state.status).toBe('succeeded'));

    const post = api.calls.find((c) => c.method === 'POST')!;
    expect(post.url).toBe(`/api/analysis/${RUN_ID}/regenerate`);
    expect(post.body).toEqual({ reports: ['CodeSmells'], synthesis: true });
    const streams = api.calls.filter((c) => c.url.startsWith(`/api/analysis/${RUN_ID}/events`));
    expect(streams.map((c) => c.url)).toEqual([
      `/api/analysis/${RUN_ID}/events?after=0`,
      `/api/analysis/${RUN_ID}/events?after=${lastSeq}`,
    ]);

    const state = result.current.state;
    expect(state.runId).toBe(RUN_ID);
    expect(state.log).toHaveLength(lastSeq + regeneration.length);
    expect(state.auditors.CodeSmells.status).toBe('succeeded');
    // Con un reset, DI habría vuelto a «En espera» y el registro habría empezado de cero.
    expect(state.auditors.InyeccionDependenciasExtensiones.status).toBe('cancelled');
    expect(result.current.regenerating).toBeNull();
    expect(result.current.regenerateError).toBeNull();
    expect(onFinished).toHaveBeenCalledTimes(2);
  });

  it('un rechazo del servidor queda en regenerateError, con los informes pedidos, y no toca el estado', async () => {
    const api = server(() => jsonResponse({ error: 'chat_turn_running', message: 'El chat está respondiendo.' }, 409));
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const { result } = renderHook(() => useAnalysisRun());
    await waitFor(() => expect(result.current.state.lastSeq).toBe(lastSeq));

    await act(() => result.current.regenerate(['CodeSmells'], false));

    // El texto es el del cliente para ese código (apiErrorText), no el del servidor.
    expect(result.current.regenerateError).toEqual({ reports: ['CodeSmells'], code: 'chat_turn_running', message: expect.stringMatching(/chat/) });
    expect(result.current.regenerating).toBeNull();
    expect(result.current.state.status).toBe('cancelled');
    expect(result.current.state.lastSeq).toBe(lastSeq);
    expect(api.calls.filter((c) => c.url.startsWith(`/api/analysis/${RUN_ID}/events`))).toHaveLength(1);
  });
});
