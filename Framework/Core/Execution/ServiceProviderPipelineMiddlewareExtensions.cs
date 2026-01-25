using AITaskAgent.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Extension methods for registering pipeline middlewares from IServiceProvider.
/// </summary>
public static class ServiceProviderPipelineMiddlewareExtensions
{
    /// <summary>
    /// Registers a pipeline middleware of type TMiddleware.
    /// If the middleware is not registered in DI, it will be created using ActivatorUtilities.
    /// </summary>
    /// <typeparam name="TMiddleware">The middleware type to register.</typeparam>
    /// <param name="serviceProvider">The service provider.</param>
    /// <returns>The service provider for chaining.</returns>
    public static IServiceProvider RegisterPipelineMiddleware<TMiddleware>(this IServiceProvider serviceProvider)
        where TMiddleware : class, IPipelineMiddleware
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var middleware = serviceProvider.GetService<TMiddleware>();
        if (middleware is null)
        {
            // Create instance with constructor injection if not registered in DI
            middleware = ActivatorUtilities.CreateInstance<TMiddleware>(serviceProvider);
        }

        PipelineMiddlewareRegistry.Register(middleware);
        return serviceProvider;
    }

    /// <summary>
    /// Registers multiple pipeline middlewares by their types.
    /// Each middleware will be resolved from DI or created using ActivatorUtilities.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="middlewareTypes">The middleware types to register.</param>
    /// <returns>The service provider for chaining.</returns>
    public static IServiceProvider RegisterPipelineMiddlewares(
        this IServiceProvider serviceProvider,
        params Type[] middlewareTypes)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(middlewareTypes);

        foreach (var type in middlewareTypes)
        {
            if (!typeof(IPipelineMiddleware).IsAssignableFrom(type))
            {
                throw new ArgumentException(
                    $"Type '{type.FullName}' does not implement {nameof(IPipelineMiddleware)}.",
                    nameof(middlewareTypes));
            }

            if (serviceProvider.GetService(type) is not IPipelineMiddleware middleware)
            {
                middleware = (IPipelineMiddleware)ActivatorUtilities.CreateInstance(serviceProvider, type);
            }

            PipelineMiddlewareRegistry.Register(middleware);
        }

        return serviceProvider;
    }
}
