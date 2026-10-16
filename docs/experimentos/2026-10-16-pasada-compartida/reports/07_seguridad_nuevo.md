# Informe de Seguridad: superficie de APIs, entradas externas y riesgos

## Resumen

Se incluyen 18 entradas confirmadas del registro: las 9 de `kind: security` (REG-001, REG-077, REG-078, REG-079, REG-173, REG-174, REG-175, REG-176, REG-177) y 9 de otras `kind` cuyo texto o categoría son de seguridad (REG-008, REG-019, REG-020, REG-055, REG-056, REG-110, REG-116, REG-117, REG-182). La severidad es la del registro.

| Severidad | Entradas |
|---|---|
| P0 | REG-001 |
| P1 | REG-008 |
| P2 | REG-019, REG-020, REG-055, REG-056, REG-077, REG-078, REG-079 |
| P3 | REG-110, REG-116, REG-117, REG-173, REG-174, REG-175, REG-176, REG-177, REG-182 |

El riesgo se concentra en el motor de plantillas. REG-001 (P0) permite que un valor interpolado como `{{ input }}` que contenga `@[ruta]` lea un fichero arbitrario del servidor y lo envíe al proveedor LLM. REG-008 (P1) permite tumbar el proceso con la misma entrada, por recursión sin límite. Ambos comparten el pase 3 de includes de `TinyTemplateRendered` (`Support/Template/TinyTemplateRendered.cs:109-115`).

El resto son exposición de datos en logs, ejecución de texto del modelo como llamada a herramienta, y superficies de código dinámico (Roslyn, V8). Ninguna de estas últimas tiene hoy llamador interno en el framework según el propio registro.

## Inventario de superficie

### Puntos de entrada de datos externos

| Entrada | Dónde llega | Cita |
|---|---|---|
| Texto de usuario y metadatos del contexto, interpolados en prompts | `PromptTemplateResolver.Resolve` construye el modelo `{ Input, Context }` y lo pasa al proveedor de plantillas o a `TinyTemplateRendered` | contracts.md:1404-1407, 1431-1435 |
| Respuesta del LLM (texto, JSON, etiquetas, tool calls) | `JsonResponseParser.Parse<T>`, `ToolCallXmlParser.ParseToolCalls`, `StreamingTagParser` y `ITagHandler` (ejemplo `write_file`) | contracts.md:1056, 1071-1079, 1935-1949, 18-19 |
| Argumentos JSON de herramientas | `ITool.ExecuteAsync(argumentsJson, ...)` | contracts.md:1959, 1978 |
| Configuración (`IConfiguration`, sección `AITaskAgent`, `LlmProviders`) | `GetEx`/`BindEx` reconstruyen JSON y expanden `${VAR}`; `ConfigureLlmEnvironmentOptions` crea proveedores y roles | contracts.md:242-250, 1136, 1145 |
| Ficheros YAML de pipeline y JSON Schema | `YamlPipelineFactory.CreateFromFolderAsync` y `SchemaCompiler.CompileFromFolderAsync` leen carpetas de forma recursiva | contracts.md:1005, 1031 |
| Plantillas `.md` | `FileTemplateProvider.GetTemplate`/`LoadTemplateFromFile` | contracts.md:1413-1414 |
| Ficheros de conversación persistidos (JSON y YAML) | `FileConversationStorage.GetConversationAsync`, `YamlFileConversationStorage.GetConversationAsync` | contracts.md:131, 158 |
| Datos de reanudación HITL | `ResumeRequest.UserInput` y `Metadata` | contracts.md:1178-1182 |

### Efectos sobre ficheros, red y ejecución de código

| Efecto | Detalle | Cita |
|---|---|---|
| Escritura de ficheros | Almacenes JSON/YAML escriben sin atomicidad; el YAML hace lectura y reescritura sin bloqueo | contracts.md:130, 157 |
| Lectura de ficheros | Includes de plantilla leen rutas; la comprobación de base solo aplica si `baseDirectory` no es null | contracts.md:1441 |
| Salida por consola | `ResolveFileInclude` usa `Console.WriteLine` ante error; `ConsoleStepTracer` escribe a consola | contracts.md:1441, 1455 |
| Red hacia proveedores LLM | `ILlmService.InvokeAsync`/`InvokeStreamingAsync` hacen E/S de red en las implementaciones; `LlmRequest` lleva `BaseUrl`, `ApiKey`, `ApiVersion` | contracts.md:1196-1197, 1298 |
| Compilación y carga de código | Roslyn compila a memoria y carga el ensamblado en `AssemblyLoadContext.Default`, no descargable | contracts.md:1011 |
| Script JS en proceso | El código generado crea un motor V8 por validación y expone `context` y `self` | contracts.md:1008 |
| Reflexión y activación | `DefaultStepActivator` ejecuta constructores del tipo, incluso no públicos; `DiContractResolver` sustituye el creador por `ActivatorUtilities` | contracts.md:310, 1585 |
| Descubrimiento de tipos | El deserializador YAML recorre todos los ensamblados cargados para mapear tags `[YamlStepTag]` | contracts.md:1033 |
| Ejecución de herramientas y handlers de tags | `ExecuteToolCallsAsync` ejecuta herramientas; los handlers de tags actúan sobre el texto del modelo | contracts.md:1351, 18-20 |

