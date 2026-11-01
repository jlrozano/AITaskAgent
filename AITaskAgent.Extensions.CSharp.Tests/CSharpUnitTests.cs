using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace AITaskAgent.CSharp.Tests;

/// <summary>The unit of a package: bodies compiled together with shared code and schema classes, run in process with the full API.</summary>
public sealed class CSharpUnitTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_step_body_reads_the_input_and_the_context_and_returns_json()
    {
        (string yaml, CSharpStepSource source) = UnitHarness.Step("greet", """
            var name = call.InputAs<JObject>()!["name"]!.Value<string>();
            call.Set("seen", name);
            return call.Ok(new { greeting = "hello " + name, attempt = call.Attempt, step = call.StepName });
            """);
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([source]));

        PipelineContext context = new(null, harness.Services);
        IStepResult result = await harness.RunAsync(UnitHarness.Document(yaml), JObject.Parse("""{ "name": "Ana" }"""), context);

        Assert.False(result.HasError, result.Error?.Message);
        JObject value = Assert.IsType<JObject>(result.Value);
        Assert.Equal("hello Ana", (string?)value["greeting"]);
        Assert.Equal(1, (int?)value["attempt"]);
        Assert.Equal("greet", (string?)value["step"]);
        Assert.Equal("Ana", (string?)((JToken)context.Metadata["seen"]!));
    }

    [Fact]
    public async Task Steps_share_the_helpers_of_the_shared_file_and_the_previous_result_flows_through()
    {
        const string shared = """
            public static class Strings
            {
                public static string Title(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
            }
            """;
        (string yaml1, CSharpStepSource first) = UnitHarness.Step("title", "return call.Ok(Strings.Title(call.InputAs<string>() ?? \"\"));");
        (string yaml2, CSharpStepSource second) = UnitHarness.Step("shout", "return call.Ok(call.InputAs<string>()!.ToUpperInvariant() + \"!\");", yamlLineOffset: CountLines(yaml1));
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([first, second], shared));

        IStepResult result = await harness.RunAsync(UnitHarness.Document(yaml1, yaml2), new JValue("ana"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("ANA!", (string?)(JToken)result.Value!);
    }

    [Fact]
    public async Task A_compile_error_is_reported_on_the_yaml_line_of_the_code_with_the_line_inside_the_body()
    {
        (_, CSharpStepSource source) = UnitHarness.Step("broken", """
            var a = 1;
            var b = missing + a;
            return call.Ok(b);
            """);

        UnitCompilation compilation = await UnitHarness.CompileAsync(CSharpUnitSources.Of([source]));

        Assert.False(compilation.Success);
        CSharpDiagnostic error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal("CS0103", error.Id);
        Assert.Equal("workflow.yaml", error.File);
        Assert.Equal("broken", error.StepId);
        Assert.Equal(2, error.CodeLine);
        Assert.Equal(source.FirstLine + 1, error.Line);
        Assert.Equal(9, error.Column);
    }

    [Fact]
    public async Task A_missing_return_is_reported_on_the_step()
    {
        (_, CSharpStepSource source) = UnitHarness.Step("noreturn", "var a = 1;");

        UnitCompilation compilation = await UnitHarness.CompileAsync(CSharpUnitSources.Of([source]));

        CSharpDiagnostic error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal("CS0161", error.Id);
        Assert.Equal("noreturn", error.StepId);
        Assert.Equal(source.FirstLine, error.Line);
    }

    [Fact]
    public async Task An_error_in_the_shared_file_names_that_file()
    {
        (_, CSharpStepSource source) = UnitHarness.Step("ok", "return call.Ok(1);");

        UnitCompilation compilation = await UnitHarness.CompileAsync(CSharpUnitSources.Of([source], "public static class A\n{\n    public static int X() => nope;\n}\n"));

        CSharpDiagnostic error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal("csharp/shared.cs", error.File);
        Assert.Equal(3, error.Line);
        Assert.Null(error.StepId);
    }

    [Fact]
    public async Task A_step_that_throws_becomes_an_error_result_with_the_yaml_line()
    {
        (string yaml, CSharpStepSource source) = UnitHarness.Step("boom", """
            var x = 1;
            throw new InvalidOperationException("kaput");
            """);
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([source]));

        IStepResult result = await harness.RunAsync(UnitHarness.Document(yaml));

        Assert.True(result.HasError);
        Assert.Contains("'boom'", result.Error!.Message);
        Assert.Contains("kaput", result.Error.Message);
        Assert.Contains($"workflow.yaml, line {source.FirstLine + 1}", result.Error.Message);
    }

    [Fact]
    public async Task A_step_can_return_next_steps_built_with_the_registry()
    {
        (string parentYaml, CSharpStepSource parent) = UnitHarness.Step("parent", """
            var child = call.CreateStep("CSharpStep", "child", new JObject { ["code"] = "return call.Ok(\"from child\");" });
            return call.Ok("from parent", [child]);
            """);
        CSharpStepSource childSource = new("child", "return call.Ok(\"from child, after \" + call.InputAs<string>());", 1);
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([parent, childSource]));

        IStepResult result = await harness.RunAsync(UnitHarness.Document(parentYaml));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("from child, after from parent", (string?)(JToken)result.Value!);
    }

    [Fact]
    public async Task The_cancellation_token_reaches_the_body()
    {
        (string yaml, CSharpStepSource source) = UnitHarness.Step("wait", """
            await Task.Delay(Timeout.Infinite, call.CancellationToken);
            return call.Ok(1);
            """);
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([source]));
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(300));

        YAML.Execution.YamlPipeline pipeline = harness.Factory.CreateFromYaml(UnitHarness.Document(yaml));
        Task<IStepResult> run = pipeline.ExecuteAsync(new JObject(), new PipelineContext(null, harness.Services), cancel.Token);

        IStepResult result;
        try
        {
            result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.True(result.HasError, "a cancelled run must not succeed");
    }

    [Theory]
    [InlineData("my-step.1")]
    [InlineData("1st")]
    [InlineData("class")]
    public async Task Any_step_id_gets_a_valid_method_name(string id)
    {
        (string yaml, CSharpStepSource source) = UnitHarness.Step(id, "return call.Ok(call.StepName);");
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([source]));

        IStepResult result = await harness.RunAsync(UnitHarness.Document(yaml));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(id, (string?)(JToken)result.Value!);
    }

    [Fact]
    public async Task Two_ids_that_sanitize_to_the_same_name_still_get_their_own_method()
    {
        (string a, CSharpStepSource first) = UnitHarness.Step("a-b", "return call.Ok(\"dash\");");
        (string b, CSharpStepSource second) = UnitHarness.Step("a_b", "return call.Ok(\"underscore\");", yamlLineOffset: CountLines(a));
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([first, second]));

        IStepResult result = await harness.RunAsync(UnitHarness.Document(a, b));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("underscore", (string?)(JToken)result.Value!);
        Assert.Contains("a-b", harness.Compilation.Unit!.StepIds);
        Assert.Contains("a_b", harness.Compilation.Unit.StepIds);
    }

    [Fact]
    public async Task A_denying_policy_stops_the_step_without_running_the_code()
    {
        const string variable = "CSHARP_UNIT_TEST_DENIED_RAN";
        Environment.SetEnvironmentVariable(variable, null);
        (string yaml, CSharpStepSource source) = UnitHarness.Step("denied", "Environment.SetEnvironmentVariable(\"" + variable + "\", \"yes\"); return call.Ok(1);");
        using UnitHarness harness = await UnitHarness.CreateAsync(
            CSharpUnitSources.Of([source]),
            configure: services => services.AddSingleton<ICSharpStepPolicy>(new DenyPolicy()));

        IStepResult result = await harness.RunAsync(UnitHarness.Document(yaml));

        Assert.True(result.HasError);
        Assert.Contains("not trusted", result.Error!.Message);
        Assert.Null(Environment.GetEnvironmentVariable(variable));
    }

    [Fact]
    public async Task Without_a_unit_the_step_says_so()
    {
        ServiceCollection services = new();
        services.AddCSharpExtension();
        await using ServiceProvider provider = services.BuildServiceProvider();
        CSharpStep step = new("lonely") { Code = "return call.Ok(1);" };

        IStepResult result = await ((IStep)step).ExecuteAsync(new JsonStepResult(step, new JObject()), new PipelineContext(null, provider), 1, null, CancellationToken.None);

        Assert.True(result.HasError);
        Assert.Contains("no compiled C# unit", result.Error!.Message);
    }

    [Fact]
    public async Task An_empty_source_is_an_empty_unit_not_a_compilation()
    {
        UnitCompilation compilation = await UnitHarness.CompileAsync(CSharpUnitSources.Of([]));

        Assert.True(compilation.Success);
        Assert.Empty(compilation.Unit!.StepIds);
    }

    [Fact]
    public async Task Cold_and_warm_compile_times_are_measured()
    {
        (_, CSharpStepSource source) = UnitHarness.Step("timed", "return call.Ok(" + Environment.TickCount64 + ");");
        CSharpUnitSources sources = CSharpUnitSources.Of([source], "public static class T { public static int One => 1; }");
        string cache = Path.Combine(Path.GetTempPath(), "csharp-cache-" + Guid.NewGuid().ToString("N"));
        CSharpUnitCompiler compiler = new(new CSharpCompileOptions { CacheDirectory = cache });
        try
        {
            UnitCompilation cold = await compiler.CompileAsync(sources);
            UnitCompilation memory = await compiler.CompileAsync(sources);
            CSharpUnitCompiler.ClearMemoryForTests();
            UnitCompilation disk = await compiler.CompileAsync(sources);

            output.WriteLine($"compile: {cold.Elapsed.TotalMilliseconds:F0} ms ({cold.Source}); memory: {memory.Elapsed.TotalMilliseconds:F0} ms ({memory.Source}); disk: {disk.Elapsed.TotalMilliseconds:F0} ms ({disk.Source})");
            Assert.Equal(UnitCacheSource.None, cold.Source);
            Assert.Equal(UnitCacheSource.Memory, memory.Source);
            Assert.Equal(UnitCacheSource.Disk, disk.Source);
            Assert.Equal(cold.Hash, disk.Hash);
            Assert.True(File.Exists(Path.Combine(cache, cold.Hash + ".dll")));
            cold.Unit!.Dispose();
            memory.Unit!.Dispose();
            disk.Unit!.Dispose();
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Another_source_is_another_hash_and_a_failed_compile_is_cached_too()
    {
        (_, CSharpStepSource a) = UnitHarness.Step("h", "return call.Ok(" + Environment.TickCount64 + ");");
        (_, CSharpStepSource b) = UnitHarness.Step("h", "return call.Ok(" + (Environment.TickCount64 + 1) + ");");
        (_, CSharpStepSource bad) = UnitHarness.Step("h", "return nothing;" + new string(' ', (int)(Environment.TickCount64 % 50)));

        UnitCompilation first = await UnitHarness.CompileAsync(CSharpUnitSources.Of([a]));
        UnitCompilation second = await UnitHarness.CompileAsync(CSharpUnitSources.Of([b]));
        UnitCompilation failed1 = await UnitHarness.CompileAsync(CSharpUnitSources.Of([bad]));
        UnitCompilation failed2 = await UnitHarness.CompileAsync(CSharpUnitSources.Of([bad]));

        Assert.NotEqual(first.Hash, second.Hash);
        Assert.False(failed1.Success);
        Assert.Equal(UnitCacheSource.Memory, failed2.Source);
        Assert.Equal(failed1.Diagnostics.Count, failed2.Diagnostics.Count);
        first.Unit!.Dispose();
        second.Unit!.Dispose();
    }

    [Fact]
    public async Task Disposing_a_unit_lets_its_load_context_be_collected()
    {
        (_, CSharpStepSource source) = UnitHarness.Step("gc", "return call.Ok(" + Environment.TickCount64 + ");");
        WeakReference reference = await LoadAndDispose(source);

        for (int i = 0; i < 20 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(20);
        }

        Assert.False(reference.IsAlive, "the collectible AssemblyLoadContext of a disposed unit should be unloaded");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadAndDispose(CSharpStepSource source)
    {
        UnitCompilation compilation = await UnitHarness.CompileAsync(CSharpUnitSources.Of([source]));
        WeakReference reference = compilation.Unit!.ContextReference;
        compilation.Unit.Dispose();
        return reference;
    }

    private static int CountLines(string text) => text.TrimEnd('\n').Split('\n').Length;

    [Fact]
    public async Task Check_gives_the_diagnostics_of_a_compilation_and_writes_or_caches_nothing()
    {
        string unique = "U" + Guid.NewGuid().ToString("N");
        CSharpUnitSources broken = CSharpUnitSources.Of([], $"public static class {unique}\n{{\n    public static int X() => nope;\n}}\n");
        CSharpUnitSources good = CSharpUnitSources.Of([], $"public static class {unique}Ok {{ public static int One => 1; }}");
        string cache = Path.Combine(Path.GetTempPath(), "csharp-check-" + Guid.NewGuid().ToString("N"));
        CSharpUnitCompiler compiler = new(new CSharpCompileOptions { CacheDirectory = cache });
        try
        {
            IReadOnlyList<CSharpDiagnostic> checkedBroken = await compiler.CheckAsync(broken);
            IReadOnlyList<CSharpDiagnostic> checkedGood = await compiler.CheckAsync(good);
            IReadOnlyList<CSharpDiagnostic> empty = await compiler.CheckAsync(CSharpUnitSources.Of([]));

            Assert.False(Directory.Exists(cache));
            UnitCompilation compiled = await compiler.CompileAsync(broken);
            Assert.Equal(compiled.Diagnostics, checkedBroken);
            CSharpDiagnostic error = Assert.Single(checkedBroken, d => d.IsError);
            Assert.Equal(("csharp/shared.cs", 3), (error.File, error.Line));
            Assert.DoesNotContain(checkedGood, d => d.IsError);
            Assert.Empty(empty);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    private sealed class DenyPolicy : ICSharpStepPolicy
    {
        public string? Deny(string stepName) => "the package is not trusted to run C# code in this run.";
    }
}
