// Adaptador del anfitrión: lo único que el chat necesita saber de la aplicación que lo aloja (el diseñador de informes,
// otro producto…). El chat no conoce el documento: lo pide, lo bloquea y delega en el anfitrión la aplicación de una
// propuesta (una sola entrada de deshacer; el documento queda bloqueado durante el turno).
import type { ProposalEvent } from "./protocol.ts";

export type ProposalOutcome =
  | { outcome: "applied"; operations?: number }
  | { outcome: "conflict"; message?: string }
  | { outcome: "failed"; message?: string };

export interface HostAdapter {
  /** Documento actual del anfitrión, tal como el servidor lo espera en `MessageRequest.document`. Opaco para el chat. */
  readDocument?(): unknown | Promise<unknown>;
  /** Estado de la interfaz (selección, vista…) para `MessageRequest.context`. */
  readContext?(): Record<string, unknown> | Promise<Record<string, unknown>>;
  /** Bloquea (true) o libera (false) la edición del documento: true al enviar, false en `done`/`error`/cancelación. */
  setEditingLocked(locked: boolean): void;
  /** Aplica la propuesta al documento del anfitrión, en una sola entrada de deshacer. */
  applyProposal(proposal: ProposalEvent): ProposalOutcome | Promise<ProposalOutcome>;
  /** Opcional: el usuario descartó la propuesta (quitar resaltados, etc.). */
  discardProposal?(proposal: ProposalEvent): void | Promise<void>;
}
