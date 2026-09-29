import { useEffect, useId, useMemo, useRef, useState } from 'react';
import type { EventLevel } from '../api/types';
import { formatDuration, formatTime } from '../format';
import type { LogEntry } from '../state/runReducer';

const LEVELS: { level: EventLevel; label: string }[] = [
  { level: 'info', label: 'Info' },
  { level: 'success', label: 'Correcto' },
  { level: 'warning', label: 'Aviso' },
  { level: 'error', label: 'Error' },
];

const MAX_ROWS = 1500;
/** Tiempo durante el que un scroll se atribuye a la última rueda, toque o tecla del usuario. */
const USER_INTENT_MS = 800;
const SCROLL_KEYS = new Set(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' ']);

type Row =
  | { kind: 'entry'; entry: LogEntry; completion: LogEntry | null }
  | { kind: 'indexer'; entries: LogEntry[] };

/**
 * Agrupa para mostrar: el `tool.completed` correcto se funde con su `tool.started` (misma fila, con duración) y las
 * líneas seguidas del indexador se pliegan en un bloque.
 */
export function buildRows(entries: LogEntry[]): Row[] {
  const rows: Row[] = [];
  const openTools = new Map<string, Extract<Row, { kind: 'entry' }>[]>();
  for (const entry of entries) {
    if (entry.type === 'log' && entry.channel === 'indexer') {
      const last = rows[rows.length - 1];
      if (last?.kind === 'indexer') last.entries.push(entry);
      else rows.push({ kind: 'indexer', entries: [entry] });
      continue;
    }
    if (entry.type === 'tool.completed' && entry.success !== false) {
      const key = `${entry.source}|${entry.tool}`;
      const queue = openTools.get(key);
      const started = queue?.shift();
      if (started) {
        started.completion = entry;
        continue;
      }
    }
    const row: Extract<Row, { kind: 'entry' }> = { kind: 'entry', entry, completion: null };
    if (entry.type === 'tool.started') {
      const key = `${entry.source}|${entry.tool}`;
      openTools.set(key, [...(openTools.get(key) ?? []), row]);
    }
    rows.push(row);
  }
  return rows;
}

function EntryText({ entry, completion }: { entry: LogEntry; completion: LogEntry | null }) {
  if (entry.type === 'tool.started') {
    return (
      <>
        <span className="log__who">{entry.source ?? '—'}</span>
        <span className="log__arrow" aria-label="llama a">
          {' → '}
        </span>
        <span className="log__tool">{entry.tool}</span>
        {entry.detail && <span className="log__arg"> {entry.detail}</span>}
        {completion && <span className="log__dur"> {formatDuration(completion.durationMs)}</span>}
        {!completion && <span className="log__dur muted"> …</span>}
      </>
    );
  }
  return (
    <>
      {entry.source && <span className="log__who">{entry.source}</span>}
      <span className="log__msg">{entry.message}</span>
    </>
  );
}

