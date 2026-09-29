import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { WorkspaceDto } from '../api/types';
import { WorkspaceProvider } from '../state/workspace';
import { jsonResponse, meta, mockFetch } from '../test/fixtures';
import { WorkspaceBar } from './WorkspaceBar';

const empty: WorkspaceDto = {
  repositoryPath: null,
  databasePath: null,
  reportsPath: null,
  chatPath: null,
  logsPath: null,
  hasGraph: false,
  reportCount: 0,
  analysisRunning: false,
  documentsPath: null,
  documentCount: 0,
  customReportCount: 0,
};

afterEach(() => vi.unstubAllGlobals());

async function openWith(response: () => Response) {
  const api = mockFetch({
    'GET /api/workspace': () => jsonResponse(empty),
    'GET /api/meta': () => jsonResponse(meta),
    'PUT /api/workspace': response,
  });
  vi.stubGlobal('fetch', vi.fn(api.fn));
  const user = userEvent.setup();
  render(
    <WorkspaceProvider>
      <WorkspaceBar analysisRunning={false} />
    </WorkspaceProvider>,
  );
  await user.type(screen.getByLabelText('Repositorio', { selector: 'input' }), 'C:\\repos\\solo-lectura');
  await user.click(screen.getByRole('button', { name: 'Abrir' }));
  return { api, alert: await screen.findByRole('alert') };
}

describe('WorkspaceBar', () => {
  it('un 400 workspace_not_writable explica el permiso que falta, con el detalle, y deja la ruta para corregirla', async () => {
    const detail = "No se puede escribir en 'C:\\repos\\solo-lectura\\.codebase\\chat': Access to the path is denied.";
    const { api, alert } = await openWith(() => jsonResponse({ error: 'workspace_not_writable', message: detail }, 400));
    expect(api.calls.find((c) => c.method === 'PUT')?.body).toEqual({ repositoryPath: 'C:\\repos\\solo-lectura' });
    expect(alert).toHaveTextContent(/No se puede abrir este repositorio: la aplicación necesita escribir en su carpeta \.codebase/);
    expect(alert).toHaveTextContent(`(${detail})`);
    const input = screen.getByLabelText('Repositorio', { selector: 'input' });
    expect(input).toHaveValue('C:\\repos\\solo-lectura');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByText('Sin repositorio')).toBeInTheDocument();
  });

  it('un 409 chat_turn_running pide parar la respuesta antes de cambiar de repositorio', async () => {
    const { alert } = await openWith(() =>
      jsonResponse({ error: 'chat_turn_running', message: 'Hay un turno de chat en curso.' }, 409),
    );
    expect(alert).toHaveTextContent('No se puede cambiar de repositorio mientras el chat está respondiendo.');
  });

  it('un 400 invalid_request se muestra con el texto del cliente y el detalle', async () => {
    const { alert } = await openWith(() => jsonResponse({ error: 'invalid_request', message: 'JSON mal formado.' }, 400));
    expect(alert).toHaveTextContent('El servidor no ha entendido la petición.');
    expect(alert).toHaveTextContent('(JSON mal formado.)');
  });
});
