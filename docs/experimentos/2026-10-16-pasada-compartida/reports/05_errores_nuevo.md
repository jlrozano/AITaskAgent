# Informe de Errores, Bugs y Vulnerabilidades con Criticidad y Soluciones

## Resumen ejecutivo

Se recogen 114 hallazgos confirmados de tipo `bug` o `security`. Hay una vulnerabilidad P0 (lectura arbitraria de ficheros hacia el LLM mediante `@[ruta]`) y siete P1 de corrección o disponibilidad. Dos de los P1 actúan por la misma vía que el P0: REG-008 es una recursión infinita por el mismo pase de includes, y REG-007 hace que el LLM reciba el nombre de la plantilla como prompt. Los demás P1 afectan a la composición de pasos: el DAG se ejecuta como cadena, Loop+Parallel reutiliza cachés, la suspensión HITL de una rama se pierde y la fusión multi-YAML no fusiona.

La mayoría de los P2 son degradaciones silenciosas en APIs públicas que el framework no usa internamente (resiliencia, parsers de tool calls, `Factory<T>`, almacenes de fichero). Aun así, los que sí están en la ruta por defecto (REG-036, REG-063, REG-067, REG-074, REG-077) conviene tratarlos en la misma tanda que los P1.

Las rutas son relativas a la raíz del framework tal como las registra el registro de hallazgos. Las entradas `debt` y las rechazadas quedan fuera de este informe.

## Severidades

| Severidad | Entradas |
|---|---|
| P0 | 1 |
| P1 | 7 |
| P2 | 64 |
| P3 | 42 |
| **Total** | **114** |

---

## P0

### REG-001 — Inclusiones `@[ruta]` expandidas tras la interpolación y sin directorio base (security-injection)

- **Ancla**: `Support/Template/TinyTemplateRendered.cs:109-115`; también `Support/Template/TinyTemplateRendered.cs:57`, `LLM/Support/PromptTemplateResolver.cs:59-60`, `Support/Template/TinyTemplateRendered.cs:253`.
- **Mecanismo**: el pase 3 de includes se aplica sobre el texto ya interpolado. Un valor de `{{ input }}` o `{{ context.x }}` que contenga `@[ruta]` inyecta el fichero en el prompt. Desde el camino de DI (`PromptTemplateResolver` sin proveedor e `ITemplateRendered`) el `baseDirectory` es `null`, y `ResolveFileInclude` solo restringe cuando no es `null`, así que se aceptan rutas absolutas, relativas al directorio de trabajo y con `..`.
- **Activación**: cualquier paso `TemplateLlmStep`/`YamlLlmStep` sin `ITemplateProvider` cuya plantilla interpole texto del usuario. Basta un `@[appsettings.json]` en la entrada. No hay precondiciones inusuales. Con `FileTemplateProvider` sin motor la base es `folderPath` y el control sí aplica.
- **Consecuencia**: el contenido (secretos, claves, código) viaja al proveedor LLM y el modelo puede devolverlo al usuario.
- **Solución**: (1) no ejecutar el pase de includes sobre valores interpolados: expandir includes solo sobre el texto de plantilla antes de interpolar, o escapar `@[` en los valores. (2) Hacer obligatorio un `baseDirectory`: si es `null`, desactivar los includes o lanzar. (3) Resolver con `Path.GetFullPath` y comprobar que el resultado empieza por la base canónica; rechazar rutas absolutas y segmentos `..`. (4) Opcionalmente, lista blanca de extensiones.

---

## P1

### REG-002 — `ConvertOrThrow` usa la cultura actual para numéricos y fechas (llm-output-parsing)

- **Ancla**: `LLM/Support/LlmHelpers.cs:147-150`; también `:152-155`, `:162-165`, `:137-140`.
- **Mecanismo**: `int/long/double/decimal/DateTime.Parse` sin `CultureInfo`, invocado por `ParseLlmResponseAsync` para todo `ValueType` no clase.
- **Activación**: paso LLM con `TOut` numérico o `DateTime` en un host con cultura `es-ES`. `double.Parse("3.5")` da 35 y `int.Parse("1.000")` da 1000, sin excepción; `DateTime.Parse` invierte día y mes según la cultura.
- **Consecuencia**: valor erróneo que pasa la validación estructural.
- **Solución**: usar `CultureInfo.InvariantCulture` con `NumberStyles` explícitos en todos los `Parse`, y `DateTime.Parse` con `DateTimeStyles.RoundtripKind` (o `ParseExact` ISO 8601).

### REG-003 — `CloneForBranch` descarta la ruta actual: ramas paralelas en un `LoopStep` reutilizan caché (pipeline-flow)

- **Ancla**: `Core/Models/PipelineContext.cs:153`; también `:140`.
- **Mecanismo**: el clon fija `_currentPath = ""` y comparte `StepResults`. `ParallelStep` añade solo el nombre de la rama, así que las claves son `Rama/Rama` sin el prefijo del padre. Dentro de un `LoopStep` la clave es idéntica en todas las iteraciones y `ExecuteStepAsync` devuelve el resultado cacheado de la primera (`Core/Execution/Pipeline.cs:370` y `394-398`).
- **Activación**: `Loop` que contiene `Parallel`, o dos `ParallelStep` en subárboles hermanos con ramas homónimas.
- **Consecuencia**: desde la iteración 2 las ramas no se ejecutan y devuelven datos obsoletos, sin error.
- **Solución**: que `CloneForBranch` copie la ruta actual del padre (`_currentPath = _currentPath`) para que las claves de rama queden prefijadas por la ruta completa, incluida la iteración. Añadir una prueba Loop+Parallel con 3 iteraciones.

### REG-004 — `ParallelStep` no propaga la suspensión (HITL) de una rama (pipeline-flow)