export function EventLog({ entries, droppedUpToSeq }: { entries: LogEntry[]; droppedUpToSeq: number | null }) {
  const [levels, setLevels] = useState<Set<EventLevel>>(() => new Set(LEVELS.map((l) => l.level)));
  const [source, setSource] = useState('');
  const [follow, setFollow] = useState(true);
  const scrollRef = useRef<HTMLDivElement>(null);
  const followRef = useRef(follow);
  followRef.current = follow;
  // Instante de la última interacción que puede desplazar el registro (rueda, toque, tecla) y arrastre en curso de
  // la barra de scroll.
  const intentAt = useRef(0);
  const dragging = useRef(false);
  const sourceId = useId();

  const sources = useMemo(() => {
    const set = new Set<string>();
    for (const e of entries) if (e.source) set.add(e.source);
    return [...set].sort();
  }, [entries]);

  const filtered = useMemo(
    () => entries.filter((e) => levels.has(e.level) && (!source || e.source === source)),
    [entries, levels, source],
  );
  const rows = useMemo(() => buildRows(filtered), [filtered]);
  const visible = rows.length > MAX_ROWS ? rows.slice(rows.length - MAX_ROWS) : rows;

  useEffect(() => {
    const el = scrollRef.current;
    if (follow && el) el.scrollTop = el.scrollHeight;
  }, [visible, follow]);

  // Al volver a la pestaña (el panel pasa de display:none a visible) o cambiar el alto, se baja otra vez al final.
  useEffect(() => {
    const el = scrollRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(() => {
      if (followRef.current && el.clientHeight > 0) el.scrollTop = el.scrollHeight;
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const markIntent = () => {
    intentAt.current = Date.now();
  };

  /**
   * Solo el usuario desactiva «Seguir al final». El evento scroll llega asíncrono: el de nuestro propio autoscroll
   * puede leerse cuando ya se han añadido filas debajo, y el navegador emite otro al ocultar el panel. Antes cualquiera
   * que no estuviera al final lo desactivaba. Volver al final a mano lo reactiva.
   */
  const onScroll = () => {
    const el = scrollRef.current;
    if (!el || el.clientHeight === 0) return;
    const byUser = dragging.current || Date.now() - intentAt.current < USER_INTENT_MS;
    if (!byUser) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    if (atBottom !== follow) setFollow(atBottom);
  };

  const toggleLevel = (level: EventLevel) =>
    setLevels((prev) => {
      const next = new Set(prev);
      if (next.has(level)) next.delete(level);
      else next.add(level);
      return next;
    });

  return (
    <section className="log" aria-labelledby="log-title">
      <header className="log__head">
        <h2 id="log-title" className="panel-title">
          Registro
        </h2>
        <div className="log__filters" role="group" aria-label="Filtrar por nivel">
          {LEVELS.map(({ level, label }) => (
            <label key={level} className={`chip-toggle chip-toggle--${level}`}>
              <input type="checkbox" checked={levels.has(level)} onChange={() => toggleLevel(level)} />
              <span>{label}</span>
            </label>
          ))}
        </div>
        <label className="log__source" htmlFor={sourceId}>
          <span className="visually-hidden">Filtrar por origen</span>
          <select id={sourceId} value={source} onChange={(e) => setSource(e.target.value)}>
            <option value="">Todos los orígenes</option>
            {sources.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </label>
        <label className="check check--inline">
          <input type="checkbox" checked={follow} onChange={(e) => setFollow(e.target.checked)} />
          <span className="check__label">Seguir al final</span>
        </label>
      </header>
      {droppedUpToSeq !== null && (
        <p className="log__notice">El servidor descartó los eventos hasta el nº {droppedUpToSeq} (límite del registro).</p>
      )}
      {rows.length > MAX_ROWS && (
        <p className="log__notice">Se muestran las últimas {MAX_ROWS} filas de {rows.length}.</p>
      )}
      <div
        className="log__scroll"
        ref={scrollRef}
        onScroll={onScroll}
        onWheel={markIntent}
        onTouchMove={markIntent}
        onKeyDown={(e) => {
          if (SCROLL_KEYS.has(e.key)) markIntent();
        }}
        onPointerDown={() => {
          // Arrastre de la barra: dura hasta soltar, aunque se suelte fuera del registro.
          dragging.current = true;
          const end = () => {
            dragging.current = false;
            markIntent();
            window.removeEventListener('pointerup', end);
            window.removeEventListener('pointercancel', end);
          };
          window.addEventListener('pointerup', end);
          window.addEventListener('pointercancel', end);
        }}
        role="log"
        aria-live="off"
        tabIndex={0}
      >
        {visible.length === 0 ? (
          <p className="log__empty">{entries.length === 0 ? 'Todavía no hay eventos.' : 'Ningún evento cumple el filtro.'}</p>
        ) : (
          <ol className="log__list">
            {visible.map((row) =>
              row.kind === 'indexer' ? (
                <li key={`idx-${row.entries[0].seq}`} className="log__row log__row--indexer">
                  <details>
                    <summary>
                      <time className="log__time">{formatTime(row.entries[0].ts)}</time>
                      <span className="log__who">indexador</span>
                      <span className="log__msg">
                        {row.entries.length === 1 ? '1 línea' : `${row.entries.length} líneas`}
                        {' · '}
                        {row.entries[row.entries.length - 1].message}
                      </span>
                    </summary>
                    <pre className="log__indexer">{row.entries.map((e) => e.message).join('\n')}</pre>
                  </details>
                </li>
              ) : (
                <li
                  key={row.entry.seq}
                  className={`log__row log__row--${row.entry.level}${row.entry.type === 'tool.started' ? ' log__row--tool' : ''}`}
                >
                  <time className="log__time" dateTime={row.entry.ts}>
                    {formatTime(row.entry.ts)}
                  </time>
                  <span className={`log__level log__level--${row.entry.level}`}>
                    {LEVELS.find((l) => l.level === row.entry.level)?.label ?? row.entry.level}
                  </span>
                  <span className="log__text">
                    <EntryText entry={row.entry} completion={row.completion} />
                  </span>
                </li>
              ),
            )}
          </ol>
        )}
      </div>
    </section>
  );
}
