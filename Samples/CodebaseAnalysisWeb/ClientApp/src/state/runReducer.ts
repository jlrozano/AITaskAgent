// Estado de una ejecución de análisis derivado de sus eventos (plan §4.3, §7, D11). Función pura: el mismo
// replay produce siempre el mismo estado, y los eventos repetidos (seq <= lastSeq) se ignoran, de modo que una
// reconexión que solape eventos no altera nada.

import type { AnalysisEventDto, EventLevel, Health, Phase, ReportType, RunStatus, RunStatusDto } from '../api/types';

export const PHASES: readonly Phase[] = ['detect', 'enrich', 'facets', 'reports', 'synthesis'];

export const PHASE_LABELS: Record<Phase, string> = {
  detect: 'Detección e indexado',
  enrich: 'Enriquecimiento',
  facets: 'Facetas SQL',
  reports: 'Auditores',
  synthesis: 'Resumen ejecutivo',
};

/**
 * Steps de primer nivel de cada fase (§4.3), en el orden en que se ejecutan. `enrich` tiene dos: el enriquecimiento
 * nativo por lenguaje y `BdpEnricherStep` (P16). `facets` también, pero `PrepareRegenerationStep` solo corre al
 * regenerar informes, en lugar de la detección. El `step.started` de cualquiera abre la fase; solo el `step.completed`
 * correcto del último la cierra, y el fallo de cualquiera la marca fallida.
 */
export const PHASE_STEPS: Record<string, Phase> = {
  DetectAndIndexGraphStep: 'detect',
  LanguageEnricherStep: 'enrich',
  BdpEnricherStep: 'enrich',
  PrepareRegenerationStep: 'facets',
  SqlFacetExtractorStep: 'facets',
  ParallelReportsExecution: 'reports',
  ExecutiveSynthesizerStep: 'synthesis',
};

/** Último step de primer nivel de cada fase: el que la da por terminada. */
export const PHASE_LAST_STEP: Partial<Record<Phase, string>> = Object.entries(PHASE_STEPS).reduce<Partial<Record<Phase, string>>>(
  (last, [step, phase]) => ({ ...last, [phase]: step }),
  {},
);

/** Fases con más de un step de primer nivel: su duración va del primer evento de la fase al cierre. */
const MULTI_STEP_PHASES: ReadonlySet<Phase> = new Set(
  PHASES.filter((phase) => Object.values(PHASE_STEPS).filter((p) => p === phase).length > 1),
);

export const AUDITOR_PREFIX = 'Auditor_';

/**
 * Fichero de cada informe del catálogo (`AnalysisPipelineInput.GetReportFileName` del servidor). `failedReports` de
 * `run.completed` trae ficheros y los carriles son tipos: sin esta tabla, la marca del servidor no casaba nunca (R8).
 */
export const REPORT_FILE_NAMES: Record<ReportType, string> = {
  AnalisisFuncional: '01_analisis_funcional.md',
  CodeSmells: '02_code_smells.md',
  CodigoMuerto: '03_codigo_muerto.md',
  FlujosEndToEnd: '04_flujos_end_to_end.md',
  ErroresYBugs: '05_errores_y_bugs.md',
  ArquitecturaAcoplamiento: '06_arquitectura_acoplamiento.md',
  InventarioApisSeguridad: '07_inventario_apis_seguridad.md',
  DeudaTecnicaHotspots: '08_deuda_tecnica_hotspots.md',
  InyeccionDependenciasExtensiones: '09_inyeccion_dependencias.md',
};

/** Si `failedReports` incluye el informe de ese carril, por tipo o por nombre de fichero. */
export function isFailedReport(failedReports: readonly string[], report: string): boolean {
  const fileName = (REPORT_FILE_NAMES as Record<string, string | undefined>)[report];
  return failedReports.includes(report) || (fileName !== undefined && failedReports.includes(fileName));
}

/**
 * `failedReports` como ficheros, para mostrarlo: el servidor ya manda ficheros, pero un tipo se traduce con la misma tabla
 * (R8) para que la lista no mezcle las dos formas. Sin repetidos y en el orden recibido.
 */
