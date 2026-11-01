using AITaskAgent.Core.Models;
using AITaskAgent.Script.Protocol;
using Newtonsoft.Json.Linq;
using System.Runtime.CompilerServices;

namespace AITaskAgent.Script.Isolation;

/// <summary>
/// The JavaScript of one run: its pieces (kept by the host, the source of every worker of the run) and the worker that
/// has them compiled. Calls are serialized: one V8 isolate, one call at a time, each in a fresh context.
/// </summary>
/// <remarks>
/// <para>A call that breaks a limit, is cancelled or kills the worker leaves no usable worker: the next call starts a
/// new one and compiles the same pieces again (nothing of the old one is reused). Disposing the session (the end of the
/// run, whatever the outcome) kills the worker.</para>
/// <para>Serializing means a ParallelStep whose branches run scripts runs those scripts one after another; a script that
/// hangs holds the others at most its own <c>timeoutMs</c> plus the host's 2 s grace, after which its worker is killed
/// and the queue goes on with a fresh one.</para>
/// </remarks>
internal sealed class ScriptRunSession : IAsyncDisposable
{
    private readonly string _label;
    private readonly IReadOnlyList<BundlePiece> _pieces;
    private readonly int _heapMb;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ScriptWorker? _worker;
    private bool _disposed;

    private ScriptRunSession(string label, IReadOnlyList<BundlePiece> pieces, int heapMb)
    {
        _label = label;
        _pieces = pieces;
        _heapMb = heapMb;
    }

    /// <summary>
    /// Starts the run's worker and compiles the pieces once. Compile errors come back as <c>Errors</c> (and no session);
    /// a worker that cannot be started or capped comes back as <c>Failure</c>.
    /// </summary>
    public static async Task<(ScriptRunSession? Session, IReadOnlyList<(string Id, string Error)> Errors, string? Failure, Exception? Exception)> OpenAsync(
        string label, IReadOnlyList<BundlePiece> pieces, int heapMb, CancellationToken cancellationToken)
    {
        (ScriptRunSession? session, IReadOnlyList<(string Id, string Error)> errors, string? failure, Exception? exception, _) =
            await OpenAsync(label, pieces, heapMb, [], cancellationToken);
        return (session, errors, failure, exception);
    }

    /// <summary>
    /// <see cref="OpenAsync(string, IReadOnlyList{BundlePiece}, int, CancellationToken)"/> that also lists the functions the libraries
    /// <paramref name="exports"/> export (<c>{ lib: { functions } | { error } }</c>; null when none was asked or the bundle did not compile).
    /// </summary>
    public static async Task<(ScriptRunSession? Session, IReadOnlyList<(string Id, string Error)> Errors, string? Failure, Exception? Exception, JObject? Exports)> OpenAsync(
        string label, IReadOnlyList<BundlePiece> pieces, int heapMb, IReadOnlyList<string> exports, CancellationToken cancellationToken)
    {
        ScriptRunSession session = new(label, pieces, heapMb);
        (ScriptWorker? worker, IReadOnlyList<(string Id, string Error)> errors, string? failure, Exception? exception, JObject? listed) =
            await StartWorkerAsync(label, pieces, heapMb, checkOnly: false, exports, cancellationToken);
        if (worker == null)
        {
            return (null, errors, failure, exception, null);
        }

        session._worker = worker;
        return (session, [], null, null, listed);
    }

    /// <summary>Compiles the pieces in a worker that keeps nothing: the designer's syntax check, same bundle as a run.</summary>
    public static async Task<(IReadOnlyList<(string Id, string Error)> Errors, string? Failure)> CheckAsync(
        IReadOnlyList<BundlePiece> pieces, CancellationToken cancellationToken)
    {
        (IReadOnlyList<(string Id, string Error)> errors, string? failure, _) = await CheckAsync(pieces, [], cancellationToken);
        return (errors, failure);
    }

    /// <summary>The syntax check that also lists the functions the libraries <paramref name="exports"/> export (null when the bundle did not compile).</summary>
    public static async Task<(IReadOnlyList<(string Id, string Error)> Errors, string? Failure, JObject? Exports)> CheckAsync(
        IReadOnlyList<BundlePiece> pieces, IReadOnlyList<string> exports, CancellationToken cancellationToken)
    {
        (ScriptWorker? worker, IReadOnlyList<(string Id, string Error)> errors, string? failure, _, JObject? listed) =
            await StartWorkerAsync("syntax-check", pieces, ScriptLimits.DefaultHeapMb, checkOnly: true, exports, cancellationToken);
        worker?.Dispose();
        return (errors, failure, listed);
    }

