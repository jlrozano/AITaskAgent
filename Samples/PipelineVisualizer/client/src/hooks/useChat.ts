import { useState, useCallback } from 'react';
import { apiClient } from '../services/apiClient';
import { ChatMessage } from '../types';

interface UseChatReturn {
    messages: ChatMessage[];
    isProcessing: boolean;
    error?: string;
    sendMessage: (content: string) => Promise<void>;
    clearMessages: () => void;
}

/**
 * Hook for managing chat state and API communication.
 */
export function useChat(): UseChatReturn {
    const [messages, setMessages] = useState<ChatMessage[]>([]);
    const [conversationId, setConversationId] = useState<string | null>(null);
    const [isProcessing, setIsProcessing] = useState(false);
    const [error, setError] = useState<string | undefined>();

    const sendMessage = useCallback(async (content: string) => {
        if (!content.trim() || isProcessing) return;

        setError(undefined);

        // Add user message immediately
        const userMessage: ChatMessage = {
            id: crypto.randomUUID(),
            role: 'user',
            content: content.trim(),
            timestamp: new Date(),
        };
        setMessages((prev) => [...prev, userMessage]);
        setIsProcessing(true);

        try {
            const response = await apiClient.sendMessage(content, conversationId);

            // Update conversation ID if provided
            if (response.conversationId) {
                setConversationId(response.conversationId);
            }

            // Add assistant response
            const assistantMessage: ChatMessage = {
                id: crypto.randomUUID(),
                role: 'assistant',
                content: response.content || response.error || 'No response',
                timestamp: new Date(),
                correlationId: response.correlationId,
            };
            setMessages((prev) => [...prev, assistantMessage]);

            if (!response.success) {
                setError(response.error);
            }
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Unknown error';
            setError(errorMessage);

            // Add error message to chat
            const errorChatMessage: ChatMessage = {
                id: crypto.randomUUID(),
                role: 'system',
                content: `Error: ${errorMessage}`,
                timestamp: new Date(),
            };
            setMessages((prev) => [...prev, errorChatMessage]);
        } finally {
            setIsProcessing(false);
        }
    }, [isProcessing, conversationId]);

    const clearMessages = useCallback(() => {
        setMessages([]);
        setError(undefined);
    }, []);

    return {
        messages,
        isProcessing,
        error,
        sendMessage,
        clearMessages,
    };
}
