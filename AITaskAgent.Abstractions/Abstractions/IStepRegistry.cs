using AITaskAgent.Core.Models;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// Registry of step types by tag: registration, lookup, creation (DI-aware, populated from a
/// JSON configuration) and description (JSON Schema of the step's configuration).
/// </summary>
/// <remarks>
/// One instance per service collection, registered as a singleton by <c>AddAITaskAgentCore</c>
/// and filled by the extensions (<see cref="IExtension"/>). There is no global/static registry.
/// Tags are case-sensitive.
/// </remarks>
public interface IStepRegistry
{
    /// <summary>
    /// Registers <paramref name="stepType"/> under <paramref name="tag"/>, or under its
    /// <c>[StepTag]</c> when <paramref name="tag"/> is null or empty.
    /// Registering the same type twice under the same tag is a no-op.
    /// </summary>
    /// <exception cref="ArgumentException">The type does not implement <see cref="IStep"/>,
    /// is abstract, an interface or an open generic, or has no tag.</exception>
    /// <exception cref="InvalidOperationException">The tag is already registered with another type.</exception>
    void Add(Type stepType, string? tag = null);

    /// <summary>Generic form of <see cref="Add(Type, string?)"/>.</summary>
    void Add<TStep>(string? tag = null) where TStep : IStep => Add(typeof(TStep), tag);

    /// <summary>
    /// Registers every concrete, non-generic <see cref="IStep"/> type of <paramref name="assembly"/>
    /// that carries <c>[StepTag]</c>. Abstract types, interfaces and open generics are ignored.
    /// </summary>
    /// <returns>Number of types found (already registered ones included).</returns>
    int AddFromAssembly(Assembly assembly);

    /// <summary>Looks up the type registered under <paramref name="tag"/>.</summary>
    bool TryGetType(string tag, [NotNullWhen(true)] out Type? stepType);

    /// <summary>Snapshot of the tag-to-type mappings.</summary>
    IReadOnlyDictionary<string, Type> GetRegisteredTypes();

    /// <summary>
    /// Creates the step registered under <paramref name="tag"/>. Constructor arguments are resolved
    /// from <paramref name="services"/> when given; a parameter that cannot be resolved (or any
    /// parameter, without <paramref name="services"/>) takes its default value when it has one.
    /// Then the public writable properties are populated from <paramref name="config"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No step is registered under the tag; the message
    /// lists the registered tags.</exception>
    /// <exception cref="InvalidOperationException">With the default activator: no constructor of the
    /// step type can be called that way (the constructor is never skipped).</exception>
    IStep Create(string tag, JObject? config = null, IServiceProvider? services = null);

    /// <summary>Describes the step registered under <paramref name="tag"/>.</summary>
    /// <exception cref="KeyNotFoundException">No step is registered under the tag.</exception>
    StepDescription Describe(string tag);

    /// <summary>
    /// Describes <paramref name="stepType"/>: tag (the registered one, else its <c>[StepTag]</c>,
    /// else the type name), <c>[Description]</c> and a JSON Schema derived from its public
    /// writable properties.
    /// </summary>
    StepDescription Describe(Type stepType);
}