### Secretos y configuración sensible

- `LlmProvider.ApiKey` se guarda en texto plano en el modelo (contracts.md:1271). `LlmProviderRaw` la recibe del binder (contracts.md:1159) y `LlmRequest` la copia del proveedor (contracts.md:1298).
- La configuración expande `${NOMBRE}` leyendo variables de entorno, y si la variable no existe deja el texto original (contracts.md:249).
- `ObservabilityOptions.EventLogLevel` tiene por defecto Debug (contracts.md:1590), mientras que el constructor de `EventChannel` usa Information (contracts.md:1474) y `EventChannelOptions` usa None (contracts.md:1490). El nivel efectivo depende de cómo se construya el canal.
- `EventChannel.LogEvent` incluye el contenido completo de la respuesta LLM en el log (contracts.md:1478).
- `ExecuteToolCallsAsync` registra los argumentos de las herramientas (contracts.md:1351).

## Hallazgos

### REG-001 (P0) Las inclusiones `@[ruta]` se expanden tras la interpolación y sin directorio base desde el camino de DI

- **Ancla**: `Support/Template/TinyTemplateRendered.cs:109-115`. Instancias: `Support/Template/TinyTemplateRendered.cs:57`, `LLM/Support/PromptTemplateResolver.cs:59-60`, `Support/Template/TinyTemplateRendered.cs:253`.
- **Camino de activación**: la plantilla interpola `{{ input }}` o `{{ context.x }}` con texto del usuario. El pase 3 aplica `@[ruta]` sobre el texto ya interpolado. `PromptTemplateResolver` sin proveedor y `ITemplateRendered.Render` pasan `baseDirectory` null, y `ResolveFileInclude` solo restringe la base si no es null. Rutas absolutas, relativas al directorio de trabajo y con `..` se aceptan. Contratos: contracts.md:1435, 1441, 1407.
- **Impacto**: un `@[appsettings.json]` en la entrada lee cualquier fichero accesible al proceso y lo envía al proveedor LLM. El modelo puede reproducirlo al usuario. Con `FileTemplateProvider` sin motor la base es `folderPath` y el control sí aplica.
- **Mitigación**: pasar siempre un `baseDirectory` y rechazar rutas fuera de él. Ejecutar los includes antes de interpolar, o no expandir `@[ ]` sobre valores interpolados.

### REG-008 (P1) Includes recursivos sin límite de profundidad ni detección de ciclos

- **Ancla**: `Support/Template/TinyTemplateRendered.cs:268-270`.
- **Camino de activación**: `ResolveFileInclude` llama a `RenderInternal` sobre el fichero incluido sin límite de profundidad. Una entrada `@[plantilla.md]` cuya plantilla interpola `{{ input }}` recursa sin fin, incluso con la base restringida de `FileTemplateProvider`. Es el mismo pase 3 de REG-001. Contratos: contracts.md:1441.
- **Impacto**: StackOverflow, no capturable, que termina el proceso. Es alcanzable con entrada no confiable. El registro anota que sería P2 si solo la activara el autor de la plantilla.
- **Mitigación**: límite de profundidad, conjunto de rutas en curso para detectar ciclos y no reinterpolar el contenido incluido con el modelo del usuario. Corregir REG-001 elimina el disparador por entrada de usuario.

### REG-019 (P2) `DiContractResolver` reemplaza el creador de todas las clases

- **Ancla**: `Configuration/DiContractResolver.cs:18-20`.
- **Camino de activación**: solo si se pasa `sp` a `GetEx`/`BindEx`. La única ruta es `ConfigureEx` (`Configuration/ConfigurationPatchExtensions.cs:205` y `237`), que el framework no invoca. Contratos: contracts.md:1585.
- **Impacto**: fallo explícito al deserializar tipos cuyo constructor no resuelve DI. Es un riesgo de integridad de la deserialización de configuración, sin explotación descrita en el registro.
- **Mitigación**: limitar el resolver a los tipos del framework.

