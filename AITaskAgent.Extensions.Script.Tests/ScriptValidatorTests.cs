using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.Script.Configuration;
using AITaskAgent.Script.Hosting;
using AITaskAgent.Script.Isolation;
using AITaskAgent.Script.Validators;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AITaskAgent.Script.Tests;

/// <summary>
/// JavaScript validators and result validators (architecture §18.7): functions exported by a library, named <c>js:&lt;lib&gt;.&lt;fn&gt;</c>,
/// run in the run's script worker with facades over JSON (<c>args.result</c> read-only, <c>args.vars</c> writable through a patch,
/// <c>args.step</c> read-only). No LLM.
/// </summary>
[Collection("ScriptWorker")]
public sealed class ScriptValidatorTests(ITestOutputHelper output)
{
    /// <summary>A step that returns the JSON of its <see cref="Json"/> property: lets a run have JS validators and no ScriptStep.</summary>
    [StepTag("JsonEcho")]
    public sealed class JsonEchoStep : StepBase
    {
        public JsonEchoStep() : base("JsonEcho", typeof(IStepResult), typeof(JsonStepResult))
        {
            MaxRetries = 1;
        }

        public string Json { get; set; } = "null";

        protected override Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => Task.FromResult<IStepResult>(new JsonStepResult(this, JToken.Parse(Json)));
    }

    /// <summary>The script objects of the end-to-end tests: one argument, an injected token, a task.</summary>
    public sealed class Inventory
    {
        public int Calls { get; private set; }

        public object Stock(StockQuery query)
        {
            Calls++;
            return new { sku = query.Sku, units = query.Sku == "A-1" ? 7 : 0 };
        }

        public async Task<string> EchoAsync(string text, CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            return "echo:" + text;
        }

        public string Ping() => "pong";
    }

    public sealed record StockQuery(string Sku);

