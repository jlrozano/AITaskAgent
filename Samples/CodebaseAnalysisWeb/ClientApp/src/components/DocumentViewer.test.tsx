import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DocumentDetailDto } from '../api/types';
import { documentOf, enabledFeatures, jsonResponse, meta, mockFetch, withFeatures } from '../test/fixtures';
import { renderWithWorkspace } from '../test/renderWithWorkspace';
import { DocumentViewer } from './DocumentViewer';

vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn(async () => ({ svg: '<svg></svg>' })) } }));

const md = (text: string) => new Response(text, { headers: { 'Content-Type': 'text/markdown; charset=utf-8' } });

function serve(doc: DocumentDetailDto, text = '## Página 1\n\nHola mundo.\n', extra: Parameters<typeof mockFetch>[0] = {}) {
  const api = mockFetch({
    [`GET /api/documents/${doc.id}`]: () => jsonResponse(doc),
    [`GET /api/documents/${doc.id}/text`]: () => md(text),
    ...extra,
  });
  vi.stubGlobal('fetch', vi.fn(api.fn));
  return api;
}

const show = (doc: DocumentDetailDto, options: Parameters<typeof renderWithWorkspace>[1] = {}) =>
  renderWithWorkspace(
    <DocumentViewer id={doc.id} onChanged={() => undefined} onDeleted={() => undefined} analysisRunning={false} chatBusy={false} />,
    options,
  );

afterEach(() => vi.unstubAllGlobals());

