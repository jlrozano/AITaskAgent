import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReportSummaryDto, ReportVersionDto } from '../api/types';
import { catalogReportFields, customReport, documentOf, jsonResponse, mockFetch } from '../test/fixtures';
import { FakeXhr } from '../test/fakeXhr';
import { renderWithWorkspace } from '../test/renderWithWorkspace';
import { AssistantTab } from './AssistantTab';

vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn(async () => ({ svg: '<svg data-testid="diagram"></svg>' })) } }));

const reports: ReportSummaryDto[] = [
  {
    fileName: '05_errores_y_bugs.md',
    reportType: 'ErroresYBugs',
    title: 'Errores y bugs',
    health: 'Red',
    criticalFindings: 6,
    sizeBytes: 48211,
    lastModified: '2026-09-27T10:40:12Z',
    ...catalogReportFields('05_errores_y_bugs.md'),
  },
  {
    fileName: '00_resumen_ejecutivo.md',
    reportType: null,
    title: 'Resumen ejecutivo',
    health: 'Amber',
    criticalFindings: null,
    sizeBytes: 9000,
    lastModified: '2026-09-27T10:41:00Z',
    ...catalogReportFields('00_resumen_ejecutivo.md'),
  },
];

function Harness({ version = 0, library = 0, initial = null }: { version?: number; library?: number; initial?: string | null }) {
  const [selected, setSelected] = useState<string | null>(initial);
  return <AssistantTab active selected={selected} onSelect={setSelected} reportsVersion={version} libraryVersion={library} />;
}

const markdown = (text: string) => new Response(text, { headers: { 'Content-Type': 'text/markdown; charset=utf-8' } });

const versions: ReportVersionDto[] = [
  { versionId: 'v0002', createdAt: '2026-09-27T11:00:00Z', action: 'updated', origin: { type: 'chat', sessionId: 's1', turnId: 't-0004' },
    note: 'Añadida la sección de colas', sizeBytes: 51230, sha256: 'b', isCurrent: true },
  { versionId: 'v0001', createdAt: '2026-09-27T10:40:00Z', action: 'baseline', origin: { type: 'pipeline', sessionId: null, turnId: null },
    note: null, sizeBytes: 48211, sha256: 'a', isCurrent: false },
];

afterEach(() => vi.unstubAllGlobals());

