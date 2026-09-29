import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '../api/client';
import { isAbortError } from '../api/sse';
import type { DocumentSummaryDto, MetaDocumentsDto } from '../api/types';
import { uploadDocument } from '../api/upload';
import { formatBytes } from '../format';

export type UploadStatus = 'queued' | 'uploading' | 'done' | 'duplicate' | 'error' | 'cancelled';

export interface UploadItem {
  key: number;
  name: string;
  size: number;
  status: UploadStatus;
  loaded: number;
  total: number;
  /** Texto para el usuario con `error`. */
  error: string | null;
  /** Con `done`, el creado; con `duplicate`, el que ya existía. */
  documentId: string | null;
}

const OFFICE_LEGACY: Record<string, string> = { '.doc': '.docx', '.xls': '.xlsx', '.ppt': '.pptx' };
const EXECUTABLE_OR_ARCHIVE = new Set(['.exe', '.dll', '.msi', '.com', '.scr', '.zip', '.7z', '.rar', '.tar', '.gz']);

export function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot <= 0 ? '' : name.slice(dot).toLowerCase();
}

/**
 * Rechazo local por extensión, vacío y tamaño (plan v2 §9): ahorra subir 20 MB para oír un 400. Manda el servidor:
 * lo que pasa aquí puede rechazarse allí igual (contenido que no casa, duplicado, límite total).
 */
export function checkLocally(file: { name: string; size: number }, limits: MetaDocumentsDto | null | undefined): string | null {
  if (!limits) return null;
  const ext = extensionOf(file.name);
  const type = limits.types.find((t) => t.extensions.includes(ext));
  if (!type) {
    if (OFFICE_LEGACY[ext]) return `Los ${ext} antiguos no se admiten: guárdalo como ${OFFICE_LEGACY[ext]} y súbelo de nuevo.`;
    if (EXECUTABLE_OR_ARCHIVE.has(ext)) return `No se admiten ejecutables ni comprimidos (${ext}).`;
    return ext ? `Tipo no admitido (${ext}).` : 'El fichero no tiene extensión: no se puede saber su tipo.';
  }
  if (file.size === 0) return 'El fichero está vacío.';
  if (file.size > type.maxBytes) return `Supera el máximo para ${ext} (${formatBytes(file.size)} de ${formatBytes(type.maxBytes)}).`;
  return null;
}

/** Texto de un fallo de subida. Todo 413 del servidor trae `file_too_large` en JSON, así que basta con su mensaje. */
export function uploadErrorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

let nextKey = 0;

/**
 * Cola de subida: un fichero a la vez, con progreso por fichero (XMLHttpRequest) y resultado por fichero.
 * `onUploaded` se llama con cada documento creado, para recargar la lista.
 */
export function useUploadQueue(limits: MetaDocumentsDto | null | undefined, onUploaded: (doc: DocumentSummaryDto) => void) {
  const [items, setItems] = useState<UploadItem[]>([]);
  const files = useRef(new Map<number, File>());
  const current = useRef<{ key: number; controller: AbortController } | null>(null);
  const onUploadedRef = useRef(onUploaded);
  onUploadedRef.current = onUploaded;

  const patch = useCallback((key: number, change: Partial<UploadItem>) => {
    setItems((list) => list.map((i) => (i.key === key ? { ...i, ...change } : i)));
  }, []);

  const enqueue = useCallback(
    (incoming: FileList | File[]) => {
      const added: UploadItem[] = [];
      for (const file of Array.from(incoming)) {
        const key = ++nextKey;
        const local = checkLocally(file, limits);
        added.push({
          key,
          name: file.name,
          size: file.size,
          status: local ? 'error' : 'queued',
          loaded: 0,
          total: file.size,
          error: local,
          documentId: null,
        });
        if (!local) files.current.set(key, file);
      }
      if (added.length > 0) setItems((list) => [...list, ...added]);
    },
    [limits],
  );

  // Arranca el siguiente en cola cuando no hay ninguno subiendo.
  useEffect(() => {
    if (current.current) return;
    const next = items.find((i) => i.status === 'queued');
    if (!next) return;
    const file = files.current.get(next.key);
    if (!file) return;
    const controller = new AbortController();
    current.current = { key: next.key, controller };
    patch(next.key, { status: 'uploading' });
    uploadDocument(file, {
      signal: controller.signal,
      onProgress: (loaded, total) => patch(next.key, { loaded, total }),
    })
      .then(
        (doc) => {
          patch(next.key, { status: 'done', loaded: next.size, documentId: doc.id });
          onUploadedRef.current(doc);
        },
        (e) => {
          if (isAbortError(e)) patch(next.key, { status: 'cancelled' });
          else if (e instanceof ApiError && e.code === 'duplicate_document')
            patch(next.key, { status: 'duplicate', documentId: e.body?.documentId ?? null, error: e.message });
          else patch(next.key, { status: 'error', error: uploadErrorText(e) });
        },
      )
      .finally(() => {
        files.current.delete(next.key);
        current.current = null;
        // Fuerza otra vuelta del efecto para el siguiente en cola.
        setItems((list) => [...list]);
      });
  }, [items, patch]);

  useEffect(() => () => current.current?.controller.abort(), []);

  const cancel = useCallback(
    (key: number) => {
      if (current.current?.key === key) {
        current.current.controller.abort();
        return;
      }
      files.current.delete(key);
      patch(key, { status: 'cancelled' });
    },
    [patch],
  );

  const clearFinished = useCallback(() => {
    setItems((list) => list.filter((i) => i.status === 'queued' || i.status === 'uploading'));
  }, []);

  return { items, enqueue, cancel, clearFinished };
}

export type UploadQueue = ReturnType<typeof useUploadQueue>;
