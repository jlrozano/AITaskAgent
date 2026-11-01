using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Streaming;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Support;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NJsonSchema;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace AITaskAgent.YAML.Steps;

/// <summary>
/// Configurable LLM step, the one a YAML pipeline declares with <c>type: LlmStep</c>.
/// Inherits all execution logic (retry, tools, telemetry, streaming, provider fallback) from
/// <see cref="BaseLlmStep"/>; it only wires the declared prompts, role and JSON schemas.
/// </summary>
/// <remarks>
/// <para>
/// The step registry creates it through DI and then populates its configuration from JSON, so it
/// is built before the role it should use is known. It starts on <see cref="UnresolvedRole"/> (a
/// <see cref="NullLlmService"/> that throws if ever invoked) and resolves the real role from
/// <see cref="LlmEnvironment"/> as soon as its configuration has been populated (the
/// <c>[OnDeserialized]</c> callback): <see cref="ProfileName"/>, or the environment's
/// <see cref="LlmEnvironment.DefaultRole"/>. An unknown role is an error at that moment, so a YAML
/// pipeline that names a missing role fails while it is being built, not when it runs.
/// </para>
/// <para>
/// Without an output schema the result is an <see cref="LlmStringStepResult"/>. With one
/// (<see cref="OutputJsonSchema"/>, assigned from <c>outputSchema</c>) the result is an
/// <see cref="LlmJsonStepResult"/>, the schema goes to the provider as response format (or into the
/// system prompt) through <see cref="Schema"/>, and the response is validated against it; an
/// invalid response is retried with the validation errors as feedback.
/// </para>
/// </remarks>
[StepTag("LlmStep")]
[Description("LLM call with declared prompts and role; optional JSON output schema.")]
public class YamlLlmStep : BaseLlmStep, IJsonSchemaStep
{
    private readonly ITemplateProvider? _templateProvider;
    private readonly IOptions<LlmEnvironment>? _llmEnvironment;
    private readonly IServiceProvider? _services;
    private JsonSchema? _outputJsonSchema;

    public YamlLlmStep(ITemplateProvider? templateProvider = null, IOptions<LlmEnvironment>? llmEnvironment = null)
        : this(services: null, templateProvider, llmEnvironment)
    {
    }

    /// <summary>
    /// Constructor the step registry uses: <paramref name="services"/> is the provider the pipeline is
    /// built with, where the names in <see cref="ToolNames"/> are looked up (<see cref="ResolveTools"/>).
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public YamlLlmStep(IServiceProvider? services, ITemplateProvider? templateProvider = null, IOptions<LlmEnvironment>? llmEnvironment = null)
        : base(
            $"YamlLlmStep-{Guid.NewGuid()}",
            UnresolvedRole,
            typeof(IStepResult),
            typeof(LlmStringStepResult),
            prompt: string.Empty)
    {
        _templateProvider = templateProvider;
        _llmEnvironment = llmEnvironment;
        _services = services;
    }

    /// <summary>
    /// Placeholder role used until the real one is resolved. Its service throws on any call, so a
    /// step that reaches execution without a resolved role fails with a clear message instead of
    /// silently talking to the wrong provider.
    /// </summary>
    private static LlmRole UnresolvedRole => new()
    {
        Provider = new LlmProvider { Service = new NullLlmService() },
        Model = string.Empty
    };

    /// <summary>Name of the LLM role. Optional; falls back to the environment's default role.</summary>
    [Description("LLM role name; defaults to the configured default role.")]
    public string? ProfileName { get; set; }

    /// <summary>Prompt sent as user message. Template name or inline template text.</summary>
    [Description("User message: template name or inline template over input and context.")]
    public string? UserPrompt { get; set; }

    /// <summary>
    /// When true (default), the step uses the shared pipeline conversation history.
    /// When false, the step starts with a clean conversation context, useful for steps
    /// whose output depends only on their input and system prompt, not on prior turns.
    /// </summary>
    [Description("Use the shared conversation history (default true).")]
    public bool UseConversationHistory { get; set; } = true;

