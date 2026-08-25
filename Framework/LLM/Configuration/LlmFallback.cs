namespace AITaskAgent.LLM.Configuration;

public record LlmFallback(
    string Model,
    LlmProvider Provider,
    LlmInferenceSettings? Inference = null
);