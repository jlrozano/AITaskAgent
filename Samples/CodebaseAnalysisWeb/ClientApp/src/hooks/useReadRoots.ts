import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage } from '../api/client';
import type { ChatReadRootDto } from '../api/types';

export interface ReadRootsApi {
  roots: ChatReadRootDto[];
  /** Añadir o quitar en curso: es instantáneo (solo se guarda la ruta), sirve para no duplicar el envío. */
  busy: boolean;
  /** Motivo del último rechazo (ruta no válida...); se limpia al volver a intentar. */
  error: string | null;
  add: (path: string) => Promise<boolean>;
  remove: (path: string) => Promise<void>;
}

/**
 * Rutas de lectura adicionales del chat del repositorio abierto. Si la lista no carga queda vacía sin error: el
 * formulario sigue sirviendo y el servidor rechazará lo que no valga.
 */
export function useReadRoots(enabled: boolean): ReadRootsApi {
  const [roots, setRoots] = useState<ChatReadRootDto[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!enabled) {
      setRoots([]);
      return;
    }
    let cancelled = false;
    api.listChatReadRoots().then(
      (dto) => !cancelled && setRoots(dto.roots),
      () => !cancelled && setRoots([]),
    );
    return () => {
      cancelled = true;
    };
  }, [enabled]);

  const add = useCallback(async (path: string) => {
    setBusy(true);
    setError(null);
    try {
      setRoots((await api.addChatReadRoot(path)).roots);
      return true;
    } catch (e) {
      setError(errorMessage(e));
      return false;
    } finally {
      setBusy(false);
    }
  }, []);

  const remove = useCallback(async (path: string) => {
    setBusy(true);
    setError(null);
    try {
      await api.removeChatReadRoot(path);
      setRoots((current) => current.filter((r) => r.path !== path));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }, []);

  return { roots, busy, error, add, remove };
}