export function failedReportFileNames(failedReports: readonly string[]): string[] {
  const files = failedReports.map((r) => (REPORT_FILE_NAMES as Record<string, string | undefined>)[r] ?? r);
  return [...new Set(files)];
}

/** Fases que la regeneración vuelve a ejecutar; `synthesis`, además, solo si se pidió. */
const REGENERATED_PHASES: readonly Phase[] = ['facets', 'reports'];

export type PhaseStatus = 'pending' | 'running' | 'succeeded' | 'failed' | 'skipped' | 'cancelled';
export type AuditorStatus = 'pending' | 'running' | 'succeeded' | 'failed' | 'cancelled' | 'skipped';

export interface PhaseState {
  status: PhaseStatus;
  startedAt: string | null;
  finishedAt: string | null;
  durationMs: number | null;
  /** `data.error` del `step.completed` fallido del step de primer nivel. */
  error: string | null;
}

export interface AuditorState {
  report: string;
  status: AuditorStatus;
  startedAt: string | null;
  finishedAt: string | null;
  durationMs: number | null;
  /** Número de reintentos (attempt - 1 del último `step.started`). */
  retries: number;
  toolCalls: number;
  toolErrors: number;
  /** Instantes (ISO) de cada `tool.started`, para dibujar el carril. */
  toolTimes: string[];
  activeTool: string | null;
  tokens: number | null;
  costUsd: number | null;
  finishReason: string | null;
  health: Health | null;
  criticalFindings: number | null;
  truncated: boolean;
  fileName: string | null;
  error: string | null;
}

export interface LogEntry {
  seq: number;
  ts: string;
  type: string;
  level: EventLevel;
  source: string | null;
  report: string | null;
  phase: Phase | null;
  message: string;
  /** Texto adicional: argumento principal de una tool (`src/X.cs`), canal del log... */
  detail: string | null;
  channel: string | null;
  /** `data.tool`, `data.success` y `data.durationMs` de los eventos `tool.*`. */
  tool: string | null;
  success: boolean | null;
  durationMs: number | null;
}

/**
 * Consumo LLM de la ejecución: se suma en vivo de los `llm.response` que traen el reparto de tokens (uno por llamada) y
 * el `usage` de `run.completed`, que es el total del servidor (`LlmUsageTracker`), lo sustituye. `costUsd` es null si
 * ninguna llamada tiene precio; con `unpricedCalls > 0` es un mínimo.
 */
export interface RunUsage {
  calls: number;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  costUsd: number | null;
  unpricedCalls: number;
}

export interface RunState {
  runId: string | null;
  repositoryPath: string | null;
  status: RunStatus | null;
  startedAt: string | null;
  finishedAt: string | null;
  forceReindex: boolean;
  requestedReports: string[];
  lastSeq: number;
  phases: Record<Phase, PhaseState>;
  auditors: Record<string, AuditorState>;
  auditorOrder: string[];
  log: LogEntry[];
  warnings: number;
  errors: number;
  /**
   * Eventos `level=error` cuyo error es el de una cancelación (`isCancellationError`). Cuentan en `errors` hasta que
   * `run.completed` dice `cancelled`; entonces se descuentan, porque no son fallos sino el efecto de «Cancelar».
   */
  cancellationErrors: number;
  failedReports: string[];
  error: string | null;
  exitCode: number | null;
  durationMs: number | null;
  executiveReportPath: string | null;
  /** Hasta qué seq descartó eventos el servidor (`log.truncated`). */
  droppedUpToSeq: number | null;
  /** Cambia cada vez que se escribe un informe o termina la ejecución: la lista de informes se refresca. */
  reportsVersion: number;
  /** Argumentos pendientes de emparejar por step: se toman del `llm.response` y se asignan en orden (§3.3). */
  pendingToolArgs: Record<string, { name: string; arguments: string }[]>;
  /** Null hasta la primera llamada con reparto de tokens o el `usage` de un `run.completed`. */
  usage: RunUsage | null;
}

export type RunAction =
  | { type: 'reset'; status: RunStatusDto | null }
  | { type: 'status'; status: RunStatusDto }
  | { type: 'event'; event: AnalysisEventDto }
  | { type: 'events'; events: AnalysisEventDto[] };

