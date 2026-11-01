using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.Script.Hosting;
using AITaskAgent.Script.Steps;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AITaskAgent.Script.Validators;

/// <summary>Which hook slots take a JavaScript hook (architecture §18.8), for hosts and designers. Additive (2026-10-23).</summary>
public static class JsHookSlots
{
    /// <summary>
    /// Why <paramref name="slot"/> is C#-only (its arguments are objects with behaviour, not data), or null when it takes a JavaScript hook. Since
    /// 2026-10-24 the synchronous slots take one too (the call waits for the worker); of the framework's slots only <c>onConversationCreated</c>
    /// stays C#-only.
    /// </summary>
    public static string? Unsupported(HookSlot slot) => JsHook.Supports(slot);
}

/// <summary>
/// Builds the <c>js:</c> hooks of the YAML (<see cref="IHookFactory"/>, architecture §18.8): <c>hooks: { postProcess: js:orders.stamp }</c>.
/// The library must be known to the host's <see cref="IScriptCatalog"/> when the pipeline is built; whether it exports the function is checked
/// when the run starts (the run's worker lists it) and by the designer's validation. Only a slot whose arguments have a data view
/// (<see cref="IHookData{TSelf}"/>) and whose delegate has a known form, asynchronous or synchronous, takes a JavaScript hook
/// (<see cref="JsHook.Supports"/>); the others are C#-only.
/// </summary>
internal sealed class JsHookFactory(IServiceProvider services) : IHookFactory
{
    public string Prefix => JsValidatorName.Prefix;

    public Delegate CreateHook(string name, StepBase step, HookSlot slot)
    {
        (string library, string function) = JsValidatorName.Parse(name, out string? problem, "hook")
            ?? throw new InvalidOperationException(problem);
        if (JsHook.Supports(slot) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        IScriptCatalog? catalog = services.GetService<IScriptCatalog>();
        if (catalog?.FindLibrary(library) == null)
        {
            string known = string.Join(", ", catalog?.Libraries.Select(l => l.Name) ?? []);
            throw new InvalidOperationException(
                $"the JavaScript library '{library}' is not known to this host. Libraries: {(known.Length == 0 ? "(none)" : known)}.");
        }

        return new JsHook(name.Trim(), library, function, step, slot).CreateDelegate();
    }
}

/// <summary>
/// A JavaScript hook (architecture §18.8): the function <see cref="Function"/> exported by the library <see cref="Library"/>, run in the run's
/// script worker like a JavaScript validator (same bundle, limits per call and kill/respawn; a run whose only JavaScript is its hooks starts a
/// worker for them). It receives one argument, <c>args</c>: the data view of the slot's arguments (<see cref="HookData"/>; read-only members
/// throw a <c>TypeError</c> naming the property when written) plus <c>args.vars</c>. It returns nothing: what it changes in the writable
/// members (<c>args.result</c>, <c>args.message</c>...) comes back as a patch the host applies to the real arguments, and its writes to
/// <c>args.vars</c> as a patch applied to <c>PipelineContext.Metadata</c>. Untouched, the default value stays.
/// </summary>
/// <remarks>
/// It runs where a C# hook of the same slot runs: an around slot (<see cref="HookAround{TArgs, T}"/>) in the after form, at the end of the
/// virtual method with the step's own value in the arguments; a notification slot (<c>Func&lt;TArgs, Task&gt;</c>) as a notification; a
/// decision slot (<c>Func&lt;TArgs, Task&lt;T&gt;&gt;</c> whose arguments are <see cref="IHookOutcome{T}"/>) through the outcome; a synchronous
/// slot (<c>Action&lt;TArgs&gt;</c>, or <c>Func&lt;TArgs, T&gt;</c> whose arguments are <see cref="IHookOutcome{T}"/>) with a bounded blocking wait
/// (<see cref="RunBlocking"/>). A JavaScript error, a broken limit, a wait that expires, a return value or a write that does not fit is a
/// <see cref="StepHookException"/>, handled as an exception of a C# hook of that slot (fatal for most slots; logged and ignored for the
/// <c>enrich*</c> ones).
/// </remarks>
internal sealed class JsHook(string name, string library, string function, StepBase step, HookSlot slot) : IPipelineRunHookProvider, IJsFunction
{
    /// <inheritdoc/>
    public string Id { get; } = name;

