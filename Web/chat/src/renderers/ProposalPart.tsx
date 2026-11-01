import type { RendererProps } from "../registry.ts";
import type { ProposalPart as Part } from "../state.ts";

/** Propuesta con diferencias (o, si el servidor no las envía, con la lista de operaciones) y Aplicar/Descartar. */
export function ProposalPart({ part, ctx }: RendererProps<Part>) {
  const p = part.proposal;
  const l = ctx.labels;
  const kindLabel = { added: l.added, changed: l.changed, removed: l.removed } as const;
  const pending = part.status === "pending";
  return (
    <section className="atc-proposal" data-status={part.status}>
      {p.summary ? <h4>{p.summary}</h4> : null}
      {p.changes && p.changes.length > 0 ? (
        <ul className="atc-diff">
          {p.changes.map((c, i) => (
            <li key={i} className={`atc-diff-${c.kind}`} data-kind={c.kind}>
              <strong>{kindLabel[c.kind]}</strong> {c.label ?? c.target}
              {c.before !== undefined || c.after !== undefined ? (
                <span className="atc-diff-values">
                  {c.before !== undefined ? <del>{c.before}</del> : null} {c.after !== undefined ? <ins>{c.after}</ins> : null}
                </span>
              ) : null}
            </li>
          ))}
        </ul>
      ) : (
        <ul className="atc-ops">
          {p.operations.map((o, i) => (
            <li key={i}>{o.label}</li>
          ))}
        </ul>
      )}
      {p.validation && p.validation.valid === false ? <p className="atc-warn">La validación del servidor no es limpia.</p> : null}
      {pending ? (
        <div className="atc-actions">
          <button type="button" className="atc-primary" disabled={ctx.busy} onClick={() => ctx.applyProposal(part.id)}>{l.apply}</button>
          <button type="button" onClick={() => ctx.discardProposal(part.id)}>{l.discard}</button>
        </div>
      ) : (
        <p className="atc-status" role="status">
          {part.status === "applied" ? l.applied : part.status === "discarded" ? l.discarded : part.status === "conflict" ? (part.message ?? l.conflict) : part.status === "failed" ? (part.message ?? l.failed) : "…"}
        </p>
      )}
    </section>
  );
}
