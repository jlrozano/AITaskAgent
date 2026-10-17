using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace AITaskAgent.Core.Abstractions
{
    public class Factory<T>(IServiceProvider sp, IServiceCollection services, string keyName = "Name") : IFactory<T>
        where T : class
    {
        private Dictionary<string, (Type Type, ServiceLifetime Lifetime, T? Instance)>? _cache;

        private Dictionary<string, (Type Type, ServiceLifetime Lifetime, T? Instance)> BuildCache()
        {
            return sp.GetServices<T>().ToDictionary(
                s => GetServiceName(s),
                s =>
                {
                    Type type = s.GetType();
                    ServiceLifetime lifetime = services
                        .FirstOrDefault(d => d.ImplementationType == type)?.Lifetime
                        ?? ServiceLifetime.Transient;

                    T? instance = lifetime == ServiceLifetime.Singleton ? s : null;

                    return (type, lifetime, instance);
                });
        }
        private string GetServiceName(T instance)
        {
            return instance!.GetType().GetProperty(keyName,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
                ?.GetValue(instance)?.ToString() ?? string.Empty;
        }
        public virtual T GetService(string name)
        {
            _cache ??= BuildCache();

            // Primero busca en DI
            if (_cache.TryGetValue(name, out (Type Type, ServiceLifetime Lifetime, T? Instance) entry))
            {
                return entry.Lifetime switch
                {
                    ServiceLifetime.Singleton => entry.Instance!,
                    ServiceLifetime.Transient => (T)ActivatorUtilities.CreateInstance(sp, entry.Type),
                    ServiceLifetime.Scoped => sp.GetServices<T>().First(s => GetServiceName(s) == name),
                    _ => throw new InvalidOperationException()
                };
            }

            // se registró como una configuración
            IOptionsMonitor<T>? monitor = sp.GetService<IOptionsMonitor<T>>();
            if (monitor != null)
            {
                T instance = monitor.Get(name);
                if (instance != null)
                {
                    return instance;
                }
            }

            throw new ArgumentException($"Unknown service '{name}' for {typeof(T).Name}");
        }
    }
}
