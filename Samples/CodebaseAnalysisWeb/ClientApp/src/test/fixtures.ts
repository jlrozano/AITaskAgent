// Secuencias de ejemplo construidas sobre los contratos del plan (§4.2-§4.4). La ejecución falsa de F2
// (IAnalysisWorkflow de CodebaseAnalysisWeb.Tests) debe emitir el mismo orden de tipos para que ambos lados
// se prueben contra la misma historia.

import type {
  AnalysisEventDto,
  ChatStreamEvent,
  ChatTurn,
  DocumentDetailDto,
  DocumentFamily,
  DocumentTypeDto,
  DocumentViewer,
  KnowledgeState,
  MetaDto,
  ReportChange,
  ReportSummaryDto,
  RunStatusDto,
} from '../api/types';
import { REPORT_FILE_NAMES } from '../state/runReducer';

export const RUN_ID = '3f2a9c1b7d4e';

export const meta: MetaDto = {
  reports: [
    { type: 'AnalisisFuncional', alias: 'funcional', fileName: '01_analisis_funcional.md', title: 'Análisis funcional' },
    { type: 'CodeSmells', alias: 'smells', fileName: '02_code_smells.md', title: 'Code smells' },
    { type: 'CodigoMuerto', alias: 'deadcode', fileName: '03_codigo_muerto.md', title: 'Código muerto' },
    { type: 'InyeccionDependenciasExtensiones', alias: 'di', fileName: '09_inyeccion_dependencias.md', title: 'Inyección de dependencias' },
  ],
  executiveReportFileName: '00_resumen_ejecutivo.md',
  defaultReports: ['AnalisisFuncional', 'CodeSmells', 'CodigoMuerto', 'InyeccionDependenciasExtensiones'],
  customReportsFolder: 'custom',
  documents: {
    maxFileBytes: 26_214_400,
    maxTotalBytes: 524_288_000,
    maxDocuments: 300,
    types: documentTypes(),
  },
  features: {
    embeddings: { enabled: false, model: null, dimensions: null, disabledReason: 'Aún no disponible en esta versión.' },
    imageDescription: { enabled: false, model: null, disabledReason: 'Aún no disponible en esta versión.' },
  },
};

/** La tabla de §7.2 tal como la sirve `/api/meta` (`DocumentTypeRegistry` de Core). */
function documentTypes(): DocumentTypeDto[] {
  const MiB = 1024 * 1024;
  const code =
    '.cs .vb .bas .cls .frm .ts .tsx .js .jsx .mjs .py .java .kt .go .rs .c .h .cpp .hpp .sql .ps1 .psm1 .sh .bat .rb ' +
    '.php .swift .scala .css .scss .razor .cshtml .xaml .csproj .props .targets .gradle .toml .ini .cfg';
  return [
    { family: 'markdown', extensions: ['.md', '.markdown'], maxBytes: 5 * MiB, viewer: 'markdown' },
    { family: 'text', extensions: ['.txt', '.log'], maxBytes: 5 * MiB, viewer: 'text' },
    { family: 'code', extensions: code.split(' '), maxBytes: 5 * MiB, viewer: 'markdown' },
    { family: 'structured', extensions: ['.json', '.yaml', '.yml', '.xml'], maxBytes: 5 * MiB, viewer: 'markdown' },
    { family: 'csv', extensions: ['.csv', '.tsv'], maxBytes: 10 * MiB, viewer: 'markdown' },
    { family: 'html', extensions: ['.html', '.htm'], maxBytes: 5 * MiB, viewer: 'html' },
    { family: 'pdf', extensions: ['.pdf'], maxBytes: 25 * MiB, viewer: 'pdf' },
    { family: 'docx', extensions: ['.docx'], maxBytes: 25 * MiB, viewer: 'markdown' },
    { family: 'xlsx', extensions: ['.xlsx'], maxBytes: 25 * MiB, viewer: 'markdown' },
    { family: 'pptx', extensions: ['.pptx'], maxBytes: 25 * MiB, viewer: 'markdown' },
    { family: 'image', extensions: ['.png', '.jpg', '.jpeg', '.gif', '.webp'], maxBytes: 10 * MiB, viewer: 'image' },
    { family: 'svg', extensions: ['.svg'], maxBytes: 2 * MiB, viewer: 'image' },
  ];
}

