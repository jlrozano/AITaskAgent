# AITaskAgent Framework - Especificación Funcional Definitiva

**Versión**: 4.0 (Consolidada)  
**Estado**: DEFINITIVO  
**Fecha**: Enero 2026  
**Documento**: Especificación Maestra Unificada

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
- Equipos que prefieren configuración YAML/JSON sobre código
- Proyectos que priorizan autonomía total del agente sobre control

### 1.4 Principios de Diseño
AITaskAgent se construye sobre cuatro pilares fundamentales que dictan cada decisión de arquitectura:

1. **Determinismo sobre Autonomía.** Rechazamos la idea de que la IA debe controlar el flujo de la aplicación. En este framework, el código C# es el rey y el LLM es un consejero. El pipeline define la estructura rígida; el agente solo rellena el contenido flexible. No hay "bucles mágicos" ni planes emergentes impredecibles.
2. **Type Safety (Seguridad de Tipos) Radical.** Si no compila, no debería ejecutarse. Huimos de los diccionarios de string y los objetos dinámicos. Desde la definición de los Steps hasta la agregación paralela, todo está fuertemente tipado para aprovechar la robustez del compilador de .NET.
3. **Aislamiento Cognitivo.** Los errores de la IA (alucinaciones, sintaxis inválida) deben resolverse dentro del Agente (containment), nunca propagarse al orquestador. El pipeline principal solo ve resultados válidos o fallos fatales, manteniendo la traza de ejecución limpia y lineal.
4. **Observabilidad Inevitable.** La telemetría no es un "plugin" opcional; es parte de la estructura de datos que transporta la ejecución. Hacemos arquitectónicamente imposible ejecutar un paso sin que deje huella, garantizando auditoría total en entornos productivos.

---

## 2. Architecture Decision Log (ADL)

Estas decisiones son los **cimientos inmutables** del framework. Cualquier cambio futuro debe respetar estos principios o proporcionar una justificación arquitectónica sólida.

### ADR-001: Abstracción de Protocolos LLM

**Decisión:** El framework abstrae el protocolo de comunicación con LLMs a través de la interfaz `ILlmService`, permitiendo implementaciones para diferentes proveedores (OpenAI, Google, Anthropic, etc.).

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
├─ OpenAILlmService (implementación OpenAI/Azure OpenAI)
├─ GoogleLlmService (implementación Google AI)
└─ [Custom implementations]
```

**Consecuencias:**
- Código de negocio desacoplado del proveedor LLM
- Cada implementación puede optimizar para su proveedor específico
- Las características únicas de cada proveedor se configuran vía `LlmProviderConfig`

---

### ADR-002: Uso de Reflection

**Decisión:** Usar `System.Reflection` para extracción de parámetros de templates y parsing de resultados, sin cacheo manual.

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
- API limpia sin atributos especiales o código generado
- Debugging directo con breakpoints
-  No apto para hot-paths de microsegundos (pero este no es nuestro caso)

**Alternativas rechazadas:**
- Source Generators: Añaden complejidad de compilación para ganancia de 0.001%
- Cacheo manual: .NET ya lo hace mejor que cualquier implementación custom

---

### ADR-003: Separación de Contextos (Técnico vs Negocio)

**Decisión:** `PipelineContext` (técnico, singleton) es inmutable y separado de `ConversationContext` (negocio, mutable pero cloneable).

**Contexto:**
- En ejecución paralela (`ParallelStep`), múltiples ramas pueden modificar el estado conversacional
- Race conditions en conversaciones compartidas causan corrupción de mensajes
- El contexto técnico (logger, métricas, cancellation) debe ser compartido

**Justificación:**
- **Thread-safety**: El contexto técnico es inmutable (record C#), seguro por diseño.
- **Context Scoping (Foco)**: Al clonar, permitimos filtrar el historial. Los sub-agentes no necesitan recibir toda la conversación "basura" anterior, solo el contexto relevante. Esto ahorra tokens y reduce alucinaciones.
- **Aislamiento**: Cada rama paralela posee su propia instancia, evitando corrupciones de memoria en escrituras concurrentes.

**Implementación:**
```
PipelineContext (record inmutable)
├─ Services (compartido, read-only)
├─ Metrics (compartido, thread-safe)
├─ Logger (compartido, scoped)
└─ Conversation (referencia a objeto mutable)

En ParallelStep:
├─ Contexto técnico → Compartido
└─ Conversation → Clonada por rama
```

**Consecuencias:**
- Seguridad en concurrencia garantizada
- Trazabilidad: Cada rama tiene su propia historia conversacional
-  Requiere clonación explícita en splits paralelos (documentado)

---

### ADR-004: Validación Interna con Feedback Loop

**Decisión:** La corrección de errores semánticos (ej: código que no compila) ocurre **dentro** del Agente mediante un bucle de reintentos con feedback del validador. El Pipeline principal es lineal y no gestiona retrocesos (*forward-only*).

**Contexto:**
- Los LLMs producen salidas probabilísticas que pueden fallar validaciones de negocio
- Un pipeline cíclico (Agente → Validador → Agente) es difícil de mantener y monitorear
- Los reintentos deben mantener contexto del error anterior para corrección efectiva

**Justificación:**
- **Observabilidad**: El pipeline ve solo "Agente completado con éxito o fallo"
- **Encapsulación**: La lógica de corrección está contenida en el Agente
- **Performance**: Los reintentos no atraviesan toda la cadena de observabilidad

**Diagrama del patrón:**
```
Pipeline (lineal, forward-only)
  └─ AgentStep (caja negra con loop interno)
       ├─ Intento 1: LLM genera → ValidaciónA ✓ → ValidaciónB ✗
       ├─ Intento 2: LLM genera con error de B → ValidaciónA ✓ → ValidaciónB ✓
       └─ Retorna resultado válido al Pipeline
```

**Consecuencias:**
- Métricas limpias: "Agente tardó 3 segundos, 2 reintentos internos"
- Debugging: Breakpoints en el loop interno del Agente
-  Los errores de validación NO son visibles en el pipeline (intencional)

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

**Decisión:** Distinción clara entre validación del DTO (`IStepResult.ValidateAsync`) y validación de negocio (delegado inyectado en el Agente).

**Contexto:**
- Las validaciones tienen diferentes niveles de coste y responsabilidad
- Algunos checks son síncronos y baratos (nulls, rangos)
- Otros requieren servicios externos costosos (compiladores, APIs)

**Separación de responsabilidades:**

| Tipo | Responsable | Cuándo se ejecuta | Ejemplos |
|------|-------------|-------------------|----------|
| **Estructural** | `IStepResult.ValidateAsync()` | Siempre, incluso en dry-run | Nulls, tipos, formatos básicos |
| **Semántica** | Delegado en AgentStep | Solo en producción, tras validación estructural | Compilación, queries DB, lógica compleja |

**Ejemplo conceptual:**
```
Resultado: Código C# generado

Validación Estructural (en Result):
✓ La propiedad CSharpCode no es null
✓ Contiene al menos una declaración de tipo
✓ Tiene sintaxis básica de llaves balanceadas

