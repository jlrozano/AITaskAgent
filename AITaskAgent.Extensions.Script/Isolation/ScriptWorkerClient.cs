using AITaskAgent.Script.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AITaskAgent.Script.Isolation;

/// <summary>
/// The parameters of one call, as the worker receives them: a step's function, or (with <paramref name="Export"/>) a JavaScript validator or
/// hook, the function <paramref name="Export"/> of the library the session's function names, with its payload as input
/// (<paramref name="Kind"/>: <c>validate</c> by default with an export, or <c>hook</c>).
/// </summary>
internal readonly record struct WorkerCall(
    string Name,
    string InputJson,
    int TimeoutMs,
    int HeapMb,
    int StackKb,
    IReadOnlyList<string>? Uses = null,
    IReadOnlyList<string>? Allow = null,
    int MaxCalls = ScriptLimits.DefaultMaxCalls,
    int MaxCallBytes = ScriptLimits.DefaultMaxCallBytes,
    string? Export = null,
    string? Kind = null);

/// <summary>How a call ended: a value, or an error message ready to report.</summary>
internal readonly record struct WorkerResult(JToken? Value, string? Error, Exception? Exception, string? NextStepsJson = null);

/// <summary>
/// How a load ended: the per-piece report (<c>[ { id, error? } ]</c>) or an error that stopped it, and the functions of the libraries the load
/// asked about (<c>{ lib: { functions } | { error } }</c>; null when it asked for none).
/// </summary>
internal readonly record struct LoadResult(JArray? Report, string? Error, Exception? Exception, JObject? Exports = null)
{
    /// <summary>The compile errors of the pieces, by id (an empty id is an error of the bundle as a whole).</summary>
    public IReadOnlyList<(string Id, string Error)> Errors
        => Report == null
            ? []
            : [.. Report.Where(e => e.Value<string>("error") != null).Select(e => (e.Value<string>("id") ?? "", e.Value<string>("error")!))];
}

/// <summary>The host's answer to a worker's <c>call</c>: a JSON value, or an error message the script sees as an <c>Error</c>.</summary>
internal readonly record struct HostCallReply(bool Ok, string? Json, string? Error);

/// <summary>What the script can reach in the host through <c>context</c> and <c>log</c>; always JSON text.</summary>
internal interface IScriptHostBridge
{
    /// <summary>JSON text of a pipeline variable, or null when absent or not JSON-like.</summary>
    string? Get(string key);

    /// <summary>Stores a JSON value in the pipeline variables.</summary>
    void Set(string key, string json);

    /// <summary>Writes a script log line (<c>info</c>, <c>warn</c>, <c>error</c>, <c>debug</c>).</summary>
    void Log(string level, string text);

    /// <summary>Runs a host function for the script. The client has already checked the allow list and the budget.</summary>
    Task<HostCallReply> CallAsync(string function, string argsJson, CancellationToken cancellationToken);
}

/// <summary>Bookkeeping of the worker processes, for the tests that prove none is left behind.</summary>
internal static class ScriptWorkerClient
{
    private static readonly ConcurrentDictionary<string, int> ActiveByLabel = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> StartedByLabel = new(StringComparer.Ordinal);

    /// <summary>
    /// Workers started under <paramref name="label"/> and not yet confirmed gone. The label is the step name for a
    /// step run on its own and the <c>CorrelationId</c> of the context for a pipeline run (per label, so tests running
    /// in parallel do not see each other's workers).
    /// </summary>
    internal static int ActiveWorkers(string label) => ActiveByLabel.GetValueOrDefault(label);

    /// <summary>Workers ever started under <paramref name="label"/> (one per run, plus one per limit that killed one).</summary>
    internal static int StartedWorkers(string label) => StartedByLabel.GetValueOrDefault(label);

    internal static void Started(string label)
    {
        ActiveByLabel.AddOrUpdate(label, 1, (_, n) => n + 1);
        StartedByLabel.AddOrUpdate(label, 1, (_, n) => n + 1);
    }

    internal static void Gone(string label) => ActiveByLabel.AddOrUpdate(label, 0, (_, n) => n - 1);
}

