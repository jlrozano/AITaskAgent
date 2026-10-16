# Contratos del repositorio (salida de la lectura por lote; sin código fuente)


## Subsistema `conversation` — Contexto de conversación
Reúne mensajes, historial y almacenamiento de conversaciones junto con tipos de contexto para la ejecución.

- **Configuration/ConversationOptions.cs** (modelo de configuracion): Clase sellada con cinco opciones de gestion de conversacion (MaxTokens, UseBookmarks, UseSlidingWindow, KeepFirstNMessages, SlidingWindowMaxTokens), todas init-only con valores por defecto. En este lote solo ConversationContext consume KeepFirstNMessages y UseBookmarks.
- **Core/Execution/IPipelineContextStorage.cs** (contrato): Contrato de persistencia del PipelineContext de un pipeline suspendido, indexado por correlationId (Save, Load, Delete, Clear). El comentario declara que la implementacion debe encargarse de la serializacion.
- **Core/Execution/InMemoryPipelineContextStorage.cs** (implementacion): Implementacion volatil del almacenamiento de contextos sobre un ConcurrentDictionary. Guarda y devuelve la misma instancia viva, sin clonar ni serializar, y sin caducidad de entradas.
- **Core/Models/PipelineContext.cs** (modelo central de ejecucion): Define EmptyServiceProvider, el record PipelineContext (estado compartido de una ejecucion: canal de eventos, service provider, runtime, conversacion, metadatos, resultados de pasos, ruta actual, clonado para ramas paralelas, envio de eventos) y PipelineContextFactory (fabrica estatica respaldada por una instancia singleton registrada en DI).
- **LLM/Conversation/Context/ConversationContext.cs** (implementacion): Contexto de una conversacion: id, historial (MessageHistory), metadatos y marcas de tiempo. Ofrece helpers para anadir mensajes por rol, marcadores (bookmarks), seleccion de mensajes para una peticion (ventana deslizante o marcador) y clonado.
- **LLM/Conversation/MessageHistory.cs** (implementacion): Lista de mensajes con marcadores por posicion, estimacion de tokens y dos estrategias de recorte (mensajes recientes y ventana deslizante) que preservan la integridad de los pares assistant(tool_calls)/tool exigida por los proveedores.
- **LLM/Conversation/Storage/FileConversationStorage.cs** (implementacion): Persistencia de conversaciones en ficheros JSON (uno por conversacion) mediante el DTO interno ConversationSnapshot. Sanitiza el id como nombre de fichero y verifica que la ruta resultante quede dentro del directorio base.
- **LLM/Conversation/Storage/IConversationStorage.cs** (contrato): Contrato minimo de persistencia de conversaciones: guardar y recuperar por id.
- **LLM/Conversation/Storage/InMemoryConversationStorage.cs** (implementacion): Almacen en memoria de conversaciones que guarda y devuelve clones para aislar mutaciones externas. Registrado como singleton de IConversationStorage segun el grafo.
- **LLM/Conversation/Storage/YamlFileConversationStorage.cs** (implementacion): Persistencia en ficheros YAML con el formato de sesion legacy de CodeGui (prefijo session-, roles user/agent/orchestrator/system/tool). Convierte Message a LegacySessionMessage y viceversa, conservando timestamps previos por posicion al regrabar.
- **LLM/Models/Message.cs** (modelo): Record sellado e inmutable de un mensaje de conversacion independiente del proveedor: rol, contenido, nombre, id de tool call, tool calls, metadatos y objeto nativo del proveedor.
- **LLM/Streaming/ITagHandler.cs** (contrato): Contrato de un manejador de etiquetas de accion en texto (por ejemplo write_file): nombre de etiqueta, instrucciones para el LLM y ejecucion una vez por etiqueta cerrada, devolviendo el placeholder que sustituye a la etiqueta.
- **LLM/Streaming/StreamingTagParser.cs** (implementacion): Escaner de etiquetas por maquina de estados (Normal, PotentialTag, InTag, PotentialClose) alimentado por fragmentos. Devuelve el texto depurado, ejecuta el handler al cerrarse una etiqueta conocida y emite TagStartedEvent/TagCompletedEvent. FlushAsync devuelve como texto plano lo que quedo sin cerrar.
- **LLM/Streaming/TagHandlerBase.cs** (clase base abstracta): Plantilla para handlers de etiquetas: mide la duracion de InternalOnCompleteTagAsync, registra logs y emite siempre un TagCompletedEvent (exito o fallo) en un finally; relanza las excepciones para que el parser las convierta en placeholder.

### ConversationOptions (class) — Configuration/ConversationOptions.cs:6-22
Opciones de configuracion de la gestion de conversacion con valores por defecto. Solo propiedades init-only sin logica.
Colabora con: ConversationContext: lee KeepFirstNMessages y UseBookmarks en su constructor.
- `MaxTokens { get; init; } = 8192` [9-9]: Limite de tokens del contexto. | entra: init | sale: valor | efectos: ninguno | lanza: ninguno
- `UseBookmarks { get; init; } = true` [12-12]: Habilita rollback por marcadores. | entra: init | sale: valor | efectos: ninguno | lanza: ninguno
- `UseSlidingWindow { get; init; } = true` [15-15]: Habilita ventana deslizante. | entra: init | sale: valor | efectos: ninguno | lanza: ninguno
- `KeepFirstNMessages { get; init; } = 2` [18-18]: Mensajes iniciales siempre conservados. | entra: init | sale: valor | efectos: ninguno | lanza: ninguno
- `SlidingWindowMaxTokens { get; init; } = 8192` [21-21]: Limite de tokens de la ventana. | entra: init | sale: valor | efectos: ninguno | lanza: ninguno

### IPipelineContextStorage (interface) — Core/Execution/IPipelineContextStorage.cs:9-30
Contrato de persistencia y recuperacion del estado de un pipeline suspendido por correlationId.
Colabora con: PipelineContext: valor almacenado.; InMemoryPipelineContextStorage: implementacion del lote.; PipelineContextFactory: lo usa para CreateFromId.
- `SaveAsync(string correlationId, PipelineContext context, CancellationToken ct)` [14-14]: Guarda el contexto bajo el correlationId. | entra: correlationId y contexto del llamador | sale: Task | efectos: persistencia segun implementacion | lanza: segun implementacion
- `LoadAsync(string correlationId, CancellationToken ct)` [19-19]: Recupera un contexto suspendido. | entra: correlationId | sale: PipelineContext o null | efectos: ninguno | lanza: segun implementacion
- `DeleteAsync(string correlationId, CancellationToken ct)` [24-24]: Elimina el contexto almacenado. | entra: correlationId | sale: Task | efectos: borrado | lanza: segun implementacion
- `ClearAsync(CancellationToken ct)` [29-29]: Vacia todo el almacen (pruebas y limpieza). | entra: token | sale: Task | efectos: borrado total | lanza: segun implementacion

### InMemoryPipelineContextStorage (class) — Core/Execution/InMemoryPipelineContextStorage.cs:9-40
Almacen volatil de PipelineContext en un ConcurrentDictionary. Registrado como singleton de IPipelineContextStorage segun el grafo. No serializa: guarda referencias vivas.
Colabora con: PipelineContext: valor almacenado por referencia.; ConcurrentDictionary: almacenamiento interno.
- `SaveAsync(correlationId, context, ct)` [14-18]: Inserta o reemplaza la referencia al contexto. | entra: correlationId no nulo | sale: Task completada | efectos: escribe _storage | lanza: ArgumentNullException si correlationId es null (ConcurrentDictionary)
- `LoadAsync(correlationId, ct)` [21-25]: Devuelve la misma instancia guardada o null. | entra: correlationId | sale: PipelineContext? (misma referencia) | efectos: ninguno | lanza: ArgumentNullException si correlationId es null
- `DeleteAsync(correlationId, ct)` [28-32]: Elimina la entrada si existe. | entra: correlationId | sale: Task completada | efectos: borra de _storage | lanza: ArgumentNullException si correlationId es null
- `ClearAsync(ct)` [35-39]: Vacia el diccionario. | entra: token ignorado | sale: Task completada | efectos: vacia _storage | lanza: ninguno

### EmptyServiceProvider (class) — Core/Models/PipelineContext.cs:16-20
IServiceProvider nulo y singleton usado como valor por defecto cuando un PipelineContext se crea sin contenedor.
Colabora con: PipelineContext: lo usa en su constructor sin proveedor.
- `Instance (static readonly)` [18-18]: Instancia unica. | entra: ninguno | sale: EmptyServiceProvider | efectos: ninguno | lanza: ninguno
- `GetService(Type serviceType)` [19-19]: Devuelve siempre null. | entra: tipo solicitado | sale: null | efectos: ninguno | lanza: ninguno

### PipelineContext (record) — Core/Models/PipelineContext.cs:26-238
Estado compartido de una ejecucion de pipeline: canal de eventos, service provider, runtime, conversacion, metadatos y resultados concurrentes, ruta de paso actual con pila, clonado para ramas paralelas y puente hacia el runtime para ejecutar pasos hijos.
Colabora con: IEventChannel: envia eventos de observabilidad.; IPipelineRuntime: motor que ejecuta sub-pasos y provee LoggerFactory.; ConversationContext: conversacion activa (se clona por rama).; IStepResult/IStep: resultados y pasos hijos.; EmptyServiceProvider: proveedor por defecto.
- `PipelineContext(IEventChannel? eventChannel = null)` [31-34]: Delega en el constructor completo con EmptyServiceProvider. | entra: canal opcional | sale: instancia | efectos: ninguno | lanza: ninguno
- `PipelineContext(IEventChannel? eventChannel, IServiceProvider serviceProvider)` [39-43]: Asigna canal y proveedor. | entra: proveedor no nulo | sale: instancia | efectos: ninguno | lanza: ArgumentNullException si serviceProvider es null
- `EventChannel { get; } [JsonIgnore]` [56-56]: Canal de eventos (no serializado). | entra: constructor | sale: IEventChannel? | efectos: ninguno | lanza: ninguno
- `ServiceProvider { get; } [JsonIgnore]` [64-64]: Proveedor de servicios (no serializado). | entra: constructor | sale: IServiceProvider | efectos: ninguno | lanza: ninguno
- `Runtime { get; internal set; } [JsonIgnore]` [81-81]: Motor que ejecuta el contexto; null hasta que arranca una ejecucion. | entra: asignado por el pipeline y por CloneForBranch | sale: IPipelineRuntime? | efectos: ninguno | lanza: ninguno
- `Conversation { get; init; } = new()` [83-83]: Conversacion activa con valores por defecto. | entra: init | sale: ConversationContext | efectos: ninguno | lanza: ninguno
- `CorrelationId { get; init; } = Guid.NewGuid().ToString()` [89-89]: Identificador de la ejecucion. | entra: init | sale: string | efectos: ninguno | lanza: ninguno
- `Metadata { get; init; }` [94-94]: Diccionario concurrente de metadatos compartido entre ramas. | entra: init | sale: ConcurrentDictionary | efectos: ninguno | lanza: ninguno
- `GetMetadata(string key)` [99-99]: Lee un metadato o null. | entra: clave | sale: object? | efectos: ninguno | lanza: ArgumentNullException si key es null
- `SetMetadata(string key, object? value)` [104-104]: Escribe un metadato. | entra: clave y valor | sale: void | efectos: muta Metadata | lanza: ArgumentNullException si key es null
- `StepResults { get; init; }` [110-110]: Resultados de pasos por ruta; compartido entre ramas. | entra: init | sale: ConcurrentDictionary | efectos: ninguno | lanza: ninguno
- `StepState { get; private init; }` [120-120]: Estado interno por paso (no concurrente, no clonado a ramas). | entra: init privado | sale: Dictionary<string, object> | efectos: ninguno | lanza: ninguno
- `CurrentStepPath { get; }` [126-126]: Devuelve la ruta actual del arbol de ejecucion. | entra: ninguno | sale: string | efectos: ninguno | lanza: ninguno
- `AddPathPart(string part)` [128-132]: Apila la ruta actual y le concatena part separado por barra. | entra: segmento | sale: void | efectos: muta _lastPath y _currentPath (no thread-safe) | lanza: ninguno
- `RemovePathPart()` [133-136]: Restaura la ruta anterior desde la pila o vacia. | entra: ninguno | sale: void | efectos: muta _lastPath y _currentPath | lanza: ninguno
- `CloneForBranch()` [143-165]: Crea contexto para rama paralela: clona la conversacion, comparte Metadata y StepResults, ruta vacia, StepState nuevo, y reasigna por reflexion la pila _lastPath invirtiendo el orden. | entra: estado actual | sale: PipelineContext nuevo | efectos: reflexion sobre campo privado | lanza: NullReferenceException si el campo _lastPath no existe (GetField con !)
- `SendEventAsync<TEvent>(TEvent eventData, CancellationToken ct = default)` [172-190]: Envia el evento al canal si existe; traga cualquier excepcion. | entra: evento | sale: true si se envio, false si no hay canal o hubo excepcion | efectos: I/O hacia el canal de eventos | lanza: ninguno (todo se captura)
- `LoggerFactory (propiedad privada)` [200-203]: Resuelve ILoggerFactory: Runtime, luego contenedor, luego NullLoggerFactory. | entra: estado del contexto | sale: ILoggerFactory | efectos: ninguno | lanza: ninguno
- `GetLogger<T>()` [208-208]: Crea ILogger<T> con la factoria resuelta. | entra: tipo generico | sale: ILogger<T> | efectos: ninguno | lanza: ninguno
- `GetLogger(Type type)` [213-213]: Crea ILogger para un tipo. | entra: tipo | sale: ILogger | efectos: ninguno | lanza: ninguno
- `ExecuteStepsWithNextStepsAsync(steps, currentResult, pipelineName, ct, isBranchScope)` [219-237]: Delega en el runtime (propio o resuelto del contenedor) la ejecucion de sub-pasos. | entra: pasos, resultado actual, nombre del pipeline | sale: Task<IStepResult> | efectos: ejecuta pasos hijos | lanza: InvalidOperationException (sincrona, no Task fallida) si no hay runtime

### PipelineContextFactory (class) — Core/Models/PipelineContext.cs:240-302
Fabrica de PipelineContext con API estatica que depende de una instancia singleton estatica asignada en el constructor (registrada en DI). Permite subscribers locales y carga de contextos por id.
Colabora con: IEventChannel/EventChannel: canal global o local por llamada.; IPipelineContextStorage: carga en CreateFromId.; IOptions<AITaskAgentOptions>: nivel de log de eventos.; ConversationContext: conversacion por defecto.
- `PipelineContextFactory(IEventChannel?, IServiceProvider, IPipelineContextStorage)` [246-252]: Guarda dependencias y asigna la instancia estatica _instance = this. | entra: dependencias del contenedor | sale: instancia | efectos: escribe estado estatico global | lanza: ninguno
- `Create(ConversationContext? conversation = null)` [254-257]: Sobrecarga que delega en Create(conversation, null). | entra: conversacion opcional | sale: PipelineContext | efectos: ver Create completo | lanza: InvalidOperationException si la fabrica no esta inicializada
- `Create(ConversationContext? conversation, IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers)` [259-294]: Crea un contexto con el canal global o, si hay subscribers, con un EventChannel local que los reemplaza; asigna la conversacion o una nueva. | entra: conversacion y subscribers opcionales; _instance estatica | sale: PipelineContext | efectos: crea un EventChannel por llamada con subscribers; lee opciones del contenedor | lanza: InvalidOperationException si _instance es null
- `CreateFromId(string id)` [295-301]: Carga un contexto desde el storage por id. | entra: id | sale: Task<PipelineContext> | efectos: lectura del storage | lanza: InvalidOperationException si no esta inicializada; KeyNotFoundException (sin mensaje) si no existe

### ConversationContext (class) — LLM/Conversation/Context/ConversationContext.cs:13-229
Contexto de conversacion con id, historial, metadatos y marcas de tiempo; ofrece operaciones de alta nivel sobre MessageHistory, marcadores, seleccion de mensajes para peticion y clonado. Constructor primario con maxTokens, contador de tokens, logger y opciones.
Colabora con: MessageHistory: almacena mensajes.; Message/ToolCall: contenido.; ConversationOptions: KeepFirstNMessages y UseBookmarks.; ILogger: trazas.
- `ConversationContext(int maxTokens = 4000, Func<string,int>? tokenCounter, ILogger?, ConversationOptions?)` [13-26]: Constructor primario; toma KeepFirstNMessages y UseBookmarks de las opciones o de sus defaults. | entra: parametros opcionales | sale: instancia | efectos: ninguno | lanza: ninguno
- `ConversationId { get; init; }` [29-29]: Id (Guid por defecto). | entra: init | sale: string | efectos: ninguno | lanza: ninguno
- `History { get; }` [32-32]: Historial creado con maxTokens y tokenCounter. | entra: constructor | sale: MessageHistory | efectos: ninguno | lanza: ninguno
- `Metadata { get; init; }` [35-35]: Diccionario de metadatos no concurrente. | entra: init | sale: Dictionary | efectos: ninguno | lanza: ninguno
- `StartedAt { get; init; } / LastActivityAt { get; set; }` [38-41]: Marcas de tiempo UTC. | entra: init/set | sale: DateTime | efectos: ninguno | lanza: ninguno
- `AddUserMessage(string content)` [46-57]: Anade mensaje de rol user y actualiza LastActivityAt. | entra: contenido no nulo (se usa content.Length) | sale: this | efectos: muta History y LastActivityAt; log | lanza: NullReferenceException si content es null
- `AddAssistantMessage(string content)` [62-73]: Anade mensaje assistant. | entra: contenido no nulo | sale: this | efectos: muta History y LastActivityAt; log | lanza: NullReferenceException si content es null
- `AddAssistantMessageWithToolCalls(IEnumerable<ToolCall>, string? content = null)` [85-102]: Anade mensaje assistant con tool calls y texto opcional. | entra: tool calls y contenido | sale: this | efectos: muta History y LastActivityAt; log | lanza: ArgumentNullException si toolCalls es null
- `ReplaceMessage(int index, Message message)` [109-115]: Reemplaza un mensaje del historial y actualiza actividad. | entra: indice y mensaje | sale: this | efectos: muta History y LastActivityAt | lanza: ArgumentOutOfRangeException o ArgumentNullException (via History)
- `AddToolMessage(string toolCallId, string content)` [120-132]: Anade mensaje de rol tool. | entra: id y contenido | sale: this | efectos: muta History y LastActivityAt; log | lanza: ninguno
- `AddSystemMessage(string content)` [137-147]: Anade mensaje system. | entra: contenido | sale: this | efectos: muta History y LastActivityAt; log | lanza: ninguno
- `AddMessage(Message message)` [152-156]: Anade un mensaje crudo y fija LastActivityAt a ahora. | entra: mensaje | sale: void | efectos: muta History y LastActivityAt | lanza: ninguno
- `CreateBookmark()` [161-167]: Crea marcador en la posicion actual. | entra: ninguno | sale: nombre del marcador | efectos: muta marcadores de History; log | lanza: ninguno
- `RestoreBookmark(string bookmark)` [172-178]: Trunca el historial tras el marcador y elimina ese marcador. | entra: nombre | sale: void | efectos: elimina mensajes | lanza: ArgumentException si el marcador no existe
- `GetMessagesForRequest(int? maxTokens, string? fromBookmark, bool useSlidingWindow = true)` [187-205]: Devuelve mensajes desde marcador (si _useBookmarks), con ventana deslizante o el historial completo. | entra: limites y opciones | sale: List<Message> | efectos: log | lanza: ArgumentException si el marcador no existe
- `Clone()` [210-228]: Crea una copia con mismo id, metadatos copiados superficialmente y historial copiado. | entra: estado actual | sale: ConversationContext | efectos: log | lanza: ninguno

### MessageHistory (class) — LLM/Conversation/MessageHistory.cs:11-337
Almacena mensajes en una lista con marcadores por indice, estima tokens y ofrece recorte por recientes o ventana deslizante respetando la agrupacion assistant(tool_calls)/tool.
Colabora con: Message/ToolCall: elementos.; LlmConstants.MessageRoles: roles.; Func<string,int>: contador de tokens.
- `Messages / Bookmarks / MaxTokens / TokenCounter` [18-26]: Propiedades de solo lectura sobre el estado interno. | entra: ninguno | sale: vistas de solo lectura | efectos: ninguno | lanza: ninguno
- `AddMessage(Message message)` [31-34]: Anade un mensaje. | entra: mensaje | sale: void | efectos: muta _messages | lanza: ninguno
- `AddMessages(IEnumerable<Message> messages)` [39-42]: Anade varios mensajes. | entra: mensajes | sale: void | efectos: muta _messages | lanza: ArgumentNullException si es null
- `CreateBookmark()` [47-52]: Registra un marcador Guid con la posicion actual. | entra: ninguno | sale: nombre | efectos: muta _bookmarks | lanza: ninguno
- `RemoveMessage(int index)` [56-66]: Elimina y devuelve el mensaje; null si el indice es invalido. | entra: indice | sale: Message? | efectos: muta _messages sin ajustar marcadores | lanza: ninguno
- `ReplaceMessage(int index, Message message)` [74-84]: Reemplaza un mensaje in situ. | entra: indice y mensaje | sale: void | efectos: muta _messages | lanza: ArgumentNullException; ArgumentOutOfRangeException
- `GetMessagesFromBookmark(string bookmarkName)` [89-94]: Devuelve mensajes desde el indice del marcador. | entra: nombre | sale: List<Message> | efectos: ninguno | lanza: ArgumentException si no existe
- `GetRecentMessages(int? maxTokens = null)` [101-138]: Devuelve los mensajes mas recientes que caben, siempre incluyendo el ultimo grupo y sin tool huerfano al inicio. | entra: limite opcional | sale: List<Message> | efectos: ninguno | lanza: ninguno
- `GetMessagesWithSlidingWindow(int keepFirstN = 2, int? maxTokens = null)` [144-244]: Conserva cabeza (con sus tool results), ultimo grupo y mensajes antiguos que quepan; elimina tool results huerfanos. | entra: keepFirstN y limite | sale: List<Message> | efectos: ninguno | lanza: ninguno
- `Clear()` [249-253]: Vacia mensajes y marcadores. | entra: ninguno | sale: void | efectos: muta estado | lanza: ninguno
- `ClearAfterBookmark(string bookmarkName)` [258-272]: Trunca mensajes desde el indice del marcador y elimina ese marcador. | entra: nombre | sale: void | efectos: elimina mensajes | lanza: ArgumentException si no existe
- `CopyFrom(MessageHistory other)` [277-286]: Copia mensajes (referencias) y marcadores. | entra: otro historial | sale: void | efectos: reemplaza estado | lanza: NullReferenceException si other es null
- `GetTotalTokenCount()` [291-294]: Suma tokens estimados. | entra: ninguno | sale: int | efectos: ninguno | lanza: ninguno
- `FindLastGroupStart(int lowerBound)` [301-310]: Retrocede sobre la racha final de mensajes tool. | entra: cota inferior | sale: indice | efectos: ninguno | lanza: ninguno
- `EstimateMessageTokens(Message message)` [312-329]: Estima tokens de contenido y tool calls. | entra: mensaje | sale: int | efectos: ninguno | lanza: NullReferenceException si Name o Arguments de un ToolCall son null y el contador por defecto
- `DefaultTokenCounter(string text)` [331-335]: Estima longitud/4 redondeando hacia arriba. | entra: texto | sale: int | efectos: ninguno | lanza: NullReferenceException si text es null

### FileConversationStorage (class) — LLM/Conversation/Storage/FileConversationStorage.cs:14-138
Persistencia de conversaciones en ficheros JSON mediante ConversationSnapshot, con sanitizacion del id y comprobacion de que la ruta quede bajo el directorio base. Anade Delete y List fuera del contrato de IConversationStorage.
Colabora con: ConversationContext: se serializa a snapshot y se reconstruye.; ConversationSnapshot: DTO.; Newtonsoft.Json: serializacion.; IConversationStorage: contrato.
- `FileConversationStorage(string basePath = "conversations")` [23-34]: Crea el directorio base y configura Newtonsoft (indentado, TypeNameHandling.None). | entra: ruta base | sale: instancia | efectos: crea directorio | lanza: IOException/UnauthorizedAccessException al crear el directorio
- `SaveConversationAsync(conversation, ct)` [37-52]: Serializa un snapshot del contexto y escribe el fichero. | entra: conversacion no nula | sale: Task | efectos: escribe fichero (sin escritura atomica) | lanza: UnauthorizedAccessException por traversal; IOException
- `GetConversationAsync(conversationId, ct)` [55-87]: Lee el JSON, lo deserializa y reconstruye el contexto con MaxTokens, metadatos, marcas y mensajes. | entra: id | sale: ConversationContext? (null si no hay fichero o snapshot null) | efectos: lee fichero | lanza: JsonException si el JSON es invalido; ArgumentNullException si Messages es null
- `DeleteConversationAsync(conversationId, ct)` [92-104]: Borra el fichero si existe. | entra: id | sale: Task completada (sincrona) | efectos: borra fichero | lanza: UnauthorizedAccessException por traversal; IOException
- `ListConversationsAsync(ct)` [109-122]: Lista los nombres de ficheros *.json sin extension. | entra: ninguno | sale: IEnumerable<string> con nombres de fichero (ya sanitizados) | efectos: lee directorio | lanza: IOException
- `GetFilePath(string conversationId)` [126-137]: Sanitiza el id y valida que la ruta completa empiece por el directorio base. | entra: id | sale: ruta | efectos: ninguno | lanza: UnauthorizedAccessException si sale del directorio base

### ConversationSnapshot (record) — LLM/Conversation/Storage/FileConversationStorage.cs:144-150
DTO interno de serializacion del estado persistido de una conversacion (id, MaxTokens, mensajes, metadatos, marcas de tiempo).
Colabora con: FileConversationStorage: lo crea y lo lee.; Message: elementos.
- `ConversationSnapshot(string, int, List<Message>, Dictionary<string, object?>, DateTime, DateTime)` [144-150]: Constructor posicional del record. | entra: campos persistidos | sale: instancia | efectos: ninguno | lanza: ninguno

### IConversationStorage (interface) — LLM/Conversation/Storage/IConversationStorage.cs:8-19
Contrato de persistencia de conversaciones: guardar y obtener por id.
Colabora con: ConversationContext: valor.; FileConversationStorage, InMemoryConversationStorage, YamlFileConversationStorage: implementaciones.
- `SaveConversationAsync(ConversationContext, CancellationToken)` [13-13]: Guarda la conversacion. | entra: conversacion | sale: Task | efectos: persistencia | lanza: segun implementacion
- `GetConversationAsync(string, CancellationToken)` [18-18]: Recupera por id. | entra: id | sale: ConversationContext? | efectos: ninguno | lanza: segun implementacion

### InMemoryConversationStorage (class) — LLM/Conversation/Storage/InMemoryConversationStorage.cs:9-29
Almacen en memoria que guarda y devuelve clones para aislar mutaciones. Registrado como singleton de IConversationStorage segun el grafo.
Colabora con: ConversationContext.Clone: copia al guardar y al leer.
- `SaveConversationAsync(conversation, ct)` [13-18]: Guarda un clon por id. | entra: conversacion | sale: Task completada | efectos: escribe _store | lanza: NullReferenceException si conversation es null
- `GetConversationAsync(conversationId, ct)` [20-28]: Devuelve un clon del contexto guardado o null. | entra: id | sale: ConversationContext? | efectos: ninguno | lanza: ArgumentNullException si id es null

### YamlFileConversationStorage (class) — LLM/Conversation/Storage/YamlFileConversationStorage.cs:13-283
Persistencia YAML en el formato legacy de sesiones de CodeGui. Traduce roles y mensajes, preserva timestamps por posicion y mantiene el prefijo session- en disco.
Colabora con: YamlDotNet: (de)serializacion.; LegacySessionDocument/LegacySessionMessage/LegacyToolCall: DTOs privados.; LlmConstants.MessageRoles: mapeo de roles.; ConversationContext/Message/ToolCall.
- `YamlFileConversationStorage(string basePath)` [23-38]: Valida la ruta, crea el directorio y construye serializador y deserializador (camelCase, ignora propiedades desconocidas). | entra: ruta base no vacia | sale: instancia | efectos: crea directorio | lanza: ArgumentException; IOException
- `SaveConversationAsync(conversation, ct)` [41-80]: Lee el YAML existente (si hay), actualiza id, titulo, agentes, fechas y mensajes (preservando timestamps por posicion) y reescribe el fichero. | entra: conversacion no nula | sale: Task | efectos: lee y escribe fichero (read-modify-write sin bloqueo) | lanza: YamlException si el YAML existente es invalido; IOException
- `GetConversationAsync(conversationId, ct)` [83-140]: Lee el YAML y reconstruye el contexto con titulo y agentes como metadatos y mensajes convertidos. | entra: id no vacio | sale: ConversationContext? con ConversationId sin prefijo session- | efectos: lee fichero | lanza: ArgumentException; YamlException
- `EnsureSessionPrefix(string id)` [142-143]: Anade el prefijo session- si falta. | entra: id | sale: string | efectos: ninguno | lanza: ninguno
- `NormalizeConversationId(string conversationId)` [145-148]: Quita el prefijo session- si existe. | entra: id | sale: string | efectos: ninguno | lanza: ninguno
- `GetFilePath(string normalizedConversationId)` [150-155]: Compone la ruta .yaml sanitizando caracteres invalidos de nombre de fichero, sin comprobacion de contencion. | entra: id normalizado | sale: ruta | efectos: ninguno | lanza: ninguno
- `ToLegacyMessage(Message message, string? existingTimestamp = null)` [157-186]: Convierte Message a mensaje legacy mapeando rol y descartando Name en user/system/tool, Metadata y Native. | entra: mensaje y timestamp previo | sale: LegacySessionMessage | efectos: ninguno | lanza: ninguno
- `FromLegacyMessage(LegacySessionMessage? legacyMessage)` [188-218]: Convierte mensaje legacy a Message; roles desconocidos pasan a assistant; descarta tool calls incompletas. | entra: mensaje legacy | sale: Message? (null si rol vacio) | efectos: ninguno | lanza: ninguno
- `TryParseUtcTimestamp(string? timestamp)` [220-227]: Parsea fecha asumiendo UTC. | entra: texto | sale: DateTime? | efectos: ninguno | lanza: ninguno
- `FormatUtcTimestamp(DateTime? timestamp)` [229-232]: Formatea como yyyy-MM-ddTHH:mm:ssZ sin convertir a UTC. | entra: fecha | sale: string | efectos: ninguno | lanza: ninguno
- `GetMetadataString(metadata, key)` [234-237]: Lee un metadato como string si lo es. | entra: diccionario y clave | sale: string? | efectos: ninguno | lanza: ninguno
- `GetMetadataStringList(metadata, key)` [239-254]: Lee un metadato como lista de strings desde List<string> o IEnumerable<object>. | entra: diccionario y clave | sale: List<string>? | efectos: ninguno | lanza: ninguno

### LegacySessionDocument (class) — LLM/Conversation/Storage/YamlFileConversationStorage.cs:256-265
DTO privado del documento YAML de sesion (id, titulo, fechas, agentes, mensajes, eventos).
Colabora con: YamlFileConversationStorage: lo (de)serializa.
- `Id, Title, StartedAt, CompletedAt, Agents, Messages, Events` [258-264]: Propiedades auto sin logica. | entra: YamlDotNet | sale: valores | efectos: ninguno | lanza: ninguno

### LegacySessionMessage (class) — LLM/Conversation/Storage/YamlFileConversationStorage.cs:267-275
DTO privado de un mensaje en el YAML legacy.
Colabora con: LegacyToolCall: tool calls anidadas.
- `Role, AgentName, Content, Timestamp, ToolCallId, ToolCalls` [269-274]: Propiedades auto sin logica. | entra: YamlDotNet | sale: valores | efectos: ninguno | lanza: ninguno

### LegacyToolCall (class) — LLM/Conversation/Storage/YamlFileConversationStorage.cs:277-282
DTO privado de una tool call en el YAML legacy.
Colabora con: LegacySessionMessage: contenedor.
- `Id, Name, Arguments` [279-281]: Propiedades auto sin logica. | entra: YamlDotNet | sale: valores | efectos: ninguno | lanza: ninguno

### Message (record) — LLM/Models/Message.cs:7-32
Mensaje de conversacion independiente del proveedor, inmutable por init pero con colecciones mutables (ToolCalls, Metadata).
Colabora con: ToolCall: tool calls del assistant.; NativeObject: objeto nativo del proveedor.
- `Role { get; init; } required` [10-10]: Rol del mensaje. | entra: init | sale: string | efectos: ninguno | lanza: ninguno
- `Content, Name, ToolCallId` [13-19]: Contenido, nombre e id de tool call opcionales. | entra: init | sale: string? | efectos: ninguno | lanza: ninguno
- `ToolCalls { get; init; }` [22-22]: Lista mutable de tool calls. | entra: init | sale: List<ToolCall>? | efectos: ninguno | lanza: ninguno
- `Metadata { get; init; }` [25-25]: Diccionario mutable de extensiones. | entra: init | sale: Dictionary | efectos: ninguno | lanza: ninguno
- `Native { get; init; }` [31-31]: Objeto nativo del proveedor para reutilizar. | entra: init | sale: NativeObject? | efectos: ninguno | lanza: ninguno

### ITagHandler (interface) — LLM/Streaming/ITagHandler.cs:13-41
Contrato de un manejador de etiqueta de accion en texto del LLM, invocado una vez por etiqueta cerrada con su cuerpo completo.
Colabora con: StreamingTagParser: invocante.; PipelineContext: contexto del paso.; TagHandlerBase: implementacion base.
- `TagName { get; }` [16-16]: Nombre de la etiqueta sin corchetes. | entra: ninguno | sale: string | efectos: ninguno | lanza: ninguno
- `GetInstructions()` [22-22]: Instrucciones para el LLM sobre el uso de la etiqueta. | entra: ninguno | sale: string | efectos: ninguno | lanza: ninguno
- `OnCompleteTagAsync(attributes, content, context, ct)` [36-40]: Ejecuta la accion de una etiqueta completa. | entra: atributos, cuerpo, contexto | sale: placeholder que sustituye la etiqueta | efectos: segun handler | lanza: segun handler

### StreamingTagParser (class) — LLM/Streaming/StreamingTagParser.cs:26-414
Maquina de estados que filtra texto del LLM: deja pasar texto plano, retiene cuerpos de etiquetas conocidas y ejecuta su handler al cierre, sustituyendo la etiqueta por el placeholder. Con estado mutable por instancia, no thread-safe.
Colabora con: ITagHandler: ejecuta acciones.; PipelineContext: envio de eventos y contexto al handler.; TagStartedEvent/TagCompletedEvent: observabilidad.; ILogger: fallos de handlers.
- `ParserState (enum privado)` [28-34]: Estados Normal, PotentialTag, InTag, PotentialClose. | entra: ninguno | sale: enum | efectos: ninguno | lanza: ninguno
- `StreamingTagParser(List<ITagHandler> handlers, ILogger? logger = null)` [54-58]: Indexa handlers por TagName sin distinguir mayusculas. | entra: handlers | sale: instancia | efectos: ninguno | lanza: ArgumentException si hay TagName duplicados; ArgumentNullException
- `ExecutedTags / CompletedTagReferences` [61-67]: Exponen tags ejecutados y placeholders acumulados. | entra: ninguno | sale: vistas de solo lectura | efectos: ninguno | lanza: ninguno
- `ProcessChunkAsync(string chunk, PipelineContext context, CancellationToken ct)` [77-211]: Recorre el fragmento caracter a caracter segun el estado, emite TagStartedEvent al abrir una etiqueta conocida y ejecuta el handler al cierre. | entra: fragmento no nulo y contexto | sale: texto depurado del fragmento | efectos: muta buffers y estado; eventos; ejecuta handlers | lanza: OperationCanceledException propagada; NullReferenceException si chunk es null
- `DiscardPotentialTagBuffer(StringBuilder output, char causingChar)` [220-240]: Devuelve el buffer como texto o reinicia un posible tag si el causante es <. | entra: salida y caracter | sale: void | efectos: muta buffer y estado | lanza: ninguno
- `DiscardPotentialCloseBuffer(char causingChar)` [248-267]: Devuelve un intento fallido de cierre al cuerpo. | entra: caracter | sale: void | efectos: muta buffers y estado | lanza: ninguno
- `FlushAsync(PipelineContext? context = null, CancellationToken ct = default)` [277-311]: Devuelve como texto plano lo pendiente, registra un warning y emite TagCompletedEvent fallido si procede, y reinicia el estado. | entra: contexto opcional | sale: texto pendiente | efectos: log y evento; reinicia estado | lanza: OperationCanceledException
- `ExecuteActiveTagAsync(PipelineContext context, CancellationToken ct)` [321-350]: Ejecuta el handler activo con el cuerpo recortado; convierte excepciones en placeholder de error salvo cancelacion. | entra: contexto | sale: placeholder | efectos: ejecuta handler; registra tags ejecutados; reinicia estado | lanza: OperationCanceledException propagada
- `TrimTagBody(string body)` [358-369]: Quita un salto de linea inicial y uno final. | entra: cuerpo | sale: string | efectos: ninguno | lanza: ninguno
- `ResetTagState()` [371-379]: Limpia estado de la etiqueta activa. | entra: ninguno | sale: void | efectos: muta estado | lanza: ninguno
- `ParseOpeningTag(string tagText)` [381-403]: Extrae nombre y atributos entre comillas dobles de la etiqueta de apertura. | entra: texto de la etiqueta | sale: (nombre?, atributos) | efectos: ninguno | lanza: ninguno
- `ParseClosingTag(string tagText)` [405-409]: Extrae el nombre de una etiqueta de cierre. | entra: texto | sale: string | efectos: ninguno | lanza: ninguno
- `AttributeRegex()` [412-413]: Regex generada para atributos nombre="valor". | entra: ninguno | sale: Regex | efectos: ninguno | lanza: ninguno

