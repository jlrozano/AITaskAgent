import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

// jsdom no trae ResizeObserver (lo usa react-resizable-panels) ni el modo modal de <dialog>. Sin layout no hay
// tamaños que observar; showModal/close solo cambian `open` y avisan con `close`, como el navegador.
if (typeof window.ResizeObserver === 'undefined') {
  window.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
}
// jsdom pone todo en (0,0) con tamaño 0, justo donde user-event pulsa: la librería tomaría cada clic por el inicio de
// un arrastre de separador. Paneles y separadores se sacan de la pantalla para que ningún puntero los toque.
const rect = Element.prototype.getBoundingClientRect;
Element.prototype.getBoundingClientRect = function getBoundingClientRect(this: Element) {
  if (this.hasAttribute('data-panel') || this.hasAttribute('data-separator')) return new DOMRect(-100000, -100000, 0, 0);
  return rect.call(this);
};
if (!HTMLDialogElement.prototype.showModal) {
  HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
    this.open = true;
  };
  HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
    if (!this.open) return;
    this.open = false;
    this.dispatchEvent(new Event('close'));
  };
}

afterEach(() => {
  cleanup();
  window.location.hash = '';
});