export const absentKnowledge: KnowledgeState = {
  graph: { state: 'absent', nodeCount: 0, edgeCount: 0, addedAt: null },
  embeddings: { state: 'absent', chunkCount: 0, indexedAt: null, model: null },
};

/** Campos de V2 de un informe del catálogo sin historial ni conocimiento (lo que devuelve V2-0). */
export function catalogReportFields(
  fileName: string,
): Pick<ReportSummaryDto, 'kind' | 'id' | 'origin' | 'modifiedByChat' | 'versionCount' | 'knowledge'> {
  return { kind: 'catalog', id: fileName, origin: 'pipeline', modifiedByChat: false, versionCount: 0, knowledge: absentKnowledge };
}

export const runningStatus: RunStatusDto = {
  runId: RUN_ID,
  repositoryPath: 'C:\\repos\\miapp',
  status: 'running',
  startedAt: '2026-09-27T10:15:00Z',
  finishedAt: null,
  forceReindex: false,
  requestedReports: ['CodeSmells', 'InyeccionDependenciasExtensiones'],
  lastSeq: 0,
  failedReports: [],
  error: null,
};

type EventDraft = Omit<AnalysisEventDto, 'seq' | 'runId' | 'ts'> & { ts?: string };

function build(list: EventDraft[]): AnalysisEventDto[] {
  const base = Date.parse('2026-09-27T10:15:00.000Z');
  return list.map((e, i) => ({
    seq: i + 1,
    runId: RUN_ID,
    ts: e.ts ?? new Date(base + i * 1000).toISOString(),
    ...e,
  }));
}

const ev = (
  type: string,
  source: string | null,
  phase: AnalysisEventDto['phase'],
  data: Record<string, unknown> | null,
  extra: Partial<AnalysisEventDto> = {},
): EventDraft => ({
  type,
  source,
  report: source?.startsWith('Auditor_') ? source.slice('Auditor_'.length) : null,
  phase,
  level: 'info',
  message: type,
  data,
  ...extra,
});

/**
 * Ejecución completa: 5 fases (6 steps de primer nivel: `enrich` tiene dos), 2 auditores en paralelo, un reintento, tools
 * y `run.completed` correcto.
 */