    /// <inheritdoc/>
    public string Library { get; } = library;

    /// <inheritdoc/>
    public string Function { get; } = function;

    /// <inheritdoc/>
    public StepBase Step { get; } = step;

    /// <summary>The slot it is bound to.</summary>
    public HookSlot Slot { get; } = slot;

    /// <inheritdoc/>
    public string Label => $"hook '{Slot.Name}'";

    /// <inheritdoc/>
    IPipelineRunHook IPipelineRunHookProvider.RunHook => ScriptRunHook.Instance;

    /// <summary>Why <paramref name="slot"/> cannot take a JavaScript hook (C#-only), or null when it can.</summary>
    public static string? Supports(HookSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.ArgsType is not { } args || HookData.MembersOf(args) == null)
        {
            return $"the slot '{slot.Name}' is C#-only: its arguments ({slot.ArgsType?.Name ?? "none"}) are objects with behaviour, not data a JavaScript function can receive.";
        }

        return Shape(slot) == null
            ? $"the slot '{slot.Name}' is C#-only: its delegate ({HookSlots.Describe(slot.DelegateType)}) has no form a JavaScript hook can take."
            : null;
    }

    /// <summary>The delegate of the slot's property type, with this instance as its target (so the run's preparation finds it).</summary>
    public Delegate CreateDelegate()
    {
        (string method, Type[] arguments) = Shape(Slot) ?? throw new InvalidOperationException(Supports(Slot));
        return typeof(JsHook).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(arguments)
            .CreateDelegate(Slot.DelegateType, this);
    }

    /// <summary>
    /// Which form the slot's delegate is, as (method of this class, its type arguments); null for a delegate no JavaScript hook can take. Asynchronous:
    /// an around slot (after form), <c>Func&lt;TArgs, Task&gt;</c> (notification) and <c>Func&lt;TArgs, Task&lt;T&gt;&gt;</c> with
    /// <see cref="IHookOutcome{T}"/> arguments (decision). Synchronous (2026-10-24): <c>Action&lt;TArgs&gt;</c> and <c>Func&lt;TArgs, T&gt;</c> with
    /// <see cref="IHookOutcome{T}"/> arguments (transform), which wait for the worker (<see cref="RunBlocking"/>).
    /// </summary>
    private static (string Method, Type[] Arguments)? Shape(HookSlot slot)
    {
        Type type = slot.DelegateType;
        if (!type.IsGenericType)
        {
            return null;
        }

        Type definition = type.GetGenericTypeDefinition();
        Type[] arguments = type.GetGenericArguments();
        if (definition == typeof(HookAround<,>) && slot.AfterType != null)
        {
            return (nameof(AroundAsync), arguments);
        }

        if (definition == typeof(Action<>))
        {
            return (nameof(NotifySync), arguments);
        }

        if (definition != typeof(Func<,>))
        {
            return null;
        }

        if (arguments[1] == typeof(Task))
        {
            return (nameof(NotifyAsync), [arguments[0]]);
        }

        if (arguments[1].IsGenericType && arguments[1].GetGenericTypeDefinition() == typeof(Task<>))
        {
            return typeof(IHookOutcome<>).MakeGenericType(arguments[1].GetGenericArguments()[0]).IsAssignableFrom(arguments[0])
                ? (nameof(DecideAsync), [arguments[0], arguments[1].GetGenericArguments()[0]])
                : null;
        }

        return !typeof(Task).IsAssignableFrom(arguments[1]) && typeof(IHookOutcome<>).MakeGenericType(arguments[1]).IsAssignableFrom(arguments[0])
            ? (nameof(TransformSync), arguments)
            : null;
    }

