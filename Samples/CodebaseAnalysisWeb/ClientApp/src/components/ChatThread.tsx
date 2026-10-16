import { useEffect, useRef, useState } from 'react';
import type { ChatModelDto, ChatSession, ChatToolCall, ChatTurn } from '../api/types';
import { formatCost, formatDuration, formatNumber, formatTime } from '../format';
import type { ReportChange } from '../api/types';
import { openSegmentText, turnReportChanges, USER_STOPPED_MESSAGE, type LiveTool, type LiveTurn } from '../state/chatReducer';
import { AuditSummary, ChatAnswer } from './ChatAnswer';
import { MarkdownView } from './MarkdownView';
import { ReportChangeChip } from './ReportChangeChip';
import { ToolCallChip, type ToolChipStatus } from './ToolCallChip';
import { ToolCallDetail, type ToolCallView } from './ToolCallDetail';

/**
 * La llamada abierta en el detalle, que navega entre las de su turno. Del turno en curso se vuelve a leer a cada render, para que la
 * salida aparezca al llegar; `views` es la foto del momento de abrirla, que se sigue enseñando cuando el turno termina (el historial
 * no guarda las salidas).
 */
interface OpenTools {
  live: boolean;
  views: ToolCallView[];
  index: number;
}

type OpenTool = (views: ToolCallView[], index: number, live?: boolean) => void;

/** Una llamada de un turno guardado: el historial no guarda la salida. */
function savedView(t: ChatToolCall): ToolCallView {
  return {
    name: t.name,
    durationMs: t.durationMs,
    status: chipStatus(t.success),
    arguments: t.arguments,
    outputNote: 'El historial del chat no guarda la salida de las herramientas: solo se ve mientras llega la respuesta.',
  };
}

function liveView(t: LiveTool): ToolCallView {
  return {
    name: t.name,
    durationMs: t.durationMs,
    status: t.status,
    error: t.error,
    arguments: t.arguments,
    output: t.output ?? null,
    outputTruncated: t.outputTruncated,
    outputBytes: t.outputBytes,
  };
}

/** Acciones de los chips de informe (V2-B) y de las referencias de las respuestas. */
export interface ReportChipHandlers {
  /** Abre en el visor: el id de un informe, `doc/<id>` o `src/<ruta>[:ini-fin]`. */
  onOpenReport?: (id: string) => void;
  onReportChanged?: () => void;
}

function ReportChanges({
  changes,
  handlers,
  disabledReason = null,
}: {
  changes: ReportChange[];
  handlers: ReportChipHandlers;
  disabledReason?: string | null;
}) {
  if (changes.length === 0) return null;
  return (
    <div className="turn__reports" aria-label="Informes escritos">
      {changes.map((c, i) => (
        <ReportChangeChip
          key={`${c.id}-${c.versionId}-${i}`}
          change={c}
          onOpen={handlers.onOpenReport}
          onChanged={handlers.onReportChanged}
          disabledReason={disabledReason}
        />
      ))}
    </div>
  );
}

function chipStatus(success: boolean | null | undefined): ToolChipStatus {
  return success === true ? 'ok' : success === false ? 'error' : 'unknown';
}

/** Etiqueta del modelo que dio la respuesta: la del catálogo si sigue ahí; si no, el deployment que guardó el turno. */
function modelLabel(turn: ChatTurn, models: ChatModelDto[]): string | null | undefined {
  return models.find((m) => m.id === turn.modelId)?.label ?? turn.model;
}

function TurnView({
  turn,
  handlers,
  models,
  onOpenTool,
}: {
  turn: ChatTurn;
  handlers: ReportChipHandlers;
  models: ChatModelDto[];
  onOpenTool: OpenTool;
}) {
  const tools = turn.toolCalls ?? [];
  const truncated = (turn.finishReason ?? '').toLowerCase() === 'length';
  return (
    <article className={`turn turn--${turn.status}`} aria-label={`Pregunta de las ${formatTime(turn.askedAt)}`}>
      <div className="turn__question">
        <p>{turn.question}</p>
      </div>
      <div className="turn__answer">
        {tools.length > 0 && (
          <div className="turn__tools" aria-label="Herramientas usadas">
            {tools.map((t, i) => (
              <ToolCallChip
                key={i}
                name={t.name}
                arguments={t.arguments}
                status={chipStatus(t.success)}
                durationMs={t.durationMs}
                onOpen={() => onOpenTool(tools.map(savedView), i)}
              />
            ))}
          </div>
        )}
        <ReportChanges changes={turnReportChanges(turn)} handlers={handlers} />
        {turn.answer && <ChatAnswer source={turn.answer} audit={turn.audit} onOpen={handlers.onOpenReport} />}
        <AuditSummary audit={turn.audit} />
        {turn.status !== 'completed' && (
          <p className={`turn__status turn__status--${turn.status}`}>
            {turn.status === 'cancelled'
              ? turn.error === USER_STOPPED_MESSAGE
                ? 'Respuesta parada por el usuario.'
                : `Respuesta cancelada${turn.error ? `: ${turn.error}` : '.'}`
              : `La respuesta falló${turn.error ? `: ${turn.error}` : '.'}`}
          </p>
        )}
        {truncated && <p className="turn__status turn__status--warning">La respuesta se cortó al agotar MaxOutputTokens.</p>}
        {turn.status === 'completed' && (
          <p className="turn__meta">
            {[
              modelLabel(turn, models),
              turn.tokens != null ? `${formatNumber(turn.tokens)} tokens` : null,
              // Coste 0 = el proveedor no lo informa (D8): se omite en vez de mostrar «0,000 $».
              turn.costUsd != null && turn.costUsd > 0 ? formatCost(turn.costUsd) : null,
              turn.durationMs != null ? formatDuration(turn.durationMs) : null,
            ]
              .filter(Boolean)
              .join(' · ')}
          </p>
        )}
      </div>
    </article>
  );
}

