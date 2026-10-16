// Contratos de la API (plan §4 y plan v2 §5). JSON en camelCase, fechas ISO-8601 UTC, enums como string.
// Los campos de V2 en DTOs de v1 salen siempre del servidor V2: por eso aquí son obligatorios.

export type ReportType =
  | 'AnalisisFuncional'
  | 'CodeSmells'
  | 'CodigoMuerto'
  | 'FlujosEndToEnd'
  | 'ErroresYBugs'
  | 'ArquitecturaAcoplamiento'
  | 'InventarioApisSeguridad'
  | 'DeudaTecnicaHotspots'
  | 'InyeccionDependenciasExtensiones';

export type Health = 'Green' | 'Amber' | 'Red' | 'Unknown';

/**
 * Códigos de error de la API (§4.1 y correcciones post-auditoría del backend). `chat_turn_running` aparece también
 * en `POST /api/analysis`; `workspace_not_writable` es 400 en `PUT /api/workspace`; `report_unavailable` es 503 en
 * `GET /api/reports/{f}`; `invalid_request` es 400 (cuerpo ilegible) o 415 (Content-Type que no es JSON).
 */
export type ApiErrorCode =
  | 'invalid_path'
  | 'workspace_not_writable'
  | 'no_workspace'
  | 'analysis_running'
  | 'chat_turn_running'
  | 'unknown_report'
  | 'not_running'
  | 'not_found'
  | 'empty_message'
  | 'message_too_long'
  | 'report_unavailable'
  | 'invalid_request'
  // Plan v2 §5.4.
  | 'invalid_file_name'
  | 'unsupported_type'
  | 'content_mismatch'
  | 'empty_file'
  | 'not_an_image'
  | 'report_too_large'
  | 'version_not_found'
  | 'file_too_large'
  | 'duplicate_document'
  | 'document_limit_reached'
  | 'report_limit_reached'
  | 'no_graph'
  | 'embeddings_disabled'
  | 'image_description_unavailable'
  | 'knowledge_operation_running'
  // 409 de `POST /api/analysis/{runId}/regenerate`: la ejecución es de otro repositorio que el abierto.
  | 'run_from_other_workspace'
  | 'embedding_provider_error'
  | 'llm_error'
  | 'cross_site_request';

/** Códigos que el cliente fabrica sin respuesta del servidor (`http_<status>` aparte). */
export type LocalErrorCode = 'network' | 'no_stream';

export interface ApiErrorBody {
  /** Un `ApiErrorCode`; se admite cualquier texto para no romper con un código que el cliente aún no conoce. */
  error: ApiErrorCode | (string & {});
  message: string;
  runId?: string;
  /** Solo en `duplicate_document`: el documento que ya tenía ese contenido. */
  documentId?: string;
}

export interface MetaReportDto {
  type: string;
  alias: string;
  fileName: string;
  title: string;
}

export interface MetaDto {
  reports: MetaReportDto[];
  executiveReportFileName: string;
  defaultReports: string[];
  /** Carpeta de los informes propios dentro de la de informes: `custom`. */
  customReportsFolder: string;
  documents: MetaDocumentsDto;
  features: MetaFeaturesDto;
}

/** Límites de la subida (§7.2). `maxFileBytes` es el mayor de los máximos por familia; cada familia tiene el suyo. */
export interface MetaDocumentsDto {
  maxFileBytes: number;
  maxTotalBytes: number;
  maxDocuments: number;
  /** En el orden de §7.2; las extensiones van en minúsculas y con punto. */
  types: DocumentTypeDto[];
}

export interface DocumentTypeDto {
  family: DocumentFamily;
  extensions: string[];
  maxBytes: number;
  viewer: DocumentViewer;
}

/** Con `enabled: false`, `model` y `dimensions` son null y `disabledReason` es el texto para el usuario. */
export interface EmbeddingsFeatureDto {
  enabled: boolean;
  model: string | null;
  dimensions: number | null;
  disabledReason: string | null;
}