    private async Task<T> AroundAsync<TArgs, T>(TArgs args, Func<Task<T>> next) where TArgs : IHookOutcome<T>
    {
        args.Outcome = await next();
        await RunAsync(args!, ContextOf(args!));
        return args.Outcome;
    }

    private Task NotifyAsync<TArgs>(TArgs args) => RunAsync(args!, ContextOf(args!));

    private async Task<T> DecideAsync<TArgs, T>(TArgs args) where TArgs : IHookOutcome<T>
    {
        await RunAsync(args!, ContextOf(args!));
        return args.Outcome;
    }

    private void NotifySync<TArgs>(TArgs args) => RunBlocking(args!, ContextOf(args!));

    private T TransformSync<TArgs, T>(TArgs args) where TArgs : IHookOutcome<T>
    {
        RunBlocking(args!, ContextOf(args!));
        return args.Outcome;
    }

    /// <summary>The run's context and cancellation, read from the arguments (every hook argument record carries its <c>Context</c>).</summary>
    private static (PipelineContext Context, CancellationToken Token) ContextOf(object args)
    {
        Type type = args.GetType();
        PipelineContext context = type.GetProperty("Context")?.GetValue(args) as PipelineContext
            ?? throw new InvalidOperationException($"{type.Name} carries no PipelineContext.");
        CancellationToken token = type.GetProperty("CancellationToken")?.GetValue(args) is CancellationToken t ? t : CancellationToken.None;
        return (context, token);
    }

    private async Task RunAsync(object args, (PipelineContext Context, CancellationToken Token) run)
    {
        (JObject fields, JObject payload) = Prepare(args);
        JObject envelope = await JsFunctionCall.InvokeAsync(this, ScriptCallKinds.Hook, payload, run.Context, run.Token);
        Finish(args, fields, envelope, run.Context);
    }

    /// <summary>
    /// Extra wait of a synchronous slot besides the call's own limit (<c>TimeoutMs</c> plus the worker client's 2 s kill grace): queueing behind
    /// another call of the run (calls are serialized) and starting a respawned worker.
    /// </summary>
    internal const int SyncSlackMs = 10_000;

    /// <summary>Test seam: replaces the bounded wait of a synchronous slot (null = the options' value).</summary>
    internal static int? SyncWaitOverrideMs { get; set; }

    /// <summary>How long a synchronous slot waits for its call: the options' <c>TimeoutMs</c> + 2 s kill grace + <see cref="SyncSlackMs"/>.</summary>
    internal static int SyncWaitMs(JsValidatorOptions options) => SyncWaitOverrideMs ?? options.TimeoutMs + 2_000 + SyncSlackMs;

