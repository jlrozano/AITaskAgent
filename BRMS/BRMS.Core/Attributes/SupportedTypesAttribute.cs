using BRMS.Core.Models;

namespace BRMS.Core.Attributes;

/// <summary>
/// Atributo que especifica los tipos de entrada soportados por una regla
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class SupportedTypesAttribute(params RuleInputType[] types) : Attribute
{
    /// <summary>
    /// Tipos de entrada soportados por la regla
    /// </summary>
    public RuleInputType[] Types { get; } = types ?? [RuleInputType.Any];
}
