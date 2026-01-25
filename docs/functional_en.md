# AITaskAgent Framework - Definitive Functional Specification

**Version**: 4.0 (Consolidated)  
**Status**: DEFINITIVE  
**Date**: January 2026  
**Document**: Unified Master Specification

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

**AITaskAgent** is a .NET framework designed to orchestrate **Specialized Agents** and **Deterministic Processes** in enterprise systems where AI creativity must be strictly bounded by business rules, code validations, and predictable execution.

**It is not a generic framework for any type of agent.** It is specifically optimized for:

- **Enterprise task-oriented agents** with finite and known capabilities
- Workflows where **LLM output must be validated** before progressing
- Systems requiring **complete audit** of every decision
- Applications where **cost control** (tokens, LLM calls) is critical
- Production environments that need **predictable behavior**

### 1.2 Design Philosophy: "Strict Hybrid"

The framework enforces a rigid architectural distinction between two worlds:

```
┌─────────────────────────────────────────────────────────────┐
│                    PROBABILISTIC WORLD                       │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              AGENTS (LLM-Powered)                   │    │
│  │  • Controlled creativity                            │    │
│  │  • Conversational memory                            │    │
│  │  • Tool access                                      │    │
│  │  • Output NOT guaranteed until validation           │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
                            ↓
                   ┌────────────────┐
                   │   VALIDATION   │
                   │   (Bridge)     │
                   └────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│                    DETERMINISTIC WORLD                       │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              STEPS (C# Code)                        │    │
│  │  • Compilers and parsers                            │    │
│  │  • Schema validators                                │    │
│  │  • Data transformers                                │    │
│  │  • I/O connectors (DB, APIs)                        │    │
│  │  • Binary execution: success or failure             │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

**Purpose:** Provide a *runtime* where Agents "live" within a linear pipeline that guarantees observability, error handling, and that **no invalid result progresses through the flow**.

### 1.3 Value Proposition

**Who this framework is for:**

- C# teams building specialized agents (not generic open chatbots)
- Projects where flow and cost control are critical
- Applications needing predictable behavior in production
- Developers who value type safety and breakpoint debugging
- Systems requiring complete audit (compliance, regulation)

**Who it is NOT for:**

- Open conversational chatbots without predefined structure
- Systems where the LLM must completely decide the flow without restrictions
- Rapid experimental prototyping without production requirements
- Teams preferring YAML/JSON configuration over code
- Projects prioritizing total agent autonomy over control

### 1.4 Design Principles

AITaskAgent is built on four fundamental pillars that dictate every architectural decision:

1. **Determinism over Autonomy.** We reject the idea that AI should control the application flow. In this framework, C# code is king and the LLM is an advisor. The pipeline defines the rigid structure; the agent only fills in the flexible content. There are no "magic loops" or unpredictable emergent plans.
2. **Radical Type Safety.** If it doesn't compile, it shouldn't run. We avoid string dictionaries and dynamic objects. From Step definitions to parallel aggregation, everything is strongly typed to leverage .NET compiler robustness.
3. **Cognitive Isolation.** AI errors (hallucinations, invalid syntax) must be resolved within the Agent (containment), never propagated to the orchestrator. The main pipeline only sees valid results or fatal failures, keeping the execution trace clean and linear.
4. **Inevitable Observability.** Telemetry is not an optional "plugin"; it's part of the data structure that transports execution. We make it architecturally impossible to execute a step without leaving a trace, guaranteeing total audit in production environments.

---

## 2. Architecture Decision Log (ADL)

These decisions are the **immutable foundations** of the framework. Any future changes must respect these principles or provide solid architectural justification.

### ADR-001: LLM Protocol Abstraction

**Decision:** The framework abstracts LLM communication protocol through the `ILlmService` interface, allowing implementations for different providers (OpenAI, Google, Anthropic, etc.).

**Context:** 
- Different LLM providers have different APIs but similar functionality
- Business requirements may change the preferred provider
- The framework must be agnostic of the specific provider

**Justification:**
- **Flexibility**: Switch providers without modifying business code
- **Testability**: Easy to mock `ILlmService` in unit tests
- **Multi-provider**: Use different models for different steps as needed

**Implementation:**
```
ILlmService (abstraction)
├─ OpenAILlmService (OpenAI/Azure OpenAI implementation)
├─ GoogleLlmService (Google AI implementation)
└─ [Custom implementations]
```

**Consequences:**
- Business code decoupled from LLM provider
- Each implementation can optimize for its specific provider
- Unique features of each provider are configured via `LlmProviderConfig`

---

### ADR-002: Use of Reflection

**Decision:** Use `System.Reflection` for template parameter extraction and result parsing, without manual caching.

**Context:**
- The framework is **I/O Bound** - latency is dominated by LLM calls (1000-5000ms)
- Reflection in .NET 7+ with NativeAOT is aggressively optimized by the runtime
- Reflection overhead is ~10-50µs per object vs ~2000ms for LLM (0.0025% of total time)

**Justification:**
- Development ergonomics: Users define normal properties, framework reads them automatically
- Maintainability: No code generation, manual serialization, or special interfaces
- Performance reality: In an I/O-bound system, optimizing CPU is premature optimization

**Empirical measurements:**
```
GetProperties() on object with 20 properties: ~15µs
Complete JSON serialization: ~100µs
Minimum LLM latency: 1,000,000µs (1 second)
→ Reflection is 0.0015% of total time
```

**Consequences:**
- Clean API without special attributes or generated code
- Direct debugging with breakpoints
- ⚠ Not suitable for microsecond hot-paths (but that's not our case)

**Rejected alternatives:**
- Source Generators: Add compilation complexity for 0.001% gain
- Manual caching: .NET already does it better than any custom implementation

---

### ADR-003: Context Separation (Technical vs Business)

**Decision:** `PipelineContext` (technical, singleton) is immutable and separate from `ConversationContext` (business, mutable but cloneable).

**Context:**
- In parallel execution (`ParallelStep`), multiple branches can modify conversational state
- Race conditions on shared conversations cause message corruption
- Technical context (logger, metrics, cancellation) must be shared

**Justification:**
- **Thread-safety**: Technical context is immutable (C# record), safe by design.
- **Context Scoping (Focus)**: When cloning, we allow filtering the history. Sub-agents don't need to receive all the previous "garbage" conversation, only the relevant context. This saves tokens and reduces hallucinations.
- **Isolation**: Each parallel branch owns its own instance, preventing memory corruption in concurrent writes.

**Implementation:**
```
PipelineContext (immutable record)
├─ Services (shared, read-only)
├─ Metrics (shared, thread-safe)
├─ Logger (shared, scoped)
└─ Conversation (reference to mutable object)