function emptyPhases(): Record<Phase, PhaseState> {
  const phases = {} as Record<Phase, PhaseState>;
  for (const phase of PHASES) phases[phase] = { status: 'pending', startedAt: null, finishedAt: null, durationMs: null, error: null };
  return phases;
}

function newAuditor(report: string): AuditorState {
  return {
    report,
    status: 'pending',
    startedAt: null,
    finishedAt: null,
    durationMs: null,
    retries: 0,
    toolCalls: 0,
    toolErrors: 0,
    toolTimes: [],
    activeTool: null,
    tokens: null,
    costUsd: null,
    finishReason: null,
    health: null,
    criticalFindings: null,
    truncated: false,
    fileName: null,
    error: null,
  };
}

export const initialRunState: RunState = createRunState(null);

export function createRunState(status: RunStatusDto | null): RunState {
  const state: RunState = {
    runId: status?.runId ?? null,
    repositoryPath: status?.repositoryPath ?? null,
    status: status?.status ?? null,
    startedAt: status?.startedAt ?? null,
    finishedAt: status?.finishedAt ?? null,
    forceReindex: status?.forceReindex ?? false,
    requestedReports: status?.requestedReports ?? [],
    lastSeq: 0,
    phases: emptyPhases(),
    auditors: {},
    auditorOrder: [],
    log: [],
    warnings: 0,
    errors: 0,
    cancellationErrors: 0,
    failedReports: status?.failedReports ?? [],
    error: status?.error ?? null,
    exitCode: null,
    durationMs: null,
    executiveReportPath: null,
    droppedUpToSeq: null,
    reportsVersion: 0,
    pendingToolArgs: {},
    usage: null,
  };
  for (const report of state.requestedReports) addAuditor(state, report);
  return state;
}

function addAuditor(state: RunState, report: string): AuditorState {
  let auditor = state.auditors[report];
  if (!auditor) {
    auditor = newAuditor(report);
    state.auditors = { ...state.auditors, [report]: auditor };
    state.auditorOrder = [...state.auditorOrder, report];
  }
  return auditor;
}

function updateAuditor(state: RunState, report: string, patch: (a: AuditorState) => Partial<AuditorState>): void {
  const current = addAuditor(state, report);
  state.auditors = { ...state.auditors, [report]: { ...current, ...patch(current) } };
}

function updatePhase(state: RunState, phase: Phase, patch: Partial<PhaseState>): void {
  state.phases = { ...state.phases, [phase]: { ...state.phases[phase], ...patch } };
}

/** `report` del evento o, si falta, el tipo sacado de `Auditor_<Tipo>` en `source` o en `data.branch`. */
export function auditorOf(event: AnalysisEventDto): string | null {
  if (event.report) return event.report;
  const candidates = [event.source, typeof event.data?.branch === 'string' ? event.data.branch : null];
  for (const name of candidates) {
    if (name && name.startsWith(AUDITOR_PREFIX) && name.length > AUDITOR_PREFIX.length) {
      return name.slice(AUDITOR_PREFIX.length);
    }
  }
  return null;
}

const str = (v: unknown): string | null => (typeof v === 'string' ? v : null);

/**
 * Error de una cancelación: el backend convierte la excepción en `ErrorStepResult` con su mensaje
 * (`RetryMiddleware.cs:53`), que es el de `TaskCanceledException` («A task was canceled.») u
 * `OperationCanceledException` («The operation was canceled.»). Solo se usa si la ejecución termina `cancelled`: en
 * otra ejecución ese mismo texto puede ser un timeout HTTP y es un fallo de verdad.
 */
export function isCancellationError(text: string | null | undefined): boolean {
  return !!text && /cancell?ed\b|cancelad[oa]\b|OperationCanceled|TaskCanceled/i.test(text);
}
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const bool = (v: unknown): boolean | null => (typeof v === 'boolean' ? v : null);

