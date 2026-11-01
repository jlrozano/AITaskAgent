namespace AITaskAgent.LLM.Tools.Abstractions;

/// <summary>
/// Another name a tool answers to in a step's <c>tools: [...]</c> (for instance <c>FileTools:write_to_file</c>). It is only a lookup key:
/// the name the model sees stays <see cref="ITool.Name"/> (providers reject <c>:</c>). Register it as a service next to the tool; a bare
/// name always has priority over an alias, so an alias can reach a tool another one of the same name shadows.
/// </summary>
/// <param name="Name">The alias, compared ignoring case after trimming.</param>
/// <param name="Tool">The tool it resolves to.</param>
public sealed record ToolAlias(string Name, ITool Tool);
