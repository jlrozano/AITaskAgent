import { describe, expect, it } from 'vitest';
import type { AnalysisEventDto } from '../api/types';
import { cancelledDuringReports, cancelledRun, regenerationAfter, RUN_ID, runningStatus, successfulRun } from '../test/fixtures';
import { canRegenerate, createRunState, isFailedReport, mainArgument, progressOf, runReducer, type RunState } from './runReducer';

function replay(events: AnalysisEventDto[], state: RunState = createRunState(runningStatus)): RunState {
  return events.reduce((s, event) => runReducer(s, { type: 'event', event }), state);
}

const upTo = (type: string, occurrence = 1) => {
  let seen = 0;
  const index = successfulRun.findIndex((e) => e.type === type && ++seen === occurrence);
  return successfulRun.slice(0, index + 1);
};

describe('runReducer', () => {
  it('parte de los auditores pedidos en RunStatusDto, todos en espera', () => {
    const state = createRunState(runningStatus);
    expect(state.auditorOrder).toEqual(['CodeSmells', 'InyeccionDependenciasExtensiones']);
    expect(state.auditors.CodeSmells.status).toBe('pending');
    expect(Object.values(state.phases).every((p) => p.status === 'pending')).toBe(true);
  });

  it('reproduce la ejecución completa: fases y auditores acaban en el estado esperado', () => {
    const state = replay(successfulRun);
    expect(state.status).toBe('succeeded');
    expect(state.exitCode).toBe(0);
    expect(state.lastSeq).toBe(successfulRun.length);
    expect(Object.fromEntries(Object.entries(state.phases).map(([k, v]) => [k, v.status]))).toEqual({
      detect: 'succeeded',
      enrich: 'succeeded',
      facets: 'succeeded',
      reports: 'succeeded',
      synthesis: 'succeeded',
    });
    expect(state.phases.detect.durationMs).toBe(4000);
    // enrich tiene dos steps: su duración va del primer evento de la fase al cierre del último (P16).
    expect(state.phases.enrich.durationMs).toBe(5000);

    const smells = state.auditors.CodeSmells;
    expect(smells).toMatchObject({
      status: 'succeeded',
      toolCalls: 2,
      toolErrors: 1,
      tokens: 15000,
      costUsd: 0.04,
      health: 'Amber',
      criticalFindings: 3,
      truncated: false,
      fileName: '02_code_smells.md',
      retries: 0,
      activeTool: null,
    });
    const di = state.auditors.InyeccionDependenciasExtensiones;
    expect(di).toMatchObject({ status: 'succeeded', health: 'Red', truncated: true, finishReason: 'Length' });
    // El 00 no crea auditor.
    expect(state.auditorOrder).toHaveLength(2);
    expect(progressOf(state)).toEqual({ done: 7, total: 7, ratio: 1 });
  });

  it('cuenta un reintento (attempt > 1) y vuelve a poner el auditor en marcha', () => {
    const state = replay(upTo('step.started', 8)); // el segundo step.started del auditor DI (attempt 2)
    const di = state.auditors.InyeccionDependenciasExtensiones;
    expect(di.retries).toBe(1);
    expect(di.status).toBe('running');
    expect(di.error).toBeNull();
    expect(state.warnings).toBeGreaterThanOrEqual(1);
  });

  it('marca la fase en curso y las siguientes pendientes a mitad de ejecución', () => {
    const state = replay(upTo('tool.started'));
    expect(state.phases.detect.status).toBe('succeeded');
    expect(state.phases.reports.status).toBe('running');
    expect(state.phases.synthesis.status).toBe('pending');
    expect(state.auditors.CodeSmells.activeTool).toBe('view_file');
    expect(state.auditors.CodeSmells.status).toBe('running');
  });

  it('empareja el argumento de la tool con el llm.response previo del mismo step, en orden', () => {
    const state = replay(successfulRun);
    const toolRows = state.log.filter((e) => e.type === 'tool.started');
    expect(toolRows.map((r) => [r.tool, r.detail])).toEqual([
      ['view_file', 'src/Orders/OrderService.cs'],
      ['graph_search', 'Order'],
    ]);
  });

  it('ignora eventos repetidos (solape de replay al reconectar) y de otra ejecución', () => {
    const half = successfulRun.slice(0, 20);
    const once = replay(successfulRun);
    const withOverlap = replay(successfulRun, replay(half));
    expect(withOverlap).toEqual(once);
    const foreign = { ...successfulRun[successfulRun.length - 1], seq: 999, runId: 'otra' };
    expect(runReducer(once, { type: 'event', event: foreign })).toBe(once);
  });

  it('run.completed fija el estado final: cancelado deja fases y auditores interrumpidos', () => {
    const partial = upTo('tool.started');
    const last = partial[partial.length - 1];
    const cancelled: AnalysisEventDto = {
      seq: last.seq + 1,
      runId: RUN_ID,
      ts: '2026-09-27T10:20:00Z',
      type: 'run.completed',
      source: null,
      report: null,
      phase: null,
      level: 'warning',
      message: 'Cancelado',
      data: { status: 'cancelled', exitCode: 1, durationMs: 300000, failedReports: [], executiveReportPath: null, error: null },
    };
    const state = replay([...partial, cancelled]);
    expect(state.status).toBe('cancelled');
    expect(state.finishedAt).toBe('2026-09-27T10:20:00Z');
    expect(state.phases.reports.status).toBe('cancelled');
    expect(state.phases.synthesis.status).toBe('skipped');
    expect(state.auditors.CodeSmells.status).toBe('cancelled');
    expect(state.auditors.CodeSmells.activeTool).toBeNull();
    expect(progressOf(state).ratio).toBe(1);
  });

  it('run.completed con informes fallidos los marca como fallidos', () => {
    const last = successfulRun[successfulRun.length - 1];
    const events = [
      ...successfulRun.slice(0, -1),
      { ...last, data: { ...last.data, status: 'completed_with_failures', exitCode: 1, failedReports: ['CodeSmells'] } },
    ];
    const state = replay(events);
    expect(state.status).toBe('completed_with_failures');
    expect(state.failedReports).toEqual(['CodeSmells']);
    expect(state.auditors.CodeSmells.status).toBe('failed');
    expect(state.auditors.InyeccionDependenciasExtensiones.status).toBe('succeeded');
  });

  it('una fase que no llega a arrancar antes de la siguiente queda omitida', () => {
    const withoutEnrich = successfulRun.filter((e) => e.source !== 'LanguageEnricherStep' && e.source !== 'BdpEnricherStep');
    const state = replay(withoutEnrich);
    expect(state.phases.enrich.status).toBe('skipped');
  });

  it('enrich no parpadea entre sus dos steps: solo el step.completed de BdpEnricherStep la cierra', () => {
    const languageDone = successfulRun.findIndex((e) => e.type === 'step.completed' && e.source === 'LanguageEnricherStep');
    const afterFirst = replay(successfulRun.slice(0, languageDone + 1));
    expect(afterFirst.phases.enrich).toMatchObject({ status: 'running', finishedAt: null, durationMs: null });

    const bdpStarted = replay(successfulRun.slice(0, languageDone + 2));
    expect(bdpStarted.phases.enrich.status).toBe('running');
    expect(bdpStarted.phases.enrich.startedAt).toBe(afterFirst.phases.enrich.startedAt);

    const bdpDone = successfulRun.findIndex((e) => e.type === 'step.completed' && e.source === 'BdpEnricherStep');
    const closed = replay(successfulRun.slice(0, bdpDone + 1));
    expect(closed.phases.enrich.status).toBe('succeeded');
    expect(closed.phases.enrich.finishedAt).toBe(successfulRun[bdpDone].ts);
    expect(closed.phases.facets.status).toBe('pending');
  });

  it('el fallo de cualquiera de los dos steps de enrich la marca fallida', () => {
    const languageDone = successfulRun.findIndex((e) => e.type === 'step.completed' && e.source === 'LanguageEnricherStep');
    const failedFirst = successfulRun.map((e, i) =>
      i === languageDone ? { ...e, level: 'error' as const, data: { ...e.data, success: false, error: 'boom' } } : e,
    );
    const bdpDone = successfulRun.findIndex((e) => e.type === 'step.completed' && e.source === 'BdpEnricherStep');

    const afterFailure = replay(failedFirst.slice(0, languageDone + 1));
    expect(afterFailure.phases.enrich).toMatchObject({ status: 'failed', error: 'boom' });
    // El step siguiente (attempt 1) no reabre la fase ni su éxito la da por buena.
    const afterBdp = replay(failedFirst.slice(0, bdpDone + 1));
    expect(afterBdp.phases.enrich).toMatchObject({ status: 'failed', error: 'boom' });

    const failedLast = successfulRun.map((e, i) =>
      i === bdpDone ? { ...e, level: 'error' as const, data: { ...e.data, success: false, error: 'HTTP 500' } } : e,
    );
    expect(replay(failedLast.slice(0, bdpDone + 1)).phases.enrich).toMatchObject({ status: 'failed', error: 'HTTP 500' });
  });

  it('cuenta avisos y errores y sube reportsVersion con cada informe y al terminar', () => {
    const state = replay(successfulRun);
    const warnings = successfulRun.filter((e) => e.level === 'warning').length;
    const errors = successfulRun.filter((e) => e.level === 'error').length;
    expect(state.warnings).toBe(warnings);
    expect(state.errors).toBe(errors);
    expect(state.reportsVersion).toBe(4); // 3 report.written + run.completed
  });

  it('log.truncated anota hasta dónde descartó el servidor', () => {
    const truncated: AnalysisEventDto = {
      seq: 1,
      runId: RUN_ID,
      ts: '2026-09-27T10:15:00Z',
      type: 'log.truncated',
      source: null,
      report: null,
      phase: null,
      level: 'warning',
      message: 'Eventos descartados',
      data: { droppedUpToSeq: 1200 },
    };
    expect(replay([truncated]).droppedUpToSeq).toBe(1200);
  });

  it('reset con otro RunStatusDto empieza de cero', () => {
    const done = replay(successfulRun);
    const next = runReducer(done, { type: 'reset', status: { ...runningStatus, runId: 'nuevo', requestedReports: ['CodeSmells'] } });
    expect(next.runId).toBe('nuevo');
    expect(next.log).toHaveLength(0);
    expect(next.auditorOrder).toEqual(['CodeSmells']);
  });
});

