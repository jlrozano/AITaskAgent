using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Hooks;
using System.Reflection;
using System.Text.Json.Serialization;

namespace AITaskAgent.Core.Abstractions;

/// <summary>Names of the <see cref="ExtensionEntry.Kind"/> values.</summary>
public static class ExtensionKinds
{
    /// <summary>An <c>IStep</c> class; <see cref="ExtensionEntry.Name"/> is its tag.</summary>
    public const string Step = "step";

    /// <summary>An <c>ITool</c> class (or instance); <see cref="ExtensionEntry.Name"/> is the tool's name when it is known.</summary>
    public const string Tool = "tool";

    /// <summary>An <c>IToolSource</c> class.</summary>
    public const string ToolSource = "toolSource";

    /// <summary>A hook (<c>[Hook]</c>).</summary>
    public const string Hook = "hook";

    /// <summary>A step validator (<c>[Validator]</c>).</summary>
    public const string Validator = "validator";

    /// <summary>A result validator (<c>[ResultValidator]</c>).</summary>
    public const string ResultValidator = "resultValidator";

    /// <summary>A response type (<c>[ResponseType]</c>).</summary>
    public const string ResponseType = "responseType";

    /// <summary>An <c>ITemplateSource</c> class.</summary>
    public const string TemplateSource = "templateSource";

    /// <summary>The extension's configuration class (<see cref="IExtension.ConfigurationType"/>); at most one.</summary>
    public const string Configuration = "configuration";

    /// <summary>A top-level configuration section the host lifts to the root (<see cref="IExtension.RootSections"/>).</summary>
    public const string RootSection = "rootSection";

    /// <summary>
    /// A .NET object scripts call as <c>host.&lt;name&gt;.&lt;method&gt;(args)</c> (<see cref="IExtensionBuilder.AddScriptObject"/>);
    /// <see cref="ExtensionEntry.Name"/> is its name, <see cref="ExtensionEntry.Type"/> its class.
    /// </summary>
    public const string ScriptObject = "scriptObject";

    /// <summary>
    /// A JavaScript library of a plugin (<c>js/&lt;name&gt;.js</c> of its folder, found by the host, not registered by <c>Register</c>);
    /// <see cref="ExtensionEntry.Name"/> is its name. Listed by hosts on the plugin's card; <see cref="ExtensionCapture.ActualInfo"/> never reports it.
    /// </summary>
    public const string ScriptLibrary = "scriptLibrary";
}

/// <summary>One thing an extension registers.</summary>
/// <param name="Kind">One of <see cref="ExtensionKinds"/>.</param>
/// <param name="Name">Its registry name: step tag, hook name, response type name, tool name, root section; empty for kinds that have none.</param>
/// <param name="Type">Full name of the class (the declaring class for a hook); null when there is none.</param>
/// <param name="Description">Human text (the attribute's or the type's description), optional.</param>
public sealed record ExtensionEntry(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("description")] string? Description = null)
{
    /// <summary>An entry for a class: its full name, its <c>[Description]</c> and <paramref name="name"/> (empty when the kind has none).</summary>
    public static ExtensionEntry For(string kind, Type type, string name = "")
        => new(kind, name, type.FullName, type.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>(inherit: false)?.Description);

    /// <summary>What identifies the entry when two lists are compared: the name for named kinds, the class for the rest.</summary>
    [JsonIgnore]
    public string Key => Kind switch
    {
        ExtensionKinds.Step or ExtensionKinds.Hook or ExtensionKinds.Validator or ExtensionKinds.ResultValidator
            or ExtensionKinds.ResponseType or ExtensionKinds.RootSection or ExtensionKinds.ScriptObject or ExtensionKinds.ScriptLibrary => Kind + ":" + Name,
        _ => Kind + ":" + (Type ?? Name),
    };
}

/// <summary>
/// What an <see cref="IExtension"/> declares it will register (<see cref="IExtension.Info"/>): plain data, no types, so any host can read it.
/// It is a declaration, not a guarantee: <see cref="Difference"/> compares it with what the extension really registered.
/// </summary>
public sealed record ExtensionInfo
{
    /// <summary>Declares nothing.</summary>
    public static ExtensionInfo Empty { get; } = new();

    /// <summary>Every declared entry.</summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<ExtensionEntry> Entries { get; init; } = [];

    /// <summary>The entries of one kind (<see cref="ExtensionKinds"/>).</summary>
    public IEnumerable<ExtensionEntry> Of(string kind) => Entries.Where(e => e.Kind == kind);

