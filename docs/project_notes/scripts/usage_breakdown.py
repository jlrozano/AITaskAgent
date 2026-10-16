import json, re, sys
from collections import defaultdict, Counter

path = sys.argv[1]
usage_re = re.compile(r'^\[(\d\d:\d\d:\d\d) VRB\] Usage from [^:]+: (\{.*\}), reasoning_tokens')
final_re = re.compile(r'^\[\d\d:\d\d:\d\d VRB\] \[LLM\] Step (\S+) Final Response:')
tools_re = re.compile(r'^\[ToolCalls\]: (.*)')
call_re = re.compile(r'(\w+)\((\{.*?\})\)(?:, |$)')

steps = defaultdict(list)
pending = None
tool_calls = defaultdict(list)
last_tools = None
with open(path, encoding='utf-8', errors='replace') as f:
    for line in f:
        m = tools_re.match(line)
        if m:
            last_tools = m.group(1)
            continue
        m = usage_re.match(line)
        if m:
            pending = (m.group(1), json.loads(m.group(2)), last_tools)
            continue
        m = final_re.match(line)
        if m and pending:
            ts, u, tl = pending
            steps[m.group(1)].append(u)
            if tl:
                for name, args in call_re.findall(tl):
                    tool_calls[m.group(1)].append((name, args))
            pending = None

def group(step):
    if step.startswith('Investigation_'):
        return 'Investigation_' + step.split('_')[1]
    return step

PRICE_IN, PRICE_CACHED, PRICE_OUT = 2.5, 0.25, 15.0
tot = Counter()
by_group = defaultdict(Counter)
print(f"{'step':60} {'calls':>5} {'prompt':>9} {'cached':>9} {'%cache':>6} {'first':>7} {'last':>7} {'out':>7} {'reason':>7}")
for step, us in sorted(steps.items()):
    p = sum(u['prompt_tokens'] for u in us)
    c = sum(u.get('prompt_tokens_details', {}).get('cached_tokens', 0) or 0 for u in us)
    o = sum(u['completion_tokens'] for u in us)
    r = sum(u.get('completion_tokens_details', {}).get('reasoning_tokens', 0) or 0 for u in us)
    print(f"{step[:60]:60} {len(us):5} {p:9} {c:9} {100*c/max(p,1):5.0f}% {us[0]['prompt_tokens']:7} {us[-1]['prompt_tokens']:7} {o:7} {r:7}")
    for k, v in (('calls', len(us)), ('prompt', p), ('cached', c), ('out', o), ('reason', r)):
        tot[k] += v; by_group[group(step)][k] += v

print("\nPor grupo:")
for g, c in sorted(by_group.items(), key=lambda x: -x[1]['prompt']):
    full = c['prompt'] * PRICE_IN / 1e6 + c['out'] * PRICE_OUT / 1e6
    real = (c['prompt'] - c['cached']) * PRICE_IN / 1e6 + c['cached'] * PRICE_CACHED / 1e6 + c['out'] * PRICE_OUT / 1e6
    print(f"  {g:45} calls={c['calls']:4} prompt={c['prompt']:9} cached={c['cached']:9} ({100*c['cached']/max(c['prompt'],1):.0f}%) out={c['out']:7} reason={c['reason']:7}  coste_sin_cache={full:6.2f}  coste_con_cache={real:6.2f}")
full = tot['prompt'] * PRICE_IN / 1e6 + tot['out'] * PRICE_OUT / 1e6
real = (tot['prompt'] - tot['cached']) * PRICE_IN / 1e6 + tot['cached'] * PRICE_CACHED / 1e6 + tot['out'] * PRICE_OUT / 1e6
print(f"\nTOTAL calls={tot['calls']} prompt={tot['prompt']} cached={tot['cached']} ({100*tot['cached']/max(tot['prompt'],1):.0f}%) out={tot['out']} reason={tot['reason']}  sin_cache={full:.2f} con_cache={real:.2f}")

# Lecturas repetidas entre investigaciones/auditores
print("\nLecturas de fichero/código repetidas en pasos distintos:")
seen = defaultdict(set)
for step, calls in tool_calls.items():
    for name, args in calls:
        try:
            a = json.loads(args)
        except Exception:
            continue
        key = None
        if name == 'view_file':
            key = ('view_file', a.get('AbsolutePath') or a.get('path') or a.get('file'))
        elif name == 'view_code_item':
            key = ('view_code_item', a.get('File') or a.get('file'), str(a.get('NodePaths') or a.get('node_path')))
        if key:
            seen[key].add(step)
total_reads = sum(1 for s in tool_calls.values() for n, _ in s if n in ('view_file', 'view_code_item'))
multi = {k: v for k, v in seen.items() if len(v) > 1}
print(f"  lecturas totales={total_reads} distintas={len(seen)} leídas en >1 paso={len(multi)}")
for k, v in sorted(multi.items(), key=lambda x: -len(x[1]))[:15]:
    print(f"  {len(v)}x {k[1]}  <- {sorted(group(s) for s in v)}")
print("\nHerramientas usadas:", Counter(n for s in tool_calls.values() for n, _ in s).most_common())