Validación Semántica (delegado):
✓ El código compila sin errores
✓ Pasa análisis estático (no nullrefs)
✓ Las referencias a librerías existen
```

**Consecuencias:**
- Separación clara de concerns
- Performance: Validaciones costosas solo cuando es necesario
- Testabilidad: Validación estructural se testea sin mocks
-  Requiere disciplina del desarrollador para no mezclarlas

---

### ADR-007: Robustez en Parsing JSON (Newtonsoft)
**Decisión**: Estandarizar el uso de Newtonsoft.Json y NJsonSchema para toda la deserialización de respuestas LLM y persistencia de estado.

**Justificación**: System.Text.Json es demasiado estricto para la "creatividad" sintáctica de los LLMs (comas extra, comentarios). Newtonsoft prioriza la tolerancia a fallos sobre la micro-optimización de CPU en este contexto I/O bound.

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
│         AGENT PIPELINE                      │
│  (Secuencia orquestada de pasos)            │
└─────────────────────────────────────────────┘
        │
        ├─ AgentStep (Cognitivo)
        │    ├─ IntentionRouter
        │    ├─ CodingAgent
        │    └─ SummarizerAgent
        │
        ├─ SwitchStep (Bifurcación)
        │    └─ RouteByIntention
        │
        └─ ActionStep (Efectos)
             ├─ CommitToRepo
             └─ SendNotification
```

### 3.2 Glosario de Componentes

#### Componentes Principales

**AgentPipeline**
- **Qué es:** Unidad de ejecución que orquesta una secuencia lineal de steps
- **Responsabilidad:** Controlar flujo, manejar errores, reportar métricas
- **Característica clave:** Forward-only (no retrocede)

**AgentStep (Cognitivo)**
- **Qué es:** Unidad con capacidad de razonamiento LLM
- **Características:**
  - Tiene System Prompt y memoria conversacional
  - Puede usar herramientas (tools)
  - Implementa bucle interno de corrección
  - Salida no garantizada hasta validación

**ActionStep (Determinista)**
- **Qué es:** Ejecutor de efectos colaterales (I/O)
- **Características:**
  - Fire-and-forget o transaccional
  - No modifica flujo de datos principal
  - Típicamente punto final de una rama

**SwitchStep (Determinista)**
- **Qué es:** Bifurcador de flujo basado en valor determinista
- **Uso típico:** Enruta basándose en Enum de un RouterAgent

#### Tipos de Agentes Especializados

**RouterAgentStep**
- **Propósito:** Clasificar intención del usuario
- **Input:** Texto libre del usuario
- **Output:** Enum fuertemente tipado (IntentionResult<T>)
- **Optimización:** Usa Few-Shot Prompting dinámico

**ChatAgentStep**
- **Propósito:** Mantener conversaciones coherentes
- **Características:**
  - Stateful: lee/escribe historial
  - Gestiona bookmarks automáticamente
  - Optimiza tokens con sliding window

**AgentStep (Genérico)**
- **Propósito:** Transformaciones texto-a-texto o texto-a-JSON
- **Características:**
  - Stateless por defecto
  - Puede inyectársele memoria si se necesita

### 3.3 Relaciones entre Componentes

```
1 Aplicación
  └─ N Modos de Interacción (Chat, Batch, Builder UI)
       └─ 1 AgentPipeline principal por modo
            ├─ N Steps (secuencia)
            └─ Puede invocar sub-pipelines (composición)

1 AgentStep
  ├─ 1 System Prompt
  ├─ 0..1 ConversationContext (opcional)
  ├─ 0..N Tools (capabilities)
  └─ 1 Validador semántico (opcional)

1 Pipeline
  ├─ N Steps (ejecución secuencial)
  ├─ 1 PipelineContext (infraestructura)
  └─ 0..N Observers (métricas, SSE)
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

**Modelo C: Inversión de Control con Delegado (AITaskAgent)**
```
Pipeline inyecta delegado en Context
Step decide siguiente → Pide a Pipeline que ejecute
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
│ Pipeline.ExecuteAsync()              │
│ • Crea PipelineContext               │
│ • Inyecta delegado InvokeStep        │
│ • Delegado apunta a método interno   │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Step1.InvokeAsync()                  │
│ • Ejecuta lógica interna             │
│ • Decide siguiente: Step2            │
│ • NO invoca directamente             │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ context.InvokeStep(Step2, result)    │
│ • Llama al delegado                  │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Pipeline.ExecuteStepWithControl()    │
│ • BeforeHook (opcional)              │
│ • Notifica Observer (start)          │
│ • Inicia métricas                    │
│ • Aplica timeout                     │
│ • Ejecuta Step2                      │
│ • Registra métricas                  │
│ • AfterHook (opcional)               │
│ • Notifica Observer (complete)       │
└──────────────────────────────────────┘
   │
   ▼
