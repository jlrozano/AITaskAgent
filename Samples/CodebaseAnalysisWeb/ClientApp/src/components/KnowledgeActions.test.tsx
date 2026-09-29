import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { KnowledgeState, SourceRefDto } from '../api/types';
import { absentKnowledge, enabledFeatures, jsonResponse, meta, mockFetch, withFeatures } from '../test/fixtures';
import { renderWithWorkspace, workspace } from '../test/renderWithWorkspace';
import { KnowledgeActions } from './KnowledgeActions';

const source: SourceRefDto = { kind: 'document', id: '20260927-111203-a7qk' };

const present: KnowledgeState = {
  graph: { state: 'present', nodeCount: 19, edgeCount: 27, addedAt: '2026-09-27T12:00:00Z' },
  embeddings: { state: 'outdated', chunkCount: 24, indexedAt: '2026-09-27T12:00:00Z', model: 'text-embedding-3-small' },
};

function Harness({ initial = absentKnowledge, analysisRunning = false, chatBusy = false, src = source }: {
  initial?: KnowledgeState;
  analysisRunning?: boolean;
  chatBusy?: boolean;
  src?: SourceRefDto;
}) {
  const [k, setK] = useState(initial);
  return (
    <KnowledgeActions
      source={src}
      knowledge={k}
      onChange={(s) => setK({ graph: s.graph, embeddings: s.embeddings })}
      analysisRunning={analysisRunning}
      chatBusy={chatBusy}
    />
  );
}

const group = (name: 'Grafo' | 'Embeddings') => screen.getByText(name).closest('.knowledge__group') as HTMLElement;

afterEach(() => vi.unstubAllGlobals());

describe('KnowledgeActions (V2-D)', () => {
  it('«Añadir al grafo» manda la fuente y pinta el estado nuevo; «Quitar» pide confirmación y usa DELETE por kind/id', async () => {
    const api = mockFetch({
      'POST /api/knowledge/graph': () => jsonResponse({ source, ...present }),
      [`DELETE /api/knowledge/graph/document/${source.id}`]: () => jsonResponse({ source, ...absentKnowledge, embeddings: present.embeddings }),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />, { meta: withFeatures(enabledFeatures) });

    expect(within(group('Grafo')).getByText('No está en el grafo')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Añadir al grafo' }));
    expect(await within(group('Grafo')).findByText('En el grafo · 19 nodos · 27 aristas')).toBeInTheDocument();
    expect(api.calls[0].body).toEqual({ source });
    // Embeddings desactualizados: «Actualizar» y «Quitar».
    expect(within(group('Embeddings')).getByText(/Desactualizado/)).toBeInTheDocument();
    expect(within(group('Embeddings')).getByRole('button', { name: 'Actualizar' })).toBeEnabled();

    await user.click(screen.getByRole('button', { name: 'Quitar del grafo' }));
    expect(api.calls).toHaveLength(1);
    await user.click(within(group('Grafo')).getByRole('button', { name: 'Sí, quitar' }));
    expect(await within(group('Grafo')).findByText('No está en el grafo')).toBeInTheDocument();
    expect(api.calls[1].method).toBe('DELETE');
  });

  it('sin grafo, con análisis o con turno en curso, el grafo se deshabilita con el motivo', () => {
    const { unmount } = renderWithWorkspace(<Harness />, { workspace: { ...workspace, hasGraph: false } });
    const add = screen.getByRole('button', { name: 'Añadir al grafo' });
    expect(add).toBeDisabled();
    expect(add).toHaveAttribute('title', 'Este repositorio aún no tiene grafo: lanza un análisis antes.');
    unmount();

    const second = renderWithWorkspace(<Harness analysisRunning />);
    expect(screen.getByRole('button', { name: 'Añadir al grafo' })).toHaveAttribute('title', 'Hay un análisis en curso.');
    second.unmount();

    renderWithWorkspace(<Harness chatBusy />);
    expect(screen.getByRole('button', { name: 'Añadir al grafo' })).toHaveAttribute('title', 'Hay una respuesta del chat en curso.');
  });

  it('embeddings desactivados: botón deshabilitado con features.embeddings.disabledReason', () => {
    renderWithWorkspace(<Harness />);
    const add = screen.getByRole('button', { name: 'Añadir a embeddings' });
    expect(add).toBeDisabled();
    expect(add).toHaveAttribute('title', meta.features.embeddings.disabledReason);
    expect(within(group('Embeddings')).getByText(meta.features.embeddings.disabledReason as string)).toBeInTheDocument();
  });

  it('un informe del catálogo no se indexa con un análisis en curso; un documento sí', () => {
    const { unmount } = renderWithWorkspace(<Harness analysisRunning src={{ kind: 'report', id: '05_errores_y_bugs.md' }} />, {
      meta: withFeatures(enabledFeatures),
    });
    expect(screen.getByRole('button', { name: 'Añadir a embeddings' })).toBeDisabled();
    unmount();
    renderWithWorkspace(<Harness analysisRunning />, { meta: withFeatures(enabledFeatures) });
    expect(screen.getByRole('button', { name: 'Añadir a embeddings' })).toBeEnabled();
  });

  it('un 409 que llega igual se muestra como aviso, no como error; un 502 sí es error', async () => {
    let calls = 0;
    const api = mockFetch({
      'POST /api/knowledge/embeddings': () =>
        ++calls === 1
          ? jsonResponse({ error: 'knowledge_operation_running', message: 'busy' }, 409)
          : jsonResponse({ error: 'embedding_provider_error', message: 'HTTP 500 del proveedor' }, 502),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />, { meta: withFeatures(enabledFeatures) });

    await user.click(screen.getByRole('button', { name: 'Añadir a embeddings' }));
    const warning = await screen.findByText(/Hay una operación de grafo, embeddings o documentos en curso/);
    expect(warning.closest('.banner')).toHaveClass('banner--warning');
    expect(warning.closest('.banner')).toHaveAttribute('role', 'status');

    await user.click(screen.getByRole('button', { name: 'Añadir a embeddings' }));
    const error = await screen.findByText(/El servicio de embeddings ha fallado.*\(HTTP 500 del proveedor\)/);
    expect(error.closest('.banner')).toHaveClass('banner--error');
    expect(within(group('Embeddings')).getByText('No indexado')).toBeInTheDocument();
  });
});
