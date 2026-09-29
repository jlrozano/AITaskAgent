import { ApiError, apiErrorFromText, networkErrorMessage } from './client';
import type { DocumentSummaryDto } from './types';

export interface UploadOptions {
  /** Bytes enviados y total; solo si el navegador conoce el total. */
  onProgress?: (loaded: number, total: number) => void;
  signal?: AbortSignal;
}

/**
 * Sube un fichero a `POST /api/documents` (plan v2 §5.1, §5.6): el cuerpo son los bytes tal cual, con
 * `Content-Type: application/octet-stream` y el nombre en `X-File-Name` codificado con `encodeURIComponent`; un fichero
 * por petición. Con `XMLHttpRequest` y no `fetch`, porque `fetch` no informa del progreso de subida.
 *
 * Resuelve con el documento creado (201). Rechaza con `ApiError` (código del servidor, o `network` si no hay respuesta;
 * en `duplicate_document`, `error.body.documentId` es el documento existente) y con `DOMException` `AbortError` si se
 * aborta.
 */
export function uploadDocument(file: File, options: UploadOptions = {}): Promise<DocumentSummaryDto> {
  const { onProgress, signal } = options;
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('Subida cancelada.', 'AbortError'));
      return;
    }

    const xhr = new XMLHttpRequest();
    const onAbort = () => xhr.abort();
    const cleanup = () => signal?.removeEventListener('abort', onAbort);

    xhr.open('POST', '/api/documents');
    xhr.setRequestHeader('Content-Type', 'application/octet-stream');
    xhr.setRequestHeader('X-File-Name', encodeURIComponent(file.name));
    xhr.setRequestHeader('Accept', 'application/json');
    if (onProgress) {
      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable) onProgress(e.loaded, e.total);
      };
    }
    xhr.onload = () => {
      cleanup();
      if (xhr.status === 201 || xhr.status === 200) {
        try {
          resolve(JSON.parse(xhr.responseText) as DocumentSummaryDto);
        } catch {
          reject(apiErrorFromText(xhr.status, null));
        }
        return;
      }
      reject(apiErrorFromText(xhr.status, xhr.responseText));
    };
    xhr.onerror = () => {
      cleanup();
      reject(new ApiError(0, 'network', networkErrorMessage));
    };
    xhr.onabort = () => {
      cleanup();
      reject(new DOMException('Subida cancelada.', 'AbortError'));
    };
    signal?.addEventListener('abort', onAbort);
    xhr.send(file);
  });
}