describe('runReducer: ejecución cancelada (D4)', () => {
  const statuses = (state: RunState) => Object.fromEntries(Object.entries(state.phases).map(([k, v]) => [k, v.status]));

  it('secuencia real: la fase interrumpida queda cancelada, no «Con error», y las cancelaciones no cuentan como errores', () => {
    const state = replay(cancelledRun, createRunState(null));
    expect(state.status).toBe('cancelled');
    expect(statuses(state)).toEqual({
      detect: 'cancelled',
      enrich: 'skipped',
      facets: 'skipped',
      reports: 'skipped',
      synthesis: 'skipped',
    });
    expect(state.errors).toBe(0);
    expect(state.warnings).toBe(1); // el propio run.completed
    expect(Object.values(state.auditors).every((a) => a.status === 'skipped')).toBe(true);
    expect(progressOf(state).ratio).toBe(1);
  });

  it('mientras no llega run.completed el fallo por cancelación se ve como error (aún no se sabe)', () => {
    const state = replay(cancelledRun.slice(0, -1), createRunState(null));
    expect(state.phases.detect.status).toBe('failed');
    expect(state.errors).toBe(2);
  });

  it('con auditores en marcha: el cancelado pasa a cancelado y el que ya había fallado de verdad sigue fallido', () => {
    const state = replay(cancelledDuringReports);
    expect(state.status).toBe('cancelled');
    expect(state.phases.reports.status).toBe('cancelled');
    expect(state.phases.synthesis.status).toBe('skipped');
    expect(state.auditors.InyeccionDependenciasExtensiones.status).toBe('cancelled');
    expect(state.auditors.CodeSmells.status).toBe('failed');
    expect(state.auditors.CodeSmells.error).toBe('HTTP 429');
    // Solo cuentan los dos eventos del HTTP 429 (step.completed y branch.completed del auditor).
    expect(state.errors).toBe(2);
  });

  it('un run fallido (no cancelado) con el mismo texto de error sigue contando el error y la fase fallida', () => {
    const last = cancelledRun[cancelledRun.length - 1];
    const failed = [
      ...cancelledRun.slice(0, -1),
      { ...last, level: 'error' as const, data: { ...last.data, status: 'failed', error: 'A task was canceled.' } },
    ];
    const state = replay(failed, createRunState(null));
    expect(state.phases.detect.status).toBe('failed');
    expect(state.errors).toBe(3);
  });
});

