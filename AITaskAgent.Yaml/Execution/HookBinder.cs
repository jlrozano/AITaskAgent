using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Steps;
using AITaskAgent.YAML.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace AITaskAgent.YAML.Execution;

/// <summary>
/// Resolves the <c>hooks:</c>, <c>validators:</c> and <c>resultValidators:</c> keys of a step against the
/// <see cref="IHookRegistry"/>, the DI services and the <see cref="SchemaRegistry"/>, and attaches them to
/// the step. Every problem is a build error that names the step and the available names.
/// </summary>
internal sealed class HookBinder(IServiceProvider services, SchemaRegistry schemas)
{
    public const string HooksKey = "hooks";
    public const string ValidatorsKey = "validators";
    public const string ResultValidatorsKey = "resultValidators";
    private const string SchemaPrefix = "schema:";

    private static readonly Type ValidatorDelegate = typeof(Func<ValidateArgs, Task<(bool IsValid, string? Error)>>);
    private static readonly Type ResultValidatorDelegate = typeof(Func<ResultValidateArgs, Task<(bool IsValid, string? Error)>>);

    private IHookRegistry? Registry => services.GetService<IHookRegistry>();

    private ILogger? Logger => services.GetService<ILoggerFactory>()?.CreateLogger<YamlPipelineFactory>();

    public static bool IsHookKey(string key) => key is HooksKey or ValidatorsKey or ResultValidatorsKey;

    /// <summary>Binds the three keys of <paramref name="node"/> to <paramref name="step"/>.</summary>
    public void Bind(JObject node, IStep step, string id, string type, string where, string? outputSchemaName)
    {
        bool declared = node.ContainsKey(HooksKey) || node.ContainsKey(ValidatorsKey) || node.ContainsKey(ResultValidatorsKey);
        if (!declared)
        {
            return;
        }

        if (step is not StepBase stepBase)
        {
            throw new InvalidOperationException(
                $"Step '{id}' (type '{type}'){where} does not accept '{HooksKey}'/'{ValidatorsKey}'/'{ResultValidatorsKey}': " +
                "the type does not derive from StepBase.");
        }

        if (node.TryGetValue(HooksKey, StringComparison.Ordinal, out JToken? hooks) && hooks.Type != JTokenType.Null)
        {
            BindHooks(stepBase, hooks, id, type);
        }

        if (node.TryGetValue(ValidatorsKey, StringComparison.Ordinal, out JToken? validators) && validators.Type != JTokenType.Null)
        {
            foreach (string name in ReadNames(validators, ValidatorsKey, id))
            {
                BindValidator(stepBase, name, id, type);
            }
        }

        if (node.TryGetValue(ResultValidatorsKey, StringComparison.Ordinal, out JToken? results) && results.Type != JTokenType.Null)
        {
            foreach (string name in ReadNames(results, ResultValidatorsKey, id))
            {
                BindResultValidator(stepBase, name, id, type, outputSchemaName);
            }
        }
    }

    private void BindHooks(StepBase step, JToken value, string id, string type)
    {
        if (value is not JObject map)
        {
            throw new InvalidOperationException(
                $"Step '{id}': '{HooksKey}'{YamlJson.Where(value)} must be a mapping of slot names to hook names.");
        }

        Type stepType = step.GetType();
        IReadOnlyList<HookSlot> slots = HookSlots.For(stepType);
        Dictionary<string, Delegate> bound = new(StringComparer.Ordinal);

        foreach (JProperty entry in map.Properties())
        {
            HookSlot? slot = slots.FirstOrDefault(s => string.Equals(s.Name, entry.Name, StringComparison.Ordinal));
            if (slot == null)
            {
                throw new InvalidOperationException(
                    $"Step '{id}' (type '{type}'): unknown hook slot '{entry.Name}'{YamlJson.Where(entry.Value)}. " +
                    $"Valid slots: {string.Join(", ", slots.Select(s => s.Name))}.");
            }

            string name = entry.Value.Type == JTokenType.String ? entry.Value.Value<string>()!.Trim() : string.Empty;
            if (name.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Step '{id}': hook '{entry.Name}'{YamlJson.Where(entry.Value)} must be the name of a registered hook.");
            }

            if (HookFactoryFor(name) is { } factory)
            {
                Delegate built = Build(() => factory.CreateHook(name, step, slot), name, id, type, $"hook '{slot.Name}'{YamlJson.Where(entry.Value)}");
                bound[slot.Name] = Fit(built, slot)
                    ?? throw new InvalidOperationException(
                        $"Step '{id}' (type '{type}'): hook '{slot.Name}' '{name}': the factory built a {HookSlots.Describe(built.GetType())}, not {Accepted(slot)}.");
                continue;
            }

            bound[slot.Name] = ResolveSlot(name, slot, id, type, entry.Value);
        }

        if (bound.Count == 0)
        {
            return;
        }

        step.Hooks = HookSlots.Create(stepType, bound);
        foreach (string warning in HookSlots.FindOverrideWarnings(stepType, bound.Keys))
        {
            Logger?.LogWarning("Step {StepId}: {Warning}", id, warning);
        }
    }

