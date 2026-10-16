import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { AnalysisEventDto } from '../api/types';
import { RUN_ID, runningStatus, successfulRun } from '../test/fixtures';
import { createRunState, runReducer, type RunState } from '../state/runReducer';
import { SharedPassPanel } from './SharedPassPanel';

const replay = (events: AnalysisEventDto[], base: RunState = createRunState(runningStatus)): RunState =>
  runReducer(base, { type: 'events', events });

const startedAt = '2026-09-27T10:20:00Z';

const progress = (seq: number, data: Record<string, unknown>): AnalysisEventDto => ({
  seq,
  runId: RUN_ID,
  ts: new Date(Date.parse(startedAt) + seq * 1000).toISOString(),
  type: 'sharedpass.progress',
  source: 'SharedPassStep',
  report: null,
  phase: 'facets',
  level: 'info',
  message: 'Pasada compartida',
  data,
});

const batch = (id: string, status: string, extra: Record<string, unknown> = {}) => ({
  id,
  files: 4,
  estimatedTokens: 12000,
  status,
  calls: 0,
  costUsd: 0,
  tokens: 0,
  findings: null,
  gaps: null,
  ...extra,
});

const totals = (extra: Record<string, unknown> = {}) => ({
  costUsd: 1.5,
  maxCostUsd: 25,
  calls: 3,
  maxCalls: 1000,
  tokens: 40000,
  maxTokens: 50000000,
  unpricedCalls: 0,
  elapsedMs: 60000,
  maxMinutes: 90,
  batchesDone: 1,
  batchesTotal: 2,
  ...extra,
});

const reading = progress(100, {
  phase: 'reading',
  startedAt,
  batches: [batch('core', 'complete', { calls: 2, costUsd: 1.2, findings: 5 }), batch('billing', 'retrying', { calls: 1, costUsd: 0.3 })],
  totals: totals(),
  judge: null,
  stopReason: null,
  error: null,
  result: null,
});

const finished = progress(101, {
  phase: 'cut',
  startedAt,
  batches: [batch('core', 'complete', { calls: 2, costUsd: 1.2, findings: 5 }), batch('billing', 'notRead', { gaps: 4 })],
  totals: totals({ batchesDone: 2, costUsd: 24 }),
  judge: { candidates: 3, tandas: 1, tandasDone: 1 },
  stopReason: 'tope de coste de la ejecución alcanzado (24 de 25 USD)',
  error: null,
  result: { entries: 6, rejected: 1, severities: { P0: 0, P1: 2, P2: 1, P3: 2 } },
});

describe('runReducer: sharedpass.progress (W1)', () => {
  it('guarda el último estado sin añadir filas al registro ni avisos', () => {
    const base = replay(successfulRun.slice(0, 14));
    const state = replay([reading, { ...finished, level: 'warning' }], base);

    expect(state.log).toHaveLength(base.log.length);
    expect(state.warnings).toBe(base.warnings);
    expect(state.sharedPass).toMatchObject({ phase: 'cut', entries: 6, rejected: 1, severities: { P1: 2 } });
    expect(state.sharedPass?.batches.map((b) => b.status)).toEqual(['complete', 'notRead']);
  });

  it('un estado superado que el servidor reenvía sin lotes conserva los que ya había', () => {
    const superseded = progress(102, { phase: 'judging', startedAt, totals: totals(), superseded: true });
    const state = replay([reading, superseded]);
    expect(state.sharedPass?.phase).toBe('judging');
    expect(state.sharedPass?.batches.map((b) => b.id)).toEqual(['core', 'billing']);
  });
});

describe('SharedPassPanel', () => {
  it('sin eventos de la pasada no se pinta', () => {
    const { container } = render(<SharedPassPanel pass={replay(successfulRun).sharedPass} running={false} now={0} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('en curso: fase, lotes con su estado, coste frente al tope y tiempo que avanza', () => {
    const pass = replay([reading]).sharedPass;
    render(<SharedPassPanel pass={pass} running now={Date.parse(startedAt) + 120_000} />);

    const panel = screen.getByRole('region', { name: 'Pasada compartida' });
    expect(within(panel).getByText('Leyendo lotes')).toBeInTheDocument();
    expect(within(panel).getByText('1,50 $ / 25,00 $')).toBeInTheDocument();
    expect(within(panel).getByText('2 min 00 s / 1 h 30 min')).toBeInTheDocument();
    const batches = within(within(panel).getByRole('list', { name: 'Lotes' })).getAllByRole('listitem');
    expect(batches.map((li) => li.textContent)).toEqual([expect.stringMatching(/^core.*Completo.*5 hallazgos/), expect.stringMatching(/^billing.*Reintentando/)]);
    expect(within(panel).getAllByRole('progressbar')).toHaveLength(4);
  });

  it('al terminar con corte: motivo y registros por severidad', () => {
    const pass = replay([reading, finished]).sharedPass;
    render(<SharedPassPanel pass={pass} running={false} now={0} />);

    expect(screen.getByText('Terminada con corte')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('tope de coste de la ejecución alcanzado');
    const severities = screen.getByRole('list', { name: 'Lotes' }).parentElement!.querySelector('.sharedpass__severities')!;
    expect(severities.textContent).toContain('P12');
    expect(severities.textContent).toContain('Registros6 (1 rechazados)');
    expect(screen.getByText('No leído')).toBeInTheDocument();
  });
});
