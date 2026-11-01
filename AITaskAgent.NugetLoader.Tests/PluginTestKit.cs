using System.Text;
using WorkflowDesigner.Testing;

namespace AITaskAgent.NugetLoader.Tests;

/// <summary>Plugin code for the tests: compiled with Roslyn against the host's AITaskAgent.Abstractions/Core/Extensions.Llm.</summary>
internal static class PluginTestKit
{
    public const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using AITaskAgent.Core.Abstractions;
        using AITaskAgent.Core.Attributes;
        using AITaskAgent.Core.Hooks;
        using AITaskAgent.Core.Models;
        using AITaskAgent.Core.StepResults;
        using AITaskAgent.Core.Steps;
        using AITaskAgent.LLM.Models;
        using AITaskAgent.LLM.Tools.Abstractions;
        using AITaskAgent.Support.Template;
        using Microsoft.Extensions.Logging;
        using Newtonsoft.Json.Linq;
        """;

    /// <summary>The host's Extensions.Llm (ITool, IToolSource), beside the test assembly.</summary>
    public static string LlmDll => Path.Combine(AppContext.BaseDirectory, "AITaskAgent.Extensions.Llm.dll");

    /// <summary>A step class: <c>[StepTag(tag)]</c> when <paramref name="tag"/> is given.</summary>
    public static string Step(string className, string? tag = null, string baseClass = "StepBase") => $$"""
        {{(tag != null ? $"[StepTag(\"{tag}\")]" : "")}}
        public sealed class {{className}} : {{baseClass}}
        {
            public {{className}}() : base("{{className}}", typeof(IStepResult), typeof(JsonStepResult)) { }
            protected override Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? last, CancellationToken ct)
                => Task.FromResult<IStepResult>(new JsonStepResult(this, new JValue("{{className}}")));
        }
        """;

    /// <summary>A tool with a public parameterless constructor called <paramref name="toolName"/>.</summary>
    public static string Tool(string className, string toolName) => $$"""
        public sealed class {{className}} : ITool
        {
            public string Name => "{{toolName}}";
            public string Description => "A test tool.";
            public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
            public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken = default)
                => Task.FromResult(argumentsJson);
        }
        """;

    /// <summary>Everything a plugin can bring: two steps (one without tag), a tool, a tool with arguments and its source, hooks, a response type, templates and configuration.</summary>
    public static string FullPlugin(string tag, string toolName, string hookName) => $$"""
        {{Step("TaggedStep", tag)}}
        {{Step("PlainStep")}}
        {{Tool("EchoTool", toolName)}}
        public sealed class ArgsTool : ITool
        {
            private readonly string _folder;
            public ArgsTool(string folder) => _folder = folder;
            public string Name => "{{toolName}}.args";
            public string Description => _folder;
            public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
            public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken = default)
                => Task.FromResult(_folder);
        }
        public sealed class ArgsTools : IToolSource
        {
            public IEnumerable<ITool> Create(PluginContext context) => [new ArgsTool(context.Folder)];
        }
        public sealed class Templates : ITemplateSource
        {
            public ITemplateProvider? Create(PluginContext context) => null;
        }
        public sealed class Settings { public string? Suffix { get; set; } }
        public sealed class Configuration : IPluginConfiguration
        {
            public Type? OptionsType => typeof(Settings);
            public IReadOnlyList<string> RootSections => ["AcmeRoot"];
        }
        public static class Hooks
        {
            [Hook(Name = "{{hookName}}", Description = "Named hook.")]
            public static Task Done(FinalizeArgs args) => Task.CompletedTask;

            [Validator(Description = "Checks.")]
            public static Task<(bool IsValid, string? Error)> Check(ValidateArgs args) => Task.FromResult((true, (string?)null));

        }
        public sealed class Odd
        {
            [Hook]
            public void NotStatic() { }
        }
        [ResponseType("Finding")]
        public sealed class FindingDto { public string? Text { get; set; } }
        """;

    /// <summary>Compiles <paramref name="body"/> (namespace = <paramref name="assembly"/>) into <paramref name="path"/>.</summary>
    public static void Compile(string path, string assembly, string body, params string[] extra)
        => PluginTestFixture.CompileTo(path, assembly, Usings + "\nnamespace " + assembly + ";\n" + body, [LlmDll, .. extra]);

    /// <summary>A unique assembly name.</summary>
    public static string NewAssembly(string prefix = "Plugin") => prefix + Guid.NewGuid().ToString("N");

    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    public static bool IsLoaded(string assembly) => AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == assembly);
}
