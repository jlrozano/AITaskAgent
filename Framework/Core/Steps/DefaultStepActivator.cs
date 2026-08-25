using AITaskAgent.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AITaskAgent.Core.Steps;

public sealed class DefaultStepActivator : IStepActivator
{
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

            IOrderedEnumerable<ConstructorInfo> ctors = stepType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderByDescending(c => c.GetParameters().Length);

            foreach (ConstructorInfo? ctor in ctors)
            {
                ParameterInfo[] parameters = ctor.GetParameters();
                object?[] args = new object?[parameters.Length];
                bool canCreate = true;

                for (int i = 0; i < parameters.Length; i++)
                {
                    ParameterInfo parameter = parameters[i];
                    object? service = serviceProvider.GetService(parameter.ParameterType);
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
        }

        ConstructorInfo? defaultCtor = stepType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(c => c.GetParameters().Length == 0);

        return defaultCtor != null
            ? defaultCtor.Invoke(null)!
            : RuntimeHelpers.GetUninitializedObject(stepType);
    }
}