export interface ImageDescriptionFeatureDto {
  enabled: boolean;
  model: string | null;
  disabledReason: string | null;
}

export interface MetaFeaturesDto {
  embeddings: EmbeddingsFeatureDto;
  imageDescription: ImageDescriptionFeatureDto;
}

export interface WorkspaceDto {
  repositoryPath: string | null;
  databasePath: string | null;
  reportsPath: string | null;
  chatPath: string | null;
  logsPath: string | null;
  hasGraph: boolean;
  reportCount: number;
  analysisRunning: boolean;
  /** `<repo>/.codebase/documents`; null sin workspace. */
  documentsPath: string | null;
  documentCount: number;
  customReportCount: number;
}

export type RunStatus = 'running' | 'succeeded' | 'completed_with_failures' | 'failed' | 'cancelled';

export interface RunStatusDto {
  runId: string;
  repositoryPath: string;
  status: RunStatus;
  startedAt: string;
  finishedAt: string | null;
  forceReindex: boolean;
  requestedReports: string[];
  lastSeq: number;
  failedReports: string[];
  error: string | null;
}

export interface StartAnalysisRequest {
  forceReindex: boolean;
  reports: string[];
}

/**
 * Cuerpo de `POST /api/analysis/{runId}/regenerate`: vuelve a generar esos informes de la última ejecución, ya terminada,
 * con el mismo `runId`. `synthesis` regenera también el resumen ejecutivo.
 */
export interface RegenerateReportsRequest {
  reports: string[];
  synthesis: boolean;
}

/**
 * Cuerpo de `POST /api/analysis/{runId}/verify`: comprueba esos informes ya escritos contra el código y reescribe los que
 * corrige (la versión anterior queda en el historial). `synthesis` regenera después el resumen ejecutivo; por defecto no.
 */
export interface VerifyReportsRequest {
  reports: string[];
  synthesis: boolean;
}

/** `catalog`: el 00 y los nueve de los auditores. `custom`: los de `reports/custom/`. */
export type ReportKind = 'catalog' | 'custom';

export interface ReportSummaryDto {
  /** Nombre del fichero sin carpeta: `05_errores_y_bugs.md`, `estudio-de-colas.md`. */
  fileName: string;
  /** null en el 00 y en los propios. */
  reportType: string | null;
  title: string;
  /** De la línea `analysis-meta`; sin ella, `Unknown` (y `criticalFindings` null). */
  health: Health;
  criticalFindings: number | null;
  sizeBytes: number;
  lastModified: string;
  kind: ReportKind;
  /** El del hash del cliente: `fileName` en el catálogo, `custom/<fileName>` en los propios. */
  id: string;
  origin: 'pipeline' | 'chat';
  /** El contenido actual es una versión escrita por el chat. */
  modifiedByChat: boolean;
  /** Versiones del historial; 0 si no tiene. */
  versionCount: number;
  /** Estado de su fuente de conocimiento, que da `reportSource(report)` de `client.ts`. */
  knowledge: KnowledgeState;
}

/** Un informe para las rutas de la API: `/api/reports/{fileName}` o `/api/reports/custom/{fileName}`. */
export interface ReportRef {
  kind: ReportKind;
  fileName: string;
}

export type ReportVersionAction = 'baseline' | 'created' | 'updated' | 'restored' | 'deleted';

export interface ReportVersionOriginDto {
  type: 'pipeline' | 'chat' | 'ui';
  /** Solo con `type: 'chat'`; si no, null. */
  sessionId: string | null;
  turnId: string | null;
}

/**
 * Una versión del historial (§6.4). `versionId`: `v0001`, `v0002`… El listado va de la más reciente a la más antigua.
 * `isCurrent`: coincide con el fichero actual (a lo sumo una; ninguna si el pipeline lo regeneró después).
 */
export interface ReportVersionDto {
  versionId: string;
  createdAt: string;
  action: ReportVersionAction;
  origin: ReportVersionOriginDto;
  note: string | null;
  sizeBytes: number;
  sha256: string;
  isCurrent: boolean;
}

