# Informe de Deuda Técnica y Hotspots

## Resumen ejecutivo

El registro contiene 68 entradas `debt` confirmadas: 8 P2 y 60 P3, ninguna P0/P1. La deuda no es un problema de correctitud inmediata (los fallos viven en las entradas `bug`), sino de mantenibilidad y de diagnóstico: casi un tercio es higiene de código (22 entradas `maintainability`) y otro tercio es pérdida de trazabilidad o contratos inconsistentes (`error-handling-validation` 10, `observability-events` 9, `llm-output-parsing` 6).

Tres focos concentran el coste real de mantener:

1. **`BaseLlmStep` y su invocación** (REG-049, cc=75): 12 entradas del registro, 5 de ellas deuda. Es el camino de toda llamada LLM.
2. **Estado estático global** (REG-009, REG-081, REG-087): impide tener más de un host por proceso y complica los tests.
3. **Dos generadores de esquema divergentes** (REG-076, REG-170): cada tipo nuevo hay que añadirlo en dos o tres sitios.

El orden de refactor recomendado empieza por lo barato (trazas en `catch`, código muerto, mensajes de error), sigue con unificar el generador de esquemas y cierra con la descomposición de `InvokeLlmAsync`.

## Deuda por categoría

| Categoría | Entradas | P2 | P3 | IDs |
|---|---|---|---|---|
| maintainability | 22 | 1 | 21 | REG-049, REG-121 a REG-141 |
| error-handling-validation | 10 | 0 | 10 | REG-098, REG-100 a REG-104, REG-106, REG-109, REG-111, REG-113 |
| observability-events | 9 | 0 | 9 | REG-147 a REG-149, REG-151 a REG-156 |
| configuration-di | 8 | 3 | 5 | REG-023, REG-028, REG-029, REG-086, REG-087, REG-089 a REG-091 |
| llm-output-parsing | 6 | 0 | 6 | REG-114 a REG-119 |
| templating-prompts | 4 | 0 | 4 | REG-179 a REG-182 |
| schema-serialization | 3 | 1 | 2 | REG-076, REG-170, REG-171 |
| concurrency-state | 2 | 1 | 1 | REG-009, REG-081 |
| resource-lifecycle | 2 | 2 | 0 | REG-062, REG-066 |
| persistence | 1 | 0 | 1 | REG-157 |
| pipeline-flow | 1 | 0 | 1 | REG-164 |
| **Total** | **68** | **8** | **60** | |

Las 8 P2 son REG-009, REG-023, REG-028, REG-029, REG-049, REG-062, REG-066 y REG-076.

## Hotspots

Recuento de entradas confirmadas de cualquier kind (debt, bug, security) por fichero del ancla principal del registro. No se cuentan las ubicaciones secundarias (`instances`).

| Fichero | Total | Debt | Bug | Security | IDs |
|---|---|---|---|---|---|
| Core/Execution/Pipeline.cs | 9 | 2 | 7 | 0 | deuda REG-023, REG-126; bug REG-050, REG-051, REG-059, REG-060, REG-063, REG-162, REG-166 |
| LLM/Support/ToolCallXmlParser.cs | 8 | 4 | 3 | 1 | deuda REG-116 a REG-119; bug REG-045, REG-046, REG-047; security REG-078 |
| YAML/Execution/YamlPipelineFactory.cs | 7 | 3 | 4 | 0 | deuda REG-029, REG-113, REG-141; bug REG-005, REG-006, REG-039, REG-112 |
| LLM/Steps/BaseLlmStep.LlmInvocation.cs | 7 | 2 | 4 | 1 | deuda REG-049, REG-115; bug REG-011, REG-036, REG-040, REG-068; security REG-077 |
| Core/Execution/StepResultFactory.cs | 7 | 2 | 5 | 0 | deuda REG-098, REG-170; bug REG-071, REG-072, REG-073, REG-074, REG-099 |
| Observability/EventChannel.cs | 6 | 2 | 4 | 0 | deuda REG-091, REG-149; bug REG-053, REG-064, REG-083, REG-150 |
| Core/Models/PipelineContext.cs | 5 | 3 | 2 | 0 | deuda REG-101, REG-102, REG-127; bug REG-003, REG-144 |
| Core/Steps/StepManager.cs | 5 | 3 | 2 | 0 | deuda REG-076, REG-081, REG-087; bug REG-024, REG-075 |
| LLM/Steps/BaseLlmStep.cs | 5 | 3 | 2 | 0 | deuda REG-089, REG-090, REG-134; bug REG-026, REG-146 |
| Configuration/ConfigurationPatchExtensions.cs | 5 | 1 | 4 | 0 | deuda REG-121; bug REG-016, REG-017, REG-018, REG-085 |