describe('runReducer: replay grande (P3-12)', () => {
  function bigReplay(count: number): AnalysisEventDto[] {
    const events: AnalysisEventDto[] = [];
    const t0 = Date.parse('2026-09-27T10:15:00Z');
    for (let i = 1; i <= count; i++) {
      events.push({
        seq: i,
        runId: RUN_ID,
        ts: new Date(t0 + i).toISOString(),
        type: 'log',
        source: 'DetectAndIndexGraphStep',
        report: null,
        phase: 'detect',
        level: 'info',
        message: `línea ${i}`,
        data: { channel: 'indexer' },
      });
    }
    return events;
  }

  it('aplica un lote de 50 000 eventos (el tope del servidor) en tiempo lineal', () => {
    const events = bigReplay(50_000);
    const t0 = performance.now();
    const state = runReducer(createRunState(runningStatus), { type: 'events', events });
    const elapsed = performance.now() - t0;
    expect(state.log).toHaveLength(50_000);
    expect(state.lastSeq).toBe(50_000);
    expect(elapsed).toBeLessThan(1500);
  });

  it('el lote da el mismo estado que aplicar los eventos de uno en uno', () => {
    const events = [...successfulRun];
    const batched = runReducer(createRunState(runningStatus), { type: 'events', events });
    expect(batched).toEqual(replay(events));
  });

  it('no muta el estado anterior', () => {
    const before = replay(successfulRun.slice(0, 10));
    const snapshot = structuredClone(before);
    runReducer(before, { type: 'events', events: successfulRun.slice(10) });
    expect(before).toEqual(snapshot);
  });
});

