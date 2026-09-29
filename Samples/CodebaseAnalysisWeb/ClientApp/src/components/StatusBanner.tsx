import type { ReactNode } from 'react';

export type BannerTone = 'info' | 'warning' | 'error' | 'success';

interface Props {
  tone: BannerTone;
  title?: string;
  children?: ReactNode;
  action?: ReactNode;
  onDismiss?: () => void;
}

/** Aviso en línea. Los errores se anuncian como `alert`; el resto como `status`, sin interrumpir. */
export function StatusBanner({ tone, title, children, action, onDismiss }: Props) {
  return (
    <div className={`banner banner--${tone}`} role={tone === 'error' ? 'alert' : 'status'}>
      <div className="banner__body">
        {title && <strong className="banner__title">{title}</strong>}
        {children && <div className="banner__text">{children}</div>}
      </div>
      {action}
      {onDismiss && (
        <button type="button" className="btn btn--ghost btn--small" onClick={onDismiss} aria-label="Cerrar aviso">
          ×
        </button>
      )}
    </div>
  );
}
