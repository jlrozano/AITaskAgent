# Flujo de análisis: plan final (coste, profundidad y grafo para el chat)

Fecha: 2026-10-06. Estado: **plan pendiente del «adelante»; nada implementado.** Sustituye como guía de implantación a la
propuesta de compactación (`2026-10-06-compactacion-historial-investigaciones-design.md`, que queda como registro del debate y
de las sondas). Marcas: [V] verificado con `fichero:línea` o medida, [I] inferido, [?] por comprobar.

## 1. Decisión que se pide

Aprobar el flujo de la sección 4 y el orden de tandas de la sección 6. Las decisiones abiertas están en la sección 8.

## 2. Diagnóstico medido

**Ejecución real del 2026-10-05** (solo informe funcional, `gpt-5.4`, razonamiento alto, 106 llamadas)
- Coste declarado: 24,78 USD [V] `_run_meta.json` de la ejecución `0a6a3bab3899`.
- El 57 % de la entrada salió de caché y la app no la descuenta [V], así que el coste real ronda los 13,4 USD [I] (caché a
  0,25 USD/M, tarifa de Azure sin confirmar).
- Las investigaciones suman el 97 % de la entrada. Cada una crece de ~8,6 k a 140-218 k tokens en ~13 vueltas, porque cada
  vuelta reenvía todos los resultados anteriores [V] (`docs/project_notes/scripts/usage_breakdown.py`).
- Ninguna lectura se repite entre investigaciones de un mismo informe [V]: el problema es el historial, no leer de más.
- La consolidación conserva el ~19 % de las citas de las notas [V] (documento de estado, 05-10).

**Sondas del 2026-10-06** (Python directo contra Azure; repo `Online`; pregunta sobre notificaciones; 3,84 USD nominales en total)
- Cuando se le pide, `gpt-5.4` escribe `<nota r="Rn">` en el texto de la respuesta junto a las tool calls [V]: 21 de 21 vueltas,
  todos los resultados anotados. El 0/194 del log real era su comportamiento por defecto, no un límite del modelo.
- Comparación con 13 vueltas. A = notas y sustitución de cada resultado ya anotado; B = como hoy. Coste real con la caché a
  0,25 USD/M:

| | A (notas) | B (como hoy) |
|---|---|---|
| Entrada total | 171 k | 655 k (−74 % en A) |
| Prompt de la última vuelta con tools | 17,5 k | 81,6 k |
| Parte de la entrada cacheada | 29 % | 70 % |
| Salida (A: notas incluidas) | 22,3 k | 16,7 k |
| Coste nominal (sin descontar caché) | 0,76 USD | 1,89 USD (−60 % en A) |
| **Coste real estimado** | **0,65 USD** | **0,86 USD (−25 % en A)** |
| Citas `ruta:línea` distintas | 165, en 54 ficheros | 77, en 9 ficheros |

- Con 8 vueltas casi empataban (0,49 frente a 0,54). **El ahorro crece con la longitud de la investigación.** A la escala real
  (prompts de 150-218 k) la extrapolación da ≈ −45 % [I].
- **La llamada final sin tools pierde toda la caché** [V]: `cached: 0` en la última llamada de las cuatro ejecuciones. En la
  app, `CustomStatelessTemplateLlmStep.cs:229` pone `Tools = null`. En B esa llamada fue el 25 % del coste (85 k tokens sin caché).
- **Calidad.** A recorre muchos más ficheros y encuentra incoherencias transversales: tres significados distintos de «no
  enviada» según el DAO, o un comentario que contradice al código. B profundiza más en la clase central: en una ejecución
  encontró reglas que A no tenía (criterio diario `IN_ADVANCE`/`IN_ARREARS`, `errorCartaYaEnviada`, validaciones
  `esInsertable`) y en la otra fue al revés. Con una ejecución de cada variante no hay un ganador claro en calidad, solo
  estilos distintos (anchura frente a profundidad) [V, salidas en el scratchpad de la sesión 64a2a19f, `probe_out2` y `probe_out13`].

## 3. Hechos del código que fijan el diseño

- **El punto de extensión es la herramienta.** `LlmTool.InternalExecuteAsync(argumentsJson, PipelineContext, ...)` es
  `protected abstract` [V] `Framework/LLM/Tools/Base/LlmTool.cs:150`.
- **El orden de la vuelta encaja.** Primero se procesan los tags de la respuesta [V] `BaseLlmStep.LlmInvocation.cs:501-530`, luego
  esa respuesta se guarda en el historial [V] `:182` y por último se ejecutan las tools del turno [V] `:184`. Cuando una tool
  del turno n+1 se ejecuta, la `<nota>` sobre el resultado n ya existe.
- **El cuerpo del tag se purga de lo que se guarda** y solo queda su referencia [V] `:518-530`. Por eso la nota no se reenvía
  dos veces.
