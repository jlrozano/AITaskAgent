import hljs from 'highlight.js/lib/common';
import { useEffect, useMemo, useRef, useState } from 'react';
import { ApiError, api, errorMessage } from '../../api/client';
import type { SourceFileDto } from '../../api/types';
import { formatBytes } from '../../format';
import { StatusBanner } from '../StatusBanner';

/** Por encima, el fichero se enseña sin resaltar: highlight.js tarda demasiado. */
const MAX_HIGHLIGHT_CHARS = 300_000;

const LANGUAGE_BY_EXTENSION: Record<string, string> = {
  cs: 'csharp',
  csx: 'csharp',
  vb: 'vbnet',
  bas: 'vbnet',
  cls: 'vbnet',
  frm: 'vbnet',
  ts: 'typescript',
  tsx: 'typescript',
  js: 'javascript',
  jsx: 'javascript',
  mjs: 'javascript',
  java: 'java',
  kt: 'kotlin',
  py: 'python',
  go: 'go',
  rs: 'rust',
  c: 'c',
  h: 'c',
  cpp: 'cpp',
  hpp: 'cpp',
  sql: 'sql',
  json: 'json',
  xml: 'xml',
  csproj: 'xml',
  props: 'xml',
  config: 'xml',
  html: 'xml',
  css: 'css',
  scss: 'scss',
  md: 'markdown',
  yml: 'yaml',
  yaml: 'yaml',
  ps1: 'powershell',
  sh: 'bash',
  php: 'php',
  rb: 'ruby',
};

export function languageFor(path: string): string | null {
  const ext = /\.([A-Za-z0-9]+)$/.exec(path)?.[1]?.toLowerCase();
  const language = ext ? LANGUAGE_BY_EXTENSION[ext] : undefined;
  return language && hljs.getLanguage(language) ? language : null;
}

const escapeHtml = (text: string) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/**
 * Parte en líneas el HTML de highlight.js cerrando los `<span>` abiertos al final de cada línea y reabriéndolos en la
 * siguiente, para que un comentario o una cadena de varias líneas siga coloreada.
 */
export function splitHighlightedLines(html: string): string[] {
  const lines: string[] = [];
  const open: string[] = [];
  let current = '';
  for (const token of html.match(/<span[^>]*>|<\/span>|\n|[^<\n]+/g) ?? []) {
    if (token === '\n') {
      lines.push(current + '</span>'.repeat(open.length));
      current = open.join('');
    } else if (token.startsWith('</span')) {
      open.pop();
      current += token;
    } else if (token.startsWith('<span')) {
      open.push(token);
      current += token;
    } else {
      current += token;
    }
  }
  lines.push(current + '</span>'.repeat(open.length));
  return lines;
}

/** Líneas en HTML seguro: el de highlight.js ya viene escapado; sin lenguaje, se escapa aquí. */
export function renderLines(text: string, path: string): string[] {
  const body = text.endsWith('\n') ? text.slice(0, -1) : text;
  const language = body.length <= MAX_HIGHLIGHT_CHARS ? languageFor(path) : null;
  if (!language) return body.split('\n').map(escapeHtml);
  return splitHighlightedLines(hljs.highlight(body, { language, ignoreIllegals: true }).value);
}

interface Props {
  path: string;
  startLine?: number | null;
  endLine?: number | null;
}

/** Visor de un fichero del repositorio: números de línea, resaltado y el tramo citado marcado y a la vista. */
export function SourceViewer({ path, startLine, endLine }: Props) {
  const [file, setFile] = useState<SourceFileDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const firstHit = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    const controller = new AbortController();
    setFile(null);
    setError(null);
    api.getSource(path, controller.signal).then(setFile, (e) => {
      if (controller.signal.aborted) return;
      setError(e instanceof ApiError && e.status === 404 ? `No existe «${path}» en el repositorio.` : errorMessage(e));
    });
    return () => controller.abort();
  }, [path]);

  const lines = useMemo(() => (file ? renderLines(file.text, file.path) : []), [file]);
  const from = startLine ?? null;
  const to = endLine ?? startLine ?? null;

  useEffect(() => {
    // jsdom no tiene scrollIntoView.
    firstHit.current?.scrollIntoView?.({ block: 'center' });
  }, [lines, from, to]);

  if (error) return <StatusBanner tone="error">{error}</StatusBanner>;
  if (!file) return <p className="muted">Cargando {path}…</p>;

  const outOfRange = from !== null && from > lines.length;
  return (
    <div className="source-viewer">
      <header className="doc-head source-viewer__head">
        <span className="doc-badge doc-badge--code" aria-hidden="true">
          Código
        </span>
        <div className="doc-head__main">
          <h2 className="doc-head__title source-viewer__path">{file.path}</h2>
          <p className="doc-head__meta">
            {from !== null && <span>{to !== null && to !== from ? `Líneas ${from}–${to}` : `Línea ${from}`}</span>}
            <span>{file.totalLines === 1 ? '1 línea' : `${file.totalLines} líneas`}</span>
            <span>{formatBytes(file.sizeBytes)}</span>
          </p>
        </div>
      </header>
      {outOfRange && <StatusBanner tone="warning">El fichero no llega a la línea {from}: puede haber cambiado desde la respuesta.</StatusBanner>}
      <pre className="source" aria-label={`Código de ${file.path}`}>
        <code className="hljs">
          {lines.map((html, i) => {
            const n = i + 1;
            const hit = from !== null && to !== null && n >= from && n <= to;
            return (
              <span key={n} className={`source__line${hit ? ' source__line--hit' : ''}`} ref={hit && n === from ? firstHit : undefined}>
                <span className="source__num" aria-hidden="true">
                  {n}
                </span>
                <span className="source__text" dangerouslySetInnerHTML={{ __html: html || ' ' }} />
              </span>
            );
          })}
        </code>
      </pre>
    </div>
  );
}
