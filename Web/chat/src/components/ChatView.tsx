import { useEffect, useMemo, useState, type FormEvent } from "react";
import { defaultLabels, type Labels } from "../labels.ts";
import type { MarkdownOptions } from "../markdown.tsx";
import type { NoticeRenderers, RendererContext, RendererRegistry } from "../registry.ts";
import { defaultRenderers } from "../renderers/index.ts";
import type { UseChat } from "../useChat.ts";
import type { TransformEvent } from "../transform.ts";
import { MessageList } from "./MessageList.tsx";

export interface ChatViewProps {
  /** Resultado de `useChat`. */
  chat: UseChat;
  renderers?: RendererRegistry;
  labels?: Partial<Labels>;
  /** Contenido extra sobre el cuadro de texto (selector de modelo, interruptores…): lo pone el anfitrión. */
  toolbar?: React.ReactNode;
  /** Plugins (Mermaid, código…), componentes y rehype extra para el markdown del renderizador `text`, sin sustituirlo. */
  markdown?: MarkdownOptions;
  /** Renderizador por `code` de aviso; el que no coincida usa `renderers.notice`. */
  noticeRenderers?: NoticeRenderers;
  /** Herramientas con botón de icono que abre una modal con su entrada y salida (visor JSON), en vez de los argumentos en línea. Por defecto no. */
  toolInspector?: boolean;
  /** Hook previo al reductor para cada evento del servidor (ver `TransformEvent`); registra su valor en `chat` (`setTransformEvent`). */
  transformEvent?: TransformEvent;
  /** Filtros de presentación (por defecto true), posteriores al hook: `false` oculta el razonamiento / los nodos de tool. */
  showThinking?: boolean;
  showToolCalls?: boolean;
}

/** Vista completa: lista de mensajes, cuadro de texto y botón Enviar/Detener. */
export function ChatView({ chat, renderers = defaultRenderers, labels, toolbar, markdown, noticeRenderers, toolInspector = false, transformEvent, showThinking = true, showToolCalls = true }: ChatViewProps) {
  const setHook = chat.setTransformEvent;
  useEffect(() => {
    if (transformEvent === undefined) return undefined;
    setHook(transformEvent);
    return () => setHook(undefined);
  }, [transformEvent, setHook]);
  const l = useMemo<Labels>(() => ({ ...defaultLabels, ...labels }), [labels]);
  const [text, setText] = useState("");
  const busy = chat.state.busy;
  const ctx = useMemo<RendererContext>(
    () => ({ labels: l, busy, toolInspector, showThinking, showToolCalls, ...(markdown ? { markdown } : {}), applyProposal: (id) => void chat.applyProposal(id), discardProposal: (id) => void chat.discardProposal(id), answerQuestion: (id, a, s) => void chat.answer(id, a, s) }),
    [l, busy, markdown, toolInspector, showThinking, showToolCalls, chat],
  );
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (busy || !text.trim()) return;
    const t = text;
    setText("");
    void chat.send(t);
  };
  return (
    <div className="atc-chat">
      <MessageList messages={chat.state.messages} ctx={ctx} renderers={renderers} noticeRenderers={noticeRenderers} />
      {toolbar}
      <form className="atc-composer" onSubmit={submit}>
        <textarea
          aria-label={l.placeholder}
          placeholder={l.placeholder}
          value={text}
          disabled={busy}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) submit(e);
          }}
        />
        {busy ? (
          <button type="button" onClick={() => void chat.cancel()}>{l.stop}</button>
        ) : (
          <button type="submit" className="atc-primary" disabled={!text.trim()}>{l.send}</button>
        )}
      </form>
    </div>
  );
}