- **El paso sin estado trabaja con su propia conversación** [V] `StatelessTemplateLlmStep.cs:55-65`, y
  `CustomStatelessTemplateLlmStep` ya sobrescribe `GetConversationContext` [V] `CustomStatelessTemplateLlmStep.cs:213-219`.
- **No hay operación para reemplazar un mensaje.** `Message.Content` es `init` y `MessageHistory` solo sabe añadir y quitar [V]
  `MessageHistory.cs:31-66`.
- **El framework no conoce `tool_choice`** [V] (no hay ninguna coincidencia en el repo).
- **El precio no distingue los tokens cacheados** [V] `LlmProvider.cs:37-54`. `cached_tokens` solo se registra en el log [V]
  `OpenAILlmService.cs:1175-1177`.

## 4. Flujo propuesto (de principio a fin)

Lo que no se menciona sigue igual: los informes en modo A y B, la síntesis y la verificación a demanda.

### 4.1 Grafo: indexación y enriquecimiento
1. `DetectAndIndexGraphStep` (cbm), seguido de los enriquecedores Java, Struts/JSP y DotNet y de las facetas SQL. Sin cambios.
2. **Nuevo E2, tablas y acceso a datos** (determinista). Nodos `Table` y aristas `QUERIES`/`MODIFIES` desde el método o la clase
   que contiene el literal SQL, con su `ruta:línea`. `SqlTableScanner` ya extrae la tabla, el verbo y el fichero [V]
   `SqlTableScanner.cs:68-74`; habría que añadirle la línea o el método [?]. Escritura idempotente anclada en los QN, como hace
   `JavaGraphWriter`.

### 4.2 Preparación
3. `RepositoryInventory`, `DomainDiscovery`, `CoveragePlanning` y `TimeBudget`, sin cambios.
4. **Nuevo E1, subsistemas como nodos.** Al terminar `DomainDiscoveryStep` se escriben nodos `Subsystem` con aristas
   `CONTAINS` (hacia clases, JSP y rutas) y `DEPENDS_ON`, a partir de `_domain_map.json`, que hoy solo se escribe como fichero
   [V] `PreparationModels.cs:386`.

### 4.3 Investigaciones y auditores: protocolo de notas y sustitución de resultados
5. **Herramientas con sustitución** (sample, por composición). Una subclase de `LlmTool` envuelve cada herramienta de lectura
   del auditor: `view_file`, `view_code_item`, `grep_search`, `graph_search`, `graph_neighbors` y `graph_domain`. Al ejecutarse:
   (a) en la conversación del paso, sustituye por su nota cada resultado anterior que ya tenga una; (b) ejecuta la herramienta
   original; (c) marca su resultado con `[Rn]`.
6. **`ITagHandler` `<nota r="Rn">`** (sample). Guarda cada nota en un almacén por investigación, en la rama de contexto.
7. **El paso de investigación expone su conversación.** Una subclase de `CustomStatelessTemplateLlmStep` sobrescribe
   `GetConversationContext` y deja la conversación en la rama de contexto, que es donde la busca la herramienta del punto 5.
8. **Reemplazar un mensaje.** Una de dos:
   - (a) quitar y volver a añadir la cola con lo que ya hay;
   - (b) un `MessageHistory.ReplaceMessage` en el framework. Es una operación de datos, no un hook.
   El ahorro y la caché son los mismos en las dos: solo cambia el último resultado ya consumido.
9. **Guardarraíl.** Un resultado sin nota se queda entero y se registra un aviso; no se falla.
10. **Notas finales** = almacén de notas + respuesta final. Si se agota el tope de herramientas, las notas ya existen.
11. **Plantillas.** `_investigacion_prompt` incorpora el protocolo, con texto genérico. Para no perder la profundidad que mostró
    B: «lee completo el punto de entrada principal antes de ampliar».
12. **La respuesta final conserva la caché.** En modo respuesta final se envían las tools con `tool_choice: none` en lugar de
    `Tools = null` (`CustomStatelessTemplateLlmStep.cs:229`). Depende de que el proveedor admita pasarlo por `Options`, como hoy
    `reasoning_effort` [?]. Si no se puede, queda fuera de esta tanda. Esta mejora sirve también a los auditores actuales.
13. **Alcance.** Se aplica a las investigaciones del modo B y, tras medirlo, a los auditores del modo A, que usan el mismo bucle
    de herramientas. No se aplica al chat: una pregunta de seguimiento puede necesitar el contenido original.

### 4.4 Consolidación
14. Recibe las notas como hoy. La corrección de plantillas del 05-10 («integración completa») sigue sin medir y se mide aquí.
    Si la retención sigue baja, la opción siguiente es consolidar por capítulos (un subsistema o capacidad por llamada, con el
    01 como índice). No se decide hasta tener la medida.

