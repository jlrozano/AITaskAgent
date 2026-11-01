// Estado del chat como reductor puro: cada evento del protocolo se convierte en «partes» ordenadas dentro del mensaje del
// asistente. Cada tipo de parte tiene su renderizador en el registro (registry.ts); el reductor no sabe de React.
import type { ProposalEvent, QuestionEvent, ServerEvent, UsageCounts } from "./protocol.ts";

export type ProposalStatus = "pending" | "applying" | "applied" | "discarded" | "conflict" | "failed";

export interface TextPart { type: "text"; id: string; text: string }
export interface ThinkingPart { type: "thinking"; id: string; text: string }
export interface ToolPart {
  type: "tool";
  id: string;
  name: string;
  label?: string;
  where?: "server" | "browser";
  status: "running" | "ok" | "error";
  summary?: string;
  elapsedMs?: number;
  errorCode?: string;
  sampleRowsSent?: boolean;
  /** Argumentos de la llamada (`tool.started.input`), tal cual los envía el servidor. */
  input?: unknown;
  /** Salida (`tool.completed.output`): JSON, o string si no lo era. */
  output?: unknown;
  /** La salida llegó recortada por el servidor. */
  outputTruncated?: boolean;
  /** Agente (`AgentPart.id`) que lanzó la herramienta. */
  stepId?: string;
  /** Texto de presentación del inicio (`tool.started.display`) y del fin (`tool.completed.display`; sustituye al del inicio). */
  displayStart?: string;
  displayEnd?: string;
}
/** Paso del flujo (`step.started`/`step.completed`): un agente, con las herramientas que lanza (las que traen su `stepId`). */
export interface AgentPart {
  type: "agent";
  /** = `stepId`. */
  id: string;
  name: string;
  label?: string;
  kind: "llm" | "tool" | "step";
  attempt: number;
  status: "running" | "ok" | "error";
  elapsedMs?: number;
  summary?: string;
  errorCode?: string;
  /** Qué hace el paso (`step.started.detail`). */
  detail?: string;
  /** Solo `llm` (`step.completed`): modelo, tokens y coste de la llamada. */
  model?: string;
  inputTokens?: number;
  outputTokens?: number;
  costUsd?: number;
  /** Texto de presentación del inicio (`step.started.display`) y del fin (`step.completed.display`; sustituye al del inicio). */
  displayStart?: string;
  displayEnd?: string;
  /**
   * Índice en `message.parts` donde cierra el paso (`parts.length` al llegar `step.completed`, o al cerrar el turno con el paso
   * abierto). Los eventos `text.delta`/`thinking.delta` no traen `stepId`: un paso «posee» las partes `thinking`, `text` y
   * `tool` que aparecen después de él y antes de ese índice (mientras corre, todo lo que llegue). Opcional: sin él (partes
   * construidas a mano, datos antiguos) un paso terminado solo posee las herramientas de su `stepId`.
   */
  endIndex?: number;
}
/** Contenido de un paso en orden cronológico: razonamiento, texto del modelo y herramientas. */
export type FlowItem = ThinkingPart | TextPart | ToolPart;
/** Lo que recibe el renderizador `agent`: el agente, sus herramientas y su secuencia lineal (lo calcula `MessageList`). */
export interface AgentNodePart extends AgentPart {
  /** Herramientas del paso (todas las de su `stepId` más las lanzadas mientras estaba abierto). */
  tools?: ToolPart[];
  /** Razonamiento, herramientas y texto del paso, intercalados en el orden en que llegaron. */
  flow?: FlowItem[];
}
/** Entrada de un grupo con agentes: una herramienta suelta (sin `stepId` conocido) o un agente con sus herramientas. */
export type GroupEntry = { kind: "tool"; tool: ToolPart } | { kind: "agent"; agent: AgentNodePart };
/** Fichero adjunto a un mensaje del usuario (el anfitrión puede añadir sus propios campos: miniatura, formato…). */
export interface ChatAttachment { id: string; name: string; kind?: string; size?: number; [extra: string]: unknown }
export interface AttachmentsPart { type: "attachments"; id: string; items: ChatAttachment[] }
/** Varias herramientas seguidas de un mismo mensaje, agrupadas para pintarlas en un panel plegable (no es una parte del estado). */
export interface ToolGroupPart {
  type: "toolGroup";
  id: string;
  /** Todas las herramientas del grupo, en orden (también las que cuelgan de un agente). */
  tools: ToolPart[];
  /** Solo si el grupo trae agentes: el orden real, con cada agente y sus herramientas. */
  entries?: GroupEntry[];
  /** Pintar la cabecera de cada agente (falsa con un único agente sin nada que contar; ver MessageList). */
  agentHeaders?: boolean;
}
export interface ProposalPart { type: "proposal"; id: string; proposal: ProposalEvent; status: ProposalStatus; message?: string }
export interface QuestionPart { type: "question"; id: string; question: QuestionEvent; answered?: string }
export interface NoticePart { type: "notice"; id: string; code: string; message: string }
export interface ErrorPart { type: "error"; id: string; code: string; message: string; retryable: boolean }
export interface UsagePart {
  type: "usage";
  id: string;
  model?: string;
  counts: UsageCounts;
  estimatedCost?: number;
  currency?: string;
}

