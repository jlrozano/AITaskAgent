# Compactación del historial en las investigaciones por subsistema (propuesta, sin aprobar)

Fecha: 2026-10-06. Estado: **propuesta para estudiar; nada implementado.** Sale de un debate en dos rondas entre tres modelos
(Opus 5.5, Sonnet 5.5, Fable 5.1) sobre los datos medidos en el log de la ejecución `0a6a3bab3899` del 2026-10-05 (solo
informe funcional, `gpt-5.4`, razonamiento alto). Script de medida: `docs/project_notes/scripts/usage_breakdown.py`.

## Problema medido

- 106 llamadas, 8,9 M tokens de entrada, 24,78 USD declarados. Las investigaciones son el 97 % de la entrada.
- Cada investigación empieza en ~8,6 k tokens y termina en 140-218 k tras ~13 vueltas: cada vuelta reenvía todos los
  resultados de herramientas anteriores (crecimiento cuadrático).
- El 57 % de la entrada fue `cached_tokens` y `_run_meta.json` la cobra a precio completo. Con la caché al 10 % (precio de
  Azure sin confirmar) el coste real rondaría 13,4 USD.
- Ninguna lectura se repite entre investigaciones del mismo informe: el problema es el historial, no leer de más.

## Hechos del código que condicionan el diseño

- `ConfigureLlmRequest` se llama una vez (`Framework/LLM/Steps/BaseLlmStep.cs:451`); el bucle de herramientas recurre con el
  mismo `request` (`BaseLlmStep.LlmInvocation.cs:193-197`) y añade los resultados a la conversación del estado (`:348`, `:440`).
  Entre `ExecuteToolCallsAsync` (`:184`) y la recursión no hay ningún virtual: **hoy no se puede compactar desde el sample**.
- `ConversationContext` es `sealed` y `GetMessagesForRequest` no es virtual (`ConversationContext.cs:13,174`).
- `Message.Content` es `init` (`Message.cs:13`); `MessageHistory` solo tiene añadir/quitar (`MessageHistory.cs:31-66`).
- La ventana deslizante existente tira grupos enteros sin rastro (`MessageHistory.cs:126-189`) y en el sample está a 262144
  tokens (`appsettings.analysis.json:534`): no actúa.
- **`gpt-5.4` con razonamiento alto no escribe texto cuando pide herramientas**: 0 de 194 turnos con tool calls traen
  `[Content]` en el log. Descarta cualquier mecanismo de notas que viaje en el texto de la respuesta (tags incluidos).
- `view_file` devuelve hasta 800 líneas (`FileTools/Tools/ViewFileTool.cs:19`) y `grep_search` hasta 50 coincidencias con el
  nombre de fichero repetido en cada una (`FileTools/Tools/GrepSearchTool.cs:30,37`). `FileTools` lo comparten el chat y
  otros consumidores.

## Propuesta consensuada (por orden de coste de implantación)

0. **Contar bien el coste**: descontar `cached_tokens` en el consumo (`OpenAILlmService.cs:1175-1177` ya lo lee). Sin esto no
   se puede medir ninguna optimización.
1. **Recortar en origen** (complementario, no alternativo): `grep_search` agrupado por fichero; `view_file` con un tope por
   defecto menor (~300 líneas) y «pide el rango siguiente». Baja el suelo del prompt (las 2 vueltas que siempre quedan vivas
   pesan ~26 k de ~35 k). Coste: más vueltas de paginación; hay que medirlo. Afecta a otros consumidores de `FileTools`:
   mejor como parámetro del registro de la herramienta que como cambio de defecto global.
2. **Gancho genérico en el framework**: `protected virtual Task CompactConversationAsync(ConversationContext, int iteration,
   PipelineContext)` en `BaseLlmStep`, no-op por defecto, entre `:184` y `:193`; y `MessageHistory.ReplaceMessage` (o
   equivalente) que conserve el `ToolCallId`.
3. **Bloc de notas como herramienta del sample, `save_notes(text)`**: el prompt de investigación pide volcar lo hallado, con
   `ruta:línea`, junto con las lecturas del mismo turno (no cuesta vuelta extra si va en paralelo). Las notas quedan en los
   argumentos del mensaje del asistente, que la compactación no toca. Notas finales = bloc + respuesta final; si se agota el
   tope de herramientas, el bloc ya existe.
4. **Compactación en lote al cruzar un umbral T** (40-60 k): los resultados anteriores a las 2 últimas vueltas pasan a un stub
   determinista (`[consumido: view_file X l.a-b; está en tus notas; vuelve a pedirlo si te falta]`). En lote y no por vuelta
   para que el prefijo cacheado siga estable: ~3 roturas de caché por investigación.
5. **Guardarraíl (no fallar duro)**: un resultado solo se sustituye si después hay un `save_notes`. Sin notas, se conserva, se
   avisa y el siguiente stub recuerda anotar.

## Ahorro estimado

- Entrada de las investigaciones: −44 a −50 % (Opus, Sonnet), −60 % si solo queda 1 vuelta viva (Fable).
- En dinero real (caché al 10 %, sin confirmar): **~3-6 USD menos por informe sobre ~13 USD**. Gran parte del crecimiento
  cuadrático ya lo amortiza la caché. Ganancia añadida: latencia y margen frente al límite de contexto.
- El razonamiento (95 k de 168 k de salida, ≈1,4 USD) no es la palanca: no se toca `reasoning_effort` por coste.

## Descartado

