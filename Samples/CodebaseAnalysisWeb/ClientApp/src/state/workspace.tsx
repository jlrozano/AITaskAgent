import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, errorMessage } from '../api/client';
import type { MetaDto, WorkspaceDto } from '../api/types';

const RECENTS_KEY = 'cba.recentRepositories';
const MAX_RECENTS = 8;

// localStorage puede no existir o lanzar (ventana privada, datos bloqueados): todo va envuelto (§7).
export function readRecents(): string[] {
  try {
    const raw = window.localStorage.getItem(RECENTS_KEY);
    const parsed = raw ? (JSON.parse(raw) as unknown) : [];
    return Array.isArray(parsed) ? parsed.filter((p): p is string => typeof p === 'string').slice(0, MAX_RECENTS) : [];
  } catch {
    return [];
  }
}

export function pushRecent(path: string): string[] {
  const next = [path, ...readRecents().filter((p) => p.toLowerCase() !== path.toLowerCase())].slice(0, MAX_RECENTS);
  try {
    window.localStorage.setItem(RECENTS_KEY, JSON.stringify(next));
  } catch {
    // sin persistencia: la lista vale para esta sesión
  }
  return next;
}

export interface WorkspaceContextValue {
  workspace: WorkspaceDto | null;
  meta: MetaDto | null;
  loading: boolean;
  /** Error al cargar workspace/meta al arrancar (servidor caído...). */
  loadError: string | null;
  recents: string[];
  open: (repositoryPath: string) => Promise<void>;
  refresh: () => Promise<void>;
}

const WorkspaceContext = createContext<WorkspaceContextValue | null>(null);

export function WorkspaceProvider({ children }: { children: ReactNode }) {
  const [workspace, setWorkspace] = useState<WorkspaceDto | null>(null);
  const [meta, setMeta] = useState<MetaDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [recents, setRecents] = useState<string[]>(() => readRecents());

  const refresh = useCallback(async () => {
    try {
      const ws = await api.getWorkspace();
      setWorkspace(ws);
      setLoadError(null);
    } catch (error) {
      setLoadError(errorMessage(error));
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [ws, m] = await Promise.all([api.getWorkspace(), api.getMeta()]);
        if (cancelled) return;
        setWorkspace(ws);
        setMeta(m);
        setLoadError(null);
      } catch (error) {
        if (!cancelled) setLoadError(errorMessage(error));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  // Si arrancó sin servidor, se reintenta la meta cuando se refresca el workspace con éxito.
  useEffect(() => {
    if (meta || loadError || loading) return;
    api.getMeta().then(setMeta, () => undefined);
  }, [meta, loadError, loading]);

  const open = useCallback(async (repositoryPath: string) => {
    // Lanza ApiError (400 invalid_path, 409 analysis_running / chat_turn_running): lo muestra quien llama.
    const ws = await api.openWorkspace(repositoryPath);
    setWorkspace(ws);
    setLoadError(null);
    if (ws.repositoryPath) setRecents(pushRecent(ws.repositoryPath));
  }, []);

  const value = useMemo<WorkspaceContextValue>(
    () => ({ workspace, meta, loading, loadError, recents, open, refresh }),
    [workspace, meta, loading, loadError, recents, open, refresh],
  );
  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>;
}

export function useWorkspace(): WorkspaceContextValue {
  const value = useContext(WorkspaceContext);
  if (!value) throw new Error('useWorkspace fuera de WorkspaceProvider');
  return value;
}

/** Para tests y componentes aislados: permite inyectar un valor fijo. */
export const WorkspaceContextProvider = WorkspaceContext.Provider;
