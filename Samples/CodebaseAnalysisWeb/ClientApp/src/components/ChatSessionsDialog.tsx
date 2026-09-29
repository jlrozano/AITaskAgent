import { useEffect, useRef, type ComponentProps } from 'react';
import { ChatSessions } from './ChatSessions';

type Props = ComponentProps<typeof ChatSessions> & {
  open: boolean;
  onClose: () => void;
};

/**
 * Conversaciones en un `<dialog>` modal: el navegador pone el fondo, atrapa el foco y cierra con Esc. Elegir una
 * conversación o empezar otra lo cierra; borrar no, para poder seguir limpiando.
 */
export function ChatSessionsDialog({ open, onClose, onSelect, ...sessions }: Props) {
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    else if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog
      ref={ref}
      className="sessions-dialog"
      aria-label="Conversaciones"
      onClose={onClose}
      // Un clic en el fondo llega al propio <dialog>: se cierra.
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      {open && (
        <div className="sessions-dialog__body">
          <ChatSessions
            {...sessions}
            onSelect={(id) => {
              onSelect(id);
              onClose();
            }}
          />
          <button type="button" className="btn btn--ghost btn--small sessions-dialog__close" onClick={onClose} aria-label="Cerrar">
            ×
          </button>
        </div>
      )}
    </dialog>
  );
}
