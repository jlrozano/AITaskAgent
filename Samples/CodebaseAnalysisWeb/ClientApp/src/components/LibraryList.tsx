import type { ReactNode } from 'react';
import type { DocumentSummaryDto, ReportSummaryDto } from '../api/types';
import { formatBytes, formatDateTime } from '../format';
import { extractionText, FAMILY_LABEL } from './DocumentViewer';
import { HealthBadge } from './HealthBadge';
import { KnowledgeMini } from './KnowledgeActions';

/** Id de la selección en el hash: `05_x.md`, `custom/x.md` o `doc/<id>`. */
export const docSelection = (id: string) => `doc/${id}`;

interface Props {
  catalog: ReportSummaryDto[] | null;
  custom: ReportSummaryDto[] | null;
  documents: DocumentSummaryDto[] | null;
  selected: string | null;
  onSelect: (id: string) => void;
  /** Zona de subida, encima de la lista de documentos. */
  uploader?: ReactNode;
  /** Errores de carga por sección. */
  errors?: { catalog?: string | null; custom?: string | null; documents?: string | null };
}

function Section({ title, count, children, label }: { title: string; count: number | null; children: ReactNode; label: string }) {
  return (
    <section className="library__section" aria-label={label}>
      <h2 className="panel-title library__title">
        {title}
        {count !== null && <span className="library__count">{count}</span>}
      </h2>
      {children}
    </section>
  );
}

/**
 * Barra lateral de Informes (V2-B/C/D, plan v2 §9): «Informes del análisis» (00-09), «Informes propios» y
 * «Documentos aportados», cada uno con su recuento, las marcas del chat y el miniestado de grafo y embeddings.
 */
export function LibraryList({ catalog, custom, documents, selected, onSelect, uploader, errors = {} }: Props) {
  const sortedCatalog = catalog ? [...catalog].sort((a, b) => a.fileName.localeCompare(b.fileName)) : null;
  const sortedCustom = custom ? [...custom].sort((a, b) => a.title.localeCompare(b.title, 'es')) : null;
  const sortedDocs = documents ? [...documents].sort((a, b) => (a.uploadedAt < b.uploadedAt ? 1 : -1)) : null;

  return (
    <div className="library">
      <Section title="Informes del análisis" count={catalog?.length ?? null} label="Informes del análisis">
        {errors.catalog && <p className="text-error library__error">{errors.catalog}</p>}
        {!errors.catalog && catalog === null && <p className="muted">Cargando…</p>}
        {catalog?.length === 0 && <p className="muted">Todavía no hay informes. Lanza un análisis desde la pestaña Análisis.</p>}
        {sortedCatalog && sortedCatalog.length > 0 && (
          <nav className="report-list" aria-label="Informes">
            <ul>
              {sortedCatalog.map((r) => {
                const active = r.id === selected;
                return (
                  <li key={r.id}>
                    <button
                      type="button"
                      className={`report-item${active ? ' report-item--active' : ''}${r.reportType === null ? ' report-item--executive' : ''}`}
                      aria-current={active ? 'page' : undefined}
                      onClick={() => onSelect(r.id)}
                    >
                      <span className="report-item__num">{r.fileName.slice(0, 2)}</span>
                      <span className="report-item__title">{r.title}</span>
                      <HealthBadge health={r.health} compact />
                      <span className="report-item__meta">
                        {r.criticalFindings !== null && (
                          <span className={r.criticalFindings > 0 ? 'text-error' : undefined}>
                            {r.criticalFindings === 1 ? '1 crítico' : `${r.criticalFindings} críticos`}
                          </span>
                        )}
                        <span>{formatBytes(r.sizeBytes)}</span>
                        <time dateTime={r.lastModified}>{formatDateTime(r.lastModified)}</time>
                        {r.modifiedByChat && <span className="mark mark--chat">editado en el chat</span>}
                        <KnowledgeMini knowledge={r.knowledge} />
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </nav>
        )}
      </Section>

      <Section title="Informes propios" count={custom?.length ?? null} label="Informes propios">
        {errors.custom && <p className="text-error library__error">{errors.custom}</p>}
        {!errors.custom && custom === null && <p className="muted">Cargando…</p>}
        {custom?.length === 0 && <p className="muted">Pide al chat que guarde un informe o un estudio y aparecerá aquí.</p>}
        {sortedCustom && sortedCustom.length > 0 && (
          <nav className="report-list" aria-label="Informes propios">
            <ul>
              {sortedCustom.map((r) => {
                const active = r.id === selected;
                return (
                  <li key={r.id}>
                    <button
                      type="button"
                      className={`report-item report-item--custom${active ? ' report-item--active' : ''}`}
                      aria-current={active ? 'page' : undefined}
                      onClick={() => onSelect(r.id)}
                    >
                      <span className="report-item__num" aria-hidden="true">
                        ✎
                      </span>
                      <span className="report-item__title">{r.title}</span>
                      {r.health !== 'Unknown' ? <HealthBadge health={r.health} compact /> : <span />}
                      <span className="report-item__meta">
                        <span>{formatBytes(r.sizeBytes)}</span>
                        <time dateTime={r.lastModified}>{formatDateTime(r.lastModified)}</time>
                        {r.origin === 'chat' && <span className="mark mark--chat">creado en el chat</span>}
                        <KnowledgeMini knowledge={r.knowledge} />
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </nav>
        )}
      </Section>

      <Section title="Documentos aportados" count={documents?.length ?? null} label="Documentos aportados">
        {uploader}
        {errors.documents && <p className="text-error library__error">{errors.documents}</p>}
        {!errors.documents && documents === null && <p className="muted">Cargando…</p>}
        {documents?.length === 0 && <p className="muted">Aún no hay documentos. Sube especificaciones, actas o diagramas para que el asistente y los auditores los tengan en cuenta.</p>}
        {sortedDocs && sortedDocs.length > 0 && (
          <nav className="report-list" aria-label="Documentos aportados">
            <ul>
              {sortedDocs.map((d) => {
                const id = docSelection(d.id);
                const active = id === selected;
                const bad = d.extraction.status === 'failed' || d.extraction.status === 'empty';
                return (
                  <li key={d.id}>
                    <button
                      type="button"
                      className={`report-item report-item--doc${active ? ' report-item--active' : ''}`}
                      aria-current={active ? 'page' : undefined}
                      onClick={() => onSelect(id)}
                    >
                      <span className={`doc-badge doc-badge--small doc-badge--${d.family}`} aria-hidden="true">
                        {FAMILY_LABEL[d.family] ?? d.family}
                      </span>
                      <span className="report-item__title">{d.title}</span>
                      <span />
                      <span className="report-item__meta">
                        <span>{formatBytes(d.sizeBytes)}</span>
                        <span className={bad ? 'text-warning' : d.extraction.status === 'pending_description' ? 'muted' : undefined}>
                          {extractionText(d)}
                        </span>
                        {!d.useInAnalysis && <span className="mark">fuera del análisis</span>}
                        <KnowledgeMini knowledge={d.knowledge} />
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </nav>
        )}
      </Section>
    </div>
  );
}
