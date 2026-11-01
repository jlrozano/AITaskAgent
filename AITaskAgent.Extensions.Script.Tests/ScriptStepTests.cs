using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.Script.Configuration;
using AITaskAgent.Script.Steps;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AITaskAgent.Script.Tests;

/// <summary>Logger factory that records every formatted line.</summary>
internal static class ResultExtensions
{
    public static JToken Json(this IStepResult result) => (JToken)result.Value!;
}

internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void AddProvider(ILoggerProvider provider) { }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);

    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}

/// <summary>Direct tests of <see cref="ScriptStep"/> and its registration; no LLM anywhere.</summary>
[Collection("ScriptWorker")]
public sealed class ScriptStepTests
{
    private readonly CapturingLoggerFactory _logs = new();

    private PipelineContext NewContext()
        => new(null, new ServiceCollection().AddSingleton<ILoggerFactory>(_logs).BuildServiceProvider());

    private static ScriptStep Step(string script, int timeoutMs = ScriptStep.DefaultTimeoutMs, string id = "calc")
        => new(id) { Script = script, TimeoutMs = timeoutMs };

    private Task<IStepResult> Run(ScriptStep step, object? input = null, PipelineContext? context = null)
        => ((IStep)step).ExecuteAsync(
            new JsonStepResult(step, input == null ? JValue.CreateNull() : JToken.FromObject(input)),
            context ?? NewContext(), 1, null, CancellationToken.None);

    [Fact]
    public async Task A_simple_script_returns_an_object()
    {
        var result = await Run(Step("return { ok: true, n: 1 + 2, items: [1, 'a', null] };"));

        Assert.False(result.HasError, result.Error?.Message);
        var json = Assert.IsType<JObject>(result.Value);
        Assert.True((bool)json["ok"]!);
        Assert.Equal(3, (int)json["n"]!);
        Assert.Equal(3, ((JArray)json["items"]!).Count);
    }

    [Fact]
    public async Task The_script_reads_input_as_a_plain_object()
    {
        var result = await Run(
            Step("return { total: input.lines.reduce((s, l) => s + l.qty * l.price, 0), who: input.customer.name };"),
            new { customer = new { name = "Acme" }, lines = new[] { new { qty = 2, price = 1.5 }, new { qty = 1, price = 4.0 } } });

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(7, (double)result.Json()["total"]!);
        Assert.Equal("Acme", (string?)result.Json()["who"]);
    }

    [Fact]
    public async Task A_script_without_return_yields_json_null()
    {
        var result = await Run(Step("var x = 1;"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(JTokenType.Null, result.Json().Type);
    }

    [Fact]
    public async Task Multiline_script_with_special_characters_runs_intact()
    {
        const string script = """
            // comentario: acentos áéíñ, emoji 🚀, # y : y { } y \ y "comillas" y 'simples'
            const text = `línea 1
            línea 2 con ${input.who} y \n literal`;
            const re = /^[a-z]+\d{2}$/;
            return { text, path: "C:\\temp\\file", ok: re.test("abc12"), braces: "{ \"k\": [1,2] }" };
            """;

        var result = await Run(Step(script), new { who = "Ñandú" });

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("línea 1\nlínea 2 con Ñandú y \n literal", (string?)result.Json()["text"]);
        Assert.Equal("C:\\temp\\file", (string?)result.Json()["path"]);
        Assert.True((bool)result.Json()["ok"]!);
        Assert.Equal("{ \"k\": [1,2] }", (string?)result.Json()["braces"]);
    }

    [Fact]
    public async Task A_javascript_exception_becomes_an_error_with_the_step_id_and_line()
    {
        var result = await Run(Step("var a = 1;\nvar b = 2;\nthrow new Error('boom ' + (a + b));", id: "explode"));

        Assert.True(result.HasError);
        string message = result.Error!.Message;
        Assert.Contains("'explode'", message);
        Assert.Contains("boom 3", message);
        // The bundle is a compiled (kept) document, so V8 no longer adds " [temp]" after the name.
        Assert.Contains("explode:3:", message);
    }

    [Fact]
    public async Task A_syntax_error_becomes_an_error_with_the_step_id()
    {
        var result = await Run(Step("return {{{ ;", id: "broken"));

        Assert.True(result.HasError);
        Assert.Contains("'broken'", result.Error!.Message);
        Assert.Contains("SyntaxError", result.Error.Message);
    }

    [Fact]
    public async Task An_infinite_loop_is_stopped_by_the_timeout()
    {
        Stopwatch watch = Stopwatch.StartNew();
        var result = await Run(Step("while (true) {}", timeoutMs: 300, id: "spin"))
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.HasError);
        Assert.Contains("'spin'", result.Error!.Message);
        Assert.Contains("300 ms", result.Error.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_script()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));
        ScriptStep step = Step("while (true) {}", timeoutMs: 60000);

        var result = await ((IStep)step).ExecuteAsync(new JsonStepResult(step, new JObject()), NewContext(), 1, null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.HasError);
        Assert.Contains("cancelled", result.Error!.Message);
    }

