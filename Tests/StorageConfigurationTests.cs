using AITaskAgent.Configuration;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Conversation.Storage;
using AITaskAgent.YAML.Execution;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Tests;

/// <summary>
/// <c>AITaskAgent:Storage</c> chooses the context and conversation storages that
/// <c>AddAITaskAgent()</c> registers: memory by default, files on request, clear errors for a wrong
/// type or a missing folder, and a storage the host registers itself always wins.
/// </summary>
public sealed class StorageConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aitask-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        foreach (string path in _cleanup.Append(_directory))
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null)
        => HostKit.Build(new RecordingLlmService(), settings, configure);

    [Fact]
    public async Task Without_the_section_both_storages_stay_in_memory()
    {
        await using var provider = Build([]);

        Assert.IsType<InMemoryPipelineContextStorage>(provider.GetRequiredService<IPipelineContextStorage>());
        Assert.IsType<InMemoryConversationStorage>(provider.GetRequiredService<IConversationStorage>());
    }

    [Theory]
    [InlineData("file", typeof(FileConversationStorage))]
    [InlineData("YAML", typeof(YamlFileConversationStorage))]
    [InlineData("Memory", typeof(InMemoryConversationStorage))]
    public async Task The_section_chooses_the_storages(string conversationType, Type expected)
    {
        await using var provider = Build(new()
        {
            ["AITaskAgent:Storage:Context:Type"] = "File",
            ["AITaskAgent:Storage:Context:Directory"] = Path.Combine(_directory, "contexts"),
            ["AITaskAgent:Storage:Conversation:Type"] = conversationType,
            ["AITaskAgent:Storage:Conversation:Directory"] = Path.Combine(_directory, "conversations")
        });

        var contexts = Assert.IsType<FilePipelineContextStorage>(provider.GetRequiredService<IPipelineContextStorage>());
        Assert.Equal(Path.Combine(_directory, "contexts"), contexts.Directory);
        Assert.IsType(expected, provider.GetRequiredService<IConversationStorage>());
    }

    [Fact]
    public async Task Relative_folders_resolve_against_the_current_directory_and_variables_are_expanded()
    {
        string relative = $"aitask-ctx-{Guid.NewGuid():N}";
        _cleanup.Add(Path.GetFullPath(relative));
        string variable = $"AITASK_TEST_DIR_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, _directory);
        try
        {
            await using var provider = Build(new()
            {
                ["AITaskAgent:Storage:Context:Type"] = "file",
                ["AITaskAgent:Storage:Context:Directory"] = relative,
                ["AITaskAgent:Storage:Conversation:Type"] = "file",
                ["AITaskAgent:Storage:Conversation:Directory"] = "${" + variable + "}/conversations"
            });

            var contexts = Assert.IsType<FilePipelineContextStorage>(provider.GetRequiredService<IPipelineContextStorage>());
            Assert.Equal(Path.GetFullPath(relative), contexts.Directory);

            var conversations = provider.GetRequiredService<IConversationStorage>();
            await conversations.SaveConversationAsync(new ConversationContext { ConversationId = "c1" });
            Assert.True(File.Exists(Path.Combine(_directory, "conversations", "c1.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void An_unknown_type_names_the_key_and_the_valid_values()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["AITaskAgent:Storage:Context:Type"] = "redis"
        }));

        Assert.Contains("AITaskAgent:Storage:Context:Type", ex.Message);
        Assert.Contains("redis", ex.Message);
        Assert.Contains("memory, file", ex.Message);
    }

    [Fact]
    public void Yaml_is_only_valid_for_conversations()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["AITaskAgent:Storage:Conversation:Type"] = "sql"
        }));

        Assert.Contains("AITaskAgent:Storage:Conversation:Type", ex.Message);
        Assert.Contains("memory, file, yaml", ex.Message);

        Assert.Throws<InvalidOperationException>(() => Build(new() { ["AITaskAgent:Storage:Context:Type"] = "yaml" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("${AITASK_TEST_UNDEFINED_VARIABLE}/x")]
    public void A_file_storage_without_a_usable_folder_is_an_error(string? directory)
    {
        Dictionary<string, string?> settings = new() { ["AITaskAgent:Storage:Context:Type"] = "file" };
        if (directory != null)
        {
            settings["AITaskAgent:Storage:Context:Directory"] = directory;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("AITaskAgent:Storage:Context:Directory", ex.Message);
    }

    [Fact]
    public async Task A_storage_the_host_registers_wins_before_or_after_AddAITaskAgent()
    {
        InMemoryPipelineContextStorage mine = new();
        InMemoryConversationStorage myConversations = new();

        // After: the last registration is the one resolved. The invalid section is never read.
        await using (var after = Build(
            new() { ["AITaskAgent:Storage:Context:Type"] = "redis", ["AITaskAgent:Storage:Conversation:Type"] = "redis" },
            services =>
            {
                services.AddSingleton<IPipelineContextStorage>(mine);
                services.AddSingleton<IConversationStorage>(myConversations);
            }))
        {
            Assert.Same(mine, after.GetRequiredService<IPipelineContextStorage>());
            Assert.Same(myConversations, after.GetRequiredService<IConversationStorage>());
        }

        // Before: TryAdd keeps it.
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddSingleton<IPipelineContextStorage>(mine);
        services.AddSingleton<IConversationStorage>(myConversations);
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        services.AddAITaskAgent();
        await using var before = services.BuildServiceProvider();

        Assert.Same(mine, before.GetRequiredService<IPipelineContextStorage>());
        Assert.Same(myConversations, before.GetRequiredService<IConversationStorage>());
    }

    [Fact]
    public async Task A_context_built_beforehand_with_metadata_and_a_stored_conversation_reaches_the_llm_steps()
    {
        RecordingLlmService llm = new(_ => "noted");
        await using var provider = HostKit.Build(llm, new()
        {
            ["AITaskAgent:Storage:Conversation:Type"] = "file",
            ["AITaskAgent:Storage:Conversation:Directory"] = _directory
        });

        // A previous session left this conversation in the configured storage.
        ConversationContext previous = new() { ConversationId = "session-1" };
        previous.AddUserMessage("My name is Ana").AddAssistantMessage("Hello Ana");
        await provider.GetRequiredService<IConversationStorage>().SaveConversationAsync(previous);

        LlmPipelineContext context = await LlmPipelineContextFactory.CreateForConversationAsync(provider, "session-1");
        context.Metadata["customer"] = "ACME";

        YamlPipeline pipeline = provider.GetRequiredService<YamlPipelineFactory>().CreateFromYaml("""
            name: continue
            steps:
              - { id: reply, type: LlmStep, userPrompt: "Customer {{context.customer}}: what is my name?" }
            """);

        var result = await pipeline.ExecuteAsync(new JObject(), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(
            ["user:My name is Ana", "assistant:Hello Ana", "user:Customer ACME: what is my name?"],
            Assert.Single(llm.Conversations));
        Assert.Equal("session-1", context.Conversation.ConversationId);
        Assert.Equal(4, context.Conversation.History.Messages.Count);
    }

    [Fact]
    public async Task An_unknown_conversation_id_starts_an_empty_conversation_with_that_id()
    {
        await using var provider = Build([]);

        LlmPipelineContext context = await LlmPipelineContextFactory.CreateForConversationAsync(provider, "new-session");

        Assert.Equal("new-session", context.Conversation.ConversationId);
        Assert.Empty(context.Conversation.History.Messages);
        Assert.Same(provider.GetRequiredService<PipelineContextFactory>().GetType(), typeof(LlmPipelineContextFactory));
    }
}
