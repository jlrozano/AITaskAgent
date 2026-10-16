import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ChatReadRootDto } from '../api/types';
import { jsonResponse, mockFetch } from '../test/fixtures';
import { renderWithWorkspace } from '../test/renderWithWorkspace';
import { ChatReadRoots } from './ChatReadRoots';

const docs: ChatReadRootDto = { path: 'D:\\documentos', global: false, problem: null };
const shared: ChatReadRootDto = { path: 'D:\\compartida', global: true, problem: null };

function stub(initial: ChatReadRootDto[]) {
  let roots = initial;
  const api = mockFetch({
    'GET /api/chat/read-roots': () => jsonResponse({ roots }),
    'POST /api/chat/read-roots': (call) => {
      const path = (call.body as { path: string }).path;
      if (!path.startsWith('D:\\')) {
        return jsonResponse({ error: 'invalid_path', message: 'La ruta tiene que ser absoluta (por ejemplo C:\\documentos).' }, 400);
      }
      roots = [...roots, { path, global: false, problem: null }];
      return jsonResponse({ roots }, 201);
    },
    'DELETE /api/chat/read-roots': (call) => {
      const path = new URL(call.url, 'http://x').searchParams.get('path');
      roots = roots.filter((r) => r.path !== path);
      return new Response(null, { status: 204 });
    },
  });
  vi.stubGlobal('fetch', vi.fn(api.fn));
  return api;
}

afterEach(() => vi.unstubAllGlobals());

const group = () => screen.getByRole('group', { name: 'Rutas de lectura adicionales' });

describe('ChatReadRoots', () => {
  it('lista las rutas con «Quitar» solo en las del usuario', async () => {
    stub([docs, shared]);
    renderWithWorkspace(<ChatReadRoots />);

    const items = await within(group()).findAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('D:\\documentos');
    expect(within(items[0]).getByRole('button', { name: 'Quitar D:\\documentos' })).toBeInTheDocument();
    expect(items[1]).toHaveTextContent('D:\\compartida');
    expect(items[1]).toHaveTextContent('configuración');
    expect(within(items[1]).queryByRole('button')).not.toBeInTheDocument();
  });

  it('añadir envía la ruta, la lista y vacía el campo, sin barras de progreso', async () => {
    const api = stub([]);
    const user = userEvent.setup();
    renderWithWorkspace(<ChatReadRoots />);

    await user.type(await screen.findByLabelText('Ruta a añadir'), 'D:\\notas');
    await user.click(screen.getByRole('button', { name: 'Añadir' }));

    expect(await within(group()).findByText('D:\\notas')).toBeInTheDocument();
    expect(api.calls.find((c) => c.method === 'POST')?.body).toEqual({ path: 'D:\\notas' });
    expect(screen.getByLabelText('Ruta a añadir')).toHaveValue('');
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('Intro también añade, y un campo vacío no envía nada', async () => {
    const api = stub([]);
    const user = userEvent.setup();
    renderWithWorkspace(<ChatReadRoots />);
    const field = await screen.findByLabelText('Ruta a añadir');

    expect(screen.getByRole('button', { name: 'Añadir' })).toBeDisabled();
    await user.type(field, 'D:\\notas{Enter}');

    expect(await within(group()).findByText('D:\\notas')).toBeInTheDocument();
    expect(api.calls.filter((c) => c.method === 'POST')).toHaveLength(1);
  });

  it('una ruta rechazada muestra el motivo del servidor, conserva lo escrito y no la lista', async () => {
    stub([]);
    const user = userEvent.setup();
    renderWithWorkspace(<ChatReadRoots />);

    await user.type(await screen.findByLabelText('Ruta a añadir'), 'docs\\relativa');
    await user.click(screen.getByRole('button', { name: 'Añadir' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('La ruta tiene que ser absoluta');
    expect(screen.getByLabelText('Ruta a añadir')).toHaveValue('docs\\relativa');
    expect(within(group()).queryByRole('listitem')).not.toBeInTheDocument();
  });

  it('quitar llama a DELETE con la ruta codificada y la saca de la lista', async () => {
    const api = stub([docs]);
    const user = userEvent.setup();
    renderWithWorkspace(<ChatReadRoots />);

    await user.click(await screen.findByRole('button', { name: 'Quitar D:\\documentos' }));

    await waitFor(() => expect(within(group()).queryByRole('listitem')).not.toBeInTheDocument());
    expect(api.calls.find((c) => c.method === 'DELETE')?.url).toBe('/api/chat/read-roots?path=D%3A%5Cdocumentos');
  });

  it('una ruta que ya no vale se marca con su motivo', async () => {
    stub([{ ...docs, problem: 'La ruta no existe o no es un directorio.' }]);
    renderWithWorkspace(<ChatReadRoots />);

    expect(await screen.findByText('La ruta no existe o no es un directorio.')).toBeInTheDocument();
  });

  it('si la lista no carga, el formulario sigue disponible', async () => {
    vi.stubGlobal('fetch', vi.fn(mockFetch({}).fn));
    renderWithWorkspace(<ChatReadRoots />);

    expect(await screen.findByLabelText('Ruta a añadir')).toBeInTheDocument();
  });
});
