import { SseEvent } from '../types';

type EventHandler = (event: SseEvent) => void;
type ConnectionHandler = (connected: boolean) => void;

/**
 * SSE client for connecting to the backend event stream.
 * Handles reconnection and event parsing.
 */
export class SseClient {
    private eventSource: EventSource | null = null;
    private onEvent: EventHandler;
    private onConnection: ConnectionHandler;
    private reconnectAttempts = 0;
    private readonly maxReconnectAttempts = 5;
    private readonly baseUrl: string;

    constructor(
        baseUrl: string,
        onEvent: EventHandler,
        onConnection: ConnectionHandler
    ) {
        this.baseUrl = baseUrl;
        this.onEvent = onEvent;
        this.onConnection = onConnection;
    }

    connect(): void {
        if (this.eventSource) {
            this.disconnect();
        }

        this.eventSource = new EventSource(`${this.baseUrl}/api/events`);

        this.eventSource.onopen = () => {
            this.reconnectAttempts = 0;
            this.onConnection(true);
            console.log('[SSE] Connected');
        };

        this.eventSource.onmessage = (event) => {
            try {
                const parsed = JSON.parse(event.data) as SseEvent;
                this.onEvent(parsed);
            } catch (error) {
                console.error('[SSE] Failed to parse event:', error);
            }
        };

        this.eventSource.onerror = () => {
            console.error('[SSE] Connection error');
            this.onConnection(false);
            this.scheduleReconnect();
        };
    }

    disconnect(): void {
        if (this.eventSource) {
            this.eventSource.close();
            this.eventSource = null;
            this.onConnection(false);
            console.log('[SSE] Disconnected');
        }
    }

    private scheduleReconnect(): void {
        if (this.reconnectAttempts >= this.maxReconnectAttempts) {
            console.error('[SSE] Max reconnection attempts reached');
            return;
        }

        const delay = Math.min(1000 * Math.pow(2, this.reconnectAttempts), 30000);
        this.reconnectAttempts++;

        console.log(`[SSE] Reconnecting in ${delay}ms (attempt ${this.reconnectAttempts})`);
        setTimeout(() => this.connect(), delay);
    }
}