In ParallelStep:
├─ Technical context → Shared
└─ Conversation → Cloned per branch
```

**Consequences:**
- Guaranteed concurrency safety
- Traceability: Each branch has its own conversational history
- ⚠ Requires explicit cloning in parallel splits (documented)

---

### ADR-004: Internal Validation with Feedback Loop

**Decision:** Semantic error correction (e.g., code that doesn't compile) occurs **inside** the Agent through a retry loop with validator feedback. The main Pipeline is linear and doesn't manage backtracking (*forward-only*).

**Context:**
- LLMs produce probabilistic outputs that can fail business validations
- A cyclic pipeline (Agent → Validator → Agent) is hard to maintain and monitor
- Retries must maintain previous error context for effective correction

**Justification:**
- **Observability**: Pipeline only sees "Agent completed successfully or failed"
- **Encapsulation**: Correction logic is contained within the Agent
- **Performance**: Retries don't traverse the entire observability chain

**Pattern diagram:**
```
Pipeline (linear, forward-only)
  └─ AgentStep (black box with internal loop)
       ├─ Attempt 1: LLM generates → ValidationA ✓ → ValidationB ✗
       ├─ Attempt 2: LLM generates with B's error → ValidationA ✓ → ValidationB ✓
       └─ Returns valid result to Pipeline
```

**Consequences:**
- Clean metrics: "Agent took 3 seconds, 2 internal retries"
- Debugging: Breakpoints in Agent's internal loop
- ⚠ Validation errors are NOT visible in pipeline (intentional)

---

### ADR-005: Mandatory Async

**Decision:** All public API is async. Blocking threads with `.Result`, `.Wait()` or excessive locks is forbidden.

**Context:**
- LLM operations are inherently asynchronous (HTTP, streaming)
- Blocking ThreadPool threads causes deadlocks and degrades server performance
- .NET has first-class support for async/await

**Justification:**
- **Scalability**: Thousands of concurrent requests without exhausting threads
- **Responsiveness**: UI doesn't freeze in desktop/mobile applications
- **Compatibility**: Natural integration with ASP.NET Core, Blazor, etc.

**Requirements:**
- Use `Task.WhenAll` or `Parallel.ForEachAsync` for concurrency
- CancellationToken propagated in all operations
- Avoid `ConfigureAwait(false)` in library code (unnecessary in .NET Core+)

**Consequences:**
- Optimal performance on web servers
- Natural integration with modern .NET ecosystem
- ⚠ Learning curve for developers unfamiliar with async

---

### ADR-006: Hybrid Validation (Structural vs Semantic)

**Decision:** Clear distinction between DTO validation (`IStepResult.ValidateAsync`) and business validation (delegate injected in Agent).

**Context:**
- Validations have different cost levels and responsibilities
- Some checks are synchronous and cheap (nulls, ranges)
- Others require expensive external services (compilers, APIs)

**Separation of responsibilities:**

| Type | Responsible | When executed | Examples |
|------|-------------|---------------|----------|
| **Structural** | `IStepResult.ValidateAsync()` | Always, even in dry-run | Nulls, types, basic formats |
| **Semantic** | Delegate in AgentStep | Only in production, after structural validation | Compilation, DB queries, complex logic |

**Conceptual example:**
```
Result: Generated C# Code

Structural Validation (in Result):
✓ CSharpCode property is not null
✓ Contains at least one type declaration
✓ Has basic balanced braces syntax { }

Semantic Validation (delegate):
✓ C# syntax is valid
✓ Type references exist
✓ No compilation errors
✓ Critical warnings absent
```

**Consequences:**
- Clear separation of concerns
- Performance: Expensive validations only when necessary
- Testability: Structural validation tested without mocks
- ⚠ Requires developer discipline not to mix them

---

### ADR-007: JSON Parsing Robustness (Newtonsoft)

**Decision**: Standardize use of Newtonsoft.Json and NJsonSchema for all LLM response deserialization and state persistence.

**Justification**: System.Text.Json is too strict for LLMs' syntactic "creativity" (extra commas, comments). Newtonsoft prioritizes fault tolerance over CPU micro-optimization in this I/O-bound context.

## 3. Fundamental Concepts and Taxonomy

### 3.1 Conceptual Hierarchy

```
┌─────────────────────────────────────────────────────┐
│              APPLICATION / HOST                      │
│  (E.g., REST API, Blazor App, Console)               │
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
│         AGENT PIPELINE                      │
│  (Orchestrated sequence of steps)           │
└─────────────────────────────────────────────┘
        │
        ├─ AgentStep (Cognitive)
        │    ├─ IntentionRouter
        │    ├─ CodingAgent
        │    └─ SummarizerAgent
        │
        ├─ SwitchStep (Bifurcation)
        │    └─ RouteByIntention
        │
        └─ ActionStep (Effects)
             ├─ CommitToRepo
             └─ SendNotification
```

### 3.2 Component Glossary

#### Main Components

**AgentPipeline**
- **What it is:** Execution unit that orchestrates a linear sequence of steps
- **Responsibility:** Control flow, handle errors, report metrics
- **Key characteristic:** Forward-only (no backtracking)

**AgentStep (Cognitive)**
- **What it is:** Unit with LLM reasoning capability
- **Characteristics:**
  - Has System Prompt and conversational memory
  - Can use tools
  - Implements internal correction loop
  - Output not guaranteed until validation

**ActionStep (Deterministic)**
- **What it is:** Side effect executor (I/O)
- **Characteristics:**
  - Fire-and-forget or transactional
  - Does not modify main data flow
  - Typically an endpoint of a pipeline branch

**SwitchStep (Deterministic)**
- **What it is:** Flow bifurcator based on deterministic value
- **Typical use:** Routes based on Enum from a RouterAgent

#### Specialized Agent Types

**RouterAgentStep**
- **Purpose:** Classify user intention
- **Input:** Free text from user
- **Output:** Strongly typed Enum (`IntentionResult<T>`)
- **Optimization:** Uses dynamic Few-Shot Prompting

**ChatAgentStep**
- **Purpose:** Maintain coherent multi-turn conversations
- **Characteristics:**
  - Stateful: reads/writes history
  - Automatically manages bookmarks
  - Optimizes tokens with sliding window

**AgentStep (Generic)**
- **Purpose:** Text-to-text or text-to-JSON transformations
- **Characteristics:**
  - Stateless by default
  - Can have memory injected if needed

### 3.3 Component Relationships

```
1 Application
  └─ N Interaction Modes (Chat, Batch, Builder UI)
       └─ 1 Main AgentPipeline per mode
            ├─ N Steps (sequence)
            └─ Can invoke sub-pipelines (composition)

1 AgentStep
  ├─ 1 System Prompt
  ├─ 0..1 ConversationContext (optional)
  ├─ 0..N Tools (capabilities)
  └─ 1 Semantic validator (optional)

1 Pipeline
  ├─ N Steps (sequential execution)
  ├─ 1 PipelineContext (infrastructure)
  └─ 0..N Observers (metrics, SSE)
```

---

## 4. The Execution Model: Inversion of Control

### 4.1 The Problem It Solves

Traditionally, there are two execution models for pipelines:

**Model A: Autonomous Steps**
```
Step1 ──invokes──> Step2 ──invokes──> Step3

