import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// V2-A (plan v2 §3.2). jsdom no calcula layout: el alto del documento se mide con Chrome headless; aquí se fija la
// regla de CSS que lo garantiza. Un descendiente absoluto (.visually-hidden) cuyo bloque contenedor está por encima
// del contenedor con scroll escapa de su overflow y estira la página.
const css = readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'styles.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');

interface Rule {
  selector: string;
  body: string;
}

const rules: Rule[] = [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((m) => ({
  selector: m[1].trim().replace(/\s+/g, ' '),
  body: m[2],
}));

const bodiesOf = (selector: string) => rules.filter((r) => r.selector.split(',').map((s) => s.trim()).includes(selector)).map((r) => r.body);
const isScroller = (body: string) => /overflow(-x|-y)?\s*:\s*(auto|scroll)/.test(body);
const positioned = (body: string) => /position\s*:\s*(relative|absolute|fixed|sticky)/.test(body);

describe('styles.css: contenedores con scroll (V2-A)', () => {
  it.each([
    '.thread',
    '.sessions-dialog__body',
    '.reports__side',
    '.reports__main',
    '.analysis__side',
    '.analysis__main',
    '.log__scroll',
  ])('%s está posicionado', (selector) => {
    const bodies = bodiesOf(selector);
    expect(bodies.length).toBeGreaterThan(0);
    expect(bodies.some(isScroller)).toBe(true);
    expect(bodies.some((b) => /position\s*:\s*relative/.test(b))).toBe(true);
  });

  it('.panels recorta lo que se escape con overflow: clip', () => {
    expect(bodiesOf('.panels').some((b) => /overflow\s*:\s*clip/.test(b))).toBe(true);
  });

  it('todo bloque con overflow auto o scroll posiciona su elemento (en el propio bloque o en el de su última clase)', () => {
    const positionedClasses = new Set(
      rules.filter((r) => positioned(r.body)).flatMap((r) => r.selector.split(',').map((s) => s.trim())),
    );
    const offenders = rules
      .filter((r) => isScroller(r.body) && !positioned(r.body))
      .flatMap((r) => r.selector.split(',').map((s) => s.trim()))
      .filter((selector) => {
        const lastClass = selector.match(/\.[\w-]+(?![\s\S]*\.[\w-]+)/)?.[0];
        return !lastClass || !positionedClasses.has(lastClass);
      });
    expect(offenders).toEqual([]);
  });
});