    /// <summary>
    /// Names of the tools this step offers the model (YAML key <c>tools</c>). Null or empty keeps the
    /// rule of <see cref="BaseLlmStep"/>: the role's tools. Resolved by <see cref="ResolveTools"/>
    /// when the configuration has been populated.
    /// </summary>
    [JsonProperty("tools")]
    [Description("Names of the tools offered to the model; empty uses the role's tools.")]
    public List<string>? ToolNames { get; set; }

    // ── Inherited properties a YAML step cannot use ──────────────────────────
    // The registry describes every public writable property that is not [JsonIgnore]d, and the YAML
    // factory rejects any key outside that description. Hiding these four keeps `prompt:` (ignored here:
    // the message is UserPrompt) and the three that need a live object (tools, tagHandlers, enrichable)
    // from being accepted and then silently doing nothing or failing with a deserialization error.

    /// <summary>Not configurable from YAML: the user message is <see cref="UserPrompt"/>.</summary>
    [JsonIgnore]
    public override string? Prompt
    {
        get => base.Prompt;
        set => base.Prompt = value;
    }

    /// <summary>Not configurable from YAML as live objects: <c>tools</c> lists names (<see cref="ToolNames"/>).</summary>
    [JsonIgnore]
    public override IList<ITool> Tools
    {
        get => base.Tools;
        set => base.Tools = value;
    }

    /// <summary>Not configurable from YAML: handlers are live objects.</summary>
    [JsonIgnore]
    public override List<ITagHandler> TagHandlers
    {
        get => base.TagHandlers;
        init => base.TagHandlers = value;
    }

    /// <summary>Not configurable from YAML: enrichers are live objects.</summary>
    [JsonIgnore]
    public override IEnrichable? Enrichable
    {
        get => base.Enrichable;
        set => base.Enrichable = value;
    }

    /// <inheritdoc/>
    [JsonIgnore]
    public JsonSchema? InputJsonSchema { get; set; }

    /// <inheritdoc/>
    /// <remarks>Setting it switches <see cref="StepBase.OutputType"/> between <see cref="LlmStringStepResult"/> and <see cref="LlmJsonStepResult"/>.</remarks>
    [JsonIgnore]
    public JsonSchema? OutputJsonSchema
    {
        get => _outputJsonSchema;
        set
        {
            _outputJsonSchema = value;
            OutputType = value == null ? typeof(LlmStringStepResult) : typeof(LlmJsonStepResult);
        }
    }

    /// <summary>The declared output schema, when there is one; otherwise the type-derived default.</summary>
    protected override JsonSchema? Schema => _outputJsonSchema ?? base.Schema;

    /// <summary>
    /// Resolves the role: <see cref="ProfileName"/>, or the environment's default role. Both misses
    /// are hard errors: running a step against the wrong model silently is worse than refusing to start.
    /// </summary>
    /// <exception cref="InvalidOperationException">No environment, or the role is not configured.</exception>
    public void ResolveRole()
    {
        LlmEnvironment env = _llmEnvironment?.Value
            ?? throw new InvalidOperationException(
                "An LLM step needs an LlmEnvironment and none is configured. " +
                "Register the framework services and provide the AITaskAgent:LlmProviders section.");

        string roleName = ProfileName ?? env.DefaultRole;

        if (!env.Roles.TryGetValue(roleName, out LlmRole? role))
        {
            string available = env.Roles.Count > 0
                ? string.Join(", ", env.Roles.Keys)
                : "(none configured)";
            throw new InvalidOperationException(
                $"LLM role '{roleName}' is not configured. Available roles: {available}.");
        }

        SetRole(role);
    }