### TagHandlerBase (class) — LLM/Streaming/TagHandlerBase.cs:17-93
Base abstracta (Template Method) para handlers de etiquetas: temporiza, registra y emite TagCompletedEvent siempre.
Colabora con: PipelineContext.SendEventAsync: emision.; TagCompletedEvent: evento.; ILogger: logs.
- `TagName { get; } (abstract)` [20-20]: Nombre de la etiqueta. | entra: ninguno | sale: string | efectos: ninguno | lanza: ninguno
- `GetInstructions() (abstract)` [23-23]: Instrucciones para el LLM. | entra: ninguno | sale: string | efectos: ninguno | lanza: ninguno
- `Logger { get; } (abstract protected)` [26-26]: Logger del handler. | entra: ninguno | sale: ILogger | efectos: ninguno | lanza: ninguno
- `OnCompleteTagAsync(attributes, content, context, ct)` [32-75]: Ejecuta la accion interna, registra y en finally emite el TagCompletedEvent; relanza excepciones. | entra: atributos, cuerpo, contexto | sale: placeholder | efectos: logs y evento | lanza: las excepciones de InternalOnCompleteTagAsync
- `InternalOnCompleteTagAsync(...) (abstract protected)` [78-82]: Accion concreta del handler. | entra: atributos, cuerpo, contexto | sale: placeholder | efectos: segun handler | lanza: para reportar fallo
- `EnrichCompletedEvent(baseEvent, attributes, content, placeholder)` [88-92]: Hook virtual que devuelve el evento sin cambios. | entra: evento y datos | sale: TagCompletedEvent | efectos: ninguno | lanza: ninguno

## Subsistema `core-attributes` — Activación de pasos
Agrupa la activación y gestión de pasos con atributos, configuración y servicios de soporte.

- **Configuration/ConfigurationPatchExtensions.cs** (infrastructure): Extensiones sobre IConfiguration que reconstruyen una sección como JToken (objetos, arrays por claves numéricas, primitivos tipados, expansión de ${VAR}) y la deserializan o pueblan con Newtonsoft, opcionalmente con DiContractResolver. Incluye ServiceCollectionConfigurationExtensions.ConfigureEx, que registra IConfigureOptions<T> e IOptionsChangeTokenSource<T> usando BindEx en lugar del binder estándar.
- **Core/Abstractions/IStepActivator.cs** (contract): Contrato de una línea para crear instancias de un tipo de step a partir de un Type y un IServiceProvider opcional. Permite sustituir la estrategia de activación.
- **Core/Attributes/StepTagAttribute.cs** (model): Atributo sellado de clase (sin múltiples usos) que asocia un nombre de tag YAML a un step. Lo leen StepManager.AddStep y RegisterStepsFromAssembly para descubrir steps.
- **Core/Attributes/ValidatorTagAttribute.cs** (model): Atributo gemelo de StepTagAttribute para clases IStepValidator. Su XML-doc dice que lo descubre la fábrica YAML; ningún código del lote lo lee.
- **Core/Execution/StepResultFactory.cs** (infrastructure): Fábrica interna estática que crea instancias de IStepResult por reflexión con constructores compilados (Expression) y los cachea por Type en un ConcurrentDictionary. Además construye un JsonSchema (NJsonSchema) del tipo de la propiedad Value, con soporte de enums, diccionarios, listas, clases anidadas y atributos de validación DataAnnotations.
- **Core/Models/StepDescription.cs** (model): DTO inmutable (init) con tag, descripción, tipo del step y JsonSchema de parámetros. Es el valor que produce y cachea StepManager.GetStepDescription.
- **Core/Steps/DefaultStepActivator.cs** (implementation): Implementación por defecto de IStepActivator. Intenta ActivatorUtilities, luego un constructor resuelto manualmente contra el provider, luego el constructor sin parámetros y, como último recurso, un objeto sin inicializar (GetUninitializedObject).
- **Core/Steps/StepManager.cs** (infrastructure): Registro estático tag-a-tipo de steps con descubrimiento por ensamblado, creación DI-aware (provider explícito, AsyncLocal o global) con población opcional desde JObject, y generación/caché de StepDescription con un JsonSchema propio simplificado.
- **LLM/Constants/FinishReason.cs** (model): Enum FinishReason y extensiones: Parse convierte la cadena del proveedor en enum (vacío/null equivale a Streaming, desconocido a Other con el valor crudo) y ToLlmString hace la conversión inversa.
- **LLM/Constants/LlmConstants.cs** (infrastructure): Clase estática parcial con la clase anidada MessageRoles, que define las constantes de rol de mensaje (system, user, assistant, tool).
- **Support/Caching/MemoryCacheService.cs** (implementation): Implementación de ICacheService sobre un MemoryCache propio, independiente del contenedor DI. Ofrece Get, Set con expiración absoluta relativa, Remove, Clear y GetOrCreateAsync.

### ConfigurationPatchExtensions (class) — Configuration/ConfigurationPatchExtensions.cs:11-174
Clase estática parcial de extensiones de IConfiguration. Reconstruye una sección como árbol JToken y la convierte a un T o puebla una instancia existente con Newtonsoft, saltándose el binder estándar de Microsoft.
Colabora con: DiContractResolver: resolver de contrato con DI usado cuando se pasa un IServiceProvider; IConfiguration/IConfigurationSection: fuente de datos; Newtonsoft JToken/JsonSerializer: materialización
- `GetEx<T>(this IConfiguration, string? key, IServiceProvider? sp)` [13-48]: Obtiene la sección (o la raíz), la reconstruye como JToken y la convierte a T; si T es JToken o subtipo, devuelve el token con comprobación de tipo exacto. | entra: configuration no nulo; key opcional; sp opcional para DiContractResolver. | sale: T? o null si la sección no existe; JToken tal cual si T es JToken. | efectos: ninguno | lanza: InvalidCastException si T es subtipo de JToken y el tipo exacto del token difiere; excepciones de Newtonsoft en ToObject.
- `BindEx<T>(this IConfigurationSection, T instance, IServiceProvider? sp)` [52-71]: Reconstruye la sección como JToken y, si no es null, la vuelca sobre instance con JsonSerializer.Populate. | entra: section, instance no nulo, sp opcional. | sale: void; muta instance. | efectos: Muta la instancia recibida. | lanza: ArgumentNullException si instance es null; excepciones de Newtonsoft en Populate.
- `ReconstructSectionJson(IConfiguration)` [73-122]: Convierte recursivamente una sección en JToken: valor escalar a primitivo, hijos todos numéricos a JArray ordenado por índice, resto a JObject. | entra: configuration o sección. | sale: JToken (JValue null si no hay valor ni hijos). | efectos: ninguno | lanza: ninguno
- `ParsePrimitiveOrString(string?)` [124-162]: Intenta interpretar la cadena como bool, entero, decimal o double (cultura invariante) y si no, devuelve cadena con variables de entorno expandidas. | entra: valor crudo de configuración. | sale: JValue tipado. | efectos: Lee variables de entorno vía ExpandEnvVars. | lanza: ninguno
- `ExpandEnvVars(string)` [164-170]: Sustituye ${NOMBRE} por el valor de la variable de entorno; si no existe deja el texto original. | entra: cadena. | sale: cadena expandida. | efectos: Lee variables de entorno. | lanza: ninguno
- `MyRegex()` [172-173]: Regex generada en compilación para ${(\w+)}. | entra: ninguno | sale: Regex | efectos: ninguno | lanza: ninguno

### ServiceCollectionConfigurationExtensions (class) — Configuration/ConfigurationPatchExtensions.cs:179-246
Extensiones de IServiceCollection que registran la configuración de opciones de T enlazada con BindEx, más un token de cambio asociado a la sección.
Colabora con: ConfigurationPatchExtensions.BindEx: realiza el enlace; IConfigureOptions<T>/IOptionsChangeTokenSource<T>: se registran como singleton
- `ConfigureEx<T>(this IServiceCollection, string sectionName)` [181-217]: Registra un ConfigureNamedOptions<T> que resuelve IConfiguration del provider y llama BindEx sobre la sección, y un ConfigurationChangeTokenSource<T>. | entra: services, sectionName; IConfiguration debe estar registrado. | sale: services (encadenable). | efectos: Añade dos registros singleton al contenedor. | lanza: InvalidOperationException diferida si IConfiguration no está registrado.
- `ConfigureEx<T>(this IServiceCollection, IConfigurationSection section)` [219-245]: Igual que la sobrecarga por nombre pero con la sección ya resuelta. | entra: services, section. | sale: services. | efectos: Añade dos registros singleton. | lanza: ninguno

### IStepActivator (interface) — Core/Abstractions/IStepActivator.cs:3-6
Contrato para instanciar steps a partir de su Type y un provider opcional.
Colabora con: DefaultStepActivator: implementación por defecto; StepManager: consumidor
- `Create(Type stepType, IServiceProvider? serviceProvider)` [5-5]: Crea una instancia del tipo indicado. | entra: stepType, provider opcional. | sale: object. | efectos: depende de la implementación | lanza: no especificado por el contrato

### StepTagAttribute (class) — Core/Attributes/StepTagAttribute.cs:7-11
Atributo de clase que declara el tag YAML con el que se descubre un step.
Colabora con: StepManager: lo lee con reflexión en AddStep, RegisterStepsFromAssembly y GetStepDescription
- `StepTagAttribute(string tagName) / TagName` [8-10]: Constructor primario que guarda el tag en la propiedad de solo lectura TagName. | entra: tagName sin validación. | sale: atributo con TagName. | efectos: ninguno | lanza: ninguno

### ValidatorTagAttribute (class) — Core/Attributes/ValidatorTagAttribute.cs:8-13
Atributo de clase equivalente a StepTagAttribute para validadores IStepValidator.
Colabora con: IStepValidator (externo al lote): tipo al que se aplica según el XML-doc
- `ValidatorTagAttribute(string tagName) / TagName` [9-12]: Constructor primario que guarda el tag en TagName. | entra: tagName sin validación. | sale: atributo con TagName. | efectos: ninguno | lanza: ninguno

### StepResultFactory (class) — Core/Execution/StepResultFactory.cs:13-631
Fábrica estática interna de IStepResult con constructores compilados con Expression y caché por Type. Calcula también el JsonSchema del tipo de Value para esquemas de salida de LLM.
Colabora con: IStep/IStepError/IStepResult: tipos construidos; NJsonSchema: modelo del esquema; NewtonsoftJsonDataConverter (según grafo): usado en la compilación de constructores; Pipeline, StepBase, TypedStep: consumidores externos
- `_cache` [65-65]: ConcurrentDictionary<Type, StepActivatorInfo> estático. | entra: ninguno | sale: caché de info de activación | efectos: Crece sin límite con cada Type distinto. | lanza: ninguno
- `CreateStepResult<T>(IStep, IStepError?)` [67-71]: Atajo genérico que delega en la versión por Type. | entra: step, error. | sale: T. | efectos: Puebla la caché. | lanza: InvalidOperationException si T no es válido.
- `CreateStepResult<T>(IStep, object?, IStepError?)` [72-76]: Atajo genérico con valor. | entra: step, value, error. | sale: T. | efectos: Puebla la caché. | lanza: InvalidOperationException por tipo de valor incompatible o Value de solo lectura.
- `CreateStepResult(Type, IStep, IStepError?)` [78-84]: Obtiene la info cacheada y crea la instancia. | entra: type, step, error. | sale: IStepResult. | efectos: Puebla la caché. | lanza: InvalidOperationException (tipo no IStepResult, sin Value, sin constructor).
- `CreateStepResult(Type, IStep, object?, IStepError?)` [86-91]: Igual que la anterior y asigna Value. | entra: type, step, value, error. | sale: IStepResult. | efectos: Puebla la caché; asigna Value por reflexión. | lanza: InvalidOperationException.
- `GetStepActivatorInfo(Type)` [92-95]: GetOrAdd sobre la caché con BuildInfo. | entra: type. | sale: StepActivatorInfo. | efectos: Puebla la caché; BuildInfo puede ejecutarse más de una vez bajo concurrencia. | lanza: las de BuildInfo.
- `BuildInfo(Type)` [97-175]: Valida que el tipo sea IStepResult, localiza la propiedad Value, construye el esquema y elige constructor: (IStep, IStepError), (IStep), (IStep, opcional) o (IStep, cualquiera) con valor por defecto. | entra: type. | sale: StepActivatorInfo; si no hay constructor adecuado, ambos delegados quedan null. | efectos: Compila expresiones. | lanza: InvalidOperationException si no es IStepResult o no tiene Value.
- `GetValuePropertyInfo(Type)` [177-194]: Busca Value declarada subiendo por la jerarquía de base. | entra: type. | sale: PropertyInfo. | efectos: ninguno | lanza: InvalidOperationException si no hay Value.
- `BuildJsonSchema(Type)` [196-227]: Devuelve null para string, object y primitivos; para otros tipos crea un esquema objeto con additionalProperties=false y rellena propiedades. | entra: tipo de Value. | sale: JsonSchema? (null también ante cualquier excepción). | efectos: ninguno | lanza: ninguno (traga todas).
- `AddPropertiesFromType(Type, IDictionary, ICollection, HashSet<Type>)` [229-280]: Recorre propiedades públicas escribibles no ignoradas, calcula nombre camelCase, esquema, descripción, requeridas (no nullable + init + sin default) y valor por defecto. | entra: type y colecciones destino. | sale: muta properties y requiredProperties. | efectos: Muta los argumentos. | lanza: ArgumentException por clave duplicada en properties.Add.
- `CreateSchemaForType(Type, HashSet<Type>)` [282-376]: Mapea un tipo .NET a JsonSchemaProperty: primitivos, fecha, enum con descripciones, diccionario con clave string, arrays/listas y clases con recursión protegida contra ciclos. | entra: propertyType, visitedTypes. | sale: JsonSchemaProperty (sin Type si el tipo no está contemplado). | efectos: Muta visitedTypes (Add/Remove). | lanza: las de AddPropertiesFromType.
- `TryGetStringKeyDictionaryValueType(Type, out Type)` [378-414]: Detecta Dictionary/IDictionary con clave string, directamente o por interfaz, y devuelve el tipo de valor. | entra: type. | sale: bool y tipo de valor (object por defecto). | efectos: ninguno | lanza: ninguno
- `CreateAdditionalPropertiesSchema(Type, HashSet<Type>)` [416-508]: Equivalente a CreateSchemaForType pero devolviendo JsonSchema, usado para valores de diccionario y sus elementos. | entra: valueType, visitedTypes. | sale: JsonSchema. | efectos: Muta visitedTypes. | lanza: las de AddPropertiesFromType.
- `ApplyValidationAttributes(PropertyInfo, JsonSchema)` [510-579]: Traduce Range, StringLength, MinLength, MaxLength, RegularExpression, EmailAddress y Url a restricciones del esquema. | entra: propertyInfo y esquema ya tipado. | sale: muta el esquema. | efectos: Muta el esquema. | lanza: FormatException/InvalidCastException de Convert.ToDecimal en límites Range no convertibles.
- `CompileCtorWithError(ConstructorInfo)` [581-592]: Compila un delegado (IStep, IStepError) a objeto. | entra: constructor de 2 parámetros. | sale: Func<IStep, IStepError?, object>. | efectos: Compila expresión. | lanza: ArgumentException si los tipos no encajan.
- `CompileCtor(ConstructorInfo)` [594-602]: Compila un delegado (IStep) a objeto. | entra: constructor de 1 parámetro. | sale: Func<IStep, object>. | efectos: Compila expresión. | lanza: ArgumentException si no encajan.
- `CompileCtorWithOptionalValue(ConstructorInfo)` [604-615]: Compila delegado que pasa default(tipo del segundo parámetro). | entra: constructor (IStep, opcional). | sale: Func<IStep, object>. | efectos: Compila expresión. | lanza: ArgumentException si no encajan.
- `CompileCtorWithSecondParamDefault(ConstructorInfo, Type valueType)` [617-629]: Cuerpo idéntico al anterior; el parámetro valueType no se usa. | entra: constructor (IStep, cualquiera), valueType ignorado. | sale: Func<IStep, object>. | efectos: Compila expresión. | lanza: ArgumentException si no encajan.

### StepResultFactory.StepActivatorInfo (record) — Core/Execution/StepResultFactory.cs:15-62
Registro interno que agrupa los delegados de construcción, la propiedad Value y el JsonSchema de un tipo de resultado, y crea instancias con valor opcional.
Colabora con: StepBase, TypedStep, Factory, Pipeline y otros (según grafo): invocan CreateInstance
- `ValueType` [22-22]: Tipo de la propiedad Value. | entra: ValueProperty. | sale: Type. | efectos: ninguno | lanza: ninguno
- `CreateInstance(IStep, IStepError?)` [24-38]: Usa el constructor con error o el de un parámetro y asigna Error después. | entra: step, error. | sale: object (IStepResult). | efectos: Asigna Error en la instancia. | lanza: InvalidOperationException si ambos delegados son null.
- `CreateInstance(IStep, object?, IStepError?)` [40-60]: Crea la instancia, valida asignabilidad del valor y asigna Value por reflexión. | entra: step, value, error. | sale: object. | efectos: ValueProperty.SetValue sobre la instancia. | lanza: InvalidOperationException por tipo incompatible o Value de solo lectura.

### StepDescription (class) — Core/Models/StepDescription.cs:9-15
DTO de metadatos de un tipo de step registrado.
Colabora con: StepManager: lo construye y cachea
- `TagName / Description / StepType / Parameters` [11-14]: Propiedades init con valores por defecto vacíos. | entra: inicializador de objeto. | sale: valores almacenados. | efectos: ninguno | lanza: ninguno

### DefaultStepActivator (class) — Core/Steps/DefaultStepActivator.cs:8-68
Activador por defecto con cadena de estrategias de instanciación y degradación silenciosa hasta un objeto sin inicializar.
Colabora con: ActivatorUtilities: primera estrategia; IServiceProvider: resolución manual de parámetros; StepManager: lo usa como activador por defecto; SchemaCompiler, JsonResponseParser, Serializations, TinyTemplateRendered (según grafo): consumidores externos
- `Create(Type stepType, IServiceProvider? serviceProvider)` [10-67]: Con provider: ActivatorUtilities, luego constructor más largo resoluble (públicos o no) con defaults; sin provider o sin éxito: constructor sin parámetros o GetUninitializedObject. | entra: stepType no nulo; provider opcional. | sale: object (nunca null). | efectos: Ejecuta constructores del tipo; puede ejecutarse un constructor varias veces si el primer intento falla tras efectos parciales. | lanza: ArgumentNullException; TargetInvocationException desde ctor.Invoke; la primera estrategia traga todo.

### StepManager (class) — Core/Steps/StepManager.cs:18-309
Registro estático global de tags a tipos de step, con creación DI-aware y descripción con esquema. Mantiene estado mutable estático (tipos, caché, provider global, provider por flujo asíncrono).
Colabora con: StepTagAttribute: fuente del tag; DefaultStepActivator: instanciación; StepDescription/JsonSchema: salida de descripción; IStep: restricción de tipo
- `CurrentScopeProvider (get/set)` [31-35]: Lee y escribe el provider del flujo asíncrono actual (AsyncLocal). | entra: IServiceProvider?. | sale: IServiceProvider?. | efectos: Estado AsyncLocal. | lanza: ninguno
- `SetServiceProvider(IServiceProvider)` [37-38]: Fija el provider global estático. | entra: serviceProvider. | sale: void. | efectos: Escribe estado estático global. | lanza: ninguno
- `AddStep(Type, string?)` [46-66]: Valida IStep, obtiene el tag del argumento o de [StepTag] (sin herencia) y registra tag a tipo, sobrescribiendo. | entra: stepType, tagName opcional. | sale: void. | efectos: Escribe _stepTypes. | lanza: ArgumentException si no es IStep o falta tag.
- `RegisterStepsFromAssembly(Assembly)` [72-84]: Registra los tipos concretos IStep con [StepTag] del ensamblado. | entra: assembly. | sale: void. | efectos: Escribe _stepTypes. | lanza: ReflectionTypeLoadException de GetTypes; las de AddStep.
- `GetRegisteredTypes()` [87-88]: Devuelve el diccionario interno como IReadOnlyDictionary. | entra: ninguno | sale: vista viva, no snapshot. | efectos: ninguno | lanza: ninguno
- `TryGetStepType(string, out Type)` [90-91]: Busca el tipo por tag. | entra: tag. | sale: bool y tipo. | efectos: ninguno | lanza: ArgumentNullException si tag es null.
- `CreateStep(string, JObject?, IServiceProvider?)` [100-128]: Resuelve el tipo por tag, instancia con el provider (sp, scope o global) y puebla propiedades desde config con JsonSerializer. | entra: tag registrado; config opcional. | sale: IStep. | efectos: Instancia el step; puebla propiedades. | lanza: KeyNotFoundException si el tag no existe; InvalidCastException si no es IStep; errores de Newtonsoft.
- `GetStepDescription(Type)` [136-173]: Calcula el tag (con herencia), consulta la caché por tag, y si no está construye descripción y esquema y la guarda. | entra: stepType. | sale: StepDescription. | efectos: Escribe _descriptionCache bajo Lock. | lanza: ArgumentNullException.
- `GetDescriptionFromClass(Type)` [177-191]: Busca [Description] subiendo por la jerarquía hasta object. | entra: type. | sale: string (vacío si no hay). | efectos: ninguno | lanza: ninguno
- `BuildSchema(Type)` [193-284]: Construye un esquema objeto de propiedades públicas escribibles con tipos escalares, enums y colecciones de escalares; marca como requeridas las no nullable o con [Required]. | entra: stepType. | sale: JsonSchema. | efectos: ninguno | lanza: ninguno explícito
- `IsEnumerableType(Type, out Type)` [286-308]: Detecta tipos genéricos que implementan IEnumerable<T> y devuelve T. | entra: type. | sale: bool y elemento. | efectos: ninguno | lanza: ninguno

### FinishReason (enum) — LLM/Constants/FinishReason.cs:6-25
Motivos de finalización estándar de una respuesta LLM: Streaming, Stop, Length, ToolCalls, ContentFilter, Other.
Colabora con: FinishReasonExtensions: conversión desde y hacia cadena
- `Streaming/Stop/Length/ToolCalls/ContentFilter/Other` [9-24]: Valores del enum. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### FinishReasonExtensions (class) — LLM/Constants/FinishReason.cs:30-70
Extensiones estáticas de conversión cadena a FinishReason y a la inversa.
Colabora con: LlmHelpers, ToolCallXmlParser (según grafo): invocan Parse
- `Parse(string?)` [38-55]: Null o vacío da Streaming; compara en minúsculas con variantes snake_case y PascalCase; otro valor da Other con el texto crudo. | entra: cadena del proveedor. | sale: (FinishReason, string? Raw). | efectos: ninguno | lanza: ninguno
- `ToLlmString(this FinishReason)` [60-69]: Devuelve la cadena estándar; Streaming da cadena vacía y Other da other. | entra: reason. | sale: string. | efectos: ninguno | lanza: ninguno

### LlmConstants (class) — LLM/Constants/LlmConstants.cs:3-22
Contenedor estático parcial de constantes del subsistema LLM; solo aloja MessageRoles en este fichero.
Colabora con: MessageHistory y YamlFileConversationStorage (según grafo): usan sus constantes
- `MessageRoles (clase anidada)` [8-21]: Aloja las constantes de rol. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### LlmConstants.MessageRoles (class) — LLM/Constants/LlmConstants.cs:8-21
Constantes de cadena para los roles de mensaje: system, user, assistant, tool.
- `System/User/Assistant/Tool` [11-20]: Constantes const string. | entra: ninguno | sale: cadenas | efectos: ninguno | lanza: ninguno

### MemoryCacheService (class) — Support/Caching/MemoryCacheService.cs:8-70
Caché en memoria sobre MemoryCache privado, sin dependencia del contenedor DI, que implementa ICacheService.
Colabora con: ICacheService: contrato implementado; IMemoryCache/MemoryCache: almacenamiento
- `MemoryCacheService()` [15-18]: Crea un MemoryCache propio con opciones por defecto. | entra: ninguno | sale: instancia. | efectos: Reserva un MemoryCache no liberado. | lanza: ninguno
- `Get<T>(string)` [20-24]: Devuelve el valor o default. | entra: key no vacío. | sale: T?. | efectos: ninguno | lanza: ArgumentException si la clave es nula o en blanco.
- `Set<T>(string, T, TimeSpan?)` [26-37]: Guarda el valor con expiración absoluta relativa opcional; sin expiración no caduca. | entra: key, value, expiration. | sale: void. | efectos: Escribe en la caché. | lanza: ArgumentException si la clave es inválida.
- `Remove(string)` [39-43]: Elimina la entrada. | entra: key. | sale: void. | efectos: Modifica la caché. | lanza: ArgumentException.
- `Clear()` [45-51]: Compacta al 100% el MemoryCache para vaciarlo. | entra: ninguno | sale: void. | efectos: Vacía la caché. | lanza: ninguno
- `GetOrCreateAsync<T>(string, Func<Task<T>>, TimeSpan?)` [53-69]: Devuelve el valor cacheado o ejecuta factory, guarda y devuelve; sin exclusión mutua por clave. | entra: key, factory no nulo, expiration. | sale: Task<T>. | efectos: Ejecuta factory; escribe en la caché. | lanza: ArgumentException, ArgumentNullException y las de factory.

## Subsistema `core-steps` — Pasos y resultados
Define tipos de pasos, incluidos los paralelos y los de bucle, y sus resultados.

- **Core/Abstractions/Factory.cs** (implementation): Factory<T> resuelve instancias de T por nombre. Construye una caché perezosa con todos los T registrados en DI (clave = valor de una propiedad `keyName` leída por reflexión), crea según el ciclo de vida registrado y, si no hay registro, recurre a IOptionsMonitor<T>.Get(name).
- **Core/Abstractions/IEnrichable.cs** (contract): Interfaz opcional para que un paso enriquezca actividades OpenTelemetry y eventos de inicio/fin. Los dos hooks de Activity tienen implementación por defecto vacía; los de evento son obligatorios. Los invoca ObservabilityMiddleware.
- **Core/Abstractions/IFactory.cs** (contract): Contrato mínimo IFactory<T>: GetService(string key) devuelve una instancia de T por clave.
- **Core/Abstractions/IFallbackable.cs** (contract): Contrato IFallbackable: SwitchToNextFallback permite a un paso cambiar de alternativa (p. ej. otro modelo) tras un error; devuelve bool. Lo consume RetryMiddleware.
- **Core/Abstractions/IStepValidator.cs** (contract): Contrato de validador semántico de resultados de paso, con Id para referenciarlo desde YAML y ValidateAsync que devuelve (IsValid, Error).
- **Core/Execution/StepValidation.cs** (infrastructure): Validación estática de unicidad de nombres de paso. Recorre recursivamente GroupStep, ParallelStep y LoopStep lanzando InvalidOperationException ante duplicados. Lo llama Pipeline.ExecuteAsync.
- **Core/Models/StepError.cs** (model): Record sellado StepError que implementa IStepError: mensaje obligatorio y excepción original opcional, para comunicar errores sin lanzar.
- **Core/StepResults/ActionResult.cs** (implementation): Resultado de ActionStep (StepResult<string>). Constructor privado con fábricas estáticas para éxito, error por mensaje y error por excepción; añade un diccionario Metadata.
- **Core/StepResults/ParallelResult.cs** (implementation): Contenedor de resultados de ParallelStep: ConcurrentDictionary nombre de paso -> IStepResult, con Value de solo lectura, GetResult tipado y un indexador que devuelve el Value de cada resultado.
- **Core/StepResults/StepErrorResult.cs** (implementation): ErrorStepResult: resultado de error sin tipo (StepResult<string>) que usa el framework cuando un paso falla. Fábricas FromException y FromMessage.
- **Core/StepResults/StepResult.cs** (contract): Clase base abstracta de todos los resultados (StepResult) y su variante tipada StepResult<T>. Guarda valor, nombre/tipo del paso, error, SuspendInfo y NextSteps; GetParameters extrae por reflexión las propiedades públicas en PascalCase y camelCase para plantillas.
- **Core/StepResults/SuccessStepResult.cs** (implementation): SuccessStepResult: StepResult<string> con un mensaje como Value.
- **Core/StepResults/SuspendStepResult.cs** (implementation): SuspendStepResult: resultado que indica suspensión del pipeline para intervención humana; el estado a restaurar viaja en Value y los datos a revisar en DataToReview.
- **Core/Steps/ActionStep.cs** (implementation): ActionStep<TIn>: paso determinista que ejecuta un delegado con efectos laterales, en modo normal (espera y captura excepciones) o fire-and-forget (Task.Run, siempre devuelve éxito).
- **Core/Steps/BreakStep.cs** (implementation): BreakStepResult y BreakStep: paso de control de flujo que devuelve un BreakStepResult con el Value de la entrada para que un LoopStep envolvente termine.
- **Core/Steps/DelegatedStep.cs** (implementation): DelegatedStep<TIn,TOut>: paso que delega ExecuteAsync en un Func proporcionado; captura toda excepción y la convierte en resultado de error.
- **Core/Steps/EmptyStep.cs** (implementation): EmptyStep: IStep marcador sin comportamiento; ExecuteAsync devuelve la entrada, ValidateAsync siempre válido, FinalizeAsync no hace nada. No hereda de StepBase.
- **Core/Steps/GroupStep.cs** (implementation): GroupStep (no genérico, deserializable desde YAML) y GroupStep<TIn>: agrupa pasos secuenciales volcándolos en input.NextSteps para que el Pipeline padre los ejecute; la variante tipada valida el tipo de entrada del primer paso.
- **Core/Steps/LoopStep.cs** (implementation): LoopStep: ejecuta sus pasos hasta MaxIterations veces mediante context.ExecuteStepsWithNextStepsAsync, sale ante error/suspensión o BreakStepResult y emite LoopExhaustedEvent al agotar iteraciones.
- **Core/Steps/ParallelStep.cs** (implementation): ParallelStep y ParallelStep<TIn>: ejecuta ramas con Parallel.ForEachAsync, cada una sobre un PipelineContext clonado (CloneForBranch), emite BranchStarted/CompletedEvent y agrega resultados y errores en un ParallelResult.
- **Core/Steps/StepBase.cs** (contract): Clase base abstracta de pasos: nombre, tipos de entrada/salida, reintentos, timeout, validadores, logger, fábrica de resultados por activator, hooks de enriquecimiento (delegan en Enrichable), Finalize, Save/RestoreState y SwitchToNextFallback, con implementaciones explícitas de IStep/IEnrichable/IResume/IFallbackable.
- **Core/Steps/TypedStep.cs** (contract): TypedStep<TIn,TOut>: base de pasos con tipos; adapta el ExecuteAsync no genérico al tipado, intentando convertir la entrada con StepResultFactory si no es del tipo esperado.
- **LLM/Results/InputMessage.cs** (implementation): InputMessage: StepResult<string> vacío, declarado sin namespace.
- **LLM/Results/Intention.cs** (model): Modelos de resultado de análisis de intención (Intention, UserFeedbackInfo, UserChoiceChip, Question) con atributos Description que alimentan el esquema JSON enviado al LLM.
- **LLM/Results/LlmStringStepResult.cs** (implementation): LlmStringStepResult: resultado de un paso LLM con Content (alias de Value) y ToolCalls; válido si hay contenido o llamadas a herramientas.
- **Observability/EventEnrichmentResult.cs** (model): Record struct genérico EventEnrichmentResult<TEvent> (evento + indicador de supresión). Sin referencias en el grafo.

### Factory<T> (class) — Core/Abstractions/Factory.cs:7-65
Fábrica genérica por nombre sobre el contenedor DI. Cachea (tipo, ciclo de vida, instancia singleton) por nombre; el nombre se lee por reflexión de la propiedad indicada en keyName. Si el nombre no está en la caché usa IOptionsMonitor<T>.
Colabora con: IServiceProvider: GetServices<T>/GetService para poblar la caché y crear instancias; IServiceCollection: determinar ServiceLifetime por ImplementationType; IOptionsMonitor<T>: resolución alternativa por nombre; IFactory<T>: contrato que implementa
- `Factory(IServiceProvider sp, IServiceCollection services, string keyName = "Name")` [7-7]: Constructor primario; captura proveedor, colección y nombre de la propiedad clave. | entra: sp, services desde DI; keyName con valor por defecto | sale: instancia | efectos: ninguno | lanza: ninguno
- `BuildCache()` [12-27]: Materializa todos los T registrados y construye un diccionario nombre -> (Type, Lifetime, Instance); Instance solo se guarda si es Singleton. | entra: sp.GetServices<T>() y services | sale: Dictionary nombre->tupla | efectos: Instancia todos los T registrados (también los transient) para leer su nombre | lanza: ArgumentException de ToDictionary si dos servicios dan el mismo nombre
- `GetServiceName(T instance)` [28-35]: Lee por reflexión la propiedad keyName (pública o no) de la instancia y devuelve su ToString, o cadena vacía. | entra: instancia de T | sale: string nombre ('' si no existe propiedad o es null) | efectos: ninguno | lanza: ninguno
- `GetService(string name)` [36-64]: Devuelve el servicio por nombre: singleton cacheado, transient creado con ActivatorUtilities, scoped vía GetServices<T>().First(...), o IOptionsMonitor<T>.Get(name). | entra: name; _cache perezoso | sale: T | efectos: Inicializa _cache; crea instancias | lanza: InvalidOperationException (lifetime desconocido), ArgumentException si el nombre no existe

### IFactory<T> (interface) — Core/Abstractions/IFactory.cs:4-7
Contrato de fábrica por clave.
Colabora con: Factory<T>: implementación
- `GetService(string key)` [6-6]: Devuelve la instancia asociada a la clave. | entra: key | sale: T | efectos: ninguno | lanza: ninguno

### IEnrichable (interface) — Core/Abstractions/IEnrichable.cs:12-51
Hooks opcionales de enriquecimiento de observabilidad por paso: tags de Activity y eventos de inicio/fin.
Colabora con: ObservabilityMiddleware: invoca los hooks antes y después del paso; StepBase: implementación explícita que delega en Enrichable; StepStartedEvent / IStepCompletedEvent: eventos enriquecidos
- `EnrichActivityBefore(Activity? activity, IStepResult input, PipelineContext context)` [21-21]: Hook por defecto vacío para añadir tags antes de ejecutar. | entra: activity (puede ser null), input, context | sale: void | efectos: ninguno | lanza: ninguno
- `EnrichActivityAfter(Activity? activity, IStepResult result, PipelineContext context)` [30-30]: Hook por defecto vacío para añadir tags tras ejecutar. | entra: activity, result, context | sale: void | efectos: ninguno | lanza: ninguno
- `EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context)` [40-40]: Devuelve el evento de inicio enriquecido (sin implementación por defecto). | entra: baseEvent, input, context | sale: StepStartedEvent | efectos: ninguno | lanza: ninguno
- `EnrichCompletedEvent(IStepCompletedEvent baseEvent, IStepResult result, PipelineContext context)` [50-50]: Devuelve el evento de fin enriquecido (sin implementación por defecto). | entra: baseEvent, result, context | sale: IStepCompletedEvent | efectos: ninguno | lanza: ninguno

### IFallbackable (interface) — Core/Abstractions/IFallbackable.cs:5-8
Contrato para pasos que pueden cambiar a una alternativa tras un fallo.
Colabora con: RetryMiddleware: lo llama entre reintentos; StepBase: implementación explícita que delega en SwitchToNextFallback protegido
- `SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt)` [7-7]: Intenta pasar a la siguiente alternativa; true si cambió. | entra: error del intento, context, número de intento | sale: bool | efectos: ninguno | lanza: ninguno