- **Ancla**: `Core/Steps/ParallelStep.cs:114`; también `:145-148`.
- **Mecanismo**: el sub-pipeline guarda el estado y emite `PipelineSuspendedEvent` con `IsBranchSuspension`, pero `ParallelStep` solo lo refleja en el `Success` del evento y devuelve un `ParallelResult` sin `SuspendInfo` ni error. El padre lo cachea como completado (`Core/Execution/Pipeline.cs:346`).
- **Activación**: HITL dentro de una rama de un `ParallelStep`.
- **Consecuencia**: el pipeline termina con `Success=true`; al reanudar, el `ParallelStep` es acierto de caché y la rama suspendida no se vuelve a ejecutar. La suspensión es irrecuperable.
- **Solución**: si alguna rama devuelve `SuspendInfo`, `ParallelStep` debe devolver un resultado suspendido que lo propague (agregando las ramas suspendidas), sin cachearlo como completado. En la reanudación, re-ejecutar solo las ramas suspendidas y reutilizar las completadas.

### REG-005 — La fusión multi-fichero con `---` no fusiona los pasos (pipeline-flow)

- **Ancla**: `YAML/Execution/YamlPipelineFactory.cs:66`; también `:74`.
- **Mecanismo**: `CreateFromFolderAsync` concatena todos los `*.yaml` con `---` y llama a `Deserialize<YamlPipelineDocument>(string)`, que materializa un único documento. Con dos o más ficheros YamlDotNet lanza al encontrar el segundo `DocumentStart` o descarta los siguientes; nunca se fusionan las listas `Steps`. El registro marca el modo exacto de fallo como inferido (no se leyó YamlDotNet).
- **Activación**: carpeta con varios `*.yaml`, que es el caso documentado (responsabilidad 1, `YAML/Execution/YamlPipelineFactory.cs:45`).
- **Consecuencia**: excepción al construir o pipeline incompleto.
- **Solución**: deserializar cada fichero a su propio `YamlPipelineDocument` y concatenar `Steps` (validando ids duplicados), o recorrer el stream con el parser multi-documento de YamlDotNet y fusionar explícitamente.

### REG-006 — El DAG se valida por aristas pero se ejecuta como cadena lineal (pipeline-flow)

- **Ancla**: `YAML/Execution/YamlPipelineFactory.cs:200`; también `Core/Execution/Pipeline.cs:262`.
- **Mecanismo**: `ValidateAndOrder` solo comprueba `OutputSchema(dep) == InputSchema(step)` y devuelve el orden topológico; `Pipeline` pasa `currentResult` de cada paso al siguiente de la lista.
- **Activación**: DAG YAML con ramas (A→B, A→C) o varias raíces. C recibe el resultado de B; la segunda raíz recibe el de la primera.
- **Consecuencia**: el dato equivocado entra al prompt en silencio.
- **Solución**: resolver la entrada de cada paso desde el resultado de sus dependencias declaradas (lookup por id en `StepResults`, con fusión explícita si hay varias). Si no se implementa, rechazar en validación cualquier DAG que no sea una cadena.

### REG-007 — Nombres de plantilla por defecto terminan en `.md` y el proveedor añade otro `.md` (templating-prompts)

- **Ancla**: `LLM/Steps/TemplateLlmStep.cs:36-37`; también `:37`, `Support/Template/FileTemplateProvider.cs:81`.
- **Mecanismo**: los defaults `{name}_prompt.md` / `{name}_system.md` ya llevan extensión y `FileTemplateProvider` busca `X_prompt.md.md`, que nunca existe. Sin proveedor se renderiza el literal.
- **Activación**: constructor de `TemplateLlmStep` con nombres por defecto. El system por defecto se usa siempre que el rol no traiga `SystemPrompt`. El registro deja pendiente confirmar los nombres reales de los samples.
- **Consecuencia**: el LLM recibe el nombre del fichero como prompt, sin error.
- **Solución**: que los defaults sean `{name}_prompt` y `{name}_system`, o que el proveedor no añada `.md` si el nombre ya lo trae. Combinar con la corrección de REG-080 para que una plantilla ausente falle en lugar de degradar a literal.

### REG-008 — Includes recursivos sin límite de profundidad ni detección de ciclos (templating-prompts)

- **Ancla**: `Support/Template/TinyTemplateRendered.cs:268-270`.
- **Mecanismo**: `ResolveFileInclude` llama a `RenderInternal` sobre el fichero incluido sin límite de profundidad (la guarda de la línea 61 mira el tamaño del fichero, no la profundidad). Una autoinclusión recursa hasta `StackOverflow`. Como el incluido se reinterpola con el mismo modelo, un `@[plantilla.md]` en la entrada, con una plantilla que contiene `{{ input }}`, recursa sin fin incluso con la base restringida de `FileTemplateProvider`.
- **Activación**: entrada no confiable con `@[plantilla.md]` (mismo pase 3 que REG-001). Si solo la activara el autor de la plantilla sería P2, según el propio registro.
- **Consecuencia**: caída del proceso (el `StackOverflow` no es capturable).
- **Solución**: parámetro de profundidad con tope bajo (p. ej. 5-8) y conjunto de rutas en expansión para detectar ciclos; lanzar una excepción controlada. Al cerrar REG-001 (no expandir sobre valores interpolados) se elimina la vía de entrada de usuario.

---

## P2

### Concurrencia y estado compartido

