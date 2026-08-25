using AITaskAgent.Core.Abstractions;
using AITaskAgent.LLM.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AITaskAgent.LLM.Configuration;

public sealed class LlmServiceFactory(IServiceProvider sp, IServiceCollection services) :
    Factory<ILlmService>(sp, services, keyName: "ProviderName"), ILlmServiceFactory
{

}