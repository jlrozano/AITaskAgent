using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.CSharp.Tests;

/// <summary>Hooks, validators, response types and schema classes declared by a package.</summary>
public sealed class UnitDeclarationsTests
{
    private const string PersonSchema = """{ "type": "object", "properties": { "name": { "type": "string" }, "age": { "type": "integer" } }, "required": ["name"] }""";

    private const string Shared = """
        using System.ComponentModel;

        public static class Checks
        {
            [Validator(Description = "The value must not be empty")]
            public static Task<(bool IsValid, string? Error)> NotEmpty(ValidateArgs args)
                => Task.FromResult(((args.Result.Value?.ToString() ?? "").Length > 0, (string?)"the value is empty"));

            [ResultValidator]
            public static Task<(bool IsValid, string? Error)> HasTitle(ResultValidateArgs args)
                => Task.FromResult((args.As<JObject>()?["title"] != null, (string?)"the result has no title"));

            [Hook(Description = "Remembers which step finished")]
            public static Task Done(FinalizeArgs args)
            {
                args.Context.Metadata["finalized"] = args.Step.Name;
                return Task.CompletedTask;
            }

            public static int NotAHook() => 1;
        }

        [ResponseType("Finding")]
        public sealed class FindingDto
        {
            [Description("Short title")]
            public required string Title { get; init; }
            public int Severity { get; init; }
        }
        """;

    [Fact]
    public async Task Hooks_and_validators_of_the_unit_are_registered_with_the_package_as_source()
    {
        (_, CSharpStepSource step) = UnitHarness.Step("s", "return call.Ok(1);");
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([step], Shared), package: "my-package");

        IReadOnlyCollection<HookInfo> hooks = harness.Services.GetRequiredService<IHookRegistry>().List();