Lectura por tipo (campo `symbol` del registro, primer segmento): `BaseLlmStep` acumula 12 entradas (5 deuda) repartidas entre `BaseLlmStep.cs` y `BaseLlmStep.LlmInvocation.cs`; `ToolCallXmlParser` 8 (4 deuda); `EventChannel` 8 (3 deuda); `Pipeline` 7 (2 deuda); `YamlPipelineFactory` 6 (2 deuda).

Dónde la deuda coincide con bugs (candidatos a refactor con más retorno):

- **Pipeline.cs**: la reconstrucción de la cadena de middleware por sub-pipeline (REG-126) y los timeouts no desactivables (REG-023) conviven con 7 bugs. Tocar este fichero para arreglar bugs es el momento de limpiar la deuda.
- **BaseLlmStep.LlmInvocation.cs**: `InvokeLlmAsync` (REG-049) es el método donde viven REG-011, REG-036, REG-040, REG-068 y REG-077; la duplicación batch/streaming es la causa que cita el registro para las divergencias.
- **ToolCallXmlParser.cs**: los cuatro caminos duplicados de construcción de `ToolCall` (REG-125) y las cuatro deudas del parser se solapan con los bugs REG-045 a REG-047.
- **StepResultFactory.cs y StepManager.cs**: la doble generación de esquema (REG-076, REG-170) se ancla en ambos.

## Hallazgos por categoría

