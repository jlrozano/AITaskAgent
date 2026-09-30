# Plan: verificación de cada informe contra el código

Estado: plan del 2026-09-30, **pendiente de aprobación** (gate). Diseño cerrado con el usuario en la conversación del
2026-09-30; las decisiones están en la memoria `project_report_catalog_decisions`. Ámbito: `Samples/CodebaseAnalysis.Core`,
la web y dos arreglos compatibles del framework.

## Decisiones

- **V1. Flujo por informe**, confirmado por el usuario: cada rama del paralelo lleva su propio verificador. El orden es
  auditor (LLM) → preparación (código) → verificador (LLM). El «cierre» de V5 no es un paso aparte: lo hace el propio
  paso del verificador al terminar, igual que hoy el auditor escribe su fichero. Queda por comprobar si una rama de
  `ParallelStep` admite una secuencia de pasos o hace falta un paso compuesto.
- **V1b. Estructura del pipeline** (decisión del usuario, 2026-09-30):
  - Un `LanguageDetectionStep` (`RepositoryScan` → `DetectedLanguages`) va seguido de un `SwitchStep` con estas rutas:
    - Java → `Group[JavaAnalyzer, StrutsJsp]`;
    - C# → `BdpEnricher`;
    - Java y C# → `Group[JavaAnalyzer, StrutsJsp, BdpEnricher]`;
    - ninguno de los dos → `SemanticEnrichment` (LLM).
  - Después, `SqlFacetExtractor`.
  - Cada rama del `ParallelStep` de informes es un `GroupStep[Auditor, Preparación, Verificador]`.
  - Comprobado:
    - `GroupStep` ejecuta en secuencia y en el mismo contexto (`Framework/Core/Steps/GroupStep.cs:13-49`);
    - `ParallelStep` ejecuta cada rama con `NextSteps` (`Pipeline.cs:226`);
    - `SwitchStep<TIn>` elige un único destino (`SwitchStep.cs:16-60`).
  - Esto parte `LanguageEnricherStep` en dos, saca de `BdpEnricherStep` la regla P2 (se decide en el switch) y obliga a
    ampliar el mapeo de fases de la web con los nombres nuevos.
- **V2. Auditor.** Hace lo mismo que hoy (LLM con tools), pero devuelve el borrador en texto plano y ya no escribe el
  fichero. El borrador termina con un **índice de evidencias** (tema → ficheros y rangos).
- **V3. Preparación** (paso de código, `DelegatedStep`, sin LLM):
  - extrae las citas `ruta:línea` y los identificadores;
  - recoge automáticamente lo que el auditor **leyó** con sus tools, con el mismo mecanismo que `TurnEvidence` en el chat
    y capturado antes de que se compacte la conversación;
  - pasa el validador determinista sobre el borrador;
  - saca el fragmento de código de cada cita, con unas líneas de margen, y marca lo citado sin haberlo leído;
  - neutraliza las secuencias `@[` del borrador para que el renderizador no las expanda. Esto es responsabilidad de quien
    diseña la plantilla, y el framework no cambia. Aprobado por el usuario. Es la única sintaxis que se trata, porque la
    interpolación inserta los valores en crudo y solo la pasada de `@[ruta]` actúa después, sobre el resultado
    (`TinyTemplateRendered.cs:95-101`). Un `@[x]` escrito en la plantilla sigue funcionando. El cierre restaura el texto
    original antes de escribir el fichero.

  El resultado es **un único texto** con tres secciones separadas por marcas poco comunes: borrador, fallos y evidencias.
- **V4. Verificador.** Es un LLM en una conversación nueva y corta, con rol propio (`ReportVerifier`, que por defecto
  usa el mismo modelo que el auditor). Su plantilla solo lleva las instrucciones y `{{ input }}`. Tiene `view_file`,
  `view_code_item`, `grep_search` y `graph_neighbors`.
  - Comprueba cada hallazgo contra el código y **corrige**: citas, afirmaciones, prioridad, o retira el hallazgo si el
    código lo contradice.
  - Devuelve el informe final completo en texto plano, con un apartado final **«Cambios de la verificación»** que lista
    cada corrección y su motivo.
- **V5. Cierre** (paso de código):
  - escribe el fichero y añade `analysis-meta`, con la procedencia actual más `verification=ok|corrected(n)|warnings(n)`;
  - revalida el informe con el validador y emite `report.written`;
  - si quedan fallos, escribe igual y avisa, sin bloquear.
- **V6. Citas en todos los informes (01-09):** cada afirmación dice de dónde sale (`ruta:línea`). Se pide en
  `_common_rules.md` y en las plantillas que haga falta.
- **V7. Identificadores `NN-CAT-nnn`** (p. ej. `05-SEC-002`), únicos en todo el análisis. Lo piden las plantillas y lo
  comprueba el validador.
