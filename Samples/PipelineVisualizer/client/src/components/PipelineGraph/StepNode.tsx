import { memo } from 'react';
import { Handle, Position, NodeProps } from 'reactflow';
import { clsx } from 'clsx';
import {
    Brain,
    GitBranch,
    Folder,
    Layers,
    MessageSquare,
    Edit3,
    Code,
    CheckCircle,
    XCircle,
    Loader2,
    BrainCircuit,
    GitFork,
    Box,
    Columns,
    PenTool
} from 'lucide-react';

interface StepNodeData {
    label: string;
    stepType: string;
    stepTypeMetadata?: {
        icon: string;
        color: string;
        borderColor?: string;
        backgroundColor?: string;
        description: string;
        category: string;
    };
    routeLabel?: string;
    isActive: boolean;
    isCompleted: boolean;
    hasError?: boolean;
    layout?: 'horizontal-compact' | 'vertical-grouped';
}

// Icon mapping
const iconMap: Record<string, React.ReactNode> = {
    BrainCircuit: <BrainCircuit className="w-4 h-4" />,
    Brain: <Brain className="w-4 h-4" />,
    GitFork: <GitFork className="w-4 h-4" />,
    GitBranch: <GitBranch className="w-4 h-4" />,
    Box: <Box className="w-4 h-4" />,
    Folder: <Folder className="w-4 h-4" />,
    Columns: <Columns className="w-4 h-4" />,
    Layers: <Layers className="w-4 h-4" />,
    MessageSquare: <MessageSquare className="w-4 h-4" />,
    PenTool: <PenTool className="w-4 h-4" />,
    Edit3: <Edit3 className="w-4 h-4" />,
    Code: <Code className="w-4 h-4" />,
};

// Fallback hardcoded values (used if backend doesn't provide metadata)
const stepIcons: Record<string, React.ReactNode> = {
    IntentionAnalyzerStep: <Brain className="w-4 h-4" />,
    IntentionRouterStep: <GitBranch className="w-4 h-4" />,
    GroupStep: <Folder className="w-4 h-4" />,
    ParallelStep: <Layers className="w-4 h-4" />,
    StatelessTemplateLlmStep: <MessageSquare className="w-4 h-4" />,
    StatelessRewriterStep: <Edit3 className="w-4 h-4" />,
    DelegatedStep: <Code className="w-4 h-4" />,
};

const stepColors: Record<string, string> = {
    IntentionAnalyzerStep: 'border-purple-500 bg-purple-50',
    IntentionRouterStep: 'border-amber-500 bg-amber-50',
    GroupStep: 'border-slate-400 bg-slate-50',
    ParallelStep: 'border-emerald-500 bg-emerald-50',
    StatelessTemplateLlmStep: 'border-blue-500 bg-blue-50',
    StatelessRewriterStep: 'border-pink-500 bg-pink-50',
    DelegatedStep: 'border-slate-400 bg-slate-50',
};

/**
 * Custom node component for pipeline steps.
 */
export const StepNode = memo(({ data }: NodeProps<StepNodeData>) => {
    const baseType = data.stepType.split('`')[0];
    const isVertical = data.layout === 'vertical-grouped';
    const targetHandlePosition = isVertical ? Position.Top : Position.Left;
    const sourceHandlePosition = isVertical ? Position.Bottom : Position.Right;

    // Use metadata from backend if available, otherwise fallback to hardcoded
    const icon = data.stepTypeMetadata?.icon
        ? (iconMap[data.stepTypeMetadata.icon] || stepIcons[baseType] || <Code className="w-4 h-4" />)
        : (stepIcons[baseType] || <Code className="w-4 h-4" />);

    // Determine if using hex colors or Tailwind classes
    const useInlineStyles = data.stepTypeMetadata?.borderColor?.startsWith('#');
    const borderColor = data.stepTypeMetadata?.borderColor || '#94A3B8';
    const backgroundColor = data.stepTypeMetadata?.backgroundColor || '#F8FAFC';
    const fallbackClass = stepColors[baseType] || 'border-slate-400 bg-slate-50';


    return (
        <div
            className={clsx(
                'px-3 py-2 rounded-xl border-2 min-w-[80px] shadow-sm transition-all duration-300',
                {
                    'ring-4 ring-blue-400 ring-opacity-50 scale-105': data.isActive,
                    'border-red-500 bg-red-50': data.hasError && !useInlineStyles,
                    'border-green-500 bg-green-50': data.isCompleted && !data.hasError && !useInlineStyles,
                    'opacity-60': !data.isActive && !data.isCompleted && !data.hasError,
                },
                !useInlineStyles && fallbackClass
            )}
            style={useInlineStyles ? {
                borderColor: data.hasError ? '#EF4444' : (data.isCompleted ? '#22C55E' : borderColor),
                backgroundColor: data.hasError ? '#FEF2F2' : (data.isCompleted ? '#F0FDF4' : backgroundColor),
            } : undefined}
        >
            <Handle type="target" position={targetHandlePosition} className="!bg-slate-400" />

            <div className="flex items-center gap-2">
                <div className={clsx(
                    'w-8 h-8 rounded-lg flex items-center justify-center',
                    data.hasError ? 'bg-red-500 text-white' :
                        data.isActive ? 'bg-blue-500 text-white' :
                            data.isCompleted ? 'bg-green-500 text-white' :
                                'bg-white text-slate-600'
                )}>
                    {data.hasError ? (
                        <XCircle className="w-4 h-4" />
                    ) : data.isActive ? (
                        <Loader2 className="w-4 h-4 animate-spin" />
                    ) : data.isCompleted ? (
                        <CheckCircle className="w-4 h-4" />
                    ) : (
                        icon
                    )}
                </div>

                <div className="flex-1">
                    <div className="text-sm font-semibold text-slate-800">
                        {data.label}
                    </div>
                    {data.routeLabel && (
                        <div className="text-xs text-slate-500 mt-0.5">
                            {data.routeLabel}
                        </div>
                    )}
                </div>
            </div>

            <Handle type="source" position={sourceHandlePosition} className="!bg-slate-400" />
        </div>
    );
});

StepNode.displayName = 'StepNode';
