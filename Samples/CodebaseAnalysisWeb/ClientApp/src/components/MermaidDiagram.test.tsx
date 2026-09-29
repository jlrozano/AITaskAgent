import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { MermaidDiagram, naturalSize } from './MermaidDiagram';

// Lo que devuelve mermaid 11 con useMaxWidth: ancho 100 %, max-width y viewBox con el tamaño natural.
const wideSvg = (id: string) =>
  `<svg id="${id}" width="100%" style="max-width: 1405.5px;" viewBox="-8 -8 1405.5 604" data-testid="diagram"><text>ok</text></svg>`;
vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn(async (id: string) => ({ svg: wideSvg(id) })) } }));

describe('naturalSize', () => {
  it('lee el tamaño natural del viewBox', () => {
    expect(naturalSize(wideSvg('x'))).toEqual({ width: 1405.5, height: 604 });
    expect(naturalSize('<svg width="10"></svg>')).toBeNull();
  });
});

describe('MermaidDiagram (D3)', () => {
  it('se muestra a tamaño natural con scroll propio y se puede ajustar al ancho', async () => {
    const user = userEvent.setup();
    render(<MermaidDiagram code="flowchart LR\n  A --> B" />);
    await screen.findByTestId('diagram');
    const figure = screen.getByRole('group', { name: 'Diagrama' });
    expect(figure).toHaveClass('mermaid--natural');
    expect(figure.style.getPropertyValue('--mmd-w')).toBe('1405.5px');

    await user.click(screen.getByRole('button', { name: 'Ajustar al ancho' }));
    expect(figure).toHaveClass('mermaid--fit');
    await user.click(screen.getByRole('button', { name: 'Tamaño real' }));
    expect(figure).toHaveClass('mermaid--natural');
  });

  it('se abre a pantalla completa y se cierra con Escape, devolviendo el foco', async () => {
    const user = userEvent.setup();
    render(<MermaidDiagram code="flowchart LR\n  A --> B" />);
    await screen.findByTestId('diagram');
    const open = screen.getByRole('button', { name: 'Pantalla completa' });
    await user.click(open);

    const dialog = screen.getByRole('dialog', { name: 'Diagrama a pantalla completa' });
    // El SVG se mueve al diálogo: dos copias repetirían los id que usan sus estilos y marcadores.
    expect(screen.getAllByTestId('diagram')).toHaveLength(1);
    expect(dialog).toContainElement(screen.getByTestId('diagram'));
    expect(screen.getByRole('button', { name: 'Cerrar' })).toHaveFocus();

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getAllByTestId('diagram')).toHaveLength(1);
    expect(open).toHaveFocus();
  });
});
