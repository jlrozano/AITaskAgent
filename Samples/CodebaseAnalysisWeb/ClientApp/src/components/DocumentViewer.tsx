import { useEffect, useState } from 'react';
import { ApiError, api, errorMessage } from '../api/client';
import type { DocumentDetailDto, DocumentSummaryDto, KnowledgeStatusDto } from '../api/types';
import { formatBytes, formatDateTime } from '../format';
import { useWorkspace } from '../state/workspace';
import { KnowledgeActions } from './KnowledgeActions';
import { StatusBanner } from './StatusBanner';
import { ExtractedTextView } from './viewers/ExtractedTextView';
import { HtmlViewer } from './viewers/HtmlViewer';
import { ImageViewer } from './viewers/ImageViewer';
import { PdfViewer } from './viewers/PdfViewer';

interface Props {
  id: string;
  /** Cambió algo que sale en la lista (título, uso en el análisis, conocimiento, descripción). */
  onChanged: (doc: DocumentSummaryDto) => void;
  onDeleted: () => void;
  analysisRunning: boolean;
  chatBusy: boolean;
}

export const FAMILY_LABEL: Record<DocumentSummaryDto['family'], string> = {
  markdown: 'MD',
  text: 'TXT',
  code: 'CÓD',
  structured: 'DAT',
  csv: 'CSV',
  html: 'HTML',
  pdf: 'PDF',
  docx: 'DOCX',
  xlsx: 'XLSX',
  pptx: 'PPTX',
  image: 'IMG',
  svg: 'SVG',
};

const EXTRACTION_TEXT: Record<DocumentSummaryDto['extraction']['status'], string> = {
  ok: 'Texto extraído',
  empty: 'Sin texto',
  failed: 'Extracción fallida',
  pending_description: 'Sin describir',
};

export function extractionText(doc: DocumentSummaryDto): string {
  return EXTRACTION_TEXT[doc.extraction.status] ?? doc.extraction.status;
}

/** Visores con original y texto derivado distintos: pestañas «Vista» y «Texto extraído» (decisión 0-4). */
const HAS_ORIGINAL_VIEW = new Set(['pdf', 'html', 'image']);

type Notice = { tone: 'warning' | 'error' | 'success'; text: string } | null;

/**
 * Visor de un documento aportado (V2-C, plan v2 §9), elegido por `viewer`: Markdown o preformateado sobre `/text`,
 * PDF e HTML en iframe y la imagen en `<img>` sobre `/raw`. Cabecera con renombrar, «Usar en el análisis»,
 * «Descargar original» y eliminar, y `KnowledgeActions` (V2-D).
 */
