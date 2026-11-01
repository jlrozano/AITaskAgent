using AITaskAgent.CSharp;

namespace AITaskAgent.CSharp.Tests;

/// <summary>The completion of the editor: the symbols the unit binds at the caret of a step body or of the shared file, without emitting anything.</summary>
public sealed class CSharpCompletionTests
{
    private const string Shared = """
        public static class Strings
        {
            public static string Title(string text) => text;
            public static string Title(string text, bool all) => text;
        }

        public static class Checks
        {
            [Validator(Description = "Not empty")]
            public static Task<(bool IsValid, string? Error)> NotEmpty(ValidateArgs args)
            {
                var value = args.
                return Task.FromResult<(bool IsValid, string? Error)>((true, null));
            }
        }
        """;

    private const string PersonSchema = """{ "type": "object", "properties": { "name": { "type": "string" }, "age": { "type": "integer" } } }""";

    private static readonly CSharpUnitSources Context = new([], Shared, new Dictionary<string, string> { ["Person"] = PersonSchema });

    /// <summary>Completes <paramref name="text"/> at the <c>|</c> it contains.</summary>
    private static Task<CSharpCompletionList> CompleteAsync(string text, string? stepId = "step1", CSharpUnitSources? context = null, CSharpCompileOptions? options = null)
    {
        int caret = text.IndexOf('|', StringComparison.Ordinal);
        return new CSharpUnitCompiler(options).CompleteAsync(context ?? Context, new CSharpCompletionRequest(text.Remove(caret, 1), caret, stepId));
    }

    private static CSharpCompletionItem Item(CSharpCompletionList list, string label)
        => Assert.Single(list.Items, i => i.Label == label);

    [Fact]
    public async Task After_call_dot_a_step_body_lists_the_members_of_StepCall()
    {
        CSharpCompletionList list = await CompleteAsync("var x = 1;\ncall.|\nreturn call.Ok();");

        foreach (string member in new[] { "Input", "Context", "InputAs", "Ok", "Fail", "Set", "Get", "Log", "Attempt", "StepName" })
        {
            Assert.Contains(list.Items, i => i.Label == member);
        }

        Assert.Equal("method", Item(list, "Ok").Kind);
        Assert.Contains("(+1 overload)", Item(list, "Ok").Detail);
        Assert.Equal("property", Item(list, "Input").Kind);
        Assert.Equal(1, Item(list, "Input").Boost);
        Assert.DoesNotContain(list.Items, i => i.Label == ".ctor" || i.Label == "StepCall");
    }

    [Fact]
    public async Task The_identifier_being_typed_is_the_range_and_filters_by_prefix()
    {
        CSharpCompletionList list = await CompleteAsync("return call.Inp|");

        Assert.Equal("return call.".Length, list.From);
        Assert.Equal("return call.Inp".Length, list.To);
        Assert.Equal(["Input", "InputAs"], list.Items.Select(i => i.Label));
    }

    [Fact]
    public async Task A_class_of_the_shared_file_is_offered_in_a_step_body_by_prefix()
    {
        CSharpCompletionList list = await CompleteAsync("var t = Str|\nreturn call.Ok(t);");

        CSharpCompletionItem strings = Item(list, "Strings");
        Assert.Equal("class", strings.Kind);
        Assert.Equal(0, strings.Boost);
        Assert.Contains(list.Items, i => i.Label == "String");
        Assert.DoesNotContain(list.Items, i => i.Label == "WorkflowNamespaceAnchor");
    }

    [Fact]
    public async Task After_a_type_dot_come_its_static_members_with_overloads_grouped()
    {
        CSharpCompletionList list = await CompleteAsync("var t = Strings.|\nreturn call.Ok(t);");

        CSharpCompletionItem title = Item(list, "Title");
        Assert.Equal("method", title.Kind);
        Assert.Contains("(+1 overload)", title.Detail);
        Assert.DoesNotContain(list.Items, i => i.Label == "ToString");
    }

    [Fact]
    public async Task A_member_of_a_schema_class_is_offered_after_a_dot()
    {
        CSharpCompletionList list = await CompleteAsync("var p = call.InputAs<Person>()!;\nvar n = p.|\nreturn call.Ok(n);");

        Assert.Contains(list.Items, i => i.Label is "Name" or "name");
        Assert.Contains(list.Items, i => i.Label is "Age" or "age");
    }

    [Fact]
    public async Task A_local_variable_has_boost_3_and_a_namespace_minus_1()
    {
        CSharpCompletionList list = await CompleteAsync("var counter = 1;\nvar s = co|\nreturn call.Ok(s);");
        CSharpCompletionList namespaces = await CompleteAsync("var s = Sys|\nreturn call.Ok(s);");

        CSharpCompletionItem counter = Item(list, "counter");
        Assert.Equal("variable", counter.Kind);
        Assert.Equal(3, counter.Boost);
        Assert.Equal("counter", list.Items[0].Label);
        Assert.Equal(-1, Item(namespaces, "System").Boost);
        Assert.Equal("namespace", Item(namespaces, "System").Kind);
    }

