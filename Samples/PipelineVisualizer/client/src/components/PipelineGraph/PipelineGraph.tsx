import { useEffect, useMemo, useState } from 'react';
import ReactFlow, {
    Node,
    Edge,
    Background,
    Controls,
    MiniMap,
    useNodesState,
    useEdgesState,
    MarkerType,
    Panel,
} from 'reactflow';
import 'reactflow/dist/style.css';
import dagre from 'dagre';
import { PipelineStep, PipelineDefinition } from '../../types';
import { StepNode } from './StepNode';
import { GroupNode } from './GroupNode';
import clsx from 'clsx';
import { Layout, GitFork } from 'lucide-react';

interface PipelineGraphProps {
    pipeline: PipelineDefinition | null;
    activeSteps: string[];
    completedSteps: string[];
    failedSteps?: string[];
}

type LayoutMode = 'horizontal-compact' | 'vertical-grouped';

export function PipelineGraph({ pipeline, activeSteps, completedSteps, failedSteps = [] }: PipelineGraphProps) {
    const [nodes, setNodes, onNodesChange] = useNodesState([]);
    const [edges, setEdges, onEdgesChange] = useEdgesState([]);
    const [layoutMode, setLayoutMode] = useState<LayoutMode>('horizontal-compact');

    const nodeTypes = useMemo(() => ({
        stepNode: StepNode,
        groupNode: GroupNode,
    }), []);

    useEffect(() => {
        if (!pipeline) {
            setNodes([]);
            setEdges([]);
            return;
        }

        const engine = new GraphEngine(pipeline);
        const { nodes: layoutedNodes, edges: layoutedEdges } = engine.computeLayout(layoutMode);

        setNodes(layoutedNodes);
        setEdges(layoutedEdges);
    }, [pipeline, setNodes, setEdges, layoutMode]);

    // Update node states dynamically
    useEffect(() => {
        setNodes((nds) =>
            nds.map((node) => ({
                ...node,
                data: {
                    ...node.data,
                    isActive: activeSteps.includes(node.id),
                    isCompleted: completedSteps.includes(node.id),
                    hasError: failedSteps.includes(node.id),
                },
            }))
        );
    }, [activeSteps, completedSteps, failedSteps, setNodes]);

    // Animate active edges
    useEffect(() => {
        setEdges((eds) =>
            eds.map((edge) => {
                const isActive = activeSteps.includes(edge.source);
                return {
                    ...edge,
                    animated: isActive,
                    style: {
                        ...edge.style,
                        stroke: isActive ? '#3b82f6' : '#94a3b8',
                        strokeWidth: isActive ? 3 : 2,
                    }
                };
            })
        );
    }, [activeSteps, setEdges]);

    if (!pipeline) {
        return (
            <div className="h-full flex items-center justify-center bg-slate-50 text-slate-400">
                <div className="flex flex-col items-center gap-4">
                    <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-500"></div>
                    <p>Loading pipeline...</p>
                </div>
            </div>
        );
    }

    return (
        <div className="h-full w-full bg-slate-50 relative">
            <ReactFlow
                nodes={nodes}
                edges={edges}
                onNodesChange={onNodesChange}
                onEdgesChange={onEdgesChange}
                nodeTypes={nodeTypes}
                fitView
                fitViewOptions={{ padding: 0.2 }}
                minZoom={0.1}
                maxZoom={2}
                defaultEdgeOptions={{ type: 'smoothstep', markerEnd: { type: MarkerType.ArrowClosed } }}
            >
                <Background color="#cbd5e1" gap={24} size={1} />
                <Controls className="bg-white shadow-lg border border-slate-200 rounded-lg p-1" />
                <MiniMap
                    className="border border-slate-200 shadow-lg rounded-lg overflow-hidden"
                    nodeColor={(node) => {
                        if (node.data.hasError) return '#ef4444';
                        if (node.data.isActive) return '#3b82f6';
                        if (node.data.isCompleted) return '#22c55e';
                        return '#e2e8f0';
                    }}
                    maskColor="rgba(240, 248, 255, 0.5)"
                />
                <Panel position="top-right" className="bg-white p-2 rounded-lg shadow-md border border-slate-200 flex gap-2">
                    <button
                        onClick={() => setLayoutMode('horizontal-compact')}
                        className={clsx(
                            "flex items-center gap-2 px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
                            layoutMode === 'horizontal-compact' 
                                ? "bg-blue-100 text-blue-700 border border-blue-200" 
                                : "hover:bg-slate-100 text-slate-600 border border-transparent"
                        )}
                    >
                        <Layout className="w-4 h-4" />
                        Compact
                    </button>
                    <button
                        onClick={() => setLayoutMode('vertical-grouped')}
                        className={clsx(
                            "flex items-center gap-2 px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
                            layoutMode === 'vertical-grouped' 
                                ? "bg-blue-100 text-blue-700 border border-blue-200" 
                                : "hover:bg-slate-100 text-slate-600 border border-transparent"
                        )}
                    >
                        <GitFork className="w-4 h-4 rotate-90" />
                        Vertical Grouped
                    </button>
                </Panel>
            </ReactFlow>
        </div>
    );
}

