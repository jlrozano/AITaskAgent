import { useCallback, useEffect, useRef, useState, type CSSProperties, type DragEvent, type ReactNode, type RefObject } from 'react';
import {
  Group,
  Panel,
  Separator,
  useDefaultLayout,
  usePanelRef,
  type LayoutStorage,
  type PanelImperativeHandle,
} from 'react-resizable-panels';
import { ApiError, api, errorMessage, reportRefFromId, reportSource } from '../api/client';
import { parseSourceSelection } from './claims';
import type { DocumentSummaryDto, KnowledgeStatusDto, ReportSummaryDto, SourceRefDto } from '../api/types';
import { useUploadQueue } from '../hooks/useUploadQueue';
import { useWorkspace } from '../state/workspace';
import { DocumentUploader } from './DocumentUploader';
import { DocumentViewer } from './DocumentViewer';
import { HealthBadge } from './HealthBadge';
import { KnowledgeActions } from './KnowledgeActions';
import { docSelection, LibraryList } from './LibraryList';
import { MarkdownView } from './MarkdownView';
import { ReportVersions } from './ReportVersions';
import { StatusBanner } from './StatusBanner';
import { SourceViewer } from './viewers/SourceViewer';

interface Props {
  /** Fichero del visor (`file` del hash): `<fichero>`, `custom/<fichero>`, `doc/<id>` o `src/<ruta>[:ini-fin]`. */
  selected: string | null;
  onSelect: (id: string | null) => void;
  /** Cambia con cada `report.written` y `run.completed`: se vuelve a pedir el listado. */
  reportsVersion: number;
  /** V2-B: cambia con cada `report.changed` del chat y con «Deshacer»/«Borrar» de sus chips. */
  libraryVersion?: number;
  /** Algo de aquí cambia lo que ve otra pestaña (hoy nada lo necesita; se deja para simetría con el chat). */
  onLibraryChanged?: () => void;
  analysisRunning?: boolean;
  chatBusy?: boolean;
  active: boolean;
  /** Panel central. El chat vive en App para no perder el turno en curso al cambiar de pestaña. */
  chat?: ReactNode;
}

type SectionError = { code: string; message: string } | null;

type PaneId = 'library' | 'chat' | 'viewer';

const PANES: { id: PaneId; label: string }[] = [
  { id: 'library', label: 'Biblioteca' },
  { id: 'chat', label: 'Chat' },
  { id: 'viewer', label: 'Visor' },
];

// Los contenedores con scroll son los hijos (.reports__side, .thread, .reports__main); el panel solo recorta.
const CLIP: CSSProperties = { overflow: 'clip' };

/** Tamaños y plegados de los paneles, por navegador. Sin almacenamiento disponible se arranca con los de serie. */
const layoutStorage: LayoutStorage = {
  getItem: (key) => {
    try {
      return window.localStorage.getItem(key);
    } catch {
      return null;
    }
  },
  setItem: (key, value) => {
    try {
      window.localStorage.setItem(key, value);
    } catch {
      /* sin almacenamiento: el layout no se recuerda */
    }
  },
};

const errorOf = (e: unknown): SectionError => ({ code: e instanceof ApiError ? e.code : 'error', message: errorMessage(e) });

function hasFiles(e: DragEvent): boolean {
  return Array.from(e.dataTransfer?.types ?? []).includes('Files');
}

/**
 * Vista Asistente: biblioteca | chat | visor del fichero elegido, en paneles redimensionables y plegables. El visor
 * enseña solo el fichero seleccionado, sin solapas.
 */