- **REG-010** (`Core/Execution/PipelineMiddlewareRegistry.cs:57-61`). `GetMiddlewares` devuelve la lista interna mutable sin copia ni lock. Con `Register`/`Clear` concurrentes con ejecuciones (típico en tests en paralelo), el enumerador lanza `InvalidOperationException`; además un `cast` a `List` permite mutar el registro global. Solución: devolver una copia inmutable (`ToArray`) bajo lock, o usar un array copy-on-write.
- **REG-011** (`LLM/Steps/BaseLlmStep.LlmInvocation.cs:227`; instancias en `:230`, `:233`, `LLM/Steps/StatelessTemplateLlmStep.cs:29-30`, `:59-60`, `Core/Steps/SwitchStep.cs:24`, `:58`). Estado por ejecución en campos de instancia de pasos reutilizables: detección de bucles de tool calls, conversación activa/restaurada y `_outType`. Con ejecuciones concurrentes de la misma instancia o HITL se pisan entre sí (conversación guardada de otra ejecución, `OutputType` variable). Solución: mover ese estado a un objeto por ejecución (`InvocationState` o el contexto) y no a campos del paso.
- **REG-012** (`Resilience/CircuitBreaker.cs:57`). `EnsureCircuitClosed` solo actúa sobre `Open`; en `HalfOpen` pasan todas las llamadas concurrentes como sonda. Si el servicio sigue caído, todas fallan. Solución: en `HalfOpen` admitir una sola llamada de prueba (`Interlocked`/semáforo) y rechazar el resto. API pública sin uso interno.
- **REG-013** (`Resilience/RateLimiter.cs:63`; también `:161`). `Release(_availableTokens - oldTokens)` en cada recarga sin comprobar esperas: los permisos se acumulan, `WaitAsync` consume permisos viejos y gira, y el contador puede llegar a `int.MaxValue` con `SemaphoreFullException` desde el callback del timer. Solución: liberar como máximo el número de esperas pendientes, o sustituir por `System.Threading.RateLimiting.TokenBucketRateLimiter`.
- **REG-014** (`Support/Caching/MemoryCacheService.cs:61-68`). `GetOrCreateAsync` no es atómico: `TryGetValue`, `await factory()` y `Set` sin lock. Llamadas concurrentes con la misma clave ejecutan la factory varias veces (coste LLM duplicado) y un `null` se almacena como `T`. Solución: `Lazy<Task<T>>` o `SemaphoreSlim` por clave, y no cachear `null`.
- **REG-015** (`YAML/Compilation/SchemaCompiler.cs:45-46`; también `:30`, `:185`). Compilación concurrente o de esquemas homónimos en subcarpetas: dos ensamblados con el mismo nombre en `AssemblyLoadContext.Default`; el segundo puede fallar o sustituir al primero. Solución: nombre de ensamblado único (sufijo hash/guid), exclusión por clave y detección de duplicados con error explícito.

### Configuración y DI

- **REG-016** (`Configuration/ConfigurationPatchExtensions.cs:75-78`). Una clave con cadena vacía se convierte en `null` y, con `NullValueHandling.Ignore`, no sobrescribe un default no vacío. Activación: configuración en blanco para anular un default. Solución: distinguir `Value == null` de `Value == ""` y devolver `JValue("")` para el segundo.
- **REG-017** (`Configuration/ConfigurationPatchExtensions.cs:101-112`). Secciones con todas las claves numéricas se convierten siempre en array: un diccionario con claves numéricas pierde claves o se compacta. Solución: tratar como array solo si los índices son contiguos desde 0; en otro caso, objeto.
- **REG-018** (`Configuration/ConfigurationPatchExtensions.cs:141-143`; también `:136`, `:151-152`). La coerción de primitivos altera cadenas con aspecto de número o booleano (`007`→`7`, `1,000`→`1000`, `1e5`→`100000`, `true`→`True`) en propiedades `string` como `ApiKey`, `Model` o `ApiVersion`, vía `BindEx`/`GetEx` por defecto. Solución: no coaccionar; dejar que Newtonsoft convierta según el tipo destino, o mantener el literal como cadena.
- **REG-019** (`Configuration/DiContractResolver.cs:18-20`). `DefaultCreator = ActivatorUtilities.CreateInstance` para toda clase salvo `string`: un tipo con constructor que pide datos del JSON (record posicional) o no resoluble falla. Solo se activa si se pasa `sp`, y el único camino es `ConfigureEx`, que el framework no invoca. Solución: limitar el creador a tipos registrados en DI o a un `CanConvert` explícito.
- **REG-020** (`Configuration/ServiceCollectionExtensions.cs:162`). Sin coincidencia exacta se acepta el primer servicio cuyo `Name` contiene el `ServiceType`; con varias coincidencias manda el orden de registro, y puede enviarse a otro proveedor. Solución: exigir coincidencia exacta y fallar con mensaje si no existe o es ambigua.
- **REG-021** (`Core/Abstractions/Factory.cs:14-15`; también `:38`). `BuildCache` lanza `ArgumentException` con claves duplicadas o vacías, instancia todos los servicios (incluso transient) solo para leer el nombre, y `_cache ??= BuildCache()` no es seguro ante concurrencia. `Factory<T>` no se registra ni usa en el framework. Solución: validar claves con mensaje, inicializar con `Lazy<T>` y no instanciar para leer metadatos.
- **REG-022** (`Core/Abstractions/Factory.cs:47`). En la rama `Scoped` el predicado `keyName == name` compara el nombre de la propiedad clave con el nombre pedido, no con el valor del servicio: `First` lanza casi siempre. Solución: comparar el nombre del servicio con `name`.
- **REG-024** (`Core/Steps/StepManager.cs:76`). `Assembly.GetTypes()` lanza `ReflectionTypeLoadException` si un tipo no carga y aborta todo el registro. Solución: capturar la excepción y continuar con los `ex.Types` no nulos, registrando un warning.
- **REG-025** (`LLM/Conversation/Context/ConversationContext.cs:14`; también `Core/Models/PipelineContext.cs:83`, `:292`, `Configuration/ConversationOptions.cs:9`). `ConversationOptions` se aplica solo en parte (`KeepFirstNMessages`, `UseBookmarks`), el contexto por defecto se crea con `new()` sin opciones y los defaults difieren (4000 frente a 8192). Activación: configurar `AITaskAgentOptions.Conversation`. Solución: crear los contextos desde las opciones inyectadas y unificar los defaults.
- **REG-026** (`LLM/Steps/BaseLlmStep.cs:587`). `JsonSupport` se lee solo del proveedor: un rol con `Inference.JsonSupport=true` sobre un proveedor sin él, o un fallback con inferencia propia, no obtiene respuesta estructurada. Solución: resolver con la misma precedencia rol→proveedor que `GetInference`.
- **REG-027** (`LLM/Steps/TemplateLlmStep.cs:39`). Si `tools` es `null` el constructor reasigna `Tools = []` y descarta las herramientas del rol. Solución: usar `role.Tools` cuando no se pasa lista, y fallar si un nombre no está registrado (ver REG-028).

