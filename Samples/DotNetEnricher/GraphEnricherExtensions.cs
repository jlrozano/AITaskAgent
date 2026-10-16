using Microsoft.Extensions.DependencyInjection;

namespace DotNetEnricher;

public static class GraphEnricherExtensions
{
    public static IServiceCollection AddGraphEnricher<T>(this IServiceCollection services)
        where T : class, IGraphEnricher
        => services.AddTransient<IGraphEnricher, T>();
}