    [Fact]
    public async Task With_nothing_typed_only_locals_parameters_and_members_of_the_type_come()
    {
        CSharpCompletionList list = await CompleteAsync("var counter = 1;\n|\nreturn call.Ok(counter);");

        Assert.Contains(list.Items, i => i.Label == "counter" && i.Boost == 3);
        Assert.Contains(list.Items, i => i.Label == "call" && i.Boost == 3);
        Assert.All(list.Items, i => Assert.True(i.Boost >= 2, i.Label));
        Assert.DoesNotContain(list.Items, i => i.Label == "System" || i.Label == "Strings");
    }

    [Fact]
    public async Task Args_dot_in_a_validator_of_the_shared_file_lists_ValidateArgs()
    {
        int caret = Shared.IndexOf("args.", StringComparison.Ordinal) + "args.".Length;

        CSharpCompletionList list = await new CSharpUnitCompiler().CompleteAsync(Context, new CSharpCompletionRequest(Shared, caret));

        foreach (string member in new[] { "Step", "Result", "Context", "CancellationToken", "As" })
        {
            Assert.Contains(list.Items, i => i.Label == member);
        }
    }

    [Fact]
    public async Task Broken_code_mid_typing_still_completes()
    {
        CSharpCompletionList list = await CompleteAsync("if (call.Attempt > 1 {\nvar r = call.Fa|\nreturn call.Ok(;");

        Assert.Equal("Fail", Assert.Single(list.Items).Label);
    }

    [Fact]
    public async Task A_caret_on_a_trailing_empty_line_works()
    {
        CSharpCompletionList list = await CompleteAsync("var counter = 1;\nreturn call.Ok(counter);\n\nco|");

        Assert.Contains(list.Items, i => i.Label == "counter");
    }

    [Fact]
    public async Task Crlf_text_works_and_the_range_is_in_the_requests_offsets()
    {
        const string text = "var counter = 1;\r\nvar s = call.Inp|\r\nreturn call.Ok(s);";

        CSharpCompletionList list = await CompleteAsync(text);

        int caret = text.IndexOf('|', StringComparison.Ordinal);
        Assert.Equal(caret, list.To);
        Assert.Equal(caret - 3, list.From);
        Assert.Contains(list.Items, i => i.Label == "Input");
    }

    [Fact]
    public async Task An_empty_body_completes_the_parameter()
    {
        CSharpCompletionList list = await CompleteAsync("|");

        Assert.Contains(list.Items, i => i.Label == "call");
    }

    [Fact]
    public async Task Nothing_is_emitted_or_cached_on_disk()
    {
        string cache = Path.Combine(Path.GetTempPath(), "csu-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            CSharpCompletionList list = await CompleteAsync("return call.|", options: new CSharpCompileOptions { CacheDirectory = cache });

            Assert.NotEmpty(list.Items);
            Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task A_caret_outside_the_text_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new CSharpUnitCompiler().CompleteAsync(Context, new CSharpCompletionRequest("abc", 4, "s")));
    }

    private const string HookShared = """
        public static class Hooks
        {
            [Hook]
            public static Task Probe(PostProcessArgs args)
            {
                var a = args.|;
                return Task.CompletedTask;
            }
        }
        """;

    /// <summary>Completes the shared file <paramref name="shared"/> at its <c>|</c>.</summary>
    private static Task<CSharpCompletionList> CompleteSharedAsync(string shared)
    {
        int caret = shared.IndexOf('|', StringComparison.Ordinal);
        string text = shared.Remove(caret, 1);
        return new CSharpUnitCompiler().CompleteAsync(new CSharpUnitSources([], text, new Dictionary<string, string>()), new CSharpCompletionRequest(text, caret));
    }

    [Fact]
    public async Task After_args_dot_in_a_hook_the_members_of_its_arguments_come_with_their_container_and_docs()
    {
        CSharpCompletionList list = await CompleteSharedAsync(HookShared);

        foreach (string member in new[] { "Step", "Input", "Result", "Context", "Attempt", "CancellationToken", "IsSuspended" })
        {
            CSharpCompletionItem item = Item(list, member);
            Assert.Equal("AITaskAgent.Core.Hooks.PostProcessArgs", item.Container);
        }

        // The framework ships its XML documentation: the summary is the item's documentation.
        Assert.Contains("suspension", Item(list, "IsSuspended").Documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("property", Item(list, "Result").Kind);
    }

    [Theory]
    [InlineData("args.Result.|", "Value", "AITaskAgent.Core.Abstractions.IStepResult")]
    [InlineData("args.Context.|", "Metadata", "AITaskAgent.Core.Models.PipelineContext")]
    [InlineData("args.Step.|", "Name", null)]
    [InlineData("args.Result.Value?.|", "ToString", "System.Object")]
    public async Task Nested_members_complete_after_each_dot(string expression, string expected, string? container)
    {
        CSharpCompletionList list = await CompleteSharedAsync(HookShared.Replace("args.|", expression, StringComparison.Ordinal));

        CSharpCompletionItem item = Item(list, expected);
        if (container != null)
        {
            Assert.Equal(container, item.Container);
        }
    }
}