    /// <summary>
    /// Calls <paramref name="function"/>. Waits for the call in flight, if any; starts a new worker from the kept pieces
    /// when the last one is gone.
    /// </summary>
    public async Task<WorkerResult> CallAsync(string function, WorkerCall call, IScriptHostBridge bridge, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new WorkerResult(null, "the execution was cancelled.", null);
        }

        try
        {
            if (_disposed)
            {
                return new WorkerResult(null, "the run that owned the script worker has ended.", null);
            }

            ScriptWorker? worker = _worker;
            if (worker is not { IsUsable: true })
            {
                worker?.Dispose();
                _worker = null;
                (worker, IReadOnlyList<(string Id, string Error)> errors, string? failure, Exception? exception, _) =
                    await StartWorkerAsync(_label, _pieces, _heapMb, checkOnly: false, [], cancellationToken);
                if (worker == null)
                {
                    return new WorkerResult(null, failure ?? Describe(errors), exception);
                }

                if (_disposed)
                {
                    // The run ended while this worker was starting.
                    worker.Dispose();
                    return new WorkerResult(null, "the run that owned the script worker has ended.", null);
                }

                _worker = worker;
            }

            // A local: the run may end (DisposeAsync) while the call is in flight.
            WorkerResult result = await worker.CallAsync(function, call, bridge, cancellationToken);
            if (!worker.IsUsable)
            {
                worker.Dispose();
                Interlocked.CompareExchange(ref _worker, null, worker);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Kills the worker. A call still in flight (the run ended under it) sees its worker die.</summary>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        Interlocked.Exchange(ref _worker, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The compile errors as one message, each under its id.</summary>
    public static string Describe(IReadOnlyList<(string Id, string Error)> errors)
        => string.Join(Environment.NewLine, errors.Select(e => e.Id.Length == 0 ? e.Error : $"{e.Id}: {e.Error}"));

    private static async Task<(ScriptWorker? Worker, IReadOnlyList<(string Id, string Error)> Errors, string? Failure, Exception? Exception, JObject? Exports)> StartWorkerAsync(
        string label, IReadOnlyList<BundlePiece> pieces, int heapMb, bool checkOnly, IReadOnlyList<string> exports, CancellationToken cancellationToken)
    {
        (ScriptWorker? worker, string? error, Exception? exception) = await ScriptWorker.StartAsync(label, heapMb, cancellationToken);
        if (worker == null)
        {
            return (null, [], error, exception, null);
        }

        LoadResult load = await worker.LoadAsync(pieces, heapMb, checkOnly, cancellationToken, exports);
        if (load.Error != null)
        {
            worker.Dispose();
            return (null, [], load.Error, load.Exception, null);
        }

        if (load.Errors.Count > 0)
        {
            worker.Dispose();
            return (null, load.Errors, null, null, load.Exports);
        }

        return (worker, [], null, null, load.Exports);
    }
}

/// <summary>
/// Which run session a context belongs to. Keyed by the context's <see cref="PipelineContext.Metadata"/> instance, which
/// the branch clones of a ParallelStep share with their parent, so every step of the run finds the same session. A run
/// nested in another on the same context stacks its session on top and removes it when it ends.
/// </summary>
internal static class ScriptRunSessions
{
    private static readonly ConditionalWeakTable<object, Holder> Sessions = [];

    /// <summary>Attaches <paramref name="session"/> with the function of each bundled step; disposing the scope detaches and disposes it.</summary>
    public static IAsyncDisposable Attach(PipelineContext context, ScriptRunSession session, Dictionary<object, string> functions)
    {
        Holder holder = Sessions.GetValue(context.Metadata, _ => new Holder());
        Entry entry = new(session, functions);
        lock (holder)
        {
            holder.Entries.Add(entry);
        }

        return new Scope(holder, entry);
    }

    /// <summary>The session and function of <paramref name="step"/> in a run of <paramref name="context"/> that bundled it (innermost run first).</summary>
    public static (ScriptRunSession Session, string Function)? Lookup(PipelineContext context, object step)
    {
        if (!Sessions.TryGetValue(context.Metadata, out Holder? holder))
        {
            return null;
        }

        lock (holder)
        {
            for (int i = holder.Entries.Count - 1; i >= 0; i--)
            {
                if (holder.Entries[i].Functions.TryGetValue(step, out string? function))
                {
                    return (holder.Entries[i].Session, function);
                }
            }
        }

        return null;
    }

    private sealed record Entry(ScriptRunSession Session, Dictionary<object, string> Functions);

    private sealed class Holder
    {
        public List<Entry> Entries { get; } = [];
    }

    private sealed class Scope(Holder holder, Entry entry) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (holder)
            {
                holder.Entries.Remove(entry);
            }

            await entry.Session.DisposeAsync();
        }
    }
}