function LiveTurnView({ live, handlers, onOpenTool }: { live: LiveTurn; handlers: ReportChipHandlers; onOpenTool: OpenTool }) {
  const closed = live.segments.filter((s) => s.closed);
  const final = openSegmentText(live);
  const toolsAfter = (n: number) => live.tools.filter((t) => t.afterSegment === n);
  return (
    <article className="turn turn--live" aria-busy="true" aria-label="Respuesta en curso">
      <div className="turn__question">
        <p>{live.question}</p>
      </div>
      <div className="turn__answer">
        {live.thinking && (
          <details className="turn__thinking">
            <summary>Razonamiento del modelo</summary>
            <p>{live.thinking}</p>
          </details>
        )}
        {toolsAfter(0).length > 0 && <ToolRow live={live} tools={toolsAfter(0)} onOpenTool={onOpenTool} />}
        {closed.map((segment, i) => (
          <div key={i}>
            {segment.text.trim() && (
              <div className="turn__intermediate" aria-label="Razonamiento intermedio">
                <span className="turn__intermediate-label">Razonamiento intermedio</span>
                <MarkdownView source={segment.text} />
              </div>
            )}
            {toolsAfter(i + 1).length > 0 && <ToolRow live={live} tools={toolsAfter(i + 1)} onOpenTool={onOpenTool} />}
          </div>
        ))}
        <ReportChanges changes={live.reports} handlers={handlers} disabledReason="Podrás deshacerlo cuando termine la respuesta." />
        {final ? (
          <ChatAnswer source={final} onOpen={handlers.onOpenReport} className="turn__streaming" />
        ) : (
          <p className="turn__waiting">
            <span className="typing" aria-hidden="true">
              <span />
              <span />
              <span />
            </span>
            {live.tools.some((t) => t.status === 'running') ? 'Consultando…' : 'Pensando…'}
          </p>
        )}
      </div>
    </article>
  );
}

function ToolRow({ live, tools, onOpenTool }: { live: LiveTurn; tools: LiveTurn['tools']; onOpenTool: OpenTool }) {
  return (
    <div className="turn__tools" aria-label="Herramientas">
      {tools.map((t) => (
        <ToolCallChip
          key={t.key}
          name={t.name}
          arguments={t.arguments}
          status={t.status}
          durationMs={t.durationMs}
          error={t.error}
          onOpen={() => onOpenTool(live.tools.map(liveView), live.tools.indexOf(t), true)}
        />
      ))}
    </div>
  );
}

export function ChatThread({
  session,
  live,
  onOpenReport,
  onReportChanged,
  models = [],
}: { session: ChatSession | null; live: LiveTurn | null; models?: ChatModelDto[] } & ReportChipHandlers) {
  const handlers: ReportChipHandlers = { onOpenReport, onReportChanged };
  const [openTools, setOpenTools] = useState<OpenTools | null>(null);
  const onOpenTool: OpenTool = (views, index, isLive = false) => setOpenTools({ live: isLive, views, index });
  const toolViews = openTools ? (openTools.live && live ? live.tools.map(liveView) : openTools.views) : [];
  const scrollRef = useRef<HTMLDivElement>(null);
  const stickRef = useRef(true);
  const turns = session?.turns ?? [];
  const liveText = live ? openSegmentText(live).length + live.tools.length + live.segments.length + live.reports.length : 0;

  useEffect(() => {
    const el = scrollRef.current;
    if (el && stickRef.current) el.scrollTop = el.scrollHeight;
  }, [turns.length, liveText]);

  const onScroll = () => {
    const el = scrollRef.current;
    if (el) stickRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 48;
  };

  return (
    <div className="thread" ref={scrollRef} onScroll={onScroll}>
      {turns.length === 0 && !live && (
        <div className="empty empty--chat">
          <h2>Pregunta sobre el repositorio</h2>
          <p>
            El asistente consulta el grafo de código, lee ficheros y los informes del análisis antes de responder, y
            cita <code>fichero:línea</code>. Cada afirmación lleva su marca: <span className="claim claim--verified">V</span>{' '}
            verificada en el código, <span className="claim claim--documented">D</span> sacada de un informe o documento
            e <span className="claim claim--inferred">I</span> inferida. Pulsa una referencia para abrirla en el visor.
          </p>
        </div>
      )}
      {turns.map((t) => (
        <TurnView key={t.id} turn={t} handlers={handlers} models={models} onOpenTool={onOpenTool} />
      ))}
      {live && <LiveTurnView live={live} handlers={handlers} onOpenTool={onOpenTool} />}
      <ToolCallDetail
        call={openTools ? (toolViews[openTools.index] ?? null) : null}
        onClose={() => setOpenTools(null)}
        position={
          openTools
            ? {
                index: openTools.index,
                total: toolViews.length,
                onPrev: () => setOpenTools((o) => (o ? { ...o, index: Math.max(0, o.index - 1) } : o)),
                onNext: () => setOpenTools((o) => (o ? { ...o, index: Math.min(toolViews.length - 1, o.index + 1) } : o)),
              }
            : undefined
        }
      />
      <p className="visually-hidden" role="status">
        {live ? (live.status === 'sending' ? 'Enviando pregunta' : 'Recibiendo respuesta') : ''}
      </p>
    </div>
  );
}
