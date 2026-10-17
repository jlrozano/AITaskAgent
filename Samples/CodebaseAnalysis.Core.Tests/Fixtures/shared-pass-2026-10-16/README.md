# Experimento 2026-10-16: pasada compartida de lectura sobre el grafo (caso `Framework`)

Simulación con agentes Claude (Sonnet 5.5 para lectura y redacción, Opus 5.5 con razonamiento alto para consolidación y severidad) del diseño «leer el código una sola vez por lote del grafo». No cambia el enriquecimiento ni la aplicación. Material: grafo `m\fw\.codebase\codebase.db` (copia de solo lectura) y mapa de dominios de la corrida 3 (11 subsistemas, 177 ficheros). Decisiones previas: memoria `project_shared_pass_design_2026_10_16`.

## Qué se hizo
1. **Paquetes por lote (determinista, `scripts/build_packets.py`).** Por subsistema: código numerado con la línea real, hechos del grafo (rangos de cada tipo y método, complejidad, fan-in/out, issues del compilador, candidatos a código muerto, acoplamiento, herencia, ciclos, DI) y relaciones entrantes y salientes con otros subsistemas. Total ~202 k tokens (el mayor, 50 k). `instructions_E3.md` es el prompt.
2. **Lectura por lote (E3)**, un agente Sonnet por subsistema, sin herramientas más allá de leer su paquete. Salida JSON: resumen por fichero, contrato de cada tipo y miembro (qué hace, entrada, salida, efectos, excepciones), hallazgos con ancla literal, veredicto sobre cada semilla del compilador, relaciones y preguntas abiertas. Sin severidad: solo impacto, probabilidad y confianza.
3. **Consolidación y severidad (E4-E5)**, un agente Opus: deduplica entre lotes, rechaza, reverifica contra el código real todos los candidatos a P0/P1, la seguridad y lo de baja confianza, y fija una sola severidad con rúbrica (`instructions_E4.md`).
4. **Informes** desde el registro (errores, deuda, seguridad) y el funcional **solo con los contratos**, sin acceso al código.

## Resultados medidos (scripts de `scripts/`)
| Medida | Resultado |
|---|---|
| Ficheros cubiertos por la lectura | 177 de 177 |
| Métodos del grafo con entrada de contrato | 398 de 431 (33 sin entrada por coincidencia de nombre; **[I]** parte son sobrecargas) |
| Hallazgos de la lectura | 230 (bug 113, deuda 106, seguridad 11) |
| Anclas literales válidas (cita existe en `ruta:líneas ±3`) | 230 de 230 principales y 161 de 161 instancias; 14 de 14 por lote en relaciones |
| Registro tras consolidar | 187 entradas: 182 confirmadas, 5 rechazadas con motivo; 397 de 397 anclas válidas |
| Severidad | P0 1, P1 7, P2 72, P3 102 |
| Reverificadas contra el código | 116 entradas |
| Funcional solo con contratos | ~3.200 palabras, 4 diagramas; pidió **12 lecturas de código** para afirmar con más certeza (sección 8 del informe) |

**Comparación con los informes de GPT-SOL de la corrida 3** (`compare_findings.md`, cotejo por ruta y líneas ±12, no por defecto idéntico): 112 de los 117 hallazgos viejos con ID caen sobre algún registro nuevo; 168 de 182 registros nuevos caen sobre algún hallazgo viejo. Los 5 viejos sin equivalente: tres de duplicación (`02-DUP-002/003/005`) y dos errores P2 (`05-ERR-028`, `05-ERR-030`). Hay 43 pares con severidad distinta (tabla en el fichero). **La verdad la fija el autor del código.**

## Para que el autor revise: P0 y P1 del registro
- **REG-001 P0** (seguridad) `@[ruta]` se expande tras la interpolación y sin directorio base en el camino de DI por defecto: `Support/Template/TinyTemplateRendered.cs:109`. En los informes viejos era `05-SEC-001` P1.
- **REG-002 P1** `ConvertOrThrow` usa `Parse` con la cultura actual: `LLM/Support/LlmHelpers.cs:147` (no estaba en los informes viejos).
- **REG-003 P1** `CloneForBranch` descarta la ruta actual; ramas paralelas dentro de un `LoopStep` reutilizan resultado cacheado: `Core/Models/PipelineContext.cs:153`.
- **REG-004 P1** `ParallelStep` no propaga la suspensión de una rama: `Core/Steps/ParallelStep.cs:114`.
- **REG-005 P1** La fusión multi-fichero `---` no fusiona pasos: `YAML/Execution/YamlPipelineFactory.cs:66`.
- **REG-006 P1** El DAG YAML se valida por aristas pero se ejecuta como cadena lineal: `YAML/Execution/YamlPipelineFactory.cs:200`.
- **REG-007 P1** Nombres de plantilla por defecto terminan en `.md` y el proveedor añade otro `.md`: `LLM/Steps/TemplateLlmStep.cs:36`.
- **REG-008 P1** Includes recursivos sin límite de profundidad ni detección de ciclos: `Support/Template/TinyTemplateRendered.cs:268`.
Rechazados (REG-183..187) con la razón en el propio registro.

## Coste proyectado con GPT-SOL (estimación, no medición)
Precios de `appsettings.analysis.json`: entrada 2 $/M, caché 0,2 $/M, salida 10 $/M. Tokens estimados por tamaño de fichero.
- E3: entrada ~202 k (0,40 $) y salida ~237 k (2,4 $), sin razonamiento.
- E4-E5: entrada ~180 k, reverificaciones ~290 k y registro ~80 k de salida: ~1,7 $.
- Informes (funcional + errores + deuda + seguridad): ~1,2 $.
- **Total ≈ 6 $ sin razonamiento; 10-12 $ con razonamiento donde interesa.** La corrida 3 costó 126,22 $ (17,6 $ sin el bucle) con 118 M tokens de entrada; este proceso mueve ~1 M.
- No incluye los otros informes (arquitectura, flujos, DI, smells, código muerto, síntesis), que partirían del grafo y los contratos. Orden de magnitud ≤ 15 $ en total **[I]**.

## Límites del experimento
- Lo ejecutaron agentes Claude con herramientas de lectura, no GPT-SOL en una llamada por lote: mide la calidad del diseño, no el coste real.
- Sin verdad conocida en `Framework`: la precisión depende de la revisión del autor. El recall sobre fallos sembrados (`csharp-simple`) y el experimento de formatos (carpeta en prosa libre frente a lote estructurado) **no se han hecho**.
- Dos de los tres redactores no leyeron el registro entero (el de errores lo extrajo por script y dedujo las soluciones de los P3 por su título). Los informes `reports/*_nuevo.md` son borradores para comparar, no producto final.
- El lote más grande (`llm-configuration`) se escribió en 4 partes por tamaño de salida.
- La reverificación P0/P1 degradó o rechazó varios candidatos, pero no se probó que el modelo no haya sobreestimado otros.
