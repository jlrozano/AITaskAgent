import re, os, sys
LOG = r"C:\BBVA\Fuentes\cli-bbva-clan\TEDT\Construccion\delta\Online\src\logs\analysis-pipeline-20261005.log"
REP = r"C:\BBVA\Fuentes\cli-bbva-clan\TEDT\Construccion\delta\Online\src\reports\01_analisis_funcional.md"
OUT = r"C:\Users\JUAN~1.ROZ\AppData\Local\Temp\claude\C--GeminiCli-AITask-AITaskAgent\49abf624-c44b-4a8e-ae69-11aa00c1e675\scratchpad\notes"
os.makedirs(OUT, exist_ok=True)
head = re.compile(r"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+ [+-]\d\d:\d\d \[[A-Z]{3}\] ")
pat = re.compile(r"LLM step (Investigation_AnalisisFuncional_[^ ]+) response content: ")
notes = {}
cur = None
buf = []
start_ts = "2026-10-05 13:2"   # solo la última ejecución (la de las 11:26 UTC = 13:26 local)
with open(LOG, encoding="utf-8", errors="replace") as f:
    for line in f:
        if head.match(line):
            if cur is not None:
                notes[cur] = "".join(buf)
                cur, buf = None, []
            m = pat.search(line)
            if m and line.startswith("2026-10-05 13:"):
                cur = m.group(1)
                buf = [line[m.end():]]
        elif cur is not None:
            buf.append(line)
if cur is not None:
    notes[cur] = "".join(buf)

cite = re.compile(r"[\w./-]+\.(?:java|jsp|xml|cs|js|properties):\d+")
rep = open(REP, encoding="utf-8-sig").read()
print("%-62s %7s %6s %6s" % ("investigación", "chars", "líneas", "citas"))
tot_c = tot_ci = 0
for k, v in sorted(notes.items()):
    open(os.path.join(OUT, k + ".txt"), "w", encoding="utf-8").write(v)
    print("%-62s %7d %6d %6d" % (k, len(v), v.count("\n"), len(set(cite.findall(v)))))
    tot_c += len(v)
    tot_ci += len(set(cite.findall(v)))
print("TOTAL notas: %d chars (~%d tokens), %d citas únicas por nota" % (tot_c, tot_c // 3.5, tot_ci))
allnotes = set()
for v in notes.values():
    allnotes |= set(cite.findall(v))
inrep = set(cite.findall(rep))
print("citas únicas en notas (unión):", len(allnotes), "| en el informe 01:", len(inrep), "| de las del informe que están en notas:", len(inrep & allnotes))
print("informe 01: %d chars, %d líneas" % (len(rep), rep.count("\n")))
