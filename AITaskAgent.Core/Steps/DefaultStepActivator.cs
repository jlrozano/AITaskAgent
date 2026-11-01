using AITaskAgent.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Creates step instances for the registry. With a service provider, constructor parameters are resolved
/// from it (<see cref="ActivatorUtilities"/> first); with or without one, a parameter that cannot be
/// resolved takes its default value when it declares one. A type with no constructor that can be
/// satisfied that way is an error: the constructor is never skipped.
/// </summary>
public sealed class DefaultStepActivator : IStepActivator
{
    /// <exception cref="InvalidOperationException">No constructor of <paramref name="stepType"/> can be called.</exception>
    public object Create(Type stepType, IServiceProvider? serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(stepType);

        if (serviceProvider != null)
        {
            try
            {
                return ActivatorUtilities.CreateInstance(serviceProvider, stepType);
            }
            catch
            {
            }
        }

        IOrderedEnumerable<ConstructorInfo> ctors = stepType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length);

        foreach (ConstructorInfo ctor in ctors)
        {
            ParameterInfo[] parameters = ctor.GetParameters();
            object?[] args = new object?[parameters.Length];
            bool canCreate = true;

            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo parameter = parameters[i];
                object? service = serviceProvider?.GetService(parameter.ParameterType);
                if (service != null)
                {
                    args[i] = service;
                }
                else if (parameter.HasDefaultValue)
                {
                    args[i] = parameter.DefaultValue;
                }
                else
                {
                    canCreate = false;
                    break;
                }
            }

            if (canCreate)
            {
                return ctor.Invoke(args);
            }
        }

        throw new InvalidOperationException(
            $"Step type '{stepType.FullName}' cannot be created: no constructor can be called with " +
            (serviceProvider == null
                ? "no service provider (pass an IServiceProvider that registers its dependencies)."
                : "the services registered in the provider and the parameters' default values."));
    }
}
