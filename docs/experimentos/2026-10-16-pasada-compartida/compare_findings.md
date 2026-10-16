# Comparación: hallazgos GPT-SOL (informes actuales) frente al registro nuevo

Hallazgos con ID en informes viejos: 117 (117 con cita ruta:línea). Registro nuevo: 182 confirmados.

- Viejos con equivalente en el registro (misma ruta, líneas ±12): 112 de 117
- Registros con equivalente en algún informe viejo: 168 de 182

| Severidad nueva | entradas | con equivalente viejo |
|---|---|---|
| P0 | 1 | 1 |
| P1 | 7 | 6 |
| P2 | 72 | 69 |
| P3 | 102 | 92 |

## Viejos sin equivalente nuevo (posibles omisiones del nuevo, o falsos del viejo)

| ID viejo | sev | informe | título | citas |
|---|---|---|---|---|
| 02-DUP-002 | None | 02 | ` · Wrappers de roles, duplicación conservada.** `AddUserMessage` y `AddAssistantMessage` tienen estructura si | LLM/Conversation/Context/ConversationContext.cs:46-73 |
| 02-DUP-003 | None | 02 | ` · Selectores de modelo y proveedor, duplicación conservada.** `GetModel` y `GetProvider` comparten la selecc | LLM/Steps/BaseLlmStep.cs:83-97 |
| 02-DUP-005 | None | 02 | ` · Descartes del parser, similitud conservada.** `DiscardPotentialTagBuffer` devuelve texto a la salida y `Di | LLM/Streaming/StreamingTagParser.cs:220-240; LLM/Streaming/StreamingTagParser.cs:248-267 |
| 05-ERR-028 | P2 | 05 | P2 | Mutación parcial al añadir mensaje nulo | `LLM/Conversation/Context/ConversationContext.cs:46-71` | LLM/Conversation/Context/ConversationContext.cs:46-71 |
| 05-ERR-030 | P2 | 05 | · P2 · Limpieza de finalización condicionada `BaseLlmStep.FinalizeAsync` restaura el bookmark y después elimin | LLM/Steps/BaseLlmStep.cs:217-264; LLM/Steps/BaseLlmStep.cs:267-331 |

(0 IDs viejos sin cita ruta:línea no se pueden comparar: )

## Equivalentes con severidad distinta

