using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using System.Collections.Concurrent;
using System.Reflection;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Turns the data of a <see cref="PipelineContext"/> into JSON and back, so a suspended run can be
/// resumed by another process. Used by <see cref="FilePipelineContextStorage"/>; a storage of your
/// own (a database, Redis) can use it too and only move the text.
/// </summary>
/// <remarks>
/// <para><b>What is kept.</b> What the pipeline needs to resume with fast-forward:
/// <see cref="PipelineContext.CorrelationId"/>, <see cref="PipelineContext.Metadata"/> (where the
/// pipeline keeps the saved step state and the suspended path, see <see cref="PipelineResume"/>),
/// <see cref="PipelineContext.StepResults"/> (the cache of finished steps and the suspended result)
/// and, for a context implementing <see cref="IPersistableContextState"/>, its own state (the LLM
/// context's conversation). The runtime type of the context is recorded and checked on load.</para>
/// <para><b>What is not.</b> The live objects (<c>EventChannel</c>, <c>ServiceProvider</c>,
/// <c>Runtime</c>): <see cref="Deserialize"/> writes the data onto a fresh context built by the
/// resuming process, which supplies them. Also not kept: the step path stack (empty between runs),
/// the <c>NextSteps</c> of cached results (a cached step has already run its children), the
/// exception of a result's <c>Error</c> (only its message), and properties of type
/// <see cref="JsonSchema"/> (the run-time schema of a <see cref="JsonStepResult"/>: a cached result
/// is not validated again).</para>
/// <para><b>Values.</b> Every value (metadata entry, result value, result property) is stored with
/// its runtime type and read back as that type with Newtonsoft.Json, without type-name handling.
/// JSON (<see cref="JToken"/>), strings, numbers, booleans, dates, enums and plain DTOs or
/// collections of them work. Saving checks each value by reading it back: one that cannot be
/// serialized, or that comes back different (a get-only property, a constructor that drops data, a
/// reference loop), fails the save with an exception that names the entry, instead of being lost
/// silently. Live objects (delegates, steps, services, streams, tasks, reflection objects,
/// contexts) are refused outright.</para>
/// <para><b>Results.</b> A step result must derive from <see cref="StepResult"/> and have a
/// constructor whose first parameter is the step and whose other parameters are optional (all
/// the framework's results do: <see cref="StepResult{T}"/>, <see cref="JsonStepResult"/>,
/// <see cref="SuspendStepResult"/>, <see cref="AwaitingInputResult"/>,
/// <see cref="BreakStepResult"/>, <see cref="ParallelResult"/> with its branch results, and the LLM
/// extension's <c>LlmStringStepResult</c> and <c>LlmJsonStepResult</c>). It is rebuilt with that
/// constructor and given its value, <c>SuspendInfo</c>, error message and every property that has a
/// setter (of any visibility), so <c>StepName</c> and <c>StepType</c> are those of the original.</para>
/// <para><b>Trust.</b> Loading creates the types the document names. Treat the stored documents like
/// the application's own configuration: only load what this application wrote.</para>
/// </remarks>
public static class PipelineContextSerializer
{
    /// <summary>Version of the document layout written by <see cref="ToJson"/>.</summary>
    public const int FormatVersion = 1;

    private const string JsonTypeMarker = "json";
    private const string StepResultTypeMarker = "stepResult";