- Notas en el texto de la respuesta o en un tag `<nota>`: el modelo no escribe texto al pedir herramientas (0/194).
- Bajar `SlidingWindowMaxTokens`: tira evidencia sin stub y rompe la caché en cada vuelta.
- Resumir con LLM los resultados viejos: más llamadas, mata la caché y pierde citas.
- Override de `ConfigureLlmRequest` sin tocar el framework: no actúa por vuelta (ver hechos).

## Revisión del usuario (2026-10-06): sustituye a la propuesta consensuada

- **Tag en vez de tool para las notas.** Escribir la nota cuesta la misma salida en los dos casos, pero el cuerpo del tag se
  purga del historial y solo queda su referencia (`BaseLlmStep.LlmInvocation.cs:518-530`, persistido en `:182`); los
  argumentos de `save_notes` se reenvían en cada vuelta. El tag no añade ida y vuelta. Bloqueo pendiente de medir: el 0/194
  se midió con un prompt que nunca pedía texto; hay que probar si `gpt-5.4` emite `<nota>` en `content` junto a tool calls
  cuando se le pide.
- **Por cada fichero leído, dejar solo lo que hace falta y eliminar su contenido** (no por épocas). Si se sustituye solo el
  resultado más reciente, ya consumido, el prefijo anterior no cambia y la caché se conserva: el prompt deja de crecer
  (~8,6 k base + resultado en curso + referencias). Estimación: ~−70-75 % de entrada en las investigaciones. Sigue haciendo
  falta el gancho del framework entre `:184` y `:193`.
- **Sin gancho nuevo en el framework (indicación del usuario): herencia/override y, si no es virtual, composición.**
  El punto es la herramienta: `LlmTool.InternalExecuteAsync(argumentsJson, PipelineContext, ...)` es `protected abstract`
  (`Framework/LLM/Tools/Base/LlmTool.cs:150`). Cuando se ejecutan las tools del turno n+1, la respuesta n+1 (con su `<nota>`
  sobre el resultado n) ya se procesó y se añadió al historial (`LlmInvocation.cs:501-530`, `:182`). Una subclase de la tool
  en el sample, antes de devolver su resultado, sustituye en la conversación los resultados ya anotados por su nota.
  Condición: el paso sin estado usa su propia conversación, no `context.Conversation` (`StatelessTemplateLlmStep.cs:55-65`),
  así que la subclase del paso de investigación sobrescribe `GetConversationContext` (virtual, `BaseLlmStep.cs:387`) para
  dejarla accesible en el contexto de su rama. Sustituir un mensaje: `Message.Content` es `init` y `MessageHistory` solo tiene
  añadir/quitar (`MessageHistory.cs:31-66`), así que se hace quitando y volviendo a añadir la cola; por comprobar si
  `ConversationContext` ofrece algo mejor. Lo sustituido queda igual en las vueltas siguientes: el prefijo cacheado se mantiene.
  (Descartado el decorador de `ILlmService`: funcionaba, pero rodeaba el punto de extensión natural.)
- **Variante alternativa**: destilar en la herramienta. Un envoltorio de `view_file` en el sample pasa el
  fichero por un modelo barato con las preguntas de la investigación y devuelve solo el extracto con `ruta:línea`. El
  historial solo crece por el final (caché intacta) y no depende de que el modelo principal escriba texto. Riesgo: el
  extracto pierde detalle o citas.

## Sonda con LLM real (2026-10-06, `gpt-5.4`, razonamiento alto, 1,82 USD nominales en total)

Script `probe_notes.py` (scratchpad de la sesión 64a2a19f; Python directo contra Azure, sin la app). Repo `Online`, pregunta
sobre notificaciones, 8 vueltas con tools + 1 final. A = protocolo `<nota r="Rn">` y sustitución de cada resultado ya anotado
por su nota; B = como hoy. Una sola ejecución de cada variante: indicativo, no estadístico.

- **El modelo escribe `<nota>` junto a las tool calls cuando se le pide**: 8 de 8 vueltas, 3 de 3 resultados anotados en cada
  una, también los «sin interés». El 0/194 del log era su comportamiento por defecto, no un límite.
- **Entrada**: A se mantiene entre 6 y 24 k tokens por llamada (108 k en total); B crece de 0,3 k a 62 k (284 k en total).
- **Caché**: B cachea el 58 % (como la ejecución real, 57 %); A, poco, porque lo que no está en caché son las notas nuevas y los
  resultados nuevos, que son lo único que crece. La llamada final sin tools no tuvo caché en ninguna variante (las tools forman
  parte del prefijo).
- **Coste real estimado** (caché a 0,25 USD/M, sin confirmar): A ≈ 0,49 USD frente a B ≈ 0,54 USD. **Con 8 vueltas casi
  empatan**: lo que A ahorra en entrada lo gasta en salida (notas: 17,7 k tokens frente a 13,4 k). A 13 vueltas y con
  prompts de 150-218 k, como en la ejecución real, la extrapolación da ≈0,85 frente a ≈1,6 USD por investigación (−45 %) [I].
- **Profundidad**: A cita 129 `ruta:línea` distintas en 50 ficheros (final y notas); B, 91 en 11 ficheros. A explora más en
  anchura; B lee pocos ficheros enteros. Falta juzgar la calidad del contenido.

## Abierto (decisión del usuario)

- **Guardarraíl**: cualquier `save_notes` posterior (Opus, Sonnet) o uno que cite la misma ruta (Fable, más estricto).
- **Umbral T** y número de vueltas vivas (1 o 2).
- **Orden**: hacer 0 y 1, medir, y decidir 2-5 con datos; o todo junto.
- **Precio real de la caché** en Azure para `gpt-5.4`.
- Piloto con LLM real: con tope de gasto y OK explícito.
