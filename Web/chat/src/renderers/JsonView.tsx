// Visor JSON propio (sin dependencias): árbol plegable de objetos/arrays con los escalares coloreados por variables CSS,
// cadenas largas con salto de línea y botón de copiar. Lo que no es JSON válido se muestra como texto.
import { useState } from "react";
import type { Labels } from "../labels.ts";

type Parsed = { kind: "json"; value: unknown } | { kind: "text"; text: string };

/** Un objeto/array se pinta como árbol; un string que contiene JSON de objeto/array se parsea; el resto de strings, texto. */
export function parseValue(raw: unknown): Parsed {
  if (typeof raw === "string") {
    const t = raw.trim();
    if (t.startsWith("{") || t.startsWith("[")) {
      try {
        const v: unknown = JSON.parse(t);
        if (typeof v === "object" && v !== null) return { kind: "json", value: v };
      } catch {
        /* no es JSON: texto */
      }
    }
    return { kind: "text", text: raw };
  }
  return { kind: "json", value: raw };
}

function copyText(text: string): Promise<void> {
  try {
    if (navigator.clipboard?.writeText) return navigator.clipboard.writeText(text);
  } catch {
    /* sin portapapeles */
  }
  return Promise.reject(new Error("clipboard"));
}

function Scalar({ v }: { v: unknown }) {
  if (v === null) return <span className="atc-json-null">null</span>;
  if (typeof v === "string") return <span className="atc-json-str">{JSON.stringify(v)}</span>;
  if (typeof v === "number") return <span className="atc-json-num">{String(v)}</span>;
  if (typeof v === "boolean") return <span className="atc-json-bool">{String(v)}</span>;
  return <span className="atc-json-null">{String(v)}</span>;
}

function Node({ name, value, depth, labels }: { name?: string; value: unknown; depth: number; labels: Labels }) {
  const composite = typeof value === "object" && value !== null;
  const [open, setOpen] = useState(depth < 3);
  const key = name !== undefined ? <><span className="atc-json-key">{name}</span><span className="atc-json-punct">: </span></> : null;
  if (!composite) {
    return (
      <div className="atc-json-row" role="treeitem">
        <span className="atc-json-toggle" aria-hidden="true" />
        {key}
        <Scalar v={value} />
      </div>
    );
  }
  const isArr = Array.isArray(value);
  const entries: Array<[string, unknown]> = isArr ? (value as unknown[]).map((x, i) => [String(i), x]) : Object.entries(value as Record<string, unknown>);
  const [o, c] = isArr ? ["[", "]"] : ["{", "}"];
  const count = `${entries.length} ${isArr ? labels.jsonItems : labels.jsonKeys}`;
  const empty = entries.length === 0;
  return (
    <div className="atc-json-node" role="treeitem" aria-expanded={empty ? undefined : open}>
      <div className="atc-json-row">
        {empty ? (
          <span className="atc-json-toggle" aria-hidden="true" />
        ) : (
          <button type="button" className="atc-json-toggle" aria-label={`${open ? labels.jsonCollapse : labels.jsonExpand}${name !== undefined ? ` ${name}` : ""}`} data-testid="json-toggle" onClick={() => setOpen((x) => !x)}>
            <span aria-hidden="true">{open ? "▾" : "▸"}</span>
          </button>
        )}
        {key}
        <span className="atc-json-punct">{o}</span>
        {empty ? <span className="atc-json-punct">{c}</span> : null}
        {!empty && !open ? <span className="atc-json-meta"> {count} </span> : null}
        {!empty && !open ? <span className="atc-json-punct">{c}</span> : null}
      </div>
      {open && !empty ? (
        <div className="atc-json-children" role="group">
          {entries.map(([k, v]) => (
            <Node key={k} name={k} value={v} depth={depth + 1} labels={labels} />
          ))}
          <div className="atc-json-row"><span className="atc-json-toggle" aria-hidden="true" /><span className="atc-json-punct">{c}</span></div>
        </div>
      ) : null}
    </div>
  );
}

/** Valor JSON (o texto) con botón de copiar. */
export function JsonView({ value, labels, testId }: { value: unknown; labels: Labels; testId?: string }) {
  const parsed = parseValue(value);
  const [copied, setCopied] = useState(false);
  const text = parsed.kind === "text" ? parsed.text : (JSON.stringify(parsed.value, null, 2) ?? String(parsed.value));
  const copy = () => {
    void copyText(text).then(
      () => {
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      },
      () => undefined,
    );
  };
  return (
    <div className="atc-json" data-testid={testId} data-kind={parsed.kind}>
      <button type="button" className="atc-json-copy" onClick={copy} data-testid="json-copy">{copied ? labels.copied : labels.copy}</button>
      {parsed.kind === "text" ? (
        <pre className="atc-json-text">{parsed.text}</pre>
      ) : (
        <div className="atc-json-tree" role="tree">
          <Node value={parsed.value} depth={0} labels={labels} />
        </div>
      )}
    </div>
  );
}