[Retorna resultado a Step1]
```

**Componentes clave:**

**PipelineContext**
- Estructura de datos que transporta el delegado
- Inmutable (record C#)
- Contiene: Services, Métricas, Logger, Cancellation, Conversation

**Delegado InvokeStep**
- Firma: `Func<IStep, IStepResult, Task<IStepResult>>`
- Inyectado por el Pipeline
- Readonly para prevenir modificaciones

**Step**
- Decide cuál es el siguiente paso
- Pide al Pipeline que lo ejecute
- No tiene lógica de observabilidad

### 4.3 Ventajas del Modelo

| Característica | Detalle |
|----------------|---------|
| **Observabilidad Garantizada** | Todo paso por steps pasa por el pipeline. Imposible saltarse logging/métricas |
| **Flexibilidad Mantenida** | Steps siguen decidiendo el flujo. Routing dinámico funciona perfectamente |
| **Simplicidad Arquitectónica** | No necesita registry de steps con IDs ni grafo declarativo |
| **Control Centralizado** | Timeouts, circuit breakers, dry-run mode en un solo lugar |
| **Testing Mejorado** | Mock del delegado para tests unitarios. Verificación de qué steps se invocaron |

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

---

## 5. Agentes: Entidades Cognitivas

### 5.1 Anatomía de un Agente

Los Agentes son los componentes "inteligentes" que heredan de `AgentStepBase`. A diferencia de los steps deterministas, gestionan incertidumbre y poseen mecanismos de autocorrección.

**Componentes de un Agente:**

```
┌────────────────────────────────────────┐
│         AGENT STEP                     │
├────────────────────────────────────────┤
│                                        │
│  🧠 IDENTIDAD                         │
│  ├─ System Prompt (personalidad)       │
│  ├─ Model (GPT-4, Claude, etc.)        │
│  └─ Temperature (creatividad)          │
│                                        │
│  💾 MEMORIA                           │
│  ├─ ConversationContext (opcional)     │
│  ├─ Message History                    │
│  └─ Bookmarks (optimización)           │
│                                        │
│   CAPACIDADES                       │
│  ├─ Tool Registry (herramientas)       │
│  ├─ Tool Names (permisos)              │
│  └─ Tool Execution (recursiva)         │
│                                        │
│  RESILIENCIA                       │
│  ├─ Max Retries (validación)           │
│  ├─ Feedback Loop (corrección)         │
│  └─ Bookmark Cleanup (tokens)          │
│                                        │
│  MÉTRICAS                          │
│  ├─ Tokens Used                        │
│  ├─ Cost USD                           │
│  └─ Cognitive Retries (autocorr.)      │
│                                        │
└────────────────────────────────────────┘
```

### 5.2 Tipos de Agentes

#### AgentStep (El Estándar)
**Propósito:** Transformaciones generales texto-a-texto o texto-a-JSON

**Características:**
- Stateless respecto a conversación (a menos que se inyecte)
- Útil para tareas puntuales: resúmenes, extracción de entidades, generación de contenido
- Sin memoria persistente entre invocaciones

**Casos de uso:**
- Generar documentación a partir de código
- Extraer datos estructurados de texto libre
- Traducir entre formatos (JSON → YAML)
- Clasificar sentiment o categorías

#### ChatAgentStep (El Conversacional)
**Propósito:** Mantener coherencia en conversaciones multi-turn

**Características:**
- Stateful: Lee y escribe en `ConversationContext.History`
- Gestiona bookmarks automáticamente para optimizar tokens
- Sliding window: Mantiene primeros N mensajes + últimos M

**Casos de uso:**
- Chatbots de soporte al cliente
- Asistentes interactivos de configuración
- Tutores educativos con seguimiento de progreso
- Sistemas de recomendación contextuales

#### RouterAgentStep (El Clasificador)
**Propósito:** Toma de decisiones categóricas

**Características:**
- Input: Texto del usuario
- Output: Enum fuertemente tipado (`IntentionResult<T>`)
- Usa Few-Shot Prompting dinámico basado en `[Description]` del Enum
- Temperature baja (0.3) para decisiones consistentes

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
│ Pipeline invoca AgentStep               │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ Agente crea BOOKMARK en conversación    │
│ (Punto de restauración)                 │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ LOOP: Hasta MaxLlmRetries               │
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
│  │    • Con timeout                │    │
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
│  │    • Delegado inyectado         │    │
│  │    • (ej: compilador, DB)       │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ ¿Todo válido?                   │    │
│  │  SI → SALIR del loop            │    │
│  │  NO → Restaurar bookmark        │    │
│  │       Agregar error al prompt   │    │
│  │       Continuar loop            │    │
│  └─────────────────────────────────┘    │
│                                         │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ Limpiar conversación                    │
│ • Borrar intentos fallidos              │
│ • Mantener solo respuesta válida        │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ Agregar resultado a conversación        │
│ (Si ConversationContext presente)       │
└─────────────────────────────────────────┘
   │
   ▼
[Retornar resultado al Pipeline]
```

**Detalles críticos:**

1. **Bookmark inicial**: Marca el punto de inicio antes de cualquier intento
2. **Loop de corrección**: Los reintentos incluyen el error como feedback al LLM
3. **Limpieza automática**: Los intentos fallidos NO contaminan la conversación final
4. **Métricas cognitivas**: Se cuenta el número de reintentos como "Cognitive Retries"

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
│ 3. Ejecutar TODAS las tools:                │
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
- `MaxToolIterations = 10` (configurable)
- Previene loops infinitos si el LLM siempre pide tools
- Cada tool tiene timeout individual (configurable en `LlmOptions.ToolTimeout`)

### 5.5 Optimización de Tokens con Bookmarks

Los bookmarks son puntos de restauración en la conversación que permiten:

1. **Limpiar intentos fallidos**: No contaminar el contexto con respuestas inválidas
2. **Sliding window inteligente**: Mantener primeros N mensajes + últimos M
3. **Compresión selectiva**: Resumir secciones antiguas de la conversación

**Estrategias de optimización:**

| Estrategia | Cuándo usar | Ahorro de tokens |
|------------|-------------|------------------|
| **Bookmark + Cleanup** | Reintentos de validación | ~500-1000 tokens/retry |
| **Sliding Window** | Conversaciones >10 mensajes | ~30-50% del total |
| **Summary Bookmarks** | Conversaciones >50 mensajes | ~60-70% del total |

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
    object? Value { get; }
    bool IsError { get; }
    StepError? Error { get; }  // ← Información estructurada
}

// Información de error
public sealed record StepError
{
    public required string Message { get; init; }
    public string? StepName { get; init; }
    public Exception? OriginalException { get; init; }
}
```

**Flujo de errores:**
```
Excepción en Step
      │
      ▼
StepBase.catch captura
      │
      ▼
Crea ErrorStepResult.FromException()
      │
      ▼
Pipeline detecta result.IsError
      │
      ▼
Pipeline para el flujo y retorna error al usuario
```

**Beneficios:**
- No hay excepciones escapando del pipeline
- Cualquier resultado tipado puede indicar error via `IsError`
- Información estructurada de error para debugging
- El pipeline para gracefully en errores

**Factory methods en ErrorStepResult:**
```csharp
// Desde excepción capturada
ErrorStepResult.FromException(ex, stepName);

// Desde mensaje simple
ErrorStepResult.FromMessage("Error description", stepName);
```

### 6.2 ParserStep (El Traductor)

**Propósito:** Transformar `StringStepResult` (JSON crudo) en objeto POCO/Record tipado

**Características:**
- Usa `JsonResponseParser` con múltiples estrategias de fallback
- Si falla, devuelve error (no reintenta)
- Típicamente usado después de un Agente bien configurado

**Estrategias de parsing (en orden):**

1. **Direct Parse**: Intentar deserializar JSON directamente
2. **Extract Code Block**: Buscar ```json ... ``` o ``` ... ```
3. **Find JSON in Text**: Regex para encontrar objetos/arrays JSON
4. **Clean and Retry**: Remover basura (markdown, comentarios, etc.)

**Nota:** Si el Agente usa bucle de corrección, el ParserStep raramente debería fallar.

### 6.3 ActionStep (El Ejecutor)

**Propósito:** Ejecutar efectos colaterales (side effects)

**Características:**
- No modifica el `Value` del resultado (o lo pasa transparente)
- Suele ser el punto final de una rama del pipeline
- Puede ser Fire-and-Forget o Transaccional

**Modos de ejecución:**

| Modo | Descripción | Uso típico |
|------|-------------|------------|
| **Fire-and-Forget** | Lanza tarea en background, no espera | Envío de emails, logging asíncrono |
| **Transaccional** | Espera confirmación, puede rollback | Guardar en DB, commits a Git |
| **Idempotente** | Puede ejecutarse múltiples veces | Publicar eventos, crear archivos con overwrite |

**Casos de uso:**
```
┌─────────────────────────────────┐
│ SaveToDatabase                  │
│ • Input: ValidatedEntity        │
│ • Action: db.Save(entity)       │
│ • Output: SavedEntity (con ID)  │
└─────────────────────────────────┘

┌─────────────────────────────────┐
│ PublishEvent                    │
│ • Input: EventData              │
│ • Action: eventBus.Publish()    │
│ • Output: PublishedEvent        │
└─────────────────────────────────┘