### maintainability (22)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-049 | P2 | LLM/Steps/BaseLlmStep.LlmInvocation.cs:457-862 | `InvokeLlmAsync` junta batch, streaming, tags, coste y eventos en ~400 líneas (cc=75, fan_out=99); las ramas duplican el escaneo de tags y la construcción de `LlmResult`. | Ya ha producido divergencias batch/streaming (el streaming no valida finish reason ni respuesta vacía, según el registro); difícil de probar por partes. | Extraer la parte común (escaneo de tags, construcción de resultado, emisión de eventos) y dejar batch y streaming como adaptadores finos. |
| REG-121 | P3 | Configuration/ConfigurationPatchExtensions.cs:189-197 | `ConfigureEx` conserva bloques comentados y marcas CAMBIO AQUÍ; las sobrecargas repiten lógica y usan `AddSingleton` sin `TryAdd`. | Registrar dos veces el mismo `T` duplica token de cambio y acciones. | Borrar lo comentado, unificar sobrecargas y pasar a `TryAdd`. |
| REG-122 | P3 | Configuration/ServiceCollectionExtensions.cs:116 | API sin llamadores ni registro en el framework: `AddToolDiscovery`, `InjectSchemaInUserMessage`, subsistema `IStepTracer`, opciones y clases de Resilience, `EventEnrichmentResult<TEvent>`. | Superficie pública que mantener y documentar; opciones con defaults distintos a los de las clases. | Decidir por pieza: eliminar o marcar como API opcional con tests. |
| REG-123 | P3 | Core/Abstractions/IStep.cs:22 | Erratas en miembros públicos: `MiillisecondsBetweenRetries`, `WithoutExeption`. | Cuanto más se tarde, más implementadores rompe el cambio (el registro cita Ca=33 para `IStep`). | Renombrar con alias `[Obsolete]` en una versión mayor. Es cambio de contrato: requiere aprobación. |
| REG-124 | P3 | Core/Execution/Middlewares/ObservabilityMiddleware.cs:13 | Diez comentarios XML describen un comportamiento distinto al del código (orden de middleware, excepciones, tipos y clases inexistentes). | Documentación engañosa para mantenedores. | Reescribir o borrar los comentarios; sin riesgo funcional. |
| REG-125 | P3 | Core/Execution/Middlewares/RetryMiddleware.cs:94-120 | Lógica duplicada casi literal en seis puntos (Retry, StepResultFactory, RateLimiter, EventChannel, ToolCallXmlParser, JsonResponseParser). | Cada arreglo hay que replicarlo. | Extraer helpers; empezar por ToolCallXmlParser y StepResultFactory. |
| REG-126 | P3 | Core/Execution/Pipeline.cs:238 | Cada sub-pipeline reconstruye lista, tres middlewares, tres loggers y la cadena; el delegado terminal no se ejecuta porque `RetryMiddleware` nunca invoca `next`. | Coste repetido por paso compuesto o iteración de bucle; código inalcanzable que confunde. | Construir la cadena una vez y reutilizarla; retirar o corregir el delegado terminal. |
| REG-127 | P3 | Core/Models/PipelineContext.cs:161-162 | `CloneForBranch` escribe `_lastPath` por reflexión sobre una copia `with { }`. | Renombrar el campo produce `NullReferenceException` en ejecución, no al compilar. | Copiar la pila al construir el clon, sin reflexión. |
| REG-128 | P3 | Core/StepResults/StepResult.cs:58 | `GetParameters` reflexiona sin caché en cada llamada y expone todas las propiedades públicas; sin llamadores en el framework. | Sin impacto interno hoy. | Cachear por tipo o retirar si nadie lo usa. |
| REG-129 | P3 | Core/Steps/BreakStep.cs:28 | Dos constructores equivalentes; el de valor por defecto es inalcanzable sin argumentos. | Ruido. | Dejar uno. |
| REG-130 | P3 | Core/Steps/TypedStep.cs:14-15 | `TypedStep` duplica el `Lazy` del activator y oculta con `new` métodos de `StepBase`. | El comportamiento depende del tipo estático de la llamada. | Hacer virtuales los métodos base y quitar el `new`. |
| REG-131 | P3 | LLM/Conversation/MessageHistory.cs:144-146 | `GetMessagesWithSlidingWindow` (cc=15) duplica `GetRecentMessages`; `StreamingTagParser.ProcessChunkAsync` (cc=29, 135 líneas) concentra los cuatro estados. | Difíciles de probar por partes; han acumulado ajustes. | Extraer la integridad de pares tool_calls/tool y separar la máquina de estados por estado. |
| REG-132 | P3 | LLM/Results/InputMessage.cs:4 | `InputMessage` sin namespace; `JsonResponseParser` en `AITaskAgent.JSON` frente a `AITaskAgent.Support.JSON`. | Usings distintos para tipos del mismo directorio. | Alinear namespaces (cambio de contrato menor). |
| REG-133 | P3 | LLM/Results/LlmStepResult.cs:30 | `Value` oculto con `new` y cast `(T?)base.Value` sin comprobar. | Solo falla ante uso incorrecto de la API. | Evitar `new`: acceso tipado explícito. |
| REG-134 | P3 | LLM/Steps/BaseLlmStep.cs:606-614 | Ramas inalcanzables (rama `JsonObject`, nulls de `CreateStep`, catch de `ChannelClosedException`, throw de `RouterStep`); dos con efectos laterales latentes. | Código que parece defensivo y no lo es. | Eliminarlas o convertirlas en aserciones. |
| REG-135 | P3 | Observability/EventSubscription.cs:18 | `DisposeAsync` no es idempotente a diferencia de `HandlerSubscription`. | Inocuo en la práctica por la lambda de baja. | Guarda de idempotencia por coherencia. |
| REG-136 | P3 | Observability/Events/StepCachedHitEvent.cs:9 | Tipos de evento con literales, constantes locales y `AITaskAgentConstants.EventTypes`: tres mecanismos. | Renombrar o buscar un tipo exige revisar cada fichero. | Centralizar en `AITaskAgentConstants.EventTypes`. |
| REG-137 | P3 | Observability/Events/StepProgressEvent.cs:21 | Redeclara propiedades comunes en vez de heredar de `ProgressEventBase`. | Un cambio en la base no se propaga. | Heredar de la base. |
| REG-138 | P3 | Observability/Events/StepValidationEvent.cs:20 | `ValidationType` es string libre pese a dos valores documentados. | Valores no contemplados sin error de compilación. | Enum o constantes. |
| REG-139 | P3 | Support/Caching/ICacheService.cs:11 | `Get<T>` devuelve `default` para tipos valor: fallo indistinguible de valor almacenado. | Contrato ambiguo; sin consumidor afectado visible. | Añadir `TryGet` al contrato. |
| REG-140 | P3 | Support/Template/FileTemplateProvider.cs:67 | `GetTemplate` lee el fichero dentro de `_cacheLock`. | Serializa peticiones durante la lectura de disco; menor. | Lectura fuera del lock. |
| REG-141 | P3 | YAML/Execution/YamlPipelineFactory.cs:113 | `BuildDeserializer` recorre todos los tipos de todos los ensamblados en cada `CreateFromYaml`. | Coste de reflexión por construcción de pipeline; tags duplicados dependen del orden. | Cachear el mapa tag a tipo y detectar duplicados. |

