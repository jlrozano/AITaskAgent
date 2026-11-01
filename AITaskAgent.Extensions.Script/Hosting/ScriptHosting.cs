using AITaskAgent.Core.Models;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Script.Hosting;

/// <summary>What a host function gets when a script calls it.</summary>
/// <param name="StepName">The <c>Name</c> of the calling <c>ScriptStep</c>.</param>
/// <param name="Context">The pipeline context of the run (the function reads it; the worker never sees it).</param>
/// <param name="Services">The service provider of the run.</param>
public sealed record ScriptCallContext(string StepName, PipelineContext Context, IServiceProvider Services);

/// <summary>
/// A function of the host that a script can call as <c>host.&lt;ns&gt;.&lt;fn&gt;(args)</c>. Registered in DI; a script reaches it only
/// when the step lists it in <c>allow</c> <b>and</b> the host grants it (<see cref="ScriptHostOptions.GrantedFunctions"/>).
/// </summary>
public interface IScriptHostFunction
{
    /// <summary>Dotted name, two segments (<c>file.readText</c>): namespace and function. Letters, digits and <c>_</c>.</summary>
    string Name { get; }

    /// <summary>What it does and what permission it needs, for catalogs and trust dialogs.</summary>
    string? Description { get; }

    /// <summary>JSON Schema of the argument, or null when it takes none.</summary>
    JObject? ArgsSchema { get; }

    /// <summary>Runs the call. Throwing becomes a JavaScript <c>Error</c> in the script; the message is shown to the script.</summary>
    Task<JToken?> InvokeAsync(JToken args, ScriptCallContext context, CancellationToken cancellationToken);
}

/// <summary>A named block of JavaScript a step can <c>require</c> after declaring it in <c>uses</c>.</summary>
public interface IScriptLibrary
{
    /// <summary>Name used in <c>uses</c> and in <c>require('name')</c>.</summary>
    string Name { get; }

    /// <summary>One-line description, for catalogs.</summary>
    string? Description { get; }

    /// <summary>CommonJS source: assigns to <c>module.exports</c> or <c>exports</c>.</summary>
    string Source { get; }
}

/// <summary>Plain <see cref="IScriptLibrary"/>.</summary>
public sealed record ScriptLibrary(string Name, string Source, string? Description = null) : IScriptLibrary;

/// <summary>Plain <see cref="IScriptHostFunction"/> over a delegate.</summary>
public sealed class DelegateHostFunction(
    string name,
    Func<JToken, ScriptCallContext, CancellationToken, Task<JToken?>> handler,
    string? description = null,
    JObject? argsSchema = null) : IScriptHostFunction
{
    /// <inheritdoc/>
    public string Name { get; } = name;

    /// <inheritdoc/>
    public string? Description { get; } = description;

    /// <inheritdoc/>
    public JObject? ArgsSchema { get; } = argsSchema;

    /// <inheritdoc/>
    public Task<JToken?> InvokeAsync(JToken args, ScriptCallContext context, CancellationToken cancellationToken)
        => handler(args, context, cancellationToken);
}

/// <summary>
/// What the host lets scripts do. Everything is denied until it is set here: a script only calls a function that its
/// step lists in <c>allow</c>, that the catalog knows and that is in <see cref="GrantedFunctions"/>.
/// </summary>
public sealed class ScriptHostOptions
{
    /// <summary>Largest text <c>file.readText</c> returns, in bytes.</summary>
    public const int DefaultMaxFileBytes = 1024 * 1024;

    /// <summary>Host functions scripts may call (names as in <see cref="IScriptHostFunction.Name"/>). Empty = none.</summary>
    public HashSet<string> GrantedFunctions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Folders <c>file.readText</c> and <c>file.exists</c> may read under. Empty = every file call fails.</summary>
    public List<string> FileRoots { get; set; } = [];

    /// <summary>Values <c>config.get</c> returns (read-only text).</summary>
    public Dictionary<string, string> Config { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Largest text <c>file.readText</c> returns, in bytes.</summary>
    public int MaxFileBytes { get; set; } = DefaultMaxFileBytes;
}

/// <summary>The libraries and host functions known to this host.</summary>
public interface IScriptCatalog
{
    /// <summary>Libraries a step can declare in <c>uses</c>.</summary>
    IReadOnlyList<IScriptLibrary> Libraries { get; }

    /// <summary>Host functions a step can list in <c>allow</c> (granted or not).</summary>
    IReadOnlyList<IScriptHostFunction> HostFunctions { get; }

    /// <summary>Looks a library up by name (ordinal).</summary>
    IScriptLibrary? FindLibrary(string name);

    /// <summary>Looks a host function up by name (ordinal).</summary>
    IScriptHostFunction? FindHostFunction(string name);
}

/// <summary>Default <see cref="IScriptCatalog"/>: what was registered in DI, the built-ins included. A duplicate name is an error.</summary>
public sealed class ScriptCatalog : IScriptCatalog
{
    private readonly Dictionary<string, IScriptLibrary> _libraries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IScriptHostFunction> _functions = new(StringComparer.Ordinal);

    /// <summary>Builds the catalog from the registered libraries and functions.</summary>
    public ScriptCatalog(IEnumerable<IScriptLibrary> libraries, IEnumerable<IScriptHostFunction> functions)
    {
        foreach (IScriptLibrary library in libraries)
        {
            if (!_libraries.TryAdd(library.Name, library))
            {
                throw new InvalidOperationException($"Script library '{library.Name}' is registered more than once.");
            }
        }

        foreach (IScriptHostFunction function in functions)
        {
            if (!_functions.TryAdd(function.Name, function))
            {
                throw new InvalidOperationException($"Script host function '{function.Name}' is registered more than once.");
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IScriptLibrary> Libraries => [.. _libraries.Values.OrderBy(l => l.Name, StringComparer.Ordinal)];

    /// <inheritdoc/>
    public IReadOnlyList<IScriptHostFunction> HostFunctions => [.. _functions.Values.OrderBy(f => f.Name, StringComparer.Ordinal)];

    /// <inheritdoc/>
    public IScriptLibrary? FindLibrary(string name) => _libraries.GetValueOrDefault(name);

    /// <inheritdoc/>
    public IScriptHostFunction? FindHostFunction(string name) => _functions.GetValueOrDefault(name);
}
