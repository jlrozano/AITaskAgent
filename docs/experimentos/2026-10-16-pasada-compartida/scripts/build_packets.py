import sqlite3,json,os,collections,sys
ROOT='C:/Users/juan.rozano/m/fw'
c=sqlite3.connect('codebase.db'); c.row_factory=sqlite3.Row
dm=json.load(open('_domain_map.json',encoding='utf-8'))
file2sub={}
for s in dm['subsystems']:
    for f in s['files']: file2sub[f]=s['id']
nodes={r['id']:r for r in c.execute("select id,label,name,qualified_name,file_path,start_line,end_line,properties from nodes")}
def P(n):
    try: return json.loads(n['properties'] or '{}')
    except Exception: return {}
short=lambda qn: qn.split('C-Users-juan.rozano-m-fw.',1)[-1]
EXT=('CALLS','USAGE','INHERITS','IMPLEMENTS','OVERRIDES','READS','WRITES','THROWS')
edges=[(e['source_id'],e['target_id'],e['type']) for e in c.execute("select source_id,target_id,type from edges") if e['type'] in EXT]
# resolve each node to a file
def fileof(n):
    return n['file_path'] if n and n['file_path'] else None
os.makedirs('packets',exist_ok=True)
stats=[]
for s in dm['subsystems']:
    sid=s['id']; files=sorted(s['files'])
    out=[f"# Lote: {s['name']} (id `{sid}`)\n",f"Descripción del mapa de dominios: {s.get('description','')}\n",f"Ficheros: {len(files)}\n"]
    # graph facts
    out.append("\n## Hechos del grafo (compilador/indexador; exactos salvo CALLS aproximadas)\n")
    for f in files:
        ns=[n for n in nodes.values() if n['file_path']==f and n['label'] in ('Class','Interface','Enum','Method')]
        if not ns: continue
        out.append(f"\n### {f}\n")
        for n in sorted(ns,key=lambda x:(x['start_line'] or 0)):
            p=P(n)
            if n['label']=='Method':
                bits=[f"{short(n['qualified_name']).split('.')[-2]}.{n['name']}{p.get('signature','')}",f"L{n['start_line']}-{n['end_line']}",f"cc={p.get('cyclomatic',p.get('complexity'))}",f"fan_in={p.get('fan_in')}",f"fan_out={p.get('fan_out')}"]
                if p.get('accessibility'): bits.append(p['accessibility'])
                if p.get('dead_code_candidate'): bits.append(f"DEAD?({p.get('dead_code_confidence')}: {p.get('dead_code_reason')})")
                for iss in p.get('detected_issues',[]): bits.append(f"ISSUE[{iss}]")
                out.append("- M "+" | ".join(str(b) for b in bits))
            else:
                bits=[f"{n['label']} {n['name']}",f"L{n['start_line']}-{n['end_line']}",f"role={p.get('type_role')}",f"base={p.get('base_classes')}",f"Ca={p.get('afferent_coupling')}",f"Ce={p.get('efferent_coupling')}"]
                if p.get('in_cycle'): bits.append(f"in_cycle={p.get('cycle_members')}")
                if p.get('di_lifetime'): bits.append(f"DI={p.get('di_lifetime')}->{p.get('di_registered_as')}")
                if p.get('dead_code_candidate'): bits.append(f"DEAD?({p.get('dead_code_confidence')}: {p.get('dead_code_reason')})")
                out.append("- T "+" | ".join(str(b) for b in bits))
    # external edges
    ext_in=collections.Counter(); ext_out=collections.Counter()
    for a,b,t in edges:
        na,nb=nodes.get(a),nodes.get(b)
        fa,fb=fileof(na),fileof(nb)
        if not fa or not fb: continue
        sa,sb=file2sub.get(fa),file2sub.get(fb)
        if sa==sb: continue
        if sb==sid and sa: ext_in[(sa,short(na['qualified_name']),t,short(nb['qualified_name']))]+=1
        if sa==sid and sb: ext_out[(sb,short(nb['qualified_name']),t,short(na['qualified_name']))]+=1
    out.append("\n## Relaciones con otros subsistemas\n\nEntrantes (quién de fuera usa este lote):\n")
    for (sub,who,t,what),n in sorted(ext_in.items())[:80]: out.append(f"- [{sub}] {who} -{t}-> {what}")
    out.append("\nSalientes (qué de fuera usa este lote):\n")
    for (sub,who,t,what),n in sorted(ext_out.items())[:80]: out.append(f"- {what} -{t}-> [{sub}] {who}")
    out.append("\n## Código fuente (numerado; la numeración es la línea real del fichero)\n")
    chars=0
    for f in files:
        path=os.path.join(ROOT,f)
        try: text=open(path,encoding='utf-8-sig',errors='replace').read()
        except Exception as ex: out.append(f"\n### FILE {f}\n(no legible: {ex})"); continue
        lines=text.splitlines()
        out.append(f"\n### FILE {f}  ({len(lines)} líneas)\n```")
        for i,l in enumerate(lines,1): out.append(f"{i}: {l}")
        out.append("```"); chars+=len(text)
    txt="\n".join(out)
    open(f'packets/{sid}.md','w',encoding='utf-8').write(txt)
    stats.append((sid,len(files),chars//4,len(txt)//4))
tot=0
for sid,nf,ct,pt in stats:
    print(f"{sid:20s} files={nf:3d} code_tok~{ct:7d} packet_tok~{pt:7d}"); tot+=pt
print('TOTAL packet tokens ~',tot)