export const successfulRun: AnalysisEventDto[] = build([
  ev('run.started', null, null, {
    repositoryPath: 'C:\\repos\\miapp',
    databasePath: 'C:\\repos\\miapp\\.codebase\\codebase.db',
    reportsPath: 'C:\\repos\\miapp\\reports',
    forceReindex: false,
    requestedReports: ['CodeSmells', 'InyeccionDependenciasExtensiones'],
  }),
  ev('pipeline.started', null, null, { totalSteps: 6 }),
  ev('step.started', 'DetectAndIndexGraphStep', 'detect', { stepPath: 'CodebaseAnalysis/DetectAndIndexGraphStep', attempt: 1 }),
  ev('log', 'DetectAndIndexGraphStep', 'detect', { channel: 'indexer' }, { message: 'indexing 120 files' }),
  ev('log', 'DetectAndIndexGraphStep', 'detect', { channel: 'indexer' }, { message: 'done: 262 nodes' }),
  ev('step.completed', 'DetectAndIndexGraphStep', 'detect', { stepPath: 'x', success: true, durationMs: 4000, error: null }, { level: 'success' }),
  ev('step.started', 'LanguageEnricherStep', 'enrich', { stepPath: 'x', attempt: 1 }),
  ev('log', 'LanguageEnricherStep', 'enrich', { channel: 'pipeline' }, { message: '🔎 Lenguajes detectados en disco: C# (12 ficheros .cs).' }),
  ev('step.completed', 'LanguageEnricherStep', 'enrich', { stepPath: 'x', success: true, durationMs: 200, error: null }, { level: 'success' }),
  ev('step.started', 'BdpEnricherStep', 'enrich', { stepPath: 'x', attempt: 1 }),
  ev('log', 'BdpEnricherStep', 'enrich', { channel: 'pipeline' }, { message: 'Grafo ya enriquecido: se omite' }),
  ev('step.completed', 'BdpEnricherStep', 'enrich', { stepPath: 'x', success: true, durationMs: 50, error: null }, { level: 'success' }),
  ev('step.started', 'SqlFacetExtractorStep', 'facets', { stepPath: 'x', attempt: 1 }),
  ev('step.completed', 'SqlFacetExtractorStep', 'facets', { stepPath: 'x', success: true, durationMs: 300, error: null }, { level: 'success' }),
  ev('step.started', 'ParallelReportsExecution', 'reports', { stepPath: 'x', attempt: 1 }),
  ev('branch.started', 'ParallelReportsExecution', 'reports', { branch: 'Auditor_CodeSmells' }, { report: 'CodeSmells' }),
  ev('branch.started', 'ParallelReportsExecution', 'reports', { branch: 'Auditor_InyeccionDependenciasExtensiones' }, { report: 'InyeccionDependenciasExtensiones' }),
  ev('step.started', 'Auditor_CodeSmells', 'reports', { stepPath: 'x', attempt: 1 }),
  ev('step.started', 'Auditor_InyeccionDependenciasExtensiones', 'reports', { stepPath: 'x', attempt: 1 }),
  ev('llm.response', 'Auditor_CodeSmells', 'reports', {
    finishReason: 'ToolCalls',
    tokens: 900,
    model: 'gpt-5.4',
    contentLength: 0,
    toolCalls: [
      { id: 'c1', name: 'view_file', arguments: '{"path":"src/Orders/OrderService.cs"}' },
      { id: 'c2', name: 'graph_search', arguments: '{"name_pattern":"Order"}' },
    ],
  }),
  ev('tool.started', 'Auditor_CodeSmells', 'reports', { tool: 'view_file' }),
  ev('tool.completed', 'Auditor_CodeSmells', 'reports', { tool: 'view_file', success: true, durationMs: 120 }, { message: 'view_file (120 ms)' }),
  ev('tool.started', 'Auditor_CodeSmells', 'reports', { tool: 'graph_search' }),
  ev('tool.completed', 'Auditor_CodeSmells', 'reports', { tool: 'graph_search', success: false, durationMs: 30, error: 'timeout' }, { level: 'warning' }),
  ev('step.completed', 'Auditor_InyeccionDependenciasExtensiones', 'reports', { stepPath: 'x', success: false, durationMs: 2000, error: 'HTTP 429' }, { level: 'error' }),
  ev('step.started', 'Auditor_InyeccionDependenciasExtensiones', 'reports', { stepPath: 'x', attempt: 2 }, { level: 'warning' }),
  ev('report.written', 'Auditor_CodeSmells', 'reports', {
    fileName: '02_code_smells.md',
    reportType: 'CodeSmells',
    health: 'Amber',
    criticalFindings: 3,
    truncated: false,
  }),
  ev('step.completed', 'Auditor_CodeSmells', 'reports', {
    stepPath: 'x',
    success: true,
    durationMs: 9000,
    error: null,
    tokens: 15000,
    costUsd: 0.04,
    finishReason: 'Stop',
  }, { level: 'success' }),
  ev('branch.completed', 'ParallelReportsExecution', 'reports', { branch: 'Auditor_CodeSmells', success: true, durationMs: 9000, error: null }, { report: 'CodeSmells', level: 'success' }),
  ev('report.written', 'Auditor_InyeccionDependenciasExtensiones', 'reports', {
    fileName: '09_inyeccion_dependencias.md',
    reportType: 'InyeccionDependenciasExtensiones',
    health: 'Red',
    criticalFindings: 1,
    truncated: true,
  }),
  ev('step.completed', 'Auditor_InyeccionDependenciasExtensiones', 'reports', {
    stepPath: 'x',
    success: true,
    durationMs: 7000,
    error: null,
    tokens: 12000,
    costUsd: 0.03,
    finishReason: 'Length',
  }, { level: 'success' }),
  ev('branch.completed', 'ParallelReportsExecution', 'reports', {
    branch: 'Auditor_InyeccionDependenciasExtensiones',
    success: true,
    durationMs: 11000,
    error: null,
  }, { report: 'InyeccionDependenciasExtensiones', level: 'success' }),
  ev('step.completed', 'ParallelReportsExecution', 'reports', { stepPath: 'x', success: true, durationMs: 12000, error: null }, { level: 'success' }),
  ev('step.started', 'ExecutiveSynthesizerStep', 'synthesis', { stepPath: 'x', attempt: 1 }),
  ev('report.written', 'ExecutiveSynthesizerStep', 'synthesis', {
    fileName: '00_resumen_ejecutivo.md',
    reportType: null,
    health: 'Amber',
    criticalFindings: 4,
    truncated: false,
  }),
  ev('step.completed', 'ExecutiveSynthesizerStep', 'synthesis', { stepPath: 'x', success: true, durationMs: 3000, error: null }, { level: 'success' }),
  ev('pipeline.completed', null, null, { success: true, durationMs: 30000, error: null }, { level: 'success' }),
  ev('run.completed', null, null, {
    status: 'succeeded',
    exitCode: 0,
    durationMs: 31000,
    failedReports: [],
    executiveReportPath: 'C:\\repos\\miapp\\reports\\00_resumen_ejecutivo.md',
    error: null,
  }, { level: 'success' }),
]);

