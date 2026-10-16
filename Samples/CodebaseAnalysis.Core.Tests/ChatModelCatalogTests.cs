using AITaskAgent.Configuration;
using AITaskAgent.LLM.Configuration;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>El catálogo de modelos seleccionables del chat: sale de <c>CodebaseAnalysis:Chat:Models</c> y de los roles ya construidos.</summary>
public sealed class ChatModelCatalogTests
{
    private static LlmRole Role(string model, bool streaming = true, LlmProvider? provider = null) => new()
    {
        Provider = provider ?? new LlmProvider
        {
            Service = new ScriptedStreamingLlmService(),
            BaseUrl = "http://fake.invalid/",
            Models = [new LlmModelSettings("m-priced", 2m, 10m, new LlmInferenceSettings())]
        },
        Model = model,
        SystemPrompt = "chat_system",
        Inference = new LlmInferenceSettings { UseStreaming = streaming }
    };

    private static Dictionary<string, LlmRole> Roles(params (string Name, LlmRole Role)[] roles) =>
        roles.ToDictionary(r => r.Name, r => r.Role, StringComparer.OrdinalIgnoreCase);

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) => TestConfig.FromPairs(pairs);

    [Fact]
    public void Without_a_models_list_the_chat_role_is_the_only_option()
    {
        var warnings = new List<string>();
        var catalog = ChatModelCatalog.Build(Config(), Roles(("ChatAssistant", Role("m-priced"))), warnings.Add);

        var only = Assert.Single(catalog.Models);
        Assert.Equal("m-priced", only.Id);
        Assert.Equal("m-priced", only.Label);
        Assert.Same(only, catalog.Default);
        Assert.Equal(2m, only.InputPricePerMillion);
        Assert.Equal(10m, only.OutputPricePerMillion);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Models_come_from_the_configured_list_with_label_streaming_flag_and_default()
    {
        var warnings = new List<string>();
        var roles = Roles(("ChatAssistant", Role("m-priced")), ("Chat.slow", Role("m-slow")));
        var catalog = ChatModelCatalog.Build(Config(
            ("CodebaseAnalysis:Chat:Models:0:Id", "fast"), ("CodebaseAnalysis:Chat:Models:0:Label", "Rápido"), ("CodebaseAnalysis:Chat:Models:0:Role", "ChatAssistant"),
            ("CodebaseAnalysis:Chat:Models:1:Id", "slow"), ("CodebaseAnalysis:Chat:Models:1:Role", "Chat.slow"), ("CodebaseAnalysis:Chat:Models:1:Streaming", "false"),
            ("CodebaseAnalysis:Chat:DefaultModel", "slow")), roles, warnings.Add);

        Assert.Equal(["fast", "slow"], catalog.Models.Select(m => m.Id));
        Assert.Equal("Rápido", catalog.Models[0].Label);
        Assert.Equal("slow", catalog.Models[1].Label);
        Assert.True(catalog.Models[0].Streaming);
        Assert.False(catalog.Models[1].Streaming);
        Assert.Equal("slow", catalog.Default.Id);
        Assert.Same(roles["Chat.slow"], catalog.Default.Role);
        Assert.Equal("m-slow", catalog.Models[1].Model);
        // m-slow no tiene precio en el proveedor: avisa, y el precio queda a null.
        Assert.Null(catalog.Models[1].InputPricePerMillion);
        Assert.Contains(warnings, w => w.Contains("m-slow") && w.Contains("Models"));
    }

    [Fact]
    public void Entries_with_a_missing_role_or_a_repeated_id_are_skipped_with_a_warning()
    {
        var warnings = new List<string>();
        var catalog = ChatModelCatalog.Build(Config(
            ("CodebaseAnalysis:Chat:Models:0:Id", "a"), ("CodebaseAnalysis:Chat:Models:0:Role", "ChatAssistant"),
            ("CodebaseAnalysis:Chat:Models:1:Id", "b"), ("CodebaseAnalysis:Chat:Models:1:Role", "NoExiste"),
            ("CodebaseAnalysis:Chat:Models:2:Id", "a"), ("CodebaseAnalysis:Chat:Models:2:Role", "ChatAssistant")),
            Roles(("ChatAssistant", Role("m-priced"))), warnings.Add);

        Assert.Equal(["a"], catalog.Models.Select(m => m.Id));
        Assert.Contains(warnings, w => w.Contains("NoExiste"));
        Assert.Contains(warnings, w => w.Contains("'a'") && w.Contains("repetido"));
    }

    [Fact]
    public void An_unknown_default_falls_back_to_the_chat_role_with_a_warning()
    {
        var warnings = new List<string>();
        var catalog = ChatModelCatalog.Build(Config(
            ("CodebaseAnalysis:Chat:Models:0:Id", "x"), ("CodebaseAnalysis:Chat:Models:0:Role", "Chat.x"),
            ("CodebaseAnalysis:Chat:Models:1:Id", "y"), ("CodebaseAnalysis:Chat:Models:1:Role", "ChatAssistant"),
            ("CodebaseAnalysis:Chat:DefaultModel", "retirado")),
            Roles(("ChatAssistant", Role("m-priced")), ("Chat.x", Role("m-priced"))), warnings.Add);

        Assert.Equal("y", catalog.Default.Id);
        Assert.Contains(warnings, w => w.Contains("retirado"));
    }

    [Fact]
    public void A_list_with_no_valid_entry_falls_back_to_the_chat_role()
    {
        var warnings = new List<string>();
        var catalog = ChatModelCatalog.Build(Config(
            ("CodebaseAnalysis:Chat:Models:0:Id", "b"), ("CodebaseAnalysis:Chat:Models:0:Role", "NoExiste")),
            Roles(("ChatAssistant", Role("m-priced"))), warnings.Add);

        Assert.Equal("m-priced", Assert.Single(catalog.Models).Id);
    }

    [Fact]
    public void Streaming_true_on_a_role_without_UseStreaming_is_flagged()
    {
        var warnings = new List<string>();
        ChatModelCatalog.Build(Config(
            ("CodebaseAnalysis:Chat:Models:0:Id", "a"), ("CodebaseAnalysis:Chat:Models:0:Role", "ChatAssistant"), ("CodebaseAnalysis:Chat:Models:0:Streaming", "true")),
            Roles(("ChatAssistant", Role("m-priced", streaming: false))), warnings.Add);

        Assert.Contains(warnings, w => w.Contains("UseStreaming"));
    }

    [Fact]
    public void Resolve_returns_the_default_and_warns_for_unknown_ids_but_not_for_missing_ones()
    {
        var catalog = ChatModelCatalog.Build(Config(
            ("CodebaseAnalysis:Chat:Models:0:Id", "a"), ("CodebaseAnalysis:Chat:Models:0:Role", "ChatAssistant"),
            ("CodebaseAnalysis:Chat:Models:1:Id", "b"), ("CodebaseAnalysis:Chat:Models:1:Role", "Chat.b")),
            Roles(("ChatAssistant", Role("m-priced")), ("Chat.b", Role("m-priced"))), _ => { });
        var warnings = new List<string>();

        Assert.Equal("b", catalog.Resolve("B", warnings.Add).Id);
        Assert.Equal("a", catalog.Resolve(null, warnings.Add).Id);
        Assert.Empty(warnings);
        Assert.Equal("a", catalog.Resolve("retirado", warnings.Add).Id);
        Assert.Contains("retirado", Assert.Single(warnings));
    }

    /// <summary>
    /// La configuración del sample: once modelos, sol por defecto, cada uno con sus <c>Options</c> y su precio (salvo
    /// gpt-oss-120b, que no tiene tarifa y solo genera ese aviso de arranque).
    /// </summary>
    [Fact]
    public void The_sample_configuration_offers_eleven_models_with_their_own_options_and_prices()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.analysis.json")).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddCodebaseAnalysisLlmServices();
        services.AddAITaskAgent();
        using var provider = services.BuildServiceProvider();
        var warnings = new List<string>();
        var roles = LlmRoleFactory.BuildRoles(provider, _ => null, _ => { }, [.. LlmRoleFactory.RequiredRoles, ChatAgent.RoleName]);

        var catalog = ChatModelCatalog.Build(configuration, roles, warnings.Add);

        Assert.Contains("gpt-oss-120b", Assert.Single(warnings));
        Assert.Equal(["gpt-6-sol", "gpt-6-luna", "gpt-5.6-terra", "gpt-5.4", "gpt-6-sol-global", "gpt-4.1-mini", "gpt-4.1-nano",
            "gpt-5-mini", "gpt-5-nano", "o4-mini", "gpt-oss-120b"], catalog.Models.Select(m => m.Id));
        Assert.Equal("gpt-6-sol", catalog.Default.Id);
        Assert.All(catalog.Models, m => Assert.Equal(m.Id, m.Model));
        Assert.All(catalog.Models.Where(m => m.Id != "gpt-oss-120b"), m => Assert.NotNull(m.InputPricePerMillion));
        Assert.Null(catalog.Find("gpt-oss-120b")!.InputPricePerMillion);
        Assert.Equal((0.1m, 0.5m), (catalog.Models[1].InputPricePerMillion!.Value, catalog.Models[1].OutputPricePerMillion!.Value));
        // Los deployments gpt-6-* y gpt-5.6-terra solo aceptan tools por chat/completions con reasoning_effort none.
        foreach (var id in new[] { "gpt-6-sol", "gpt-6-luna", "gpt-5.6-terra", "gpt-6-sol-global" })
            Assert.Equal("none", (string?)catalog.Find(id)!.Role.Inference!.Options["reasoning_effort"]);
        // Medido contra el recurso: gpt-4.1-*, gpt-5.4, gpt-5-* y gpt-oss no aceptan reasoning_effort none o no lo llevan;
        // o4-mini no admite none, así que ninguno de ellos lo declara.
        foreach (var id in new[] { "gpt-5.4", "gpt-4.1-mini", "gpt-4.1-nano", "gpt-5-mini", "gpt-5-nano", "o4-mini", "gpt-oss-120b" })
            Assert.Null(catalog.Find(id)!.Role.Inference!.Options["reasoning_effort"]);
        // gpt-5-mini, gpt-5-nano y o4-mini rechazan una temperatura distinta de la por defecto: sus roles no la llevan.
        foreach (var id in new[] { "gpt-5-mini", "gpt-5-nano", "o4-mini" })
            Assert.Null(catalog.Find(id)!.Role.Inference!.Temperature);
        foreach (var id in new[] { "gpt-6-sol", "gpt-4.1-nano", "gpt-oss-120b" })
            Assert.NotNull(catalog.Find(id)!.Role.Inference!.Temperature);
        // Medido (2026-10-02): van goteando gpt-4.1-mini, gpt-4.1-nano, o4-mini y gpt-oss-120b; el resto suelta la respuesta de golpe.
        Assert.Equal(["gpt-4.1-mini", "gpt-4.1-nano", "o4-mini", "gpt-oss-120b"], catalog.Models.Where(m => m.Streaming).Select(m => m.Id));
        Assert.All(catalog.Models, m => Assert.True(m.Role.Inference!.UseStreaming));
    }
}
