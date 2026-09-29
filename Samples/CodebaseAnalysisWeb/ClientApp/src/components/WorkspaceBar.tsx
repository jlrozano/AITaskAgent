import { useEffect, useId, useState, type FormEvent } from 'react';
import { ApiError, errorMessage } from '../api/client';
import { useTheme, type ThemePreference } from '../state/theme';
import { useWorkspace } from '../state/workspace';

const THEMES: { value: ThemePreference; label: string }[] = [
  { value: 'auto', label: 'Auto' },
  { value: 'light', label: 'Claro' },
  { value: 'dark', label: 'Oscuro' },
];

function isAbsolutePath(path: string): boolean {
  return /^[a-zA-Z]:[\\/]/.test(path) || path.startsWith('\\\\') || path.startsWith('/');
}

export function WorkspaceBar({ analysisRunning }: { analysisRunning: boolean }) {
  const { workspace, recents, open, loadError } = useWorkspace();
  const { preference, setPreference } = useTheme();
  const [path, setPath] = useState('');
  const [opening, setOpening] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputId = useId();
  const listId = useId();
  const errorId = useId();

  useEffect(() => {
    if (workspace?.repositoryPath) setPath(workspace.repositoryPath);
  }, [workspace?.repositoryPath]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const value = path.trim().replace(/^"(.*)"$/, '$1');
    if (!value) return;
    if (!isAbsolutePath(value)) {
      setError('Escribe la ruta absoluta del repositorio, por ejemplo C:\\repos\\miapp.');
      return;
    }
    setOpening(true);
    setError(null);
    try {
      await open(value);
    } catch (err) {
      if (err instanceof ApiError && (err.code === 'analysis_running' || err.code === 'chat_turn_running')) {
        setError(
          err.code === 'analysis_running'
            ? 'No se puede cambiar de repositorio mientras corre un análisis. Espera a que termine o cancélalo.'
            : 'No se puede cambiar de repositorio mientras el chat está respondiendo. Para la respuesta y vuelve a intentarlo.',
        );
      } else {
        setError(errorMessage(err));
      }
    } finally {
      setOpening(false);
    }
  };

  const running = analysisRunning || !!workspace?.analysisRunning;
  const current = workspace?.repositoryPath;

  return (
    <header className="topbar">
      <div className="topbar__brand">
        <span className="brand-mark" aria-hidden="true" />
        <h1>Análisis de código</h1>
      </div>

      <form className="topbar__repo" onSubmit={submit} aria-label="Repositorio">
        <label htmlFor={inputId} className="topbar__label">
          Repositorio
        </label>
        <input
          id={inputId}
          className="input input--mono"
          value={path}
          onChange={(e) => setPath(e.target.value)}
          list={listId}
          placeholder="C:\ruta\al\repositorio"
          spellCheck={false}
          autoComplete="off"
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
        />
        <datalist id={listId}>
          {recents.map((r) => (
            <option key={r} value={r} />
          ))}
        </datalist>
        <button type="submit" className="btn" disabled={opening || !path.trim()}>
          {opening ? 'Abriendo…' : current && path.trim() === current ? 'Reabrir' : 'Abrir'}
        </button>
      </form>

      <ul className="topbar__chips" aria-label="Estado del repositorio">
        {current ? (
          <>
            <li className={`chip${workspace?.hasGraph ? ' chip--ok' : ''}`}>{workspace?.hasGraph ? 'Grafo indexado' : 'Sin grafo'}</li>
            <li className="chip">
              {workspace?.reportCount === 1 ? '1 informe' : `${workspace?.reportCount ?? 0} informes`}
            </li>
          </>
        ) : (
          <li className="chip">Sin repositorio</li>
        )}
        {running && <li className="chip chip--running">Análisis en curso</li>}
      </ul>

      <div className="topbar__theme" role="group" aria-label="Tema">
        {THEMES.map((t) => (
          <button
            key={t.value}
            type="button"
            className={`seg${preference === t.value ? ' seg--on' : ''}`}
            aria-pressed={preference === t.value}
            onClick={() => setPreference(t.value)}
          >
            {t.label}
          </button>
        ))}
      </div>

      {(error || loadError) && (
        <p id={errorId} className="topbar__error" role="alert">
          {error ?? loadError}
        </p>
      )}
    </header>
  );
}