### Parsing de salida del LLM y streaming

- **REG-040** (`LLM/Steps/BaseLlmStep.LlmInvocation.cs:709`; también `:743`). En streaming, un flujo sin finish reason o sin datos se considera completo (finish por defecto `Stop`, candidato vacío fabricado). En batch, sin candidatos es `Failure`. Solución: tratar la ausencia de finish reason y de chunks como fallo retriable, igual que en batch.
- **REG-041** (`LLM/Streaming/StreamingTagParser.cs:131`; también `:384`). Una etiqueta autocerrada de un handler conocido deja el parser en `InTag`: el resto del texto se retiene hasta `FlushAsync`, con `TagStartedEvent` seguido de un `TagCompletedEvent` fallido. Solución: detectar `/>` en `ParseOpeningTag` y completar la etiqueta sin cuerpo.
- **REG-042** (`LLM/Streaming/StreamingTagParser.cs:159-162`; también `:108`). El buffer de posible etiqueta retiene prosa sin límite (`a<b y después…`, `<x dice "hola`) hasta que aparece un carácter fuera del conjunto o termina la respuesta. Solución: tope de tamaño y abandono del buffering si el prefijo no coincide con ningún handler registrado.
- **REG-043** (`LLM/Streaming/StreamingTagParser.cs:202-205`). Un espacio antes de `>` en la etiqueta de cierre descarta el cierre: la etiqueta queda abierta, el handler no se ejecuta y la acción se pierde. Solución: tolerar espacios en blanco entre el nombre y `>` del cierre.
- **REG-044** (`LLM/Support/LlmHelpers.cs:27-33`). `CleanJsonResponse` corta del primer `{`/`[` al último `}`/`]`; prosa con corchetes rompe el parseo y cuesta reintentos. Solución: extraer con un escáner de balanceo que respete cadenas, probando candidatos en orden.
- **REG-045** (`LLM/Support/ToolCallXmlParser.cs:34`). `GlmArgRegex` usa `[^<\n\r]+` y exige `<arg_value>` justo tras la clave: valores multilínea o con `<` se truncan y un `</arg_key>` explícito cae al fallback `{content: crudo}`. Sin llamadores internos. Solución: regex tolerante a cierres explícitos y valores multilínea (hasta `</arg_value>`).
- **REG-046** (`LLM/Support/ToolCallXmlParser.cs:120-125`). Solo se procesa el primer `<tool_call>{JSON}</tool_call>` (`Regex.Match`). Solución: `Regex.Matches` y procesar todos. Sin llamadores internos.
- **REG-047** (`LLM/Support/ToolCallXmlParser.cs:166`; también `:157`). `Value<string>()` sobre `name`/`tool_name` no primitivo lanza `InvalidCastException`, capturada con `catch` vacío: se descartan también las llamadas válidas del array. Solución: validar `JTokenType.String` por entrada y registrar log al descartar.
- **REG-048** (`Support/JSON/JsonResponseParser.cs:21-26`; también `:35`). Con `MissingMemberHandling.Ignore`, un objeto JSON válido con claves ajenas se deserializa a un `T` vacío y `Parse` no prueba las demás estrategias. Solución: tras deserializar, exigir al menos una propiedad coincidente o validar contra el esquema antes de aceptar.

### Manejo de errores y validación

- **REG-032** (`Core/Execution/Middlewares/RetryMiddleware.cs:27`; también `:160-162`). Con `MaxRetries <= 0` el bucle no itera, el paso no se ejecuta y se devuelve «Validation failed after N attempts». Solución: validar `MaxRetries >= 0` y ejecutar siempre al menos una vez (`attempt <= MaxRetries` con intentos = MaxRetries+1, o `Math.Max(1, …)`).
- **REG-033** (`Core/StepResults/StepResult.cs:68`; también `Core/StepResults/ParallelResult.cs:51-52`). `GetParameters` enumera el indexador `Item` de `ParallelResult` y `GetValue(this)` lanza `TargetParameterCountException`. Sin llamadores internos. Solución: filtrar `GetIndexParameters().Length > 0`.
- **REG-034** (`Core/Steps/DefaultStepActivator.cs:16-22`; también `:55`, `:65`). Se traga cualquier excepción de `ActivatorUtilities`, se reintenta por reflexión (el constructor que lanzó se invoca dos veces) y se propaga `TargetInvocationException` sin desenvolver. Solución: capturar solo los fallos de resolución, desenvolver `TargetInvocationException` y no reintentar si el constructor lanzó.
- **REG-035** (`Core/Steps/DefaultStepActivator.cs:64-66`). Sin constructor satisfacible se devuelve `GetUninitializedObject`: instancia sin inicializar que falla después con NRE. Solución: lanzar `InvalidOperationException` con el tipo y los parámetros no resueltos.
- **REG-036** (`LLM/Steps/BaseLlmStep.LlmInvocation.cs:64-71`). El `catch` envuelve cualquier excepción (handlers de tags, cálculo de coste, eventos) en `LlmProviderException` con `isRetriable=true`: reintentos y saltos de fallback inútiles, con efectos ya ejecutados repetidos. Solución: envolver solo errores de la llamada al proveedor y dejar propagar las demás con su tipo.
- **REG-037** (`Support/JSON/JsonSchemaValidator.cs:24`). `TryGetProperty` lanza `InvalidOperationException` si la raíz no es objeto y solo se captura `JsonException`. Solución: comprobar `ValueKind == Object` y devolver la tupla de error.
- **REG-038** (`YAML/Compilation/SchemaCompiler.cs:94`; también `:95`). La validación JS toma como válido todo retorno no `bool` y devuelve siempre error `null`. Solución: tratar un retorno no `bool` como fallo (o interpretar cadena como mensaje) y propagar el motivo.
- **REG-039** (`YAML/Execution/YamlPipelineFactory.cs:109`; también `:132`). La fábrica de objetos cae a `Activator.CreateInstance` ante cualquier excepción de DI y `SafeGetTypes` descarta todos los tipos de un ensamblado con `ReflectionTypeLoadException`. Solución: registrar la causa, filtrar la excepción a los fallos de resolución y conservar `ex.Types` cargables.

