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
 * nativo por lenguaje y `DotNetEnricherStep` (P16). `facets` también, pero `PrepareRegenerationStep` solo corre al
 * regenerar informes, en lugar de la detección. El `step.started` de cualquiera abre la fase; solo el `step.completed`
 * correcto del último la cierra, y el fallo de cualquiera la marca fallida.
 */
export const PHASE_STEPS: Record<string, Phase> = {
  DetectAndIndexGraphStep: 'detect',
  LanguageEnricherStep: 'enrich',
  DotNetEnricherStep: 'enrich',
  PrepareRegenerationStep: 'facets',
  SqlFacetExtractorStep: 'facets',
  ParallelReportsExecution: 'reports',
  ExecutiveSynthesizerStep: 'synthesis',
};

/**
 * Pasos de la preparación por subsistema (`CodebaseAnalysis:Preparation:Mode = Subsystems`), en el orden en que se ejecutan
 * tras `SqlFacetExtractorStep`. El servidor los asigna a la fase `facets` (`AnalysisEventMapper`). No están en `PHASE_STEPS`
 * porque con `Mode = Off` no existen y entonces `SqlFacetExtractorStep` sigue cerrando la fase: aquí solo la reabren (el
 * `step.started` de cualquiera) y la cierra el último, `TimeBudgetStep`. Su sub-step LLM (`DomainNaming`) no abre ni cierra
 * nada, igual que los sub-steps de los auditores.
 */
export const PREPARATION_STEPS: readonly string[] = ['RepositoryInventoryStep', 'DomainDiscoveryStep', 'CoveragePlanningStep', 'TimeBudgetStep'];

/**
 * La pasada compartida (`CodebaseAnalysis:SharedPass:Enabled`) corre tras `TimeBudgetStep` y antes de los informes, y el servidor
 * la asigna a la fase `facets` (`AnalysisEventMapper`). Cuando existe es el último paso de esa fase: su `step.started` la reabre,
 * su `step.completed` la cierra y su `sharedpass.progress` en fase `failed` la marca fallida. No está en `PHASE_STEPS` porque con la
 * pasada apagada no existe y entonces la cierran las facetas o la preparación.
 */
export const SHARED_PASS_STEP = 'SharedPassStep';

/** Último paso de la preparación: con él termina la fase `facets` cuando la preparación está activa. */
const LAST_PREPARATION_STEP = PREPARATION_STEPS[PREPARATION_STEPS.length - 1];

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

/** Prefijos de los steps de la verificación: el paso por informe (`Verify_<Tipo>`) y su sub-step LLM (`Verifier_<Tipo>`). */
export const VERIFY_PREFIXES = ['Verify_', 'Verifier_'] as const;

/**
 * Estado de la verificación de un informe (`report.verified`): no es el del auditor, que sigue siendo `succeeded`.
 * `verified`: sin correcciones. `corrected`: reescrito con correcciones. `warnings`: quedan avisos de validación.
 * `failed`: no se pudo verificar y el informe sigue como estaba.
 */
export type VerificationStatus = 'idle' | 'running' | 'verified' | 'corrected' | 'warnings' | 'failed';

export interface VerificationState {
  status: VerificationStatus;
  startedAt: string | null;
  finishedAt: string | null;
  toolCalls: number;
  activeTool: string | null;
  /** Correcciones que lista el apartado «Cambios de la verificación». */
  changes: number;
  /** Versión del historial que «Deshacer» restaura; null si el informe no se reescribió. */
  previousVersionId: string | null;
  versionId: string | null;
  /** Resumen del servidor (`report.verified.message`) o motivo del fallo. */
  message: string | null;
}

export interface PhaseState {
  status: PhaseStatus;
  startedAt: string | null;
  finishedAt: string | null;
  durationMs: number | null;
  /** `data.error` del `step.completed` fallido del step de primer nivel. */
  error: string | null;
}