- **V8. Síntesis (00).** Se hace sobre los informes ya verificados, sin verificador LLM. Solo se comprueba de forma
  determinista que los IDs que cita existen y son únicos.
- **V9. Regeneración.** Pasa por el mismo camino: auditor, preparación, verificador y cierre.
- **V10. Límite de tiempo** propio del verificador: `CodebaseAnalysis:Timeouts:VerifierMinutes`, 10 min por defecto.
- **V11. Web.** Cada carril pasa por *auditando → verificando* y termina en *verificado*, *corregido (n)* o
  *con avisos (n)*.
- **F1 (framework).** Un placeholder que no se resuelve deja un warning en el log con su nombre. Sin cambio de
  comportamiento.
- **F2 (framework).** Un `@[ruta]` que no sea absoluto se resuelve respecto del directorio de trabajo. Se corrige el
  `baseDirectory: context.CurrentStepPath` del camino sin proveedor (`PromptTemplateResolver.Resolve`).
- **Anotado para después**, sin diseñar (usuario, 2026-09-30):
  - añadir enriquecedores por tecnología: JSP (Jasper), React/TypeScript y más;
  - en lugar de un `SwitchStep` que enumera combinaciones, **un paso que cree los pasos según lo detectado**: un
    registro de enriquecedores, cada uno con su señal, y el paso inyecta en `NextSteps` los que tocan
    (`Pipeline.cs:312-321` ya ejecuta los `NextSteps` que se inyectan en tiempo de ejecución).
  - Es específico de este proyecto: el usuario no sabe si se puede generalizar al framework.
- **Fuera de alcance:** fusionar informes (decidido: todavía no) y el verificador LLM de la síntesis.

## Tareas

Van en secuencia. Cada una la hace un agente y termina en commit.

1. **Framework (F1, F2).** `TinyTemplateRendered` y `PromptTemplateResolver`, con tests en `Tests/`.
2. **Validador sobre texto.** `ReportValidator` pasa a aceptar el contenido en memoria, además de los ficheros. Se
   añaden la regla de IDs `NN-CAT-nnn` con unicidad global y la regla de citas por informe (V6). Tests.
3. **Evidencia del auditor.** Captura de las tools leídas en `CustomStatelessTemplateLlmStep` antes de compactar,
   reutilizando `TurnEvidence`. El paso deja de escribir el fichero cuando va seguido de verificación. Tests con un servicio
   falso que emite tool calls.
4. **Preparación, verificador y cierre.**
   - `DelegatedStep` de preparación, rol `ReportVerifier` y plantillas `report_verifier_system.md`/`_prompt.md`.
   - El paso de cierre.
   - La composición de las ramas en `AnalysisPipelineFactory`, para el análisis y la regeneración.
   - `VerifierMinutes`.
   - Tests de extremo a extremo con un LLM enlatado: borrador con una cita rota y un hallazgo contradicho → informe
     corregido con su apartado de cambios, fichero escrito y `analysis-meta` con `verification=corrected(2)`.
5. **Plantillas (V6, V7).** `_common_rules.md`, las plantillas por informe y la síntesis, que cita IDs con prefijo.
6. **Web y eventos (V11).** Estados del carril, mapeo de fases y Vitest.
7. **Documentación.** Guía, READMEs, diagrama del flujo (que ya muestre la verificación como proceso) y documento de estado.

## Criterios de aceptación

1. Con un LLM enlatado, un borrador con una cita a una línea que no existe y un hallazgo que contradice el código acaba
   en un informe con la cita corregida y el hallazgo retirado o corregido. El apartado «Cambios de la verificación» los
   lista con su motivo.
2. El fichero en disco es siempre el verificado. Si la verificación falla o se agota su tiempo, se escribe el borrador
   con `verification=warnings(n)` y un aviso, sin bloquear.
3. Un `@[ruta]` escrito en el borrador no se expande en la plantilla del verificador.
4. Un placeholder mal escrito deja un warning en el log.
5. Los IDs se repiten ya en ningún informe, y el validador lo comprueba de forma global.
6. La regeneración de un informe pasa por el verificador.
7. Pasan todas las suites (Core, Web, Framework y Vitest).
8. La prueba real con LLM queda **pendiente de Azure**. Sus cifras (coste y tiempo reales de la verificación) se
   medirán en el análisis completo del repo Java.

## Riesgos

- El coste estimado es de unos 20.000–40.000 tokens por informe, en torno a un 10 % más por análisis, y 2–4 minutos más
  en la fase de informes. Sin medir.
- La captura de evidencia del auditor depende de interceptar antes de que se compacte la conversación. Es el mismo punto
  que resolvió `AuditedChatStep`.
- Un verificador demasiado celoso puede retirar hallazgos ciertos. El apartado de cambios lo hace visible, y el
  validador de hallazgos esperados (tanda E) sirve para medirlo cuando haya listas.