const CANCELLED_RUN_ID = '31638f921cf4';
const ALL_REPORTS = meta.reports.map((r) => r.type).concat(['FlujosEndToEnd', 'ErroresYBugs']);

/**
 * Ejecución cancelada durante el indexado, tal como la emitió el backend real el 2026-09-27 (F4, punto 3; 113
 * eventos, aquí con los `log` resumidos y las rutas y la lista de informes acortadas): el step de primer nivel y el pipeline terminan con
 * `level=error` y el error de la `TaskCanceledException`, y después llega `run.completed` con `status=cancelled`.
 */
export const cancelledRun: AnalysisEventDto[] = [
  ['run.started', null, null, 'info', 'Análisis iniciado en C:\\repos\\big', {
    repositoryPath: 'C:\\repos\\big',
    databasePath: 'C:\\repos\\big\\.codebase\\codebase.db',
    reportsPath: 'C:\\repos\\big\\reports',
    forceReindex: true,
    requestedReports: ALL_REPORTS,
  }],
  ['log', 'AnalysisRunner', null, 'info', '🚀 Ejecutando AnalysisPipeline completo con AITaskFramework...', { channel: 'pipeline' }],
  ['pipeline.started', 'CodebaseAnalysisPipeline', null, 'info', 'Pipeline CodebaseAnalysisPipeline iniciado (5 pasos)', { totalSteps: 5 }],
  ['step.started', 'DetectAndIndexGraphStep', 'detect', 'info', 'DetectAndIndexGraphStep iniciado', { stepPath: '', attempt: 1 }],
  ['log', 'DetectAndIndexGraphStep', 'detect', 'info', 'ℹ️ ForceReindex: no había ejecución anterior que guardar.', { channel: 'pipeline' }],
  ['log', 'DetectAndIndexGraphStep', 'detect', 'info', '   [indexador-err] level=info msg=parallel.extract.progress done=400 total=406', { channel: 'indexer' }],
  ['log', 'DetectAndIndexGraphStep', 'detect', 'info', '   [indexador-err] level=info msg=parallel.resolve.start files=406 workers=14', { channel: 'indexer' }],
  ['step.completed', 'DetectAndIndexGraphStep', 'detect', 'error', 'DetectAndIndexGraphStep falló: A task was canceled.', {
    stepPath: '',
    success: false,
    durationMs: 717,
    error: 'A task was canceled.',
  }],
  ['pipeline.completed', 'CodebaseAnalysisPipeline', null, 'error', 'Pipeline CodebaseAnalysisPipeline terminado con error: A task was canceled.', {
    success: false,
    durationMs: 718,
    error: 'A task was canceled.',
  }],
  ['run.completed', null, null, 'warning', 'Análisis cancelado.', {
    status: 'cancelled',
    exitCode: 1,
    durationMs: 723,
    failedReports: [],
    executiveReportPath: null,
    error: 'Análisis cancelado.',
  }],
].map(([type, source, phase, level, message, data], i) => ({
  seq: i + 1,
  runId: CANCELLED_RUN_ID,
  ts: new Date(Date.parse('2026-09-27T15:01:42.845Z') + i * 80).toISOString(),
  type: type as string,
  source: source as string | null,
  report: null,
  phase: phase as AnalysisEventDto['phase'],
  level: level as AnalysisEventDto['level'],
  message: message as string,
  data: data as Record<string, unknown>,
}));

