import { describe, expect, it } from 'vitest';
import type { AnalysisEventDto } from '../api/types';
import { cancelledDuringReports, cancelledRun, regenerationAfter, RUN_ID, runningStatus, successfulRun, verificationAfter } from '../test/fixtures';
import { preparedRun, preparedRunStatus } from '../test/preparationFixtures';
import {
  canRegenerate,
  canUndoVerification,
  canVerify,
  createRunState,
  isFailedReport,
  mainArgument,
  progressOf,
  runReducer,
  type RunState,
} from './runReducer';

function replay(events: AnalysisEventDto[], state: RunState = createRunState(runningStatus)): RunState {
  return events.reduce((s, event) => runReducer(s, { type: 'event', event }), state);
}

const upTo = (type: string, occurrence = 1) => {
  let seen = 0;
  const index = successfulRun.findIndex((e) => e.type === type && ++seen === occurrence);
  return successfulRun.slice(0, index + 1);
};

describe('runReducer: consumo LLM', () => {
  it('suma en vivo los llm.response con reparto de tokens y run.completed lo sustituye por el total del servidor', () => {
    const live = replay(upTo('llm.response'));
    expect(live.usage).toEqual({ calls: 1, promptTokens: 800, completionTokens: 100, totalTokens: 900, costUsd: null, unpricedCalls: 1 });

    const done = replay(successfulRun.map((e) =>
      e.type === 'run.completed'
        ? { ...e, data: { ...e.data, usage: { calls: 2, promptTokens: 1000, completionTokens: 150, totalTokens: 1150, costUsd: 0.25, unpricedCalls: 0 } } }
        : e,
    ));
    expect(done.usage).toEqual({ calls: 2, promptTokens: 1000, completionTokens: 150, totalTokens: 1150, costUsd: 0.25, unpricedCalls: 0 });
  });

  it('un llm.response sin reparto no cuenta como llamada, y el coste solo existe si alguna llamada lo trae', () => {
    const base = upTo('run.started');
    const call = (seq: number, data: Record<string, unknown>): AnalysisEventDto => ({
      ...successfulRun.find((e) => e.type === 'llm.response')!,
      seq,
      data: { finishReason: 'Stop', tokens: 10, toolCalls: [], ...data },
    });
    const state = replay([
      ...base,
      call(100, {}),
      call(101, { promptTokens: 10, completionTokens: 5, costUsd: null }),
      call(102, { promptTokens: 20, completionTokens: 5, costUsd: 0.5 }),
    ]);
    expect(state.usage).toEqual({ calls: 2, promptTokens: 30, completionTokens: 10, totalTokens: 40, costUsd: 0.5, unpricedCalls: 1 });
  });
});

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
    const withoutEnrich = successfulRun.filter((e) => e.source !== 'LanguageEnricherStep' && e.source !== 'DotNetEnricherStep');
    const state = replay(withoutEnrich);
    expect(state.phases.enrich.status).toBe('skipped');
  });

  it('enrich no parpadea entre sus dos steps: solo el step.completed de DotNetEnricherStep la cierra', () => {
    const languageDone = successfulRun.findIndex((e) => e.type === 'step.completed' && e.source === 'LanguageEnricherStep');
    const afterFirst = replay(successfulRun.slice(0, languageDone + 1));
    expect(afterFirst.phases.enrich).toMatchObject({ status: 'running', finishedAt: null, durationMs: null });

    const dotNetStarted = replay(successfulRun.slice(0, languageDone + 2));
    expect(dotNetStarted.phases.enrich.status).toBe('running');
    expect(dotNetStarted.phases.enrich.startedAt).toBe(afterFirst.phases.enrich.startedAt);

    const dotNetDone = successfulRun.findIndex((e) => e.type === 'step.completed' && e.source === 'DotNetEnricherStep');
    const closed = replay(successfulRun.slice(0, dotNetDone + 1));
    expect(closed.phases.enrich.status).toBe('succeeded');
    expect(closed.phases.enrich.finishedAt).toBe(successfulRun[dotNetDone].ts);
    expect(closed.phases.facets.status).toBe('pending');
  });

  it('el fallo de cualquiera de los dos steps de enrich la marca fallida', () => {
    const languageDone = successfulRun.findIndex((e) => e.type === 'step.completed' && e.source === 'LanguageEnricherStep');
    const failedFirst = successfulRun.map((e, i) =>
      i === languageDone ? { ...e, level: 'error' as const, data: { ...e.data, success: false, error: 'boom' } } : e,
    );
    const dotNetDone = successfulRun.findIndex((e) => e.type === 'step.completed' && e.source === 'DotNetEnricherStep');

    const afterFailure = replay(failedFirst.slice(0, languageDone + 1));
    expect(afterFailure.phases.enrich).toMatchObject({ status: 'failed', error: 'boom' });
    // El step siguiente (attempt 1) no reabre la fase ni su éxito la da por buena.
    const afterDotNet = replay(failedFirst.slice(0, dotNetDone + 1));
    expect(afterDotNet.phases.enrich).toMatchObject({ status: 'failed', error: 'boom' });

    const failedLast = successfulRun.map((e, i) =>
      i === dotNetDone ? { ...e, level: 'error' as const, data: { ...e.data, success: false, error: 'HTTP 500' } } : e,
    );
    expect(replay(failedLast.slice(0, dotNetDone + 1)).phases.enrich).toMatchObject({ status: 'failed', error: 'HTTP 500' });
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

  it('canRegenerate: carriles terminados (fallidos, cancelados y, por D1, correctos) con la ejecución terminada', () => {
    const done = replay(cancelledDuringReports);
    expect(canRegenerate(done, 'CodeSmells')).toBe(true); // failed
    expect(canRegenerate(done, 'InyeccionDependenciasExtensiones')).toBe(true); // cancelled
    expect(canRegenerate(done, 'CodigoMuerto')).toBe(false); // no es un carril

    const running = replay(regeneration.slice(0, 1), done);
    expect(canRegenerate(running, 'InyeccionDependenciasExtensiones')).toBe(false);

    // D1: rehacer un informe sano también se ofrece.
    const succeeded = replay(successfulRun);
    expect(canRegenerate(succeeded, 'CodeSmells')).toBe(true);
    expect(canRegenerate(replay(successfulRun.slice(0, 15)), 'CodeSmells')).toBe(false); // con la ejecución en curso
  });
});