### 4.5 Persistencia para el chat y la regeneración
15. **Nuevo E3, notas como conocimiento citado.** Nodos `InvestigationNote`, con aristas `ABOUT` hacia el `Subsystem` y `CITES`
    hacia los nodos de código que contienen cada `ruta:línea`. Se indexan para `semantic_search` por el mismo canal que
    `UserDocSection`/`DOC_DESCRIBES` [I]. Las notas ya están pagadas: hoy se pierden tras la consolidación [V]. Que el informe no
    conserve algo deja de significar que se pierda.
16. **Chat.** Una vista `subsystems` y otra `tables` en `graph_domain`; hoy solo tiene `routes`, `di`, `attributes`, `projects` y
    `contracts` [V] `GraphDomainTool.cs:21,30`. Las notas son consultables por `semantic_search` y `graph_neighbors`.
    Preguntas que pasa a responder:
    - «¿Qué subsistemas hay y qué hace cada uno?»
    - «¿Quién escribe en `GEO_NOTIFICACIONES`?»
    - «¿Qué reglas o estados tiene Notificaciones?», con cita y sin volver a investigar.

### 4.6 Contabilidad del coste
17. **Corrección de contrato en el framework.** El resultado lleva `cached_tokens` y el precio admite
    `CachedInputPricePerMillion` (`LlmProvider.cs:37-54`). `_run_meta.json` informa del coste real. Sin esto no se puede
    medir nada de lo anterior.

## 5. Enriquecimientos del grafo para después
- **E4, constantes y estados.** Valores de enum y constantes `static final`, con aristas `COMPARES_WITH`/`ASSIGNS` desde los
  métodos. Ampliación del analizador Java [I]. Responde «¿quién pasa una notificación a ENVIADA?» y alimenta los informes 01, 04
  y 05.
- **E5, mensajería e integraciones.** `Queue`/`Topic`/`ExternalEndpoint` con aristas `PUBLISHES_TO`/`CONSUMES_FROM`, sacados
  de API genéricas de JMS y HTTP. Heurística nueva.
- **E6, embeddings de código por método.** Búsqueda por intención en el chat. Coste alto: ~150 k métodos [?]. Poco valor para
  los informes.

## 6. Tandas de implantación (cada una con tests y una verificación al final)

| Tanda | Contenido | LLM real |
|---|---|---|
| T1 | Contabilidad de la caché (17) | No |
| T2 | Protocolo de notas y sustitución (5-11) en las investigaciones; tests con LLM falso: sustitución, guardarraíl, notas finales con el tope agotado | No |
| T3 | Respuesta final con caché (12), si el proveedor lo admite | Sonda pequeña |
| T4 | E1 + E2 + vistas del chat (2, 4, 16) | No |
| T5 | E3: notas en el grafo y en `semantic_search` (15) | No |
| T6 | Medida real: informe funcional de `Online` con la raíz que contiene `WebContent`, comparado con la ejecución del 05-10 (coste real, retención de citas en la consolidación, cobertura de dominios). Tope y OK previos | Sí, con tope |
| T7 | Según T6: llevar el protocolo al modo A y/o consolidar por capítulos | Según T6 |
| Después | E4, E5, E6; fusión de informes (02+05, 02+08, 06+09, 01+04) con los informes reales delante | — |

## 7. Riesgos
- **Anchura frente a profundidad.** A tiende a ampliar el alcance y puede quedarse corto en la clase central. Lo mitiga la
  instrucción de la plantilla (punto 11), y se mide en T6.
- **Notas pobres.** Si el modelo anota poco, el resultado se pierde sin remedio una vez sustituido; puede volver a pedirlo,
  porque la nota lleva la ruta. Se mide la tasa de relecturas.
- **Coste de salida.** Las notas son salida a 15 USD/M: con 13 vueltas fueron ~15 k tokens por investigación. Si el modelo
  anota en exceso, el ahorro desaparece. La plantilla tiene que pedir notas densas.
- **Una ejecución por variante.** Las cifras de la sonda son indicativas; T6 es la medida que decide.

## 8. Decisiones del usuario (2026-10-06): «adelante» con estas condiciones
1. Reemplazar mensajes con `MessageHistory.ReplaceMessage` en el framework (opción b).
2. OK al cambio de contrato de T1 (`cached_tokens` en el resultado y precio de caché en el proveedor).
3. El protocolo de notas va también al modo A, desde T2, con un interruptor de configuración del sample para comparar.
4. Tope de 30 USD para la medida real (T6). El usuario quiere datos reales.

## 8 bis. Decisiones abiertas (histórico)
1. Reemplazar mensajes: (a) quitar y volver a añadir en el sample, o (b) `ReplaceMessage` en el framework.
2. Contabilidad (T1): cambio de contrato en el framework (`cached_tokens` y precio de caché). Necesita OK.
3. ¿Se lleva el protocolo al modo A sin esperar a T6?
4. Tope de gasto para T6.
5. Tarifa real de la caché de `gpt-5.4` en Azure.
