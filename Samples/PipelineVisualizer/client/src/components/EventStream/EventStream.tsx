import { Virtuoso } from 'react-virtuoso';
import { SseEvent, LogEvent, PipelineEvent } from '../../types';
import {
    Activity,
    AlertCircle,
    CheckCircle,
    Info,
    PlayCircle,
    AlertTriangle,
    Zap,
    MessageSquare
} from 'lucide-react';

interface EventStreamProps {
    events: SseEvent[];
    maxHeight?: string;
}

/**
 * Virtualized event stream viewer for logs and pipeline events.
 */
export function EventStream({ events, maxHeight = '100%' }: EventStreamProps) {
    return (
        <div className="h-full flex flex-col bg-gray-900 text-gray-100 rounded-lg overflow-hidden">
            <div className="px-4 py-2 bg-gray-800 border-b border-gray-700 flex items-center gap-2">
                <Activity className="w-4 h-4 text-green-400" />
                <span className="text-sm font-medium">Event Stream</span>
                <span className="ml-auto text-xs text-gray-400">{events.length} events</span>
            </div>

            <div className="flex-1 overflow-hidden" style={{ maxHeight }}>
                <Virtuoso
                    className="h-full"
                    data={events}
                    followOutput="smooth"
                    initialTopMostItemIndex={events.length - 1}
                    itemContent={(_, event) => <EventItem event={event} />}
                />
            </div>
        </div>
    );
}

function EventItem({ event }: { event: SseEvent }) {
    if (event.type === 'log') {
        return <LogEventItem event={event} />;
    }
    return <PipelineEventItem event={event} />;
}

function LogEventItem({ event }: { event: LogEvent }) {
    const levelColors: Record<string, string> = {
        Debug: 'text-gray-400',
        Information: 'text-blue-400',
        Warning: 'text-yellow-400',
        Error: 'text-red-400',
        Fatal: 'text-red-600',
    };

    const levelIcons: Record<string, React.ReactNode> = {
        Debug: <Info className="w-3 h-3" />,
        Information: <Info className="w-3 h-3" />,
        Warning: <AlertTriangle className="w-3 h-3" />,
        Error: <AlertCircle className="w-3 h-3" />,
        Fatal: <AlertCircle className="w-3 h-3" />,
    };

    return (
        <div className="px-3 py-1.5 border-b border-gray-800 hover:bg-gray-800/50 text-xs font-mono">
            <div className="flex items-start gap-2">
                <span className={levelColors[event.level]}>
                    {levelIcons[event.level]}
                </span>
                <span className="text-gray-500 shrink-0">
                    {new Date(event.timestamp).toLocaleTimeString()}
                </span>
                <span className={`shrink-0 ${levelColors[event.level]}`}>
                    [{event.level.substring(0, 3).toUpperCase()}]
                </span>
                <span className="text-gray-300 break-all">{event.message}</span>
            </div>
        </div>
    );
}

function PipelineEventItem({ event }: { event: PipelineEvent }) {
    const eventStyles: Record<string, { icon: React.ReactNode; color: string }> = {
        'step.started': { icon: <PlayCircle className="w-3 h-3" />, color: 'text-blue-400' },
        'step.completed': { icon: <CheckCircle className="w-3 h-3" />, color: 'text-green-400' },
        'step.routing': { icon: <Zap className="w-3 h-3" />, color: 'text-yellow-400' },
        'llm.response': { icon: <MessageSquare className="w-3 h-3" />, color: 'text-purple-400' },
        'pipeline.started': { icon: <PlayCircle className="w-3 h-3" />, color: 'text-emerald-400' },
        'pipeline.completed': { icon: <CheckCircle className="w-3 h-3" />, color: 'text-emerald-400' },
        'context.snapshot': { icon: <Activity className="w-3 h-3" />, color: 'text-cyan-400' },
    };

    const style = eventStyles[event.eventType] || { icon: <Info className="w-3 h-3" />, color: 'text-gray-400' };

    return (
        <div className="px-3 py-1.5 border-b border-gray-800 hover:bg-gray-800/50 text-xs font-mono">
            <div className="flex items-start gap-2">
                <span className={style.color}>{style.icon}</span>
                <span className="text-gray-500 shrink-0">
                    {new Date(event.timestamp).toLocaleTimeString()}
                </span>
                <span className={`shrink-0 ${style.color}`}>
                    [{event.eventType}]
                </span>
                {event.stepName && (
                    <span className="text-amber-400 shrink-0">{event.stepName}</span>
                )}
            </div>
        </div>
    );
}
