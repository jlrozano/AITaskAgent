// Hook de estado del chat: une ChatClient, el reductor y el HostAdapter. Un turno a la vez.
import { useCallback, useEffect, useMemo, useReducer, useRef } from "react";
import type { ChatClient } from "./client.ts";
import type { HostAdapter } from "./host.ts";
import type { AnswerRequest, MessageRequest } from "./protocol.ts";
import { chatReducer, initialState, type ChatState, type ProposalPart } from "./state.ts";
import type { TransformEvent } from "./transform.ts";

export interface UseChatOptions {
  client: ChatClient;
  host?: HostAdapter;
  sessionId?: string;
  /** Tarea o modo opcional para `MessageRequest.task`. */
  task?: string;
  /** Opciones libres que se reenvían en `MessageRequest.options` / `AnswerRequest.options` (rol, modelo…). */
  options?: Record<string, unknown>;
  /** Hook previo al reductor para cada evento del servidor (ver `TransformEvent`). Gana al del `ChatClient`. */
  transformEvent?: TransformEvent;
}

export interface UseChat {
  state: ChatState;
  /** Sustituye el hook `transformEvent` de las opciones (lo usa la prop `transformEvent` de `ChatView`); `undefined` vuelve al de las opciones. */
  setTransformEvent(hook: TransformEvent | undefined): void;
  send(text: string): Promise<void>;
  answer(partId: string, answer: Pick<AnswerRequest, "approved" | "answers" | "text">, summary: string): Promise<void>;
  cancel(): Promise<void>;
  applyProposal(partId: string): Promise<void>;
  discardProposal(partId: string): Promise<void>;
  reset(): void;
}

let counter = 0;
const nextId = (p: string) => `${p}${++counter}`;

export function useChat({ client, host, sessionId, task, options, transformEvent }: UseChatOptions): UseChat {
  const [state, dispatch] = useReducer(chatReducer, initialState);
  const optionHook = useRef(transformEvent);
  optionHook.current = transformEvent;
  const overrideHook = useRef<TransformEvent | undefined>(undefined);
  const setTransformEvent = useCallback((hook: TransformEvent | undefined) => {
    overrideHook.current = hook;
  }, []);
  const stateRef = useRef(state);
  stateRef.current = state;
  const hostRef = useRef(host);
  hostRef.current = host;

  useEffect(() => () => client.abort(), [client]);

  const run = useCallback(
    async (request: MessageRequest | AnswerRequest) => {
      dispatch({ type: "assistant.begin", id: nextId("a") });
      hostRef.current?.setEditingLocked(true);
      try {
        const hook = overrideHook.current ?? optionHook.current;
        await client.send(request, (event) => dispatch({ type: "event", event }), hook ? { transformEvent: hook } : {});
      } finally {
        hostRef.current?.setEditingLocked(false);
      }
    },
    [client],
  );

  const send = useCallback(
    async (text: string) => {
      const t = text.trim();
      if (!t || stateRef.current.busy) return;
      dispatch({ type: "user", id: nextId("u"), text: t });
      const h = hostRef.current;
      const [document, context] = await Promise.all([h?.readDocument?.(), h?.readContext?.()]);
      await run({
        type: "message",
        text: t,
        ...(sessionId ? { sessionId } : {}),
        ...(task ? { task } : {}),
        ...(options ? { options } : {}),
        ...(document !== undefined ? { document } : {}),
        ...(context ? { context } : {}),
      });
    },
    [run, sessionId, task, options],
  );

  const answer = useCallback<UseChat["answer"]>(
    async (partId, a, summary) => {
      if (stateRef.current.busy) return;
      const part = stateRef.current.messages.flatMap((m) => m.parts).find((p) => p.id === partId);
      if (!part || part.type !== "question") return;
      dispatch({ type: "question.answered", partId, answer: summary });
      // La respuesta abre un turno nuevo: el servidor necesita el documento vivo igual que con un mensaje.
      const h = hostRef.current;
      const [document, context] = await Promise.all([h?.readDocument?.(), h?.readContext?.()]);
      await run({
        type: "answer",
        callId: part.question.callId,
        ...(sessionId ? { sessionId } : {}),
        ...a,
        ...(options ? { options } : {}),
        ...(document !== undefined ? { document } : {}),
        ...(context ? { context } : {}),
      });
    },
    [run, sessionId, options],
  );

  const findProposal = (partId: string): ProposalPart | undefined => {
    const p = stateRef.current.messages.flatMap((m) => m.parts).find((x) => x.id === partId);
    return p && p.type === "proposal" ? p : undefined;
  };

  const applyProposal = useCallback(async (partId: string) => {
    const part = findProposal(partId);
    const h = hostRef.current;
    if (!part || !h || part.status !== "pending") return;
    dispatch({ type: "proposal.status", partId, status: "applying" });
    try {
      const r = await h.applyProposal(part.proposal);
      dispatch({ type: "proposal.status", partId, status: r.outcome, ...("message" in r && r.message ? { message: r.message } : {}) });
    } catch (e) {
      dispatch({ type: "proposal.status", partId, status: "failed", message: e instanceof Error ? e.message : String(e) });
    }
  }, []);

  const discardProposal = useCallback(async (partId: string) => {
    const part = findProposal(partId);
    if (!part || part.status !== "pending") return;
    dispatch({ type: "proposal.status", partId, status: "discarded" });
    await hostRef.current?.discardProposal?.(part.proposal);
  }, []);

  const cancel = useCallback(() => client.cancel(sessionId), [client, sessionId]);
  const reset = useCallback(() => dispatch({ type: "reset" }), []);

  return useMemo(() => ({ state, setTransformEvent, send, answer, cancel, applyProposal, discardProposal, reset }), [state, setTransformEvent, send, answer, cancel, applyProposal, discardProposal, reset]);
}
