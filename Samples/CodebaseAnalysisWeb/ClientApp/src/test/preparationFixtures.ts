// Ejecución con la preparación por subsistema y dos informes en modo B (`CodebaseAnalysis:Preparation:Mode = Subsystems`), con
// la forma que emite el servidor (`AnalysisEventMapper`): los cuatro pasos de preparación van en la fase `facets`, el
// envoltorio del informe es `Auditor_<tipo>` y sus investigaciones son sub-steps `Investigation_<tipo>_<id>` que se invocan
// sin middleware (no hay step.started/step.completed de ellas): solo sus llm.response y tool.*, con el `report` del informe.

import type { AnalysisEventDto, RunStatusDto } from '../api/types';
import { RUN_ID } from './fixtures';

export const MODE_B_REPORTS = ['AnalisisFuncional', 'FlujosEndToEnd'] as const;

export const preparedRunStatus: RunStatusDto = {
  runId: RUN_ID,
  repositoryPath: 'C:\\repos\\miapp',
  status: 'running',
  startedAt: '2026-10-04T10:00:00Z',
  finishedAt: null,
  forceReindex: false,
  requestedReports: [...MODE_B_REPORTS],
  lastSeq: 0,
  failedReports: [],
  error: null,
};

type Draft = Omit<AnalysisEventDto, 'seq' | 'runId' | 'ts'>;

const ev = (
  type: string,
  source: string | null,
  phase: AnalysisEventDto['phase'],
  data: Record<string, unknown> | null,
  extra: Partial<AnalysisEventDto> = {},
): Draft => ({ type, source, report: null, phase, level: 'info', message: type, data, ...extra });

const step = (name: string, phase: AnalysisEventDto['phase'], durationMs = 1000): Draft[] => [
  ev('step.started', name, phase, { stepPath: 'x', attempt: 1 }),
  ev('step.completed', name, phase, { stepPath: 'x', success: true, durationMs, error: null }, { level: 'success' }),
];

/** Los eventos de una investigación: una llamada al LLM con una tool y su respuesta, todo en el carril del informe. */
const investigation = (report: string, subsystem: string): Draft[] => {
  const source = `Investigation_${report}_${subsystem}`;
  return [
    ev('log', `Auditor_${report}`, 'reports', { channel: 'pipeline' }, { report, message: `🔬 Investigando '${subsystem}'...` }),
    ev(
      'llm.response',
      source,
      'reports',
      {
        finishReason: 'ToolCalls',
        tokens: 500,
        promptTokens: 400,
        completionTokens: 100,
        costUsd: 0.01,
        model: 'gpt-5.4',
        toolCalls: [{ id: 'c1', name: 'view_file', arguments: `{"path":"src/${subsystem}/Service.cs"}` }],
      },
      { report },
    ),
    ev('tool.started', source, 'reports', { tool: 'view_file' }, { report }),
    ev('tool.completed', source, 'reports', { tool: 'view_file', success: true, durationMs: 40 }, { report, message: 'view_file (40 ms)' }),
    ev('llm.response', source, 'reports', { finishReason: 'Stop', tokens: 300, promptTokens: 250, completionTokens: 50, costUsd: 0.005, model: 'gpt-5.4', toolCalls: [] }, { report }),
  ];
};

const consolidation = (report: string, fileName: string): Draft[] => [
  ev('llm.response', `Auditor_${report}`, 'reports', { finishReason: 'Stop', tokens: 2000, promptTokens: 1500, completionTokens: 500, costUsd: 0.03, model: 'gpt-5.4', toolCalls: [] }, { report }),
  ev('report.written', `Auditor_${report}`, 'reports', { fileName, reportType: report, health: 'Amber', criticalFindings: 2, truncated: false }, { report }),
  ev('step.completed', `Auditor_${report}`, 'reports', { stepPath: 'x', success: true, durationMs: 9000, error: null, tokens: 2800, costUsd: 0.045, finishReason: 'Stop' }, { report, level: 'success' }),
  ev('branch.completed', 'ParallelReportsExecution', 'reports', { branch: `Auditor_${report}`, success: true, durationMs: 9000, error: null }, { report, level: 'success' }),
];

const drafts: Draft[] = [
  ev('run.started', null, null, {
    repositoryPath: 'C:\\repos\\miapp',
    databasePath: 'C:\\repos\\miapp\\.codebase\\codebase.db',
    reportsPath: 'C:\\repos\\miapp\\reports',
    forceReindex: false,
    requestedReports: [...MODE_B_REPORTS],
  }),
  ev('pipeline.started', null, null, { totalSteps: 10 }),
  ...step('DetectAndIndexGraphStep', 'detect', 4000),
  ...step('LanguageEnricherStep', 'enrich', 200),
  ...step('DotNetEnricherStep', 'enrich', 50),
  ...step('SqlFacetExtractorStep', 'facets', 300),
  ...step('RepositoryInventoryStep', 'facets', 700),
  // DomainDiscoveryStep nombra con una llamada al LLM (sub-step DomainNaming), que va en la misma fase.
  ev('step.started', 'DomainDiscoveryStep', 'facets', { stepPath: 'x', attempt: 1 }),
  ev('step.started', 'DomainNaming', 'facets', { stepPath: 'x', attempt: 1 }),
  ev('llm.response', 'DomainNaming', 'facets', { finishReason: 'Stop', tokens: 700, promptTokens: 600, completionTokens: 100, costUsd: 0.01, model: 'gpt-5.4', toolCalls: [] }),
  ev('step.completed', 'DomainNaming', 'facets', { stepPath: 'x', success: true, durationMs: 2000, error: null }, { level: 'success' }),
  ev('step.completed', 'DomainDiscoveryStep', 'facets', { stepPath: 'x', success: true, durationMs: 3000, error: null }, { level: 'success' }),
  ...step('CoveragePlanningStep', 'facets', 100),
  ...step('TimeBudgetStep', 'facets', 10),
  ev('step.started', 'ParallelReportsExecution', 'reports', { stepPath: 'x', attempt: 1 }),
  ...MODE_B_REPORTS.map((r) => ev('branch.started', 'ParallelReportsExecution', 'reports', { branch: `Auditor_${r}` }, { report: r })),
  ...MODE_B_REPORTS.map((r) => ev('step.started', `Auditor_${r}`, 'reports', { stepPath: 'x', attempt: 1 }, { report: r })),
  ...investigation('AnalisisFuncional', 'catalog'),
  ...investigation('FlujosEndToEnd', 'catalog'),
  ...investigation('AnalisisFuncional', 'ledger'),
  ...investigation('FlujosEndToEnd', 'ledger'),
  ...consolidation('AnalisisFuncional', '01_analisis_funcional.md'),
  ...consolidation('FlujosEndToEnd', '04_flujos_end_to_end.md'),
  ev('step.completed', 'ParallelReportsExecution', 'reports', { stepPath: 'x', success: true, durationMs: 20000, error: null }, { level: 'success' }),
  ...step('ExecutiveSynthesizerStep', 'synthesis', 5000),
  ev('pipeline.completed', null, null, { success: true, durationMs: 40000, error: null }, { level: 'success' }),
  ev('run.completed', null, null, { status: 'succeeded', exitCode: 0, durationMs: 40000, failedReports: [], executiveReportPath: '00_resumen_ejecutivo.md' }, { level: 'success' }),
];

const base = Date.parse('2026-10-04T10:00:00.000Z');

export const preparedRun: AnalysisEventDto[] = drafts.map((d, i) => ({
  seq: i + 1,
  runId: RUN_ID,
  ts: new Date(base + i * 1000).toISOString(),
  ...d,
}));
