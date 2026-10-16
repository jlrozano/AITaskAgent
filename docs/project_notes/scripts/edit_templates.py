import re, sys
T = r"C:\GeminiCli\AITask\AITaskAgent\Samples\CodebaseAnalysis.Core\templates"

def load(p):
    raw = open(p, "rb").read()
    return raw.decode("utf-8").replace("\r\n", "\n"), b"\r\n" in raw

def save(p, s, crlf):
    if crlf:
        s = s.replace("\n", "\r\n")
    open(p, "wb").write(s.encode("utf-8"))

# Contenido propio de cada informe para la regla de integración.
PER_REPORT = {
    "report_funcional_prompt.md": ("cada capacidad de negocio", "cada regla de negocio, estado y transición, excepción, código de error, constante de negocio, entidad y cita",
        "Cada capacidad de negocio se desarrolla en su propio apartado (qué hace, quién la usa, reglas de negocio, estados y transiciones, datos, flujos alternativos y excepciones, fronteras con otras capacidades), con el nivel de detalle de las notas. Las secciones transversales (misión, sector, arquitectura, actores, conclusión) se añaden a ese desarrollo y no lo sustituyen."),
    "report_flujos_prompt.md": ("cada flujo", "cada paso, rama alternativa, condición, estado, excepción, frontera entre subsistemas y cita",
        "Cada flujo se desarrolla completo, de la entrada a la persistencia, con sus ramas alternativas y de error. Los flujos que cruzan fronteras entre subsistemas se unen en uno solo, sin perder ningún paso de las notas."),
    "report_errores_prompt.md": ("cada hallazgo", "cada hallazgo, causa, condición de disparo, camino de error, recurso, consecuencia y cita",
        "Cada hallazgo de las notas se desarrolla con su causa, su condición y su consecuencia; solo se fusionan los que describen exactamente lo mismo. La severidad la asigna la rúbrica de este informe."),
    "report_arquitectura_prompt.md": ("cada subsistema", "cada capa, dependencia entrante y saliente, acoplamiento, ciclo, punto de integración y cita",
        "Cada subsistema se desarrolla con sus capas y sus dependencias en ambos sentidos; las dependencias entre subsistemas se cruzan y se dejan completas, sin perder ninguna de las notas."),
}

for fn, (unit, items, extra) in PER_REPORT.items():
    p = T + "\\" + fn
    s, crlf = load(p)
    m = re.search(r"^   - \*\*Presupuesto de herramientas de la consolidación:\*\*.*$", s, re.M)
    assert m, fn
    integ = (
        "   - **Integración completa, sin límite de extensión:** cada nota describe un subsistema con todo lo que su investigación encontró. "
        f"El informe integra **todo** ese contenido y no un resumen: {items} de las notas aparecen en el apartado que les corresponde, "
        "reorganizados por tema y escritos una sola vez cuando dos notas dicen lo mismo. No hay límite de longitud: el informe mide lo que mida su contenido, "
        "y se prefiere uno largo y completo a uno corto que descarte reglas o datos. "
        f"{extra} Cada afirmación conserva la cita que traen las notas."
    )
    herr = (
        "   - **Presupuesto de herramientas de la consolidación:** esta fase parte de las notas y no repite la investigación. "
        "Usa las herramientas para comprobar una cita dudosa (una nota que contradice a otra o a las facetas, o cuya cita no cuadra con lo que ves) "
        "o para cubrir un hueco concreto (una pregunta del informe que ninguna nota responde); no hace falta volver a recorrer los subsistemas ni verificar cada cita. "
        "Si el sistema te limita las consultas y las agotas, redacta con lo que tienes y marca como inferido lo que no pudiste comprobar."
    )
    s = s[:m.start()] + integ + "\n" + herr + s[m.end():]
    # La frase «No las copies: intégralas en el análisis» invitaba a resumir: se retira.
    s = s.replace(" No las copies: intégralas en el análisis.", " Intégralas en el análisis sin perder su contenido.")
    save(p, s, crlf)
    print("editado", fn)

# Reglas comunes: las dos reglas de brevedad no se aplican al contenido de las notas.
p = T + "\\_common_rules.md"
s, crlf = load(p)
a = "Una sección sin hallazgos propios se reduce a una línea (\"Sin hallazgos con la evidencia disponible\") en lugar de rellenarse."
assert a in s
s = s.replace(a, a + " Excepción: si el informe recibe notas de investigación por subsistema, la extensión la fija el contenido de las notas y no se reduce ninguna sección que tenga contenido en ellas.", 1)
b = "sin volver a contarlo."
i = s.index("**Cada cosa en un solo sitio:**")
j = s.index(b, i) + len(b)
s = s[:j] + " Excepción: lo que dos notas dicen igual se escribe una vez, pero lo que cada nota aporta de distinto se conserva entero." + s[j:]
save(p, s, crlf)
print("editado _common_rules.md")
