import { useEffect, useRef, useState, useCallback } from 'react';
import { SseClient } from '../services/sseClient';
import { SseEvent, ContextSnapshot, PipelineStatus } from '../types';

// ... imports

interface UseSseReturn {
    events: SseEvent[];
    connected: boolean;
    status: PipelineStatus;
    activeSteps: string[];
    completedSteps: string[];
    failedSteps: string[];
    context?: ContextSnapshot;
    clearEvents: () => void;
}

/**
 * Hook for managing SSE connection and event state.
 */
export function useSse(baseUrl: string = import.meta.env.VITE_API_URL || ''): UseSseReturn {
    const [events, setEvents] = useState<SseEvent[]>([]);
    const [connected, setConnected] = useState(false);
    const [status, setStatus] = useState<PipelineStatus>('idle');
    const [activeSteps, setActiveSteps] = useState<Set<string>>(new Set());
    const [completedSteps, setCompletedSteps] = useState<string[]>([]);
    const [failedSteps, setFailedSteps] = useState<Set<string>>(new Set());
    const [context, setContext] = useState<ContextSnapshot | undefined>();

    const clientRef = useRef<SseClient | null>(null);

    const handleEvent = useCallback((event: SseEvent) => {
        console.log('SSE Event:', event);
        setEvents((prev) => [...prev, event]);

        if (event.type === 'pipeline') {
            switch (event.eventType) {
                case 'pipeline.started':
                    setStatus('running');
                    setCompletedSteps([]);
                    setActiveSteps(new Set());
                    setFailedSteps(new Set());
                    break;
                case 'pipeline.completed':
                    setStatus('completed');
                    setActiveSteps(new Set());
                    break;
                case 'step.started':
                    setActiveSteps(prev => {
                        const next = new Set(prev);
                        next.add(event.stepName);
                        return next;
                    });
                    break;
                case 'step.completed':
                    setActiveSteps(prev => {
                        const next = new Set(prev);
                        next.delete(event.stepName);
                        return next;
                    });
                    setCompletedSteps((prev) => [...prev, event.stepName]);

                    // Check for error in result using PascalCase 'Success' property
                    const data = event.data as any;
                    // Check explicitly for Success === false (PascalCase based on user input)
                    if (data && data.Success === false) {
                        setFailedSteps(prev => {
                            const next = new Set(prev);
                            next.add(event.stepName);
                            return next;
                        });
                    }
                    break;
                case 'context.snapshot':
                    setContext(event.data as unknown as ContextSnapshot);
                    break;
            }
        }
    }, []);

    const clearEvents = useCallback(() => {
        setEvents([]);
        setStatus('idle');
        setActiveSteps(new Set());
        setCompletedSteps([]);
        setFailedSteps(new Set());
        setContext(undefined);
    }, []);

    useEffect(() => {
        clientRef.current = new SseClient(baseUrl, handleEvent, setConnected);
        clientRef.current.connect();

        return () => {
            clientRef.current?.disconnect();
        };
    }, [baseUrl, handleEvent]);

    return {
        events,
        connected,
        status,
        activeSteps: Array.from(activeSteps),
        completedSteps,
        failedSteps: Array.from(failedSteps),
        context,
        clearEvents,
    };
}
