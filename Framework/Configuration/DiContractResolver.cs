using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Serialization;

namespace AITaskAgent.Configuration;

/// <summary>
/// A Newtonsoft.Json contract resolver that uses .NET Dependency Injection 
/// to instantiate objects, allowing constructors with parameters.
/// </summary>
public class DiContractResolver(IServiceProvider sp) : DefaultContractResolver
{
    protected override JsonObjectContract CreateObjectContract(Type objectType)
    {
        JsonObjectContract contract = base.CreateObjectContract(objectType);

        // We only use DI for classes that are not basic types (like string)
        // and belong to the framework or need complex instantiation.
        if (objectType.IsClass && objectType != typeof(string))
        {
            contract.DefaultCreator = () => ActivatorUtilities.CreateInstance(sp, objectType);
        }

        return contract;
    }
}
