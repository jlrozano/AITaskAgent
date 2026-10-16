"""Compara registros de la pasada compartida (formato _shared_pass_registry.json o registry.json del experimento).

Uso: python compare_registries.py <registro_nuevo.json> [<registro_referencia.json> ...]
Por defecto la referencia es el registro del experimento de Claude (../registry.json).
Casa por ruta y líneas (±12): es un cotejo por ubicación, no prueba que sea el mismo defecto.
"""
import json, sys, os, collections

HERE = os.path.dirname(os.path.abspath(__file__))
CLAUDE = os.path.join(HERE, '..', 'registry.json')


def load(path):
    d = json.load(open(path, encoding='utf-8'))
    return [e for e in d['entries'] if e.get('status', 'confirmed') == 'confirmed'], d.get('summary', {})


def spans(e):
    return [(a['path'].replace(chr(92), '/'), a.get('startLine', 0), a.get('endLine', 0))
            for a in [e.get('anchor')] + (e.get('instances') or []) if a and a.get('path')]


def ov(a, b, t=12):
    return a[0] == b[0] and a[1] <= b[2] + t and b[1] <= a[2] + t


def main():
    if len(sys.argv) < 2:
        print(__doc__); return
    new, ns = load(sys.argv[1])
    refs = sys.argv[2:] or [CLAUDE]
    print('NUEVO', sys.argv[1]); print(' resumen', {k: ns.get(k) for k in ('total', 'confirmed', 'rejected', 'bySeverity', 'byKind', 'readerCostUsd', 'coverageGaps', 'reverifiedCount')})
    print(' categorías distintas:', len({e.get('category') for e in new}))
    print(' P0/P1:'); [print('  ', e['id'], e['severity'], e['kind'], e['title'][:90], '|', e['anchor']['path'] + ':' + str(e['anchor']['startLine'])) for e in new if e['severity'] in ('P0', 'P1')]
    for r in refs:
        ref, _ = load(r)
        print('\nREFERENCIA', r)
        print(' nuevos con equivalente en la referencia:', sum(1 for e in new if any(ov(a, b) for a in spans(e) for o in ref for b in spans(o))), 'de', len(new))
        print(' de la referencia con equivalente en el nuevo:', sum(1 for o in ref if any(ov(a, b) for a in spans(o) for e in new for b in spans(e))), 'de', len(ref))
        print(' P0/P1 de la referencia -> en el nuevo (id, severidad):')
        for o in ref:
            if o['severity'] in ('P0', 'P1'):
                hit = [(e['id'], e['severity']) for e in new if any(ov(a, b) for a in spans(o) for b in spans(e))]
                print('  ', o['id'], o['severity'], o['title'][:60], '->', hit[:3] or 'NO')


main()