describe('runReducer: verificación a demanda', () => {
  const verification = verificationAfter(successfulRun, ['CodeSmells']);
  const lastOf = (events: AnalysisEventDto[], type: string) => events.findLast((e) => e.type === type)!;

  it('canVerify: solo carriles con el auditor terminado bien, con la ejecución terminada y sin otra verificación en curso', () => {
    const done = replay(successfulRun);
    expect(canVerify(done, 'CodeSmells')).toBe(true);
    expect(canVerify(done, 'CodigoMuerto')).toBe(false); // no es un carril

    const failed = replay(cancelledDuringReports);
    expect(canVerify(failed, 'CodeSmells')).toBe(false); // el auditor falló: no hay informe que verificar
    expect(canVerify(failed, 'InyeccionDependenciasExtensiones')).toBe(false); // cancelado

    expect(canVerify(replay(successfulRun.slice(0, 15)), 'CodeSmells')).toBe(false); // la ejecución sigue en curso
    const verifying = replay(verification.slice(0, 1), done);
    expect(canVerify(verifying, 'CodeSmells')).toBe(false); // y mientras verifica no hay botones
  });

  it('run.verification_started reabre la ejecución y arranca la verificación solo de esos carriles, sin tocar los auditores ni las fases', () => {
    const done = replay(successfulRun);
    const state = replay(verification.slice(0, 1), done);

    expect(state.status).toBe('running');
    expect(state.finishedAt).toBeNull();
    expect(state.verifying).toBe(true);
    expect(state.auditors.CodeSmells.verification.status).toBe('running');
    expect(state.auditors.InyeccionDependenciasExtensiones.verification.status).toBe('idle');
    expect(state.auditors.CodeSmells.status).toBe('succeeded');
    expect(state.phases).toEqual(done.phases);
  });

  it('secuencia completa: el carril pasa a «corregido (n)» con sus versiones, sin tocar el auditor ni las fases, y el resumen queda desfasado', () => {
    const done = replay(successfulRun);
    const state = replay(verification, done);

    expect(state.status).toBe('succeeded');
    expect(state.verifying).toBe(false);
    expect(state.auditors.CodeSmells.verification).toMatchObject({
      status: 'corrected',
      changes: 2,
      previousVersionId: 'v0001',
      versionId: 'v0002',
      toolCalls: 1,
      activeTool: null,
    });
    // Las tools y el LLM del verificador no son del auditor.
    expect(state.auditors.CodeSmells.toolCalls).toBe(done.auditors.CodeSmells.toolCalls);
    expect(state.auditors.CodeSmells.status).toBe('succeeded');
    expect(state.auditors.CodeSmells.finishReason).toBe(done.auditors.CodeSmells.finishReason);
    expect(state.auditors.CodeSmells.durationMs).toBe(done.auditors.CodeSmells.durationMs);
    // Las fases siguen como estaban: la preparación y la síntesis de la verificación no las reabren.
    expect(state.phases).toEqual(done.phases);
    expect(state.synthesisStale).toBe(true);
    expect(state.reportsVersion).toBeGreaterThan(done.reportsVersion);
    expect(state.usage!.calls).toBe(done.usage!.calls + 1);
    expect(canUndoVerification(state, 'CodeSmells')).toBe(true);
    expect(canUndoVerification(state, 'InyeccionDependenciasExtensiones')).toBe(false);
  });

  it('sin correcciones queda «verificado» y no hay nada que deshacer ni resumen desfasado', () => {
    const events = verificationAfter(successfulRun, ['CodeSmells'], { outcomes: { CodeSmells: { outcome: 'verified', changes: 0 } } });
    const state = replay(events, replay(successfulRun));

    expect(state.auditors.CodeSmells.verification.status).toBe('verified');
    expect(state.synthesisStale).toBe(false);
    expect(canUndoVerification(state, 'CodeSmells')).toBe(false);
  });

  it('con avisos de validación queda «con avisos» y conserva su versión anterior', () => {
    const events = verificationAfter(successfulRun, ['CodeSmells'], {
      outcomes: { CodeSmells: { outcome: 'warnings', changes: 1, previousVersionId: 'v0003', versionId: 'v0004' } },
    });
    const state = replay(events, replay(successfulRun));

    expect(state.auditors.CodeSmells.verification).toMatchObject({ status: 'warnings', changes: 1, previousVersionId: 'v0003' });
    expect(state.synthesisStale).toBe(true);
    expect(canUndoVerification(state, 'CodeSmells')).toBe(true);
  });

  it('si el verificador falla, el carril lo dice con el motivo, no hay versión que deshacer y la ejecución no falla', () => {
    const events = verificationAfter(successfulRun, ['CodeSmells'], {
      outcomes: { CodeSmells: { outcome: 'failed', error: 'El verificador falló: HTTP 500' } },
    });
    const state = replay(events, replay(successfulRun));

    expect(state.auditors.CodeSmells.verification).toMatchObject({ status: 'failed', message: 'El verificador falló: HTTP 500', previousVersionId: null });
    expect(state.status).toBe('succeeded');
    expect(state.errors).toBeGreaterThan(replay(successfulRun).errors); // el evento de nivel error cuenta en el registro
    expect(state.synthesisStale).toBe(false);
    expect(canUndoVerification(state, 'CodeSmells')).toBe(false);
  });

  it('con síntesis pedida el resumen no queda desfasado y la fase de síntesis no se reabre', () => {
    const events = verificationAfter(successfulRun, ['CodeSmells'], { synthesis: true });
    const state = replay(events, replay(successfulRun));

    expect(state.verifySynthesis).toBe(true);
    expect(state.synthesisStale).toBe(false);
    expect(state.phases.synthesis).toEqual(replay(successfulRun).phases.synthesis);
  });

  it('cancelada a mitad, el carril vuelve a «sin verificar»; terminada con error, queda fallida', () => {
    const cut = verification.findIndex((e) => e.type === 'tool.started') + 1;
    const done = replay(successfulRun);
    const lastSeq = verification[cut - 1].seq;
    const completed = (status: string, error: string | null): AnalysisEventDto => ({
      ...lastOf(verification, 'run.completed'),
      seq: lastSeq + 1,
      level: 'warning',
      data: { status, exitCode: 1, durationMs: 5, failedReports: [], unverifiedReports: 0, executiveReportPath: null, error },
    });

    const cancelled = replay([...verification.slice(0, cut), completed('cancelled', 'Verificación cancelada.')], done);
    expect(cancelled.status).toBe('cancelled');
    expect(cancelled.verifying).toBe(false);
    expect(cancelled.auditors.CodeSmells.verification.status).toBe('idle');
    expect(cancelled.auditors.CodeSmells.status).toBe('succeeded');

    const failed = replay([...verification.slice(0, cut), completed('failed', 'se cayó')], done);
    expect(failed.auditors.CodeSmells.verification).toMatchObject({ status: 'failed', message: 'se cayó' });
  });

  it('una regeneración con resumen apaga el aviso de desfase y estrena el carril sin verificación', () => {
    const verified = replay(verification, replay(successfulRun));
    expect(verified.synthesisStale).toBe(true);

    const regenerated = replay(regenerationAfter([...successfulRun, ...verification], ['CodeSmells']), verified);

    expect(regenerated.synthesisStale).toBe(false);
    expect(regenerated.auditors.CodeSmells.verification.status).toBe('idle');
    expect(regenerated.verifying).toBe(false);
  });

  it('deshacer devuelve el carril a «sin verificar» y refresca los informes', () => {
    const verified = replay(verification, replay(successfulRun));

    const undone = runReducer(verified, { type: 'verification_undone', report: 'CodeSmells' });

    expect(undone.auditors.CodeSmells.verification.status).toBe('idle');
    expect(canUndoVerification(undone, 'CodeSmells')).toBe(false);
    expect(undone.reportsVersion).toBe(verified.reportsVersion + 1);
    expect(runReducer(verified, { type: 'verification_undone', report: 'NoExiste' })).toBe(verified);
  });

  it('el mismo replay desde 0 (recarga de la página) da el mismo estado que seguirlo en directo', () => {
    const live = replay(verification, replay(successfulRun));
    const fromZero = runReducer(createRunState(runningStatus), { type: 'events', events: [...successfulRun, ...verification] });
    expect(fromZero).toEqual(live);
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

describe('runReducer: preparación por subsistema e investigaciones (modo B)', () => {
  const prepared = (events: AnalysisEventDto[]) => replay(events, createRunState(preparedRunStatus));
  const indexOf = (type: string, source: string) => preparedRun.findIndex((e) => e.type === type && e.source === source);
  const upToEvent = (type: string, source: string) => preparedRun.slice(0, indexOf(type, source) + 1);

  it('la ejecución completa deja las cinco fases y los dos carriles de modo B terminados, sin carriles por investigación', () => {
    const state = prepared(preparedRun);
    expect(Object.values(state.phases).map((p) => p.status)).toEqual(['succeeded', 'succeeded', 'succeeded', 'succeeded', 'succeeded']);
    expect(state.status).toBe('succeeded');
    // Un carril por informe: las investigaciones (`Investigation_<tipo>_<id>`) no abren carriles propios.
    expect(state.auditorOrder).toEqual(['AnalisisFuncional', 'FlujosEndToEnd']);
    expect(state.auditors.AnalisisFuncional).toMatchObject({ status: 'succeeded', health: 'Amber', fileName: '01_analisis_funcional.md', error: null });
    expect(state.auditors.FlujosEndToEnd).toMatchObject({ status: 'succeeded', health: 'Amber', fileName: '04_flujos_end_to_end.md', error: null });
    expect(progressOf(state)).toMatchObject({ done: 7, total: 7, ratio: 1 });
    expect(state.pendingToolArgs).toEqual({});
  });

  it('la fase facets sigue en marcha entre las facetas y los cuatro pasos de preparación, y la cierra TimeBudgetStep', () => {
    const facetsStart = preparedRun[indexOf('step.started', 'SqlFacetExtractorStep')].ts;
    for (const source of ['RepositoryInventoryStep', 'DomainDiscoveryStep', 'CoveragePlanningStep']) {
      const state = prepared(upToEvent('step.completed', source));
      expect(state.phases.facets, source).toMatchObject({ status: 'running', finishedAt: null, startedAt: facetsStart });
      expect(state.phases.reports.status, source).toBe('pending');
    }

    const closed = prepared(upToEvent('step.completed', 'TimeBudgetStep'));
    const finishedAt = preparedRun[indexOf('step.completed', 'TimeBudgetStep')].ts;
    expect(closed.phases.facets).toMatchObject({ status: 'succeeded', startedAt: facetsStart, finishedAt, error: null });
    // La duración cubre las facetas y la preparación (de la primera a la última), no solo el último paso.
    expect(closed.phases.facets.durationMs).toBe(Date.parse(finishedAt) - Date.parse(facetsStart));
  });

  it('el sub-step DomainNaming (LLM) y sus llamadas no mueven fases ni carriles, pero cuentan en el consumo', () => {
    const state = prepared(upToEvent('llm.response', 'DomainNaming'));
    expect(state.phases.facets.status).toBe('running');
    expect(state.auditorOrder).toEqual(['AnalisisFuncional', 'FlujosEndToEnd']);
    expect(Object.values(state.auditors).map((a) => a.status)).toEqual(['pending', 'pending']);
    expect(state.usage).toMatchObject({ calls: 1, totalTokens: 700 });
  });

  it('el fallo de un paso de preparación marca la fase facets como fallida', () => {
    const failedAt = indexOf('step.completed', 'CoveragePlanningStep');
    const failing = preparedRun.slice(0, failedAt + 1).map((e, i) =>
      i === failedAt ? { ...e, level: 'error' as const, data: { ...e.data, success: false, error: 'boom' } } : e,
    );
    const state = prepared(failing);
    expect(state.phases.facets).toMatchObject({ status: 'failed', error: 'boom' });
    expect(state.errors).toBe(1);
  });

  it('con la preparación apagada (Mode = Off) las facetas se cierran con SqlFacetExtractorStep como siempre', () => {
    const preparation = ['RepositoryInventoryStep', 'DomainDiscoveryStep', 'DomainNaming', 'CoveragePlanningStep', 'TimeBudgetStep'];
    const withoutPreparation = preparedRun.filter((e) => !preparation.includes(e.source ?? ''));
    const closedAt = withoutPreparation.findIndex((e) => e.type === 'step.completed' && e.source === 'SqlFacetExtractorStep');
    const state = prepared(withoutPreparation.slice(0, closedAt + 1));
    expect(state.phases.facets).toMatchObject({ status: 'succeeded', durationMs: 1000 });
  });

  it('las investigaciones suman al carril de su informe: tools, consumo y tool activa, sin tocar el otro carril', () => {
    // La primera investigación de AnalisisFuncional (catalog): llamada con una tool, sin cerrar todavía.
    const toolStarted = preparedRun.findIndex((e) => e.type === 'tool.started' && e.source === 'Investigation_AnalisisFuncional_catalog');
    const during = prepared(preparedRun.slice(0, toolStarted + 1));
    expect(during.auditors.AnalisisFuncional).toMatchObject({ status: 'running', toolCalls: 1, activeTool: 'view_file' });
    expect(during.auditors.FlujosEndToEnd).toMatchObject({ status: 'running', toolCalls: 0, activeTool: null });
    // El argumento de la tool se empareja por step: la investigación tiene su propia cola.
    expect(during.log.at(-1)).toMatchObject({ type: 'tool.started', source: 'Investigation_AnalisisFuncional_catalog', report: 'AnalisisFuncional', detail: 'src/catalog/Service.cs' });

    // Las cuatro investigaciones (dos por informe, una tool cada una) antes de que arranque la consolidación.
    const consolidating = preparedRun.findIndex((e) => e.type === 'llm.response' && e.source === 'Auditor_AnalisisFuncional');
    const after = prepared(preparedRun.slice(0, consolidating));
    expect(after.auditors.AnalisisFuncional).toMatchObject({ toolCalls: 2, toolErrors: 0, activeTool: null, status: 'running' });
    expect(after.auditors.FlujosEndToEnd).toMatchObject({ toolCalls: 2, toolErrors: 0, activeTool: null, status: 'running' });
    expect(after.auditors.AnalisisFuncional.toolTimes).toHaveLength(2);
    // Consumo: una llamada de nombrado y dos por investigación (4 investigaciones).
    expect(after.usage).toMatchObject({ calls: 1 + 4 * 2, unpricedCalls: 0 });
  });

  it('un lote con la ejecución entera da el mismo estado que aplicarla evento a evento, y repetirlo no cambia nada', () => {
    const live = prepared(preparedRun);
    const batched = runReducer(createRunState(preparedRunStatus), { type: 'events', events: preparedRun });
    expect(batched).toEqual(live);
    expect(runReducer(live, { type: 'events', events: preparedRun })).toBe(live);
  });

  it('los informes terminados de una ejecución con investigaciones se pueden regenerar y verificar', () => {
    const state = prepared(preparedRun);
    expect(canRegenerate(state, 'AnalisisFuncional')).toBe(true);
    expect(canVerify(state, 'FlujosEndToEnd')).toBe(true);
  });
});
