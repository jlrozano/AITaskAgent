import { useState, useEffect } from 'react';
import { apiClient } from '../services/apiClient';
import { PipelineDefinition } from '../types';

interface UsePipelineReturn {
    pipeline: PipelineDefinition | null;
    loading: boolean;
    error?: string;
    refetch: () => void;
}

/**
 * Hook for fetching and caching pipeline definition.
 */
export function usePipeline(): UsePipelineReturn {
    const [pipeline, setPipeline] = useState<PipelineDefinition | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | undefined>();

    const fetchPipeline = async () => {
        setLoading(true);
        setError(undefined);

        try {
            const data = await apiClient.getPipeline();
            setPipeline(data);
        } catch (err) {
            setError(err instanceof Error ? err.message : 'Failed to load pipeline');
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        fetchPipeline();
    }, []);

    return {
        pipeline,
        loading,
        error,
        refetch: fetchPipeline,
    };
}