    private void BindValidator(StepBase step, string name, string id, string type)
    {
        if (FactoryFor(name) is { } factory)
        {
            step.AddValidator(Build(() => factory.CreateValidator(name, step), name, id, type, "validator"));
            return;
        }

        IStepValidator[] di = [.. (services.GetServices<IStepValidator>()).Where(v => string.Equals(v.Id, name, StringComparison.Ordinal))];
        bool inRegistry = Registry?.List().Any(h => h.Kind == HookKind.StepValidator && h.Name == name) == true;

        if (di.Length > 1 || (di.Length == 1 && inRegistry))
        {
            throw new InvalidOperationException(
                $"Step '{id}' (type '{type}'): validator '{name}' is defined more than once " +
                "(hook registry and/or IStepValidator services). A name must have a single source.");
        }

        if (di.Length == 1)
        {
            step.AddValidator(di[0]);
            return;
        }

        Delegate hook = Resolve(name, HookKind.StepValidator, ValidatorDelegate, id, type, "a step validator", null);
        step.AddValidator(new DelegateValidator(name, step, (Func<ValidateArgs, Task<(bool IsValid, string? Error)>>)hook));
    }

    private void BindResultValidator(StepBase step, string name, string id, string type, string? outputSchemaName)
    {
        if (name.StartsWith(SchemaPrefix, StringComparison.Ordinal))
        {
            string schemaName = name[SchemaPrefix.Length..].Trim();
            if (!schemas.TryGet(schemaName, out JsonSchema? schema))
            {
                throw new InvalidOperationException(
                    $"Step '{id}': result validator '{name}' names a schema that is not registered. {schemas.DescribeRegistered()}");
            }

            if (outputSchemaName != null && string.Equals(SchemaRegistry.Normalize(schemaName), outputSchemaName, StringComparison.OrdinalIgnoreCase))
            {
                Logger?.LogWarning(
                    "Step {StepId}: resultValidators '{Name}' repeats its outputSchema '{Schema}': the value is already validated against it.",
                    id, name, outputSchemaName);
            }

            step.AddResultValidator(new SchemaResultValidator(name, schema));
            return;
        }

        if (FactoryFor(name) is { } factory)
        {
            step.AddResultValidator(Build(() => factory.CreateResultValidator(name, step), name, id, type, "result validator"));
            return;
        }

        IResultValidator[] di = [.. services.GetServices<IResultValidator>().Where(v => string.Equals(v.Id, name, StringComparison.Ordinal))];
        bool inRegistry = Registry?.List().Any(h => h.Kind == HookKind.ResultValidator && h.Name == name) == true;

        if (di.Length > 1 || (di.Length == 1 && inRegistry))
        {
            throw new InvalidOperationException(
                $"Step '{id}' (type '{type}'): result validator '{name}' is defined more than once " +
                "(hook registry and/or IResultValidator services). A name must have a single source.");
        }

        if (di.Length == 1)
        {
            step.AddResultValidator(di[0]);
            return;
        }

        Delegate hook = Resolve(name, HookKind.ResultValidator, ResultValidatorDelegate, id, type, "a result validator", null);
        step.AddResultValidator(new DelegateResultValidator(name, step.Name, (Func<ResultValidateArgs, Task<(bool IsValid, string? Error)>>)hook));
    }

    /// <summary>
    /// A registered hook for <paramref name="slot"/>: of the property's type, or of the slot's "after" form (adapted with
    /// <see cref="HookForms.AfterAsAround"/>); otherwise the error says which of the forms the slot takes.
    /// </summary>
    private Delegate ResolveSlot(string name, HookSlot slot, string id, string type, JToken at)
    {
        IHookRegistry? registry = Registry;
        if (registry != null)
        {
            foreach (Type accepted in slot.AcceptedTypes)
            {
                if (registry.TryGet(name, HookKind.Hook, accepted, out Delegate? hook) && hook != null && Fit(hook, slot) is { } fitted)
                {
                    return fitted;
                }
            }
        }

        // The registry's own diagnosis (another kind, another signature, unknown), with the forms the slot takes.
        return Resolve(name, HookKind.Hook, slot.DelegateType, id, type, $"slot '{slot.Name}'", at, Accepted(slot));
    }

