import { PipelineStatus } from '../../types';
import { Activity, Wifi, WifiOff, CheckCircle, AlertCircle, Loader2, ChevronDown } from 'lucide-react';

interface Pipeline {
    name: string;
    description: string;
    version: string;
}

interface HeaderProps {
    status: PipelineStatus;
    connected: boolean;
    eventsCount: number;
    stepsCompleted: number;
    pipelines: Pipeline[];
    selectedPipeline: string | null;
    onPipelineChange: (name: string) => void;
}

/**
 * Application header with status indicators and metrics.
 */
export function Header({
    status,
    connected,
    eventsCount,
    stepsCompleted,
    pipelines,
    selectedPipeline,
    onPipelineChange
}: HeaderProps) {
    return (
        <header className="h-14 bg-gradient-to-r from-slate-900 to-slate-800 border-b border-slate-700 px-6 flex items-center justify-between shadow-lg">
            <div className="flex items-center gap-3">
                <div className="w-9 h-9 bg-gradient-to-br from-blue-500 to-purple-600 rounded-lg flex items-center justify-center text-white font-bold shadow-md">
                    <Activity className="w-5 h-5" />
                </div>
                <div>
                    <h1 className="text-lg font-bold text-white">Pipeline Visualizer</h1>
                    <p className="text-xs text-slate-400">Real-time observability</p>
                </div>

                {/* Pipeline Selector */}
                {pipelines.length > 0 && (
                    <div className="ml-6 relative">
                        <select
                            value={selectedPipeline || ''}
                            onChange={(e) => onPipelineChange(e.target.value)}
                            className="appearance-none bg-slate-700/50 text-white text-sm px-4 py-2 pr-10 rounded-lg border border-slate-600 hover:bg-slate-700 focus:outline-none focus:ring-2 focus:ring-blue-500 cursor-pointer transition-colors"
                        >
                            {pipelines.map((pipeline) => (
                                <option key={pipeline.name} value={pipeline.name}>
                                    {pipeline.name} (v{pipeline.version})
                                </option>
                            ))}
                        </select>
                        <ChevronDown className="absolute right-3 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400 pointer-events-none" />
                    </div>
                )}
            </div>

            <div className="flex items-center gap-6">
                {/* Connection Status */}
                <StatusBadge
                    connected={connected}
                />

                {/* Pipeline Status */}
                <PipelineStatusBadge status={status} />

                {/* Metrics */}
                <div className="flex gap-4 text-xs">
                    <MetricBadge label="Steps" value={stepsCompleted} color="blue" />
                    <MetricBadge label="Events" value={eventsCount} color="purple" />
                </div>
            </div>
        </header>
    );
}

function StatusBadge({ connected }: { connected: boolean }) {
    return (
        <div className={`flex items-center gap-2 px-3 py-1.5 rounded-full ${connected
            ? 'bg-green-500/20 text-green-400'
            : 'bg-red-500/20 text-red-400'
            }`}>
            {connected ? (
                <Wifi className="w-3.5 h-3.5" />
            ) : (
                <WifiOff className="w-3.5 h-3.5" />
            )}
            <span className="text-xs font-medium">
                {connected ? 'Connected' : 'Disconnected'}
            </span>
        </div>
    );
}

function PipelineStatusBadge({ status }: { status: PipelineStatus }) {
    const statusConfig: Record<PipelineStatus, { icon: React.ReactNode; color: string; label: string }> = {
        idle: { icon: <Activity className="w-3.5 h-3.5" />, color: 'bg-slate-500/20 text-slate-400', label: 'Idle' },
        running: { icon: <Loader2 className="w-3.5 h-3.5 animate-spin" />, color: 'bg-blue-500/20 text-blue-400', label: 'Running' },
        completed: { icon: <CheckCircle className="w-3.5 h-3.5" />, color: 'bg-green-500/20 text-green-400', label: 'Completed' },
        error: { icon: <AlertCircle className="w-3.5 h-3.5" />, color: 'bg-red-500/20 text-red-400', label: 'Error' },
    };

    const config = statusConfig[status];

    return (
        <div className={`flex items-center gap-2 px-3 py-1.5 rounded-full ${config.color}`}>
            {config.icon}
            <span className="text-xs font-medium">{config.label}</span>
        </div>
    );
}

function MetricBadge({ label, value, color }: { label: string; value: number; color: 'blue' | 'purple' }) {
    const colors = {
        blue: 'text-blue-400',
        purple: 'text-purple-400',
    };

    return (
        <div className="flex flex-col items-center">
            <span className={`text-lg font-bold ${colors[color]}`}>{value}</span>
            <span className="text-slate-500">{label}</span>
        </div>
    );
}
