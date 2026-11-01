using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.YAML.Schemas;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using System.Reflection;

namespace AITaskAgent.YAML.Execution;

/// <summary>
/// A built pipeline ready for execution.
/// </summary>
public sealed class YamlPipeline
{
    private readonly IPipeline _pipeline;

    internal YamlPipeline(string name, IReadOnlyList<IStep> steps, IPipeline pipeline, JsonSchema? inputSchema)
    {
        Name = name;
        Steps = steps;
        InputSchema = inputSchema;
        _pipeline = pipeline;
    }

    public string Name { get; }

    /// <summary>Top-level steps in execution (topological) order. Nested steps live inside their containers.</summary>
    public IReadOnlyList<IStep> Steps { get; }

    /// <summary>The <c>inputSchema</c> of the first step, when it declares one.</summary>
    public JsonSchema? InputSchema { get; }

    /// <summary>
    /// Runs the pipeline. An <see cref="IStepResult"/> input is passed as is; anything else (a
    /// <see cref="JToken"/>, a POCO, a string) becomes a <see cref="JsonStepResult"/>. When the first
    /// step declares an <c>inputSchema</c>, the input value is validated against it first and a
    /// mismatch is returned as an error result without running any step.
    /// </summary>
    /// <remarks>
    /// When <paramref name="context"/> holds a suspended run (<see cref="PipelineResume.IsSuspended"/>),
    /// the call resumes it: <paramref name="input"/> is the response to the step that suspended (a
    /// <c>SuspendStep</c> returns it as its result), so it is not checked against the first step's
    /// <c>inputSchema</c>, and the steps that already finished are served from the context's cache.
    /// </remarks>
    public Task<IStepResult> ExecuteAsync<T>(
        T input,
        PipelineContext? context = null,
        CancellationToken cancellationToken = default)
    {
        EmptyStep source = new("input");
        IStepResult seed = input as IStepResult ?? new JsonStepResult(source, ToJson(input), InputSchema);

        if (InputSchema != null && (context == null || !PipelineResume.IsSuspended(context)))
        {
            JToken? value = seed.Value is null ? null : ToJson(seed.Value);
            (bool isValid, string? error) = JsonStepResult.Validate(value, InputSchema);
            if (!isValid)
            {
                return Task.FromResult<IStepResult>(ErrorStepResult.FromMessage(
                    source, $"The input of pipeline '{Name}' does not match the inputSchema of step '{Steps[0].Name}'. {error}"));
            }
        }

        return _pipeline.ExecuteAsync(Name, Steps, seed, context, cancellationToken: cancellationToken);
    }

    private static JToken ToJson(object? value) => value switch
    {
        null => JValue.CreateNull(),
        JToken token => token,
        _ => JToken.FromObject(value)
    };
}