export function AssistantTab({
  selected,
  onSelect,
  reportsVersion,
  libraryVersion = 0,
  analysisRunning = false,
  chatBusy = false,
  active,
  chat,
}: Props) {
  const { workspace, meta } = useWorkspace();
  const repo = workspace?.repositoryPath ?? null;
  const [catalog, setCatalog] = useState<ReportSummaryDto[] | null>(null);
  const [custom, setCustom] = useState<ReportSummaryDto[] | null>(null);
  const [documents, setDocuments] = useState<DocumentSummaryDto[] | null>(null);
  const [errors, setErrors] = useState<{ catalog: SectionError; custom: SectionError; documents: SectionError }>({
    catalog: null,
    custom: null,
    documents: null,
  });
  // Sube tras restaurar una versión, borrar o actualizar embeddings: se vuelve a pedir todo.
  const [localVersion, setLocalVersion] = useState(0);
  const reload = useCallback(() => setLocalVersion((n) => n + 1), []);
  const [dropping, setDropping] = useState(false);
  const dragDepth = useRef(0);

  const refs: Record<PaneId, RefObject<PanelImperativeHandle | null>> = {
    library: usePanelRef(),
    chat: usePanelRef(),
    viewer: usePanelRef(),
  };
  const [collapsed, setCollapsed] = useState<Record<PaneId, boolean>>({ library: false, chat: false, viewer: false });
  const layout = useDefaultLayout({ id: 'cba.assistantLayout', storage: layoutStorage });
  const syncCollapsed = (id: PaneId) => () => {
    const now = refs[id].current?.isCollapsed() ?? false;
    setCollapsed((prev) => (prev[id] === now ? prev : { ...prev, [id]: now }));
  };
  const visibleCount = PANES.filter((p) => !collapsed[p.id]).length;
  const toggle = (id: PaneId) => {
    const panel = refs[id].current;
    if (!panel) return;
    if (panel.isCollapsed()) panel.expand();
    else panel.collapse();
  };

  // Elegir un fichero (en la biblioteca o con «Abrir» en el chat) enseña el visor si estaba plegado. Al montar no:
  // se respeta el plegado que se recuerda aunque el hash traiga fichero.
  const viewerRef = refs.viewer;
  const previousSelected = useRef(selected);
  useEffect(() => {
    if (previousSelected.current === selected) return;
    previousSelected.current = selected;
    if (selected && viewerRef.current?.isCollapsed()) viewerRef.current.expand();
  }, [selected, viewerRef]);

  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    const load = <T,>(call: () => Promise<T>, set: (v: T | null) => void, key: 'catalog' | 'custom' | 'documents') =>
      call().then(
        (v) => {
          if (cancelled) return;
          set(v);
          setErrors((prev) => ({ ...prev, [key]: null }));
        },
        (e) => {
          if (cancelled) return;
          set(null);
          setErrors((prev) => ({ ...prev, [key]: errorOf(e) }));
        },
      );
    void load(api.getReports, setCatalog, 'catalog');
    void load(api.getCustomReports, setCustom, 'custom');
    void load(api.listDocuments, setDocuments, 'documents');
    return () => {
      cancelled = true;
    };
  }, [active, repo, reportsVersion, libraryVersion, localVersion]);

  const queue = useUploadQueue(meta?.documents, (doc) =>
    setDocuments((list) => [doc, ...(list ?? []).filter((d) => d.id !== doc.id)]),
  );

  const patchReport = (id: string, change: Partial<ReportSummaryDto>) => {
    const apply = (list: ReportSummaryDto[] | null) => list?.map((r) => (r.id === id ? { ...r, ...change } : r)) ?? null;
    setCatalog(apply);
    setCustom(apply);
  };

  if (errors.catalog?.code === 'no_workspace') {
    return (
      <div className="empty">
        <h2>No hay repositorio abierto</h2>
        <p>Abre un repositorio en la barra superior para ver sus informes.</p>
      </div>
    );
  }

  const docId = selected?.startsWith('doc/') ? selected.slice('doc/'.length) : null;
  const source = selected && !docId ? parseSourceSelection(selected) : null;
  const reportId = selected && !docId && !source ? selected : null;
  const summary = reportId ? ([...(catalog ?? []), ...(custom ?? [])].find((r) => r.id === reportId) ?? null) : null;

  // Arrastrar ficheros sobre cualquier punto de la vista muestra la capa de suelta (plan v2 §9).
  const dragHandlers = repo
    ? {
        onDragEnter: (e: DragEvent) => {
          if (!hasFiles(e)) return;
          dragDepth.current += 1;
          setDropping(true);
        },
        onDragLeave: (e: DragEvent) => {
          if (!hasFiles(e)) return;
          dragDepth.current = Math.max(0, dragDepth.current - 1);
          if (dragDepth.current === 0) setDropping(false);
        },
        onDragOver: (e: DragEvent) => {
          if (hasFiles(e)) e.preventDefault();
        },
        // La capa se quita en la captura: la zona de la barra para la propagación del `drop` (sube ella) y, sin esto,
        // la capa se quedaba puesta tras soltar allí.
        onDropCapture: () => {
          dragDepth.current = 0;
          setDropping(false);
        },
        onDrop: (e: DragEvent) => {
          if (!hasFiles(e)) return;
          e.preventDefault();
          if (e.dataTransfer.files.length) queue.enqueue(e.dataTransfer.files);
        },
      }
    : {};

  return (
    <div className="assistant" {...dragHandlers}>
      <div className="pane-toggles" role="toolbar" aria-label="Paneles">
        {PANES.map((p) => {
          const shown = !collapsed[p.id];
          return (
            <button
              key={p.id}
              type="button"
              className={`pane-toggle${shown ? ' pane-toggle--on' : ''}`}
              aria-pressed={shown}
              aria-controls={`assistant-${p.id}`}
              // El último panel visible no se pliega: la vista nunca se queda vacía.
              disabled={shown && visibleCount === 1}
              onClick={() => toggle(p.id)}
            >
              {p.label}
            </button>
          );
        })}
      </div>
      <Group
        className="assistant__panes"
        style={CLIP}
        defaultLayout={layout.defaultLayout}
        onLayoutChanged={layout.onLayoutChanged}
      >
        <Panel
          id="assistant-library"
          panelRef={refs.library}
          className="assistant__pane"
          style={CLIP}
          collapsible
          defaultSize="24"
          minSize={200}
          onResize={syncCollapsed('library')}
        >
          <aside className="reports__side">
            <LibraryList
              catalog={catalog}
              custom={custom}
              documents={documents}
              selected={selected}
              onSelect={onSelect}
              errors={{ catalog: errors.catalog?.message, custom: errors.custom?.message, documents: errors.documents?.message }}
              uploader={
                repo ? <DocumentUploader limits={meta?.documents} queue={queue} onOpen={(id) => onSelect(docSelection(id))} /> : null
              }
            />
            <OutdatedEmbeddings catalog={catalog} custom={custom} documents={documents} onDone={reload} />
          </aside>
        </Panel>
        <Separator className="resize-handle" aria-label="Redimensionar biblioteca y chat" />
        <Panel
          id="assistant-chat"
          panelRef={refs.chat}
          className="assistant__pane"
          style={CLIP}
          collapsible
          defaultSize="38"
          minSize={320}
          onResize={syncCollapsed('chat')}
        >
          {chat}
        </Panel>
        <Separator className="resize-handle" aria-label="Redimensionar chat y visor" />
        <Panel
          id="assistant-viewer"
          panelRef={refs.viewer}
          className="assistant__pane"
          style={CLIP}
          collapsible
          defaultSize="38"
          minSize={320}
          onResize={syncCollapsed('viewer')}
        >
          <section className="reports__main" aria-label="Contenido del informe">
            {!selected && (
              <div className="empty">
                <h2>Elige un informe o un documento</h2>
                <p>El resumen ejecutivo (00) enlaza los hallazgos de los demás. Los documentos que subas los lee el asistente.</p>
              </div>
            )}
            {source && <SourceViewer key={source.path} path={source.path} startLine={source.start} endLine={source.end} />}
            {docId && (
              <DocumentViewer
                key={docId}
                id={docId}
                analysisRunning={analysisRunning}
                chatBusy={chatBusy}
                onChanged={(doc) => setDocuments((list) => list?.map((d) => (d.id === doc.id ? { ...d, ...doc } : d)) ?? null)}
                onDeleted={() => {
                  setDocuments((list) => list?.filter((d) => d.id !== docId) ?? null);
                  onSelect(null);
                }}
              />
            )}
            {reportId && (
              <ReportPane
                key={reportId}
                id={reportId}
                summary={summary}
                listVersion={reportsVersion + libraryVersion + localVersion}
                analysisRunning={analysisRunning}
                chatBusy={chatBusy}
                onKnowledge={(status) => patchReport(reportId, { knowledge: { graph: status.graph, embeddings: status.embeddings } })}
                onRestored={reload}
                onDeleted={() => {
                  setCustom((list) => list?.filter((r) => r.id !== reportId) ?? null);
                  onSelect(null);
                }}
              />
            )}
          </section>
        </Panel>
      </Group>
      {dropping && (
        <div className="drop-overlay" aria-hidden="true">
          <p>Suelta los ficheros para subirlos como documentos aportados</p>
        </div>
      )}
    </div>
  );
}

