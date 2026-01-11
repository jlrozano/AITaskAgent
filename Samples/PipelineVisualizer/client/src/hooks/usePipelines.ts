import { useState, useEffect } from 'react';

interface Pipeline {
    name: string;
    description: string;
    version: string;
}

interface PipelinesResponse {
    pipelines: Pipeline[];
}

const API_BASE = import.meta.env.VITE_API_URL || '';

/**
 * Hook to fetch available pipelines from backend.
 */
export function usePipelines() {
    const [pipelines, setPipelines] = useState<Pipeline[]>([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        const fetchPipelines = async () => {
            try {
                const response = await fetch(`${API_BASE}/api/pipelines`);
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data: PipelinesResponse = await response.json();
                setPipelines(data.pipelines);
            } catch (err) {
                setError(err instanceof Error ? err.message : 'Failed to fetch pipelines');
            } finally {
                setLoading(false);
            }
        };

        fetchPipelines();
    }, []);

    return { pipelines, loading, error };
}

/**
 * Hook to fetch a specific pipeline schema from backend.
 */
export function usePipelineSchema(pipelineName: string | null) {
    const [schema, setSchema] = useState<any>(null);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        if (!pipelineName) {
            setSchema(null);
            return;
        }

        const fetchSchema = async () => {
            setLoading(true);
            setError(null);
            try {
                const response = await fetch(`${API_BASE}/api/pipelines/${pipelineName}/schema`);
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                setSchema(data);
            } catch (err) {
                setError(err instanceof Error ? err.message : 'Failed to fetch schema');
                setSchema(null);
            } finally {
                setLoading(false);
            }
        };

        fetchSchema();
    }, [pipelineName]);

    return { schema, loading, error };
}
