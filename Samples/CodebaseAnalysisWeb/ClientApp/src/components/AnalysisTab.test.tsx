import { render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { AnalysisEventDto, WorkspaceDto } from '../api/types';
import type { AnalysisRunApi } from '../hooks/useAnalysisRun';
import { createRunState, runReducer, type RunState } from '../state/runReducer';
import { WorkspaceContextProvider } from '../state/workspace';
import { cancelledDuringReports, meta, regenerationAfter, runningStatus, successfulRun } from '../test/fixtures';
import { AnalysisTab } from './AnalysisTab';

const replay = (events: AnalysisEventDto[]): RunState =>
  runReducer(createRunState(runningStatus), { type: 'events', events });

const workspace: WorkspaceDto = {
  repositoryPath: runningStatus.repositoryPath,
  databasePath: null,
  reportsPath: null,
  chatPath: null,
  logsPath: null,
  hasGraph: true,
  reportCount: 0,
  analysisRunning: false,
  documentsPath: null,
  documentCount: 0,
  customReportCount: 0,
};

function renderTab(state: RunState, openWorkspace: WorkspaceDto = workspace) {
  const run: AnalysisRunApi = {
    state,
    connection: 'closed',
    error: null,
    starting: false,
    cancelling: false,
    regenerating: null,
    regenerateError: null,
    start: vi.fn(),
    cancel: vi.fn(),
    regenerate: vi.fn(),
    clearError: vi.fn(),
  };
  render(
    <WorkspaceContextProvider
      value={{ workspace: openWorkspace, meta, loading: false, loadError: null, recents: [], open: vi.fn(), refresh: vi.fn() }}
    >
      <AnalysisTab run={run} onOpenReports={vi.fn()} />
    </WorkspaceContextProvider>,
  );
}

const banner = (title: string) => screen.getByText(title).closest('.banner')!;

describe('AnalysisTab: aviso de fin de la ejecución', () => {
  it('tras cancelar dos informes y regenerar uno, el aviso es de fallos y lista el que falta', () => {
    // Como el servidor: la cancelación deja failedReports vacío y la regeneración lista el que sigue sin informe.
    const cancelled = cancelledDuringReports.map((e) =>
      e.type === 'run.completed' ? { ...e, data: { ...e.data, failedReports: [] } } : e,
    );
    const regeneration = regenerationAfter(cancelled, ['CodeSmells'], { failedReports: ['09_inyeccion_dependencias.md'] });

    renderTab(replay([...cancelled, ...regeneration]));

    const notice = banner('Análisis terminado con fallos');
    expect(notice).toHaveClass('banner--warning');
    expect(within(notice as HTMLElement).getByText('Informes sin generar: 09_inyeccion_dependencias.md.')).toBeInTheDocument();
    expect(screen.queryByText('Análisis terminado')).not.toBeInTheDocument();
    // El carril que falta conserva su botón.
    expect(screen.getByRole('button', { name: 'Regenerar Inyección de dependencias' })).toBeEnabled();
  });

  it('un tipo en failedReports se muestra con su fichero (R8)', () => {
    const last = successfulRun[successfulRun.length - 1];
    renderTab(
      replay([
        ...successfulRun.slice(0, -1),
        { ...last, data: { ...last.data, status: 'completed_with_failures', exitCode: 1, failedReports: ['InyeccionDependenciasExtensiones'] } },
      ]),
    );

    expect(screen.getByText('Informes sin generar: 09_inyeccion_dependencias.md.')).toBeInTheDocument();
  });

  it('sin informes pendientes el aviso es de éxito', () => {
    renderTab(replay(successfulRun));

    expect(banner('Análisis terminado')).toHaveClass('banner--success');
    expect(screen.queryByText(/Informes sin generar/)).not.toBeInTheDocument();
  });
});

describe('AnalysisTab: ejecución de otro repositorio (H-7)', () => {
  it('no ofrece «Regenerar» si la ejecución es de otro repositorio que el abierto', () => {
    const other: WorkspaceDto = { ...workspace, repositoryPath: 'C:\\otro\\repositorio' };

    renderTab(replay(cancelledDuringReports), other);

    expect(screen.getByText(/no del repositorio abierto/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Regenerar / })).not.toBeInTheDocument();
  });

  it('con el mismo repositorio, aunque cambie la caja, sí lo ofrece', () => {
    const same: WorkspaceDto = { ...workspace, repositoryPath: runningStatus.repositoryPath.toUpperCase() };

    renderTab(replay(cancelledDuringReports), same);

    expect(screen.queryByText(/no del repositorio abierto/)).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /^Regenerar / }).length).toBeGreaterThan(0);
  });
});
