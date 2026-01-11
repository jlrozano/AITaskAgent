// ========================================
// Pipeline Types
// ========================================

export interface PipelineDefinition {
    name: string;
    description: string;
    version: string;
    intentions: Record<string, { description: string }>;
    pipeline: PipelineStep[];
    stepTypes: Record<string, StepTypeInfo>;
    profiles: Record<string, { description: string }>;
}

export interface PipelineStep {
    name: string;
    type: string;
    inputType?: string;
    outputType?: string;
    config?: Record<string, unknown>;
    steps?: PipelineStep[];
    routes?: Record<string, PipelineStep>;
    defaultRoute?: string;
}

export interface StepTypeInfo {
    category: 'llm' | 'control-flow' | 'container' | 'utility';
    description: string;
    icon: string;
    color: string;
}

// ========================================
// Event Types (matching C# events)
// ========================================

export type EventType =
    | 'step.started'
    | 'step.completed'
    | 'step.progress'
    | 'step.validation'
    | 'step.routing'
    | 'pipeline.started'
    | 'pipeline.completed'
    | 'llm.response'
    | 'tool.started'
    | 'tool.completed'
    | 'context.snapshot';

export interface PipelineEvent {
    type: 'pipeline';
    eventType: EventType;
    stepName: string;
    correlationId: string;
    timestamp: string;
    data: Record<string, unknown>;
}

export interface LogEvent {
    type: 'log';
    level: 'Debug' | 'Information' | 'Warning' | 'Error' | 'Fatal';
    timestamp: string;
    message: string;
    properties: Record<string, string>;
}

export type SseEvent = PipelineEvent | LogEvent;

// ========================================
// Context Types
// ========================================

export interface ContextSnapshot {
    stepResults: Record<string, unknown>;
    metadata: Record<string, unknown>;
    currentPath: string;
}

// ========================================
// Chat Types
// ========================================

export interface ChatMessage {
    id: string;
    role: 'user' | 'assistant' | 'system';
    content: string;
    timestamp: Date;
    correlationId?: string;
}

export interface ChatRequest {
    message: string;
    conversationId?: string | null;
}

export interface ChatResponse {
    correlationId: string;
    conversationId: string;
    content?: string;
    error?: string;
    success: boolean;
}

// ========================================
// UI State Types
// ========================================

export type PipelineStatus = 'idle' | 'running' | 'completed' | 'error';

export interface AppState {
    status: PipelineStatus;
    currentStep?: string;
    completedSteps: string[];
    context?: ContextSnapshot;
    messages: ChatMessage[];
    events: SseEvent[];
}
