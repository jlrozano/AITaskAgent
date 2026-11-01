// Inspector de herramienta: modal con la entrada y la salida de una llamada (visor JSON plegable). role=dialog, foco
// atrapado, Esc y clic en el fondo cierran y devuelven el foco al botón que lo abrió.
import { useEffect, useId, useRef } from "react";
import { createPortal } from "react-dom";
import type { Labels } from "../labels.ts";
import type { ToolPart } from "../state.ts";
import { JsonView } from "./JsonView.tsx";
import { nodeTitle } from "./NodeIcons.tsx";

const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function ToolInspector({ part, labels, onClose }: { part: ToolPart; labels: Labels; onClose: () => void }) {
  const box = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    box.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        e.preventDefault();
        onCloseRef.current();
      } else if (e.key === "Tab" && box.current) {
        const items = Array.from(box.current.querySelectorAll<HTMLElement>(FOCUSABLE));
        if (items.length === 0) {
          e.preventDefault();
          return;
        }
        const first = items[0]!;
        const last = items[items.length - 1]!;
        const active = document.activeElement;
        if (!box.current.contains(active) || active === box.current) {
          e.preventDefault();
          (e.shiftKey ? last : first).focus();
        } else if (e.shiftKey && active === first) {
          e.preventDefault();
          last.focus();
        } else if (!e.shiftKey && active === last) {
          e.preventDefault();
          first.focus();
        }
      }
    };
    document.addEventListener("keydown", onKey, true);
    return () => {
      document.removeEventListener("keydown", onKey, true);
      previous?.focus();
    };
  }, []);

  const failed = part.status === "error";
  const hasInput = part.input !== undefined && part.input !== null && !(typeof part.input === "object" && Object.keys(part.input as object).length === 0);
  const hasOutput = part.output !== undefined;
  const title = nodeTitle(part);
  const hasDisplay = part.displayStart !== undefined || part.displayEnd !== undefined;
  return createPortal(
    <div
      className="atc-modal-backdrop"
      data-testid="tool-inspector-backdrop"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div ref={box} className="atc-modal" role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1} data-testid="tool-inspector">
        <header className="atc-modal-head">
          <h3 id={titleId} className="atc-modal-title">
            {labels.toolInspectorTitle}: {title}
            {title !== part.name ? <code className="atc-modal-name">{part.name}</code> : null}
          </h3>
          <button type="button" className="atc-modal-close" aria-label={labels.close} title={labels.close} onClick={onClose} data-testid="tool-inspector-close">
            <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true" focusable="false"><path d="M3 3l10 10M13 3L3 13" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" fill="none" /></svg>
          </button>
        </header>
        <div className="atc-modal-body">
          {hasDisplay || part.label ? (
            <section className="atc-modal-section" data-testid="tool-names">
              <dl className="atc-modal-dl">
                <dt>{labels.toolTechName}</dt>
                <dd><code data-testid="tool-tech-name">{part.name}</code></dd>
                {part.displayStart !== undefined ? (<><dt>{labels.toolDisplayStart}</dt><dd data-testid="tool-display-start">{part.displayStart}</dd></>) : null}
                {part.displayEnd !== undefined ? (<><dt>{labels.toolDisplayEnd}</dt><dd data-testid="tool-display-end">{part.displayEnd}</dd></>) : null}
              </dl>
            </section>
          ) : null}
          <section className="atc-modal-section" data-testid="tool-input">
            <h4>{labels.toolInput}</h4>
            {hasInput ? <JsonView value={part.input} labels={labels} testId="tool-input-json" /> : <p className="atc-modal-empty">{labels.toolNoInput}</p>}
          </section>
          <section className="atc-modal-section" data-testid="tool-output">
            <h4>{labels.toolOutput}</h4>
            {part.status === "running" ? (
              <p className="atc-modal-empty" data-testid="tool-output-pending">{labels.toolOutputPending}</p>
            ) : (
              <>
                {failed ? (
                  <p className="atc-modal-error" role="alert" data-testid="tool-error">
                    {part.summary ?? labels.toolFailed}
                    {part.errorCode ? <code className="atc-tool-code">{part.errorCode}</code> : null}
                  </p>
                ) : null}
                {part.summary && !failed ? <p className="atc-modal-summary" data-testid="tool-summary">{part.summary}</p> : null}
                {hasOutput ? <JsonView value={part.output} labels={labels} testId="tool-output-json" /> : failed ? null : <p className="atc-modal-empty">{labels.toolNoOutput}</p>}
                {part.outputTruncated ? <p className="atc-modal-warn" data-testid="tool-truncated">{labels.toolOutputTruncated}</p> : null}
              </>
            )}
          </section>
        </div>
      </div>
    </div>,
    document.body,
  );
}