describe('DocumentViewer (V2-C): un visor por `viewer`', () => {
  it('pdf: iframe sin sandbox sobre /raw, «Abrir en una pestaña nueva» y pestaña de texto extraído', async () => {
    const doc = documentOf('pdf');
    const api = serve(doc);
    const user = userEvent.setup();
    show(doc);
    const frame = (await screen.findByTitle(`PDF: ${doc.title}`)) as HTMLIFrameElement;
    expect(frame.tagName).toBe('IFRAME');
    expect(frame).toHaveAttribute('src', `/api/documents/${doc.id}/raw`);
    expect(frame.hasAttribute('sandbox')).toBe(false);
    const open = screen.getByRole('link', { name: 'Abrir en una pestaña nueva' });
    expect(open).toHaveAttribute('target', '_blank');
    expect(open).toHaveAttribute('rel', 'noopener noreferrer');
    // El texto no se pide hasta abrir su pestaña.
    expect(api.calls.some((c) => c.url.endsWith('/text'))).toBe(false);

    await user.click(screen.getByRole('tab', { name: 'Texto extraído (lo que lee el asistente)' }));
    expect(await screen.findByRole('heading', { name: 'Página 1' })).toBeInTheDocument();
    expect(screen.queryByTitle(`PDF: ${doc.title}`)).not.toBeInTheDocument();
    expect(screen.getByText('12 páginas')).toBeInTheDocument();
  });

  it('html: iframe con sandbox vacío (sin ningún permiso)', async () => {
    const doc = documentOf('html');
    serve(doc);
    show(doc);
    const frame = await screen.findByTitle(`HTML: ${doc.title}`);
    expect(frame.getAttribute('sandbox')).toBe('');
    expect(frame).toHaveAttribute('src', `/api/documents/${doc.id}/raw`);
  });

  it('svg: <img> sobre /raw y sin «Describir»', async () => {
    const doc = documentOf('svg');
    serve(doc);
    show(doc);
    const img = await screen.findByRole('img', { name: doc.title });
    expect(img.tagName).toBe('IMG');
    expect(img).toHaveAttribute('src', `/api/documents/${doc.id}/raw`);
    expect(screen.queryByRole('button', { name: /Describir/ })).not.toBeInTheDocument();
  });

  it('imagen: «Describir» deshabilitado con el motivo de features.imageDescription', async () => {
    const doc = documentOf('image');
    serve(doc);
    show(doc);
    const button = await screen.findByRole('button', { name: 'Describir' });
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute('title', meta.features.imageDescription.disabledReason);
    expect(screen.getByText('Imagen sin describir')).toBeInTheDocument();
  });

  it('imagen: «Describir» habilitado llama a /describe y deja la descripción en «Texto extraído»', async () => {
    const doc = documentOf('image');
    const described: DocumentDetailDto = {
      ...doc,
      extraction: { ...doc.extraction, status: 'ok' },
      description: { model: 'gpt-5.4', generatedAt: '2026-09-27T12:00:00Z' },
    };
    const api = serve(doc, '# Descripción\n\nUna imagen mitad roja y mitad azul.\n', {
      [`POST /api/documents/${doc.id}/describe`]: () => jsonResponse(described),
    });
    const user = userEvent.setup();
    show(doc, { meta: withFeatures(enabledFeatures) });
    await user.click(await screen.findByRole('button', { name: 'Describir' }));
    expect(await screen.findByText('Descripción generada: está en «Texto extraído».')).toBeInTheDocument();
    expect(api.calls.find((c) => c.method === 'POST')?.body).toEqual({});
    await user.click(screen.getByRole('tab', { name: 'Texto extraído (lo que lee el asistente)' }));
    expect(await screen.findByText('Una imagen mitad roja y mitad azul.')).toBeInTheDocument();
    expect(screen.getByText(/Descripción generada por gpt-5\.4/)).toBeInTheDocument();
  });

  it('markdown (código): el bloque cercado de /text sale resaltado', async () => {
    const doc = documentOf('code');
    serve(doc, '```csharp\npublic sealed class Pedido { }\n```\n');
    const { container } = show(doc);
    await waitFor(() => expect(container.querySelector('code.hljs.language-csharp')).not.toBeNull());
    expect(screen.queryByRole('tab')).not.toBeInTheDocument();
  });

  it('text: preformateado, sin interpretar Markdown', async () => {
    const doc = documentOf('text');
    serve(doc, '# no es un título\nlínea 2\n');
    show(doc);
    const pre = await screen.findByLabelText(doc.title, { selector: 'pre' });
    expect(pre).toHaveTextContent('# no es un título');
    expect(screen.queryByRole('heading', { name: 'no es un título' })).not.toBeInTheDocument();
  });

  it('extracción fallida: aviso con el motivo; eliminar pide confirmación y un 409 se avisa sin romper', async () => {
    const doc = documentOf('pdf', {
      extraction: { status: 'failed', charCount: 0, sectionCount: 0, pageCount: null, truncated: false, warnings: ['El PDF está cifrado.'] },
    });
    serve(doc, '', {
      [`DELETE /api/documents/${doc.id}`]: () =>
        jsonResponse({ error: 'analysis_running', message: 'Hay un análisis en curso: no se puede quitar del grafo ahora.' }, 409),
    });
    const user = userEvent.setup();
    show(doc);
    expect(await screen.findByText('El PDF está cifrado.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Eliminar' }));
    await user.click(screen.getByRole('button', { name: 'Sí, eliminar' }));
    const notice = await screen.findByText('Hay un análisis en curso: no se puede quitar del grafo ahora.');
    expect(notice.closest('.banner')).toHaveClass('banner--warning');
  });

  it('renombrar y «Usar en el análisis» mandan PATCH con solo el campo cambiado', async () => {
    const doc = documentOf('markdown');
    let current: DocumentDetailDto = doc;
    const api = serve(doc, '# Hola\n', {
      [`PATCH /api/documents/${doc.id}`]: (call) => jsonResponse((current = { ...current, ...(call.body as object) })),
    });
    const onChanged = vi.fn();
    const user = userEvent.setup();
    renderWithWorkspace(<DocumentViewer id={doc.id} onChanged={onChanged} onDeleted={() => undefined} analysisRunning={false} chatBusy={false} />);
    await user.click(await screen.findByRole('button', { name: 'Renombrar' }));
    const box = screen.getByRole('textbox', { name: 'Título del documento' });
    await user.clear(box);
    await user.type(box, 'Especificación API{Enter}');
    expect(await screen.findByRole('heading', { level: 2, name: 'Especificación API' })).toBeInTheDocument();
    await user.click(screen.getByRole('checkbox', { name: 'Usar en el análisis' }));
    await waitFor(() => expect(api.calls.filter((c) => c.method === 'PATCH')).toHaveLength(2));
    expect(api.calls.filter((c) => c.method === 'PATCH').map((c) => c.body)).toEqual([{ title: 'Especificación API' }, { useInAnalysis: false }]);
    expect(onChanged).toHaveBeenLastCalledWith(expect.objectContaining({ title: 'Especificación API', useInAnalysis: false }));
  });
});
