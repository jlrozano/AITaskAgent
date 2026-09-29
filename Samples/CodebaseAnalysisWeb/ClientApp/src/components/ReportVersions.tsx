import { useEffect, useState } from 'react';
import { ApiError, api, errorMessage } from '../api/client';
import type { ReportRef, ReportVersionDto } from '../api/types';
import { formatBytes, formatDateTime } from '../format';
import { StatusBanner } from './StatusBanner';

const ACTION_TEXT: Record<ReportVersionDto['action'], string> = {
  baseline: 'Original del análisis',
  created: 'Creado',
  updated: 'Modificado',
  restored: 'Restaurado',
  deleted: 'Borrado',
};

function originText(v: ReportVersionDto): string {
  if (v.origin.type === 'chat') return 'por el chat';
  if (v.origin.type === 'ui') return 'desde la interfaz';
  return 'por el análisis';
}

interface Props {
  report: ReportRef;
  /** Versión en vista previa (null: la actual). */
  previewId: string | null;
  onPreview: (versionId: string | null) => void;
  /** Tras restaurar: recargar el listado y el contenido. */
  onRestored: () => void;
  /** Cambia cuando cambia el informe: se vuelve a pedir el historial. */
  version: number;
}

/** Historial de un informe (V2-B, plan v2 §6.4-§6.5): de la más reciente a la más antigua, con vista previa y «Restaurar». */
export function ReportVersions({ report, previewId, onPreview, onRestored, version }: Props) {
  const [versions, setVersions] = useState<ReportVersionDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ tone: 'warning' | 'error'; text: string } | null>(null);
  const [restoring, setRestoring] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    api.getReportVersions(report, controller.signal).then(
      (list) => {
        setVersions(list);
        setError(null);
      },
      (e) => {
        if (controller.signal.aborted) return;
        setVersions(null);
        setError(e instanceof ApiError && e.status === 404 ? 'Este informe no tiene historial.' : errorMessage(e));
      },
    );
    return () => controller.abort();
  }, [report.kind, report.fileName, version]);

  const restore = async (versionId: string) => {
    setRestoring(versionId);
    setNotice(null);
    try {
      await api.restoreReportVersion(report, versionId);
      onPreview(null);
      onRestored();
    } catch (e) {
      setNotice({ tone: e instanceof ApiError && e.status === 409 ? 'warning' : 'error', text: errorMessage(e) });
    } finally {
      setRestoring(null);
    }
  };

  return (
    <section className="versions" aria-label="Versiones del informe">
      <h3 className="versions__title">Versiones</h3>
      {error && <p className="muted">{error}</p>}
      {!error && versions === null && <p className="muted">Cargando…</p>}
      {versions?.length === 0 && <p className="muted">Aún no hay versiones: el historial empieza con el primer cambio del chat.</p>}
      {notice && (
        <StatusBanner tone={notice.tone} onDismiss={() => setNotice(null)}>
          {notice.text}
        </StatusBanner>
      )}
      {versions && versions.length > 0 && (
        <ol className="versions__list">
          {versions.map((v) => {
            const previewing = previewId === v.versionId;
            return (
              <li key={v.versionId} className={`version${previewing ? ' version--preview' : ''}${v.isCurrent ? ' version--current' : ''}`}>
                <span className="version__id">{v.versionId}</span>
                <span className="version__what">
                  <strong>{ACTION_TEXT[v.action] ?? v.action}</strong> {originText(v)}
                  {v.isCurrent && <span className="version__badge">actual</span>}
                  {v.note && <span className="version__note">{v.note}</span>}
                </span>
                <span className="version__meta">
                  <time dateTime={v.createdAt}>{formatDateTime(v.createdAt)}</time>
                  <span>{formatBytes(v.sizeBytes)}</span>
                </span>
                <span className="version__actions">
                  {v.action !== 'deleted' && (
                    <button
                      type="button"
                      className="btn btn--small btn--ghost"
                      aria-pressed={previewing}
                      onClick={() => onPreview(previewing ? null : v.versionId)}
                    >
                      {previewing ? 'Ocultar' : 'Ver'}
                    </button>
                  )}
                  {v.action !== 'deleted' && (
                    <button
                      type="button"
                      className="btn btn--small"
                      disabled={v.isCurrent || restoring !== null}
                      title={v.isCurrent ? 'Es el contenido actual' : undefined}
                      onClick={() => void restore(v.versionId)}
                    >
                      {restoring === v.versionId ? 'Restaurando…' : 'Restaurar'}
                    </button>
                  )}
                </span>
              </li>
            );
          })}
        </ol>
      )}
    </section>
  );
}
