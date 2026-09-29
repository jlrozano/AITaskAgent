import type { AnalysisRunApi } from '../hooks/useAnalysisRun';
import { useNow } from '../hooks/useNow';
import { failedReportFileNames } from '../state/runReducer';
import { useWorkspace } from '../state/workspace';
import { AnalysisLauncher } from './AnalysisLauncher';
import { AuditorGrid } from './AuditorGrid';
import { EventLog } from './EventLog';
import { PhaseStepper } from './PhaseStepper';
import { ProgressSummary, RUN_STATUS_LABELS } from './ProgressSummary';
import { StatusBanner } from './StatusBanner';

interface Props {
  run: AnalysisRunApi;
  onOpenReports: () => void;
  /**
   * Hay un turno de chat en curso en este cliente. El servidor responde 409 `chat_turn_running` si se lanza: se
   * bloquea antes, igual que el chat se bloquea durante un análisis. Un turno de otra pestaña solo se ve por el 409.
   */
  chatBusy?: boolean;
}

/** Título y tono del aviso de un error al lanzar o cancelar. */
function errorBanner(code: string): { tone: 'warning' | 'error'; title: string } {
  if (code === 'analysis_running') return { tone: 'warning', title: 'Ya hay un análisis en curso' };
  if (code === 'chat_turn_running') return { tone: 'warning', title: 'El chat está respondiendo' };
  if (code === 'invalid_request') return { tone: 'error', title: 'Petición rechazada por el servidor' };
  return { tone: 'error', title: 'No se pudo completar la acción' };
}

export function AnalysisTab({ run, onOpenReports, chatBusy = false }: Props) {
  const { workspace, meta } = useWorkspace();
  const { state } = run;
  const running = state.status === 'running';
  const now = useNow(running);
  const blockedReason = !workspace?.repositoryPath
    ? 'Abre un repositorio en la barra superior para poder lanzar el análisis.'
    : chatBusy && !running
      ? 'El chat está respondiendo. Podrás lanzar el análisis cuando termine la respuesta o si la paras.'
      : null;
  const otherRepo =
    state.repositoryPath && workspace?.repositoryPath && state.repositoryPath.toLowerCase() !== workspace.repositoryPath.toLowerCase();

  return (
    <div className="analysis">
      <aside className="analysis__side">
        <AnalysisLauncher
          meta={meta}
          blockedReason={blockedReason}
          running={running}
          starting={run.starting}
          cancelling={run.cancelling}
          onStart={(req) => void run.start(req)}
          onCancel={() => void run.cancel()}
        />
      </aside>

      <div className="analysis__main">
        {run.error && (
          <StatusBanner
            {...errorBanner(run.error.code)}
            onDismiss={run.clearError}
          >
            {run.error.message}
          </StatusBanner>
        )}
        {run.connection === 'failed' && (
          <StatusBanner tone="error" title="Se ha perdido la ejecución">
            El servidor ya no tiene esta ejecución (se ha reiniciado o empezó otra). Los informes escritos siguen en disco.
          </StatusBanner>
        )}

        {!state.runId ? (
          <div className="empty">
            <h2>Sin ejecuciones desde que arrancó el servidor</h2>
            <p>
              Elige los informes a la izquierda y pulsa «Lanzar análisis». El progreso aparece aquí en directo y puedes
              cerrar la pestaña: el análisis sigue en el servidor.
            </p>
          </div>
        ) : (
          <>
            {otherRepo && (
              <StatusBanner tone="info">
                Esta ejecución es de <code>{state.repositoryPath}</code>, no del repositorio abierto.
              </StatusBanner>
            )}
            <ProgressSummary state={state} connection={run.connection} now={now} />
            {state.status && state.status !== 'running' && (
              <StatusBanner
                // Con fallos es aviso y no error: la ejecución terminó y lo que falta se puede regenerar.
                tone={state.status === 'succeeded' ? 'success' : state.status === 'failed' ? 'error' : 'warning'}
                title={`Análisis ${RUN_STATUS_LABELS[state.status].toLowerCase()}`}
                action={
                  <button type="button" className="btn btn--small" onClick={onOpenReports}>
                    Ver informes
                  </button>
                }
              >
                {state.error && <span>{state.error} </span>}
                {state.failedReports.length > 0 && (
                  <span>Informes sin generar: {failedReportFileNames(state.failedReports).join(', ')}.</span>
                )}
              </StatusBanner>
            )}
            <PhaseStepper state={state} now={now} />
            <AuditorGrid
              state={state}
              meta={meta}
              now={now}
              // Si el servidor ya no tiene la ejecución (reinicio, R7), no hay nada que regenerar. Si es de otro repositorio,
              // tampoco: regenerarla reapuntaría las herramientas del chat a ese repositorio (el servidor da 409
              // `run_from_other_workspace`, que el carril muestra si llega por otra pestaña).
              onRegenerate={
                run.connection === 'failed' || otherRepo ? undefined : (reports, synthesis) => void run.regenerate(reports, synthesis)
              }
              regenerating={run.regenerating}
              regenerateError={run.regenerateError}
            />
            <EventLog entries={state.log} droppedUpToSeq={state.droppedUpToSeq} />
          </>
        )}
      </div>
    </div>
  );
}
