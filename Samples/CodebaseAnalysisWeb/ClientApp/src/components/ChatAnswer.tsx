import { memo, useMemo, useRef, type ReactNode } from 'react';
import type { Components } from 'react-markdown';
import type { ChatClaimStatus, ChatReference, ChatReferenceStatus, ChatTurnAudit } from '../api/types';
import { MARK_KIND, remarkClaims, selectionFor, type ClaimMark } from './claims';
import { MarkdownView } from './MarkdownView';

const plugins = [remarkClaims];

const CLAIM_LABEL: Record<ChatClaimStatus, string> = {
  verified: 'Verificado',
  documented: 'Documentado',
  inferred: 'Inferido',
  unchecked: 'Sin comprobar',
};

const REFERENCE_LABEL: Record<ChatReferenceStatus, string> = {
  ok: 'leído en esta respuesta',
  partial: 'leído solo en parte en esta respuesta',
  not_read: 'no se leyó en esta respuesta',
  out_of_range: 'esas líneas no existen',
  missing: 'no existe en el repositorio',
};

interface Props {
  source: string;
  /** Null mientras llega la respuesta o en turnos anteriores a la auditoría. */
  audit?: ChatTurnAudit | null;
  /** Abre en el visor: `src/<ruta>[:ini-fin]`, el id de un informe o `doc/<id>`. */
  onOpen?: (selection: string) => void;
  className?: string;
}

/**
 * Respuesta del chat con sus marcas como insignias (verificado, documentado, inferido o sin comprobar, según la
 * auditoría del servidor) y cada referencia como botón que la abre en el visor.
 */
export const ChatAnswer = memo(function ChatAnswer({ source, audit, onOpen, className }: Props) {
  // El padre pasa una función nueva en cada render (cada delta del streaming): por ref, para no rehacer los componentes
  // y volver a pintar todas las respuestas.
  const onOpenRef = useRef(onOpen);
  onOpenRef.current = onOpen;
  const canOpen = !!onOpen;
  const overrides = useMemo<Components>(() => {
    const references = new Map<string, ChatReference>((audit?.references ?? []).map((r) => [r.text, r]));
    return {
      span({ node: _node, children, ...rest }) {
        const props = rest as Record<string, unknown>;
        const index = props['data-claim'];
        const mark = props['data-mark'] as ClaimMark | undefined;
        if (index === undefined || !mark) return <span {...rest}>{children}</span>;
        const claim = audit?.claims[Number(index)];
        // Si la numeración no casa (la marca no es de la misma clase), no se enseña un estado que no es suyo.
        const status: ChatClaimStatus | 'pending' =
          claim && claim.kind === MARK_KIND[mark] ? claim.status : audit ? MARK_KIND[mark] : 'pending';
        const label = status === 'pending' ? `${CLAIM_LABEL[MARK_KIND[mark]]} (se comprueba al terminar)` : CLAIM_LABEL[status];
        const title = claim?.status === 'unchecked' && claim.reason ? `${label}: ${claim.reason}` : label;
        return (
          <span className={`claim claim--${status}`} title={title} aria-label={title} role="img">
            {status === 'unchecked' ? '?' : mark}
          </span>
        );
      },
      code({ node: _node, children, ...rest }) {
        const props = rest as Record<string, unknown>;
        const text = props['data-ref'] as string | undefined;
        if (!text) return <code {...rest}>{children}</code>;
        const reference = references.get(text);
        const selection = selectionFor(text, reference);
        const status = reference?.status;
        const title = status ? `${text}: ${REFERENCE_LABEL[status]}` : text;
        const body: ReactNode = <code>{text}</code>;
        if (!selection || !canOpen) {
          return (
            <span className={`ref${status ? ` ref--${status}` : ''}`} title={title}>
              {body}
            </span>
          );
        }
        return (
          <button type="button" className={`ref ref--link${status ? ` ref--${status}` : ''}`} title={`${title}. Abrir en el visor`} onClick={() => onOpenRef.current?.(selection)}>
            {body}
          </button>
        );
      },
    };
  }, [audit, canOpen]);

  return <MarkdownView source={source} plugins={plugins} overrides={overrides} className={className} />;
});

/** «3 verificadas · 1 sin comprobar …» al pie de una respuesta auditada. */
export function AuditSummary({ audit }: { audit: ChatTurnAudit | null | undefined }) {
  if (!audit || audit.claims.length === 0) return null;
  const count = (status: ChatClaimStatus) => audit.claims.filter((c) => c.status === status).length;
  const parts: [ChatClaimStatus, number, string, string][] = [
    ['verified', count('verified'), 'verificada', 'verificadas'],
    ['documented', count('documented'), 'documentada', 'documentadas'],
    ['inferred', count('inferred'), 'inferida', 'inferidas'],
    ['unchecked', count('unchecked'), 'sin comprobar', 'sin comprobar'],
  ];
  return (
    <p className="audit-summary" aria-label="Afirmaciones de la respuesta">
      {parts
        .filter(([, n]) => n > 0)
        .map(([status, n, one, many]) => (
          <span key={status} className={`audit-summary__item audit-summary__item--${status}`}>
            <span className={`claim claim--${status}`} aria-hidden="true">
              {status === 'verified' ? 'V' : status === 'documented' ? 'D' : status === 'inferred' ? 'I' : '?'}
            </span>
            {n} {n === 1 ? one : many}
          </span>
        ))}
    </p>
  );
}