export function DocumentViewer({ id, onChanged, onDeleted, analysisRunning, chatBusy }: Props) {
  const { meta } = useWorkspace();
  const [doc, setDoc] = useState<DocumentDetailDto | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [text, setText] = useState<string | null>(null);
  const [textError, setTextError] = useState<string | null>(null);
  const [tab, setTab] = useState<'view' | 'text'>('view');
  const [textVersion, setTextVersion] = useState(0);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [busy, setBusy] = useState<'describe' | 'patch' | 'delete' | null>(null);
  const [notice, setNotice] = useState<Notice>(null);

  useEffect(() => {
    const controller = new AbortController();
    setDoc(null);
    setLoadError(null);
    setTab('view');
    setNotice(null);
    setRenaming(null);
    setConfirmDelete(false);
    api.getDocument(id, controller.signal).then(setDoc, (e) => {
      if (controller.signal.aborted) return;
      setLoadError(e instanceof ApiError && e.status === 404 ? 'Este documento ya no existe.' : errorMessage(e));
    });
    return () => controller.abort();
  }, [id]);

  const viewer = doc?.viewer ?? null;
  const needsText = viewer !== null && (!HAS_ORIGINAL_VIEW.has(viewer) || tab === 'text');

  useEffect(() => {
    if (!needsText) return;
    const controller = new AbortController();
    setText(null);
    setTextError(null);
    api.getDocumentText(id, controller.signal).then(setText, (e) => {
      if (!controller.signal.aborted) setTextError(errorMessage(e));
    });
    return () => controller.abort();
  }, [id, needsText, textVersion]);

  if (loadError) return <StatusBanner tone="error">{loadError}</StatusBanner>;
  if (!doc) return <p className="muted">Cargando documento…</p>;

  const raw = api.documentRawUrl(doc.id);
  const imageDescription = meta?.features.imageDescription;
  const canDescribe = doc.family === 'image';
  const describeReason = !imageDescription?.enabled ? (imageDescription?.disabledReason ?? 'No disponible.') : null;

  const update = (next: DocumentSummaryDto | DocumentDetailDto) => {
    const merged: DocumentDetailDto = { ...doc, ...next };
    setDoc(merged);
    onChanged(merged);
  };

  const act = async (kind: 'describe' | 'patch' | 'delete', call: () => Promise<void>) => {
    setBusy(kind);
    setNotice(null);
    try {
      await call();
    } catch (e) {
      setNotice({ tone: e instanceof ApiError && e.status === 409 ? 'warning' : 'error', text: errorMessage(e) });
    } finally {
      setBusy(null);
    }
  };

  const describe = () =>
    act('describe', async () => {
      const next = await api.describeDocument(doc.id);
      update(next);
      setTextVersion((n) => n + 1);
      setNotice({ tone: 'success', text: 'Descripción generada: está en «Texto extraído».' });
    });

  const saveTitle = () => {
    const title = (renaming ?? '').trim();
    if (!title || title === doc.title) {
      setRenaming(null);
      return;
    }
    void act('patch', async () => {
      update(await api.patchDocument(doc.id, { title }));
      setRenaming(null);
    });
  };

  const toggleUse = (useInAnalysis: boolean) =>
    void act('patch', async () => update(await api.patchDocument(doc.id, { useInAnalysis })));

  const remove = () =>
    void act('delete', async () => {
      setConfirmDelete(false);
      await api.deleteDocument(doc.id);
      onDeleted();
    });

  const onKnowledge = (status: KnowledgeStatusDto) => update({ ...doc, knowledge: { graph: status.graph, embeddings: status.embeddings } });

  const ex = doc.extraction;
  const describeButton = canDescribe ? (
    <button
      type="button"
      className="btn btn--small btn--primary"
      onClick={() => void describe()}
      disabled={!!describeReason || busy !== null}
      title={describeReason ?? `Genera con ${imageDescription?.model ?? 'el modelo'} una descripción que el asistente puede leer`}
    >
      {busy === 'describe' ? 'Describiendo…' : doc.description ? 'Describir de nuevo' : 'Describir'}
    </button>
  ) : null;

  return (
    <article className="doc" aria-label={doc.title}>
      <header className="doc-head">
        <span className={`doc-badge doc-badge--${doc.family}`} aria-hidden="true">
          {FAMILY_LABEL[doc.family] ?? doc.family}
        </span>
        <div className="doc-head__main">
          {renaming === null ? (
            <h2 className="doc-head__title">{doc.title}</h2>
          ) : (
            <form
              className="doc-head__rename"
              onSubmit={(e) => {
                e.preventDefault();
                saveTitle();
              }}
            >
              <input
                className="input"
                value={renaming}
                maxLength={200}
                autoFocus
                aria-label="Título del documento"
                onChange={(e) => setRenaming(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Escape') setRenaming(null);
                }}
              />
              <button type="submit" className="btn btn--small btn--primary" disabled={busy !== null}>
                Guardar
              </button>
              <button type="button" className="btn btn--small btn--ghost" onClick={() => setRenaming(null)}>
                Cancelar
              </button>
            </form>
          )}
          <p className="doc-head__meta">
            <span className="doc-head__file">{doc.originalFileName}</span>
            <span>{formatBytes(doc.sizeBytes)}</span>
            {ex.pageCount !== null && <span>{ex.pageCount} {doc.family === 'pptx' ? 'diapositivas' : doc.family === 'xlsx' ? 'hojas' : 'páginas'}</span>}
            <span>{ex.sectionCount === 1 ? '1 sección' : `${ex.sectionCount} secciones`}</span>
            <time dateTime={doc.uploadedAt}>Subido {formatDateTime(doc.uploadedAt)}</time>
          </p>
        </div>
        <div className="doc-head__actions">
          <label className="check check--inline" title="Los auditores reciben un índice de los documentos marcados">
            <input
              type="checkbox"
              checked={doc.useInAnalysis}
              disabled={busy !== null}
              onChange={(e) => toggleUse(e.target.checked)}
            />
            Usar en el análisis
          </label>
          <a className="btn btn--small" href={raw} download={doc.originalFileName}>
            Descargar original
          </a>
          {renaming === null && (
            <button type="button" className="btn btn--small btn--ghost" onClick={() => setRenaming(doc.title)}>
              Renombrar
            </button>
          )}
          {confirmDelete ? (
            <span className="doc-head__confirm">
              <button type="button" className="btn btn--small btn--danger" onClick={remove} disabled={busy !== null}>
                Sí, eliminar
              </button>
              <button type="button" className="btn btn--small btn--ghost" onClick={() => setConfirmDelete(false)}>
                No
              </button>
            </span>
          ) : (
            <button type="button" className="btn btn--small btn--danger" onClick={() => setConfirmDelete(true)} disabled={busy !== null}>
              {busy === 'delete' ? 'Eliminando…' : 'Eliminar'}
            </button>
          )}
        </div>
      </header>

      {notice && (
        <StatusBanner tone={notice.tone} onDismiss={() => setNotice(null)}>
          {notice.text}
        </StatusBanner>
      )}
      {ex.status === 'failed' && (
        <StatusBanner tone="warning" title="No se ha podido extraer el texto">
          {ex.warnings[0] ?? 'El asistente no puede leer este documento; el original sigue disponible.'}
        </StatusBanner>
      )}
      {ex.status === 'empty' && (
        <StatusBanner tone="warning" title="El documento no tiene texto extraíble">
          {ex.warnings[0] ?? 'El asistente no puede leer nada de él.'}
        </StatusBanner>
      )}
      {ex.status === 'pending_description' && (
        <StatusBanner tone="info" title="Imagen sin describir">
          El asistente no ve la imagen hasta que se describe. {describeReason ? describeReason : 'Pulsa «Describir».'}
        </StatusBanner>
      )}
      {ex.truncated && <StatusBanner tone="warning">El texto extraído se ha truncado: el asistente solo lee el principio.</StatusBanner>}
      {ex.status !== 'failed' && ex.status !== 'empty' && ex.warnings.length > 0 && (
        <ul className="doc-warnings" aria-label="Avisos de la extracción">
          {ex.warnings.map((w, i) => (
            <li key={i}>{w}</li>
          ))}
        </ul>
      )}

      <KnowledgeActions
        source={{ kind: 'document', id: doc.id }}
        knowledge={doc.knowledge}
        onChange={onKnowledge}
        analysisRunning={analysisRunning}
        chatBusy={chatBusy}
      />

      {HAS_ORIGINAL_VIEW.has(doc.viewer) && (
        <div className="doc-tabs" role="tablist" aria-label="Vista del documento">
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'view'}
            className={`seg${tab === 'view' ? ' seg--on' : ''}`}
            onClick={() => setTab('view')}
          >
            Original
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'text'}
            className={`seg${tab === 'text' ? ' seg--on' : ''}`}
            onClick={() => setTab('text')}
          >
            Texto extraído (lo que lee el asistente)
          </button>
        </div>
      )}
      {doc.description && tab === 'text' && (
        <p className="hint">
          Descripción generada por {doc.description.model} el {formatDateTime(doc.description.generatedAt)}; puede contener errores.
        </p>
      )}

      <div className="doc-body">
        {tab === 'view' && doc.viewer === 'pdf' && <PdfViewer src={raw} title={doc.title} />}
        {tab === 'view' && doc.viewer === 'html' && <HtmlViewer src={raw} title={doc.title} />}
        {tab === 'view' && doc.viewer === 'image' && <ImageViewer src={raw} title={doc.title} actions={describeButton} />}
        {(doc.viewer === 'markdown' || doc.viewer === 'text' || tab === 'text') && (
          <ExtractedTextView
            text={text}
            error={textError}
            mode={doc.viewer === 'text' ? 'pre' : 'markdown'}
            label={doc.title}
            emptyText={
              ex.status === 'pending_description' ? 'Aún no hay descripción de esta imagen.' : 'No hay texto extraído.'
            }
          />
        )}
      </div>
    </article>
  );
}
