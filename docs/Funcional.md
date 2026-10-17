# AITaskAgent Framework - Especificación Funcional Definitiva

**Versión**: 4.0 (Consolidada)  
**Estado**: DEFINITIVO  
**Fecha**: Enero 2026  
**Documento**: Especificación Maestra Unificada  
**Revisión contra el código**: 2026-10-17 (rama `consolidation`). Donde el texto original describía componentes que no existen en el código, se ha sustituido por el componente real o se ha marcado como *no implementado*.

> **Correspondencia de nombres.** Este documento nació como especificación conceptual y usa varios nombres que no existen como clases. Equivalencias en el código actual:
>
> | Nombre conceptual | Componente real |
> |---|---|
> | `AgentPipeline` | `Pipeline` / `IPipeline` (`Framework/Core/Execution/Pipeline.cs`) |
> | `AgentStep`, `AgentStepBase` | `BaseLlmStep` y `BaseLlmStep<TIn,TOut>` (`Framework/LLM/Steps/BaseLlmStep.cs`); en la práctica `TemplateLlmStep<TIn,TOut>` |
> | `ChatAgentStep` | No existe como clase: `TemplateLlmStep` es *stateful* (usa `context.Conversation`); `StatelessTemplateLlmStep` usa una conversación limpia por ejecución |
> | `RouterAgentStep` | `IntentionAnalyzerStep<TIn>` (clasifica) + `RouterStep` (enruta) |
> | `ParserStep` | No existe: el parseo JSON lo hace `BaseLlmStep.ParseLlmResponseAsync` |
> | `PipelineStep` | No existe: la composición se hace con `GroupStep` (o `NextSteps`) |
> | `LambdaStep` | `DelegatedStep<TIn,TOut>` |
> | `MaxLlmRetries` | `StepBase.MaxRetries` (por defecto 3), aplicado por `RetryMiddleware` |

---

## Tabla de Contenidos