┌─────────────────────────────────┐
│ SendEmail                       │
│ • Input: EmailRequest           │
│ • Action: smtp.Send()           │
│ • Output: EmailSent (receipt)   │
└─────────────────────────────────┘
```

### 6.4 SwitchStep (El Enrutador)

**Propósito:** Bifurcar el flujo basándose en un valor determinista (típicamente un Enum)

**Configuración:**
- Diccionario: `Dictionary<TEnum, IStep>`
- Type safety completo en compile-time
- Falla si no hay ruta definida para el valor

**Patrón de uso típico:**

```
RouterAgentStep (clasifica intención)
          ↓
   IntentionResult<Intent>
          ↓
   SwitchStep<Intent>
          ↓
    ┌─────┴─────┬─────────┐
    ▼           ▼         ▼
 CreatePipe  ModifyPipe  QueryPipe
```

**Ejemplo conceptual:**
```
Enum: DocumentIntent
├─ Summarize
├─ ExtractInfo
├─ Compare
└─ GeneralChat

SwitchStep routes:
├─ Summarize   → SummarizePipeline
├─ ExtractInfo → ExtractionPipeline
├─ Compare     → ComparisonPipeline
└─ GeneralChat → ChatPipeline
```

### 6.5 ParallelStep (Ejecución Concurrente)

**Propósito:** Ejecutar múltiples steps en paralelo, aislados cognitivamente y agregando resultados de forma segura.

**Arquitectura:**

- **Patrón Fluent Builder**: Vincula explícitamente el Step con la lógica de mapeo, evitando acoplamiento posicional (índices).
- **Clonación de Contexto (Deep Copy):** Cada rama recibe una copia independiente del ConversationContext para evitar condiciones de carrera en el historial de chat.
- **Merge Sincronizado**: El framework aplica un bloqueo (lock) interno durante la fase de agregación de resultados para permitir el uso seguro de listas y propiedades complejas en el DTO de salida.
- **Mecánica**:
  
1. Se define un DTO de salida (TResult).
2. Se registran ramas (IParallelBranch) que encapsulan el Step y la Acción de Merge.
3. Ejecución paralela (Parallel.ForEachAsync) de los steps.
4. Fusión sincronizada: El resultado se inyecta en el DTO único usando el delegado configurado.

```csharp
// 1. La Interfaz Agnóstica (Lo que ve el ParallelStep)
// No usamos genéricos aquí para poder hacer List<IParallelBranch>
public interface IParallelBranch
{
    IStep Step { get; }
    
    // EL TRUCO: En lugar de exponer la Action, exponemos un método que hace el trabajo sucio
    void MergeResult(object mainDto, object stepOutput);
}

// 2. La Clase Concreta (Lo que usas para construir)
public class ParallelBranch<TMainDto, TStepOutput> : IParallelBranch
{
    private readonly Action<TMainDto, TStepOutput> _mergeAction;

    public IStep Step { get; }

    // Constructor Type-Safe: Aquí obligamos a que los tipos coincidan
    public ParallelBranch(IStep<SomeInput, TStepOutput> step, Action<TMainDto, TStepOutput> mergeAction)
    {
        Step = step;
        _mergeAction = mergeAction;
    }

    // Implementación del puente
    public void MergeResult(object mainDto, object stepOutput)
    {
        // Aquí ocurre la magia del casting seguro encapsulado
        // Si entra algo incorrecto, explota aquí, pero el orquestador no tiene que saber tipos
        _mergeAction((TMainDto)mainDto, (TStepOutput)stepOutput);
    }
}

// 3. El ParallelStep (El Orquestador)
public class ParallelStep<TMainDto> : IStep where TMainDto : new()
{
    private readonly List<IParallelBranch> _branches;

    public ParallelStep(List<IParallelBranch> branches)
    {
        _branches = branches;
    }

    public async Task<IStepResult> ExecuteAsync(...)
    {
        var finalDto = new TMainDto();
        var lockObj = new object();

        await Parallel.ForEachAsync(_branches, async (branch, ct) => 
        {
            // 1. Ejecutar el step (Devuelve IStepResult genérico)
            var result = await branch.Step.ExecuteAsync(context);
            
            // 2. Merge sin saber tipos concretos
            lock(lockObj) 
            {
                // El branch sabe cómo castear sus propias cosas internamente
                branch.MergeResult(finalDto, result.Value);
            }
        });

        return new StepResult(finalDto);
    }
}
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

### 6.6 PipelineStep (Composición)

**Propósito:** Ejecutar un pipeline completo como un step de otro pipeline

**Características:**
- Permite reutilización de pipelines como building blocks
- Type safety: Input/Output del pipeline interno se validan
- Contexto compartido: `PipelineContext` se propaga

**Patrón de reutilización:**

```
Pipeline Compartido: ValidationPipeline
├─ LambdaStep: Sintaxis JSON
├─ LambdaStep: Schema válido
└─ LambdaStep: Business rules

Pipeline A: ChatFlow
├─ AgentStep: Generate
├─ PipelineStep: ValidationPipeline  ← Reutiliza
└─ ActionStep: Save

Pipeline B: BatchFlow
├─ ActionStep: Load from file
├─ PipelineStep: ValidationPipeline  ← Reutiliza
└─ ActionStep: Export results

Pipeline C: APIFlow
├─ ActionStep: Parse HTTP body
├─ PipelineStep: ValidationPipeline  ← Reutiliza
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

**Principio:** El Agente es responsable de entregar un resultado válido. La validación se inyecta **dentro** del Agente como un delegado.

```
Pipeline (lineal, forward-only)
   │
   ▼
┌──────────────────────────────────────────┐
│  AgentStep (caja negra para Pipeline)    │
│  ┌────────────────────────────────────┐  │
│  │ LOOP Interno (MaxLlmRetries=3)     │  │
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
**Cuándo:** Siempre, incluso en dry-run  
**Coste:** Bajo (CPU pura)  
**Acción si falla:** Agente reintenta corrigiendo formato

**Ejemplos de validaciones estructurales:**
```
• Propiedades required no son null
• Strings tienen formato esperado (email, URL)
• Números están en rangos válidos
• Fechas son coherentes (start < end)
• Arrays/listas no están vacíos
• Enums tienen valores definidos
```

**Nivel 2: Semántica (Delegado Inyectado)**

**Responsable:** Servicio externo  
**Cuándo:** Solo en producción, tras validación estructural  
**Coste:** Alto (I/O, procesamiento complejo)  
**Acción si falla:** Error se añade al prompt y Agente reintenta

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

**Validación Semántica (delegado en Agente):**
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
AgentStep (Intento 1)
├─ LLM genera código
├─ Validación estructural ✓
├─ Validación semántica ✗
│   Error: "CS0246: Type 'string' could not be found (missing using System;)"
│
AgentStep (Intento 2)
├─ LLM genera código (incluye "using System;")
├─ Validación estructural ✓
├─ Validación semántica ✓
└─ ÉXITO → Retorna código compilable