/**
 * Una llamada a herramienta de un carril, para su detalle: se abre con su `tool.started` y se cierra con el `tool.completed` del mismo
 * step y la misma tool (las tools de un step van en secuencia). Los argumentos llegan en los dos eventos (o, de un servidor anterior, del
 * `llm.response`, truncados a 500 caracteres); la salida, solo en el de fin y truncada en el servidor.
 */
export interface ToolCallRecord {
  /** `seq` del `tool.started`: identifica la llamada en el carril. */
  key: number;
  tool: string;
  source: string | null;
  startedAt: string;
  finishedAt: string | null;
  durationMs: number | null;
  success: boolean | null;
  error: string | null;
  arguments: string | null;
  argumentsTruncated: boolean;
  output: string | null;
  outputTruncated: boolean;
  /** Tamaño entero de la salida en bytes UTF-8 (`data.outputBytes`), aunque llegue truncada. */
  outputBytes: number | null;
  /** La salida se descartó para acotar la memoria (en el servidor al reenviar, o aquí: `MAX_TOOL_OUTPUTS`). */
  outputDropped: boolean;
}

/** Salidas de tool que se conservan en el estado del cliente; las más antiguas se descartan (`outputDropped`). */
export const MAX_TOOL_OUTPUTS = 300;

/** Estado de un lote de la pasada compartida (`sharedpass.progress`). */
export type SharedPassBatchStatus = 'pending' | 'reading' | 'retrying' | 'complete' | 'partial' | 'failed' | 'notRead';
export type SharedPassPhase = 'reading' | 'consolidating' | 'judging' | 'saving' | 'done' | 'cut' | 'failed';

export interface SharedPassBatch {
  id: string;
  files: number;
  estimatedTokens: number;
  status: SharedPassBatchStatus;
  calls: number;
  costUsd: number;
  tokens: number;
  findings: number | null;
  gaps: number | null;
}

export interface SharedPassTotals {
  costUsd: number;
  maxCostUsd: number | null;
  calls: number;
  maxCalls: number | null;
  tokens: number;
  maxTokens: number | null;
  unpricedCalls: number;
  elapsedMs: number;
  maxMinutes: number | null;
  batchesDone: number;
  batchesTotal: number;
}

/** El último `sharedpass.progress`: el servidor manda el estado entero en cada cambio. */
export interface SharedPassState {
  phase: SharedPassPhase;
  startedAt: string | null;
  batches: SharedPassBatch[];
  totals: SharedPassTotals;
  judge: { candidates: number; tandas: number; tandasDone: number } | null;
  stopReason: string | null;
  error: string | null;
  /** Al terminar: registros confirmados por severidad (`P0`…`P3`), total de registros y rechazados por el juez. */
  severities: Record<string, number> | null;
  entries: number | null;
  rejected: number | null;
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
  /** Las mismas llamadas que `toolTimes`, en el mismo orden, con su detalle. */
  calls: ToolCallRecord[];
  activeTool: string | null;
  tokens: number | null;
  costUsd: number | null;
  finishReason: string | null;
  health: Health | null;
  criticalFindings: number | null;
  truncated: boolean;
  fileName: string | null;
  error: string | null;
  /** Verificación a demanda del informe (`POST /verify`); no cambia `status`, que sigue siendo el del auditor. */
  verification: VerificationState;
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

/** `data.validation` de `run.completed`: los totales de `_validation.json` (`ReportValidationSummary.ToData`). */
export interface RunValidation {
  reports: number;
  citationsTotal: number;
  citationsValid: number;
  brokenCitations: number;
  severeFindingsWithoutLocation: number;
  errors: number;
  warnings: number;
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
  /** Totales de la validación de informes del último `run.completed` (`data.validation`); null si no se validó. */
  validation: RunValidation | null;
  /** True desde `run.verification_started` hasta el siguiente `run.completed`: los steps de esa ejecución no mueven las fases. */
  verifying: boolean;
  /** Pasada compartida (`sharedpass.progress`); null si la ejecución no la tiene. */
  sharedPass: SharedPassState | null;
  /** Llamadas con salida guardada, de la más antigua a la más reciente, para descartar las que pasan de `MAX_TOOL_OUTPUTS`. */
  toolOutputs: { report: string; key: number }[];
  /** Si la verificación en curso o la última pidió regenerar también el resumen ejecutivo. */
  verifySynthesis: boolean;
  /**
   * True si una verificación corrigió informes y el resumen ejecutivo no se ha regenerado después: puede contradecirlos.
   * Se apaga al regenerar con resumen, al verificar con resumen y al lanzar otro análisis.
   */
  synthesisStale: boolean;
}

export type RunAction =
  | { type: 'reset'; status: RunStatusDto | null }
  | { type: 'status'; status: RunStatusDto }
  /** El usuario deshizo la verificación de ese informe (restauró su versión anterior): vuelve a «sin verificar». */
  | { type: 'verification_undone'; report: string }
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
    calls: [],
    activeTool: null,
    tokens: null,
    costUsd: null,
    finishReason: null,
    health: null,
    criticalFindings: null,
    truncated: false,
    fileName: null,
    error: null,
    verification: newVerification(),
  };
}

