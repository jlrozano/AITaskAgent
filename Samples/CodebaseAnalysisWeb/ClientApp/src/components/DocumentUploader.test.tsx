import { act, fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DocumentSummaryDto } from '../api/types';
import { useUploadQueue } from '../hooks/useUploadQueue';
import { FakeXhr } from '../test/fakeXhr';
import { documentOf, meta } from '../test/fixtures';
import { renderWithWorkspace } from '../test/renderWithWorkspace';
import { DocumentUploader } from './DocumentUploader';

function Harness({ onUploaded = () => undefined, onOpen = () => undefined }: { onUploaded?: (d: DocumentSummaryDto) => void; onOpen?: (id: string) => void }) {
  const queue = useUploadQueue(meta.documents, onUploaded);
  return <DocumentUploader limits={meta.documents} queue={queue} onOpen={onOpen} />;
}

const file = (name: string, size = 10, type = 'application/octet-stream') => {
  const f = new File(['x'.repeat(Math.min(size, 64))], name, { type });
  Object.defineProperty(f, 'size', { value: size });
  return f;
};

const input = () => screen.getByLabelText('Elegir ficheros para subir') as HTMLInputElement;
const rows = () => within(screen.getByRole('list', { name: 'Subidas' })).getAllByRole('listitem');

beforeEach(() => {
  FakeXhr.reset();
  vi.stubGlobal('XMLHttpRequest', FakeXhr);
});
afterEach(() => vi.unstubAllGlobals());

describe('DocumentUploader (V2-C)', () => {
  it('el selector acepta las extensiones de /api/meta', () => {
    renderWithWorkspace(<Harness />);
    const accept = input().getAttribute('accept') ?? '';
    expect(accept.split(',')).toEqual(expect.arrayContaining(['.pdf', '.md', '.cs', '.png', '.svg', '.docx']));
  });

  it('rechaza en local por extensión, formato Office antiguo, vacío y tamaño, sin enviar nada', async () => {
    const user = userEvent.setup({ applyAccept: false });
    renderWithWorkspace(<Harness />);
    await user.upload(input(), [file('setup.exe'), file('acta.doc'), file('vacio.md', 0), file('foto.png', 11 * 1024 * 1024)]);
    const texts = rows().map((r) => r.textContent);
    expect(texts[0]).toContain('No se admiten ejecutables ni comprimidos (.exe).');
    expect(texts[1]).toContain('guárdalo como .docx');
    expect(texts[2]).toContain('El fichero está vacío.');
    expect(texts[3]).toMatch(/Supera el máximo para \.png \(11,0 MB de 10,0 MB\)/);
    expect(FakeXhr.all).toHaveLength(0);
  });

  it('sube uno a uno con barra de progreso y avisa al terminar cada uno', async () => {
    const uploaded = vi.fn();
    const user = userEvent.setup();
    renderWithWorkspace(<Harness onUploaded={uploaded} />);
    await user.upload(input(), [file('spec.pdf', 1000), file('notas.md', 200)]);

    // Solo el primero está en vuelo; el segundo espera en cola.
    expect(FakeXhr.all).toHaveLength(1);
    expect(FakeXhr.last?.headers['X-File-Name']).toBe('spec.pdf');
    expect(rows()[1]).toHaveTextContent('En cola');

    act(() => FakeXhr.last?.progress(500, 1000));
    const bar = screen.getByRole('progressbar', { name: 'Subiendo spec.pdf' });
    expect(bar).toHaveAttribute('aria-valuenow', '50');
    expect(rows()[0]).toHaveTextContent('50 %');

    const pdf = documentOf('pdf');
    await act(async () => FakeXhr.last?.respond(201, pdf));
    expect(uploaded).toHaveBeenCalledWith(pdf);
    expect(rows()[0]).toHaveTextContent('Subido');

    expect(FakeXhr.all).toHaveLength(2);
    expect(FakeXhr.last?.headers['X-File-Name']).toBe('notas.md');
    await act(async () => FakeXhr.last?.respond(201, documentOf('markdown')));
    expect(rows()[1]).toHaveTextContent('Subido');
    expect(uploaded).toHaveBeenCalledTimes(2);
  });

  it('errores por fichero: 413 file_too_large, content_mismatch con el texto del servidor y duplicado con «abrir»', async () => {
    const onOpen = vi.fn();
    const user = userEvent.setup();
    renderWithWorkspace(<Harness onOpen={onOpen} />);
    await user.upload(input(), [file('enorme.pdf', 1000), file('falso.pdf', 1000), file('otra-vez.pdf', 1000)]);

    await act(async () =>
      FakeXhr.last?.respond(413, {
        error: 'file_too_large',
        message: 'El fichero supera el máximo de 25 MiB que admite el servidor.',
      }),
    );
    await act(async () =>
      FakeXhr.last?.respond(400, { error: 'content_mismatch', message: 'Se declaró .pdf pero el contenido es un ejecutable (MZ).' }),
    );
    await act(async () =>
      FakeXhr.last?.respond(409, { error: 'duplicate_document', message: 'Ya estaba subido.', documentId: '20260101-000000-aaaa' }),
    );

    const [big, mismatch, dup] = rows();
    expect(big).toHaveTextContent('El fichero supera el máximo de 25 MiB que admite el servidor.');
    expect(mismatch).toHaveTextContent('Se declaró .pdf pero el contenido es un ejecutable (MZ).');
    expect(dup).toHaveTextContent('Ya estaba subido');
    await user.click(within(dup).getByRole('button', { name: 'abrir' }));
    expect(onOpen).toHaveBeenCalledWith('20260101-000000-aaaa');
  });

  it('acepta ficheros arrastrados a la zona y permite cancelar el que está subiendo', async () => {
    const user = userEvent.setup();
    renderWithWorkspace(<Harness />);
    fireEvent.drop(screen.getByTestId('dropzone'), { dataTransfer: { files: [file('diagrama.svg', 100)], types: ['Files'] } });
    expect(FakeXhr.all).toHaveLength(1);
    await user.click(screen.getByRole('button', { name: 'Cancelar diagrama.svg' }));
    expect(rows()[0]).toHaveTextContent('Cancelado');
    await user.click(screen.getByRole('button', { name: 'Limpiar la lista' }));
    expect(screen.queryByRole('list', { name: 'Subidas' })).not.toBeInTheDocument();
  });
});