    /// <summary>
    /// Replaces <see cref="Tools"/> with the tools named in <see cref="ToolNames"/>, looked up (ignoring
    /// case) in the provider the step was created with: its <see cref="ITool"/> services first, then its
    /// <see cref="ITools"/> registry, then the <see cref="ToolAlias"/> services. Nothing static is read, so two pipelines built from different
    /// providers never see each other's tools. Does nothing when no names are given, so the role's
    /// tools still apply (<see cref="BaseLlmStep"/> takes them only when the step has none).
    /// </summary>
    /// <exception cref="InvalidOperationException">A name is not available; the message lists the available ones.</exception>
    public void ResolveTools()
    {
        if (ToolNames is not { Count: > 0 } names)
        {
            return;
        }

        Dictionary<string, ITool> available = new(StringComparer.OrdinalIgnoreCase);
        foreach (ITool tool in _services?.GetServices<ITool>() ?? [])
        {
            available.TryAdd(tool.Name, tool);
        }

        foreach (ITool tool in _services?.GetService<ITools>()?.GetAllTools() ?? [])
        {
            available.TryAdd(tool.Name, tool);
        }

        // Aliases last: a bare name wins, an alias only reaches what another tool of the same name shadows.
        foreach (ToolAlias alias in _services?.GetServices<ToolAlias>() ?? [])
        {
            available.TryAdd(alias.Name.Trim(), alias.Tool);
        }

        List<string> missing = [.. names.Where(n => string.IsNullOrWhiteSpace(n) || !available.ContainsKey(n.Trim()))];
        if (missing.Count > 0)
        {
            string list = available.Count > 0
                ? string.Join(", ", available.Keys.Order(StringComparer.OrdinalIgnoreCase))
                : "(none registered)";
            throw new InvalidOperationException(
                $"Tool(s) {string.Join(", ", missing.Select(n => $"'{n}'"))} not available. Available tools: {list}.");
        }

        Tools = [.. names.Select(n => available[n.Trim()]).Distinct()];
    }

    /// <summary>Runs once the registry has populated the configuration (Newtonsoft callback).</summary>
    /// <remarks>Tools first: the role only fills <see cref="Tools"/> when the step brought none.</remarks>
    [OnDeserialized]
    internal void OnConfigured(StreamingContext context)
    {
        ResolveTools();
        ResolveRole();
    }

    // ── BaseLlmStep hooks ────────────────────────────────────────────────────

    /// <remarks>
    /// Does not call <c>base</c> (its default message is <c>Prompt</c>, which a YAML step does not use); it feeds
    /// its own rendering to the same hook dispatcher the base uses, so <c>hooks.buildUserMessage</c> wraps it.
    /// </remarks>
    protected override Task<string?> BuildUserMessageAsync(IStepResult input, PipelineContext context)
        => DispatchUserMessageHook(input, context, () => Task.FromResult<string?>(
            UserPrompt != null
                ? PromptTemplateResolver.Resolve(UserPrompt, input, context, _templateProvider)
                : JsonConvert.SerializeObject(input.Value ?? input)));

    /// <remarks>See <see cref="BuildUserMessageAsync"/>: same dispatcher, own rendering as <c>next</c>.</remarks>
    protected override Task<string?> BuildSystemMessageAsync(IStepResult input, PipelineContext context)
        => DispatchSystemMessageHook(input, context, () => Task.FromResult<string?>(
            SystemPrompt != null
                ? PromptTemplateResolver.Resolve(SystemPrompt, input, context, _templateProvider)
                : null));

    protected override ConversationContext GetConversationContext(PipelineContext context)
    {
        ConversationContext conversation = context.AsLlmContext(Name).Conversation;
        return UseConversationHistory
            ? conversation
            : new ConversationContext(conversation.History.MaxTokens, conversation.History.TokenCounter);
    }

    /// <summary>Binds the parsed JSON result to the declared schema, so its validation applies it.</summary>
    protected override async Task<(IStepResult? Result, string? Error)> ParseLlmResponseAsync(LlmResult response, PipelineContext context)
    {
        (IStepResult? result, string? error) = await base.ParseLlmResponseAsync(response, context);
        if (result is LlmJsonStepResult json)
        {
            json.Schema = _outputJsonSchema;
        }

        return (result, error);
    }
}
