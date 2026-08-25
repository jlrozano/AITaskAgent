namespace AITaskAgent.LLM.Abstractions;

public interface ILlmServiceFactory
{
    ILlmService GetService(string serviceName);
}