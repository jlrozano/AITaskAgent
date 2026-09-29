import { useCallback, useEffect, useRef, useState } from 'react';

export type TabId = 'analisis' | 'asistente';

/** Lo que abre la vista Asistente: el fichero del visor y la conversación del chat. */
export interface AssistantParams {
  /** `<fichero>`, `custom/<fichero>` o `doc/<id>`. */
  file: string | null;
  chat: string | null;
}

export interface HashRoute extends AssistantParams {
  tab: TabId;
}

const EMPTY: AssistantParams = { file: null, chat: null };

function decode(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

/**
 * `#asistente?file=<sel>&chat=<id>`. Los enlaces de antes de fusionar Informes y Chat siguen valiendo:
 * `#informes/<sel>` abre `file` y `#chat/<id>` abre `chat` (`legacy` avisa para reescribir el hash).
 */
export function parseHash(hash: string): HashRoute & { legacy: boolean } {
  const raw = hash.replace(/^#\/?/, '');
  const cut = raw.search(/[/?]/);
  const head = (cut === -1 ? raw : raw.slice(0, cut)).toLowerCase();
  const rest = cut === -1 ? '' : raw.slice(cut + 1);
  if (head === 'informes') return { tab: 'asistente', file: rest ? decode(rest) : null, chat: null, legacy: true };
  if (head === 'chat') return { tab: 'asistente', file: null, chat: rest ? decode(rest) : null, legacy: true };
  if (head !== 'asistente') return { tab: 'analisis', ...EMPTY, legacy: false };
  const query = new URLSearchParams(raw[cut] === '?' ? rest : '');
  return { tab: 'asistente', file: query.get('file') || null, chat: query.get('chat') || null, legacy: false };
}

/** Cada segmento de `file` se codifica aparte: `custom/x.md` queda legible y `parseHash` lo devuelve igual. */
export function formatHash(route: HashRoute): string {
  if (route.tab !== 'asistente') return `#${route.tab}`;
  const query = [
    route.file ? `file=${route.file.split('/').map(encodeURIComponent).join('/')}` : null,
    route.chat ? `chat=${encodeURIComponent(route.chat)}` : null,
  ].filter(Boolean);
  return `#asistente${query.length ? `?${query.join('&')}` : ''}`;
}

export interface HashNavigation {
  route: HashRoute;
  /** Lo último abierto en Asistente, también mientras se está en otra pestaña: el chat sigue ligado a su sesión. */
  assistant: AssistantParams;
  /** Ir a una pestaña. En Asistente, los parámetros que no se pasan se conservan. */
  navigate: (tab: TabId, params?: Partial<AssistantParams>) => void;
  /** Olvida fichero y conversación (otro repositorio). */
  resetAssistant: () => void;
}

/** Pestaña activa reflejada en `location.hash`, sin router (§7). */
export function useHashTab(): HashNavigation {
  const [route, setRoute] = useState<HashRoute>(() => {
    const { legacy, ...parsed } = parseHash(window.location.hash);
    if (legacy) window.history.replaceState(null, '', formatHash(parsed));
    return parsed;
  });
  const routeRef = useRef(route);
  const lastAssistant = useRef<AssistantParams>(route.tab === 'asistente' ? { file: route.file, chat: route.chat } : EMPTY);

  const apply = useCallback((next: HashRoute) => {
    routeRef.current = next;
    if (next.tab === 'asistente') lastAssistant.current = { file: next.file, chat: next.chat };
    setRoute(next);
  }, []);

  useEffect(() => {
    const onChange = () => {
      const { legacy, ...parsed } = parseHash(window.location.hash);
      if (legacy) window.history.replaceState(null, '', formatHash(parsed));
      apply(parsed);
    };
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, [apply]);

  const navigate = useCallback(
    (tab: TabId, params: Partial<AssistantParams> = {}) => {
      const next: HashRoute = tab === 'asistente' ? { tab, ...lastAssistant.current, ...params } : { tab, ...EMPTY };
      const hash = formatHash(next);
      if (window.location.hash !== hash) window.location.hash = hash;
      apply(next);
    },
    [apply],
  );

  const resetAssistant = useCallback(() => {
    lastAssistant.current = EMPTY;
    if (routeRef.current.tab === 'asistente') navigate('asistente', EMPTY);
  }, [navigate]);

  const assistant = route.tab === 'asistente' ? { file: route.file, chat: route.chat } : lastAssistant.current;
  return { route, assistant, navigate, resetAssistant };
}
