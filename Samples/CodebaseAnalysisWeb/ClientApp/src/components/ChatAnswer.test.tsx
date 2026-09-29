import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { ChatTurnAudit } from '../api/types';
import { AuditSummary, ChatAnswer } from './ChatAnswer';
import { looksLikeReference, parseCodeReference, parseSourceSelection, selectionFor, sourceSelection } from './claims';

vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn(async () => ({ svg: '<svg></svg>' })) } }));

describe('referencias', () => {
  it('reconoce `ruta:línea`, `ruta:ini-fin`, rutas sin línea y fuentes; un identificador no es referencia', () => {
    expect(parseCodeReference('src/Orders.cs:12-18')).toEqual({ path: 'src/Orders.cs', start: 12, end: 18 });
    expect(parseCodeReference('Orders.cs:7')).toEqual({ path: 'Orders.cs', start: 7, end: 7 });
    expect(parseCodeReference('src\\Orders.cs')).toEqual({ path: 'src/Orders.cs', start: null, end: null });
    expect(parseCodeReference('CGAR.MontarFiltro')).toBeNull();
    expect(looksLikeReference('05_errores_y_bugs.md › BUG-001')).toBe(true);
    expect(looksLikeReference('ExecProcediment')).toBe(false);
  });

  it('la selección del visor de código va y vuelve', () => {
    expect(sourceSelection('src/Orders.cs', 12, 18)).toBe('src/src/Orders.cs:12-18');
    expect(parseSourceSelection('src/src/Orders.cs:12-18')).toEqual({ path: 'src/Orders.cs', start: 12, end: 18 });
    expect(parseSourceSelection('src/a/b.bas')).toEqual({ path: 'a/b.bas', start: null, end: null });
    expect(parseSourceSelection('05_errores_y_bugs.md')).toBeNull();
  });

  it('con auditoría manda su destino; sin ella, el código se abre tal cual y las fuentes no', () => {
    expect(selectionFor('CGAR.bas:5', { text: 'CGAR.bas:5', kind: 'code', target: 'mod/CGAR.bas', startLine: 5, endLine: 5, status: 'ok' })).toBe('src/mod/CGAR.bas:5');
    expect(selectionFor('x › y', { text: 'x › y', kind: 'source', target: 'doc/d1', status: 'ok' })).toBe('doc/d1');
    expect(selectionFor('nada.cs:1', { text: 'nada.cs:1', kind: 'code', target: 'nada.cs', status: 'missing' })).toBeNull();
    expect(selectionFor('a/b.cs:3-4', undefined)).toBe('src/a/b.cs:3-4');
    expect(selectionFor('01_x.md › Flujo', undefined)).toBeNull();
  });
});

const audit: ChatTurnAudit = {
  claims: [
    { kind: 'inferred', status: 'inferred', references: [] },
    { kind: 'verified', status: 'verified', references: ['src/Orders.cs:12-18'] },
    { kind: 'verified', status: 'unchecked', reason: '`src/Orders.cs:40` no se leyó en este turno.', references: ['src/Orders.cs:40'] },
    { kind: 'documented', status: 'documented', references: ['05_errores_y_bugs.md › BUG-001'] },
  ],
  references: [
    { text: 'src/Orders.cs:12-18', kind: 'code', target: 'src/Orders.cs', startLine: 12, endLine: 18, status: 'ok' },
    { text: 'src/Orders.cs:40', kind: 'code', target: 'src/Orders.cs', startLine: 40, endLine: 40, status: 'not_read' },
    { text: '05_errores_y_bugs.md › BUG-001', kind: 'source', target: '05_errores_y_bugs.md', status: 'ok' },
  ],
};

const answer = [
  'Una marca en código `[V]` no cuenta. [I] Parece que valida en cliente.',
  '',
  '- [V] Valida importes en `src/Orders.cs:12-18`.',
  '- [V] Reintenta en `src/Orders.cs:40`.',
  '',
  '[D] Hay un bug de filtros `05_errores_y_bugs.md › BUG-001`.',
  '',
  '```cs',
  '// [V] tampoco dentro de un bloque',
  '```',
].join('\n');

describe('ChatAnswer', () => {
  it('pinta cada marca con el estado de la auditoría, en el mismo orden que el servidor', () => {
    render(<ChatAnswer source={answer} audit={audit} onOpen={() => {}} />);

    expect(screen.getAllByRole('img').map((b) => b.getAttribute('aria-label'))).toEqual([
      'Inferido',
      'Verificado',
      'Sin comprobar: `src/Orders.cs:40` no se leyó en este turno.',
      'Documentado',
    ]);
    expect(screen.getByText('[V]', { selector: 'code' })).toBeInTheDocument();
    expect(document.querySelector('pre')?.textContent).toContain('[V] tampoco');
  });

  it('una referencia abre su destino en el visor', async () => {
    const onOpen = vi.fn();
    const user = userEvent.setup();
    render(<ChatAnswer source={answer} audit={audit} onOpen={onOpen} />);

    await user.click(screen.getByRole('button', { name: 'src/Orders.cs:12-18' }));
    await user.click(screen.getByRole('button', { name: '05_errores_y_bugs.md › BUG-001' }));

    expect(onOpen.mock.calls).toEqual([['src/src/Orders.cs:12-18'], ['05_errores_y_bugs.md']]);
    expect(screen.getByRole('button', { name: 'src/Orders.cs:40' })).toHaveClass('ref--not_read');
  });

  it('sin auditoría (en streaming) las marcas quedan pendientes', () => {
    render(<ChatAnswer source="[V] Algo `a/b.cs:1`." />);
    expect(screen.getByRole('img')).toHaveAttribute('aria-label', 'Verificado (se comprueba al terminar)');
    expect(screen.getByRole('img')).toHaveClass('claim--pending');
  });

  it('si la numeración no casa con la auditoría, no se toma prestado un estado ajeno', () => {
    const shifted: ChatTurnAudit = { claims: [{ kind: 'verified', status: 'verified', references: [] }], references: [] };
    render(<ChatAnswer source="[I] Deducción." audit={shifted} />);
    expect(screen.getByRole('img')).toHaveClass('claim--inferred');
  });

  it('el resumen cuenta por estado', () => {
    render(<AuditSummary audit={audit} />);
    expect(screen.getByLabelText('Afirmaciones de la respuesta').textContent).toBe('V1 verificadaD1 documentadaI1 inferida?1 sin comprobar');
  });
});