type Content = { text: string } | { error: string; code: string } | null;

function ReportPane({
  id,
  summary,
  listVersion,
  analysisRunning,
  chatBusy,
  onKnowledge,
  onRestored,
  onDeleted,
}: {
  id: string;
  summary: ReportSummaryDto | null;
  listVersion: number;
  analysisRunning: boolean;
  chatBusy: boolean;
  onKnowledge: (status: KnowledgeStatusDto) => void;
  onRestored: () => void;
  onDeleted: () => void;
}) {
  const { workspace } = useWorkspace();
  const repo = workspace?.repositoryPath ?? null;
  const ref = reportRefFromId(id);
  const [content, setContent] = useState<Content>(null);
  // Sube con «Reintentar» tras un 503 report_unavailable (fichero bloqueado por quien lo escribe): vuelve a pedirlo.
  const [attempt, setAttempt] = useState(0);
  const [showVersions, setShowVersions] = useState(false);
  const [previewId, setPreviewId] = useState<string | null>(null);
  const [preview, setPreview] = useState<Content>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [notice, setNotice] = useState<{ tone: 'warning' | 'error'; text: string } | null>(null);
  const lastModified = summary?.lastModified ?? null;

  useEffect(() => {
    const controller = new AbortController();
    api.getReportMarkdown(ref, controller.signal).then(
      (text) => setContent({ text }),
      (e) => {
        if (controller.signal.aborted) return;
        const message = e instanceof ApiError && e.status === 404 ? 'Este informe no existe (todavía) en el repositorio.' : errorMessage(e);
        setContent({ error: message, code: e instanceof ApiError ? e.code : 'error' });
      },
    );
    return () => controller.abort();
    // `ref` se deriva de `id`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, repo, lastModified, attempt, listVersion]);

  useEffect(() => {
    if (!previewId) {
      setPreview(null);
      return;
    }
    const controller = new AbortController();
    setPreview(null);
    api.getReportVersionContent(ref, previewId, controller.signal).then(
      (text) => setPreview({ text }),
      (e) => {
        if (!controller.signal.aborted) setPreview({ error: errorMessage(e), code: e instanceof ApiError ? e.code : 'error' });
      },
    );
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, previewId]);

  const remove = async () => {
    setConfirmDelete(false);
    setNotice(null);
    try {
      await api.deleteCustomReport(ref.fileName);
      onDeleted();
    } catch (e) {
      setNotice({ tone: e instanceof ApiError && e.status === 409 ? 'warning' : 'error', text: errorMessage(e) });
    }
  };

  if (!content) return <p className="muted">Cargando informe…</p>;
  if ('error' in content)
    return content.code === 'report_unavailable' ? (
      <StatusBanner
        tone="warning"
        action={
          <button type="button" className="btn btn--small" onClick={() => setAttempt((n) => n + 1)}>
            Reintentar
          </button>
        }
      >
        {content.error}
      </StatusBanner>
    ) : (
      <StatusBanner tone="error">{content.error}</StatusBanner>
    );

  const shown = previewId ? preview : content;
  const source: SourceRefDto = reportSource(ref);

  return (
    <>
      <header className="report-head">
        <p className="report-head__file">{ref.kind === 'custom' ? `custom/${ref.fileName}` : ref.fileName}</p>
        {summary && (ref.kind === 'catalog' || summary.health !== 'Unknown') && <HealthBadge health={summary.health} />}
        {summary?.modifiedByChat && <span className="mark mark--chat">editado en el chat</span>}
        {summary?.kind === 'custom' && summary.origin === 'chat' && <span className="mark mark--chat">creado en el chat</span>}
        <span className="report-head__actions">
          <button
            type="button"
            className="btn btn--small"
            aria-expanded={showVersions}
            onClick={() => {
              setShowVersions((v) => !v);
              setPreviewId(null);
            }}
          >
            Versiones{summary && summary.versionCount > 0 ? ` (${summary.versionCount})` : ''}
          </button>
          {ref.kind === 'custom' &&
            (confirmDelete ? (
              <>
                <button type="button" className="btn btn--small btn--danger" onClick={() => void remove()}>
                  Sí, eliminar
                </button>
                <button type="button" className="btn btn--small btn--ghost" onClick={() => setConfirmDelete(false)}>
                  No
                </button>
              </>
            ) : (
              <button type="button" className="btn btn--small btn--danger" onClick={() => setConfirmDelete(true)}>
                Eliminar
              </button>
            ))}
        </span>
      </header>
      {notice && (
        <StatusBanner tone={notice.tone} onDismiss={() => setNotice(null)}>
          {notice.text}
        </StatusBanner>
      )}
      {summary && (
        <KnowledgeActions
          source={source}
          knowledge={summary.knowledge}
          onChange={onKnowledge}
          analysisRunning={analysisRunning}
          chatBusy={chatBusy}
        />
      )}
      {showVersions && (
        <ReportVersions
          report={ref}
          previewId={previewId}
          onPreview={setPreviewId}
          onRestored={onRestored}
          version={listVersion + (summary?.versionCount ?? 0)}
        />
      )}
      {previewId && (
        <StatusBanner
          tone="info"
          title={`Vista previa de la versión ${previewId}`}
          action={
            <button type="button" className="btn btn--small" onClick={() => setPreviewId(null)}>
              Volver a la actual
            </button>
          }
        >
          No es el contenido actual del informe. «Restaurar» en la lista la convierte en la actual.
        </StatusBanner>
      )}
      {!shown && <p className="muted">Cargando versión…</p>}
      {shown && 'error' in shown && <StatusBanner tone="error">{shown.error}</StatusBanner>}
      {shown && 'text' in shown && <MarkdownView source={shown.text} toc label={summary?.title ?? ref.fileName} />}
    </>
  );
}

/** «Actualizar desactualizados» (plan v2 §8.2.5): reindexa uno a uno los que tienen embeddings `outdated` o `model_mismatch`. */
function OutdatedEmbeddings({
  catalog,
  custom,
  documents,
  onDone,
}: {
  catalog: ReportSummaryDto[] | null;
  custom: ReportSummaryDto[] | null;
  documents: DocumentSummaryDto[] | null;
  onDone: () => void;
}) {
  const { meta } = useWorkspace();
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [notice, setNotice] = useState<{ tone: 'warning' | 'error'; text: string } | null>(null);
  const stale = (s: string) => s === 'outdated' || s === 'model_mismatch';
  const sources: SourceRefDto[] = [
    ...[...(catalog ?? []), ...(custom ?? [])]
      .filter((r) => stale(r.knowledge?.embeddings.state))
      .map((r) => reportSource(reportRefFromId(r.id))),
    ...(documents ?? []).filter((d) => stale(d.knowledge.embeddings.state)).map((d) => ({ kind: 'document' as const, id: d.id })),
  ];
  if (!meta?.features.embeddings.enabled || (sources.length === 0 && !progress && !notice)) return null;

  const run = async () => {
    setNotice(null);
    let done = 0;
    setProgress({ done, total: sources.length });
    try {
      for (const source of sources) {
        await api.indexEmbeddings(source);
        setProgress({ done: ++done, total: sources.length });
      }
    } catch (e) {
      setNotice({ tone: e instanceof ApiError && e.status === 409 ? 'warning' : 'error', text: errorMessage(e) });
    } finally {
      setProgress(null);
      onDone();
    }
  };

  return (
    <div className="library__outdated">
      {sources.length > 0 && (
        <p className="hint hint--warning">
          {sources.length === 1 ? '1 fuente con embeddings desactualizados.' : `${sources.length} fuentes con embeddings desactualizados.`}{' '}
          <button type="button" className="btn btn--link" disabled={progress !== null} onClick={() => void run()}>
            {progress ? `Actualizando ${progress.done}/${progress.total}…` : 'Actualizar desactualizados'}
          </button>
        </p>
      )}
      {notice && (
        <StatusBanner tone={notice.tone} onDismiss={() => setNotice(null)}>
          {notice.text}
        </StatusBanner>
      )}
    </div>
  );
}