### IStepValidator (interface) — Core/Abstractions/IStepValidator.cs:9-29
Validador semántico externo de resultados, referenciable desde YAML por Id.
Colabora con: StepBase.Validate: los itera en orden
- `Id { get; }` [14-14]: Identificador único del validador. | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)` [25-28]: Valida un resultado. | entra: result, context, token | sale: Task<(bool IsValid, string? Error)> | efectos: ninguno | lanza: ninguno

### StepValidation (class) — Core/Execution/StepValidation.cs:6-34
Utilidad estática que verifica unicidad de nombres de paso recorriendo pasos anidados.
Colabora con: GroupStep, ParallelStep, LoopStep: tipos cuyos hijos recorre; Pipeline.ExecuteAsync: llamador
- `ValidateUniqueStepNames(IReadOnlyList<IStep> steps, string pipelineName, Dictionary<string,string>? existingNames = null)` [8-33]: Registra el nombre de cada paso en el diccionario y lanza si ya existe; para contenedores recurre pasando una copia del diccionario. | entra: lista de pasos, nombre del pipeline, nombres ya vistos | sale: void; muta existingNames con los nombres del nivel | efectos: Muta el diccionario recibido | lanza: InvalidOperationException ante nombre duplicado

### StepError (record) — Core/Models/StepError.cs:9-16
Error estructurado sellado de un paso.
Colabora con: IStepError: contrato; ErrorStepResult/ActionResult/StepBase/ParallelStep: lo crean
- `Message { get; init; }` [12-12]: Mensaje obligatorio. | entra: init | sale: string | efectos: ninguno | lanza: ninguno
- `OriginalException { get; init; }` [15-15]: Excepción original opcional. | entra: init | sale: Exception? | efectos: ninguno | lanza: ninguno

### ActionResult (class) — Core/StepResults/ActionResult.cs:6-31
Resultado de ActionStep: string con mensaje y Error opcional.
Colabora con: StepResult<string>: base; StepError: error asociado; ActionStep: productor
- `ActionResult(IStep step, string message, IStepError? error)` [9-13]: Constructor privado: asigna Value=message y Error. | entra: step, message, error | sale: instancia | efectos: ninguno | lanza: ninguno
- `Metadata { get; init; }` [15-15]: Diccionario libre inicializado vacío. | entra: init | sale: Dictionary<string, object?> | efectos: ninguno | lanza: ninguno
- `FromExceptionAction(IStep step, Exception ex, string? message = null)` [16-21]: Crea resultado de error con StepError(ex.Message, ex); el Value es message ?? ex.Message. | entra: step, ex, message opcional | sale: ActionResult | efectos: ninguno | lanza: ninguno
- `FromErrorMessage(IStep step, string message)` [24-28]: Crea resultado de error con StepError(message). | entra: step, message | sale: ActionResult | efectos: ninguno | lanza: ninguno
- `FromSuccessMessage(IStep step, string message)` [30-30]: Crea resultado sin error. | entra: step, message | sale: ActionResult | efectos: ninguno | lanza: ninguno

### ParallelResult (class) — Core/StepResults/ParallelResult.cs:13-53
Resultados de las ramas de un ParallelStep indexados por nombre de paso, almacenados en un ConcurrentDictionary.
Colabora con: ParallelStep: único escritor (AddResult internal); StepResult: base; IStepResult<IReadOnlyDictionary<...>>: contrato tipado
- `ParallelResult(IStep step)` [13-13]: Constructor primario; delega en StepResult(step). | entra: step | sale: instancia | efectos: ninguno | lanza: ninguno
- `Value { get; } (new)` [20-20]: Oculta StepResult.Value y devuelve vista de solo lectura del diccionario. | entra: estado interno | sale: IReadOnlyDictionary<string, IStepResult> | efectos: ninguno | lanza: ninguno
- `IStepResult.Value (explícita)` [23-23]: Devuelve la misma vista de solo lectura como object?. | entra: estado interno | sale: object? | efectos: ninguno | lanza: ninguno
- `AddResult(string stepName, IStepResult result)` [28-31]: Añade con TryAdd; un nombre repetido se ignora en silencio. | entra: stepName, result | sale: void | efectos: Muta el diccionario concurrente | lanza: ninguno
- `GetResult(string stepName)` [36-37]: Busca el resultado por nombre. | entra: stepName | sale: IStepResult? o null | efectos: ninguno | lanza: ninguno
- `GetResult<TResult>(string stepName)` [42-43]: GetResult con cast `as TResult`. | entra: stepName | sale: TResult? o null | efectos: ninguno | lanza: ninguno
- `this[string stepName]` [51-52]: Indexador que devuelve el Value del resultado de la rama o null. | entra: stepName | sale: object? | efectos: ninguno | lanza: ninguno

### ErrorStepResult (class) — Core/StepResults/StepErrorResult.cs:11-31
Resultado de error genérico del framework (string).
Colabora con: StepError; RetryMiddleware/TimeoutMiddleware/Pipeline/ParallelStep: productores
- `ErrorStepResult(IStep step, IStepError error)` [13-17]: Asigna Error y Value=error.Message. | entra: step, error | sale: instancia | efectos: ninguno | lanza: ninguno
- `FromException(IStep step, Exception ex)` [20-24]: Crea resultado con StepError(ex.Message, ex). | entra: step, ex | sale: ErrorStepResult | efectos: ninguno | lanza: ninguno
- `FromMessage(IStep step, string message)` [27-30]: Crea resultado con StepError(message). | entra: step, message | sale: ErrorStepResult | efectos: ninguno | lanza: ninguno

### StepResult (class) — Core/StepResults/StepResult.cs:10-80
Base abstracta de resultados; guarda valor, StepName, StepType, Error, SuspendInfo y NextSteps y extrae parámetros por reflexión.
Colabora con: IStep: origen de StepName/StepType; StepResultFactory (core-attributes): construcción dinámica y GetParameters; LlmStepResult, ActionResult, etc.: derivados
- `StepResult(IStep step, object? value = null)` [16-22]: Guarda valor y copia Name y tipo del paso. | entra: step no nulo | sale: instancia | efectos: ninguno | lanza: ArgumentNullException si step es null
- `Value { get; internal protected set; }` [24-24]: Acceso al valor almacenado. | entra: campo _value | sale: object? | efectos: ninguno | lanza: ninguno
- `StepName { get; }` [26-26]: Nombre del paso. | entra: fijado en ctor | sale: string | efectos: ninguno | lanza: ninguno
- `StepType { get; }` [28-28]: Nombre del tipo del paso. | entra: fijado en ctor | sale: string | efectos: ninguno | lanza: ninguno
- `HasError { get; }` [30-30]: Error != null. | entra: Error | sale: bool | efectos: ninguno | lanza: ninguno
- `Error { get; set; }` [33-33]: Error asociado. | entra: set público | sale: IStepError? | efectos: ninguno | lanza: ninguno
- `SuspendInfo { get; set; }` [36-36]: Información de suspensión HITL. | entra: set público | sale: object? | efectos: ninguno | lanza: ninguno
- `ValidateAsync(PipelineContext context)` [40-43]: Validación virtual; por defecto válido. | entra: context | sale: Task<(true, null)> | efectos: ninguno | lanza: ninguno
- `NextSteps { get; internal protected set; }` [49-49]: Pasos adicionales dinámicos; el setter copia la lista o usa vacía. | entra: lista | sale: List<IStep> | efectos: ninguno | lanza: ninguno
- `GetParameters()` [55-79]: Recorre las propiedades públicas de instancia del tipo concreto (excepto Value y HasError) y las vuelca al diccionario en PascalCase y camelCase. | entra: reflexión sobre GetType() | sale: Dictionary<string, object?> | efectos: Reflexión en cada llamada, sin caché | lanza: Excepciones de PropertyInfo.GetValue (p. ej. TargetParameterCountException con indexadores)

### StepResult<T> (class) — Core/StepResults/StepResult.cs:85-91
Variante tipada de StepResult que expone Value como T?.
Colabora con: StepResult: base; IStepResult<T>
- `StepResult(IStep step, T? value = default)` [85-85]: Constructor primario; pasa value a la base. | entra: step, value | sale: instancia | efectos: ninguno | lanza: ninguno
- `Value { get; internal protected set; } (new)` [88-88]: Getter con cast (T?)base.Value; setter asigna a la base. | entra: base.Value | sale: T? | efectos: ninguno | lanza: InvalidCastException si el valor almacenado no es T

### SuccessStepResult (class) — Core/StepResults/SuccessStepResult.cs:5-11
StepResult<string> de éxito con mensaje.
Colabora con: StepResult<string>
- `SuccessStepResult(IStep step, string message)` [7-10]: Asigna Value=message. | entra: step, message | sale: instancia | efectos: ninguno | lanza: ninguno

### SuspendStepResult (class) — Core/StepResults/SuspendStepResult.cs:14-22
Resultado que pide suspender el pipeline para revisión humana, portando estado y datos a revisar.
Colabora con: StepResult<object>; IResume/Pipeline: consumo externo
- `SuspendStepResult(IStep step, object? state = null)` [14-14]: Constructor primario; state pasa a Value. | entra: step, state | sale: instancia | efectos: ninguno | lanza: ninguno
- `DataToReview { get; init; }` [21-21]: Datos para el revisor. | entra: init | sale: object? | efectos: ninguno | lanza: ninguno

### ActionStep<TIn> (class) — Core/Steps/ActionStep.cs:12-50
Paso que ejecuta un delegado asíncrono sin resultado propio y devuelve un ActionResult de éxito o error.
Colabora con: TypedStep<TIn, ActionResult>: base; ActionResult: resultado; PipelineContext: pasado al delegado
- `ActionStep(string name, Func<TIn,PipelineContext,int,ActionResult?,Task> action, bool fireAndForget = false)` [12-15]: Constructor primario con delegado y modo. | entra: name, action, fireAndForget | sale: instancia | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(TIn input, PipelineContext context, int Attempt, ActionResult? LastStepResult, CancellationToken ct)` [17-47]: En fireAndForget lanza el delegado en Task.Run (intento 1, sin último resultado) y devuelve éxito; en modo normal espera el delegado y convierte cualquier excepción en ActionResult de error. | entra: input, context, attempt, último resultado | sale: ActionResult | efectos: Ejecuta el delegado (efectos laterales), logging de errores, Task.Run en segundo plano | lanza: ninguna (todas capturadas)

### BreakStepResult (class) — Core/Steps/BreakStep.cs:10-15
Marca de salida de bucle que porta el valor a propagar.
Colabora con: LoopStep: lo detecta y desenvuelve; StepResult<object>
- `BreakStepResult(IStep step, object? value = null)` [12-14]: Constructor que pasa el valor a la base. | entra: step, value | sale: instancia | efectos: ninguno | lanza: ninguno

### BreakStep (class) — Core/Steps/BreakStep.cs:22-37
Paso que devuelve un BreakStepResult con el Value de la entrada.
Colabora con: StepBase; BreakStepResult; LoopStep: consumidor
- `BreakStep()` [24-26]: Constructor sin parámetros (nombre "Break"); para YAML. | entra: ninguno | sale: instancia | efectos: ninguno | lanza: ninguno
- `BreakStep(string name = "Break")` [28-30]: Constructor con nombre opcional. | entra: name | sale: instancia | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken ct)` [32-36]: Devuelve BreakStepResult(this, input.Value). | entra: input | sale: Task<IStepResult> | efectos: ninguno | lanza: ninguno

### DelegatedStep<TIn,TOut> (class) — Core/Steps/DelegatedStep.cs:12-35
Paso cuyo cuerpo es un Func recibido; encapsula errores en resultado de error.
Colabora con: TypedStep<TIn,TOut>; TypedStep.CreateErrorResult
- `DelegatedStep(string name, Func<...> action)` [12-15]: Constructor primario con el delegado. | entra: name, action | sale: instancia | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(TIn input, PipelineContext context, int attempt, TOut? lastStepResult, CancellationToken ct)` [17-32]: Invoca action(this, input, context, attempt, lastStepResult); en excepción devuelve CreateErrorResult con el mensaje. | entra: input, context, attempt, último resultado | sale: TOut | efectos: Las del delegado | lanza: ninguna (todas capturadas, incluida cancelación)

### EmptyStep (class) — Core/Steps/EmptyStep.cs:9-42
Paso marcador: no transforma nada.
Colabora con: IStep; IResume (Resume => null)
- `Name { get; }` [11-11]: Nombre. | entra: ctor | sale: string | efectos: ninguno | lanza: ninguno
- `InputType` [13-13]: typeof(void). | entra: ninguno | sale: Type | efectos: ninguno | lanza: ninguno
- `OutputType` [15-15]: typeof(void). | entra: ninguno | sale: Type | efectos: ninguno | lanza: ninguno
- `Timeout` [17-17]: null. | entra: ninguno | sale: TimeSpan? | efectos: ninguno | lanza: ninguno
- `MaxRetries` [19-19]: 1. | entra: ninguno | sale: int | efectos: ninguno | lanza: ninguno
- `Resume` [21-21]: null. | entra: ninguno | sale: IResume? | efectos: ninguno | lanza: ninguno
- `EmptyStep()` [26-26]: Nombre "EmptyStep". | entra: ninguno | sale: instancia | efectos: ninguno | lanza: ninguno
- `EmptyStep(string name)` [31-31]: Nombre personalizado. | entra: name | sale: instancia | efectos: ninguno | lanza: ninguno
- `FinalizeAsync(...)` [33-34]: No hace nada. | entra: result, context, ct | sale: Task completada | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(...)` [36-37]: Devuelve la entrada sin cambios. | entra: input | sale: Task<IStepResult> con input | efectos: ninguno | lanza: ninguno
- `ValidateAsync(...)` [39-40]: Siempre (true, null). | entra: result | sale: Task<(true, null)> | efectos: ninguno | lanza: ninguno

### GroupStep (class) — Core/Steps/GroupStep.cs:13-51
Agrupador secuencial: delega la ejecución al Pipeline añadiendo sus pasos a input.NextSteps.
Colabora con: StepBase; Pipeline: ejecuta NextSteps; StepValidation: recorre Steps
- `GroupStep()` [18-21]: Constructor para YAML; tipos IStepResult, MaxRetries=1. | entra: ninguno | sale: instancia | efectos: ninguno | lanza: ninguno
- `GroupStep(string name, IReadOnlyList<IStep> steps)` [24-31]: Deriva InputType del primer paso y OutputType del último; copia los pasos; MaxRetries=1. | entra: name, steps | sale: instancia | efectos: ninguno | lanza: NullReferenceException si steps es null (se accede a steps.Count en el inicializador base antes de ThrowIfNull)
- `Steps { get; init; }` [36-40]: Lista de pasos; init reemplaza la lista interna (null -> vacía). | entra: init | sale: List<IStep> | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken ct)` [43-50]: Vacía input.NextSteps, añade los pasos del grupo y devuelve la propia entrada. | entra: input | sale: input mutado | efectos: Muta input.NextSteps | lanza: ninguno

### GroupStep<TIn> (class) — Core/Steps/GroupStep.cs:57-73
Variante tipada: fija InputType a TIn y comprueba compatibilidad con el primer paso.
Colabora con: GroupStep
- `GroupStep(string name, IReadOnlyList<IStep> steps)` [60-69]: Llama a la base, fija InputType y valida el tipo del primer paso. | entra: name, steps | sale: instancia | efectos: ninguno | lanza: ArgumentException si el primer paso no acepta TIn
- `_steps (propiedad privada)` [72-72]: Alias a base.Steps. | entra: base.Steps | sale: List<IStep> | efectos: ninguno | lanza: ninguno

### LoopStep (class) — Core/Steps/LoopStep.cs:13-97
Bucle de pasos con tope de iteraciones y salida por BreakStepResult, error o suspensión.
Colabora con: PipelineContext.ExecuteStepsWithNextStepsAsync/AddPathPart/RemovePathPart/SendEventAsync; BreakStepResult; LoopExhaustedEvent
- `LoopStep()` [17-20]: Constructor para YAML. | entra: ninguno | sale: instancia | efectos: ninguno | lanza: ninguno
- `LoopStep(string name, IReadOnlyList<IStep> steps, int maxIterations = 50)` [22-29]: Copia los pasos y fija MaxIterations; sin validar su valor. | entra: name, steps, maxIterations | sale: instancia | efectos: ninguno | lanza: ArgumentNullException si steps es null
- `Steps { get; init; }` [34-38]: Pasos por iteración. | entra: init | sale: List<IStep> | efectos: ninguno | lanza: ninguno
- `MaxIterations { get; init; }` [43-43]: Tope de iteraciones (50). | entra: init | sale: int | efectos: ninguno | lanza: ninguno
- `OutputIterationToMetadata { get; init; }` [49-49]: Clave de Metadata donde se escribe la iteración 1-based. | entra: init | sale: string? | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken ct)` [51-96]: Itera: escribe contador en Metadata, añade ruta Iter_i, ejecuta los pasos, quita la ruta; devuelve el resultado ante error/suspensión, desenvuelve BreakStepResult en StepResult<object>, y al agotar emite LoopExhaustedEvent y devuelve el último resultado. | entra: input, context | sale: IStepResult | efectos: Escribe context.Metadata, modifica la ruta del contexto, emite evento sin esperar | lanza: las del contexto/pasos hijos

### ParallelStep (class) — Core/Steps/ParallelStep.cs:17-152
Ejecución concurrente de ramas con contexto clonado y agregación de resultados/errores.
Colabora con: Parallel.ForEachAsync; PipelineContext.CloneForBranch/ExecuteStepsWithNextStepsAsync; BranchStartedEvent/BranchCompletedEvent; ParallelResult; ErrorStepResult
- `ParallelStep()` [22-25]: Constructor para YAML. | entra: ninguno | sale: instancia | efectos: ninguno | lanza: ninguno
- `ParallelStep(string name, params IStep[] branches)` [28-33]: Copia las ramas. | entra: name, branches | sale: instancia | efectos: ninguno | lanza: ninguno
- `Branches { get; init; }` [38-42]: Lista de ramas. | entra: init | sale: List<IStep> | efectos: ninguno | lanza: ninguno
- `MaxDegreeOfParallelism { get; init; }` [50-50]: Límite; <=0 usa ProcessorCount. | entra: init | sale: int | efectos: ninguno | lanza: ninguno
- `CreateParallelOptions(CancellationToken ct)` [56-61]: Construye ParallelOptions con scheduler por defecto y el grado indicado o ProcessorCount. | entra: ct | sale: ParallelOptions | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken ct)` [64-151]: Por rama: clona contexto, emite evento de inicio, ejecuta el paso sobre el clon, registra el resultado, anota errores y emite evento de fin; las excepciones se convierten en ErrorStepResult. Al final fija result.Error con los mensajes unidos. | entra: input, context | sale: ParallelResult | efectos: Eventos, logging, ejecución concurrente de pasos | lanza: Puede propagar excepciones si falla el envío de eventos dentro del catch

### ParallelStep<TIn> (class) — Core/Steps/ParallelStep.cs:158-166
Variante tipada que fija InputType a TIn (TIn : StepResult).
Colabora con: ParallelStep
- `ParallelStep(string name, params IStep[] branches)` [161-165]: Llama a la base y fija InputType. | entra: name, branches | sale: instancia | efectos: ninguno | lanza: ninguno

### StepBase (class) — Core/Steps/StepBase.cs:18-245
Base de todos los pasos con plantilla de hooks; encapsula creación de resultados mediante StepResultFactory.
Colabora con: StepResultFactory: crea resultados; IStepValidator: validadores; IEnrichable (Enrichable): delegado de hooks; PipelineContext.ServiceProvider: logger; StepError
- `StepBase(string name, Type inputType, Type outputType)` [18-25]: Constructor primario; prepara el Lazy del activator de resultado con el tipo de salida normalizado. | entra: name, tipos | sale: instancia | efectos: ninguno | lanza: ninguno
- `Name { get; protected set; }` [26-26]: Nombre. | entra: ctor | sale: string | efectos: ninguno | lanza: ninguno
- `MaxRetries { get; set; }` [27-27]: Reintentos (3). | entra: set | sale: int | efectos: ninguno | lanza: ninguno
- `Resume` [28-28]: this por defecto. | entra: ninguno | sale: IResume? | efectos: ninguno | lanza: ninguno
- `InputType { get; protected internal set; }` [29-36]: Tipo de entrada; el setter rechaza null. | entra: set | sale: Type | efectos: ninguno | lanza: ArgumentNullException
- `OutputType { get; protected internal set; }` [37-48]: Tipo de salida; si cambia reemplaza el Lazy del activator. | entra: set | sale: Type | efectos: Reemplaza _activatorInfo | lanza: ArgumentNullException
- `Logger { get; }` [53-53]: Devuelve _logger asignando NullLogger si aún es null. | entra: _logger | sale: ILogger | efectos: Fija _logger a NullLogger si no estaba inicializado | lanza: ninguno
- `InitializeLogger(PipelineContext context)` [59-64]: Crea el logger desde ILoggerFactory del ServiceProvider solo si _logger es null. | entra: context.ServiceProvider | sale: void | efectos: Asigna _logger | lanza: ninguno
- `Timeout { get; set; }` [69-69]: Timeout opcional. | entra: set | sale: TimeSpan? | efectos: ninguno | lanza: ninguno
- `Validators` [72-72]: Vista de los validadores. | entra: _validators | sale: IReadOnlyList<IStepValidator> | efectos: ninguno | lanza: ninguno
- `AddValidator(IStepValidator validator)` [78-82]: Añade un validador. | entra: validator | sale: void | efectos: Muta _validators | lanza: ArgumentNullException
- `InputSchema / OutputSchema` [91-96]: Nombres de esquema solo para YAML. | entra: init | sale: string? | efectos: ninguno | lanza: ninguno
- `Enrichable { get; set; }` [99-99]: Delegado opcional de enriquecimiento. | entra: set | sale: IEnrichable? | efectos: ninguno | lanza: ninguno
- `ValueType / Schema` [101-102]: Tipo de valor y esquema JSON del activator. | entra: _activatorInfo | sale: Type / JsonSchema? | efectos: ninguno | lanza: ninguno
- `CreateResult(object? value, IStepError? error = null)` [103-106]: Crea el resultado vía activator. | entra: value, error | sale: IStepResult | efectos: ninguno | lanza: Excepciones de la fábrica/cast
- `CreateErrorResult(string message, Exception? exception = null)` [108-115]: CreateResult(null, StepError). | entra: message, exception | sale: IStepResult | efectos: ninguno | lanza: ninguno
- `NormalizeResultType(Type resultType)` [117-122]: Sustituye interfaces/abstractos por StepResult<object?>. | entra: resultType | sale: Type | efectos: ninguno | lanza: ninguno
- `ExecuteAsync (abstracto)` [129-129]: Cuerpo del paso. | entra: input, context, attempt, lastStepResult, ct | sale: Task<IStepResult> | efectos: ninguno | lanza: ninguno
- `EnrichActivityBefore(...)` [135-138]: Delega en Enrichable. | entra: activity, input, context | sale: void | efectos: ninguno | lanza: ninguno
- `EnrichActivityAfter(...)` [144-147]: Delega en Enrichable. | entra: activity, result, context | sale: void | efectos: ninguno | lanza: ninguno
- `EnrichStartedEvent(...)` [153-156]: Delegado o baseEvent. | entra: baseEvent, input, context | sale: StepStartedEvent | efectos: ninguno | lanza: ninguno
- `EnrichCompletedEvent(...)` [162-165]: Delegado o baseEvent. | entra: baseEvent, result, context | sale: IStepCompletedEvent | efectos: ninguno | lanza: ninguno
- `FinalizeAsync(...)` [172-175]: Hook vacío. | entra: result, context, ct | sale: Task completada | efectos: ninguno | lanza: ninguno
- `Validate(IStepResult result, PipelineContext context, CancellationToken ct)` [182-193]: Ejecuta los validadores en orden y corta en el primer fallo. | entra: result, context | sale: (bool, string?) | efectos: Las de los validadores | lanza: ninguno
- `Implementaciones explícitas IEnrichable/IStep` [196-218]: Reenvían a los miembros protegidos; IStep.ExecuteAsync llama antes a InitializeLogger. | entra: los de cada método | sale: los de cada método | efectos: Inicializa logger | lanza: ninguno
- `IResume.SaveStateAsync / RestoreStateAsync (explícitas)` [220-222]: Reenvían a los hooks protegidos. | entra: callingStep/step, state, hitlResponse | sale: Task | efectos: ninguno | lanza: ninguno
- `SaveStateAsync()` [228-228]: Devuelve null por defecto. | entra: ninguno | sale: Task<object?> | efectos: ninguno | lanza: ninguno
- `RestoreStateAsync(object? state, object? hitlResponse)` [234-234]: No hace nada por defecto. | entra: state, hitlResponse | sale: Task completada | efectos: ninguno | lanza: ninguno
- `SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt)` [236-239]: Devuelve false. | entra: error, context, attempt | sale: bool | efectos: ninguno | lanza: ninguno
- `IFallbackable.SwitchToNextFallback (explícita)` [241-244]: Reenvía al método protegido. | entra: error, context, attempt | sale: bool | efectos: ninguno | lanza: ninguno

### TypedStep<TIn,TOut> (class) — Core/Steps/TypedStep.cs:10-57
Adaptador tipado sobre StepBase: ofrece ExecuteAsync con TIn/TOut y convierte la entrada cuando no coincide.
Colabora con: StepResultFactory.GetStepActivatorInfo/CreateStepResult; StepError; StepBase
- `_activatorInfo (campo)` [14-15]: Segundo Lazy del activator para TOut, distinto del de la base. | entra: typeof(TOut) | sale: StepActivatorInfo | efectos: ninguno | lanza: ninguno
- `CreateResult(object? value = null, IStepError? error = null) (new)` [17-20]: Crea TOut con el activator propio. | entra: value, error | sale: TOut | efectos: ninguno | lanza: InvalidCastException si no es TOut
- `CreateErrorResult(string message, Exception? exception = null) (new)` [22-29]: CreateResult con StepError. | entra: message, exception | sale: TOut | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(TIn, PipelineContext, int, TOut?, ct) (abstracto)` [31-31]: Cuerpo tipado. | entra: ver firma | sale: Task<TOut> | efectos: ninguno | lanza: ninguno
- `IStep<TIn,TOut>.ExecuteAsync (explícita)` [33-34]: Reenvía al abstracto. | entra: ver firma | sale: Task<TOut> | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken ct)` [36-56]: Si la entrada es asignable a TIn llama directamente; si no, intenta convertirla con StepResultFactory.CreateStepResult y ejecuta; cualquier excepción del bloque try se convierte en InvalidOperationException de tipo de entrada incorrecto. | entra: input, lastStepResult (cast a TOut?) | sale: IStepResult | efectos: ninguno | lanza: InvalidOperationException; InvalidCastException por (TOut?)lastStepResult

### InputMessage (class) — LLM/Results/InputMessage.cs:4-8
StepResult<string> vacío en el espacio de nombres global.
Colabora con: StepResult<string>
- `InputMessage(IStep step)` [4-4]: Constructor primario. | entra: step | sale: instancia | efectos: ninguno | lanza: ninguno

### Intention (class) — LLM/Results/Intention.cs:9-25
Resultado del análisis de intención (opción, motivo, feedback, prompt detallado, información extra).
Colabora con: UserFeedbackInfo; IntentionAnalyzerStep (option): consumidor
- `SelectedOption/SelectionReason/UserFeedback/DetailedPrompt/ExtraInformation` [11-24]: Propiedades init con valores por defecto. | entra: init | sale: valores | efectos: ninguno | lanza: ninguno

### UserFeedbackInfo (class) — LLM/Results/Intention.cs:28-38
Mensaje al usuario, indicador NeedMoreInfo, preguntas y permiso de texto libre.
Colabora con: Question
- `Message/NeedMoreInfo/Questions/AllowFreeText` [31-37]: Propiedades init. | entra: init | sale: valores | efectos: ninguno | lanza: ninguno

### UserChoiceChip (class) — LLM/Results/Intention.cs:41-51
Opción seleccionable (Id, Label, Description).
Colabora con: Question.ChoiceChips
- `Id/Label/Description` [44-50]: Propiedades init. | entra: init | sale: valores | efectos: ninguno | lanza: ninguno

### Question (class) — LLM/Results/Intention.cs:53-61
Pregunta de aclaración con indicador de selección múltiple y chips.
Colabora con: UserChoiceChip
- `ClarificationQuestion/Multiple/ChoiceChips` [56-60]: Propiedades init. | entra: init | sale: valores | efectos: ninguno | lanza: ninguno

### LlmStringStepResult (class) — LLM/Results/LlmStringStepResult.cs:11-29
Resultado string de un paso LLM con ToolCalls.
Colabora con: LlmStepResult<string>; ToolCall; RetryMiddleware: llama ValidateAsync
- `LlmStringStepResult(IStep step)` [11-11]: Constructor primario. | entra: step | sale: instancia | efectos: ninguno | lanza: ninguno
- `Content { get; init; }` [13-13]: Alias de Value. | entra: Value | sale: string? | efectos: ninguno | lanza: ninguno
- `ToolCalls { get; init; }` [14-14]: Llamadas a herramientas. | entra: init | sale: List<ToolCall>? | efectos: ninguno | lanza: ninguno
- `ValidateAsync(PipelineContext context)` [16-28]: Válido si hay contenido no vacío o ToolCalls no vacío; si no, error 'Response must have either Content or ToolCalls'. | entra: Content, ToolCalls | sale: Task<(bool, string?)> | efectos: ninguno | lanza: ninguno

### EventEnrichmentResult<TEvent> (struct) — Observability/EventEnrichmentResult.cs:7-10
Record struct readonly con evento y flag Suppress; TEvent : IProgressEvent.
Colabora con: IProgressEvent
- `EventEnrichmentResult(TEvent Event, bool Suppress)` [7-10]: Constructor posicional del record struct. | entra: Event, Suppress | sale: valor | efectos: ninguno | lanza: ninguno

## Subsistema `event` — Eventos de ejecución
Define eventos de inicio y finalización para pasos, herramientas, etiquetas y otras actividades observables.

- **Observability/Events/BranchCompletedEvent.cs** (evento): Registro sellado que notifica la finalizacion de una rama de ejecucion, con exito, duracion y mensaje de error. Expone la constante EventTypeName = "branch.completed".
- **Observability/Events/BranchStartedEvent.cs** (evento): Registro sellado que notifica el inicio de una rama. EventTypeName = "branch.started".
- **Observability/Events/LlmResponseEvent.cs** (evento): Evento de respuesta del LLM: chunk de streaming (FinishReason = Streaming) o respuesta final. Transporta contenido, motivo de finalizacion, tokens (total, prompt, completion, cacheados), coste USD, modelo, proveedor y tool calls.
- **Observability/Events/LoopExhaustedEvent.cs** (evento): Evento que indica que un bucle agoto sus iteraciones maximas.
- **Observability/Events/PipelineCompletedEvent.cs** (evento): Evento de finalizacion de pipeline con nombre, exito, duracion y error.
- **Observability/Events/PipelineResumedEvent.cs** (evento): Evento emitido tras reanudar un pipeline. EventTypeName = "pipeline.resumed".
- **Observability/Events/PipelineResumingEvent.cs** (evento): Evento emitido al empezar a reanudar un pipeline. EventTypeName = "pipeline.resuming".
- **Observability/Events/PipelineStartedEvent.cs** (evento): Evento de inicio de pipeline con nombre y numero de pasos.
- **Observability/Events/PipelineSuspendedEvent.cs** (evento): Evento de suspension HITL del pipeline a la espera de entrada humana.
- **Observability/Events/ProgressEventBase.cs** (contrato): Record abstracto base de los eventos de progreso. Implementa IProgressEvent con StepName, EventType abstracto, Timestamp (UtcNow en construccion), CorrelationId, SuppressFromUser y SchemaVersion "1.0".
- **Observability/Events/StepCachedHitEvent.cs** (evento): Evento de acierto de cache de un paso.
- **Observability/Events/StepCompletedEvent.cs** (evento): Evento de finalizacion de paso. Incluye exito, duracion, error, AdditionalData y el IStepResult.
- **Observability/Events/StepProgressEvent.cs** (evento): Evento generico de progreso para notificaciones personalizadas. Implementa IProgressEvent directamente, sin heredar de ProgressEventBase.
- **Observability/Events/StepRoutingEvent.cs** (evento): Evento de decision de enrutado de un router.
- **Observability/Events/StepStartedEvent.cs** (evento): Evento de inicio de paso con numero de intento y datos adicionales.
- **Observability/Events/StepTimeoutEvent.cs** (evento): Evento de timeout de un paso.
- **Observability/Events/StepValidationEvent.cs** (evento): Evento de validacion de resultado de un paso (estructural o semantica).
- **Observability/Events/TagCompletedEvent.cs** (evento): Evento de finalizacion de tag de streaming.
- **Observability/Events/TagStartedEvent.cs** (evento): Evento de inicio de tag de streaming.
- **Observability/Events/ToolCompletedEvent.cs** (evento): Evento de finalizacion de herramienta.
- **Observability/Events/ToolStartedEvent.cs** (evento): Evento de inicio de herramienta.

