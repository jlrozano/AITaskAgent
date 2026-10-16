import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { cancelledDuringReports, meta, regenerationAfter, runningStatus, successfulRun, verificationAfter } from '../test/fixtures';
import { preparedRun, preparedRunStatus } from '../test/preparationFixtures';
import { createRunState, runReducer, type RunState } from '../state/runReducer';
import type { AnalysisEventDto } from '../api/types';
import { AuditorGrid } from './AuditorGrid';

const replay = (events: AnalysisEventDto[], base: RunState = createRunState(runningStatus)): RunState =>
  runReducer(base, { type: 'events', events });

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

  it('D1: los carriles correctos también ofrecen «Regenerar» (y su casilla), para rehacer un informe sano', async () => {
    const onRegenerate = vi.fn();
    const user = userEvent.setup();
    render(<AuditorGrid state={replay(successfulRun)} meta={meta} now={now} onRegenerate={onRegenerate} />);

    expect(screen.getByLabelText('Actualizar también el resumen ejecutivo')).toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Regenerar Code smells' }));
    expect(onRegenerate).toHaveBeenCalledWith(['CodeSmells'], true);
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

describe('AuditorGrid: verificar un informe a demanda', () => {
  const done = replay(successfulRun);

  it('pinta «Verificar» solo en carriles terminados bien y de una ejecución terminada, y pide ese informe sin resumen por defecto', async () => {
    const onVerify = vi.fn();
    const user = userEvent.setup();
    render(<AuditorGrid state={done} meta={meta} now={now} onVerify={onVerify} />);

    expect(within(lane('Code smells')).getByRole('button', { name: 'Verificar Code smells' })).toBeEnabled();
    expect(within(lane('Inyección de dependencias')).getByRole('button', { name: 'Verificar Inyección de dependencias' })).toBeEnabled();
    const synthesis = screen.getByLabelText('Regenerar el resumen ejecutivo tras verificar');
    expect(synthesis).not.toBeChecked();

    await user.click(screen.getByRole('button', { name: 'Verificar Code smells' }));
    expect(onVerify).toHaveBeenCalledWith(['CodeSmells'], false);

    await user.click(synthesis);
    await user.click(screen.getByRole('button', { name: 'Verificar Inyección de dependencias' }));
    expect(onVerify).toHaveBeenLastCalledWith(['InyeccionDependenciasExtensiones'], true);
  });

  it('no hay botón en un carril fallido o cancelado, con la ejecución en curso ni sin onVerify', () => {
    render(<AuditorGrid state={replay(cancelledDuringReports)} meta={meta} now={now} onVerify={vi.fn()} />);
    expect(screen.queryByRole('button', { name: /Verificar/ })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Regenerar el resumen ejecutivo tras verificar')).not.toBeInTheDocument();

    const running = replay(verificationAfter(successfulRun, ['CodeSmells']).slice(0, 1), done);
    render(<AuditorGrid state={running} meta={meta} now={now} onVerify={vi.fn()} />);
    expect(screen.queryByRole('button', { name: /Verificar/ })).not.toBeInTheDocument();

    render(<AuditorGrid state={done} meta={meta} now={now} />);
    expect(screen.queryByRole('button', { name: /Verificar/ })).not.toBeInTheDocument();
  });

  it('mientras arranca la verificación (o la regeneración) los botones y las casillas se desactivan', () => {
    render(<AuditorGrid state={done} meta={meta} now={now} onVerify={vi.fn()} onRegenerate={vi.fn()} verifying={['CodeSmells']} />);

    expect(screen.getByRole('button', { name: 'Verificar Code smells' })).toHaveTextContent('Verificando…');
    expect(screen.getByRole('button', { name: 'Verificar Code smells' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Verificar Inyección de dependencias' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Regenerar Code smells' })).toBeDisabled();
    expect(screen.getByLabelText('Regenerar el resumen ejecutivo tras verificar')).toBeDisabled();
  });

  it('muestra el error del servidor en el carril del informe pedido', () => {
    render(
      <AuditorGrid
        state={done}
        meta={meta}
        now={now}
        onVerify={vi.fn()}
        verifyError={{ reports: ['CodeSmells'], message: 'Hay una respuesta del chat en curso.' }}
      />,
    );
    expect(within(lane('Code smells')).getByRole('alert')).toHaveTextContent('Hay una respuesta del chat en curso.');
    expect(within(lane('Inyección de dependencias')).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('tras corregir muestra «corregido (n)», «Deshacer verificación» y el aviso de que el resumen puede estar desfasado', async () => {
    const onUndo = vi.fn();
    const user = userEvent.setup();
    const state = replay(verificationAfter(successfulRun, ['CodeSmells']), done);
    render(<AuditorGrid state={state} meta={meta} now={now} onVerify={vi.fn()} onUndoVerification={onUndo} />);

    expect(within(lane('Code smells')).getByText(/Verificado: 2 corrección\(es\)/)).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('El resumen ejecutivo puede estar desfasado');
    expect(within(lane('Inyección de dependencias')).queryByRole('button', { name: /Deshacer/ })).not.toBeInTheDocument();

    await user.click(within(lane('Code smells')).getByRole('button', { name: 'Deshacer verificación de Code smells' }));
    expect(onUndo).toHaveBeenCalledWith('CodeSmells');
  });

  it('sin correcciones: «verificado», sin deshacer y sin aviso de desfase', () => {
    const events = verificationAfter(successfulRun, ['CodeSmells'], { outcomes: { CodeSmells: { outcome: 'verified', changes: 0 } } });
    render(<AuditorGrid state={replay(events, done)} meta={meta} now={now} onVerify={vi.fn()} onUndoVerification={vi.fn()} />);

    expect(within(lane('Code smells')).getByText('Verificado: sin correcciones.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Deshacer/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('con avisos de validación o con fallo del verificador lo dice en el carril', () => {
    const warnings = verificationAfter(successfulRun, ['CodeSmells'], {
      outcomes: { CodeSmells: { outcome: 'warnings', changes: 1, previousVersionId: 'v0001', versionId: 'v0002' } },
    });
    render(<AuditorGrid state={replay(warnings, done)} meta={meta} now={now} onVerify={vi.fn()} />);
    expect(within(lane('Code smells')).getByText(/quedan avisos de validación/)).toBeInTheDocument();

    const failed = verificationAfter(successfulRun, ['InyeccionDependenciasExtensiones'], {
      outcomes: { InyeccionDependenciasExtensiones: { outcome: 'failed', error: 'El verificador falló: HTTP 500' } },
    });
    render(<AuditorGrid state={replay(failed, done)} meta={meta} now={now} onVerify={vi.fn()} />);
    expect(screen.getAllByText(/No se pudo verificar: El verificador falló: HTTP 500/)[0]).toBeInTheDocument();
  });
});

describe('AuditorGrid: informes en modo B con investigaciones por subsistema', () => {
  const prepared = (events: AnalysisEventDto[]) => replay(events, createRunState(preparedRunStatus));
  const lanes = () => screen.getAllByRole('listitem').filter((li) => li.classList.contains('lane'));
  const toolCount = (li: HTMLElement) => within(li).getByText('Tools').closest('div')!.querySelector('dd')!.textContent;

  it('muestra un único carril por informe aunque cada uno lance una investigación por subsistema', () => {
    const toolStarted = preparedRun.findIndex((e) => e.type === 'tool.started' && e.source === 'Investigation_AnalisisFuncional_catalog');
    render(<AuditorGrid state={prepared(preparedRun.slice(0, toolStarted + 1))} meta={meta} now={now} />);

    expect(lanes()).toHaveLength(2);
    // La tool de la investigación se ve en el carril de su informe, con el informe aún «trabajando».
    const functional = lane('Análisis funcional');
    expect(within(functional).getByText(/Trabajando/)).toHaveTextContent('Trabajando · view_file');
    expect(toolCount(functional)).toBe('1');
    expect(toolCount(lane('FlujosEndToEnd'))).toBe('0');
    expect(functional.querySelectorAll('.lane__tick')).toHaveLength(1);
  });

  it('con las investigaciones hechas cada carril suma las tools de las suyas y, al terminar, ofrece regenerar y verificar el informe', () => {
    const consolidating = preparedRun.findIndex((e) => e.type === 'llm.response' && e.source === 'Auditor_AnalisisFuncional');
    const { unmount } = render(<AuditorGrid state={prepared(preparedRun.slice(0, consolidating))} meta={meta} now={now} />);
    expect(lanes()).toHaveLength(2);
    expect(toolCount(lane('Análisis funcional'))).toBe('2');
    expect(toolCount(lane('FlujosEndToEnd'))).toBe('2');
    unmount();

    render(<AuditorGrid state={prepared(preparedRun)} meta={meta} now={now} onRegenerate={vi.fn()} onVerify={vi.fn()} />);
    expect(lanes()).toHaveLength(2);
    expect(within(lane('Análisis funcional')).getByText('Terminado')).toBeInTheDocument();
    expect(within(lane('Análisis funcional')).getByRole('button', { name: 'Regenerar Análisis funcional' })).toBeEnabled();
    expect(within(lane('FlujosEndToEnd')).getByRole('button', { name: 'Verificar FlujosEndToEnd' })).toBeEnabled();
  });

  it('durante la preparación (antes de los informes) los carriles piden «En espera» sin errores', () => {
    const preparing = preparedRun.findIndex((e) => e.type === 'step.started' && e.source === 'DomainDiscoveryStep');
    render(<AuditorGrid state={prepared(preparedRun.slice(0, preparing + 1))} meta={meta} now={now} />);
    expect(lanes()).toHaveLength(2);
    expect(screen.getAllByText('En espera')).toHaveLength(2);
  });
});