/**
 * Cancelación con los auditores en marcha, construida sobre el contrato (§4.3, §5.4) y el formato de error que emite
 * el backend real al cancelar (`ErrorStepResult.FromException` con el mensaje de la excepción, `RetryMiddleware.cs:53`).
 * `CodeSmells` ya había fallado de verdad (HTTP 429) antes de la cancelación: debe seguir como fallido.
 */
export const cancelledDuringReports: AnalysisEventDto[] = build([
  ev('run.started', null, null, { requestedReports: ['CodeSmells', 'InyeccionDependenciasExtensiones'] }),
  ev('pipeline.started', 'CodebaseAnalysisPipeline', null, { totalSteps: 6 }),
  ev('step.started', 'DetectAndIndexGraphStep', 'detect', { stepPath: '', attempt: 1 }),
  ev('step.completed', 'DetectAndIndexGraphStep', 'detect', { stepPath: '', success: true, durationMs: 4000, error: null }, { level: 'success' }),
  ev('step.started', 'LanguageEnricherStep', 'enrich', { stepPath: '', attempt: 1 }),
  ev('step.completed', 'LanguageEnricherStep', 'enrich', { stepPath: '', success: true, durationMs: 20, error: null }, { level: 'success' }),
  ev('step.started', 'BdpEnricherStep', 'enrich', { stepPath: '', attempt: 1 }),
  ev('step.completed', 'BdpEnricherStep', 'enrich', { stepPath: '', success: true, durationMs: 50, error: null }, { level: 'success' }),
  ev('step.started', 'SqlFacetExtractorStep', 'facets', { stepPath: '', attempt: 1 }),
  ev('step.completed', 'SqlFacetExtractorStep', 'facets', { stepPath: '', success: true, durationMs: 300, error: null }, { level: 'success' }),
  ev('step.started', 'ParallelReportsExecution', 'reports', { stepPath: '', attempt: 1 }),
  ev('step.started', 'Auditor_CodeSmells', 'reports', { stepPath: '', attempt: 1 }),
  ev('step.started', 'Auditor_InyeccionDependenciasExtensiones', 'reports', { stepPath: '', attempt: 1 }),
  ev('step.completed', 'Auditor_CodeSmells', 'reports', { stepPath: '', success: false, durationMs: 2000, error: 'HTTP 429' }, { level: 'error' }),
  ev('branch.completed', 'ParallelReportsExecution', 'reports', { branch: 'Auditor_CodeSmells', success: false, durationMs: 2000, error: 'HTTP 429' }, { report: 'CodeSmells', level: 'error' }),
  ev('tool.started', 'Auditor_InyeccionDependenciasExtensiones', 'reports', { tool: 'view_file' }),
  ev('step.completed', 'Auditor_InyeccionDependenciasExtensiones', 'reports', {
    stepPath: '',
    success: false,
    durationMs: 5000,
    error: 'The operation was canceled.',
  }, { level: 'error', message: 'Auditor_InyeccionDependenciasExtensiones falló: The operation was canceled.' }),
  ev('branch.completed', 'ParallelReportsExecution', 'reports', {
    branch: 'Auditor_InyeccionDependenciasExtensiones',
    success: false,
    durationMs: 5000,
    error: 'The operation was canceled.',
  }, { report: 'InyeccionDependenciasExtensiones', level: 'error' }),
  ev('step.completed', 'ParallelReportsExecution', 'reports', { stepPath: '', success: false, durationMs: 5200, error: 'A task was canceled.' }, { level: 'error' }),
  ev('pipeline.completed', 'CodebaseAnalysisPipeline', null, { success: false, durationMs: 9800, error: 'A task was canceled.' }, { level: 'error' }),
  ev('run.completed', null, null, {
    status: 'cancelled',
    exitCode: 1,
    durationMs: 9900,
    failedReports: ['CodeSmells'],
    executiveReportPath: null,
    error: 'Análisis cancelado.',
  }, { level: 'warning' }),
]);

