// XMLHttpRequest falso para la subida (V2-C): guarda cada petición y deja al test decidir progreso y respuesta.

type ProgressHandler = (e: { lengthComputable: boolean; loaded: number; total: number }) => void;

export class FakeXhr {
  static all: FakeXhr[] = [];
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
    FakeXhr.all.push(this);
  }
  static get last(): FakeXhr | undefined {
    return FakeXhr.all[FakeXhr.all.length - 1];
  }
  static reset() {
    FakeXhr.all = [];
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
  progress(loaded: number, total: number) {
    this.upload.onprogress?.({ lengthComputable: true, loaded, total });
  }
  /** `body` null: respuesta sin cuerpo (p. ej. el 413 de Kestrel). */
  respond(status: number, body: unknown) {
    this.status = status;
    this.responseText = body === null ? '' : JSON.stringify(body);
    this.onload?.();
  }
}