// ── Documentos aportados (plan v2 §5.2, §7) ─────────────────────────────────

export type DocumentFamily =
  | 'markdown'
  | 'text'
  | 'code'
  | 'structured'
  | 'csv'
  | 'html'
  | 'pdf'
  | 'docx'
  | 'xlsx'
  | 'pptx'
  | 'image'
  | 'svg';

/**
 * Componente del visor. `markdown`: MarkdownView sobre `/text` (markdown, code, structured, csv, docx, xlsx, pptx).
 * `text`: preformateado sobre `/text`. `pdf`: iframe sin sandbox sobre `/raw`. `html`: iframe `sandbox=""` sobre `/raw`.
 * `image`: `<img>` sobre `/raw` (image y svg; «Describir» solo con `family: 'image'`). Con `pdf`, `html` e `image` hay
 * además pestaña «Texto extraído» sobre `/text`.
 */
export type DocumentViewer = 'markdown' | 'text' | 'pdf' | 'html' | 'image';

export type DocumentExtractionStatus = 'ok' | 'empty' | 'failed' | 'pending_description';

export interface DocumentExtractionDto {
  status: DocumentExtractionStatus;
  charCount: number;
  sectionCount: number;
  /** Páginas del PDF, diapositivas del PPTX u hojas del XLSX; null en el resto. */
  pageCount: number | null;
  truncated: boolean;
  /** Textos para el usuario; con `failed`, el primero es el motivo. */
  warnings: string[];
}

/** Su fuente de conocimiento es `{ kind: 'document', id }`. */
export interface DocumentSummaryDto {
  /** `yyyyMMdd-HHmmss-xxxx`. */
  id: string;
  title: string;
  originalFileName: string;
  /** En minúsculas y con punto. */
  extension: string;
  family: DocumentFamily;
  /** Detectado por contenido. */
  mediaType: string;
  viewer: DocumentViewer;
  sizeBytes: number;
  sha256: string;
  uploadedAt: string;
  useInAnalysis: boolean;
  extraction: DocumentExtractionDto;
  knowledge: KnowledgeState;
}

/** Encabezado de nivel 1 a 3 de `content.md`; líneas desde 1, ambas incluidas. */
export interface DocumentSectionDto {
  ordinal: number;
  heading: string;
  level: number;
  startLine: number;
  endLine: number;
}

export interface DocumentDetailDto extends DocumentSummaryDto {
  sections: DocumentSectionDto[];
  /** Solo en imágenes ya descritas. */
  description: { model: string; generatedAt: string } | null;
}

/** Un campo ausente no cambia. */
export interface PatchDocumentRequest {
  title?: string;
  useInAnalysis?: boolean;
}

// ── Grafo y embeddings (plan v2 §5.2, §8) ─────────────────────────────────

export type SourceKind = 'document' | 'report' | 'custom';

/** Con `custom`, `id` es el nombre del fichero sin `custom/`; con `report`, el nombre del catálogo. */
export interface SourceRefDto {
  kind: SourceKind;
  id: string;
}

export type GraphKnowledgeStateName = 'absent' | 'present' | 'stale' | 'pending';
export type EmbeddingsKnowledgeStateName = 'absent' | 'indexed' | 'outdated' | 'model_mismatch';

/** Con `absent` y `pending`, recuentos a 0 y `addedAt` null. */
export interface GraphKnowledgeState {
  state: GraphKnowledgeStateName;
  nodeCount: number;
  edgeCount: number;
  addedAt: string | null;
}

/** Con `absent`, `chunkCount` 0 y el resto null. */
export interface EmbeddingsKnowledgeState {
  state: EmbeddingsKnowledgeStateName;
  chunkCount: number;
  indexedAt: string | null;
  model: string | null;
}

export interface KnowledgeState {
  graph: GraphKnowledgeState;
  embeddings: EmbeddingsKnowledgeState;
}

export interface KnowledgeStatusDto extends KnowledgeState {
  source: SourceRefDto;
}