/**
 * Regeneración que continúa `previous` con el mismo `runId` y el `seq` siguiente, en el orden de tipos que emite
 * `FakeAnalysisWorkflow.RegenerateAsync` (CodebaseAnalysisWeb.Tests): preparación y facetas (fase `facets`), los auditores
 * pedidos, la síntesis si se pide y otro `run.completed`. `failing` son los auditores que vuelven a fallar; `failedReports`,
 * lo que manda el servidor (ficheros).
 */
export function regenerationAfter(
  previous: AnalysisEventDto[],
  reports: string[],
  options: { synthesis?: boolean; failing?: string[]; failedReports?: string[] } = {},
): AnalysisEventDto[] {
  const { synthesis = true, failing = [], failedReports = [] } = options;
  const last = previous[previous.length - 1];
  const allFailed = reports.every((r) => failing.includes(r));
  const drafts: EventDraft[] = [
    ev('run.regeneration_started', null, null, { reports, synthesis }),
    ev('pipeline.started', 'CodebaseAnalysisRegeneration', null, { totalSteps: synthesis ? 4 : 3 }),
    ev('step.started', 'PrepareRegenerationStep', 'facets', { stepPath: '', attempt: 1 }),
    ev('step.completed', 'PrepareRegenerationStep', 'facets', { stepPath: '', success: true, durationMs: 40, error: null }, { level: 'success' }),
    ev('step.started', 'SqlFacetExtractorStep', 'facets', { stepPath: '', attempt: 1 }),
    ev('step.completed', 'SqlFacetExtractorStep', 'facets', { stepPath: '', success: true, durationMs: 300, error: null }, { level: 'success' }),
    ev('step.started', 'ParallelReportsExecution', 'reports', { stepPath: '', attempt: 1 }),
  ];
  for (const r of reports) {
    drafts.push(ev('branch.started', 'ParallelReportsExecution', 'reports', { branch: `Auditor_${r}` }, { report: r }));
    drafts.push(ev('step.started', `Auditor_${r}`, 'reports', { stepPath: '', attempt: 1 }));
  }
  for (const r of reports) {
    if (failing.includes(r)) {
      drafts.push(ev('step.completed', `Auditor_${r}`, 'reports', { stepPath: '', success: false, durationMs: 800, error: 'HTTP 500' }, { level: 'error' }));
      drafts.push(ev('branch.completed', 'ParallelReportsExecution', 'reports', { branch: `Auditor_${r}`, success: false, durationMs: 800, error: 'HTTP 500' }, { report: r, level: 'error' }));
      continue;
    }
    drafts.push(ev('report.written', `Auditor_${r}`, 'reports', {
      fileName: (REPORT_FILE_NAMES as Record<string, string>)[r] ?? `${r}.md`,
      reportType: r,
      health: 'Green',
      criticalFindings: 0,
      truncated: false,
    }, { level: 'success' }));
    drafts.push(ev('step.completed', `Auditor_${r}`, 'reports', { stepPath: '', success: true, durationMs: 800, error: null, tokens: 1000 }, { level: 'success' }));
    drafts.push(ev('branch.completed', 'ParallelReportsExecution', 'reports', { branch: `Auditor_${r}`, success: true, durationMs: 800, error: null }, { report: r, level: 'success' }));
  }
  drafts.push(ev('step.completed', 'ParallelReportsExecution', 'reports', {
    stepPath: '',
    success: !allFailed,
    durationMs: 900,
    error: allFailed ? 'HTTP 500' : null,
  }, { level: allFailed ? 'error' : 'success' }));
  if (synthesis && !allFailed) {
    drafts.push(ev('step.started', 'ExecutiveSynthesizerStep', 'synthesis', { stepPath: '', attempt: 1 }));
    drafts.push(ev('report.written', 'ExecutiveSynthesizerStep', 'synthesis', {
      fileName: '00_resumen_ejecutivo.md',
      reportType: null,
      health: 'Green',
      criticalFindings: 0,
      truncated: false,
    }, { level: 'success' }));
    drafts.push(ev('step.completed', 'ExecutiveSynthesizerStep', 'synthesis', { stepPath: '', success: true, durationMs: 1200, error: null }, { level: 'success' }));
  }
  drafts.push(ev('pipeline.completed', 'CodebaseAnalysisRegeneration', null, { success: !allFailed, durationMs: 2500, error: null }));
  drafts.push(ev('run.completed', null, null, {
    status: failedReports.length > 0 ? 'completed_with_failures' : 'succeeded',
    exitCode: failedReports.length > 0 ? 1 : 0,
    durationMs: 2600,
    failedReports,
    executiveReportPath: synthesis && !allFailed ? 'C:\\repos\\miapp\\reports\\00_resumen_ejecutivo.md' : null,
    error: null,
  }, { level: failedReports.length > 0 ? 'warning' : 'success', message: 'Regeneración completada.' }));

  const base = Date.parse(last.ts) + 60_000;
  return drafts.map((d, i) => ({ seq: last.seq + i + 1, runId: last.runId, ts: new Date(base + i * 1000).toISOString(), ...d }));
}

