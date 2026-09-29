import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { cancelledDuringReports, meta, regenerationAfter, runningStatus, successfulRun } from '../test/fixtures';
import { createRunState, runReducer, type RunState } from '../state/runReducer';
import type { AnalysisEventDto } from '../api/types';
import { AuditorGrid } from './AuditorGrid';

const replay = (events: AnalysisEventDto[]): RunState =>
  runReducer(createRunState(runningStatus), { type: 'events', events });

const now = Date.parse('2026-09-27T10:30:00Z');
const lane = (title: string) => screen.getByText(title).closest('li')!;

describe('AuditorGrid: regenerar un informe (R5, R6)', () => {
  it('pinta «Regenerar» en los carriles fallidos o cancelados de una ejecución terminada y pide ese informe con síntesis', async () => {
    const onRegenerate = vi.fn();
    const user = userEvent.setup();
    render(<AuditorGrid state={replay(cancelledDuringReports)} meta={meta} now={now} onRegenerate={onRegenerate} />);

    // CodeSmells falló (HTTP 429) y DI se canceló: los dos se pueden regenerar.
    expect(within(lane('Code smells')).getByRole('button', { name: 'Regenerar Code smells' })).toBeEnabled();
    expect(within(lane('Inyección de dependencias')).getByRole('button', { name: 'Regenerar Inyección de dependencias' })).toBeEnabled();
    const synthesis = screen.getByLabelText('Actualizar también el resumen ejecutivo');
    expect(synthesis).toBeChecked();

    await user.click(screen.getByRole('button', { name: 'Regenerar Code smells' }));
    expect(onRegenerate).toHaveBeenCalledWith(['CodeSmells'], true);

    await user.click(synthesis);
    await user.click(screen.getByRole('button', { name: 'Regenerar Inyección de dependencias' }));
    expect(onRegenerate).toHaveBeenLastCalledWith(['InyeccionDependenciasExtensiones'], false);
  });

  it('sin carriles fallidos, ni botón ni casilla', () => {
    render(<AuditorGrid state={replay(successfulRun)} meta={meta} now={now} onRegenerate={vi.fn()} />);
    expect(screen.queryByRole('button', { name: /Regenerar/ })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Actualizar también el resumen ejecutivo')).not.toBeInTheDocument();
  });

  it('con una ejecución en curso no hay botón (criterio 4)', () => {
    const regeneration = regenerationAfter(cancelledDuringReports, ['CodeSmells']);
    const running = replay([...cancelledDuringReports, regeneration[0]]);
    render(<AuditorGrid state={running} meta={meta} now={now} onRegenerate={vi.fn()} />);
    expect(screen.queryByRole('button', { name: /Regenerar/ })).not.toBeInTheDocument();
  });

  it('sin onRegenerate (el servidor ya no tiene la ejecución) no hay botón', () => {
    render(<AuditorGrid state={replay(cancelledDuringReports)} meta={meta} now={now} />);
    expect(screen.queryByRole('button', { name: /Regenerar/ })).not.toBeInTheDocument();
  });

  it('mientras arranca la regeneración los botones y la casilla se desactivan', () => {
    render(
      <AuditorGrid state={replay(cancelledDuringReports)} meta={meta} now={now} onRegenerate={vi.fn()} regenerating={['CodeSmells']} />,
    );
    expect(within(lane('Code smells')).getByRole('button', { name: 'Regenerar Code smells' })).toBeDisabled();
    expect(within(lane('Code smells')).getByRole('button', { name: 'Regenerar Code smells' })).toHaveTextContent('Regenerando…');
    expect(screen.getByRole('button', { name: 'Regenerar Inyección de dependencias' })).toBeDisabled();
    expect(screen.getByLabelText('Actualizar también el resumen ejecutivo')).toBeDisabled();
  });

  it('muestra el error del servidor en el carril donde se pulsó', () => {
    render(
      <AuditorGrid
        state={replay(cancelledDuringReports)}
        meta={meta}
        now={now}
        onRegenerate={vi.fn()}
        regenerateError={{ reports: ['CodeSmells'], message: 'El chat está respondiendo.' }}
      />,
    );
    expect(within(lane('Code smells')).getByRole('alert')).toHaveTextContent('El chat está respondiendo.');
    expect(within(lane('Inyección de dependencias')).queryByRole('alert')).not.toBeInTheDocument();
  });
});
