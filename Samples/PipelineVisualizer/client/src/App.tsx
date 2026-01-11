import { useState } from 'react';
import { useSse, useChat } from './hooks';
import { usePipelines, usePipelineSchema } from './hooks/usePipelines';
import { Header, PipelineGraph, ContextViewer, Chat, EventStream } from './components';

/**
 * Main application component.
 * Orchestrates SSE connection, chat, and pipeline visualization.
 */
function App() {
    const { pipelines } = usePipelines();
    const [selectedPipeline, setSelectedPipeline] = useState<string | null>(null);

    // Auto-select first pipeline when loaded
    if (pipelines.length > 0 && !selectedPipeline) {
        setSelectedPipeline(pipelines[0].name);
    }

    const { schema: pipeline } = usePipelineSchema(selectedPipeline);
    const { events, connected, status, activeSteps, completedSteps, failedSteps, context, clearEvents } = useSse();
    const { messages, isProcessing, sendMessage } = useChat();

    const handleSendMessage = (message: string) => {
        clearEvents(); // Clear previous events
        sendMessage(message);
    };

    return (
        <div className="flex flex-col h-screen bg-slate-100">
            {/* Header */}
            <Header
                status={status}
                connected={connected}
                eventsCount={events.length}
                stepsCompleted={completedSteps.length}
                pipelines={pipelines}
                selectedPipeline={selectedPipeline}
                onPipelineChange={setSelectedPipeline}
            />

            {/* Main Content - 2x2 Grid */}
            <div className="flex-1 grid grid-cols-[1fr_400px] grid-rows-[1fr_300px] overflow-hidden">
                {/* Top Left: Pipeline Graph */}
                <div className="bg-slate-100 border-r border-b border-slate-200">
                    <PipelineGraph
                        pipeline={pipeline}
                        activeSteps={activeSteps}
                        completedSteps={completedSteps}
                        failedSteps={failedSteps}
                    />
                </div>

                {/* Top Right: Chat */}
                <div className="border-b border-slate-200 h-full">
                    <Chat
                        messages={messages}
                        onSendMessage={handleSendMessage}
                        isProcessing={isProcessing}
                    />
                </div>

                {/* Bottom Left: Context Viewer */}
                <div className="border-r border-slate-200">
                    <ContextViewer context={context} />
                </div>

                {/* Bottom Right: Event Stream */}
                <div>
                    <EventStream events={events} />
                </div>
            </div>
        </div>
    );
}

export default App;
