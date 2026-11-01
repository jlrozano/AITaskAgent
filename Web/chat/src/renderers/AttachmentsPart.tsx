import type { RendererProps } from "../registry.ts";
import type { AttachmentsPart as Part } from "../state.ts";

/** Adjuntos de un mensaje del usuario: lista de nombres (el anfitrión puede sustituirla por una con miniaturas). */
export function AttachmentsPart({ part, ctx }: RendererProps<Part>) {
  return (
    <ul className="atc-attachments" aria-label={ctx.labels.attachments}>
      {part.items.map((a) => (
        <li key={a.id} className="atc-attachment" data-kind={a.kind}>{a.name}</li>
      ))}
    </ul>
  );
}
