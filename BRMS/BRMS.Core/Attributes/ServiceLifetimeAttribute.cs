using Microsoft.Extensions.DependencyInjection;

namespace BRMS.Core.Attributes;

/// <summary>
/// Attribute to specify the service lifetime for dependency injection registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class ServiceLifetimeAttribute(ServiceLifetime lifetime) : Attribute
{
    /// <summary>
    /// Gets the service lifetime.
    /// </summary>
    public ServiceLifetime Lifetime { get; } = lifetime;
}

/// <summary>
/// Attribute to mark a class as a singleton service.
/// </summary>
public class SingletonAttribute : ServiceLifetimeAttribute
{
    /// <summary>
    /// Initializes a new instance of the SingletonAttribute class.
    /// </summary>
    public SingletonAttribute() : base(ServiceLifetime.Singleton)
    {
    }
}

/// <summary>
/// Attribute to mark a class as a scoped service.
/// </summary>
public class ScopedAttribute : ServiceLifetimeAttribute
{
    /// <summary>
    /// Initializes a new instance of the ScopedAttribute class.
    /// </summary>
    public ScopedAttribute() : base(ServiceLifetime.Scoped)
    {
    }
}

/// <summary>
/// Attribute to mark a class as a transient service.
/// </summary>
public class TransientAttribute : ServiceLifetimeAttribute
{
    /// <summary>
    /// Initializes a new instance of the TransientAttribute class.
    /// </summary>
    public TransientAttribute() : base(ServiceLifetime.Transient)
    {
    }
}
