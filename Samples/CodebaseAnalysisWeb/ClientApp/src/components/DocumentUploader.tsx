import { useRef, useState, type DragEvent } from 'react';
import type { MetaDocumentsDto } from '../api/types';
import { formatBytes } from '../format';
import type { UploadItem, UploadQueue } from '../hooks/useUploadQueue';

interface Props {
  limits: MetaDocumentsDto | null | undefined;
  queue: UploadQueue;
  /** «Ya estaba subido: abrir» y el enlace de los subidos. */
  onOpen: (documentId: string) => void;
}

export function acceptList(limits: MetaDocumentsDto | null | undefined): string | undefined {
  return limits ? limits.types.flatMap((t) => t.extensions).join(',') : undefined;
}

function percent(item: UploadItem): number {
  if (item.status === 'done') return 100;
  return item.total > 0 ? Math.min(100, Math.round((item.loaded / item.total) * 100)) : 0;
}

function UploadRow({ item, onCancel, onOpen }: { item: UploadItem; onCancel: () => void; onOpen: (id: string) => void }) {
  const pct = percent(item);
  return (
    <li className={`upload upload--${item.status}`}>
      <span className="upload__name" title={item.name}>
        {item.name}
      </span>
      <span className="upload__size">{formatBytes(item.size)}</span>
      {(item.status === 'uploading' || item.status === 'queued') && (
        <>
          <span
            className="progress upload__bar"
            role="progressbar"
            aria-label={`Subiendo ${item.name}`}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={pct}
          >
            <span className="progress__fill" style={{ width: `${pct}%` }} />
          </span>
          <span className="upload__status">{item.status === 'queued' ? 'En cola' : `${pct} %`}</span>
          <button type="button" className="btn btn--ghost btn--small" onClick={onCancel} aria-label={`Cancelar ${item.name}`}>
            Cancelar
          </button>
        </>
      )}
      {item.status === 'done' && (
        <span className="upload__status upload__status--ok">
          Subido
          {item.documentId && (
            <button type="button" className="btn btn--link" onClick={() => onOpen(item.documentId as string)}>
              abrir
            </button>
          )}
        </span>
      )}
      {item.status === 'duplicate' && (
        <span className="upload__status upload__status--warn">
          Ya estaba subido
          {item.documentId && (
            <>
              :{' '}
              <button type="button" className="btn btn--link" onClick={() => onOpen(item.documentId as string)}>
                abrir
              </button>
            </>
          )}
        </span>
      )}
      {item.status === 'cancelled' && <span className="upload__status">Cancelado</span>}
      {item.status === 'error' && (
        <span className="upload__error" role="alert">
          {item.error}
        </span>
      )}
    </li>
  );
}

/**
 * Zona de subida de «Documentos aportados» (V2-C, plan v2 §9): arrastre y «Elegir ficheros», cola con progreso por
 * fichero y el resultado de cada uno. La capa de suelta de toda la vista la pone AssistantTab con la misma cola.
 */
export function DocumentUploader({ limits, queue, onOpen }: Props) {
  const input = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  const finished = queue.items.some((i) => i.status !== 'queued' && i.status !== 'uploading');

  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setOver(false);
    if (e.dataTransfer?.files?.length) queue.enqueue(e.dataTransfer.files);
  };

  return (
    <div className="uploader">
      <div
        className={`dropzone${over ? ' dropzone--over' : ''}`}
        onDragOver={(e) => {
          e.preventDefault();
          e.stopPropagation();
          setOver(true);
        }}
        onDragLeave={() => setOver(false)}
        onDrop={onDrop}
        data-testid="dropzone"
      >
        <p className="dropzone__text">Arrastra ficheros aquí o</p>
        <button type="button" className="btn btn--small" onClick={() => input.current?.click()}>
          Elegir ficheros
        </button>
        <input
          ref={input}
          type="file"
          multiple
          hidden
          accept={acceptList(limits)}
          aria-label="Elegir ficheros para subir"
          onChange={(e) => {
            if (e.target.files?.length) queue.enqueue(e.target.files);
            e.target.value = '';
          }}
        />
        {limits && (
          <p className="dropzone__hint">
            PDF, Office, Markdown, texto, código, HTML, CSV e imágenes · hasta {formatBytes(limits.maxFileBytes)} por fichero
          </p>
        )}
      </div>
      {queue.items.length > 0 && (
        <div className="uploads">
          <ul className="uploads__list" aria-label="Subidas">
            {queue.items.map((item) => (
              <UploadRow key={item.key} item={item} onCancel={() => queue.cancel(item.key)} onOpen={onOpen} />
            ))}
          </ul>
          {finished && (
            <button type="button" className="btn btn--link uploads__clear" onClick={queue.clearFinished}>
              Limpiar la lista
            </button>
          )}
        </div>
      )}
    </div>
  );
}
