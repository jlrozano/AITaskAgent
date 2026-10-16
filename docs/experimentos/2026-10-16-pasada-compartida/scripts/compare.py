import json,re,glob,os,collections
reg=json.load(open('registry.json',encoding='utf-8'))['entries']
reg=[e for e in reg if e.get('status')=='confirmed']
def spans(e):
    out=[]
    for a in [e.get('anchor')]+(e.get('instances') or []):
        if a and a.get('path'): out.append((a['path'].replace(chr(92),'/'),a.get('startLine') or 0,a.get('endLine') or a.get('startLine') or 0))
    return out
rs={e['id']:spans(e) for e in reg}
IDRE=re.compile(r'\b(\d\d-[A-Z]{2,4}-\d{3})\b')
CITE=re.compile(r'`?((?:[\w.\-]+/)+[\w.\-]+\.(?:cs|json|yaml|yml|md|csproj)):((?:\d+(?:-\d+)?)(?:,\s*\d+(?:-\d+)?)*)')
def old_findings():
    res={}
    for f in sorted(glob.glob('C:/Users/juan.rozano/m/fw/reports/0[0-9]_*.md')):
        t=open(f,encoding='utf-8').read()
        ms=list(IDRE.finditer(t))
        for i,m in enumerate(ms):
            end=ms[i+1].start() if i+1<len(ms) else len(t)
            blk=t[m.start():min(end,m.start()+2500)]
            cites=[]
            for c in CITE.finditer(blk):
                for part in c.group(2).split(','):
                    p=part.strip().split('-'); s=int(p[0]); e=int(p[-1]); cites.append((c.group(1),s,e))
            sev=re.search(r'\bP[0-3]\b',blk)
            title=re.sub(r'\s+',' ',blk[len(m.group(1)):200]).strip(' |:-*#')
            id=m.group(1)
            if id not in res or len(cites)>len(res[id]['cites']):
                res[id]={'cites':cites,'sev':sev.group(0) if sev else None,'title':title[:110],'report':os.path.basename(f)}
    return res
old=old_findings()
def overlap(a,b,tol=12):
    return a[0]==b[0] and a[1]<=b[2]+tol and b[1]<=a[2]+tol
matchesold=collections.defaultdict(list); matchesnew=collections.defaultdict(list)
for oid,o in old.items():
    for rid,sp in rs.items():
        if any(overlap(c,s) for c in o['cites'] for s in sp):
            matchesold[oid].append(rid); matchesnew[rid].append(oid)
withcites=[o for o in old if old[o]['cites']]
out=["# Comparación: hallazgos GPT-SOL (informes actuales) frente al registro nuevo\n"]
out.append(f"Hallazgos con ID en informes viejos: {len(old)} ({len(withcites)} con cita ruta:línea). Registro nuevo: {len(reg)} confirmados.\n")
out.append(f"- Viejos con equivalente en el registro (misma ruta, líneas ±12): {sum(1 for o in withcites if matchesold[o])} de {len(withcites)}")
out.append(f"- Registros con equivalente en algún informe viejo: {sum(1 for r in rs if matchesnew[r])} de {len(rs)}\n")
# by severity of new
bysev=collections.defaultdict(lambda:[0,0])
for e in reg:
    bysev[e['severity']][0]+=1; bysev[e['severity']][1]+= 1 if matchesnew[e['id']] else 0
out.append("| Severidad nueva | entradas | con equivalente viejo |\n|---|---|---|")
for s in sorted(bysev): out.append(f"| {s} | {bysev[s][0]} | {bysev[s][1]} |")
out.append("\n## Viejos sin equivalente nuevo (posibles omisiones del nuevo, o falsos del viejo)\n\n| ID viejo | sev | informe | título | citas |\n|---|---|---|---|---|")
for o in sorted(withcites):
    if not matchesold[o]: out.append(f"| {o} | {old[o]['sev']} | {old[o]['report'][:2]} | {old[o]['title']} | {'; '.join(f'{c[0]}:{c[1]}-{c[2]}' for c in old[o]['cites'][:2])} |")
nocite=[o for o in old if not old[o]['cites']]
out.append(f"\n({len(nocite)} IDs viejos sin cita ruta:línea no se pueden comparar: {', '.join(sorted(nocite)[:30])})")
out.append("\n## Equivalentes con severidad distinta\n\n| ID viejo | sev vieja | REG | sev nueva | título nuevo |\n|---|---|---|---|---|")
byid={e['id']:e for e in reg}
for o in sorted(withcites):
    for r in matchesold[o][:2]:
        if old[o]['sev'] and old[o]['sev']!=byid[r]['severity']:
            out.append(f"| {o} | {old[o]['sev']} | {r} | {byid[r]['severity']} | {byid[r]['title'][:80]} |")
out.append("\n## Nuevos P0/P1/P2 sin equivalente viejo\n\n| REG | sev | kind | título | ancla |\n|---|---|---|---|---|")
for e in sorted(reg,key=lambda x:x['severity']):
    if e['severity'] in('P0','P1','P2') and not matchesnew[e['id']]:
        a=e['anchor']; out.append(f"| {e['id']} | {e['severity']} | {e['kind']} | {e['title'][:90]} | {a['path']}:{a['startLine']}-{a['endLine']} |")
open('compare_findings.md','w',encoding='utf-8').write("\n".join(out))
print("\n".join(out[:12]))
