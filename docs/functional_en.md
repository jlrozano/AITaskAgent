# AITaskAgent Framework - Definitive Functional Specification

**Version**: 4.0 (Consolidated)  
**Status**: DEFINITIVE  
**Date**: January 2026  
**Document**: Unified Master Specification  
**Review against the code**: 2026-10-17 (branch `consolidation`). Where the original text described components that do not exist in the code, they have been replaced by the actual component or marked as *not implemented*.

> **Name correspondence.** This document started as a conceptual specification and uses several names that do not exist as classes. Equivalents in the current code:
>
> | Conceptual name | Actual component |
> |---|---|
> | `AgentPipeline` | `Pipeline` / `IPipeline` (`Framework/Core/Execution/Pipeline.cs`) |
> | `AgentStep`, `AgentStepBase` | `BaseLlmStep` and `BaseLlmStep<TIn,TOut>` (`Framework/LLM/Steps/BaseLlmStep.cs`); in practice `TemplateLlmStep<TIn,TOut>` |
> | `ChatAgentStep` | Does not exist as a class: `TemplateLlmStep` is *stateful* (it uses `context.Conversation`); `StatelessTemplateLlmStep` uses a clean conversation per execution |
> | `RouterAgentStep` | `IntentionAnalyzerStep<TIn>` (classifies) + `RouterStep` (routes) |
> | `ParserStep` | Does not exist: JSON parsing is done by `BaseLlmStep.ParseLlmResponseAsync` |
> | `PipelineStep` | Does not exist: composition is done with `GroupStep` (or `NextSteps`) |
> | `LambdaStep` | `DelegatedStep<TIn,TOut>` |
> | `MaxLlmRetries` | `StepBase.MaxRetries` (default 3), applied by `RetryMiddleware` |

---

## Table of Contents