Pipeline continúa con CodeGenerationResult válido
```

### 7.5 Gestión de Feedback al LLM

**Estrategia de prompt de corrección:**

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
- Limitar tamaño del feedback (max 500 tokens)

---

## 8. Conversaciones Multi-Turn

### 8.1 Arquitectura de Conversaciones

El framework gestiona el estado conversacional **desacoplado** del estado de ejecución técnica.

**Componentes:**

```
ConversationContext (Negocio)
├─ ConversationId: string
├─ SystemPrompt: string?
├─ MessageHistory
│   ├─ Messages: List<ChatMessage>
│   ├─ Bookmarks: Dict<string, int>
│   └─ MaxTokens: int
├─ Metadata: Dict<string, object?>
└─ Timestamps (created, lastActivity)

MessageHistory (Optimización)
├─ AddMessage()
├─ CreateBookmark()
├─ GetMessagesFromBookmark()
├─ GetRecentMessages()
├─ GetMessagesWithSlidingWindow()
└─ ClearAfterBookmark()
```

### 8.2 Persistencia (Esquema Lógico)

**Modelo de datos conceptual:**

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

**Implementaciones disponibles:**
- SQLite (referencia, producción ligera)
- Memory (testing, demos)
- Custom (interfaz para Redis, PostgreSQL, etc.)

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

**2. Bookmarks de Resumen**
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
1. Crear bookmark antes de intento LLM
2. Si validación falla, ClearAfterBookmark()
3. Solo el intento exitoso queda en historial

Ahorro: ~500-1000 tokens por retry evitado
```

**Configuración recomendada:**
```
ConversationOptions
├─ MaxTokens: 4000 (para modelos de 8K context)
├─ UseBookmarks: true
├─ UseSlidingWindow: true
├─ KeepFirstNMessages: 2 (system + primer user)
└─ SummaryInterval: 10 mensajes
```

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

**Best practices:**
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
| **Transitorios (HTTP)** | Pipeline | Retry automático con backoff | 429 Rate Limit, 503 Service Unavailable |
| **Validación (LLM)** | AgentStep | Loop interno con feedback | JSON inválido, formato incorrecto |
| **Semánticos (Negocio)** | AgentStep | Loop interno con validador | Código no compila, schema inválido |
| **Lógicos (Programación)** | StepBase | Return ErrorStepResult | Saldo insuficiente, archivo no existe |

### 9.2 Políticas de Retry

**RetryPolicy (Configuración Global)**

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
Intento 1: Falla → Espera 1s (±150ms jitter)
Intento 2: Falla → Espera 2s (±300ms jitter)
Intento 3: Falla → Espera 4s (±600ms jitter)
Intento 4: Falla → Lanza excepción
```

**Por qué jitter:** Previene "thundering herd" cuando múltiples requests fallan simultáneamente.

### 9.3 Circuit Breaker

**Propósito:** Evitar sobrecargar servicios que están fallando consistentemente.

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
│  HALF-OPEN  │ (Probando - permite 1 request)
│  ? Testing  │
└──────┬──────┘
       │
       ├─ Éxito → CLOSED
       └─ Fallo → OPEN
```

**Configuración:**
```
CircuitBreakerOptions
├─ FailureThreshold: 5 (fallos consecutivos)
├─ OpenDuration: 60 segundos
└─ HalfOpenMaxAttempts: 3
```

**Métricas recomendadas:**
- Tasa de fallos por minuto
- Tiempo en estado OPEN (indisponibilidad)
- Ratio de éxito en HALF-OPEN

### 9.4 Rate Limiting

**Propósito:** Controlar la tasa de llamadas a servicios externos (especialmente LLMs).

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
T=1s:  Refill +10     → Bucket [15/10] (capped)
T=1s:  Request 6 procede → Bucket [14/10]
```

**Configuración por entorno:**
```
Development:
├─ MaxTokens: 100
└─ RefillInterval: 100ms (permite bursts)

Production:
├─ MaxTokens: 10
└─ RefillInterval: 1000ms (controla costes)
```

### 9.5 Timeouts y Cancelación

**Jerarquía de timeouts:**

```
┌──────────────────────────────────────┐
│ Request HTTP (ej: 2 minutos)         │ ← Más externo
│  ┌────────────────────────────────┐  │
│  │ Pipeline (ej: 60 segundos)     │  │
│  │  ┌──────────────────────────┐  │  │
│  │  │ AgentStep (ej: 30s)      │  │  │
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
PipelineContext.CancellationToken se cancela
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
Estrategia:
1. Leer header "Retry-After"
2. Esperar ese tiempo + jitter
3. Reintentar una vez
4. Si falla de nuevo → Propagar error
```

**HTTP 503 (Service Unavailable):**
```
Estrategia:
1. Reintentar con backoff exponencial
2. Si persiste tras 3 intentos → Abrir circuit
3. Registrar métrica de degradación
```

**JSON Parse Error (LLM):**
```
Estrategia:
1. Agregar error al prompt
2. LLM reintenta generación
3. Máximo 3 intentos
4. Si falla → ErrorStepResult con JSON crudo
```

**Validation Error (Semántico):**
```
Estrategia:
1. Delegado retorna ValidationResult con error
2. Error se agrega al prompt del LLM
3. LLM reintenta con contexto
4. Máximo MaxLlmRetries (default: 3)
```

**Business Logic Error:**
```
Estrategia:
1. Fail-fast (no retry)
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
│        TOOL REGISTRY                   │
│  (Catálogo global thread-safe)         │
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
    Register()          GetTools()
         │                   │
┌────────┴────┐       ┌──────┴─────────┐
│   Startup   │       │  AgentStep     │
│  (DI Setup) │       │  (Runtime)     │
└─────────────┘       └────────────────┘
```

**Principio de Least Privilege:**
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
├─ Execute(argumentsJson):
│   └─ Returns: "2025-12-16 14:30:00 UTC"
│
└─ ChatTool (OpenAI format):
    └─ Generado automáticamente
```

**Proceso de ejecución:**
```
[LLM solicita tool]
   │
   ▼
{"name": "get_current_time", "arguments": {"timezone": "UTC"}}
   │
   ▼
[Framework deserializa argumentos]
   │
   ▼
[Ejecuta Tool.ExecuteAsync()]
   │
   ▼
[Retorna resultado como string]
   │
   ▼
[Agrega resultado como ChatMessage.Tool a conversación]
   │
   ▼
[LLM ve resultado y continúa razonamiento]
```

### 10.3 Tipos de Tools Comunes

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

**Reglas de seguridad implementadas:**

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

**Mock para desarrollo:**
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
Las tools complejas (como `BaseFileTool` y sus derivadas) deben heredar de `LlmTool` (en lugar de implementar directament `ITool`) para acceder a hooks de observabilidad (`EnrichActivity`) y al contexto de ejecución completo (`PipelineContext`, `ILogger`).

**Mensajería (`NotifyProgressAsync`):**
Permite a una tool enviar actualizaciones de estado intermedias visibles para el usuario:
```csharp
// Ejemplo en ListDirTool
await NotifyProgressAsync($"📂 Listing directory '{path}'...", context, cancellationToken);
```
Esto emite un `StepProgressEvent` que puede ser renderizado por la UI.

---

## 11. Observabilidad y Control

### 11.1 Sistema de Observabilidad