### error-handling-validation (10)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-098 | P3 | Core/Execution/StepResultFactory.cs:36-37 | El error de constructor ausente cita `ValueType.Name` y no el tipo de resultado. | Diagnóstico al tipo equivocado. | Corregir el mensaje. |
| REG-100 | P3 | Core/Execution/StepValidation.cs:22 | Los nombres repetidos entre subárboles hermanos no se detectan; no colisionan en caché porque la clave incluye la ruta. | Validador inconsistente sin fallo observable. | Documentar el alcance o validar globalmente. |
| REG-101 | P3 | Core/Models/PipelineContext.cs:185-188 | `SendEventAsync` traga toda excepción, incluida la cancelación, sin log. | Los fallos de observabilidad desaparecen sin rastro. | Log a nivel debug y distinguir cancelación. |
| REG-102 | P3 | Core/Models/PipelineContext.cs:300 | `CreateFromId` lanza `KeyNotFoundException` sin texto ni id. | Diagnóstico pobre al reanudar. | Mensaje con el `correlationId`. |
| REG-103 | P3 | Core/Steps/GroupStep.cs:25-26 | Faltan validaciones de argumentos en `GroupStep`, `LoopStep`, `RateLimiter`, `CircuitBreaker` y `RegisterRange`. | Uso erróneo da fallos tardíos y confusos. | Guard clauses en constructores y registros. |
| REG-104 | P3 | Core/Steps/SwitchStep.cs:81 | El catch atribuye al selector fallos que no pueden llegar de `SendEventAsync`; el ctor protegido deja un selector placeholder que lanza `NotImplementedException`. | Mensaje impreciso y plantilla frágil. | Acotar el try al selector y hacer abstracto el selector. |
| REG-106 | P3 | LLM/Models/LlmResult.cs:52 | `Success` acepta `candidates` null y fallaría con NRE. | Solo ante uso incorrecto. | `ThrowIfNull`. |
| REG-109 | P3 | Support/JSON/JsonResponseParser.cs:113 | `catch` vacío en fallbacks deliberados de `JsonResponseParser`, `ToolCallXmlParser` y `SchemaCompiler`. | Un JSON truncado se convierte en argumento sin explicación. | Log de debug en cada fallback. |
| REG-111 | P3 | Support/JSON/Serializations.cs:33 | El manejador marca todo como `Handled`. | Volcados incompletos sin indicio. | Registrar el error en el manejador. |
| REG-113 | P3 | YAML/Execution/YamlPipelineFactory.cs:181 | `StepId` duplicado produce `ArgumentException` genérica de `ToDictionary`. | Mensaje sin el id duplicado. | Validar antes de ordenar con mensaje descriptivo. |

Solución común: una pasada única de "hacer visible el fallo" (mensajes con id y tipo reales, log en cada `catch` deliberado). Es de bajo riesgo porque ninguna de estas entradas altera el flujo.

### observability-events (9)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-147 | P3 | LLM/Steps/IntentionAnalyzerStep.cs:84 | `FinalizeAsync` emite siempre `LlmResponseEvent` con `Content` vacío si no hay feedback. | Ruido en eventos. | Emitir solo con feedback. |
| REG-148 | P3 | LLM/Streaming/TagHandlerBase.cs:71 | El evento se envía en `finally` con el token ya cancelado; una excepción en `EnrichCompletedEvent` sustituye a la original. | Evento perdido o excepción enmascarada. | Token independiente para el evento y proteger el enriquecimiento. |
| REG-149 | P3 | Observability/EventChannel.cs:77 | La comprobación de capacidad previa a `TryWrite` no es atómica. | Avisos de descarte falsos u omitidos. | Basar el aviso en el resultado de `TryWrite`. |
| REG-151 | P3 | Observability/Events/LlmResponseEvent.cs:67 | Records sellados con colecciones mutables; igualdad por referencia; misma instancia a todos los suscriptores. | Un suscriptor puede mutar el evento. | Colecciones de solo lectura. |
| REG-152 | P3 | Observability/Events/StepCompletedEvent.cs:28 | `AdditionalData` con dos formas de tipo entre eventos. | Los consumidores tratan dos contratos. | Unificar el tipo. |
| REG-153 | P3 | Observability/Events/StepCompletedEvent.cs:32 | El evento retiene el `IStepResult` completo. | Acopla observabilidad y resultado; retención acotada por el canal. | Proyección ligera del resultado. |
| REG-154 | P3 | Observability/Events/StepRoutingEvent.cs:12 | Sin `StepPath`, a diferencia de otros eventos. | Correlación dispar. | Añadir la ruta al evento. |
| REG-155 | P3 | Observability/Events/TagCompletedEvent.cs:15 | `Success` por defecto `true`; el resto de eventos Completed, `false`. | Un emisor que lo olvide produce éxito falso solo en tags. | Alinear el default. |
| REG-156 | P3 | Observability/IEventChannel.cs:28 | `Dispose` de `Subscribe` no drena el handler pese a lo documentado. | Eventos pendientes tras disponer. | Cumplir el contrato o corregir la documentación. |

