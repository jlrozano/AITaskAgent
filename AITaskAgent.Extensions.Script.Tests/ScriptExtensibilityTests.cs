using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Script.Configuration;
using AITaskAgent.Script.Hosting;
using AITaskAgent.Script.Steps;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Script.Tests;

/// <summary>
/// Phase F: closed <c>require</c>, the native library, host functions (default deny, budgets, built-ins), declarative next
/// steps and the static checks. Everything runs in the real worker; no LLM.
/// </summary>
[Collection("ScriptWorker")]
public sealed class ScriptExtensibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "script-ext-" + Guid.NewGuid().ToString("N"));

    public ScriptExtensibilityTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ServiceCollection Base() => (ServiceCollection)new ServiceCollection().AddScriptExtension();

    private static async Task<IStepResult> Run(ScriptStep step, IServiceProvider services, object? input = null)
        => await ((IStep)step).ExecuteAsync(
            new JsonStepResult(step, input == null ? JValue.CreateNull() : JToken.FromObject(input)),
            new PipelineContext(null, services), 1, null, CancellationToken.None);

    private static ScriptStep Step(string script, string id = "ext", params string[] uses)
        => new(id) { Script = script, Uses = [.. uses], TimeoutMs = 20_000 };

    private static ScriptStep Allowing(string script, string id, params string[] allow)
        => new(id) { Script = script, Allow = [.. allow], TimeoutMs = 20_000 };

    [Fact]
    public async Task The_worker_refuses_a_run_of_another_protocol_version_and_announces_its_own()
    {
        using System.Diagnostics.Process worker = System.Diagnostics.Process.Start(AITaskAgent.Script.Isolation.ScriptWorkerLocator.CreateStartInfo())!;
        string? ready = await worker.StandardOutput.ReadLineAsync();
        await worker.StandardInput.WriteLineAsync("""{"t":"run","proto":1,"fn":"n","name":"n","input":"null","timeoutMs":5000,"heapMb":64,"stackKb":512}""");
        await worker.StandardInput.FlushAsync();
        string? result = await worker.StandardOutput.ReadLineAsync();
        await worker.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        // Protocol 3 (2026-10-23): one worker per run, load + calls.
        Assert.Contains("\"proto\":5", ready);
        Assert.Contains("\"ok\":false", result);
        Assert.Contains("speaks script protocol 1", result);
    }

    // ---- require ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_declared_library_is_required_and_cached_per_run()
    {
        ServiceProvider services = Base()
            .AddScriptLibrary(new ScriptLibrary("text", "var n = 0; exports.slug = function (s) { n++; return s.toLowerCase().replace(/\\s+/g, '-'); }; exports.count = function () { return n; };"))
            .BuildServiceProvider();

        var result = await Run(Step("var a = require('text'), b = require('text'); a.slug('Hello World'); return { slug: b.slug('A B'), calls: a.count(), same: a === b };", "req", "text"), services);

        Assert.False(result.HasError, result.Error?.Message);
        var json = (JObject)result.Value!;
        Assert.Equal("a-b", (string?)json["slug"]);
        Assert.Equal(2, (int)json["calls"]!);
        Assert.True((bool)json["same"]!);
    }

    [Fact]
    public async Task A_library_can_require_another_declared_one_and_cycles_get_the_partial_exports()
    {
        ServiceProvider services = Base()
            .AddScriptLibrary(new ScriptLibrary("a", "exports.name = 'a'; var b = require('b'); exports.fromB = b.name; exports.seenByB = b.sawA;"))
            .AddScriptLibrary(new ScriptLibrary("b", "var a = require('a'); exports.name = 'b'; exports.sawA = a.name;"))
            .BuildServiceProvider();

        var result = await Run(Step("var a = require('a'); return { fromB: a.fromB, seenByB: a.seenByB };", "cycle", "a", "b"), services);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("b", (string?)((JObject)result.Value!)["fromB"]);
        Assert.Equal("a", (string?)((JObject)result.Value!)["seenByB"]);
    }

    [Fact]
    public async Task Requiring_something_not_declared_in_uses_fails_with_a_clear_message()
    {
        ServiceProvider services = Base().AddScriptLibrary(new ScriptLibrary("text", "exports.x = 1;")).BuildServiceProvider();

        var result = await Run(Step("return require('text').x;", "closed"), services);

        Assert.True(result.HasError);
        Assert.Contains("module 'text' is not declared in 'uses'", result.Error!.Message);
    }

    [Fact]
    public async Task Fs_and_other_node_style_modules_do_not_exist()
    {
        var result = await Run(Step("return require('fs').readFileSync('x');", "nofs"), Base().BuildServiceProvider());

        Assert.True(result.HasError);
        Assert.Contains("module 'fs' is not declared in 'uses'", result.Error!.Message);
    }

    [Fact]
    public async Task An_unknown_library_in_uses_is_an_error_naming_it()
    {
        var result = await Run(Step("return 1;", "unknown", "ghost"), Base().BuildServiceProvider());

        Assert.True(result.HasError);
        Assert.Contains("'ghost'", result.Error!.Message);
    }

    [Fact]
    public async Task A_library_error_names_the_library_and_the_line()
    {
        ServiceProvider services = Base()
            .AddScriptLibrary(new ScriptLibrary("broken", "exports.x = 1;\nexports.y = (;\n"))
            .BuildServiceProvider();

        var result = await Run(Step("return require('broken').x;", "broken-step", "broken"), services);

        Assert.True(result.HasError);
        Assert.Contains("lib:broken", result.Error!.Message);
    }

    [Fact]
    public async Task A_library_runtime_error_names_the_library()
    {
        ServiceProvider services = Base()
            .AddScriptLibrary(new ScriptLibrary("boom", "exports.go = function () {\n  throw new Error('kaboom');\n};"))
            .BuildServiceProvider();

        var result = await Run(Step("return require('boom').go();", "boom-step", "boom"), services);

        Assert.True(result.HasError);
        Assert.Contains("kaboom", result.Error!.Message);
        Assert.Contains("lib:boom", result.Error.Message);
    }

    // ---- native library --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_native_library_hashes_encodes_and_makes_ids()
    {
        var result = await Run(Step("""
            return {
              sha256: hash.sha256('abc'), md5: hash.md5('abc'), sha1: hash.sha1('abc'),
              b64: base64.encode('Ñandú ✓'), back: base64.decode(base64.encode('Ñandú ✓')),
              uuid: uuid.v4(), uuid2: uuid.v4()
            };
            """, "native"), Base().BuildServiceProvider());

        Assert.False(result.HasError, result.Error?.Message);
        var json = (JObject)result.Value!;
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", (string?)json["sha256"]);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", (string?)json["md5"]);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", (string?)json["sha1"]);
        Assert.Equal("w5FhbmTDuiDinJM=", (string?)json["b64"]);
        Assert.Equal("Ñandú ✓", (string?)json["back"]);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", (string?)json["uuid"]);
        Assert.NotEqual((string?)json["uuid"], (string?)json["uuid2"]);
    }

    [Fact]
    public async Task The_native_dates_use_invariant_culture_and_explicit_zones()
    {
        var result = await Run(Step("""
            return {
              utc: date.format('2026-10-19T08:30:00Z', 'yyyy-MM-dd HH:mm'),
              plus2: date.format('2026-10-19T08:30:00Z', 'yyyy-MM-dd HH:mm zzz', '+02:00'),
              ms: date.format(0, 'yyyy-MM-dd', 'UTC'),
              parsed: 't=' + date.parse('19/10/2026 10:30', 'dd/MM/yyyy HH:mm', '+02:00'),
              iso: 't=' + date.parse('2026-10-19T08:30:00+00:00')
            };
            """, "dates"), Base().BuildServiceProvider());

        Assert.False(result.HasError, result.Error?.Message);
        var json = (JObject)result.Value!;
        Assert.Equal("2026-10-19 08:30", (string?)json["utc"]);
        Assert.Equal("2026-10-19 10:30 +02:00", (string?)json["plus2"]);
        Assert.Equal("1970-01-01", (string?)json["ms"]);
        Assert.Equal("t=2026-10-19T08:30:00.000Z", (string?)json["parsed"]);
        Assert.Equal("t=2026-10-19T08:30:00.000Z", (string?)json["iso"]);
    }

    [Fact]
    public async Task An_unknown_time_zone_is_a_script_error()
    {
        var result = await Run(Step("return date.format(0, 'yyyy', 'Nowhere/Land');", "badzone"), Base().BuildServiceProvider());

        Assert.True(result.HasError);
        Assert.Contains("unknown time zone", result.Error!.Message);
    }

    [Fact]
    public async Task The_native_path_helpers_are_pure_string_logic()
    {
        var result = await Run(Step("""
            return {
              join: path.join('a', 'b/../c', 'd.txt'), norm: path.normalize('/x//y/./z/..'), base: path.basename('/a/b/c.tar.gz'),
              dir: path.dirname('/a/b/c.txt'), ext: path.extname('/a/b/c.tar.gz'), noext: path.extname('/a/.hidden'),
              back: path.normalize('a\\b\\..\\c')
            };
            """, "paths"), Base().BuildServiceProvider());

        Assert.False(result.HasError, result.Error?.Message);
        var json = (JObject)result.Value!;
        Assert.Equal("a/c/d.txt", (string?)json["join"]);
        Assert.Equal("/x/y", (string?)json["norm"]);
        Assert.Equal("c.tar.gz", (string?)json["base"]);
        Assert.Equal("/a/b", (string?)json["dir"]);
        Assert.Equal(".gz", (string?)json["ext"]);
        Assert.Equal("", (string?)json["noext"]);
        Assert.Equal("a/c", (string?)json["back"]);
    }

    [Fact]
    public async Task The_globals_of_the_library_are_frozen()
    {
        var result = await Run(Step("'use strict'; try { hash.sha256 = 1; return 'changed'; } catch (e) { return 'frozen'; }", "frozen"), Base().BuildServiceProvider());

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("frozen", (string?)result.Json());
    }

    // ---- host functions --------------------------------------------------------------------------------------

    private static IScriptHostFunction Echo(Func<JToken, JToken?>? handler = null)
        => new DelegateHostFunction("test.echo", (args, _, _) => Task.FromResult<JToken?>(handler?.Invoke(args) ?? args), "Echoes its argument.");

    [Fact]
    public async Task Without_a_grant_a_host_function_is_refused_before_the_script_starts()
    {
        ServiceProvider services = Base().AddScriptHostFunction(Echo()).BuildServiceProvider();

        var result = await Run(Allowing("return host.test.echo(1);", "deny", "test.echo"), services);

        Assert.True(result.HasError);
        Assert.Contains("'test.echo'", result.Error!.Message);
        Assert.Contains("not granted", result.Error.Message);
    }

    [Fact]
    public async Task A_granted_and_allowed_host_function_is_called_with_json_both_ways()
    {
        ServiceProvider services = Base()
            .AddScriptHostFunction(Echo(a => new JObject { ["got"] = a, ["extra"] = true }))
            .ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = ["test.echo"] })
            .BuildServiceProvider();

        var result = await Run(Allowing("return host.test.echo({ n: [1, 2], s: 'ñ' });", "call", "test.echo"), services);

        Assert.False(result.HasError, result.Error?.Message);
        var json = (JObject)result.Value!;
        Assert.Equal(2, ((JArray)json["got"]!["n"]!).Count);
        Assert.Equal("ñ", (string?)json["got"]!["s"]);
        Assert.True((bool)json["extra"]!);
    }

    [Fact]
    public async Task A_function_the_step_did_not_allow_does_not_exist_in_the_script_even_if_the_host_grants_it()
    {
        ServiceProvider services = Base()
            .AddScriptHostFunction(Echo())
            .ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = ["test.echo"] })
            .BuildServiceProvider();

        var result = await Run(Step("return typeof host.test;", "narrow"), services);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("undefined", (string?)result.Json());
    }

    [Fact]
    public async Task A_host_function_error_is_a_catchable_javascript_error()
    {
        ServiceProvider services = Base()
            .AddScriptHostFunction(new DelegateHostFunction("test.fail", (_, _, _) => throw new InvalidOperationException("nope"), "Always fails."))
            .ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = ["test.fail"] })
            .BuildServiceProvider();

        var result = await Run(Allowing("try { host.test.fail(); return 'no'; } catch (e) { return String(e.message); }", "catch", "test.fail"), services);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Contains("nope", (string?)result.Json());
    }

    [Fact]
    public async Task The_call_budget_is_enforced()
    {
        ServiceProvider services = Base()
            .AddScriptHostFunction(Echo())
            .ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = ["test.echo"] })
            .BuildServiceProvider();

        ScriptStep step = Allowing("var n = 0; for (var i = 0; i < 5; i++) { host.test.echo(i); n++; } return n;", "budget", "test.echo");
        step.MaxCalls = 3;
        var result = await Run(step, services);

        Assert.True(result.HasError);
        Assert.Contains("budget of 3 host calls", result.Error!.Message);
    }

    [Fact]
    public async Task The_byte_budget_is_enforced()
    {
        ServiceProvider services = Base()
            .AddScriptHostFunction(Echo())
            .ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = ["test.echo"] })
            .BuildServiceProvider();

        ScriptStep step = Allowing("host.test.echo('x'.repeat(3000)); return 1;", "bytes", "test.echo");
        step.MaxCallKb = 1;
        var result = await Run(step, services);

        Assert.True(result.HasError);
        Assert.Contains("bytes exchanged with host functions", result.Error!.Message);
    }

    [Fact]
    public async Task The_ceilings_cannot_be_raised_from_yaml()
    {
        ScriptStep step = Step("return 1;", "ceiling");
        step.MaxCalls = 1_000_000;
        var result = await Run(step, Base().BuildServiceProvider());

        Assert.True(result.HasError);
        Assert.Contains("'maxCalls' must be between", result.Error!.Message);
    }

    [Fact]
    public async Task Config_get_reads_only_what_the_host_exposes()
    {
        ServiceProvider services = Base()
            .ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = ["config.get"], Config = new() { ["region"] = "eu" } })
            .BuildServiceProvider();

        var result = await Run(Allowing("return { a: host.config.get('region'), b: host.config.get('secret') === undefined };", "cfg", "config.get"), services);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("eu", (string?)((JObject)result.Value!)["a"]);
        Assert.True((bool)((JObject)result.Value!)["b"]!);
    }

    [Fact]
    public async Task File_read_text_works_under_a_root_and_refuses_everything_else()
    {
        string allowed = Path.Combine(_root, "data");
        Directory.CreateDirectory(allowed);
        await File.WriteAllTextAsync(Path.Combine(allowed, "hello.txt"), "hola ñ");
        string secret = Path.Combine(_root, "secret.txt");
        await File.WriteAllTextAsync(secret, "top secret");

        ServiceProvider services = Base()
            .ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = ["file.readText", "file.exists"], FileRoots = [allowed] })
            .BuildServiceProvider();

        var result = await Run(Allowing($$"""
            var out = { ok: host.file.readText('hello.txt'), exists: host.file.exists('hello.txt'), missing: host.file.exists('nope.txt') };
            function attempt(p) { try { host.file.readText(p); return 'read'; } catch (e) { return 'refused'; } }
            out.dotdot = attempt('../secret.txt');
            out.absolute = attempt({{System.Text.Json.JsonSerializer.Serialize(secret)}});
            out.unc = attempt('\\\\\\\\server\\\\share\\\\x.txt');
            out.existsOutside = host.file.exists('../secret.txt');
            return out;
            """, "files", "file.readText", "file.exists"), services);

        Assert.False(result.HasError, result.Error?.Message);
        var json = (JObject)result.Value!;
        Assert.Equal("hola ñ", (string?)json["ok"]);
        Assert.True((bool)json["exists"]!);
        Assert.False((bool)json["missing"]!);
        Assert.Equal("refused", (string?)json["dotdot"]);
        Assert.Equal("refused", (string?)json["absolute"]);
        Assert.Equal("refused", (string?)json["unc"]);
        Assert.False((bool)json["existsOutside"]!);
    }

    [Fact]
    public async Task File_read_text_without_roots_is_an_error()
    {
        ServiceProvider services = Base()
            .ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = ["file.readText"] })
            .BuildServiceProvider();

        var result = await Run(Allowing("return host.file.readText('anything.txt');", "noroots", "file.readText"), services);

        Assert.True(result.HasError);
        Assert.Contains("outside the folders", result.Error!.Message);
    }

    [Fact]
    public async Task File_read_text_refuses_a_file_over_the_size_limit()
    {
        string allowed = Path.Combine(_root, "big");
        Directory.CreateDirectory(allowed);
        await File.WriteAllTextAsync(Path.Combine(allowed, "big.txt"), new string('x', 2000));
        ServiceProvider services = Base()
            .ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = ["file.readText"], FileRoots = [allowed], MaxFileBytes = 1000 })
            .BuildServiceProvider();

        var result = await Run(Allowing("return host.file.readText('big.txt').length;", "toobig", "file.readText"), services);

        Assert.True(result.HasError);
        Assert.Contains("the limit is 1000", result.Error!.Message);
    }

    // ---- next steps ------------------------------------------------------------------------------------------

    private static YamlPipelineFactory Factory(ServiceProvider services)
        => new(services, services.GetRequiredService<IStepRegistry>(), new SchemaRegistry(), new Pipeline());

    [Fact]
    public async Task A_script_can_queue_next_steps_that_the_pipeline_runs()
    {
        ServiceProvider services = Base().BuildServiceProvider();
        var pipeline = Factory(services).CreateFromYaml("""
            name: dynamic
            steps:
              - id: planner
                type: ScriptStep
                script: |
                  nextStep({ type: 'ScriptStep', id: 'child_a', script: "return { n: input.seed + 1 };" });
                  nextStep({ type: 'ScriptStep', id: 'child_b', script: "return { n: input.n * 10 };" });
                  return { seed: 4 };
            """);

        var result = await pipeline.ExecuteAsync(new JObject(), new PipelineContext(null, services));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(50, (int)((JObject)result.Value!)["n"]!);
    }

    [Fact]
    public async Task Queued_steps_are_discarded_when_the_script_fails()
    {
        ServiceProvider services = Base().BuildServiceProvider();

        var result = await Run(Step("nextStep({ type: 'ScriptStep', id: 'x', script: 'return 1;' }); throw new Error('late');", "discard"), services);

        Assert.True(result.HasError);
        Assert.Empty(result.NextSteps);
    }

    [Theory]
    [InlineData("nextStep({ type: 'ScriptStep', script: 'return 1;' });", "needs a text 'type' and a text 'id'")]
    [InlineData("nextStep({ type: 'NoSuchStep', id: 'x' });", "could not be created")]
    [InlineData("nextStep({ type: 'ScriptStep', id: 'x', script: '1' }); nextStep({ type: 'ScriptStep', id: 'x', script: '2' });", "used twice")]
    [InlineData("nextStep('text');", "nextStep takes a step definition object")]
    public async Task A_bad_next_step_is_an_error_naming_the_problem(string script, string expected)
    {
        var result = await Run(Step(script, "badnext"), Base().BuildServiceProvider());

        Assert.True(result.HasError);
        Assert.Contains(expected, result.Error!.Message);
    }

    [Fact]
    public async Task At_most_fifty_next_steps_can_be_queued()
    {
        var result = await Run(Step("for (var i = 0; i < 51; i++) nextStep({ type: 'ScriptStep', id: 's' + i, script: 'return 1;' }); return 1;", "many"), Base().BuildServiceProvider());

        Assert.True(result.HasError);
        Assert.Contains("at most 50", result.Error!.Message);
    }

    // ---- YAML ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Uses_and_allow_are_yaml_properties()
    {
        ServiceProvider services = Base()
            .AddScriptLibrary(new ScriptLibrary("math2", "exports.double = function (x) { return x * 2; };"))
            .AddScriptHostFunction(Echo())
            .ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = ["test.echo"] })
            .BuildServiceProvider();
        var pipeline = Factory(services).CreateFromYaml("""
            name: yaml-ext
            steps:
              - id: go
                type: ScriptStep
                uses: [math2]
                allow: [test.echo]
                maxCalls: 5
                script: |
                  return { d: require('math2').double(21), e: host.test.echo('hi') };
            """);

        var result = await pipeline.ExecuteAsync(new JObject(), new PipelineContext(null, services));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(42, (int)((JObject)result.Value!)["d"]!);
        Assert.Equal("hi", (string?)((JObject)result.Value!)["e"]);
    }

    // ---- static checks ---------------------------------------------------------------------------------------

    [Fact]
    public void Checks_report_unknown_names_ungranted_functions_and_stray_mentions()
    {
        IScriptCatalog catalog = new ScriptCatalog([new ScriptLibrary("text", "")], [Echo()]);

        IReadOnlyList<ScriptIssue> issues = ScriptChecks.Check(
            "var t = require('text');\nvar u = require('other');\nhost.test.echo(1);\nhost.file.readText('x');",
            uses: ["text", "ghost"],
            allow: ["nothing.here"],
            catalog,
            granted: new HashSet<string>());

        Assert.Contains(issues, i => i.IsError && i.Code == "script_library_unknown" && i.Message.Contains("'ghost'"));
        Assert.Contains(issues, i => i.IsError && i.Code == "script_host_function_unknown" && i.Message.Contains("'nothing.here'"));
        Assert.Contains(issues, i => !i.IsError && i.Code == "script_require_not_declared" && i.Line == 2);
        Assert.Contains(issues, i => !i.IsError && i.Code == "script_host_call_not_allowed" && i.Line == 3);
        Assert.Contains(issues, i => !i.IsError && i.Code == "script_host_call_not_allowed" && i.Line == 4);
    }

    [Fact]
    public void Checks_warn_when_an_allowed_function_is_not_granted_and_are_quiet_when_everything_matches()
    {
        IScriptCatalog catalog = new ScriptCatalog([new ScriptLibrary("text", "")], [Echo()]);

        Assert.Contains(
            ScriptChecks.Check("return 1;", [], ["test.echo"], catalog, new HashSet<string>()),
            i => !i.IsError && i.Code == "script_host_function_not_granted");
        Assert.Empty(ScriptChecks.Check(
            "require('text'); host.test.echo(1);", ["text"], ["test.echo"], catalog, new HashSet<string> { "test.echo" }));
    }

    [Fact]
    public async Task The_syntax_check_compiles_without_running_and_reports_only_the_broken_ones()
    {
        IReadOnlyList<ScriptSyntaxIssue> issues = await ScriptSyntax.CheckAsync(
        [
            new ScriptSyntaxItem("good", false, "return 1 + 1;"),
            new ScriptSyntaxItem("bad", false, "var a = ;\nreturn a;"),
            new ScriptSyntaxItem("lib:ok", true, "exports.x = 1;"),
            new ScriptSyntaxItem("lib:bad", true, "exports.x = (;"),
            new ScriptSyntaxItem("never-runs", false, "while (true) {}"),
        ]);

        Assert.Equal(["bad", "lib:bad"], issues.Select(i => i.Id).Order().ToArray());
        Assert.All(issues, i => Assert.Contains("SyntaxError", i.Message));
        Assert.Equal(1, issues.Single(i => i.Id == "bad").Line);
    }

    [Fact]
    public async Task The_syntax_check_of_nothing_starts_no_worker()
    {
        Assert.Empty(await ScriptSyntax.CheckAsync([]));
    }

    [Fact]
    public void The_built_in_host_functions_are_listed_but_nothing_is_granted_by_default()
    {
        ServiceProvider services = Base().BuildServiceProvider();
        IScriptCatalog catalog = services.GetRequiredService<IScriptCatalog>();

        Assert.Equal(["config.get", "file.exists", "file.readText"], catalog.HostFunctions.Select(f => f.Name).ToArray());
        Assert.Null(services.GetService<ScriptHostOptions>());
    }

    [Fact]
    public void A_name_registered_twice_is_an_error()
    {
        ServiceProvider services = Base().AddScriptHostFunction(Echo()).AddScriptHostFunction(Echo()).BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IScriptCatalog>());
    }
}