/** Suma una llamada (`llm.response` con `promptTokens` o `completionTokens`) al consumo; null si el evento no trae reparto. */
function addCall(usage: RunUsage | null, data: Record<string, unknown>): RunUsage | null {
  const prompt = num(data.promptTokens);
  const completion = num(data.completionTokens);
  if (prompt === null && completion === null) return usage;
  const cost = num(data.costUsd);
  const current = usage ?? { calls: 0, promptTokens: 0, completionTokens: 0, totalTokens: 0, costUsd: null, unpricedCalls: 0 };
  return {
    calls: current.calls + 1,
    promptTokens: current.promptTokens + (prompt ?? 0),
    completionTokens: current.completionTokens + (completion ?? 0),
    totalTokens: current.totalTokens + (prompt ?? 0) + (completion ?? 0),
    costUsd: cost === null ? current.costUsd : (current.costUsd ?? 0) + cost,
    unpricedCalls: current.unpricedCalls + (cost === null ? 1 : 0),
  };
}

/** El `usage` de `run.completed`; null si no viene o no tiene la forma esperada. */
function usageOf(value: unknown): RunUsage | null {
  if (!value || typeof value !== 'object') return null;
  const u = value as Record<string, unknown>;
  const calls = num(u.calls);
  if (calls === null) return null;
  return {
    calls,
    promptTokens: num(u.promptTokens) ?? 0,
    completionTokens: num(u.completionTokens) ?? 0,
    totalTokens: num(u.totalTokens) ?? 0,
    costUsd: num(u.costUsd),
    unpricedCalls: num(u.unpricedCalls) ?? 0,
  };
}

/** Argumento principal de una tool para mostrarlo en una línea (`path`, `name_pattern`, `file_name`...). */
export function mainArgument(args: string | null | undefined): string | null {
  if (!args) return null;
  try {
    const parsed = JSON.parse(args) as unknown;
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      const values = Object.values(parsed as Record<string, unknown>);
      const first = values.find((v) => typeof v === 'string' || typeof v === 'number');
      if (first !== undefined) return String(first);
      return null;
    }
  } catch {
    // argumentos truncados a 500 caracteres (§4.3): no siempre son JSON válido
  }
  return args.length > 80 ? `${args.slice(0, 79)}…` : args;
}

function toLogEntry(event: AnalysisEventDto, detail: string | null): LogEntry {
  return {
    seq: event.seq,
    ts: event.ts,
    type: event.type,
    level: event.level,
    source: event.source,
    report: event.report,
    phase: event.phase,
    message: event.message,
    detail,
    channel: str(event.data?.channel),
    tool: str(event.data?.tool),
    success: bool(event.data?.success),
    durationMs: num(event.data?.durationMs),
  };
}

function markPhaseRunning(state: RunState, phase: Phase, ts: string): void {
  const index = PHASES.indexOf(phase);
  // Las fases son secuenciales: si arranca una posterior, las anteriores que no llegaron a arrancar se omitieron.
  for (let i = 0; i < index; i++) {
    const earlier = PHASES[i];
    if (state.phases[earlier].status === 'pending') updatePhase(state, earlier, { status: 'skipped' });
  }
  if (state.phases[phase].status === 'pending') updatePhase(state, phase, { status: 'running', startedAt: ts });
}

/**
 * Duración de la fase al cerrarse: la del step si la fase tiene uno solo y, si tiene varios, de `startedAt` a
 * `finishedAt` (P16), que cubre todos sus steps.
 */
function phaseDuration(phase: PhaseState, name: Phase, finishedAt: string, stepDurationMs: number | null): number | null {
  if (!MULTI_STEP_PHASES.has(name) || !phase.startedAt) return stepDurationMs;
  const elapsed = Date.parse(finishedAt) - Date.parse(phase.startedAt);
  return Number.isFinite(elapsed) ? Math.max(0, elapsed) : stepDurationMs;
}