### REG-020 (P2) Resolución de `ServiceType` por subcadena puede elegir un servicio distinto del configurado

- **Ancla**: `Configuration/ServiceCollectionExtensions.cs:162`.
- **Camino de activación**: configuración ambigua en la que ningún `Name` coincide exactamente. Se acepta el primer servicio cuyo `Name` contenga `ServiceType`, y con varias coincidencias decide el orden de registro. Contratos: contracts.md:1145.
- **Impacto**: el tráfico, con su `ApiKey` y `BaseUrl`, puede enviarse a otro proveedor. El registro lo califica de serio pero improbable.
- **Mitigación**: exigir coincidencia exacta y fallar en arranque si es ambigua.

### REG-055 (P2) Escritura no atómica y read-modify-write sin bloqueo en almacenes de fichero

- **Ancla**: `LLM/Conversation/Storage/FileConversationStorage.cs:51`. Instancias: `LLM/Conversation/Storage/YamlFileConversationStorage.cs:79`, `LLM/Conversation/Storage/YamlFileConversationStorage.cs:51-52`.
- **Camino de activación**: almacén de fichero (no es el por defecto) con guardados concurrentes del mismo id o caída a mitad de escritura. Contratos: contracts.md:130, 157.
- **Impacto**: fichero truncado o corrupto, pérdida de integridad del historial persistido.
- **Mitigación**: escribir a fichero temporal y renombrar; serializar los guardados por id.

### REG-056 (P2) La sanitización de ids como nombre de fichero genera colisiones y es inconsistente entre almacenes

- **Ancla**: `LLM/Conversation/Storage/FileConversationStorage.cs:129`. Instancias: `LLM/Conversation/Storage/YamlFileConversationStorage.cs:153`, `LLM/Conversation/Storage/FileConversationStorage.cs:117-119`.
- **Camino de activación**: ids con caracteres reservados. `a/b` y `a_b` comparten fichero. El almacén YAML no comprueba contención de ruta; el registro indica que solo lo protegen el prefijo `session-` y la sanitización. Contratos: contracts.md:134, 161.
- **Impacto**: una conversación sobrescribe otra, lo que afecta a la separación entre sesiones. El registro no describe escape del directorio base.
- **Mitigación**: derivar el nombre con un hash o codificación reversible del id, y aplicar la comprobación de contención también en el almacén YAML.

### REG-077 (P2) Argumentos de herramientas registrados a nivel Information y prompts completos a nivel Trace

- **Ancla**: `LLM/Steps/BaseLlmStep.LlmInvocation.cs:432`. Instancia: `LLM/Steps/BaseLlmStep.LlmInvocation.cs:486`.
- **Camino de activación**: configuración por defecto (nivel Information) registra nombre y argumentos completos de cada herramienta. Trace vuelca system prompt, historial y respuestas. Contratos: contracts.md:1351, 1352.
- **Impacto**: datos de usuario y de herramientas en los sinks de log. El registro indica que no es explotable directamente.
- **Mitigación**: registrar solo el nombre de la herramienta y un resumen del tamaño, o aplicar redacción de campos sensibles.

### REG-078 (P2) Texto del contenido con una etiqueta o JSON que coincide con una herramienta se ejecuta como tool call

- **Ancla**: `LLM/Support/ToolCallXmlParser.cs:66`. Instancia: `LLM/Support/ToolCallXmlParser.cs:128-129`.
- **Camino de activación**: `ParseToolCalls` trata como llamada cualquier etiqueta o JSON cuyo nombre coincida con una herramienta registrada. El registro indica que no hay llamadores en el framework, así que depende de un consumidor externo. Contratos: contracts.md:1939-1940.
- **Impacto**: contenido de la respuesta del modelo, o texto inyectado en ella, puede disparar herramientas registradas. La gravedad depende de qué hagan esas herramientas, que los contratos no muestran.
- **Mitigación**: aceptar tool calls solo del canal nativo del proveedor, o exigir confirmación para herramientas que no sean de solo lectura.

### REG-079 (P2) Escapado incompleto de la función JS embebida en código C# compilado en proceso

- **Ancla**: `YAML/Compilation/SchemaCompiler.cs:87`.
- **Camino de activación**: solo si se pasa `jsValidateFunction` por API directa; el registro indica que ningún llamador interno lo hace. El escapado trata comillas y saltos de línea pero no la barra invertida, de modo que barra más comilla puede cerrar el literal C#. Contratos: contracts.md:1006, 1008.
- **Impacto**: inyección de código C# en un ensamblado que se carga en el proceso. El propio registro señala que quien controla el JS ya ejecuta código por diseño. El efecto realista es funcional: un JS con barra invertida no compila.
- **Mitigación**: escapar la barra invertida o pasar el JS como recurso o dato, no interpolado en el código generado.