1. [Visión y Propósito](#1-visión-y-propósito)
2. [Architecture Decision Log (ADL)](#2-architecture-decision-log-adl)
3. [Conceptos Fundamentales y Taxonomía](#3-conceptos-fundamentales-y-taxonomía)
4. [El Modelo de Ejecución: Inversión de Control](#4-el-modelo-de-ejecución-inversión-de-control)
5. [Agentes: Entidades Cognitivas](#5-agentes-entidades-cognitivas)
6. [Steps Deterministas: El Rigor del Código](#6-steps-deterministas-el-rigor-del-código)
7. [Patrones de Validación y Corrección](#7-patrones-de-validación-y-corrección)
8. [Conversaciones Multi-Turn](#8-conversaciones-multi-turn)
9. [Gestión de Errores y Reintentos](#9-gestión-de-errores-y-reintentos)
10. [Herramientas (Capabilities)](#10-herramientas-capabilities)
11. [Observabilidad y Control](#11-observabilidad-y-control)
12. [Patrones de Uso Avanzados](#12-patrones-de-uso-avanzados)
13. [Guías de Implementación](#13-guías-de-implementación)


---

## 1. Visión y Propósito

### 1.1 ¿Qué es AITaskAgent?

**AITaskAgent** es un marco de trabajo .NET diseñado para orquestar **Agentes Especializados** y **Procesos Deterministas** en sistemas empresariales donde la creatividad de la Inteligencia Artificial debe estar estrictamente acotada por reglas de negocio, validaciones de código y una ejecución predecible.

**No es un framework genérico para cualquier tipo de agente.** Está optimizado específicamente para:

- **Agentes task-oriented empresariales** con capacidades finitas y conocidas
- Flujos de trabajo donde la **salida del LLM debe ser validada** antes de progresar
- Sistemas que requieren **auditoría completa** de cada decisión
- Aplicaciones donde el **control de costes** (tokens, llamadas LLM) es crítico
- Entornos de producción que necesitan **comportamiento predecible**

### 1.2 Filosofía de Diseño: "Híbrido Estricto"

El framework impone una distinción arquitectónica rígida entre dos mundos:

```
┌─────────────────────────────────────────────────────────────┐
│                    MUNDO PROBABILÍSTICO                     │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              AGENTES (LLM-Powered)                  │    │
│  │  • Creatividad controlada                           │    │
│  │  • Memoria conversacional                           │    │
│  │  • Acceso a herramientas                            │    │
│  │  • Salida NO garantizada hasta validación           │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
                            ↓
                   ┌────────────────┐
                   │   VALIDACIÓN   │
                   │   (Puente)     │
                   └────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│                    MUNDO DETERMINISTA                       │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              STEPS (Código C#)                      │    │
│  │  • Compiladores y parsers                           │    │
│  │  • Validadores de esquemas                          │    │
│  │  • Transformadores de datos                         │    │
│  │  • Conectores I/O (DB, APIs)                        │    │
│  │  • Ejecución binaria: éxito o fallo                 │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

**Propósito:** Proveer un *runtime* donde los Agentes "viven" dentro de un pipeline lineal que garantiza observabilidad, manejo de errores y que **ningún resultado inválido progrese en el flujo**.

### 1.3 Propuesta de Valor

**Para quién es este framework:**

- Equipos C# que construyen agentes especializados (no chatbots genéricos abiertos)
- Proyectos donde el control del flujo y costes son críticos
- Aplicaciones que necesitan comportamiento predecible en producción
- Desarrolladores que valoran type safety y debugging con breakpoints
- Sistemas que requieren auditoría completa (compliance, regulación)

**Para quién NO es:**

- Chatbots conversacionales abiertos sin estructura predefinida
- Sistemas donde el LLM debe decidir completamente el flujo sin restricciones
- Prototipado rápido experimental sin requisitos de producción
- Equipos que quieren definir flujos solo con configuración, sin C# (existe un motor de pipelines YAML —`YamlPipelineFactory`—, pero los tipos de step, validadores y tools siguen siendo código)
- Proyectos que priorizan autonomía total del agente sobre control

### 1.4 Principios de Diseño
AITaskAgent se construye sobre cuatro pilares fundamentales que dictan cada decisión de arquitectura:

1. **Determinismo sobre Autonomía.** Rechazamos la idea de que la IA debe controlar el flujo de la aplicación. En este framework, el código C# es el rey y el LLM es un consejero. El pipeline define la estructura rígida; el agente solo rellena el contenido flexible. No hay "bucles mágicos" ni planes emergentes impredecibles.
2. **Type Safety (Seguridad de Tipos) Radical.** Si no compila, no debería ejecutarse. Huimos de los diccionarios de string y los objetos dinámicos. Desde la definición de los Steps hasta la agregación paralela, todo está fuertemente tipado para aprovechar la robustez del compilador de .NET.
3. **Aislamiento Cognitivo.** Los errores de la IA (alucinaciones, sintaxis inválida) deben resolverse dentro del Agente (containment), nunca propagarse al orquestador. El pipeline principal solo ve resultados válidos o fallos fatales, manteniendo la traza de ejecución limpia y lineal.
4. **Observabilidad Inevitable.** La telemetría no es un "plugin" opcional; es parte de la estructura de datos que transporta la ejecución. Todo step ejecutado a través de `IPipeline.ExecuteAsync` pasa por el `ObservabilityMiddleware`, que siempre está presente (activity, métricas y eventos de inicio/fin). Un step invocado directamente, fuera del pipeline, no deja esa huella.

---

## 2. Architecture Decision Log (ADL)

Estas decisiones son los **cimientos inmutables** del framework. Cualquier cambio futuro debe respetar estos principios o proporcionar una justificación arquitectónica sólida.

### ADR-001: Abstracción de Protocolos LLM

**Decisión:** El framework abstrae el protocolo de comunicación con LLMs a través de la interfaz `ILlmService`, permitiendo implementaciones para diferentes proveedores (en el repositorio: OpenAI, Azure OpenAI, OpenRouter y Google Gemini).

**Contexto:** 
- Diferentes proveedores LLM tienen APIs distintas pero funcionalidad similar
- Los requisitos de negocio pueden cambiar el proveedor preferido
- El framework debe ser agnóstico del proveedor específico

**Justificación:**
- **Flexibilidad**: Cambiar de proveedor sin modificar código de negocio
- **Testabilidad**: Fácil mockear `ILlmService` en pruebas unitarias
- **Multi-proveedor**: Usar diferentes modelos para diferentes steps según necesidad

**Implementación:**
```
ILlmService (abstracción)
├─ OpenAILlmService       (Name "openai",       proyecto OpenAILLmService)
│   ├─ AzureOpenAILlmService (Name "azure-openai")
│   └─ OpenRouterLlmService  (Name "openrouter")
├─ GeminiLlmService       (Name "gemini",       proyecto GeminiLlmService)
├─ NullLlmService         (Framework, sustituto cuando no hay servicio)
└─ [Custom implementations]
```

**Consecuencias:**
- Código de negocio desacoplado del proveedor LLM
- Cada implementación puede optimizar para su proveedor específico
- La configuración vive en `AITaskAgent:LlmProviders` y se resuelve en `LlmEnvironment`: proveedores (`LlmProvider`: `BaseUrl`, `ApiKey`, `ServiceType`, `Inference`), roles (`LlmRole`: proveedor, modelo, system prompt, `Inference`, `Fallbacks`) y `DefaultRole`. Las opciones propias de cada proveedor viajan en `LlmInferenceSettings.Options`.
- Fallback de proveedor: si una llamada falla con `LlmProviderException`, `BaseLlmStep.SwitchToNextFallback` pasa al siguiente `LlmFallback` del rol y reinicia los intentos; cualquier otro error no cambia de proveedor.

---

### ADR-002: Uso de Reflection

**Decisión:** Usar `System.Reflection` para extracción de parámetros de templates y parsing de resultados. (Nota de revisión: el código sí cachea la información de activación de tipos de resultado en `StepResultFactory`, un `ConcurrentDictionary<Type, StepActivatorInfo>`.)

**Contexto:**
- El framework es **I/O Bound** - la latencia está dominada por llamadas LLM (1000-5000ms)
- Reflection en .NET 7+ con NativeAOT está agresivamente optimizada por el runtime
- El overhead de reflection es ~10-50µs por objeto vs ~2000ms del LLM (0.0025% del tiempo total)

**Justificación:**
- Ergonomía de desarrollo: Los usuarios definen propiedades normales, el framework las lee automáticamente
- Mantenibilidad: No hay generación de código, serialización manual o interfaces especiales
- Realidad de performance: En un sistema I/O bound, optimizar CPU es optimización prematura

**Mediciones empíricas:**
```
GetProperties() en objeto con 20 propiedades: ~15µs
Serialización JSON completa: ~100µs
Latencia LLM mínima: 1,000,000µs (1 segundo)
→ Reflection es 0.0015% del tiempo total
```

**Consecuencias:**
- API limpia sin atributos especiales en el camino programático (el registro YAML sí usa `[StepTag]`/`[YamlStepTag]`, y `SchemaCompiler` genera y compila tipos C# con Roslyn a partir de JSON Schema)
- Debugging directo con breakpoints
-  No apto para hot-paths de microsegundos (pero este no es nuestro caso)

**Alternativas rechazadas:**
- Source Generators: Añaden complejidad de compilación para ganancia de 0.001%
- Cacheo manual: .NET ya lo hace mejor que cualquier implementación custom

---

### ADR-003: Separación de Contextos (Técnico vs Negocio)

**Decisión:** `PipelineContext` (técnico, uno por ejecución) está separado de `ConversationContext` (negocio, mutable pero cloneable). `PipelineContext` es un `record` sellado, pero no es inmutable: `Metadata` y `StepResults` son `ConcurrentDictionary` compartidos y la ruta de ejecución (`CurrentStepPath`) cambia durante la ejecución.

**Contexto:**
- En ejecución paralela (`ParallelStep`), múltiples ramas pueden modificar el estado conversacional
- Race conditions en conversaciones compartidas causan corrupción de mensajes
- El contexto técnico (logger, métricas, cancellation) debe ser compartido

**Justificación:**
- **Thread-safety**: Los diccionarios compartidos (`Metadata`, `StepResults`) son `ConcurrentDictionary`; el estado por invocación de los steps LLM vive en un `StepState` interno que no se comparte entre ramas.
- **Context Scoping (Foco)**: `ConversationContext.Clone()` copia el historial completo; no hay filtrado al clonar. Para que un sub-agente no herede el historial se usa `StatelessTemplateLlmStep`, que crea una conversación limpia en cada ejecución.
- **Aislamiento**: Cada rama paralela posee su propia instancia de conversación, evitando corrupciones de memoria en escrituras concurrentes.

**Implementación:**
```
PipelineContext (sealed record)
├─ ServiceProvider (no serializado)
├─ EventChannel    (no serializado)
├─ Runtime         (IPipelineRuntime que la ejecuta, no serializado)
├─ CorrelationId
├─ Metadata        (ConcurrentDictionary, compartido)
├─ StepResults     (ConcurrentDictionary, compartido; caché de resultados por ruta)
└─ Conversation    (ConversationContext, mutable)

En ParallelStep (PipelineContext.CloneForBranch):
├─ ServiceProvider, EventChannel, Runtime, CorrelationId, Metadata, StepResults → Compartidos
└─ Conversation → Clonada por rama; ruta y StepState → propios de la rama
```

**Consecuencias:**
- Seguridad en concurrencia garantizada
- Trazabilidad: Cada rama tiene su propia historia conversacional
-  Requiere clonación explícita en splits paralelos (documentado)

---

### ADR-004: Validación Interna con Feedback Loop

**Decisión:** La corrección de errores semánticos (ej: código que no compila) ocurre **sobre el mismo step**, mediante un bucle de reintentos con feedback del validador, sin volver a steps anteriores. El Pipeline principal es lineal y no gestiona retrocesos (*forward-only*). El bucle lo ejecuta `RetryMiddleware` (siempre presente en la cadena de middlewares del pipeline), no el propio step: reejecuta el step con `attempt` y el resultado erróneo anterior, y `BaseLlmStep` añade el mensaje de error a la conversación como feedback.

**Contexto:**
- Los LLMs producen salidas probabilísticas que pueden fallar validaciones de negocio
- Un pipeline cíclico (Agente → Validador → Agente) es difícil de mantener y monitorear
- Los reintentos deben mantener contexto del error anterior para corrección efectiva

**Justificación:**
- **Observabilidad**: `ObservabilityMiddleware` envuelve una sola vez el conjunto de intentos, así que el step aparece como una única ejecución (una activity, una duración)
- **Encapsulación**: La lógica de corrección está contenida en el step y en `RetryMiddleware`
- **Performance**: Los reintentos no atraviesan toda la cadena de observabilidad

**Diagrama del patrón:**
```
Pipeline (lineal, forward-only)
  └─ Observability → Timeout → RetryMiddleware → BaseLlmStep
       ├─ Intento 1: LLM genera → Validación estructural ✓ → Validación semántica ✗
       ├─ Intento 2: LLM genera con el error como feedback → ✓ → ✓
       └─ Retorna resultado válido al Pipeline
```

**Consecuencias:**
- Métricas: una ejecución del step (`aitaskagent.step.executions`) y N reintentos (`aitaskagent.step.retries`)
- Debugging: Breakpoints en `RetryMiddleware` o en el step
- Los errores de validación sí son visibles: cada fallo emite un `StepValidationEvent` y cada reintento un `StepStartedEvent` con `AttemptNumber`

---

### ADR-005: Async Obligatorio

**Decisión:** Toda la API pública es async. Prohibido bloquear hilos con `.Result`, `.Wait()` o locks excesivos.

**Contexto:**
- Las operaciones de LLM son inherentemente asíncronas (HTTP, streaming)
- Bloquear hilos del ThreadPool causa deadlocks y degrada performance del servidor
- .NET tiene soporte de primera clase para async/await

**Justificación:**
- **Escalabilidad**: Miles de requests concurrentes sin agotar threads
- **Responsividad**: UI no se congela en aplicaciones desktop/mobile
- **Compatibilidad**: Integración natural con ASP.NET Core, Blazor, etc.

**Requisitos:**
- Uso de `Task.WhenAll` o `Parallel.ForEachAsync` para concurrencia
- CancellationToken propagado en todas las operaciones
- Evitar `ConfigureAwait(false)` en código de biblioteca (innecesario en .NET Core+)

**Consecuencias:**
- Performance óptima en servidores web
- Integración natural con ecosistema .NET moderno
-  Curva de aprendizaje para desarrolladores no familiarizados con async

---

### ADR-006: Validación Híbrida (Estructural vs Semántica)

**Decisión:** Distinción clara entre validación del DTO (`IStepResult.ValidateAsync`) y validación de negocio (`IStep.ValidateAsync`, que en `StepBase` recorre los `IStepValidator` registrados con `AddValidator` y que una subclase puede sobrescribir en `Validate`).

**Contexto:**
- Las validaciones tienen diferentes niveles de coste y responsabilidad
- Algunos checks son síncronos y baratos (nulls, rangos)
- Otros requieren servicios externos costosos (compiladores, APIs)

**Separación de responsabilidades:**

| Tipo | Responsable | Cuándo se ejecuta | Ejemplos |
|------|-------------|-------------------|----------|
| **Estructural** | `IStepResult.ValidateAsync()` | Tras cada intento sin error (no existe modo dry-run) | Nulls, tipos, formatos básicos |
| **Semántica** | `IStep.ValidateAsync()` / `IStepValidator` | Siempre, tras una validación estructural correcta | Compilación, queries DB, lógica compleja |

**Ejemplo conceptual:**
```
Resultado: Código C# generado

Validación Estructural (en Result):
✓ La propiedad CSharpCode no es null
✓ Contiene al menos una declaración de tipo
✓ Tiene sintaxis básica de llaves balanceadas

Validación Semántica (IStepValidator / Validate):
✓ El código compila sin errores
✓ Pasa análisis estático (no nullrefs)
✓ Las referencias a librerías existen
```

**Consecuencias:**
- Separación clara de concerns
- Performance: La validación semántica (costosa) no se ejecuta si falla la estructural
- Testabilidad: Validación estructural se testea sin mocks
-  Requiere disciplina del desarrollador para no mezclarlas

---

### ADR-007: Robustez en Parsing JSON (Newtonsoft)
**Decisión**: Estandarizar el uso de Newtonsoft.Json y NJsonSchema para toda la deserialización de respuestas LLM y persistencia de estado.

**Justificación**: System.Text.Json es demasiado estricto para la "creatividad" sintáctica de los LLMs (comas extra, comentarios). Newtonsoft prioriza la tolerancia a fallos sobre la micro-optimización de CPU en este contexto I/O bound. (Excepciones: `YamlFileConversationStorage` persiste conversaciones en YAML con YamlDotNet, y los pipelines YAML se deserializan también con YamlDotNet.)

## 3. Conceptos Fundamentales y Taxonomía

### 3.1 Jerarquía Conceptual

```
┌─────────────────────────────────────────────────────┐
│              APLICACIÓN / HOST                      │
│  (Ej: API REST, Blazor App, Console)                │
└─────────────────────────────────────────────────────┘
                      │
        ┌─────────────┴─────────────┐
        ▼                           ▼
┌─────────────────┐         ┌─────────────────┐
│  MODO: Chat     │         │  MODO: Batch    │
│  Interactivo    │         │  Procesamiento  │
└─────────────────┘         └─────────────────┘
        │                           │
        ▼                           ▼
┌─────────────────────────────────────────────┐
│         PIPELINE (IPipeline)                │
│  (Secuencia orquestada de pasos)            │
└─────────────────────────────────────────────┘
        │
        ├─ Steps LLM (BaseLlmStep)
        │    ├─ IntentionAnalyzerStep
        │    ├─ TemplateLlmStep (CodingAgent)
        │    └─ StatelessTemplateLlmStep (Summarizer)
        │
        ├─ SwitchStep / RouterStep (Bifurcación)
        │    └─ RouteByIntention
        │
        ├─ ParallelStep / GroupStep / LoopStep (Composición)
        │
        └─ ActionStep / DelegatedStep (Código)
             ├─ CommitToRepo
             └─ SendNotification
```

### 3.2 Glosario de Componentes

#### Componentes Principales

**Pipeline (`IPipeline`)**
- **Qué es:** Unidad de ejecución que orquesta una secuencia lineal de steps (`ExecuteAsync(name, steps, input, context, timeout, ct, subscribers)`)
- **Responsabilidad:** Controlar flujo, manejar errores, reportar métricas; compone los middlewares de usuario (`PipelineMiddlewareRegistry`) con los internos Observability → Timeout → Retry
- **Característica clave:** Forward-only (no retrocede). Exige al menos un step y nombres de step únicos

**BaseLlmStep (Cognitivo)**
- **Qué es:** Unidad con capacidad de razonamiento LLM
- **Características:**
  - Tiene System Prompt y memoria conversacional (`context.Conversation` por defecto)
  - Puede usar herramientas (tools) y *tags* en el texto (`TagHandlers`)
  - Se corrige mediante los reintentos de `RetryMiddleware`, con el error como feedback
  - Salida no garantizada hasta validación

**ActionStep (Determinista)**
- **Qué es:** Ejecutor de efectos colaterales (I/O): `ActionStep<TIn>(name, action, fireAndForget)`
- **Características:**
  - Espera a la acción o, con `fireAndForget: true`, la lanza en segundo plano y devuelve éxito al momento
  - Devuelve un `ActionResult` (mensaje de texto), que pasa a ser la entrada del step siguiente: no deja pasar el valor de entrada
  - Típicamente punto final de una rama

**DelegatedStep (Determinista)**
- **Qué es:** Transformación en línea `DelegatedStep<TIn,TOut>(name, func)`; una excepción se convierte en resultado de error

**SwitchStep (Determinista)**
- **Qué es:** Bifurcador de flujo: un selector `Func<TIn, PipelineContext, (IStep, IStepResult)>` elige el step siguiente, que se añade a `NextSteps`
- **Uso típico:** `RouterStep` (subclase) enruta por la opción elegida por `IntentionAnalyzerStep`

**ParallelStep, GroupStep, LoopStep, BreakStep (Composición)**
- `ParallelStep`: ramas concurrentes con conversación clonada por rama; resultado `ParallelResult`
- `GroupStep`: secuencia de steps en el mismo contexto (vía `NextSteps`)
- `LoopStep`: repite sus steps hasta `MaxIterations` (50 por defecto) o hasta que uno devuelve `BreakStepResult` (`BreakStep`)

#### Tipos de Agentes Especializados

**IntentionAnalyzerStep + RouterStep**
- **Propósito:** Clasificar intención del usuario y enrutar
- **Input:** Texto libre del usuario (plantilla por defecto `{{ input }}`)
- **Output:** `LlmIntentionResult` con un `Intention` (`SelectedOption`, `SelectionReason`, `DetailedPrompt`, `UserFeedback`, `ExtraInformation`); la opción es un `string` que debe estar entre las `IntentionOption` configuradas (si no, falla la validación estructural y se reintenta)
- **Prompt:** El catálogo de opciones (`Id` y `Description`) se añade al system prompt; no hay few-shot ni enums

**TemplateLlmStep (conversacional)**
- **Propósito:** Mantener conversaciones coherentes
- **Características:**
  - Stateful: lee/escribe `context.Conversation`
  - Gestiona bookmarks automáticamente (deja solo el par usuario/asistente final)
  - El sliding window lo aplica el servicio LLM al construir la petición (`AITaskAgent:Conversation`)

**StatelessTemplateLlmStep**
- **Propósito:** Transformaciones texto-a-texto o texto-a-JSON
- **Características:**
  - Crea una conversación limpia en cada ejecución (no hereda ni escribe el historial del contexto)
  - Guarda su conversación al suspenderse para poder reanudar

### 3.3 Relaciones entre Componentes

```
1 Aplicación
  └─ N Modos de Interacción (Chat, Batch, Builder UI)
       └─ 1 Pipeline principal por modo
            ├─ N Steps (secuencia)
            └─ Puede ejecutar sub-secuencias (GroupStep, NextSteps, ParallelStep, LoopStep)

1 BaseLlmStep
  ├─ 1 LlmRole (proveedor, modelo, inferencia, fallbacks)
  ├─ 0..1 System Prompt
  ├─ 1 ConversationContext (el del contexto o uno limpio en StatelessTemplateLlmStep)
  ├─ 0..N Tools (capabilities) y 0..N TagHandlers
  └─ 0..N IStepValidator (validación semántica)

1 Pipeline
  ├─ N Steps (ejecución secuencial)
  ├─ 1 PipelineContext (infraestructura)
  └─ 0..N suscriptores de IEventChannel (UI, SSE, logs)
```

---

## 4. El Modelo de Ejecución: Inversión de Control

### 4.1 El Problema que Resuelve

Tradicionalmente, hay dos modelos de ejecución para pipelines:

**Modelo A: Steps Autónomos**
```
Step1 ──invoca──> Step2 ──invoca──> Step3

Ventaja: Flexibilidad total
Desventaja: Observabilidad no garantizada
Desventaja: Debugging complejo
Desventaja: Métricas inconsistentes
```

**Modelo B: Pipeline Orquestador Tradicional**
```
Pipeline conoce grafo completo
  ├─ Ejecuta Step1
  ├─ Ejecuta Step2
  └─ Ejecuta Step3

Ventaja: Observabilidad garantizada
Desventaja: Requiere declarar grafo completo
Desventaja: Routing dinámico complejo
Desventaja: Overhead arquitectónico
```

**Modelo C: Inversión de Control con NextSteps (AITaskAgent)**
```
Step decide siguiente → lo devuelve en result.NextSteps
Pipeline lo ejecuta a través de su cadena de middlewares
Pipeline envuelve con observabilidad

Flexibilidad de A
Observabilidad de B
Sin overhead de grafo declarativo
```

### 4.2 Mecánica del Modelo

**Flujo de ejecución:**

```
[Inicio]
   │
   ▼
┌──────────────────────────────────────┐
│ Pipeline.ExecuteAsync(name, steps,   │
│                       input, context)│
│ • Crea PipelineContext si no hay uno │
│ • context.Runtime = este pipeline    │
│ • Emite PipelineStartedEvent         │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Por cada step (y su ruta en el árbol)│
│ • ¿Resultado en context.StepResults? │
│   → se reutiliza (reanudación)       │
│ • Si no: cadena de middlewares       │
│   Usuario → Observability → Timeout  │
│   → Retry → step.ExecuteAsync()      │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Step1 devuelve su resultado          │
│ • Decide siguiente: Step2            │
│ • NO lo invoca: lo añade a           │
│   result.NextSteps                   │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Pipeline ejecuta los NextSteps       │
│ • Misma cadena de middlewares        │
│ • Ruta anidada (Step1/Step2)         │
│ • Error o suspensión → corta y sube  │
│ • Éxito → guarda en StepResults      │
└──────────────────────────────────────┘
   │
   ▼
[Sigue con el siguiente step de la lista; al final emite PipelineCompletedEvent]
```

**Componentes clave:**

**PipelineContext**
- Estado compartido de la ejecución
- `sealed record` con diccionarios concurrentes (no inmutable)
- Contiene: ServiceProvider, EventChannel, Runtime, CorrelationId, Metadata, StepResults, Conversation. El `CancellationToken` no forma parte del contexto: se pasa como parámetro.

**NextSteps e IPipelineRuntime**
- `IStepResult.NextSteps`: lista de steps que el pipeline ejecutará a continuación (la usan `SwitchStep`, `RouterStep` y `GroupStep`)
- Los steps compuestos (`ParallelStep`, `LoopStep`) ejecutan sus hijos con `context.ExecuteStepsWithNextStepsAsync`, que delega en `context.Runtime` (el pipeline que corre), de modo que los hijos también pasan por los middlewares

**Step**
- Decide cuál es el siguiente paso
- Lo devuelve al Pipeline en lugar de ejecutarlo
- No tiene lógica de observabilidad (puede enriquecerla con hooks `EnrichActivityBefore/After` y `EnrichStartedEvent/EnrichCompletedEvent`)

### 4.3 Ventajas del Modelo

| Característica | Detalle |
|----------------|---------|
| **Observabilidad Garantizada** | Todo paso por steps pasa por el pipeline. Imposible saltarse logging/métricas |
| **Flexibilidad Mantenida** | Steps siguen decidiendo el flujo. Routing dinámico funciona perfectamente |
| **Simplicidad Arquitectónica** | No necesita grafo declarativo; basta con nombres de step únicos por nivel (el registro `StepManager` solo se usa para los pipelines YAML) |
| **Control Centralizado** | Timeouts, reintentos y observabilidad en middlewares; se pueden añadir middlewares propios con `PipelineMiddlewareRegistry` |
| **Testing Mejorado** | Los resultados por ruta quedan en `context.StepResults`, lo que permite verificar qué steps se ejecutaron |

### 4.4 Por qué NO es Coreografía

**Diferencias con Coreografía (Microservicios)**

| Aspecto | Coreografía Clásica | AITaskAgent |
|---------|---------------------|-------------------|
| **Control de flujo** | Distribuido entre actores | Centralizado en Pipeline |
| **Orden de ejecución** | Emergente e impredecible | Determinista y definido |
| **Conocimiento de contexto** | Cada servicio debe conocer a otros | Steps no conocen al resto |
| **Acoplamiento** | Alto (mensajes y eventos) | Bajo (solo contrato I/O) |
| **Supervisión** | Difícil (sin punto central) | Total (Pipeline controla todo) |



**Diagrama aclaratorio:**
```
Coreografía (Microservicio A no sabe que B existe)
   ServiceA → EventBus → ServiceB → EventBus → ServiceC
   (Flujo emergente, difícil de seguir)

AITaskAgent (Step1 decide pero no ejecuta)
   Pipeline → Step1 (decide Step2) → Pipeline (ejecuta Step2)
   (Flujo definido, control centralizado)
```

### 4.5 Suspensión y Reanudación (Human-in-the-loop)

*(Sección añadida en la revisión: el mecanismo existe en el código y no estaba documentado.)*

- Un step se suspende devolviendo un resultado con `SuspendInfo` (por ejemplo `SuspendStepResult`). Debe implementar `IResume`; `StepBase` lo implementa con los hooks `SaveStateAsync` / `RestoreStateAsync`. Si el step no implementa `IResume`, el pipeline lanza `InvalidOperationException`.
- El pipeline guarda el estado del step en `context.Metadata`, registra el resultado en `context.StepResults`, emite `PipelineSuspendedEvent` y devuelve el resultado suspendido (el `PipelineCompletedEvent` sale con `Success = false` y el mensaje "Suspended awaiting input").
- Para reanudar se vuelve a llamar a `ExecuteAsync` con el **mismo** contexto (o uno recuperado con `PipelineContextFactory.CreateFromId` desde `IPipelineContextStorage`, en memoria por defecto) y la respuesta humana como entrada. Los steps ya completados se saltan reutilizando `StepResults` (*fast-forward*) y el step suspendido recibe la respuesta en `RestoreStateAsync`.
- `LoopStep` propaga la suspensión (o el error) de un hijo sin cachear su propio resultado, para poder rehidratarse después. `ParallelStep` no la propaga: el resultado suspendido de una rama queda dentro de su `ParallelResult` (la rama se emite como `BranchCompletedEvent` con `Success = false`).

---

## 5. Agentes: Entidades Cognitivas

### 5.1 Anatomía de un Agente

Los Agentes son los componentes "inteligentes" que heredan de `BaseLlmStep`. A diferencia de los steps deterministas, gestionan incertidumbre y poseen mecanismos de autocorrección.

**Componentes de un Agente:**

```
┌────────────────────────────────────────┐
│         AGENT STEP                     │
├────────────────────────────────────────┤
│                                        │
│  🧠 IDENTIDAD (LlmRole)               │
│  ├─ System Prompt (personalidad)       │
│  ├─ Provider + Model (+ Fallbacks)     │
│  └─ Inference (Temperature, MaxTokens) │
│                                        │
│  💾 MEMORIA                           │
│  ├─ ConversationContext (opcional)     │
│  ├─ Message History                    │
│  └─ Bookmarks (optimización)           │
│                                        │
│   CAPACIDADES                       │
│  ├─ Tools (AvailableTools)             │
│  ├─ Tool Names (permisos)              │
│  ├─ Tool Execution (recursiva)         │
│  └─ TagHandlers (tags en el texto)     │
│                                        │
│  RESILIENCIA                       │
│  ├─ MaxRetries (RetryMiddleware)       │
│  ├─ Feedback Loop (corrección)         │
│  ├─ Fallback de proveedor              │
│  └─ Bookmark Cleanup (tokens)          │
│                                        │
│  MÉTRICAS                          │
│  ├─ TokensUsed                         │
│  ├─ CostUsd                            │
│  └─ aitaskagent.step.retries           │
│                                        │
└────────────────────────────────────────┘
```

### 5.2 Tipos de Agentes

#### StatelessTemplateLlmStep (El Estándar)
**Propósito:** Transformaciones generales texto-a-texto o texto-a-JSON

**Características:**
- Stateless respecto a conversación: crea una `ConversationContext` limpia en cada ejecución
- Útil para tareas puntuales: resúmenes, extracción de entidades, generación de contenido
- Sin memoria persistente entre invocaciones
- El tipo de salida (`TOut : ILlmStepResult`) decide el parseo: texto tal cual o JSON deserializado al tipo del valor; si el tipo tiene esquema, este se pasa al proveedor (si el proveedor declara `JsonSupport`) o se inyecta en el system prompt

**Casos de uso:**
- Generar documentación a partir de código
- Extraer datos estructurados de texto libre
- Traducir entre formatos (JSON → YAML)
- Clasificar sentiment o categorías

#### TemplateLlmStep (El Conversacional)
**Propósito:** Mantener coherencia en conversaciones multi-turn

**Características:**
- Stateful: Lee y escribe en `context.Conversation.History`
- Prompts como plantillas: por defecto `{name}_prompt.md` y `{name}_system.md` vía `ITemplateProvider`, o texto literal
- Gestiona bookmarks automáticamente para optimizar tokens
- Sliding window (lo aplica el servicio LLM): mantiene los primeros N mensajes + los más recientes que quepan en el presupuesto

**Casos de uso:**
- Chatbots de soporte al cliente
- Asistentes interactivos de configuración
- Tutores educativos con seguimiento de progreso
- Sistemas de recomendación contextuales

#### IntentionAnalyzerStep (El Clasificador)
**Propósito:** Toma de decisiones categóricas

**Características:**
- Input: Texto del usuario
- Output: `LlmIntentionResult` (`Intention.SelectedOption` es un `string`, validado contra la lista de `IntentionOption` con comparación sin mayúsculas)
- El catálogo de opciones (`Id` + `Description`) se añade al system prompt; no hay few-shot ni enums
- La temperatura es la del `LlmRole`; el step no fija ninguna
- Si `UserFeedback.NeedMoreInfo` es verdadero, deja en la conversación el feedback para el usuario; la ruta la decide después `RouterStep`

**Casos de uso:**
- Clasificar intención de usuario (crear, modificar, consultar)
- Detectar idioma de entrada
- Seleccionar departamento de atención
- Determinar nivel de urgencia

### 5.3 Ciclo de Vida de un Agente

```
[Usuario envía request]
   │
   ▼
┌─────────────────────────────────────────┐
│ Pipeline invoca BaseLlmStep             │
│ (a través de RetryMiddleware)           │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ Intento 1: crea BOOKMARK en conversación│
│ (Punto de restauración) y añade el      │
│ mensaje de usuario                      │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ LOOP (RetryMiddleware): MaxRetries = 3  │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 1. Construir prompt             │    │
│  │    • System prompt              │    │
│  │    • Conversación previa        │    │
│  │    • Error anterior (si retry)  │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 2. Invocar LLM                  │    │
│  │    • Con tools si configuradas  │    │
│  │    • Timeout del step (global   │    │
│  │      a todos los intentos)      │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 3. ¿Hay tool calls?             │    │
│  │    SI → Ejecutar recursivamente │    │
│  │    NO → Continuar               │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 4. Parsear respuesta            │    │
│  │    • JSON → Objeto tipado       │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 5. Validación Estructural       │    │
│  │    • IStepResult.ValidateAsync()│    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 6. Validación Semántica         │    │
│  │    • IStep.ValidateAsync /      │    │
│  │      IStepValidator             │    │
│  │    • (ej: compilador, DB)       │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ ¿Todo válido?                   │    │
│  │  SI → SALIR del loop            │    │
│  │  NO → Agregar el error como     │    │
│  │       mensaje de usuario        │    │
│  │       (sin restaurar aún)       │    │
│  │       Continuar loop            │    │
│  └─────────────────────────────────┘    │
│                                         │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ FinalizeAsync: limpiar conversación     │
│ • Restaurar el bookmark inicial         │
│ • Éxito: escribir el par canónico       │
│   [usuario / asistente]                 │
│ • Error: no se registra nada más        │
└─────────────────────────────────────────┘
   │
   ▼
[Retornar resultado al Pipeline]
```

**Detalles críticos:**

1. **Bookmark inicial**: Marca el punto de inicio antes de cualquier intento
2. **Loop de corrección**: Los reintentos incluyen el error como feedback al LLM. Solo se reintentan los errores **sin** excepción (respuesta no parseable, validación fallida); un error con excepción no se reintenta y, si es `LlmProviderException`, provoca el cambio al siguiente proveedor de fallback del rol (con los intentos a cero y el bookmark restaurado)
3. **Limpieza automática**: Los intentos fallidos y el tráfico de tools NO quedan en la conversación final (lo hace `FinalizeAsync`; quien invoque el step sin `RetryMiddleware` debe llamarlo)
4. **Métricas**: Cada reintento suma en `aitaskagent.step.retries` y emite un `StepStartedEvent` con `AttemptNumber`

### 5.4 Ejecución Recursiva de Tools

Cuando un Agente solicita usar herramientas, el framework maneja la recursión automáticamente hasta que el LLM deja de solicitar tools.

**Diagrama de recursión:**

```
┌─────────────────────────────────────────────┐
│ InvokeLlmWithToolsAsync(iteration=0)        │
├─────────────────────────────────────────────┤
│ 1. LLM genera respuesta con tool_calls      │
│    [{"name": "get_time"}, {"name": "calc"}] │
│                                             │
│ 2. Agregar mensaje assistant con tool_calls │
│    a conversación                           │
│                                             │
│ 3. Ejecutar TODAS las tools, en secuencia   │
│    (las llamadas duplicadas se eliminan):   │
│    • get_time() → "14:30"                   │
│    • calc(2+2) → "4"                        │
│                                             │
│ 4. Agregar resultados como tool messages    │
│    a conversación                           │
│                                             │
│ 5. Recursión: iteration=1                   │
│    ┌────────────────────────────────────┐   │
│    │ InvokeLlmWithToolsAsync(iteration=1)│  │
│    │ • LLM ve contexto + tool results   │   │
│    │ • Genera respuesta final SIN tools │   │
│    │ • Retorna respuesta                │   │
│    └────────────────────────────────────┘   │
│                                             │
│ 6. Retornar respuesta final                 │
└─────────────────────────────────────────────┘
```

**Límites de seguridad:**
- `MaxToolIterations = 5` (propiedad `init` de `BaseLlmStep`). Al superarlo el step falla con `ToolIterationLimitExceededException`, que no provoca cambio de proveedor
- Detector de repeticiones: corta el bucle si una tool de solo lectura (`ITool.IsReadOnly`) se repite con los mismos argumentos en 3 de las últimas 10 iteraciones, o si un conjunto de llamadas que modifican se repite idéntico en iteraciones consecutivas (a la 2.ª repetición). Una iteración que ejecutó un tag cuenta como progreso. Al cortar, la respuesta final lleva un mensaje explicativo (`ToolLoopStopMessage`)
- Una tool puede terminar el bucle lanzando `ToolLoopStopRequestedException`
- Una tool desconocida o que lanza excepción no rompe el step: su error vuelve al modelo como mensaje de tool
- No hay timeout por tool: `TimeoutOptions.DefaultToolTimeout` existe pero el framework no lo lee; el límite efectivo es el timeout del step

### 5.5 Optimización de Tokens con Bookmarks

Los bookmarks son puntos de restauración en la conversación que permiten:

1. **Limpiar intentos fallidos**: No contaminar el contexto con respuestas inválidas
2. **Sliding window inteligente**: Mantener primeros N mensajes + últimos M (lo implementa `MessageHistory.GetMessagesWithSlidingWindow`, independiente de los bookmarks)
3. **Compresión selectiva**: Resumir secciones antiguas de la conversación (*no implementado*)

**Estrategias de optimización:**

| Estrategia | Cuándo usar | Ahorro de tokens |
|------------|-------------|------------------|
| **Bookmark + Cleanup** | Reintentos de validación | ~500-1000 tokens/retry |
| **Sliding Window** | Conversaciones >10 mensajes | ~30-50% del total |
| **Summary Bookmarks** (*no implementado*) | Conversaciones >50 mensajes | ~60-70% del total |

Las cifras de ahorro son estimaciones de diseño, no mediciones.

**Ejemplo de sliding window:**
```
Conversación original (15 mensajes, 3000 tokens):
[System, User1, Asst1, User2, Asst2, ..., User15, Asst15]

Con sliding window (keepFirstN=2, maxTokens=1500):
[System, User1] + [User13, Asst13, User14, Asst14, User15, Asst15]
                   ↑                                           ↑
           Primeros 2                              Últimos 6
           (contexto)                              (recientes)

Ahorro: 3000 → 1500 tokens (50%)
```

El último grupo de mensajes (el último mensaje y, si es un resultado de tool, la llamada que lo pidió) se envía siempre aunque supere el presupuesto, y nunca se envía un resultado de tool sin su llamada. `UseSlidingWindow` y `SlidingWindowMaxTokens` se leen de `AITaskAgent:Conversation` en cada petición; `KeepFirstNMessages` (2 por defecto) solo se aplica si se pasa un `ConversationOptions` al construir la `ConversationContext`, cosa que la fábrica registrada por `AddAITaskAgent` no hace.

---

## 6. Steps Deterministas: El Rigor del Código

Los steps deterministas **NO usan LLMs**. Son funciones C# puras que garantizan que el pipeline sea predecible y seguro.

### 6.1 Gestión de Errores con StepError

**Arquitectura:** El framework NO usa excepciones para comunicar errores en el flujo normal. En su lugar, cada `IStepResult` puede contener información de error estructurada.

**Componentes:**

```csharp
// Interface (todo resultado implementa)
public interface IStepResult
{
    bool HasError => Error != null;
    IStepError? Error { get; set; }   // ← Información estructurada
    object? SuspendInfo { get; set; } // ← Suspensión (human-in-the-loop)
    string StepName { get; }
    string StepType { get; }
    object? Value { get; }
    List<IStep> NextSteps { get; }    // ← Steps a ejecutar a continuación
    Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context); // validación estructural
}

// Información de error
public sealed record StepError : IStepError
{
    public required string Message { get; init; }
    public Exception? OriginalException { get; init; }
}
```

**Flujo de errores:**
```
Excepción en Step
      │
      ▼
RetryMiddleware la captura (no se reintenta)
      │
      ▼
Crea ErrorStepResult.FromException(step, ex)
      │
      ▼
Pipeline detecta result.HasError
      │
      ▼
Pipeline para el flujo y retorna error al usuario
```

**Beneficios:**
- Las excepciones de los steps no escapan del pipeline (salvo errores de configuración: lista de steps vacía, nombres duplicados o un step que se suspende sin implementar `IResume`)
- Cualquier resultado tipado puede indicar error via `HasError`
- Información estructurada de error para debugging
- El pipeline para gracefully en errores

**Ojo:** un resultado de error **sin** `OriginalException` se considera recuperable y `RetryMiddleware` reintenta el step hasta `MaxRetries`; con excepción, se devuelve sin reintentar.

**Factory methods en ErrorStepResult:**
```csharp
// Desde excepción capturada
ErrorStepResult.FromException(step, ex);

// Desde mensaje simple
ErrorStepResult.FromMessage(step, "Error description");
```

### 6.2 Parseo de Respuestas (no existe `ParserStep`)

**Propósito:** Transformar la respuesta de texto del LLM en el objeto tipado del resultado

**Dónde ocurre:** dentro de `BaseLlmStep.ParseLlmResponseAsync`, no en un step aparte.

**Características:**
- Si el valor del resultado es `string` o primitivo, se usa (o convierte) el contenido tal cual
- Si es una clase, `LlmHelpers.CleanJsonResponse` recorta del primer `{`/`[` al último `}`/`]` (o quita las vallas de markdown) y se deserializa con Newtonsoft
- Si falla, devuelve un error sin excepción, de modo que `RetryMiddleware` reintenta con el mensaje de error como feedback al LLM
- Un step derivado puede sobrescribir `ParseLlmResponseAsync`

**Nota:** existe `JsonResponseParser` (`Framework/Support/JSON`) con cuatro estrategias (parseo directo, bloque de código, búsqueda de JSON en el texto, limpieza y reintento), pero el framework no lo usa; está disponible para código de usuario.

### 6.3 ActionStep (El Ejecutor)

**Propósito:** Ejecutar efectos colaterales (side effects)

**Características:**
- Firma: `ActionStep<TIn>(name, Func<TIn, PipelineContext, int, ActionResult?, Task> action, bool fireAndForget = false)`
- Devuelve un `ActionResult` con un mensaje de texto ("The action X was executed successfully" o el error); no pasa transparente el `Value` de entrada
- Suele ser el punto final de una rama del pipeline
- Si la acción lanza, devuelve un `ActionResult` de error con la excepción (no se reintenta)

**Modos de ejecución:**

| Modo | Descripción | Uso típico |
|------|-------------|------------|
| **Fire-and-Forget** (`fireAndForget: true`) | Lanza la acción con `Task.Run` y devuelve éxito sin esperar; los errores solo se registran en el log | Envío de emails, logging asíncrono |
| **Esperando** (por defecto) | Espera a la acción; no hay rollback ni transacción gestionados por el framework | Guardar en DB, commits a Git |

La idempotencia, si se necesita, es responsabilidad de la acción.

**Casos de uso:**
```
┌─────────────────────────────────┐
│ SaveToDatabase                  │
│ • Input: ValidatedEntity        │
│ • Action: db.Save(entity)       │
│ • Output: ActionResult (mensaje)│
└─────────────────────────────────┘

┌─────────────────────────────────┐
│ PublishEvent                    │
│ • Input: EventData              │
│ • Action: eventBus.Publish()    │
│ • Output: ActionResult (mensaje)│
└─────────────────────────────────┘

┌─────────────────────────────────┐
│ SendEmail                       │
│ • Input: EmailRequest           │
│ • Action: smtp.Send()           │
│ • Output: ActionResult (mensaje)│
└─────────────────────────────────┘
```

### 6.4 SwitchStep (El Enrutador)

**Propósito:** Bifurcar el flujo basándose en un valor determinista

**Configuración:**
- `SwitchStep<TIn>(name, Func<TIn, PipelineContext, (IStep, IStepResult)> selector)`: el selector devuelve el step destino y el resultado que se le pasa; el destino se añade a `NextSteps` y se emite un `StepRoutingEvent`
- `RouterStep(name, Dictionary<string, IStep> routes, IStep? defaultRoute)`: enruta por `IRouterResult.SelectedOption`, sin distinguir mayúsculas
- Falla si el selector no devuelve ruta, o si `RouterStep` no encuentra la opción y no hay `defaultRoute` (lanza `InvalidOperationException`, que `RetryMiddleware` convierte en resultado de error)

**Patrón de uso típico:**

```
IntentionAnalyzerStep (clasifica intención)
          ↓
   LlmIntentionResult (SelectedOption)
          ↓
   RouterStep
          ↓
    ┌─────┴─────┬─────────┐
    ▼           ▼         ▼
 CreatePipe  ModifyPipe  QueryPipe
```

**Ejemplo conceptual:**
```
IntentionOption (Id): DocumentIntent
├─ Summarize
├─ ExtractInfo
├─ Compare
└─ GeneralChat

RouterStep routes:
├─ Summarize   → SummarizePipeline
├─ ExtractInfo → ExtractionPipeline
├─ Compare     → ComparisonPipeline
└─ GeneralChat → ChatPipeline
```

### 6.5 ParallelStep (Ejecución Concurrente)

**Propósito:** Ejecutar múltiples steps en paralelo, aislados cognitivamente, y recoger sus resultados por nombre de rama.

**Arquitectura:**

- **Construcción**: `new ParallelStep(name, params IStep[] branches)` o `ParallelStep<TIn>`.
- **Clonación de Contexto:** Cada rama se ejecuta sobre `context.CloneForBranch()`: conversación clonada (copia completa del historial) y ruta propia; `Metadata` y `StepResults` se comparten.
- **Concurrencia**: `Parallel.ForEachAsync` con `MaxDegreeOfParallelism` (0 o menos = `Environment.ProcessorCount`).
- **Resultado**: un `ParallelResult` cuyo `Value` es un diccionario `nombre de rama → IStepResult` (accesible con `GetResult`, `GetResult<T>` o el indexador, que devuelve el `Value` de la rama). No hay delegados de *merge* ni un DTO de salida: la agregación la hace el step siguiente leyendo el `ParallelResult`.
- **Errores**: si alguna rama falla (o lanza), el `ParallelResult` lleva un `Error` con los mensajes concatenados, y el pipeline se detiene tras el `ParallelStep`. Las demás ramas no se cancelan.
- **Eventos**: `BranchStartedEvent` y `BranchCompletedEvent` por rama.
- **Reintentos**: `MaxRetries = 1` (no se reintenta el conjunto).

```csharp
var parallel = new ParallelStep("Enrichment",
    getUserProfileStep,      // cada rama es un IStep normal
    getPreferencesStep,
    getRecommendationsStep)
{
    MaxDegreeOfParallelism = 2
};

// Step siguiente: combina los resultados por nombre de rama
var merge = new DelegatedStep<ParallelResult, StepResult<UserView>>("Merge",
    (step, input, ctx, attempt, last) => Task.FromResult(new StepResult<UserView>(step,
        new UserView(input["GetUserProfile"], input["GetPreferences"], input["GetRecommendations"]))));
```

**Casos de uso:**

```
┌─────────────────────────────────────────┐
│ RAG Multi-Fuente (Parallel Query)       │
├─────────────────────────────────────────┤
│  Rama 1: VectorDB Technical             │
│  Rama 2: VectorDB Examples              │
│  Rama 3: VectorDB FAQs                  │
│  Rama 4: SQL Historical Data            │
│                                         │
│  Merge: Rankear por relevancia          │
│         Filtrar top 10 documentos       │
└─────────────────────────────────────────┘

┌─────────────────────────────────────────┐
│ Validaciones Independientes             │
├─────────────────────────────────────────┤
│  Rama 1: Schema Validator               │
│  Rama 2: Business Rules Checker         │
│  Rama 3: Security Policy Validator      │
│                                         │
│  Merge: Agregar todos los warnings      │
└─────────────────────────────────────────┘

┌─────────────────────────────────────────┐
│ Enrichment de Datos                     │
├─────────────────────────────────────────┤
│  Rama 1: GetUserProfile (API)           │
│  Rama 2: GetPreferences (DB)            │
│  Rama 3: GetRecommendations (ML)        │
│                                         │
│  Merge: Construir objeto completo       │
└─────────────────────────────────────────┘
```

**Nota de performance:** Solo útil si los sub-steps son I/O-bound (llamadas HTTP, DB, LLM). Para CPU-bound tasks, el overhead puede ser negativo.

### 6.6 Composición: GroupStep, LoopStep y BreakStep (no existe `PipelineStep`)

**Propósito:** Reutilizar una secuencia de steps como un step de otro pipeline

**Características:**
- `GroupStep(name, steps)` / `GroupStep<TIn>`: añade sus steps a `NextSteps` y el pipeline padre los ejecuta en orden, en el **mismo** contexto (ruta anidada `Grupo/Step`). Toma como tipo de entrada el del primer step y como salida el del último; `GroupStep<TIn>` comprueba en el constructor que el primer step acepte `TIn`
- Contexto compartido: `PipelineContext` se propaga
- `LoopStep(name, steps, maxIterations = 50)`: repite la secuencia; sale al devolver un hijo un `BreakStepResult` (lo produce `BreakStep`, que conserva el valor de su entrada), o al agotar las iteraciones (emite `LoopExhaustedEvent`). Cada iteración tiene ruta propia (`Loop/Iter_N`) y, con `OutputIterationToMetadata`, publica el número de iteración en `Metadata`
- Otra opción es llamar a `IPipeline.ExecuteAsync` desde un `DelegatedStep`/`ActionStep`, lo que crea una ejecución anidada independiente

**Patrón de reutilización:**

```
Secuencia Compartida: ValidationGroup (GroupStep)
├─ DelegatedStep: Sintaxis JSON
├─ DelegatedStep: Schema válido
└─ DelegatedStep: Business rules

Pipeline A: ChatFlow
├─ TemplateLlmStep: Generate
├─ GroupStep: ValidationGroup  ← Reutiliza
└─ ActionStep: Save

Pipeline B: BatchFlow
├─ DelegatedStep: Load from file
├─ GroupStep: ValidationGroup  ← Reutiliza
└─ ActionStep: Export results

Pipeline C: APIFlow
├─ DelegatedStep: Parse HTTP body
├─ GroupStep: ValidationGroup  ← Reutiliza
└─ ActionStep: Return JSON
```

**Ventajas:**
- DRY (Don't Repeat Yourself)
- Testing: Testea el pipeline compartido una vez
- Mantenimiento: Cambios en un solo lugar
- Claridad: Composición explícita

---

## 7. Patrones de Validación y Corrección

Esta es la **innovación clave de la arquitectura**. Resuelve el problema de generación de código/datos inválidos sin crear ciclos complejos en el orquestador.

### 7.1 El Problema de los Pipelines Cíclicos

**Pipeline Cíclico Tradicional (Antipatrón):**
```
AgentStep → ValidationStep → ¿Válido?
               ↓                  │
           [Error]                │
               ↓                  │
               └──────────────────┘
                   (Reintentar)

Problemas:
• Difícil de monitorear (¿cuántas vueltas dio?)
• Riesgo de stack overflow
• Métricas confusas (¿qué contar como "ejecución"?)
• Debugging complejo (breakpoints en ciclos)
```

### 7.2 Solución: Validación Híbrida Inyectada

**Principio:** El step es responsable de entregar un resultado válido. La validación se declara en el propio step (`IStepResult.ValidateAsync` del tipo de resultado, `Validate`/`IStepValidator` del step) y la ejecuta `RetryMiddleware`, que reintenta el mismo step.

```
Pipeline (lineal, forward-only)
   │
   ▼
┌──────────────────────────────────────────┐
│  BaseLlmStep + RetryMiddleware           │
│  ┌────────────────────────────────────┐  │
│  │ LOOP (MaxRetries=3)                │  │
│  │                                    │  │
│  │  Intento 1:                        │  │
│  │  ├─ LLM genera                     │  │
│  │  ├─ Validación estructural ✓       │ │
│  │  ├─ Validación semántica ✗         │ │
│  │  │   Error: "Line 40: missing ;"    │ │
│  │  │                                  │ │
│  │  Intento 2:                         │ │
│  │  ├─ LLM genera (con error previo)   │ │
│  │  ├─ Validación estructural ✓        │ │
│  │  ├─ Validación semántica ✓          │ │
│  │  └─ ÉXITO                           │ │
│  └─────────────────────────────────────┘ │
└──────────────────────────────────────────┘
   │
   ▼
Pipeline continúa con resultado válido
```

### 7.3 Niveles de Validación

**Nivel 1: Estructural (IStepResult.ValidateAsync)**

**Responsable:** El propio objeto DTO  
**Cuándo:** Tras cada intento que no devolvió error  
**Coste:** Bajo (CPU pura)  
**Acción si falla:** `StepValidationEvent` (tipo estructural) y el step reintenta con el error como feedback

**Ejemplos de validaciones estructurales:**
```
• Propiedades required no son null
• Strings tienen formato esperado (email, URL)
• Números están en rangos válidos
• Fechas son coherentes (start < end)
• Arrays/listas no están vacíos
• Enums tienen valores definidos
```

**Nivel 2: Semántica (`IStep.ValidateAsync` / `IStepValidator`)**

**Responsable:** El step (sobrescribiendo `Validate`) o validadores registrados con `AddValidator` (en los pipelines YAML, la función JS `validate` de un esquema la compila `SchemaCompiler` dentro del `ValidateAsync` del tipo de resultado, es decir, en el nivel estructural)  
**Cuándo:** Siempre, tras una validación estructural correcta; el primer validador que falla corta  
**Coste:** Alto (I/O, procesamiento complejo)  
**Acción si falla:** `StepValidationEvent` (tipo semántico), el error se añade a la conversación y el step reintenta

**Ejemplos de validaciones semánticas:**
```
• Código compila sin errores
• Schema JSON es válido según especificación
• Query SQL es sintácticamente correcta
• Integridad referencial (claves foráneas existen)
• Reglas de negocio complejas
• Llamadas a APIs de validación externas
```

### 7.4 Ejemplo Canónico: Generación de Código

**Escenario:** Agente genera código C# que debe compilar sin errores

**Estructura del Result:**
```
CodeGenerationResult
├─ CSharpCode: string (el código generado)
├─ Dependencies: string[] (using statements)
└─ Namespace: string
```

**Validación Estructural (en Result):**
```
Checks:
✓ CSharpCode no es null ni vacío
✓ Contiene al menos "class" o "record"
✓ Llaves están balanceadas { }
✓ No tiene caracteres inválidos (control chars)

Si falla: 
→ Agente reintenta con error de formato
```

**Validación Semántica (IStepValidator del step):**
```
Servicio: ICompilerService

Checks:
✓ Sintaxis de C# es válida
✓ Referencias a tipos existen
✓ No hay errores de compilación
✓ Warnings críticos ausentes

Si falla:
→ Error detallado (línea, columna, mensaje)
→ Se añade al prompt del Agente
→ Agente reintenta con contexto del error
```

**Flujo completo:**
```
Usuario: "Genera una clase User con propiedades Name y Email"
   │
   ▼
TemplateLlmStep (Intento 1)
├─ LLM genera código
├─ Validación estructural ✓
├─ Validación semántica ✗
│   Error: "CS0246: Type 'string' could not be found (missing using System;)"
│
TemplateLlmStep (Intento 2)
├─ LLM genera código (incluye "using System;")
├─ Validación estructural ✓
├─ Validación semántica ✓
└─ ÉXITO → Retorna código compilable

Pipeline continúa con CodeGenerationResult válido
```

### 7.5 Gestión de Feedback al LLM

**Estrategia de prompt de corrección:**

Lo que hace el framework: en cada reintento `BaseLlmStep` añade a la conversación, como mensaje de usuario, el mensaje de error del intento anterior tal cual (o, si no hay mensaje, "Your response is invalid for {tipo}. Please ensure you return valid value"). El texto del error lo escribe quien valida, así que el formato siguiente es una recomendación para redactar esos mensajes, no algo que el framework genere:

```
Prompt en Intento 1:
"Generate a C# class User with properties Name and Email."

Prompt en Intento 2 (con feedback):
"PREVIOUS ATTEMPT FAILED:
The code you generated had compilation errors:
- Line 1, Column 1: CS0246 'string' could not be found
- Suggestion: Add 'using System;' at the top

Please correct the code and regenerate."
```

**Mejores prácticas:**
- Incluir error exacto (línea, columna si disponible)
- Dar sugerencias constructivas
- Mantener contexto del request original
- Limitar tamaño del feedback (max 500 tokens; el framework no lo recorta)

---

## 8. Conversaciones Multi-Turn

### 8.1 Arquitectura de Conversaciones

El framework gestiona el estado conversacional **desacoplado** del estado de ejecución técnica. El system prompt no forma parte de la conversación: lo aporta cada step en su `LlmRequest`.

**Componentes:**

```
ConversationContext (Negocio)
├─ ConversationId: string
├─ History: MessageHistory
│   ├─ Messages: IReadOnlyList<Message>
│   ├─ Bookmarks: Dict<string, int> (no se serializa)
│   └─ MaxTokens: int
├─ Metadata: Dict<string, object?>
├─ StartedAt / LastActivityAt
└─ CreateBookmark(), RestoreBookmark(), GetMessagesForRequest(), Clone()

MessageHistory (Optimización)
├─ AddMessage()
├─ CreateBookmark()
├─ GetMessagesFromBookmark()
├─ GetRecentMessages()
├─ GetMessagesWithSlidingWindow()
└─ ClearAfterBookmark()
```

### 8.2 Persistencia (Esquema Lógico)

**Modelo de datos conceptual** (*no implementado*: es una propuesta para una persistencia propia; el framework no define estas tablas ni tipos de bookmark):

```
CONVERSATIONS
├─ Id: string (PK)
├─ UserId: string
├─ Title: string?
├─ CreatedAt: datetime
├─ UpdatedAt: datetime
└─ IsArchived: bool

MESSAGES
├─ Id: string (PK)
├─ ConversationId: string (FK)
├─ Role: enum (user, assistant, system, tool)
├─ Content: string
├─ Timestamp: datetime
└─ TokenCount: int?

BOOKMARKS
├─ Id: string (PK)
├─ ConversationId: string (FK)
├─ Type: enum (Summary, KeyPoint, Manual)
├─ Content: string
├─ TokenCount: int
├─ CreatedAt: datetime
└─ TurnRange: json {StartId, EndId}
```

**Implementaciones disponibles** (`IConversationStorage`: `SaveConversationAsync`, `GetConversationAsync`):
- `InMemoryConversationStorage` (la que registra `AddAITaskAgent`)
- `FileConversationStorage` (JSON en disco)
- `YamlFileConversationStorage` (YAML en disco)
- Custom (implementar la interfaz para Redis, PostgreSQL, etc.)

El estado de ejecución completo (`PipelineContext`, para suspender/reanudar) se guarda aparte con `IPipelineContextStorage`, cuya única implementación incluida es `InMemoryPipelineContextStorage`.

### 8.3 Optimización de Tokens

**Problema:** Conversaciones largas exceden límites de tokens (ej: 8K, 16K, 128K)

**Estrategias implementadas:**

**1. Sliding Window**
```
Mantiene:
• Primeros N mensajes (contexto inicial)
• Últimos M mensajes (conversación reciente)

Descarta:
• Mensajes del medio

Ahorro: ~30-50% para conversaciones >10 mensajes
```

**2. Bookmarks de Resumen** (*no implementado*)
```
Proceso:
1. Cada 10 mensajes, crear summary bookmark
2. LLM resume esos 10 mensajes en ~100 tokens
3. En requests futuros, usar summary + mensajes recientes

Ahorro: ~60-70% para conversaciones >50 mensajes
```

**3. Limpieza de Reintentos**
```
Proceso:
1. Crear bookmark antes del primer intento LLM
2. Si validación falla, el error se añade como feedback (sin limpiar)
3. Al terminar (FinalizeAsync) se restaura el bookmark y, si hubo
   éxito, solo queda el par [usuario / respuesta final]

Ahorro: ~500-1000 tokens por retry evitado
```

**Configuración (`AITaskAgent:Conversation`, valores por defecto del código):**
```
ConversationOptions
├─ MaxTokens: 8192
├─ UseBookmarks: true
├─ UseSlidingWindow: true
├─ KeepFirstNMessages: 2 (primeros mensajes que siempre se envían)
└─ SlidingWindowMaxTokens: 8192
```
(No existe `SummaryInterval`.)

### 8.4 Gestión de Múltiples Conversaciones

**Por usuario:**
```
Usuario puede tener N conversaciones activas
├─ Conversación A: "Ayuda con código Python"
├─ Conversación B: "Planificación de proyecto"
└─ Conversación C: "Traducción de documentos"

Cada una tiene:
• Historial independiente
• Bookmarks propios
• Context aislado
```

**Best practices** (recomendaciones para la aplicación host; el framework no las implementa):
- Título auto-generado en primer mensaje
- Auto-archivo tras 30 días de inactividad
- Límite por usuario (ej: 50 conversaciones activas)
- Exportación a JSON para backup/análisis

---

## 9. Gestión de Errores y Reintentos

### 9.1 Taxonomía de Errores

El framework distingue cuatro categorías de errores con estrategias diferentes:

| Tipo de Error | Responsable | Estrategia | Ejemplo |
|---------------|-------------|------------|---------|
| **Transitorios (HTTP)** | Servicio LLM (`ILlmService`) y, si persisten, fallback de proveedor del rol | Retry con backoff dentro del servicio; luego `SwitchToNextFallback` | 429 Rate Limit, 503 Service Unavailable |
| **Validación (LLM)** | `RetryMiddleware` + `BaseLlmStep` | Reintento del step con feedback | JSON inválido, formato incorrecto |
| **Semánticos (Negocio)** | `RetryMiddleware` + `IStep.ValidateAsync` | Reintento del step con el error del validador | Código no compila, schema inválido |
| **Lógicos (Programación)** | El step | Resultado de error **con** excepción (no se reintenta) | Saldo insuficiente, archivo no existe |

### 9.2 Políticas de Retry

**RetryPolicy** (`Framework/Resilience/RetryPolicy.cs`). No la aplica el pipeline: la usan los servicios LLM (`GeminiLlmService` la ejecuta; `OpenAILlmService` toma de ella `MaxAttempts`, `InitialDelay` y `MaxDelay`, con 3 / 2 s / 60 s si no se le pasa ninguna). Los reintentos de step de `RetryMiddleware` son inmediatos, sin espera entre intentos.

```
Configuración por defecto:
├─ MaxAttempts: 3
├─ InitialDelay: 1 segundo
├─ MaxDelay: 30 segundos
├─ BackoffMultiplier: 2.0 (exponencial)
├─ UseJitter: true (±15% aleatorio)
└─ ShouldRetry: Func<Exception, bool>
```

**Backoff exponencial con jitter:**
```
Intento 1: Falla → Espera 1s
Intento 2: Falla → Espera ~2s (×0,85-1,15 de jitter)
Intento 3: Falla → Lanza InvalidOperationException (MaxAttempts = 3)

Por defecto solo se reintentan HttpRequestException, TimeoutException
y TaskCanceledException, salvo que se defina ShouldRetry.
```

**Por qué jitter:** Previene "thundering herd" cuando múltiples requests fallan simultáneamente.

### 9.3 Circuit Breaker

**Propósito:** Evitar sobrecargar servicios que están fallando consistentemente.

**Estado en el código:** la clase `CircuitBreaker` (`Framework/Resilience`) existe, pero ni el pipeline ni los servicios LLM incluidos la usan, y `CircuitBreakerOptions` (`AITaskAgent:CircuitBreaker`) se enlaza desde configuración sin que nadie la lea. Para usarla hay que envolver las llamadas a mano con `ExecuteAsync`.

**Estados:**
```
┌─────────────┐
│   CLOSED    │ (Normal - permite requests)
│  ✓ Healthy  │
└──────┬──────┘
       │ Fallos consecutivos > threshold
       ▼
┌─────────────┐
│    OPEN     │ (Cortado - rechaza requests)
│  ✗ Unhealthy│
└──────┬──────┘
       │ Tras timeout de recuperación
       ▼
┌─────────────┐
│  HALF-OPEN  │ (Probando - deja pasar requests)
│  ? Testing  │
└──────┬──────┘
       │
       ├─ Éxito → CLOSED
       └─ Fallo → OPEN (el contador de fallos no se reinicia al pasar a HALF-OPEN)
```

**Configuración:**
```
CircuitBreaker(failureThreshold: 5, openDuration: 1 min)
CircuitBreakerOptions
├─ FailureThreshold: 5 (fallos consecutivos)
└─ OpenDurationSeconds: 60
(No existe HalfOpenMaxAttempts.)
```

**Métricas recomendadas** (el framework no emite métricas del circuit breaker):
- Tasa de fallos por minuto
- Tiempo en estado OPEN (indisponibilidad)
- Ratio de éxito en HALF-OPEN

### 9.4 Rate Limiting

**Propósito:** Controlar la tasa de llamadas a servicios externos (especialmente LLMs).

**Estado en el código:** `RateLimiter` (`Framework/Resilience`) implementa un token bucket (`WaitAsync`), con valores por defecto `maxTokens = 10`, recarga cada 1 s y `tokensPerRefill = maxTokens`. Solo `GeminiLlmService` acepta uno (opcional, por constructor). `RateLimitOptions` (`AITaskAgent:RateLimit`: 100 / 1000 ms / 10) se enlaza pero no se lee. `OpenAILlmService` limita en su lugar la concurrencia por endpoint y despliegue (`MaxConcurrentRequests` en `Inference.Options`, 9 por defecto) y comparte entre llamadas la pausa de un `Retry-After`.

**Algoritmo: Token Bucket**
```
Bucket (capacidad: 10 tokens)
├─ RefillInterval: 1 segundo
├─ TokensPerRefill: 10
└─ Comportamiento:
    • Request consume 1 token
    • Si bucket vacío → Espera hasta refill
    • Tokens no usados se acumulan (hasta max)
```

**Ejemplo de flujo:**
```
T=0s:  Bucket [10/10] → 5 requests simultáneos
T=0s:  Bucket [5/10]  → Request 6 espera
T=1s:  Refill +10     → Bucket [10/10] (capped en el máximo)
T=1s:  Request 6 procede → Bucket [9/10]
```

**Configuración por entorno** (ejemplo; habría que pasarla al construir el `RateLimiter`):
```
Development:
├─ MaxTokens: 100
└─ RefillInterval: 100ms (permite bursts)

Production:
├─ MaxTokens: 10
└─ RefillInterval: 1000ms (controla costes)
```

### 9.5 Timeouts y Cancelación

**Jerarquía de timeouts** (ilustrativa; en el framework existen dos niveles: el del pipeline, `pipelineTimeout` o `DefaultPipelineTimeout`, y el del step, `IStep.Timeout` o `DefaultStepTimeout`, aplicado por `TimeoutMiddleware` y **global a todos los reintentos** del step. Con `AddAITaskAgent` los valores por defecto salen de `AITaskAgent:Timeouts`: 10 min y 1 min; sin DI, `new Pipeline()` usa 60 min y 30 min. Un step que agota su timeout devuelve un resultado de error y emite `StepTimeoutEvent`. No hay timeout por llamada LLM en el framework; el streaming tiene un límite de inactividad propio, `LlmInferenceSettings.StreamingInactivityTimeoutSeconds`, que lee `OpenAILlmService`):

```
┌──────────────────────────────────────┐
│ Request HTTP (ej: 2 minutos)         │ ← Más externo
│  ┌────────────────────────────────┐  │
│  │ Pipeline (ej: 60 segundos)     │  │
│  │  ┌──────────────────────────┐  │  │
│  │  │ BaseLlmStep (ej: 30s)    │  │  │
│  │  │  ┌────────────────────┐  │  │  │
│  │  │  │ LLM Call (ej: 15s) │  │  │  │ ← Más interno
│  │  │  └────────────────────┘  │  │  │
│  │  └──────────────────────────┘  │  │
│  └────────────────────────────────┘  │
└──────────────────────────────────────┘
```

**Propagación de cancelación:**
```
Usuario cancela request
   ↓
HttpContext.RequestAborted se dispara
   ↓
El CancellationToken pasado a IPipeline.ExecuteAsync se cancela
(enlazado con el del timeout del pipeline)
   ↓
Todos los steps reciben señal de cancelación
   ↓
LLM service cancela request HTTP
```

**Best practices:**
- Siempre propagar `CancellationToken`
- No ignorar `OperationCanceledException`
- Cleanup de recursos en finally blocks
- Timeouts progresivamente más cortos hacia dentro

### 9.6 Manejo de Errores por Tipo

**HTTP 429 (Rate Limit):**
```
Estrategia (OpenAILlmService):
1. Leer header "Retry-After"
2. Esperar ese tiempo (o el backoff si no viene), compartiendo la pausa
   con las demás llamadas al mismo endpoint/despliegue
3. Reintentar hasta MaxAttempts (3 por defecto)
4. Si falla de nuevo → LlmProviderException → fallback del rol, si lo hay
```

**HTTP 503 (Service Unavailable):**
```
Estrategia:
1. Reintentar con backoff exponencial (dentro del servicio LLM)
2. Si persiste tras 3 intentos → LlmProviderException → fallback del rol
   (no hay circuit breaker conectado)
3. Registrar métrica de degradación (no la emite el framework)
```

**JSON Parse Error (LLM):**
```
Estrategia:
1. Agregar error a la conversación
2. LLM reintenta generación
3. Máximo MaxRetries intentos (3 por defecto)
4. Si falla → resultado de error del step con el mensaje de parseo
```

**Validation Error (Semántico):**
```
Estrategia:
1. IStep.ValidateAsync / IStepValidator devuelve (false, error)
2. Error se agrega a la conversación del LLM
3. LLM reintenta con contexto
4. Máximo MaxRetries (default: 3)
```

**Business Logic Error:**
```
Estrategia:
1. Fail-fast (no retry): solo si el error lleva OriginalException
   (un error sin excepción se trata como recuperable y se reintenta)
2. Retornar ErrorStepResult descriptivo
3. Pipeline detiene ejecución
4. Usuario recibe explicación clara
```

---

## 10. Herramientas (Capabilities)

### 10.1 Arquitectura de Tools

Las Tools son funciones que los Agentes **pueden** usar, pero no son steps del pipeline. Representan capacidades externas.

**Diseño:**
```
┌────────────────────────────────────────┐
│   TOOL REGISTRY (AvailableTools)       │
│  (Catálogo global thread-safe, ITools) │
├────────────────────────────────────────┤
│                                        │
│  [get_current_time]                    │
│  [search_database]                     │
│  [send_email]                          │
│  [execute_python]                      │
│  [call_api]                            │
│                                        │
└────────────────────────────────────────┘
         ↑                   ↑
         │                   │
    Register()          GetTools(names)
         │                   │
┌────────┴────────┐   ┌──────┴─────────────┐
│ UseAITaskAgent  │   │ TemplateLlmStep    │
│ (ITool del DI)  │   │ (nombres de tools) │
└─────────────────┘   └────────────────────┘
```

**Principio de Least Privilege** (cada step recibe solo las tools que se le indican: nombres en `TemplateLlmStep`, o la lista `Tools` del step o del `LlmRole`):
```
Agente A (Code Generator):
├─ Tools permitidas:
│   ├─ compile_code
│   ├─ run_unit_tests
│   └─ check_style
│
Agente B (Customer Support):
├─ Tools permitidas:
│   ├─ search_knowledge_base
│   ├─ get_order_status
│   └─ create_ticket

Agente B NO puede usar compile_code
Seguridad por diseño
```

### 10.2 Anatomía de una Tool

**Estructura conceptual:**
```
Tool: get_current_time
├─ Name: "get_current_time"
├─ Description: "Gets current date and time"
├─ Parameters Schema:
│   {
│     "type": "object",
│     "properties": {
│       "timezone": {
│         "type": "string",
│         "description": "IANA timezone (e.g., 'America/New_York')"
│       }
│     }
│   }
├─ IsReadOnly / UsageGuidelines (opcionales)
├─ ExecuteAsync(argumentsJson, context, stepName, logger, ct):
│   └─ Returns: "2025-12-16 14:30:00 UTC"
│
└─ GetDefinition() → ToolDefinition (agnóstico del proveedor;
    cada ILlmService lo traduce a su formato)
```

`UsageGuidelines` (o, si no hay, `Description`) de cada tool se añade al system prompt en una sección "TOOL USAGE GUIDELINES".

**Proceso de ejecución:**
```
[LLM solicita tool]
   │
   ▼
{"name": "get_current_time", "arguments": {"timezone": "UTC"}}
   │
   ▼
[Framework busca la tool por nombre en la lista del step]
   │
   ▼
[Ejecuta Tool.ExecuteAsync() con el JSON de argumentos tal cual;
 la tool lo deserializa]
   │
   ▼
[Retorna resultado como string (o el error como texto)]
   │
   ▼
[Agrega resultado como mensaje de rol tool a la conversación]
   │
   ▼
[LLM ve resultado y continúa razonamiento]
```

### 10.3 Tipos de Tools Comunes

Tools incluidas en el repositorio: `FileTools` (listar, buscar, ver, escribir y reemplazar en ficheros), `OfficeTools` (extracción de documentos Office/Access) y `McpIntegration` (tools de servidores MCP). La clasificación siguiente es conceptual.

**1. Information Retrieval**
```
Tools:
├─ search_database(query)
├─ get_user_profile(user_id)
├─ fetch_document(doc_id)
└─ query_knowledge_base(question)

Características:
• Read-only
• Rápidas (< 1 segundo)
• Idempotentes
```

**2. Computation**
```
Tools:
├─ calculate_expression(formula)
├─ compile_code(source_code)
├─ validate_json_schema(schema, data)
└─ run_unit_tests(test_suite)

Características:
• CPU-bound
• Pueden tardar varios segundos
• Deterministas
```

**3. External APIs**
```
Tools:
├─ call_weather_api(location)
├─ translate_text(text, target_lang)
├─ generate_image(prompt)
└─ search_web(query)

Características:
• Network I/O
• Requieren API keys
• Pueden fallar (rate limits)
```

**4. Side Effects**
```
Tools:
├─ send_email(to, subject, body)
├─ create_jira_ticket(title, description)
├─ commit_to_git(message, files)
└─ publish_event(event_data)

Características:
• Modifican estado externo
• NO idempotentes
• Requieren permisos especiales
```

### 10.4 Seguridad de Tools

**Reglas de seguridad recomendadas** (de esta lista el framework solo implementa la 4: `LlmTool` emite `ToolStartedEvent`/`ToolCompletedEvent` con argumentos y resultado, una activity `Tool.{nombre}` y las métricas `aitaskagent.tool.executions` y `aitaskagent.tool.duration`; el resto es responsabilidad de cada tool o del host):

1. **Validación de argumentos:** Antes de ejecutar, validar schema
2. **Timeout por tool:** Cada tool tiene timeout individual
3. **Rate limiting:** Limitar calls por minuto/hora
4. **Audit logging:** Registrar todas las ejecuciones
5. **Permissions:** Tools solo disponibles si usuario tiene rol adecuado

**Ejemplo de tool con validación:**
```
Tool: execute_sql_query

Validaciones:
├─ Query debe empezar con SELECT (read-only)
├─ No puede contener DROP, DELETE, UPDATE
├─ Usuario debe tener role "data_analyst"
├─ Timeout: 30 segundos
└─ Rate limit: 10 queries/minuto

Si falla validación:
→ Retorna error sin ejecutar
→ Log de seguridad registra intento
```

### 10.5 Testing de Tools

**Estrategias recomendadas:**

**Unit Testing (Tools aisladas):**
```
Test: get_current_time_returns_valid_format
├─ Mock del sistema de tiempo
├─ Ejecutar tool con timezone="UTC"
├─ Assert: Formato ISO 8601
└─ Assert: Timezone correcta
```

**Integration Testing (Tools con servicios reales):**
```
Test: search_database_integration
├─ Usar base de datos de test
├─ Insertar datos conocidos
├─ Ejecutar tool con query
├─ Assert: Resultados esperados
└─ Cleanup de datos de test
```

**Mock para desarrollo** (no existe en el framework; basta con registrar en `AvailableTools` implementaciones falsas de `ITool`):
```
MockToolRegistry
├─ Retorna respuestas predefinidas
├─ No hace llamadas reales
├─ Simula latencia realista
└─ Útil para testing de Agentes sin deps externas
```

---

### 10.6 Enriquecimiento y Mensajería en Tools

Para mejorar la experiencia de usuario, las tools pueden emitir mensajes de progreso en tiempo real y enriquecer los eventos estándar.

**Arquitectura Base (`LlmTool`):**
Las tools complejas (como `BaseFileTool` y sus derivadas) deben heredar de `LlmTool` (en lugar de implementar directamente `ITool`): `LlmTool.ExecuteAsync` es un *template method* que crea la activity, emite `ToolStartedEvent`/`ToolCompletedEvent`, registra métricas y llama a `InternalExecuteAsync(argumentsJson, context, logger, ct)`. Ofrece los hooks `EnrichActivityBefore`, `EnrichActivityAfter`, `EnrichStartedEvent` y `EnrichCompletedEvent`.

**Mensajería (`NotifyProgressAsync`):**
Es un helper de `BaseFileTool` (proyecto `FileTools`), no de `LlmTool`. Permite a una tool enviar actualizaciones de estado intermedias visibles para el usuario:
```csharp
// Ejemplo en ListDirTool
await NotifyProgressAsync($"📂 Listing directory '{path}'...", context, cancellationToken);
```
Esto emite un `StepProgressEvent` (con `EventType = "ToolProgress"`) que puede ser renderizado por la UI.

---

## 11. Observabilidad y Control

### 11.1 Sistema de Observabilidad

La observabilidad está **garantizada por diseño** gracias a la cadena de middlewares del pipeline: todo step ejecutado por el pipeline (incluidos los `NextSteps` y los hijos de `ParallelStep`/`LoopStep`) pasa por `ObservabilityMiddleware`.

**Capas de observabilidad:**

```
┌─────────────────────────────────────────┐
│  CAPA 4: MÉTRICAS DE NEGOCIO            │
│  • Intenciones detectadas               │
│  • Tools más usadas                     │
│  • Tasa de conversión (intent→acción)   │
└─────────────────────────────────────────┘
         ↑
┌─────────────────────────────────────────┐
│  CAPA 3: MÉTRICAS COGNITIVAS            │
│  • Tokens usados (llm.tokens)           │
│  • Coste en USD (llm.cost)              │
│  • Reintentos (step.retries)            │
│  • Tool calls ejecutadas                │
└─────────────────────────────────────────┘
         ↑
┌─────────────────────────────────────────┐
│  CAPA 2: MÉTRICAS TÉCNICAS              │
│  • Latencia por step (histograma)       │
│  • Ejecuciones y errores por step       │
│  • (sin métricas de circuit breaker ni  │
│     de rate limiter)                    │
└─────────────────────────────────────────┘
         ↑
┌─────────────────────────────────────────┐
│  CAPA 1: EVENTOS DE PIPELINE            │
│  • Pipeline/Step Started y Completed    │
│    (Success = false si falla)           │
│  • Tool Started/Completed               │
│  • Progress updates                     │
│  • Streaming (LlmResponseEvent)         │
└─────────────────────────────────────────┘
```

### 11.2 Los Tres Pilares de Observabilidad

AITaskAgent implementa los tres pilares estándar de observabilidad moderna, siguiendo las mejores prácticas de la industria:

**Arquitectura de Observabilidad:**

```
┌─────────────────────────────────────────────────────────────┐
│                    APLICACIÓN                                │
│                    (Pipeline Execution)                      │
└─────────────────────────────────────────────────────────────┘
                          │
        ┌─────────────────┼─────────────────┐
        │                 │                 │
        ▼                 ▼                 ▼
┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│   TRACES     │  │   METRICS    │  │     LOGS     │
│  (Spans)     │  │  (Counters)  │  │  (Events)    │
├──────────────┤  ├──────────────┤  ├──────────────┤
│ActivitySource│  │    Meter     │  │  ILogger     │
│ "AITaskAgent"│  │ "AITaskAgent │  │              │
│              │  │  .Pipeline"  │  │              │
└──────────────┘  └──────────────┘  └──────────────┘
        │                 │                 │
        │                 │                 │
        ▼                 ▼                 ▼
┌─────────────────────────────────────────────────────────────┐
│              OBSERVABILITY BACKENDS                          │
│  • Cualquier listener de ActivitySource / Meter              │
│  • OpenTelemetry SDK (configurado por el host)               │
│  • Custom Implementations                                    │
└─────────────────────────────────────────────────────────────┘
```

#### Pilar 1: Traces (Distributed Tracing)

**Propósito:** Rastrear el flujo de ejecución a través de steps, creando una jerarquía de spans que muestra el camino completo de una request.

**Implementación:** `System.Diagnostics.ActivitySource` llamado `"AITaskAgent"` (`Telemetry.Source`). El framework abre estas activities:
```
Pipeline.{nombre}  (Pipeline.ExecuteAsync)
  tags: pipeline.name, correlation.id, pipeline.step_count, pipeline.timeout_ms
  └─ Step.{nombre}  (ObservabilityMiddleware, una por step y no por intento)
       tags: step.name, step.type, correlation.id, step.duration_ms, step.success
       ├─ LLM.Invoke  (BaseLlmStep, una por iteración del bucle de tools)
       │    tags: llm.model, llm.provider, llm.iteration, llm.has_tools,
       │          llm.tokens_used, llm.finish_reason
       └─ Tool.{nombre}  (LlmTool)
            tags: tool.name, step.name, correlation.id, tool.duration_ms, tool.success
```
Los steps y las tools pueden añadir tags con los hooks `EnrichActivityBefore` / `EnrichActivityAfter`.

**Nota:** existe la interfaz `IStepTracer` (`OnTraceEventAsync(StepTraceEvent)`) con `ConsoleStepTracer` y `NullStepTracer`, pero el framework no la invoca en ningún punto. AdvancedLlmPipeline registra `NullStepTracer` en DI y YamlPipelineDemo define un `LoggerStepTracer`, sin efecto sobre la ejecución.

#### Pilar 2: Metrics (Telemetría Cuantitativa)

**Propósito:** Recopilar métricas numéricas sobre la ejecución: duración, tokens, costos, tasas de éxito.

**Implementación:** `System.Diagnostics.Metrics.Meter` llamado `"AITaskAgent.Pipeline"` (clase interna `Metrics`). No existe una interfaz `IStepMetricsCollector`.

**Métricas emitidas:**
```
Counters:
  • aitaskagent.pipeline.executions
  • aitaskagent.step.executions
  • aitaskagent.step.errors
  • aitaskagent.step.retries
  • aitaskagent.llm.requests
  • aitaskagent.llm.tokens   (tokens)
  • aitaskagent.llm.cost     (USD)
  • aitaskagent.tool.executions

Histograms (ms):
  • aitaskagent.pipeline.duration
  • aitaskagent.step.duration
  • aitaskagent.llm.duration
  • aitaskagent.tool.duration
```
No hay gauges (ni pasos activos ni estado de circuit breaker). `ObservabilityOptions.EnableMetrics` y `EnableTracing` se enlazan desde configuración, pero el framework no los lee.

#### Pilar 3: Logs (Eventos Estructurados)

**Propósito:** Registrar eventos discretos con contexto para debugging y auditoría.

**Interfaz Core:**
```
ILogger (Microsoft.Extensions.Logging)
  └─ LogInformation/Warning/Error()
       ├─ Structured Logging (template + parámetros)
       ├─ Scopes para contexto
       └─ Niveles: Trace, Debug, Info, Warning, Error, Critical
```

**Uso en AITaskAgent:**
```
Pipeline Level:
  • Pipeline started/completed
  • Step transitions
  • Error handling

Step Level:
  • LLM requests/responses
  • Tool executions
  • Validation failures
  • Retry attempts

Eventos (IEventChannel):
  • EventChannel registra en el log cada evento que recibe, al nivel
    configurado en ObservabilityOptions.EventLogLevel
```

**Implementaciones:**
- Console Logger (development)
- Serilog (production, structured logging)
- Application Insights, Datadog, etc.

### 11.2.1 Integración con OpenTelemetry

*(El texto original describía un paquete `AITaskAgent.OpenTelemetry` con `OpenTelemetryTracingBridge` y `OpenTelemetryMetricsBridge`. Ese paquete no existe.)*

El core no depende de OpenTelemetry: usa directamente las APIs nativas de .NET (`ActivitySource` y `Meter`), que el SDK de OpenTelemetry sabe escuchar. La aplicación host que quiera exportar trazas y métricas configura su `TracerProvider` / `MeterProvider` suscribiéndose a la fuente `"AITaskAgent"` y al meter `"AITaskAgent.Pipeline"`, y elige los exporters (consola, OTLP hacia Jaeger, Tempo, Prometheus...). El `CorrelationId` viaja como tag `correlation.id`; no se usa como `TraceId`.

**Mapeo de Conceptos:**

| AITaskAgent | OpenTelemetry | Descripción |
|-------------|---------------|-------------|
| Activity `Pipeline.*` / `Step.*` / `LLM.Invoke` / `Tool.*` | Span | Unidad de trabajo rastreable |
| `CorrelationId` | Atributo `correlation.id` | Identificador de la ejecución |
| `pipeline.name`, `step.name`, `step.type` | Span Attributes | Metadatos de la operación |
| Meter `AITaskAgent.Pipeline` | Meter | Métricas cuantitativas |
| `aitaskagent.*.duration` | Histogram | Distribución de tiempos |
| `aitaskagent.llm.tokens` | Counter | Contador acumulativo |

### 11.3 Streaming de Eventos en Tiempo Real (IEventChannel)

**Arquitectura: System.Threading.Channels**

El framework utiliza canales asíncronos de alto rendimiento para desacoplar la emisión de eventos de su procesamiento, garantizando que la observabilidad no impacte la latencia del pipeline.

**Componentes:**

```csharp
// Interfaz Pública
public interface IEventChannel
{
    Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent;

    // Suscriptor con handler: canal propio + bucle en segundo plano
    IDisposable Subscribe(Func<IProgressEvent, CancellationToken, Task> handler,
                          string? correlationId = null);

    // Suscriptor que lee él mismo el canal
    IEventSubscription CreateSubscription(int capacity = 100, string? correlationId = null);
}

// Implementación (Infrastructure)
public sealed class EventChannel : IEventChannel, IAsyncDisposable
{
    // Un Channel<IProgressEvent> acotado POR SUSCRIPTOR (DropOldest).
    // SendAsync registra el evento en el log y lo escribe sin esperar en
    // el canal de cada suscriptor cuyo filtro de correlationId coincida.
}
```

**Flujo de Datos:**

```
[Step / Tool]
    │
    ▼
context.SendEventAsync(new ToolCompletedEvent(...))   (los errores se tragan)
    │
    ▼
EventChannel.SendAsync
 • Log del evento (EventLogLevel)
 • SuppressFromUser → no se reparte
    │
    ▼
[Channel por suscriptor] (acotado; si está lleno descarta el más antiguo y avisa en el log)
    │
    ▼ (Asíncrono, bucle por suscriptor)
[Suscriptores]
 • SSE Endpoint (UI Updates)
 • WebSocket Service
 • Console Logger
```

La capacidad de los suscriptores con handler es `EventChannelOptions.ChannelCapacity` (1000); `ObservabilityOptions.EventChannelCapacity` (100) existe en configuración pero no se usa. `IPipeline.ExecuteAsync` acepta además `subscribers`: si el contexto no se pasa, crea para esa ejecución un `EventChannel` local con esos handlers y lo cierra al terminar.

**Tipos de Eventos (IProgressEvent) que emite el framework:**

1. **Pipeline**: `PipelineStartedEvent`, `PipelineCompletedEvent`, `PipelineSuspendedEvent`.
2. **Step Lifecycle**: `StepStartedEvent` (con `AttemptNumber` en los reintentos), `StepCompletedEvent` (con `Success`; no existe `StepFailedEvent`), `StepValidationEvent`, `StepTimeoutEvent`, `StepRoutingEvent`, `LoopExhaustedEvent`, `BranchStartedEvent` / `BranchCompletedEvent`.
3. **LLM Interaction**: `LlmResponseEvent` (fragmentos en streaming con `FinishReason = Streaming`, razonamiento con `IsThinking`, y respuesta final con tokens, coste, modelo y tool calls). No existe `LlmRequestEvent`.
4. **Tools**: `ToolStartedEvent` y `ToolCompletedEvent` (argumentos, resultado, duración, éxito); `StepProgressEvent` para progreso intermedio.
5. **Artefactos en Streaming**: `TagStartedEvent` (inicio), `TagCompletedEvent` (fin).
    > *Nota: Los artefactos (e.g., escritura de archivos xml) son side-effects generados durante el streaming y no cuentan como turnos de conversación.*

No existe `ContentDeltaEvent`: el efecto máquina de escribir sale de los `LlmResponseEvent` de streaming. `PipelineResumingEvent`, `PipelineResumedEvent` y `StepCachedHitEvent` están definidos pero el framework no los emite.

### 11.4 Proveedores de Implementación

El framework es agnóstico del proveedor LLM:
- **`OpenAILlmService`**: Integración estándar OpenAI (SDK `OpenAI`), con las variantes `AzureOpenAILlmService` y `OpenRouterLlmService`. Reintentos con `Retry-After` y límite de concurrencia por endpoint.
- **`GeminiLlmService`**: Integración nativa Google GenAI SDK. Soporta:
  - **Context Caching**: Optimización de costos.
  - **Google Grounding**: Búsqueda en Google integrada.
  - **Thinking models**: Presupuesto de razonamiento.

**Ventajas del canal de eventos:**

1. **Non-blocking**: La escritura en el canal es inmediata; el procesamiento es background.
2. **Backpressure**: Canales acotados por suscriptor; ante un pico se descartan los eventos más antiguos en lugar de frenar el pipeline.
3. **Desacoplamiento**: El productor (Step) no conoce a los consumidores (UI, Logs).



### 11.5 CorrelationId para Trazabilidad Distribuida

**Propósito:** Identificar y correlacionar todas las operaciones de un flujo de ejecución completo, desde la solicitud inicial hasta la respuesta final, incluyendo todos los steps intermedios, logs y eventos de observabilidad.

**Implementación:**

El framework proporciona `CorrelationId` automáticamente en `PipelineContext`:

```csharp
public sealed record PipelineContext
{
    // Auto-generado si no se proporciona
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString();
    
    // ... otras propiedades
}
```

**Propagación Automática:**

El `CorrelationId` se propaga automáticamente a:
1. **Logging scope** de cada step (Serilog, NLog, etc.)
2. **Eventos** de observabilidad (`IProgressEvent.CorrelationId`)
3. **Activities** (tag `correlation.id`); las métricas no lo llevan como tag
4. **Ramas paralelas**: `CloneForBranch` conserva el mismo `CorrelationId`

**Uso Básico (Auto-generado):**

```csharp
// El CorrelationId se genera automáticamente
var context = PipelineContextFactory.Create(conversation);
// context.CorrelationId = GUID auto-generado

var result = await pipeline.ExecuteAsync("ChatPipeline", steps, input, context);
```

**Uso Avanzado (Propagación desde HTTP):**

```csharp
// En un controlador ASP.NET Core
[HttpPost("/api/chat")]
public async Task<IActionResult> Chat([FromBody] ChatRequest request)
{
    // Leer CorrelationId del header o generar uno nuevo
    var correlationId = HttpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault()
        ?? Guid.NewGuid().ToString();
    
    // Propagar a respuesta
    Response.Headers.Add("X-Correlation-ID", correlationId);
    
    var context = new PipelineContext(_eventChannel, _serviceProvider)
    {
        CorrelationId = correlationId  // Usar el mismo ID
    };
    
    var result = await _pipeline.ExecuteAsync("ChatPipeline", _steps, request.Message, context);
    return Ok(result);
}
```

**Reutilizar ConversationId como CorrelationId:**

```csharp
// Para conversaciones multi-turn, usar ConversationId
var context = new PipelineContext(eventChannel, serviceProvider)
{
    Conversation = conversation,
    CorrelationId = conversation.ConversationId  // Mismo ID para toda la conversación
};
```

**Integración con Serilog:**

```csharp
// El Pipeline abre un scope por step con Path, Step y CorrelationId
// Los logs emitidos durante la ejecución de cada step tendrán el CorrelationId

// Configuración de Serilog
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()  // IMPORTANTE: habilitar LogContext
    .WriteTo.Console(outputTemplate: 
        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
```

**Ejemplo de Logs con CorrelationId:**

```
[19:35:00 INF] Pipeline ChatPipeline starting execution with 2 steps, CorrelationId: a1b2c3d4-e5f6-7890-abcd-ef1234567890
[19:35:01 INF] a1b2c3d4-e5f6-7890-abcd-ef1234567890 LLM step IntentionAnalyzer starting, model: ...
[19:35:02 INF] a1b2c3d4-e5f6-7890-abcd-ef1234567890 Routing to SalesPipeline
[19:35:03 INF] Pipeline ChatPipeline completed with status: Success, duration: 3012ms
```

**Eventos de Observabilidad con CorrelationId:**

```csharp
// Todo IProgressEvent incluye CorrelationId; IEventChannel puede filtrar por él
using var subscription = eventChannel.Subscribe(async (evt, ct) =>
{
    Console.WriteLine($"[{evt.StepName}] {evt.EventType}");
}, correlationId: targetCorrelationId);
```

**Búsqueda en Logs (Elasticsearch/Kibana):**

```json
// Query para obtener todos los logs de una ejecución
{
  "query": {
    "match": {
      "correlationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    }
  },
  "sort": [
    { "timestamp": "asc" }
  ]
}
```

**Ventajas:**

1. **Debugging facilitado**: Filtrar todos los logs de una ejecución específica
2. **Trazabilidad completa**: Seguir el flujo desde request hasta response
3. **Distributed tracing**: Compatible con OpenTelemetry y APM tools
4. **Propagación HTTP**: Puede venir de headers del cliente
5. **Queries eficientes**: Buscar en logs por CorrelationId
6. **Multi-servicio**: Propagar entre microservicios

**Integración con OpenTelemetry:**

No hace falta crear activities a mano: `Pipeline.ExecuteAsync` abre la activity `Pipeline.{nombre}` con el tag `correlation.id` (véase 11.2.1). El `CorrelationId` no sustituye al `TraceId`.

### 11.6 Logging Estructurado

**Estructura recomendada:**

```
Log entry:
├─ Timestamp: ISO 8601
├─ Level: Debug/Info/Warning/Error
├─ CorrelationId: GUID (traza completa)
├─ PipelineName: string
├─ StepName: string
├─ UserId: string
├─ ConversationId: string?
├─ Message: string
└─ Properties: dict
    ├─ Duration: TimeSpan
    ├─ TokensUsed: int?
    ├─ CostUsd: decimal?
    └─ [custom properties]
```

**Scoped logging** (ilustrativo; el framework solo abre el scope por step descrito más abajo):
```
Pipeline inicia
└─ (sin scope de pipeline; el log de inicio incluye el CorrelationId)
    ├─ Step 1 inicia
    │   └─ Scope: {Path, Step: "IntentionAnalyzer", CorrelationId}
    │       ├─ Log: "Analyzing user intention..."
    │       ├─ Log: "Selected option: CreateSchema"
    │       └─ Log: "Step completed in 1.2s"
    │
    ├─ Step 2 inicia
    │   └─ Scope: {Path, Step: "RouterStep", CorrelationId}
    │       └─ ...
    │
    └─ Pipeline completo
        └─ Log: "Pipeline ChatPipeline completed with status: Success, duration: 5300ms"
```

**Logging Independiente por Step (Scope Automático):**

El framework envuelve automáticamente la ejecución de cada `IStep` en un Scope de logging que incluye:
- `Step`: Nombre del paso.
- `Path`: Ruta de ejecución (e.g. `MainPipeline/Router/SalesPipeline`).
- `CorrelationId`: ID único de la traza.

Esto permite filtrar logs de un paso específico incluso si ocurren dentro de servicios inyectados o middlewares.

**Ejemplo de filtro (Serilog):**
```csharp
// Solo ver logs del paso "SchemaValidator"
.Filter.ByIncludingOnly(le => 
    le.Properties.ContainsKey("Step") && 
    le.Properties["Step"].ToString().Contains("SchemaValidator"))
```

**Proveedores compatibles:**
- Serilog (recomendado)
- NLog
- Application Insights
- OpenTelemetry Collector
- ELK Stack (Elasticsearch, Logstash, Kibana)

### 11.7 Métricas y Dashboards

**KPIs recomendados** (algunos no tienen métrica en el framework: circuit breaker, coste por usuario y satisfacción tendría que medirlos el host; véase 11.2 para las métricas reales):

**Rendimiento:**
```
• Latencia P50, P90, P99 por step
• Throughput (requests/segundo)
• Error rate (%)
• Circuit breaker opens/hour
```

**Costes:**
```
• Tokens/request (promedio, P95)
• Cost/request en USD
• Cost/user/día
• Top 10 usuarios por coste
```

**Calidad:**
```
• Reintentos (aitaskagent.step.retries: cuánto se autocorrige el LLM)
• Validation failure rate
• Tool execution success rate
• User satisfaction score
```

**Dashboard conceptual:**
```
┌─────────────────────────────────────────┐
│  AITASKAGENT DASHBOARD                  │
├─────────────────────────────────────────┤
│                                         │
│  HEALTH                              │
│  ├─ Request Rate: 120 req/min           │
│  ├─ Error Rate: 0.3%                    │
│  ├─ Avg Latency: 2.3s (P99: 8.5s)       │
│  └─ Circuit Breaker: CLOSED ✓           │
│                                         │
│  💰 COSTS (Last 24h)                    │
│  ├─ Total Tokens: 2.4M                  │
│  ├─ Total Cost: $42.50                  │
│  ├─ Cost/Request: $0.0035               │
│  └─ Top User: user_xyz ($5.20)          │
│                                         │
│  INTENTS (Top 5)                     │
│  ├─ CreateSchema: 45%                   │
│  ├─ ModifySchema: 30%                   │
│  ├─ QueryKnowledge: 15%                 │
│  ├─ ValidateData: 8%                    │
│  └─ Other: 2%                           │
│                                         │
│   TOOLS (Most Used)                   │
│  ├─ compile_code: 234 calls             │
│  ├─ search_database: 189 calls          │
│  ├─ validate_schema: 156 calls          │
│  └─ send_notification: 45 calls         │
│                                         │
└─────────────────────────────────────────┘
```

### 11.8 Alertas Recomendadas

**Críticas (Pagerduty / SMS):**
```
• Error rate > 5% durante 5 minutos
• Circuit breaker OPEN por >2 minutos
• Latency P99 > 30 segundos
• Coste diario > $200 (threshold configurable)
```

**Warnings (Email / Slack):**
```
• Error rate > 2% durante 15 minutos
• Reintentos (step.retries) > 50% de requests
• Tool failure rate > 10%
• Conversation storage > 90% capacidad
```

**Informativas (Dashboard):**
```
• Nuevo pico de tráfico detectado
• Usuario superó límite de tokens diario
• Nueva intención detectada (no en enum)
```
---
### 11.9 Intercepción de Mensajes (Audit & Security)

**Propósito**: Inspeccionar/modificar el payload crudo antes del envío al LLM (Decorador sobre ILlmService). *No implementado en el framework*: es un patrón que la aplicación puede aplicar registrando su propio `ILlmService` decorador; el framework no incluye interceptores.

**Uso**: PII Masking (ocultar emails), Guardrails de seguridad y cálculo exacto de costes antes de la llamada.

---

## 12. Patrones de Uso Avanzados

### 12.1 Patrón: Intention → Route → Action

**Propósito:** Clasificar intención del usuario y enrutar a pipeline especializado.

**Diagrama de flujo:**
```
Usuario: "Quiero crear un schema JSON para usuarios"
   │
   ▼
┌────────────────────────────────────┐
│ IntentionAnalyzerStep              │
│ • SelectedOption: CreateSchema     │
│ • SelectionReason: "User expl..."  │
│ • DetailedPrompt: "Generate..."    │
└────────────────────────────────────┘
   │
   ▼
┌────────────────────────────────────┐
│ RouterStep                         │
│ • Lee: SelectedOption=CreateSchema │
│ • Ruta: CreateSchemaPipeline       │
└────────────────────────────────────┘
   │
   ▼
┌────────────────────────────────────┐
│ CreateSchemaPipeline (GroupStep)   │
│ ├─ SchemaGeneratorAgent            │
│ ├─ SchemaValidatorGuard            │
│ └─ SaveToDatabaseAction            │
└────────────────────────────────────┘
   │
   ▼
Respuesta: Schema JSON válido guardado
```

**Ventajas:**
- Separación clara de responsabilidades
- Fácil añadir nuevas intenciones
- Testing aislado por intención
- Métricas por intención

### 12.2 Patrón: Validación en Capas

**Propósito:** Validar en múltiples niveles antes de ejecutar acción costosa.

**Diagrama de capas:**
```
Input del usuario
   │
   ▼
┌─────────────────────────────┐
│ Capa 1: Sintaxis            │
│ • JSON parseable            │
│ • Tipos correctos           │
│ • Nulls donde no permitido  │
└─────────────────────────────┘
   │ ✓
   ▼
┌─────────────────────────────┐
│ Capa 2: Semántica           │
│ • Schema válido             │
│ • Referencias resuelven     │
│ • Estructura coherente      │
└─────────────────────────────┘
   │ ✓
   ▼
┌─────────────────────────────┐
│ Capa 3: Negocio             │
│ • Reglas de dominio         │
│ • Límites y restricciones   │
│ • Best practices            │
└─────────────────────────────┘
   │ ✓
   ▼
Acción costosa (guardar, compilar, etc.)
```

**Implementación:**
```
Pipeline de validación compartido (GroupStep):
├─ DelegatedStep: Sintaxis (rápido, CPU)
├─ DelegatedStep: Semántica (medio, I/O)
└─ DelegatedStep: Negocio (lento, lógica compleja)

Reutilizado por:
├─ Chat Pipeline
├─ Batch Pipeline
├─ API Pipeline
└─ Builder UI Pipeline
```

### 12.3 Patrón: RAG Multi-Fuente

**Propósito:** Consultar múltiples fuentes de conocimiento en paralelo y combinar resultados.

**Arquitectura:**
```
Query del usuario
   │
   ▼
┌──────────────────────────────────────┐
│ ParallelStep (4 ramas)               │
├──────────────────────────────────────┤
│                                      │
│  Rama 1: VectorDB Technical          │
│  └─ Top 5 documentos más relevantes  │
│                                      │
│  Rama 2: VectorDB Examples           │
│  └─ Top 5 ejemplos similares         │
│                                      │
│  Rama 3: VectorDB FAQs               │
│  └─ Top 5 preguntas relacionadas     │
│                                      │
│  Rama 4: SQL Historical              │
│  └─ Queries previas del usuario      │
│                                      │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Step siguiente (lee ParallelResult)  │
│ • Rankear por relevancia (score)     │
│ • Deduplicar contenido similar       │
│ • Filtrar top 10 documentos          │
│ • Agregar metadata (fuente, score)   │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ AnswerGeneratorAgent                 │
│ • System prompt con contexto RAG     │
│ • Genera respuesta citando fuentes   │
│ • Incluye links a documentos         │
└──────────────────────────────────────┘
   │
   ▼
Respuesta con fuentes verificadas
```

**Ventajas:**
- Latencia reducida (queries en paralelo)
- Respuestas más completas (múltiples fuentes)
- Trazabilidad (fuente de cada dato)
- Escalable (agregar nuevas fuentes fácilmente)

### 12.4 Patrón: Respuesta Rápida + Procesamiento Asíncrono

**Propósito:** Responder rápido al usuario mientras se procesa tarea larga en background.

**Flujo:**
```
Usuario: "Genera 100 reportes PDF"
   │
   ▼
┌─────────────────────────────┐
│ ValidationGuard             │
│ • Verifica permisos         │
│ • Check límites             │
└─────────────────────────────┘
   │ ✓
   ▼
┌─────────────────────────────┐
│ QuickResponseStep           │
│ • Genera job_id único       │
│ • Guarda en queue           │
│ • Retorna 202 Accepted      │
└─────────────────────────────┘
   │
   ├─────────────────────────────────┐
   │                                 │
   ▼                                 ▼
Usuario recibe:              Background worker:
"Job 123 iniciado"          ├─ Procesa 100 reportes
"Status: /api/jobs/123"     ├─ Actualiza progreso
                            ├─ Guarda resultados
                            └─ Envía notificación

Usuario consulta:
GET /api/jobs/123
└─ {"status": "processing", "progress": "45/100"}
```

**Implementación con Channel:**
```
JobQueue (Channel<JobRequest>)
   │
   ├─ API escribe: job request
   │
   └─ Worker lee: procesa en background
       ├─ Actualiza JobStatus en DB
       ├─ Publica eventos de progreso
       └─ Notifica al completar (email/webhook)
```

### 12.5 Patrón: Pipeline Compartido Reutilizable

**Propósito:** Definir una vez, usar en múltiples contextos.

**Ejemplo: Pipeline de Compilación**

```
CompilationGroup (GroupStep reutilizable)
├─ DelegatedStep: Código no vacío
├─ TemplateLlmStep: Genera código ejecutable
├─ DelegatedStep: Sin errores de compilación
└─ ActionStep: Guarda binario

Usado en:
┌─────────────────────────────┐
│ ChatFlow                    │
│ └─ GroupStep(Compilation)   │
└─────────────────────────────┘

┌─────────────────────────────┐
│ BatchFlow                   │
│ └─ GroupStep(Compilation)   │
└─────────────────────────────┘

┌─────────────────────────────┐
│ CI/CD Integration           │
│ └─ GroupStep(Compilation)   │
└─────────────────────────────┘

Nota: el mismo objeto step no debe aparecer dos veces en un mismo nivel
(los nombres de step tienen que ser únicos en la lista que recibe el pipeline).
```

**Ventajas:**
- DRY (Don't Repeat Yourself)
- Testing centralizado
- Mantenimiento en un solo lugar
- Comportamiento consistente

---
### 12.6 Patrón: Context Scoping

**Propósito:** Entregar a los sub-agentes solo la información necesaria, no todo el historial.

**Solución:** Usar `StatelessTemplateLlmStep` para los sub-agentes: cada ejecución parte de una conversación limpia, ahorrando tokens y reduciendo alucinaciones causadas por ruido anterior. El ```ParallelStep``` clona el ```ConversationContext``` por rama para aislar las escrituras, pero la copia incluye el historial completo (no filtra).

---

## 13. Guías de Implementación

### 13.1 Setup Inicial

**Referencia al framework:** en el repositorio los samples referencian el proyecto `Framework/AITaskAgent.csproj` (y los de proveedor y tools) por `ProjectReference`; no se ha verificado que exista un paquete NuGet publicado.

**Configuración en Startup:**
```
Registrar servicios:
├─ AddSingleton<ILlmService, OpenAILlmService>() / GeminiLlmService ...
│     (uno por proveedor usado en AITaskAgent:LlmProviders)
├─ AddSingleton<ITool, MiTool>()     (tools propias)
├─ AddAITaskAgent()                  (sin parámetros; lee IConfiguration del DI:
│     opciones, LlmEnvironment, IPipeline, IEventChannel, PipelineContextFactory,
│     IPipelineContextStorage, IConversationStorage, ITools, ConversationContext transient)
└─ serviceProvider.UseAITaskAgent()  (crea los singletons y registra
      las ITool del DI en AvailableTools)
```
Los roles se obtienen de `IOptions<LlmEnvironment>` (`Roles[nombre]`).

### 13.2 Creación de un Agente Básico

**Paso 1: Definir capacidades (IntentionOption)**
```
var options = new List<IntentionOption>
{
    new() { Id = "Summarize",   Description = "User wants to summarize a document" },
    new() { Id = "ExtractInfo", Description = "User wants to extract specific information" },
    new() { Id = "Compare",     Description = "User wants to compare multiple documents" }
};
```

**Paso 2: Crear Pipelines de Acción**
```
Para cada intención:
├─ SummarizePipeline
│   ├─ LoadDocumentStep
│   ├─ SummarizerAgent
│   └─ FormatOutputStep
│
├─ ExtractionPipeline
│   ├─ LoadDocumentStep
│   ├─ ExtractorAgent
│   ├─ ValidationGuard
│   └─ FormatOutputStep
│
└─ ComparePipeline
    ├─ LoadMultipleDocumentsStep
    ├─ ParallelStep("AnalyzeDocs", analyzeDoc1, analyzeDoc2)
    │   └─ resultado: ParallelResult { "AnalyzeDoc1": ..., "AnalyzeDoc2": ... }
    ├─ ComparisonAgent (lee el ParallelResult)
    └─ FormatOutputStep
```

**Paso 3: Construir Pipeline Principal**
```
Main Pipeline:
├─ IntentionAnalyzerStep<StepResult<string>>("Intent", role, options)
└─ RouterStep("Router", routes)
    ├─ "Summarize"   → GroupStep(SummarizePipeline)
    ├─ "ExtractInfo" → GroupStep(ExtractionPipeline)
    └─ "Compare"     → GroupStep(ComparePipeline)

await pipeline.ExecuteAsync("Main", [intentStep, routerStep], userText, context);
```


### 13.3 Mejores Prácticas

**Organización del Código:**
```
/YourProject
├─ /Agents
│   ├─ DocumentSummarizerAgent.cs
│   ├─ DataExtractorAgent.cs
│   └─ RouterAgent.cs
│
├─ /Pipelines
│   ├─ /Chat
│   │   └─ ChatPipeline.cs
│   ├─ /Batch
│   │   └─ BatchProcessingPipeline.cs
│   └─ /Shared
│       └─ ValidationPipeline.cs
│
├─ /Steps
│   ├─ /Guards
│   │   ├─ SecurityGuard.cs
│   │   └─ SchemaValidatorGuard.cs
│   └─ /Actions
│       ├─ SaveToDatabaseAction.cs
│       └─ SendNotificationAction.cs
│
├─ /Results
│   ├─ DocumentResult.cs
│   ├─ ExtractionResult.cs
│   └─ ValidationResult.cs
│
├─ /Tools
│   ├─ DatabaseTool.cs
│   ├─ FileTool.cs
│   └─ HttpTool.cs
│
└─ /Configuration
    ├─ AgentConfig.cs
    └─ PipelineConfig.cs
```

**Configuración por Entorno:**
```
Development:
├─ LLM: Modelo rápido y barato (gpt-3.5-turbo)
├─ MaxRetries: 1 (fail-fast para debugging)
├─ Logging: Verbose (Debug level)
├─ Circuit Breaker: Deshabilitado (no está conectado en el framework)
└─ Storage: In-memory

Staging:
├─ LLM: Modelo de producción
├─ MaxRetries: 2
├─ Logging: Info level
├─ Circuit Breaker: Habilitado (threshold bajo; requiere integrarlo a mano)
└─ Storage: SQLite o test DB (implementación propia de IConversationStorage)

Production:
├─ LLM: Modelo optimizado (costo/calidad)
├─ MaxRetries: 3
├─ Logging: Warning level + métricas
├─ Circuit Breaker: Habilitado (threshold alto; requiere integrarlo a mano)
└─ Storage: PostgreSQL/Redis (implementación propia de IConversationStorage)
```

**Manejo de Secrets:**
```
BIEN:
├─ Usar Azure Key Vault / AWS Secrets Manager
├─ Variables de entorno (CI/CD)
├─ User Secrets (desarrollo local)
└─ Nunca en código o git

MAL:
├─ API keys hardcodeadas
├─ Strings en appsettings.json committeado
└─ Secrets en logs o excepciones
```

**Logging Sensible:**
```
Loguear:
├─ Métricas de performance
├─ Errores y excepciones
├─ Decisiones del pipeline (qué intención, qué ruta)
└─ Auditoría de acciones críticas

NO loguear:
├─ API keys o tokens
├─ Datos personales (PII)
├─ Passwords o credenciales
└─ Contenido de mensajes de usuarios (GDPR)
```

### 13.4 Pipelines YAML

*(Sección añadida en la revisión: la capacidad existe en `Framework/YAML` y no estaba documentada.)*

- `SchemaCompiler` compila los JSON Schema de una carpeta (`CompileFromFolderAsync`) a tipos C# en memoria que implementan `IStepResult` (NJsonSchema + Roslyn). Un esquema puede llevar una función JavaScript `validate(self, context)` (ClearScript V8) que pasa a ser la validación estructural del tipo.
- `YamlPipelineFactory.CreateFromFolderAsync` une todos los `*.yaml` de una carpeta, deserializa los steps por su etiqueta (solo se mapean los tipos `IYamlStep` con `[YamlStepTag]`; en el framework, únicamente `!LlmStep` → `YamlLlmStep`. `ParallelStep`, `GroupStep`, `LoopStep` y `BreakStep` llevan `[StepTag]` y comentarios que los dan por serializables desde YAML, pero la fábrica no los registra), resuelve el rol de cada `YamlLlmStep` por `profileName` en `LlmEnvironment`, asigna los tipos compilados de `inputSchema`/`outputSchema`, valida y ordena las dependencias (`dependsOn`) y devuelve un `YamlPipeline` que se ejecuta sobre `IPipeline`.
- El sample `YamlPipelineDemo` (`pipelines/main.yaml`) muestra un pipeline de tres `!LlmStep` (clasificar, extraer, resumir).

### 13.5 Samples del Repositorio

*(Sección añadida en la revisión; descripción tomada de cada README o `Program.cs`, sin verificar su ejecución.)*

| Sample | Qué muestra |
|---|---|
| `SimpleChatDemo` | Chat de consola con proveedores OpenAI/Gemini y una tool sencilla (hora actual) |
| `AdvancedLlmPipeline` | Pipeline de escritor y crítico con bookmarks de conversación y plantillas |
| `GeminiTestApp` | Prueba directa de `GeminiLlmService` |
| `FileToolsTestApp` | Agente de consola con las tools de `FileTools` |
| `McpIntegrationDemo` | Consumo de tools de un servidor MCP a través de `McpIntegration` |
| `PipelineVisualizer` | Aplicación web que retransmite los eventos del `EventChannel` por SSE |
| `YamlPipelineDemo` | Pipeline definido en YAML con esquemas compilados y razonamiento en streaming |
| `TextToTemplateDemo` | Sin LLM real: un step de código produce texto que un `StatelessTemplateLlmStep` incrusta en su plantilla (servicio eco) |
| `CodebaseAnalysis.Core`, `CodebaseAnalysisPipeline`, `CodebaseAnalysisWeb` | Aplicación de análisis de repositorios (núcleo, CLI y web) que audita un repositorio sobre un grafo de conocimiento y genera informes en Markdown; cada proyecto tiene su README |
| `DotNetEnricher` | Enriquecedor Roslyn del grafo para soluciones .NET, lanzado por el análisis |

---

## Conclusión

**AITaskAgent** representa un enfoque maduro y pragmático para construir agentes especializados en entornos empresariales. A través de decisiones arquitectónicas conscientes (ADRs), el framework equilibra control determinista con flexibilidad de LLMs, garantizando observabilidad sin sacrificar performance.

**Principios clave:**

1. **Híbrido Estricto**: Separación clara entre mundo probabilístico (agentes LLM) y determinista (steps de código)
2. **Observabilidad Garantizada**: Todo paso ejecutado por el pipeline deja huella (activity, métricas y eventos)
3. **Validación en Capas**: Estructural vs Semántica, con corrección automática
4. **Type Safety**: Steps genéricos (`TypedStep<TIn,TOut>`, `BaseLlmStep<TIn,TOut>`); la compatibilidad entre steps consecutivos se comprueba en tiempo de ejecución (con conversión de la entrada cuando es posible)
5. **Production First**: Diseñado desde día uno para sistemas críticos empresariales

El framework no intenta ser todo para todos. Es una herramienta especializada para equipos que construyen agentes task-oriented donde el control, la auditabilidad y la predictibilidad son requisitos no negociables.

---

**Versión del Documento**: 4.0 (Consolidada)  
**Última Actualización**: Enero 2026 (revisión contra el código: 2026-10-17)