### Observabilidad, eventos y ciclo de vida

- **REG-050** (`Core/Execution/Pipeline.cs:109`). Los `subscribers` solo se usan en `PipelineContextFactory.Create`, que se invoca solo si `context` es `null`: con contexto aportado (p. ej. reanudación) se descartan sin aviso. Solución: suscribirlos también al contexto recibido o lanzar si se pasan ambos.
- **REG-051** (`Core/Execution/Pipeline.cs:216`; también `:215`). El `finally` dispone el `EventChannel` aportado por el llamador: en una segunda ejecución o reanudación con el mismo contexto no se entregan eventos a nadie. Solución: disponer solo el canal que `Pipeline` creó, y que `DisposeAsync` deje estado `disposed`.
- **REG-052** (`Core/Steps/StepBase.cs:53`; también `:61`). `Logger` usa `_logger ??= NullLogger.Instance` e `InitializeLogger` solo asigna si es `null`: cualquier acceso previo fija `NullLogger` para siempre y un paso compartido conserva el logger del primer pipeline. Solución: no autoasignar en el getter y permitir reinicializar por ejecución.
- **REG-053** (`Observability/EventChannel.cs:252`; también `:144`). `DisposeAsync` espera `Task.WhenAll` sin timeout y los handlers reciben `CancellationToken.None`: un handler que no termina cuelga `ExecuteAsync` desde el `finally`. Solución: timeout en la espera de dispose y token de cancelación al disponer.
- **REG-054** (`Observability/Events/IStepCompletedEvent.cs:8`). `AdditionalData => []` crea un diccionario nuevo en cada lectura: las escrituras se pierden sin error. Solución: propiedad abstracta o miembro de interfaz sin implementación por defecto, o respaldada en las clases base.
- **REG-063** (`Core/Execution/Pipeline.cs:134`). El `timeoutCts` no se dispone (solo `linkedCts` en el `finally`): cada ejecución deja un temporizador vivo hasta 60 minutos. Retención acotada, no fuga ilimitada. Solución: `using`/`Dispose` de `timeoutCts` en el `finally`.
- **REG-064** (`Observability/EventChannel.cs:159-163`). `_backgroundLoops` acumula una tarea por cada `Subscribe` sobre el singleton durante toda la vida del canal; el camino del framework no lo activa (usa canal local). El registro advierte que subiría a P1 si un host suscribe por ejecución en un proceso de larga vida. Solución: retirar la tarea al completarse el bucle o al desuscribirse.
- **REG-065** (`Resilience/RateLimiter.cs:180-181`). `Dispose` libera el semáforo antes de detener el timer; un callback en curso puede lanzar `ObjectDisposedException` en un hilo del pool sin capturar y terminar el proceso. Solución: detener el timer esperando el callback (`Dispose(WaitHandle)`) antes de liberar el semáforo, y proteger el callback con `try/catch`. API sin uso interno.

### Persistencia

- **REG-055** (`LLM/Conversation/Storage/FileConversationStorage.cs:51`; también `YamlFileConversationStorage.cs:79`, `:51`). `File.WriteAllTextAsync` directo sobre el destino y read-modify-write sin bloqueo: un fallo a mitad deja el fichero truncado y dos guardados del mismo id se pisan. Solución: escribir a fichero temporal y `File.Move` con sobrescritura, más lock por id.
- **REG-056** (`LLM/Conversation/Storage/FileConversationStorage.cs:129`; también `YamlFileConversationStorage.cs:153`, `FileConversationStorage.cs:117`). La sanitización sustituye caracteres inválidos por `_`, así que `a/b` y `a_b` comparten fichero, y `ListConversationsAsync` devuelve nombres de fichero y no ids. El almacén YAML no comprueba contención de ruta. Solución: codificación reversible del id (hex/base64url) y la misma comprobación de ruta en ambos almacenes.
- **REG-057** (`LLM/Conversation/Storage/YamlFileConversationStorage.cs:105-109`; también `:176`, `:213`, `:111`). Ida y vuelta con pérdida: `Name`, `Message.Metadata`, `Native`, metadatos y `MaxTokens`. El formato legacy no tiene dónde guardarlos. Solución: ampliar el formato con campos opcionales versionados, o documentar la pérdida y desaconsejar el almacén para estado completo.
- **REG-058** (`LLM/Conversation/Storage/YamlFileConversationStorage.cs:129-135`; también `LLM/Conversation/Context/ConversationContext.cs:152`). `AddMessage` fija `LastActivityAt = UtcNow` tras cargar, así que el valor cargado es la hora de carga y el siguiente guardado la escribe como `completedAt`. Solución: restaurar `LastActivityAt` después de añadir los mensajes.

### Flujo del pipeline

