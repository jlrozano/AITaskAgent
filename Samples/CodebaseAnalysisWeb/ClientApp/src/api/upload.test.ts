import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './client';
import { uploadDocument } from './upload';

type ProgressHandler = (e: { lengthComputable: boolean; loaded: number; total: number }) => void;

/** XMLHttpRequest falso: guarda la petición y deja al test decidir la respuesta. */
class FakeXhr {
  static last: FakeXhr | null = null;
  method = '';
  url = '';
  headers: Record<string, string> = {};
  body: unknown = null;
  status = 0;
  responseText = '';
  upload: { onprogress: ProgressHandler | null } = { onprogress: null };
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onabort: (() => void) | null = null;

  constructor() {
    FakeXhr.last = this;
  }
  open(method: string, url: string) {
    this.method = method;
    this.url = url;
  }
  setRequestHeader(name: string, value: string) {
    this.headers[name] = value;
  }
  send(body: unknown) {
    this.body = body;
  }
  abort() {
    this.onabort?.();
  }
  respond(status: number, body: unknown) {
    this.status = status;
    this.responseText = JSON.stringify(body);
    this.onload?.();
  }
}

afterEach(() => {
  vi.unstubAllGlobals();
  FakeXhr.last = null;
});

describe('uploadDocument (plan v2 §5.1, §5.6)', () => {
  it('manda los bytes como octet-stream con el nombre codificado, informa del progreso y resuelve con el 201', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXhr);
    const file = new File(['hola'], 'Especificación API.pdf');
    const progress: number[] = [];

    const pending = uploadDocument(file, { onProgress: (loaded) => progress.push(loaded) });
    const xhr = FakeXhr.last!;
    xhr.upload.onprogress?.({ lengthComputable: true, loaded: 2, total: 4 });
    xhr.respond(201, { id: '20260927-111203-a7qk' });

    await expect(pending).resolves.toEqual({ id: '20260927-111203-a7qk' });
    expect(xhr.method).toBe('POST');
    expect(xhr.url).toBe('/api/documents');
    expect(xhr.headers['Content-Type']).toBe('application/octet-stream');
    expect(xhr.headers['X-File-Name']).toBe('Especificaci%C3%B3n%20API.pdf');
    expect(xhr.body).toBe(file);
    expect(progress).toEqual([2]);
  });

  it('un 409 duplicate_document rechaza con ApiError y el id del documento existente', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXhr);

    const pending = uploadDocument(new File(['x'], 'a.md'));
    FakeXhr.last!.respond(409, { error: 'duplicate_document', message: 'Ese fichero ya estaba subido.', documentId: 'd-1' });

    const error = await pending.catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe('duplicate_document');
    expect((error as ApiError).body?.documentId).toBe('d-1');
  });

  it('abortar rechaza con AbortError', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXhr);
    const controller = new AbortController();

    const pending = uploadDocument(new File(['x'], 'a.md'), { signal: controller.signal });
    controller.abort();

    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
  });
});