describe('AssistantTab', () => {
  it('lista los informes ordenados con semáforo y críticos, y abre el Markdown elegido', async () => {
    const api = mockFetch({
      'GET /api/reports': () => jsonResponse(reports),
      'GET /api/reports/05_errores_y_bugs.md': () =>
        new Response('# Errores y bugs\n\n<!-- analysis-meta: health=Red -->\n\n## P0\n\n| A | B |\n|---|---|\n| 1 | 2 |\n', {
          headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
        }),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);

    const nav = await screen.findByRole('navigation', { name: 'Informes' });
    const items = within(nav).getAllByRole('button');
    expect(items.map((b) => b.querySelector('.report-item__title')?.textContent)).toEqual(['Resumen ejecutivo', 'Errores y bugs']);
    expect(within(items[1]).getByText('6 críticos')).toBeInTheDocument();
    expect(within(items[1]).getByText('Rojo')).toBeInTheDocument();

    await user.click(items[1]);
    expect(await screen.findByRole('heading', { level: 1, name: 'Errores y bugs' })).toBeInTheDocument();
    expect(screen.getByRole('table')).toBeInTheDocument();
    expect(document.body.textContent).not.toContain('analysis-meta');
    expect(items[1]).toHaveAttribute('aria-current', 'page');
  });

  it('vuelve a pedir el listado cuando cambia reportsVersion (report.written / run.completed)', async () => {
    const api = mockFetch({ 'GET /api/reports': () => jsonResponse(reports) });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const { rerender } = renderWithWorkspace(<Harness version={0} />);
    await screen.findByRole('navigation', { name: 'Informes' });
    rerender(<Harness version={1} />);
    await waitFor(() => expect(api.calls.filter((c) => c.url === '/api/reports')).toHaveLength(2));
  });

  it('un 409 no_workspace invita a abrir un repositorio', async () => {
    const api = mockFetch({
      'GET /api/reports': () => jsonResponse({ error: 'no_workspace', message: 'No hay workspace' }, 409),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    renderWithWorkspace(<Harness />, { workspace: null });
    expect(await screen.findByText('No hay repositorio abierto')).toBeInTheDocument();
  });

  it('un 503 report_unavailable se avisa como transitorio y «Reintentar» lo vuelve a pedir', async () => {
    let reads = 0;
    const api = mockFetch({
      'GET /api/reports': () => jsonResponse(reports),
      'GET /api/reports/05_errores_y_bugs.md': () =>
        ++reads === 1
          ? jsonResponse({ error: 'report_unavailable', message: "No se puede leer el informe '05_errores_y_bugs.md' ahora: locked" }, 503)
          : new Response('# Errores y bugs\n\nContenido.\n', { headers: { 'Content-Type': 'text/markdown; charset=utf-8' } }),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);
    const nav = await screen.findByRole('navigation', { name: 'Informes' });
    await user.click(within(nav).getAllByRole('button')[1]);

    const text = await screen.findByText(/El informe no se puede leer ahora: puede que lo esté escribiendo el análisis/);
    const banner = text.closest('.banner') as HTMLElement;
    expect(banner).toHaveClass('banner--warning');
    expect(banner).toHaveTextContent("(No se puede leer el informe '05_errores_y_bugs.md' ahora: locked)");
    await user.click(within(banner).getByRole('button', { name: 'Reintentar' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Errores y bugs' })).toBeInTheDocument();
    expect(reads).toBe(2);
    expect(screen.queryByRole('button', { name: 'Reintentar' })).not.toBeInTheDocument();
  });

  it('un informe que no existe (404) se explica', async () => {
    const api = mockFetch({ 'GET /api/reports': () => jsonResponse(reports) });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);
    const nav = await screen.findByRole('navigation', { name: 'Informes' });
    await user.click(within(nav).getAllByRole('button')[0]);
    expect(await screen.findByText('Este informe no existe (todavía) en el repositorio.')).toBeInTheDocument();
  });

  it('V2-B/C: secciones de la biblioteca con recuentos, marcas del chat y documentos; el hash doc/<id> abre el visor', async () => {
    const edited = { ...reports[0], modifiedByChat: true, versionCount: 2 };
    const pdf = documentOf('pdf', { title: 'Especificación API' });
    const api = mockFetch({
      'GET /api/reports': () => jsonResponse([edited, reports[1]]),
      'GET /api/reports/custom': () => jsonResponse([customReport('estudio-de-colas.md', 'Estudio de colas')]),
      'GET /api/documents': () => jsonResponse([pdf]),
      [`GET /api/documents/${pdf.id}`]: () => jsonResponse(pdf),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);

    const catalog = await screen.findByRole('region', { name: 'Informes del análisis' });
    expect(await within(catalog).findByText('editado en el chat')).toBeInTheDocument();
    expect(within(catalog).getByText('2', { selector: '.library__count' })).toBeInTheDocument();
    const custom = screen.getByRole('region', { name: 'Informes propios' });
    expect(await within(custom).findByText('Estudio de colas')).toBeInTheDocument();
    expect(within(custom).getByText('creado en el chat')).toBeInTheDocument();
    const docs = screen.getByRole('region', { name: 'Documentos aportados' });
    expect(within(docs).getByRole('button', { name: 'Elegir ficheros' })).toBeInTheDocument();
    const item = await within(docs).findByRole('button', { name: /Especificación API/ });
    expect(item).toHaveTextContent('Texto extraído');

    await user.click(item);
    expect(await screen.findByTitle('PDF: Especificación API')).toBeInTheDocument();
  });

  it('V2-C: soltar en la zona de la barra quita la capa de suelta de la pestaña y sube el fichero una sola vez', async () => {
    const api = mockFetch({
      'GET /api/reports': () => jsonResponse(reports),
      'GET /api/reports/custom': () => jsonResponse([]),
      'GET /api/documents': () => jsonResponse([]),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    FakeXhr.reset();
    vi.stubGlobal('XMLHttpRequest', FakeXhr);
    const { container } = renderWithWorkspace(<Harness />);
    await screen.findByRole('navigation', { name: 'Informes' });
    const files = [new File(['# Nota'], 'nota.md', { type: 'text/markdown' })];
    const dataTransfer = { files, types: ['Files'] };

    // El arrastre entra por la pestaña (la capa aparece) y se suelta en la zona propia, que para la propagación.
    fireEvent.dragEnter(container.querySelector('.assistant') as Element, { dataTransfer });
    expect(container.querySelector('.drop-overlay')).not.toBeNull();
    fireEvent.drop(screen.getByTestId('dropzone'), { dataTransfer });

    expect(container.querySelector('.drop-overlay')).toBeNull();
    expect(FakeXhr.all).toHaveLength(1);
  });

  it('V2-B: vuelve a pedir la biblioteca cuando cambia libraryVersion (report.changed del chat)', async () => {
    const api = mockFetch({
      'GET /api/reports': () => jsonResponse(reports),
      'GET /api/reports/custom': () => jsonResponse([]),
      'GET /api/documents': () => jsonResponse([]),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const { rerender } = renderWithWorkspace(<Harness library={0} />);
    await screen.findByRole('navigation', { name: 'Informes' });
    rerender(<Harness library={1} />);
    await waitFor(() => expect(api.calls.filter((c) => c.url === '/api/reports/custom')).toHaveLength(2));
  });

  it('V2-B: «Versiones» lista el historial, «Ver» muestra la vista previa y «Restaurar» la convierte en la actual', async () => {
    let content = '# Errores y bugs\n\nVersión nueva.\n';
    const api = mockFetch({
      'GET /api/reports': () => jsonResponse([{ ...reports[0], modifiedByChat: true, versionCount: 2 }]),
      'GET /api/reports/custom': () => jsonResponse([]),
      'GET /api/documents': () => jsonResponse([]),
      'GET /api/reports/05_errores_y_bugs.md': () => markdown(content),
      'GET /api/reports/05_errores_y_bugs.md/versions': () => jsonResponse(versions),
      'GET /api/reports/05_errores_y_bugs.md/versions/v0001': () => markdown('# Errores y bugs\n\nVersión original.\n'),
      'POST /api/reports/05_errores_y_bugs.md/versions/v0001/restore': () => {
        content = '# Errores y bugs\n\nVersión original.\n';
        return jsonResponse(reports[0]);
      },
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness initial="05_errores_y_bugs.md" />);

    expect(await screen.findByText('Versión nueva.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Versiones (2)' }));
    const list = await screen.findByRole('region', { name: 'Versiones del informe' });
    const rows = await within(list).findAllByRole('listitem');
    expect(rows[0]).toHaveTextContent('Modificado por el chat');
    expect(rows[0]).toHaveTextContent('actual');
    expect(within(rows[0]).getByRole('button', { name: 'Restaurar' })).toBeDisabled();
    expect(rows[1]).toHaveTextContent('Original del análisis por el análisis');

    await user.click(within(rows[1]).getByRole('button', { name: 'Ver' }));
    expect(await screen.findByText('Vista previa de la versión v0001')).toBeInTheDocument();
    expect(await screen.findByText('Versión original.')).toBeInTheDocument();

    await user.click(within(rows[1]).getByRole('button', { name: 'Restaurar' }));
    await waitFor(() => expect(screen.queryByText('Vista previa de la versión v0001')).not.toBeInTheDocument());
    expect(await screen.findByText('Versión original.')).toBeInTheDocument();
    expect(api.calls.some((c) => c.method === 'POST' && c.url.endsWith('/v0001/restore'))).toBe(true);
  });

  it('V2-B: un informe propio se abre por custom/<fichero> y se puede eliminar', async () => {
    const api = mockFetch({
      'GET /api/reports': () => jsonResponse(reports),
      'GET /api/reports/custom': () => jsonResponse([customReport('estudio-de-colas.md', 'Estudio de colas')]),
      'GET /api/documents': () => jsonResponse([]),
      'GET /api/reports/custom/estudio-de-colas.md': () => markdown('# Estudio de colas\n\nTexto.\n'),
      'DELETE /api/reports/custom/estudio-de-colas.md': () => new Response(null, { status: 204 }),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    const user = userEvent.setup();
    renderWithWorkspace(<Harness initial="custom/estudio-de-colas.md" />);
    expect(await screen.findByRole('heading', { level: 1, name: 'Estudio de colas' })).toBeInTheDocument();
    expect(screen.getByText('custom/estudio-de-colas.md')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Eliminar' }));
    await user.click(screen.getByRole('button', { name: 'Sí, eliminar' }));
    expect(await screen.findByText('Elige un informe o un documento')).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Informes propios' })).not.toBeInTheDocument();
  });

  it('tres paneles: biblioteca, el chat que le pasa App y el visor, con conmutadores y dos separadores', async () => {
    vi.stubGlobal('fetch', vi.fn(mockFetch({ 'GET /api/reports': () => jsonResponse(reports) }).fn));
    renderWithWorkspace(<AssistantTab active selected={null} onSelect={() => {}} reportsVersion={0} chat={<p>chat aquí</p>} />);

    await screen.findByRole('navigation', { name: 'Informes' });
    expect(screen.getByText('chat aquí')).toBeInTheDocument();
    expect(screen.getByText('Elige un informe o un documento')).toBeInTheDocument();
    expect(screen.getAllByRole('separator')).toHaveLength(2);
    // jsdom no tiene layout (grupo de tamaño 0): plegar y desplegar solo se puede comprobar en un navegador.
    const toolbar = screen.getByRole('toolbar', { name: 'Paneles' });
    for (const name of ['Biblioteca', 'Chat', 'Visor']) {
      expect(within(toolbar).getByRole('button', { name })).toHaveAttribute('aria-pressed', 'true');
    }
  });
});
