import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { AnalysisEventDto, ChatSession } from '../api/types';
import { completedTurn, meta, RUN_ID, runningStatus, successfulRun } from '../test/fixtures';
import { createRunState, MAX_TOOL_OUTPUTS, runReducer, type RunState } from '../state/runReducer';
import type { LiveTurn } from '../state/chatReducer';
import { AuditorGrid } from './AuditorGrid';
import { ChatThread } from './ChatThread';
import { prettyJson } from './ToolCallDetail';

const replay = (events: AnalysisEventDto[], base: RunState = createRunState(runningStatus)): RunState =>
  runReducer(base, { type: 'events', events });

const now = Date.parse('2026-09-27T10:30:00Z');

const event = (seq: number, type: string, data: Record<string, unknown>, extra: Partial<AnalysisEventDto> = {}): AnalysisEventDto => ({
  seq,
  runId: RUN_ID,
  ts: new Date(Date.parse('2026-09-27T10:20:00Z') + seq * 1000).toISOString(),
  type,
  source: 'Auditor_CodeSmells',
  report: 'CodeSmells',
  phase: 'reports',
  level: 'info',
  message: type,
  data,
  ...extra,
});

/** Una llamada con entrada y salida en los eventos (servidor con W2). */
const withPayload = (seq: number, output: string, extra: Record<string, unknown> = {}): AnalysisEventDto[] => [
  event(seq, 'tool.started', { tool: 'view_file', arguments: '{"path":"src/A.cs","start_line":1}' }),
  event(seq + 1, 'tool.completed', {
    tool: 'view_file',
    success: true,
    durationMs: 42,
    error: null,
    arguments: '{"path":"src/A.cs","start_line":1}',
    output,
    outputTruncated: false,
    outputBytes: output.length,
    ...extra,
  }),
];

describe('runReducer: detalle de las tool calls (W2)', () => {
  it('empareja inicio y fin por step y tool, con argumentos y salida', () => {
    const state = replay([...successfulRun.slice(0, 20), ...withPayload(500, '{"lines":3}')]);
    const calls = state.auditors.CodeSmells.calls;
    const last = calls[calls.length - 1];
    expect(last).toMatchObject({ tool: 'view_file', source: 'Auditor_CodeSmells', durationMs: 42, success: true, output: '{"lines":3}', outputBytes: 11 });
    expect(last.arguments).toBe('{"path":"src/A.cs","start_line":1}');
    expect(state.auditors.CodeSmells.calls.length).toBe(state.auditors.CodeSmells.toolTimes.length);
  });

  it('con un servidor anterior (sin datos en los eventos) toma los argumentos del llm.response y no hay salida', () => {
    const state = replay(successfulRun);
    const [first, second] = state.auditors.CodeSmells.calls;
    expect(first).toMatchObject({ tool: 'view_file', arguments: '{"path":"src/Orders/OrderService.cs"}', output: null, success: true });
    expect(second).toMatchObject({ tool: 'graph_search', success: false, error: 'timeout' });
  });

  it(`conserva solo las últimas ${MAX_TOOL_OUTPUTS} salidas`, () => {
    const events = Array.from({ length: MAX_TOOL_OUTPUTS + 2 }, (_, i) => withPayload(1000 + i * 2, `salida ${i}`)).flat();
    const calls = replay([...successfulRun.slice(0, 20), ...events]).auditors.CodeSmells.calls;
    const withOutput = calls.filter((c) => c.output !== null);
    expect(withOutput).toHaveLength(MAX_TOOL_OUTPUTS);
    expect(calls.filter((c) => c.outputDropped).map((c) => c.output)).toEqual([null, null]);
    expect(withOutput[withOutput.length - 1].output).toBe(`salida ${MAX_TOOL_OUTPUTS + 1}`);
  });
});