Solución común: una revisión del contrato de eventos (base común, defaults uniformes, tipos de datos adicionales y ruta) hecha de una vez, porque cada cambio suelto altera el esquema para los consumidores.

### configuration-di (8)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-023 | P2 | Core/Execution/Pipeline.cs:52 | Los timeouts por defecto (60 y 30 min) nunca son nulos; `PipelineOptions` promete `IOptions`/`Pipeline.Configure`, que no existen. | Una ejecución legítima de más de 60 minutos se corta; documentación que miente. | Permitir desactivar el timeout y conectar `PipelineOptions` por DI, o corregir el comentario. |
| REG-028 | P2 | LLM/Tools/Implementation/AvailableTools.cs:63 | `GetTools` descarta nombres no registrados sin error; el filtro de módulo compara el ensamblado del tipo concreto. | Un paso arranca sin herramienta y sin diagnóstico. | Fallar o avisar por nombre desconocido; definir el criterio de módulo. |
| REG-029 | P2 | YAML/Execution/YamlPipelineFactory.cs:38 | `YamlPipeline.ExecuteAsync` no pasa `pipelineTimeout` ni subscribers. | Toda ejecución YAML queda con 60 min y sin suscriptores por ejecución. | Ampliar la firma o aceptar opciones de ejecución. |
| REG-086 | P3 | Configuration/ServiceCollectionExtensions.cs:86 | El estimador de tokens sale del último `ILlmService` registrado. | Estimación desligada del proveedor del rol. | Resolver el contador por rol. |
| REG-087 | P3 | Core/Steps/StepManager.cs:65 | `AddStep` y `AvailableTools.Register` sobrescriben por indexador. | Gana el último registro sin aviso. | Detectar y avisar de duplicados. |
| REG-089 | P3 | LLM/Steps/BaseLlmStep.cs:439 | `inference ?? provider` inútil para booleanos no anulables; funciona por el orden de inicialización. | Frágil ante reordenar inicializadores. | Booleanos anulables o fusión explícita. |
| REG-090 | P3 | LLM/Steps/BaseLlmStep.cs:470 | `MergeOptions` usa `Union` para arrays frente a `Replace` en `LlmInferenceSettings`. | Un rol no puede vaciar una lista del proveedor. | Unificar la semántica. |
| REG-091 | P3 | Observability/EventChannel.cs:31-39 | Defaults contradictorios y opciones no inyectables en `EventChannel`. | `ChannelCapacity` inalterable desde fuera. | Inyectar `EventChannelOptions`. |

### llm-output-parsing (6)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-114 | P3 | LLM/Constants/FinishReason.cs:40-43 | `Parse` trata null o vacío como `Streaming`; sin llamadores en el framework. | Semántica ambigua en API sin uso interno. | Devolver un valor desconocido o retirar. |
| REG-115 | P3 | LLM/Steps/BaseLlmStep.LlmInvocation.cs:371 | Nombres de herramienta comparados con `==` y con `OrdinalIgnoreCase`; la clave solo ordena el primer nivel. | Degradación menor. | Un único comparador. |
| REG-116 | P3 | LLM/Support/ToolCallXmlParser.cs:18 | Regex sobre texto del modelo sin `matchTimeout`. | Coste cuadrático en casos patológicos. | Pasar `matchTimeout`. |
| REG-117 | P3 | LLM/Support/ToolCallXmlParser.cs:177 | `arguments` null o string no JSON se pasa a la herramienta. | La herramienta falla en su parseo. | Validar antes de construir el `ToolCall`. |
| REG-118 | P3 | LLM/Support/ToolCallXmlParser.cs:290 | `FindBestToolMatch` devuelve el primero que coincide tras normalizar. | Resultado dependiente del orden del `HashSet`. | Orden determinista o error por ambigüedad. |
| REG-119 | P3 | LLM/Support/ToolCallXmlParser.cs:302 | `RemoveToolCallTags` no elimina los bloques JSON que `ParseToolCalls` interpreta. | Texto de tool calls queda en el contenido. | Alinear eliminación y parseo. |

