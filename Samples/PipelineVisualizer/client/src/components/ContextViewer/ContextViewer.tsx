import { useState } from 'react';
import { ContextSnapshot } from '../../types';
import { Database, ChevronDown, ChevronRight } from 'lucide-react';

interface ContextViewerProps {
    context?: ContextSnapshot;
}

interface ContextEntry {
    timestamp: number;
    path: string;
    data: ContextSnapshot;
}

// Store accumulated context entries
const contextHistory: ContextEntry[] = [];

/**
 * Displays accumulated PipelineContext snapshots with collapsible boxes.
 */
export function ContextViewer({ context }: ContextViewerProps) {
    // Add new context to history
    if (context && !contextHistory.some(entry =>
        entry.timestamp === Date.now() && entry.path === context.currentPath
    )) {
        contextHistory.push({
            timestamp: Date.now(),
            path: context.currentPath || 'unknown',
            data: context
        });
    }

    if (contextHistory.length === 0) {
        return (
            <div className="h-full flex items-center justify-center bg-gray-50 text-gray-400 text-sm">
                No context snapshots yet
            </div>
        );
    }

    return (
        <div className="h-full flex flex-col bg-white overflow-hidden">
            <div className="px-4 py-2 bg-gray-50 border-b flex items-center gap-2">
                <Database className="w-4 h-4 text-blue-500" />
                <span className="text-sm font-medium text-gray-700">Context Snapshots</span>
                <span className="ml-auto text-xs text-gray-500">
                    {contextHistory.length} snapshots
                </span>
            </div>

            <div className="flex-1 overflow-y-auto p-2 space-y-2">
                {contextHistory.map((entry, index) => (
                    <ContextBox key={index} entry={entry} index={index} />
                ))}
            </div>
        </div>
    );
}

interface ContextBoxProps {
    entry: ContextEntry;
    index: number;
}

function ContextBox({ entry, index }: ContextBoxProps) {
    const [isExpanded, setIsExpanded] = useState(false);
    const time = new Date(entry.timestamp).toLocaleTimeString();

    return (
        <div className="border border-gray-200 rounded-lg overflow-hidden bg-white shadow-sm">
            {/* Header */}
            <button
                onClick={() => setIsExpanded(!isExpanded)}
                className="w-full px-3 py-2 bg-gray-50 hover:bg-gray-100 transition-colors flex items-center gap-2 text-left"
            >
                {isExpanded ? (
                    <ChevronDown className="w-4 h-4 text-gray-500 shrink-0" />
                ) : (
                    <ChevronRight className="w-4 h-4 text-gray-500 shrink-0" />
                )}
                <div className="flex-1 min-w-0">
                    <div className="text-xs font-medium text-gray-700 truncate">
                        #{index + 1} - {entry.path}
                    </div>
                    <div className="text-xs text-gray-500">
                        {time}
                    </div>
                </div>
            </button>

            {/* Expanded Content */}
            {isExpanded && (
                <div className="p-3 bg-slate-50">
                    <pre className="text-xs font-mono text-gray-700 overflow-x-auto whitespace-pre-wrap break-words">
                        {JSON.stringify(entry.data, null, 2)}
                    </pre>
                </div>
            )}
        </div>
    );
}