function newVerification(): VerificationState {
  return {
    status: 'idle',
    startedAt: null,
    finishedAt: null,
    toolCalls: 0,
    activeTool: null,
    changes: 0,
    previousVersionId: null,
    versionId: null,
    message: null,
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
    validation: null,
    verifying: false,
    verifySynthesis: false,
    synthesisStale: false,
    sharedPass: null,
    toolOutputs: [],
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

/** True si el step (o la rama) es de la verificación: sus eventos actualizan `verification` y no el estado del auditor. */
export function isVerificationSource(event: AnalysisEventDto): boolean {
  const branch = typeof event.data?.branch === 'string' ? event.data.branch : null;
  return [event.source, branch].some((name) => !!name && VERIFY_PREFIXES.some((prefix) => name.startsWith(prefix)));
}

function updateVerification(state: RunState, report: string, patch: (v: VerificationState) => Partial<VerificationState>): void {
  updateAuditor(state, report, (a) => ({ verification: { ...a.verification, ...patch(a.verification) } }));
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

/** El `validation` de `run.completed`; null si no viene o no tiene la forma esperada. */
function validationOf(value: unknown): RunValidation | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  const total = num(v.citationsTotal);
  if (total === null) return null;
  return {
    reports: num(v.reports) ?? 0,
    citationsTotal: total,
    citationsValid: num(v.citationsValid) ?? 0,
    brokenCitations: num(v.brokenCitations) ?? 0,
    severeFindingsWithoutLocation: num(v.severeFindingsWithoutLocation) ?? 0,
    errors: num(v.errors) ?? 0,
    warnings: num(v.warnings) ?? 0,
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

/**
 * La pasada compartida mantiene abierta la fase `facets` mientras trabaja (`TimeBudgetStep` ya la cerró) y propaga su fallo: un
 * `sharedpass.progress` en curso la reabre; en fase `failed` la marca fallida (el step captura sus errores y termina con éxito,
 * así que su `step.completed` no lo diría). El cierre bueno lo hace el `step.completed` del step.
 */
function syncFacetsWithSharedPass(state: RunState, event: AnalysisEventDto): void {
  const phase = state.sharedPass?.phase;
  const facets = state.phases.facets;
  if (phase === 'failed') {
    if (facets.status !== 'failed') {
      updatePhase(state, 'facets', {
        status: 'failed',
        finishedAt: event.ts,
        durationMs: phaseDuration(facets, 'facets', event.ts, null),
        error: event.message || 'La pasada compartida falló.',
      });
    }
  } else if (phase !== 'done' && phase !== 'cut' && facets.status === 'succeeded') {
    updatePhase(state, 'facets', { status: 'running', finishedAt: null, durationMs: null });
  }
}

const SHARED_PASS_PHASES: readonly SharedPassPhase[] = ['reading', 'consolidating', 'judging', 'saving', 'done', 'cut', 'failed'];
const SHARED_PASS_BATCH_STATUSES: readonly SharedPassBatchStatus[] = ['pending', 'reading', 'retrying', 'complete', 'partial', 'failed', 'notRead'];
const FINISHED_BATCH: readonly SharedPassBatchStatus[] = ['complete', 'partial', 'failed', 'notRead'];

const record = (v: unknown): Record<string, unknown> | null =>
  v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null;

/**
 * El estado de la pasada de un `sharedpass.progress`. Uno ya superado que el servidor guardó sin lotes (`superseded`, al reenviar
 * el registro) conserva los lotes que ya se tenían.
 */
export function sharedPassOf(data: Record<string, unknown>, previous: SharedPassState | null): SharedPassState {
  const phase = str(data.phase) as SharedPassPhase | null;
  const t = record(data.totals) ?? {};
  const judge = record(data.judge);
  const result = record(data.result);
  const severities = record(result?.severities);
  const batches = Array.isArray(data.batches)
    ? data.batches
        .map(record)
        .filter((b): b is Record<string, unknown> => !!b && typeof b.id === 'string')
        .map<SharedPassBatch>((b) => {
          const status = str(b.status) as SharedPassBatchStatus | null;
          return {
            id: b.id as string,
            files: num(b.files) ?? 0,
            estimatedTokens: num(b.estimatedTokens) ?? 0,
            status: status && SHARED_PASS_BATCH_STATUSES.includes(status) ? status : 'pending',
            calls: num(b.calls) ?? 0,
            costUsd: num(b.costUsd) ?? 0,
            tokens: num(b.tokens) ?? 0,
            findings: num(b.findings),
            gaps: num(b.gaps),
          };
        })
    : (previous?.batches ?? []);
  return {
    phase: phase && SHARED_PASS_PHASES.includes(phase) ? phase : (previous?.phase ?? 'reading'),
    startedAt: str(data.startedAt) ?? previous?.startedAt ?? null,
    batches,
    totals: {
      costUsd: num(t.costUsd) ?? 0,
      maxCostUsd: num(t.maxCostUsd),
      calls: num(t.calls) ?? 0,
      maxCalls: num(t.maxCalls),
      tokens: num(t.tokens) ?? 0,
      maxTokens: num(t.maxTokens),
      unpricedCalls: num(t.unpricedCalls) ?? 0,
      elapsedMs: num(t.elapsedMs) ?? 0,
      maxMinutes: num(t.maxMinutes),
      batchesDone: num(t.batchesDone) ?? batches.filter((b) => FINISHED_BATCH.includes(b.status)).length,
      batchesTotal: num(t.batchesTotal) ?? batches.length,
    },
    judge: judge ? { candidates: num(judge.candidates) ?? 0, tandas: num(judge.tandas) ?? 0, tandasDone: num(judge.tandasDone) ?? 0 } : null,
    stopReason: str(data.stopReason),
    error: str(data.error),
    severities: severities
      ? (Object.fromEntries(Object.entries(severities).filter(([, v]) => typeof v === 'number')) as Record<string, number>)
      : null,
    entries: num(result?.entries),
    rejected: num(result?.rejected),
  };
}

/** Añade al carril `report` la llamada que abre un `tool.started`. */
function openCall(state: RunState, report: string, event: AnalysisEventDto, queuedArguments: string | null): void {
  const data = event.data ?? {};
  const own = str(data.arguments);
  const call: ToolCallRecord = {
    key: event.seq,
    tool: str(data.tool) ?? '?',
    source: event.source,
    startedAt: event.ts,
    finishedAt: null,
    durationMs: null,
    success: null,
    error: null,
    arguments: own ?? queuedArguments,
    // Los del `llm.response` vienen cortados a 500 caracteres: si llegan a ese tope, pueden estar truncados.
    argumentsTruncated: own !== null ? (bool(data.argumentsTruncated) ?? false) : (queuedArguments?.endsWith('…') ?? false),
    output: null,
    outputTruncated: false,
    outputBytes: null,
    outputDropped: false,
  };
  updateAuditor(state, report, (a) => ({ calls: [...a.calls, call] }));
}

/**
 * Cierra con un `tool.completed` la última llamada abierta del mismo step y la misma tool (las de un step van en secuencia); si no la
 * hay (un replay que empezó a mitad), no hace nada. Guarda la salida y descarta las más antiguas por encima de `MAX_TOOL_OUTPUTS`.
 */
function closeCall(state: RunState, report: string, event: AnalysisEventDto): void {
  const data = event.data ?? {};
  const tool = str(data.tool);
  const calls = state.auditors[report]?.calls ?? [];
  let index = -1;
  for (let i = calls.length - 1; i >= 0; i--) {
    if (calls[i].finishedAt === null && calls[i].tool === tool && calls[i].source === event.source) {
      index = i;
      break;
    }
  }
  if (index < 0) return;
  const output = str(data.output);
  const own = str(data.arguments);
  const closed: ToolCallRecord = {
    ...calls[index],
    finishedAt: event.ts,
    durationMs: num(data.durationMs),
    success: bool(data.success) ?? event.level !== 'error',
    error: str(data.error),
    arguments: own ?? calls[index].arguments,
    argumentsTruncated: own !== null ? (bool(data.argumentsTruncated) ?? false) : calls[index].argumentsTruncated,
    output,
    outputTruncated: bool(data.outputTruncated) ?? false,
    outputBytes: num(data.outputBytes),
    outputDropped: bool(data.outputDropped) ?? false,
  };
  updateAuditor(state, report, (a) => ({ calls: a.calls.map((c, i) => (i === index ? closed : c)) }));
  if (output === null) return;
  const outputs = [...state.toolOutputs, { report, key: closed.key }];
  while (outputs.length > MAX_TOOL_OUTPUTS) {
    const oldest = outputs.shift()!;
    updateAuditor(state, oldest.report, (a) => ({
      calls: a.calls.map((c) => (c.key === oldest.key ? { ...c, output: null, outputDropped: true } : c)),
    }));
  }
  state.toolOutputs = outputs;
}

/** Aplica un evento sobre `state` (una copia propia de `applyEvents`) y devuelve su fila del registro, o null si no tiene. */
function applyEvent(state: RunState, event: AnalysisEventDto): LogEntry | null {
  const data = event.data ?? {};
  // El estado de la pasada no es una línea del registro (sus mensajes llegan aparte, como `log`) ni cuenta como aviso.
  if (event.type === 'sharedpass.progress') {
    state.sharedPass = sharedPassOf(data, state.sharedPass);
    if (!state.verifying) syncFacetsWithSharedPass(state, event);
    return null;
  }
  const report = auditorOf(event);
  let detail: string | null = null;

  if (event.level === 'warning') state.warnings++;
  if (event.level === 'error') {
    state.errors++;
    if (isCancellationError(str(data.error) ?? event.message)) state.cancellationErrors++;
  }

  // Verificar no vuelve a ejecutar fases: la preparación y la síntesis de esa ejecución no mueven el avance del análisis.
  const verifying = state.verifying;
  const verifier = report && isVerificationSource(event) ? report : null;
  const topPhase = event.source && !verifying ? PHASE_STEPS[event.source] ?? (PREPARATION_STEPS.includes(event.source) || event.source === SHARED_PASS_STEP ? 'facets' : undefined) : undefined;
  if (event.phase && !verifying && event.type !== 'run.completed' && event.type !== 'run.started') {
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
      state.verifying = false;
      if (bool(data.synthesis) !== false) state.synthesisStale = false;
      for (const phase of phases) {
        updatePhase(state, phase, { status: 'pending', startedAt: null, finishedAt: null, durationMs: null, error: null });
      }
      state.pendingToolArgs = {};
      break;
    }
    case 'run.verification_started': {
      // La misma ejecución vuelve a correr (como al regenerar), pero los carriles pedidos conservan su auditor: solo
      // arranca su verificación, y las fases no se tocan.
      const reports = Array.isArray(data.reports) ? (data.reports.filter((r) => typeof r === 'string') as string[]) : [];
      state.status = 'running';
      state.finishedAt = null;
      state.error = null;
      state.exitCode = null;
      state.durationMs = null;
      state.verifying = true;
      state.verifySynthesis = bool(data.synthesis) ?? false;
      if (state.verifySynthesis) state.synthesisStale = false;
      for (const r of reports) {
        addAuditor(state, r);
        updateAuditor(state, r, () => ({ verification: { ...newVerification(), status: 'running', startedAt: event.ts } }));
      }
      state.pendingToolArgs = {};
      break;
    }
    case 'report.verified': {
      const reportType = str(data.reportType) ?? report;
      if (reportType) {
        const outcome = str(data.outcome);
        const status: VerificationStatus =
          outcome === 'verified' || outcome === 'corrected' || outcome === 'warnings' ? outcome : 'failed';
        const changes = num(data.changes) ?? 0;
        updateVerification(state, reportType, (v) => ({
          status,
          finishedAt: event.ts,
          activeTool: null,
          changes,
          previousVersionId: str(data.previousVersionId),
          versionId: str(data.versionId),
          message: status === 'failed' ? (str(data.error) ?? event.message) : event.message,
          startedAt: v.startedAt ?? event.ts,
        }));
        // Un informe corregido deja el resumen ejecutivo describiendo la versión anterior, salvo que se regenere después.
        if (changes > 0 && !state.verifySynthesis) state.synthesisStale = true;
      }
      state.reportsVersion++;
      break;
    }
    case 'step.started': {
      const attempt = num(data.attempt) ?? 1;
      // Un reintento reabre la fase; el step siguiente de una fase que ya falló no la da por buena.
      if (topPhase && (state.phases[topPhase].status !== 'failed' || attempt > 1)) {
        updatePhase(state, topPhase, { status: 'running', finishedAt: null });
      }
      if (verifier) {
        updateVerification(state, verifier, (v) => ({ status: 'running', startedAt: v.startedAt ?? event.ts }));
      } else if (report) {
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
      const isLast = !!topPhase && (PHASE_LAST_STEP[topPhase] === event.source || event.source === LAST_PREPARATION_STEP || event.source === SHARED_PASS_STEP);
      const closesPhase = topPhase && (!success || (isLast && state.phases[topPhase].status !== 'failed'));
      if (topPhase && closesPhase) {
        updatePhase(state, topPhase, {
          status: success ? 'succeeded' : 'failed',
          finishedAt: event.ts,
          durationMs: phaseDuration(state.phases[topPhase], topPhase, event.ts, num(data.durationMs)),
          error: success ? null : str(data.error) ?? event.message,
        });
      }
      if (verifier) {
        // El paso de verificación no falla aunque el verificador no responda (eso llega en `report.verified`): si falla, es
        // un error del paso y el informe no se verificó.
        if (!success) {
          updateVerification(state, verifier, (v) => ({
            status: 'failed',
            finishedAt: event.ts,
            activeTool: null,
            message: str(data.error) ?? event.message,
            startedAt: v.startedAt ?? event.ts,
          }));
        }
      } else if (report) {
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
      if (verifier) updateVerification(state, verifier, (v) => ({ status: 'running', startedAt: v.startedAt ?? event.ts }));
      else if (report) updateAuditor(state, report, (a) => ({ status: 'running', startedAt: a.startedAt ?? event.ts }));
      break;
    }
    case 'branch.completed': {
      if (verifier) break;
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
      if (report && !verifier) {
        updateAuditor(state, report, (a) => ({ finishReason: str(data.finishReason) ?? a.finishReason }));
      }
      break;
    }
    case 'tool.started': {
      const tool = str(data.tool);
      let queuedArguments: string | null = null;
      if (event.source && tool) {
        // Las tools de una iteración se ejecutan en secuencia: la primera pendiente con ese nombre es esta.
        const queue = state.pendingToolArgs[event.source] ?? [];
        const index = queue.findIndex((q) => q.name === tool);
        if (index >= 0) {
          queuedArguments = queue[index].arguments;
          detail = mainArgument(queue[index].arguments);
          state.pendingToolArgs = {
            ...state.pendingToolArgs,
            [event.source]: [...queue.slice(0, index), ...queue.slice(index + 1)],
          };
        }
      }
      if (verifier) {
        updateVerification(state, verifier, (v) => ({ toolCalls: v.toolCalls + 1, activeTool: tool }));
      } else if (report) {
        updateAuditor(state, report, (a) => ({
          toolCalls: a.toolCalls + 1,
          toolTimes: [...a.toolTimes, event.ts],
          activeTool: tool,
          status: a.status === 'pending' ? 'running' : a.status,
          startedAt: a.startedAt ?? event.ts,
        }));
        openCall(state, report, event, queuedArguments);
      }
      if (!detail) detail = mainArgument(str(data.arguments));
      break;
    }
    case 'tool.completed': {
      if (verifier) {
        updateVerification(state, verifier, () => ({ activeTool: null }));
      } else if (report) {
        const success = bool(data.success) ?? true;
        updateAuditor(state, report, (a) => ({
          activeTool: null,
          toolErrors: a.toolErrors + (success ? 0 : 1),
        }));
        closeCall(state, report, event);
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
      state.validation = validationOf(data.validation);
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
      // Una verificación que no llegó a su `report.verified` (cancelada o fallida) no deja resultado: el informe sigue como estaba.
      state.verifying = false;
      for (const r of state.auditorOrder) {
        if (state.auditors[r].verification.status !== 'running') continue;
        updateVerification(state, r, (v) => ({
          status: status === 'cancelled' ? 'idle' : 'failed',
          finishedAt: event.ts,
          activeTool: null,
          message: status === 'cancelled' ? null : (state.error ?? v.message ?? 'La verificación no terminó.'),
        }));
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
    const entry = applyEvent(next, event);
    if (entry) added.push(entry);
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
    case 'verification_undone': {
      const current = state.auditors[action.report];
      if (!current) return state;
      return {
        ...state,
        auditors: { ...state.auditors, [action.report]: { ...current, verification: newVerification() } },
        reportsVersion: state.reportsVersion + 1,
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
 * Si el carril admite «Regenerar»: ha terminado el auditor (con éxito, con error o cancelado) y la ejecución ya ha
 * terminado (R6, D1). No depende de `failedReports`, que llega vacío cuando falla el pipeline entero. Los cancelados
 * entran para poder regenerar tras cancelar a mitad de los informes (criterio 5): la cancelación los deja en `cancelled` y
 * no en `failed`. Los correctos también (D1 del plan del verificador): rehacer un informe sano es una decisión del usuario.
 */
export function canRegenerate(state: RunState, report: string): boolean {
  const status = state.auditors[report]?.status;
  return !!state.runId && state.status !== null && state.status !== 'running' && (status === 'failed' || status === 'cancelled' || status === 'succeeded');
}

/**
 * Si el carril admite «Verificar»: el auditor terminó con éxito (hay un informe en disco que comprobar) y la ejecución ya
 * ha terminado. Se puede volver a verificar un informe ya verificado, corregido o con avisos.
 */
export function canVerify(state: RunState, report: string): boolean {
  const lane = state.auditors[report];
  return !!state.runId && state.status !== null && state.status !== 'running' && lane?.status === 'succeeded' && lane.verification.status !== 'running';
}

/**
 * Si el carril ofrece «Deshacer verificación»: la última verificación reescribió el informe (hay versión anterior que
 * restaurar). Es la de `report.verified.previousVersionId`.
 */
export function canUndoVerification(state: RunState, report: string): boolean {
  const v = state.auditors[report]?.verification;
  return !!state.runId && state.status !== null && state.status !== 'running' && !!v?.previousVersionId && v.status !== 'running';
}
