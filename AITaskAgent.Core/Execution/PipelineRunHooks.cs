using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Calls the <see cref="IPipelineRunHook"/>s named by the steps of a run when it starts, and releases what they
/// prepared when it ends.
/// </summary>
internal sealed class PipelineRunHooks : IAsyncDisposable
{
    private static readonly PipelineRunHooks None = new([], null);

    private readonly List<IAsyncDisposable> _scopes;
    private readonly ILogger? _logger;

    private PipelineRunHooks(List<IAsyncDisposable> scopes, ILogger? logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>
    /// Groups the known steps of the run by the hook they name and calls each hook once. If one throws, what the
    /// hooks before it prepared is released and the exception propagates (the run fails before any step runs).
    /// </summary>
    public static async Task<PipelineRunHooks> StartAsync(
        IReadOnlyList<IStep> steps, PipelineContext context, ILogger logger, CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, IStep>> known = [];
        Collect(steps, "", known);

        Dictionary<IPipelineRunHook, List<KeyValuePair<string, IStep>>> byHook = new(ReferenceEqualityComparer.Instance);
        List<IPipelineRunHook> order = [];
        void Add(IPipelineRunHook hook, KeyValuePair<string, IStep> entry)
        {
            if (!byHook.TryGetValue(hook, out List<KeyValuePair<string, IStep>>? list))
            {
                byHook[hook] = list = [];
                order.Add(hook);
            }

            // A step that names a hook more than once (itself and its validators) is passed once.
            if (!list.Any(e => ReferenceEquals(e.Value, entry.Value) && e.Key == entry.Key))
            {
                list.Add(entry);
            }
        }

        foreach (KeyValuePair<string, IStep> entry in known)
        {
            if (entry.Value is IPipelineRunHookProvider { RunHook: { } hook })
            {
                Add(hook, entry);
            }

            // Validators, result validators and the targets of the step's hooks may name a run hook too (JavaScript validators and hooks
            // share the run's script worker): the step is passed.
            if (entry.Value is StepBase stepBase)
            {
                IEnumerable<object?> parts = stepBase.Validators.Cast<object?>().Concat(stepBase.ResultValidators)
                    .Concat(Hooks.HookSlots.Bound(stepBase.Hooks).Select(h => h.Value.Target));
                foreach (object? part in parts)
                {
                    if (part is IPipelineRunHookProvider { RunHook: { } partHook })
                    {
                        Add(partHook, entry);
                    }
                }
            }
        }

        if (order.Count == 0)
        {
            return None;
        }

        List<IAsyncDisposable> scopes = [];
        try
        {
            foreach (IPipelineRunHook hook in order)
            {
                if (await hook.RunStartingAsync(byHook[hook], context, cancellationToken) is { } scope)
                {
                    scopes.Add(scope);
                }
            }
        }
        catch
        {
            await new PipelineRunHooks(scopes, logger).DisposeAsync();
            throw;
        }

        return new PipelineRunHooks(scopes, logger);
    }

    /// <summary>Releases in reverse order; a failure to release is logged, never thrown over the run's own outcome.</summary>
    public async ValueTask DisposeAsync()
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            try
            {
                await _scopes[i].DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "A pipeline run hook failed to release what it prepared.");
            }
        }

        _scopes.Clear();
    }

    /// <summary>The steps whose place is known before the run (same containers as <see cref="StepValidation.ValidateRequiredContext"/>).</summary>
    private static void Collect(IReadOnlyList<IStep> steps, string parent, List<KeyValuePair<string, IStep>> known)
    {
        foreach (IStep step in steps)
        {
            string path = parent.Length == 0 ? step.Name : $"{parent}/{step.Name}";
            known.Add(KeyValuePair.Create(path, step));

            switch (step)
            {
                case GroupStep group:
                    Collect(group.Steps, path, known);
                    break;
                case ParallelStep parallel:
                    Collect(parallel.Branches, path, known);
                    break;
                case LoopStep loop:
                    Collect(loop.Steps, path, known);
                    break;
                case DeclarativeSwitchStep switchStep:
                    foreach (KeyValuePair<string, List<IStep>> @case in switchStep.Cases)
                    {
                        Collect(@case.Value, $"{path}[{@case.Key}]", known);
                    }

                    if (switchStep.Default is not null)
                    {
                        Collect(switchStep.Default, $"{path}[default]", known);
                    }

                    break;
            }
        }
    }
}
