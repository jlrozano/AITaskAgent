import { ChatRequest, ChatResponse, PipelineDefinition } from '../types';

const API_BASE = import.meta.env.VITE_API_URL || '';

/**
 * API client for backend endpoints.
 */
export const apiClient = {
    /**
     * Fetches the pipeline definition.
     */
    async getPipeline(): Promise<PipelineDefinition> {
        const response = await fetch(`${API_BASE}/api/pipeline`);
        if (!response.ok) {
            throw new Error(`Failed to fetch pipeline: ${response.statusText}`);
        }
        return response.json();
    },

    /**
     * Sends a chat message to execute the pipeline.
     */
    async sendMessage(message: string, conversationId?: string | null): Promise<ChatResponse> {
        const request: ChatRequest = { message, conversationId };

        const response = await fetch(`${API_BASE}/api/pipelines/execute`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(request),
        });

        const data = await response.json();
        return {
            ...data,
            success: response.ok,
        };
    },

    /**
     * Checks backend health.
     */
    async healthCheck(): Promise<boolean> {
        try {
            const response = await fetch(`${API_BASE}/api/health`);
            return response.ok;
        } catch {
            return false;
        }
    },
};