describe('runReducer: regeneración de informes (R3, R8)', () => {
  const statuses = (state: RunState) => Object.fromEntries(Object.entries(state.phases).map(([k, v]) => [k, v.status]));
  const regeneration = regenerationAfter(cancelledDuringReports, ['CodeSmells']);

  it('run.regeneration_started vuelve a running, resetea los carriles pedidos y conserva los demás', () => {
    const before = replay(cancelledDuringReports);
    const state = replay(regeneration.slice(0, 1), before);

    expect(state.status).toBe('running');
    expect(state.runId).toBe(RUN_ID);
    expect(state.finishedAt).toBeNull();
    expect(state.error).toBeNull();
    expect(state.exitCode).toBeNull();
    // El pedido empieza de cero: sin el error del HTTP 429, sin tiempos ni tools.
    expect(state.auditors.CodeSmells).toMatchObject({ status: 'pending', error: null, startedAt: null, finishedAt: null, toolCalls: 0 });
    // El otro carril y el registro siguen como estaban.
    expect(state.auditors.InyeccionDependenciasExtensiones).toEqual(before.auditors.InyeccionDependenciasExtensiones);
    expect(state.auditorOrder).toEqual(before.auditorOrder);
    expect(state.log).toHaveLength(cancelledDuringReports.length + 1);
    expect(state.lastSeq).toBe(cancelledDuringReports.length + 1);
    // Detección y enriquecimiento no se repiten; facetas, auditores y síntesis vuelven a estar pendientes.
    expect(statuses(state)).toEqual({ detect: 'succeeded', enrich: 'succeeded', facets: 'pending', reports: 'pending', synthesis: 'pending' });
  });

  it('sin síntesis, la fase del resumen ejecutivo conserva su estado', () => {
    const noSynthesis = regenerationAfter(cancelledDuringReports, ['CodeSmells'], { synthesis: false });
    const state = replay(noSynthesis.slice(0, 1), replay(cancelledDuringReports));
    expect(state.phases.synthesis.status).toBe('skipped');
    expect(state.phases.reports.status).toBe('pending');
  });

  it('la regeneración completa deja el carril regenerado terminado y los demás intactos', () => {
    const state = replay([...cancelledDuringReports, ...regeneration]);

    expect(state.status).toBe('succeeded');
    expect(state.auditors.CodeSmells).toMatchObject({ status: 'succeeded', health: 'Green', fileName: '02_code_smells.md', error: null });
    expect(state.auditors.InyeccionDependenciasExtensiones.status).toBe('cancelled');
    expect(statuses(state)).toEqual({ detect: 'succeeded', enrich: 'succeeded', facets: 'succeeded', reports: 'succeeded', synthesis: 'succeeded' });
    // facets tiene dos steps en la regeneración: la preparación no la cierra, la cierra el de facetas.
    expect(state.phases.facets.durationMs).toBe(3000);
    expect(state.lastSeq).toBe(cancelledDuringReports.length + regeneration.length);
    expect(state.log.filter((e) => e.type === 'run.completed')).toHaveLength(2);
  });

  it('entre el step de preparación y el de facetas, la fase facets sigue en marcha', () => {
    const upToPrepare = regeneration.findIndex((e) => e.type === 'step.completed' && e.source === 'PrepareRegenerationStep');
    const state = replay(regeneration.slice(0, upToPrepare + 1), replay(cancelledDuringReports));
    expect(state.phases.facets.status).toBe('running');
    expect(state.phases.detect.status).toBe('succeeded');
  });

  it('un auditor que vuelve a fallar queda fallido y el servidor lo lista por fichero', () => {
    const failing = regenerationAfter(cancelledDuringReports, ['CodeSmells'], {
      synthesis: false,
      failing: ['CodeSmells'],
      failedReports: ['02_code_smells.md'],
    });
    const state = replay([...cancelledDuringReports, ...failing]);
    expect(state.status).toBe('completed_with_failures');
    expect(state.auditors.CodeSmells).toMatchObject({ status: 'failed', error: 'HTTP 500' });
    expect(canRegenerate(state, 'CodeSmells')).toBe(true);
  });

  it('el mismo replay desde 0 (recarga de la página) da el mismo estado que seguirlo en directo', () => {
    const live = replay(regeneration, replay(cancelledDuringReports));
    const fromZero = runReducer(createRunState(runningStatus), { type: 'events', events: [...cancelledDuringReports, ...regeneration] });
    expect(fromZero).toEqual(live);
  });

  it('criterio 6: failedReports con nombres de fichero marca los carriles correctos', () => {
    const last = successfulRun[successfulRun.length - 1];
    const events = [
      ...successfulRun.slice(0, -1),
      { ...last, data: { ...last.data, status: 'completed_with_failures', exitCode: 1, failedReports: ['09_inyeccion_dependencias.md'] } },
    ];
    const state = replay(events);
    expect(state.auditors.InyeccionDependenciasExtensiones.status).toBe('failed');
    expect(state.auditors.CodeSmells.status).toBe('succeeded');
    expect(isFailedReport(['02_code_smells.md'], 'CodeSmells')).toBe(true);
    expect(isFailedReport(['CodeSmells'], 'CodeSmells')).toBe(true);
    expect(isFailedReport(['02_code_smells.md'], 'CodigoMuerto')).toBe(false);
  });

  it('criterio 6: un auditor sin terminar que está en failedReports (fichero) queda fallido, no omitido', () => {
    const cut = successfulRun.findIndex((e) => e.type === 'branch.started');
    const completed = {
      ...successfulRun[successfulRun.length - 1],
      seq: cut + 2,
      data: { status: 'completed_with_failures', exitCode: 1, durationMs: 1, failedReports: ['02_code_smells.md'], executiveReportPath: null, error: null },
    };
    const state = replay([...successfulRun.slice(0, cut + 1), completed]);
    expect(state.auditors.CodeSmells.status).toBe('failed');
    expect(state.auditors.InyeccionDependenciasExtensiones.status).toBe('skipped');
  });

  it('canRegenerate: solo carriles fallidos o cancelados y con la ejecución terminada', () => {
    const done = replay(cancelledDuringReports);
    expect(canRegenerate(done, 'CodeSmells')).toBe(true); // failed
    expect(canRegenerate(done, 'InyeccionDependenciasExtensiones')).toBe(true); // cancelled
    expect(canRegenerate(done, 'CodigoMuerto')).toBe(false); // no es un carril

    const running = replay(regeneration.slice(0, 1), done);
    expect(canRegenerate(running, 'InyeccionDependenciasExtensiones')).toBe(false);

    const succeeded = replay(successfulRun);
    expect(canRegenerate(succeeded, 'CodeSmells')).toBe(false);
  });
});

describe('mainArgument', () => {
  it('toma el primer valor del JSON de argumentos o recorta el texto si no es JSON', () => {
    expect(mainArgument('{"path":"src/X.cs","start_line":3}')).toBe('src/X.cs');
    expect(mainArgument('{}')).toBeNull();
    expect(mainArgument('{"path":"src/muy/lar')).toBe('{"path":"src/muy/lar');
    expect(mainArgument(null)).toBeNull();
  });
});
