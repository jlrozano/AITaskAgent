import { memo } from 'react';
import { Handle, Position, NodeProps } from 'reactflow';
import { clsx } from 'clsx';
import { Layers, Loader2, CheckCircle } from 'lucide-react';

interface GroupNodeData {
    label: string;
    isActive: boolean;
    isCompleted: boolean;
    layout?: 'horizontal-compact' | 'vertical-grouped';
}

/**
 * Custom node component for Group/Parallel steps.
 * Renders as a container with a header and handles.
 */
export const GroupNode = memo(({ data }: NodeProps<GroupNodeData>) => {
    const isVertical = data.layout === 'vertical-grouped';
    const targetHandlePosition = isVertical ? Position.Top : Position.Left;
    const sourceHandlePosition = isVertical ? Position.Bottom : Position.Right;
    // style prop is handled by wrapper if needed, but not passed here by default in v11?

    return (
        <div className="h-full w-full relative">
            <div
                className={clsx(
                    'absolute inset-0 rounded-xl border-2 transition-all duration-300',
                    'pointer-events-none', // Allow clicking through to children if needed, but handles need events
                    {
                        'border-blue-400 bg-blue-50/30 ring-4 ring-blue-400 ring-opacity-20': data.isActive,
                        'border-green-500 bg-green-50/30': data.isCompleted,
                        'border-slate-300 bg-slate-50/30': !data.isActive && !data.isCompleted,
                    }
                )}
            >
                {/* Header Badge */}
                <div className={clsx(
                    'absolute -top-3 left-4 px-2 py-0.5 rounded-full border shadow-sm text-xs font-semibold flex items-center gap-1 z-10',
                    data.isActive ? 'bg-blue-100 border-blue-300 text-blue-700' :
                        data.isCompleted ? 'bg-green-100 border-green-300 text-green-700' :
                            'bg-white border-slate-200 text-slate-600'
                )}>
                    {data.isActive ? <Loader2 className="w-3 h-3 animate-spin" /> :
                        data.isCompleted ? <CheckCircle className="w-3 h-3" /> :
                            <Layers className="w-3 h-3" />}
                    {data.label}
                </div>
            </div>

            {/* Input Handle */}
            <Handle
                type="target"
                position={targetHandlePosition}
                className="!bg-slate-400 !w-3 !h-3 -ml-1.5 z-20"
            />

            {/* Output Handle */}
            <Handle
                type="source"
                position={sourceHandlePosition}
                className="!bg-slate-400 !w-3 !h-3 -mr-1.5 z-20"
            />
        </div>
    );
});

GroupNode.displayName = 'GroupNode';
