namespace AITaskAgent.Core.Abstractions
{

    public interface IFactory<T> where T : class
    {
        T GetService(string key);
    }
}