// ==========================================
// GRAPH ENGINE (DUAL MODE)
// ==========================================

class GraphEngine {
    private pipeline: PipelineDefinition;
    private nodes: Node[] = [];
    private edges: Edge[] = [];
    private layoutMode: LayoutMode = 'horizontal-compact';

    constructor(pipeline: PipelineDefinition) {
        this.pipeline = pipeline;
    }

    public computeLayout(mode: LayoutMode) {
        this.nodes = [];
        this.edges = [];
        this.layoutMode = mode;

        if (mode === 'horizontal-compact') {
            this.processSequenceCompact(this.pipeline.pipeline, []);
        } else {
            const inputId = '__INPUT__';
            this.nodes.push({
                id: inputId,
                type: 'stepNode',
                data: {
                    label: 'User Input',
                    stepType: 'Input',
                    isActive: false,
                    isCompleted: false,
                    layout: mode,
                },
                position: { x: 0, y: 0 },
            });

            this.processSequenceGrouped(this.pipeline.pipeline, undefined, [inputId]);
        }

        return this.applyDagreLayout(this.nodes, this.edges);
    }

    // ==========================================
    // COMPACT MODE LOGIC (Horizontal, Expanded)
    // ==========================================

    private processSequenceCompact(steps: PipelineStep[], previousNodeIds: string[]): string[] {
        let currentPrev = previousNodeIds;
        for (const step of steps) {
            currentPrev = this.processStepCompact(step, currentPrev);
        }
        return currentPrev;
    }

    private processStepCompact(step: PipelineStep, incomingNodeIds: string[], edgeLabel?: string): string[] {
        const isParallel = step.type.startsWith('ParallelStep');
        const hasChildren = step.steps && step.steps.length > 0;
        const isRouter = !!step.routes;

        // Unwrap logical groups (Phases)
        if (hasChildren && !isParallel && !isRouter) {
            return this.processSequenceCompact(step.steps!, incomingNodeIds);
        }

        const nodeId = step.name;
        
        this.nodes.push({
            id: nodeId,
            type: 'stepNode',
            data: {
                label: step.name,
                stepType: step.type,
                stepTypeMetadata: this.pipeline.stepTypes?.[step.type.split('`')[0]],
                isActive: false,
                isCompleted: false,
                layout: this.layoutMode,
            },
            position: { x: 0, y: 0 },
        });

        if (incomingNodeIds.length > 0) {
            incomingNodeIds.forEach(prevId => {
                this.edges.push({
                    id: `${prevId}-${nodeId}`,
                    source: prevId,
                    target: nodeId,
                    label: edgeLabel,
                    type: 'smoothstep',
                    markerEnd: { type: MarkerType.ArrowClosed },
                    style: { stroke: edgeLabel ? '#f59e0b' : '#64748b', strokeWidth: 2 },
                });
            });
        }

        if (isParallel && step.steps) {
            const allBranchTails: string[] = [];
            step.steps.forEach(child => {
                const childTails = this.processStepCompact(child, [nodeId]);
                allBranchTails.push(...childTails);
            });
            return allBranchTails;
        }

        if (isRouter && step.routes) {
            const allRouteTails: string[] = [];
            Object.entries(step.routes).forEach(([routeName, routeStep]) => {
                const routeTails = this.processStepCompact(routeStep, [nodeId], routeName);
                allRouteTails.push(...routeTails);
            });
            return allRouteTails;
        }

        return [nodeId];
    }