La observabilidad está **garantizada por diseño** gracias al modelo de ejecución con delegado. Cada transición entre steps pasa obligatoriamente por el pipeline.

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
│  • Tokens usados por agente             │
│  • Coste en USD                         │
│  • Cognitive Retries (autocorrección)   │
│  • Tool calls ejecutadas                │
└─────────────────────────────────────────┘
         ↑
┌─────────────────────────────────────────┐
│  CAPA 2: MÉTRICAS TÉCNICAS              │
│  • Latencia por step (percentiles)      │
│  • Tasa de éxito/fallo                  │
│  • Circuit breaker state                │
│  • Rate limiter tokens disponibles      │
└─────────────────────────────────────────┘
         ↑
┌─────────────────────────────────────────┐
│  CAPA 1: EVENTOS DE PIPELINE            │
│  • Step Started/Completed/Failed        │
│  • Tool Completed/Failed                │
│  • Progress updates                     │
│  • Streaming tokens                     │
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
│ IStepTracer  │  │ IStepMetrics │  │  ILogger     │
│              │  │  Collector   │  │              │
└──────────────┘  └──────────────┘  └──────────────┘
        │                 │                 │
        │                 │                 │
        ▼                 ▼                 ▼
┌─────────────────────────────────────────────────────────────┐
│              OBSERVABILITY BACKENDS                          │
│  • Console (Development)                                     │
│  • OpenTelemetry (Production)                                │
│  • Custom Implementations                                    │
└─────────────────────────────────────────────────────────────┘
```

#### Pilar 1: Traces (Distributed Tracing)

**Propósito:** Rastrear el flujo de ejecución a través de steps, creando una jerarquía de spans que muestra el camino completo de una request.

**Interfaz Core:**
```
IStepTracer
  └─ OnTraceEventAsync(StepTraceEvent)
       ├─ StepName: Identificador del step
       ├─ Status: Started | InProgress | Completed | Failed
       ├─ PipelineName: Nombre del pipeline
       ├─ StepType: Tipo de step (LlmStep, LambdaStep, etc.)
       ├─ CorrelationId: Trace ID para correlación
       ├─ ParentStepName: Para jerarquías anidadas
       └─ Attributes: Diccionario extensible (model, tokens, etc.)
```

**Eventos de Ciclo de Vida:**
```
Pipeline.ExecuteAsync()
  │
  ├─ Emit: StepTraceEvent { Status = Started }
  │   • Timestamp de inicio
  │   • Contexto del step (pipeline, tipo)
  │
  ├─ [Ejecución del step...]
  │
  └─ Emit: StepTraceEvent { Status = Completed/Failed }
      • Timestamp de fin
      • Duración calculada
      • Atributos finales (tokens, cost)
```

**Implementaciones:**
- `ConsoleStepTracer`: Output a consola para desarrollo
- `OpenTelemetryTracingBridge`: Convierte a OpenTelemetry Activity (spans)
- Custom: Implementar `IStepTracer` para backends propietarios

#### Pilar 2: Metrics (Telemetría Cuantitativa)

**Propósito:** Recopilar métricas numéricas sobre la ejecución: duración, tokens, costos, tasas de éxito.

**Interfaz Core:**
```
IStepMetricsCollector
  └─ RecordStepExecution(StepMetrics)
       ├─ PipelineName: Nombre del pipeline
       ├─ StepName: Nombre del step
       ├─ Duration: Tiempo de ejecución
       ├─ Success: bool (éxito/fallo)
       ├─ TokensUsed: Tokens consumidos (LLM)
       ├─ CostUsd: Costo en dólares
       └─ RetryCount: Número de reintentos
```

**Tipos de Métricas:**
```
Counters (Contadores):
  • step_executions_total
  • step_tokens_total
  • step_retries_total

Histograms (Distribuciones):
  • step_duration_ms (percentiles: p50, p95, p99)
  • step_cost_usd

Gauges (Valores instantáneos):
  • active_steps_count
  • circuit_breaker_state
```

**Implementaciones:**
- `ConsoleStepMetricsCollector`: Output a consola
- `OpenTelemetryMetricsBridge`: Convierte a OpenTelemetry Meter
- Custom: Implementar `IStepMetricsCollector` para Prometheus, StatsD, etc.

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

Infrastructure Level:
  • Circuit breaker state changes
  • Rate limiter throttling
  • Cache hits/misses
```

**Implementaciones:**
- Console Logger (development)
- Serilog (production, structured logging)
- Application Insights, Datadog, etc.

### 11.2.1 Patrón Bridge para OpenTelemetry

**Problema:** El core framework debe ser agnóstico de OpenTelemetry, pero los usuarios deben poder integrarlo fácilmente.

**Solución:** Patrón Bridge - Paquete separado `AITaskAgent.OpenTelemetry` que implementa las interfaces core.

**Arquitectura del Bridge:**

```
┌─────────────────────────────────────────────────────────────┐
│              AITaskAgent.Core (Framework)                    │
│  • IStepTracer (interfaz)                                    │
│  • IStepMetricsCollector (interfaz)                          │
│  • ConsoleStepTracer (implementación default)                │
│  • ConsoleStepMetricsCollector (implementación default)      │
│  • SIN dependencia de OpenTelemetry                          │
└─────────────────────────────────────────────────────────────┘
                          ▲
                          │ implementa
                          │
┌─────────────────────────────────────────────────────────────┐
│         AITaskAgent.OpenTelemetry (Paquete Opcional)         │
│                                                              │
│  ┌────────────────────────────────────────────────────┐     │
│  │  OpenTelemetryTracingBridge : IStepTracer          │     │
│  │  • Convierte StepTraceEvent → Activity (span)      │     │
│  │  • Gestiona jerarquía de spans                     │     │
│  │  • Propaga CorrelationId como TraceId              │     │
│  └────────────────────────────────────────────────────┘     │
│                                                              │
│  ┌────────────────────────────────────────────────────┐     │
│  │  OpenTelemetryMetricsBridge : IStepMetricsCollector│     │
│  │  • Convierte StepMetrics → Meter (metrics)         │     │
│  │  • Histograms para duración                        │     │
│  │  • Counters para tokens, cost, retries             │     │
│  └────────────────────────────────────────────────────┘     │
│                                                              │
│  ┌────────────────────────────────────────────────────┐     │
│  │  ServiceCollectionExtensions                       │     │
│  │  • AddAITaskAgentOpenTelemetry()                   │     │
│  │  • Configura TracerProvider y MeterProvider        │     │
│  │  • Registra bridges en DI                          │     │
│  └────────────────────────────────────────────────────┘     │
│                                                              │
│  Dependencias:                                               │
│  • OpenTelemetry                                             │
│  • OpenTelemetry.Exporter.Console                            │
│  • OpenTelemetry.Exporter.OpenTelemetryProtocol              │
└─────────────────────────────────────────────────────────────┘
```

**Flujo de Datos:**