Advantage: Total flexibility
Disadvantage: Observability not guaranteed
Disadvantage: Complex debugging
Disadvantage: Inconsistent metrics
```

**Model B: Traditional Orchestrator Pipeline**
```
Pipeline knows complete graph
  ├─ Executes Step1
  ├─ Executes Step2
  └─ Executes Step3

Advantage: Guaranteed observability
Disadvantage: Requires declaring complete graph
Disadvantage: Complex dynamic routing
Disadvantage: Architectural overhead
```

**Model C: Inversion of Control with Delegate (AITaskAgent)**
```
Pipeline injects delegate in Context
Step decides next → Asks Pipeline to execute
Pipeline wraps with observability

Flexibility of A
Observability of B
No declarative graph overhead
```

### 4.2 Model Mechanics

**Execution flow:**

```
[Start]
   │
   ▼
┌──────────────────────────────────────┐
│ Pipeline.ExecuteAsync()              │
│ • Creates PipelineContext            │
│ • Injects InvokeStep delegate        │
│ • Delegate points to internal method │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Step1.InvokeAsync()                  │
│ • Executes internal logic            │
│ • Decides next: Step2                │
│ • Does NOT invoke directly           │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ context.InvokeStep(Step2, result)    │
│ • Calls delegate                     │
└──────────────────────────────────────┘
   │
   ▼
┌──────────────────────────────────────┐
│ Pipeline.ExecuteStepWithControl()    │
│ • BeforeHook (optional)              │
│ • Notifies Observer (start)          │
│ • Starts metrics                     │
│ • Applies timeout                    │
│ • Executes Step2                     │
│ • Records metrics                    │
│ • AfterHook (optional)               │
│ • Notifies Observer (complete)       │
└──────────────────────────────────────┘
   │
   ▼
