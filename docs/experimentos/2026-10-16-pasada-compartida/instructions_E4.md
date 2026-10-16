# Instrucciones de consolidación y severidad (etapas E4-E5)

Eres el juez de un registro común de hallazgos de un repositorio de código. Recibes los hallazgos que 11 lecturas independientes (una por subsistema) produjeron, cada uno con ancla literal comprobada (`path`, líneas, `quote`), `impact` (1-5), `likelihood` (1-5), `confidence`, `activationPath` y, si procede, la semilla del compilador de la que parte. También los veredictos sobre semillas y las preguntas abiertas.

Ficheros de entrada (en el mismo directorio que estas instrucciones):
- `findings_all.json`: 230 hallazgos (campo `gid` único).
- `seedverdicts_all.json`: veredictos sobre semillas.
- `openq_all.json`: preguntas abiertas de cada lote (a menudo dependen de código de OTRO lote: cruza con los hallazgos y contratos de ese lote).
- `contracts.md`: contratos (qué hace, entrada, salida) de todos los tipos y miembros, para entender el contexto.
Código fuente real (solo lectura, para reverificar): `C:\Users\juan.rozano\m\fw\<path>`.

## Qué hacer
1. **Deduplicar y agrupar entre lotes.** El mismo defecto visto desde dos lotes se funde en un registro con todas las anclas en `instances`. El mismo patrón en N sitios se registra una vez como patrón con instancias, salvo que su gravedad difiera entre instancias.
2. **Descartar o degradar** lo que sea falso positivo, especulación sin mecanismo, o contradiga otro hallazgo con mejor evidencia. Un hallazgo descartado va al registro con `status: "rejected"` y `rejectReason`. Usa las preguntas abiertas: si un lote preguntaba por algo que otro lote responde, resuélvelo y ajusta.
3. **Reverificar contra el código real** (lee los ficheros y rangos citados) TODOS los candidatos a P0 y P1 y todo lo de seguridad, y los que tengan `confidence` < 0.6. No hace falta releer el resto. Si al leer el código el hallazgo no se sostiene, rechaza o degrada, y dilo.
4. **Fijar UNA severidad por registro** con esta rúbrica (la severidad depende del camino de activación realista, no del daño máximo imaginable):
   - **P0**: pérdida/corrupción de datos o fallo de seguridad explotable en la configuración por defecto o en el uso normal, sin precondiciones inusuales.
   - **P1**: fallo funcional serio (resultado incorrecto, cuelgue, fuga de recursos sostenida) o problema de seguridad que ocurre con precondiciones realistas en una ruta soportada.
   - **P2**: requiere configuración no por defecto, uso incorrecto de la API o condición poco frecuente; degradación, no fallo.
   - **P3**: menor, higiene, deuda sin impacto observable.
   - Un hallazgo cuyo camino de activación exige una opción no por defecto o un uso erróneo de la API NO puede superar P2. Un hallazgo de deuda técnica sin impacto en fallos es P2 o P3, nunca P0/P1.
   Anota en `severityBasis` una línea: el camino de activación y el factor que fija el nivel.
5. **Categorizar** con una taxonomía cerrada y corta (máx. ~15 categorías) coherente entre kinds.
6. Cada registro conserva el `anchor` literal (y `instances`) del hallazgo principal, sin modificar `quote`. No inventes anclas nuevas; si reverificando encuentras un ancla mejor, pon la nueva con su quote literal copiado del código.
7. Los veredictos `confirmed` de semillas deben acabar en algún registro (o explicar en `notes` por qué no). Los `false_positive` no generan registro.

## Salida
Escribe `registry.json` en el mismo directorio (si no cabe en una escritura, `registry.part1.json`, `registry.part2.json`… con el array `entries` partido en trozos disjuntos y el resto de campos solo en el primero). Estructura:
```json
{ "entries": [ {"id":"REG-001","kind":"bug|debt|security","category":"...","title":"...","rationale":"mecanismo, 2-5 líneas",
   "severity":"P0|P1|P2|P3","severityBasis":"...","status":"confirmed|rejected","rejectReason":null,
   "anchor":{"path":"","startLine":0,"endLine":0,"quote":""},"instances":[{"path":"","startLine":0,"endLine":0,"quote":""}],
   "symbol":"","sources":["gid1","gid2"],"reverified":true,"confidence":0.0} ],
  "summary": {"total":0,"confirmed":0,"rejected":0,"bySeverity":{},"byKind":{},"mergedFrom":230,"reverifiedCount":0},
  "notes": ["decisiones relevantes, discrepancias entre lotes y cómo las resolviste"] }
```
JSON válido UTF-8. Verifica con un intento de lectura. Responde solo con el resumen (`summary`) y la ruta, en 4 líneas.