    /// <summary>A copy with <paramref name="entries"/> added.</summary>
    public ExtensionInfo With(params ExtensionEntry[] entries) => this with { Entries = [.. Entries, .. entries] };

    /// <summary>A copy with the entries of <paramref name="other"/> added.</summary>
    public ExtensionInfo Merge(ExtensionInfo other) => this with { Entries = [.. Entries, .. other.Entries] };

    /// <summary>
    /// The entries of <paramref name="declared"/> that <paramref name="actual"/> lacks (<c>Missing</c>: announced and not registered) and the
    /// entries of <paramref name="actual"/> that <paramref name="declared"/> lacks (<c>Extra</c>: registered and not announced), by
    /// <see cref="ExtensionEntry.Key"/>.
    /// </summary>
    public static (IReadOnlyList<ExtensionEntry> Missing, IReadOnlyList<ExtensionEntry> Extra) Difference(ExtensionInfo declared, ExtensionInfo actual)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(actual);
        HashSet<string> declaredKeys = [.. declared.Entries.Select(e => e.Key)];
        HashSet<string> actualKeys = [.. actual.Entries.Select(e => e.Key)];
        return ([.. declared.Entries.Where(e => !actualKeys.Contains(e.Key))], [.. actual.Entries.Where(e => !declaredKeys.Contains(e.Key))]);
    }
}

/// <summary>A hook, validator or result validator found by <see cref="ExtensionDiscovery.Hooks"/>.</summary>
/// <param name="Name">Registry name: <c>[Hook(Name = …)]</c> or <c>Class.Method</c> (nested classes <c>Outer.Inner.Method</c>, no namespace).</param>
/// <param name="Kind">What it is.</param>
/// <param name="Method">The <c>public static</c> method.</param>
/// <param name="Description">The attribute's description.</param>
public sealed record DiscoveredHook(string Name, HookKind Kind, MethodInfo Method, string? Description);