### BranchCompletedEvent (record) — Observability/Events/BranchCompletedEvent.cs:3-20
Registro sellado que notifica la finalizacion de una rama de ejecucion, con exito, duracion y mensaje de error. Expone la constante EventTypeName = "branch.completed".
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventTypeName` [5-5]: Constante de tipo de evento 'branch.completed' | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `StepPath` [7-7]: Ruta del paso (nullable) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `BranchName` [9-9]: Nombre de la rama | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `BranchId` [11-11]: Id de la rama | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Success` [13-13]: Resultado de la rama | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Duration` [15-15]: Duracion | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ErrorMessage` [17-17]: Mensaje de error opcional | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `EventType` [19-19]: Devuelve EventTypeName | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### BranchStartedEvent (record) — Observability/Events/BranchStartedEvent.cs:3-14
Registro sellado que notifica el inicio de una rama. EventTypeName = "branch.started".
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventTypeName` [5-5]: Constante 'branch.started' | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `StepPath` [7-7]: Ruta del paso | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `BranchName` [9-9]: Nombre de la rama | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `BranchId` [11-11]: Id de la rama | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `EventType` [13-13]: Devuelve EventTypeName | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### LlmResponseEvent (record) — Observability/Events/LlmResponseEvent.cs:10-68
Evento de respuesta del LLM: chunk de streaming (FinishReason = Streaming) o respuesta final. Transporta contenido, motivo de finalizacion, tokens (total, prompt, completion, cacheados), coste USD, modelo, proveedor y tool calls.
Colabora con: ProgressEventBase: base comun; FinishReason (LLM.Constants): enum del motivo; ToolCall (LLM.Models): elementos de ToolCalls
- `EventType` [13-13]: Devuelve la constante EventTypes.LlmResponse | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Content` [16-16]: Contenido del chunk o respuesta (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `FinishReason` [21-21]: Motivo de finalizacion; por defecto Streaming | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ChoiceIndex` [26-26]: Indice de la eleccion | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `RawFinishReason` [31-31]: Cadena cruda del proveedor | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `IsThinking` [34-34]: Marca contenido de razonamiento | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `TokensUsed` [37-37]: Tokens totales (solo final) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `PromptTokens` [43-43]: Tokens de prompt, nullable | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `CompletionTokens` [46-46]: Tokens de completion, nullable | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `CachedPromptTokens` [52-52]: Tokens de prompt servidos desde cache, nullable | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `CostUsd` [58-58]: Coste en USD, nullable | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Model` [61-61]: Modelo usado | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Provider` [64-64]: Proveedor usado | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ToolCalls` [67-67]: Lista mutable de ToolCall solicitadas | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### LoopExhaustedEvent (record) — Observability/Events/LoopExhaustedEvent.cs:3-12
Evento que indica que un bucle agoto sus iteraciones maximas.
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventType` [5-5]: Devuelve EventTypes.LoopExhausted | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `LoopName` [7-7]: Nombre del bucle | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `IterationsCompleted` [9-9]: Iteraciones realizadas | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `MaxIterations` [11-11]: Maximo configurado | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### PipelineCompletedEvent (record) — Observability/Events/PipelineCompletedEvent.cs:6-22
Evento de finalizacion de pipeline con nombre, exito, duracion y error.
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventType` [9-9]: Devuelve EventTypes.PipelineCompleted | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `PipelineName` [12-12]: Nombre (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Success` [15-15]: Resultado | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Duration` [18-18]: Duracion total | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ErrorMessage` [21-21]: Error opcional | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### PipelineResumedEvent (record) — Observability/Events/PipelineResumedEvent.cs:3-12
Evento emitido tras reanudar un pipeline. EventTypeName = "pipeline.resumed".
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventTypeName` [5-5]: Constante 'pipeline.resumed' | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `StepPath` [7-7]: Ruta del paso | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `TargetStepPath` [9-9]: Ruta del paso destino | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `EventType` [11-11]: Devuelve EventTypeName | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### PipelineResumingEvent (record) — Observability/Events/PipelineResumingEvent.cs:3-12
Evento emitido al empezar a reanudar un pipeline. EventTypeName = "pipeline.resuming".
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventTypeName` [5-5]: Constante 'pipeline.resuming' | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `StepPath` [7-7]: Ruta del paso | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `TargetStepPath` [9-9]: Ruta del paso destino | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `EventType` [11-11]: Devuelve EventTypeName | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### PipelineStartedEvent (record) — Observability/Events/PipelineStartedEvent.cs:6-16
Evento de inicio de pipeline con nombre y numero de pasos.
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventType` [9-9]: Devuelve EventTypes.PipelineStarted | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `PipelineName` [12-12]: Nombre (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `TotalSteps` [15-15]: Numero total de pasos | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### PipelineSuspendedEvent (record) — Observability/Events/PipelineSuspendedEvent.cs:6-20
Evento de suspension HITL del pipeline a la espera de entrada humana.
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventType` [9-9]: Devuelve EventTypes.PipelineSuspended | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `TargetStepPath` [12-12]: Ruta del paso destino (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `IsBranchSuspension` [14-14]: Indica suspension de una rama | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `BranchName` [16-16]: Rama suspendida | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ErrorMessage` [19-19]: Error que motivo la suspension | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### ProgressEventBase (record) — Observability/Events/ProgressEventBase.cs:8-27
Record abstracto base de los eventos de progreso. Implementa IProgressEvent con StepName, EventType abstracto, Timestamp (UtcNow en construccion), CorrelationId, SuppressFromUser y SchemaVersion "1.0".
Colabora con: IProgressEvent: contrato que implementa; 19 eventos derivados
- `StepName` [11-11]: Nombre del paso; por defecto cadena vacia | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `EventType` [14-14]: Propiedad abstracta de tipo de evento | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Timestamp` [17-17]: Marca temporal inicializada a DateTimeOffset.UtcNow | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `CorrelationId` [20-20]: Id de correlacion opcional | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `SuppressFromUser` [23-23]: Oculta el evento al usuario | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `SchemaVersion` [26-26]: Version de esquema fija '1.0' | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### StepCachedHitEvent (record) — Observability/Events/StepCachedHitEvent.cs:6-13
Evento de acierto de cache de un paso.
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventType` [9-9]: Devuelve literal 'step.cached_hit' | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `StepPath` [12-12]: Ruta del paso | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### StepCompletedEvent (record) — Observability/Events/StepCompletedEvent.cs:8-33
Evento de finalizacion de paso. Incluye exito, duracion, error, AdditionalData y el IStepResult.
Colabora con: IStepCompletedEvent: contrato implementado; IStepResult: resultado referenciado; ObservabilityMiddleware: lo emite
- `EventType` [11-11]: Devuelve EventTypes.StepCompleted | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `StepPath` [13-13]: Ruta del paso | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Success` [16-16]: Resultado | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Duration` [19-19]: Duracion | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ErrorMessage` [22-22]: Error opcional | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `AdditionalData` [28-28]: Diccionario mutable inicializado vacio | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `StepResult` [32-32]: Resultado del paso, nullable | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### StepProgressEvent (record) — Observability/Events/StepProgressEvent.cs:6-37
Evento generico de progreso para notificaciones personalizadas. Implementa IProgressEvent directamente, sin heredar de ProgressEventBase.
Colabora con: IProgressEvent: contrato implementado
- `StepName` [9-9]: Nombre del paso (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `EventType` [12-12]: Tipo de evento (required, settable) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Timestamp` [15-15]: UtcNow por defecto | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `CorrelationId` [18-18]: Id de correlacion | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `SchemaVersion` [21-21]: '1.0' | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `SuppressFromUser` [24-24]: Oculta al usuario | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Message` [27-27]: Mensaje (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `CurrentProgress` [30-30]: Progreso actual opcional | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `TotalProgress` [33-33]: Progreso total opcional | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Metadata` [36-36]: Metadatos opcionales | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### StepRoutingEvent (record) — Observability/Events/StepRoutingEvent.cs:6-16
Evento de decision de enrutado de un router.
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventType` [9-9]: Devuelve EventTypes.StepRouting | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `SelectedRoute` [12-12]: Ruta elegida (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `RoutingReason` [15-15]: Motivo (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### StepStartedEvent (record) — Observability/Events/StepStartedEvent.cs:6-18
Evento de inicio de paso con numero de intento y datos adicionales.
Colabora con: ProgressEventBase: base; StepBase / IEnrichable / ObservabilityMiddleware: lo crean o enriquecen
- `EventType` [9-9]: Devuelve EventTypes.StepStarted | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `StepPath` [11-11]: Ruta del paso | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `AttemptNumber` [14-14]: Intento, por defecto 1 | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `AdditionalData` [17-17]: Diccionario nullable | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### StepTimeoutEvent (record) — Observability/Events/StepTimeoutEvent.cs:6-16
Evento de timeout de un paso.
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventType` [9-9]: Devuelve literal 'step.timeout' | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `StepPath` [12-12]: Ruta del paso | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ConfiguredTimeout` [15-15]: Timeout configurado | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### StepValidationEvent (record) — Observability/Events/StepValidationEvent.cs:6-24
Evento de validacion de resultado de un paso (estructural o semantica).
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventType` [9-9]: Devuelve EventTypes.StepValidation | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `IsValid` [12-12]: Resultado | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `StepPath` [14-14]: Ruta del paso | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ValidationError` [17-17]: Error de validacion | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ValidationType` [20-20]: Tipo como string; por defecto Structural | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `AttemptNumber` [23-23]: Intento, por defecto 1 | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### TagCompletedEvent (record) — Observability/Events/TagCompletedEvent.cs:6-25
Evento de finalizacion de tag de streaming.
Colabora con: ProgressEventBase: base; TagHandlerBase: lo crea/enriquece
- `EventType` [9-9]: Devuelve EventTypes.TagCompleted | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `TagName` [12-12]: Nombre (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Success` [15-15]: Exito; por defecto true | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Duration` [18-18]: Duracion | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ErrorMessage` [21-21]: Error opcional | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `AdditionalData` [24-24]: Diccionario nullable | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### TagStartedEvent (record) — Observability/Events/TagStartedEvent.cs:6-19
Evento de inicio de tag de streaming.
Colabora con: ProgressEventBase: hereda las propiedades comunes (StepName, Timestamp, CorrelationId, SuppressFromUser, SchemaVersion)
- `EventType` [9-9]: Devuelve EventTypes.TagStarted | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `TagName` [12-12]: Nombre (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Attributes` [15-15]: Atributos del tag | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `AdditionalData` [18-18]: Diccionario nullable | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### ToolCompletedEvent (record) — Observability/Events/ToolCompletedEvent.cs:6-25
Evento de finalizacion de herramienta.
Colabora con: ProgressEventBase: base; LlmTool: lo crea/enriquece
- `EventType` [9-9]: Devuelve EventTypes.ToolCompleted | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ToolName` [12-12]: Nombre (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Success` [15-15]: Resultado | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `Duration` [18-18]: Duracion | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ErrorMessage` [21-21]: Error | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `AdditionalData` [24-24]: Diccionario nullable | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

### ToolStartedEvent (record) — Observability/Events/ToolStartedEvent.cs:6-16
Evento de inicio de herramienta.
Colabora con: ProgressEventBase: base; LlmTool: lo crea/enriquece
- `EventType` [9-9]: Devuelve EventTypes.ToolStarted | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `ToolName` [12-12]: Nombre (required) | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno
- `AdditionalData` [15-15]: Diccionario nullable | entra: valor asignado por el inicializador del emisor | sale: valor almacenado | efectos: ninguno | lanza: ninguno

## Subsistema `execution` — Ejecución de pipelines
Reúne pipelines, runtimes y middleware de ejecución, incluidos componentes para pasos YAML.

- **Core/Abstractions/IPipelineMiddleware.cs** (contract): Define IPipelineMiddleware, el contrato de middleware de pasos con la forma de ASP.NET Core: recibe paso, entrada, contexto, delegado next (que acepta un CancellationToken para poder sustituirlo) y token. Es el punto de extensión por el que se componen Observability, Timeout, Retry y los middlewares de usuario.
- **Core/Constants/AITaskAgentConstants.EventTypes.cs** (constants): Fragmento parcial de AITaskAgentConstants con la clase anidada EventTypes: cadenas const para los tipos de evento de observabilidad (pipeline.started, step.completed, tag.started, loop.exhausted, etc.). Solo datos, sin lógica.
- **Core/Constants/AITaskAgentConstants.TelemetryTags.cs** (constants): Fragmento parcial de AITaskAgentConstants con TelemetryTags: nombres de tags OpenTelemetry (pipeline.name, step.name, llm.model...) y nombre/versión de ActivitySource y Meter. Mezcla etiquetas con constantes de identidad de la fuente de telemetría. Solo datos.
- **Core/Constants/AITaskAgentConstants.ValidationTypes.cs** (constants): Fragmento parcial de AITaskAgentConstants con ValidationTypes: los dos valores 'structural' y 'semantic' que RetryMiddleware pone en StepValidationEvent.ValidationType.
- **Core/Execution/IPipeline.cs** (contract): Define IPipeline, la superficie que usan las aplicaciones: dos timeouts por defecto y ExecuteAsync<T> que ejecuta una lista de pasos con composición de middleware, contexto opcional, timeout de pipeline y suscriptores de eventos por ejecución.
- **Core/Execution/IPipelineRuntime.cs** (contract): Define IPipelineRuntime, la superficie que el motor ofrece al contexto en ejecución: LoggerFactory y ExecuteStepsWithNextStepsAsync, usada por pasos compuestos (Parallel, Loop, routers) para ejecutar hijos con la misma cadena de middleware. Existe para evitar service location de IPipeline desde el contexto.
- **Core/Execution/Middlewares/ObservabilityMiddleware.cs** (implementation): Middleware interno que abre una Activity por paso, emite StepStartedEvent/StepCompletedEvent, registra métricas (ejecuciones, duración, errores) y delega el enriquecimiento en IEnrichable. Cubre el camino de excepción volviendo a emitir un evento de fin y relanzando.
- **Core/Execution/Middlewares/RetryMiddleware.cs** (implementation): Middleware terminal de la cadena: no llama a next, ejecuta step.ExecuteAsync en un bucle de intentos. Reintenta errores sin excepción, validación estructural (IStepResult.ValidateAsync) y semántica (IStep.ValidateAsync); las excepciones y los errores con OriginalException no se reintentan y activan el cambio de fallback si el paso es IFallbackable.
- **Core/Execution/Middlewares/TimeoutMiddleware.cs** (implementation): Middleware que impone un timeout global (no por intento) sobre todo lo que cuelga de next, usando un CTS enlazado. Si salta el timeout y no hay cancelación externa, emite StepTimeoutEvent y devuelve un ErrorStepResult.
- **Core/Execution/Pipeline.cs** (implementation): Motor de ejecución de pipelines: valida nombres, resuelve la entrada (incluida la reanudación tras suspensión HITL), aplica timeout de pipeline, abre telemetría, compone la cadena User -> Observability -> Timeout -> Retry y ejecuta los pasos secuencialmente con soporte de NextSteps, cache diferida de resultados por ruta y suspensión/reanudación. Implementa también IPipelineRuntime.
- **Core/Execution/PipelineOptions.cs** (model): Opciones de timeout por defecto (60 min de pipeline, 30 min de paso). El comentario habla de IOptions y de Pipeline.Configure, pero Pipeline crea su propia instancia.
- **Core/Execution/PipelineRuntimeBase.cs** (contract): Base abstracta de motores de pipeline: implementa IPipelineRuntime garantizando LoggerFactory no nulo (NullLoggerFactory como respaldo) y declara abstracto ExecuteStepsWithNextStepsAsync. No implementa IPipeline a propósito.
- **Core/Execution/ServiceProviderPipelineMiddlewareExtensions.cs** (infrastructure): Extensiones de IServiceProvider para registrar middlewares de pipeline: resuelven la instancia del contenedor o la construyen con ActivatorUtilities y la guardan en PipelineMiddlewareRegistry (registro externo al lote).
- **YAML/Abstractions/IYamlStep.cs** (contract): Interfaz marcadora para pasos deserializables desde YAML: StepId, InputSchema, OutputSchema y DependsOn, todas init.
- **YAML/Attributes/YamlStepTagAttribute.cs** (model): Atributo [YamlStepTag(nombre)] que marca una clase como descubrible por YamlPipelineFactory mediante el tag YAML !nombre.
- **YAML/Compilation/SchemaCompiler.cs** (implementation): Compila JSON Schemas a tipos C# en memoria: NJsonSchema genera el POCO, se renombra a {Nombre}Data, se genera un wrapper {Nombre} que hereda de LlmStepResult<{Nombre}Data> con ValidateAsync opcional vía función JS en V8, Roslyn compila a un ensamblado que se carga en AssemblyLoadContext.Default. Mantiene un caché por nombre.
- **YAML/Execution/YamlPipelineFactory.cs** (implementation): Construye pipelines declarados en YAML: lee y une ficheros, descubre por reflexión los IYamlStep con [YamlStepTag], deserializa con YamlDotNet usando DI como fábrica de objetos, resuelve roles LLM, inyecta tipos compilados de entrada/salida, valida el DAG (ciclos, dependencias, compatibilidad de esquemas) y ordena topológicamente. Contiene también YamlPipelineDocument (modelo interno) y YamlPipeline (resultado ejecutable).
- **YAML/Steps/YamlLlmStep.cs** (implementation): Paso LLM deserializable desde YAML (!LlmStep). Hereda ejecución, reintentos y fallback de BaseLlmStep; se construye con un rol marcador (NullLlmService) que la factoría sustituye con ApplyRole; sobrescribe los hooks de mensaje de usuario, sistema y contexto de conversación.

### IPipelineMiddleware (interface) — Core/Abstractions/IPipelineMiddleware.cs:9-26
Contrato de middleware de ejecución de un paso. Recibe el paso, su entrada, el contexto, un delegado next(CancellationToken) y el token de cancelación, y devuelve el IStepResult.
Colabora con: IStep, IStepResult, PipelineContext: parámetros del contrato; Pipeline.BuildMiddlewareChain: compone las implementaciones en una cadena
- `InvokeAsync(IStep, IStepResult, PipelineContext, Func<CancellationToken,Task<IStepResult>>, CancellationToken)` [20-25]: Ejecuta la lógica del middleware y decide si invoca next. | entra: Paso, entrada previa, contexto, delegado al siguiente eslabón y token. | sale: Task<IStepResult> con el resultado del paso o uno sustituido por el middleware. | efectos: depende de la implementación | lanza: ninguno

### AITaskAgentConstants (class) — Core/Constants/AITaskAgentConstants.EventTypes.cs:6-52
Clase estática parcial que agrupa constantes del framework; en este fichero aporta EventTypes.
Colabora con: EventTypes: clase anidada

### EventTypes (class) — Core/Constants/AITaskAgentConstants.EventTypes.cs:11-51
Constantes string con los tipos de evento de observabilidad (pipeline.*, step.*, tool.*, llm.response, tag.*, loop.exhausted).
Colabora con: Core.Base.Metrics y Core.Base.Telemetry la usan según el grafo (relaciones entrantes)
- `PipelineStarted..LoopExhausted (13 const string)` [13-50]: Valores literales de tipo de evento. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### AITaskAgentConstants (class) — Core/Constants/AITaskAgentConstants.TelemetryTags.cs:6-71
Fragmento parcial de la clase estática de constantes; aporta TelemetryTags.
Colabora con: TelemetryTags: clase anidada

### TelemetryTags (class) — Core/Constants/AITaskAgentConstants.TelemetryTags.cs:12-70
Nombres de tags OpenTelemetry y constantes de identidad de ActivitySource y Meter.
Colabora con: ObservabilityMiddleware y Pipeline las usan en SetTag y en las métricas
- `PipelineName..PipelineTimeoutMs (const string)` [15-69]: Valores literales de tag, nombre y versión de fuentes de telemetría. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### AITaskAgentConstants (class) — Core/Constants/AITaskAgentConstants.ValidationTypes.cs:6-19
Fragmento parcial de la clase estática de constantes; aporta ValidationTypes.
Colabora con: ValidationTypes: clase anidada

### ValidationTypes (class) — Core/Constants/AITaskAgentConstants.ValidationTypes.cs:11-18
Constantes 'structural' y 'semantic' para StepValidationEvent.ValidationType.
Colabora con: RetryMiddleware las asigna en los eventos de validación fallida
- `Structural / Semantic (const string)` [14-17]: Valores literales del tipo de validación. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### IPipeline (interface) — Core/Execution/IPipeline.cs:11-48
Superficie de la aplicación para ejecutar pipelines; documentado como singleton en DI.
Colabora con: PipelineContext, IStep, IProgressEvent: parámetros; Pipeline: implementación
- `DefaultPipelineTimeout { get; }` [16-16]: Timeout por defecto de la ejecución completa. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `DefaultStepTimeout { get; }` [21-21]: Timeout por defecto de cada paso. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `ExecuteAsync<T>(string, IReadOnlyList<IStep>, T, PipelineContext?, TimeSpan?, CancellationToken, IEnumerable<Func<IProgressEvent,CancellationToken,Task>>?)` [40-47]: Ejecuta los pasos secuencialmente con middleware. | entra: Nombre, pasos, entrada, contexto opcional, timeout opcional, token, suscriptores opcionales. | sale: IStepResult del último paso ejecutado. | efectos: según implementación | lanza: ninguno

### IPipelineRuntime (interface) — Core/Execution/IPipelineRuntime.cs:21-49
Superficie que el motor ofrece al contexto de ejecución y a los pasos compuestos: logger factory y ejecución de sub-pipelines respetando NextSteps.
Colabora con: PipelineContext: mantiene la referencia al runtime (ciclo señalado por el grafo); Pasos compuestos: lo invocan para ejecutar hijos
- `LoggerFactory { get; }` [27-27]: Fábrica de loggers no nula. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `ExecuteStepsWithNextStepsAsync(IReadOnlyList<IStep>, IStepResult, PipelineContext, string, CancellationToken, bool isBranchScope=false)` [42-48]: Ejecuta un sub-pipeline con la misma cadena de middleware. | entra: Pasos, resultado inicial, contexto (clon en ramas), nombre y token. | sale: IStepResult final del sub-pipeline. | efectos: según implementación | lanza: ninguno

### ObservabilityMiddleware (class) — Core/Execution/Middlewares/ObservabilityMiddleware.cs:15-138
Middleware que traza, mide y emite eventos de inicio y fin por paso, con ganchos de enriquecimiento si el paso implementa IEnrichable.
Colabora con: Telemetry.Source: crea la Activity; Metrics: contadores e histograma de pasos; IEnrichable: enriquece actividad y eventos; PipelineContext.SendEventAsync: emite eventos; StepStartedEvent, StepCompletedEvent: eventos emitidos
- `InvokeAsync(IStep, IStepResult, PipelineContext, Func<CancellationToken,Task<IStepResult>>, CancellationToken)` [17-137]: Abre Activity, emite StepStartedEvent con AttemptNumber=1, invoca next, registra métricas y emite StepCompletedEvent; en excepción registra métricas, emite un evento de fin con Success=false y relanza. | entra: Paso, entrada, contexto, next y token del llamador. | sale: El IStepResult devuelto por next. | efectos: Activity, tags, métricas globales, dos eventos al canal del contexto. | lanza: Relanza cualquier excepción de next o del envío de eventos.

### RetryMiddleware (class) — Core/Execution/Middlewares/RetryMiddleware.cs:14-167
Middleware terminal que ejecuta el paso en un bucle de hasta MaxRetries intentos con tres causas de reintento (error sin excepción, validación estructural, validación semántica) y gestión de fallback.
Colabora con: IFallbackable: SwitchToNextFallback tras error fatal; ErrorStepResult: FromException/FromMessage; StepValidationEvent, StepStartedEvent: eventos por intento; Metrics.StepRetries: contador; IStep.FinalizeAsync/ValidateAsync: cierre y validación semántica
- `RetryMiddleware(ILogger<RetryMiddleware>) [constructor primario]` [14-16]: Guarda el logger. | entra: ILogger inyectado por Pipeline.CreateInternalMiddlewares. | sale: ninguno | efectos: ninguno | lanza: ninguno
- `InvokeAsync(IStep, IStepResult, PipelineContext, Func<CancellationToken,Task<IStepResult>>, CancellationToken)` [18-166]: Itera intentos; ejecuta step.ExecuteAsync directamente (no usa next); captura excepciones y las convierte en ErrorStepResult sin reintentar; reintenta errores sin OriginalException y fallos de validación; llama a FinalizeAsync al terminar. | entra: Paso, entrada, contexto, token (ya enlazado con el timeout), intento y resultado previo hacia el paso. | sale: Último resultado, éxito o error final. | efectos: Eventos StepStarted (intentos > 1) y StepValidation, métricas de reintento, logs, mutación de result.Error, FinalizeAsync del paso. | lanza: No propaga excepciones del paso; las excepciones de ValidateAsync fuera del try sí escapan.

### TimeoutMiddleware (class) — Core/Execution/Middlewares/TimeoutMiddleware.cs:16-65
Middleware que aplica un timeout global al tramo Timeout -> Retry -> Paso, con el timeout del paso o el valor por defecto.
Colabora con: PipelineContext.SendEventAsync: emite StepTimeoutEvent; ErrorStepResult.FromMessage: resultado de error; IStep.Timeout: sobrescribe el valor por defecto
- `TimeoutMiddleware(ILogger<TimeoutMiddleware>, TimeSpan? defaultTimeout=null) [constructor primario]` [16-21]: Guarda logger y timeout por defecto. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `InvokeAsync(IStep, IStepResult, PipelineContext, Func<CancellationToken,Task<IStepResult>>, CancellationToken)` [23-64]: Sin timeout delega en next; con timeout crea CTS enlazado con CancelAfter y captura OperationCanceledException causada solo por el CTS. | entra: Paso, entrada, contexto, next y token externo. | sale: Resultado de next o ErrorStepResult de timeout. | efectos: Log de advertencia y StepTimeoutEvent al canal del contexto. | lanza: Propaga excepciones distintas del timeout y la cancelación externa.

### Pipeline (class) — Core/Execution/Pipeline.cs:22-446
Motor de ejecución de pipelines: resuelve entrada y reanudación, aplica timeouts, telemetría y eventos, compone la cadena de middleware y ejecuta pasos con NextSteps, cache de resultados por ruta y suspensión HITL.
Colabora con: PipelineMiddlewareRegistry: middlewares de usuario (estático, externo); ObservabilityMiddleware, TimeoutMiddleware, RetryMiddleware: middlewares internos; PipelineContext/PipelineContextFactory: contexto y rutas; StepValidation, StepResultFactory, ErrorStepResult, EmptyStep: apoyo; IEventChannel: canal de eventos local; IResume: guardado y restauración de estado de pasos suspendidos
- `DefaultPipelineTimeout / DefaultStepTimeout { get; }` [31-37]: Propiedades de solo lectura asignadas en el constructor. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `Pipeline(TimeSpan?, TimeSpan?, ILoggerFactory?)` [42-54]: Inicializa runtime base, logger, un PipelineOptions propio y los timeouts por defecto (parámetro o valor de PipelineOptions). | entra: Timeouts opcionales y logger factory opcional. | sale: Instancia con ambos timeouts siempre con valor. | efectos: ninguno | lanza: ninguno
- `delegate StepExecutor(IStep, IStepResult, PipelineContext, CancellationToken)` [60-60]: Delegado de la cadena de middleware ya compuesta. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `PendingResumeKey(PipelineContext)` [67-68]: Clave de metadata por CorrelationId para la respuesta de reanudación pendiente. | entra: Contexto. | sale: string | efectos: ninguno | lanza: ninguno
- `ResumeStateKey(PipelineContext)` [74-75]: Clave de metadata por CorrelationId para el estado persistido de reanudación. | entra: Contexto. | sale: string | efectos: ninguno | lanza: ninguno
- `ExecuteAsync<T>(string, IReadOnlyList<IStep>, T, PipelineContext?, TimeSpan?, CancellationToken, IEnumerable<...>?)` [83-222]: Valida argumentos y nombres únicos, detecta reanudación, crea el contexto si falta y le asigna Runtime, construye la cadena, crea CTS de timeout enlazado, abre Activity, emite PipelineStarted/Completed y ejecuta los pasos; en finally libera el CTS enlazado y el canal de eventos si difiere del singleton. | entra: Pasos no vacíos, entrada no nula (si es reanudación debe ser IStepResult), contexto opcional. | sale: IStepResult final (puede ser error o suspendido). | efectos: Muta context.Runtime y Metadata, eventos, métricas, logs, Dispose del canal de eventos. | lanza: ArgumentNullException, ArgumentException (sin pasos), InvalidCastException en reanudación con entrada no IStepResult, excepciones de StepValidation y de envío de eventos.
- `ExecuteStepsWithNextStepsAsync(IReadOnlyList<IStep>, IStepResult, PipelineContext, string, CancellationToken, bool=false) [override público]` [230-241]: Reconstruye la cadena de middleware y delega en la sobrecarga privada; sin pipelineActivity. | entra: Parámetros de IPipelineRuntime. | sale: IStepResult del sub-pipeline. | efectos: Asignación de listas y middlewares por llamada. | lanza: ninguno
- `ExecuteStepsWithNextStepsAsync(... StepExecutor, string, ILogger, Activity?, CancellationToken, bool) [privado estático]` [243-351]: Recorre pasos: ejecuta, detiene en error, gestiona suspensión (guarda estado, emite PipelineSuspendedEvent, cachea en StepResults), ejecuta NextSteps recursivamente con ruta anidada y cachea el resultado solo tras éxito completo. | entra: Pasos, resultado previo como entrada, contexto con StepResults y Metadata. | sale: Último resultado, error o suspensión. | efectos: StepResults, Metadata, CurrentStepPath, NextSteps.Clear(), eventos, logs. | lanza: InvalidOperationException si SuspendInfo llega sin IResume en el paso.
- `ExecuteStepAsync(IStep, IStepResult, PipelineContext, StepExecutor, ILogger, CancellationToken)` [357-411]: Devuelve el resultado cacheado (limpiando NextSteps) o restaura un paso suspendido con la respuesta pendiente; en otro caso ejecuta la cadena; convierte toda excepción en ErrorStepResult. | entra: Paso, entrada, contexto y cadena. | sale: IStepResult cacheado, nuevo o de error. | efectos: Elimina claves de Metadata; RestoreStateAsync del paso; logs. | lanza: No propaga excepciones (incluida la cancelación).
- `BuildMiddlewareChain(List<IPipelineMiddleware>)` [416-431]: Compone los middlewares en orden inverso con un delegado terminal que llama step.ExecuteAsync. | entra: Lista ordenada de middlewares. | sale: StepExecutor compuesto. | efectos: ninguno | lanza: ninguno
- `CreateInternalMiddlewares()` [440-445]: Crea Observability, Timeout y Retry con sus loggers. | entra: Estado del pipeline (loggerFactory, DefaultStepTimeout). | sale: Lista de tres middlewares nuevos. | efectos: Instancia loggers y middlewares en cada llamada. | lanza: ninguno

### PipelineOptions (class) — Core/Execution/PipelineOptions.cs:7-21
Opciones de timeout por defecto del pipeline y del paso.
Colabora con: Pipeline: lo instancia con new en su constructor
- `SectionName` [9-9]: Constante 'Pipeline'. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `DefaultPipelineTimeout { get; set; } = 60 min` [14-14]: Timeout por defecto del pipeline. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `DefaultStepTimeout { get; set; } = 30 min` [20-20]: Timeout por defecto del paso. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### PipelineRuntimeBase (class) — Core/Execution/PipelineRuntimeBase.cs:17-37
Base abstracta que implementa IPipelineRuntime con LoggerFactory garantizado no nulo.
Colabora con: Pipeline: la hereda; NullLoggerFactory: respaldo
- `PipelineRuntimeBase(ILoggerFactory?)` [23-24]: Asigna LoggerFactory o NullLoggerFactory.Instance. | entra: Logger factory opcional. | sale: Instancia inicializada. | efectos: ninguno | lanza: ninguno
- `LoggerFactory { get; }` [27-27]: Propiedad de solo lectura. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `ExecuteStepsWithNextStepsAsync(...) [abstracto]` [30-36]: Contrato abstracto para ejecutar sub-pipelines. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### ServiceProviderPipelineMiddlewareExtensions (class) — Core/Execution/ServiceProviderPipelineMiddlewareExtensions.cs:9-64
Extensiones de IServiceProvider para registrar middlewares en el registro estático PipelineMiddlewareRegistry.
Colabora con: PipelineMiddlewareRegistry.Register: destino (externo); ActivatorUtilities: construcción cuando el contenedor no lo tiene
- `RegisterPipelineMiddleware<TMiddleware>(this IServiceProvider)` [18-29]: Resuelve o construye el middleware y lo registra. | entra: Proveedor no nulo. | sale: El mismo IServiceProvider. | efectos: Escribe en el registro estático. | lanza: ArgumentNullException; excepciones de construcción.
- `RegisterPipelineMiddlewares(this IServiceProvider, params Type[])` [38-63]: Valida cada tipo, lo resuelve o construye y lo registra. | entra: Proveedor y tipos que deben implementar IPipelineMiddleware. | sale: El mismo IServiceProvider. | efectos: Escribe en el registro estático. | lanza: ArgumentNullException, ArgumentException si un tipo no implementa la interfaz.

### IYamlStep (interface) — YAML/Abstractions/IYamlStep.cs:7-23
Contrato marcador de pasos YAML con identificador, esquemas de entrada/salida y dependencias.
Colabora con: YamlPipelineFactory: lo consume; YamlLlmStep: lo implementa
- `StepId {get;init;}` [9-9]: Identificador del paso. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `InputSchema {get;init;}` [15-15]: Nombre del esquema de entrada, nullable. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `OutputSchema {get;init;}` [20-20]: Nombre del esquema de salida, nullable. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `DependsOn {get;init;}` [22-22]: Ids de pasos de los que depende. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### YamlStepTagAttribute (class) — YAML/Attributes/YamlStepTagAttribute.cs:7-10
Atributo con el nombre de tag YAML que identifica un tipo de paso.
Colabora con: YamlPipelineFactory.BuildDeserializer: lo lee por reflexión
- `YamlStepTagAttribute(string tagName) / TagName` [7-9]: Guarda el nombre de tag. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### SchemaCompiler (class) — YAML/Compilation/SchemaCompiler.cs:22-232
Compila JSON Schemas a tipos C# en memoria, con caché por nombre, y los carga en el contexto de carga por defecto.
Colabora con: NJsonSchema: parseo y generación del POCO; Roslyn: compilación; ClearScript V8: validación JS dentro del código generado; LlmStepResult<>: base del wrapper; YamlPipelineFactory: consume GetCompiledType
- `_compiled (ConcurrentDictionary<string,Type>)` [24-24]: Caché de tipos por nombre de esquema. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `CompileFromFolderAsync(string)` [27-37]: Lee todos los *.json recursivamente y compila cada uno en paralelo usando el nombre de fichero como nombre de esquema. | entra: Carpeta existente. | sale: Task completada cuando todos compilan. | efectos: Lectura de ficheros, compilación y carga de ensamblados. | lanza: DirectoryNotFoundException, InvalidOperationException de compilación, AggregateException vía Task.WhenAll.
- `CompileAsync(string name, string jsonSchema, string? jsValidateFunction=null)` [43-67]: Devuelve el tipo cacheado o genera, enriquece, compila y cachea. | entra: Nombre (se usa como identificador C# y nombre de ensamblado) y esquema. | sale: Type del wrapper generado. | efectos: Carga un ensamblado en AssemblyLoadContext.Default y escribe en el caché. | lanza: Errores de NJsonSchema, InvalidOperationException de compilación.
- `GetCompiledType(string)` [70-75]: Busca el tipo en el caché. | entra: Nombre ya compilado. | sale: Type. | efectos: ninguno | lanza: KeyNotFoundException si no se compiló.
- `BuildValidationMethod(string?)` [79-97]: Devuelve el cuerpo C# de ValidateAsync: siempre válido si no hay JS; si hay JS, crea un V8ScriptEngine, expone context y self, ejecuta el JS e invoca validate. | entra: Función JS opcional. | sale: Texto de código C#. | efectos: ninguno (el código generado crea un motor V8 por validación) | lanza: ninguno
- `EnrichGeneratedCode(string, string, string)` [99-143]: Renombra el POCO a {Nombre}Data y añade el wrapper {Nombre} heredando LlmStepResult<{Nombre}Data> con dos constructores y ValidateAsync. | entra: Nombre, código crudo y cuerpo de validación. | sale: Código fuente C# completo. | efectos: ninguno | lanza: ninguno
- `RenameGeneratedClass(string, string, string)` [149-157]: Reemplaza por regex la declaración 'class Nombre'. | entra: Código, nombre antiguo y nuevo. | sale: Código con la clase renombrada. | efectos: ninguno | lanza: ninguno
- `CompileAndLoadAsync(string, string)` [159-189]: Compila con Roslyn a memoria y carga el ensamblado en AssemblyLoadContext.Default; devuelve el tipo. | entra: Nombre y fuente. | sale: Type cargado. | efectos: Carga de ensamblado no descargable. | lanza: InvalidOperationException si falla la compilación o no encuentra el tipo.
- `GetRequiredReferences()` [191-231]: Reúne referencias de metadatos (runtime, Newtonsoft, framework, V8, DataAnnotations, netstandard/System.Runtime/System.Collections). | entra: ninguno | sale: MetadataReference[]. | efectos: Lee ficheros de ensamblados por su Location. | lanza: Excepciones de CreateFromFile fuera del try (el de V8 se traga).

### YamlPipelineDocument (class) — YAML/Execution/YamlPipelineFactory.cs:20-24
Modelo interno del documento YAML: nombre y lista de IYamlStep.
Colabora con: YamlPipelineFactory: deserializa a este tipo
- `Name {get;init;}` [22-22]: Nombre del pipeline. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `Steps {get;init;}` [23-23]: Lista de pasos deserializados. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### YamlPipeline (class) — YAML/Execution/YamlPipelineFactory.cs:29-39
Pipeline construido y listo para ejecutar: nombre, pasos ordenados y el IPipeline.
Colabora con: IPipeline.ExecuteAsync: delega la ejecución
- `YamlPipeline(string, IReadOnlyList<IStep>, IPipeline) [constructor primario]` [29-29]: Guarda nombre, pasos y pipeline. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `Name / Steps` [31-32]: Propiedades de solo lectura. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `ExecuteAsync<T>(T, PipelineContext?, CancellationToken)` [34-38]: Ejecuta pipeline.ExecuteAsync con el nombre y los pasos; no reenvía timeout ni suscriptores. | entra: Entrada, contexto opcional y token. | sale: IStepResult final. | efectos: Según Pipeline.ExecuteAsync. | lanza: Según Pipeline.ExecuteAsync.

### YamlPipelineFactory (class) — YAML/Execution/YamlPipelineFactory.cs:52-232
Factoría que convierte YAML en un YamlPipeline validado y ordenado topológicamente.
Colabora con: IServiceProvider: fábrica de objetos de YamlDotNet; SchemaCompiler: tipos de entrada/salida; IPipeline: motor de ejecución; LlmEnvironment: roles LLM; YamlLlmStep: asigna rol
- `YamlPipelineFactory(IServiceProvider, SchemaCompiler, IPipeline, IOptions<LlmEnvironment>?=null) [constructor primario]` [52-57]: Captura dependencias. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `CreateFromFolderAsync(string)` [59-68]: Lee *.yaml recursivamente, los concatena con separador '---' y llama a CreateFromYaml. | entra: Carpeta existente. | sale: YamlPipeline. | efectos: Lectura de ficheros. | lanza: DirectoryNotFoundException y las de CreateFromYaml.
- `CreateFromYaml(string)` [71-98]: Deserializa, aplica roles a YamlLlmStep, inyecta InputType/OutputType a pasos StepBase, valida y ordena, construye YamlPipeline. | entra: YAML fusionado. | sale: YamlPipeline. | efectos: Muta los pasos (rol, tipos). | lanza: InvalidOperationException (esquema ausente, rol, DAG), KeyNotFoundException (esquema no compilado), NullReferenceException si el documento es nulo, errores de YamlDotNet.
- `BuildDeserializer()` [102-127]: Crea el deserializador con convención camelCase, fábrica de objetos vía DI con respaldo a Activator y mapea tags de todos los tipos [YamlStepTag] cargados. | entra: Estado de AppDomain. | sale: IDeserializer. | efectos: Recorre todos los ensamblados cargados. | lanza: ninguno por la fábrica; errores de tags duplicados no cubiertos aquí.
- `SafeGetTypes(Assembly)` [129-133]: Devuelve GetTypes() o vacío si falla. | entra: Ensamblado. | sale: IEnumerable<Type>. | efectos: ninguno (traga todas las excepciones) | lanza: ninguno
- `RequireSchema(string?, string, string)` [143-148]: Exige esquema no vacío. | entra: Valor, id de paso y clave YAML. | sale: El esquema. | efectos: ninguno | lanza: InvalidOperationException.
- `ResolveRole(string?, string)` [157-177]: Obtiene el LlmRole por perfil o por DefaultRole del entorno; error si no hay entorno o rol. | entra: Perfil opcional e id de paso. | sale: LlmRole. | efectos: ninguno | lanza: InvalidOperationException.
- `ValidateAndOrder(List<IYamlStep>)` [179-230]: Comprueba dependencias, compatibilidad OutputSchema/InputSchema por arista y ordena con Kahn; detecta ciclos. | entra: Pasos con StepId únicos. | sale: Lista ordenada de IStep. | efectos: ninguno | lanza: InvalidOperationException (dependencia inexistente, mismatch, ciclo), ArgumentException por StepId duplicado.

### YamlLlmStep (class) — YAML/Steps/YamlLlmStep.cs:29-96
Paso LLM configurable desde YAML sobre BaseLlmStep, con prompts, perfil de rol, dependencias y política de historial.
Colabora con: BaseLlmStep: herencia; LlmRole/NullLlmService: rol marcador; PromptTemplateResolver: resuelve plantillas; ITemplateProvider: opcional; ConversationContext: contexto aislado opcional; YamlPipelineFactory: llama a ApplyRole
- `YamlLlmStep(ITemplateProvider?=null) [constructor primario]` [30-38]: Construye la base con nombre GUID, rol marcador, tipos IStepResult/LlmStringStepResult y prompt vacío. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `UnresolvedRole` [44-48]: Rol marcador nuevo con NullLlmService y modelo vacío. | entra: ninguno | sale: LlmRole nuevo por acceso. | efectos: ninguno | lanza: ninguno
- `StepId {get;init;} (required)` [51-51]: Alias de Name. | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `DependsOn / ProfileName / UserPrompt / UseConversationHistory` [57-70]: Propiedades de configuración YAML (UseConversationHistory=true por defecto). | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `ApplyRole(LlmRole)` [76-76]: Sustituye el rol llamando a SetRole. | entra: Rol resuelto. | sale: void | efectos: Cambia el rol de la base. | lanza: ninguno
- `BuildUserMessageAsync(IStepResult, PipelineContext)` [80-84]: Resuelve UserPrompt como plantilla o serializa input.Value (o el input entero) a JSON. | entra: Entrada y contexto. | sale: Mensaje de usuario. | efectos: ninguno | lanza: ninguno
- `BuildSystemMessageAsync(IStepResult, PipelineContext)` [86-90]: Resuelve SystemPrompt como plantilla o null. | entra: Entrada y contexto. | sale: Mensaje de sistema o null. | efectos: ninguno | lanza: ninguno
- `GetConversationContext(PipelineContext)` [92-95]: Devuelve el historial compartido o un ConversationContext nuevo con los mismos límites de tokens. | entra: Contexto. | sale: ConversationContext. | efectos: Crea un contexto nuevo en cada llamada si UseConversationHistory es false. | lanza: ninguno

## Subsistema `json` — Soporte JSON
Agrupa conversión, análisis de respuestas, serialización y validación de esquemas JSON.

- **Core/Execution/NewtonsoftJsonDataConverter.cs** (implementation): Define NewtonsoftJsonDataConverter, con un unico metodo estatico Convert(object, Type) que rehidrata datos crudos de almacenamiento al tipo destino. Devuelve null si la entrada es null, el mismo objeto si ya es asignable, y en otro caso usa JToken.ToObject (directo si ya es JToken, o via JToken.FromObject). Lo invoca StepResultFactory desde fuera del lote.
- **Support/JSON/JsonResponseCapability.cs** (model): Enum con las capacidades de respuesta JSON estructurada de un modelo LLM: None, JsonObject y JsonSchema, siguiendo el estandar response_format de OpenAI. Solo declara valores; no hay logica.
- **Support/JSON/JsonResponseParser.cs** (implementation): Parser tolerante de JSON procedente de respuestas LLM. Parse<T> encadena cuatro estrategias (parseo directo, bloque de codigo markdown, objeto/array localizado con regex en el texto, limpieza por recorte entre el primer inicio y el ultimo cierre JSON) y devuelve null con un warning si todas fallan. Usa Newtonsoft con camelCase, ignorando nulos y miembros desconocidos.
- **Support/JSON/JsonSchemaValidator.cs** (implementation): Validador JSON basico con System.Text.Json: comprueba la presencia de propiedades de primer nivel y si el JSON deserializa a un tipo T no nulo. Devuelve tuplas (IsValid, Error). No valida ningun esquema real pese al nombre.
- **Support/JSON/Serializations.cs** (infrastructure): Utilidades internas de serializacion para volcar errores a JObject sin propiedades de tipo Exception (para logs/telemetria). Incluye un contract resolver que anula la serializacion de propiedades cuyo tipo declarado deriva de Exception, y settings seguros que ignoran bucles de referencia, nulos y errores de miembros.

### NewtonsoftJsonDataConverter (class) — Core/Execution/NewtonsoftJsonDataConverter.cs:9-33
Conversor estatico de datos crudos a un tipo destino usando Newtonsoft.Json. No implementa ninguna interfaz (sin base en el grafo) pese a que su comentario dice cumplir el contrato IDataConverter.
Colabora con: JToken (Newtonsoft.Json.Linq): usado para ToObject y FromObject; StepResultFactory (externo): lo invoca desde CompileCtor y variantes segun el grafo
- `Convert(object rawData, Type targetType)` [12-32]: Devuelve null si rawData es null; devuelve rawData si ya es asignable al tipo destino; si es JToken usa ToObject; si no, lo pasa por JToken.FromObject y luego ToObject. | entra: rawData: objeto crudo de almacenamiento (puede ser null pese a la firma no anulable); targetType: tipo destino. | sale: Instancia del tipo destino, el propio rawData o null. | efectos: ninguno | lanza: Excepciones de Newtonsoft (JsonException, ArgumentNullException si targetType es null) no capturadas.

### JsonResponseCapability (enum) — Support/JSON/JsonResponseCapability.cs:7-17
Enumera el nivel de soporte de respuestas JSON estructuradas de un modelo: ninguno, json_object o json_schema.
- `None` [10-10]: Sin soporte nativo de JSON estructurado. | entra: n/a | sale: valor de enum | efectos: ninguno | lanza: ninguno
- `JsonObject` [13-13]: Soporta json_object (JSON libre sin esquema). | entra: n/a | sale: valor de enum | efectos: ninguno | lanza: ninguno
- `JsonSchema` [16-16]: Soporta json_schema (JSON validado por el modelo). | entra: n/a | sale: valor de enum | efectos: ninguno | lanza: ninguno

### JsonResponseParser (class) — Support/JSON/JsonResponseParser.cs:11-169
Extrae un objeto tipado de una respuesta LLM aplicando estrategias de parseo en cascada de la mas estricta a la mas laxa. Mantiene un logger y unos JsonSerializerSettings compartidos entre llamadas.
Colabora con: ILogger<JsonResponseParser>: registra fallos (Debug por estrategia, Warning final); JsonConvert/JsonSerializerSettings (Newtonsoft): deserializacion; Regex generados (GeneratedRegex): localizan bloques de codigo, objetos y arrays
- `JsonResponseParser(ILogger<JsonResponseParser> logger, JsonSerializerSettings? jsonSettings = null)` [18-27]: Guarda el logger y los settings; si no se pasan, crea camelCase, NullValueHandling.Ignore y MissingMemberHandling.Ignore. | entra: logger y settings opcionales (probablemente por DI). | sale: instancia | efectos: ninguno | lanza: ninguno (no valida logger nulo)
- `Parse<T>(string response)` [32-64]: Prueba en orden TryDirectParse, TryExtractFromCodeBlock, TryExtractJsonFromText y TryCleanAndParse; devuelve el primer resultado no nulo. | entra: response: texto de la respuesta LLM. | sale: T o null si todas fallan. | efectos: Escribe logs (Warning si todas fallan). | lanza: ArgumentNullException si response es null (la excepcion no es JsonException y no se captura en las estrategias).
- `TryDirectParse<T>(string response)` [66-77]: Deserializa la respuesta completa. | entra: response | sale: T o null si JsonException. | efectos: Log Debug en fallo. | lanza: Solo excepciones no JsonException.
- `TryExtractFromCodeBlock<T>(string response)` [79-100]: Busca el primer bloque ``` (con o sin json), recorta su contenido y lo deserializa. | entra: response | sale: T o null si no hay bloque o falla el parseo. | efectos: Log Debug en fallo. | lanza: Solo excepciones no JsonException.
- `TryExtractJsonFromText<T>(string response)` [102-129]: Localiza con regex el primer objeto balanceado y, si falla, el primer array balanceado; intenta deserializar cada uno. | entra: response | sale: T o null. | efectos: ninguno (descarta JsonException sin registrar). | lanza: Solo excepciones no JsonException.
- `TryCleanAndParse<T>(string response)` [131-161]: Normaliza saltos de linea, recorta desde el primer { o [ hasta el ultimo } o ] y deserializa. | entra: response | sale: T o null. | efectos: Log Debug en fallo. | lanza: Solo excepciones no JsonException.
- `MarkDownCodeBlockRegEx()` [163-164]: Regex generado para bloques de codigo markdown (IgnoreCase, Singleline, cultura es-ES). | entra: n/a | sale: Regex | efectos: ninguno | lanza: ninguno
- `JsonObjectRegEx()` [165-166]: Regex generado de llaves balanceadas con grupos de equilibrio. | entra: n/a | sale: Regex | efectos: ninguno | lanza: ninguno
- `JsonArrayRegEx()` [167-168]: Regex generado de corchetes balanceados con grupos de equilibrio. | entra: n/a | sale: Regex | efectos: ninguno | lanza: ninguno

### JsonSchemaValidator (class) — Support/JSON/JsonSchemaValidator.cs:8-55
Validaciones JSON simples con System.Text.Json. Clase sealed que solo contiene metodos estaticos. No valida esquemas.
Colabora con: JsonDocument / JsonSerializer (System.Text.Json): parseo y deserializacion
- `ValidateRequiredProperties(string json, params string[] requiredProperties)` [13-36]: Parsea el JSON y comprueba que cada propiedad requerida exista en el elemento raiz. | entra: json y nombres de propiedad (sensible a mayusculas). | sale: Tupla (IsValid, Error) con el primer faltante o el mensaje de JSON invalido. | efectos: ninguno | lanza: Solo captura JsonException; InvalidOperationException (raiz no objeto) y ArgumentNullException se propagan.
- `ValidateStructure<T>(string json)` [41-54]: Deserializa a T con opciones por defecto y considera valido si el resultado no es null. | entra: json | sale: Tupla (IsValid, Error). | efectos: ninguno | lanza: Solo captura JsonException; NotSupportedException y ArgumentNullException se propagan.

### Serializations (class) — Support/JSON/Serializations.cs:9-49
Clase estatica interna con extensiones para serializar objetos de error a JObject excluyendo propiedades Exception, con settings estaticos tolerantes a errores.
Colabora con: IStepError (Core.Abstractions): entrada de WithoutExeption, usa OriginalException; IgnoreExceptionContractResolver: resolver usado en _safeSettings; JObject/JsonSerializer (Newtonsoft): volcado
- `_safeSettings (campo estatico)` [27-34]: Settings con resolver que ignora Exception, ReferenceLoopHandling.Ignore, NullValueHandling.Ignore y manejador de errores que marca todo como Handled. | entra: n/a | sale: JsonSerializerSettings compartido | efectos: Estado estatico compartido. | lanza: ninguno
- `WithoutExceptionProperties(this object error)` [36-42]: Convierte el objeto a JObject con los settings seguros. | entra: error: cualquier objeto. | sale: JObject o null si error es null. | efectos: ninguno | lanza: Excepciones de JObject.FromObject no cubiertas por Error (p. ej. tipos no serializables a objeto).
- `WithoutExeption(this IStepError error)` [44-48]: Devuelve el propio error si es null o no tiene OriginalException; en otro caso su version JObject sin propiedades Exception. | entra: error: IStepError. | sale: object (IStepError original o JObject). | efectos: ninguno | lanza: ninguno directo

### Serializations.IgnoreExceptionContractResolver (class) — Support/JSON/Serializations.cs:11-25
DefaultContractResolver que desactiva ShouldSerialize de toda propiedad cuyo tipo declarado sea o derive de Exception.
Colabora con: DefaultContractResolver (Newtonsoft): clase base
- `CreateProperty(MemberInfo member, MemberSerialization memberSerialization)` [13-24]: Crea la propiedad base y, si su PropertyType es asignable a Exception, fija ShouldSerialize en false. | entra: member y modo de serializacion proporcionados por Newtonsoft. | sale: JsonProperty configurada. | efectos: ninguno | lanza: ninguno

## Subsistema `llm-configuration` — Pasos y solicitudes LLM
Reúne pasos LLM, solicitudes, servicios y proveedores de plantillas con su configuración asociada.

- **Configuration/ServiceCollectionExtensions.cs** (infrastructure/DI): Extension methods que registran el framework en IServiceCollection (opciones, Pipeline, plantillas, canal de eventos, almacenes en memoria, caché, fábrica de ConversationContext) y UseAITaskAgent, que fuerza singletons y registra las ITool en el singleton estático AvailableTools. Contiene además ConfigureLlmEnvironmentOptions, que convierte la sección de configuración LlmProviders (modelos Raw) en un LlmEnvironment con proveedores, roles y fallbacks validados, y falla en arranque ante referencias inexistentes.
- **Core/Execution/ExcludeFromLlmSchemaAttribute.cs** (model/attribute): Atributo marcador de propiedad que excluye un campo del JSON schema enviado al LLM sin ocultarlo de la serialización Newtonsoft general (a diferencia de JsonIgnore), de modo que sigue visible a las plantillas. Lo consume StepResultFactory (fuera del lote).
- **Core/Models/LlmResponseEmptyException.cs** (model/exception): Excepción para respuestas de proveedor sin choices (0 candidatos). Expone Model y Provider y ofrece dos constructores (con y sin excepción interna). El comentario afirma que es retriable pero la clase no transporta ningún indicador de ello.
- **Core/Models/ResumeRequest.cs** (model): DTO con la información necesaria para reanudar un pipeline suspendido: ruta del paso objetivo, entrada del usuario y metadatos opcionales.
- **Core/Models/StepValidationException.cs** (model/exception): Excepción para fallos de un paso de validación determinista; guarda StepName y ValidationError y compone el mensaje con ambos.
- **LLM/Abstractions/ILlmService.cs** (contract): Contrato de un servicio LLM: nombre de proveedor, invocación completa, invocación en streaming, estimación de tokens y tamaño máximo de contexto.
- **LLM/Abstractions/LlmHttpResponse.cs** (model): Record genérico que envuelve datos de respuesta con código HTTP y cabeceras, con IsSuccess calculado para 2xx. Destinado a lógica de reintento de los servicios.
- **LLM/Abstractions/NullLlmService.cs** (implementation/sentinel): ILlmService centinela interno que lanza InvalidOperationException si se invoca (preset YAML sin resolver). EstimateTokenCount usa length/4 y GetMaxContextTokens devuelve 0.
- **LLM/Configuration/LlmEnvironment.cs** (model/config): Raíz de configuración LLM: diccionarios de proveedores y roles (insensibles a mayúsculas), rol por defecto y lista de fallbacks globales sin resolver (FallbackProviderSettings).
- **LLM/Configuration/LlmFallback.cs** (model): Record con un fallback ya resuelto: modelo, LlmProvider e inferencia opcional.
- **LLM/Configuration/LlmInferenceSettings.cs** (model/config): Record de parámetros de inferencia respaldado por un único JObject (Options). Las propiedades tipadas (Temperature, MaxTokens, UseStreaming, timeouts...) son fachadas que leen y escriben claves de ese JObject sin distinguir mayúsculas. Options.init fusiona en lugar de reemplazar. Incluye copia profunda en el constructor protegido.
- **LLM/Configuration/LlmPromptTemplates.cs** (infrastructure): Plantillas por defecto para inyectar el JSON schema en el system prompt o en el mensaje de usuario, con alias de solo lectura y helpers Format* que sustituyen los marcadores {schema} y {message} mediante string.Replace.
- **LLM/Configuration/LlmProvider.cs** (model/config): Describe un proveedor (BaseUrl, ApiKey, ApiVersion, inferencia por defecto, catálogo de modelos con precios, servicio) y calcula el coste en USD de una llamada a partir de precios por millón, con tratamiento de tokens cacheados. LlmModelSettings es el record de modelo con precios.
- **LLM/Configuration/LlmRole.cs** (model/config): Rol de LLM: proveedor, modelo, system prompt, inferencia, fallbacks y herramientas, todo con init.
- **LLM/Models/LlmProviderException.cs** (model/exception): Excepción que tipa un fallo del proveedor (HTTP, respuesta vacía, stream roto, circuito abierto) como distinto de un fallo de lógica del paso; es la única que activa el cambio a fallback en BaseLlmStep.SwitchToNextFallback. Lleva IsRetriable.
- **LLM/Models/LlmRequest.cs** (model): Record sellado de petición al LLM que hereda LlmInferenceSettings; añade conversación, ventana deslizante, system prompt, modelo, formato de respuesta, herramientas y credenciales de conexión (BaseUrl, ApiKey, ApiVersion). Copy() realiza un with superficial.
- **LLM/Models/LlmResponse.cs** (model): Record de un candidato generado: índice, contenido, razonamiento, motivo de fin normalizado y crudo, y tool calls.
- **LLM/Models/LlmStreamChunk.cs** (model): Fragmento de una respuesta en streaming: delta, indicador de thinking, fin, motivo de fin, contadores de tokens (total, prompt, completion, razonamiento, cacheados), deltas de tool calls, índice de choice y objeto nativo.
- **LLM/Steps/BaseLlmStep.LlmInvocation.cs** (implementation/engine): Parte parcial de BaseLlmStep con el bucle recursivo de herramientas (InvokeLlmWithToolsAsync), la detección de llamadas repetidas, la ejecución de tool calls, la invocación del servicio en modo batch o streaming con procesado de tags de acción, el cálculo de coste y la emisión de eventos LlmResponseEvent y telemetría.
- **LLM/Steps/BaseLlmStep.cs** (implementation/engine): Motor base de todos los pasos LLM: gestión del rol y fallbacks, estado por invocación en PipelineContext.StepState, ExecuteAsync con bookmark y reintento con feedback, FinalizeAsync que compacta la conversación, construcción de LlmRequest, configuración de salida JSON, parsing de respuesta y enriquecimiento del system prompt con herramientas y tags. Incluye la variante genérica BaseLlmStep<TIn,TOut>.
- **LLM/Steps/StatelessTemplateLlmStep.cs** (implementation): Paso de plantilla que crea una conversación limpia por ejecución en lugar de usar context.Conversation, con soporte de guardado/restauración de estado para HITL mediante campos de instancia.
- **LLM/Steps/TemplateLlmStep.cs** (implementation): Paso LLM con plantillas: nombres por defecto de plantilla de prompt/system derivados del nombre del paso, variables de paso en minúsculas y resolución de mensajes mediante PromptTemplateResolver y un ITemplateProvider opcional.
- **LLM/Support/LlmHelpers.cs** (infrastructure): Utilidades estáticas: limpieza de JSON de respuesta, clave normalizada de tool call, inyección del schema en system prompt o mensaje, truncado para errores y conversión de string a tipo destino.
- **LLM/Support/PromptTemplateResolver.cs** (infrastructure): Resuelve un prompt contra el input del paso y el contexto del pipeline (metadatos + variables en minúsculas) usando un ITemplateProvider o, si no hay, el renderizador estático TinyTemplateRendered. Compartido por TemplateLlmStep y YamlLlmStep.
- **Support/Template/FileTemplateProvider.cs** (implementation): Proveedor de plantillas desde ficheros .md de una carpeta, con caché LRU+TTL limitada por tamaño, comprobación de path traversal y render mediante un ITemplateRendered opcional o TinyTemplateRendered.
- **Support/Template/ITemplateRendered.cs** (contract): Contrato de motor de plantillas: Render(template, obj) devuelve texto con parámetros sustituidos.
- **Support/Template/TinyTemplateRendered.cs** (implementation): Motor de plantillas ligero basado en regex y JToken: elimina comentarios, expande includes {{ include 'x' }}, interpola {{ a.b | filtro ?? default }} con filtros json/yaml/csv/etc., y expande includes legacy @[ruta] al final. Serializa el modelo con propiedades en minúsculas. Incluye LowercaseNamingStrategy.

### ServiceCollectionExtensions (class) — Configuration/ServiceCollectionExtensions.cs:24-324
Clase estática de extensiones de DI. Registra opciones, Pipeline, infraestructura compartida y fábrica de ConversationContext; UseAITaskAgent inicializa singletons y registra ITool en el singleton estático AvailableTools.
Colabora con: AITaskAgentOptions/PipelineOptions: se enlazan y proyectan desde configuración; AvailableTools.Instance: registro estático de herramientas; Pipeline, EventChannel, MemoryCacheService, TinyTemplateRendered: se registran como singletons; ConfigureLlmEnvironmentOptions: configurador de LlmEnvironment
- `AddAITaskAgent(this IServiceCollection services)` [31-94]: Registra opciones del agente, configurador de LlmEnvironment, PipelineOptions, Pipeline, plantillas, EventChannel, almacenes en memoria, caché, ITools y fábrica transitoria de ConversationContext. | entra: IServiceCollection; requiere IConfiguration con la sección raíz AITaskAgent en tiempo de resolución. | sale: La misma colección para encadenar. | efectos: Muta la colección de servicios; mezcla AddSingleton y TryAddSingleton. | lanza: ninguno en el registro; las factorías pueden fallar al resolverse.
- `UseAITaskAgent(this IServiceProvider serviceProvider)` [100-114]: Fuerza la creación de IPipeline y PipelineContextFactory y registra cada ITool resuelta en AvailableTools.Instance. | entra: ServiceProvider ya construido. | sale: El mismo provider. | efectos: Muta el singleton estático AvailableTools.Instance (estado global de proceso). | lanza: InvalidOperationException si IPipeline o PipelineContextFactory no están registrados.
- `AddToolDiscovery(IServiceCollection services)` [116-131]: Registra como ITool singleton todos los tipos concretos de ITool del ensamblado de entrada; su llamada está comentada. | entra: IServiceCollection. | sale: void. | efectos: Muta la colección (si se invocara). | lanza: ninguno
- `SafeGetTypes(Assembly assembly)` [133-136]: Devuelve los tipos de un ensamblado o un array vacío si GetTypes lanza cualquier excepción. | entra: Assembly. | sale: Type[] (vacío ante error). | efectos: ninguno | lanza: ninguno (catch general).

### ConfigureLlmEnvironmentOptions (class) — Configuration/ServiceCollectionExtensions.cs:140-294
IConfigureOptions<LlmEnvironment> privado que lee la sección LlmProviders, enlaza modelos Raw, resuelve cada proveedor contra los ILlmService registrados y construye roles y fallbacks, lanzando excepciones claras ante configuración inconsistente.
Colabora con: IServiceProvider: resuelve IConfiguration e ILlmService; LlmProvider/LlmRole/LlmFallback: objetos que construye; LlmEnvironmentRaw/LlmProviderRaw/LlmRoleRaw: modelos de binding; ConfigurationPatchExtensions.GetEx: enlace de configuración
- `ConfigureLlmEnvironmentOptions(IServiceProvider sp) [ctor primario]` [140-140]: Captura el IServiceProvider. | entra: IServiceProvider raíz. | sale: instancia | efectos: ninguno | lanza: ninguno
- `Configure(LlmEnvironment options)` [142-265]: Enlaza la sección, crea LlmProvider por entrada con su ILlmService (coincidencia exacta o por subcadena), construye fallbacks globales, copia los FallbackProviders en bruto, crea roles validando proveedor y modelo, asigna DefaultRole y valida que exista. | entra: LlmEnvironment recién construido; configuración de LlmProviders y ILlmService registrados. | sale: Muta options.Providers, Roles, FallbackProviders y DefaultRole. | efectos: Resuelve servicios del contenedor; escribe en options. | lanza: InvalidOperationException si falta servicio, BaseUrl, Provider o Model de rol, o el rol por defecto no existe.
- `BuildFallback(FallbackProviderSettings f, Dictionary<string, LlmProvider> providers, string origin)` [271-293]: Convierte un fallback en bruto en LlmFallback validando proveedor y modelo. | entra: Ajustes en bruto, diccionario de proveedores ya construidos, texto de origen para errores. | sale: LlmFallback. | efectos: ninguno | lanza: InvalidOperationException si el proveedor no existe o el modelo está vacío.

### LlmEnvironmentRaw (class) — Configuration/ServiceCollectionExtensions.cs:296-302
Modelo de enlace intermedio de configuración para LlmEnvironment: proveedores, roles, rol por defecto y fallbacks globales.
Colabora con: ConfigureLlmEnvironmentOptions: lo rellena el binder y lo lee
- `Providers { get; set; }` [298-298]: Diccionario de LlmProviderRaw. | entra: binder | sale: propiedad | efectos: ninguno | lanza: ninguno
- `Roles { get; set; }` [299-299]: Diccionario de LlmRoleRaw. | entra: binder | sale: propiedad | efectos: ninguno | lanza: ninguno
- `DefaultRole { get; set; }` [300-300]: Nombre del rol por defecto (default). | entra: binder | sale: propiedad | efectos: ninguno | lanza: ninguno
- `FallbackProviders { get; set; }` [301-301]: Lista de fallbacks globales en bruto. | entra: binder | sale: propiedad | efectos: ninguno | lanza: ninguno

### LlmProviderRaw (class) — Configuration/ServiceCollectionExtensions.cs:304-312
Modelo de enlace de un proveedor: URL, clave, versión, tipo de servicio, inferencia y catálogo de modelos.
Colabora con: LlmInferenceSettings, LlmModelSettings: tipos de propiedades
- `BaseUrl, ApiKey, ApiVersion, ServiceType, Inference, Models (propiedades)` [306-311]: Propiedades auto-implementadas de enlace. | entra: binder | sale: propiedades | efectos: ninguno | lanza: ninguno

### LlmRoleRaw (class) — Configuration/ServiceCollectionExtensions.cs:314-321
Modelo de enlace de un rol: nombre de proveedor, modelo, system prompt, inferencia y fallbacks propios.
Colabora con: FallbackProviderSettings, LlmInferenceSettings: tipos de propiedades
- `Provider, Model, SystemPrompt, Inference, Fallbacks (propiedades)` [316-320]: Propiedades auto-implementadas de enlace. | entra: binder | sale: propiedades | efectos: ninguno | lanza: ninguno

### ExcludeFromLlmSchemaAttribute (class) — Core/Execution/ExcludeFromLlmSchemaAttribute.cs:14-15
Atributo sellado aplicable a propiedades que indica a StepResultFactory que omita el campo del schema JSON enviado al LLM.
Colabora con: StepResultFactory: consumidor (fuera del lote, según el comentario)

### LlmResponseEmptyException (class) — Core/Models/LlmResponseEmptyException.cs:8-26
Excepción de respuesta vacía de proveedor (0 choices) con Model y Provider.
Colabora con: Exception: base
- `Model { get; }` [10-10]: Modelo consultado. | entra: ctor | sale: string | efectos: ninguno | lanza: ninguno
- `Provider { get; }` [11-11]: Proveedor consultado. | entra: ctor | sale: string | efectos: ninguno | lanza: ninguno
- `LlmResponseEmptyException(string model, string provider)` [13-18]: Compone el mensaje y guarda modelo y proveedor. | entra: model, provider | sale: instancia | efectos: ninguno | lanza: ninguno
- `LlmResponseEmptyException(string model, string provider, Exception innerException)` [20-25]: Igual que el anterior con excepción interna. | entra: model, provider, innerException | sale: instancia | efectos: ninguno | lanza: ninguno

### ResumeRequest (class) — Core/Models/ResumeRequest.cs:6-22
DTO para reanudar un pipeline suspendido.
- `TargetStepPath { get; set; }` [11-11]: Ruta exacta del paso a la espera. | entra: llamador | sale: string | efectos: ninguno | lanza: ninguno
- `UserInput { get; set; }` [16-16]: Dato del usuario a inyectar; por defecto new object(). | entra: llamador | sale: object | efectos: ninguno | lanza: ninguno
- `Metadata { get; set; }` [21-21]: Estado personalizado opcional. | entra: llamador | sale: object? | efectos: ninguno | lanza: ninguno

### StepValidationException (class) — Core/Models/StepValidationException.cs:6-24
Excepción de fallo de validación determinista de un paso.
Colabora con: Exception: base
- `StepName { get; }` [8-8]: Nombre del paso. | entra: ctor | sale: string | efectos: ninguno | lanza: ninguno
- `ValidationError { get; }` [9-9]: Texto del error de validación. | entra: ctor | sale: string | efectos: ninguno | lanza: ninguno
- `StepValidationException(string stepName, string validationError)` [11-16]: Construye el mensaje y guarda campos. | entra: stepName, validationError | sale: instancia | efectos: ninguno | lanza: ninguno
- `StepValidationException(string stepName, string validationError, Exception innerException)` [18-23]: Igual con excepción interna. | entra: stepName, validationError, innerException | sale: instancia | efectos: ninguno | lanza: ninguno

### ILlmService (interface) — LLM/Abstractions/ILlmService.cs:8-38
Contrato de proveedor LLM: invocación completa y en streaming, estimación de tokens y límite de contexto.
Colabora con: LlmRequest/LlmResult/LlmStreamChunk: tipos de entrada y salida
- `Name { get; }` [13-13]: Nombre del proveedor. | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `InvokeAsync(LlmRequest request, CancellationToken ct)` [18-20]: Invoca el LLM y devuelve la respuesta completa. | entra: request, token | sale: Task<LlmResult> | efectos: I/O de red en implementaciones | lanza: depende de la implementación
- `InvokeStreamingAsync(LlmRequest request, CancellationToken ct)` [25-27]: Invoca con respuesta en streaming. | entra: request, token | sale: IAsyncEnumerable<LlmStreamChunk> | efectos: I/O de red en implementaciones | lanza: depende de la implementación
- `EstimateTokenCount(string text)` [32-32]: Estima tokens de un texto. | entra: texto | sale: int | efectos: ninguno | lanza: ninguno
- `GetMaxContextTokens(string? model = null)` [37-37]: Tamaño máximo de contexto del modelo. | entra: modelo opcional | sale: int | efectos: ninguno | lanza: ninguno

### LlmHttpResponse<TResponse> (record) — LLM/Abstractions/LlmHttpResponse.cs:8-14
Envoltorio de datos con metadatos HTTP para lógica de reintento.
Colabora con: HttpStatusCode: estado
- `Data { get; init; } (required)` [10-10]: Payload. | entra: inicializador | sale: TResponse | efectos: ninguno | lanza: ninguno
- `StatusCode { get; init; } (required)` [11-11]: Código HTTP. | entra: inicializador | sale: HttpStatusCode | efectos: ninguno | lanza: ninguno
- `Headers { get; init; }` [12-12]: Cabeceras como diccionario (por defecto vacío). | entra: inicializador | sale: Dictionary<string,string> | efectos: ninguno | lanza: ninguno
- `IsSuccess { get; }` [13-13]: True si el código está en 200-299. | entra: StatusCode | sale: bool | efectos: ninguno | lanza: ninguno

### NullLlmService (class) — LLM/Abstractions/NullLlmService.cs:10-26
Servicio centinela interno: lanza al invocarse para hacer visible un preset no resuelto.
Colabora con: ILlmService: implementa; BaseLlmStep.GetLlmService: lo usa como sustituto cuando no hay servicio
- `Name { get; }` [12-12]: Devuelve Null. | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `InvokeAsync(LlmRequest, CancellationToken)` [14-17]: Lanza siempre. | entra: request | sale: nunca devuelve | efectos: ninguno | lanza: InvalidOperationException
- `InvokeStreamingAsync(LlmRequest, CancellationToken)` [19-21]: Lanza al llamarse (no al enumerar). | entra: request | sale: nunca devuelve | efectos: ninguno | lanza: InvalidOperationException
- `EstimateTokenCount(string text)` [23-23]: Longitud/4. | entra: texto | sale: int | efectos: ninguno | lanza: NullReferenceException si text es null
- `GetMaxContextTokens(string? model)` [25-25]: Devuelve 0. | entra: modelo | sale: int | efectos: ninguno | lanza: ninguno

### LlmEnvironment (class) — LLM/Configuration/LlmEnvironment.cs:7-33
Raíz de configuración LLM resuelta: proveedores, roles, rol por defecto y fallbacks globales en bruto.
Colabora con: LlmProvider, LlmRole, FallbackProviderSettings: contenidos
- `Providers { get; init; }` [13-13]: Diccionario insensible a mayúsculas de proveedores. | entra: configurador | sale: Dictionary | efectos: ninguno | lanza: ninguno
- `Roles { get; init; }` [19-19]: Diccionario insensible a mayúsculas de roles. | entra: configurador | sale: Dictionary | efectos: ninguno | lanza: ninguno
- `DefaultRole { get; set; }` [30-30]: Nombre del rol por defecto. | entra: configurador | sale: string | efectos: ninguno | lanza: ninguno
- `FallbackProviders { get; init; }` [32-32]: Lista de fallbacks globales sin resolver. | entra: configurador | sale: List | efectos: ninguno | lanza: ninguno

### FallbackProviderSettings (class) — LLM/Configuration/LlmEnvironment.cs:35-50
Ajustes en bruto de un fallback: nombre de proveedor, modelo e inferencia opcional.
Colabora con: ConfigureLlmEnvironmentOptions.BuildFallback: lo convierte en LlmFallback
- `Provider { get; init; }` [40-40]: Clave del proveedor. | entra: binder | sale: string | efectos: ninguno | lanza: ninguno
- `Model { get; init; }` [45-45]: Modelo del fallback. | entra: binder | sale: string | efectos: ninguno | lanza: ninguno
- `Inference { get; init; }` [49-49]: Inferencia opcional. | entra: binder | sale: LlmInferenceSettings? | efectos: ninguno | lanza: ninguno

### LlmFallback (record) — LLM/Configuration/LlmFallback.cs:3-7
Fallback resuelto: modelo, proveedor e inferencia opcional.
Colabora con: LlmProvider, LlmInferenceSettings
- `LlmFallback(string Model, LlmProvider Provider, LlmInferenceSettings? Inference = null)` [3-7]: Constructor posicional del record. | entra: modelo, proveedor, inferencia | sale: record | efectos: ninguno | lanza: ninguno

### LlmInferenceSettings (record) — LLM/Configuration/LlmInferenceSettings.cs:10-172
Parámetros de inferencia como fachada tipada sobre un JObject de opciones. Es la fuente única de verdad; las propiedades leen y escriben claves sin distinguir mayúsculas.
Colabora con: LlmRequest: hereda; LlmProvider/LlmRole/LlmFallback: lo transportan; BaseLlmStep.BuildLlmRequestAsync: lo lee y fusiona
- `LlmInferenceSettings()` [14-14]: Constructor vacío. | entra: n/a | sale: instancia | efectos: ninguno | lanza: ninguno
- `LlmInferenceSettings(LlmInferenceSettings other) [protected]` [16-19]: Copia profunda de las opciones. | entra: other | sale: instancia | efectos: ninguno | lanza: NullReferenceException si other es null
- `Options { get; init; }` [32-42]: Acceso al JObject; el init fusiona claves reemplazando arrays. | entra: JObject opcional | sale: JObject | efectos: Merge sobre el JObject interno | lanza: ninguno
- `Temperature { get; init; }` [46-50]: Fachada float? sobre la clave Temperature. | entra: valor | sale: float? | efectos: escribe en Options | lanza: JsonException si el valor no convierte
- `TopP { get; init; }` [52-56]: Fachada float?. | entra: valor | sale: float? | efectos: escribe en Options | lanza: como Temperature
- `MaxTokens { get; init; }` [58-62]: Fachada int?. | entra: valor | sale: int? | efectos: escribe en Options | lanza: como Temperature
- `UseStreaming { get; init; }` [64-68]: Fachada bool; false si no existe. | entra: valor | sale: bool | efectos: escribe en Options | lanza: como Temperature
- `JsonSupport { get; init; }` [70-74]: Fachada bool; false por defecto. | entra: valor | sale: bool | efectos: escribe en Options | lanza: como Temperature
- `ToolSupport { get; init; }` [76-80]: Fachada bool; true por defecto. | entra: valor | sale: bool | efectos: escribe en Options | lanza: como Temperature
- `Stop { get; init; }` [82-86]: Fachada de lista de strings; el getter crea una lista nueva en cada lectura. | entra: valor | sale: IList<string>? | efectos: escribe en Options | lanza: como Temperature
- `UseMaxCompletionTokens { get; init; }` [96-100]: Fachada bool; indica usar max_completion_tokens. | entra: valor | sale: bool | efectos: escribe en Options | lanza: como Temperature
- `RequestTimeoutSeconds { get; init; }` [111-115]: Fachada int? del límite por intento. | entra: valor | sale: int? | efectos: escribe en Options | lanza: como Temperature
- `StreamingInactivityTimeoutSeconds { get; init; }` [122-126]: Fachada int? del límite de inactividad en streaming. | entra: valor | sale: int? | efectos: escribe en Options | lanza: como Temperature
- `GetOption<T>(string key)` [140-140]: Lee una opción por nombre. | entra: clave | sale: T? o default | efectos: ninguno | lanza: JsonException si no convierte
- `Get<T>(string key) [protected]` [142-146]: Busca el token ignorando mayúsculas y lo convierte a T. | entra: clave | sale: T? o default | efectos: ninguno | lanza: JsonException/InvalidCastException si el tipo no coincide
- `Set(string key, object? value) [protected]` [148-171]: Elimina la clave si el valor es null; si no, actualiza la existente o la añade. | entra: clave, valor | sale: void | efectos: muta el JObject interno | lanza: JsonException si el valor no serializa

### LlmPromptTemplates (class) — LLM/Configuration/LlmPromptTemplates.cs:8-111
Plantillas por defecto para inyectar el schema en system prompt y mensaje de usuario, más helpers de formato.
Colabora con: LlmHelpers: usa FormatSystemPrompt y FormatUserMessage
- `DefaultSystemPromptSchemaTemplate` [15-35]: Plantilla de system prompt con marcador {schema}. | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `DefaultUserMessageSchemaTemplate` [42-64]: Plantilla de mensaje de usuario con {message} y {schema}. | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `SystemPromptSchemaTemplate { get; }` [73-73]: Alias de solo lectura. | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `UserMessageSchemaTemplate { get; }` [80-80]: Alias de solo lectura. | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `FormatSystemPrompt(string schemaJson, string? customTemplate = null)` [93-95]: Sustituye {schema} en la plantilla indicada o en la por defecto. | entra: schemaJson, plantilla opcional | sale: string | efectos: ninguno | lanza: NullReferenceException si schemaJson es null
- `FormatUserMessage(string message, string schemaJson, string? customTemplate = null)` [107-110]: Sustituye {message} y luego {schema} sobre el texto resultante. | entra: mensaje, schema, plantilla opcional | sale: string | efectos: ninguno | lanza: ArgumentNullException si message o schemaJson son null

### LlmProvider (class) — LLM/Configuration/LlmProvider.cs:5-57
Proveedor LLM configurado: conexión, inferencia por defecto, catálogo de modelos con precios y servicio. Calcula el coste en USD de una llamada.
Colabora con: ILlmService: Service requerido; LlmModelSettings: catálogo de precios; BaseLlmStep.PriceOf: llama a CalculateCost
- `BaseUrl, ApiKey, ApiVersion (propiedades init)` [7-11]: Datos de conexión; ApiKey en texto plano. | entra: binder/configurador | sale: string? | efectos: ninguno | lanza: ninguno
- `Inference, Models (propiedades init)` [9-10]: Inferencia por defecto y lista de modelos. | entra: binder/configurador | sale: propiedades | efectos: ninguno | lanza: ninguno
- `Service { get; init; } (required)` [12-12]: ILlmService asociado. | entra: configurador | sale: ILlmService | efectos: ninguno | lanza: ninguno
- `CalculateCost(string? model, int? promptTokens, int? completionTokens, int? cachedPromptTokens = null)` [33-56]: Busca el modelo sin distinguir mayúsculas y calcula coste con precio de entrada, de salida y de tokens cacheados (por defecto el de entrada); tokens cacheados acotados al total de prompt. | entra: modelo y contadores de tokens | sale: decimal? (null si no hay precios o modelo vacío) | efectos: ninguno | lanza: ninguno

### LlmModelSettings (record) — LLM/Configuration/LlmProvider.cs:65-71
Modelo de un proveedor con precios en USD por millón de tokens (nulo = desconocido) e inferencia.
Colabora con: LlmProvider.CalculateCost: lo consume
- `LlmModelSettings(string Name, decimal? InputPricePerMillion, decimal? OutputPricePerMillion, LlmInferenceSettings Inference, decimal? CachedInputPricePerMillion = null)` [65-71]: Constructor posicional del record. | entra: nombre, precios, inferencia | sale: record | efectos: ninguno | lanza: ninguno

### LlmRole (class) — LLM/Configuration/LlmRole.cs:5-13
Configuración de rol: proveedor, modelo, system prompt, inferencia, fallbacks y herramientas.
Colabora con: LlmProvider, LlmFallback, ITool, LlmInferenceSettings
- `Provider, Model, SystemPrompt, Inference, Fallbacks, Tools (propiedades init)` [7-12]: Propiedades de rol; Provider y Model son required. | entra: configurador | sale: propiedades | efectos: ninguno | lanza: ninguno

### LlmProviderException (class) — LLM/Models/LlmProviderException.cs:8-18
Tipa un fallo del proveedor; gatilla el cambio a fallback.
Colabora con: BaseLlmStep.SwitchToNextFallback: lo comprueba; BaseLlmStep.InvokeLlmWithToolsAsync: lo crea al capturar
- `IsRetriable { get; }` [11-11]: Indica fallo transitorio. | entra: ctor | sale: bool | efectos: ninguno | lanza: ninguno
- `LlmProviderException(string message, Exception? innerException, bool isRetriable)` [13-17]: Construye la excepción. | entra: mensaje, interna, retriable | sale: instancia | efectos: ninguno | lanza: ninguno

### LlmRequest (record) — LLM/Models/LlmRequest.cs:10-34
Petición agnóstica de proveedor; extiende LlmInferenceSettings con conversación, ventana deslizante, system prompt, modelo, herramientas y credenciales de conexión.
Colabora con: ConversationContext: historial; ResponseFormatOptions: formato; ToolDefinition: herramientas; ILlmService: consumidor
- `Conversation { get; init; } (required)` [13-13]: Conversación enviada. | entra: BuildLlmRequestAsync | sale: ConversationContext | efectos: ninguno | lanza: ninguno
- `UseSlidingWindow, SlidingWindowMaxTokens, MessageMaxTokens` [14-16]: Configuración de ventana (por defecto true, 8192, 8192). | entra: init | sale: propiedades | efectos: ninguno | lanza: ninguno
- `SystemPrompt, Model (required), ResponseFormat, User, Tools` [18-25]: Contenido de la petición. | entra: init | sale: propiedades | efectos: ninguno | lanza: ninguno
- `BaseUrl, ApiKey, ApiVersion` [26-28]: Credenciales y destino copiados del proveedor. | entra: init | sale: propiedades | efectos: ninguno | lanza: ninguno
- `Copy()` [33-33]: Copia superficial mediante with. | entra: n/a | sale: LlmRequest | efectos: ninguno | lanza: ninguno

### LlmResponse (record) — LLM/Models/LlmResponse.cs:7-26
Un candidato generado por el LLM.
Colabora con: ToolCall, FinishReason
- `Index, Content (required), Reasoning, FinishReason, RawFinishReason, ToolCalls` [10-25]: Propiedades del candidato. | entra: init | sale: propiedades | efectos: ninguno | lanza: ninguno

### LlmStreamChunk (class) — LLM/Models/LlmStreamChunk.cs:8-48
Fragmento de una respuesta en streaming con contenido, finalización, uso de tokens y tool call deltas.
Colabora con: BaseLlmStep.InvokeLlmAsync: lo ensambla; ToolCallUpdate
- `Delta (required), IsThinking, IsComplete, FinishReason, RawFinishReason` [11-23]: Contenido y estado del fragmento. | entra: init | sale: propiedades | efectos: ninguno | lanza: ninguno
- `TokensUsed, PromptTokens, CompletionTokens, ReasoningTokens, CachedPromptTokens` [26-38]: Uso de tokens. | entra: init | sale: int? | efectos: ninguno | lanza: ninguno
- `ToolCallUpdates, ChoiceIndex, NativeChunk` [41-47]: Deltas de tool calls, índice de choice y objeto nativo. | entra: init | sale: propiedades | efectos: ninguno | lanza: ninguno

### BaseLlmStep (class) — LLM/Steps/BaseLlmStep.cs:38-684
Clase base abstracta no genérica de los pasos LLM: gestiona rol y fallbacks, estado por invocación en PipelineContext.StepState, ejecución con reintento y feedback, finalización con compactación de conversación, construcción de LlmRequest y parsing de respuesta.
Colabora con: StepBase: hereda ExecuteAsync/FinalizeAsync/SwitchToNextFallback; LlmRole/LlmProvider/LlmInferenceSettings: configuración; ConversationContext: historial con bookmarks; ITool/ITagHandler: herramientas y tags; LlmHelpers: parsing y schema; AITaskAgentOptions: ventana deslizante
- `BaseLlmStep(name, role, inputType, outputType, prompt, systemPrompt) [ctor primario]` [38-45]: Guarda el rol, toma Tools del rol y deriva SystemPrompt del argumento o del rol. | entra: nombre, rol, tipos, prompt | sale: instancia | efectos: ninguno | lanza: NullReferenceException si role es null
- `SetRole(LlmRole newRole)` [61-74]: Sustituye el rol; copia Tools y SystemPrompt del nuevo rol solo si estaban vacíos. | entra: rol no nulo | sale: void | efectos: muta _role, Tools, SystemPrompt | lanza: ArgumentNullException si newRole es null
- `GetLlmService(PipelineContext context)` [76-81]: Devuelve el servicio del proveedor activo según FallbackIndex o NullLlmService. | entra: contexto | sale: ILlmService | efectos: crea estado si no existe | lanza: ArgumentOutOfRangeException si el índice excede fallbacks
- `GetModel(PipelineContext context)` [83-89]: Modelo activo (rol o fallback). | entra: contexto | sale: string | efectos: crea estado | lanza: InvalidOperationException si no hay modelo
- `GetProvider(PipelineContext context)` [91-97]: Proveedor activo. | entra: contexto | sale: LlmProvider | efectos: crea estado | lanza: InvalidOperationException si no hay proveedor
- `GetInference(PipelineContext context)` [99-104]: Inferencia activa: la del rol o fallback, si no la del proveedor, si no una vacía. | entra: contexto | sale: LlmInferenceSettings | efectos: crea estado | lanza: ninguno
- `MaxToolIterations, Tools, TagHandlers, SystemPrompt, Prompt (propiedades)` [106-158]: Configuración pública del paso: iteraciones máximas (5), herramientas, handlers de tags, system prompt y prompt. | entra: init/set | sale: propiedades | efectos: ninguno | lanza: ninguno
- `StateKey(PipelineContext context)` [141-142]: Clave de estado: CurrentStepPath o Name. | entra: contexto | sale: string | efectos: ninguno | lanza: ninguno
- `GetState(PipelineContext context)` [144-154]: Obtiene o crea el InvocationState en StepState. | entra: contexto | sale: InvocationState | efectos: escribe en context.StepState | lanza: ninguno
- `SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt)` [160-209]: Si el error es LlmProviderException y hay otro fallback válido, avanza FallbackIndex y restaura el bookmark inicial. | entra: error, contexto, intento | sale: bool | efectos: muta FallbackIndex y la conversación (RestoreBookmark) | lanza: ArgumentOutOfRangeException improbable
- `BuildUserMessageAsync(IStepResult input, PipelineContext context)` [212-212]: Devuelve Prompt por defecto. | entra: input, contexto | sale: Task<string?> | efectos: ninguno | lanza: ninguno
- `BuildSystemMessageAsync(IStepResult input, PipelineContext context)` [215-215]: Devuelve SystemPrompt por defecto. | entra: input, contexto | sale: Task<string?> | efectos: ninguno | lanza: ninguno
- `FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken ct)` [217-265]: Restaura el bookmark, escribe el par user/assistant canónico en éxito, llama AddConversationToContext y elimina el estado del paso. | entra: resultado, contexto | sale: Task completada | efectos: muta conversación y StepState | lanza: ninguno
- `ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken ct)` [267-385]: Intento 1: prepara conversación, bookmark, mensaje y request. Intentos posteriores: añade feedback de error. Invoca el bucle de herramientas, parsea y registra el mensaje del asistente. | entra: input, intento, último resultado | sale: IStepResult (resultado o error) | efectos: muta conversación y estado; logs | lanza: NullReferenceException si attempt>1 sin estado previo
- `GetConversationContext(PipelineContext context)` [387-387]: Devuelve context.Conversation. | entra: contexto | sale: ConversationContext | efectos: ninguno | lanza: ninguno
- `AddConversationToContext(PipelineContext, ConversationContext, ILlmStepResult)` [389-391]: Hook vacío. | entra: contexto, conversación, resultado | sale: void | efectos: ninguno | lanza: ninguno
- `BuildLlmRequestAsync(IStepResult input, PipelineContext context, ConversationContext conversation, CancellationToken ct)` [396-455]: Compone el system prompt (herramientas y tags), opciones de conversación, definiciones de herramientas y LlmRequest con inferencia fusionada rol/proveedor; aplica ConfigureLlmRequest y ConfigureJsonResponse. | entra: input, contexto, conversación | sale: LlmRequest | efectos: lee IOptions del ServiceProvider | lanza: ninguno
- `ConfigureLlmRequest(LlmRequest request, PipelineContext context)` [461-461]: Hook virtual que devuelve la request. | entra: request | sale: LlmRequest | efectos: ninguno | lanza: ninguno
- `MergeOptions(params JObject?[] optionsList)` [463-474]: Fusiona JObjects con MergeArrayHandling.Union. | entra: lista de JObject | sale: JObject | efectos: ninguno | lanza: ninguno
- `ParseLlmResponseAsync(LlmResult response, PipelineContext context)` [478-536]: Convierte el contenido a ValueType (string, primitivo o JSON), crea el resultado y devuelve un mensaje de error si falla el parseo. | entra: respuesta | sale: (IStepResult?, string?) | efectos: logs | lanza: ninguno (captura JsonException y Exception)
- `SetLlmMetrics(IStepResult? result, LlmResult response)` [538-549]: Copia contenido, razonamiento, modelo, tokens, coste y finish reason al LlmStepResult. | entra: resultado, respuesta | sale: void | efectos: muta el resultado | lanza: ninguno
- `UpdateContextMetrics(PipelineContext context, LlmResult response)` [551-575]: Incrementa métricas y sobrescribe Metadata TokensUsed y CostUsd con los valores de esa llamada. | entra: contexto, respuesta | sale: void | efectos: métricas y context.Metadata | lanza: ninguno
- `ConfigureJsonResponse(LlmRequest request, string userMessage, LlmProvider? provider)` [579-621]: Si hay Schema, fija formato JSON o inyecta el schema en el system prompt según JsonSupport del proveedor. | entra: request, mensaje, proveedor | sale: LlmRequest | efectos: la rama JsonObject añadiría un mensaje de usuario a la conversación | lanza: NullReferenceException si Schema es no nulo y ValueType nulo (log)
- `EnrichSystemPromptWithToolGuidelines(string? basePrompt, IList<ITool> tools)` [623-653]: Añade una sección de guías de uso de herramientas. | entra: prompt, herramientas | sale: string | efectos: ninguno | lanza: ninguno
- `EnrichSystemPromptWithTags(string? basePrompt, List<ITagHandler> handlers)` [655-683]: Añade la referencia de tags de acción con las instrucciones de cada handler. | entra: prompt, handlers | sale: string | efectos: ninguno | lanza: ninguno

### BaseLlmStep (parcial LlmInvocation) (class) — LLM/Steps/BaseLlmStep.LlmInvocation.cs:16-922
Mitad del motor: bucle recursivo de herramientas, detección de repeticiones, ejecución de tool calls, invocación batch/streaming con procesamiento de tags, coste y eventos.
Colabora con: ILlmService: invocación; StreamingTagParser/ITagHandler: tags; ITool: ejecución; LlmHelpers.NormalizeToolCallKey; LlmProvider.CalculateCost; PipelineContext.SendEventAsync, Telemetry, Metrics
- `InvokeLlmWithToolsAsync(LlmRequest request, PipelineContext context, int toolIteration, CancellationToken ct)` [18-220]: Recursión: llama a InvokeLlmAsync, ejecuta tool calls y vuelve a llamar hasta respuesta final o límite; agrega tokens y coste de las iteraciones. | entra: request, contexto, iteración | sale: LlmResult | efectos: telemetría, métricas, escribe en conversación, ejecuta herramientas | lanza: ninguno (excepciones del proveedor se convierten en LlmResult.Failure; propaga cancelación)
- `_tagsExecutedInLastResponse, _lastToolCallKeys, _consecutiveIdenticalToolCalls (campos)` [227-233]: Estado de repetición y de tags guardado en campos de instancia. | entra: n/a | sale: n/a | efectos: estado compartido por instancia | lanza: ninguno
- `ResetToolCallRepeatTracking()` [236-240]: Pone a cero el seguimiento de repeticiones. | entra: n/a | sale: void | efectos: muta campos de instancia | lanza: ninguno
- `IsStuckRepeatingToolCalls(List<ToolCall> toolCalls, out string message)` [252-293]: Compara las claves de tool calls con las de la iteración anterior; umbral 3 si todas son de solo lectura, si no 1. | entra: tool calls | sale: bool y mensaje | efectos: muta campos de instancia; logs | lanza: ninguno
- `StopWithMessage(LlmResult result, LlmResponse candidate, string message)` [299-318]: Convierte la respuesta en final sin tool calls con el mensaje de parada si el contenido está vacío. | entra: resultado, candidato, mensaje | sale: LlmResult | efectos: ninguno | lanza: ninguno
- `DeduplicateToolCalls(List<ToolCall> toolCalls)` [320-343]: Elimina tool calls con la misma clave normalizada. | entra: lista | sale: lista sin duplicados | efectos: log de aviso | lanza: ninguno
- `ExecuteToolCallsAsync(string stepName, List<ToolCall> toolCalls, IList<ITool>? availableTools, PipelineContext context, CancellationToken ct)` [345-455]: Ejecuta cada tool call y añade el resultado o un mensaje de error a la conversación; trata especialmente los tags invocados como herramientas. | entra: tool calls, herramientas | sale: Task | efectos: ejecuta herramientas, escribe en conversación, logs de argumentos | lanza: ninguno (captura excepciones de herramienta)
- `InvokeLlmAsync(LlmRequest request, PipelineContext context, string stepName, CancellationToken ct)` [457-862]: Invoca el servicio en modo batch o streaming, procesa tags en la respuesta, calcula coste, normaliza y emite LlmResponseEvent. | entra: request, contexto | sale: LlmResult | efectos: I/O de red, eventos, ejecución de handlers de tags, escribe campo _tagsExecutedInLastResponse | lanza: excepciones del servicio o de handlers propagadas al llamador
- `PriceOf(LlmProvider provider, string? responseModel, string requestedModel, int? prompt, int? completion, int? cached)` [869-873]: Coste buscando primero por el modelo de la respuesta y luego por el solicitado. | entra: proveedor, modelos, tokens | sale: decimal? | efectos: ninguno | lanza: ninguno
- `AppendReferences(string content, IReadOnlyList<string> references)` [883-893]: Añade referencias de tags de razonamiento al contenido. | entra: contenido, referencias | sale: string | efectos: ninguno | lanza: ninguno
- `CanonicalizeToolCallsResponse(LlmResponse response)` [895-905]: Deduplica tool calls y clona la respuesta si cambió. | entra: respuesta | sale: LlmResponse | efectos: ninguno | lanza: ninguno
- `CloneResponseWithToolCalls(LlmResponse response, List<ToolCall>? toolCalls)` [907-920]: Copia la respuesta con otra lista de tool calls. | entra: respuesta, lista | sale: LlmResponse | efectos: ninguno | lanza: ninguno

### InvocationState (class) — LLM/Steps/BaseLlmStep.cs:125-132
Estado privado por invocación (índice de fallback, conversación, bookmark, mensaje, request) guardado en PipelineContext.StepState.
Colabora con: BaseLlmStep.GetState
- `FallbackIndex, Conversation, InitialBookmark, Message, Request (campos públicos)` [127-131]: Campos del estado. | entra: BaseLlmStep | sale: campos | efectos: ninguno | lanza: ninguno

### BaseLlmStep<TIn, TOut> (class) — LLM/Steps/BaseLlmStep.cs:690-753
Variante genérica tipada que adapta la entrada y expone IStep<TIn,TOut>.
Colabora con: BaseLlmStep: base; StepResultFactory: conversión de entrada
- `BaseLlmStep(name, role, prompt, systemPrompt) [ctor primario]` [690-703]: Delega al ctor base con typeof(TIn) y typeof(TOut). | entra: nombre, rol, prompts | sale: instancia | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken ct)` [707-727]: Convierte el input al tipo TIn si no es asignable y delega a la base. | entra: input | sale: Task<IStepResult> | efectos: ninguno | lanza: InvalidOperationException si la conversión falla
- `CreateResult(object? value, IStepError? error)` [731-732]: Versión tipada de CreateResult. | entra: valor, error | sale: TOut | efectos: ninguno | lanza: InvalidCastException si el tipo no es TOut
- `CreateErrorResult(string message, Exception? exception)` [734-735]: Versión tipada de CreateErrorResult. | entra: mensaje, excepción | sale: TOut | efectos: ninguno | lanza: InvalidCastException
- `IStep.InputType / IStep.OutputType` [739-740]: Implementación explícita de tipos. | entra: n/a | sale: Type | efectos: ninguno | lanza: ninguno
- `IStep<TIn,TOut>.ExecuteAsync(TIn input, PipelineContext context, int attempt, TOut? lastStepResult, CancellationToken ct)` [742-751]: Ejecuta la versión no genérica y castea a TOut. | entra: input tipado | sale: Task<TOut> | efectos: ninguno | lanza: InvalidCastException

### StatelessTemplateLlmStep<TIn, TOut> (class) — LLM/Steps/StatelessTemplateLlmStep.cs:17-71
Paso de plantilla que usa una conversación nueva por ejecución, salvo cuando restaura una guardada.
Colabora con: TemplateLlmStep: base; ConversationContext; PipelineContext.Conversation: toma MaxTokens y TokenCounter
- `StatelessTemplateLlmStep(name, role, templateProvider, tools, promtTemplateString, systemTemplateString) [ctor primario]` [17-24]: Delega a TemplateLlmStep y captura MaxTokens del rol. | entra: nombre, rol, plantillas | sale: instancia | efectos: ninguno | lanza: ninguno
- `SaveStateAsync()` [33-41]: Devuelve el campo _currentConversation como estado. | entra: n/a | sale: Task<object?> | efectos: ninguno | lanza: ninguno
- `RestoreStateAsync(object? state, object? hitlResponse)` [43-50]: Guarda la conversación restaurada en _restoredConversation. | entra: estado | sale: Task | efectos: escribe campo de instancia | lanza: ninguno
- `GetConversationContext(PipelineContext context)` [55-69]: Usa la conversación restaurada o crea una nueva con los tokens máximos del rol o del contexto. | entra: contexto | sale: ConversationContext | efectos: escribe _currentConversation y _restoredConversation | lanza: NullReferenceException si context.Conversation es null

### TemplateLlmStep<TIn, TOut> (class) — LLM/Steps/TemplateLlmStep.cs:18-87
Paso LLM con prompts de plantilla y variables de paso, usando PromptTemplateResolver.
Colabora con: BaseLlmStep<TIn,TOut>: base; ITemplateProvider; AvailableTools.Instance: resuelve herramientas por nombre; PromptTemplateResolver
- `TemplateLlmStep(name, role, templateProvider, tools, promtTemplateString, systemTemplateString)` [25-41]: Fija prompt y system por defecto a nombres de fichero derivados del nombre del paso y resuelve herramientas desde el registro estático. | entra: nombre, rol, plantillas, nombres de herramientas | sale: instancia | efectos: lee AvailableTools.Instance | lanza: depende de AvailableTools.GetTools
- `GetVariable(string key)` [43-46]: Lee variable por clave en minúsculas. | entra: clave | sale: object? | efectos: ninguno | lanza: ninguno
- `SetVariable(string key, object? value)` [48-51]: Escribe variable. | entra: clave, valor | sale: void | efectos: muta _variables compartido por instancia | lanza: ninguno
- `RemoveVariable(string key)` [53-56]: Elimina variable. | entra: clave | sale: void | efectos: muta _variables | lanza: ninguno
- `ClearVariables()` [58-61]: Vacía variables. | entra: n/a | sale: void | efectos: muta _variables | lanza: ninguno
- `BuildUserMessageAsync(IStepResult input, PipelineContext context)` [63-67]: Resuelve el prompt mediante PromptTemplateResolver. | entra: input, contexto | sale: Task<string?> | efectos: ninguno | lanza: excepciones del proveedor de plantillas
- `BuildSystemMessageAsync(IStepResult input, PipelineContext context)` [70-75]: Resuelve el system prompt con SystemTemplateProvider. | entra: input, contexto | sale: Task<string?> | efectos: ninguno | lanza: excepciones del proveedor de plantillas
- `SystemTemplateProvider { get; }` [85-85]: Proveedor para el system prompt; por defecto el del constructor. | entra: n/a | sale: ITemplateProvider? | efectos: ninguno | lanza: ninguno

### LlmHelpers (class) — LLM/Support/LlmHelpers.cs:12-193
Utilidades estáticas de LLM: limpieza JSON, claves de tool calls, inyección de schema y conversión de string.
Colabora con: LlmPromptTemplates; NJsonSchema.JsonSchema; ToolCall; FinishReasonExtensions (según el grafo, fuera del lote)
- `CleanJsonResponse(string content)` [17-51]: Extrae el texto entre el primer { o [ y el último } o ]; si no hay, quita cercas markdown. | entra: texto | sale: string | efectos: ninguno | lanza: ninguno
- `NormalizeToolCallKey(ToolCall toolCall)` [57-82]: Clave nombre:argumentos con propiedades de primer nivel ordenadas; si no es JSON usa el texto bruto. | entra: tool call | sale: string | efectos: ninguno | lanza: ArgumentNullException si Arguments es null
- `InjectSchemaInSystemPrompt(string? existingPrompt, JsonSchema schema)` [88-96]: Añade la instrucción de schema al system prompt. | entra: prompt, schema | sale: string | efectos: ninguno | lanza: ninguno
- `InjectSchemaInUserMessage(string message, JsonSchema schema)` [102-106]: Aplica la plantilla de mensaje con schema. | entra: mensaje, schema | sale: string | efectos: ninguno | lanza: ninguno
- `TruncateForError(string content, int maxLength = 500)` [111-116]: Trunca el texto con marcador. | entra: texto, longitud | sale: string | efectos: ninguno | lanza: ninguno
- `ConvertOrThrow(string value, Type targetType)` [122-184]: Convierte string a tipo destino con Parse sensible a cultura actual. | entra: valor, tipo | sale: object | efectos: ninguno | lanza: FormatException, OverflowException, ArgumentException, InvalidOperationException
- `ConvertOrThrow<T>(string value)` [189-192]: Versión genérica. | entra: valor | sale: T | efectos: ninguno | lanza: como la no genérica

### PromptTemplateResolver (class) — LLM/Support/PromptTemplateResolver.cs:14-62
Renderiza un prompt con modelo { Input, Context } con un proveedor de plantillas o directamente.
Colabora con: ITemplateProvider; TinyTemplateRendered; PipelineContext.Metadata/GetLogger
- `Resolve(string? rawPrompt, IStepResult input, PipelineContext context, ITemplateProvider? templateProvider = null, IEnumerable<KeyValuePair<string, object?>>? variables = null)` [30-61]: Fusiona metadatos y variables (claves en minúsculas, ganan las variables), arma el modelo y delega al proveedor o a TinyTemplateRendered; si el proveedor devuelve null usa el texto bruto. | entra: prompt o nombre, input, contexto | sale: string | efectos: log de placeholders sin resolver | lanza: ArgumentException (claves nulas) o excepciones del proveedor

### FileTemplateProvider (class) — Support/Template/FileTemplateProvider.cs:15-167
Proveedor de plantillas .md con caché LRU, TTL y tope de tamaño, con protección contra path traversal.
Colabora con: ITemplateProvider: implementa; ITemplateRendered/TinyTemplateRendered: render; CachedTemplate
- `FileTemplateProvider(folderPath, templateEngine, enableCache, ttl, maxCacheSizeBytes, validateTemplates) [ctor primario]` [15-34]: Fija carpeta, motor, caché, TTL y tamaño máximo. | entra: parámetros | sale: instancia | efectos: ninguno | lanza: ninguno
- `GetTemplate(string name)` [40-77]: Devuelve el contenido desde caché (si no expiró) o desde disco bajo un lock. | entra: nombre sin extensión | sale: string? (null si no existe) | efectos: lee fichero, modifica caché | lanza: ArgumentException si el nombre es nulo o vacío; IOException
- `LoadTemplateFromFile(string name)` [79-90]: Lee carpeta/name.md si queda dentro de la carpeta base y existe. | entra: nombre | sale: string? | efectos: lee fichero | lanza: IOException, ArgumentException por caracteres no válidos
- `AddToCache(string name, string content)` [92-107]: Inserta en caché y evicta LRU si supera el límite con margen. | entra: nombre, contenido | sale: void | efectos: muta caché | lanza: ninguno
- `RemoveFromCache(string name)` [109-115]: Elimina y descuenta el tamaño. | entra: nombre | sale: void | efectos: muta caché | lanza: ninguno
- `EvictLruItems()` [117-134]: Expulsa por acceso más antiguo hasta bajar del límite. | entra: n/a | sale: void | efectos: muta caché | lanza: ninguno
- `Render(string name, object model)` [144-157]: Obtiene la plantilla y la renderiza; si falta devuelve null o lanza según validateTemplates. | entra: nombre, modelo | sale: string? | efectos: lee fichero; el motor puede leer includes | lanza: FileNotFoundException si validateTemplates

### CachedTemplate (record) — Support/Template/FileTemplateProvider.cs:159-166
Entrada de caché con contenido, instantes de carga y acceso y tamaño.
Colabora con: FileTemplateProvider
- `CachedTemplate(Content, LoadedAt, LastAccessedAt, SizeBytes) / LastAccessedAt { get; set; }` [159-165]: Record con LastAccessedAt mutable. | entra: valores | sale: record | efectos: ninguno | lanza: ninguno

### ITemplateRendered (interface) — Support/Template/ITemplateRendered.cs:6-12
Contrato del motor de plantillas.
Colabora con: TinyTemplateRendered: implementación
- `Render(string template, object obj)` [11-11]: Renderiza plantilla con el objeto. | entra: plantilla, objeto | sale: string | efectos: depende | lanza: depende

### TinyTemplateRendered (class) — Support/Template/TinyTemplateRendered.cs:11-334
Motor de plantillas con regex sobre un JToken del modelo, con pases de comentarios, includes, interpolación con filtros y includes legacy.
Colabora con: ITemplateRendered: implementa; FileTemplateProvider y PromptTemplateResolver: lo invocan; YamlDotNet: filtro yaml; File/Directory: includes
- `Render(string template, object obj, bool strictMode = false, string? baseDirectory = null, ILogger? logger = null) [static]` [43-55]: Serializa el modelo con propiedades en minúsculas y llama a RenderInternal. | entra: plantilla, modelo, opciones | sale: string | efectos: lectura de ficheros por includes | lanza: ArgumentNullException si obj es null
- `ITemplateRendered.Render(string template, object obj)` [57-57]: Delega con strictMode false y sin baseDirectory. | entra: plantilla, objeto | sale: string | efectos: como Render | lanza: como Render
- `RenderInternal(string template, JToken rootToken, bool strictMode, string? baseDirectory, ILogger? logger)` [59-118]: Pase 0 quita comentarios, pase 1 includes con comillas, pase 2 interpolación, pase 3 includes @[ruta] sobre el texto ya interpolado. | entra: plantilla, token | sale: string | efectos: lee ficheros; logs | lanza: excepciones de include en strictMode
- `SelectTokenIgnoreCase(JToken root, string jpath)` [132-170]: Resuelve el path por segmentos sin distinguir mayúsculas. | entra: raíz, path | sale: JToken? | efectos: ninguno | lanza: JsonException con índices inválidos
- `ToJPath(string expr)` [179-184]: Convierte dict['Key'] a dict.Key. | entra: expresión | sale: string | efectos: ninguno | lanza: ninguno
- `IsScribanDirective(string expr)` [186-192]: Detecta palabras clave Scriban para dejarlas intactas. | entra: expresión | sale: bool | efectos: ninguno | lanza: ninguno
- `ApplyPipeFilter(JToken token, string pipeExpr)` [194-229]: Aplica filtros json, yaml, upcase, downcase, capitalize, strip, date, default, csv y size. | entra: token, filtro | sale: string? (null si filtro desconocido) | efectos: ninguno | lanza: FormatException con formato de fecha inválido
- `Capitalize(string s)` [231-232]: Primera letra en mayúscula. | entra: texto | sale: string | efectos: ninguno | lanza: ninguno
- `ResolveFileInclude(string rawPath, string originalMatch, JToken rootToken, bool strictMode, string? baseDirectory, ILogger? logger)` [234-282]: Resuelve y lee un fichero incluido (con comprobación de base solo si baseDirectory no es null) y lo renderiza recursivamente. | entra: ruta, modelo | sale: string | efectos: lee ficheros, Console.WriteLine ante error | lanza: en strictMode relanza; si no devuelve marcador de error
- `FormatAsCsv(JToken token)` [284-309]: Formatea un array como CSV. | entra: token | sale: string | efectos: ninguno | lanza: ninguno
- `EscapeCsvValue(string v)` [311-314]: Escapa valores CSV. | entra: texto | sale: string | efectos: ninguno | lanza: ninguno
- `SerializeToYaml(JToken token)` [316-322]: Serializa el token con YamlDotNet sin alias. | entra: token | sale: string | efectos: ninguno | lanza: excepciones de YamlDotNet
- `PipeFilterRegex/TemplateRegex/FileIncludeRegex/ScribanIncludeRegex/ScribanCommentRegex` [324-333]: Regex generadas en compilación. | entra: n/a | sale: Regex | efectos: ninguno | lanza: ninguno

### LowercaseNamingStrategy (class) — Support/Template/TinyTemplateRendered.cs:341-344
Estrategia de nombres Newtonsoft que pone en minúsculas los nombres de propiedad.
Colabora con: TinyTemplateRendered.LowercaseSerializer
- `ResolvePropertyName(string name)` [343-343]: Devuelve name en minúsculas. | entra: nombre | sale: string | efectos: ninguno | lanza: ninguno

## Subsistema `observability` — Canales de observabilidad
Proporciona canales, suscripciones y trazadores para observar la ejecución de pasos.

- **Observability/ConsoleStepTracer.cs** (implementation): Implementación de IStepTracer que escribe cada StepTraceEvent en la consola con un icono emoji por estado, el nombre del paso y el mensaje opcional. Si el evento trae StreamingContent lo emite con Console.Write sin salto de línea. Pensada para desarrollo y depuración.
- **Observability/EventChannel.cs** (implementation): Implementación por defecto de IEventChannel sobre System.Threading.Channels. Mantiene una lista de suscriptores (correlationId, canal acotado, capacidad), enruta eventos por correlationId, registra cada evento por ILogger y ofrece dos modos de suscripción: handler con bucle de consumo en segundo plano y suscripción con ChannelReader. DisposeAsync completa los canales y espera a los bucles de consumo. Incluye HandlerSubscription (file-scoped) como IDisposable idempotente.
- **Observability/EventChannelOptions.cs** (model): Opciones de EventChannel: nivel de log de eventos (por defecto None) y capacidad del canal acotado por suscriptor (por defecto 1000). Solo EventChannel la instancia internamente.
- **Observability/EventSubscription.cs** (implementation): Implementación de IEventSubscription que expone el ChannelReader del suscriptor y, al disponer, invoca la acción de baja suministrada por EventChannel.
- **Observability/IEventChannel.cs** (contract): Contrato del canal de eventos de progreso: envío con enrutado por CorrelationId, suscripción con handler y suscripción con ChannelReader. Registrado como Singleton según el grafo.
- **Observability/IEventSubscription.cs** (contract): Contrato de una suscripción consumible mediante ChannelReader y liberable de forma asíncrona.
- **Observability/IStepTracer.cs** (contract): Contrato de un trazador de pasos que recibe StepTraceEvent para construir spans de observabilidad. Un único método asíncrono.
- **Observability/NullStepTracer.cs** (implementation): Trazador nulo que descarta todos los eventos. Patrón Null Object para IStepTracer.
- **Observability/StepStatus.cs** (model): Enumeración de estados de ejecución de un paso: Started, InProgress, Retrying, Completed y Failed.
- **Observability/StepTraceEvent.cs** (model): Modelo inmutable (init) de un punto de traza: paso, estado, mensaje, correlación, pipeline, tipo de paso, paso padre, atributos, marca temporal UTC y contenido de streaming.

### ConsoleStepTracer (class) — Observability/ConsoleStepTracer.cs:7-34
Trazador de pasos que vuelca los eventos a la salida estándar con icono por estado. Sin estado propio.
Colabora con: IStepTracer: contrato que implementa; StepTraceEvent: evento que formatea; StepStatus: selecciona el icono
- `OnTraceEventAsync(StepTraceEvent evt)` [9-33]: Elige un icono según evt.Status, escribe una línea con icono, StepName, Status y Message, y escribe StreamingContent sin salto de línea si existe. | entra: Evento de traza no nulo (no se valida). | sale: Task.CompletedTask. | efectos: Escritura en Console.Out; las escrituras de varias líneas no son atómicas entre hilos. | lanza: NullReferenceException si evt es null.

### EventChannel (class) — Observability/EventChannel.cs:11-260
Bus de eventos en proceso. Cada suscriptor tiene un canal acotado DropOldest; SendAsync escribe sin bloquear en los canales cuyo correlationId coincide (o es null). Registra los eventos por ILogger y en DisposeAsync drena los consumidores.
Colabora con: IEventChannel: contrato que implementa; EventChannelOptions: nivel de log y capacidad; IProgressEvent: evento transportado (tipo externo al lote); EventSubscription: devuelto por CreateSubscription; HandlerSubscription: devuelto por Subscribe; LlmResponseEvent: se registra con campos extra en LogEvent; ILogger<EventChannel>: logging
- `EventChannel(LogLevel eventLogLevel = Information, ILogger<EventChannel>? logger = null)` [31-40]: Fija el logger (NullLogger si null) y crea EventChannelOptions solo con EventLogLevel; ChannelCapacity queda en el valor por defecto 1000. | entra: Nivel de log y logger opcional. | sale: Instancia inicializada. | efectos: ninguno | lanza: ninguno
- `SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)` [42-108]: Registra el evento, descarta el envío a suscriptores si SuppressFromUser, copia la lista de suscriptores bajo lock y hace TryWrite en cada canal cuyo correlationId es null o coincide, avisando si el canal estaba lleno o el write falla. | entra: Evento no nulo; el token se ignora. | sale: Task ya completada. | efectos: Escribe en los canales de los suscriptores; log; elimina suscriptores ante ChannelClosedException. | lanza: ArgumentNullException si el evento es null.
- `Subscribe(Func<IProgressEvent, CancellationToken, Task> handler, string? correlationId = null)` [115-178]: Crea un canal acotado (capacidad de las opciones, DropOldest), lanza con Task.Run un bucle que invoca el handler secuencialmente con CancellationToken.None, registra suscriptor y tarea bajo lock y devuelve un IDisposable que da de baja y completa el writer. | entra: Handler no nulo y filtro opcional de correlación. | sale: IDisposable de baja (HandlerSubscription). | efectos: Añade a _subscribers y _backgroundLoops; arranca una tarea en el ThreadPool; las excepciones del handler se registran y se tragan. | lanza: ArgumentNullException si handler es null.
- `CreateSubscription(int capacity = 100, string? correlationId = null)` [185-208]: Crea un canal acotado DropOldest con la capacidad indicada, lo registra y devuelve un EventSubscription cuya baja lo retira y completa el writer. | entra: Capacidad (no se valida salvo por BoundedChannelOptions) y filtro opcional. | sale: IEventSubscription con el ChannelReader. | efectos: Añade a _subscribers. | lanza: ArgumentOutOfRangeException (de BoundedChannelOptions) si capacity <= 0.
- `LogEvent(IProgressEvent evt)` [210-227]: Si el nivel configurado no es None, registra el evento; para LlmResponseEvent incluye IsThinking, FinishReason, Content y Tokens. | entra: Evento no nulo. | sale: ninguno | efectos: Escritura en el logger; el contenido completo de la respuesta LLM va al log. | lanza: ninguno
- `DisposeAsync()` [234-259]: Completa todos los canales, vacía suscriptores y bucles bajo lock y espera con Task.WhenAll a que los bucles de consumo terminen; registra el error si la espera falla. | entra: Estado interno. | sale: ValueTask asíncrono. | efectos: Vacía _subscribers y _backgroundLoops; espera a handlers de usuario; no marca el canal como liberado. | lanza: ninguno (excepciones capturadas y registradas).

### HandlerSubscription (class) — Observability/EventChannel.cs:266-277
IDisposable mínimo (file-scoped) devuelto por Subscribe. Ejecuta la acción de baja una sola vez.
Colabora con: EventChannel.Subscribe: único creador
- `HandlerSubscription(Action unsubscribe)` [266-266]: Constructor primario que captura la acción de baja. | entra: Acción de baja. | sale: Instancia. | efectos: ninguno | lanza: ninguno
- `Dispose()` [270-276]: Con Interlocked.Exchange garantiza que la baja se ejecute una sola vez; no espera al drenaje del consumidor. | entra: ninguno | sale: ninguno | efectos: Retira el suscriptor y completa el writer. | lanza: ninguno

### EventChannelOptions (class) — Observability/EventChannelOptions.cs:8-22
Opciones de EventChannel con dos propiedades auto-implementadas.
Colabora con: EventChannel: único consumidor visible en el lote
- `EventLogLevel { get; set; }` [15-15]: Nivel de log; por defecto None. | entra: LogLevel | sale: LogLevel | efectos: ninguno | lanza: ninguno
- `ChannelCapacity { get; set; }` [21-21]: Capacidad por suscriptor de Subscribe; por defecto 1000. | entra: int | sale: int | efectos: ninguno | lanza: ninguno

### EventSubscription (class) — Observability/EventSubscription.cs:8-21
Suscripción basada en ChannelReader; delega la baja en una acción externa.
Colabora con: IEventSubscription: contrato; EventChannel.CreateSubscription: creador y proveedor de la acción de baja
- `Reader => reader` [13-13]: Expone el ChannelReader. | entra: ninguno | sale: ChannelReader<IProgressEvent> | efectos: ninguno | lanza: ninguno
- `DisposeAsync()` [16-20]: Invoca la acción de baja (retira suscriptor y completa el canal) sin guarda de idempotencia propia. | entra: ninguno | sale: ValueTask completada | efectos: Baja del suscriptor. | lanza: Lo que lance unsubscribe (nada en el uso actual).

### IEventChannel (interface) — Observability/IEventChannel.cs:10-42
Contrato del canal de eventos de progreso con enrutado por CorrelationId.
Colabora con: IProgressEvent: evento transportado; IEventSubscription: suscripción por reader
- `SendAsync<TEvent>(TEvent, CancellationToken)` [16-17]: Envía un evento a los suscriptores coincidentes. | entra: Evento y token | sale: Task | efectos: Entrega a suscriptores | lanza: ninguno declarado
- `Subscribe(Func<IProgressEvent, CancellationToken, Task>, string?)` [29-31]: Suscribe un handler con búfer interno. | entra: Handler y filtro | sale: IDisposable | efectos: Alta de suscriptor | lanza: ninguno declarado
- `CreateSubscription(int capacity = 100, string? correlationId = null)` [41-41]: Crea suscripción por ChannelReader. | entra: Capacidad y filtro | sale: IEventSubscription | efectos: Alta de suscriptor | lanza: ninguno declarado

### IEventSubscription (interface) — Observability/IEventSubscription.cs:8-14
Suscripción liberable de forma asíncrona que expone un ChannelReader.
Colabora con: IProgressEvent: tipo leído
- `Reader { get; }` [13-13]: Lector de eventos. | entra: ninguno | sale: ChannelReader<IProgressEvent> | efectos: ninguno | lanza: ninguno

### IStepTracer (interface) — Observability/IStepTracer.cs:7-14
Contrato de trazador de pasos.
Colabora con: StepTraceEvent: evento recibido
- `OnTraceEventAsync(StepTraceEvent evt)` [13-13]: Recibe un evento de traza. | entra: StepTraceEvent | sale: Task | efectos: Depende de la implementación | lanza: ninguno declarado

### NullStepTracer (class) — Observability/NullStepTracer.cs:7-14
Trazador que descarta los eventos.
Colabora con: IStepTracer: contrato; StepTraceEvent: parámetro ignorado
- `OnTraceEventAsync(StepTraceEvent evt)` [9-13]: No hace nada. | entra: Evento (ignorado) | sale: Task.CompletedTask | efectos: ninguno | lanza: ninguno

### StepStatus (enum) — Observability/StepStatus.cs:6-22
Estados de un paso: Started, InProgress, Retrying, Completed, Failed.
Colabora con: StepTraceEvent: propiedad Status; ConsoleStepTracer: mapea a iconos

### StepTraceEvent (class) — Observability/StepTraceEvent.cs:7-38
DTO de traza con propiedades init; StepName y Status son required.
Colabora con: StepStatus: estado; IStepTracer: consumidor
- `StepName { get; init; }` [10-10]: Nombre del paso (required). | entra: string | sale: string | efectos: ninguno | lanza: ninguno
- `Status { get; init; }` [13-13]: Estado (required). | entra: StepStatus | sale: StepStatus | efectos: ninguno | lanza: ninguno
- `Message, CorrelationId, PipelineName, StepType, ParentStepName` [16-28]: Cadenas opcionales de contexto. | entra: string? | sale: string? | efectos: ninguno | lanza: ninguno
- `Attributes { get; init; }` [31-31]: Diccionario mutable de atributos, inicializado vacío. | entra: Dictionary<string, object> | sale: Dictionary<string, object> | efectos: ninguno | lanza: ninguno
- `Timestamp { get; init; }` [34-34]: Marca temporal, por defecto DateTime.UtcNow en la construcción. | entra: DateTime | sale: DateTime | efectos: ninguno | lanza: ninguno
- `StreamingContent { get; init; }` [37-37]: Fragmento de streaming opcional. | entra: string? | sale: string? | efectos: ninguno | lanza: ninguno

## Subsistema `option` — Opciones y enrutamiento
Agrupa opciones de configuración con pasos de análisis de intención y enrutamiento, sin una responsabilidad única más precisa.

- **Configuration/AITaskAgentConfigurationKeys.cs** (infrastructure): Constantes con las rutas de las secciones de configuracion del framework (raiz AITaskAgent y subsecciones). Evita literales repetidos al leer IConfiguration. Solo contiene const string.
- **Configuration/AITaskAgentOptions.cs** (model): Agregado inmutable (init) de las opciones del framework: observabilidad, timeouts, circuit breaker, rate limit y conversacion. Cada propiedad se inicializa con su valor por defecto.
- **Configuration/CircuitBreakerOptions.cs** (model): Opciones del circuit breaker: umbral de fallos consecutivos (5) y duracion de apertura en segundos (60). POCO sin logica.
- **Configuration/DiContractResolver.cs** (implementation): Resolver de contratos de Newtonsoft.Json que sustituye el creador por defecto de cada clase por ActivatorUtilities.CreateInstance sobre un IServiceProvider, para permitir constructores con parametros inyectados al deserializar.
- **Configuration/ObservabilityOptions.cs** (model): Opciones de observabilidad: nivel de log de eventos, capacidad del canal de eventos por suscriptor y banderas de metricas y trazado. POCO con valores por defecto.
- **Configuration/RateLimitOptions.cs** (model): Opciones de rate limiting en forma de token bucket: tokens maximos (100), intervalo de recarga en ms (1000) y tokens por recarga (10). POCO sin logica.
- **Configuration/TimeoutOptions.cs** (model): Timeouts por defecto de pipeline (10 min), paso (1 min) y herramienta (30 s). Mantiene StreamingActivityTimeout marcada [Obsolete] solo para que la configuracion existente siga enlazando.
- **Core/Steps/SwitchStep.cs** (implementation): Paso generico de enrutamiento: ejecuta un selector (delegado o SelectTargetStep sobrescrito) que devuelve el paso destino y el resultado a propagar, anade el destino a NextSteps del resultado, emite StepRoutingEvent y devuelve el resultado. Base de RouterStep.
- **LLM/Abstractions/IRouterOptions.cs** (contract): Contrato que expone la lista de opciones validas de un paso de enrutamiento o clasificacion. Lo implementan IntentionAnalyzerStep y RouterStep y lo consulta LlmIntentionResult para validar.
- **LLM/Abstractions/IRouterResult.cs** (contract): Contrato de un resultado de paso que lleva la opcion seleccionada (SelectedOption); es la entrada tipada de RouterStep.
- **LLM/Models/LlmOptions.cs** (model): Record con parametros de invocacion LLM (modelo, temperatura, MaxTokens, formato de respuesta, stop, TopP/TopK, penalizaciones, seed, nombres de herramientas, usuario). El grafo no registra consumidores dentro del lote.
- **LLM/Results/IntentionOption.cs** (model): Opcion de clasificacion dinamica con Id obligatorio y Description (cuando elegirla). Se usa para construir el catalogo de opciones del prompt de IntentionAnalyzerStep.
- **LLM/Results/LlmIntentionResult.cs** (implementation): Resultado LLM tipado con Intention que implementa IRouterResult. Su ValidateAsync combina la validacion base con la comprobacion de que la opcion elegida figure entre las del paso (IRouterOptions).
- **LLM/Steps/IntentionAnalyzerStep.cs** (implementation): Paso LLM que clasifica la intencion del usuario entre un catalogo de IntentionOption. Compone el system prompt (role + texto + catalogo), publica el catalogo como variable y en FinalizeAsync emite el feedback al usuario y ajusta el historial de conversacion.
- **LLM/Steps/RouterStep.cs** (implementation): SwitchStep especializado sobre IRouterResult: busca SelectedOption en un diccionario de rutas (insensible a mayusculas) con ruta por defecto opcional, anade el destino a NextSteps y devuelve el mismo resultado.
- **LLM/Support/ResponseFormatOptions.cs** (model): Record con el formato de respuesta (Text o JsonObject) y un JsonSchema opcional, mas el enum ResponseFormatType. Lo consumen LlmRequest y BaseLlmStep.ConfigureJsonResponse.
- **Resilience/CircuitBreaker.cs** (implementation): Circuit breaker con estados Closed/Open/HalfOpen protegido por lock: cuenta fallos consecutivos, abre al superar el umbral y pasa a HalfOpen tras la duracion de apertura. ExecuteAsync envuelve una accion asincrona.
- **Resilience/CircuitBreakerOpenException.cs** (model): Excepcion sellada lanzada por CircuitBreaker cuando el circuito esta abierto. Constructor primario que solo reenvia el mensaje.
- **Resilience/CircuitState.cs** (model): Enum de estados del circuit breaker: Closed, Open, HalfOpen.
- **Resilience/RateLimiter.cs** (implementation): Token bucket con recarga por temporizador y bajo demanda, y un SemaphoreSlim como senal para despertar a los que esperan. Ofrece WaitAsync, TryAcquire y AvailableTokens; es IDisposable por el Timer y el semaforo.
- **Resilience/RetryPolicy.cs** (implementation): Politica de reintentos con backoff exponencial, tope de retardo y jitter opcional, mas tres presets estaticos. ExecuteAsync reintenta segun un predicado configurable o, por defecto, ante HttpRequestException, TimeoutException y TaskCanceledException.

### AITaskAgentConfigurationKeys (class) — Configuration/AITaskAgentConfigurationKeys.cs:6-25
Clase estatica de constantes con las rutas de configuracion del framework. Centraliza los nombres de seccion bajo AITaskAgent.
Colabora con: ServiceCollectionExtensions.AddAITaskAgent: lee RootSection (relacion entrante)
- `RootSection (const)` [9-9]: Seccion raiz AITaskAgent. | entra: ninguno | sale: constante | efectos: ninguno | lanza: ninguno
- `LlmProviders/Observability/Timeouts/Resilience/Conversation (const)` [12-24]: Subsecciones concatenadas con RootSection por interpolacion const. | entra: ninguno | sale: constantes string | efectos: ninguno | lanza: ninguno

### AITaskAgentOptions (class) — Configuration/AITaskAgentOptions.cs:7-23
Contenedor sellado de las opciones del framework. Cada subopcion se instancia con valores por defecto.
Colabora con: ObservabilityOptions, TimeoutOptions, CircuitBreakerOptions, RateLimitOptions, ConversationOptions: agregados como propiedades; BaseLlmStep.BuildLlmRequestAsync: consumidor
- `Observability { get; init; }` [10-10]: Opciones de observabilidad. | entra: valor por defecto new() | sale: ObservabilityOptions | efectos: ninguno | lanza: ninguno
- `Timeouts { get; init; }` [13-13]: Opciones de timeouts. | entra: valor por defecto new() | sale: TimeoutOptions | efectos: ninguno | lanza: ninguno
- `CircuitBreaker { get; init; }` [16-16]: Opciones del circuit breaker. | entra: valor por defecto new() | sale: CircuitBreakerOptions | efectos: ninguno | lanza: ninguno
- `RateLimit { get; init; }` [19-19]: Opciones de rate limit. | entra: valor por defecto new() | sale: RateLimitOptions | efectos: ninguno | lanza: ninguno
- `Conversation { get; init; }` [22-22]: Opciones de conversacion. | entra: valor por defecto new() | sale: ConversationOptions | efectos: ninguno | lanza: ninguno

### CircuitBreakerOptions (class) — Configuration/CircuitBreakerOptions.cs:6-13
Opciones del circuit breaker (umbral y duracion de apertura en segundos). Sin conexion visible en el lote con la clase CircuitBreaker.
Colabora con: AITaskAgentOptions: lo agrega
- `FailureThreshold { get; init; } = 5` [9-9]: Fallos consecutivos para abrir. | entra: configuracion | sale: int | efectos: ninguno | lanza: ninguno
- `OpenDurationSeconds { get; init; } = 60` [12-12]: Segundos de apertura. | entra: configuracion | sale: int | efectos: ninguno | lanza: ninguno

### DiContractResolver (class) — Configuration/DiContractResolver.cs:10-25
Contract resolver de Newtonsoft que hace que cada contrato de objeto construya sus instancias con ActivatorUtilities y el IServiceProvider recibido por constructor primario.
Colabora con: DefaultContractResolver: base; ActivatorUtilities: instanciacion; StepResultFactory.StepActivatorInfo.CreateInstance: relacion saliente del grafo
- `DiContractResolver(IServiceProvider sp)` [10-10]: Constructor primario que captura el proveedor de servicios. | entra: IServiceProvider | sale: instancia | efectos: ninguno | lanza: ninguno
- `CreateObjectContract(Type objectType) : JsonObjectContract` [12-24]: Obtiene el contrato base y, si el tipo es clase distinta de string, reemplaza DefaultCreator por ActivatorUtilities.CreateInstance(sp, objectType). | entra: tipo a deserializar | sale: contrato modificado | efectos: muta el contrato devuelto (cacheado por el resolver) | lanza: no captura: las excepciones de ActivatorUtilities se producen al deserializar, no aqui

### ObservabilityOptions (class) — Configuration/ObservabilityOptions.cs:8-21
Opciones de observabilidad con valores por defecto: Debug, capacidad 100, metricas y tracing activos.
Colabora con: LogLevel: tipo de EventLogLevel
- `EventLogLevel` [11-11]: Nivel de log de eventos. | entra: config | sale: LogLevel | efectos: ninguno | lanza: ninguno
- `EventChannelCapacity` [14-14]: Capacidad del canal por suscriptor. | entra: config | sale: int | efectos: ninguno | lanza: ninguno
- `EnableMetrics` [17-17]: Activa metricas. | entra: config | sale: bool | efectos: ninguno | lanza: ninguno
- `EnableTracing` [20-20]: Activa tracing. | entra: config | sale: bool | efectos: ninguno | lanza: ninguno

### RateLimitOptions (class) — Configuration/RateLimitOptions.cs:6-16
Opciones del token bucket (maximo, intervalo en ms, tokens por recarga). Sus defaults (100/1000/10) difieren de los del constructor de RateLimiter.
Colabora con: AITaskAgentOptions: lo agrega
- `MaxTokens` [9-9]: Capacidad del bucket. | entra: config | sale: int | efectos: ninguno | lanza: ninguno
- `RefillIntervalMs` [12-12]: Intervalo de recarga en ms. | entra: config | sale: int | efectos: ninguno | lanza: ninguno
- `TokensPerRefill` [15-15]: Tokens por intervalo. | entra: config | sale: int | efectos: ninguno | lanza: ninguno

### TimeoutOptions (class) — Configuration/TimeoutOptions.cs:6-25
Timeouts por defecto del framework. StreamingActivityTimeout esta marcada obsoleta y documentada como no leida por nadie.
Colabora con: LlmInferenceSettings.StreamingInactivityTimeoutSeconds: sustituto citado en el atributo Obsolete
- `DefaultPipelineTimeout` [9-9]: Timeout de pipeline (10 min). | entra: config | sale: TimeSpan | efectos: ninguno | lanza: ninguno
- `DefaultStepTimeout` [12-12]: Timeout de paso (1 min). | entra: config | sale: TimeSpan | efectos: ninguno | lanza: ninguno
- `DefaultToolTimeout` [15-15]: Timeout de herramienta (30 s). | entra: config | sale: TimeSpan | efectos: ninguno | lanza: ninguno
- `StreamingActivityTimeout [Obsolete]` [23-24]: Propiedad obsoleta que solo mantiene el enlace de configuracion. | entra: config | sale: TimeSpan | efectos: ninguno | lanza: ninguno

### SwitchStep<TIn> (class) — Core/Steps/SwitchStep.cs:16-94
Paso de enrutamiento generico. Ejecuta un selector que devuelve el paso destino y el valor a propagar, registra el destino en value.NextSteps, emite un evento de enrutamiento y devuelve el valor.
Colabora con: IStep: implementa; PipelineContext: logger, SendEventAsync, CorrelationId; StepRoutingEvent: evento emitido; RouterStep: subclase
- `SwitchStep(string name, Func<...> selector)` [16-23]: Constructor primario; guarda el selector validando que no sea null. | entra: nombre y selector | sale: instancia | efectos: ninguno | lanza: ArgumentNullException si selector es null
- `SwitchStep(string name) [protected]` [25-28]: Constructor para subclases: encadena con un selector placeholder (null!,null!) y luego asigna _selector = SelectTargetStep. | entra: nombre | sale: instancia | efectos: ninguno | lanza: ninguno
- `SelectTargetStep(TIn, PipelineContext) [protected virtual]` [30-33]: Selector por defecto de las subclases; lanza NotImplementedException. | entra: entrada y contexto | sale: nunca devuelve | efectos: ninguno | lanza: NotImplementedException
- `Name { get; } = name` [35-35]: Nombre del paso. | entra: constructor | sale: string | efectos: ninguno | lanza: ninguno
- `IStep.InputType` [37-37]: Devuelve typeof(TIn). | entra: ninguno | sale: Type | efectos: ninguno | lanza: ninguno
- `IStep.OutputType` [39-39]: Devuelve _outType, que cambia tras cada ejecucion. | entra: estado _outType | sale: Type | efectos: ninguno | lanza: ninguno
- `Resume { get; }` [41-41]: Siempre null. | entra: ninguno | sale: IResume? | efectos: ninguno | lanza: ninguno
- `IStep.ExecuteAsync(IStepResult input, PipelineContext, int attempt, IStepResult? last, CancellationToken)` [43-84]: Castea input a TIn, invoca el selector, valida que destino y valor no sean null, fija _outType, anade el destino a value.NextSteps, emite StepRoutingEvent y registra el enrutamiento. | entra: resultado del paso previo (debe ser TIn) y contexto | sale: el valor devuelto por el selector | efectos: muta _outType y value.NextSteps; evento y log | lanza: InvalidCastException si input no es TIn; InvalidOperationException si el selector devuelve null; relanza cualquier excepcion tras loguearla
- `GetRoutingReason(TIn, IStep) [protected virtual]` [90-93]: Texto del motivo de enrutamiento por defecto. | entra: entrada y destino | sale: string | efectos: ninguno | lanza: ninguno

### IRouterOptions (interface) — LLM/Abstractions/IRouterOptions.cs:4-7
Contrato que expone las opciones validas de un enrutador o clasificador.
Colabora con: IntentionAnalyzerStep, RouterStep: implementan; LlmIntentionResult: lo consulta
- `Options { get; }` [6-6]: Lista de identificadores de opcion. | entra: ninguno | sale: IList<string> | efectos: ninguno | lanza: ninguno

### IRouterResult (interface) — LLM/Abstractions/IRouterResult.cs:5-9
Resultado de paso que declara la opcion elegida.
Colabora con: IStepResult: hereda; LlmIntentionResult: implementa; RouterStep: entrada tipada
- `SelectedOption { get; }` [7-7]: Opcion seleccionada. | entra: ninguno | sale: string | efectos: ninguno | lanza: ninguno

### LlmOptions (record) — LLM/Models/LlmOptions.cs:9-77
Record con parametros de muestreo y de invocacion LLM. No se ve ningun consumidor en el lote.
Colabora con: ResponseFormatOptions: propiedad ResponseFormat
- `Model` [12-12]: Identificador del modelo. | entra: init | sale: string? | efectos: ninguno | lanza: ninguno
- `Temperature` [18-18]: Temperatura de muestreo. | entra: init | sale: float? | efectos: ninguno | lanza: ninguno
- `MaxTokens` [21-21]: Tokens maximos a generar. | entra: init | sale: int? | efectos: ninguno | lanza: ninguno
- `ResponseFormat` [27-27]: Formato de respuesta. | entra: init | sale: ResponseFormatOptions? | efectos: ninguno | lanza: ninguno
- `Stop` [33-33]: Secuencias de parada. | entra: init | sale: string[]? | efectos: ninguno | lanza: ninguno
- `TopP` [40-40]: Nucleus sampling. | entra: init | sale: float? | efectos: ninguno | lanza: ninguno
- `TopK` [46-46]: Top-K sampling. | entra: init | sale: int? | efectos: ninguno | lanza: ninguno
- `FrequencyPenalty` [52-52]: Penalizacion por frecuencia. | entra: init | sale: float? | efectos: ninguno | lanza: ninguno
- `PresencePenalty` [58-58]: Penalizacion por presencia. | entra: init | sale: float? | efectos: ninguno | lanza: ninguno
- `Seed` [65-65]: Semilla de reproducibilidad. | entra: init | sale: long? | efectos: ninguno | lanza: ninguno
- `ToolNames` [71-71]: Nombres de herramientas a resolver en IToolRegistry. | entra: init | sale: string[]? | efectos: ninguno | lanza: ninguno
- `User` [76-76]: Identificador de usuario opcional. | entra: init | sale: string? | efectos: ninguno | lanza: ninguno

### IntentionOption (class) — LLM/Results/IntentionOption.cs:6-17
Opcion de clasificacion con Id y Description, ambas obligatorias (required) pero sin validacion de contenido.
Colabora con: IntentionAnalyzerStep: consume la lista
- `Id { get; init; }` [11-11]: Identificador unico de la opcion. | entra: init | sale: string | efectos: ninguno | lanza: ninguno
- `Description { get; init; }` [16-16]: Cuando elegir la opcion. | entra: init | sale: string | efectos: ninguno | lanza: ninguno

### LlmIntentionResult (class) — LLM/Results/LlmIntentionResult.cs:7-34
Resultado LLM cuyo valor es Intention y que actua como IRouterResult. Valida que la opcion devuelta pertenezca a las opciones del paso que lo produjo.
Colabora con: LlmStepResult<Intention>: base; IStep (_step): origen, comprobado contra IRouterOptions; RetryMiddleware.InvokeAsync: invoca ValidateAsync
- `LlmIntentionResult(IStep step)` [7-7]: Constructor primario que reenvia el paso a la base. | entra: paso productor | sale: instancia | efectos: ninguno | lanza: ninguno
- `_step` [9-9]: Copia privada del paso productor. | entra: constructor | sale: IStep | efectos: ninguno | lanza: ninguno
- `IRouterResult.SelectedOption` [10-10]: Devuelve Value.SelectedOption o cadena vacia si Value es null. | entra: Value | sale: string, nunca null | efectos: ninguno | lanza: ninguno
- `ValidateAsync(PipelineContext)` [12-33]: Ejecuta la validacion base de forma bloqueante, y si es valida comprueba que Value sea Intention y que su SelectedOption este (sin distinguir mayusculas) en IRouterOptions.Options del paso. | entra: contexto; Value ya poblado | sale: tupla (IsValid, Error) en Task completada | efectos: ninguno | lanza: AggregateException si la tarea base falla (.Result); posible NullReferenceException si SelectedOption es null

### IntentionAnalyzerStep<TIn> (class) — LLM/Steps/IntentionAnalyzerStep.cs:14-100
Paso LLM de clasificacion de intencion. Construye el system prompt con el catalogo de opciones, publica la variable options y, al finalizar, emite el mensaje de feedback y retoca el historial.
Colabora con: TemplateLlmStep<TIn, LlmIntentionResult>: base; IRouterOptions: expone ids de opcion; LlmRole: aporta SystemPrompt; PipelineContext: eventos y Conversation; LlmResponseEvent: evento de feedback
- `IntentionAnalyzerStep(name, role, options, promptTemplateName, systemPromptTemplateName, templateProvider, tools)` [33-56]: Compone el system prompt (role.SystemPrompt + texto + catalogo) para la base, guarda las opciones, exige que no esten vacias y fija la variable options con una lista markdown. | entra: opciones obligatorias; plantillas opcionales | sale: instancia | efectos: SetVariable(options) | lanza: ArgumentNullException si options es null; ArgumentException si esta vacia
- `IRouterOptions.Options` [58-58]: Proyecta los Id de _options a una lista nueva en cada acceso. | entra: _options | sale: IList<string> | efectos: ninguno | lanza: ninguno
- `SystemTemplateProvider [protected override]` [71-71]: Devuelve null para que el texto de sistema compuesto se renderice directamente. | entra: ninguno | sale: null | efectos: ninguno | lanza: ninguno
- `FinalizeAsync(IStepResult, PipelineContext, CancellationToken)` [74-99]: Tras la base, si el valor es Intention emite LlmResponseEvent con el mensaje de feedback, elimina el ultimo mensaje del historial y, si hay NeedMoreInfo, anade el feedback serializado como mensaje de asistente. | entra: resultado con Intention; contexto con Conversation | sale: Task | efectos: evento, borrado del ultimo mensaje del historial, nuevo mensaje de asistente | lanza: las de la base, SendEventAsync o RemoveMessage

### RouterStep (class) — LLM/Steps/RouterStep.cs:14-62
Enrutador por intencion: elige el paso destino segun SelectedOption entre las rutas dadas, con ruta por defecto opcional.
Colabora con: SwitchStep<IRouterResult>: base; IRouterResult: entrada; IRouterOptions: expone las claves de rutas
- `RouterStep(string name, Dictionary<string, IStep> routes, IStep? defaultRoute)` [14-18]: Constructor primario que invoca el constructor protegido de la base. | entra: nombre, rutas, ruta por defecto | sale: instancia | efectos: ninguno | lanza: ninguno
- `DefaultRoute` [24-24]: Expone la ruta por defecto. | entra: constructor | sale: IStep? | efectos: ninguno | lanza: ninguno
- `IRouterOptions.Options` [26-26]: Instantanea de las claves de routes tomada al construir. | entra: routes | sale: IList<string> | efectos: ninguno | lanza: ninguno
- `Routes` [37-37]: Copia del diccionario con comparador OrdinalIgnoreCase; es mutable y es la que lee SelectTargetStep. | entra: routes | sale: Dictionary<string, IStep> | efectos: ninguno | lanza: ArgumentException si routes tiene claves que solo difieren en mayusculas
- `GetRoutingReason(IRouterResult, IStep) [override]` [43-46]: Texto del motivo con la opcion seleccionada. | entra: entrada y destino | sale: string | efectos: ninguno | lanza: ninguno
- `SelectTargetStep(IRouterResult, PipelineContext) [override]` [48-61]: Busca SelectedOption en Routes, recurre a defaultRoute si no existe, anade el destino a input.NextSteps y devuelve (destino, input). | entra: resultado con SelectedOption | sale: tupla (IStep, IStepResult) | efectos: anade a input.NextSteps | lanza: InvalidOperationException si SelectedOption es null o no hay ruta ni ruta por defecto

### ResponseFormatOptions (record) — LLM/Support/ResponseFormatOptions.cs:8-24
Formato de salida del LLM (tipo y esquema JSON opcional).
Colabora con: ResponseFormatType: tipo; NJsonSchema.JsonSchema: esquema; LlmRequest y BaseLlmStep.ConfigureJsonResponse: consumidores
- `Type { get; init; } = Text` [16-16]: Tipo de formato. | entra: init | sale: ResponseFormatType | efectos: ninguno | lanza: ninguno
- `JsonSchema { get; init; }` [22-22]: Esquema JSON opcional. | entra: init | sale: JsonSchema? | efectos: ninguno | lanza: ninguno

### ResponseFormatType (enum) — LLM/Support/ResponseFormatOptions.cs:26-30
Tipos de formato de respuesta soportados: Text y JsonObject.
Colabora con: ResponseFormatOptions: usa el enum
- `Text / JsonObject` [28-29]: Valores del enum. | entra: ninguno | sale: enum | efectos: ninguno | lanza: ninguno

### CircuitBreaker (class) — Resilience/CircuitBreaker.cs:8-115
Circuit breaker thread-safe por lock. Abre tras N fallos consecutivos y tras la duracion de apertura deja pasar llamadas en HalfOpen.
Colabora con: CircuitState: estado; CircuitBreakerOpenException: se lanza al estar abierto; ILogger: trazas opcionales
- `CircuitBreaker(int failureThreshold = 5, TimeSpan? openDuration = null, ILogger? logger = null)` [8-20]: Constructor primario; copia umbral, duracion (1 min por defecto) y logger; estado inicial Closed. | entra: parametros sin validar | sale: instancia | efectos: ninguno | lanza: ninguno
- `State { get; }` [22-31]: Lee el estado bajo lock. | entra: _state | sale: CircuitState | efectos: ninguno | lanza: ninguno
- `ExecuteAsync<T>(Func<Task<T>> action)` [36-51]: Comprueba el circuito, ejecuta la accion y registra exito o fallo. | entra: accion asincrona | sale: resultado de la accion | efectos: cambia contadores y estado | lanza: CircuitBreakerOpenException si esta abierto; relanza la excepcion de la accion
- `EnsureCircuitClosed()` [53-71]: Si esta Open y paso la duracion pasa a HalfOpen; si no, lanza. | entra: estado y _openedAt | sale: void | efectos: puede fijar HalfOpen; log | lanza: CircuitBreakerOpenException
- `OnSuccess()` [73-85]: Cierra el circuito si estaba HalfOpen y pone a cero los fallos consecutivos. | entra: estado | sale: void | efectos: muta estado; log | lanza: ninguno
- `OnFailure(Exception ex)` [87-103]: Incrementa fallos y abre el circuito al alcanzar el umbral. | entra: excepcion | sale: void | efectos: muta estado y _openedAt; log | lanza: ninguno
- `Reset()` [105-114]: Fuerza Closed y limpia contadores. | entra: ninguno | sale: void | efectos: muta estado; log | lanza: ninguno

### CircuitBreakerOpenException (class) — Resilience/CircuitBreakerOpenException.cs:3-3
Excepcion que senala circuito abierto.
Colabora con: CircuitBreaker.EnsureCircuitClosed: la lanza
- `CircuitBreakerOpenException(string message)` [3-3]: Reenvia el mensaje a Exception. | entra: mensaje | sale: instancia | efectos: ninguno | lanza: ninguno

### CircuitState (enum) — Resilience/CircuitState.cs:3-8
Estados del circuit breaker.
Colabora con: CircuitBreaker: usa el enum
- `Closed / Open / HalfOpen` [5-7]: Valores del enum. | entra: ninguno | sale: enum | efectos: ninguno | lanza: ninguno

### RateLimiter (class) — Resilience/RateLimiter.cs:10-183
Token bucket asincrono. Recarga por un Timer periodico y de forma perezosa en cada operacion; los que esperan se despiertan con un SemaphoreSlim.
Colabora con: Timer: recarga periodica; SemaphoreSlim: senal de recarga; ILogger<RateLimiter>: trazas
- `RateLimiter(int maxTokens = 10, TimeSpan? refillInterval = null, int? tokensPerRefill = null, ILogger<RateLimiter>? logger = null)` [25-39]: Inicializa campos (intervalo 1 s, recarga igual a maxTokens por defecto), llena el bucket y arranca el timer. | entra: parametros sin validar | sale: instancia | efectos: crea un Timer en marcha | lanza: las que lance el Timer con intervalos invalidos
- `RefillTimerCallback(object? state)` [41-67]: Bajo lock recalcula la recarga segun el tiempo transcurrido, limita al maximo y libera permisos en el semaforo. | entra: estado ignorado | sale: void | efectos: muta tokens y _lastRefill; Release del semaforo; log | lanza: ObjectDisposedException si el semaforo ya esta liberado
- `WaitAsync(CancellationToken)` [72-117]: Bucle: recarga, intenta consumir un token y si no hay espera la senal del semaforo. | entra: token de cancelacion | sale: Task completada al consumir un token | efectos: consume un token; log | lanza: OperationCanceledException; ObjectDisposedException tras Dispose
- `TryAcquire()` [122-139]: Recarga y consume un token sin esperar. | entra: ninguno | sale: bool | efectos: consume un token; log | lanza: ninguno
- `RefillTokens()` [141-164]: Misma logica de recarga que el callback, sin tomar el lock (lo supone tomado). | entra: estado bajo lock | sale: void | efectos: muta tokens; Release del semaforo | lanza: ObjectDisposedException si el semaforo esta liberado
- `AvailableTokens { get; }` [166-176]: Recarga y devuelve los tokens disponibles bajo lock. | entra: ninguno | sale: int | efectos: puede recargar y liberar permisos | lanza: ninguno
- `Dispose()` [178-182]: Libera el semaforo y luego el timer. | entra: ninguno | sale: void | efectos: libera recursos | lanza: ninguno

### RetryPolicy (class) — Resilience/RetryPolicy.cs:8-97
Politica de reintentos con backoff exponencial y jitter, con tres presets estaticos y predicado de reintento configurable.
Colabora con: ILogger: aviso en cada reintento; Random.Shared: jitter
- `MaxAttempts / InitialDelay / MaxDelay / BackoffMultiplier / UseJitter` [10-14]: Parametros de la politica con valores por defecto 3, 1 s, 30 s, 2.0 y true. | entra: init | sale: valores | efectos: ninguno | lanza: ninguno
- `ShouldRetry { get; init; }` [19-19]: Predicado opcional que decide si reintentar. | entra: init | sale: Func<Exception,bool>? | efectos: ninguno | lanza: ninguno
- `Default / Aggressive / Conservative` [21-35]: Presets estaticos que crean una politica nueva en cada acceso. | entra: ninguno | sale: RetryPolicy | efectos: ninguno | lanza: ninguno
- `ExecuteAsync<T>(Func<int, Task<T>> action, ILogger? logger, CancellationToken)` [40-83]: Ejecuta la accion con el numero de intento; ante excepcion reintentable espera, aplica backoff y jitter y repite hasta MaxAttempts. | entra: accion que recibe el intento | sale: resultado de la accion | efectos: espera con Task.Delay; log | lanza: la ultima excepcion (sin envolver) al agotar intentos; InvalidOperationException solo si MaxAttempts <= 0; TaskCanceledException del Delay
- `ShouldRetryException(Exception ex)` [85-96]: Usa ShouldRetry si existe; si no, reintenta ante HttpRequestException, TimeoutException o TaskCanceledException. | entra: excepcion | sale: bool | efectos: ninguno | lanza: lo que lance el predicado

## Subsistema `shared` — Infraestructura compartida


- **Core/Abstractions/IResume.cs** (contract): Contrato para pasos que pueden suspenderse y reanudarse (HITL). SaveStateAsync devuelve el estado a persistir y RestoreStateAsync lo reaplica junto con la respuesta humana. No contiene lógica.
- **Core/Abstractions/IStep.cs** (contract): Contrato base de un paso del pipeline (nombre, reanudación, reintentos, timeout, tipos de entrada/salida) con implementaciones por defecto de FinalizeAsync y ValidateAsync. Incluye IStep<TIn,TOut>, que implementa de forma explícita el ExecuteAsync no genérico comprobando por reflexión los tipos y delegando en el tipado.
- **Core/Abstractions/IStepError.cs** (contract): Contrato mínimo de error de paso: mensaje y excepción original, ambos init-only.
- **Core/Abstractions/IStepResult.cs** (contract): Contrato del resultado de un paso: error, información de suspensión, validación por defecto siempre válida, nombre/tipo de paso, valor y lista de siguientes pasos. IStepResult<T> añade el valor tipado ocultando Value.
- **Core/Base/Metrics.cs** (infrastructure): Clase estática interna con el Meter nativo de .NET y los contadores/histogramas de pipeline, paso, LLM y herramientas. Solo declara instrumentos; no los usa.
- **Core/Base/Telemetry.cs** (infrastructure): Expone el ActivitySource único para crear spans de OpenTelemetry; nombre y versión salen de constantes.
- **Core/Execution/PipelineMiddlewareRegistry.cs** (infrastructure): Registro estático global de middlewares aplicados a todos los pipelines. Register, RegisterRange y Clear usan lock; GetMiddlewares devuelve la lista interna sin lock ni copia.
- **LLM/Abstractions/ILlmStepResult.cs** (contract): Contrato de resultado de paso LLM: mensaje del asistente, coste, motivo de fin, modelo y tokens, más una variante genérica que combina con IStepResult<T>.
- **LLM/Models/LlmResult.cs** (model): Resultado de una petición al proveedor: candidatos, métricas de tokens, coste, modelo, respuesta nativa y error con indicador de reintento. Ofrece fábricas Success/Failure y propiedades de conveniencia sobre el primer candidato.
- **LLM/Models/NativeObject.cs** (model): Registro que envuelve un objeto nativo de proveedor con su nombre de proveedor, nombre de tipo y fecha de creación, para reutilizarlo o convertirlo entre proveedores.
- **LLM/Models/ToolCall.cs** (model): Registro independiente del proveedor para una llamada a herramienta (id, nombre, argumentos JSON) con el objeto nativo excluido de la serialización Newtonsoft.
- **LLM/Results/LlmStepResult.cs** (implementation): Resultado de paso LLM sobre StepResult con coste, motivo de fin, modelo, tokens y razonamiento (setters internos). AssistantMessage se resuelve con un valor por defecto cuando no hay mensaje ni valor. Incluye la variante genérica LlmStepResult<T>.
- **Observability/Events/IStepCompletedEvent.cs** (contract): Evento de progreso de fin de paso: duración, mensaje de error y resultado del paso, con propiedad AdditionalData por defecto.
- **Observability/IProgressEvent.cs** (contract): Interfaz base de todos los eventos de progreso: paso emisor, tipo, marca de tiempo, correlación, supresión al usuario y versión de esquema.
- **Support/Caching/ICacheService.cs** (contract): Contrato de caché clave-valor genérica con expiración opcional, borrado, vaciado y GetOrCreateAsync.
- **Support/Template/ITemplateProvider.cs** (contract): Contrato de proveedor de plantillas por nombre: obtener texto (null si no existe) y renderizar con un modelo.

### IResume (interface) — Core/Abstractions/IResume.cs:6-22
Capacidad de guardar y restaurar el estado de un paso para suspender/reanudar ejecuciones con intervención humana.
Colabora con: IStep: se pasa como paso llamante o destino de la restauración
- `SaveStateAsync(IStep callingStep)` [13-13]: Devuelve el estado a persistir del paso, o null si no necesita estado. | entra: Paso que invoca el guardado. | sale: Task<object?> con el estado. | efectos: Depende de la implementación | lanza: Ninguna definida en el contrato
- `RestoreStateAsync(IStep step, object? state, object? ResumeResponse)` [21-21]: Reaplica el estado guardado al paso junto con la respuesta de reanudación. | entra: Paso destino, estado previo y respuesta humana (ambos anulables). | sale: Task sin valor. | efectos: Depende de la implementación | lanza: Ninguna definida en el contrato

### IStep (interface) — Core/Abstractions/IStep.cs:8-52
Contrato no genérico de un paso del pipeline: identidad, política de reintentos y timeout, ejecución sobre IStepResult, finalización y validación con implementación por defecto.
Colabora con: IStepResult: entrada y salida de la ejecución; PipelineContext: contexto de ejecución; IResume: capacidad de reanudación opcional
- `Name { get; }` [11-11]: Nombre del paso para observabilidad. | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `Resume { get; }` [14-14]: Capacidad de reanudación o null. | entra: n/a | sale: IResume? | efectos: ninguno | lanza: ninguno
- `MaxRetries => 1` [18-18]: Valor por defecto de reintentos máximos: 1. | entra: n/a | sale: int | efectos: ninguno | lanza: ninguno
- `MiillisecondsBetweenRetries => 100` [22-22]: Espera por defecto entre reintentos: 100 ms. El nombre del miembro tiene una errata (Miilliseconds). | entra: n/a | sale: int | efectos: ninguno | lanza: ninguno
- `Timeout => null` [27-27]: Timeout opcional del paso; null delega en el valor por defecto del pipeline. | entra: n/a | sale: TimeSpan? | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken)` [29-29]: Ejecuta el paso sobre el resultado previo. | entra: Resultado de entrada, contexto, número de intento y último resultado. | sale: Task<IStepResult> | efectos: Depende de la implementación | lanza: Depende de la implementación
- `FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken)` [37-37]: Hook de finalización; por defecto no hace nada. | entra: Resultado, contexto y token. | sale: Task completada | efectos: ninguno por defecto | lanza: ninguno por defecto
- `ValidateAsync(IStepResult result, PipelineContext context, CancellationToken)` [46-46]: Validación del resultado; por defecto devuelve (true, null). | entra: Resultado, contexto y token. | sale: Task<(bool IsValid, string? Error)> | efectos: ninguno por defecto | lanza: ninguno por defecto
- `InputType { get; }` [48-48]: Tipo de entrada esperado. | entra: n/a | sale: Type | efectos: ninguno | lanza: ninguno
- `OutputType { get; }` [51-51]: Tipo de salida esperado. | entra: n/a | sale: Type | efectos: ninguno | lanza: ninguno

### IStep<TIn, TOut> (interface) — Core/Abstractions/IStep.cs:54-73
Variante tipada de IStep. Implementa explícitamente el ExecuteAsync no genérico comprobando por reflexión los tipos de entrada y último resultado y delegando en el ExecuteAsync tipado; oculta InputType/OutputType con typeof.
Colabora con: IStep: interfaz base; IStepResult: restricción de TIn y TOut
- `IStep.ExecuteAsync(IStepResult input, PipelineContext, int attempt, IStepResult? lastStepResult, CancellationToken)` [59-67]: Si input es asignable a TIn y lastStepResult es null o asignable a TOut, convierte y llama al ExecuteAsync tipado; si no, lanza InvalidOperationException. | entra: input no nulo (se usa input.GetType()); lastStepResult anulable. | sale: Task<IStepResult> con el TOut devuelto. | efectos: Los de la implementación tipada | lanza: InvalidOperationException ante tipo incompatible; NullReferenceException si input es null
- `ExecuteAsync(TIn input, PipelineContext context, int Attempt, TOut? lastStepResult, CancellationToken)` [69-69]: Ejecución tipada a implementar. | entra: Entrada y último resultado tipados. | sale: Task<TOut> | efectos: Depende de la implementación | lanza: Depende de la implementación
- `InputType => typeof(TIn)` [71-71]: Devuelve typeof(TIn). | entra: n/a | sale: Type | efectos: ninguno | lanza: ninguno
- `OutputType => typeof(TOut)` [72-72]: Devuelve typeof(TOut). | entra: n/a | sale: Type | efectos: ninguno | lanza: ninguno

### IStepError (interface) — Core/Abstractions/IStepError.cs:3-8
Error asociado al resultado de un paso: mensaje y excepción original.
Colabora con: IStepResult: lo expone en Error
- `Message { get; init; }` [5-5]: Mensaje de error. | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `OriginalException { get; init; }` [6-6]: Excepción original, si existe. | entra: n/a | sale: Exception? | efectos: ninguno | lanza: ninguno

### IStepResult (interface) — Core/Abstractions/IStepResult.cs:8-33
Resultado de la ejecución de un paso, con estado de error, suspensión, valor, identidad del paso y siguientes pasos dinámicos.
Colabora con: IStepError: error; IStep: lista NextSteps; PipelineContext: validación
- `HasError => Error != null` [11-11]: Indica si hay error. | entra: n/a | sale: bool | efectos: ninguno | lanza: ninguno
- `Error { get; set; }` [14-14]: Error del resultado. | entra: n/a | sale: IStepError? | efectos: ninguno | lanza: ninguno
- `SuspendInfo { get; set; }` [17-17]: Información de suspensión. | entra: n/a | sale: object? | efectos: ninguno | lanza: ninguno
- `ValidateAsync(PipelineContext context)` [20-21]: Validación por defecto: siempre (true, null). | entra: Contexto. | sale: Task<(bool, string?)> | efectos: ninguno | lanza: ninguno
- `StepName / StepType / Value / NextSteps` [22-32]: Propiedades de solo lectura de identidad, valor y siguientes pasos. El comentario de NextSteps afirma que la implementación por defecto devuelve una colección vacía, pero NextSteps no tiene implementación por defecto. | entra: n/a | sale: string, string, object?, List<IStep> | efectos: ninguno | lanza: ninguno

### IStepResult<T> (interface) — Core/Abstractions/IStepResult.cs:39-43
Variante tipada que oculta Value con T?.
Colabora con: IStepResult: base
- `Value { get; }` [42-42]: Valor tipado (new). | entra: n/a | sale: T? | efectos: ninguno | lanza: ninguno

### Metrics (class) — Core/Base/Metrics.cs:9-86
Contenedor estático interno de instrumentos de métricas nativas de .NET para el pipeline, pasos, LLM y herramientas.
Colabora con: AITaskAgentConstants.TelemetryTags: nombre y versión del Meter
- `Meter` [12-14]: Crea el Meter con nombre y versión de constantes. | entra: Constantes. | sale: Meter | efectos: Registra el Meter en el runtime | lanza: ninguno
- `PipelineExecutions / StepExecutions / StepErrors / StepRetries` [18-36]: Contadores long de pipeline y pasos. | entra: n/a | sale: Counter<long> | efectos: ninguno al declarar | lanza: ninguno
- `LlmRequests / LlmTokens / LlmCost` [40-54]: Contadores de peticiones, tokens y coste (double, USD). | entra: n/a | sale: Counter | efectos: ninguno al declarar | lanza: ninguno
- `ToolExecutions` [58-60]: Contador de ejecuciones de herramientas. | entra: n/a | sale: Counter<long> | efectos: ninguno al declarar | lanza: ninguno
- `StepDuration / LlmDuration / PipelineDuration / ToolDuration` [64-85]: Histogramas de duración en ms. | entra: n/a | sale: Histogram<double> | efectos: ninguno al declarar | lanza: ninguno

### Telemetry (class) — Core/Base/Telemetry.cs:10-18
Punto único de acceso al ActivitySource de trazado distribuido.
Colabora con: AITaskAgentConstants.TelemetryTags: nombre y versión
- `Source` [15-17]: ActivitySource estático. | entra: Constantes. | sale: ActivitySource | efectos: Registra la fuente de actividades | lanza: ninguno

### PipelineMiddlewareRegistry (class) — Core/Execution/PipelineMiddlewareRegistry.cs:9-74
Registro estático global de IPipelineMiddleware aplicados a todas las ejecuciones. Estado compartido de proceso.
Colabora con: IPipelineMiddleware: elementos almacenados
- `Register(IPipelineMiddleware middleware)` [19-29]: Añade el middleware bajo lock si no está ya (Contains). | entra: middleware no nulo. | sale: void; modifica la lista estática. | efectos: Estado estático global | lanza: ArgumentNullException si es null
- `RegisterRange(IEnumerable<IPipelineMiddleware> middlewares)` [36-49]: Añade cada elemento no presente bajo lock, sin validar elementos nulos. | entra: Colección no nula. | sale: void | efectos: Estado estático global | lanza: ArgumentNullException si la colección es null
- `GetMiddlewares()` [57-61]: Devuelve la lista interna mutable como IReadOnlyList, sin lock ni copia. | entra: ninguna | sale: Referencia viva a _middlewares | efectos: ninguno | lanza: ninguno
- `Clear()` [67-73]: Vacía la lista bajo lock. | entra: ninguna | sale: void | efectos: Elimina todos los middlewares globales | lanza: ninguno

### ILlmStepResult (interface) — LLM/Abstractions/ILlmStepResult.cs:6-15
Contrato de resultado de un paso LLM con metadatos de coste, modelo, tokens y mensaje del asistente.
Colabora con: IStepResult: base; FinishReason: motivo de fin
- `AssistantMessage { get; set; }` [8-8]: Mensaje del asistente (no anulable). | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `CostUsd / FinishReason / Model / TokensUsed` [9-12]: Metadatos de solo lectura. | entra: n/a | sale: decimal?, FinishReason?, string?, int? | efectos: ninguno | lanza: ninguno
- `GetAssistantMessageOrDefault(string? defaultMessage = null)` [14-14]: Mensaje del asistente o un valor por defecto. | entra: Mensaje por defecto opcional. | sale: string | efectos: ninguno | lanza: ninguno

### ILlmStepResult<T> (interface) — LLM/Abstractions/ILlmStepResult.cs:17-19
Combina ILlmStepResult con IStepResult<T>.
Colabora con: ILlmStepResult; IStepResult<T>

### LlmResult (class) — LLM/Models/LlmResult.cs:9-88
Resultado de una petición de generación: candidatos, métricas y error de proveedor (patrón Result). Sellada, con propiedades init-only salvo CostUsd.
Colabora con: LlmResponse: candidatos; ToolCall: llamadas a herramienta del primer candidato; FinishReason: motivo de fin; BaseLlmStep: rellena CostUsd tras la llamada según el comentario
- `Candidates / TokensUsed / PromptTokens / CompletionTokens / ReasoningTokens / CachedPromptTokens` [12-30]: Propiedades init-only de datos y métricas. | entra: n/a | sale: valores | efectos: ninguno | lanza: ninguno
- `CostUsd { get; internal set; }` [37-37]: Coste estimado, mutable dentro del ensamblado. | entra: n/a | sale: decimal? | efectos: ninguno | lanza: ninguno
- `Model / NativeResponse / Error / IsRetriable` [40-49]: Propiedades init-only. | entra: n/a | sale: valores | efectos: ninguno | lanza: ninguno
- `Content` [52-52]: Contenido del primer candidato o cadena vacía. | entra: n/a | sale: string | efectos: ninguno | lanza: NullReferenceException si Candidates es null
- `Reasoning` [55-55]: Razonamiento del primer candidato o null. | entra: n/a | sale: string? | efectos: ninguno | lanza: NullReferenceException si Candidates es null
- `IsSuccess` [58-58]: True si Error es null. | entra: n/a | sale: bool | efectos: ninguno | lanza: ninguno
- `FinishReason` [61-61]: Motivo de fin del primer candidato o null. | entra: n/a | sale: FinishReason? | efectos: ninguno | lanza: NullReferenceException si Candidates es null
- `ToolCalls` [64-64]: Llamadas a herramienta del primer candidato o null. | entra: n/a | sale: List<ToolCall>? | efectos: ninguno | lanza: NullReferenceException si Candidates es null
- `Success(IReadOnlyList<LlmResponse> candidates, ... )` [67-79]: Construye un resultado correcto copiando los argumentos a las propiedades. | entra: Candidatos y métricas opcionales; no valida null. | sale: LlmResult | efectos: ninguno | lanza: ninguno
- `Failure(Exception error, bool isRetriable)` [82-87]: Construye un resultado fallido con error e indicador de reintento. | entra: Excepción del proveedor y bandera. | sale: LlmResult con Candidates vacío | efectos: ninguno | lanza: ninguno

### NativeObject (record) — LLM/Models/NativeObject.cs:7-20
Envoltorio de un objeto nativo de proveedor con metadatos para decidir reutilización o conversión.
Colabora con: ToolCall: lo referencia en Native; Message (externo): lo usa
- `Provider / Value / TypeName` [10-16]: Propiedades required init-only. | entra: n/a | sale: string, object, string | efectos: ninguno | lanza: ninguno
- `CreatedAt` [19-19]: Fecha UTC de creación por defecto. | entra: n/a | sale: DateTimeOffset | efectos: ninguno | lanza: ninguno

### ToolCall (record) — LLM/Models/ToolCall.cs:8-22
Llamada a herramienta del LLM independiente del proveedor.
Colabora con: NativeObject: objeto nativo, excluido de JSON; Newtonsoft.Json: atributo JsonIgnore
- `Id / Name / Arguments` [11-17]: Propiedades required init-only; Arguments es JSON como cadena. | entra: n/a | sale: string | efectos: ninguno | lanza: ninguno
- `Native` [20-21]: Objeto nativo del proveedor, ignorado por Newtonsoft. | entra: n/a | sale: NativeObject? | efectos: ninguno | lanza: ninguno

### LlmStepResult (class) — LLM/Results/LlmStepResult.cs:8-22
Resultado de paso LLM sobre StepResult con coste, motivo de fin, modelo, tokens y razonamiento; resuelve el mensaje del asistente con valores de reserva.
Colabora con: StepResult: base; ILlmStepResult: implementa; IStep: constructor
- `LlmStepResult(IStep step)` [8-8]: Constructor primario que pasa el paso a StepResult. | entra: Paso. | sale: instancia | efectos: ninguno | lanza: Según StepResult
- `CostUsd / FinishReason / Model / TokensUsed / Reasoning` [11-16]: Propiedades con setter internal. | entra: n/a | sale: valores | efectos: ninguno | lanza: ninguno
- `AssistantMessage { get; set; }` [15-15]: El getter delega en GetAssistantMessageOrDefault(); el setter guarda en _assistantMessage. | entra: string en el setter. | sale: string | efectos: Escribe el campo _assistantMessage | lanza: ninguno
- `GetAssistantMessageOrDefault(string? defaultMessage = null)` [17-21]: Si hay Error devuelve Error.Message; si no, el mensaje guardado o Value.ToString(); si está vacío devuelve el defecto o un texto fijo de éxito. | entra: Mensaje por defecto opcional. | sale: string no vacío | efectos: ninguno | lanza: ninguno

### LlmStepResult<T> (class) — LLM/Results/LlmStepResult.cs:28-31
Variante tipada de LlmStepResult que oculta Value con T?.
Colabora con: LlmStepResult: base; ILlmStepResult<T>: implementa
- `LlmStepResult(IStep step)` [28-28]: Constructor primario. | entra: Paso. | sale: instancia | efectos: ninguno | lanza: ninguno
- `Value { get; internal protected set; }` [30-30]: Getter convierte base.Value a T?; setter asigna base.Value. | entra: T? en el setter. | sale: T? | efectos: Escribe el valor base | lanza: InvalidCastException si el valor base no es T

### IStepCompletedEvent (interface) — Observability/Events/IStepCompletedEvent.cs:6-14
Evento de progreso emitido al completar un paso.
Colabora con: IProgressEvent: base; IStepResult: resultado asociado
- `AdditionalData => []` [8-8]: Propiedad por defecto que crea un diccionario vacío nuevo en cada acceso. | entra: n/a | sale: Dictionary<string, object?> | efectos: ninguno | lanza: ninguno
- `Duration / ErrorMessage / StepResult` [9-12]: Propiedades init-only. | entra: n/a | sale: TimeSpan, string?, IStepResult? | efectos: ninguno | lanza: ninguno

### IProgressEvent (interface) — Observability/IProgressEvent.cs:7-26
Contrato base de eventos de progreso con identidad, tiempo, correlación, supresión y versión de esquema.
Colabora con: IStepCompletedEvent: la extiende
- `StepName / EventType / Timestamp / CorrelationId / SuppressFromUser / SchemaVersion` [10-25]: Propiedades de solo lectura. | entra: n/a | sale: string, string, DateTimeOffset, string?, bool, string | efectos: ninguno | lanza: ninguno

### ICacheService (interface) — Support/Caching/ICacheService.cs:6-35
Contrato de caché genérica clave-valor con expiración opcional.
Colabora con: MemoryCacheService (externo): implementación
- `Get<T>(string key)` [11-11]: Devuelve el valor cacheado. | entra: Clave. | sale: T? (default(T) si no existe, para tipos valor) | efectos: ninguno esperado | lanza: Según implementación
- `Set<T>(string key, T value, TimeSpan? expiration = null)` [16-16]: Guarda un valor con expiración opcional. | entra: Clave, valor, expiración. | sale: void | efectos: Escribe en la caché | lanza: Según implementación
- `Remove(string key)` [21-21]: Elimina una entrada. | entra: Clave. | sale: void | efectos: Modifica la caché | lanza: Según implementación
- `Clear()` [26-26]: Vacía la caché. | entra: ninguna | sale: void | efectos: Modifica la caché | lanza: Según implementación
- `GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null)` [31-34]: Obtiene o crea y guarda un valor. | entra: Clave, factoría y expiración. | sale: Task<T> | efectos: Puede ejecutar la factoría y escribir en la caché | lanza: Según implementación

### ITemplateProvider (interface) — Support/Template/ITemplateProvider.cs:6-15
Proveedor de plantillas por nombre.
- `GetTemplate(string name)` [13-13]: Devuelve el contenido de la plantilla o null si no existe. | entra: Nombre sin extensión. | sale: string? | efectos: Según implementación | lanza: Según implementación
- `Render(string name, object model)` [14-14]: Renderiza la plantilla con un modelo; sin documentación XML. | entra: Nombre y modelo. | sale: string? | efectos: Según implementación | lanza: Según implementación

## Subsistema `tool` — Herramientas LLM
Define herramientas disponibles para LLM y tipos de soporte para sus llamadas y límites de iteración.

- **LLM/Models/ToolCallUpdate.cs** (model): Record inmutable con la actualizacion incremental de una tool call durante streaming: indice, id, nombre, fragmento de argumentos y objeto nativo del proveedor. Existe para que los proveedores emitan deltas independientes del SDK concreto; el agregador (fuera del lote) los une en ToolCall.
- **LLM/Models/ToolDefinition.cs** (model): Record inmutable con la definicion de una herramienta agnostica del proveedor: nombre, descripcion, JSON Schema de parametros como string y objeto nativo opcional. Se incluye en LlmRequest para anunciar las herramientas al modelo.
- **LLM/Models/ToolIterationLimitExceededException.cs** (model): Excepcion (hereda de InvalidOperationException) lanzada cuando un step alcanza MaxToolIterations mientras el modelo sigue pidiendo herramientas. Guarda el nombre del step y el limite. Al no ser fallo de proveedor, el fallback por defecto no cambia de motor; un step derivado puede reconocerla por tipo.
- **LLM/Support/ToolCallXmlParser.cs** (infrastructure): Clase estatica que extrae tool calls del texto de la respuesta cuando el modelo no usa el formato nativo: formato GLM con delimitadores pipe, wrapper tool_call con nombre y arg_key/arg_value, etiquetas XML con nombre de herramienta y JSON en contenido (objeto, array o dentro de tool_call). Tambien elimina esas etiquetas del contenido. Todo se basa en expresiones regulares y Newtonsoft.Json; las herramientas validas se limitan a un HashSet de nombres.
- **LLM/Tools/Abstractions/ITool.cs** (contract): Interfaz de una herramienta invocable por el LLM: nombre, descripcion, guias de uso opcionales y flag IsReadOnly (miembros de interfaz con implementacion por defecto), definicion agnostica y ejecucion asincrona con contexto de observabilidad.
- **LLM/Tools/Abstractions/ITools.cs** (contract): Contrato del registro de herramientas: listar (con filtro por modulo), obtener por nombre, obtener varias por nombres y registrar.
- **LLM/Tools/Base/LlmTool.cs** (contract): Clase base abstracta de herramientas con patron Template Method: ExecuteAsync abre una Activity, emite ToolStartedEvent, ejecuta InternalExecuteAsync, registra metricas y emite ToolCompletedEvent en finally. Expone hooks virtuales para enriquecer actividad y eventos. Existe para que cada herramienta aporte solo su logica y obtenga observabilidad uniforme.
- **LLM/Tools/Implementation/AvailableTools.cs** (implementation): Registro de herramientas protegido con Lock y diccionario insensible a mayusculas. Ofrece una instancia estatica Instance y un constructor publico que acepta IEnumerable de ITool (para DI). Implementa registro por nombre, consulta individual, listado con filtro por nombre de ensamblado y consulta por lista de nombres.

### ToolCallUpdate (record) — LLM/Models/ToolCallUpdate.cs:6-25
Delta de una tool call recibido durante streaming. Todos los campos salvo Index pueden ser null en actualizaciones tempranas; el consumidor los acumula.
Colabora con: NativeObject: metadatos que el proveedor exige devolver (p. ej. firma de pensamiento de Gemini); ToolCall: destino de la agregacion (segun el comentario XML); LlmStreamChunk: lo transporta (relacion entrante del grafo)
- `Index {get;init;} required int` [9-9]: Posicion de la tool call en la lista | entra: lo fija el proveedor | sale: valor entero | efectos: ninguno | lanza: ninguno
- `ToolCallId {get;init;} string?` [12-12]: Id de la llamada (puede faltar al inicio) | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `FunctionName {get;init;} string?` [15-15]: Nombre de la funcion (puede faltar al inicio) | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `FunctionArgumentsUpdate {get;init;} string?` [18-18]: Fragmento incremental de argumentos JSON | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `Native {get;init;} NativeObject?` [24-24]: Objeto nativo del proveedor | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### ToolDefinition (record) — LLM/Models/ToolDefinition.cs:6-19
Descripcion agnostica de una herramienta para enviarla al proveedor LLM: nombre, descripcion y esquema JSON como string.
Colabora con: LlmRequest: lo contiene (relacion entrante del grafo); LlmTool.GetDefinition: lo construye; NativeObject: metadatos nativos opcionales
- `Name {get;init;} required string` [9-9]: Nombre de la herramienta | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `Description {get;init;} required string` [12-12]: Descripcion para el modelo | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `ParametersJsonSchema {get;init;} required string` [15-15]: JSON Schema de parametros como texto, sin validacion de formato | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `Native {get;init;} NativeObject?` [18-18]: Objeto nativo del proveedor | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno

### ToolIterationLimitExceededException (class) — LLM/Models/ToolIterationLimitExceededException.cs:9-20
Error de limite de iteraciones de herramientas. Es InvalidOperationException para que el fallback por defecto no lo trate como fallo de proveedor y un step derivado pueda decidir otra estrategia.
Colabora con: BaseLlmStep.ExecuteAsync: la lanza (relacion entrante del grafo); InvalidOperationException: base
- `StepName {get;}` [11-11]: Nombre del step que alcanzo el limite | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `MaxToolIterations {get;}` [12-12]: Limite configurado que se excedio | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `ToolIterationLimitExceededException(string stepName, int maxToolIterations)` [14-19]: Construye el mensaje fijo con el limite y guarda ambos valores | entra: stepName y maxToolIterations del step llamante | sale: instancia con StepName y MaxToolIterations | efectos: ninguno | lanza: ninguno

### ToolCallXmlParser (class) — LLM/Support/ToolCallXmlParser.cs:15-365
Parser estatico de tool calls embebidas en texto (GLM, wrapper tool_call, XML, JSON). Convierte coincidencias en ToolCall con Id GUID, nombre resuelto contra un HashSet de herramientas y argumentos serializados como JSON. Sin estado mutable; las regex son estaticas compiladas.
Colabora con: ToolCall: resultado de cada parseo; Newtonsoft JToken/JObject/JsonConvert: parseo y serializacion JSON; FinishReasonExtensions.Parse (segun grafo, relacion dudosa: no aparece en el codigo del lote)
- `Campos estaticos Regex (TagRegex, ChildElementRegex, AttributeRegex, GlmToolCallRegex, GlmArgRegex, GlmWrapperRegex, JsonToolCallWrapperRegex)` [17-44]: Define las siete expresiones regulares compiladas del parser, sin matchTimeout | entra: ninguno | sale: Regex estaticas | efectos: ninguno | lanza: ninguno
- `ParseToolCalls(string content, HashSet<string> toolNames)` [46-106]: Intenta formatos en cascada: GLM pipe, wrapper GLM, etiquetas XML con nombre de herramienta (exacto o equivalente guion/guion bajo) y, si no hubo ninguno, JSON en contenido. Devuelve en cuanto GLM o wrapper producen llamadas. | entra: content = texto de la respuesta (no se comprueba null); toolNames = nombres validos | sale: lista de ToolCall con Id GUID | efectos: ninguno | lanza: NullReferenceException o ArgumentNullException si content o toolNames son null
- `ParseJsonInContentToolCalls(string content, HashSet<string> toolNames)` [112-132]: Detecta wrapper tool_call con JSON (solo la primera coincidencia) o contenido que empieza por llave o corchete y delega en TryParseJsonToolCallToken | entra: content, toolNames; devuelve vacio si content es blanco o toolNames vacio | sale: lista de ToolCall | efectos: ninguno | lanza: ninguno
- `TryParseJsonToolCallToken(string json, HashSet<string> toolNames)` [134-160]: Parsea JSON; si es array procesa los JObject, si es objeto procesa uno. Cualquier excepcion se traga y devuelve vacio | entra: json con posible JSON | sale: lista de ToolCall (vacia en error) | efectos: ninguno | lanza: ninguno (catch vacio)
- `ParseSingleJsonToolCall(JObject obj, HashSet<string> toolNames)` [162-195]: Extrae nombre (name, tool_name o function.name), lo resuelve contra toolNames y toma argumentos de arguments, parameters, input o function.arguments | entra: obj JSON ya parseado | sale: ToolCall o null si no hay nombre o no coincide | efectos: ninguno | lanza: InvalidCastException posible en Value<string>() sobre tokens no primitivos (capturada por el llamador)
- `ParseGlmWrapperToolCalls(string content, HashSet<string> toolNames)` [197-223]: Busca wrapper tool_call con nombre en la primera linea y cuerpo, resuelve el nombre y construye argumentos con ParseGlmArguments | entra: content, toolNames | sale: lista de ToolCall | efectos: ninguno | lanza: ninguno
- `ParseGlmToolCalls(string content, HashSet<string> toolNames)` [225-251]: Busca el formato con delimitadores pipe, resuelve el nombre y construye argumentos con ParseGlmArguments | entra: content, toolNames | sale: lista de ToolCall | efectos: ninguno | lanza: ninguno
- `ParseGlmArguments(string innerContent)` [253-278]: Si el contenido parece JSON valido lo devuelve tal cual; si no, extrae pares arg_key/arg_value en un diccionario y lo serializa; sin pares, devuelve un objeto con clave content | entra: innerContent recortado | sale: string JSON de argumentos | efectos: ninguno | lanza: ninguno (catch vacio en validacion de JSON)
- `FindBestToolMatch(string tagName, HashSet<string> toolNames)` [285-300]: Devuelve la primera herramienta cuyo nombre normalizado (minusculas, guion a guion bajo) coincide con el del tag | entra: tagName, toolNames | sale: nombre de herramienta o null | efectos: ninguno | lanza: ninguno
- `RemoveToolCallTags(string content, HashSet<string> toolNames)` [302-320]: Elimina del texto los bloques wrapper GLM, pipe GLM y tags XML cuyo nombre corresponde a una herramienta; deja intactos los demas | entra: content, toolNames | sale: texto sin esas etiquetas | efectos: ninguno | lanza: ninguno
- `BuildArgumentsJson(string attrString, string innerContent)` [322-352]: Construye argumentos: JSON valido en el cuerpo se devuelve tal cual; si no, mezcla atributos del tag y elementos hijo en un diccionario; si no hay nada estructurado y hay texto, usa la clave content | entra: attrString y innerContent de un match de TagRegex | sale: string JSON de argumentos | efectos: ninguno | lanza: ninguno (catch vacio en validacion de JSON)
- `ParseAttributes(string attrString)` [354-364]: Extrae atributos nombre=valor con AttributeRegex a un diccionario | entra: attrString | sale: Dictionary<string,string> | efectos: ninguno | lanza: ninguno

### ITool (interface) — LLM/Tools/Abstractions/ITool.cs:10-56
Contrato de una herramienta invocable por el LLM. Separa Description (va en ToolDefinition) de UsageGuidelines (va en el system prompt) e IsReadOnly, que usa el bucle de herramientas para tolerar repeticiones.
Colabora con: ToolDefinition: devuelto por GetDefinition; PipelineContext: contexto de observabilidad en ExecuteAsync; BaseLlmStep: consumidor (relacion entrante del grafo); LlmRole: referencia a ITool (relacion entrante)
- `Name {get;} string` [13-13]: Nombre unico | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `Description {get;} string` [16-16]: Descripcion de la herramienta | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `UsageGuidelines {get;} string? (implementacion por defecto => null)` [23-23]: Guias de uso para el system prompt | entra: ninguno | sale: null por defecto | efectos: ninguno | lanza: ninguno
- `IsReadOnly {get;} bool (implementacion por defecto => false)` [35-35]: Indica si la herramienta solo lee estado | entra: ninguno | sale: false por defecto | efectos: ninguno | lanza: ninguno
- `GetDefinition()` [38-38]: Devuelve la definicion agnostica | entra: ninguno | sale: ToolDefinition | efectos: ninguno | lanza: ninguno
- `ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken ct)` [50-55]: Ejecuta la herramienta con argumentos JSON | entra: argumentsJson, context, stepName, logger, ct | sale: Task<string> con el resultado | efectos: definido por cada implementacion | lanza: definido por cada implementacion

### ITools (interface) — LLM/Tools/Abstractions/ITools.cs:4-10
Contrato del registro de herramientas, registrado como Singleton en DI segun el grafo.
Colabora con: ITool: elementos del registro; AvailableTools: implementacion
- `GetAllTools(string? moduleName = null)` [6-6]: Lista todas las herramientas, opcionalmente filtradas por modulo | entra: moduleName opcional | sale: IReadOnlyList<ITool> | efectos: ninguno | lanza: ninguno
- `GetTool(string name)` [7-7]: Obtiene una herramienta por nombre | entra: name | sale: ITool? (null si no existe) | efectos: ninguno | lanza: ninguno
- `GetTools(params string[] names)` [8-8]: Obtiene varias herramientas por nombre | entra: names | sale: IReadOnlyList<ITool> | efectos: ninguno | lanza: ninguno
- `Register(ITool tool)` [9-9]: Registra una herramienta | entra: tool | sale: estado del registro | efectos: modifica el registro | lanza: ninguno

### LlmTool (class) — LLM/Tools/Base/LlmTool.cs:16-191
Base abstracta de herramientas con observabilidad integrada (traza, metricas, eventos) mediante Template Method. Las subclases implementan Name, Description, ParametersSchema e InternalExecuteAsync.
Colabora con: PipelineContext: CorrelationId y SendEventAsync; Telemetry.Source y Metrics: traza y metricas; ToolStartedEvent y ToolCompletedEvent: eventos emitidos; ToolDefinition: construido en GetDefinition; ITool: interfaz implementada
- `Name {get;} abstract` [19-19]: Nombre unico, definido por la subclase | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `Description {get;} abstract` [22-22]: Descripcion, definida por la subclase | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `UsageGuidelines {get;} virtual` [28-28]: Guias de uso, null por defecto | entra: ninguno | sale: null | efectos: ninguno | lanza: ninguno
- `IsReadOnly {get;} virtual` [31-31]: Solo lectura, false por defecto | entra: ninguno | sale: false | efectos: ninguno | lanza: ninguno
- `ParametersSchema {get;} protected abstract BinaryData` [34-34]: JSON Schema de parametros como BinaryData | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `GetDefinition()` [39-47]: Construye ToolDefinition con Name, Description y el esquema convertido a string (sin Native) | entra: ninguno | sale: ToolDefinition | efectos: ninguno | lanza: NullReferenceException si ParametersSchema devuelve null
- `ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken ct)` [53-144]: Template Method: abre Activity, etiqueta, envia ToolStartedEvent, ejecuta InternalExecuteAsync, y en finally enriquece, registra metricas y envia ToolCompletedEvent. Relanza las excepciones de la herramienta. | entra: argumentsJson no nulo (se usa .Length); context con CorrelationId; stepName lo da el step llamante | sale: string con el resultado de la herramienta | efectos: Activity, logs, metricas, dos eventos al contexto | lanza: relanza cualquier excepcion de InternalExecuteAsync; puede lanzar las de hooks o SendEventAsync
- `InternalExecuteAsync(string argumentsJson, PipelineContext context, ILogger logger, CancellationToken ct)` [150-154]: Logica concreta de la herramienta (abstracto) | entra: argumentsJson, context, logger, ct | sale: Task<string> | efectos: definido por la subclase | lanza: definido por la subclase
- `EnrichActivityBefore(Activity? activity, string argumentsJson, PipelineContext context)` [163-166]: Hook vacio previo a la ejecucion | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `EnrichActivityAfter(Activity? activity, string result, PipelineContext context)` [171-174]: Hook vacio posterior a la ejecucion | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `EnrichStartedEvent(ToolStartedEvent baseEvent, string argumentsJson, PipelineContext context)` [179-182]: Hook que devuelve el evento tal cual | entra: baseEvent | sale: baseEvent | efectos: ninguno | lanza: ninguno
- `EnrichCompletedEvent(ToolCompletedEvent baseEvent, string result, PipelineContext context)` [187-190]: Hook que devuelve el evento tal cual | entra: baseEvent | sale: baseEvent | efectos: ninguno | lanza: ninguno

### AvailableTools (class) — LLM/Tools/Implementation/AvailableTools.cs:8-71
Registro en memoria de herramientas, indexado por nombre sin distinguir mayusculas y protegido con un Lock. Disponible como singleton estatico y como instancia construible (DI).
Colabora con: ITool: elementos almacenados; ITools: contrato implementado; ServiceCollectionExtensions.UseAITaskAgent: llama a Register (relacion entrante); TemplateLlmStep: llama a GetTools (relacion entrante)
- `Instance {get;} static` [10-10]: Instancia estatica compartida | entra: ninguno | sale: AvailableTools global | efectos: ninguno | lanza: ninguno
- `Campos _lock y _tools` [12-13]: Lock y diccionario con StringComparer.InvariantCultureIgnoreCase | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `AvailableTools()` [15-17]: Constructor vacio publico | entra: ninguno | sale: ninguno | efectos: ninguno | lanza: ninguno
- `AvailableTools(IEnumerable<ITool> tools)` [19-25]: Registra cada herramienta recibida | entra: coleccion de ITool (DI) | sale: registro poblado | efectos: modifica _tools | lanza: NullReferenceException si tools es null; ArgumentNullException si un elemento es null
- `Register(ITool tool)` [27-35]: Inserta o reemplaza la herramienta por su Name | entra: tool no nulo | sale: void | efectos: modifica _tools bajo lock | lanza: ArgumentNullException
- `GetTool(string name)` [37-43]: Busca por nombre | entra: name no nulo | sale: ITool o null | efectos: ninguno | lanza: ArgumentNullException del Dictionary si name es null
- `GetAllTools(string? module = null)` [45-54]: Devuelve todas o las que pertenezcan al ensamblado con ese nombre | entra: module opcional | sale: IReadOnlyList<ITool> copia | efectos: ninguno | lanza: ninguno
- `GetTools(params string[] names)` [56-70]: Devuelve las herramientas encontradas por cada nombre, omitiendo en silencio las que no existen | entra: names | sale: IReadOnlyList<ITool> en el orden pedido | efectos: ninguno | lanza: NullReferenceException si names es null; ArgumentNullException si algun nombre es null