describe('ToolCallDetail', () => {
  it('formatea JSON y deja el texto que no lo es', () => {
    expect(prettyJson('{"a":1}')).toBe('{\n  "a": 1\n}');
    expect(prettyJson('{"a":1')).toBe('{"a":1');
    expect(prettyJson('texto')).toBe('texto');
  });

  it('una marca del carril abre el detalle con paso, entrada, salida truncada y «Copiar»', async () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const state = replay([...successfulRun.slice(0, 24), ...withPayload(500, '{"lines":3}', { outputTruncated: true, outputBytes: 40000 })]);
    render(<AuditorGrid state={state} meta={meta} now={now} />);

    const ticks = screen.getAllByTitle(/^view_file · /);
    await user.click(ticks[ticks.length - 1]);

    const dialog = screen.getByRole('dialog', { name: /view_file/ });
    expect(within(dialog).getByText('Auditor_CodeSmells')).toBeInTheDocument();
    expect(within(dialog).getByText('42 ms')).toBeInTheDocument();
    expect(within(dialog).getByText(/"path": "src\/A.cs"/)).toBeInTheDocument();
    expect(within(dialog).getByText(/"lines": 3/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Salida truncada: se muestran 11 B de 39 KB/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Copiar la salida' }));
    expect(writeText).toHaveBeenCalledWith('{\n  "lines": 3\n}');
    expect(within(dialog).getByRole('button', { name: 'Copiar la salida' })).toHaveTextContent('Copiado');

    // «Anterior» va a la llamada previa del carril, que falló.
    await user.click(within(dialog).getByRole('button', { name: 'Anterior' }));
    expect(screen.getByRole('dialog', { name: /graph_search/ })).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('timeout');
  });

  it('el número de tools del carril abre la última llamada con el teclado', async () => {
    const user = userEvent.setup();
    render(<AuditorGrid state={replay(successfulRun)} meta={meta} now={now} />);

    await user.click(screen.getByRole('button', { name: 'Ver las 2 llamadas a herramientas de Code smells' }));

    const dialog = screen.getByRole('dialog', { name: /graph_search/ });
    expect(within(dialog).getByText('La herramienta falló: no hay salida.')).toBeInTheDocument();
    expect(within(dialog).getByText('2 de 2')).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Cerrar' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});

describe('ChatThread: detalle de las tools', () => {
  const session: ChatSession = {
    id: 's1',
    title: 't',
    createdAt: completedTurn.askedAt,
    updatedAt: completedTurn.askedAt,
    turns: [{ ...completedTurn, toolCalls: [{ name: 'list_reports', arguments: '{}', success: true, durationMs: 5 }] }],
  } as ChatSession;

  it('un turno guardado enseña la entrada y avisa de que el historial no guarda la salida', async () => {
    const user = userEvent.setup();
    render(<ChatThread session={session} live={null} />);

    await user.click(screen.getByRole('button', { name: /list_reports/ }));

    const dialog = screen.getByRole('dialog', { name: /list_reports/ });
    expect(within(dialog).getByText(/no guarda la salida/)).toBeInTheDocument();
  });

  it('el turno en curso enseña la salida que llegó en tool.completed', async () => {
    const user = userEvent.setup();
    const live: LiveTurn = {
      sessionId: 's1',
      question: '¿Qué hay?',
      turnId: 't2',
      askedAt: completedTurn.askedAt,
      segments: [],
      thinking: '',
      tools: [
        { key: 1, id: 'c1', name: 'read_report', arguments: '{"file_name":"02"}', status: 'ok', durationMs: 9, error: null, afterSegment: 0, output: '# Code smells', outputTruncated: false, outputBytes: 13 },
      ],
      reports: [],
      status: 'streaming',
    };
    render(<ChatThread session={session} live={live} />);

    await user.click(screen.getByRole('button', { name: /read_report/ }));

    const dialog = screen.getByRole('dialog', { name: /read_report/ });
    expect(within(dialog).getByText('# Code smells')).toBeInTheDocument();
    expect(within(dialog).getByText(/"file_name": "02"/)).toBeInTheDocument();
  });
});
