using AITaskAgent.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AITaskAgent.Tests;

/// <summary>
/// The Scoped branch of <c>Factory&lt;T&gt;.GetService</c> compared the name of the key property with the requested
/// name instead of the service's own name, so it threw for any real name and returned the first service for "Name".
/// </summary>
public sealed class FactoryScopedLookupTests
{
    public interface INamed { string Name { get; } }
    public sealed class NamedA : INamed { public string Name => "a"; }
    public sealed class NamedB : INamed { public string Name => "b"; }

    [Fact]
    public void A_scoped_service_is_found_by_its_own_name()
    {
        var services = new ServiceCollection();
        services.AddScoped<INamed, NamedA>();
        services.AddScoped<INamed, NamedB>();
        var provider = services.BuildServiceProvider();
        var factory = new Factory<INamed>(provider, services);

        Assert.IsType<NamedB>(factory.GetService("b"));
        Assert.IsType<NamedA>(factory.GetService("a"));
    }
}