/// <summary>
/// One worker process on a leash: memory-capped (Windows job object; a watchdog on other platforms), killed when a
/// call's time or the caller's token runs out, and never left behind. It serves one run: <see cref="LoadAsync"/>
/// compiles the run's JavaScript once, then <see cref="CallAsync"/> runs one step function per call, one at a time
/// (the caller serializes them).
/// </summary>
/// <remarks>
/// <para>The host reads the worker's stdout without pause while a call or a load is in flight, so a worker never blocks
/// on its output; the only time the worker waits is for the host's answer to a <c>get</c> or a <c>call</c>, and the host
/// answers it from the same loop. stderr is drained in the background so a chatty or crashing worker cannot fill its
/// pipe either.</para>
/// <para>The worker enforces the time limit itself (it can name the line); the host's kill, a grace period later, is the
/// backstop for what V8 cannot interrupt, such as one long native call or a worker stuck on a reply. A call that broke
/// a limit (the worker says <c>recycle</c>), was cancelled or killed leaves the worker unusable: <see cref="IsUsable"/>
/// turns false and the owner starts a new one.</para>
/// </remarks>
internal sealed class ScriptWorker : IDisposable
{
    private const int StartupTimeoutMs = 30_000;
    private const int KillGraceMs = 2_000;
    private const int ExitWaitMs = 2_000;
    private const int MemoryPollMs = 25;
    private const int StderrTailChars = 2_000;

    private readonly Process _process;
    private readonly string _label;
    private readonly long _memoryLimit;
    private readonly StringBuilder _stderr;
    private readonly WindowsJobObjectHolder _job;
    private readonly CancellationTokenSource _watchdogStop = new();
    private Task _watchdog = Task.CompletedTask;
    private volatile bool _memoryExceeded;
    private bool _broken;
    private int _disposed;

    private ScriptWorker(Process process, string label, long memoryLimit, StringBuilder stderr, WindowsJobObjectHolder job)
    {
        _process = process;
        _label = label;
        _memoryLimit = memoryLimit;
        _stderr = stderr;
        _job = job;
    }

    /// <summary>False once the worker died, was killed or broke a limit: it must not receive another call.</summary>
    public bool IsUsable => !_broken && Volatile.Read(ref _disposed) == 0 && !HasExited();

