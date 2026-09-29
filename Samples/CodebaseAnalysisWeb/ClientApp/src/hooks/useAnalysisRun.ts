import { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { ApiError, api, errorMessage, parseAnalysisEvent } from '../api/client';
import { connectResumableSse, type SseConnectionStatus } from '../api/sse';
import type { AnalysisEventDto, RunStatusDto, StartAnalysisRequest } from '../api/types';
import { initialRunState, runReducer, type RunState } from '../state/runReducer';

export interface AnalysisRunApi {
  state: RunState;
  connection: SseConnectionStatus | 'idle';
  /** Aviso del último intento de lanzar o cancelar (409, 400...). */
  error: { code: string; message: string } | null;
  starting: boolean;
  cancelling: boolean;
  /** Informes cuya regeneración se está pidiendo: del clic hasta que llega `run.regeneration_started`. */
  regenerating: string[] | null;
  /** Error del último intento de regenerar (409, 404...), con los informes que se pidieron. */
  regenerateError: { reports: string[]; code: string; message: string } | null;
  start: (request: StartAnalysisRequest) => Promise<void>;
  cancel: () => Promise<void>;
  /** Vuelve a generar esos informes de la ejecución terminada, sin `reset`: el SSE sigue desde el último seq. */
  regenerate: (reports: string[], synthesis: boolean) => Promise<void>;
  clearError: () => void;
}

/**
 * Ejecución de análisis actual: al montar pide `GET /api/analysis/current` y, si hay ejecución, se suscribe al SSE
 * desde `after=0`. Las reconexiones reanudan con el último seq (§7). El estado lo reconstruye `runReducer`. Una
 * regeneración continúa la misma ejecución: se vuelve a suscribir desde el último seq aplicado, sin `reset`.
 */
export function useAnalysisRun(onRunFinished?: () => void): AnalysisRunApi {
  const [state, dispatch] = useReducer(runReducer, initialRunState);
  const [connection, setConnection] = useState<SseConnectionStatus | 'idle'>('idle');
  const [error, setError] = useState<{ code: string; message: string } | null>(null);
  const [starting, setStarting] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [regenerating, setRegenerating] = useState<string[] | null>(null);
  const [regenerateError, setRegenerateError] = useState<AnalysisRunApi['regenerateError']>(null);
  const stateRef = useRef(state);
  stateRef.current = state;
  const streamRef = useRef<AbortController | null>(null);
  const finishedRef = useRef(onRunFinished);
  finishedRef.current = onRunFinished;

  /** Con `resumeAfter`, sigue la ejecución que ya tiene el estado (regeneración) desde ese seq y sin `reset`. */
  const follow = useCallback((status: RunStatusDto, resumeAfter?: number) => {
    streamRef.current?.abort();
    const controller = new AbortController();
    streamRef.current = controller;
    if (resumeAfter === undefined) dispatch({ type: 'reset', status });
    let completed = false;
    // Los eventos de un mismo trozo del stream (un replay llega en ráfagas de cientos) se aplican en una sola acción:
    // con una acción por evento, el reductor recorre el lote evento a evento igual, pero React encola miles de
    // actualizaciones (P3-12). La cola se vacía en una microtarea, antes de leer el siguiente trozo.
    let pending: AnalysisEventDto[] = [];
    const flush = () => {
      if (pending.length === 0 || controller.signal.aborted) return;
      const events = pending;
      pending = [];
      dispatch({ type: 'events', events });
      if (events.some((e) => e.type === 'run.regeneration_started')) setRegenerating(null);
      if (events.some((e) => e.type === 'run.completed')) finishedRef.current?.();
    };
    void connectResumableSse({
      signal: controller.signal,
      // Primera conexión desde 0 (o desde el seq de la regeneración); después, desde el último seq aplicado.
      url: (lastEventId) => api.analysisEventsUrl(status.runId, lastEventId !== null ? Number(lastEventId) || 0 : 0),
      initialLastEventId: resumeAfter !== undefined ? String(resumeAfter) : null,
      onMessage: (message) => {
        if (message.event !== 'analysis') return;
        const event = parseAnalysisEvent(message.data);
        if (!event) return;
        if (pending.length === 0) queueMicrotask(flush);
        pending.push(event);
        // Tras el primer run.completed puede venir una regeneración: el stream no ha terminado hasta el siguiente.
        if (event.type === 'run.completed') completed = true;
        else if (event.type === 'run.regeneration_started') completed = false;
      },
      isComplete: () => completed,
      onStatus: (s) => {
        if (controller.signal.aborted) return;
        setConnection(s);
        if (s === 'failed' || s === 'closed') setRegenerating(null);
      },
    });
  }, []);

  useEffect(() => {
    let cancelled = false;
    api.getCurrentRun().then(
      (current) => {
        if (!cancelled && current) follow(current);
      },
      () => undefined, // el WorkspaceBar ya informa si el servidor no responde
    );
    return () => {
      cancelled = true;
      streamRef.current?.abort();
    };
  }, [follow]);

  const start = useCallback(
    async (request: StartAnalysisRequest) => {
      setError(null);
      setStarting(true);
      try {
        const status = await api.startAnalysis(request);
        follow(status);
      } catch (e) {
        if (e instanceof ApiError && e.code === 'analysis_running' && e.body?.runId) {
          // Ya había uno: se muestra el aviso y se engancha a esa ejecución.
          setError({ code: e.code, message: e.message });
          if (stateRef.current.runId !== e.body.runId) {
            try {
              const current = await api.getCurrentRun();
              if (current) follow(current);
            } catch {
              // se queda el aviso
            }
          }
        } else {
          setError({ code: e instanceof ApiError ? e.code : 'error', message: errorMessage(e) });
        }
      } finally {
        setStarting(false);
      }
    },
    [follow],
  );

  const cancel = useCallback(async () => {
    const runId = stateRef.current.runId;
    if (!runId) return;
    setError(null);
    setCancelling(true);
    try {
      const status = await api.cancelRun(runId);
      dispatch({ type: 'status', status });
    } catch (e) {
      setError({ code: e instanceof ApiError ? e.code : 'error', message: errorMessage(e) });
    } finally {
      setCancelling(false);
    }
  }, []);

  const regenerate = useCallback(
    async (reports: string[], synthesis: boolean) => {
      const runId = stateRef.current.runId;
      if (!runId || reports.length === 0) return;
      setError(null);
      setRegenerateError(null);
      setRegenerating(reports);
      try {
        const status = await api.regenerateReports(runId, reports, synthesis);
        // Sin reset: el reductor conserva los demás carriles y aplica los eventos nuevos, que siguen el mismo seq.
        follow(status, stateRef.current.lastSeq);
      } catch (e) {
        setRegenerating(null);
        setRegenerateError({ reports, code: e instanceof ApiError ? e.code : 'error', message: errorMessage(e) });
      }
    },
    [follow],
  );

  const clearError = useCallback(() => {
    setError(null);
    setRegenerateError(null);
  }, []);

  return { state, connection, error, starting, cancelling, regenerating, regenerateError, start, cancel, regenerate, clearError };
}
