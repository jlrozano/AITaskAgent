namespace AITaskAgent.Core.Abstractions;

public interface IStepActivator
{
    object Create(Type stepType, IServiceProvider? serviceProvider);
}