/// <summary>
/// Reflection over an assembly for what <c>AITaskAgent.Abstractions</c> can see: tagged steps, hooks, validators and response types. The
/// rules are the plugin scanner's (architecture §25.3). Tools, tool sources and template sources are found by
/// <c>AssemblyDiscovery</c> in <c>AITaskAgent.Extensions.Llm</c>, which owns <c>ITool</c>.
/// </summary>
public static class ExtensionDiscovery
{
    /// <summary>The public types of <paramref name="assembly"/> that load, ordered by name.</summary>
    public static IReadOnlyList<Type> PublicTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
        }

        return [.. types.Where(t => t.IsVisible).OrderBy(t => t.FullName, StringComparer.Ordinal)];
    }

    /// <summary>The public, concrete, non-generic <c>IStep</c> classes with <c>[StepTag]</c> (what <c>IStepRegistry.AddFromAssembly</c> registers).</summary>
    public static IReadOnlyList<(string Tag, Type Type)> TaggedSteps(Assembly assembly)
        => [.. PublicTypes(assembly)
            .Where(t => typeof(IStep).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters)
            .Select(t => (Tag: t.GetCustomAttribute<StepTagAttribute>(inherit: false)?.TagName, Type: t))
            .Where(s => s.Tag != null)
            .Select(s => (s.Tag!, s.Type))];

    /// <summary>
    /// The <c>public static</c> non-generic methods of the public classes of <paramref name="assembly"/> marked <c>[Hook]</c>, <c>[Validator]</c>
    /// or <c>[ResultValidator]</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A name is declared twice.</exception>
    public static IReadOnlyList<DiscoveredHook> Hooks(Assembly assembly)
    {
        List<DiscoveredHook> hooks = [];
        foreach (Type type in PublicTypes(assembly).Where(t => t.IsClass && !t.ContainsGenericParameters))
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => !m.ContainsGenericParameters))
            {
                DiscoveredHook? hook =
                    method.GetCustomAttribute<HookAttribute>() is { } h ? new(h.Name ?? ClassPath(type) + "." + method.Name, HookKind.Hook, method, h.Description)
                    : method.GetCustomAttribute<ValidatorAttribute>() is { } v ? new(ClassPath(type) + "." + method.Name, HookKind.StepValidator, method, v.Description)
                    : method.GetCustomAttribute<ResultValidatorAttribute>() is { } r ? new(ClassPath(type) + "." + method.Name, HookKind.ResultValidator, method, r.Description)
                    : null;
                if (hook == null)
                {
                    continue;
                }

                if (hooks.Any(x => x.Name == hook.Name))
                {
                    throw new InvalidOperationException($"Hook name '{hook.Name}' is declared twice in '{assembly.GetName().Name}'.");
                }

                hooks.Add(hook);
            }
        }

        return hooks;
    }

    /// <summary>The public, concrete, non-generic classes of <paramref name="assembly"/> marked <c>[ResponseType]</c>, with their names.</summary>
    public static IReadOnlyList<(string Name, Type Type)> ResponseTypes(Assembly assembly)
        => [.. PublicTypes(assembly)
            .Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters && t.GetCustomAttribute<ResponseTypeAttribute>() != null)
            .Select(t => (t.GetCustomAttribute<ResponseTypeAttribute>()!.Name ?? t.Name, t))];

    /// <summary>The step entries of <see cref="TaggedSteps"/> (what <see cref="IExtensionBuilder.AddStepsFromAssembly"/> registers).</summary>
    public static IReadOnlyList<ExtensionEntry> StepEntries(Assembly assembly)
        => [.. TaggedSteps(assembly).Select(s => ExtensionEntry.For(ExtensionKinds.Step, s.Type, s.Tag))];

    /// <summary>The entry of a step class: its <c>[StepTag]</c>, else its full name.</summary>
    public static ExtensionEntry StepEntry(Type stepType)
        => ExtensionEntry.For(ExtensionKinds.Step, stepType, stepType.GetCustomAttribute<StepTagAttribute>(inherit: false)?.TagName ?? stepType.FullName ?? stepType.Name);

    /// <summary>The entry of a hook found by <see cref="Hooks"/>.</summary>
    public static ExtensionEntry EntryOf(DiscoveredHook hook) => new(
        hook.Kind switch { HookKind.StepValidator => ExtensionKinds.Validator, HookKind.ResultValidator => ExtensionKinds.ResultValidator, _ => ExtensionKinds.Hook },
        hook.Name, hook.Method.DeclaringType?.FullName, hook.Description);

    /// <summary>
    /// What <see cref="IExtensionBuilder.AddStepsFromAssembly"/>, <see cref="IExtensionBuilder.AddHooksFromAssembly"/> and
    /// <see cref="IExtensionBuilder.AddResponseTypesFromAssembly"/> would register for <paramref name="assembly"/>, as a declaration.
    /// </summary>
    public static ExtensionInfo Describe(Assembly assembly)
        => new()
        {
            Entries =
            [
                .. TaggedSteps(assembly).Select(s => new ExtensionEntry(ExtensionKinds.Step, s.Tag, s.Type.FullName, DescriptionOf(s.Type))),
                .. Hooks(assembly).Select(EntryOf),
                .. ResponseTypes(assembly).Select(r => new ExtensionEntry(ExtensionKinds.ResponseType, r.Name, r.Type.FullName, DescriptionOf(r.Type))),
            ]
        };

    /// <summary>The <c>[Description]</c> of <paramref name="type"/>, or null.</summary>
    public static string? DescriptionOf(Type type) => type.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>(inherit: false)?.Description;

    private static string ClassPath(Type type) => type.DeclaringType is { } outer ? ClassPath(outer) + "." + type.Name : type.Name;
}

/// <summary>
/// A response type an extension registered in a plain service collection (<see cref="IExtensionBuilder.AddResponseType"/>): the host that wants
/// the schemas resolves every <c>RegisteredResponseType</c>.
/// </summary>
/// <param name="Name">The schema name.</param>
/// <param name="Type">The class.</param>
public sealed record RegisteredResponseType(string Name, Type Type);

/// <summary>
/// A .NET object an extension offers to scripts (<see cref="IExtensionBuilder.AddScriptObject"/>): registered as a service of this type in the
/// collection, read by the JavaScript extension, which exposes each public instance method of <paramref name="Type"/> as
/// <c>host.&lt;Name&gt;.&lt;method&gt;(args)</c> (architecture §18.7).
/// </summary>
/// <param name="Name">The object's name in scripts (<c>host.&lt;Name&gt;</c>): a letter or <c>_</c>, then letters, digits or <c>_</c>.</param>
/// <param name="Type">The class whose public instance methods scripts may call.</param>
/// <param name="Factory">Creates the instance from the run's services (once per service provider); null = <c>ActivatorUtilities.CreateInstance</c>.</param>
/// <param name="Description">What it offers, for catalogs.</param>
public sealed record RegisteredScriptObject(string Name, Type Type, Func<IServiceProvider, object>? Factory = null, string? Description = null)
{
    /// <summary>True when <paramref name="name"/> is a valid script object name (a letter or <c>_</c>, then letters, digits or <c>_</c>).</summary>
    public static bool IsValidName(string? name)
        => !string.IsNullOrEmpty(name) && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
