using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Script.Configuration;
using AITaskAgent.Script.Hosting;
using AITaskAgent.Script.Isolation;
using AITaskAgent.Script.Protocol;
using AITaskAgent.Script.Steps;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace AITaskAgent.Script.Tests;

/// <summary>
/// One compile per run (BRMS style): when a pipeline run starts, the scripts of its ScriptSteps are joined in one bundle and
/// compiled once in one worker for the run; each step is a call of its function in a fresh V8 context. No LLM.
/// </summary>
[Collection("ScriptWorker")]
public sealed class ScriptRunBundleTests(ITestOutputHelper output)
{
    private static YamlPipelineFactory NewFactory()
    {
        ServiceProvider services = new ServiceCollection().AddScriptExtension().BuildServiceProvider();
        return new YamlPipelineFactory(
            services, services.GetRequiredService<IStepRegistry>(), new SchemaRegistry(), new Pipeline());
    }

    private static PipelineContext NewContext(string label) => new() { CorrelationId = $"{label}-{Guid.NewGuid():N}" };

    [Fact]
    public async Task A_run_of_many_script_steps_uses_one_worker_and_leaves_none_behind()
    {
        const int n = 12;
        StringBuilder yaml = new("name: many\nsteps:\n  - id: g\n    type: GroupStep\n    steps:\n");
        for (int i = 0; i < n; i++)
        {
            yaml.Append($"      - id: s{i}\n        type: ScriptStep\n        script: |\n          return (input || 0) + 1;\n");
        }

        YamlPipeline pipeline = NewFactory().CreateFromYaml(yaml.ToString());
        PipelineContext context = NewContext("bundle-many");

        IStepResult result = await pipeline.ExecuteAsync(new JValue(0), context).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(n, (int)(JToken)result.Value!);
        Assert.Equal(1, ScriptWorkerClient.StartedWorkers(context.CorrelationId));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task A_compile_error_fails_the_run_at_start_naming_the_step_and_its_own_line()
    {
        YamlPipeline pipeline = NewFactory().CreateFromYaml("""
            name: broken
            steps:
              - id: first
                type: ScriptStep
                script: |
                  context.set('ran', true);
                  return 1;
              - id: second
                type: ScriptStep
                dependsOn: [first]
                script: |
                  var a = 1;
                  var b = ;
                  return a;
            """);
        PipelineContext context = NewContext("bundle-compile");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(new JObject(), context));
        output.WriteLine(ex.Message);

        Assert.Contains("does not compile", ex.Message);
        Assert.Contains("ScriptStep 'second'", ex.Message);
        Assert.Contains("SyntaxError", ex.Message);
        Assert.Contains("second:2:", ex.Message);
        Assert.DoesNotContain("ScriptStep 'first'", ex.Message);
        Assert.False(context.Metadata.ContainsKey("ran"));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task A_runtime_error_names_the_step_and_the_line_of_its_own_script()
    {
        YamlPipeline pipeline = NewFactory().CreateFromYaml("""
            name: throws
            steps:
              - id: ok
                type: ScriptStep
                script: |
                  return { n: 1 };
              - id: explode
                type: ScriptStep
                dependsOn: [ok]
                script: |
                  var n = input.n;
                  throw new Error('boom ' + n);
            """);
        PipelineContext context = NewContext("bundle-throw");

        IStepResult result = await pipeline.ExecuteAsync(new JObject(), context).WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine(result.Error?.Message);

        Assert.True(result.HasError);
        Assert.Contains("'explode'", result.Error!.Message);
        Assert.Contains("boom 1", result.Error.Message);
        Assert.Contains("explode:2:", result.Error.Message);
        Assert.DoesNotContain("workflow-bundle", result.Error.Message);
        Assert.DoesNotContain("__bundle", result.Error.Message);
    }

    [Fact]
    public async Task Globals_written_by_one_step_are_not_seen_by_the_next()
    {
        YamlPipeline pipeline = NewFactory().CreateFromYaml("""
            name: isolation
            steps:
              - id: writer
                type: ScriptStep
                script: |
                  globalThis.leak = 42;
                  sloppy = 1;
                  Object.prototype.polluted = true;
                  Array.prototype.extra = function () { return 1; };
                  input.mutated = true;
                  return { from: 'writer' };
              - id: reader
                type: ScriptStep
                dependsOn: [writer]
                script: |
                  return {
                    leak: typeof leak, sloppy: typeof sloppy, polluted: ({}).polluted === undefined,
                    extra: typeof [].extra, from: input.from, mutated: input.mutated === undefined
                  };
            """);
        PipelineContext context = NewContext("bundle-iso");

        IStepResult result = await pipeline.ExecuteAsync(new JObject(), context).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(result.HasError, result.Error?.Message);
        JObject json = (JObject)result.Value!;
        Assert.Equal("undefined", (string?)json["leak"]);
        Assert.Equal("undefined", (string?)json["sloppy"]);
        Assert.True((bool)json["polluted"]!);
        Assert.Equal("undefined", (string?)json["extra"]);
        Assert.Equal("writer", (string?)json["from"]);
        Assert.True((bool)json["mutated"]!);
        Assert.Equal(1, ScriptWorkerClient.StartedWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task Each_loop_iteration_gets_a_fresh_context_from_the_same_compile()
    {
        YamlPipeline pipeline = NewFactory().CreateFromYaml("""
            name: loop
            steps:
              - id: l
                type: LoopStep
                maxIterations: 15
                steps:
                  - id: tick
                    type: ScriptStep
                    script: |
                      globalThis.n = (globalThis.n || 0) + 1;
                      context.set('seen', (context.get('seen') || 0) + globalThis.n);
                      return globalThis.n;
            """);
        PipelineContext context = NewContext("bundle-loop");

        IStepResult result = await pipeline.ExecuteAsync(new JObject(), context).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(15, (int)(JToken)context.Metadata["seen"]!);
        Assert.Equal(1, ScriptWorkerClient.StartedWorkers(context.CorrelationId));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task Parallel_branches_share_the_worker_without_seeing_each_other()
    {
        StringBuilder yaml = new("name: fan\nsteps:\n  - id: fan\n    type: ParallelStep\n    branches:\n");
        for (int i = 0; i < 4; i++)
        {
            yaml.Append($"      - id: b{i}\n        type: ScriptStep\n        script: |\n")
                .Append($"          var before = typeof globalThis.mark;\n")
                .Append($"          globalThis.mark = {i};\n")
                .Append($"          return {{ i: {i}, before: before, mark: globalThis.mark }};\n");
        }

        YamlPipeline pipeline = NewFactory().CreateFromYaml(yaml.ToString());
        PipelineContext context = NewContext("bundle-fan");

        IStepResult result = await pipeline.ExecuteAsync(new JObject(), context).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(result.HasError, result.Error?.Message);
        ParallelResult parallel = Assert.IsType<ParallelResult>(result);
        for (int i = 0; i < 4; i++)
        {
            JObject branch = (JObject)parallel.Value[$"b{i}"].Value!;
            Assert.Equal("undefined", (string?)branch["before"]);
            Assert.Equal(i, (int)branch["mark"]!);
        }

        Assert.Equal(1, ScriptWorkerClient.StartedWorkers(context.CorrelationId));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task A_branch_that_hangs_is_killed_at_its_timeout_and_the_other_branches_still_run()
    {
        YamlPipeline pipeline = NewFactory().CreateFromYaml("""
            name: hang
            steps:
              - id: fan
                type: ParallelStep
                failFast: false
                branches:
                  - id: spin
                    type: ScriptStep
                    timeoutMs: 400
                    script: |
                      while (true) {}
                  - id: fine
                    type: ScriptStep
                    script: |
                      return 'fine';
            """);
        PipelineContext context = NewContext("bundle-hang");
        Stopwatch watch = Stopwatch.StartNew();

        IStepResult result = await pipeline.ExecuteAsync(new JObject(), context).WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"{watch.ElapsedMilliseconds} ms");

        ParallelResult parallel = Assert.IsType<ParallelResult>(result);
        Assert.True(parallel.Value["spin"].HasError);
        Assert.Contains("400 ms", parallel.Value["spin"].Error!.Message);
        Assert.False(parallel.Value["fine"].HasError, parallel.Value["fine"].Error?.Message);
        Assert.Equal("fine", (string?)(JToken?)parallel.Value["fine"].Value);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task A_call_over_its_time_or_memory_limit_kills_the_worker_and_the_next_call_gets_a_new_one()
    {
        string label = $"bundle-limits-{Guid.NewGuid():N}";
        List<BundlePiece> pieces =
        [
            new("spin", false, "spin", "while (true) {}"),
            new("hog", false, "hog", "globalThis.keep = []; for (var i = 0; i < 40; i++) globalThis.keep.push(new Array(1e5).fill(1.5)); return 1;"),
            new("ok", false, "ok", "return (input || 0) + 1;"),
        ];
        (ScriptRunSession? session, IReadOnlyList<(string Id, string Error)> errors, string? failure, _) =
            await ScriptRunSession.OpenAsync(label, pieces, ScriptLimits.DefaultHeapMb, CancellationToken.None);
        Assert.True(session != null, failure ?? ScriptRunSession.Describe(errors));
        NullBridge bridge = new();

        await using (session)
        {
            WorkerResult first = await session.CallAsync("ok", new WorkerCall("ok", "1", 5000, 64, 512), bridge, CancellationToken.None);
            WorkerResult spin = await session.CallAsync("spin", new WorkerCall("spin", "null", 300, 64, 512), bridge, CancellationToken.None);
            WorkerResult afterSpin = await session.CallAsync("ok", new WorkerCall("ok", "2", 5000, 64, 512), bridge, CancellationToken.None);
            WorkerResult hog = await session.CallAsync("hog", new WorkerCall("hog", "null", 20000, 8, 512), bridge, CancellationToken.None);
            WorkerResult afterHog = await session.CallAsync("ok", new WorkerCall("ok", "3", 5000, 64, 512), bridge, CancellationToken.None);
            output.WriteLine($"spin: {spin.Error}\nhog: {hog.Error}");

            Assert.Equal(2, (int)first.Value!);
            Assert.Contains("300 ms", spin.Error);
            Assert.Equal(3, (int)afterSpin.Value!);
            Assert.Contains("heap limit", hog.Error);
            Assert.Equal(4, (int)afterHog.Value!);
            Assert.Equal(3, ScriptWorkerClient.StartedWorkers(label));
        }

        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(label));
    }

    [Fact]
    public async Task Cancelling_a_run_kills_its_worker_promptly()
    {
        YamlPipeline pipeline = NewFactory().CreateFromYaml("""
            name: cancel
            steps:
              - id: spin
                type: ScriptStep
                timeoutMs: 60000
                script: |
                  while (true) {}
            """);
        PipelineContext context = NewContext("bundle-cancel");
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(1500));
        Stopwatch watch = Stopwatch.StartNew();

        IStepResult result = await pipeline.ExecuteAsync(new JObject(), context, cts.Token).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.HasError);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task A_suspended_run_releases_its_worker_and_the_resumed_run_compiles_again()
    {
        YamlPipeline pipeline = NewFactory().CreateFromYaml("""
            name: suspend
            steps:
              - id: before
                type: ScriptStep
                script: |
                  context.set('before', (context.get('before') || 0) + 1);
                  return { order: 7 };
              - id: ask
                type: SuspendStep
                dependsOn: [before]
                message: "Confirm"
              - id: after
                type: ScriptStep
                dependsOn: [ask]
                script: |
                  return { approved: input.approved, leaked: typeof globalThis.anything };
            """);
        PipelineContext context = NewContext("bundle-suspend");

        IStepResult first = await pipeline.ExecuteAsync(new JObject(), context).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.NotNull(first.SuspendInfo);
        Assert.Equal(1, ScriptWorkerClient.StartedWorkers(context.CorrelationId));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));

        IStepResult resumed = await pipeline.ExecuteAsync(JObject.Parse("""{ "approved": "yes" }"""), context).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(resumed.HasError, resumed.Error?.Message);
        Assert.Equal("yes", (string?)((JObject)resumed.Value!)["approved"]);
        Assert.Equal(1, (int)(JToken)context.Metadata["before"]!);
        Assert.Equal(2, ScriptWorkerClient.StartedWorkers(context.CorrelationId));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task Validation_and_run_compile_the_same_bundle_and_report_the_same_error()
    {
        const string broken = "var a = 1;\nvar b = 2;\nreturn a +* b;";
        IReadOnlyList<ScriptSyntaxIssue> issues = await ScriptSyntax.CheckAsync(
        [
            new ScriptSyntaxItem("fine", false, "return 1;"),
            new ScriptSyntaxItem("broken", false, broken),
        ]);

        YamlPipeline pipeline = NewFactory().CreateFromYaml(
            "name: parity\nsteps:\n  - id: fine\n    type: ScriptStep\n    script: |\n      return 1;\n" +
            "  - id: broken\n    type: ScriptStep\n    dependsOn: [fine]\n    script: |\n" +
            string.Join("", broken.Split('\n').Select(l => "      " + l + "\n")));
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(new JObject(), NewContext("bundle-parity")));

        ScriptSyntaxIssue issue = Assert.Single(issues);
        Assert.Equal("broken", issue.Id);
        Assert.Equal(3, issue.Line);
        // The YAML block adds the final newline the C# string has not; the error text (message and location) is the same.
        Assert.Contains($"ScriptStep 'broken': script error: {issue.Message.Split('\n')[0]}", ex.Message);
        Assert.Contains("broken:3:", ex.Message);
    }

    [Fact]
    public async Task A_script_that_escapes_its_function_is_refused_by_validation_and_by_the_run()
    {
        // Compiles on its own as part of the bundle, but closes its function and plants another one.
        const string escape = "return 1;\n}; globalThis.__bundle.steps['victim'] = function (input, context) { return 'hijacked'; }; void function () {";
        IReadOnlyList<ScriptSyntaxIssue> issues = await ScriptSyntax.CheckAsync(
        [
            new ScriptSyntaxItem("victim", false, "return 'victim';"),
            new ScriptSyntaxItem("escape", false, escape),
        ]);
        output.WriteLine(string.Join("\n", issues.Select(i => $"{i.Id}: {i.Message}")));

        Assert.NotEmpty(issues);
        Assert.Contains(issues, i => i.Id is "escape" or "");

        IStep[] steps = [new ScriptStep("victim") { Script = "return 'victim';" }, new ScriptStep("escape") { Script = escape }];
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Pipeline().ExecuteAsync("escape", steps, new JsonStepResult(steps[0], new JObject()), NewContext("bundle-escape")));
        Assert.Contains("ScriptStep 'escape'", ex.Message);
        Assert.Contains("does not stay inside its function", ex.Message);
    }

    [Fact]
    public async Task A_step_executed_outside_a_pipeline_still_runs_in_a_worker_of_its_own()
    {
        ScriptStep step = new("alone") { Script = "return 21 * 2;" };

        IStepResult result = await ((IStep)step).ExecuteAsync(
            new AITaskAgent.Core.StepResults.JsonStepResult(step, new JObject()), new PipelineContext(), 1, null, CancellationToken.None);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(42, (int)(JToken)result.Value!);
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers("alone"));
    }

    private sealed class NullBridge : IScriptHostBridge
    {
        public string? Get(string key) => null;

        public void Set(string key, string json)
        {
        }

        public void Log(string level, string text)
        {
        }

        public Task<HostCallReply> CallAsync(string function, string argsJson, CancellationToken cancellationToken)
            => Task.FromResult(new HostCallReply(false, null, "no host functions here."));
    }
}