    // ==========================================
    // GROUPED MODE LOGIC (Vertical, Compound)
    // ==========================================

    private processSequenceGrouped(steps: PipelineStep[], parentId: string | undefined, previousNodeIds: string[]): string[] {
        let currentPrev = previousNodeIds;
        for (const step of steps) {
            currentPrev = this.processStepGrouped(step, parentId, currentPrev);
        }
        return currentPrev;
    }

    private processStepGrouped(step: PipelineStep, parentId: string | undefined, incomingNodeIds: string[], edgeLabel?: string): string[] {
        const baseType = step.type.split('`')[0];
        const isParallel = baseType === 'ParallelStep' || step.type.startsWith('ParallelStep');
        const isRouter = baseType.endsWith('RouterStep') || !!step.routes;
        const hasChildren = step.steps && step.steps.length > 0;

        if (hasChildren && !isParallel && !isRouter) {
            let currentTails = incomingNodeIds;
            let first = true;
            for (const child of step.steps!) {
                currentTails = this.processStepGrouped(child, parentId, currentTails, first ? edgeLabel : undefined);
                first = false;
            }
            return currentTails;
        }

        const nodeId = step.name;

        if (isParallel) {
            const childrenCount = step.steps?.length ?? 0;
            const childWidth = 180;
            const childHeight = 60;
            const gap = 40;
            const padding = 24;
            const headerSpace = 26;
            const groupWidth = Math.max(220, padding * 2 + childrenCount * childWidth + Math.max(0, childrenCount - 1) * gap);
            const groupHeight = headerSpace + padding + childHeight + padding;

            this.nodes.push({
                id: nodeId,
                type: 'groupNode',
                data: { label: step.name, isActive: false, isCompleted: false, layout: this.layoutMode },
                position: { x: 0, y: 0 },
                parentNode: parentId,
                extent: parentId ? 'parent' : undefined,
                style: { width: groupWidth, height: groupHeight },
            });

            if (step.steps) {
                step.steps.forEach((child, idx) => {
                    const childId = child.name;
                    this.nodes.push({
                        id: childId,
                        type: 'stepNode',
                        data: {
                            label: child.name,
                            stepType: child.type,
                            stepTypeMetadata: this.pipeline.stepTypes?.[child.type.split('`')[0]],
                            isActive: false,
                            isCompleted: false,
                            layout: this.layoutMode,
                        },
                        position: {
                            x: padding + idx * (childWidth + gap),
                            y: headerSpace + padding,
                        },
                        parentNode: nodeId,
                        extent: 'parent',
                    });
                });
            }

            if (incomingNodeIds.length > 0) {
                incomingNodeIds.forEach(prevId => {
                    this.edges.push({
                        id: `${prevId}-${nodeId}`,
                        source: prevId,
                        target: nodeId,
                        type: 'smoothstep',
                        markerEnd: { type: MarkerType.ArrowClosed },
                        style: { stroke: '#94a3b8', strokeWidth: 2 },
                    });
                });
            }

            return [nodeId];
        }

        this.nodes.push({
            id: nodeId,
            type: 'stepNode',
            data: {
                label: step.name,
                stepType: step.type,
                stepTypeMetadata: this.pipeline.stepTypes?.[step.type.split('`')[0]],
                isActive: false,
                isCompleted: false,
                layout: this.layoutMode,
            },
            position: { x: 0, y: 0 },
            parentNode: parentId,
            extent: parentId ? 'parent' : undefined,
        });

        if (incomingNodeIds.length > 0) {
            incomingNodeIds.forEach(prevId => {
                const isRouteEdge = !!edgeLabel;
                this.edges.push({
                    id: `${prevId}-${nodeId}`,
                    source: prevId,
                    target: nodeId,
                    label: edgeLabel,
                    type: 'smoothstep',
                    markerEnd: { type: MarkerType.ArrowClosed },
                    style: isRouteEdge
                        ? {
                            stroke: edgeLabel === 'NewStory' ? '#22c55e' : '#cbd5e1',
                            strokeWidth: 2,
                            strokeDasharray: '6 4',
                        }
                        : { stroke: '#94a3b8', strokeWidth: 2 },
                });
            });
        }

        if (isRouter && step.routes) {
            const allRouteTails: string[] = [];
            Object.entries(step.routes).forEach(([routeName, routeStep]) => {
                const routeTails = this.processStepGrouped(routeStep, parentId, [nodeId], routeName);
                allRouteTails.push(...routeTails);
            });
            return allRouteTails;
        }

        return [nodeId];
    }

