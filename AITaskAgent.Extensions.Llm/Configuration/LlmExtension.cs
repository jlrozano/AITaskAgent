using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.LLM.Configuration;

/// <summary>
/// The LLM extension: <see cref="LlmEnvironment"/> binding, conversation storage and factory,
/// tools, the <see cref="Conversation.Context.LlmPipelineContextFactory"/> as default context
/// factory, and the <c>[StepTag]</c> steps of this assembly.
/// </summary>
/// <remarks>
/// <c>AddAITaskAgentLlm()</c> and <c>AddAITaskAgent()</c> add it. The assembly scan registers the
/// configurable <see cref="AITaskAgent.YAML.Steps.YamlLlmStep"/> (tag <c>LlmStep</c>), the step YAML
/// pipelines declare. The other LLM steps (<c>TemplateLlmStep&lt;,&gt;</c>,
/// <c>IntentionAnalyzerStep&lt;&gt;</c>, <c>RouterStep</c>...) are generic or need constructor
/// arguments, so they carry no <c>[StepTag]</c>.
/// </remarks>
public sealed class LlmExtension : IExtension
{
    /// <inheritdoc/>
    public string Name => "AITaskAgent.Llm";

    /// <inheritdoc/>
    public Type? ConfigurationType => typeof(LlmServiceCollectionExtensions.LlmEnvironmentRaw);

    /// <inheritdoc/>
    public void Register(IExtensionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        LlmServiceCollectionExtensions.RegisterLlmServices(builder.Services);
        builder.AddStepsFromAssembly(typeof(LlmExtension).Assembly);
    }

    /// <inheritdoc/>
    public ExtensionInfo Info() => new()
    {
        Entries = [.. ExtensionDiscovery.StepEntries(typeof(LlmExtension).Assembly), ExtensionEntry.For(ExtensionKinds.Configuration, typeof(LlmServiceCollectionExtensions.LlmEnvironmentRaw))]
    };
}
