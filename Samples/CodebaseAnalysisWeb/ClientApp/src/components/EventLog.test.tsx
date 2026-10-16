import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { LogEntry } from '../state/runReducer';
import { EventLog } from './EventLog';

const entries: LogEntry[] = Array.from({ length: 30 }, (_, i) => ({
  seq: i + 1,
  ts: '2026-09-27T10:15:00Z',
  type: 'log',
  level: 'info',
  source: 'DotNetEnricherStep',
  report: null,
  phase: 'enrich',
  message: `línea ${i + 1}`,
  detail: null,
  channel: 'pipeline',
  tool: null,
  success: null,
  durationMs: null,
}));

/** jsdom no tiene layout: se fijan las medidas del contenedor a mano. */
function layout(el: HTMLElement, box: { scrollHeight: number; clientHeight: number }) {
  let top = 0;
  Object.defineProperty(el, 'scrollHeight', { configurable: true, get: () => box.scrollHeight });
  Object.defineProperty(el, 'clientHeight', { configurable: true, get: () => box.clientHeight });
  Object.defineProperty(el, 'scrollTop', { configurable: true, get: () => top, set: (v: number) => (top = Math.max(0, Math.min(v, box.scrollHeight - box.clientHeight))) });
  return {
    scrollTo: (v: number) => {
      el.scrollTop = v;
      fireEvent.scroll(el);
    },
  };
}

const follow = () => screen.getByRole('checkbox', { name: 'Seguir al final' });

describe('EventLog: «Seguir al final»', () => {
  it('un scroll que no hace el usuario (filas nuevas, panel oculto) no lo desactiva', () => {
    render(<EventLog entries={entries} droppedUpToSeq={null} />);
    const el = screen.getByRole('log');
    const box = { scrollHeight: 1000, clientHeight: 200 };
    const view = layout(el, box);
    // Llega el evento de scroll de nuestro propio autoscroll cuando ya hay filas nuevas debajo.
    box.scrollHeight = 1060;
    view.scrollTo(800);
    expect(follow()).toBeChecked();
    // Al ocultar la pestaña el navegador pone el scroll a 0 y emite scroll.
    box.clientHeight = 0;
    box.scrollHeight = 0;
    view.scrollTo(0);
    expect(follow()).toBeChecked();
  });

  it('el usuario lo desactiva al subir y lo reactiva al volver al final', () => {
    render(<EventLog entries={entries} droppedUpToSeq={null} />);
    const el = screen.getByRole('log');
    const view = layout(el, { scrollHeight: 1000, clientHeight: 200 });
    fireEvent.wheel(el, { deltaY: -120 });
    view.scrollTo(300);
    expect(follow()).not.toBeChecked();
    fireEvent.wheel(el, { deltaY: 120 });
    view.scrollTo(800);
    expect(follow()).toBeChecked();
  });

  it('también con teclado', () => {
    render(<EventLog entries={entries} droppedUpToSeq={null} />);
    const el = screen.getByRole('log');
    const view = layout(el, { scrollHeight: 1000, clientHeight: 200 });
    fireEvent.keyDown(el, { key: 'PageUp' });
    view.scrollTo(500);
    expect(follow()).not.toBeChecked();
  });
});