### REG-110 (P3) Entradas nulas o tipos no soportados escapan de los validadores JSON

- **Ancla**: `Support/JSON/JsonSchemaValidator.cs:19`. Instancia: `Support/JSON/JsonSchemaValidator.cs:45`.
- **Camino de activación**: `ValidateRequiredProperties` y `ValidateStructure<T>` solo capturan `JsonException`; `ArgumentNullException` y `NotSupportedException` se propagan. Los validadores no tienen llamadores en el framework. Contratos: contracts.md:1087-1088.
- **Impacto**: validación de entrada que no devuelve `(false, error)` sino excepción. Los contratos advierten además de que no valida esquema real (contracts.md:1057).
- **Mitigación**: capturar estas excepciones y devolver el error en la tupla.

### REG-116 (P3) Regex sobre texto del modelo sin matchTimeout

- **Ancla**: `LLM/Support/ToolCallXmlParser.cs:18`. Instancias: `LLM/Support/ToolCallXmlParser.cs:38`, `LLM/Support/ToolCallXmlParser.cs:43`, `Support/JSON/JsonResponseParser.cs:165-166`, `Support/JSON/JsonResponseParser.cs:167-168`.
- **Camino de activación**: respuesta con muchas aperturas sin cierre. El registro indica coste cuadrático, no exponencial, en parsers sin llamadores en el framework. Contratos: contracts.md:1938, 1081-1082.
- **Impacto**: degradación de CPU por texto del modelo (denegación de servicio acotada).
- **Mitigación**: fijar `matchTimeout` en las regex y acotar la longitud del texto analizado.

### REG-117 (P3) Argumentos JSON `null` o string no validado se pasan como `arguments` a la herramienta

- **Ancla**: `LLM/Support/ToolCallXmlParser.cs:177`. Instancia: `LLM/Support/ToolCallXmlParser.cs:184-185`.
- **Camino de activación**: el modelo emite `arguments` null o texto libre. La herramienta recibe `argumentsJson` inválido o `null` y falla en su propio parseo. Contratos: contracts.md:1942, 1959.
- **Impacto**: validación de entrada de herramientas delegada a cada herramienta; el registro describe fallo en su parseo, no ejecución indebida.
- **Mitigación**: validar que `arguments` sea un objeto JSON antes de invocar la herramienta.

### REG-173 (P3) Resultados de herramientas y contenido de respuestas LLM completos en logs Debug

- **Ancla**: `Configuration/ObservabilityOptions.cs:11`. Instancias: `LLM/Tools/Base/LlmTool.cs:96`, `LLM/Tools/Base/LlmTool.cs:97-98`, `Observability/EventChannel.cs:218`.
- **Camino de activación**: con nivel Debug habilitado. El registro indica que por defecto vía DI el log de eventos va a Debug. Contratos: contracts.md:1590, 1478.
- **Impacto**: datos de usuario y de herramientas en los sinks de log; higiene de logs.
- **Mitigación**: truncar o redactar resultados y contenido, y no habilitar Debug en producción.

### REG-174 (P3) `LlmRequest` transporta `ApiKey` como propiedad de un record con `ToString` autogenerado

- **Ancla**: `LLM/Models/LlmRequest.cs:27`.
- **Camino de activación**: solo si un tercero formatea o registra el objeto. El registro indica que ningún código del framework formatea un `LlmRequest`. Contratos: contracts.md:1292, 1298.
- **Impacto**: filtración de la clave en logs de un consumidor externo.
- **Mitigación**: sobrescribir `PrintMembers` o `ToString` para ocultar `ApiKey`.

### REG-175 (P3) El mensaje de excepción del handler se inserta en el texto visible y en el historial

- **Ancla**: `LLM/Streaming/StreamingTagParser.cs:344`.
- **Camino de activación**: un handler de etiqueta lanza una excepción. Su `ex.Message` sustituye la etiqueta en la salida mostrada y guardada. Contratos: contracts.md:19-20.
- **Impacto**: puede revelar rutas locales; el registro lo califica de baja sensibilidad y no explotable.
- **Mitigación**: insertar un mensaje genérico y dejar el detalle en el log.

### REG-176 (P3) El nombre del esquema (nombre de fichero) se usa sin sanear como identificador C# y nombre de ensamblado

