import type { RendererProps } from "../registry.ts";
import type { ErrorPart as ErrPart, NoticePart as NotPart, UsagePart as UsgPart } from "../state.ts";

const nf = new Intl.NumberFormat("es-ES");

/** Consumo del mensaje: tokens de entrada y salida y, si el servidor lo envía, coste estimado. */
export function UsagePart({ part, ctx }: RendererProps<UsgPart>) {
  const c = part.counts;
  return (
    <p className="atc-usage">
      {part.model ? <span>{part.model} · </span> : null}
      {nf.format(c.inputTokens)} {ctx.labels.tokensIn} · {nf.format(c.outputTokens)} {ctx.labels.tokensOut}
      {part.estimatedCost !== undefined ? <span> · {part.estimatedCost.toFixed(4)} {part.currency ?? ""}</span> : null}
    </p>
  );
}

export function NoticePart({ part }: RendererProps<NotPart>) {
  return <p className="atc-notice" data-code={part.code}>{part.message}</p>;
}

export function ErrorPart({ part, message, ctx }: RendererProps<ErrPart>) {
  return (
    <p className="atc-error" role="alert" data-code={part.code}>
      {part.message}
      {part.retryable ? <span> {ctx.labels.retryable}</span> : null}
      {part.retryable && ctx.retryTurn && ctx.canRetry?.(message.id) ? (
        <button type="button" className="atc-retry" disabled={ctx.busy} onClick={() => ctx.retryTurn!(message.id)} data-testid="msg-retry">
          {ctx.labels.retry}
        </button>
      ) : null}
    </p>
  );
}