/** Aplica un evento sobre `state` (una copia propia de `applyEvents`) y devuelve su fila del registro. */
function applyEvent(state: RunState, event: AnalysisEventDto): LogEntry {
  const data = event.data ?? {};
  const report = auditorOf(event);
  let detail: string | null = null;

  if (event.level === 'warning') state.warnings++;
  if (event.level === 'error') {
    state.errors++;
    if (isCancellationError(str(data.error) ?? event.message)) state.cancellationErrors++;
  }

  const topPhase = event.source ? PHASE_STEPS[event.source] : undefined;
  if (event.phase && event.type !== 'run.completed' && event.type !== 'run.started') {
    markPhaseRunning(state, event.phase, event.ts);
  }

  switch (event.type) {
    case 'run.started': {
      state.status = 'running';
      state.startedAt = state.startedAt ?? event.ts;
      state.repositoryPath = str(data.repositoryPath) ?? state.repositoryPath;
      state.forceReindex = bool(data.forceReindex) ?? state.forceReindex;
      const requested = Array.isArray(data.requestedReports) ? data.requestedReports.filter((r) => typeof r === 'string') : [];
      if (requested.length > 0) state.requestedReports = requested as string[];
      for (const r of state.requestedReports) addAuditor(state, r);
      break;
    }
    case 'run.regeneration_started': {
      // La misma ejecución vuelve a correr (R3): los carriles pedidos empiezan de cero y los demás se conservan.
      const reports = Array.isArray(data.reports) ? (data.reports.filter((r) => typeof r === 'string') as string[]) : [];
      state.status = 'running';
      state.finishedAt = null;
      state.error = null;
      state.exitCode = null;
      state.durationMs = null;
      for (const r of reports) {
        addAuditor(state, r);
        state.auditors = { ...state.auditors, [r]: newAuditor(r) };
      }
      const phases: readonly Phase[] = bool(data.synthesis) === false ? REGENERATED_PHASES : [...REGENERATED_PHASES, 'synthesis'];
      for (const phase of phases) {
        updatePhase(state, phase, { status: 'pending', startedAt: null, finishedAt: null, durationMs: null, error: null });
      }
      state.pendingToolArgs = {};
      break;
    }
    case 'step.started': {
      const attempt = num(data.attempt) ?? 1;
      // Un reintento reabre la fase; el step siguiente de una fase que ya falló no la da por buena.
      if (topPhase && (state.phases[topPhase].status !== 'failed' || attempt > 1)) {
        updatePhase(state, topPhase, { status: 'running', finishedAt: null });
      }
      if (report) {
        updateAuditor(state, report, (a) => ({
          status: 'running',
          startedAt: a.startedAt ?? event.ts,
          retries: Math.max(a.retries, attempt - 1),
          error: null,
        }));
      }
      break;
    }
    case 'step.completed': {
      const success = bool(data.success) ?? event.level !== 'error';
      // El éxito de un step que no es el último de su fase la deja en marcha: la fase no parpadea entre steps.
      const closesPhase = topPhase && (!success || (PHASE_LAST_STEP[topPhase] === event.source && state.phases[topPhase].status !== 'failed'));
      if (topPhase && closesPhase) {
        updatePhase(state, topPhase, {
          status: success ? 'succeeded' : 'failed',
          finishedAt: event.ts,
          durationMs: phaseDuration(state.phases[topPhase], topPhase, event.ts, num(data.durationMs)),
          error: success ? null : str(data.error) ?? event.message,
        });
      }
      if (report) {
        updateAuditor(state, report, (a) => ({
          status: success ? 'succeeded' : 'failed',
          finishedAt: event.ts,
          durationMs: num(data.durationMs) ?? a.durationMs,
          tokens: num(data.tokens) ?? a.tokens,
          costUsd: num(data.costUsd) ?? a.costUsd,
          finishReason: str(data.finishReason) ?? a.finishReason,
          error: success ? null : str(data.error) ?? a.error,
          activeTool: null,
        }));
      }
      break;
    }
    case 'branch.started': {
      if (report) updateAuditor(state, report, (a) => ({ status: 'running', startedAt: a.startedAt ?? event.ts }));
      break;
    }
    case 'branch.completed': {
      if (report) {
        const success = bool(data.success) ?? event.level !== 'error';
        updateAuditor(state, report, (a) => ({
          status: success ? 'succeeded' : 'failed',
          finishedAt: event.ts,
          durationMs: num(data.durationMs) ?? a.durationMs,
          error: success ? null : str(data.error) ?? a.error,
          activeTool: null,
        }));
      }
      break;
    }
    case 'llm.response': {
      const calls = Array.isArray(data.toolCalls) ? data.toolCalls : [];
      if (event.source && calls.length > 0) {
        const queue = [...(state.pendingToolArgs[event.source] ?? [])];
        for (const call of calls) {
          const c = call as Record<string, unknown>;
          const name = str(c.name);
          if (name) queue.push({ name, arguments: str(c.arguments) ?? '' });
        }
        state.pendingToolArgs = { ...state.pendingToolArgs, [event.source]: queue };
      }
      state.usage = addCall(state.usage, data);
      if (report) {
        updateAuditor(state, report, (a) => ({ finishReason: str(data.finishReason) ?? a.finishReason }));
      }
      break;
    }
    case 'tool.started': {
      const tool = str(data.tool);
      if (event.source && tool) {
        // Las tools de una iteración se ejecutan en secuencia: la primera pendiente con ese nombre es esta.
        const queue = state.pendingToolArgs[event.source] ?? [];
        const index = queue.findIndex((q) => q.name === tool);
        if (index >= 0) {
          detail = mainArgument(queue[index].arguments);
          state.pendingToolArgs = {
            ...state.pendingToolArgs,
            [event.source]: [...queue.slice(0, index), ...queue.slice(index + 1)],
          };
        }
      }
      if (report) {
        updateAuditor(state, report, (a) => ({
          toolCalls: a.toolCalls + 1,
          toolTimes: [...a.toolTimes, event.ts],
          activeTool: tool,
          status: a.status === 'pending' ? 'running' : a.status,
          startedAt: a.startedAt ?? event.ts,
        }));
      }
      break;
    }
    case 'tool.completed': {
      if (report) {
        const success = bool(data.success) ?? true;
        updateAuditor(state, report, (a) => ({
          activeTool: null,
          toolErrors: a.toolErrors + (success ? 0 : 1),
        }));
      }
      break;
    }
    case 'report.written': {
      const reportType = str(data.reportType) ?? report;
      if (reportType) {
        updateAuditor(state, reportType, () => ({
          health: (str(data.health) as Health | null) ?? 'Unknown',
          criticalFindings: num(data.criticalFindings),
          truncated: bool(data.truncated) ?? false,
          fileName: str(data.fileName),
        }));
      }
      state.reportsVersion++;
      break;
    }
    case 'log': {
      detail = str(data.channel);
      break;
    }
    case 'log.truncated': {
      state.droppedUpToSeq = num(data.droppedUpToSeq);
      break;
    }
    case 'run.completed': {
      const status = (str(data.status) as RunStatus | null) ?? (event.level === 'error' ? 'failed' : 'succeeded');
      state.status = status;
      state.finishedAt = event.ts;
      state.exitCode = num(data.exitCode);
      state.durationMs = num(data.durationMs);
      state.failedReports = Array.isArray(data.failedReports)
        ? (data.failedReports.filter((r) => typeof r === 'string') as string[])
        : state.failedReports;
      state.executiveReportPath = str(data.executiveReportPath);
      state.error = str(data.error);
      // El total del servidor manda sobre lo sumado en vivo (que pierde lo descartado por log.truncated).
      state.usage = usageOf(data.usage) ?? state.usage;
      const interrupted: PhaseStatus = status === 'cancelled' ? 'cancelled' : 'failed';
      for (const phase of PHASES) {
        const current = state.phases[phase].status;
        if (current === 'running') {
          updatePhase(state, phase, { status: status === 'succeeded' || status === 'completed_with_failures' ? 'succeeded' : interrupted, finishedAt: event.ts });
        } else if (current === 'pending') {
          updatePhase(state, phase, { status: 'skipped' });
        }
      }
      for (const r of state.auditorOrder) {
        const a = state.auditors[r];
        if (a.status === 'running' || a.status === 'pending') {
          const failedByServer = isFailedReport(state.failedReports, r);
          let final: AuditorStatus;
          if (status === 'cancelled') final = a.status === 'running' ? 'cancelled' : 'skipped';
          else if (failedByServer || a.status === 'running') final = 'failed';
          else final = 'skipped';
          updateAuditor(state, r, () => ({ status: final, activeTool: null }));
        } else if (isFailedReport(state.failedReports, r) && a.status === 'succeeded') {
          updateAuditor(state, r, () => ({ status: 'failed' }));
        }
      }
      if (status === 'cancelled') {
        // Lo que falló por la cancelación no es un error: queda como cancelado y deja de contar (D4).
        for (const phase of PHASES) {
          const p = state.phases[phase];
          if (p.status === 'failed' && isCancellationError(p.error)) updatePhase(state, phase, { status: 'cancelled' });
        }
        for (const r of state.auditorOrder) {
          const a = state.auditors[r];
          if (a.status === 'failed' && isCancellationError(a.error)) updateAuditor(state, r, () => ({ status: 'cancelled' }));
        }
        state.errors = Math.max(0, state.errors - state.cancellationErrors);
        state.cancellationErrors = 0;
      }
      state.pendingToolArgs = {};
      state.reportsVersion++;
      break;
    }
    default:
      break;
  }

  return toLogEntry(event, detail);
}

