import { render, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { extractH2, MarkdownView } from './MarkdownView';

// jsdom no tiene layout SVG: mermaid se sustituye por un doble (§8 F3).
const mermaidMock = vi.hoisted(() => ({
  initialize: vi.fn(),
  render: vi.fn(async (id: string, _code: string) => ({ svg: `<svg data-testid="diagram" id="${id}"><text>ok</text></svg>` })),
}));
vi.mock('mermaid', () => ({ default: mermaidMock }));

const report = `# Errores y bugs

<!-- analysis-meta: health=Red critical=6 -->

## Resumen

| Severidad | Hallazgos |
|-----------|-----------|
| P0        | 2         |
| P1        | 4         |

- [x] revisado

## Flujo

\`\`\`mermaid
flowchart LR
  A --> B
\`\`\`

\`\`\`csharp
// ## esto no es un encabezado
var x = 1;
\`\`\`

## Credenciales en \`Config.cs\`

Texto con <b>html crudo</b>.
`;

describe('MarkdownView', () => {
  beforeEach(() => {
    mermaidMock.render.mockClear();
    mermaidMock.initialize.mockClear();
  });

  it('renderiza tablas GFM', async () => {
    render(<MarkdownView source={report} />);
    const table = screen.getByRole('table');
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Severidad', 'Hallazgos']);
    expect(within(table).getAllByRole('row')).toHaveLength(3);
    expect(screen.getByRole('checkbox')).toBeChecked();
    await screen.findByTestId('diagram');
  });

  it('no muestra el comentario analysis-meta ni el HTML crudo', async () => {
    const { container } = render(<MarkdownView source={report} />);
    expect(container.textContent).not.toContain('analysis-meta');
    expect(container.querySelector('b')).toBeNull();
    await screen.findByTestId('diagram');
  });

  it('un bloque mermaid llama a mermaid.render con el código y pinta el SVG', async () => {
    const { container } = render(<MarkdownView source={report} />);
    expect(await screen.findByTestId('diagram')).toBeInTheDocument();
    expect(mermaidMock.render).toHaveBeenCalledTimes(1);
    expect(mermaidMock.render.mock.calls[0][1]).toBe('flowchart LR\n  A --> B');
    expect(mermaidMock.initialize).toHaveBeenCalledWith(expect.objectContaining({ securityLevel: 'strict', startOnLoad: false }));
    // El resto de bloques de código siguen siendo código (resaltado en trozos desde V2-C).
    expect(container.querySelector('code.language-csharp')?.textContent).toContain('var x = 1;');
  });

  it('V2-C: resalta los bloques con lenguaje con clases hljs y deja sin tocar los que no tienen', () => {
    const source = ['```csharp', 'public class A { }', '```', '', '```', 'sin lenguaje', '```', '', '```vbscriptx', 'x', '```'].join('\n');
    const { container } = render(<MarkdownView source={source} />);
    const code = container.querySelector('code.language-csharp') as HTMLElement;
    expect(code).toHaveClass('hljs');
    expect(code.querySelector('.hljs-keyword')?.textContent).toBe('public');
    const plain = [...container.querySelectorAll('pre code')].find((c) => c.textContent?.includes('sin lenguaje')) as HTMLElement;
    expect(plain).not.toHaveClass('hljs');
    // Un lenguaje que highlight.js no conoce no rompe el render.
    expect(container.textContent).toContain('x');
  });

  it('V2-C (§7.5): una imagen remota sale como enlace y solo se pintan data: y /api/', () => {
    const { container } = render(
      <MarkdownView
        source={['![logo](https://evil.example/pixel.png)', '![propia](/api/documents/d1/raw)', '![dato](data:image/png;base64,iVBORw0KGgo=)'].join('\n\n')}
      />,
    );
    const imgs = [...container.querySelectorAll('img')].map((i) => i.getAttribute('src'));
    expect(imgs).toEqual(['/api/documents/d1/raw', 'data:image/png;base64,iVBORw0KGgo=']);
    const link = screen.getByRole('link', { name: 'Imagen: logo' });
    expect(link).toHaveAttribute('href', 'https://evil.example/pixel.png');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  });

  it('si mermaid falla muestra el error y el código en lugar del diagrama', async () => {
    mermaidMock.render.mockRejectedValueOnce(new Error('Parse error on line 2'));
    render(<MarkdownView source={'```mermaid\nflowchart ???\n```'} />);
    expect(await screen.findByText(/El diagrama no se puede dibujar: Parse error on line 2/)).toBeInTheDocument();
    expect(screen.getByText('flowchart ???')).toBeInTheDocument();
  });

  it('con índice lista los encabezados ## fuera de bloques de código', async () => {
    render(<MarkdownView source={report} toc />);
    const nav = screen.getByRole('navigation', { name: 'Índice del informe' });
    expect(within(nav).getAllByRole('button').map((b) => b.textContent)).toEqual([
      'Resumen',
      'Flujo',
      'Credenciales en Config.cs',
    ]);
    await screen.findByTestId('diagram');
  });
});

describe('extractH2', () => {
  it('ignora ### y los ## dentro de bloques de código', () => {
    expect(extractH2('## A\n### B\n~~~\n## C\n~~~\n## [D](x.md) **E**')).toEqual(['A', 'D E']);
  });
});
