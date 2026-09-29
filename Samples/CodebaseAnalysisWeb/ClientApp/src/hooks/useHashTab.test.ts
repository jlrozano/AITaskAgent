import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { formatHash, parseHash, useHashTab } from './useHashTab';

describe('parseHash / formatHash', () => {
  it('Asistente lleva fichero y conversación a la vez, y el fichero con barras queda legible', () => {
    const route = { tab: 'asistente' as const, file: 'custom/estudio de colas.md', chat: 's1' };
    const hash = formatHash(route);
    expect(hash).toBe('#asistente?file=custom/estudio%20de%20colas.md&chat=s1');
    expect(parseHash(hash)).toEqual({ ...route, legacy: false });
  });

  it('sin parámetros, Asistente es solo la pestaña; lo desconocido cae en Análisis', () => {
    expect(formatHash({ tab: 'asistente', file: null, chat: null })).toBe('#asistente');
    expect(parseHash('#asistente')).toEqual({ tab: 'asistente', file: null, chat: null, legacy: false });
    expect(parseHash('#otra/cosa')).toEqual({ tab: 'analisis', file: null, chat: null, legacy: false });
    expect(parseHash('')).toMatchObject({ tab: 'analisis' });
  });

  it('los enlaces de Informes y Chat siguen abriendo lo mismo en Asistente', () => {
    expect(parseHash('#informes/custom/x.md')).toEqual({ tab: 'asistente', file: 'custom/x.md', chat: null, legacy: true });
    expect(parseHash('#informes/doc/d1')).toMatchObject({ file: 'doc/d1', legacy: true });
    expect(parseHash('#chat/s9')).toEqual({ tab: 'asistente', file: null, chat: 's9', legacy: true });
    expect(parseHash('#informes')).toEqual({ tab: 'asistente', file: null, chat: null, legacy: true });
  });
});

describe('useHashTab', () => {
  it('un enlace antiguo se reescribe al formato nuevo al cargar', () => {
    window.location.hash = '#chat/s9';
    const { result } = renderHook(() => useHashTab());
    expect(result.current.route).toEqual({ tab: 'asistente', file: null, chat: 's9' });
    expect(window.location.hash).toBe('#asistente?chat=s9');
  });

  it('cambiar el fichero conserva la conversación, y al volver de Análisis se recupera todo', () => {
    window.location.hash = '#asistente?chat=s1';
    const { result } = renderHook(() => useHashTab());

    act(() => result.current.navigate('asistente', { file: '05_errores_y_bugs.md' }));
    expect(result.current.route).toEqual({ tab: 'asistente', file: '05_errores_y_bugs.md', chat: 's1' });

    act(() => result.current.navigate('analisis'));
    expect(window.location.hash).toBe('#analisis');
    expect(result.current.assistant).toEqual({ file: '05_errores_y_bugs.md', chat: 's1' });

    act(() => result.current.navigate('asistente'));
    expect(result.current.route).toEqual({ tab: 'asistente', file: '05_errores_y_bugs.md', chat: 's1' });
  });

  it('resetAssistant olvida fichero y conversación', () => {
    window.location.hash = '#asistente?file=00_resumen_ejecutivo.md&chat=s1';
    const { result } = renderHook(() => useHashTab());
    act(() => result.current.resetAssistant());
    expect(result.current.route).toEqual({ tab: 'asistente', file: null, chat: null });
    expect(window.location.hash).toBe('#asistente');
  });
});
