using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Script.Isolation;
using AITaskAgent.Script.Steps;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AITaskAgent.Script.Tests;

/// <summary>
/// What process isolation guarantees: limits the engine cannot enforce on its own end in an error result, the host
/// keeps running with its memory untouched, and no worker is left behind.
/// </summary>
[Collection("ScriptWorker")]
public sealed class ScriptIsolationTests(ITestOutputHelper output)
{
    private static ScriptStep Step(string script, int timeoutMs = ScriptStep.DefaultTimeoutMs, string id = "iso")
        => new(id) { Script = script, TimeoutMs = timeoutMs };

    private static Task<IStepResult> Run(ScriptStep step, PipelineContext? context = null, CancellationToken cancellationToken = default)
        => ((IStep)step).ExecuteAsync(
            new JsonStepResult(step, new JObject()), context ?? new PipelineContext(), 1, null, cancellationToken);

    // Three arrays of 3e7 doubles are ~720 MB of JavaScript heap: before isolation, this exhausted the memory of the
    // host process (or of the machine). The assertions are about the host, not about which limit trips first.
    [Fact]
    public async Task Chained_huge_native_allocations_fail_in_the_worker_and_the_host_keeps_serving()
    {
        long before = Process.GetCurrentProcess().PrivateMemorySize64;

        var result = await Run(Step(
                "var a = new Array(3e7).fill(1.5); var b = new Array(3e7).fill(2.5); var c = new Array(3e7).fill(3.5); return a.length + b.length + c.length;",
                timeoutMs: 20000, id: "iso-chain"))
            .WaitAsync(TimeSpan.FromSeconds(90));

        long after = Process.GetCurrentProcess().PrivateMemorySize64;
        output.WriteLine($"error: {result.Error?.Message}");
        output.WriteLine($"host private memory: {before / (1024 * 1024)} MB -> {after / (1024 * 1024)} MB");

        Assert.True(result.HasError);
        Assert.Contains("'iso-chain'", result.Error!.Message);
        Assert.True(after - before < 300L * 1024 * 1024, $"the host grew by {(after - before) / (1024 * 1024)} MB");

        var next = await Run(Step("return 1 + 1;", id: "iso-chain-next"));
        Assert.False(next.HasError, next.Error?.Message);
        Assert.Equal(2, (int)(JToken)next.Value!);
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers("iso-chain"));
    }

    [Fact]
    public async Task A_long_native_call_is_stopped_by_the_timeout_and_the_worker_is_gone()
    {
        // One long native call over a 40 MB string: no loop back-edge and no JavaScript call for V8 to interrupt at.
        // Measured on the development machine: the worker's own timer did not stop it, the host's kill did (about
        // timeout + 2 s + start-up). Either way the contract is the same, and it is what this asserts.
        Stopwatch watch = Stopwatch.StartNew();
        var result = await Run(Step("var s = 'ab'.repeat(2e7); return s.replace(/a/g, 'xy').length;", timeoutMs: 500, id: "iso-native"))
            .WaitAsync(TimeSpan.FromSeconds(60));
        watch.Stop();
        output.WriteLine($"stopped after {watch.Elapsed.TotalMilliseconds:F0} ms: {result.Error?.Message}");

        Assert.True(result.HasError);
        Assert.Contains("'iso-native'", result.Error!.Message);
        Assert.Contains("500 ms", result.Error.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers("iso-native"));
    }

    [Fact]
    public async Task An_infinite_loop_times_out_and_leaves_no_worker()
    {
        var result = await Run(Step("while (true) {}", timeoutMs: 300, id: "iso-spin")).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.HasError);
        Assert.Contains("300 ms", result.Error!.Message);
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers("iso-spin"));
    }

    [Fact]
    public async Task Cancelling_kills_the_worker_promptly()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(700));
        Stopwatch watch = Stopwatch.StartNew();

        var result = await Run(Step("while (true) {}", timeoutMs: 60000, id: "iso-cancel"), cancellationToken: cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.HasError);
        Assert.Contains("cancelled", result.Error!.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers("iso-cancel"));
    }

    [Fact]
    public async Task Context_get_and_set_cross_the_process_boundary_in_both_directions()
    {
        PipelineContext context = new();
        context.Metadata["count"] = 41;
        context.Metadata["doc"] = JObject.Parse("""{ "items": [1, 2, 3] }""");

        var result = await Run(Step("""
            context.set('count', context.get('count') + 1);
            context.set('echo', { fromScript: true, n: context.get('doc').items.length });
            return context.get('count');
            """), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(42, (int)(JToken)result.Value!);
        Assert.Equal(42, (int)(JToken)context.Metadata["count"]!);
        Assert.Equal(3, (int)((JObject)context.Metadata["echo"]!)["n"]!);
    }

    [Fact]
    public async Task Parallel_runs_do_not_interfere()
    {
        Task<IStepResult>[] runs = [.. Enumerable.Range(0, 6).Select(i => Run(Step($"return {{ n: {i}, sq: {i} * {i} }};", id: $"iso-p{i}")))];

        IStepResult[] results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(90));

        for (int i = 0; i < results.Length; i++)
        {
            Assert.False(results[i].HasError, results[i].Error?.Message);
            Assert.Equal(i * i, (int)((JObject)results[i].Value!)["sq"]!);
        }

        Assert.Equal(0, Enumerable.Range(0, results.Length).Sum(i => ScriptWorkerClient.ActiveWorkers($"iso-p{i}")));
    }

    [Fact]
    public void The_heap_and_stack_caps_have_hard_ceilings_that_are_rejected_not_clamped()
    {
        ScriptStep step = new("caps");

        Assert.Equal(ScriptStep.DefaultMaxHeapMb, step.MaxHeapMb);
        Assert.Equal(ScriptStep.DefaultMaxStackKb, step.MaxStackKb);

        step.MaxHeapMb = ScriptStep.MaxHeapMbCeiling;
        step.MaxStackKb = ScriptStep.MaxStackKbCeiling;
        Assert.Equal(128, step.MaxHeapMb);
        Assert.Equal(2048, step.MaxStackKb);

        Assert.Throws<ArgumentOutOfRangeException>(() => step.MaxHeapMb = ScriptStep.MaxHeapMbCeiling + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => step.MaxHeapMb = ScriptStep.MinHeapMb - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => step.MaxStackKb = ScriptStep.MaxStackKbCeiling + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => step.MaxStackKb = ScriptStep.MinStackKb - 1);
        Assert.Equal(128, step.MaxHeapMb);
    }

    [Fact]
    public async Task A_lower_heap_cap_is_applied_by_the_worker()
    {
        // 40 arrays of 0.8 MB, kept reachable from a global so they are still in the heap when the run ends (the
        // post-run check is deterministic; the sampled one is not): ~32 MB, over an 8 MB cap, under the default 64 MB.
        ScriptStep step = Step("globalThis.keep = []; for (var i = 0; i < 40; i++) globalThis.keep.push(new Array(1e5).fill(1.5)); return globalThis.keep.length;", id: "small");
        step.MaxHeapMb = 8;

        var result = await Run(step).WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine(result.Error?.Message ?? "(no error)");

        Assert.True(result.HasError);
        Assert.Contains("'small'", result.Error!.Message);

        step.MaxHeapMb = ScriptStep.DefaultMaxHeapMb;
        var roomy = await Run(step).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(roomy.HasError, roomy.Error?.Message);
    }

    // The operating-system limit on its own, without the soft limits in front of it: a worker put in a job whose
    // memory limit is smaller than what the runtime and V8 need to start is killed by the OS, not by our code.
    [Fact]
    public async Task The_windows_job_object_kills_a_worker_that_commits_more_than_its_limit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using Process worker = Process.Start(ScriptWorkerLocator.CreateStartInfo())!;
        using WindowsJobObject job = WindowsJobObject.Create(24L * 1024 * 1024);
        job.Assign(worker);

        // A load makes the worker create its V8 runtime (protocol 3 and later: the run's scripts are compiled before any call).
        await worker.StandardInput.WriteLineAsync(
            """{"t":"load","proto":5,"pieces":"[{\"id\":\"n\",\"lib\":false,\"key\":\"n\",\"source\":\"return 1;\"}]","heapMb":64}""");
        await worker.StandardInput.FlushAsync();
        _ = worker.StandardOutput.ReadToEndAsync();
        _ = worker.StandardError.ReadToEndAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await worker.WaitForExitAsync(timeout.Token);
        output.WriteLine($"exit code {worker.ExitCode}");

        Assert.NotEqual(0, worker.ExitCode);
    }

    [Fact]
    public async Task A_missing_worker_is_a_clear_error_not_a_hang()
    {
        string? previous = Environment.GetEnvironmentVariable(ScriptWorkerLocator.WorkerPathVariable);
        Environment.SetEnvironmentVariable(ScriptWorkerLocator.WorkerPathVariable, Path.Combine(Path.GetTempPath(), "no-such-worker.dll"));
        try
        {
            var result = await Run(Step("return 1;", id: "lost")).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(result.HasError);
            Assert.Contains("'lost'", result.Error!.Message);
            Assert.Contains("could not be located", result.Error.Message);
            Assert.Contains(ScriptWorkerLocator.WorkerPathVariable, result.Error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScriptWorkerLocator.WorkerPathVariable, previous);
        }
    }
}