    // ==========================================
    // LAYOUT APPLICATION
    // ==========================================

    private applyDagreLayout(nodes: Node[], edges: Edge[]) {
        const isVertical = this.layoutMode === 'vertical-grouped';
        const dagreGraph = new dagre.graphlib.Graph({ compound: false });
        
        dagreGraph.setGraph({ 
            rankdir: isVertical ? 'TB' : 'LR', 
            align: 'UL',
            nodesep: isVertical ? 50 : 30, 
            ranksep: isVertical ? 80 : 50, 
            marginx: 30, 
            marginy: 30,
            ranker: 'network-simplex'
        });

        dagreGraph.setDefaultEdgeLabel(() => ({}));

        const layoutNodes = isVertical ? nodes.filter(n => !n.parentNode) : nodes;
        const layoutNodeIds = new Set(layoutNodes.map(n => n.id));
        const layoutEdges = isVertical ? edges.filter(e => layoutNodeIds.has(e.source) && layoutNodeIds.has(e.target)) : edges;

        layoutNodes.forEach(node => {
            let width = 180;
            let height = 60;
            
            if (node.type === 'groupNode') {
                const styleWidth = typeof node.style === 'object' && node.style ? (node.style as any).width : undefined;
                const styleHeight = typeof node.style === 'object' && node.style ? (node.style as any).height : undefined;
                width = typeof styleWidth === 'number' ? styleWidth : 260;
                height = typeof styleHeight === 'number' ? styleHeight : 160;
            }

            dagreGraph.setNode(node.id, { width, height });
        });

        layoutEdges.forEach(edge => {
            dagreGraph.setEdge(edge.source, edge.target);
        });

        try {
            dagre.layout(dagreGraph);
        } catch (err) {
            console.error("Layout Error", err);
        }

        const finalNodes = nodes.map(node => {
            if (isVertical && node.parentNode) {
                return node;
            }
            const nodeWithPosition = dagreGraph.node(node.id);
            if (!nodeWithPosition) return node;

            const style = { ...node.style };
            if (node.type === 'groupNode') {
                style.width = nodeWithPosition.width;
                style.height = nodeWithPosition.height;
            }

            let x = nodeWithPosition.x - nodeWithPosition.width / 2;
            let y = nodeWithPosition.y - nodeWithPosition.height / 2;

            return {
                ...node,
                style,
                position: { x, y },
            };
        });

        return { nodes: finalNodes, edges };
    }
}
