import { useCallback, useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { AnalysisTab } from './components/AnalysisTab';
import { AssistantTab } from './components/AssistantTab';
import { ChatPane } from './components/ChatPane';
import { WorkspaceBar } from './components/WorkspaceBar';
import { useAnalysisRun } from './hooks/useAnalysisRun';
import { useChatTurn } from './hooks/useChatTurn';
import { useHashTab, type TabId } from './hooks/useHashTab';
import { useWorkspace } from './state/workspace';

const TABS: { id: TabId; label: string }[] = [
  { id: 'analisis', label: 'Análisis' },
  { id: 'asistente', label: 'Asistente' },
];

export function App() {
  const { workspace, refresh } = useWorkspace();
  const { route, assistant, navigate, resetAssistant } = useHashTab();
  const run = useAnalysisRun(() => void refresh());
  const analysisRunning = run.state.status === 'running' || !!workspace?.analysisRunning;
  const repo = workspace?.repositoryPath ?? null;

  // V2-B: sube con cada `report.changed` del chat y con «Deshacer»/«Borrar» de sus chips; la biblioteca se recarga.
  const [libraryVersion, setLibraryVersion] = useState(0);
  const bumpLibrary = useCallback(() => setLibraryVersion((n) => n + 1), []);

  // La conversación sigue abierta aunque se pase a Análisis: el hook recuerda lo último de Asistente.
  const chat = useChatTurn({
    enabled: !!repo,
    activeId: assistant.chat,
    onActiveChange: (id) => navigate('asistente', { chat: id }),
    onReportChanged: bumpLibrary,
  });

  // Al cambiar de repositorio, las conversaciones y los ficheros son otros.
  const { loadSessions } = chat;
  const previousRepo = useRef(repo);
  useEffect(() => {
    if (previousRepo.current === repo) return;
    const wasOpen = previousRepo.current !== null;
    previousRepo.current = repo;
    // La primera carga del workspace no es un cambio: se respeta lo que traiga el hash.
    if (!wasOpen) return;
    resetAssistant();
    if (repo) void loadSessions();
  }, [repo, loadSessions, resetAssistant]);

  const tabRefs = useRef<Record<TabId, HTMLButtonElement | null>>({ analisis: null, asistente: null });
  const goTab = useCallback((tab: TabId) => navigate(tab), [navigate]);

  const onTabKey = (e: KeyboardEvent<HTMLButtonElement>, index: number) => {
    let next = -1;
    if (e.key === 'ArrowRight') next = (index + 1) % TABS.length;
    else if (e.key === 'ArrowLeft') next = (index - 1 + TABS.length) % TABS.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = TABS.length - 1;
    if (next < 0) return;
    e.preventDefault();
    goTab(TABS[next].id);
    tabRefs.current[TABS[next].id]?.focus();
  };

  return (
    <div className="app">
      <a className="skip-link" href="#panel" onClick={(e) => { e.preventDefault(); document.getElementById(`panel-${route.tab}`)?.focus(); }}>
        Saltar al contenido
      </a>
      <WorkspaceBar analysisRunning={analysisRunning} />
      <div className="tabs" role="tablist" aria-label="Secciones">
        {TABS.map((t, i) => {
          const selected = route.tab === t.id;
          return (
            <button
              key={t.id}
              ref={(el) => {
                tabRefs.current[t.id] = el;
              }}
              id={`tab-${t.id}`}
              role="tab"
              type="button"
              className={`tab${selected ? ' tab--on' : ''}`}
              aria-selected={selected}
              aria-controls={`panel-${t.id}`}
              tabIndex={selected ? 0 : -1}
              onClick={() => goTab(t.id)}
              onKeyDown={(e) => onTabKey(e, i)}
            >
              {t.label}
              {t.id === 'analisis' && analysisRunning && <span className="tab__pulse" aria-label="(en curso)" />}
              {t.id === 'asistente' && chat.busy && <span className="tab__pulse" aria-label="(respondiendo)" />}
            </button>
          );
        })}
      </div>

      <main className="panels">
        <div role="tabpanel" id="panel-analisis" aria-labelledby="tab-analisis" hidden={route.tab !== 'analisis'} tabIndex={-1} className="panel">
          <AnalysisTab
            run={run}
            onOpenReports={() => navigate('asistente')}
            chatBusy={chat.busy}
          />
        </div>
        <div role="tabpanel" id="panel-asistente" aria-labelledby="tab-asistente" hidden={route.tab !== 'asistente'} tabIndex={-1} className="panel">
          <AssistantTab
            active={route.tab === 'asistente'}
            selected={assistant.file}
            onSelect={(file) => navigate('asistente', { file })}
            reportsVersion={run.state.reportsVersion}
            libraryVersion={libraryVersion}
            onLibraryChanged={bumpLibrary}
            analysisRunning={analysisRunning}
            chatBusy={chat.busy}
            chat={
              <ChatPane
                chat={chat}
                analysisRunning={analysisRunning}
                onOpenReport={(id) => navigate('asistente', { file: id })}
                onReportChanged={bumpLibrary}
              />
            }
          />
        </div>
      </main>
    </div>
  );
}