// ── Chat ────────────────────────────────────────────────────────────────

export const completedTurn: ChatTurn = {
  id: 't-0001',
  askedAt: '2026-09-27T10:15:02Z',
  question: '¿Qué informes hay y cuál es el peor?',
  answer: 'El peor es **05 Errores y bugs** (rojo).',
  status: 'completed',
  error: null,
  model: 'gpt-5.4',
  tokens: 1834,
  costUsd: 0.004,
  durationMs: 2143,
  toolCalls: [{ name: 'list_reports', arguments: '{}', success: true, durationMs: 12 }],
};

/** Turno con una iteración de tools (`list_reports`) y la respuesta en la segunda iteración (§4.4). */
export const chatStream: ChatStreamEvent[] = [
  { type: 'turn.started', turnId: 't-0001', askedAt: '2026-09-27T10:15:02Z' },
  { type: 'delta', text: 'Voy a mirar ' },
  { type: 'delta', text: 'los informes.' },
  { type: 'tool.calls', calls: [{ id: 'call_1', name: 'list_reports', arguments: '{}' }] },
  { type: 'tool.started', name: 'list_reports' },
  { type: 'tool.completed', name: 'list_reports', success: true, durationMs: 12, error: null },
  { type: 'delta', text: 'El peor es ' },
  { type: 'delta', text: '**05 Errores y bugs** (rojo).' },
  { type: 'turn.completed', turn: completedTurn },
];