- **Ancla**: `YAML/Compilation/SchemaCompiler.cs:32`. Instancia: `YAML/Compilation/SchemaCompiler.cs:124`.
- **Camino de activación**: el nombre de fichero se interpola como nombre de clase y de ensamblado. La inyección exige controlar los nombres de fichero de la carpeta de esquemas, que según el registro es del desarrollador. Contratos: contracts.md:1005-1006.
- **Impacto**: error de compilación ruidoso con nombres no válidos; vía de inyección solo con directorio no confiable.
- **Mitigación**: validar el nombre contra un patrón de identificador antes de compilar.

### REG-177 (P3) El script JS recibe objetos del host con superficie .NET completa

- **Ancla**: `YAML/Compilation/SchemaCompiler.cs:90-91`.
- **Camino de activación**: `AddHostObject` expone `context` y `this` a V8; es el diseño de la validación JS. Solo es riesgo si el JS procede de una fuente no confiable, y el registro dice que ningún camino del framework lo hace. Contratos: contracts.md:1008.
- **Impacto**: un script malicioso tendría acceso a `PipelineContext` y al resultado.
- **Mitigación**: tratar el JS de validación como código de confianza o exponer un objeto proxy limitado.

### REG-182 (P3) Errores de include escritos con `Console.WriteLine` y con la ruta absoluta insertada en el prompt

- **Ancla**: `Support/Template/TinyTemplateRendered.cs:279-280`. Instancia: `Support/Template/TinyTemplateRendered.cs:279`.
- **Camino de activación**: include fallido en modo no estricto. El marcador `[Error reading ruta]`, con la ruta completa del servidor, viaja al LLM como parte del prompt. Contratos: contracts.md:1441.
- **Impacto**: revelación menor de rutas del servidor al proveedor y posiblemente al usuario; además el error se salta `ILogger`.
- **Mitigación**: registrar por `ILogger` y devolver un marcador sin ruta.

## Lo que los contratos no permiten concluir

- **Transporte hacia el proveedor**: los contratos solo declaran `ILlmService` (contracts.md:1192-1199) y no incluyen las implementaciones HTTP. No hay base para valorar TLS, validación de certificados, cabeceras ni cómo se envía `ApiKey`.
- **Autenticación y autorización**: ningún contrato describe quién puede invocar un pipeline ni con qué entrada. No se puede concluir si la entrada de REG-001 y REG-008 llega de usuarios no confiables, solo que `{{ input }}` es el uso normal según el registro.
- **Herramientas y handlers concretos**: `ITool` e `ITagHandler` son contratos. Qué hace cada implementación, incluidos los de escritura de ficheros como el ejemplo `write_file` (contracts.md:18), no consta, así que el impacto de REG-078 queda sin acotar.
- **Persistencia de contexto**: `IPipelineContextStorage` delega la serialización en la implementación (contracts.md:8). Solo se describe la implementación en memoria (contracts.md:9, 39).
- **Nivel de log efectivo**: los valores por defecto difieren entre `ObservabilityOptions` (Debug, contracts.md:1590), `EventChannel` (Information, contracts.md:1474) y `EventChannelOptions` (None, contracts.md:1490). No consta cuál se aplica en un despliegue real.
- **Almacenamiento de secretos**: los contratos muestran `ApiKey` como cadena en texto plano (contracts.md:1271) y la expansión desde variables de entorno (contracts.md:249), pero no dónde se guarda la configuración en el host.
- **Dependencias de terceros**: no hay datos de versiones ni de vulnerabilidades de YamlDotNet, Newtonsoft, Roslyn o ClearScript.

## Discrepancias de severidad y de registro

- **REG-008** está registrada como `kind: bug` y categoría `templating-prompts`, pero su base de severidad es de seguridad (caída del proceso con entrada no confiable). Se incluye con su severidad P1 sin cambios.
- **REG-079 (P2)** frente a **REG-176 y REG-177 (P3)**: las tres tratan la misma superficie de código dinámico en `SchemaCompiler`, sin llamador interno y con la misma condición de control del JS o de los nombres. Se conservan las severidades del registro, aunque la base de REG-079 describe un efecto realista solo funcional.
- **Recuento de entradas de seguridad**: las notas del registro mencionan "los 11 de seguridad" reverificados, pero el resumen contabiliza 9 entradas `kind: security` (`byKind.security = 9`). Este informe usa las 9 presentes más las 9 de otras `kind` listadas arriba.
- **REG-187** (`kind: security`, estado `rejected`) no se incluye. El registro lo rechaza porque `stepType` procede del registro por tag y no de entrada externa.