[Returns result to Step1]
```

**Key components:**

**PipelineContext**
- Data structure that transports the delegate
- Immutable (C# record)
- Contains: Services, Metrics, Logger, Cancellation, Conversation

**InvokeStep Delegate**
- Signature: `Func<IStep, IStepResult, Task<IStepResult>>`
- Injected by Pipeline
- Readonly to prevent modifications

**Step**
- Decides what the next step is
- Asks Pipeline to execute it
- Has no observability logic

### 4.3 Model Advantages

| Characteristic | Detail |
|----------------|--------|
| **Guaranteed Observability** | Every step passage goes through pipeline. Impossible to skip logging/metrics |
| **Maintained Flexibility** | Steps still decide flow. Dynamic routing works perfectly |
| **Architectural Simplicity** | No need for step registry with IDs or declarative graph |
| **Centralized Control** | Timeouts, circuit breakers, dry-run mode in one place |
| **Improved Testing** | Mock delegate for unit tests. Verification of which steps were invoked |

### 4.4 Why It's NOT Choreography

**Differences from Choreography (Microservices)**

| Aspect | Classic Choreography | AITaskAgent |
|--------|----------------------|-------------|
| **Flow control** | Distributed among actors | Centralized in Pipeline |
| **Execution order** | Emergent and unpredictable | Deterministic and defined |
| **Context knowledge** | Each service must know others | Steps don't know the rest |
| **Coupling** | High (messages and events) | Low (only I/O contract) |
| **Supervision** | Difficult (no central point) | Total (Pipeline controls everything) |

**Clarifying diagram:**
```
Choreography (Microservice A doesn't know B exists)
   ServiceA → EventBus → ServiceB → EventBus → ServiceC
   (Emergent flow, hard to follow)

AITaskAgent (Step1 decides but doesn't execute)
   Pipeline → Step1 (decides Step2) → Pipeline (executes Step2)
   (Defined flow, centralized control)
```

---

## 5. Agents: Cognitive Entities

### 5.1 Agent Anatomy

Agents are the "intelligent" components that inherit from `AgentStepBase`. Unlike deterministic steps, they manage uncertainty and possess self-correction mechanisms.

**Agent Components:**

```
┌────────────────────────────────────────┐
│         AGENT STEP                     │
├────────────────────────────────────────┤
│                                        │
│  🧠 IDENTITY                           │
│  ├─ System Prompt (personality)        │
│  ├─ Model (GPT-4, Claude, etc.)        │
│  └─ Temperature (creativity)           │
│                                        │
│  💾 MEMORY                             │
│  ├─ ConversationContext (optional)     │
│  ├─ Message History                    │
│  └─ Bookmarks (optimization)           │
│                                        │
│  🔧 CAPABILITIES                       │
│  ├─ Tool Registry (tools)              │
│  ├─ Tool Names (permissions)           │
│  └─ Tool Execution (recursive)         │
│                                        │
│  🛡️ RESILIENCE                         │
│  ├─ Max Retries (validation)           │
│  ├─ Feedback Loop (correction)         │
│  └─ Bookmark Cleanup (tokens)          │
│                                        │
│  📊 METRICS                            │
│  ├─ Tokens Used                        │
│  ├─ Cost USD                           │
│  └─ Cognitive Retries (self-corr.)     │
│                                        │
└────────────────────────────────────────┘
```

### 5.2 Agent Types

#### AgentStep (The Standard)
**Purpose:** General text-to-text or text-to-JSON transformations

**Characteristics:**
- Stateless regarding conversation (unless injected)
- Useful for one-off tasks: summaries, entity extraction, content generation
- No persistent memory between invocations

**Use cases:**
- Generate documentation from code
- Extract structured data from free text
- Translate between formats (JSON → YAML)
- Classify sentiment or categories

#### ChatAgentStep (The Conversational)
**Purpose:** Maintain coherence in multi-turn conversations

**Characteristics:**
- Stateful: Reads and writes to `ConversationContext.History`
- Automatically manages bookmarks to optimize tokens
- Sliding window: Maintains first N messages + last M

**Use cases:**
- Customer support chatbots
- Interactive configuration assistants
- Educational tutors with progress tracking
- Contextual recommendation systems

#### RouterAgentStep (The Classifier)
**Purpose:** Categorical decision-making

**Characteristics:**
- Input: User text
- Output: Strongly typed Enum (`IntentionResult<T>`)
- Uses dynamic Few-Shot Prompting based on Enum's `[Description]`
- Low temperature (0.3) for consistent decisions

**Use cases:**
- Classify user intention (create, modify, query)
- Detect input language
- Select support department
- Determine urgency level

### 5.3 Agent Lifecycle

```
[User sends request]
   │
   ▼
┌─────────────────────────────────────────┐
│ Pipeline invokes AgentStep              │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ Agent creates BOOKMARK in conversation  │
│ (Restoration point)                     │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ LOOP: Until MaxLlmRetries               │
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
│  │    • With timeout               │    │
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
│  │ 5. Structural Validation        │    │
│  │    • IStepResult.ValidateAsync()│    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ 6. Semantic Validation          │    │
│  │    • Injected delegate          │    │
│  │    • (e.g., compiler, DB)       │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ All valid?                      │    │
│  │  YES → EXIT loop                │    │
│  │  NO → Restore bookmark          │    │
│  │       Add error to prompt       │    │
│  │       Continue loop             │    │
│  └─────────────────────────────────┘    │
│                                         │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ Clean conversation                      │
│ • Delete failed attempts                │
│ • Keep only valid response              │
└─────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────┐
│ Add result to conversation              │
│ (If ConversationContext present)        │
└─────────────────────────────────────────┘
   │
   ▼
[Return result to Pipeline]
```

**Critical details:**

1. **Initial bookmark**: Marks the starting point before any attempt
2. **Correction loop**: Retries include error as feedback to LLM
3. **Automatic cleanup**: Failed attempts do NOT contaminate final conversation
4. **Cognitive metrics**: Retry count is recorded as "Cognitive Retries"

### 5.4 Recursive Tool Execution

When an Agent requests to use tools, the framework handles recursion automatically until the LLM stops requesting tools.

**Recursion diagram:**

```
┌─────────────────────────────────────────────┐
│ InvokeLlmWithToolsAsync(iteration=0)        │
├─────────────────────────────────────────────┤
│ 1. LLM generates response with tool_calls   │
│    [{"name": "get_time"}, {"name": "calc"}] │
│                                             │
│ 2. Add assistant message with tool_calls    │
│    to conversation                          │
│                                             │
│ 3. Execute ALL tools:                       │
│    • get_time() → "14:30"                   │
│    • calc(2+2) → "4"                        │
│                                             │
│ 4. Add results as tool messages             │
│    to conversation                          │
│                                             │
│ 5. Recursion: iteration=1                   │
│    ┌────────────────────────────────────┐   │
│    │ InvokeLlmWithToolsAsync(iteration=1)│  │
│    │ • LLM sees context + tool results  │   │
│    │ • Generates final response NO tools│   │
│    │ • Returns response                 │   │
│    └────────────────────────────────────┘   │
│                                             │
│ 6. Return final response                    │
└─────────────────────────────────────────────┘
```

**Safety limits:**
- `MaxToolIterations = 10` (configurable)
- Prevents infinite loops if LLM always requests tools
- Each tool has individual timeout (configurable in `LlmOptions.ToolTimeout`)

### 5.5 Token Optimization with Bookmarks

Bookmarks are restoration points in the conversation that allow:

1. **Clean failed attempts**: Don't contaminate context with invalid responses
2. **Intelligent sliding window**: Maintain first N messages + last M
3. **Selective compression**: Summarize old conversation sections

**Optimization strategies:**

| Strategy | When to use | Token savings |
|----------|-------------|---------------|
| **Bookmark + Cleanup** | Validation retries | ~500-1000 tokens/retry |
| **Sliding Window** | Conversations >10 messages | ~30-50% of total |
| **Summary Bookmarks** | Conversations >50 messages | ~60-70% of total |

**Sliding window example:**
```
Original conversation (15 messages, 3000 tokens):
[System, User1, Asst1, User2, Asst2, ..., User15, Asst15]

With sliding window (keepFirstN=2, maxTokens=1500):
[System, User1] + [User13, Asst13, User14, Asst14, User15, Asst15]
               ↑                                              ↑
       First 2                                        Last 6
       (context)                                      (recent)

Savings: 3000 → 1500 tokens (50%)
```

---

## 6. Deterministic Steps: The Rigor of Code

Deterministic steps **DO NOT use LLMs**. They are pure C# functions that guarantee the pipeline is predictable and safe.

### 6.1 Error Handling with StepError

**Architecture:** The framework does NOT use exceptions for normal flow error communication. Instead, each `IStepResult` can contain structured error information.

**Components:**

```csharp
// Interface (all results implement)
public interface IStepResult
{
    object? Value { get; }
    bool IsError { get; }
    StepError? Error { get; }  // ← Structured information
}

// Error information
public sealed record StepError
{
    public required string Message { get; init; }
    public string? StepName { get; init; }
    public Exception? OriginalException { get; init; }
}
```

**Error flow:**
```
Exception in Step
      │
      ▼
StepBase.catch captures
      │
      ▼
Creates ErrorStepResult.FromException()
      │
      ▼
Pipeline detects result.IsError
      │
      ▼
Pipeline stops flow and returns error to user
```

**Benefits:**
- No exceptions escaping the pipeline
- Any typed result can indicate error via `IsError`
- Structured error information for debugging
- Pipeline stops gracefully on errors

**Factory methods in ErrorStepResult:**
```csharp
// From captured exception
ErrorStepResult.FromException(ex, stepName);

// From simple message
ErrorStepResult.FromMessage("Error description", stepName);
```

### 6.2 ParserStep (The Translator)

**Purpose:** Transform `StringStepResult` (raw JSON) into typed POCO/Record object

**Characteristics:**
- Uses `JsonResponseParser` with multiple fallback strategies
- If fails, returns error (no retry)
- Typically used after a well-configured Agent

**Parsing strategies (in order):**

1. **Direct Parse**: Attempt to deserialize JSON directly
2. **Extract Code Block**: Look for ```json ... ``` or ``` ... ```
3. **Find JSON in Text**: Regex to find JSON objects/arrays
4. **Clean and Retry**: Remove garbage (markdown, comments, etc.)

**Note:** If Agent uses correction loop, ParserStep should rarely fail.

### 6.3 ActionStep (The Executor)

**Purpose:** Execute side effects

**Characteristics:**
- Does not modify result `Value` (or passes it transparently)
- Usually the endpoint of a pipeline branch
- Can be Fire-and-Forget or Transactional

**Execution modes:**

| Mode | Description | Typical use |
|------|-------------|-------------|
| **Fire-and-Forget** | Launches background task, doesn't wait | Email sending, async logging |
| **Transactional** | Waits for confirmation, can rollback | Save to DB, Git commits |
| **Idempotent** | Can be executed multiple times | Publish events, create files with overwrite |

**Use cases:**
```
┌─────────────────────────────────┐
│ SaveToDatabase                  │
│ • Input: ValidatedEntity        │
│ • Action: db.Save(entity)       │
│ • Output: SavedEntity (with ID) │
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

### 6.4 SwitchStep (The Router)

**Purpose:** Bifurcate flow based on deterministic value (typically an Enum)

**Configuration:**
- Dictionary: `Dictionary<TEnum, IStep>`
- Complete type safety at compile-time
- Fails if no route defined for value

**Typical usage pattern:**

```
RouterAgentStep (classifies intention)
          ↓
   IntentionResult<Intent>
          ↓
   SwitchStep<Intent>
          ↓
    ┌─────┴─────┬─────────┐
    ▼           ▼         ▼
 CreatePipe  ModifyPipe  QueryPipe
```

**Conceptual example:**
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

### 6.5 ParallelStep (Concurrent Execution)

**Purpose:** Execute multiple steps in parallel, cognitively isolated and safely aggregating results.

**Architecture:**

- **Fluent Builder Pattern**: Explicitly links Step with mapping logic, avoiding positional coupling (indices).
- **Context Cloning (Deep Copy):** Each branch receives an independent copy of ConversationContext to avoid race conditions in chat history.
- **Synchronized Merge**: Framework applies internal lock during result aggregation phase to allow safe use of lists and complex properties in output DTO.
- **Mechanics**:
   
1. Define an output DTO (TResult).
2. Register branches (IParallelBranch) encapsulating Step and Merge Action.
3. Parallel execution (Parallel.ForEachAsync) of steps.
4. Synchronized fusion: Result injected into unique DTO using configured delegate.

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

**Performance note:** Only useful if sub-steps are I/O-bound (HTTP calls, DB, LLM). For CPU-bound tasks, overhead can be negative.

### 6.6 PipelineStep (Composition)

**Purpose:** Execute a complete pipeline as a step of another pipeline

**Characteristics:**
- Allows reuse of pipelines as building blocks
- Type safety: Internal pipeline Input/Output validated
- Shared context: `PipelineContext` propagated

**Reuse pattern:**

```
Shared Pipeline: ValidationPipeline
├─ LambdaStep: JSON Syntax
├─ LambdaStep: Valid Schema
└─ LambdaStep: Business rules

Pipeline A: ChatFlow
├─ AgentStep: Generate
├─ PipelineStep: ValidationPipeline  ← Reuses
└─ ActionStep: Save

Pipeline B: BatchFlow
├─ ActionStep: Load from file
├─ PipelineStep: ValidationPipeline  ← Reuses
└─ ActionStep: Export results

Pipeline C: APIFlow
├─ ActionStep: Parse HTTP body
├─ PipelineStep: ValidationPipeline  ← Reuses
└─ ActionStep: Return JSON
```

**Advantages:**
- DRY (Don't Repeat Yourself)
- Testing: Test shared pipeline once
- Maintenance: Changes in one place
- Clarity: Explicit composition

---

## 7. Validation and Correction Patterns

This is the **key architectural innovation**. It solves the problem of invalid code/data generation without creating complex cycles in the orchestrator.

### 7.1 The Cyclic Pipeline Problem

**Traditional Cyclic Pipeline (Antipattern):**
```
AgentStep → ValidationStep → Valid?
               ↓                  │
           [Error]                │
               ↓                  │
               └──────────────────┘
                   (Retry)

Problems:
• Hard to monitor (how many loops?)
• Stack overflow risk
• Confusing metrics (what counts as "execution"?)
• Complex debugging (breakpoints in cycles)
```

### 7.2 Solution: Injected Hybrid Validation

**Principle:** The Agent is responsible for delivering a valid result. Validation is injected **inside** the Agent as a delegate.

```
Pipeline (linear, forward-only)
   │
   ▼
┌──────────────────────────────────────────┐
│  AgentStep (black box for Pipeline)      │
│  ┌────────────────────────────────────┐  │
│  │ Internal LOOP (MaxLlmRetries=3)    │  │
│  │                                    │  │
│  │  Attempt 1:                        │  │
│  │  ├─ LLM generates                  │  │
│  │  ├─ Structural validation ✓        │  │
│  │  ├─ Semantic validation ✗          │  │
│  │  │   Error: "Line 40: missing ;"   │  │
│  │  │                                 │  │
│  │  Attempt 2:                        │  │
│  │  ├─ LLM generates (with prev error)│  │
│  │  ├─ Structural validation ✓        │  │
│  │  ├─ Semantic validation ✓          │  │
│  │  └─ SUCCESS                        │  │
│  └────────────────────────────────────┘  │
└──────────────────────────────────────────┘
   │
   ▼
Pipeline continues with valid result
```

### 7.3 Validation Levels

**Level 1: Structural (IStepResult.ValidateAsync)**

**Responsible:** The DTO object itself  
**When:** Always, even in dry-run  
**Cost:** Low (pure CPU)  
**Action if fails:** Agent retries correcting format

**Structural validation examples:**
```
• Required properties are not null
• Strings have expected format (email, URL)
• Numbers are in valid ranges
• Dates are coherent (start < end)
• Arrays/lists are not empty
• Enums have defined values
```

**Level 2: Semantic (Injected Delegate)**

**Responsible:** External service  
**When:** Only in production, after structural validation  
**Cost:** High (I/O, complex processing)  
**Action if fails:** Error added to prompt and Agent retries

**Semantic validation examples:**
```
• Code compiles without errors
• JSON Schema is valid per specification
• SQL Query is syntactically correct
• Referential integrity (foreign keys exist)
• Complex business rules
• External validation API calls
```

### 7.4 Canonical Example: Code Generation

**Scenario:** Agent generates C# code that must compile without errors

**Result structure:**
```
CodeGenerationResult
├─ CSharpCode: string (generated code)
├─ Dependencies: string[] (using statements)
└─ Namespace: string
```

**Structural Validation (in Result):**
```
Checks:
✓ CSharpCode is not null or empty
✓ Contains at least "class" or "record"
✓ Braces are balanced { }
✓ No invalid characters (control chars)

If fails: 
→ Agent retries with format error
```

**Semantic Validation (delegate in Agent):**
```
Service: ICompilerService

Checks:
✓ C# syntax is valid
✓ Type references exist
✓ No compilation errors
✓ Critical warnings absent

If fails:
→ Detailed error (line, column, message)
→ Added to Agent's prompt
→ Agent retries with error context
```

**Complete flow:**
```
User: "Generate a User class with Name and Email properties"
   │
   ▼
AgentStep (Attempt 1)
├─ LLM generates code
├─ Structural validation ✓
├─ Semantic validation ✗
│   Error: "CS0246: Type 'string' could not be found (missing using System;)"
│
AgentStep (Attempt 2)
├─ LLM generates code (includes "using System;")
├─ Structural validation ✓
├─ Semantic validation ✓
└─ SUCCESS → Returns compilable code

Pipeline continues with valid CodeGenerationResult
```

### 7.5 LLM Feedback Management

**Correction prompt strategy:**

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
- Include exact error (line, column if available)
- Give constructive suggestions
- Maintain original request context
- Limit feedback size (max 500 tokens)

---

## 8. Multi-Turn Conversations

### 8.1 Conversation Architecture

The framework manages conversational state **decoupled** from technical execution state.

**Components:**

```
ConversationContext (Business)
├─ ConversationId: string
├─ SystemPrompt: string?
├─ MessageHistory
│   ├─ Messages: List<ChatMessage>
│   ├─ Bookmarks: Dict<string, int>
│   └─ MaxTokens: int
├─ Metadata: Dict<string, object?>
└─ Timestamps (created, lastActivity)

MessageHistory (Optimization)
├─ AddMessage()
├─ CreateBookmark()
├─ GetMessagesFromBookmark()
├─ GetRecentMessages()
├─ GetMessagesWithSlidingWindow()
└─ ClearAfterBookmark()
```

### 8.2 Persistence (Logical Schema)

**Conceptual data model:**

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

**Available implementations:**
- SQLite (reference, light production)
- Memory (testing, demos)
- Custom (interface for Redis, PostgreSQL, etc.)

### 8.3 Token Optimization

**Problem:** Long conversations exceed token limits (e.g., 8K, 16K, 128K)

**Implemented strategies:**

**1. Sliding Window**
```
Maintains:
• First N messages (initial context)
• Last M messages (recent conversation)

Discards:
• Middle messages

Savings: ~30-50% for conversations >10 messages
```

**2. Summary Bookmarks**
```
Process:
1. Every 10 messages, create summary bookmark
2. LLM summarizes those 10 messages in ~100 tokens
3. In future requests, use summary + recent messages

Savings: ~60-70% for conversations >50 messages
```

**3. Retry Cleanup**
```
Process:
1. Create bookmark before LLM attempt
2. If validation fails, ClearAfterBookmark()
3. Only successful attempt remains in history

Savings: ~500-1000 tokens per avoided retry
```

**Recommended configuration:**
```
ConversationOptions
├─ MaxTokens: 4000 (for 8K context models)
├─ UseBookmarks: true
├─ UseSlidingWindow: true
├─ KeepFirstNMessages: 2 (system + first user)
└─ SummaryInterval: 10 messages
```

### 8.4 Multiple Conversation Management

**Per user:**
```
User can have N active conversations
├─ Conversation A: "Python code help"
├─ Conversation B: "Project planning"
└─ Conversation C: "Document translation"

Each one has:
• Independent history
• Own bookmarks
• Isolated context
```

**Best practices:**
- Auto-generated title on first message
- Auto-archive after 30 days of inactivity
- Limit per user (e.g., 50 active conversations)
- Export to JSON for backup/analysis

---

## 9. Error Handling and Retries

### 9.1 Error Taxonomy

The framework distinguishes four error categories with different strategies:

| Error Type | Responsible | Strategy | Example |
|------------|-------------|----------|---------|
| **Transient (HTTP)** | Pipeline | Automatic retry with backoff | 429 Rate Limit, 503 Service Unavailable |
| **Validation (LLM)** | AgentStep | Internal loop with feedback | Invalid JSON, incorrect format |
| **Semantic (Business)** | AgentStep | Internal loop with validator | Code doesn't compile, invalid schema |
| **Logical (Programming)** | StepBase | Return ErrorStepResult | Insufficient balance, file not found |

### 9.2 Retry Policies

**RetryPolicy (Global Configuration)**

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
Attempt 1: Fails → Wait 1s (±150ms jitter)
Attempt 2: Fails → Wait 2s (±300ms jitter)
Attempt 3: Fails → Wait 4s (±600ms jitter)
Attempt 4: Fails → Throw exception
```

**Why jitter:** Prevents "thundering herd" when multiple requests fail simultaneously.

### 9.3 Circuit Breaker

**Purpose:** Avoid overloading services that are consistently failing.

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
       │ After recovery timeout
       ▼
┌─────────────┐
│  HALF-OPEN  │ (Testing - allows 1 request)
│  ? Testing  │
└──────┬──────┘
       │
       ├─ Success → CLOSED
       └─ Failure → OPEN
```

**Configuration:**
```
CircuitBreakerOptions
├─ FailureThreshold: 5 (consecutive failures)
├─ OpenDuration: 60 seconds
└─ HalfOpenMaxAttempts: 3
```

**Recommended metrics:**
- Failure rate per minute
- Time in OPEN state (unavailability)
- Success ratio in HALF-OPEN

### 9.4 Rate Limiting

**Purpose:** Control call rate to external services (especially LLMs).

**Algorithm: Token Bucket**
```
Bucket (capacity: 10 tokens)
├─ RefillInterval: 1 second
├─ TokensPerRefill: 10
└─ Behavior:
    • Request consumes 1 token
    • If bucket empty → Wait until refill
    • Unused tokens accumulate (up to max)
```

**Flow example:**
```
T=0s:  Bucket [10/10] → 5 simultaneous requests
T=0s:  Bucket [5/10]  → Request 6 waits
T=1s:  Refill +10     → Bucket [15/10] (capped)
T=1s:  Request 6 proceeds → Bucket [14/10]
```

**Configuration per environment:**
```
Development:
├─ MaxTokens: 100
└─ RefillInterval: 100ms (allows bursts)

Production:
├─ MaxTokens: 10
└─ RefillInterval: 1000ms (controls costs)
```

### 9.5 Timeouts and Cancellation

**Timeout hierarchy:**

```
┌──────────────────────────────────────┐
│ HTTP Request (e.g., 2 minutes)       │ ← Outermost
│  ┌────────────────────────────────┐  │
│  │ Pipeline (e.g., 60 seconds)    │  │
│  │  ┌──────────────────────────┐  │  │
│  │  │ AgentStep (e.g., 30s)    │  │  │
│  │  │  ┌────────────────────┐  │  │  │
│  │  │  │ LLM Call (e.g., 15s)│  │  │  │ ← Innermost
│  │  │  └────────────────────┘  │  │  │
│  │  └──────────────────────────┘  │  │
│  └────────────────────────────────┘  │
└──────────────────────────────────────┘
```

**Cancellation propagation:**
```
User cancels request
   ↓
HttpContext.RequestAborted fires
   ↓
PipelineContext.CancellationToken cancels
   ↓
All steps receive cancellation signal
   ↓
LLM service cancels HTTP request
```

**Best practices:**
- Always propagate `CancellationToken`
- Don't ignore `OperationCanceledException`
- Resource cleanup in finally blocks
- Progressively shorter timeouts going inward

### 9.6 Error Handling by Type

**HTTP 429 (Rate Limit):**
```
Strategy:
1. Read "Retry-After" header
2. Wait that time + jitter
3. Retry once
4. If fails again → Propagate error
```

**HTTP 503 (Service Unavailable):**
```
Strategy:
1. Retry with exponential backoff
2. If persists after 3 attempts → Open circuit
3. Record degradation metric
```

**JSON Parse Error (LLM):**
```
Strategy:
1. Add error to prompt
2. LLM retries generation
3. Maximum 3 attempts
4. If fails → ErrorStepResult with raw JSON
```

**Validation Error (Semantic):**
```
Strategy:
1. Delegate returns ValidationResult with error
2. Error added to LLM prompt
3. LLM retries with context
4. Maximum MaxLlmRetries (default: 3)
```

**Business Logic Error:**
```
Strategy:
1. Fail-fast (no retry)
2. Return descriptive ErrorStepResult
3. Pipeline stops execution
4. User receives clear explanation
```

---

## 10. Tools (Capabilities)

### 10.1 Tool Architecture

Tools are functions that Agents **can** use, but are not pipeline steps. They represent external capabilities.

**Design:**
```
┌────────────────────────────────────────┐
│        TOOL REGISTRY                   │
│  (Global thread-safe catalog)          │
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

**Least Privilege Principle:**
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

### 10.2 Tool Anatomy

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
├─ Execute(argumentsJson):
│   └─ Returns: "2025-12-16 14:30:00 UTC"
│
└─ ChatTool (OpenAI format):
    └─ Auto-generated
```

**Execution process:**
```
[LLM requests tool]
   │
   ▼
{"name": "get_current_time", "arguments": {"timezone": "UTC"}}
   │
   ▼
[Framework deserializes arguments]
   │
   ▼
[Executes Tool.ExecuteAsync()]
   │
   ▼
[Returns result as string]
   │
   ▼
[Adds result as ChatMessage.Tool to conversation]
   │
   ▼
[LLM sees result and continues reasoning]
```

### 10.3 Common Tool Types

**1. Information Retrieval**
```
Tools:
├─ search_database(query)
├─ get_user_profile(user_id)
├─ fetch_document(doc_id)
└─ query_knowledge_base(question)

Characteristics:
• Read-only
• Fast (<1 second)
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

**Implemented security rules:**

1. **Argument validation:** Before executing, validate schema
2. **Timeout per tool:** Each tool has individual timeout
3. **Rate limiting:** Limit calls per minute/hour
4. **Audit logging:** Record all executions
5. **Permissions:** Tools only available if user has appropriate role

**Example of tool with validation:**
```
Tool: execute_sql_query

Validations:
├─ Query must start with SELECT (read-only)
├─ Cannot contain DROP, DELETE, UPDATE
├─ User must have role "data_analyst"
├─ Timeout: 30 seconds
└─ Rate limit: 10 queries/minute

If validation fails:
→ Returns error without executing
→ Security log records attempt
```

### 10.5 Tool Testing

**Recommended strategies:**

**Unit Testing (Isolated Tools):**
```
Test: get_current_time_returns_valid_format
├─ Mock time system
├─ Execute tool with timezone="UTC"
├─ Assert: ISO 8601 format
└─ Assert: Correct timezone
```

**Integration Testing (Tools with real services):**
```
Test: search_database_integration
├─ Use test database
├─ Insert known data
├─ Execute tool with query
├─ Assert: Expected results
└─ Clean up test data
```

**Mock for development:**
```
MockToolRegistry
├─ Returns predefined responses
├─ Makes no real calls
├─ Simulates realistic latency
└─ Useful for Agent testing without external deps
```

---

## 11. Observability and Control

### 11.1 Observability System

Observability is **guaranteed by design** thanks to the delegate execution model. Every transition between steps mandatorily passes through the pipeline.

**Observability layers:**

`
+---------------------------------------------+
|  LAYER 4: BUSINESS METRICS                  |
|  * Detected intentions                      |
|  * Most used tools                          |
|  * Conversion rate (intent->action)         |
+---------------------------------------------+
         |
+---------------------------------------------+
|  LAYER 3: COGNITIVE METRICS                 |
|  * Tokens used per agent                    |
|  * Cost in USD                              |
|  * Cognitive Retries (self-correction)      |
|  * Tool calls executed                      |
+---------------------------------------------+
         |
+---------------------------------------------+
|  LAYER 2: TECHNICAL METRICS                 |
|  * Latency per step (percentiles)           |
|  * Success/failure rate                     |
|  * Circuit breaker state                    |
|  * Rate limiter available tokens            |
+---------------------------------------------+
         |
+---------------------------------------------+
|  LAYER 1: PIPELINE EVENTS                   |
|  * Step Started/Completed/Failed            |
|  * Tool Completed/Failed                    |
|  * Progress updates                         |
|  * Streaming tokens                         |
+---------------------------------------------+
`

### 11.2 The Three Pillars of Observability

AITaskAgent implements the three standard pillars of modern observability, following industry best practices.

**Pillar 1: Traces (Distributed Tracing)**

**Purpose:** Track execution flow through steps, creating a span hierarchy showing the complete request path.

**Core Interface:**
`
IStepTracer
  -> OnTraceEventAsync(StepTraceEvent)
       |-- StepName: Step identifier
       |-- Status: Started | InProgress | Completed | Failed
       |-- PipelineName: Pipeline name
       |-- StepType: Step type (LlmStep, LambdaStep, etc.)
       |-- CorrelationId: Trace ID for correlation
       |-- ParentStepName: For nested hierarchies
       +-- Attributes: Extensible dictionary (model, tokens, etc.)
`

**Implementations:**
- `ConsoleStepTracer`: Console output for development
- `OpenTelemetryTracingBridge`: Converts to OpenTelemetry Activity (spans)
- Custom: Implement `IStepTracer` for proprietary backends

**Pillar 2: Metrics (Quantitative Telemetry)**

**Purpose:** Collect numerical metrics about execution: duration, tokens, costs, success rates.

**Metric Types:**
`
Counters:
  * step_executions_total
  * step_tokens_total
  * step_retries_total

Histograms (Distributions):
  * step_duration_ms (percentiles: p50, p95, p99)
  * step_cost_usd

Gauges (Instantaneous values):
  * active_steps_count
  * circuit_breaker_state
`

**Pillar 3: Logs (Structured Events)**

**Purpose:** Record discrete events with context for debugging and auditing.

Uses Microsoft.Extensions.Logging with structured logging, scopes for context, and levels (Trace, Debug, Info, Warning, Error, Critical).

**Independent Step Logging (Automatic Scope):**

The framework automatically wraps each `IStep` execution in a logging Scope containing:
- `Step`: Name of the step.
- `Path`: Execution path (e.g., `MainPipeline/Router/SalesPipeline`).
- `CorrelationId`: Unique trace ID.

This allows filtering logs for a specific step even if they occur within injected services or middlewares.

**Filter Example (Serilog):**
```csharp
// Only view logs from the "SchemaValidator" step
.Filter.ByIncludingOnly(le => 
    le.Properties.ContainsKey("Step") && 
    le.Properties["Step"].ToString().Contains("SchemaValidator"))
```

### 11.3 Real-Time Event Streaming (IEventChannel)

**Architecture: System.Threading.Channels**

The framework uses high-performance asynchronous channels to decouple event emission from processing, ensuring observability doesn't impact pipeline latency.

**Event Types (IProgressEvent):**

1. **Step Lifecycle**: `StepStartedEvent`, `StepCompletedEvent`, `StepFailedEvent`
2. **LLM Interaction**: `LlmRequestEvent`, `LlmResponseEvent`
3. **Tools**: `ToolCompletedEvent` (execution details, duration, result)
4. **Streaming Artifacts**: `TagStartedEvent` (start), `TagCompletedEvent` (end).
    > *Note: Artifacts (e.g., xml file writing) are side-effects generated during streaming and do not account for conversation turns.*
5. **Streaming**: `ContentDeltaEvent` (individual tokens for typewriter effect).

**Advantages:**

1. **Non-blocking**: Writing to channel is immediate; processing is background
2. **Backpressure**: Native `System.Threading.Channels` support for handling load spikes
3. **Decoupling**: Producer (Step) doesn't know consumers (UI, Logs)

### 11.4 CorrelationId for Distributed Tracing

**Purpose:** Identify and correlate all operations of a complete execution flow.

**Implementation:**
The framework provides `CorrelationId` automatically in `PipelineContext`. It propagates automatically to logging scope, events, and metrics.

**Benefits:**
1. **Facilitated debugging**: Filter all logs for a specific execution
2. **Complete traceability**: Follow flow from request to response
3. **Distributed tracing**: Compatible with OpenTelemetry and APM tools
4. **HTTP propagation**: Can come from client headers

### 11.5 Metrics and Dashboards

**Recommended KPIs:**

**Performance:**
- Latency P50, P90, P99 per step
- Throughput (requests/second)
- Error rate (%)
- Circuit breaker opens/hour

**Costs:**
- Tokens/request (average, P95)
- Cost/request in USD
- Cost/user/day
- Top 10 users by cost

**Quality:**
- Cognitive Retries (how much LLM self-corrects)
- Validation failure rate
- Tool execution success rate
- User satisfaction score

### 11.6 Recommended Alerts

**Critical (Pagerduty / SMS):**
- Error rate > 5% for 5 minutes
- Circuit breaker OPEN for >2 minutes
- Latency P99 > 30 seconds
- Daily cost > $200 (configurable threshold)

**Warnings (Email / Slack):**
- Error rate > 2% for 15 minutes
- Cognitive Retries > 50% of requests
- Tool failure rate > 10%
- Conversation storage > 90% capacity

---

## 12. Advanced Usage Patterns

### 12.1 Pattern: Intention -> Route -> Action

**Purpose:** Classify user intention and route to specialized pipeline.

**Flow diagram:**
`
User: "I want to create a JSON schema for users"
   |
   v
+------------------------------------+
| IntentionAnalyzerStep              |
| * Classifies: CreateSchema         |
| * Reasoning: "User explicitly..."  |
| * OptimizedPrompt: "Generate..."   |
+------------------------------------+
   |
   v
+------------------------------------+
| SwitchStep<SchemaIntent>           |
| * Reads: Option = CreateSchema     |
| * Route: CreateSchemaPipeline      |
+------------------------------------+
   |
   v
+------------------------------------+
| CreateSchemaPipeline               |
| |-- SchemaGeneratorAgent           |
| |-- SchemaValidatorGuard           |
| +-- SaveToDatabaseAction           |
+------------------------------------+
   |
   v
Response: Valid JSON Schema saved
`

**Advantages:**
- Clear separation of responsibilities
- Easy to add new intentions
- Isolated testing per intention
- Metrics per intention

### 12.2 Pattern: Layered Validation

**Purpose:** Validate at multiple levels before executing expensive action.

Three layers: Syntax (cheap CPU) -> Semantics (medium I/O) -> Business (complex logic).

Shared validation pipeline reused by Chat, Batch, API, and Builder UI flows.

### 12.3 Pattern: Multi-Source RAG

**Purpose:** Query multiple knowledge sources in parallel and combine results.

Four parallel branches (VectorDB Technical, Examples, FAQs, SQL Historical) -> Merge function (rank, deduplicate, filter) -> AnswerGeneratorAgent.

**Advantages:**
- Reduced latency (parallel queries)
- More complete responses (multiple sources)
- Traceability (source of each data)
- Scalable (easily add new sources)

### 12.4 Pattern: Quick Response + Async Processing

**Purpose:** Respond quickly to user while processing long task in background.

Returns 202 Accepted with job_id immediately, background worker processes task and updates progress.

### 12.5 Pattern: Reusable Shared Pipeline

**Purpose:** Define once, use in multiple contexts.

CompilationPipeline reused in ChatFlow, BatchFlow, and CI/CD Integration.

**Advantages:**
- DRY (Don't Repeat Yourself)
- Centralized testing
- Maintenance in one place
- Consistent behavior

### 12.6 Pattern: Context Scoping

**Purpose:** Deliver to sub-agents only necessary information, not entire history.

`ParallelStep` clones and filters `ConversationContext`. Sub-agent sees clean history, saving tokens and reducing hallucinations caused by previous noise.

---

## 13. Implementation Guides

### 13.1 Initial Setup

**NuGet package installation:**
`
dotnet add package AITaskAgent
`

**Startup configuration:**
`
Register services:
|-- AddAITaskAgent(configuration)
|-- AddSingleton<IToolRegistry, ToolRegistry>()
|-- AddScoped<ConversationContext>()
|-- AddSingleton<ISSEChannel, SSEChannel>()
|-- Register custom tools
`

### 13.2 Creating a Basic Agent

**Step 1: Define capabilities (Enum)**
`csharp
public enum DocumentIntent
{
    [Description("User wants to summarize a document")]
    Summarize,
    
    [Description("User wants to extract specific information")]
    ExtractInfo,
    
    [Description("User wants to compare multiple documents")]
    Compare
}
`

**Step 2: Create Action Pipelines** for each intention (Summarize, Extract, Compare).

**Step 3: Build Main Pipeline:**
`
Main Pipeline:
|-- RouterAgentStep<DocumentIntent>
+-- SwitchStep<DocumentIntent>
    |-- Summarize   -> PipelineStep(SummarizePipeline)
    |-- ExtractInfo -> PipelineStep(ExtractionPipeline)
    +-- Compare     -> PipelineStep(ComparePipeline)
`

### 13.3 Best Practices

**Code Organization:**
`
/YourProject
|-- /Agents
|-- /Pipelines
|   |-- /Chat
|   |-- /Batch
|   +-- /Shared
|-- /Steps
|   |-- /Guards
|   +-- /Actions
|-- /Results
|-- /Tools
+-- /Configuration
`

**Configuration per Environment:**
- Development: Fast/cheap model, MaxRetries=1, Verbose logging, Circuit Breaker disabled
- Staging: Production model, MaxRetries=2, Info logging, Low threshold circuit breaker
- Production: Optimized model, MaxRetries=3, Warning+metrics logging, High threshold circuit breaker

**Secrets Management:**
- GOOD: Azure Key Vault, AWS Secrets Manager, Environment variables, User Secrets (local dev)
- BAD: Hardcoded API keys, Strings in committed appsettings.json, Secrets in logs

**Sensitive Logging:**
- DO log: Performance metrics, Errors and exceptions, Pipeline decisions, Critical action audit
- DON'T log: API keys or tokens, Personal data (PII), Passwords, User message content (GDPR)

---

## Conclusion

**AITaskAgent** represents a mature and pragmatic approach to building specialized agents in enterprise environments. Through conscious architectural decisions (ADRs), the framework balances deterministic control with LLM flexibility, guaranteeing observability without sacrificing performance.

**Key principles:**

1. **Strict Hybrid**: Clear separation between probabilistic world (LLM agents) and deterministic (code steps)
2. **Guaranteed Observability**: Impossible to execute a step without leaving an audit trace
3. **Layered Validation**: Structural vs Semantic, with automatic correction
4. **Complete Type Safety**: Compile-time checking prevents runtime errors
5. **Production First**: Designed from day one for critical enterprise systems

The framework doesn't try to be everything for everyone. It's a specialized tool for teams building task-oriented agents where control, auditability, and predictability are non-negotiable requirements.

---

**Document Version**: 4.0 (Consolidated)  
**Last Updated**: January 2026