export function sse(event: string, data: unknown, id?: number): string {
  return `${id !== undefined ? `id: ${id}\n` : ''}event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
}

/** Cuerpo `text/event-stream` controlable desde el test: se empujan chunks y se cierra a mano. */
export function controlledStream() {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  let cancelled = false;
  const encoder = new TextEncoder();
  const stream = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c;
    },
    cancel() {
      cancelled = true;
    },
  });
  return {
    stream,
    push: (text: string) => controller.enqueue(encoder.encode(text)),
    close: () => controller.close(),
    get cancelled() {
      return cancelled;
    },
  };
}

export function streamOf(chunks: string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream<Uint8Array>({
    start(c) {
      for (const chunk of chunks) c.enqueue(encoder.encode(chunk));
      c.close();
    },
  });
}

export function sseResponse(body: ReadableStream<Uint8Array>, status = 200): Response {
  return new Response(body, { status, headers: { 'Content-Type': 'text/event-stream' } });
}

export function jsonResponse(data: unknown, status = 200): Response {
  return new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json' } });
}

// ── fetch falso por ruta ─────────────────────────────────────────────────

export interface FetchCall {
  method: string;
  url: string;
  body: unknown;
  headers: Record<string, string>;
  signal: AbortSignal | null;
}

type Handler = (call: FetchCall) => Response | Promise<Response>;

/** Instala un `fetch` falso que responde según «MÉTODO ruta» (sin query) y guarda cada llamada. */
export function mockFetch(routes: Record<string, Handler>) {
  const calls: FetchCall[] = [];
  const fn = async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const url = String(input);
    const method = (init.method ?? 'GET').toUpperCase();
    const path = url.split('?')[0];
    const call: FetchCall = {
      method,
      url,
      body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined,
      headers: (init.headers ?? {}) as Record<string, string>,
      signal: init.signal ?? null,
    };
    calls.push(call);
    const handler = routes[`${method} ${path}`];
    if (!handler) return new Response(JSON.stringify({ error: 'not_found', message: `Sin ruta: ${method} ${path}` }), { status: 404 });
    return handler(call);
  };
  return { fn, calls };
}

// ── V2-B/C/D ─────────────────────────────────────────────────────────────

/** `report.changed` de un `update_report` sobre el 05 (plan v2 §5.3, decisión 0-9). */
export const reportUpdated: ReportChange = {
  id: '05_errores_y_bugs.md',
  kind: 'catalog',
  fileName: '05_errores_y_bugs.md',
  title: 'Errores y bugs',
  action: 'updated',
  versionId: 'v0002',
  previousVersionId: 'v0001',
  graphRefreshed: false,
  embeddingsOutdated: true,
};

/** `report.changed` de un `create_report`: sin versión anterior, el chip ofrece «Borrar». */
export const reportCreated: ReportChange = {
  id: 'custom/estudio-de-colas.md',
  kind: 'custom',
  fileName: 'estudio-de-colas.md',
  title: 'Estudio de colas',
  action: 'created',
  versionId: 'v0001',
  previousVersionId: null,
  graphRefreshed: false,
  embeddingsOutdated: false,
};

export function customReport(fileName: string, title: string, extra: Partial<ReportSummaryDto> = {}): ReportSummaryDto {
  return {
    fileName,
    reportType: null,
    title,
    health: 'Unknown',
    criticalFindings: null,
    sizeBytes: 2048,
    lastModified: '2026-09-27T11:00:00Z',
    kind: 'custom',
    id: `custom/${fileName}`,
    origin: 'chat',
    modifiedByChat: true,
    versionCount: 1,
    knowledge: absentKnowledge,
    ...extra,
  };
}

const VIEWER_OF: Record<DocumentFamily, DocumentViewer> = {
  markdown: 'markdown', text: 'text', code: 'markdown', structured: 'markdown', csv: 'markdown', html: 'html',
  pdf: 'pdf', docx: 'markdown', xlsx: 'markdown', pptx: 'markdown', image: 'image', svg: 'image',
};

/** Documento de ejemplo de una familia (el visor sale de la tabla de §7.2). */
export function documentOf(family: DocumentFamily, extra: Partial<DocumentDetailDto> = {}): DocumentDetailDto {
  const ext = { markdown: '.md', text: '.txt', code: '.cs', structured: '.json', csv: '.csv', html: '.html', pdf: '.pdf',
    docx: '.docx', xlsx: '.xlsx', pptx: '.pptx', image: '.png', svg: '.svg' }[family];
  return {
    id: `20260927-111203-${family.slice(0, 4).padEnd(4, 'x')}`,
    title: `Documento ${family}`,
    originalFileName: `documento${ext}`,
    extension: ext,
    family,
    mediaType: 'application/octet-stream',
    viewer: VIEWER_OF[family],
    sizeBytes: 812334,
    sha256: 'abc',
    uploadedAt: '2026-09-27T11:12:03Z',
    useInAnalysis: true,
    extraction: {
      status: family === 'image' ? 'pending_description' : 'ok',
      charCount: 100,
      sectionCount: 2,
      pageCount: family === 'pdf' ? 12 : null,
      truncated: false,
      warnings: [],
    },
    knowledge: absentKnowledge,
    sections: [],
    description: null,
    ...extra,
  };
}

export const withFeatures = (features: Partial<MetaDto['features']>): MetaDto => ({
  ...meta,
  features: { ...meta.features, ...features },
});

export const enabledFeatures: MetaDto['features'] = {
  embeddings: { enabled: true, model: 'text-embedding-3-small', dimensions: 1536, disabledReason: null },
  imageDescription: { enabled: true, model: 'gpt-5.4', disabledReason: null },
};
