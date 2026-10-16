import { useId, useState, type FormEvent } from 'react';
import { useReadRoots } from '../hooks/useReadRoots';
import { useWorkspace } from '../state/workspace';

/**
 * Rutas de solo lectura, además del repositorio, que el asistente puede recorrer con sus herramientas de ficheros. Solo se
 * guarda la ruta: no se indexa, extrae ni copia nada y nunca se escribe en ella, así que añadir es instantáneo.
 */
export function ChatReadRoots() {
  const { workspace } = useWorkspace();
  const { roots, busy, error, add, remove } = useReadRoots(!!workspace?.repositoryPath);
  const [value, setValue] = useState('');
  const inputId = useId();
  const errorId = useId();

  async function submit(e: FormEvent) {
    e.preventDefault();
    const path = value.trim();
    if (!path || busy) return;
    if (await add(path)) setValue('');
  }

  return (
    <section className="readroots" role="group" aria-label="Rutas de lectura adicionales">
      <h3 className="readroots__title" title="El asistente puede leer (no escribir) estas rutas con sus herramientas de ficheros, además del repositorio.">
        Rutas de lectura adicionales
      </h3>
      {roots.length > 0 && (
        <ul className="readroots__list">
          {roots.map((r) => (
            <li key={r.path} className="readroots__item">
              <code className="readroots__path" title={r.path}>
                {r.path}
              </code>
              {r.global && <span className="mark">configuración</span>}
              {r.problem && <span className="text-warning">{r.problem}</span>}
              {!r.global && (
                <button
                  type="button"
                  className="btn btn--ghost btn--small"
                  aria-label={`Quitar ${r.path}`}
                  disabled={busy}
                  onClick={() => void remove(r.path)}
                >
                  Quitar
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      <form className="readroots__form" onSubmit={(e) => void submit(e)}>
        <label htmlFor={inputId} className="visually-hidden">
          Ruta a añadir
        </label>
        <input
          id={inputId}
          type="text"
          value={value}
          placeholder="Ruta absoluta, por ejemplo D:\documentos"
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          onChange={(e) => setValue(e.target.value)}
        />
        <button type="submit" className="btn btn--small" disabled={busy || value.trim() === ''}>
          Añadir
        </button>
      </form>
      {error && (
        <p id={errorId} role="alert" className="text-warning readroots__error">
          {error}
        </p>
      )}
    </section>
  );
}