export type Part = TextPart | ThinkingPart | ToolPart | AgentPart | AttachmentsPart | ProposalPart | QuestionPart | NoticePart | ErrorPart | UsagePart;
export type PartType = Part["type"];

export type MessageStatus = "streaming" | "done" | "cancelled" | "error";

export interface ChatMessage {
  id: string;
  role: "user" | "assistant";
  parts: Part[];
  turnId?: string;
  model?: string;
  status: MessageStatus;
}

export interface ChatState {
  messages: ChatMessage[];
  /** Hay un turno en curso (entre `send` y `done`). */
  busy: boolean;
}

export type ChatAction =
  | { type: "user"; id: string; text: string; attachments?: ChatAttachment[] }
  | { type: "assistant.begin"; id: string }
  | { type: "event"; event: ServerEvent }
  | { type: "proposal.status"; partId: string; status: ProposalStatus; message?: string }
  | { type: "question.answered"; partId: string; answer: string }
  | { type: "reset" };

export const initialState: ChatState = { messages: [], busy: false };

const emptyUsage: UsageCounts = { requests: 0, inputTokens: 0, cachedInputTokens: 0, outputTokens: 0, reasoningTokens: 0 };

function lastAssistant(state: ChatState): number {
  for (let i = state.messages.length - 1; i >= 0; i--) if (state.messages[i]!.role === "assistant") return i;
  return -1;
}

function patch(state: ChatState, i: number, f: (m: ChatMessage) => ChatMessage): ChatState {
  return { ...state, messages: state.messages.map((m, k) => (k === i ? f(m) : m)) };
}

function addPart(m: ChatMessage, p: Part): ChatMessage {
  return { ...m, parts: [...m.parts, p] };
}

/** Texto y razonamiento se concatenan en la última parte si es del mismo tipo; si no, abren una nueva. */
function appendText(m: ChatMessage, kind: "text" | "thinking", text: string): ChatMessage {
  const last = m.parts[m.parts.length - 1];
  // No se funde con una parte que un paso ya cerró (`endIndex`): lo que llega después es de fuera del paso.
  const closed = m.parts.some((p) => p.type === "agent" && p.endIndex === m.parts.length);
  if (last && last.type === kind && !closed) return { ...m, parts: [...m.parts.slice(0, -1), { ...last, text: last.text + text }] };
  return addPart(m, { type: kind, id: `${kind}-${m.parts.length}`, text });
}

function upsertTool(m: ChatMessage, id: string, f: (old: ToolPart | undefined) => ToolPart): ChatMessage {
  const i = m.parts.findIndex((p) => p.type === "tool" && p.id === id);
  if (i < 0) return addPart(m, f(undefined));
  return { ...m, parts: m.parts.map((p, k) => (k === i ? f(p as ToolPart) : p)) };
}

function upsertAgent(m: ChatMessage, id: string, f: (old: AgentPart | undefined) => AgentPart): ChatMessage {
  const i = m.parts.findIndex((p) => p.type === "agent" && p.id === id);
  if (i < 0) return addPart(m, f(undefined));
  return { ...m, parts: m.parts.map((p, k) => (k === i ? f(p as AgentPart) : p)) };
}

function addUsage(m: ChatMessage, e: Extract<ServerEvent, { type: "usage" }>): ChatMessage {
  const i = m.parts.findIndex((p) => p.type === "usage");
  const prev = i >= 0 ? (m.parts[i] as UsagePart) : undefined;
  const base = prev?.counts ?? emptyUsage;
  const counts: UsageCounts = {
    requests: base.requests + 1,
    inputTokens: base.inputTokens + e.inputTokens,
    cachedInputTokens: base.cachedInputTokens + (e.cachedInputTokens ?? 0),
    outputTokens: base.outputTokens + e.outputTokens,
    reasoningTokens: base.reasoningTokens + (e.reasoningTokens ?? 0),
  };
  const cost = e.estimatedCost !== undefined ? (prev?.estimatedCost ?? 0) + e.estimatedCost : prev?.estimatedCost;
  const part: UsagePart = {
    type: "usage",
    id: "usage",
    counts,
    ...(e.model ?? prev?.model ? { model: (e.model ?? prev?.model) as string } : {}),
    ...(cost !== undefined ? { estimatedCost: cost } : {}),
    ...((e.currency ?? prev?.currency) ? { currency: (e.currency ?? prev?.currency) as string } : {}),
  };
  return i >= 0 ? { ...m, parts: m.parts.map((p, k) => (k === i ? part : p)) } : addPart(m, part);
}