- **REG-059** (`Core/Execution/Pipeline.cs:315`; también `:332`, `Core/Steps/LoopStep.cs:63`). `AddPathPart`/`RemovePathPart` sin `try/finally`: si la recursión lanza, la ruta queda desplazada en el contexto compartido y altera las claves de caché de ejecuciones posteriores. Solución: `try/finally` en ambos sitios.
- **REG-060** (`Core/Execution/Pipeline.cs:334-337`; también `:346`). Si los `NextSteps` quedan suspendidos se retorna sin cachear al padre; al reanudar el padre se re-ejecuta (llamada LLM duplicada, y reenrutado distinto con un padre no determinista). Solución: cachear el resultado del padre antes de ejecutar los hijos.
- **REG-061** (`Core/Steps/GroupStep.cs:47-49`). `ParallelStep` pasa el mismo `input` a todas las ramas; con dos o más ramas `GroupStep` comparten y mutan (`Clear`/`AddRange`) la misma `List`, con acceso concurrente: una rama puede ejecutar los pasos de la otra y la otra ninguno. Solución: copiar la lista de `NextSteps` por rama (o clonar el input) antes de la ejecución.

### Reintentos, timeout y cancelación

- **REG-067** (`Core/Execution/Middlewares/RetryMiddleware.cs:50-66`; también `Core/Execution/Middlewares/TimeoutMiddleware.cs:45`, `Core/Execution/Pipeline.cs:406`). `RetryMiddleware` captura cualquier excepción, incluida `OperationCanceledException`, y devuelve `ErrorStepResult`: el manejo de `TimeoutMiddleware` es inalcanzable y no se emite `StepTimeoutEvent`. Solución: relanzar la cancelación (`catch when (!ct.IsCancellationRequested)`) o reordenar los middlewares para que el timeout sea el más externo.
- **REG-068** (`LLM/Steps/BaseLlmStep.LlmInvocation.cs:508`; también `:638`). Los handlers de tags ejecutan acciones dentro de `InvokeLlmAsync`, antes de parsear y validar; un reintento o salto de fallback las repite y el restore del bookmark borra del historial la referencia a la acción hecha. Solución: diferir los efectos hasta validar la respuesta, o exigir handlers idempotentes y registrar las acciones ya ejecutadas.
- **REG-069** (`LLM/Streaming/StreamingTagParser.cs:335-340`). Se relanza toda `OperationCanceledException` sin comprobar `IsCancellationRequested`: un timeout interno del handler (`HttpClient`) aborta el turno. Solución: `catch when (cancellationToken.IsCancellationRequested)` y tratar las demás como error del handler.
- **REG-070** (`Resilience/CircuitBreaker.cs:46`). `catch (Exception)` cuenta la cancelación como fallo y puede abrir el circuito. Solución: excluir `OperationCanceledException` con el token solicitado del conteo. API sin uso interno.

### Esquemas y serialización

- **REG-071** (`Core/Execution/StepResultFactory.cs:223-226`). `BuildJsonSchema` devuelve `null` ante cualquier excepción sin registrar y se cachea por tipo: el LLM pierde el esquema de forma permanente y silenciosa. Solución: registrar la excepción y no cachear el fallo, o propagar un error de configuración.
- **REG-072** (`Core/Execution/StepResultFactory.cs:278`). `properties.Add` lanza `ArgumentException` con nombres JSON duplicados (propiedad oculta con `new`, `[JsonProperty]` coincidente) y el error se pierde en el `catch` de REG-071. Solución: usar el indexador o resolver la duplicidad explícitamente, y registrar el caso.
- **REG-073** (`Core/Execution/StepResultFactory.cs:355-359`). No se mapean `byte`, `short`, `uint`, `Guid`, `TimeSpan`, structs ni `IReadOnlyList`/`HashSet`/`IList`: esquema vacío; y `Uri` se expande como objeto. Proveedores en modo strict pueden rechazarlo. Solución: ampliar el mapeo (string con formato para `Guid`/`TimeSpan`, array para colecciones) y tratar BCL conocidos como primitivos.
- **REG-074** (`Core/Execution/StepResultFactory.cs:517`; también `:522`). `[Range]` solo fija `Minimum`/`Maximum` si `Type == Number`, así que se pierde en `int`/`long` (`Integer`). Solución: aplicar también a `Integer`.
- **REG-075** (`Core/Steps/StepManager.cs:140-147`; también `:58`). La caché de descripciones se indexa por tag con herencia: un derivado sin `[StepTag]` propio devuelve la descripción cacheada de la base, y dos tipos sin atributo con el mismo `Name` colisionan; registro y descripción leen el atributo con `inherit` distinto. Solución: indexar por `Type` y usar el mismo criterio de herencia en ambos.

### Seguridad (P2)

- **REG-077** (`LLM/Steps/BaseLlmStep.LlmInvocation.cs:432`; también `:486`). Nombre y argumentos completos de cada herramienta se registran a nivel `Information` (por defecto) y el system prompt, historial y respuestas a nivel `Trace`. Exposición a sinks de log, no explotable directamente. Solución: registrar solo nombre y tamaño a `Information`; los argumentos tras un flag explícito con redacción de campos sensibles.
- **REG-078** (`LLM/Support/ToolCallXmlParser.cs:66`; también `:128`). Cualquier tag o JSON cuyo nombre coincida con una herramienta se trata como tool call, también si procede de texto del usuario o de contenido recuperado (inyección de tool calls). Sin llamadores internos: depende del consumidor externo. Solución: aceptar tool calls solo en los mensajes del asistente en las posiciones esperadas, y exigir confirmación para herramientas con efectos.
- **REG-079** (`YAML/Compilation/SchemaCompiler.cs:87`). El escapado de la función JS embebida en código C# no trata la barra invertida: `\d` rompe la compilación Roslyn y barra+comilla cierra el literal C#. El parámetro es código ejecutable por diseño y ningún llamador interno pasa `jsValidateFunction`. Solución: emitir el JS como literal C# verbatim o con escapado completo (`SymbolDisplay.FormatLiteral`).