Solución común: cuatro de las seis viven en `ToolCallXmlParser`. Revisarlo entero junto a sus bugs REG-045 a REG-047 amortiza el coste de leer el fichero una vez.

### templating-prompts (4)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-179 | P3 | LLM/Steps/IntentionAnalyzerStep.cs:43 | `systemPromptTemplateName` se usa como texto literal. | Nombre de parámetro engañoso. | Renombrar o resolver por el proveedor. |
| REG-180 | P3 | LLM/Support/PromptTemplateResolver.cs:59-60 | Usa el renderizador estático e ignora el `ITemplateRendered` de DI. | Punto de extensión ignorado. | Inyectar `ITemplateRendered`. |
| REG-181 | P3 | Support/Template/TinyTemplateRendered.cs:103-106 | Catch general en la interpolación sin registro. | Marcador intacto sin rastro del motivo. | Log del motivo. |
| REG-182 | P3 | Support/Template/TinyTemplateRendered.cs:279-280 | Errores de include por `Console.WriteLine` y ruta absoluta dentro del prompt. | Fallo oculto y revelación de ruta al LLM. | `ILogger` y un marcador sin ruta. |

Solución común: REG-180, REG-181 y REG-182 giran alrededor de `TinyTemplateRendered`, el mismo fichero de los bugs REG-001 y REG-008; conviene tocarlo una vez.

### schema-serialization (3)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-076 | P2 | Core/Steps/StepManager.cs:193-195 | `StepManager.BuildSchema` (cc=28) y `StepResultFactory` implementan el mismo mapeo con reglas distintas (no recursa, sin `additionalProperties=false`, no nullable como requerido). | Los esquemas de paso y de resultado pueden discrepar para un mismo tipo. | Un único generador de esquema usado por ambos. |
| REG-170 | P3 | Core/Execution/StepResultFactory.cs:416-417 | `CreateSchemaForType` y `CreateAdditionalPropertiesSchema` repiten el árbol de decisiones y ya divergen en descripciones de enum. | Tipo nuevo, dos sitios. | Fusionar con un parámetro de modo. |
| REG-171 | P3 | Support/JSON/JsonSchemaValidator.cs:45 | Valida con System.Text.Json sensible a mayúsculas, sin camelCase ni esquema real; sin llamadores. | Semántica distinta de la del parser Newtonsoft. | Alinear opciones o retirar. |

### concurrency-state (2)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-009 | P2 | Core/Execution/PipelineMiddlewareRegistry.cs:11-12 | Registros y fábricas estáticos de proceso: `PipelineMiddlewareRegistry`, `PipelineContextFactory` (`_instance = this`), `StepManager`, `AvailableTools.Instance`. | Con varios `ServiceProvider` gana la última fábrica y los contextos usan canal, provider y storage ajenos; los tests en paralelo se pisan. | Mover el estado a servicios de DI con el ciclo de vida del contenedor; dejar los estáticos como fachada transitoria. |
| REG-081 | P3 | Core/Steps/StepManager.cs:86-88 | `GetRegisteredTypes` documenta un snapshot y devuelve el diccionario vivo. | Un llamador puede hacer downcast y mutar el registro global. | Devolver una copia. |

### resource-lifecycle (2)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-062 | P2 | Core/Execution/InMemoryPipelineContextStorage.cs:16 | Guarda y devuelve la instancia viva, sin clon ni caducidad. | Contextos suspendidos nunca reanudados se acumulan; el contexto cargado conserva estado mutable del original. | Caducidad configurable y clonado como en `InMemoryConversationStorage`. |
| REG-066 | P2 | Support/Caching/MemoryCacheService.cs:15-18 | `MemoryCache` sin `SizeLimit`, sin expiración por defecto y sin `IDisposable`. | Crecimiento sin límite con claves de alta cardinalidad; el temporizador de compactación nunca se libera. | `SizeLimit` o expiración por defecto, e `IDisposable`. |

