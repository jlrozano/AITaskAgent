// Registro extensible de renderizadores por tipo de elemento (parte del mensaje). El anfitrión puede sustituir uno
// (p. ej. la propuesta, para pintar las diferencias a su manera) o añadir tipos propios con `createRenderers`.
import type { ComponentType } from "react";
import type { ChatMessage, Part, ToolGroupPart } from "./state.ts";
import type { Labels } from "./labels.ts";
import type { MarkdownOptions } from "./markdown.tsx";
import type { NoticePart } from "./state.ts";

export interface RendererContext {
  labels: Labels;
  /** Hay un turno en curso. */
  busy: boolean;
  applyProposal(partId: string): void;
  discardProposal(partId: string): void;
  /** Opcional: reenvía el turno que falló (el botón «Reintentar» del error solo sale si está y `canRetry` lo permite). */
  retryTurn?(messageId: string): void;
  canRetry?(messageId: string): boolean;
  /** Opcional: plugins/componentes/rehype extra para el markdown del renderizador `text` (ver `MarkdownOptions`). */
  markdown?: MarkdownOptions;
  /** Pinta, en vez de los argumentos en línea, un botón de icono que abre una modal con la entrada y la salida de la herramienta (por defecto no). */
  toolInspector?: boolean;
  /** Filtros de presentación (por defecto true): `false` no pinta las partes `thinking` / los nodos Herramienta. El estado conserva los datos. */
  showThinking?: boolean;
  showToolCalls?: boolean;
  /** Registro en uso: lo pone `MessageList` para que `toolGroup` pinte cada agente con el renderizador `agent` (también el sustituido por el anfitrión). */
  renderers?: RendererRegistry;
  answerQuestion(partId: string, answer: { approved?: boolean; answers?: string[][]; text?: string }, summary: string): void;
}

export interface RendererProps<P extends Part | ToolGroupPart = Part> {
  part: P;
  message: ChatMessage;
  ctx: RendererContext;
}

export type Renderer<P extends Part | ToolGroupPart = Part> = ComponentType<RendererProps<P>>;

/** Clave = `Part.type`, más `toolGroup` (varias herramientas seguidas; si falta, cada una se pinta con `tool`). Un tipo sin renderizador no se pinta. */
// `any`: cada clave tiene su propio tipo de parte; el despachador de MessageList ya elige por `part.type`.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type RendererRegistry = Record<string, Renderer<any> | undefined>;

/** Renderizadores de aviso por `code`: `noticeRenderers` en `ChatView`/`MessageList`; el que no coincida usa el renderizador `notice`. */
export type NoticeRenderers = Record<string, ComponentType<RendererProps<NoticePart>> | undefined>;

export function createRenderers(base: RendererRegistry, overrides: RendererRegistry = {}): RendererRegistry {
  return { ...base, ...overrides };
}