    /// <summary>
    /// Starts a worker capped for <paramref name="heapMb"/> (the largest heap cap of the run) and waits for its
    /// <c>ready</c>. Fail closed: a worker whose memory limit cannot be applied is killed before it receives any script.
    /// </summary>
    public static async Task<(ScriptWorker? Worker, string? Error, Exception? Exception)> StartAsync(
        string label, int heapMb, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo;
        try
        {
            startInfo = ScriptWorkerLocator.CreateStartInfo();
        }
        catch (Exception ex)
        {
            return (null, $"the script worker could not be located: {ex.Message}", ex);
        }

        long memoryLimit = ScriptLimits.ProcessMemoryLimitBytes(heapMb);
        Process process = new() { StartInfo = startInfo };
        StringBuilder stderr = new();
        WindowsJobObjectHolder job = new();
        try
        {
            process.ErrorDataReceived += (_, e) => AppendTail(stderr, e.Data);
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            job.Dispose();
            return (null, $"the script worker could not be started: {ex.Message}", ex);
        }

        ScriptWorkerClient.Started(label);
        ScriptWorker worker = new(process, label, memoryLimit, stderr, job);
        try
        {
            process.BeginErrorReadLine();

            // Fail closed: a worker that cannot be capped never receives the script.
            try
            {
                job.Attach(process, memoryLimit);
            }
            catch (Exception ex)
            {
                worker.Dispose();
                return (null, $"the memory limit of the script worker could not be applied: {ex.Message}", ex);
            }

            if (!job.AppliesLimits)
            {
                worker._watchdog = Task.Run(() => worker.WatchMemoryAsync(worker._watchdogStop.Token));
            }

            using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(StartupTimeoutMs);
            JObject? hello;
            try
            {
                hello = await ReadMessageAsync(process.StandardOutput, startup.Token);
            }
            catch (OperationCanceledException)
            {
                worker.Dispose();
                return cancellationToken.IsCancellationRequested
                    ? (null, "the execution was cancelled.", null)
                    : (null, $"the script worker did not start within {StartupTimeoutMs / 1000} s.", null);
            }

            if (hello?.Value<string>(Msg.Kind) != Msg.Ready)
            {
                WorkerResult died = worker.Died("before it was ready");
                worker.Dispose();
                return (null, died.Error, died.Exception);
            }

            int? workerProto = hello.Value<int?>("proto");
            if (workerProto != ScriptProtocol.Version)
            {
                worker.Dispose();
                return (null,
                    $"the script worker speaks protocol {(workerProto?.ToString() ?? "1 or older")}, this host speaks {ScriptProtocol.Version}: " +
                    "the worker found does not match this build (see AITASKAGENT_SCRIPT_WORKER).", null);
            }

            return (worker, null, null);
        }
        catch (Exception ex)
        {
            worker.Dispose();
            return (null, $"the script worker could not be started: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sends the run's pieces; the worker joins them into one bundle and compiles it once (<paramref name="checkOnly"/>:
    /// compiles and keeps nothing). Compile errors come back in the report, one entry per piece.
    /// </summary>
    public async Task<LoadResult> LoadAsync(
        IReadOnlyList<BundlePiece> pieces, int heapMb, bool checkOnly, CancellationToken cancellationToken, IReadOnlyList<string>? exports = null)
    {
        using CancellationTokenSource load = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        load.CancelAfter(ScriptLimits.LoadTimeoutMs + KillGraceMs);
        try
        {
            await SendAsync(new JObject
            {
                [Msg.Kind] = Msg.Load,
                ["proto"] = ScriptProtocol.Version,
                ["pieces"] = ScriptBundle.ToJson(pieces),
                ["heapMb"] = heapMb,
                ["check"] = checkOnly,
                ["exports"] = exports is { Count: > 0 } ? new JArray(exports).ToString(Formatting.None) : null,
            }, load.Token);

            while (true)
            {
                JObject? message = await ReadMessageAsync(_process.StandardOutput, load.Token);
                if (message == null)
                {
                    WorkerResult died = Died("while compiling the scripts");
                    _broken = true;
                    return new LoadResult(null, died.Error, died.Exception);
                }

                if (message.Value<string>(Msg.Kind) != Msg.Loaded)
                {
                    continue;
                }

                if (message.Value<string>("error") is { } error)
                {
                    _broken = true;
                    return new LoadResult(null, error, null);
                }

                string? report = message.Value<string>("errors");
                string? listed = message.Value<string>("exports");
                return new LoadResult(report == null ? [] : JArray.Parse(report), null, null, listed == null ? null : JObject.Parse(listed));
            }
        }
        catch (OperationCanceledException)
        {
            Kill();
            return new LoadResult(null, cancellationToken.IsCancellationRequested
                ? "the execution was cancelled."
                : $"the scripts did not compile within {(ScriptLimits.LoadTimeoutMs + KillGraceMs) / 1000} s.", null);
        }
        catch (IOException ex)
        {
            WorkerResult died = Died("while compiling the scripts", ex);
            _broken = true;
            return new LoadResult(null, died.Error, ex);
        }
    }

    /// <summary>Calls the step function <paramref name="function"/> of the loaded bundle.</summary>
    public async Task<WorkerResult> CallAsync(string function, WorkerCall call, IScriptHostBridge bridge, CancellationToken cancellationToken)
    {
        using CancellationTokenSource run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            HashSet<string> allowed = new(call.Allow ?? [], StringComparer.Ordinal);
            int calls = 0;
            long callBytes = 0;

            await SendAsync(new JObject
            {
                [Msg.Kind] = Msg.Run,
                ["proto"] = ScriptProtocol.Version,
                ["call"] = call.Kind ?? (call.Export == null ? "step" : "validate"),
                ["fn"] = function,
                ["export"] = call.Export,
                ["name"] = call.Name,
                ["input"] = call.InputJson,
                ["timeoutMs"] = call.TimeoutMs,
                ["heapMb"] = call.HeapMb,
                ["stackKb"] = call.StackKb,
                ["uses"] = new JArray(call.Uses ?? []).ToString(Formatting.None),
                ["allow"] = new JArray(allowed.Order(StringComparer.Ordinal)).ToString(Formatting.None),
                ["maxCalls"] = call.MaxCalls,
                ["maxCallBytes"] = call.MaxCallBytes,
            }, run.Token);

            // Hard stop: the worker's own timer comes first; this is for what it cannot interrupt.
            run.CancelAfter(call.TimeoutMs + KillGraceMs);

            while (true)
            {
                JObject? message = await ReadMessageAsync(_process.StandardOutput, run.Token);
                if (message == null)
                {
                    _broken = true;
                    return Died("while running the script");
                }

                switch (message.Value<string>(Msg.Kind))
                {
                    case Msg.Log:
                        bridge.Log(message.Value<string>("level") ?? "info", message.Value<string>("text") ?? "");
                        break;
                    case Msg.Set:
                        bridge.Set(message.Value<string>("key") ?? "", message.Value<string>("json") ?? "null");
                        break;
                    case Msg.Get:
                        await SendAsync(new JObject
                        {
                            [Msg.Kind] = Msg.Value,
                            ["json"] = bridge.Get(message.Value<string>("key") ?? ""),
                        }, run.Token);
                        break;
                    case Msg.Call:
                        string fn = message.Value<string>("fn") ?? "";
                        string argsJson = message.Value<string>("args") ?? "null";
                        HostCallReply reply = await AnswerCallAsync(
                            bridge, fn, argsJson, allowed, call, () => ++calls, bytes => callBytes += bytes, () => callBytes, run.Token);
                        await SendAsync(new JObject
                        {
                            [Msg.Kind] = Msg.Value,
                            ["ok"] = reply.Ok,
                            ["json"] = reply.Json,
                            ["error"] = reply.Error,
                        }, run.Token);
                        break;
                    case Msg.Result:
                        if (message.Value<bool?>("recycle") == true)
                        {
                            // A broken limit: this worker's runtime is not trusted with another call.
                            _broken = true;
                            Kill();
                        }

                        return message.Value<bool>("ok")
                            ? new WorkerResult(
                                JToken.Parse(message.Value<string>("json") ?? "null"), null, null, message.Value<string>("nextSteps"))
                            : Failed(message.Value<string>("error") ?? "the script failed without a message.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Kill();
            return cancellationToken.IsCancellationRequested
                ? Failed("the execution was cancelled.")
                : Failed($"the script exceeded the time limit of {call.TimeoutMs} ms.");
        }
        catch (IOException ex)
        {
            // The worker closed its pipe: it is gone (killed by the memory limit, or crashed).
            _broken = true;
            return Died("while running the script", ex);
        }
    }

    /// <summary>Kills the process (if it still runs) and releases the job; idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _broken = true;
        _watchdogStop.Cancel();
        KillProcess(_process);
        try
        {
            _watchdog.Wait(ExitWaitMs);
        }
        catch (AggregateException)
        {
            // The watchdog ends with the process; nothing to report.
        }

        _watchdogStop.Dispose();
        _job.Dispose();
        _process.Dispose();
        ScriptWorkerClient.Gone(_label);
    }

    private void Kill()
    {
        _broken = true;
        KillProcess(_process);
    }

    private bool HasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Authoritative check of a worker's call (allow list, call count, byte budget), then the host function.</summary>
    private static async Task<HostCallReply> AnswerCallAsync(
        IScriptHostBridge bridge,
        string function,
        string argsJson,
        HashSet<string> allowed,
        WorkerCall call,
        Func<int> countCall,
        Action<long> addBytes,
        Func<long> bytesSoFar,
        CancellationToken cancellationToken)
    {
        if (!allowed.Contains(function))
        {
            return new HostCallReply(false, null, $"host function '{function}' is not allowed for this step.");
        }

        if (countCall() > call.MaxCalls)
        {
            return new HostCallReply(false, null, $"the script exceeded its budget of {call.MaxCalls} host calls.");
        }

        addBytes(Encoding.UTF8.GetByteCount(argsJson));
        if (bytesSoFar() > call.MaxCallBytes)
        {
            return new HostCallReply(false, null, $"the script exceeded its budget of {call.MaxCallBytes} bytes exchanged with host functions.");
        }

        HostCallReply reply = await bridge.CallAsync(function, argsJson, cancellationToken);
        if (reply is { Ok: true, Json: { } json })
        {
            addBytes(Encoding.UTF8.GetByteCount(json));
            if (bytesSoFar() > call.MaxCallBytes)
            {
                return new HostCallReply(false, null, $"the script exceeded its budget of {call.MaxCallBytes} bytes exchanged with host functions.");
            }
        }

        return reply;
    }

    private async Task SendAsync(JObject message, CancellationToken cancellationToken)
    {
        StreamWriter input = _process.StandardInput;
        await input.WriteLineAsync(message.ToString(Formatting.None).AsMemory(), cancellationToken);
        await input.FlushAsync(cancellationToken);
    }

    private static async Task<JObject?> ReadMessageAsync(StreamReader output, CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await output.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                return null;
            }

            try
            {
                using JsonTextReader reader = new(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
                return JObject.Load(reader);
            }
            catch (JsonReaderException)
            {
                // Not ours (a runtime banner, say): nothing to act on.
            }
        }
    }

    private WorkerResult Died(string when, Exception? exception = null)
    {
        if (_memoryExceeded)
        {
            return Failed($"the script exceeded the memory limit of {_memoryLimit / (1024 * 1024)} MB for its process.", exception);
        }

        int? exitCode = null;
        if (_process.WaitForExit(ExitWaitMs))
        {
            exitCode = _process.ExitCode;
        }

        string tail;
        lock (_stderr)
        {
            tail = _stderr.ToString().Trim();
        }

        // V8 prints "Fatal JavaScript out of memory" before it aborts the process: that one is not a guess.
        if (tail.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
        {
            return Failed(
                $"the script ran out of memory: V8 aborted its worker process (exit code {exitCode?.ToString() ?? "unknown"}; " +
                $"process memory limit {_memoryLimit / (1024 * 1024)} MB).",
                exception);
        }

        string detail = tail.Length > 0 ? $" Worker output: {tail}" : "";
        return Failed(
            $"the script worker ended {when} without a result (exit code {exitCode?.ToString() ?? "unknown"}); " +
            $"the usual cause is the memory limit of {_memoryLimit / (1024 * 1024)} MB for its process.{detail}",
            exception);
    }

    private static WorkerResult Failed(string message, Exception? exception = null) => new(null, message, exception);

    private static void AppendTail(StringBuilder buffer, string? line)
    {
        if (line == null)
        {
            return;
        }

        lock (buffer)
        {
            buffer.AppendLine(line);
            if (buffer.Length > StderrTailChars)
            {
                buffer.Remove(0, buffer.Length - StderrTailChars);
            }
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(ExitWaitMs);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exiting while we looked: nothing left to kill.
        }
    }

    /// <summary>Where the operating system gives no per-process memory limit: poll the worker and kill it.</summary>
    private async Task WatchMemoryAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                _process.Refresh();
                if (_process.PrivateMemorySize64 > _memoryLimit)
                {
                    _memoryExceeded = true;
                    _broken = true;
                    KillProcess(_process);
                    return;
                }

                await Task.Delay(MemoryPollMs, stop);
            }
        }
        catch (OperationCanceledException)
        {
            // The worker is being released.
        }
        catch (InvalidOperationException)
        {
            // The process exited.
        }
    }

    /// <summary>The job object where there is one (Windows); a no-op elsewhere, where the watchdog takes over.</summary>
    private sealed class WindowsJobObjectHolder : IDisposable
    {
        private IDisposable? _job;

        public bool AppliesLimits => _job != null;

        public void Attach(Process process, long memoryLimit)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            WindowsJobObject job = WindowsJobObject.Create(memoryLimit);
            try
            {
                job.Assign(process);
            }
            catch
            {
                job.Dispose();
                throw;
            }

            _job = job;
        }

        public void Dispose() => _job?.Dispose();
    }
}