    /// <summary>
    /// <paramref name="hook"/> as the slot's property type (an after-form hook adapted), or null when it is of neither form. The after form
    /// must return exactly <see cref="Task"/>: delegate variance would also let a <c>Task&lt;T&gt;</c> method through, and its value would be
    /// silently ignored.
    /// </summary>
    private static Delegate? Fit(Delegate hook, HookSlot slot)
        => slot.DelegateType.IsInstanceOfType(hook) ? hook
            : slot.AfterType is { } after && after.IsInstanceOfType(hook) && hook.GetType().GetMethod("Invoke")?.ReturnType == typeof(Task)
                ? HookForms.AfterAsAround(slot.DelegateType, hook)
            : null;

    /// <summary>The forms a hook of <paramref name="slot"/> may take, as text.</summary>
    private static string Accepted(HookSlot slot)
        => string.Join(" or ", slot.AcceptedTypes.Select(HookSlots.Describe));

    /// <summary>The <see cref="IHookFactory"/> whose prefix starts <paramref name="name"/> (<c>js:</c>), or null.</summary>
    private IHookFactory? HookFactoryFor(string name)
        => services.GetServices<IHookFactory>()
            .FirstOrDefault(f => !string.IsNullOrEmpty(f.Prefix) && f.Prefix != SchemaPrefix && name.StartsWith(f.Prefix, StringComparison.Ordinal));

    /// <summary>The <see cref="IValidatorFactory"/> whose prefix starts <paramref name="name"/> (<c>js:</c>), or null.</summary>
    private IValidatorFactory? FactoryFor(string name)
        => services.GetServices<IValidatorFactory>()
            .FirstOrDefault(f => !string.IsNullOrEmpty(f.Prefix) && f.Prefix != SchemaPrefix && name.StartsWith(f.Prefix, StringComparison.Ordinal));

    /// <summary>Runs a factory; its <see cref="InvalidOperationException"/> becomes a build error that names the step.</summary>
    private static T Build<T>(Func<T> create, string name, string id, string type, string label)
    {
        try
        {
            return create();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException($"Step '{id}' (type '{type}'): {label} '{name}': {ex.Message}", ex);
        }
    }

    /// <summary>Gets a delegate of the expected kind and type, or throws an error that says which of the four things is wrong.</summary>
    private Delegate Resolve(string name, HookKind kind, Type delegateType, string id, string type, string expected, JToken? at, string? expectedSignature = null)
    {
        IHookRegistry? registry = Registry;
        if (registry != null && registry.TryGet(name, kind, delegateType, out Delegate? hook) && hook != null)
        {
            return hook;
        }

        string where = at == null ? "" : YamlJson.Where(at);
        HookInfo? info = registry?.List().FirstOrDefault(h => h.Name == name);
        if (info != null && info.Kind != kind)
        {
            throw new InvalidOperationException(
                $"Step '{id}' (type '{type}'){where}: '{name}' is a {Describe(info.Kind)}, not {expected}.");
        }

        if (info != null)
        {
            throw new InvalidOperationException(
                $"Step '{id}' (type '{type}'){where}: the signature of '{name}' is {HookSlots.Describe(info.DelegateType)}, " +
                $"but {expected} expects {expectedSignature ?? HookSlots.Describe(delegateType)}.");
        }

        string available = string.Join(", ", (registry?.List() ?? []).Where(h => h.Kind == kind).Select(h => h.Name));
        throw new InvalidOperationException(
            $"Step '{id}' (type '{type}'){where}: unknown {Describe(kind)} '{name}'. " +
            $"Available: {(available.Length == 0 ? "(none registered)" : available)}.");
    }

    private static string Describe(HookKind kind) => kind switch
    {
        HookKind.Hook => "hook",
        HookKind.StepValidator => "step validator",
        _ => "result validator",
    };

    private static List<string> ReadNames(JToken token, string key, string id)
    {
        IEnumerable<JToken> items = token is JArray array ? array : [token];
        List<string> names = [];
        foreach (JToken item in items)
        {
            string? name = item.Type == JTokenType.String ? item.Value<string>()?.Trim() : null;
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException(
                    $"Step '{id}': '{key}'{YamlJson.Where(token)} must be a name or a list of names.");
            }

            names.Add(name);
        }

        return names;
    }
}