| ID viejo | sev vieja | REG | sev nueva | título nuevo |
|---|---|---|---|---|
| 02-CPX-003 | P2 | REG-005 | P1 | La fusión multi-fichero con separador --- no fusiona los pasos: varios *.yaml pr |
| 02-DUP-004 | P2 | REG-084 | P3 | La recarga descarta el tiempo fraccionario y puede saltarse un tick del timer |
| 04-PRO-002 | P2 | REG-163 | P3 | ActionStep fire-and-forget usa input y context fuera del ciclo de vida del paso |
| 04-PRO-002 | P2 | REG-167 | P3 | La cancelación se registra como fallo (LogError, estado de error, warning de rei |
| 04-PRO-006 | P2 | REG-003 | P1 | CloneForBranch descarta la ruta actual: las ramas paralelas dentro de un LoopSte |
| 04-PRO-006 | P2 | REG-127 | P3 | CloneForBranch reasigna un campo privado por reflexion y crea un record intermed |
| 05-CON-001 | P1 | REG-011 | P2 | Estado por ejecución guardado en campos de instancia de pasos reutilizables |
| 05-CON-001 | P1 | REG-049 | P2 | InvokeLlmAsync concentra batch, streaming, tags, coste y eventos en un método de |
| 05-CON-003 | P2 | REG-003 | P1 | CloneForBranch descarta la ruta actual: las ramas paralelas dentro de un LoopSte |
| 05-ERR-001 | P1 | REG-009 | P2 | Registros y fábricas como estado estático global de proceso |
| 05-ERR-001 | P1 | REG-055 | P2 | Escritura no atomica y read-modify-write sin bloqueo en almacenes de fichero |
| 05-ERR-004 | P2 | REG-082 | P3 | ValidateAsync bloquea con .Result sobre la validacion base |
| 05-ERR-004 | P2 | REG-107 | P3 | Comparacion de opcion puede lanzar NullReferenceException y la validacion se omi |
| 05-ERR-007 | P2 | REG-093 | P3 | Clone no es profundo: los mensajes y sus colecciones se comparten entre ramas |
| 05-ERR-007 | P2 | REG-094 | P3 | Los marcadores se desfasan al eliminar mensajes y quedan obsoletos tras restaura |
| 05-ERR-008 | P2 | REG-163 | P3 | ActionStep fire-and-forget usa input y context fuera del ciclo de vida del paso |
| 05-ERR-008 | P2 | REG-167 | P3 | La cancelación se registra como fallo (LogError, estado de error, warning de rei |
| 05-ERR-010 | P2 | REG-176 | P3 | El nombre del esquema (nombre de fichero) se usa sin sanear como identificador C |
| 05-ERR-014 | P2 | REG-110 | P3 | Entradas nulas o tipos no soportados escapan de los validadores JSON |
| 05-ERR-016 | P2 | REG-007 | P1 | Los nombres de plantilla por defecto terminan en .md y el proveedor añade otro . |
| 05-ERR-020 | P2 | REG-100 | P3 | ValidateUniqueStepNames no detecta duplicados entre subárboles hermanos |
| 05-ERR-022 | P2 | REG-101 | P3 | SendEventAsync traga todas las excepciones, incluida la cancelacion, sin registr |
| 05-ERR-022 | P2 | REG-105 | P3 | La conversión de entrada pierde la excepción original (TypedStep, BaseLlmStep<TI |
| 05-ERR-027 | P2 | REG-106 | P3 | LlmResult sin validación de candidatos y con CostUsd mutable internamente |
| 05-ERR-029 | P2 | REG-098 | P3 | El error de constructor ausente nombra ValueType en vez del tipo de resultado |
| 05-ERR-033 | P3 | REG-015 | P2 | Compilación concurrente o repetida del mismo nombre sin exclusión: segundo ensam |
| 05-ERR-033 | P3 | REG-046 | P2 | Solo se procesa el primer bloque tool_call con JSON; los siguientes se ignoran |
| 05-ERR-034 | P3 | REG-032 | P2 | Con step.MaxRetries <= 0 el paso no se ejecuta nunca y se devuelve un error de v |
| 05-ERR-035 | P3 | REG-006 | P1 | El DAG se valida por aristas pero se ejecuta como cadena lineal: cada paso recib |
| 05-ERR-035 | P3 | REG-039 | P2 | Excepciones tragadas en la fábrica de objetos de YamlDotNet y en el descubrimien |
| 05-SEC-001 | P1 | REG-001 | P0 | Las inclusiones @[ruta] se expanden tras la interpolación y sin directorio base  |
| 06-ARQ-001 | P1 | REG-011 | P2 | Estado por ejecución guardado en campos de instancia de pasos reutilizables |
| 06-ARQ-001 | P1 | REG-059 | P2 | AddPathPart/RemovePathPart sin try/finally: la ruta del contexto no se restaura  |
| 06-ARQ-003 | P2 | REG-097 | P3 | ExecuteAsync explícito de IStep<TIn,TOut> falla con NullReferenceException y men |
| 06-ARQ-004 | P2 | REG-007 | P1 | Los nombres de plantilla por defecto terminan en .md y el proveedor añade otro . |
| 06-ARQ-006 | P2 | REG-005 | P1 | La fusión multi-fichero con separador --- no fusiona los pasos: varios *.yaml pr |
| 06-ARQ-006 | P2 | REG-006 | P1 | El DAG se valida por aristas pero se ejecuta como cadena lineal: cada paso recib |
| 06-ARQ-008 | P2 | REG-003 | P1 | CloneForBranch descarta la ruta actual: las ramas paralelas dentro de un LoopSte |
| 06-ARQ-010 | P3 | REG-009 | P2 | Registros y fábricas como estado estático global de proceso |
| 06-ARQ-010 | P3 | REG-037 | P2 | ValidateRequiredProperties lanza InvalidOperationException si la raiz JSON no es |
| 08-IMP-001 | P1 | REG-049 | P2 | InvokeLlmAsync concentra batch, streaming, tags, coste y eventos en un método de |
| 08-IMP-001 | P1 | REG-077 | P2 | Argumentos de herramientas registrados a nivel Information y prompts completos a |
| 08-IMP-003 | P2 | REG-001 | P0 | Las inclusiones @[ruta] se expanden tras la interpolación y sin directorio base  |
| 08-IMP-003 | P2 | REG-008 | P1 | Includes recursivos sin límite de profundidad ni detección de ciclos |
| 09-IOC-001 | P1 | REG-009 | P2 | Registros y fábricas como estado estático global de proceso |
| 09-IOC-001 | P1 | REG-010 | P2 | GetMiddlewares expone la lista interna mutable sin lock |
| 09-IOC-002 | P1 | REG-009 | P2 | Registros y fábricas como estado estático global de proceso |
| 09-IOC-002 | P1 | REG-023 | P2 | Los timeouts por defecto no son nunca nulos: no hay forma de desactivarlos, y el |
| 09-IOC-004 | P2 | REG-007 | P1 | Los nombres de plantilla por defecto terminan en .md y el proveedor añade otro . |
| 09-IOC-006 | P2 | REG-005 | P1 | La fusión multi-fichero con separador --- no fusiona los pasos: varios *.yaml pr |
| 09-IOC-008 | P3 | REG-007 | P1 | Los nombres de plantilla por defecto terminan en .md y el proveedor añade otro . |
| 09-IOC-008 | P3 | REG-009 | P2 | Registros y fábricas como estado estático global de proceso |

## Nuevos P0/P1/P2 sin equivalente viejo

| REG | sev | kind | título | ancla |
|---|---|---|---|---|
| REG-002 | P1 | bug | ConvertOrThrow usa Parse con la cultura actual para numéricos y fechas de salida del LLM | LLM/Support/LlmHelpers.cs:147-150 |
| REG-030 | P2 | bug | El mensaje por defecto usa Value.ToString(), que para objetos no string produce el nombre  | LLM/Results/LlmStepResult.cs:19-19 |
| REG-031 | P2 | bug | AssistantMessage ignora el valor asignado cuando hay Error y fabrica un texto de éxito | LLM/Results/LlmStepResult.cs:20-20 |
| REG-044 | P2 | bug | CleanJsonResponse recorta desde el primer corchete/llave hasta el último, aunque haya text | LLM/Support/LlmHelpers.cs:27-33 |