/**
 * Aplica un lote. El estado de entrada no se toca: se trabaja sobre una copia superficial, cuyos objetos anidados se
 * sustituyen (nunca se mutan) en `updatePhase`/`updateAuditor`. Las filas nuevas del registro se juntan en un array
 * local y se concatenan una sola vez al final: copiar `log` por evento hacía cuadrático un replay grande (P3-12).
 */
function applyEvents(state: RunState, events: AnalysisEventDto[]): RunState {
  let next: RunState | null = null;
  const added: LogEntry[] = [];
  for (const event of events) {
    const base = next ?? state;
    if (base.runId && event.runId && event.runId !== base.runId) continue; // evento de otra ejecución
    if (event.seq <= base.lastSeq) continue; // ya aplicado (solape de replay)
    if (!next) next = { ...state };
    added.push(applyEvent(next, event));
    next.lastSeq = event.seq;
    if (!next.runId) next.runId = event.runId;
  }
  if (!next) return state;
  next.log = state.log.concat(added);
  return next;
}

export function runReducer(state: RunState, action: RunAction): RunState {
  switch (action.type) {
    case 'reset':
      return createRunState(action.status);
    case 'status': {
      const s = action.status;
      if (state.runId && s.runId !== state.runId) return createRunState(s);
      // El estado terminal lo fija `run.completed`; un RunStatusDto solo completa lo que aún no se sabe.
      const terminalFromEvents = state.status !== null && state.status !== 'running' && state.lastSeq > 0;
      return {
        ...state,
        runId: s.runId,
        repositoryPath: state.repositoryPath ?? s.repositoryPath,
        status: terminalFromEvents ? state.status : s.status,
        startedAt: state.startedAt ?? s.startedAt,
        finishedAt: state.finishedAt ?? s.finishedAt,
        forceReindex: s.forceReindex,
        error: state.error ?? s.error,
      };
    }
    case 'event':
      return applyEvents(state, [action.event]);
    case 'events':
      return applyEvents(state, action.events);
    default:
      return state;
  }
}