export interface KnowledgeSummaryDto {
  graph: { sourceCount: number };
  /** `outdatedCount` cuenta `outdated` y `model_mismatch`. */
  embeddings: {
    enabled: boolean;
    model: string | null;
    dimensions: number | null;
    sourceCount: number;
    chunkCount: number;
    outdatedCount: number;
  };
}

// ── Eventos de progreso (§4.3) ─────────────────────────────────────────────

export type Phase = 'detect' | 'enrich' | 'facets' | 'reports' | 'synthesis';
export type EventLevel = 'info' | 'success' | 'warning' | 'error';

export interface AnalysisEventDto {
  seq: number;
  runId: string;
  ts: string;
  type: string;
  source: string | null;
  report: string | null;
  phase: Phase | null;
  level: EventLevel;
  message: string;
  data: Record<string, unknown> | null;
}

export interface ToolCallInfo {
  id?: string;
  name: string;
  arguments?: string;
}

// ── Chat (§4.4, §4.5) ─────────────────────────────────────────────────────

export type ChatTurnStatus = 'completed' | 'failed' | 'cancelled';

export interface ChatToolCall {
  name: string;
  arguments?: string | null;
  success?: boolean | null;
  durationMs?: number | null;
}

export interface ChatTurn {
  id: string;
  askedAt: string;
  question: string;
  answer: string | null;
  status: ChatTurnStatus;
  error: string | null;
  model?: string | null;
  /** Id del modelo del catálogo (`GET /api/chat/models`) con que se ejecutó; ausente en turnos anteriores al selector. */
  modelId?: string | null;
  tokens?: number | null;
  costUsd?: number | null;
  durationMs?: number | null;
  finishReason?: string | null;
  toolCalls?: ChatToolCall[] | null;
  /** V2-B: informes que escribió el turno, en orden; ausente o null si no escribió ninguno. */
  artifacts?: ChatTurnArtifact[] | null;
  /** Afirmaciones marcadas y referencias, contrastadas con lo que leyó el turno; ausente en turnos anteriores. */
  audit?: ChatTurnAudit | null;
}

export type ChatClaimKind = 'verified' | 'documented' | 'inferred';
export type ChatClaimStatus = ChatClaimKind | 'unchecked';
export type ChatReferenceStatus = 'ok' | 'partial' | 'not_read' | 'out_of_range' | 'missing';

/** Una marca `[V]`, `[D]` o `[I]` de la respuesta, en el orden en que aparece fuera del código. */
export interface ChatClaim {
  kind: ChatClaimKind;
  status: ChatClaimStatus;
  /** Por qué quedó `unchecked`. */
  reason?: string | null;
  /** Textos de sus referencias (claves de `ChatTurnAudit.references`). */
  references: string[];
}

/**
 * Un span de código de la respuesta que cita código (`ruta:línea`) o una fuente (`nombre › sección`). `target`: ruta
 * relativa al repositorio, o el id del informe o `doc/<id>` con el que lo abre el visor.
 */
export interface ChatReference {
  text: string;
  kind: 'code' | 'source';
  target: string;
  startLine?: number | null;
  endLine?: number | null;
  status: ChatReferenceStatus;
}

export interface ChatTurnAudit {
  claims: ChatClaim[];
  references: ChatReference[];
}

/** `GET /api/source`: un fichero de texto del repositorio para el visor. */
export interface SourceFileDto {
  path: string;
  text: string;
  totalLines: number;
  sizeBytes: number;
}

/**
 * Lo que emiten `create_report` y `update_report` (§5.3, §6.2). `id` y `kind` como en `ReportSummaryDto`; `fileName`
 * sin carpeta. «Deshacer» restaura `previousVersionId`; es null si se creó, y entonces no hay «Deshacer» sino
 * «Borrar» del informe propio. `graphRefreshed`: se reingestó en el grafo; `embeddingsOutdated`: tenía embeddings y
 * han quedado `outdated`.
 */
