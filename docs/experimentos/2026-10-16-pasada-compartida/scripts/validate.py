import json,glob,os,re,sqlite3,collections,sys
ROOT='C:/Users/juan.rozano/m/fw'
dm=json.load(open('_domain_map.json',encoding='utf-8'))
sub_files={s['id']:set(s['files']) for s in dm['subsystems']}
cache={}
def lines_of(p):
    if p not in cache:
        try: cache[p]=open(os.path.join(ROOT,p),encoding='utf-8-sig',errors='replace').read().splitlines()
        except Exception: cache[p]=None
    return cache[p]
norm=lambda s: re.sub(r'\s+','',s or '')
def check_anchor(a):
    if not a or not a.get('path'): return 'no_anchor'
    L=lines_of(a['path'])
    if L is None: return 'bad_path'
    s,e=a.get('startLine') or 0,a.get('endLine') or 0
    if s<1 or e<s or e>len(L): return 'bad_range'
    q=norm(a.get('quote'))
    if not q: return 'no_quote'
    win=norm(''.join(L[max(0,s-1-3):min(len(L),e+3)]))
    if q in win: return 'ok'
    if q in norm(''.join(L)): return 'relocated'
    return 'quote_not_found'
c=sqlite3.connect('codebase.db'); c.row_factory=sqlite3.Row
meth=collections.defaultdict(list)
for r in c.execute("select name,file_path,start_line,end_line from nodes where label='Method'"):
    meth[r['file_path']].append((r['name'],r['start_line'],r['end_line']))
res={}
tot=collections.Counter()
for sid in sub_files:
    parts=[f for f in glob.glob(f'out/{sid}.json')+glob.glob(f'out/{sid}.part*.json')]
    if not parts: res[sid]={'missing':True}; continue
    d={'files':[],'types':[],'findings':[],'seedVerdicts':[],'relations':[],'openQuestions':[]}
    for p in parts:
        try: j=json.load(open(p,encoding='utf-8'))
        except Exception as ex: res[sid]={'invalid_json':str(ex)}; break
        for k in d: d[k]+=j.get(k,[])
    else:
        st=collections.Counter()
        for f in d['findings']:
            st[check_anchor(f.get('anchor'))]+=1
            for i in f.get('instances') or []: st['inst_'+check_anchor(i)]+=1
        rel=collections.Counter(check_anchor(r.get('anchor')) for r in d['relations'])
        fpaths={f['path'] for f in d['files']}
        miss_files=sorted(sub_files[sid]-fpaths)
        members={(t.get('path'),m.get('name','').split('(')[0].split('.')[-1]) for t in d['types'] for m in t.get('members',[])}
        expected=[(f,n) for f in sub_files[sid] for (n,s,e) in meth.get(f,[])]
        miss_m=[x for x in expected if x not in members]
        res[sid]={'files':len(d['files']),'files_expected':len(sub_files[sid]),'files_missing':miss_files,
          'types':len(d['types']),'members':len(members),'methods_expected':len(expected),'methods_missing':len(miss_m),
          'findings':len(d['findings']),'by_kind':dict(collections.Counter(f.get('kind') for f in d['findings'])),
          'anchors':dict(st),'relations':len(d['relations']),'rel_anchors':dict(rel),
          'seedVerdicts':len(d['seedVerdicts']),'verdicts':dict(collections.Counter(v.get('verdict') for v in d['seedVerdicts'])),
          'openQuestions':len(d['openQuestions'])}
        for k,v in st.items(): tot[k]+=v
        tot['findings']+=len(d['findings']); tot['methods_expected']+=len(expected); tot['methods_missing']+=len(miss_m)
json.dump(res,open('validation_E3.json','w',encoding='utf-8'),ensure_ascii=False,indent=1)
for sid,r in res.items(): print(sid,json.dumps(r,ensure_ascii=False)[:420])
print('TOTAL',dict(tot))