function applyEvent(m: ChatMessage, e: ServerEvent): ChatMessage {
  switch (e.type) {
    case "turn.start":
      return { ...m, turnId: e.turnId, ...(e.model ? { model: e.model } : {}) };
    case "text.delta":
      return appendText(m, "text", e.text);
    case "thinking.delta":
      return appendText(m, "thinking", e.text);
    case "tool.started":
      return upsertTool(m, e.callId, (old) => ({ ...(old ?? {}), type: "tool", id: e.callId, name: e.name, status: "running", ...(e.label ? { label: e.label } : {}), ...(e.where ? { where: e.where } : {}), ...(e.input !== undefined ? { input: e.input } : {}), ...(e.stepId ? { stepId: e.stepId } : {}), ...(e.display ? { displayStart: e.display } : {}) }));
    case "tool.completed":
      return upsertTool(m, e.callId, (old) => ({
        ...(old ?? {}),
        type: "tool",
        id: e.callId,
        name: e.name,
        status: e.ok ? "ok" : "error",
        ...(e.summary !== undefined ? { summary: e.summary } : {}),
        ...(e.elapsedMs !== undefined ? { elapsedMs: e.elapsedMs } : {}),
        ...(e.errorCode ? { errorCode: e.errorCode } : {}),
        ...(e.sampleRowsSent ? { sampleRowsSent: true } : {}),
        ...(e.output !== undefined ? { output: e.output } : {}),
        ...(e.outputTruncated ? { outputTruncated: true } : {}),
        ...(e.stepId ? { stepId: e.stepId } : {}),
        ...(e.display ? { displayEnd: e.display } : {}),
      }));
    case "step.started":
      return upsertAgent(m, e.stepId, () => ({ type: "agent", id: e.stepId, name: e.name, kind: e.kind, attempt: e.attempt, status: "running", ...(e.label ? { label: e.label } : {}), ...(e.detail ? { detail: e.detail } : {}), ...(e.display ? { displayStart: e.display } : {}) }));
    case "step.completed":
      return upsertAgent(m, e.stepId, (old) => ({
        type: "agent",
        name: "",
        kind: "step",
        attempt: 1,
        ...(old ?? {}),
        id: e.stepId,
        status: e.ok ? "ok" : "error",
        ...(e.elapsedMs !== undefined ? { elapsedMs: e.elapsedMs } : {}),
        ...(e.summary !== undefined ? { summary: e.summary } : {}),
        ...(e.errorCode ? { errorCode: e.errorCode } : {}),
        ...(e.model ? { model: e.model } : {}),
        ...(e.inputTokens !== undefined ? { inputTokens: e.inputTokens } : {}),
        ...(e.outputTokens !== undefined ? { outputTokens: e.outputTokens } : {}),
        ...(e.costUsd !== undefined ? { costUsd: e.costUsd } : {}),
        ...(e.display ? { displayEnd: e.display } : {}),
        endIndex: m.parts.length,
      }));
    case "usage":
      return addUsage(m, e);
    case "notice":
      return addPart(m, { type: "notice", id: `notice-${m.parts.length}`, code: e.code, message: e.message });
    case "question":
      return addPart(m, { type: "question", id: `question-${e.callId}`, question: e });
    case "proposal":
      return addPart(m, { type: "proposal", id: `proposal-${e.proposalId ?? e.turnId}`, proposal: e, status: "pending" });
    case "error":
      return addPart(m, { type: "error", id: `error-${m.parts.length}`, code: e.code, message: e.message, retryable: e.retryable === true });
    case "done": {
      const status: MessageStatus = e.reason === "cancelled" ? "cancelled" : e.reason === "error" ? "error" : "done";
      // Las herramientas que siguen «en curso» al cerrar el turno no se quedan girando.
      return { ...m, status, parts: m.parts.map((p) => (p.type === "agent" && p.status === "running" ? { ...p, status: "error" as const, endIndex: m.parts.length } : p.type === "tool" && p.status === "running" ? { ...p, status: "error" as const } : p)) };
    }
  }
}

export function chatReducer(state: ChatState, a: ChatAction): ChatState {
  switch (a.type) {
    case "user":
      return { ...state, messages: [...state.messages, { id: a.id, role: "user", status: "done", parts: [{ type: "text", id: "text-0", text: a.text }, ...(a.attachments?.length ? [{ type: "attachments" as const, id: "attachments-0", items: a.attachments }] : [])] }] };
    case "assistant.begin":
      return { busy: true, messages: [...state.messages, { id: a.id, role: "assistant", status: "streaming", parts: [] }] };
    case "event": {
      const i = lastAssistant(state);
      if (i < 0) return state;
      const next = patch(state, i, (m) => applyEvent(m, a.event));
      return a.event.type === "done" ? { ...next, busy: false } : next;
    }
    case "proposal.status":
      return mapPart(state, a.partId, (p) => (p.type === "proposal" ? { ...p, status: a.status, ...(a.message ? { message: a.message } : {}) } : p));
    case "question.answered":
      return mapPart(state, a.partId, (p) => (p.type === "question" ? { ...p, answered: a.answer } : p));
    case "reset":
      return initialState;
  }
}

function mapPart(state: ChatState, partId: string, f: (p: Part) => Part): ChatState {
  return { ...state, messages: state.messages.map((m) => (m.parts.some((p) => p.id === partId) ? { ...m, parts: m.parts.map((p) => (p.id === partId ? f(p) : p)) } : m)) };
}