    private sealed class CapturingLogger : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Text)> Lines { get; } = new();

        private string _category = "";

        public ILogger CreateLogger(string categoryName) => new Category(this, categoryName);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((_category, logLevel, formatter(state, exception)));

        public void Dispose()
        {
        }

        private sealed class Category(CapturingLogger owner, string name) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Lines.Enqueue((name, logLevel, formatter(state, exception)));
        }
    }

    private IServiceProvider? _services;

    /// <summary>A YAML factory over a provider with the script extension; the contexts of <see cref="NewContext"/> use the same provider.</summary>
    private YamlPipelineFactory Factory(Action<IServiceCollection> configure, CapturingLogger? logger = null)
    {
        ServiceCollection services = new();
        if (logger != null)
        {
            services.AddLogging(b => b.AddProvider(logger).SetMinimumLevel(LogLevel.Trace));
        }

        services.AddScriptExtension();
        configure(services);
        ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStepRegistry>().Add(typeof(JsonEchoStep), "JsonEcho");
        _services = provider;
        return new YamlPipelineFactory(provider, provider.GetRequiredService<IStepRegistry>(), new SchemaRegistry(), new Pipeline());
    }

    private static Action<IServiceCollection> Library(string name, string source, Action<IServiceCollection>? more = null)
        => services =>
        {
            services.AddScriptLibrary(new ScriptLibrary(name, source));
            more?.Invoke(services);
        };

    private static string Echo(string json, string validators, string id = "echo")
        => $"name: v\nsteps:\n  - id: {id}\n    type: JsonEcho\n    json: '{json}'\n{validators}";

    private PipelineContext NewContext(string label)
        => new(null, _services ?? throw new InvalidOperationException("Call Factory first.")) { CorrelationId = $"{label}-{Guid.NewGuid():N}" };

    private static async Task<IStepResult> RunAsync(YamlPipeline pipeline, PipelineContext context)
        => await pipeline.ExecuteAsync(new JObject(), context).WaitAsync(TimeSpan.FromSeconds(90));

    private const string Reads = """
        exports.reads = function (args) {
          var r = args.result, bad = [];
          if (r.address.city !== 'Sevilla') bad.push('nested');
          if (r.lines.length !== 2 || r.lines[1].qty !== 3) bad.push('arrays');
          if (!('address' in r) || ('nope' in r)) bad.push('in');
          if (Object.keys(r).join(',') !== 'address,lines,tags') bad.push('keys');
          if (r.lines.map(function (l) { return l.qty; }).reduce(function (a, b) { return a + b; }, 0) !== 5) bad.push('map/reduce');
          if (r.lines.filter(function (l) { return l.qty > 2; }).length !== 1) bad.push('filter');
          var seen = []; for (var t of r.tags) seen.push(t); if (seen.join('|') !== 'x|y') bad.push('for-of');
          var keys = []; for (var k in r.address) keys.push(k); if (keys.join() !== 'city,zip') bad.push('for-in');
          if (JSON.stringify(r) !== '{"address":{"city":"Sevilla","zip":"41001"},"lines":[{"qty":2},{"qty":3}],"tags":["x","y"]}') bad.push('stringify ' + JSON.stringify(r));
          var copy = Object.assign({}, r.address); if (copy.city !== 'Sevilla') bad.push('assign');
          var spread = { ...r.address, extra: 1 }; if (spread.zip !== '41001') bad.push('spread');
          if (!Array.isArray(r.lines) || r.lines.indexOf(r.lines[0]) !== 0 || r.lines.slice(1)[0].qty !== 3) bad.push('array methods');
          if (args.step.name !== 'echo') bad.push('step ' + args.step.name);
          if (Object.keys(args).join() !== 'result,vars,step') bad.push('args keys ' + Object.keys(args).join());
          if (Object.getPrototypeOf(r) !== Object.prototype || Object.getPrototypeOf(r.lines) !== Array.prototype) bad.push('prototype');
          if (typeof Object.getPrototypeOf(r).constructor !== 'function' || r.constructor !== Object) bad.push('constructor');
          if (typeof host !== 'object' || Object.keys(host).length !== 0) bad.push('host ' + Object.keys(host));
          if (typeof context !== 'undefined' || typeof nextStep !== 'undefined' || typeof input !== 'undefined') bad.push('step globals');
          return bad.join('; ');
        };
        """;

    [Fact]
    public async Task A_validator_reads_the_result_through_the_facade_as_plain_JavaScript()
    {
        YamlPipeline pipeline = Factory(Library("checks", Reads)).CreateFromYaml(Echo(
            """{"address":{"city":"Sevilla","zip":"41001"},"lines":[{"qty":2},{"qty":3}],"tags":["x","y"]}""",
            "    validators: [js:checks.reads]\n"));
        PipelineContext context = NewContext("jsval-reads");

        IStepResult result = await RunAsync(pipeline, context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(1, ScriptWorkerClient.StartedWorkers(context.CorrelationId));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    private const string Writes = """
        function expectThrow(f, message) {
          try { f(); } catch (e) { return e instanceof TypeError && e.message === message ? '' : 'wrong error: ' + e.message + ' | '; }
          return 'no error for ' + message + ' | ';
        }
        exports.readOnly = function (args) {
          return expectThrow(function () { args.result.address.city = 'x'; }, 'args.result.address.city is read-only.')
            + expectThrow(function () { args.result.lines[0].qty = 9; }, 'args.result.lines[0].qty is read-only.')
            + expectThrow(function () { args.result.lines.push(1); }, 'args.result.lines[2] is read-only.')
            + expectThrow(function () { delete args.result.address; }, 'args.result.address is read-only.')
            + expectThrow(function () { Object.defineProperty(args.result, 'n', { value: 1 }); }, 'args.result.n is read-only.')
            + expectThrow(function () { args.result = 1; }, 'args.result is read-only.')
            + expectThrow(function () { args.step.name = 'other'; }, 'args.step.name is read-only.')
            + expectThrow(function () { Object.setPrototypeOf(args.result, null); }, 'args.result cannot change its prototype.')
            + expectThrow(function () { Object.freeze(args.vars); }, 'args.vars cannot be frozen, sealed or made non-extensible.')
            + expectThrow(function () { args.vars.fn = function () {}; }, 'args.vars.fn only takes JSON values (objects, arrays, strings, numbers, booleans, null).');
        };
        """;

    [Fact]
    public async Task A_write_to_a_read_only_facade_throws_a_TypeError_that_names_the_property()
    {
        YamlPipeline pipeline = Factory(Library("checks", Writes)).CreateFromYaml(Echo(
            """{"address":{"city":"Sevilla"},"lines":[{"qty":2},{"qty":3}]}""", "    validators: [js:checks.readOnly]\n"));

        IStepResult result = await RunAsync(pipeline, NewContext("jsval-readonly"));

        Assert.False(result.HasError, result.Error?.Message);
    }

    private const string Patches = """
        exports.write = function (args) {
          var v = args.vars;
          v.count = v.count + 1;
          v.order.meta.b = 2;
          delete v.order.meta.a;
          v.order.lines.push({ qty: 3 });
          v.order.lines[0].qty = 10;
          v.fresh = { x: [1, 2] };
          v.fresh.x.push(3);
          delete v.gone;
          var old = v.order.note; v.order.note = { text: 'new' }; old.text = 'lost';
          if (!('count' in v) || ('gone' in v) || Object.keys(v).indexOf('order') < 0) return 'vars keys';
          if (v.hidden !== undefined) return 'a .NET object reached the script';
          return true;
        };
        """;

    [Fact]
    public async Task Writes_to_args_vars_come_back_as_a_patch_applied_in_place_to_the_pipeline_variables()
    {
        YamlPipeline pipeline = Factory(Library("checks", Patches)).CreateFromYaml(Echo("{}", "    validators: [js:checks.write]\n"));
        PipelineContext context = NewContext("jsval-patch");
        JObject order = JObject.Parse("""{"meta":{"a":1},"lines":[{"qty":1}],"note":{"text":"old"}}""");
        context.Metadata["count"] = 5;
        context.Metadata["order"] = order;
        context.Metadata["gone"] = "bye";
        context.Metadata["hidden"] = new Inventory();

        IStepResult result = await RunAsync(pipeline, context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(6, ((JToken)context.Metadata["count"]!).Value<int>());
        Assert.Same(order, context.Metadata["order"]);
        Assert.Equal("""{"meta":{"b":2},"lines":[{"qty":10},{"qty":3}],"note":{"text":"new"}}""", order.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("""{"x":[1,2,3]}""", ((JToken)context.Metadata["fresh"]!).ToString(Newtonsoft.Json.Formatting.None));
        Assert.False(context.Metadata.ContainsKey("gone"));
        Assert.IsType<Inventory>(context.Metadata["hidden"]);
    }

    [Fact]
    public void The_patch_format_sets_and_deletes_by_path()
    {
        PipelineContext context = new();
        JObject root = JObject.Parse("""{"a":{"b":1,"c":[1,2]}}""");
        context.Metadata["root"] = root;
        context.Metadata["drop"] = 1;

        VariablePatch.Apply(context, JArray.Parse("""
            [ { "op": "set", "path": ["root", "a", "b"], "value": 2 },
              { "op": "set", "path": ["root", "a", "c"], "value": [9] },
              { "op": "delete", "path": ["root", "x"] },
              { "op": "set", "path": ["n"], "value": { "k": true } },
              { "op": "delete", "path": ["drop"] } ]
            """));

        Assert.Equal("""{"a":{"b":2,"c":[9]}}""", root.ToString(Newtonsoft.Json.Formatting.None));
        Assert.True(((JToken)context.Metadata["n"]!).Value<bool>("k"));
        Assert.False(context.Metadata.ContainsKey("drop"));
        Assert.Throws<InvalidOperationException>(() => VariablePatch.Apply(context, JArray.Parse("""[ { "op": "set", "path": ["n", "k", "deep"], "value": 1 } ]""")));
    }

    private const string Returns = """
        exports.no = function () { return false; };
        exports.text = function () { return 'total does not match'; };
        exports.empty = function () { return ''; };
        exports.shape = function () { return { isValid: false, error: 'from shape' }; };
        exports.shapeOk = function () { return { isValid: true }; };
        exports.number = function () { return 5; };
        exports.nothing = function () { };
        exports.throws = function () { var a = 1;
          throw new Error('boom'); };
        """;

    [Theory]
    [InlineData("shapeOk", null)]
    [InlineData("empty", null)]
    [InlineData("no", "JavaScript validator 'js:r.no' rejected the result.")]
    [InlineData("text", "total does not match")]
    [InlineData("shape", "from shape")]
    [InlineData("number", "it returned number; a JavaScript validator returns true, false, an error message (empty = valid) or { isValid, error }.")]
    [InlineData("nothing", "it returned undefined;")]
    [InlineData("throws", "Error: boom\n    at exports.throws (lib:r:9:9)")]
    public async Task What_a_validator_returns_maps_to_the_CSharp_contract(string function, string? error)
    {
        YamlPipeline pipeline = Factory(Library("r", Returns)).CreateFromYaml(Echo("1", $"    validators: [js:r.{function}]\n"));
        PipelineContext context = NewContext("jsval-returns");

        IStepResult result = await RunAsync(pipeline, context);

        if (error == null)
        {
            Assert.False(result.HasError, result.Error?.Message);
        }
        else
        {
            Assert.True(result.HasError);
            output.WriteLine(result.Error!.Message);
            Assert.Contains(error, result.Error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task A_result_validator_gets_the_result_read_only_and_no_step()
    {
        const string source = """
            exports.check = function (args) {
              if ('step' in args) return 'a result validator has no step';
              if (Object.keys(args).join() !== 'result,vars') return 'args keys ' + Object.keys(args).join();
              try { args.result.total = 0; return 'writable'; } catch (e) { }
              return args.result.total === 10 ? true : 'total ' + args.result.total;
            };
            """;
        YamlPipeline pipeline = Factory(Library("rv", source)).CreateFromYaml(Echo("""{"total":10}""", "    resultValidators: [js:rv.check]\n"));

        IStepResult result = await RunAsync(pipeline, NewContext("jsval-result"));

        Assert.False(result.HasError, result.Error?.Message);
    }

    [Fact]
    public async Task A_runaway_validator_kills_the_worker_fails_its_step_and_the_next_validation_still_works()
    {
        const string source = """
            exports.loop = function () { for (;;) { } };
            exports.bomb = function () { var a = new Array(5e8).fill(1.5); return a.length > 0; };
            exports.ok = function (args) { args.vars[args.step.name] = true; return true; };
            """;
        YamlPipelineFactory factory = Factory(Library("v", source, s => s.ConfigureJsValidators(new JsValidatorOptions { TimeoutMs = 500 })));
        YamlPipeline pipeline = factory.CreateFromYaml("""
            name: runaway
            steps:
              - id: p
                type: ParallelStep
                failFast: false
                branches:
                  - id: looping
                    type: JsonEcho
                    json: '1'
                    validators: [js:v.loop]
                  - id: bombing
                    type: JsonEcho
                    json: '1'
                    validators: [js:v.bomb]
              - id: fine
                type: JsonEcho
                json: '1'
                validators: [js:v.ok]
            """);
        PipelineContext context = NewContext("jsval-runaway");
        Stopwatch clock = Stopwatch.StartNew();

        IStepResult result = await RunAsync(pipeline, context);

        ParallelResult parallel = context.StepResults.Values.OfType<ParallelResult>().Single();
        string errors = string.Join("\n", parallel.Value.Values.Where(r => r.HasError).Select(r => r.Error!.Message));
        output.WriteLine($"{clock.ElapsedMilliseconds} ms; {errors}");
        Assert.False(result.HasError, result.Error?.Message);
        Assert.True((bool)((JToken)context.Metadata["fine"]!));
        Assert.Contains("Hook 'js:v.loop' of step 'looping' failed: the script exceeded the time limit of 500 ms", errors, StringComparison.Ordinal);
        Assert.Contains("Hook 'js:v.bomb' of step 'bombing' failed", errors, StringComparison.Ordinal);
        Assert.Equal(3, ScriptWorkerClient.StartedWorkers(context.CorrelationId));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task Each_call_gets_a_fresh_context_no_global_or_module_state_survives()
    {
        const string source = """
            var counter = 0;
            exports.count = function (args) {
              counter++;
              if (typeof globalThis.leak !== 'undefined') return 'global leaked';
              globalThis.leak = args.step ? args.step.name : 'result';
              return counter === 1 ? true : 'module state leaked: ' + counter;
            };
            """;
        YamlPipeline pipeline = Factory(Library("iso", source)).CreateFromYaml("""
            name: iso
            steps:
              - id: a
                type: JsonEcho
                validators: [js:iso.count]
              - id: b
                type: JsonEcho
                validators: [js:iso.count]
              - id: c
                type: JsonEcho
                resultValidators: [js:iso.count]
            """);

        IStepResult result = await RunAsync(pipeline, NewContext("jsval-iso"));

        Assert.False(result.HasError, result.Error?.Message);
    }

    [Fact]
    public async Task Console_output_of_a_validator_goes_to_the_steps_logger()
    {
        CapturingLogger logger = new();
        const string source = "exports.talk = function (args) { console.warn('checked', { n: args.result }); log('plain'); return true; };";
        YamlPipeline pipeline = Factory(Library("talk", source), logger).CreateFromYaml(Echo("3", "    validators: [js:talk.talk]\n"));

        IStepResult result = await RunAsync(pipeline, NewContext("jsval-console"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Contains(logger.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("checked {\"n\":3}", StringComparison.Ordinal)
            && l.Text.Contains("js:talk.talk", StringComparison.Ordinal) && l.Category.EndsWith("JsonEchoStep", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, l => l.Level == LogLevel.Information && l.Text.EndsWith("plain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_compile_error_in_a_validator_library_fails_the_run_at_start_with_its_own_line()
    {
        YamlPipeline pipeline = Factory(Library("bad", "exports.f = function () {\n  return true;\n  var x = ;\n};\n")).CreateFromYaml(
            Echo("1", "    validators: [js:bad.f]\n"));
        PipelineContext context = NewContext("jsval-compile");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(pipeline, context));

        output.WriteLine(ex.Message);
        Assert.Contains("does not compile, so no step was run", ex.Message, StringComparison.Ordinal);
        Assert.Contains("library 'bad': script error:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("lib:bad:3:", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task A_function_the_library_does_not_export_fails_the_run_at_start_and_lists_what_it_exports()
    {
        YamlPipeline pipeline = Factory(Library("lib", "exports.a = function () { return true; }; exports.b = function () { return true; }; exports.c = 1;"))
            .CreateFromYaml(Echo("1", "    validators: [js:lib.nope]\n"));
        PipelineContext context = NewContext("jsval-missing");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(pipeline, context));

        Assert.Contains("names a function its library does not export", ex.Message, StringComparison.Ordinal);
        Assert.Contains("step 'echo', validator 'js:lib.nope': the library 'lib' has no function 'nope' (it exports: a, b).", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public void An_unknown_library_or_a_malformed_name_is_a_build_error()
    {
        YamlPipelineFactory factory = Factory(Library("lib", "exports.a = function () { return true; };"));

        InvalidOperationException unknown = Assert.Throws<InvalidOperationException>(() => factory.CreateFromYaml(Echo("1", "    validators: [js:other.a]\n")));
        InvalidOperationException malformed = Assert.Throws<InvalidOperationException>(() => factory.CreateFromYaml(Echo("1", "    resultValidators: ['js:lib.1a']\n")));

        Assert.Contains("validator 'js:other.a': the JavaScript library 'other' is not known to this host. Libraries: lib.", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("result validator 'js:lib.1a': 'js:lib.1a' is not 'js:<library>.<function>'", malformed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_validator_library_can_require_others_and_a_library_name_may_have_dots()
    {
        YamlPipeline pipeline = Factory(services =>
        {
            services.AddScriptLibrary(new ScriptLibrary("shop.rules", "var m = require('shop.math'); exports.positive = function (args) { return m.sum(args.result) > 0; };"));
            services.AddScriptLibrary(new ScriptLibrary("shop.math", "exports.sum = function (a) { return a.reduce(function (x, y) { return x + y; }, 0); };"));
        }).CreateFromYaml(Echo("[1,2]", "    validators: [js:shop.rules.positive]\n"));

        IStepResult result = await RunAsync(pipeline, NewContext("jsval-require"));

        Assert.False(result.HasError, result.Error?.Message);
    }

    [Fact]
    public async Task Script_objects_are_host_functions_for_steps_and_validators_without_a_grant()
    {
        Inventory inventory = new();
        const string source = """
            exports.inStock = function (args) {
              var s = host.inventory.stock({ sku: args.result.sku });
              if (host.inventory.echoAsync('x') !== 'echo:x' || host.inventory.ping() !== 'pong') return 'calls';
              return s.units > 0 ? true : 'no stock of ' + s.sku;
            };
            """;
        YamlPipelineFactory factory = Factory(Library("stock", source, s => s.AddScriptObject("inventory", _ => inventory)));
        YamlPipeline pipeline = factory.CreateFromYaml("""
            name: objects
            steps:
              - id: lookup
                type: ScriptStep
                allow: [inventory.stock]
                script: |
                  return { sku: host.inventory.stock({ sku: 'A-1' }).sku };
                validators: [js:stock.inStock]
            """);

        IStepResult result = await RunAsync(pipeline, NewContext("jsval-objects"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, inventory.Calls);

        IScriptCatalog catalog = new ServiceCollection().AddScriptExtension().AddScriptObject<Inventory>("inventory").BuildServiceProvider().GetRequiredService<IScriptCatalog>();
        Assert.Equal(["inventory.echoAsync", "inventory.ping", "inventory.stock"],
            catalog.HostFunctions.OfType<ScriptObjectFunction>().Select(f => f.Name).Order(StringComparer.Ordinal));
    }

    public sealed class Overloaded
    {
        public int Get(int a) => a;

        public int Get(string a) => a.Length;
    }

    public sealed class Exposure : IDisposable
    {
        public string Value { get; set; } = "";

        public Func<int> Factory() => () => 1;

        public void Take(Action action) => action();

        public void Dispose()
        {
        }

        public T Generic<T>() => default!;

        public string Name() => "n";
    }

    [Fact]
    public void Only_plain_public_methods_of_a_script_object_are_exposed_and_overloads_are_refused()
    {
        Assert.Equal(["Name"], ScriptObjects.Exposed(typeof(Exposure)).Select(m => m.Name));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ScriptObjects.Functions([new RegisteredScriptObject("o", typeof(Overloaded))]));
        Assert.Contains("overloads are not allowed", ex.Message, StringComparison.Ordinal);

        // In the catalog such an object is left out (with a warning); the others stay.
        IScriptCatalog catalog = new ServiceCollection().AddScriptExtension()
            .AddScriptObject<Overloaded>("broken").AddScriptObject<Inventory>("inventory")
            .BuildServiceProvider().GetRequiredService<IScriptCatalog>();
        Assert.Equal(["inventory"], catalog.HostFunctions.OfType<ScriptObjectFunction>().Select(f => f.ObjectName).Distinct());
    }

    [Fact]
    public void A_script_object_registered_by_an_extension_is_captured_and_reported_by_ActualInfo()
    {
        ExtensionCapture capture = new();
        capture.AddScriptObject<Inventory>("inventory", description: "stock");

        ExtensionEntry entry = Assert.Single(capture.ActualInfo().Entries);
        Assert.Equal(new ExtensionEntry(ExtensionKinds.ScriptObject, "inventory", typeof(Inventory).FullName, "stock"), entry);
        Assert.Throws<ArgumentException>(() => capture.AddScriptObject<Inventory>("bad name"));
    }

    [Fact]
    public async Task A_run_whose_libraries_come_from_a_plugin_js_folder_validates_with_them()
    {
        string folder = Path.Combine(Path.GetTempPath(), "jsval-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "js"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "js", "rules.js"), "exports.even = function (args) { return args.result % 2 === 0; };");
            File.WriteAllText(Path.Combine(folder, "js", "bad name!.js"), "x");
            File.WriteAllText(Path.Combine(folder, "js", "notes.txt"), "not a library");

            (IReadOnlyList<ScriptLibrary> libraries, IReadOnlyList<string> problems) = ScriptLibraryFolder.Load(folder, "plugin 'P'");

            ScriptLibrary rules = Assert.Single(libraries);
            Assert.Equal("rules", rules.Name);
            Assert.Contains("plugin 'P'", rules.Description, StringComparison.Ordinal);
            Assert.Contains("bad name!.js", Assert.Single(problems), StringComparison.Ordinal);

            YamlPipeline pipeline = Factory(s => s.AddScriptLibrary(rules)).CreateFromYaml(Echo("4", "    validators: [js:rules.even]\n"));
            Assert.False((await RunAsync(pipeline, NewContext("jsval-folder"))).HasError);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_validator_outside_a_run_gets_a_worker_of_its_own_for_the_call()
    {
        ServiceProvider services = new ServiceCollection().AddScriptExtension()
            .AddScriptLibrary(new ScriptLibrary("alone", "exports.ok = function (args) { return args.result === 1 && args.step.name === 'solo-step'; };"))
            .BuildServiceProvider();
        IValidatorFactory factory = services.GetServices<IValidatorFactory>().Single(f => f.Prefix == "js:");
        JsonEchoStep step = new();
        typeof(StepBase).GetProperty(nameof(StepBase.Name))!.GetSetMethod(nonPublic: true)!.Invoke(step, ["solo-step"]);
        IStepValidator validator = factory.CreateValidator("js:alone.ok", step);
        PipelineContext context = new(null, services);

        (bool valid, string? error) = await validator.ValidateAsync(new JsonStepResult(step, new JValue(1)), context, CancellationToken.None);

        Assert.True(valid, error);
        Assert.Equal(1, ScriptWorkerClient.StartedWorkers("solo-step"));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers("solo-step"));
    }

    /// <summary>
    /// Cost of one validation with the facades against the same check written as a ScriptStep over plain JSON (no facade), in one run's worker.
    /// Not a gate: it prints the numbers (architecture §18.7 quotes them).
    /// </summary>
    [Fact]
    public async Task Measure_the_cost_of_a_validation_with_and_without_the_facades()
    {
        // The same check twice: a validator reading the result through args.result (facades), and a ScriptStep reading the same value as
        // its plain input (JSON.parse, no Proxy). Each loop runs an echo step then the JavaScript; one call per iteration. The per-call cost
        // is the difference between 201 and 1 iterations divided by 200, so the run's start-up and compile cancel out.
        const string json = """{"lines":[{"qty":2,"price":3},{"qty":1,"price":4},{"qty":5,"price":1}],"total":15}""";
        const string check = "var s = 0; for (var i = 0; i < r.lines.length; i++) s += r.lines[i].qty * r.lines[i].price; return s === r.total;";
        YamlPipelineFactory factory = Factory(Library("cost", "exports.total = function (args) { var r = args.result; " + check + " };"));
        string Facades(int n) => "name: f\nsteps:\n  - id: l\n    type: LoopStep\n    maxIterations: " + n + "\n    steps:\n      - id: e\n        type: JsonEcho\n        json: '" + json + "'\n        validators: [js:cost.total]\n";
        string Plain(int n) => "name: p\nsteps:\n  - id: l\n    type: LoopStep\n    maxIterations: " + n + "\n    steps:\n      - id: e\n        type: JsonEcho\n        json: '" + json + "'\n      - id: s\n        type: ScriptStep\n        script: |\n          var r = input; " + check + "\n";

        async Task<long> TimeAsync(string yaml)
        {
            YamlPipeline pipeline = factory.CreateFromYaml(yaml);
            Stopwatch clock = Stopwatch.StartNew();
            IStepResult result = await RunAsync(pipeline, NewContext("jsval-cost"));
            clock.Stop();
            Assert.False(result.HasError, result.Error?.Message);
            return clock.ElapsedMilliseconds;
        }

        await TimeAsync(Facades(1));
        for (int round = 0; round < 2; round++)
        {
            long f1 = await TimeAsync(Facades(1)), f201 = await TimeAsync(Facades(201));
            long p1 = await TimeAsync(Plain(1)), p201 = await TimeAsync(Plain(201));
            output.WriteLine(
                $"round {round + 1}: facades {f1} ms (1 call) / {f201} ms (201 calls) = {(f201 - f1) / 200.0:0.00} ms per validation; " +
                $"plain {p1} ms / {p201} ms = {(p201 - p1) / 200.0:0.00} ms per call");
        }
    }
}
