// Tipos de chat-protocol/1 (schema/chat-protocol.schema.json). Escritos a mano y acotados por
// las pruebas de conformidad (tests/conformance.test.ts): el esquema manda, estos tipos lo reflejan.

export const PROTOCOL = "chat-protocol/1";

export interface UsageCounts {
  requests: number;
  inputTokens: number;
  cachedInputTokens: number;
  outputTokens: number;
  reasoningTokens: number;
}

interface Envelope {
  turnId: string;
  seq: number;
}

export interface TurnStartEvent extends Envelope {
  type: "turn.start";
  sessionId?: string;
  task?: string;
  provider?: string;
  model?: string;
  baseVersion?: number;
  autoApprove?: boolean;
  /** Modo de aprobación efectivo del turno (aditivo); equivale a `autoApprove` (`auto` = true). */
  approvalMode?: "auto" | "ask";
  learningEnabled?: boolean;
}
export interface TextDeltaEvent extends Envelope {
  type: "text.delta";
  text: string;
}
export interface ThinkingDeltaEvent extends Envelope {
  type: "thinking.delta";
  text: string;
}
export interface ToolStartedEvent extends Envelope {
  type: "tool.started";
  callId: string;
  name: string;
  label?: string;
  where?: "server" | "browser";
  input?: unknown;
  /** Paso (agente) que lanzó la herramienta (`step.started.stepId`); aditivo. */
  stepId?: string;
  /** Texto de presentación al empezar, renderizado por el framework con una plantilla (aditivo). */
  display?: string;
}
export interface ToolCompletedEvent extends Envelope {
  type: "tool.completed";
  callId: string;
  name: string;
  ok: boolean;
  summary?: string;
  elapsedMs?: number;
  errorCode?: string;
  sampleRowsSent?: true;
  /** Salida de la herramienta: JSON, o string si no lo era (aditivo). Acotada a 16384 caracteres en el servidor. */
  output?: unknown;
  /** La salida se recortó al tope del servidor. */
  outputTruncated?: true;
  stepId?: string;
  /** Texto de presentación al terminar, renderizado por el framework con una plantilla (aditivo). */
  display?: string;
}
/** Empieza un paso del flujo (un agente: llamada al modelo, herramienta o paso propio). Aditivo. */
export interface StepStartedEvent extends Envelope {
  type: "step.started";
  stepId: string;
  name: string;
  label?: string;
  kind: "llm" | "tool" | "step";
  /** Intento (≥ 1): sube con cada reintento del mismo paso. */
  attempt: number;
  /** Qué hace el paso, en una línea (aditivo). */
  detail?: string;
  /** Texto de presentación al empezar, renderizado por el framework con una plantilla (aditivo). */
  display?: string;
}
export interface StepCompletedEvent extends Envelope {
  type: "step.completed";
  stepId: string;
  ok: boolean;
  elapsedMs?: number;
  errorCode?: string;
  /** Resultado del paso (en un paso determinista, lo que produjo). */
  summary?: string;
  /** Solo en pasos `llm` (aditivo): modelo, tokens y coste de la llamada. */
  model?: string;
  inputTokens?: number;
  outputTokens?: number;
  costUsd?: number;
  /** Texto de presentación al terminar (sustituye al de `step.started`); aditivo. */
  display?: string;
}
export interface UsageEvent extends Envelope {
  type: "usage";
  provider?: string;
  model?: string;
  inputTokens: number;
  cachedInputTokens?: number;
  outputTokens: number;
  reasoningTokens?: number;
  turn?: UsageCounts;
  estimatedCost?: number;
  currency?: string;
}
export interface NoticeEvent extends Envelope {
  type: "notice";
  code: string;
  message: string;
}
export interface QuestionOption {
  label: string;
  description?: string;
  recommended?: boolean;
}
export interface QuestionItem {
  question: string;
  header?: string;
  multiSelect?: boolean;
  options?: QuestionOption[];
}
export interface QuestionEvent extends Envelope {
  type: "question";
  callId: string;
  question: string;
  kind: "approval" | "choice" | "clarification";
  options?: string[];
  stage?: string | null;
  header?: string;
  multiSelect?: boolean;
  questions?: QuestionItem[];
}
export interface ProposalOperation {
  callId?: string;
  tool: string;
  label: string;
  changed?: string[];
  created?: string[];
  deleted?: string[];
}
export interface ProposalChange {
  kind: "added" | "changed" | "removed";
  target: string;
  label?: string;
  before?: string;
  after?: string;
}
export interface ProposalEvent extends Envelope {
  type: "proposal";
  proposalId?: string;
  baseVersion?: number;
  revision?: number;
  summary?: string;
  operations: ProposalOperation[];
  changes?: ProposalChange[];
  /** Documento completo propuesto (opaco para el protocolo): lo que sustituirá al base si el anfitrión lo aplica. Aditivo. */
  document?: unknown;
  validation?: { valid?: boolean; errors?: number | unknown[]; warnings?: number | unknown[]; scriptErrors?: number | unknown[] };
}
export interface ErrorEvent extends Envelope {
  type: "error";
  code: string;
  message: string;
  retryable?: boolean;
}
export interface DoneEvent extends Envelope {
  type: "done";
  reason: string;
  usage?: UsageCounts;
  steps?: number;
  toolCalls?: number;
  elapsedMs?: number;
}

export type ServerEvent =
  | TurnStartEvent
  | TextDeltaEvent
  | ThinkingDeltaEvent
  | ToolStartedEvent
  | ToolCompletedEvent
  | StepStartedEvent
  | StepCompletedEvent
  | UsageEvent
  | NoticeEvent
  | QuestionEvent
  | ProposalEvent
  | ErrorEvent
  | DoneEvent;

export type ServerEventType = ServerEvent["type"];

export interface MessageRequest {
  type: "message";
  text: string;
  sessionId?: string;
  task?: string;
  /** Documento del anfitrión (opaco para el protocolo). */
  document?: unknown;
  context?: Record<string, unknown>;
  options?: Record<string, unknown>;
}
export interface AnswerRequest {
  type: "answer";
  callId: string;
  sessionId?: string;
  approved?: boolean;
  answers?: string[][];
  text?: string;
  /** Como en `MessageRequest`: la respuesta abre un turno nuevo y el servidor necesita el documento vivo (aditivo). */
  document?: unknown;
  context?: Record<string, unknown>;
  options?: Record<string, unknown>;
}
export interface CancelRequest {
  type: "cancel";
  sessionId?: string;
  turnId?: string;
}
export type ClientRequest = MessageRequest | AnswerRequest | CancelRequest;

const TYPES: ReadonlySet<string> = new Set([
  "turn.start", "text.delta", "thinking.delta", "tool.started", "tool.completed", "step.started", "step.completed", "usage", "notice", "question", "proposal", "error", "done",
]);

/** Comprobación mínima de forma para el cliente: objeto con `type` conocido. Un tipo desconocido se ignora (compatibilidad hacia delante). */
export function isServerEvent(x: unknown): x is ServerEvent {
  return typeof x === "object" && x !== null && typeof (x as { type?: unknown }).type === "string" && TYPES.has((x as { type: string }).type);
}