```
[Pipeline ejecuta step]
        │
        ▼
[Emite StepTraceEvent]
        │
        ├─────────────────────────────────┐
        │                                 │
        ▼                                 ▼
[ConsoleStepTracer]          [OpenTelemetryTracingBridge]
  • Console.WriteLine()        • ActivitySource.StartActivity()
  • Para desarrollo             • SetTag(attributes)
                               • SetStatus(success/error)
                               • Dispose() al completar
                                     │
                                     ▼
                            [OpenTelemetry SDK]
                                     │
                            ┌────────┴────────┐
                            ▼                 ▼
                    [Console Exporter]  [OTLP Exporter]
                    • Development       • Jaeger
                                       • Zipkin
                                       • Tempo
```

**Ventajas del Patrón Bridge:**

| Ventaja | Descripción |
|---------|-------------|
| **Sin Dependencia Core** | Framework no depende de OpenTelemetry, mantiene ligereza |
| **Opt-in** | Usuarios eligen si quieren OpenTelemetry añadiendo el paquete |
| **Flexibilidad** | Permite otros backends (Datadog, New Relic) sin modificar core |
| **Backward Compatible** | Código existente sigue funcionando sin cambios |
| **Testabilidad** | Fácil mockear `IStepTracer` en tests sin OpenTelemetry |
| **Separación de Concerns** | Lógica de negocio separada de infraestructura de observabilidad |

**Mapeo de Conceptos:**

| AITaskAgent | OpenTelemetry | Descripción |
|-------------|---------------|-------------|
| `StepTraceEvent` | `Activity` (Span) | Unidad de trabajo rastreable |
| `CorrelationId` | `TraceId` | Identificador de trace distribuido |
| `PipelineName` | `Service Name` | Nombre del servicio |
| `StepName` | `Span Name` | Nombre de la operación |
| `StepType` | `Span Attribute` | Tipo de operación |
| `Attributes` | `Tags/Attributes` | Metadatos adicionales |
| `StepMetrics` | `Meter` | Métricas cuantitativas |
| `Duration` | `Histogram` | Distribución de tiempos |
| `TokensUsed` | `Counter` | Contador acumulativo |

**Configuración Típica:**

```
Desarrollo:
  • ConsoleStepTracer (traces a consola)
  • ConsoleStepMetricsCollector (métricas a consola)
  • Serilog (logs estructurados)

Producción:
  • OpenTelemetryTracingBridge → Jaeger/Tempo
  • OpenTelemetryMetricsBridge → Prometheus
  • Serilog → Application Insights/Datadog
```

**Semantic Conventions:**

El bridge sigue las convenciones semánticas de OpenTelemetry:

```
Span Attributes:
  • pipeline.name: Nombre del pipeline
  • step.name: Nombre del step
  • step.type: Tipo de step (LlmStep, LambdaStep)
  • step.status: success | failure
  • correlation.id: ID de correlación

Metric Names:
  • aitaskagent.step.duration (histogram, ms)
  • aitaskagent.step.tokens (counter)
  • aitaskagent.step.cost (counter, USD)
  • aitaskagent.step.retries (counter)
  • aitaskagent.step.executions (counter)
```

### 11.3 Streaming de Eventos en Tiempo Real (IEventChannel)

**Arquitectura: System.Threading.Channels**

El framework utiliza canales asíncronos de alto rendimiento para desacoplar la emisión de eventos de su procesamiento, garantizando que la observabilidad no impacte la latencia del pipeline.

**Componentes:**

```csharp
// Interfaz Pública
public interface IEventChannel
{
    Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken)
        where TEvent : IProgressEvent;
}

// Implementación (Infrastructure)
public class EventChannel : IEventChannel
{
    private readonly Channel<IProgressEvent> _channel;
    
    // Background Service procesa el canal y notifica a suscriptores
}
```

**Flujo de Datos:**

```
[AgentStep]
    │
    ▼
SendAsync(new ToolCompletedEvent(...))
    │
    ▼
[Channel<IProgressEvent>] (Buffer acotado)
    │
    ▼ (Asíncrono, Thread separado)
[EventProcessingLoop]
    │
    ▼
[Suscriptores]
 • SSE Endpoint (UI Updates)
 • WebSocket Service
 • Console Logger
```

**Tipos de Eventos (IProgressEvent):**

1. **Step Lifecycle**: `StepStartedEvent`, `StepCompletedEvent`, `StepFailedEvent`.
2. **LLM Interaction**: `LlmRequestEvent`, `LlmResponseEvent`.
3. **Tools**: `ToolCompletedEvent` (detalles de ejecución, duración, resultado).
4. **Artefactos en Streaming**: `TagStartedEvent` (inicio), `TagCompletedEvent` (fin).
    > *Nota: Los artefactos (e.g., escritura de archivos xml) son side-effects generados durante el streaming y no cuentan como turnos de conversación.*
5. **Streaming**: `ContentDeltaEvent` (tokens individuales para efecto máquina de escribir).

**Ventajas:**

1. **Non-blocking**: La escritura en el canal es inmediata; el procesamiento es background.
2. **Backpressure**: Soporte nativo de `System.Threading.Channels` para manejar picos de carga.
3. **Desacoplamiento**: El productor (Step) no conoce a los consumidores (UI, Logs).



### 11.3 CorrelationId para Trazabilidad Distribuida

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
1. **Logging scope** (Serilog, NLog, etc.)
2. **StepProgressEvent** (eventos de observabilidad)
3. **Métricas** (StepMetrics)

**Uso Básico (Auto-generado):**

```csharp
// El CorrelationId se genera automáticamente
var context = new PipelineContext
{
    Services = serviceProvider,
    Conversation = conversation
    // CorrelationId = auto-generado GUID
};

var result = await pipeline.ExecuteAsync(input, context);
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
    
    var context = new PipelineContext
    {
        Services = _serviceProvider,
        CorrelationId = correlationId  // Usar el mismo ID
    };
    
    var result = await _pipeline.ExecuteAsync(input, context);
    return Ok(result);
}
```

**Reutilizar ConversationId como CorrelationId:**

```csharp
// Para conversaciones multi-turn, usar ConversationId
var context = new PipelineContext
{
    Services = serviceProvider,
    Conversation = conversation,
    CorrelationId = conversation.ConversationId  // Mismo ID para toda la conversación
};
```

**Integración con Serilog:**

```csharp
// El Pipeline automáticamente crea un scope con CorrelationId
// Todos los logs dentro del pipeline tendrán el CorrelationId

// Configuración de Serilog
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()  // IMPORTANTE: habilitar LogContext
    .WriteTo.Console(outputTemplate: 
        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
```

**Ejemplo de Logs con CorrelationId:**

```
[19:35:00 INF] a1b2c3d4-e5f6-7890-abcd-ef1234567890 Pipeline ChatPipeline starting execution
[19:35:01 INF] a1b2c3d4-e5f6-7890-abcd-ef1234567890 Step IntentionAnalyzer started
[19:35:02 INF] a1b2c3d4-e5f6-7890-abcd-ef1234567890 Step IntentionAnalyzer completed
[19:35:02 INF] a1b2c3d4-e5f6-7890-abcd-ef1234567890 Step RouterStep started
[19:35:03 INF] a1b2c3d4-e5f6-7890-abcd-ef1234567890 Pipeline ChatPipeline completed
```

