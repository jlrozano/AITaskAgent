import type {
  AnalysisEventDto,
  ApiErrorBody,
  ApiErrorCode,
  ChatSession,
  ChatSessionSummaryDto,
  ChatStreamEvent,
  DocumentDetailDto,
  DocumentSummaryDto,
  KnowledgeStatusDto,
  KnowledgeSummaryDto,
  LocalErrorCode,
  MetaDto,
  PatchDocumentRequest,
  RegenerateReportsRequest,
  ReportRef,
  ReportSummaryDto,
  ReportVersionDto,
  RunStatusDto,
  SourceFileDto,
  SourceRefDto,
  StartAnalysisRequest,
  WorkspaceDto,
} from './types';
import { SseParser, readSseStream } from './sse';

/** Error de la API con el código de §4 (`{ error, message }`) o uno local (`network`, `http_<status>`). */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly body: ApiErrorBody | null;

  constructor(status: number, code: string, message: string, body: ApiErrorBody | null = null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.body = body;
  }
}

const fallbackMessages: Record<ApiErrorCode | LocalErrorCode, string> = {
  network: 'No se puede contactar con el servidor. Comprueba que CodebaseAnalysisWeb está arrancado en 127.0.0.1:5180.',
  no_stream: 'La respuesta del chat no trae stream.',
  no_workspace: 'No hay repositorio abierto. Abre uno en la barra superior.',
  analysis_running: 'Hay un análisis en curso. Espera a que termine o cancélalo.',
  chat_turn_running:
    'Hay una respuesta del chat en curso (en esta pestaña o en otra ventana del navegador). Espera a que termine o párala.',
  invalid_path: 'La ruta no es válida: tiene que ser absoluta y el directorio debe existir.',
  workspace_not_writable:
    'No se puede abrir este repositorio: la aplicación necesita escribir en su carpeta .codebase y no tiene permiso. ' +
    'Da permiso de escritura a la carpeta o elige otra.',
  unknown_report: 'Uno de los informes pedidos no existe en el catálogo.',
  not_running: 'La ejecución ya no está en curso.',
  not_found: 'No encontrado.',
  empty_message: 'Escribe una pregunta antes de enviarla.',
  message_too_long: 'La pregunta supera los 8000 caracteres.',
  report_unavailable:
    'El informe no se puede leer ahora: puede que lo esté escribiendo el análisis o que otro programa lo tenga abierto. ' +
    'Vuelve a intentarlo en unos segundos.',
  invalid_request:
    'El servidor no ha entendido la petición. Recarga la página; si se repite, es un fallo de la aplicación.',
  // Plan v2 §5.4. Donde el servidor da el detalle útil (tipo detectado, tamaño máximo...), se usa el suyo.
  invalid_file_name: 'El nombre del fichero no es válido.',
  unsupported_type: 'Ese tipo de fichero no se admite.',
  content_mismatch: 'El contenido del fichero no corresponde a su extensión.',
  empty_file: 'El fichero está vacío.',
  not_an_image: 'Solo se pueden describir imágenes PNG, JPEG, GIF o WebP.',
  report_too_large: 'El informe supera el tamaño máximo (300 000 caracteres).',
  version_not_found: 'Esa versión del informe ya no existe.',
  file_too_large: 'El fichero supera el tamaño máximo para su tipo.',
  duplicate_document: 'Ese fichero ya estaba subido.',
  document_limit_reached:
    'Se ha alcanzado el límite de documentos o de espacio de este repositorio. Borra alguno antes de subir más.',
  report_limit_reached: 'Se ha alcanzado el máximo de informes propios de este repositorio. Borra alguno antes de crear más.',
  no_graph: 'Este repositorio aún no tiene grafo: lanza un análisis antes de añadir documentación al grafo.',
  embeddings_disabled: 'Los embeddings están desactivados en la configuración o el proveedor no tiene clave.',
  image_description_unavailable: 'El modelo no ha podido describir la imagen.',
  knowledge_operation_running:
    'Hay una operación de grafo, embeddings o documentos en curso. Espera unos segundos y vuelve a intentarlo.',
  run_from_other_workspace:
    'Esta ejecución es de otro repositorio: ábrelo para regenerar sus informes o lanza un análisis del repositorio abierto.',
  embedding_provider_error: 'El servicio de embeddings ha fallado. Vuelve a intentarlo más tarde.',
  llm_error: 'El modelo ha fallado al responder. Vuelve a intentarlo más tarde.',
  cross_site_request: 'Petición rechazada: llega desde otra web.',
};