### persistence (1)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-157 | P3 | LLM/Conversation/Storage/FileConversationStorage.cs:77-83 | Rehidrata sin contador de tokens ni `ConversationOptions`; los metadatos vuelven como `JArray`/`JObject`/`long`. | Estimación de tokens y tipos de metadatos distintos tras recargar. | Pasar opciones al reconstruir y tipar los metadatos. |

### pipeline-flow (1)

| ID | Sev | Ancla | Mecanismo | Coste de mantener | Solución |
|---|---|---|---|---|---|
| REG-164 | P3 | LLM/Steps/IntentionAnalyzerStep.cs:45 | Guarda la lista de opciones por referencia mientras el catálogo del prompt es una instantánea. | Divergencia si el llamador muta la lista. | Copiar la lista en el constructor. |

## Plan de refactor, ordenado por beneficio/coste

1. **Hacer visibles los fallos (coste bajo, beneficio alto).** Mensajes con id y tipo reales y logs en `catch` deliberados: REG-098, REG-101, REG-102, REG-109, REG-111, REG-113, REG-181, REG-182, REG-028. Sin cambio de contrato.
2. **Retirar código muerto y comentarios falsos (bajo).** REG-121, REG-122, REG-124, REG-128, REG-129, REG-134. Cada pieza de REG-122 exige decidir si se borra o se conserva como API.
3. **Unificar el generador de esquema (medio, alto).** REG-076, REG-170, REG-125 (parte de StepResultFactory), REG-171. Cierra una divergencia observable y el hotspot de `StepResultFactory`.
4. **Revisar `ToolCallXmlParser` entero (medio).** REG-116 a REG-119 más la duplicación de REG-125, junto con los bugs REG-045 a REG-047 y REG-078 del mismo fichero.
5. **Contrato de eventos (medio).** REG-136 a REG-138, REG-151 a REG-155, REG-147 a REG-149, REG-156, REG-091. Hacerlo de una vez porque cada cambio es visible para los consumidores.
6. **Timeouts y opciones por DI (medio).** REG-023, REG-029, REG-086, REG-089, REG-090. Coincide con Pipeline.cs, hotspot con 7 bugs.
7. **Ciclo de vida de almacenes (medio).** REG-062, REG-066, REG-157.
8. **Estado estático global (coste alto, beneficio alto).** REG-009, REG-081, REG-087. Es lo que bloquea tener varios hosts por proceso.
9. **Descomponer `InvokeLlmAsync` (coste alto, beneficio alto).** REG-049, REG-131, REG-115. Con tests de caracterización antes de tocar, y después del punto 5 para no mover dos cosas a la vez.
10. **Cambios de contrato público (requieren aprobación explícita).** REG-123, REG-132, REG-139, REG-138. Agrupar en una versión mayor.

Restantes P3 de bajo valor, para hacer de paso al tocar el fichero: REG-100, REG-103, REG-104, REG-106, REG-114, REG-126, REG-127, REG-130, REG-133, REG-135, REG-140, REG-141, REG-164, REG-179, REG-180.

## Discrepancias de severidad observadas

Las severidades se han respetado tal cual vienen del registro. Observaciones, sin cambiarlas:

- REG-049 (cc=75) es la única deuda de complejidad en P2, mientras REG-131 (cc=29 y cc=15) y REG-125 (duplicación en seis puntos) están en P3. Es coherente por criticidad del camino, pero la frontera es subjetiva.
- REG-062 y REG-066 (crecimiento sin límite) son P2; REG-153 (retención del resultado en el evento, acotada por el canal) es P3. La diferencia se sostiene solo por la acotación.
- REG-023 y REG-029 (mismo efecto: timeout de 60 minutos no ajustable) son P2; REG-091 (defaults contradictorios del canal) es P3.
- REG-122 agrupa ocho ubicaciones de código sin uso bajo un solo ID P3 y REG-124 agrupa diez comentarios; contados por sitio, el volumen de deuda sería mayor que 68.
- REG-114, REG-116, REG-119, REG-128 y REG-171 declaran en su base que las APIs no tienen llamadores en el framework; su prioridad real depende de si hay consumidores externos, dato que el registro no tiene.