// ── Selectores ────────────────────────────────────────────────────────────

const DONE_PHASE: PhaseStatus[] = ['succeeded', 'failed', 'skipped', 'cancelled'];
const DONE_AUDITOR: AuditorStatus[] = ['succeeded', 'failed', 'cancelled', 'skipped'];

/** Progreso = (fases terminadas + auditores terminados) / (5 + N) (§7). */
export function progressOf(state: RunState): { done: number; total: number; ratio: number } {
  const phasesDone = PHASES.filter((p) => DONE_PHASE.includes(state.phases[p].status)).length;
  const auditorsDone = state.auditorOrder.filter((r) => DONE_AUDITOR.includes(state.auditors[r].status)).length;
  const total = PHASES.length + state.auditorOrder.length;
  const done = phasesDone + auditorsDone;
  return { done, total, ratio: total === 0 ? 0 : done / total };
}

export function isRunActive(state: RunState): boolean {
  return state.status === 'running';
}

/**
 * Si el carril admite «Regenerar»: ha fallado o se ha cancelado, y la ejecución ya ha terminado (R6). No depende de
 * `failedReports`, que llega vacío cuando falla el pipeline entero. Los cancelados entran para poder regenerar tras
 * cancelar a mitad de los informes (criterio 5): la cancelación los deja en `cancelled` y no en `failed`.
 */
export function canRegenerate(state: RunState, report: string): boolean {
  const status = state.auditors[report]?.status;
  return !!state.runId && state.status !== null && state.status !== 'running' && (status === 'failed' || status === 'cancelled');
}