**Eventos de Observabilidad con CorrelationId:**

```csharp
// StepProgressEvent automáticamente incluye CorrelationId
var eventBroker = new SecureContentBroker<StepProgressEvent>(logger);

using var subscription = eventBroker.Subscribe(async (evt, ct) =>
{
    // Filtrar por CorrelationId
    if (evt.CorrelationId == targetCorrelationId)
    {
        Console.WriteLine($"[{evt.StepName}] {evt.Status}: {evt.Message}");
    }
});
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

```csharp
// Usar CorrelationId como TraceId
using var activity = new Activity("PipelineExecution");
activity.SetTag("correlation_id", context.CorrelationId);
activity.Start();

var result = await pipeline.ExecuteAsync(input, context);

activity.Stop();
```

### 11.4 Logging Estructurado

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

**Scoped logging:**
```
Pipeline inicia
└─ Scope: {CorrelationId, PipelineName}
    ├─ Step 1 inicia
    │   └─ Scope: {StepName: "IntentionAnalyzer"}
    │       ├─ Log: "Analyzing user intention..."
    │       ├─ Log: "Selected option: CreateSchema"
    │       └─ Log: "Step completed in 1.2s"
    │
    ├─ Step 2 inicia
    │   └─ Scope: {StepName: "RouterStep"}
    │       └─ ...
    │
    └─ Pipeline completo
        └─ Log: "Pipeline completed in 5.3s, 1500 tokens, $0.0045"
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

### 11.4 Métricas y Dashboards

**KPIs recomendados:**

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
• Cognitive Retries (cuánto se autocorrige el LLM)
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

### 11.5 Alertas Recomendadas

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
• Cognitive Retries > 50% de requests
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
### 11.6 Intercepción de Mensajes (Audit & Security)

**Propósito**: Inspeccionar/modificar el payload crudo antes del envío al LLM (Decorador sobre ILlmService).

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
│ • Clasifica: CreateSchema          │
│ • Reasoning: "User explicitly..."  │
│ • OptimizedPrompt: "Generate..."   │
└────────────────────────────────────┘
   │
   ▼
┌────────────────────────────────────┐
│ SwitchStep<SchemaIntent>           │
│ • Lee: Option = CreateSchema       │
│ • Ruta: CreateSchemaPipeline       │
└────────────────────────────────────┘
   │
   ▼
┌────────────────────────────────────┐
│ CreateSchemaPipeline               │
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
Pipeline de validación compartido:
├─ LambdaStep: Sintaxis (rápido, CPU)
├─ LambdaStep: Semántica (medio, I/O)
└─ LambdaStep: Negocio (lento, lógica compleja)

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
│ Merge Function                       │
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
CompilationPipeline (reutilizable)
├─ LambdaStep: Código no vacío
├─ CompilerAgent: Genera código ejecutable
├─ LambdaStep: Sin errores de compilación
└─ ActionStep: Guarda binario

Usado en:
┌─────────────────────────────┐
│ ChatFlow                    │
│ └─ PipelineStep(Compilation)│
└─────────────────────────────┘

┌─────────────────────────────┐
│ BatchFlow                   │
│ └─ PipelineStep(Compilation)│
└─────────────────────────────┘

┌─────────────────────────────┐
│ CI/CD Integration           │
│ └─ PipelineStep(Compilation)│
└─────────────────────────────┘
```

**Ventajas:**
- DRY (Don't Repeat Yourself)
- Testing centralizado
- Mantenimiento en un solo lugar
- Comportamiento consistente

---
### 12.6 Patrón: Context Scoping

**Propósito:** Entregar a los sub-agentes solo la información necesaria, no todo el historial.

**Solución:** El ```ParallelStep``` clona y filtra el ```ConversationContext```. El sub-agente ve un historial limpio, ahorrando tokens y reduciendo alucinaciones causadas por ruido anterior.

---

## 13. Guías de Implementación

### 13.1 Setup Inicial

**Instalación del paquete NuGet:**
```
dotnet add package AITaskAgent
```

**Configuración en Startup:**
```
Registrar servicios:
├─ AddAITaskAgent(configuration)
├─ AddSingleton<IToolRegistry, ToolRegistry>()
├─ AddScoped<ConversationContext>()
├─ AddSingleton<ISSEChannel, SSEChannel>() 
├─ AddSingleton<IOperationLogger>(sp => new ObservableOperationLogger(sp.GetRequiredService<ILogger<ObservableOperationLogger>>(), ...))
└─ Registrar custom tools
```

### 13.2 Creación de un Agente Básico

**Paso 1: Definir capacidades (Enum)**
```
public enum DocumentIntent
{
    [Description("User wants to summarize a document")]
    Summarize,
    
    [Description("User wants to extract specific information")]
    ExtractInfo,
    
    [Description("User wants to compare multiple documents")]
    Compare
}
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
    ├─ ParallelStep<ComparisonResultDto>
    │   ├─ Branch: (dto, doc1) => dto.Doc1Analysis = doc1
    │   └─ Branch: (dto, doc2) => dto.Doc2Analysis = doc2
    ├─ ComparisonAgent
    └─ FormatOutputStep
```

**Paso 3: Construir Pipeline Principal**
```
Main Pipeline:
├─ RouterAgentStep<DocumentIntent>
└─ SwitchStep<DocumentIntent>
    ├─ Summarize   → PipelineStep(SummarizePipeline)
    ├─ ExtractInfo → PipelineStep(ExtractionPipeline)
    └─ Compare     → PipelineStep(ComparePipeline)
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
├─ Circuit Breaker: Deshabilitado
└─ Storage: In-memory

Staging:
├─ LLM: Modelo de producción
├─ MaxRetries: 2
├─ Logging: Info level
├─ Circuit Breaker: Habilitado (threshold bajo)
└─ Storage: SQLite o test DB

Production:
├─ LLM: Modelo optimizado (costo/calidad)
├─ MaxRetries: 3
├─ Logging: Warning level + métricas
├─ Circuit Breaker: Habilitado (threshold alto)
└─ Storage: PostgreSQL/Redis
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

---

## Conclusión

**AITaskAgent** representa un enfoque maduro y pragmático para construir agentes especializados en entornos empresariales. A través de decisiones arquitectónicas conscientes (ADRs), el framework equilibra control determinista con flexibilidad de LLMs, garantizando observabilidad sin sacrificar performance.

**Principios clave:**

1. **Híbrido Estricto**: Separación clara entre mundo probabilístico (agentes LLM) y determinista (steps de código)
2. **Observabilidad Garantizada**: Imposible ejecutar un paso sin que deje huella de auditoría
3. **Validación en Capas**: Estructural vs Semántica, con corrección automática
4. **Type Safety Completo**: Compile-time checking previene errores en tiempo de ejecución
5. **Production First**: Diseñado desde día uno para sistemas críticos empresariales

El framework no intenta ser todo para todos. Es una herramienta especializada para equipos que construyen agentes task-oriented donde el control, la auditabilidad y la predictibilidad son requisitos no negociables.

---

**Versión del Documento**: 4.0 (Consolidada)  
**Última Actualización**: Enero 2026