    private static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        ReferenceLoopHandling = ReferenceLoopHandling.Error,
        DateParseHandling = DateParseHandling.None,
        NullValueHandling = NullValueHandling.Include
    };

    private static readonly JsonSerializer Json = JsonSerializer.Create(Settings);

    private static readonly MethodInfo ValueSetter =
        typeof(StepResult).GetProperty(nameof(StepResult.Value), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!
            .GetSetMethod(nonPublic: true)!;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    /// <summary>Serializes <paramref name="context"/> to JSON text (see <see cref="ToJson"/>).</summary>
    public static string Serialize(PipelineContext context, bool indented = true)
        => ToJson(context).ToString(indented ? Formatting.Indented : Formatting.None);

    /// <summary>
    /// Restores a document written by <see cref="Serialize"/> onto <paramref name="target"/> (see
    /// <see cref="FromJson"/>). The text is read without date parsing, so JSON strings stay strings.
    /// </summary>
    public static PipelineContext Deserialize(string json, PipelineContext target)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonTextReader reader = new(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        JObject document = JObject.Load(reader);
        return FromJson(document, target);
    }

    /// <summary>
    /// Builds the JSON document of <paramref name="context"/>: its type, correlation id, metadata,
    /// cached step results and extension state.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// A value or result cannot be stored without losing data; the message names the entry.
    /// </exception>
    public static JObject ToJson(PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Entries that only live while a resumed step runs; a stored context never needs them.
        HashSet<string> transient =
        [
            PipelineResume.PendingResumeKey(context),
            PipelineResume.ResumeResponseKey(context)
        ];

        JObject metadata = [];
        foreach (KeyValuePair<string, object?> entry in context.Metadata.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!transient.Contains(entry.Key))
            {
                metadata[entry.Key] = EncodeValue(entry.Value, $"Metadata['{entry.Key}']");
            }
        }

        JObject results = [];
        foreach (KeyValuePair<string, IStepResult> entry in context.StepResults.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            results[entry.Key] = EncodeVerifiedResult(entry.Value, $"StepResults['{entry.Key}']");
        }

        JObject document = new()
        {
            ["formatVersion"] = FormatVersion,
            ["contextType"] = TypeName(context.GetType()),
            ["correlationId"] = context.CorrelationId,
            ["metadata"] = metadata,
            ["stepResults"] = results
        };

        if (context is IPersistableContextState persistable && persistable.SaveExtensionState() is { } extension)
        {
            document["extension"] = extension;
        }

        return document;
    }

    /// <summary>
    /// Returns a copy of <paramref name="target"/> (a fresh context of the resuming process, holding
    /// its event channel and services) carrying the correlation id, metadata, step results and
    /// extension state of <paramref name="document"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The document is not one this version writes, names a type that cannot be found, or was saved
    /// from a context type <paramref name="target"/> is not.
    /// </exception>
    public static PipelineContext FromJson(JObject document, PipelineContext target)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);

        int version = document.Value<int?>("formatVersion")
            ?? throw new InvalidOperationException("The stored pipeline context has no 'formatVersion'.");
        if (version != FormatVersion)
        {
            throw new InvalidOperationException(
                $"The stored pipeline context has format version {version}; this version of the framework reads {FormatVersion}.");
        }

        string correlationId = document.Value<string>("correlationId")
            ?? throw new InvalidOperationException("The stored pipeline context has no 'correlationId'.");

        string contextTypeName = document.Value<string>("contextType")
            ?? throw new InvalidOperationException("The stored pipeline context has no 'contextType'.");
        Type contextType = ResolveType(contextTypeName, "the context");
        if (!contextType.IsInstanceOfType(target))
        {
            throw new InvalidOperationException(
                $"The pipeline context '{correlationId}' was saved as a {contextType.Name}, but the context to restore it " +
                $"into is a {target.GetType().Name}. Register the same context factory in the process that resumes " +
                "(AddAITaskAgent() for pipelines with LLM steps).");
        }

        PipelineContext context = target with
        {
            CorrelationId = correlationId,
            Metadata = new ConcurrentDictionary<string, object?>(),
            StepResults = new ConcurrentDictionary<string, IStepResult>()
        };

        if (document["metadata"] is JObject metadata)
        {
            foreach (JProperty entry in metadata.Properties())
            {
                context.Metadata[entry.Name] = DecodeValue(entry.Value, $"Metadata['{entry.Name}']");
            }
        }

        if (document["stepResults"] is JObject results)
        {
            foreach (JProperty entry in results.Properties())
            {
                context.StepResults[entry.Name] = DecodeResult(entry.Value, $"StepResults['{entry.Name}']");
            }
        }

        if (document["extension"] is { Type: not JTokenType.Null } extension)
        {
            context = context is IPersistableContextState persistable
                ? persistable.RestoreExtensionState(extension)
                : throw new InvalidOperationException(
                    $"The stored pipeline context carries extension state, but {context.GetType().Name} cannot restore it.");
        }

        return context;
    }

    // ── Values ───────────────────────────────────────────────────────────────

    private static JObject EncodeValue(object? value, string where)
    {
        switch (value)
        {
            case null:
                return new JObject { ["type"] = null, ["value"] = null };
            case JToken token:
                return new JObject { ["type"] = JsonTypeMarker, ["value"] = token.DeepClone() };
            case IStepResult result:
                return new JObject { ["type"] = StepResultTypeMarker, ["value"] = EncodeVerifiedResult(result, where) };
        }

        Type type = value.GetType();
        EnsureStorable(type, where);

        JToken encoded;
        try
        {
            encoded = JToken.FromObject(value, Json);
        }
        catch (Exception ex)
        {
            throw new NotSupportedException(
                $"{where}: a value of type {type.FullName} cannot be stored as JSON ({ex.Message}). " +
                "Keep JSON (JToken), primitives or plain data objects in what a suspended pipeline stores.", ex);
        }

        // Read it back now: a value that does not survive the trip must fail the save, not the resume.
        JToken again;
        try
        {
            object? back = encoded.ToObject(type, Json);
            again = back is null ? JValue.CreateNull() : JToken.FromObject(back, Json);
        }
        catch (Exception ex)
        {
            throw new NotSupportedException(
                $"{where}: a value of type {type.FullName} is written as JSON but cannot be read back ({ex.Message}).", ex);
        }

        if (!JToken.DeepEquals(encoded, again))
        {
            throw new NotSupportedException(
                $"{where}: a value of type {type.FullName} does not come back the same after storage (a get-only " +
                "property or a constructor that drops data). Store it as JSON (JToken) or as a plain data object.");
        }

        return new JObject { ["type"] = TypeName(type), ["value"] = encoded };
    }

    private static object? DecodeValue(JToken encoded, string where)
    {
        if (encoded is not JObject wrapper)
        {
            throw new InvalidOperationException($"{where}: the stored value is malformed.");
        }

        string? typeName = wrapper.Value<string>("type");
        JToken? value = wrapper["value"];

        switch (typeName)
        {
            case null:
                return null;
            case JsonTypeMarker:
                return value?.DeepClone() ?? JValue.CreateNull();
            case StepResultTypeMarker:
                return DecodeResult(value ?? JValue.CreateNull(), where);
        }

        Type type = ResolveType(typeName, where);
        EnsureStorable(type, where);
        return value is null || value.Type == JTokenType.Null ? null : value.ToObject(type, Json);
    }

    private static void EnsureStorable(Type type, string where)
    {
        bool live =
            typeof(Delegate).IsAssignableFrom(type)
            || typeof(IStep).IsAssignableFrom(type)
            || typeof(PipelineContext).IsAssignableFrom(type)
            || typeof(IServiceProvider).IsAssignableFrom(type)
            || typeof(Stream).IsAssignableFrom(type)
            || typeof(Task).IsAssignableFrom(type)
            || typeof(MemberInfo).IsAssignableFrom(type)
            || typeof(Assembly).IsAssignableFrom(type)
            || (typeof(IDisposable).IsAssignableFrom(type) && !IsCollection(type))
            || type.IsPointer
            || type.IsByRef;

        if (live)
        {
            throw new NotSupportedException(
                $"{where}: {type.FullName} is a live object, not data, and cannot be stored with a suspended pipeline.");
        }
    }

    private static bool IsCollection(Type type) => typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    // ── Results ──────────────────────────────────────────────────────────────

    /// <summary>Encodes a result and checks that rebuilding it gives the same document.</summary>
    private static JObject EncodeVerifiedResult(IStepResult result, string where)
    {
        JObject encoded = EncodeResult(result, where);
        JObject again = EncodeResult(DecodeResult(encoded, where), where);
        return JToken.DeepEquals(encoded, again)
            ? encoded
            : throw new NotSupportedException(
                $"{where}: a result of type {result.GetType().FullName} does not come back the same after storage " +
                "(one of its properties cannot be set). Persistable results expose their data through settable properties.");
    }

    private static JObject EncodeResult(IStepResult result, string where)
    {
        if (result is not StepResult)
        {
            throw new NotSupportedException(
                $"{where}: the result type {result.GetType().FullName} does not derive from StepResult, so it cannot be stored.");
        }

        Type type = result.GetType();
        JObject encoded = new()
        {
            ["type"] = TypeName(type),
            ["stepName"] = result.StepName,
            ["stepType"] = result.StepType
        };

        if (result is ParallelResult parallel)
        {
            JObject branches = [];
            foreach (KeyValuePair<string, IStepResult> branch in parallel.Value.OrderBy(b => b.Key, StringComparer.Ordinal))
            {
                branches[branch.Key] = EncodeResult(branch.Value, $"{where} branch '{branch.Key}'");
            }

            encoded["branches"] = branches;
        }
        else
        {
            encoded["value"] = EncodeValue(result.Value, $"{where}.Value");
        }

        if (result.SuspendInfo != null)
        {
            encoded["suspendInfo"] = EncodeValue(result.SuspendInfo, $"{where}.SuspendInfo");
        }

        if (result.Error != null)
        {
            encoded["error"] = result.Error.Message;
        }

        JObject properties = [];
        foreach (PropertyInfo property in PersistedProperties(type))
        {
            properties[property.Name] = EncodeValue(property.GetValue(result), $"{where}.{property.Name}");
        }

        if (properties.Count > 0)
        {
            encoded["properties"] = properties;
        }

        return encoded;
    }

    private static IStepResult DecodeResult(JToken token, string where)
    {
        if (token is not JObject encoded)
        {
            throw new InvalidOperationException($"{where}: the stored result is malformed.");
        }

        string typeName = encoded.Value<string>("type")
            ?? throw new InvalidOperationException($"{where}: the stored result has no type.");
        Type type = ResolveType(typeName, where);
        if (!typeof(StepResult).IsAssignableFrom(type) || type.IsAbstract)
        {
            throw new InvalidOperationException($"{where}: {type.FullName} is not a concrete StepResult.");
        }

        string stepName = encoded.Value<string>("stepName") ?? string.Empty;
        string stepType = encoded.Value<string>("stepType") ?? string.Empty;

        StepResult result = Construct(type, new EmptyStep(stepName), where);
        result.RestoreIdentity(stepName, stepType);

        if (result is ParallelResult parallel)
        {
            if (encoded["branches"] is JObject branches)
            {
                foreach (JProperty branch in branches.Properties())
                {
                    parallel.AddResult(branch.Name, DecodeResult(branch.Value, $"{where} branch '{branch.Name}'"));
                }
            }
        }
        else if (encoded["value"] is { } value)
        {
            ValueSetter.Invoke(result, [DecodeValue(value, $"{where}.Value")]);
        }

        if (encoded["suspendInfo"] is { } suspendInfo)
        {
            result.SuspendInfo = DecodeValue(suspendInfo, $"{where}.SuspendInfo");
        }

        if (encoded.Value<string>("error") is { } error)
        {
            result.Error = new StepError { Message = error };
        }

        if (encoded["properties"] is JObject properties)
        {
            Dictionary<string, PropertyInfo> byName = PersistedProperties(type).ToDictionary(p => p.Name, StringComparer.Ordinal);
            foreach (JProperty property in properties.Properties())
            {
                // A property the type no longer has (stored by an older version) is skipped.
                if (byName.TryGetValue(property.Name, out PropertyInfo? info))
                {
                    info.SetValue(result, DecodeValue(property.Value, $"{where}.{property.Name}"));
                }
            }
        }

        return result;
    }

    private static StepResult Construct(Type type, IStep step, string where)
    {
        IEnumerable<ConstructorInfo> constructors = type
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(c => c.GetParameters().Length);

        foreach (ConstructorInfo constructor in constructors)
        {
            ParameterInfo[] parameters = constructor.GetParameters();
            if (parameters.Length == 0
                || !parameters[0].ParameterType.IsAssignableFrom(typeof(EmptyStep))
                || !parameters.Skip(1).All(p => p.IsOptional))
            {
                continue;
            }

            object?[] arguments = [step, .. parameters.Skip(1).Select(p => p.HasDefaultValue ? p.DefaultValue : null)];
            return (StepResult)constructor.Invoke(arguments);
        }

        throw new NotSupportedException(
            $"{where}: the result type {type.FullName} has no constructor taking the step (plus optional parameters), " +
            "so it cannot be rebuilt from storage.");
    }

    /// <summary>
    /// Properties a result type adds to <see cref="StepResult"/> that can be read and set (setter of
    /// any visibility, init included), except schemas, step references and the value itself.
    /// </summary>
    private static PropertyInfo[] PersistedProperties(Type type) => PropertyCache.GetOrAdd(type, static t =>
    {
        List<PropertyInfo> result = [];
        for (Type? current = t; current != null && current != typeof(StepResult) && current != typeof(object); current = current.BaseType)
        {
            foreach (PropertyInfo property in current.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (property.GetIndexParameters().Length > 0
                    || property.GetMethod is null
                    || property.SetMethod is null
                    || property.Name == nameof(IStepResult.Value)
                    || property.IsDefined(typeof(JsonIgnoreAttribute))
                    || typeof(JsonSchema).IsAssignableFrom(property.PropertyType)
                    || typeof(IStep).IsAssignableFrom(property.PropertyType)
                    || result.Any(p => p.Name == property.Name))
                {
                    continue;
                }

                result.Add(property);
            }
        }

        return [.. result.OrderBy(p => p.Name, StringComparer.Ordinal)];
    });

    // ── Types ────────────────────────────────────────────────────────────────

    private static string TypeName(Type type) => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

    private static Type ResolveType(string name, string where)
        => Type.GetType(name, throwOnError: false)
           ?? throw new InvalidOperationException(
               $"{where}: the stored type '{name}' cannot be found. The process that resumes must load the same assemblies as the one that suspended.");
}