1. [Vision and Purpose](#1-vision-and-purpose)
2. [Architecture Decision Log (ADL)](#2-architecture-decision-log-adl)
3. [Fundamental Concepts and Taxonomy](#3-fundamental-concepts-and-taxonomy)
4. [The Execution Model: Inversion of Control](#4-the-execution-model-inversion-of-control)
5. [Agents: Cognitive Entities](#5-agents-cognitive-entities)
6. [Deterministic Steps: The Rigor of Code](#6-deterministic-steps-the-rigor-of-code)
7. [Validation and Correction Patterns](#7-validation-and-correction-patterns)
8. [Multi-Turn Conversations](#8-multi-turn-conversations)
9. [Error Handling and Retries](#9-error-handling-and-retries)
10. [Tools (Capabilities)](#10-tools-capabilities)
11. [Observability and Control](#11-observability-and-control)
12. [Advanced Usage Patterns](#12-advanced-usage-patterns)
13. [Implementation Guides](#13-implementation-guides)


---

## 1. Vision and Purpose

### 1.1 What is AITaskAgent?

**AITaskAgent** is a .NET framework designed to orchestrate **Specialized Agents** and **Deterministic Processes** in enterprise systems where the creativity of Artificial Intelligence must be strictly bounded by business rules, code validations and predictable execution.

**It is not a generic framework for any type of agent.** It is specifically optimized for:

- **Task-oriented enterprise agents** with finite and known capabilities
- Workflows where the **LLM output must be validated** before progressing
- Systems that require **complete auditing** of every decision
- Applications where **cost control** (tokens, LLM calls) is critical
- Production environments that need **predictable behavior**

### 1.2 Design Philosophy: "Strict Hybrid"

The framework enforces a rigid architectural distinction between two worlds:

```
┌─────────────────────────────────────────────────────────────┐
│                     PROBABILISTIC WORLD                     │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              AGENTS (LLM-Powered)                   │    │
│  │  • Controlled creativity                            │    │
│  │  • Conversational memory                            │    │
│  │  • Access to tools                                  │    │
│  │  • Output NOT guaranteed until validation           │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
                            ↓
                   ┌────────────────┐
                   │   VALIDATION   │
                   │    (Bridge)    │
                   └────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│                     DETERMINISTIC WORLD                     │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              STEPS (C# code)                        │    │
│  │  • Compilers and parsers                            │    │
│  │  • Schema validators                                │    │
│  │  • Data transformers                                │    │
│  │  • I/O connectors (DB, APIs)                        │    │
│  │  • Binary execution: success or failure             │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

**Purpose:** To provide a *runtime* where Agents "live" inside a linear pipeline that guarantees observability and error handling, and that **no invalid result progresses in the flow**.

### 1.3 Value Proposition

**Who this framework is for:**

- C# teams building specialized agents (not open-ended generic chatbots)
- Projects where flow control and costs are critical
- Applications that need predictable behavior in production
- Developers who value type safety and debugging with breakpoints
- Systems that require complete auditing (compliance, regulation)

**Who this framework is NOT for:**

- Open conversational chatbots without a predefined structure
- Systems where the LLM must fully decide the flow without restrictions
- Experimental rapid prototyping without production requirements
- Teams that want to define flows only through configuration, without C# (there is a YAML pipeline engine —`YamlPipelineFactory`—, but step types, validators and tools remain code)
- Projects that prioritize full agent autonomy over control

### 1.4 Design Principles
AITaskAgent is built on four fundamental pillars that dictate every architectural decision:

1. **Determinism over Autonomy.** We reject the idea that AI should control the application flow. In this framework, C# code is king and the LLM is an advisor. The pipeline defines the rigid structure; the agent only fills in the flexible content. There are no "magic loops" or unpredictable emergent plans.
2. **Radical Type Safety.** If it doesn't compile, it shouldn't run. We avoid string dictionaries and dynamic objects. From the definition of Steps to parallel aggregation, everything is strongly typed to take advantage of the robustness of the .NET compiler.
3. **Cognitive Isolation.** AI errors (hallucinations, invalid syntax) must be resolved within the Agent (containment), never propagated to the orchestrator. The main pipeline only sees valid results or fatal failures, keeping the execution trace clean and linear.
4. **Inevitable Observability.** Telemetry is not an optional "plugin"; it is part of the data structure that carries the execution. Every step executed through `IPipeline.ExecuteAsync` goes through the `ObservabilityMiddleware`, which is always present (activity, metrics and start/end events). A step invoked directly, outside the pipeline, leaves no such trace.

---

## 2. Architecture Decision Log (ADL)

These decisions are the **immutable foundations** of the framework. Any future change must respect these principles or provide a solid architectural justification.

### ADR-001: LLM Protocol Abstraction

**Decision:** The framework abstracts the communication protocol with LLMs through the `ILlmService` interface, allowing implementations for different providers (in the repository: OpenAI, Azure OpenAI, OpenRouter and Google Gemini).

**Context:** 
- Different LLM providers have different APIs but similar functionality
- Business requirements may change the preferred provider
- The framework must be agnostic of the specific provider

**Justification:**
- **Flexibility**: Switch provider without modifying business code
- **Testability**: Easy to mock `ILlmService` in unit tests
- **Multi-provider**: Use different models for different steps as needed

**Implementation:**
```
ILlmService (abstraction)
├─ OpenAILlmService       (Name "openai",       project OpenAILLmService)
│   ├─ AzureOpenAILlmService (Name "azure-openai")
│   └─ OpenRouterLlmService  (Name "openrouter")
├─ GeminiLlmService       (Name "gemini",       project GeminiLlmService)
├─ NullLlmService         (Framework, fallback when there is no service)
└─ [Custom implementations]
```

**Consequences:**
- Business code decoupled from the LLM provider
- Each implementation can optimize for its specific provider
- Configuration lives in `AITaskAgent:LlmProviders` and is resolved in `LlmEnvironment`: providers (`LlmProvider`: `BaseUrl`, `ApiKey`, `ServiceType`, `Inference`), roles (`LlmRole`: provider, model, system prompt, `Inference`, `Fallbacks`) and `DefaultRole`. Each provider's own options travel in `LlmInferenceSettings.Options`.
- Provider fallback: if a call fails with `LlmProviderException`, `BaseLlmStep.SwitchToNextFallback` moves to the next `LlmFallback` of the role and resets the attempts; any other error does not change the provider.

---

### ADR-002: Use of Reflection

**Decision:** Use `System.Reflection` for extracting template parameters and parsing results. (Review note: the code does cache the activation information of result types in `StepResultFactory`, a `ConcurrentDictionary<Type, StepActivatorInfo>`.)

**Context:**
- The framework is **I/O Bound** - latency is dominated by LLM calls (1000-5000ms)
- Reflection in .NET 7+ with NativeAOT is aggressively optimized by the runtime
- Reflection overhead is ~10-50µs per object vs ~2000ms for the LLM (0.0025% of total time)

**Justification:**
- Development ergonomics: users define normal properties and the framework reads them automatically
- Maintainability: no code generation, manual serialization or special interfaces
- Performance reality: in an I/O-bound system, optimizing CPU is premature optimization

**Empirical measurements:**
```
GetProperties() on an object with 20 properties: ~15µs
Full JSON serialization: ~100µs
Minimum LLM latency: 1,000,000µs (1 second)
→ Reflection is 0.0015% of total time
```

**Consequences:**
- Clean API without special attributes in the programmatic path (YAML registration does use `[StepTag]`/`[YamlStepTag]`, and `SchemaCompiler` generates and compiles C# types with Roslyn from JSON Schema)
- Direct debugging with breakpoints
-  Not suitable for microsecond hot paths (but this is not our case)

**Rejected alternatives:**
- Source Generators: add compilation complexity for a 0.001% gain
- Manual caching: .NET already does it better than any custom implementation

---

### ADR-003: Separation of Contexts (Technical vs Business)

**Decision:** `PipelineContext` (technical, one per execution) is separate from `ConversationContext` (business, mutable but cloneable). `PipelineContext` is a sealed `record`, but it is not immutable: `Metadata` and `StepResults` are shared `ConcurrentDictionary` instances, and the execution path (`CurrentStepPath`) changes during execution.

**Context:**
- In parallel execution (`ParallelStep`), multiple branches may modify the conversational state
- Race conditions on shared conversations cause message corruption
- The technical context (logger, metrics, cancellation) must be shared

**Justification:**
- **Thread-safety**: The shared dictionaries (`Metadata`, `StepResults`) are `ConcurrentDictionary`; the per-invocation state of LLM steps lives in an internal `StepState` that is not shared between branches.
- **Context Scoping (Focus)**: `ConversationContext.Clone()` copies the full history; there is no filtering when cloning. To prevent a sub-agent from inheriting the history, `StatelessTemplateLlmStep` is used, which creates a clean conversation on each execution.
- **Isolation**: Each parallel branch owns its own conversation instance, avoiding memory corruption from concurrent writes.

**Implementation:**
```
PipelineContext (sealed record)
├─ ServiceProvider (not serialized)
├─ EventChannel    (not serialized)
├─ Runtime         (IPipelineRuntime that executes it, not serialized)
├─ CorrelationId
├─ Metadata        (ConcurrentDictionary, shared)
├─ StepResults     (ConcurrentDictionary, shared; result cache per path)
└─ Conversation    (ConversationContext, mutable)

In ParallelStep (PipelineContext.CloneForBranch):
├─ ServiceProvider, EventChannel, Runtime, CorrelationId, Metadata, StepResults → Shared
└─ Conversation → Cloned per branch; path and StepState → own to the branch
```

**Consequences:**
- Concurrency safety guaranteed
- Traceability: each branch has its own conversational history
-  Requires explicit cloning at parallel splits (documented)

---

### ADR-004: Internal Validation with Feedback Loop

**Decision:** Correction of semantic errors (e.g., code that does not compile) happens **within the same step**, through a retry loop with feedback from the validator, without going back to previous steps. The main Pipeline is linear and does not manage backtracking (*forward-only*). The loop is run by `RetryMiddleware` (always present in the pipeline's middleware chain), not by the step itself: it re-executes the step with `attempt` and the previous erroneous result, and `BaseLlmStep` adds the error message to the conversation as feedback.

**Context:**
- LLMs produce probabilistic outputs that may fail business validations
- A cyclic pipeline (Agent → Validator → Agent) is hard to maintain and monitor
- Retries must keep the context of the previous error for effective correction

**Justification:**
- **Observability**: `ObservabilityMiddleware` wraps the set of attempts only once, so the step appears as a single execution (one activity, one duration)
- **Encapsulation**: The correction logic is contained in the step and in `RetryMiddleware`
- **Performance**: Retries do not traverse the entire observability chain

**Pattern diagram:**
```
Pipeline (linear, forward-only)
  └─ Observability → Timeout → RetryMiddleware → BaseLlmStep
       ├─ Attempt 1: LLM generates → Structural validation ✓ → Semantic validation ✗
       ├─ Attempt 2: LLM generates with the error as feedback → ✓ → ✓
       └─ Returns valid result to the Pipeline
```

**Consequences:**
- Metrics: one step execution (`aitaskagent.step.executions`) and N retries (`aitaskagent.step.retries`)
- Debugging: breakpoints in `RetryMiddleware` or in the step
- Validation errors are visible: each failure emits a `StepValidationEvent` and each retry a `StepStartedEvent` with `AttemptNumber`

---

### ADR-005: Mandatory Async

**Decision:** The entire public API is async. Blocking threads with `.Result`, `.Wait()` or excessive locks is prohibited.

**Context:**
- LLM operations are inherently asynchronous (HTTP, streaming)
- Blocking ThreadPool threads causes deadlocks and degrades server performance
- .NET has first-class support for async/await

**Justification:**
- **Scalability**: thousands of concurrent requests without exhausting threads
- **Responsiveness**: the UI does not freeze in desktop/mobile applications
- **Compatibility**: natural integration with ASP.NET Core, Blazor, etc.

**Requirements:**
- Use of `Task.WhenAll` or `Parallel.ForEachAsync` for concurrency
- CancellationToken propagated in all operations
- Avoid `ConfigureAwait(false)` in library code (unnecessary in .NET Core+)

**Consequences:**
- Optimal performance on web servers
- Natural integration with the modern .NET ecosystem
-  Learning curve for developers unfamiliar with async

---

### ADR-006: Hybrid Validation (Structural vs Semantic)

**Decision:** Clear distinction between DTO validation (`IStepResult.ValidateAsync`) and business validation (`IStep.ValidateAsync`, which in `StepBase` iterates over the `IStepValidator` instances registered with `AddValidator` and which a subclass can override in `Validate`).

**Context:**
- Validations have different levels of cost and responsibility
- Some checks are synchronous and cheap (nulls, ranges)
- Others require expensive external services (compilers, APIs)

**Separation of responsibilities:**

| Type | Responsible | When it runs | Examples |
|------|-------------|-------------------|----------|
| **Structural** | `IStepResult.ValidateAsync()` | After each attempt without error (no dry-run mode exists) | Nulls, types, basic formats |
| **Semantic** | `IStep.ValidateAsync()` / `IStepValidator` | Always, after a successful structural validation | Compilation, DB queries, complex logic |

**Conceptual example:**
```
Result: Generated C# code

Structural Validation (in Result):
✓ The CSharpCode property is not null
✓ Contains at least one type declaration
✓ Has basic balanced brace syntax

Semantic Validation (IStepValidator / Validate):
✓ The code compiles without errors
✓ Passes static analysis (no nullrefs)
✓ References to libraries exist
```

**Consequences:**
- Clear separation of concerns
- Performance: the semantic validation (expensive) is not executed if the structural one fails
- Testability: structural validation is tested without mocks
-  Requires developer discipline not to mix them

---

### ADR-007: Robustness in JSON Parsing (Newtonsoft)
**Decision**: Standardize the use of Newtonsoft.Json and NJsonSchema for all deserialization of LLM responses and state persistence.

**Justification**: System.Text.Json is too strict for the syntactic "creativity" of LLMs (extra commas, comments). Newtonsoft prioritizes fault tolerance over CPU micro-optimization in this I/O-bound context. (Exceptions: `YamlFileConversationStorage` persists conversations in YAML with YamlDotNet, and YAML pipelines are also deserialized with YamlDotNet.)

## 3. Fundamental Concepts and Taxonomy

### 3.1 Conceptual Hierarchy

```
┌─────────────────────────────────────────────────────┐
│              APPLICATION / HOST                     │
│  (E.g.: REST API, Blazor App, Console)              │
└─────────────────────────────────────────────────────┘
                      │
        ┌─────────────┴─────────────┐
        ▼                           ▼
┌─────────────────┐         ┌─────────────────┐
│  MODE: Chat     │         │  MODE: Batch    │
│  Interactive    │         │  Processing     │
└─────────────────┘         └─────────────────┘
        │                           │
        ▼                           ▼
┌─────────────────────────────────────────────┐
│         PIPELINE (IPipeline)                │
│  (Orchestrated sequence of steps)           │
└─────────────────────────────────────────────┘
        │
        ├─ LLM Steps (BaseLlmStep)
        │    ├─ IntentionAnalyzerStep
        │    ├─ TemplateLlmStep (CodingAgent)
        │    └─ StatelessTemplateLlmStep (Summarizer)
        │
        ├─ SwitchStep / RouterStep (Branching)
        │    └─ RouteByIntention
        │
        ├─ ParallelStep / GroupStep / LoopStep (Composition)
        │
        └─ ActionStep / DelegatedStep (Code)
             ├─ CommitToRepo
             └─ SendNotification
```

### 3.2 Component Glossary

#### Main Components

**Pipeline (`IPipeline`)**
- **What it is:** Execution unit that orchestrates a linear sequence of steps (`ExecuteAsync(name, steps, input, context, timeout, ct, subscribers)`)
- **Responsibility:** Controls flow, handles errors, reports metrics; composes the user middlewares (`PipelineMiddlewareRegistry`) with the internal ones Observability → Timeout → Retry
- **Key feature:** Forward-only (does not go back). Requires at least one step and unique step names

**BaseLlmStep (Cognitive)**
- **What it is:** Unit with LLM reasoning capability
- **Characteristics:**
  - Has a System Prompt and conversational memory (`context.Conversation` by default)
  - Can use tools and *tags* in the text (`TagHandlers`)
  - Is corrected through `RetryMiddleware` retries, with the error as feedback
  - Output is not guaranteed until validated

**ActionStep (Deterministic)**
- **What it is:** Executor of side effects (I/O): `ActionStep<TIn>(name, action, fireAndForget)`
- **Characteristics:**
  - Waits for the action or, with `fireAndForget: true`, launches it in the background and returns success immediately
  - Returns an `ActionResult` (text message), which becomes the input of the next step: it does not pass the input value through
  - Typically the end point of a branch

**DelegatedStep (Deterministic)**
- **What it is:** Inline transformation `DelegatedStep<TIn,TOut>(name, func)`; an exception becomes an error result

**SwitchStep (Deterministic)**
- **What it is:** Flow brancher: a selector `Func<TIn, PipelineContext, (IStep, IStepResult)>` chooses the next step, which is added to `NextSteps`
- **Typical use:** `RouterStep` (subclass) routes by the option chosen by `IntentionAnalyzerStep`

**ParallelStep, GroupStep, LoopStep, BreakStep (Composition)**
- `ParallelStep`: concurrent branches with a conversation cloned per branch; result `ParallelResult`
- `GroupStep`: sequence of steps in the same context (via `NextSteps`)
- `LoopStep`: repeats its steps until `MaxIterations` (50 by default) or until one returns `BreakStepResult` (`BreakStep`)

#### Specialized Agent Types

**IntentionAnalyzerStep + RouterStep**
- **Purpose:** Classify user intent and route it
- **Input:** Free text from the user (default template `{{ input }}`)
- **Output:** `LlmIntentionResult` with an `Intention` (`SelectedOption`, `SelectionReason`, `DetailedPrompt`, `UserFeedback`, `ExtraInformation`); the option is a `string` that must be one of the configured `IntentionOption`s (otherwise structural validation fails and it is retried)
- **Prompt:** The option catalog (`Id` and `Description`) is added to the system prompt; there are no few-shot examples or enums

**TemplateLlmStep (conversational)**
- **Purpose:** Keep conversations coherent
- **Characteristics:**
  - Stateful: reads/writes `context.Conversation`
  - Manages bookmarks automatically (leaves only the final user/assistant pair)
  - The sliding window is applied by the LLM service when building the request (`AITaskAgent:Conversation`)

**StatelessTemplateLlmStep**
- **Purpose:** Text-to-text or text-to-JSON transformations
- **Characteristics:**
  - Creates a clean conversation on each execution (does not inherit or write the context history)
  - Saves its conversation when suspended so it can be resumed

### 3.3 Relationships between Components

```
1 Application
  └─ N Interaction Modes (Chat, Batch, Builder UI)
       └─ 1 Main pipeline per mode
            ├─ N Steps (sequence)
            └─ Can execute sub-sequences (GroupStep, NextSteps, ParallelStep, LoopStep)

1 BaseLlmStep
  ├─ 1 LlmRole (provider, model, inference, fallbacks)
  ├─ 0..1 System Prompt
  ├─ 1 ConversationContext (the context's own, or a clean one in StatelessTemplateLlmStep)
  ├─ 0..N Tools (capabilities) and 0..N TagHandlers
  └─ 0..N IStepValidator (semantic validation)

1 Pipeline
  ├─ N Steps (sequential execution)
  ├─ 1 PipelineContext (infrastructure)
  └─ 0..N IEventChannel subscribers (UI, SSE, logs)
```

---

## 4. The Execution Model: Inversion of Control

### 4.1 The Problem It Solves

Traditionally, there are two execution models for pipelines:

**Model A: Autonomous Steps**
```
Step1 ──invokes──> Step2 ──invokes──> Step3

Advantage: Full flexibility
Disadvantage: No guaranteed observability
Disadvantage: Complex debugging
Disadvantage: Inconsistent metrics
```

**Model B: Traditional Orchestrator Pipeline**
```
Pipeline knows the full graph
  ├─ Executes Step1
  ├─ Executes Step2
  └─ Executes Step3

Advantage: Guaranteed observability
Disadvantage: Requires declaring the full graph
Disadvantage: Complex dynamic routing
Disadvantage: Architectural overhead
```

**Model C: Inversion of Control with NextSteps (AITaskAgent)**
```
Step decides the next step → returns it in result.NextSteps
Pipeline executes it through its middleware chain
Pipeline wraps it with observability

Flexibility of A
Observability of B
No overhead from a declarative graph
```

### 4.2 Mechanics of the Model

**Execution flow:**

```
[Start]
   │
   ▼
┌──────────────────────────────────────┐
│ Pipeline.ExecuteAsync(name, steps,   │
│                       input, context)│
│ • Creates PipelineContext if none    │
│ • context.Runtime = this pipeline    │
│ • Emits PipelineStartedEvent         │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ For each step (and its tree path)    │
│ • Result in context.StepResults?     │
│   → reused (resume)                  │
│ • If not: middleware chain           │
│   User → Observability → Timeout     │
│   → Retry → step.ExecuteAsync()      │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Step1 returns its result             │
│ • Decides next: Step2                │
│ • Does NOT invoke it: adds it to     │
│   result.NextSteps                   │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Pipeline executes the NextSteps      │
│ • Same middleware chain              │
│ • Nested path (Step1/Step2)          │
│ • Error or suspension → abort, up    │
│ • Success → saved in StepResults     │
└──────────────────────────────────────┘
   │
   ▼
[Continues with the next step in the list; at the end emits PipelineCompletedEvent]
```

**Key components:**

**PipelineContext**
- Shared state of the execution
- `sealed record` with concurrent dictionaries (not immutable)
- Contains: ServiceProvider, EventChannel, Runtime, CorrelationId, Metadata, StepResults, Conversation. The `CancellationToken` is not part of the context: it is passed as a parameter.

**NextSteps and IPipelineRuntime**
- `IStepResult.NextSteps`: list of steps the pipeline will execute next (used by `SwitchStep`, `RouterStep` and `GroupStep`)
- Composite steps (`ParallelStep`, `LoopStep`) run their children with `context.ExecuteStepsWithNextStepsAsync`, which delegates to `context.Runtime` (the running pipeline), so the children also go through the middlewares

**Step**
- Decides which step is next
- Returns it to the Pipeline instead of executing it
- Has no observability logic (it can enrich it with the `EnrichActivityBefore/After` and `EnrichStartedEvent/EnrichCompletedEvent` hooks)

### 4.3 Advantages of the Model

| Feature | Detail |
|---------|--------|
| **Guaranteed Observability** | Every step goes through the pipeline. Logging/metrics cannot be bypassed |
| **Preserved Flexibility** | Steps keep deciding the flow. Dynamic routing works perfectly |
| **Architectural Simplicity** | No declarative graph needed; unique step names per level are enough (the `StepManager` registry is only used for YAML pipelines) |
| **Centralized Control** | Timeouts, retries and observability live in middlewares; custom middlewares can be added with `PipelineMiddlewareRegistry` |
| **Improved Testing** | Results per path are kept in `context.StepResults`, which makes it possible to verify which steps were executed |

### 4.4 Why It Is NOT Choreography

**Differences from Choreography (Microservices)**

| Aspect | Classic Choreography | AITaskAgent |
|--------|---------------------|-------------------|
| **Flow control** | Distributed among the actors | Centralized in the Pipeline |
| **Execution order** | Emergent and unpredictable | Deterministic and defined |
| **Context knowledge** | Each service must know the others | Steps do not know the rest |
| **Coupling** | High (messages and events) | Low (I/O contract only) |
| **Supervision** | Difficult (no central point) | Total (the Pipeline controls everything) |



**Clarifying diagram:**
```
Choreography (Microservice A does not know that B exists)
   ServiceA → EventBus → ServiceB → EventBus → ServiceC
   (Emergent flow, hard to follow)

AITaskAgent (Step1 decides but does not execute)
   Pipeline → Step1 (decides Step2) → Pipeline (executes Step2)
   (Defined flow, centralized control)
```

### 4.5 Suspension and Resumption (Human-in-the-loop)

*(Section added in the review: the mechanism exists in the code and was not documented.)*

- A step suspends by returning a result with `SuspendInfo` (for example `SuspendStepResult`). It must implement `IResume`; `StepBase` implements it with the `SaveStateAsync` / `RestoreStateAsync` hooks. If the step does not implement `IResume`, the pipeline throws `InvalidOperationException`.
- The pipeline saves the step's state in `context.Metadata`, records the result in `context.StepResults`, emits `PipelineSuspendedEvent` and returns the suspended result (the `PipelineCompletedEvent` is emitted with `Success = false` and the message "Suspended awaiting input").
- To resume, call `ExecuteAsync` again with the **same** context (or one recovered with `PipelineContextFactory.CreateFromId` from `IPipelineContextStorage`, in memory by default) and the human response as input. Already completed steps are skipped by reusing `StepResults` (*fast-forward*), and the suspended step receives the response in `RestoreStateAsync`.
- `LoopStep` propagates a child's suspension (or error) without caching its own result, so it can be rehydrated later. `ParallelStep` does not propagate it: the suspended result of a branch stays inside its `ParallelResult` (the branch is emitted as `BranchCompletedEvent` with `Success = false`).

---

## 5. Agents: Cognitive Entities

### 5.1 Anatomy of an Agent

Agents are the "intelligent" components that inherit from `BaseLlmStep`. Unlike deterministic steps, they manage uncertainty and have self-correction mechanisms.

**Components of an Agent:**

```
┌────────────────────────────────────────┐
│         AGENT STEP                     │
├────────────────────────────────────────┤
│                                        │
│  🧠 IDENTITY (LlmRole)                 │
│  ├─ System Prompt (personality)        │
│  ├─ Provider + Model (+ Fallbacks)     │
│  └─ Inference (Temperature, MaxTokens) │
│                                        │
│  💾 MEMORY                             │
│  ├─ ConversationContext (optional)     │
│  ├─ Message History                    │
│  └─ Bookmarks (optimization)           │
│                                        │
│   CAPABILITIES                         │
│  ├─ Tools (AvailableTools)             │
│  ├─ Tool Names (permissions)           │
│  ├─ Tool Execution (recursive)         │
│  └─ TagHandlers (tags in the text)     │
│                                        │
│  RESILIENCE                            │
│  ├─ MaxRetries (RetryMiddleware)       │
│  ├─ Feedback Loop (correction)         │
│  ├─ Provider fallback                  │
│  └─ Bookmark Cleanup (tokens)          │
│                                        │
│  METRICS                               │
│  ├─ TokensUsed                         │
│  ├─ CostUsd                            │
│  └─ aitaskagent.step.retries           │
│                                        │
└────────────────────────────────────────┘
```

### 5.2 Types of Agents

#### StatelessTemplateLlmStep (The Standard)
**Purpose:** General text-to-text or text-to-JSON transformations

**Characteristics:**
- Stateless with respect to conversation: creates a clean `ConversationContext` on each execution
- Useful for one-off tasks: summaries, entity extraction, content generation
- No persistent memory between invocations
- The output type (`TOut : ILlmStepResult`) decides the parsing: text as-is or JSON deserialized to the value's type; if the type has a schema, it is passed to the provider (if the provider declares `JsonSupport`) or injected into the system prompt

**Use cases:**
- Generate documentation from code
- Extract structured data from free text
- Translate between formats (JSON → YAML)
- Classify sentiment or categories

#### TemplateLlmStep (The Conversational)
**Purpose:** Keep coherence in multi-turn conversations

**Characteristics:**
- Stateful: reads and writes `context.Conversation.History`
- Prompts as templates: by default `{name}_prompt.md` and `{name}_system.md` via `ITemplateProvider`, or literal text
- Manages bookmarks automatically to optimize tokens
- Sliding window (applied by the LLM service): keeps the first N messages plus the most recent ones that fit within the budget

**Use cases:**
- Customer support chatbots
- Interactive configuration assistants
- Educational tutors with progress tracking
- Contextual recommendation systems

#### IntentionAnalyzerStep (The Classifier)
**Purpose:** Categorical decision making

**Characteristics:**
- Input: user text
- Output: `LlmIntentionResult` (`Intention.SelectedOption` is a `string`, validated against the list of `IntentionOption` with case-insensitive comparison)
- The option catalog (`Id` + `Description`) is added to the system prompt; there are no few-shot examples or enums
- Temperature is the one from the `LlmRole`; the step does not set any
- If `UserFeedback.NeedMoreInfo` is true, it leaves the feedback in the conversation for the user; the route is decided afterwards by `RouterStep`

**Use cases:**
- Classify user intent (create, modify, query)
- Detect input language
- Select support department
- Determine urgency level

### 5.3 Agent Lifecycle

```
[User sends request]
   │
   ▼
┌─────────────────────────────────────────┐
│ Pipeline invokes BaseLlmStep            │
│ (through RetryMiddleware)               │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ Attempt 1: creates a BOOKMARK in the    │
│ conversation (restore point) and adds   │
│ the user message                        │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ LOOP (RetryMiddleware): MaxRetries = 3  │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 1. Build prompt                 │    │
│  │    • System prompt              │    │
│  │    • Previous conversation      │    │
│  │    • Previous error (if retry)  │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 2. Invoke LLM                   │    │
│  │    • With tools if configured   │    │
│  │    • Step timeout (global to    │    │
│  │      all attempts)              │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 3. Tool calls?                  │    │
│  │    YES → Execute recursively    │    │
│  │    NO → Continue                │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 4. Parse response               │    │
│  │    • JSON → Typed object        │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 5. Structural validation        │    │
│  │    • IStepResult.ValidateAsync()│    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 6. Semantic validation          │    │
│  │    • IStep.ValidateAsync /      │    │
│  │      IStepValidator             │    │
│  │    • (e.g.: compiler, DB)       │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ All valid?                      │    │
│  │  YES → EXIT the loop            │    │
│  │  NO → Add the error as a user   │    │
│  │       message (not restored yet)│    │
│  │       Continue loop             │    │
│  └─────────────────────────────────┘    │
│                                         │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ FinalizeAsync: clean up conversation    │
│ • Restore the initial bookmark          │
│ • Success: write the canonical pair     │
│   [user / assistant]                    │
│ • Error: nothing else is recorded       │
└─────────────────────────────────────────┘
   │
   ▼
[Return result to Pipeline]
```


**Critical details:**

1. **Initial bookmark**: Marks the starting point before any attempt
2. **Correction loop**: Retries include the error as feedback to the LLM. Only errors **without** an exception are retried (unparseable response, failed validation); an error with an exception is not retried and, if it is an `LlmProviderException`, triggers the switch to the role's next fallback provider (with attempts reset to zero and the bookmark restored)
3. **Automatic cleanup**: Failed attempts and tool traffic do NOT remain in the final conversation (this is done by `FinalizeAsync`; whoever invokes the step without `RetryMiddleware` must call it)
4. **Metrics**: Each retry adds to `aitaskagent.step.retries` and emits a `StepStartedEvent` with `AttemptNumber`

### 5.4 Recursive Tool Execution

When an Agent requests to use tools, the framework handles the recursion automatically until the LLM stops requesting tools.

**Recursion diagram:**

```
┌─────────────────────────────────────────────┐
│ InvokeLlmWithToolsAsync(iteration=0)        │
├─────────────────────────────────────────────┤
│ 1. LLM generates a response with tool_calls │
│    [{"name": "get_time"}, {"name": "calc"}] │
│                                             │
│ 2. Add the assistant message with tool_calls│
│    to the conversation                      │
│                                             │
│ 3. Execute ALL tools, in sequence           │
│    (duplicate calls are removed):           │
│    • get_time() → "14:30"                   │
│    • calc(2+2) → "4"                        │
│                                             │
│ 4. Add the results as tool messages         │
│    to the conversation                      │
│                                             │
│ 5. Recursion: iteration=1                   │
│    ┌────────────────────────────────────┐   │
│    │ InvokeLlmWithToolsAsync(iteration=1)│  │
│    │ • LLM sees context + tool results  │   │
│    │ • Generates final response, NO tools│  │
│    │ • Returns response                 │   │
│    └────────────────────────────────────┘   │
│                                             │
│ 6. Return the final response                │
└─────────────────────────────────────────────┘
```

**Safety limits:**
- `MaxToolIterations = 5` (`init` property of `BaseLlmStep`). When exceeded, the step fails with `ToolIterationLimitExceededException`, which does not trigger a provider switch
- Repetition detector: breaks the loop if a read-only tool (`ITool.IsReadOnly`) is repeated with the same arguments in 3 of the last 10 iterations, or if a set of state-modifying calls is repeated identically in consecutive iterations (at the 2nd repetition). An iteration that executed a tag counts as progress. When stopping, the final response carries an explanatory message (`ToolLoopStopMessage`)
- A tool can end the loop by throwing `ToolLoopStopRequestedException`
- An unknown tool, or one that throws an exception, does not break the step: its error goes back to the model as a tool message
- There is no timeout per tool: `TimeoutOptions.DefaultToolTimeout` exists, but the framework does not read it; the effective limit is the step's timeout

### 5.5 Token Optimization with Bookmarks

Bookmarks are restore points in the conversation that allow:

1. **Cleaning up failed attempts**: Not polluting the context with invalid responses
2. **Smart sliding window**: Keeping the first N messages + the last M (implemented by `MessageHistory.GetMessagesWithSlidingWindow`, independent of the bookmarks)
3. **Selective compression**: Summarizing old sections of the conversation (*not implemented*)

**Optimization strategies:**

| Strategy | When to use | Token savings |
|----------|-------------|------------------|
| **Bookmark + Cleanup** | Validation retries | ~500-1000 tokens/retry |
| **Sliding Window** | Conversations >10 messages | ~30-50% of total |
| **Summary Bookmarks** (*not implemented*) | Conversations >50 messages | ~60-70% of total |

The savings figures are design estimates, not measurements.

**Sliding window example:**
```
Original conversation (15 messages, 3000 tokens):
[System, User1, Asst1, User2, Asst2, ..., User15, Asst15]

With sliding window (keepFirstN=2, maxTokens=1500):
[System, User1] + [User13, Asst13, User14, Asst14, User15, Asst15]
                   ↑                                           ↑
           First 2                                 Last 6
           (context)                               (recent)

Savings: 3000 → 1500 tokens (50%)
```

The last group of messages (the last message and, if it is a tool result, the call that requested it) is always sent even if it exceeds the budget, and a tool result is never sent without its call. `UseSlidingWindow` and `SlidingWindowMaxTokens` are read from `AITaskAgent:Conversation` on every request; `KeepFirstNMessages` (2 by default) only applies if a `ConversationOptions` is passed when building the `ConversationContext`, which the factory registered by `AddAITaskAgent` does not do.

---

## 6. Deterministic Steps: The Rigor of Code

Deterministic steps **do NOT use LLMs**. They are pure C# functions that guarantee the pipeline is predictable and safe.

### 6.1 Error Handling with StepError

**Architecture:** The framework does NOT use exceptions to communicate errors in the normal flow. Instead, each `IStepResult` can contain structured error information.

**Components:**

```csharp
// Interface (every result implements)
public interface IStepResult
{
    bool HasError => Error != null;
    IStepError? Error { get; set; }   // ← Structured information
    object? SuspendInfo { get; set; } // ← Suspension (human-in-the-loop)
    string StepName { get; }
    string StepType { get; }
    object? Value { get; }
    List<IStep> NextSteps { get; }    // ← Steps to execute next
    Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context); // structural validation
}

// Error information
public sealed record StepError : IStepError
{
    public required string Message { get; init; }
    public Exception? OriginalException { get; init; }
}
```

**Error flow:**
```
Exception in Step
      │
      ▼
RetryMiddleware catches it (no retry)
      │
      ▼
Creates ErrorStepResult.FromException(step, ex)
      │
      ▼
Pipeline detects result.HasError
      │
      ▼
Pipeline stops the flow and returns the error to the user
```

**Benefits:**
- Step exceptions do not escape the pipeline (except configuration errors: empty step list, duplicate names, or a step that suspends without implementing `IResume`)
- Any typed result can signal an error via `HasError`
- Structured error information for debugging
- The pipeline stops gracefully on errors

**Watch out:** an error result **without** `OriginalException` is considered recoverable, and `RetryMiddleware` retries the step up to `MaxRetries`; with an exception, it is returned without retrying.

**Factory methods in ErrorStepResult:**
```csharp
// From a caught exception
ErrorStepResult.FromException(step, ex);

// From a simple message
ErrorStepResult.FromMessage(step, "Error description");
```

### 6.2 Response Parsing (there is no `ParserStep`)

**Purpose:** Transform the LLM's text response into the typed result object

**Where it happens:** inside `BaseLlmStep.ParseLlmResponseAsync`, not in a separate step.

**Characteristics:**
- If the result value is a `string` or a primitive, the content is used (or converted) as is
- If it is a class, `LlmHelpers.CleanJsonResponse` trims from the first `{`/`[` to the last `}`/`]` (or strips the markdown fences) and deserializes with Newtonsoft
- If it fails, it returns an error without an exception, so `RetryMiddleware` retries with the error message as feedback to the LLM
- A derived step can override `ParseLlmResponseAsync`

**Note:** `JsonResponseParser` (`Framework/Support/JSON`) exists, with four strategies (direct parsing, code block, JSON search in the text, cleanup and retry), but the framework does not use it; it is available for user code.

### 6.3 ActionStep (The Executor)

**Purpose:** Execute side effects

**Characteristics:**
- Signature: `ActionStep<TIn>(name, Func<TIn, PipelineContext, int, ActionResult?, Task> action, bool fireAndForget = false)`
- Returns an `ActionResult` with a text message ("The action X was executed successfully" or the error); it does not pass the input `Value` through
- Usually the end point of a pipeline branch
- If the action throws, it returns an error `ActionResult` with the exception (not retried)

**Execution modes:**

| Mode | Description | Typical use |
|------|-------------|------------|
| **Fire-and-Forget** (`fireAndForget: true`) | Launches the action with `Task.Run` and returns success without waiting; errors are only logged | Sending emails, async logging |
| **Awaited** (default) | Waits for the action to finish; the framework manages no rollback or transaction | Saving to DB, Git commits |

Idempotency, if needed, is the responsibility of the action.

**Use cases:**
```
┌─────────────────────────────────┐
│ SaveToDatabase                  │
│ • Input: ValidatedEntity        │
│ • Action: db.Save(entity)       │
│ • Output: ActionResult (message)│
└─────────────────────────────────┘

┌─────────────────────────────────┐
│ PublishEvent                    │
│ • Input: EventData              │
│ • Action: eventBus.Publish()    │
│ • Output: ActionResult (message)│
└─────────────────────────────────┘

┌─────────────────────────────────┐
│ SendEmail                       │
│ • Input: EmailRequest           │
│ • Action: smtp.Send()           │
│ • Output: ActionResult (message)│
└─────────────────────────────────┘
```

### 6.4 SwitchStep (The Router)

**Purpose:** Branch the flow based on a deterministic value

**Configuration:**
- `SwitchStep<TIn>(name, Func<TIn, PipelineContext, (IStep, IStepResult)> selector)`: the selector returns the target step and the result passed to it; the target is added to `NextSteps` and a `StepRoutingEvent` is emitted
- `RouterStep(name, Dictionary<string, IStep> routes, IStep? defaultRoute)`: routes by `IRouterResult.SelectedOption`, case-insensitively
- Fails if the selector returns no route, or if `RouterStep` finds no matching option and there is no `defaultRoute` (it throws `InvalidOperationException`, which `RetryMiddleware` converts into an error result)

**Typical usage pattern:**

```
IntentionAnalyzerStep (classifies intent)
          ↓
   LlmIntentionResult (SelectedOption)
          ↓
   RouterStep
          ↓
    ┌─────┴─────┬─────────┐
    ▼           ▼         ▼
 CreatePipe  ModifyPipe  QueryPipe
```

**Conceptual example:**
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

### 6.5 ParallelStep (Concurrent Execution)

**Purpose:** Run multiple steps in parallel, cognitively isolated, and collect their results by branch name.

**Architecture:**

- **Construction:** `new ParallelStep(name, params IStep[] branches)` or `ParallelStep<TIn>`.
- **Context cloning:** Each branch runs on `context.CloneForBranch()`: a cloned conversation (full copy of the history) and its own path; `Metadata` and `StepResults` are shared.
- **Concurrency:** `Parallel.ForEachAsync` with `MaxDegreeOfParallelism` (0 or less = `Environment.ProcessorCount`).
- **Result:** a `ParallelResult` whose `Value` is a dictionary `branch name → IStepResult` (accessible with `GetResult`, `GetResult<T>` or the indexer, which returns the branch's `Value`). There are no *merge* delegates and no output DTO: aggregation is done by the next step, which reads the `ParallelResult`.
- **Errors:** if any branch fails (or throws), the `ParallelResult` carries an `Error` with the concatenated messages, and the pipeline stops after the `ParallelStep`. The other branches are not cancelled.
- **Events:** `BranchStartedEvent` and `BranchCompletedEvent` per branch.
- **Retries:** `MaxRetries = 1` (the set as a whole is not retried).

```csharp
var parallel = new ParallelStep("Enrichment",
    getUserProfileStep,      // each branch is a regular IStep
    getPreferencesStep,
    getRecommendationsStep)
{
    MaxDegreeOfParallelism = 2
};

// Next step: combines the results by branch name
var merge = new DelegatedStep<ParallelResult, StepResult<UserView>>("Merge",
    (step, input, ctx, attempt, last) => Task.FromResult(new StepResult<UserView>(step,
        new UserView(input["GetUserProfile"], input["GetPreferences"], input["GetRecommendations"]))));
```

**Use cases:**

```
┌─────────────────────────────────────────┐
│ Multi-Source RAG (Parallel Query)       │
├─────────────────────────────────────────┤
│  Branch 1: VectorDB Technical           │
│  Branch 2: VectorDB Examples            │
│  Branch 3: VectorDB FAQs                │
│  Branch 4: SQL Historical Data          │
│                                         │
│  Merge: Rank by relevance               │
│         Filter top 10 documents         │
└─────────────────────────────────────────┘

┌─────────────────────────────────────────┐
│ Independent Validations                 │
├─────────────────────────────────────────┤
│  Branch 1: Schema Validator             │
│  Branch 2: Business Rules Checker       │
│  Branch 3: Security Policy Validator    │
│                                         │
│  Merge: Aggregate all warnings          │
└─────────────────────────────────────────┘

┌─────────────────────────────────────────┐
│ Data Enrichment                         │
├─────────────────────────────────────────┤
│  Branch 1: GetUserProfile (API)         │
│  Branch 2: GetPreferences (DB)          │
│  Branch 3: GetRecommendations (ML)      │
│                                         │
│  Merge: Build complete object           │
└─────────────────────────────────────────┘
```

**Performance note:** Only useful if the sub-steps are I/O-bound (HTTP calls, DB, LLM). For CPU-bound tasks, the overhead can be negative.

### 6.6 Composition: GroupStep, LoopStep and BreakStep (there is no `PipelineStep`)

**Purpose:** Reuse a sequence of steps as a step in another pipeline

**Characteristics:**
- `GroupStep(name, steps)` / `GroupStep<TIn>`: adds its steps to `NextSteps`, and the parent pipeline executes them in order, in the **same** context (nested path `Group/Step`). Its input type is that of the first step and its output type that of the last; `GroupStep<TIn>` checks in the constructor that the first step accepts `TIn`
- Shared context: `PipelineContext` is propagated
- `LoopStep(name, steps, maxIterations = 50)`: repeats the sequence; it exits when a child returns a `BreakStepResult` (produced by `BreakStep`, which keeps the value of its input), or when the iterations are exhausted (it emits `LoopExhaustedEvent`). Each iteration has its own path (`Loop/Iter_N`) and, with `OutputIterationToMetadata`, publishes the iteration number to `Metadata`
- Another option is to call `IPipeline.ExecuteAsync` from a `DelegatedStep`/`ActionStep`, which creates an independent nested execution

**Reuse pattern:**

```
Shared Sequence: ValidationGroup (GroupStep)
├─ DelegatedStep: JSON syntax
├─ DelegatedStep: Valid schema
└─ DelegatedStep: Business rules

Pipeline A: ChatFlow
├─ TemplateLlmStep: Generate
├─ GroupStep: ValidationGroup  ← Reuses
└─ ActionStep: Save

Pipeline B: BatchFlow
├─ DelegatedStep: Load from file
├─ GroupStep: ValidationGroup  ← Reuses
└─ ActionStep: Export results

Pipeline C: APIFlow
├─ DelegatedStep: Parse HTTP body
├─ GroupStep: ValidationGroup  ← Reuses
└─ ActionStep: Return JSON
```

**Advantages:**
- DRY (Don't Repeat Yourself)
- Testing: test the shared pipeline once
- Maintenance: changes in a single place
- Clarity: explicit composition

---

## 7. Validation and Correction Patterns

This is the **key innovation of the architecture**. It solves the problem of generating invalid code/data without creating complex cycles in the orchestrator.

### 7.1 The Problem of Cyclic Pipelines

**Traditional Cyclic Pipeline (Anti-pattern):**
```
AgentStep → ValidationStep → Valid?
               ↓                  │
           [Error]                │
               ↓                  │
               └──────────────────┘
                   (Retry)

Problems:
• Hard to monitor (how many loops did it go through?)
• Risk of stack overflow
• Confusing metrics (what counts as an "execution"?)
• Complex debugging (breakpoints in cycles)
```

### 7.2 Solution: Injected Hybrid Validation

**Principle:** The step is responsible for delivering a valid result. Validation is declared in the step itself (`IStepResult.ValidateAsync` of the result type, `Validate`/`IStepValidator` of the step) and is executed by `RetryMiddleware`, which retries the same step.

```
Pipeline (linear, forward-only)
   │
   ▼
┌──────────────────────────────────────────┐
│  BaseLlmStep + RetryMiddleware           │
│  ┌────────────────────────────────────┐  │
│  │ LOOP (MaxRetries=3)                │  │
│  │                                    │  │
│  │  Attempt 1:                        │  │
│  │  ├─ LLM generates                  │  │
│  │  ├─ Structural validation ✓        │ │
│  │  ├─ Semantic validation ✗          │ │
│  │  │   Error: "Line 40: missing ;"   │ │
│  │  │                                 │ │
│  │  Attempt 2:                        │ │
│  │  ├─ LLM generates (with previous error) │ │
│  │  ├─ Structural validation ✓        │ │
│  │  ├─ Semantic validation ✓          │ │
│  │  └─ SUCCESS                        │ │
│  └────────────────────────────────────┘ │
└──────────────────────────────────────────┘
   │
   ▼
Pipeline continues with valid result
```

### 7.3 Validation Levels

**Level 1: Structural (IStepResult.ValidateAsync)**

**Responsible:** The DTO object itself  
**When:** After each attempt that did not return an error  
**Cost:** Low (pure CPU)  
**Action on failure:** `StepValidationEvent` (structural type), and the step retries with the error as feedback

**Examples of structural validations:**
```
• Required properties are not null
• Strings have the expected format (email, URL)
• Numbers are within valid ranges
• Dates are consistent (start < end)
• Arrays/lists are not empty
• Enums have defined values
```

**Level 2: Semantic (`IStep.ValidateAsync` / `IStepValidator`)**

**Responsible:** The step (by overriding `Validate`) or validators registered with `AddValidator` (in YAML pipelines, the JS `validate` function of a schema is compiled by `SchemaCompiler` inside the `ValidateAsync` of the result type, i.e., at the structural level)  
**When:** Always, after a successful structural validation; the first validator that fails stops the chain  
**Cost:** High (I/O, complex processing)  
**Action on failure:** `StepValidationEvent` (semantic type), the error is added to the conversation, and the step retries

**Examples of semantic validations:**
```
• Code compiles without errors
• JSON schema is valid per specification
• SQL query is syntactically correct
• Referential integrity (foreign keys exist)
• Complex business rules
• Calls to external validation APIs
```

### 7.4 Canonical Example: Code Generation

**Scenario:** An agent generates C# code that must compile without errors

**Result structure:**
```
CodeGenerationResult
├─ CSharpCode: string (the generated code)
├─ Dependencies: string[] (using statements)
└─ Namespace: string
```

**Structural validation (in Result):**
```
Checks:
✓ CSharpCode is neither null nor empty
✓ Contains at least "class" or "record"
✓ Braces are balanced { }
✓ No invalid characters (control chars)

If it fails: 
→ Agent retries with a format error
```

**Semantic validation (IStepValidator of the step):**
```
Service: ICompilerService

Checks:
✓ C# syntax is valid
✓ Referenced types exist
✓ No compilation errors
✓ No critical warnings

If it fails:
→ Detailed error (line, column, message)
→ Added to the Agent's prompt
→ Agent retries with the error context
```

**Full flow:**
```
User: "Generate a User class with Name and Email properties"
   │
   ▼
TemplateLlmStep (Attempt 1)
├─ LLM generates code
├─ Structural validation ✓
├─ Semantic validation ✗
│   Error: "CS0246: Type 'string' could not be found (missing using System;)"
│
TemplateLlmStep (Attempt 2)
├─ LLM generates code (includes "using System;")
├─ Structural validation ✓
├─ Semantic validation ✓
└─ SUCCESS → Returns compilable code

Pipeline continues with a valid CodeGenerationResult
```

### 7.5 Feedback Management for the LLM

**Correction prompt strategy:**

What the framework does: on each retry, `BaseLlmStep` adds to the conversation, as a user message, the error message of the previous attempt as is (or, if there is no message, "Your response is invalid for {tipo}. Please ensure you return valid value"). The text of the error is written by whoever validates, so the following format is a recommendation for writing those messages, not something the framework generates:

```
Prompt in Attempt 1:
"Generate a C# class User with properties Name and Email."

Prompt in Attempt 2 (with feedback):
"PREVIOUS ATTEMPT FAILED:
The code you generated had compilation errors:
- Line 1, Column 1: CS0246 'string' could not be found
- Suggestion: Add 'using System;' at the top

Please correct the code and regenerate."
```

**Best practices:**
- Include the exact error (line, column if available)
- Give constructive suggestions
- Keep the context of the original request
- Limit the size of the feedback (max 500 tokens; the framework does not trim it)

---

## 8. Multi-Turn Conversations

### 8.1 Conversation Architecture

The framework manages conversational state **decoupled** from the technical execution state. The system prompt is not part of the conversation: each step provides it in its `LlmRequest`.

**Components:**

```
ConversationContext (Business)
├─ ConversationId: string
├─ History: MessageHistory
│   ├─ Messages: IReadOnlyList<Message>
│   ├─ Bookmarks: Dict<string, int> (not serialized)
│   └─ MaxTokens: int
├─ Metadata: Dict<string, object?>
├─ StartedAt / LastActivityAt
└─ CreateBookmark(), RestoreBookmark(), GetMessagesForRequest(), Clone()

MessageHistory (Optimization)
├─ AddMessage()
├─ CreateBookmark()
├─ GetMessagesFromBookmark()
├─ GetRecentMessages()
├─ GetMessagesWithSlidingWindow()
└─ ClearAfterBookmark()
```

### 8.2 Persistence (Logical Schema)

**Conceptual data model** (*not implemented*: it is a proposal for a custom persistence; the framework does not define these tables or bookmark types):

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

**Available implementations** (`IConversationStorage`: `SaveConversationAsync`, `GetConversationAsync`):
- `InMemoryConversationStorage` (the one registered by `AddAITaskAgent`)
- `FileConversationStorage` (JSON on disk)
- `YamlFileConversationStorage` (YAML on disk)
- Custom (implement the interface for Redis, PostgreSQL, etc.)

The complete execution state (`PipelineContext`, for suspend/resume) is stored separately with `IPipelineContextStorage`, whose only included implementation is `InMemoryPipelineContextStorage`.

### 8.3 Token Optimization

**Problem:** Long conversations exceed token limits (e.g.: 8K, 16K, 128K)

**Strategies implemented:**

**1. Sliding Window**
```
Keeps:
• First N messages (initial context)
• Last M messages (recent conversation)

Discards:
• Middle messages

Savings: ~30-50% for conversations >10 messages
```

**2. Summary Bookmarks** (*not implemented*)
```
Process:
1. Every 10 messages, create a summary bookmark
2. LLM summarizes those 10 messages into ~100 tokens
3. In future requests, use the summary + recent messages

Savings: ~60-70% for conversations >50 messages
```

**3. Retry Cleanup**
```
Process:
1. Create a bookmark before the first LLM attempt
2. If validation fails, the error is added as feedback (without cleaning up)
3. On completion (FinalizeAsync) the bookmark is restored and, if there was
   success, only the [user / final response] pair remains

Savings: ~500-1000 tokens per avoided retry
```

**Configuration (`AITaskAgent:Conversation`, code defaults):**
```
ConversationOptions
├─ MaxTokens: 8192
├─ UseBookmarks: true
├─ UseSlidingWindow: true
├─ KeepFirstNMessages: 2 (first messages that are always sent)
└─ SlidingWindowMaxTokens: 8192
```
(There is no `SummaryInterval`.)

### 8.4 Managing Multiple Conversations

**Per user:**
```
A user can have N active conversations
├─ Conversation A: "Help with Python code"
├─ Conversation B: "Project planning"
└─ Conversation C: "Document translation"

Each one has:
• Independent history
• Its own bookmarks
• Isolated context
```

**Best practices** (recommendations for the host application; the framework does not implement them):
- Auto-generated title from the first message
- Auto-archive after 30 days of inactivity
- Per-user limit (e.g.: 50 active conversations)
- Export to JSON for backup/analysis

---

## 9. Error Handling and Retries

### 9.1 Error Taxonomy

The framework distinguishes four error categories, each with a different strategy:

| Error Type | Owner | Strategy | Example |
|---------------|-------------|------------|---------|
| **Transient (HTTP)** | LLM service (`ILlmService`) and, if they persist, the role's provider fallback | Retry with backoff within the service; then `SwitchToNextFallback` | 429 Rate Limit, 503 Service Unavailable |
| **Validation (LLM)** | `RetryMiddleware` + `BaseLlmStep` | Step retry with feedback | Invalid JSON, incorrect format |
| **Semantic (Business)** | `RetryMiddleware` + `IStep.ValidateAsync` | Step retry with the validator's error | Code does not compile, invalid schema |
| **Logic (Programming)** | The step | Error result **with** exception (not retried) | Insufficient balance, file does not exist |

### 9.2 Retry Policies

**RetryPolicy** (`Framework/Resilience/RetryPolicy.cs`). The pipeline does not apply it: the LLM services use it (`GeminiLlmService` executes it; `OpenAILlmService` takes `MaxAttempts`, `InitialDelay` and `MaxDelay` from it, with 3 / 2 s / 60 s if none is passed). The step retries of `RetryMiddleware` are immediate, with no wait between attempts.

```
Default configuration:
├─ MaxAttempts: 3
├─ InitialDelay: 1 second
├─ MaxDelay: 30 seconds
├─ BackoffMultiplier: 2.0 (exponential)
├─ UseJitter: true (±15% random)
└─ ShouldRetry: Func<Exception, bool>
```

**Exponential backoff with jitter:**
```
Attempt 1: Fails → Wait 1s
Attempt 2: Fails → Wait ~2s (×0.85-1.15 jitter)
Attempt 3: Fails → Throws InvalidOperationException (MaxAttempts = 3)

By default only HttpRequestException, TimeoutException
and TaskCanceledException are retried, unless ShouldRetry is defined.
```

**Why jitter:** Prevents "thundering herd" when multiple requests fail simultaneously.

### 9.3 Circuit Breaker

**Purpose:** Avoid overloading services that are consistently failing.

**State in the code:** the `CircuitBreaker` class (`Framework/Resilience`) exists, but neither the pipeline nor the included LLM services use it, and `CircuitBreakerOptions` (`AITaskAgent:CircuitBreaker`) is bound from configuration without anyone reading it. To use it, the calls must be wrapped by hand with `ExecuteAsync`.

**States:**
```
┌─────────────┐
│   CLOSED    │ (Normal - allows requests)
│  ✓ Healthy  │
└──────┬──────┘
       │ Consecutive failures > threshold
       ▼
┌─────────────┐
│    OPEN     │ (Cut off - rejects requests)
│  ✗ Unhealthy│
└──────┬──────┘
       │ After the recovery timeout
       ▼
┌─────────────┐
│  HALF-OPEN  │ (Testing - lets requests through)
│  ? Testing  │
└──────┬──────┘
       │
       ├─ Success → CLOSED
       └─ Failure → OPEN (the failure counter is not reset when moving to HALF-OPEN)
```

**Configuration:**
```
CircuitBreaker(failureThreshold: 5, openDuration: 1 min)
CircuitBreakerOptions
├─ FailureThreshold: 5 (consecutive failures)
└─ OpenDurationSeconds: 60
(There is no HalfOpenMaxAttempts.)
```

**Recommended metrics** (the framework does not emit circuit breaker metrics):
- Failure rate per minute
- Time spent in the OPEN state (unavailability)
- Success ratio in HALF-OPEN

### 9.4 Rate Limiting

**Purpose:** Control the rate of calls to external services (especially LLMs).

**State in the code:** `RateLimiter` (`Framework/Resilience`) implements a token bucket (`WaitAsync`), with default values `maxTokens = 10`, refill every 1 s and `tokensPerRefill = maxTokens`. Only `GeminiLlmService` accepts one (optional, via constructor). `RateLimitOptions` (`AITaskAgent:RateLimit`: 100 / 1000 ms / 10) is bound but not read. `OpenAILlmService` instead limits concurrency per endpoint and deployment (`MaxConcurrentRequests` in `Inference.Options`, 9 by default) and shares the pause of a `Retry-After` among calls.

**Algorithm: Token Bucket**
```
Bucket (capacity: 10 tokens)
├─ RefillInterval: 1 second
├─ TokensPerRefill: 10
└─ Behavior:
    • Each request consumes 1 token
    • If the bucket is empty → Wait until refill
    • Unused tokens accumulate (up to the max)
```

**Flow example:**
```
T=0s:  Bucket [10/10] → 5 simultaneous requests
T=0s:  Bucket [5/10]  → Request 6 waits
T=1s:  Refill +10     → Bucket [10/10] (capped at the max)
T=1s:  Request 6 proceeds → Bucket [9/10]
```

**Per-environment configuration** (example; it would need to be passed when building the `RateLimiter`):
```
Development:
├─ MaxTokens: 100
└─ RefillInterval: 100ms (allows bursts)

Production:
├─ MaxTokens: 10
└─ RefillInterval: 1000ms (controls costs)
```

### 9.5 Timeouts and Cancellation

**Timeout hierarchy** (illustrative; the framework has two levels: the pipeline's, `pipelineTimeout` or `DefaultPipelineTimeout`, and the step's, `IStep.Timeout` or `DefaultStepTimeout`, applied by `TimeoutMiddleware` and **global across all retries** of the step. With `AddAITaskAgent` the default values come from `AITaskAgent:Timeouts`: 10 min and 1 min; without DI, `new Pipeline()` uses 60 min and 30 min. A step that exhausts its timeout returns an error result and emits `StepTimeoutEvent`. There is no timeout per LLM call in the framework; streaming has its own inactivity limit, `LlmInferenceSettings.StreamingInactivityTimeoutSeconds`, which `OpenAILlmService` reads):

```
┌──────────────────────────────────────┐
│ Request HTTP (e.g.: 2 minutes)       │ ← Outermost
│  ┌────────────────────────────────┐  │
│  │ Pipeline (e.g.: 60 seconds)    │  │
│  │  ┌──────────────────────────┐  │  │
│  │  │ BaseLlmStep (e.g.: 30s)  │  │  │
│  │  │  ┌────────────────────┐  │  │  │
│  │  │  │ LLM Call (e.g. 15s)│  │  │  │ ← Innermost
│  │  │  └────────────────────┘  │  │  │
│  │  └──────────────────────────┘  │  │
│  └────────────────────────────────┘  │
└──────────────────────────────────────┘
```

**Cancellation propagation:**
```
User cancels the request
   ↓
HttpContext.RequestAborted fires
   ↓
The CancellationToken passed to IPipeline.ExecuteAsync is cancelled
(linked with the pipeline timeout)
   ↓
All steps receive the cancellation signal
   ↓
The LLM service cancels the HTTP request
```

**Best practices:**
- Always propagate `CancellationToken`
- Do not ignore `OperationCanceledException`
- Clean up resources in finally blocks
- Timeouts progressively shorter the further inside you go

### 9.6 Error Handling by Type

**HTTP 429 (Rate Limit):**
```
Strategy (OpenAILlmService):
1. Read the "Retry-After" header
2. Wait that time (or the backoff if it is absent), sharing the pause
   with the other calls to the same endpoint/deployment
3. Retry up to MaxAttempts (3 by default)
4. If it fails again → LlmProviderException → role fallback, if there is one
```

**HTTP 503 (Service Unavailable):**
```
Strategy:
1. Retry with exponential backoff (within the LLM service)
2. If it persists after 3 attempts → LlmProviderException → role fallback
   (no circuit breaker is connected)
3. Record a degradation metric (the framework does not emit it)
```

**JSON Parse Error (LLM):**
```
Strategy:
1. Add the error to the conversation
2. The LLM retries generation
3. Maximum MaxRetries attempts (3 by default)
4. If it fails → step error result with the parse message
```

**Validation Error (Semantic):**
```
Strategy:
1. IStep.ValidateAsync / IStepValidator returns (false, error)
2. The error is added to the LLM conversation
3. The LLM retries with context
4. Maximum MaxRetries (default: 3)
```

**Business Logic Error:**
```
Strategy:
1. Fail-fast (no retry): only if the error carries OriginalException
   (an error without an exception is treated as recoverable and is retried)
2. Return a descriptive ErrorStepResult
3. The pipeline stops execution
4. The user receives a clear explanation
```

---

## 10. Tools (Capabilities)

### 10.1 Tool Architecture

Tools are functions that Agents **can** use, but they are not pipeline steps. They represent external capabilities.

**Design:**
```
┌────────────────────────────────────────┐
│   TOOL REGISTRY (AvailableTools)       │
│  (Global thread-safe catalog, ITools) │
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
│ (ITool via DI)  │   │ (tool names)       │
└─────────────────┘   └────────────────────┘
```

**Principle of Least Privilege** (each step receives only the tools it is given: names in `TemplateLlmStep`, or the `Tools` list of the step or of the `LlmRole`):
```
Agent A (Code Generator):
├─ Allowed tools:
│   ├─ compile_code
│   ├─ run_unit_tests
│   └─ check_style
│
Agent B (Customer Support):
├─ Allowed tools:
│   ├─ search_knowledge_base
│   ├─ get_order_status
│   └─ create_ticket

Agent B CANNOT use compile_code
Security by design
```

### 10.2 Anatomy of a Tool

**Conceptual structure:**
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
├─ IsReadOnly / UsageGuidelines (optional)
├─ ExecuteAsync(argumentsJson, context, stepName, logger, ct):
│   └─ Returns: "2025-12-16 14:30:00 UTC"
│
└─ GetDefinition() → ToolDefinition (provider-agnostic;
    each ILlmService translates it to its own format)
```

`UsageGuidelines` (or, if absent, `Description`) of each tool is added to the system prompt in a "TOOL USAGE GUIDELINES" section.

**Execution process:**
```
[LLM requests a tool]
   │
   ▼
{"name": "get_current_time", "arguments": {"timezone": "UTC"}}
   │
   ▼
[Framework looks up the tool by name in the step's list]
   │
   ▼
[Executes Tool.ExecuteAsync() with the JSON arguments as-is;
 the tool deserializes them]
   │
   ▼
[Returns the result as a string (or the error as text)]
   │
   ▼
[Adds the result as a message with role tool to the conversation]
   │
   ▼
[LLM sees the result and continues reasoning]
```

### 10.3 Common Tool Types

Tools included in the repository: `FileTools` (list, search, view, write and replace files), `OfficeTools` (Office/Access document extraction) and `McpIntegration` (tools from MCP servers). The following classification is conceptual.

**1. Information Retrieval**
```
Tools:
├─ search_database(query)
├─ get_user_profile(user_id)
├─ fetch_document(doc_id)
└─ query_knowledge_base(question)

Characteristics:
• Read-only
• Fast (< 1 second)
• Idempotent
```

**2. Computation**
```
Tools:
├─ calculate_expression(formula)
├─ compile_code(source_code)
├─ validate_json_schema(schema, data)
└─ run_unit_tests(test_suite)

Characteristics:
• CPU-bound
• Can take several seconds
• Deterministic
```

**3. External APIs**
```
Tools:
├─ call_weather_api(location)
├─ translate_text(text, target_lang)
├─ generate_image(prompt)
└─ search_web(query)

Characteristics:
• Network I/O
• Require API keys
• Can fail (rate limits)
```

**4. Side Effects**
```
Tools:
├─ send_email(to, subject, body)
├─ create_jira_ticket(title, description)
├─ commit_to_git(message, files)
└─ publish_event(event_data)

Characteristics:
• Modify external state
• NOT idempotent
• Require special permissions
```

### 10.4 Tool Security

**Recommended security rules** (of this list the framework only implements item 4: `LlmTool` emits `ToolStartedEvent`/`ToolCompletedEvent` with arguments and result, a `Tool.{name}` activity and the metrics `aitaskagent.tool.executions` and `aitaskagent.tool.duration`; the rest is the responsibility of each tool or of the host):

1. **Argument validation:** Validate the schema before executing
2. **Per-tool timeout:** Each tool has an individual timeout
3. **Rate limiting:** Limit calls per minute/hour
4. **Audit logging:** Log all executions
5. **Permissions:** Tools are only available if the user has the appropriate role

**Example of a tool with validation:**
```
Tool: execute_sql_query

Validations:
├─ Query must start with SELECT (read-only)
├─ Must not contain DROP, DELETE, UPDATE
├─ User must have role "data_analyst"
├─ Timeout: 30 seconds
└─ Rate limit: 10 queries/minute

If validation fails:
→ Returns an error without executing
→ The security log records the attempt
```

### 10.5 Tool Testing

**Recommended strategies:**

**Unit Testing (isolated Tools):**
```
Test: get_current_time_returns_valid_format
├─ Mock the time system
├─ Execute the tool with timezone="UTC"
├─ Assert: ISO 8601 format
└─ Assert: Correct timezone
```

**Integration Testing (Tools with real services):**
```
Test: search_database_integration
├─ Use a test database
├─ Insert known data
├─ Execute the tool with a query
├─ Assert: Expected results
└─ Clean up test data
```

**Mock for development** (does not exist in the framework; it is enough to register fake `ITool` implementations in `AvailableTools`):
```
MockToolRegistry
├─ Returns predefined responses
├─ Makes no real calls
├─ Simulates realistic latency
└─ Useful for testing Agents without external dependencies
```

---

### 10.6 Enrichment and Messaging in Tools

To improve the user experience, tools can emit real-time progress messages and enrich the standard events.

**Base Architecture (`LlmTool`):**
Complex tools (such as `BaseFileTool` and its derivatives) must inherit from `LlmTool` (instead of implementing `ITool` directly): `LlmTool.ExecuteAsync` is a *template method* that creates the activity, emits `ToolStartedEvent`/`ToolCompletedEvent`, records metrics and calls `InternalExecuteAsync(argumentsJson, context, logger, ct)`. It offers the hooks `EnrichActivityBefore`, `EnrichActivityAfter`, `EnrichStartedEvent` and `EnrichCompletedEvent`.

**Messaging (`NotifyProgressAsync`):**
It is a helper of `BaseFileTool` (project `FileTools`), not of `LlmTool`. It lets a tool send intermediate status updates visible to the user:
```csharp
// Example in ListDirTool
await NotifyProgressAsync($"📂 Listing directory '{path}'...", context, cancellationToken);
```
This emits a `StepProgressEvent` (with `EventType = "ToolProgress"`) that can be rendered by the UI.

---

## 11. Observability and Control

### 11.1 Observability System

Observability is **guaranteed by design** thanks to the pipeline's middleware chain: every step executed by the pipeline (including the `NextSteps` and the children of `ParallelStep`/`LoopStep`) goes through `ObservabilityMiddleware`.

**Observability layers:**

```
┌─────────────────────────────────────────┐
│  LAYER 4: BUSINESS METRICS              │
│  • Detected intents                     │
│  • Most used tools                      │
│  • Conversion rate (intent→action)      │
└─────────────────────────────────────────┘
         ↑
┌─────────────────────────────────────────┐
│  LAYER 3: COGNITIVE METRICS             │
│  • Tokens used (llm.tokens)             │
│  • Cost in USD (llm.cost)               │
│  • Retries (step.retries)               │
│  • Executed tool calls                  │
└─────────────────────────────────────────┘
         ↑
┌─────────────────────────────────────────┐
│  LAYER 2: TECHNICAL METRICS             │
│  • Latency per step (histogram)         │
│  • Executions and errors per step       │
│  • (no circuit breaker or               │
│     rate limiter metrics)               │
└─────────────────────────────────────────┘
         ↑
┌─────────────────────────────────────────┐
│  LAYER 1: PIPELINE EVENTS               │
│  • Pipeline/Step Started and Completed  │
│    (Success = false on failure)         │
│  • Tool Started/Completed               │
│  • Progress updates                     │
│  • Streaming (LlmResponseEvent)         │
└─────────────────────────────────────────┘
```

### 11.2 The Three Pillars of Observability

AITaskAgent implements the three standard pillars of modern observability, following industry best practices:

**Observability architecture:**

```
┌─────────────────────────────────────────────────────────────┐
│                    APPLICATION                              │
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
│  • Any ActivitySource / Meter listener                       │
│  • OpenTelemetry SDK (configured by the host)               │
│  • Custom Implementations                                    │
└─────────────────────────────────────────────────────────────┘
```

#### Pillar 1: Traces (Distributed Tracing)

**Purpose:** Track the execution flow across steps, creating a hierarchy of spans that shows the full path of a request.

**Implementation:** `System.Diagnostics.ActivitySource` named `"AITaskAgent"` (`Telemetry.Source`). The framework opens these activities:
```
Pipeline.{name}  (Pipeline.ExecuteAsync)
  tags: pipeline.name, correlation.id, pipeline.step_count, pipeline.timeout_ms
  └─ Step.{name}  (ObservabilityMiddleware, one per step and not per attempt)
       tags: step.name, step.type, correlation.id, step.duration_ms, step.success
       ├─ LLM.Invoke  (BaseLlmStep, one per iteration of the tool loop)
       │    tags: llm.model, llm.provider, llm.iteration, llm.has_tools,
       │          llm.tokens_used, llm.finish_reason
       └─ Tool.{name}  (LlmTool)
            tags: tool.name, step.name, correlation.id, tool.duration_ms, tool.success
```
Steps and tools can add tags through the `EnrichActivityBefore` / `EnrichActivityAfter` hooks.

**Note:** the interface `IStepTracer` (`OnTraceEventAsync(StepTraceEvent)`) exists, with `ConsoleStepTracer` and `NullStepTracer`, but the framework never invokes it. AdvancedLlmPipeline registers `NullStepTracer` in DI and YamlPipelineDemo defines a `LoggerStepTracer`, with no effect on execution.

#### Pillar 2: Metrics (Quantitative Telemetry)

**Purpose:** Collect numeric metrics about execution: duration, tokens, costs, success rates.

**Implementation:** `System.Diagnostics.Metrics.Meter` named `"AITaskAgent.Pipeline"` (internal class `Metrics`). There is no `IStepMetricsCollector` interface.

**Emitted metrics:**
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
There are no gauges (neither active steps nor circuit breaker state). `ObservabilityOptions.EnableMetrics` and `EnableTracing` are bound from configuration, but the framework does not read them.

#### Pillar 3: Logs (Structured Events)

**Purpose:** Record discrete events with context for debugging and auditing.

**Core interface:**
```
ILogger (Microsoft.Extensions.Logging)
  └─ LogInformation/Warning/Error()
       ├─ Structured Logging (template + parameters)
       ├─ Scopes for context
       └─ Levels: Trace, Debug, Info, Warning, Error, Critical
```

**Usage in AITaskAgent:**
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

Events (IEventChannel):
  • EventChannel logs every event it receives, at the level
    configured in ObservabilityOptions.EventLogLevel
```

**Implementations:**
- Console Logger (development)
- Serilog (production, structured logging)
- Application Insights, Datadog, etc.

### 11.2.1 OpenTelemetry Integration

*(The original text described an `AITaskAgent.OpenTelemetry` package with `OpenTelemetryTracingBridge` and `OpenTelemetryMetricsBridge`. That package does not exist.)*

The core does not depend on OpenTelemetry: it uses the native .NET APIs directly (`ActivitySource` and `Meter`), which the OpenTelemetry SDK knows how to listen to. A host application that wants to export traces and metrics configures its `TracerProvider` / `MeterProvider` by subscribing to the `"AITaskAgent"` source and the `"AITaskAgent.Pipeline"` meter, and chooses the exporters (console, OTLP to Jaeger, Tempo, Prometheus...). The `CorrelationId` travels as the `correlation.id` tag; it is not used as the `TraceId`.

**Concept mapping:**

| AITaskAgent | OpenTelemetry | Description |
|-------------|---------------|-------------|
| Activity `Pipeline.*` / `Step.*` / `LLM.Invoke` / `Tool.*` | Span | Traceable unit of work |
| `CorrelationId` | Attribute `correlation.id` | Execution identifier |
| `pipeline.name`, `step.name`, `step.type` | Span Attributes | Operation metadata |
| Meter `AITaskAgent.Pipeline` | Meter | Quantitative metrics |
| `aitaskagent.*.duration` | Histogram | Distribution of times |
| `aitaskagent.llm.tokens` | Counter | Cumulative counter |

### 11.3 Real-Time Event Streaming (IEventChannel)

**Architecture: System.Threading.Channels**

The framework uses high-performance asynchronous channels to decouple event emission from its processing, ensuring that observability does not affect pipeline latency.

**Components:**

```csharp
// Public interface
public interface IEventChannel
{
    Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent;

    // Subscriber with handler: own channel + background loop
    IDisposable Subscribe(Func<IProgressEvent, CancellationToken, Task> handler,
                          string? correlationId = null);

    // Subscriber that reads the channel itself
    IEventSubscription CreateSubscription(int capacity = 100, string? correlationId = null);
}

// Implementation (Infrastructure)
public sealed class EventChannel : IEventChannel, IAsyncDisposable
{
    // One bounded Channel<IProgressEvent> PER SUBSCRIBER (DropOldest).
    // SendAsync logs the event and writes it without waiting on
    // the channel of each subscriber whose correlationId filter matches.
}
```

**Data flow:**

```
[Step / Tool]
    │
    ▼
context.SendEventAsync(new ToolCompletedEvent(...))   (errors are swallowed)
    │
    ▼
EventChannel.SendAsync
 • Event log (EventLogLevel)
 • SuppressFromUser → not dispatched
    │
    ▼
[Channel per subscriber] (bounded; if full, drops the oldest and logs a warning)
    │
    ▼ (Asynchronous, loop per subscriber)
[Subscribers]
 • SSE Endpoint (UI Updates)
 • WebSocket Service
 • Console Logger
```

The capacity of subscribers with a handler is `EventChannelOptions.ChannelCapacity` (1000); `ObservabilityOptions.EventChannelCapacity` (100) exists in configuration but is not used. `IPipeline.ExecuteAsync` also accepts `subscribers`: if the context is not passed, it creates a local `EventChannel` for that execution with those handlers and closes it when finished.

**Event types (IProgressEvent) emitted by the framework:**

1. **Pipeline**: `PipelineStartedEvent`, `PipelineCompletedEvent`, `PipelineSuspendedEvent`.
2. **Step Lifecycle**: `StepStartedEvent` (with `AttemptNumber` on retries), `StepCompletedEvent` (with `Success`; there is no `StepFailedEvent`), `StepValidationEvent`, `StepTimeoutEvent`, `StepRoutingEvent`, `LoopExhaustedEvent`, `BranchStartedEvent` / `BranchCompletedEvent`.
3. **LLM Interaction**: `LlmResponseEvent` (streaming fragments with `FinishReason = Streaming`, reasoning with `IsThinking`, and the final response with tokens, cost, model and tool calls). There is no `LlmRequestEvent`.
4. **Tools**: `ToolStartedEvent` and `ToolCompletedEvent` (arguments, result, duration, success); `StepProgressEvent` for intermediate progress.
5. **Streaming Artifacts**: `TagStartedEvent` (start), `TagCompletedEvent` (end).
    > *Note: Artifacts (e.g., writing XML files) are side effects generated during streaming and do not count as conversation turns.*

There is no `ContentDeltaEvent`: the typewriter effect comes from the streaming `LlmResponseEvent`s. `PipelineResumingEvent`, `PipelineResumedEvent` and `StepCachedHitEvent` are defined but the framework does not emit them.

### 11.4 Implementation Providers

The framework is agnostic of the LLM provider:
- **`OpenAILlmService`**: Standard OpenAI integration (`OpenAI` SDK), with the `AzureOpenAILlmService` and `OpenRouterLlmService` variants. Retries with `Retry-After` and a concurrency limit per endpoint.
- **`GeminiLlmService`**: Native Google GenAI SDK integration. Supports:
  - **Context Caching**: Cost optimization.
  - **Google Grounding**: Built-in Google Search.
  - **Thinking models**: Reasoning budget.

**Advantages of the event channel:**

1. **Non-blocking**: The write to the channel is immediate; processing runs in the background.
2. **Backpressure**: Bounded channels per subscriber; during a spike the oldest events are dropped instead of slowing the pipeline.
3. **Decoupling**: The producer (Step) does not know its consumers (UI, Logs).



### 11.5 CorrelationId for Distributed Tracing

**Purpose:** Identify and correlate all operations of a complete execution flow, from the initial request to the final response, including all intermediate steps, logs and observability events.

**Implementation:**

The framework provides `CorrelationId` automatically in `PipelineContext`:

```csharp
public sealed record PipelineContext
{
    // Auto-generated if not provided
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString();
    
    // ... other properties
}
```

**Automatic propagation:**

`CorrelationId` is automatically propagated to:
1. **Logging scope** of each step (Serilog, NLog, etc.)
2. **Events** of observability (`IProgressEvent.CorrelationId`)
3. **Activities** (tag `correlation.id`); metrics do not carry it as a tag
4. **Parallel branches**: `CloneForBranch` keeps the same `CorrelationId`

**Basic usage (auto-generated):**

```csharp
// CorrelationId is generated automatically
var context = PipelineContextFactory.Create(conversation);
// context.CorrelationId = auto-generated GUID

var result = await pipeline.ExecuteAsync("ChatPipeline", steps, input, context);
```

**Advanced usage (propagation from HTTP):**

```csharp
// In an ASP.NET Core controller
[HttpPost("/api/chat")]
public async Task<IActionResult> Chat([FromBody] ChatRequest request)
{
    // Read CorrelationId from the header or generate a new one
    var correlationId = HttpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault()
        ?? Guid.NewGuid().ToString();
    
    // Propagate to the response
    Response.Headers.Add("X-Correlation-ID", correlationId);
    
    var context = new PipelineContext(_eventChannel, _serviceProvider)
    {
        CorrelationId = correlationId  // Use the same ID
    };
    
    var result = await _pipeline.ExecuteAsync("ChatPipeline", _steps, request.Message, context);
    return Ok(result);
}
```

**Reusing ConversationId as CorrelationId:**

```csharp
// For multi-turn conversations, use ConversationId
var context = new PipelineContext(eventChannel, serviceProvider)
{
    Conversation = conversation,
    CorrelationId = conversation.ConversationId  // Same ID for the whole conversation
};
```

**Serilog integration:**

```csharp
// The Pipeline opens a scope per step with Path, Step and CorrelationId
// Logs emitted while each step runs will carry the CorrelationId

// Serilog configuration
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()  // IMPORTANT: enable LogContext
    .WriteTo.Console(outputTemplate: 
        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
```

**Log example with CorrelationId:**

```
[19:35:00 INF] Pipeline ChatPipeline starting execution with 2 steps, CorrelationId: a1b2c3d4-e5f6-7890-abcd-ef1234567890
[19:35:01 INF] a1b2c3d4-e5f6-7890-abcd-ef1234567890 LLM step IntentionAnalyzer starting, model: ...
[19:35:02 INF] a1b2c3d4-e5f6-7890-abcd-ef1234567890 Routing to SalesPipeline
[19:35:03 INF] Pipeline ChatPipeline completed with status: Success, duration: 3012ms
```

**Observability events with CorrelationId:**

```csharp
// Every IProgressEvent includes CorrelationId; IEventChannel can filter by it
using var subscription = eventChannel.Subscribe(async (evt, ct) =>
{
    Console.WriteLine($"[{evt.StepName}] {evt.EventType}");
}, correlationId: targetCorrelationId);
```

**Searching logs (Elasticsearch/Kibana):**

```json
// Query to get all logs of one execution
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

**Advantages:**

1. **Easier debugging**: Filter all logs of a specific execution
2. **Full traceability**: Follow the flow from request to response
3. **Distributed tracing**: Compatible with OpenTelemetry and APM tools
4. **HTTP propagation**: It can come from the client's headers
5. **Efficient queries**: Search logs by CorrelationId
6. **Multi-service**: Propagate across microservices

**OpenTelemetry integration:**

No need to create activities by hand: `Pipeline.ExecuteAsync` opens the `Pipeline.{name}` activity with the `correlation.id` tag (see 11.2.1). `CorrelationId` does not replace `TraceId`.

### 11.6 Structured Logging

**Recommended structure:**

```
Log entry:
├─ Timestamp: ISO 8601
├─ Level: Debug/Info/Warning/Error
├─ CorrelationId: GUID (full trace)
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

**Scoped logging** (illustrative; the framework only opens the per-step scope described below):
```
Pipeline starts
└─ (no pipeline scope; the start log includes the CorrelationId)
    ├─ Step 1 starts
    │   └─ Scope: {Path, Step: "IntentionAnalyzer", CorrelationId}
    │       ├─ Log: "Analyzing user intention..."
    │       ├─ Log: "Selected option: CreateSchema"
    │       └─ Log: "Step completed in 1.2s"
    │
    ├─ Step 2 starts
    │   └─ Scope: {Path, Step: "RouterStep", CorrelationId}
    │       └─ ...
    │
    └─ Pipeline complete
        └─ Log: "Pipeline ChatPipeline completed with status: Success, duration: 5300ms"
```

**Independent logging per step (automatic scope):**

The framework automatically wraps the execution of each `IStep` in a logging scope that includes:
- `Step`: Step name.
- `Path`: Execution path (e.g. `MainPipeline/Router/SalesPipeline`).
- `CorrelationId`: unique ID of the trace.

This makes it possible to filter the logs of a specific step even when they occur inside injected services or middlewares.

**Filter example (Serilog):**
```csharp
// Only show logs of the "SchemaValidator" step
.Filter.ByIncludingOnly(le => 
    le.Properties.ContainsKey("Step") && 
    le.Properties["Step"].ToString().Contains("SchemaValidator"))
```

**Compatible providers:**
- Serilog (recommended)
- NLog
- Application Insights
- OpenTelemetry Collector
- ELK Stack (Elasticsearch, Logstash, Kibana)

### 11.7 Metrics and Dashboards

**Recommended KPIs** (some have no metric in the framework: circuit breaker, cost per user and satisfaction would have to be measured by the host; see 11.2 for the actual metrics):

**Performance:**
```
• Latency P50, P90, P99 per step
• Throughput (requests/second)
• Error rate (%)
• Circuit breaker opens/hour
```

**Costs:**
```
• Tokens/request (average, P95)
• Cost/request in USD
• Cost/user/day
• Top 10 users by cost
```

**Quality:**
```
• Retries (aitaskagent.step.retries: how much the LLM self-corrects)
• Validation failure rate
• Tool execution success rate
• User satisfaction score
```

**Conceptual dashboard:**
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

### 11.8 Recommended Alerts

**Critical (Pagerduty / SMS):**
```
• Error rate > 5% for 5 minutes
• Circuit breaker OPEN for >2 minutes
• Latency P99 > 30 seconds
• Daily cost > $200 (configurable threshold)
```

**Warnings (Email / Slack):**
```
• Error rate > 2% for 15 minutes
• Retries (step.retries) > 50% of requests
• Tool failure rate > 10%
• Conversation storage > 90% capacity
```

**Informational (Dashboard):**
```
• New traffic spike detected
• User exceeded daily token limit
• New intent detected (not in enum)
```
---
### 11.9 Message Interception (Audit & Security)

**Purpose**: Inspect/modify the raw payload before it is sent to the LLM (decorator over ILlmService). *Not implemented in the framework*: it is a pattern the application can apply by registering its own `ILlmService` decorator; the framework does not include interceptors.

**Use**: PII masking (hiding emails), security guardrails and exact cost calculation before the call.

---

## 12. Advanced Usage Patterns

### 12.1 Pattern: Intention → Route → Action

**Purpose:** Classify the user's intention and route it to a specialized pipeline.

**Flow diagram:**
```
User: "I want to create a JSON schema for users"
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
│ • Get: SelectedOption=CreateSchema │
│ • Route: CreateSchemaPipeline      │
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
Response: valid JSON schema saved
```

**Advantages:**
- Clear separation of responsibilities
- Easy to add new intentions
- Isolated testing per intention
- Metrics per intention

### 12.2 Pattern: Layered Validation

**Purpose:** Validate at multiple levels before executing an expensive action.

**Layer diagram:**
```
User input
   │
   ▼
┌─────────────────────────────┐
│ Layer 1: Syntax             │
│ • JSON parseable            │
│ • Correct types             │
│ • Nulls where not allowed   │
└─────────────────────────────┘
   │ ✓
   ▼
┌─────────────────────────────┐
│ Layer 2: Semantics          │
│ • Valid schema              │
│ • References resolve        │
│ • Coherent structure        │
└─────────────────────────────┘
   │ ✓
   ▼
┌─────────────────────────────┐
│ Layer 3: Business           │
│ • Domain rules              │
│ • Limits and constraints    │
│ • Best practices            │
└─────────────────────────────┘
   │ ✓
   ▼
Expensive action (save, compile, etc.)
```

**Implementation:**
```
Shared validation pipeline (GroupStep):
├─ DelegatedStep: Syntax (fast, CPU)
├─ DelegatedStep: Semantics (medium, I/O)
└─ DelegatedStep: Business (slow, complex logic)

Reused by:
├─ Chat Pipeline
├─ Batch Pipeline
├─ API Pipeline
└─ Builder UI Pipeline
```

### 12.3 Pattern: Multi-Source RAG

**Purpose:** Query multiple knowledge sources in parallel and combine the results.

**Architecture:**
```
User query
   │
   ▼
┌──────────────────────────────────────┐
│ ParallelStep (4 branches)            │
├──────────────────────────────────────┤
│                                      │
│  Branch 1: VectorDB Technical        │
│  └─ Top 5 most relevant documents    │
│                                      │
│  Branch 2: VectorDB Examples         │
│  └─ Top 5 similar examples           │
│                                      │
│  Branch 3: VectorDB FAQs             │
│  └─ Top 5 related questions          │
│                                      │
│  Branch 4: SQL Historical            │
│  └─ Previous user queries            │
│                                      │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Next step (reads ParallelResult)     │
│ • Rank by relevance (score)          │
│ • Deduplicate similar content        │
│ • Filter top 10 documents            │
│ • Add metadata (source, score)       │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ AnswerGeneratorAgent                 │
│ • System prompt with RAG context     │
│ • Generates an answer citing sources │
│ • Includes links to documents        │
└──────────────────────────────────────┘
   │
   ▼
Answer with verified sources
```

**Advantages:**
- Reduced latency (parallel queries)
- More complete answers (multiple sources)
- Traceability (source of each datum)
- Scalable (easy to add new sources)

### 12.4 Pattern: Fast Response + Asynchronous Processing

**Purpose:** Respond to the user quickly while a long-running task is processed in the background.

**Flow:**
```
User: "Generate 100 PDF reports"
   │
   ▼
┌─────────────────────────────┐
│ ValidationGuard             │
│ • Checks permissions        │
│ • Check limits              │
└─────────────────────────────┘
   │ ✓
   ▼
┌─────────────────────────────┐
│ QuickResponseStep           │
│ • Generates unique job_id   │
│ • Saves to queue            │
│ • Returns 202 Accepted      │
└─────────────────────────────┘
   │
   ├─────────────────────────────────┐
   │                                 │
   ▼                                 ▼
User receives:               Background worker:
"Job 123 started"            ├─ Processes 100 reports
"Status: /api/jobs/123"      ├─ Updates progress
                            ├─ Saves results
                            └─ Sends notification

User queries:
GET /api/jobs/123
└─ {"status": "processing", "progress": "45/100"}
```

**Implementation with Channel:**
```
JobQueue (Channel<JobRequest>)
   │
   ├─ API writes: job request
   │
   └─ Worker reads: processes in background
       ├─ Updates JobStatus in DB
       ├─ Publishes progress events
       └─ Notifies on completion (email/webhook)
```

### 12.5 Pattern: Reusable Shared Pipeline

**Purpose:** Define once, use in multiple contexts.

**Example: Compilation Pipeline**

```
CompilationGroup (reusable GroupStep)
├─ DelegatedStep: Non-empty code
├─ TemplateLlmStep: Generates executable code
├─ DelegatedStep: No compilation errors
└─ ActionStep: Saves binary

Used in:
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

Note: the same step object must not appear twice at the same level
(step names must be unique in the list the pipeline receives).
```

**Advantages:**
- DRY (Don't Repeat Yourself)
- Centralized testing
- Maintenance in a single place
- Consistent behavior

---
### 12.6 Pattern: Context Scoping

**Purpose:** Give sub-agents only the information they need, not the entire history.

**Solution:** Use `StatelessTemplateLlmStep` for sub-agents: each execution starts from a clean conversation, saving tokens and reducing hallucinations caused by earlier noise. The ```ParallelStep``` clones the ```ConversationContext``` per branch to isolate writes, but the copy includes the full history (it does not filter it).

---

## 13. Implementation Guides

### 13.1 Initial Setup

**Framework reference:** in the repository, the samples reference the `Framework/AITaskAgent.csproj` project (and the provider and tools projects) via `ProjectReference`; it has not been verified that a published NuGet package exists.

**Startup configuration:**
```
Register services:
├─ AddSingleton<ILlmService, OpenAILlmService>() / GeminiLlmService ...
│     (one per provider used in AITaskAgent:LlmProviders)
├─ AddSingleton<ITool, MiTool>()     (custom tools)
├─ AddAITaskAgent()                  (no parameters; reads IConfiguration from DI:
│     options, LlmEnvironment, IPipeline, IEventChannel, PipelineContextFactory,
│     IPipelineContextStorage, IConversationStorage, ITools, ConversationContext transient)
└─ serviceProvider.UseAITaskAgent()  (creates the singletons and registers
      the DI ITools in AvailableTools)
```
Roles are obtained from `IOptions<LlmEnvironment>` (`Roles[name]`).

### 13.2 Creating a Basic Agent

**Step 1: Define capabilities (IntentionOption)**
```
var options = new List<IntentionOption>
{
    new() { Id = "Summarize",   Description = "User wants to summarize a document" },
    new() { Id = "ExtractInfo", Description = "User wants to extract specific information" },
    new() { Id = "Compare",     Description = "User wants to compare multiple documents" }
};
```

**Step 2: Create action pipelines**
```
For each intention:
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
    │   └─ result: ParallelResult { "AnalyzeDoc1": ..., "AnalyzeDoc2": ... }
    ├─ ComparisonAgent (reads the ParallelResult)
    └─ FormatOutputStep
```

**Step 3: Build the main pipeline**
```
Main Pipeline:
├─ IntentionAnalyzerStep<StepResult<string>>("Intent", role, options)
└─ RouterStep("Router", routes)
    ├─ "Summarize"   → GroupStep(SummarizePipeline)
    ├─ "ExtractInfo" → GroupStep(ExtractionPipeline)
    └─ "Compare"     → GroupStep(ComparePipeline)

await pipeline.ExecuteAsync("Main", [intentStep, routerStep], userText, context);
```


### 13.3 Best Practices

**Code Organization:**
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

**Per-Environment Configuration:**
```
Development:
├─ LLM: Fast, cheap model (gpt-3.5-turbo)
├─ MaxRetries: 1 (fail-fast for debugging)
├─ Logging: Verbose (Debug level)
├─ Circuit Breaker: Disabled (not wired into the framework)
└─ Storage: In-memory

Staging:
├─ LLM: Production model
├─ MaxRetries: 2
├─ Logging: Info level
├─ Circuit Breaker: Enabled (low threshold; requires manual integration)
└─ Storage: SQLite or test DB (custom IConversationStorage implementation)

Production:
├─ LLM: Optimized model (cost/quality)
├─ MaxRetries: 3
├─ Logging: Warning level + metrics
├─ Circuit Breaker: Enabled (high threshold; requires manual integration)
└─ Storage: PostgreSQL/Redis (custom IConversationStorage implementation)
```

**Secrets Handling:**
```
GOOD:
├─ Use Azure Key Vault / AWS Secrets Manager
├─ Environment variables (CI/CD)
├─ User Secrets (local development)
└─ Never in code or git

BAD:
├─ Hardcoded API keys
├─ Strings in committed appsettings.json
└─ Secrets in logs or exceptions
```

**Sensitive Logging:**
```
Log:
├─ Performance metrics
├─ Errors and exceptions
├─ Pipeline decisions (which intention, which route)
└─ Audit trail of critical actions

DO NOT log:
├─ API keys or tokens
├─ Personal data (PII)
├─ Passwords or credentials
└─ User message content (GDPR)
```

### 13.4 YAML Pipelines

*(Section added in the review: the capability exists in `Framework/YAML` and was not documented.)*

- `SchemaCompiler` compiles the JSON Schemas in a folder (`CompileFromFolderAsync`) into in-memory C# types that implement `IStepResult` (NJsonSchema + Roslyn). A schema may include a JavaScript function `validate(self, context)` (ClearScript V8) that becomes the structural validation of the type.
- `YamlPipelineFactory.CreateFromFolderAsync` merges all `*.yaml` files in a folder, deserializes the steps by their tag (only `IYamlStep` types marked with `[YamlStepTag]` are mapped; in the framework, only `!LlmStep` → `YamlLlmStep`. `ParallelStep`, `GroupStep`, `LoopStep` and `BreakStep` carry `[StepTag]` and comments that suggest they are serializable from YAML, but the factory does not register them), resolves the role of each `YamlLlmStep` by `profileName` in `LlmEnvironment`, assigns the compiled types of `inputSchema`/`outputSchema`, validates and orders the dependencies (`dependsOn`), and returns a `YamlPipeline` that runs on `IPipeline`.
- The `YamlPipelineDemo` sample (`pipelines/main.yaml`) shows a pipeline of three `!LlmStep` steps (classify, extract, summarize).

### 13.5 Repository Samples

*(Section added in the review; descriptions taken from each README or `Program.cs`, execution not verified.)*

| Sample | What it shows |
|---|---|
| `SimpleChatDemo` | Console chat with OpenAI/Gemini providers and a simple tool (current time) |
| `AdvancedLlmPipeline` | Writer and critic pipeline with conversation bookmarks and templates |
| `GeminiTestApp` | Direct test of `GeminiLlmService` |
| `FileToolsTestApp` | Console agent with the `FileTools` tools |
| `McpIntegrationDemo` | Consumes tools from an MCP server through `McpIntegration` |
| `PipelineVisualizer` | Web application that relays `EventChannel` events over SSE |
| `YamlPipelineDemo` | Pipeline defined in YAML with compiled schemas and streamed reasoning |
| `TextToTemplateDemo` | No real LLM: a code step produces text that a `StatelessTemplateLlmStep` embeds in its template (echo service) |
| `CodebaseAnalysis.Core`, `CodebaseAnalysisPipeline`, `CodebaseAnalysisWeb` | Repository analysis application (core, CLI and web) that audits a repository against a knowledge graph and generates Markdown reports; each project has its own README |
| `DotNetEnricher` | Roslyn graph enricher for .NET solutions, launched by the analysis |

---

## Conclusion

**AITaskAgent** represents a mature, pragmatic approach to building specialized agents for enterprise environments. Through deliberate architectural decisions (ADRs), the framework balances deterministic control with the flexibility of LLMs, ensuring observability without sacrificing performance.

**Key principles:**

1. **Strict Hybrid**: Clear separation between the probabilistic world (LLM agents) and the deterministic one (code steps)
2. **Guaranteed Observability**: Every step executed by the pipeline leaves a trace (activity, metrics and events)
3. **Layered Validation**: Structural vs Semantic, with automatic correction
4. **Type Safety**: Generic steps (`TypedStep<TIn,TOut>`, `BaseLlmStep<TIn,TOut>`); compatibility between consecutive steps is checked at runtime (with input conversion when possible)
5. **Production First**: Designed from day one for critical enterprise systems

The framework does not try to be everything to everyone. It is a specialized tool for teams building task-oriented agents where control, auditability and predictability are non-negotiable requirements.

---

**Document Version**: 4.0 (Consolidated)  
**Last Updated**: January 2026 (code review: 2026-10-17)
