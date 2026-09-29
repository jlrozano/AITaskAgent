import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { jsonResponse, mockFetch } from '../../test/fixtures';
import { languageFor, renderLines, SourceViewer, splitHighlightedLines } from './SourceViewer';

afterEach(() => vi.unstubAllGlobals());

describe('resaltado por líneas', () => {
  it('un span que cruza líneas se cierra y se reabre en cada una', () => {
    expect(splitHighlightedLines('a <span class="c">/* x\ny */</span> b')).toEqual([
      'a <span class="c">/* x</span>',
      '<span class="c">y */</span> b',
    ]);
  });

  it('sin lenguaje conocido el texto se escapa; VBA y C# se resaltan', () => {
    expect(renderLines('<b>&\n', 'form.txt')).toEqual(['&lt;b&gt;&amp;']);
    expect(languageFor('modules/CGAR.bas')).toBe('vbnet');
    expect(languageFor('src/Orders.cs')).toBe('csharp');
    expect(renderLines('/* a\nb */\nint x;', 'x.cs')[1]).toContain('hljs-comment');
  });
});

describe('SourceViewer', () => {
  it('pide el fichero, numera las líneas y marca el tramo citado', async () => {
    const api = mockFetch({
      'GET /api/source': () => jsonResponse({ path: 'mod/M.bas', text: 'Sub A()\n  x = 1\nEnd Sub\n', totalLines: 3, sizeBytes: 24 }),
    });
    vi.stubGlobal('fetch', vi.fn(api.fn));
    render(<SourceViewer path="mod/M.bas" startLine={2} endLine={3} />);

    expect(await screen.findByRole('heading', { name: 'mod/M.bas' })).toBeInTheDocument();
    expect(api.calls[0].url).toBe('/api/source?path=mod%2FM.bas');
    const lines = document.querySelectorAll('.source__line');
    expect(lines).toHaveLength(3);
    expect([...lines].map((l) => l.classList.contains('source__line--hit'))).toEqual([false, true, true]);
    expect(screen.getByText('Líneas 2–3')).toBeInTheDocument();
  });

  it('un fichero que no existe se explica', async () => {
    vi.stubGlobal('fetch', vi.fn(mockFetch({}).fn));
    render(<SourceViewer path="no/existe.cs" />);
    expect(await screen.findByText('No existe «no/existe.cs» en el repositorio.')).toBeInTheDocument();
  });
});