/// <summary>
/// Builds a pipeline from YAML v2 (grammar and errors: docs/guides/yaml-pipelines.md).
///
/// Responsibilities:
///   1. Merge all *.yaml files of a folder (one YAML document each, joined with <c>---</c>).
///   2. Parse YAML to <see cref="JToken"/> and, per step, validate its keys and value types against
///      <see cref="IStepRegistry.Describe(string)"/>.
///   3. Create every step with <see cref="IStepRegistry.Create"/> (DI + JSON configuration), name it
///      after its <c>id</c>, and build container children (<c>steps</c>, <c>branches</c>,
///      <c>cases</c>, <c>default</c>...) recursively with the same rules.
///   4. Resolve <c>inputSchema</c>/<c>outputSchema</c> from the <see cref="SchemaRegistry"/>.
///   5. Fail fast: unique ids, known types and properties, <c>dependsOn</c> targets, cycles and
///      schema mismatches between dependent steps.
///   6. Order the top-level steps topologically.
/// </summary>
/// <remarks>
/// The factory knows nothing about LLMs: an LLM step resolves its own role when the registry
/// populates its configuration. A property whose type is a list of steps (<c>List&lt;IStep&gt;</c>)
/// or a map of step lists (<c>Dictionary&lt;string, List&lt;IStep&gt;&gt;</c>) is a container
/// property: its YAML value is built into steps, not passed to the registry as JSON.
/// </remarks>
public sealed class YamlPipelineFactory(
    IServiceProvider serviceProvider,
    IStepRegistry stepRegistry,
    SchemaRegistry schemaRegistry,
    IPipeline pipeline)
{
    private HookBinder? _binder;

    private const string IdKey = "id";
    private const string TypeKey = "type";
    private const string DependsOnKey = "dependsOn";
    private const string InputSchemaKey = "inputSchema";
    private const string OutputSchemaKey = "outputSchema";
    private const string StepsKey = "steps";
    private const string DisplayKey = "display";
    private const string NameKey = "name";

    /// <summary>Type assumed for a step that omits <c>type</c> but declares <c>steps</c>.</summary>
    private const string ImplicitGroupType = "GroupStep";

    /// <summary>Creates a pipeline from all *.yaml files in a folder tree (sorted by path).</summary>
    public async Task<YamlPipeline> CreateFromFolderAsync(string pipelineFolder)
    {
        string[] files = Directory.GetFiles(pipelineFolder, "*.yaml", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        List<string> parts = [];
        foreach (string file in files)
        {
            parts.Add(await File.ReadAllTextAsync(file));
        }

        string merged = string.Join($"{Environment.NewLine}---{Environment.NewLine}", parts);
        return CreateFromYaml(merged);
    }

    /// <summary>
    /// Creates a pipeline from a YAML string with one or more documents. Steps of all documents are
    /// concatenated; the pipeline name is the first non-empty <c>name</c>.
    /// </summary>
    public YamlPipeline CreateFromYaml(string mergedYaml)
    {
        ArgumentNullException.ThrowIfNull(mergedYaml);

        List<JToken> documents = YamlJson.LoadDocuments(mergedYaml);
        if (documents.Count == 0)
        {
            throw new InvalidOperationException("The pipeline YAML contains no documents.");
        }

        string name = string.Empty;
        List<JObject> stepNodes = [];

        foreach (JToken document in documents)
        {
            if (document is not JObject root)
            {
                throw new InvalidOperationException(
                    $"A pipeline document must be a mapping with 'name' and 'steps'{YamlJson.Where(document)}.");
            }

            foreach (JProperty property in root.Properties())
            {
                switch (property.Name)
                {
                    case NameKey:
                        if (property.Value.Type != JTokenType.String && property.Value.Type != JTokenType.Null)
                        {
                            throw new InvalidOperationException($"'name' must be text{YamlJson.Where(property.Value)}.");
                        }

                        if (string.IsNullOrEmpty(name))
                        {
                            name = property.Value.Value<string>() ?? string.Empty;
                        }

                        break;

                    case StepsKey:
                        stepNodes.AddRange(ReadStepList(property.Value, "the pipeline"));
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unknown key '{property.Name}' in a pipeline document{YamlJson.Where(property.Value)}. " +
                            "Valid keys: name, steps.");
                }
            }
        }

        if (stepNodes.Count == 0)
        {
            throw new InvalidOperationException("The pipeline YAML declares no steps.");
        }

        BuildSession session = new();
        List<StepNode> topLevel = [.. stepNodes.Select(node => BuildStep(node, session, parentId: null))];
        List<StepNode> ordered = ValidateAndOrder(topLevel, session);

        return new YamlPipeline(name, [.. ordered.Select(n => n.Step)], pipeline, ordered[0].InputSchema);
    }

    // ── Step construction ────────────────────────────────────────────────────

    /// <summary>A built step plus what the factory still needs from its declaration.</summary>
    private sealed record StepNode(
        string Id,
        IStep Step,
        string Where,
        IReadOnlyList<string> DependsOn,
        string? InputSchemaName,
        string? OutputSchemaName,
        JsonSchema? InputSchema);

    /// <summary>Ids seen so far (whole document) and, for nested ones, their container.</summary>
    private sealed class BuildSession
    {
        public Dictionary<string, (string Where, string? ParentId)> Ids { get; } = new(StringComparer.Ordinal);
    }

    private enum ContainerKind { StepList, StepMap }

    private StepNode BuildStep(JObject node, BuildSession session, string? parentId)
    {
        string where = YamlJson.Where(node);
        bool topLevel = parentId == null;
        string scope = topLevel ? "" : $" inside '{parentId}'";

        string id = ReadRequiredText(node, IdKey)
            ?? throw new InvalidOperationException($"A step{scope}{where} has no 'id'. Every step needs a unique 'id'.");

        if (id.Contains('/'))
        {
            throw new InvalidOperationException($"Step id '{id}'{where} must not contain '/': it separates step paths.");
        }

        if (session.Ids.TryGetValue(id, out var first))
        {
            throw new InvalidOperationException(
                $"Duplicate step id '{id}'{where}; it is already used{first.Where}. " +
                "Ids must be unique in the whole pipeline, nested steps included.");
        }

        session.Ids[id] = (where, parentId);

        string type = ReadRequiredText(node, TypeKey)
            ?? (node.ContainsKey(StepsKey)
                ? ImplicitGroupType
                : throw new InvalidOperationException(
                    $"Step '{id}'{where} has no 'type'. Available types: {AvailableTypes()}."));

        if (!stepRegistry.TryGetType(type, out Type? stepType))
        {
            throw new InvalidOperationException(
                $"Step '{id}'{where} has unknown type '{type}'. Available types: {AvailableTypes()}.");
        }

        Dictionary<string, (PropertyInfo Property, ContainerKind Kind)> containers = GetContainerProperties(stepType);
        IDictionary<string, JsonSchemaProperty> parameters =
            stepRegistry.Describe(type).Parameters?.Properties ?? new Dictionary<string, JsonSchemaProperty>();

        HashSet<string> structural = topLevel
            ? [IdKey, TypeKey, DependsOnKey, InputSchemaKey, OutputSchemaKey, DisplayKey, HookBinder.HooksKey, HookBinder.ValidatorsKey, HookBinder.ResultValidatorsKey]
            : [IdKey, TypeKey, InputSchemaKey, OutputSchemaKey, DisplayKey, HookBinder.HooksKey, HookBinder.ValidatorsKey, HookBinder.ResultValidatorsKey];

        JObject config = [];
        foreach (JProperty property in node.Properties())
        {
            if (structural.Contains(property.Name) || containers.ContainsKey(property.Name))
            {
                continue;
            }

            if (property.Name == DependsOnKey)
            {
                throw new InvalidOperationException(
                    $"Step '{id}'{YamlJson.Where(property.Value)}: 'dependsOn' is only allowed on top-level steps. " +
                    $"Steps inside '{parentId}' run in the order they are declared.");
            }

            if (!parameters.TryGetValue(property.Name, out JsonSchemaProperty? parameter))
            {
                IEnumerable<string> valid = structural.Concat(containers.Keys).Concat(parameters.Keys)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.OrdinalIgnoreCase);
                throw new InvalidOperationException(
                    $"Step '{id}' (type '{type}') has unknown property '{property.Name}'{YamlJson.Where(property.Value)}. " +
                    $"Valid properties: {string.Join(", ", valid)}.");
            }

            if (property.Value.Type == JTokenType.Null)
            {
                continue; // '~' / 'null' / empty means "not set": the step keeps its default, whatever the type.
            }

            config.Add(property.Name, CheckValue(id, type, property.Name, property.Value, parameter));
        }

        IStep step;
        try
        {
            step = stepRegistry.Create(type, config, serviceProvider);
        }
        catch (Exception ex)
        {
            Exception cause = Unwrap(ex);
            throw new InvalidOperationException(
                $"Step '{id}' (type '{type}'){where} could not be created: {cause.Message}", cause);
        }

        if (step is StepBase stepBase)
        {
            stepBase.Name = id;
        }
        else if (step.Name != id)
        {
            throw new InvalidOperationException(
                $"Step '{id}' (type '{type}'): the type does not derive from StepBase, so its name cannot be set to the declared id.");
        }

        foreach ((string key, (PropertyInfo property, ContainerKind kind)) in containers)
        {
            if (!node.TryGetValue(key, StringComparison.Ordinal, out JToken? value))
            {
                continue;
            }

            object children = kind == ContainerKind.StepList
                ? BuildChildren(value, session, id, key)
                : BuildChildMap(value, session, id, key);
            property.SetValue(step, children);
        }

        ApplyDisplay(node, step, id, type, where);

        (string? inputName, JsonSchema? inputSchema) = ResolveSchema(node, InputSchemaKey, id);
        (string? outputName, JsonSchema? outputSchema) = ResolveSchema(node, OutputSchemaKey, id);
        if (inputName != null || outputName != null)
        {
            if (step is not IJsonSchemaStep schemaStep)
            {
                throw new InvalidOperationException(
                    $"Step '{id}' (type '{type}'){where} does not accept '{InputSchemaKey}'/'{OutputSchemaKey}': " +
                    $"the type does not implement {nameof(IJsonSchemaStep)}.");
            }

            schemaStep.InputJsonSchema = inputSchema;
            schemaStep.OutputJsonSchema = outputSchema;
        }

        // After the schemas, so a bound hook can already see them.
        _binder ??= new HookBinder(serviceProvider, schemaRegistry);
        _binder.Bind(node, step, id, type, where, outputName);

        IReadOnlyList<string> dependsOn = topLevel ? ReadDependsOn(node, id) : [];

        return new StepNode(id, step, where, dependsOn, inputName, outputName, inputSchema);
    }

    private List<IStep> BuildChildren(JToken value, BuildSession session, string parentId, string key)
        => [.. ReadStepList(value, $"'{parentId}.{key}'").Select(child => BuildStep(child, session, parentId).Step)];

    private Dictionary<string, List<IStep>> BuildChildMap(JToken value, BuildSession session, string parentId, string key)
    {
        if (value is not JObject map)
        {
            throw new InvalidOperationException(
                $"'{parentId}.{key}'{YamlJson.Where(value)} must be a mapping of names to lists of steps.");
        }

        // Entries are matched ignoring case and surrounding whitespace (the switch's rule), so two keys
        // that only differ in those would leave one entry unreachable.
        Dictionary<string, List<IStep>> result = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach (JProperty entry in map.Properties())
        {
            if (normalized.TryGetValue(entry.Name.Trim(), out string? previous))
            {
                throw new InvalidOperationException(
                    $"'{parentId}.{key}' repeats the entry '{entry.Name}'{YamlJson.Where(entry.Value)}: it is the same as " +
                    $"'{previous}' once case and surrounding spaces are ignored, so one of them could never be chosen.");
            }

            normalized[entry.Name.Trim()] = entry.Name;
            result[entry.Name] = BuildChildren(entry.Value, session, parentId, $"{key}.{entry.Name}");
        }

        return result;
    }

    private static List<JObject> ReadStepList(JToken value, string owner)
    {
        if (value is not JArray array)
        {
            throw new InvalidOperationException($"The steps of {owner}{YamlJson.Where(value)} must be a list.");
        }

        List<JObject> steps = [];
        foreach (JToken item in array)
        {
            steps.Add(item as JObject
                ?? throw new InvalidOperationException(
                    $"Each step of {owner} must be a mapping with 'id' and 'type'{YamlJson.Where(item)}."));
        }

        return steps;
    }

    /// <summary>Public settable properties whose type is a step list or a map of step lists.</summary>
    private static Dictionary<string, (PropertyInfo, ContainerKind)> GetContainerProperties(Type stepType)
    {
        Dictionary<string, (PropertyInfo, ContainerKind)> result = new(StringComparer.Ordinal);
        foreach (PropertyInfo property in stepType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.SetMethod?.IsPublic != true || Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
            {
                continue;
            }

            ContainerKind? kind = IsStepList(property.PropertyType) ? ContainerKind.StepList
                : IsStepMap(property.PropertyType) ? ContainerKind.StepMap
                : null;
            if (kind != null)
            {
                result[JsonName(property)] = (property, kind.Value);
            }
        }

        return result;
    }

    private static bool IsStepList(Type type)
        => type.IsGenericType
           && type.GetGenericArguments() is [Type item]
           && item == typeof(IStep)
           && type.IsAssignableFrom(typeof(List<IStep>));

    private static bool IsStepMap(Type type)
        => type.IsGenericType
           && type.GetGenericArguments() is [Type key, Type value]
           && key == typeof(string)
           && IsStepList(value)
           && type.IsAssignableFrom(typeof(Dictionary<string, List<IStep>>));

    private static string JsonName(PropertyInfo property)
        => property.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName
           ?? char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

    // ── Values ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks a configuration value against the type the step's description declares. A number or
    /// boolean written where text is expected is accepted as its original YAML text. Properties without
    /// a declared type are not checked. Null never reaches here: a property written as <c>~</c>,
    /// <c>null</c> or empty is left out of the configuration, so the step keeps its default for any
    /// property type (value types included); null cannot be used to clear a property that has a default.
    /// </summary>
    private static JToken CheckValue(string id, string type, string name, JToken value, JsonSchemaProperty parameter)
    {
        if (parameter.Enumeration.Count > 0)
        {
            string? text = value.Type == JTokenType.String ? value.Value<string>() : YamlJson.RawText(value);
            if (text != null && parameter.Enumeration.Any(e => string.Equals(e?.ToString(), text, StringComparison.OrdinalIgnoreCase)))
            {
                return new JValue(text);
            }

            throw TypeError(id, type, name, value, $"one of: {string.Join(", ", parameter.Enumeration)}");
        }

        JsonObjectType expected = parameter.Type;
        if (expected == JsonObjectType.None)
        {
            return value;
        }

        if (expected.HasFlag(JsonObjectType.String))
        {
            if (value.Type == JTokenType.String)
            {
                return value;
            }

            if (value.Type is JTokenType.Integer or JTokenType.Float or JTokenType.Boolean)
            {
                return new JValue(YamlJson.RawText(value) ?? value.ToString(Formatting.None));
            }
        }

        bool matches =
            (expected.HasFlag(JsonObjectType.Integer) && value.Type == JTokenType.Integer)
            || (expected.HasFlag(JsonObjectType.Number) && value.Type is JTokenType.Integer or JTokenType.Float)
            || (expected.HasFlag(JsonObjectType.Boolean) && value.Type == JTokenType.Boolean)
            || (expected.HasFlag(JsonObjectType.Array) && value.Type == JTokenType.Array)
            || (expected.HasFlag(JsonObjectType.Object) && value.Type == JTokenType.Object);

        return matches ? value : throw TypeError(id, type, name, value, Describe(expected));
    }

    private static InvalidOperationException TypeError(string id, string type, string name, JToken value, string expected)
    {
        string shown = value.ToString(Formatting.None);
        if (shown.Length > 60)
        {
            shown = shown[..57] + "...";
        }

        return new InvalidOperationException(
            $"Step '{id}' (type '{type}'): property '{name}'{YamlJson.Where(value)} expects {expected} " +
            $"but got {value.Type.ToString().ToLowerInvariant()} {shown}.");
    }

    private static string Describe(JsonObjectType type)
        => string.Join(" or ", Enum.GetValues<JsonObjectType>()
            .Where(t => t != JsonObjectType.None && type.HasFlag(t))
            .Select(t => t.ToString().ToLowerInvariant()));

    private static string? ReadRequiredText(JObject node, string key)
        => node.TryGetValue(key, StringComparison.Ordinal, out JToken? token)
           && token.Type is JTokenType.String or JTokenType.Integer or JTokenType.Float or JTokenType.Boolean
           && !string.IsNullOrWhiteSpace(YamlJson.RawText(token) ?? token.ToString())
            ? (YamlJson.RawText(token) ?? token.ToString()).Trim()
            : null;

    /// <summary>
    /// <c>display: { start: ..., end: ... }</c>: the templates of the step's <c>Display</c> text in its start and
    /// completion events (see <c>DisplayTextRenderer</c>). Any step type accepts it (it derives from
    /// <see cref="StepBase"/>); an empty or null text means none.
    /// </summary>
    private static void ApplyDisplay(JObject node, IStep step, string id, string type, string where)
    {
        if (!node.TryGetValue(DisplayKey, StringComparison.Ordinal, out JToken? token) || token.Type == JTokenType.Null)
        {
            return;
        }

        if (token is not JObject display)
        {
            throw new InvalidOperationException(
                $"Step '{id}'{YamlJson.Where(token)}: 'display' must be a mapping with 'start' and/or 'end' (templates).");
        }

        if (step is not StepBase stepBase)
        {
            throw new InvalidOperationException(
                $"Step '{id}' (type '{type}'){where} does not accept 'display': the type does not derive from StepBase.");
        }

        foreach (JProperty property in display.Properties())
        {
            if (property.Name is not ("start" or "end"))
            {
                throw new InvalidOperationException(
                    $"Step '{id}'{YamlJson.Where(property.Value)} has unknown key '{property.Name}' in 'display'. Valid keys: start, end.");
            }

            if (property.Value.Type == JTokenType.Null)
            {
                continue;
            }

            if (property.Value.Type is not (JTokenType.String or JTokenType.Integer or JTokenType.Float or JTokenType.Boolean))
            {
                throw new InvalidOperationException(
                    $"Step '{id}'{YamlJson.Where(property.Value)}: 'display.{property.Name}' must be text.");
            }

            string text = YamlJson.RawText(property.Value) ?? property.Value.ToString();
            string? value = string.IsNullOrWhiteSpace(text) ? null : text;
            if (property.Name == "start")
            {
                stepBase.DisplayStart = value;
            }
            else
            {
                stepBase.DisplayEnd = value;
            }
        }
    }

    private IReadOnlyList<string> ReadDependsOn(JObject node, string id)
    {
        if (!node.TryGetValue(DependsOnKey, StringComparison.Ordinal, out JToken? token) || token.Type == JTokenType.Null)
        {
            return [];
        }

        IEnumerable<JToken> items = token is JArray array ? array : [token];
        List<string> result = [];
        foreach (JToken item in items)
        {
            string? dep = item.Type == JTokenType.Object || item.Type == JTokenType.Array
                ? null
                : YamlJson.RawText(item) ?? item.ToString();
            if (string.IsNullOrWhiteSpace(dep))
            {
                throw new InvalidOperationException(
                    $"Step '{id}': 'dependsOn'{YamlJson.Where(token)} must be a step id or a list of step ids.");
            }

            result.Add(dep.Trim());
        }

        return result;
    }

    private (string? Name, JsonSchema? Schema) ResolveSchema(JObject node, string key, string id)
    {
        if (!node.TryGetValue(key, StringComparison.Ordinal, out JToken? token) || token.Type == JTokenType.Null)
        {
            return (null, null);
        }

        string? name = token.Type == JTokenType.String ? token.Value<string>() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException($"Step '{id}': '{key}'{YamlJson.Where(token)} must be a schema name.");
        }

        if (!schemaRegistry.TryGet(name, out JsonSchema? schema))
        {
            throw new InvalidOperationException(
                $"Step '{id}': {key} '{name}'{YamlJson.Where(token)} is not registered. {schemaRegistry.DescribeRegistered()}");
        }

        return (SchemaRegistry.Normalize(name), schema);
    }

    private string AvailableTypes()
    {
        IEnumerable<string> tags = stepRegistry.GetRegisteredTypes().Keys.Order(StringComparer.Ordinal);
        string list = string.Join(", ", tags);
        return list.Length == 0 ? "(none registered)" : list;
    }

    /// <summary>
    /// Strips the reflection and serializer wrappers around an exception thrown by a step's own
    /// configuration code (for instance an LLM step whose role does not exist).
    /// </summary>
    private static Exception Unwrap(Exception ex)
    {
        while (true)
        {
            if (ex is TargetInvocationException { InnerException: { } invoked })
            {
                ex = invoked;
                continue;
            }

            if (ex is JsonSerializationException { InnerException: { } inner }
                && inner is InvalidOperationException or ArgumentException or TargetInvocationException)
            {
                ex = inner;
                continue;
            }

            return ex;
        }
    }

    // ── Ordering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Topological sort (Kahn's algorithm, declaration order among ready steps) + schema-mismatch
    /// check between a step and the steps it depends on (only when both declare the schemas).
    /// </summary>
    private static List<StepNode> ValidateAndOrder(List<StepNode> steps, BuildSession session)
    {
        Dictionary<string, StepNode> byId = steps.ToDictionary(s => s.Id, StringComparer.Ordinal);
        Dictionary<string, int> inDegree = steps.ToDictionary(s => s.Id, _ => 0, StringComparer.Ordinal);
        Dictionary<string, List<string>> dependents = steps.ToDictionary(s => s.Id, _ => new List<string>(), StringComparer.Ordinal);

        foreach (StepNode step in steps)
        {
            foreach (string dep in step.DependsOn)
            {
                if (!byId.TryGetValue(dep, out StepNode? depNode))
                {
                    if (session.Ids.TryGetValue(dep, out var nested) && nested.ParentId != null)
                    {
                        throw new InvalidOperationException(
                            $"Step '{step.Id}' depends on '{dep}', which is nested inside '{nested.ParentId}'; " +
                            "dependsOn can only reference top-level steps.");
                    }

                    throw new InvalidOperationException(
                        $"Step '{step.Id}' depends on '{dep}' which does not exist in the pipeline.");
                }

                inDegree[step.Id]++;
                dependents[dep].Add(step.Id);

                if (depNode.OutputSchemaName != null && step.InputSchemaName != null
                    && !string.Equals(depNode.OutputSchemaName, step.InputSchemaName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Schema mismatch: step '{dep}' outputs schema '{depNode.OutputSchemaName}' " +
                        $"but step '{step.Id}' expects input schema '{step.InputSchemaName}'.");
                }
            }
        }

        Queue<string> queue = new(steps.Where(s => inDegree[s.Id] == 0).Select(s => s.Id));
        List<StepNode> ordered = [];

        while (queue.Count > 0)
        {
            string id = queue.Dequeue();
            ordered.Add(byId[id]);

            foreach (string dependent in dependents[id])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        if (ordered.Count != steps.Count)
        {
            IEnumerable<string> involved = steps.Where(s => inDegree[s.Id] > 0).Select(s => s.Id);
            throw new InvalidOperationException(
                "Cycle detected in pipeline DAG. Check 'dependsOn' references for circular dependencies. " +
                $"Steps involved: {string.Join(", ", involved)}.");
        }

        return ordered;
    }
}
