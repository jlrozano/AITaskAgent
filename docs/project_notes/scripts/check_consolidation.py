import re, glob, os, sys
LOG = r"C:\BBVA\Fuentes\cli-bbva-clan\TEDT\Construccion\delta\Online\src\logs\analysis-pipeline-20261005.log"
NOTES = r"C:\Users\JUAN~1.ROZ\AppData\Local\Temp\claude\C--GeminiCli-AITask-AITaskAgent\49abf624-c44b-4a8e-ae69-11aa00c1e675\scratchpad\notes"
head = re.compile(r"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+ [+-]\d\d:\d\d \[[A-Z]{3}\] ")
req = re.compile(r"\[LLM\] Step Auditor_AnalisisFuncional Request:")
blocks = []
cur = None
buf = []
with open(LOG, encoding="utf-8", errors="replace") as f:
    for line in f:
        if head.match(line):
            if cur is not None:
                blocks.append((cur, "".join(buf)))
                cur, buf = None, []
            if req.search(line) and line.startswith("2026-10-05 13:"):
                cur = line[:23]
                buf = [line]
        elif cur is not None:
            buf.append(line)
if cur is not None:
    blocks.append((cur, "".join(buf)))
print("bloques Request de Auditor_AnalisisFuncional (13h):", [(t, len(b)) for t, b in blocks])
# el bloque más largo es la consolidación (única llamada)
t, big = max(blocks, key=lambda x: len(x[1]))
print("consolidación:", t, len(big), "chars en el Request")
print("contiene '=== SUBSISTEMA':", big.count("=== SUBSISTEMA"), "| 'Cobertura':", big.count("Cobertura"))
tot = hit = 0
for fn in sorted(glob.glob(os.path.join(NOTES, "*.txt"))):
    txt = open(fn, encoding="utf-8").read()
    lines = [l.strip() for l in txt.splitlines() if len(l.strip()) > 60 and '"' not in l and "\\" not in l]
    sample = lines[::max(1, len(lines) // 25)][:25]
    h = sum(1 for l in sample if l[:70] in big)
    tot += len(sample)
    hit += h
    print("%-58s muestras en el prompt: %d/%d" % (os.path.basename(fn)[:58], h, len(sample)))
print("TOTAL muestras en el prompt de la consolidación: %d/%d" % (hit, tot))
i = big.find("=== SUBSISTEMA")
print("primer subsistema en el prompt, a partir del car.", i, "de", len(big))
