namespace AITaskAgent.Resilience;

public sealed class CircuitBreakerOpenException(string message) : Exception(message);