export interface ReportChange {
  id: string;
  kind: ReportKind;
  fileName: string;
  title: string;
  action: 'created' | 'updated';
  versionId: string;
  previousVersionId: string | null;
  graphRefreshed: boolean;
  embeddingsOutdated: boolean;
}

/** El mismo dato persistido en el turno, para que el chip siga al recargar la sesión. */
export interface ChatTurnArtifact extends ReportChange {
  type: 'report';
  /**
   * Ausente mientras no se deshace. Lo pone el servidor al restaurar `previousVersionId` (vale el `versionId` de la
   * versión `restored`) o al borrar el informe propio (el de la versión `deleted`). Solo en el turno persistido:
   * `report.changed` no lo lleva.
   */
  undoneByVersionId?: string | null;
  /**
   * Cómo se deshizo, junto a `undoneByVersionId`: `restored` (se restauró `previousVersionId` y el informe existe) o
   * `deleted` (se borró el informe propio; «Abrir» daría 404). Ausente en los turnos guardados antes de este campo.
   */
  undoneAction?: 'restored' | 'deleted' | null;
}

export interface ChatSession {
  schemaVersion?: number;
  id: string;
  title: string | null;
  repositoryPath?: string;
  /** Modelo que responde los turnos siguientes; ausente en sesiones anteriores al selector (usan el de por defecto). */
  modelId?: string | null;
  createdAt: string;
  updatedAt: string;
  turns: ChatTurn[];
}

/** Un modelo seleccionable del chat (`GET /api/chat/models`). Los precios son USD por millón de tokens. */
export interface ChatModelDto {
  id: string;
  label: string;
  provider: string;
  model: string;
  /** False si el deployment suelta la respuesta de golpe al final en vez de ir goteando. */
  streaming: boolean;
  inputPricePerMillion?: number | null;
  outputPricePerMillion?: number | null;
}

/** Ruta de solo lectura, además del repositorio, que las tools de ficheros del chat pueden recorrer. */
export interface ChatReadRootDto {
  path: string;
  /** Viene de la configuración del servidor (`CodebaseAnalysis:Chat:AllowedReadRoots`): no se quita desde aquí. */
  global: boolean;
  /** Por qué ya no vale (la carpeta se borró, o ahora es un enlace a un sitio prohibido); null si vale. */
  problem: string | null;
}

export interface ChatReadRootsDto {
  roots: ChatReadRootDto[];
}

export interface ChatModelsDto {
  defaultId: string;
  models: ChatModelDto[];
}

/**
 * El plan no fija los campos de ChatSessionSummaryDto (§4.1). El cliente exige `id`, `title` y
 * `updatedAt`, y usa `createdAt` y `turnCount` si llegan (decisión F3, anotada en el plan).
 */
export interface ChatSessionSummaryDto {
  id: string;
  title: string | null;
  updatedAt: string;
  createdAt?: string;
  turnCount?: number;
}

export type ChatStreamEvent =
  | { type: 'turn.started'; turnId: string; askedAt: string }
  | { type: 'delta'; text: string }
  | { type: 'thinking'; text: string }
  | { type: 'tool.calls'; calls: ToolCallInfo[] }
  | { type: 'tool.started'; name: string }
  // `output`, `outputTruncated` y `outputBytes`: la salida de la tool, truncada en el servidor (16 KB); ausentes en un servidor anterior.
  | {
      type: 'tool.completed';
      name: string;
      success: boolean;
      durationMs?: number;
      error?: string | null;
      output?: string | null;
      outputTruncated?: boolean;
      outputBytes?: number | null;
    }
  | { type: 'turn.completed'; turn: ChatTurn }
  // V2-B: tras el `tool.completed` de la tool que escribió y antes de `turn.completed`.
  | ({ type: 'report.changed' } & ReportChange)
  // `turnId` es null en el `turn.failed` de respaldo por una excepción fuera del agente (F2-9).
  | { type: 'turn.failed'; turnId: string | null; status: 'failed' | 'cancelled'; error: string | null };