/**
 * Códigos cuyo texto del servidor es técnico o no dice qué hacer (nombra rutas de la API, trae el texto en inglés de
 * una excepción de E/S...): se muestra el del cliente y, si el servidor aporta detalle, entre paréntesis detrás.
 * El resto usa el del servidor, que ya es concreto (`invalid_path`, `analysis_running`...).
 */
const clientFirst: Partial<Record<ApiErrorCode, 'plain' | 'withDetail'>> = {
  no_workspace: 'plain',
  chat_turn_running: 'plain',
  workspace_not_writable: 'withDetail',
  report_unavailable: 'withDetail',
  invalid_request: 'withDetail',
  knowledge_operation_running: 'plain',
  cross_site_request: 'plain',
  image_description_unavailable: 'withDetail',
  embedding_provider_error: 'withDetail',
  llm_error: 'withDetail',
};

function isKnownCode(code: string): code is ApiErrorCode | LocalErrorCode {
  return Object.prototype.hasOwnProperty.call(fallbackMessages, code);
}

/** Texto para el usuario de un error de la API: el del cliente o el del servidor según `clientFirst`. */
export function apiErrorText(code: string, serverMessage: string | null | undefined, status: number): string {
  const fallback = isKnownCode(code) ? fallbackMessages[code] : null;
  const mode = clientFirst[code as ApiErrorCode];
  const server = serverMessage?.trim() || null;
  if (fallback && mode === 'plain') return fallback;
  if (fallback && mode === 'withDetail') return server ? `${fallback} (${server})` : fallback;
  return server || fallback || `El servidor respondió ${status}.`;
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message || apiErrorText(error.code, null, error.status);
  if (error instanceof Error) return error.message;
  return String(error);
}

/** ApiError a partir del estado y el texto de una respuesta de error (`fetch` o `XMLHttpRequest`). */
export function apiErrorFromText(status: number, text: string | null | undefined): ApiError {
  let body: ApiErrorBody | null = null;
  try {
    if (text) {
      const parsed = JSON.parse(text) as Partial<ApiErrorBody>;
      if (parsed && typeof parsed.error === 'string') body = parsed as ApiErrorBody;
    }
  } catch {
    body = null;
  }
  const code = body?.error ?? (status === 404 ? 'not_found' : `http_${status}`);
  return new ApiError(status, code, apiErrorText(code, body?.message, status), body);
}

/** Texto para el usuario cuando no hay respuesta del servidor. */
export const networkErrorMessage = fallbackMessages.network;

async function toApiError(response: Response): Promise<ApiError> {
  let text: string | null = null;
  try {
    text = await response.text();
  } catch {
    text = null;
  }
  return apiErrorFromText(response.status, text);
}

async function send(path: string, init: RequestInit = {}): Promise<Response> {
  let response: Response;
  try {
    response = await fetch(path, init);
  } catch (error) {
    if (error instanceof Error && error.name === 'AbortError') throw error;
    throw new ApiError(0, 'network', fallbackMessages.network);
  }
  if (!response.ok) throw await toApiError(response);
  return response;
}

function jsonInit(method: string, body: unknown, signal?: AbortSignal): RequestInit {
  // Los POST/PUT llevan siempre cuerpo JSON (§4): el backend responde 415 sin él.
  return {
    method,
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(body ?? {}),
    signal,
  };
}

async function json<T>(response: Response): Promise<T> {
  return (await response.json()) as T;
}

const enc = encodeURIComponent;

/** Ruta base de un informe: `/api/reports/{f}` en el catálogo, `/api/reports/custom/{f}` en los propios. */
export function reportPath(ref: ReportRef): string {
  return ref.kind === 'custom' ? `/api/reports/custom/${enc(ref.fileName)}` : `/api/reports/${enc(ref.fileName)}`;
}