        Assert.Collection(hooks.OrderBy(h => h.Name, StringComparer.Ordinal),
            h => Assert.Equal(("Checks.Done", HookKind.Hook, "unit:my-package", "Remembers which step finished"), (h.Name, h.Kind, h.Source, h.Description)),
            h => Assert.Equal(("Checks.HasTitle", HookKind.ResultValidator, "unit:my-package"), (h.Name, h.Kind, h.Source)),
            h => Assert.Equal(("Checks.NotEmpty", HookKind.StepValidator, "unit:my-package"), (h.Name, h.Kind, h.Source)));
    }

    [Fact]
    public async Task A_hook_with_a_name_is_registered_under_that_name_and_the_attributes_are_the_shared_ones_of_abstractions()
    {
        const string named = """
            public static class Named
            {
                [Hook(Name = "Acme.Finalize")]
                public static Task Done(FinalizeArgs args) => Task.CompletedTask;
            }
            """;
        (_, CSharpStepSource step) = UnitHarness.Step("s", "return call.Ok(1);");
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([step], named), package: "named");

        HookInfo hook = Assert.Single(harness.Services.GetRequiredService<IHookRegistry>().List());

        Assert.Equal(("Acme.Finalize", HookKind.Hook, "unit:named"), (hook.Name, hook.Kind, hook.Source));
        Assert.Equal("AITaskAgent.Abstractions", typeof(HookAttribute).Assembly.GetName().Name);
    }

    [Fact]
    public async Task A_yaml_step_uses_a_validator_a_result_validator_and_a_hook_of_the_unit()
    {
        (string yaml, CSharpStepSource step) = UnitHarness.Step(
            "s", "return call.Ok(new { title = call.InputAs<JObject>()![\"t\"]!.Value<string>() });",
            extra: "    validators: [Checks.NotEmpty]\n    resultValidators: [Checks.HasTitle]\n    hooks:\n      onFinalize: Checks.Done\n");
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([step], Shared));
        PipelineContext context = new(null, harness.Services);

        IStepResult good = await harness.RunAsync(UnitHarness.Document(yaml), JObject.Parse("""{ "t": "hi" }"""), context);

        Assert.False(good.HasError, good.Error?.Message);
        Assert.Equal("s", context.Metadata["finalized"]);

        IStepResult bad = await harness.RunAsync(UnitHarness.Document(yaml), JObject.Parse("""{ "other": 1 }"""), new PipelineContext(null, harness.Services));

        Assert.True(bad.HasError);
    }

    [Fact]
    public async Task A_response_type_becomes_a_schema_the_yaml_can_name()
    {
        (string yaml, CSharpStepSource step) = UnitHarness.Step(
            "s", "return call.Ok(new { title = \"x\", severity = 2 });", extra: "    resultValidators: [\"schema:Finding\"]\n");
        (string bad, CSharpStepSource badStep) = UnitHarness.Step(
            "t", "return call.Ok(new { severity = 2 });", yamlLineOffset: 5, extra: "    resultValidators: [\"schema:Finding\"]\n");
        using UnitHarness harness = await UnitHarness.CreateAsync(CSharpUnitSources.Of([step, badStep], Shared));

        UnitResponseType type = Assert.Single(harness.Compilation.Unit!.ResponseTypes);
        Assert.Equal("Finding", type.Name);
        Assert.NotNull(type.Schema);
        Assert.Contains("title", type.Schema!.Properties.Keys);
        Assert.Contains("title", type.Schema.RequiredProperties);
        Assert.False(type.Schema.AllowAdditionalProperties);

        IStepResult valid = await harness.RunAsync(UnitHarness.Document(yaml));
        Assert.False(valid.HasError, valid.Error?.Message);

        IStepResult invalid = await harness.RunAsync(UnitHarness.Document(bad));
        Assert.True(invalid.HasError);
        Assert.Contains("title", invalid.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_schema_of_the_package_becomes_a_class_the_code_can_use()
    {
        (string yaml, CSharpStepSource step) = UnitHarness.Step(
            "s", "var p = new Person { Name = \"Ana\", Age = 3 }; return call.Ok(p);", extra: "    resultValidators: [\"schema:Person\"]\n");
        CSharpUnitSources sources = new([step], null, new Dictionary<string, string> { ["Person"] = PersonSchema });
        using UnitHarness harness = await UnitHarness.CreateAsync(sources, schemaFiles: sources.Schemas);

        IStepResult result = await harness.RunAsync(UnitHarness.Document(yaml));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("Ana", (string?)((JObject)result.Value!)["name"]);
        Assert.Equal(3, (int?)((JObject)result.Value!)["age"]);
    }

    [Fact]
    public async Task The_shared_file_can_use_the_generated_classes_and_hooks_can_take_them_from_the_result()
    {
        const string shared = """
            public static class Persons
            {
                public static Person Make(string name) => new() { Name = name };
            }
            """;
        (_, CSharpStepSource step) = UnitHarness.Step("s", "return call.Ok(Persons.Make(\"Eva\"));");
        CSharpUnitSources sources = new([step], shared, new Dictionary<string, string> { ["Person"] = PersonSchema });

        UnitCompilation compilation = await UnitHarness.CompileAsync(sources);

        Assert.True(compilation.Success, string.Join('\n', compilation.Diagnostics.Select(d => d.Message)));
        compilation.Unit!.Dispose();
    }

    [Fact]
    public async Task A_class_with_the_name_of_a_schema_is_a_compile_error()
    {
        (_, CSharpStepSource step) = UnitHarness.Step("s", "return call.Ok(1);");
        CSharpUnitSources sources = new([step], "public class Person { public string? Name { get; set; } }", new Dictionary<string, string> { ["Person"] = PersonSchema });

        UnitCompilation compilation = await UnitHarness.CompileAsync(sources);

        Assert.False(compilation.Success);
        CSharpDiagnostic error = Assert.Single(compilation.Diagnostics, d => d.Id == "WFCS002");
        Assert.Equal("csharp/shared.cs", error.File);
        Assert.Contains("Person", error.Message);
    }

    [Fact]
    public async Task A_response_type_with_the_name_of_a_schema_is_a_compile_error()
    {
        (_, CSharpStepSource step) = UnitHarness.Step("s", "return call.Ok(1);");
        CSharpUnitSources sources = new([step], "[ResponseType(\"person\")] public class PersonDto { public string? Name { get; set; } }", new Dictionary<string, string> { ["Person"] = PersonSchema });

        UnitCompilation compilation = await UnitHarness.CompileAsync(sources);

        Assert.False(compilation.Success);
        Assert.Single(compilation.Diagnostics, d => d.Id == "WFCS005");
    }

    [Fact]
    public async Task Two_response_types_with_the_same_name_are_an_error()
    {
        (_, CSharpStepSource step) = UnitHarness.Step("s", "return call.Ok(1);");
        const string shared = "[ResponseType(\"Same\")] public class A { public string? X { get; set; } }\n[ResponseType(\"Same\")] public class B { public string? Y { get; set; } }";

        UnitCompilation compilation = await UnitHarness.CompileAsync(CSharpUnitSources.Of([step], shared));

        Assert.Single(compilation.Diagnostics, d => d.Id == "WFCS006");
    }

    [Theory]
    [InlineData("[Validator] public static bool Wrong(ValidateArgs a) => true;")]
    [InlineData("[Validator] public static Task<(bool, string?)> Wrong(ResultValidateArgs a) => Task.FromResult((true, (string?)null));")]
    [InlineData("[ResultValidator] public static Task<(bool, string?)> Wrong() => Task.FromResult((true, (string?)null));")]
    [InlineData("[Hook] public void Wrong() { }")]
    [InlineData("[Hook] static void Wrong() { }")]
    public async Task A_hook_with_the_wrong_shape_is_reported_where_it_is_declared(string method)
    {
        (_, CSharpStepSource step) = UnitHarness.Step("s", "return call.Ok(1);");

        UnitCompilation compilation = await UnitHarness.CompileAsync(CSharpUnitSources.Of([step], "public class C\n{\n    " + method + "\n}\n"));

        CSharpDiagnostic error = Assert.Single(compilation.Diagnostics, d => d.Id == "WFCS001");
        Assert.Equal("csharp/shared.cs", error.File);
        Assert.Equal(3, error.Line);
    }

    [Fact]
    public async Task A_unit_name_that_the_host_also_registers_is_an_error_and_the_hosts_own_hooks_keep_working()
    {
        (_, CSharpStepSource step) = UnitHarness.Step("s", "return call.Ok(1);");
        UnitCompilation compilation = await UnitHarness.CompileAsync(CSharpUnitSources.Of([step], Shared));
        Task<(bool, string?)> Host(ValidateArgs a) => Task.FromResult((true, (string?)null));

        ServiceCollection fine = new();
        fine.AddSingleton<IHookRegistry>(new HookRegistry().Add("Host.Check", HookKind.StepValidator, (Func<ValidateArgs, Task<(bool, string?)>>)Host));
        fine.AddCSharpUnit(compilation.Unit!, "p");
        using ServiceProvider provider = fine.BuildServiceProvider();
        IHookRegistry registry = provider.GetRequiredService<IHookRegistry>();
        Assert.True(registry.TryGet("Host.Check", HookKind.StepValidator, typeof(Func<ValidateArgs, Task<(bool, string?)>>), out _));
        Assert.True(registry.TryGet("Checks.NotEmpty", HookKind.StepValidator, typeof(Func<ValidateArgs, Task<(bool, string?)>>), out _));
        Assert.Equal(4, registry.List().Count);

        UnitCompilation second = await UnitHarness.CompileAsync(CSharpUnitSources.Of([step], Shared));
        ServiceCollection clash = new();
        clash.AddSingleton<IHookRegistry>(new HookRegistry().Add("Checks.NotEmpty", HookKind.StepValidator, (Func<ValidateArgs, Task<(bool, string?)>>)Host));
        clash.AddCSharpUnit(second.Unit!, "p");
        using ServiceProvider broken = clash.BuildServiceProvider();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => broken.GetRequiredService<IHookRegistry>());
        Assert.Contains("defined more than once", error.Message);
    }
}
