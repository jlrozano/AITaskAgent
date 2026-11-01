namespace AITaskAgent.Core.Hooks;

/// <summary>
/// Marks a <c>public static</c> method of a public class as a hook a YAML step can name under <c>hooks:</c>. Read by the C# unit of a
/// workflow package (<c>csharp/shared.cs</c>) and by the plugin loader. Registered as <see cref="Name"/>, or <c>Class.Method</c> when it is
/// not given; the method may have any signature the slot it is bound to accepts.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HookAttribute : Attribute
{
    /// <summary>Registry name; null means <c>Class.Method</c> (nested classes as <c>Outer.Inner.Method</c>, no namespace).</summary>
    public string? Name { get; init; }

    /// <summary>What the hook does (shown by the designer).</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Marks a <c>public static</c> method <c>Task&lt;(bool IsValid, string? Error)&gt; M(ValidateArgs)</c> as a step validator
/// (an entry of <c>validators:</c>). Registered as <c>Class.Method</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ValidatorAttribute : Attribute
{
    /// <summary>What the validator checks (shown by the designer).</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Marks a <c>public static</c> method <c>Task&lt;(bool IsValid, string? Error)&gt; M(ResultValidateArgs)</c> as a result
/// validator (an entry of <c>resultValidators:</c>). Registered as <c>Class.Method</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ResultValidatorAttribute : Attribute
{
    /// <summary>What the validator checks (shown by the designer).</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Marks a public class as a response type: its JSON schema (the one typed LLM steps send as the response format) is registered under
/// <see cref="Name"/>, or the class name, so <c>outputSchema</c> and <c>schema:Name</c> can use it.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ResponseTypeAttribute(string? name = null) : Attribute
{
    /// <summary>The schema name; null means the class name.</summary>
    public string? Name { get; } = name;
}