    [Fact]
    public async Task Runaway_memory_is_stopped_and_reported_without_crashing_the_process()
    {
        var result = await Run(Step("const a = []; while (true) { a.push('x'.repeat(100000) + a.length); }", timeoutMs: 20000, id: "hog"))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.HasError);
        Assert.Contains("'hog'", result.Error!.Message);
    }

    // Regression (review R1): a large allocation made by one builtin call never reaches a V8 interrupt, so the
    // soft cap did not stop it. Sized to stay far from any hard V8 limit, old or new: if the fix regresses, this
    // test fails instead of aborting the test host. The process-abort case (new Array(3e7)) was verified in a
    // separate process (see log-fixes.md).
    [Fact]
    public async Task A_single_large_allocation_over_the_heap_cap_is_an_error()
    {
        // 1e7 doubles (80 MB live) over the 64 MB cap. It was 5e6 (40 MB live), which only tripped the old check through
        // the transient garbage of the array's element-kind change; the cap is now measured against the baseline of the
        // run's runtime, which a GC between calls brings down to the compiled code.
        var result = await Run(Step("var a = new Array(1e7).fill(1.5); return a.length;", id: "big"))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.HasError);
        Assert.Contains("'big'", result.Error!.Message);
        Assert.Contains("heap limit", result.Error.Message);
    }

    // Regression (review R3): ArrayBuffer memory lives outside the V8 heap and had no cap at all.
    [Fact]
    public async Task An_array_buffer_over_the_cap_is_an_error_and_a_small_one_works()
    {
        var big = await Run(Step("var b = new ArrayBuffer(1024 * 1024 * 1024); new Uint8Array(b).fill(1); return b.byteLength;", id: "ab"))
            .WaitAsync(TimeSpan.FromSeconds(60));
        var small = await Run(Step("var b = new Uint8Array(1024 * 1024); b.fill(7); return b[10] + b.length;"));

        Assert.True(big.HasError);
        Assert.Contains("'ab'", big.Error!.Message);
        Assert.Contains("RangeError", big.Error.Message);
        Assert.False(small.HasError, small.Error?.Message);
        Assert.Equal(7 + 1024 * 1024, (int)small.Json());
    }

    // Regression (review R2): an already-cancelled token used to run the whole script and report success.
    [Fact]
    public async Task An_already_cancelled_token_does_not_run_the_script()
    {
        ScriptStep step = Step("context.set('ran', true); return 'ran';");
        PipelineContext context = NewContext();

        var result = await ((IStep)step).ExecuteAsync(
            new JsonStepResult(step, new JObject()), context, 1, null, new CancellationToken(canceled: true));

        Assert.True(result.HasError);
        Assert.Contains("cancelled", result.Error!.Message);
        Assert.False(context.Metadata.ContainsKey("ran"));
    }

    [Fact]
    public async Task Unbounded_recursion_is_an_error_not_a_crash()
    {
        var result = await Run(Step("function f(n) { return f(n + 1) + 1; } return f(0);", id: "deep"))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.HasError);
        Assert.Contains("'deep'", result.Error!.Message);
    }

    [Theory]
    [InlineData("System")]
    [InlineData("xHost")]
    [InlineData("process")]
    [InlineData("module")]
    [InlineData("Microsoft")]
    [InlineData("clr")]
    [InlineData("__hostLog")]
    [InlineData("__hostGet")]
    [InlineData("__hostSet")]
    [InlineData("__hostCall")]
    [InlineData("__nHash")]
    [InlineData("__allow")]
    [InlineData("__libs")]
    [InlineData("__bundle")]
    [InlineData("__fn")]
    [InlineData("__uses")]
    public async Task No_dotnet_or_host_globals_are_reachable(string name)
    {
        var result = await Run(Step($"return typeof {name};"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("undefined", (string?)result.Json());
    }

    [Fact]
    public async Task Touching_system_fails_cleanly()
    {
        var result = await Run(Step("return System.IO.File.ReadAllText('C:\\\\Windows\\\\win.ini');", id: "peek"));

        Assert.True(result.HasError);
        Assert.Contains("'peek'", result.Error!.Message);
        Assert.Contains("System", result.Error.Message);
    }

    [Fact]
    public async Task The_context_bridge_exposes_only_json_like_values()
    {
        PipelineContext context = NewContext();
        context.Metadata["counter"] = 41;
        context.Metadata["doc"] = JObject.Parse("""{ "a": [1, 2] }""");
        context.Metadata["secret"] = new object();

        var result = await Run(Step("""
            context.set('counter', context.get('counter') + 1);
            context.set('out', { fromScript: true });
            return { doc: context.get('doc'), secret: String(context.get('secret')), missing: String(context.get('nope')) };
            """), context: context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, ((JArray)result.Json()["doc"]!["a"]!).Count);
        Assert.Equal("undefined", (string?)result.Json()["secret"]);
        Assert.Equal("undefined", (string?)result.Json()["missing"]);
        Assert.Equal(42, (int)(JToken)context.Metadata["counter"]!);
        Assert.True((bool)((JObject)context.Metadata["out"]!)["fromScript"]!);
    }

    [Fact]
    public async Task Log_and_console_write_to_the_step_logger()
    {
        var result = await Run(Step("log('hello', 1); console.warn({ a: 1 }); console.error('bad'); return 1;"));

        Assert.False(result.HasError, result.Error?.Message);
        string[] lines = [.. _logs.Lines];
        Assert.Contains(lines, l => l.StartsWith("Information") && l.Contains("hello 1"));
        Assert.Contains(lines, l => l.StartsWith("Warning") && l.Contains("{\"a\":1}"));
        Assert.Contains(lines, l => l.StartsWith("Error") && l.Contains("bad"));
    }

    [Fact]
    public async Task Each_run_gets_a_fresh_engine()
    {
        ScriptStep step = Step("globalThis.leak = (globalThis.leak || 0) + 1; return globalThis.leak;");

        var first = await Run(step);
        var second = await Run(step);

        Assert.Equal(1, (int)first.Json());
        Assert.Equal(1, (int)second.Json());
    }

    [Fact]
    public async Task Empty_script_and_non_positive_timeout_are_errors()
    {
        var empty = await Run(Step("  "));
        var timeout = await Run(Step("return 1;", timeoutMs: 0));

        Assert.True(empty.HasError);
        Assert.Contains("'script'", empty.Error!.Message);
        Assert.True(timeout.HasError);
        Assert.Contains("timeoutMs", timeout.Error!.Message);
    }

    [Fact]
    public void The_extension_registers_the_step_and_AddScriptExtension_is_idempotent()
    {
        ServiceCollection services = new();
        services.AddScriptExtension().AddScriptExtension();

        var registry = services.BuildServiceProvider().GetRequiredService<IStepRegistry>();

        Assert.Equal(typeof(ScriptStep), registry.TryGetType("ScriptStep", out Type? type) ? type : null);
        Assert.Equal("Script", new ScriptExtension().Name);
        Assert.Single(services, d => d.ServiceType == typeof(IExtension));
    }
}