/** Id del hash (`05_x.md` o `custom/x.md`) a referencia de informe. */
export function reportRefFromId(id: string): ReportRef {
  return id.startsWith('custom/') ? { kind: 'custom', fileName: id.slice('custom/'.length) } : { kind: 'catalog', fileName: id };
}

/** Fuente de conocimiento de un informe: `report` para el catálogo, `custom` para los propios, con el nombre del fichero. */
export function reportSource(ref: ReportRef): SourceRefDto {
  return { kind: ref.kind === 'custom' ? 'custom' : 'report', id: ref.fileName };
}

function sourcePath(source: SourceRefDto): string {
  return `${enc(source.kind)}/${enc(source.id)}`;
}

const markdownInit = (signal?: AbortSignal): RequestInit => ({ headers: { Accept: 'text/markdown' }, signal });

export const api = {
  getMeta: async (): Promise<MetaDto> => json(await send('/api/meta')),

  getWorkspace: async (): Promise<WorkspaceDto> => json(await send('/api/workspace')),

  openWorkspace: async (repositoryPath: string): Promise<WorkspaceDto> =>
    json(await send('/api/workspace', jsonInit('PUT', { repositoryPath }))),

  startAnalysis: async (request: StartAnalysisRequest): Promise<RunStatusDto> =>
    json(await send('/api/analysis', jsonInit('POST', request))),

  /** null si no hubo ninguna ejecución desde que arrancó el servidor (204). */
  getCurrentRun: async (): Promise<RunStatusDto | null> => {
    const response = await send('/api/analysis/current');
    if (response.status === 204) return null;
    return json(response);
  },

  cancelRun: async (runId: string): Promise<RunStatusDto> =>
    json(await send(`/api/analysis/${enc(runId)}/cancel`, jsonInit('POST', {}))),

  /** 202 con la ejecución otra vez en curso; 409 si hay algo en marcha, 404 si el servidor ya no la tiene. */
  regenerateReports: async (runId: string, reports: string[], synthesis: boolean): Promise<RunStatusDto> =>
    json(await send(`/api/analysis/${enc(runId)}/regenerate`, jsonInit('POST', { reports, synthesis } satisfies RegenerateReportsRequest))),

  analysisEventsUrl: (runId: string, after: number): string => `/api/analysis/${enc(runId)}/events?after=${after}`,

  getReports: async (): Promise<ReportSummaryDto[]> => json(await send('/api/reports')),

  getReportContent: async (fileName: string, signal?: AbortSignal): Promise<string> =>
    (await send(`/api/reports/${enc(fileName)}`, markdownInit(signal))).text(),

  // ── Informes propios y versiones (plan v2 §5.1, V2-B) ──

  getCustomReports: async (): Promise<ReportSummaryDto[]> => json(await send('/api/reports/custom')),

  /** Contenido de un informe del catálogo o propio. */
  getReportMarkdown: async (ref: ReportRef, signal?: AbortSignal): Promise<string> =>
    (await send(reportPath(ref), markdownInit(signal))).text(),

  deleteCustomReport: async (fileName: string): Promise<void> => {
    await send(reportPath({ kind: 'custom', fileName }), { method: 'DELETE' });
  },

  getReportVersions: async (ref: ReportRef, signal?: AbortSignal): Promise<ReportVersionDto[]> =>
    json(await send(`${reportPath(ref)}/versions`, { signal })),

  getReportVersionContent: async (ref: ReportRef, versionId: string, signal?: AbortSignal): Promise<string> =>
    (await send(`${reportPath(ref)}/versions/${enc(versionId)}`, markdownInit(signal))).text(),

  /** Restaura una versión (también es «Deshacer»); crea una versión `restored` y devuelve el informe. */
  restoreReportVersion: async (ref: ReportRef, versionId: string): Promise<ReportSummaryDto> =>
    json(await send(`${reportPath(ref)}/versions/${enc(versionId)}/restore`, jsonInit('POST', {}))),

  // ── Documentos aportados (V2-C). La subida está en `upload.ts` (necesita el progreso de XMLHttpRequest). ──

  /** `[]` sin workspace. */
  listDocuments: async (): Promise<DocumentSummaryDto[]> => json(await send('/api/documents')),

  getDocument: async (id: string, signal?: AbortSignal): Promise<DocumentDetailDto> =>
    json(await send(`/api/documents/${enc(id)}`, { signal })),

  /** `content.md`: lo que lee el asistente. */
  getDocumentText: async (id: string, signal?: AbortSignal): Promise<string> =>
    (await send(`/api/documents/${enc(id)}/text`, markdownInit(signal))).text(),

  /** Fichero de texto del repositorio abierto (visor de código); `path` relativo a su raíz. */
  getSource: async (path: string, signal?: AbortSignal): Promise<SourceFileDto> =>
    json(await send(`/api/source?path=${enc(path)}`, { signal })),

  /** Original, para `iframe`, `img` o «Descargar original»; nunca se lee con fetch. */
  documentRawUrl: (id: string): string => `/api/documents/${enc(id)}/raw`,

  patchDocument: async (id: string, patch: PatchDocumentRequest): Promise<DocumentSummaryDto> =>
    json(await send(`/api/documents/${enc(id)}`, jsonInit('PATCH', patch))),

  deleteDocument: async (id: string): Promise<void> => {
    await send(`/api/documents/${enc(id)}`, { method: 'DELETE' });
  },

  describeDocument: async (id: string): Promise<DocumentDetailDto> =>
    json(await send(`/api/documents/${enc(id)}/describe`, jsonInit('POST', {}))),

  // ── Grafo y embeddings (V2-D) ──

  getKnowledgeSummary: async (): Promise<KnowledgeSummaryDto> => json(await send('/api/knowledge')),

  addToGraph: async (source: SourceRefDto): Promise<KnowledgeStatusDto> =>
    json(await send('/api/knowledge/graph', jsonInit('POST', { source }))),

  removeFromGraph: async (source: SourceRefDto): Promise<KnowledgeStatusDto> =>
    json(await send(`/api/knowledge/graph/${sourcePath(source)}`, { method: 'DELETE' })),

  /** Indexa o reindexa (también es «Actualizar»). */
  indexEmbeddings: async (source: SourceRefDto): Promise<KnowledgeStatusDto> =>
    json(await send('/api/knowledge/embeddings', jsonInit('POST', { source }))),

  removeEmbeddings: async (source: SourceRefDto): Promise<KnowledgeStatusDto> =>
    json(await send(`/api/knowledge/embeddings/${sourcePath(source)}`, { method: 'DELETE' })),

  listChatSessions: async (): Promise<ChatSessionSummaryDto[]> => json(await send('/api/chat/sessions')),

  createChatSession: async (title?: string): Promise<ChatSession> =>
    json(await send('/api/chat/sessions', jsonInit('POST', title ? { title } : {}))),

  getChatSession: async (id: string): Promise<ChatSession> => json(await send(`/api/chat/sessions/${enc(id)}`)),

  deleteChatSession: async (id: string): Promise<void> => {
    await send(`/api/chat/sessions/${enc(id)}`, { method: 'DELETE' });
  },

  /**
   * Envía una pregunta y consume el SSE del turno (§4.4). Sin reintento: un turno cortado se da por cancelado.
   * Rechaza con ApiError si el servidor responde error antes del stream, y con AbortError si se aborta.
   */
  sendChatMessage: async (
    sessionId: string,
    message: string,
    onEvent: (event: ChatStreamEvent) => void,
    signal: AbortSignal,
  ): Promise<void> => {
    const response = await send(`/api/chat/sessions/${enc(sessionId)}/messages`, {
      ...jsonInit('POST', { message }, signal),
      headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
    });
    if (!response.body) throw new ApiError(response.status, 'no_stream', fallbackMessages.no_stream);
    const parser = new SseParser((sse) => {
      if (sse.event !== 'chat') return;
      onEvent(JSON.parse(sse.data) as ChatStreamEvent);
    });
    await readSseStream(response.body, parser, signal);
  },
};

export function parseAnalysisEvent(data: string): AnalysisEventDto | null {
  try {
    const parsed = JSON.parse(data) as AnalysisEventDto;
    return typeof parsed?.seq === 'number' && typeof parsed.type === 'string' ? parsed : null;
  } catch {
    return null;
  }
}
