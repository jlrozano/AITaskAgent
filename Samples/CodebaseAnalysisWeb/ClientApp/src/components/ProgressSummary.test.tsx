import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { AnalysisEventDto } from '../api/types';
import { createRunState, runReducer, type RunState } from '../state/runReducer';
import { runningStatus, successfulRun } from '../test/fixtures';
import { ProgressSummary } from './ProgressSummary';

const replay = (events: AnalysisEventDto[]): RunState =>
  runReducer(createRunState(runningStatus), { type: 'events', events });

const now = Date.parse('2026-09-27T10:30:00Z');

/** El `run.completed` de `successfulRun` con otro `usage`. */
function withUsage(usage: Record<string, unknown> | undefined): AnalysisEventDto[] {
  return successfulRun.map((e) => (e.type === 'run.completed' ? { ...e, data: { ...e.data, usage } } : e));
}

describe('ProgressSummary: consumo LLM', () => {
  it('muestra los tokens con el reparto y no muestra coste si ninguna llamada tiene precio', () => {
    render(<ProgressSummary state={replay(successfulRun)} connection="closed" now={now} />);

    expect(screen.getByText('Tokens')).toBeInTheDocument();
    expect(screen.getByText('900')).toBeInTheDocument();
    expect(screen.getByText('800 de entrada · 100 de salida · 1 llamada')).toBeInTheDocument();
    expect(screen.queryByText('Coste')).not.toBeInTheDocument();
  });

  it('con precio muestra el coste, y lo marca como mínimo si hay llamadas sin precio', () => {
    const priced = { calls: 3, promptTokens: 1_500_000, completionTokens: 20_000, totalTokens: 1_520_000, costUsd: 4.2, unpricedCalls: 0 };
    const { unmount } = render(<ProgressSummary state={replay(withUsage(priced))} connection="closed" now={now} />);
    expect(screen.getByText('Coste')).toBeInTheDocument();
    expect(screen.getByText('4,20 $')).toBeInTheDocument();
    expect(screen.getByText('1.500.000 de entrada · 20.000 de salida · 3 llamadas')).toBeInTheDocument();
    unmount();

    render(<ProgressSummary state={replay(withUsage({ ...priced, unpricedCalls: 1 }))} connection="closed" now={now} />);
    expect(screen.getByText('4,20 $ (mínimo)')).toBeInTheDocument();
  });

  it('sin llamadas al modelo no hay celda de tokens', () => {
    render(<ProgressSummary state={createRunState(runningStatus)} connection="open" now={now} />);
    expect(screen.queryByText('Tokens')).not.toBeInTheDocument();
  });
});

/** El `run.completed` de `successfulRun` con un `validation`. */
function withValidation(validation: Record<string, unknown> | undefined): AnalysisEventDto[] {
  return successfulRun.map((e) => (e.type === 'run.completed' ? { ...e, data: { ...e.data, validation } } : e));
}

describe('ProgressSummary: validación de informes', () => {
  it('muestra las citas válidas y avisa si hay rotas o P0/P1 sin ubicación', () => {
    const validation = { reports: 9, citationsTotal: 164, citationsValid: 160, brokenCitations: 4, severeFindingsWithoutLocation: 1, errors: 5, warnings: 2 };
    render(<ProgressSummary state={replay(withValidation(validation))} connection="closed" now={now} />);

    expect(screen.getByText('Citas válidas')).toBeInTheDocument();
    const value = screen.getByText('160/164');
    expect(value).toHaveClass('text-warning');
    expect(screen.getByText('4 rotas · 1 P0/P1 sin ubicación · 9 informes')).toBeInTheDocument();
  });

  it('sin validación en run.completed no hay celda', () => {
    render(<ProgressSummary state={replay(withValidation(undefined))} connection="closed" now={now} />);
    expect(screen.queryByText('Citas válidas')).not.toBeInTheDocument();
  });
});
