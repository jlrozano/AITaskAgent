import type { ChatReference } from '../api/types';

/**
 * Marcas `[V]` `[D]` `[I]` y referencias de las respuestas del chat. La numeración de las marcas tiene que coincidir con
 * la de `ClaimAuditor` en el servidor: en orden de aparición, fuera de bloques y spans de código.
 */

export type ClaimMark = 'V' | 'D' | 'I';

export const MARK_KIND = { V: 'verified', D: 'documented', I: 'inferred' } as const;

const SOURCE_SEPARATOR = ' › ';
// Igual que `CodeReferencePattern` de ClaimAuditor.
const CODE_REFERENCE = /^(?<path>(?:[A-Za-z]:[\\/])?[^\s:*?"<>|`]+?)(?::(?<s>\d+)(?:\s*[-–]\s*(?<e>\d+))?)?$/;

export interface CodeReference {
  path: string;
  start: number | null;
  end: number | null;
}

/** `ruta:línea` o `ruta:ini-fin`; sin línea, solo si parece una ruta (`Pedidos.MontarFiltro` es un identificador). */
export function parseCodeReference(text: string): CodeReference | null {
  const m = CODE_REFERENCE.exec(text.trim());
  if (!m?.groups) return null;
  const { path, s, e } = m.groups;
  if (!s && !/[/\\]/.test(path)) return null;
  const start = s ? Number(s) : null;
  return { path: path.replace(/\\/g, '/'), start, end: e ? Number(e) : start };
}

export function looksLikeReference(text: string): boolean {
  return text.includes(SOURCE_SEPARATOR) || parseCodeReference(text) !== null;
}

// ── Selección del visor para código: `src/<ruta>` o `src/<ruta>:<ini>-<fin>` ──

export const SOURCE_PREFIX = 'src/';

export function sourceSelection(path: string, start?: number | null, end?: number | null): string {
  const clean = path.replace(/\\/g, '/').replace(/^\/+/, '').replace(/^\.\//, '');
  if (!start) return SOURCE_PREFIX + clean;
  return `${SOURCE_PREFIX}${clean}:${start}${end && end !== start ? `-${end}` : ''}`;
}

export function parseSourceSelection(id: string): CodeReference | null {
  if (!id.startsWith(SOURCE_PREFIX)) return null;
  const rest = id.slice(SOURCE_PREFIX.length);
  const m = /^(.+?):(\d+)(?:-(\d+))?$/.exec(rest);
  if (!m) return rest ? { path: rest, start: null, end: null } : null;
  const start = Number(m[2]);
  return { path: m[1], start, end: m[3] ? Number(m[3]) : start };
}

/** Lo que abre el visor al pulsar una referencia; null si no hay nada que abrir. */
export function selectionFor(text: string, reference: ChatReference | undefined): string | null {
  if (reference) {
    if (reference.status === 'missing') return null;
    if (reference.kind === 'source') return reference.target;
    return sourceSelection(reference.target, reference.startLine, reference.endLine);
  }
  // Sin auditoría (en streaming o turnos antiguos): el código se abre tal cual se citó; las fuentes, no.
  const code = parseCodeReference(text);
  return code ? sourceSelection(code.path, code.start, code.end) : null;
}

// ── Plugin de remark ──

interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
  data?: Record<string, unknown>;
}

const SKIP = new Set(['code', 'inlineCode', 'html']);
const MARK = /\[([VDI])\]/g;
const HAS_MARK = /\[[VDI]\]/;

function markNode(mark: ClaimMark, index: number): MdNode {
  return {
    type: 'claimMark',
    data: { hName: 'span', hProperties: { dataClaim: String(index), dataMark: mark }, hChildren: [{ type: 'text', value: mark }] },
  };
}

function referenceNode(text: string): MdNode {
  return { type: 'claimRef', data: { hName: 'code', hProperties: { dataRef: text }, hChildren: [{ type: 'text', value: text }] } };
}

/**
 * Convierte cada marca de un texto en `<span data-claim="n" data-mark="V">` y cada span de código que parece una
 * referencia en `<code data-ref="…">`, para que la vista los pinte como insignia y como enlace al visor.
 */
export function remarkClaims() {
  return (tree: MdNode) => {
    let index = 0;
    const walk = (node: MdNode) => {
      if (!node.children) return;
      const out: MdNode[] = [];
      for (const child of node.children) {
        if (child.type === 'text' && child.value && HAS_MARK.test(child.value)) {
          let last = 0;
          for (const m of child.value.matchAll(MARK)) {
            if (m.index > last) out.push({ type: 'text', value: child.value.slice(last, m.index) });
            out.push(markNode(m[1] as ClaimMark, index++));
            last = m.index + m[0].length;
          }
          if (last < child.value.length) out.push({ type: 'text', value: child.value.slice(last) });
          continue;
        }
        if (child.type === 'inlineCode' && child.value && looksLikeReference(child.value)) {
          out.push(referenceNode(child.value));
          continue;
        }
        if (!SKIP.has(child.type)) walk(child);
        out.push(child);
      }
      node.children = out;
    };
    walk(tree);
  };
}