### Plantillas

- **REG-080** (`LLM/Support/PromptTemplateResolver.cs:55-56`; también `Support/Template/FileTemplateProvider.cs:149`). Con proveedor y `validateTemplates=false` por defecto, una plantilla ausente devuelve `null` y el resolvedor usa `rawPrompt` sin interpolar: el nombre de la plantilla llega al LLM como prompt. Solución: lanzar si la plantilla referenciada por nombre no existe, o activar `validateTemplates` por defecto.

---

## P3

Las soluciones indicadas son la corrección directa que sugiere cada título; el registro no recoge más detalle para estas entradas.

| ID | Categoría | Problema | Ancla | Solución sugerida |
|---|---|---|---|---|
| REG-082 | concurrency-state | `ValidateAsync` bloquea con `.Result` sobre la validación base | `LLM/Results/LlmIntentionResult.cs:14` | `await` en lugar de `.Result` |
| REG-083 | concurrency-state | Canales con `SingleWriter = true` escritos desde `SendAsync` concurrente | `Observability/EventChannel.cs:128-130` | `SingleWriter = false` |
| REG-084 | concurrency-state | La recarga descarta el tiempo fraccionario y puede saltarse un tick | `Resilience/RateLimiter.cs:146` | Acumular el resto fraccionario |
| REG-085 | configuration-di | `GetEx` exige tipo exacto de `JToken`; falla con `JContainer` o sección vacía | `Configuration/ConfigurationPatchExtensions.cs:35-38` | Aceptar `JContainer` y secciones vacías |
| REG-088 | configuration-di | `LlmInferenceSettings.Get` lanza al leer una opción de tipo incorrecto al construir la request | `LLM/Configuration/LlmInferenceSettings.cs:145` | Conversión segura con valor por defecto y warning |
| REG-092 | conversation-history | `GetMessagesForRequest` ignora `fromBookmark` si `UseBookmarks` es `false` | `LLM/Conversation/Context/ConversationContext.cs:192` | Lanzar o avisar cuando se pide bookmark sin soporte |
| REG-093 | conversation-history | `Clone` no es profundo: mensajes y colecciones compartidos entre ramas | `LLM/Conversation/Context/ConversationContext.cs:224` | Clonar mensajes y colecciones |
| REG-094 | conversation-history | Marcadores desfasados al eliminar mensajes y obsoletos tras restaurar | `LLM/Conversation/MessageHistory.cs:63-64` | Reajustar índices al eliminar y reiniciar al restaurar |
| REG-095 | conversation-history | `FinalizeAsync` elimina el último mensaje sin comprobar su contenido | `LLM/Steps/IntentionAnalyzerStep.cs:90` | Comprobar rol y contenido antes de eliminar |
| REG-096 | conversation-history | Con `input.Value` nulo se serializa el `IStepResult` completo como mensaje de usuario | `YAML/Steps/YamlLlmStep.cs:84` | Tratar `null` como entrada vacía o error |
| REG-097 | error-handling-validation | `ExecuteAsync` explícito de `IStep<TIn,TOut>` falla con NRE y mensaje engañoso | `Core/Abstractions/IStep.cs:61-65` | Validar la entrada y lanzar `ArgumentException` |
| REG-099 | error-handling-validation | Validación de valor en `CreateInstance` redundante; permite `null` en tipos valor | `Core/Execution/StepResultFactory.cs:44-45` | Simplificar la condición y rechazar `null` en tipos valor |
| REG-105 | error-handling-validation | La conversión de entrada pierde la excepción original | `Core/Steps/TypedStep.cs:45-54` | Pasar la excepción original como `InnerException` |
| REG-107 | error-handling-validation | La comparación de opción puede lanzar NRE; la validación se omite si el paso no es `IRouterOptions` | `LLM/Results/LlmIntentionResult.cs:22` | Comprobar `null` y fallar si el paso no implementa `IRouterOptions` |
| REG-108 | error-handling-validation | Trabajo que puede lanzar dentro de `finally` enmascara la excepción original de la herramienta | `LLM/Tools/Base/LlmTool.cs:140` | Proteger el trabajo del `finally` con `try/catch` |
| REG-110 | error-handling-validation | Entradas nulas o tipos no soportados escapan de los validadores JSON | `Support/JSON/JsonSchemaValidator.cs:19` | Validar `null` y devolver error en la tupla |
| REG-112 | error-handling-validation | Un YAML vacío produce NRE sin mensaje útil | `YAML/Execution/YamlPipelineFactory.cs:78` | Comprobar `null` y lanzar un error descriptivo |
| REG-120 | llm-output-parsing | La extracción por regex solo prueba el primer objeto y el primer array | `Support/JSON/JsonResponseParser.cs:105-114` | Probar todos los candidatos en orden |
| REG-142 | observability-events | Excepción al emitir el evento de fin dentro del `try` se trata como fallo del paso: métricas duplicadas y segundo evento | `Core/Execution/Middlewares/ObservabilityMiddleware.cs:98` | Mover la emisión fuera del `try` del paso |
| REG-143 | observability-events | `Metrics.StepRetries` cuenta un reintento que no llega a producirse en el último intento | `Core/Execution/Middlewares/RetryMiddleware.cs:88-90` | Incrementar solo si habrá otro intento |
| REG-144 | observability-events | Los `subscribers` de `Create` sustituyen al canal global en vez de añadirse | `Core/Models/PipelineContext.cs:286` | Añadir a los suscriptores globales |
| REG-145 | observability-events | `LoopExhaustedEvent` enviado sin esperar ni observar el resultado | `Core/Steps/LoopStep.cs:86-93` | `await` del envío |
| REG-146 | observability-events | `TokensUsed` y `CostUsd` se sobrescriben con el valor de cada llamada, no el acumulado | `LLM/Steps/BaseLlmStep.cs:562` | Acumular en lugar de asignar |
| REG-150 | observability-events | Excepciones del handler se registran y se tragan sin cancelación posible | `Observability/EventChannel.cs:146-149` | Propagar la cancelación y pasar el token al handler |
| REG-158 | persistence | Cargas y guardados sin tolerancia a ficheros corruptos o incompletos | `LLM/Conversation/Storage/FileConversationStorage.cs:84` | Capturar el error de deserialización y devolver `null` o error explícito |
| REG-159 | persistence | Timestamps legacy por posición pueden heredar el de otro mensaje | `LLM/Conversation/Storage/YamlFileConversationStorage.cs:70-74` | Asignar timestamp por mensaje, no por posición |
| REG-160 | persistence | El `ConversationId` cambia al recargar desde YAML (se elimina el prefijo `session-`) | `LLM/Conversation/Storage/YamlFileConversationStorage.cs:113` | Conservar el id original al cargar |
| REG-161 | persistence | `FormatUtcTimestamp` no convierte a UTC y escribe `Z` sobre fechas locales | `LLM/Conversation/Storage/YamlFileConversationStorage.cs:231` | `ToUniversalTime()` antes de formatear |
| REG-162 | pipeline-flow | `RouterStep` añade el destino dos veces a `NextSteps` | `Core/Execution/Pipeline.cs:370` | Evitar la doble inserción |
| REG-163 | pipeline-flow | `ActionStep` fire-and-forget usa `input` y `context` fuera del ciclo de vida del paso | `Core/Steps/ActionStep.cs:21-31` | Esperar la acción o capturar copias inmutables |
| REG-165 | pipeline-flow | `RouterStep.Options` es una instantánea mientras `Routes` es mutable | `LLM/Steps/RouterStep.cs:26` | Derivar `Options` de `Routes` o inmutabilizar ambos |
| REG-166 | resource-lifecycle | El canal no tiene estado disposed: `Subscribe` tras `DisposeAsync` deja consumidores huérfanos | `Core/Execution/Pipeline.cs:215` | Estado `disposed` que rechace `Subscribe` |
| REG-167 | retry-timeout-cancellation | La cancelación se registra como fallo (`LogError`, estado de error, aviso de reintento) | `Core/Steps/ActionStep.cs:45` | Tratar la cancelación como estado propio sin `LogError` |
| REG-168 | retry-timeout-cancellation | El jitter se aplica después del tope y puede superar `MaxDelay` | `Resilience/RetryPolicy.cs:74-75` | Aplicar el tope tras el jitter |
| REG-169 | retry-timeout-cancellation | El `InvalidOperationException` final de `RetryPolicy` es inalcanzable con `MaxAttempts` positivo | `Resilience/RetryPolicy.cs:80-82` | Eliminar la rama o validar `MaxAttempts` |
| REG-172 | schema-serialization | El resolver filtra solo por tipo declarado: `Exception` en propiedades `object` o colecciones se sigue serializando | `Support/JSON/Serializations.cs:18` | Filtrar por tipo en tiempo de ejecución |
| REG-173 | security-data-exposure | Resultados de herramientas y contenido completo de respuestas LLM en logs Debug | `Configuration/ObservabilityOptions.cs:11` | Redacción o truncado, y flag explícito |
| REG-174 | security-data-exposure | `LlmRequest` transporta `ApiKey` en un record con `ToString` autogenerado | `LLM/Models/LlmRequest.cs:27` | Sobrescribir `PrintMembers` para omitir `ApiKey` |
| REG-175 | security-data-exposure | El mensaje de excepción del handler se inserta en el texto visible y en el historial | `LLM/Streaming/StreamingTagParser.cs:344` | Mensaje genérico en el texto; detalle solo en logs |
| REG-176 | security-injection | El nombre del esquema (nombre de fichero) se usa sin sanear como identificador C# y nombre de ensamblado | `YAML/Compilation/SchemaCompiler.cs:32` | Sanear a identificador válido y validar |
| REG-177 | security-injection | El script JS recibe objetos del host con superficie .NET completa | `YAML/Compilation/SchemaCompiler.cs:90-91` | Pasar DTOs o proxies restringidos |
| REG-178 | templating-prompts | `FormatUserMessage` sustituye `{schema}` también dentro del texto del mensaje de usuario | `LLM/Configuration/LlmPromptTemplates.cs:107-110` | Sustituir solo en la plantilla, no en el contenido del usuario |

---

## Discrepancias propuestas

El cuerpo mantiene las severidades del registro. Propuestas para revisión:

- **REG-007 (P1 → P2 si procede)**. El propio registro deja pendiente confirmar el nombre real de las plantillas de los samples (pregunta abierta 43). Si los samples pasan siempre `promtTemplateString` explícito, el defecto solo afecta a quien use los nombres por defecto, lo que encaja mejor con P2. Hay que comprobarlo antes de decidir.
- **REG-005 (confianza 0.65)**. El modo exacto de fallo es inferido; el no-merge es seguro por el tipo destino. La severidad P1 se sostiene en cualquier caso, pero la solución debe validarse con una prueba contra YamlDotNet.
- **REG-065 (P2 → P1 si se usa)**. La caída del proceso por excepción no capturada en un hilo del pool es de impacto alto. El registro la deja en P2 porque `RateLimiter` no tiene uso interno; si un consumidor lo usa, el argumento para P1 es el mismo que para REG-008.
- **REG-077 (P2 → P3 plausible)**. El registro lo describe como no explotable directamente y dependiente del sink de logs. Su severidad por defecto (argumentos completos a `Information`) lo mantiene en P2, pero comparte patrón con REG-173 (P3).