    /// <summary>
    /// A synchronous slot (<c>enrich*</c>, <c>configureRequest</c>): the same call as <see cref="RunAsync"/>, waited for with a bounded blocking wait
    /// (sync over async). The arguments are read and the patch applied on the caller's thread; only the worker call runs on the thread pool
    /// (<c>Task.Run</c>: no synchronization context is captured, so nothing it awaits needs the blocked thread, and the caller holds no lock the call
    /// takes: the run's session gate is taken and released inside the call). The wait is bounded by <see cref="SyncWaitMs"/>: when it expires the
    /// call is cancelled (the worker client kills the worker; the next call starts another) and the hook fails with a <see cref="StepHookException"/>
    /// (a call that still has not returned one second later is abandoned: its late answer is never applied, because the patch is applied here).
    /// </summary>
    private void RunBlocking(object args, (PipelineContext Context, CancellationToken Token) run)
    {
        (JObject fields, JObject payload) = Prepare(args);
        JsValidatorOptions options = run.Context.ServiceProvider.GetService<JsValidatorOptions>() ?? new JsValidatorOptions();
        int waitMs = SyncWaitMs(options);
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        bound.CancelAfter(waitMs);
        Task<JObject> call = Task.Run(() => JsFunctionCall.InvokeAsync(this, ScriptCallKinds.Hook, payload, run.Context, bound.Token), CancellationToken.None);

        JObject envelope;
        try
        {
            if (!call.Wait(waitMs + 1_000, CancellationToken.None))
            {
                bound.Cancel();
                throw JsFunctionCall.Fail(this, $"the script worker did not answer within {waitMs} ms (a synchronous slot waits at most that long).");
            }

            envelope = call.Result;
        }
        catch (AggregateException ex) when (ex.InnerException is { } inner)
        {
            if (inner is OperationCanceledException && !run.Token.IsCancellationRequested)
            {
                throw JsFunctionCall.Fail(this, $"the script worker did not answer within {waitMs} ms (a synchronous slot waits at most that long).", inner);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }

        Finish(args, fields, envelope, run.Context);
    }

    /// <summary>The data view of the arguments (<c>fields</c>) and the payload of the call.</summary>
    private (JObject Fields, JObject Payload) Prepare(object args)
    {
        IReadOnlyList<HookDataMember> members = HookData.MembersOf(args.GetType())
            ?? throw JsFunctionCall.Fail(this, $"{args.GetType().Name} has no data view.");
        JObject fields;
        try
        {
            fields = HookData.Read(args);
        }
        catch (InvalidOperationException ex)
        {
            throw JsFunctionCall.Fail(this, ex.Message, ex);
        }

        JObject payload = new()
        {
            ["fields"] = fields,
            ["writable"] = new JArray(members.Where(m => m.Writable).Select(m => m.Name)),
        };
        return (fields, payload);
    }

    /// <summary>Checks the return, then applies the patch of the writable members to the arguments and the <c>args.vars</c> patch to the variables.</summary>
    private void Finish(object args, JObject fields, JObject envelope, PipelineContext context)
    {
        IReadOnlyList<HookDataMember> members = HookData.MembersOf(args.GetType())!;
        string type = envelope.Value<string>("type") ?? "undefined";
        if (type != "undefined")
        {
            string writable = string.Join(", ", members.Where(m => m.Writable).Select(m => "args." + m.Name));
            throw JsFunctionCall.Fail(this,
                $"it returned {(type == "promise" ? "a Promise (a JavaScript hook is synchronous)" : type)}; a JavaScript hook returns nothing and " +
                (writable.Length == 0 ? "only writes args.vars." : $"changes {writable} (or args.vars)."));
        }

        Apply(args, members, fields, envelope["fields"] as JArray);
        JsFunctionCall.ApplyVariables(this, context, envelope);
    }

    /// <summary>Applies the patch of the writable members to a copy of their values and writes each member it touched back to the arguments, in the view's order.</summary>
    private void Apply(object args, IReadOnlyList<HookDataMember> members, JObject fields, JArray? patch)
    {
        if (patch is not { Count: > 0 })
        {
            return;
        }

        Dictionary<string, object?> values = fields.Properties().ToDictionary(p => p.Name, p => (object?)p.Value.DeepClone(), StringComparer.Ordinal);
        // Written back in the order of the data view (not of the writes): a view whose members overlap (configureRequest's options and temperature)
        // documents which one wins.
        HashSet<string> written = [.. patch.Select(e => (e["path"] as JArray)?.FirstOrDefault()?.Value<string>()).OfType<string>()];
        List<string> touched = [.. members.Select(m => m.Name).Where(written.Contains)];
        try
        {
            VariablePatch.Apply(values, patch);
            foreach (string member in touched)
            {
                HookData.Write(args, member, values.TryGetValue(member, out object? value) ? value as JToken : null);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
        {
            throw JsFunctionCall.Fail(this, $"its changes could not be applied: {ex.Message}", ex);
        }
    }
}
