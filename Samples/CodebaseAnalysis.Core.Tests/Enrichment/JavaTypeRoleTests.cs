using CodebaseAnalysis.Core.Enrichment.Java;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// El papel de un tipo Java (<c>type_role</c>) con las mismas categorías que el enriquecedor C#, calculado solo con lo que
/// da el analizador: clase, acceso, si es abstracta y su superclase directa.
/// </summary>
public class JavaTypeRoleTests
{
    private static JavaTypeRecord Type(string kind, string? access = "public", bool isAbstract = false, string? super = null) => new()
    {
        Kind = kind,
        Access = access,
        Abstract = isAbstract,
        Super = super == null ? null : new JavaTypeReference { Fqn = super }
    };

    [Theory]
    [InlineData("interface", "public", true, null, "contract")]
    [InlineData("interface", "package", true, null, "infrastructure")]
    [InlineData("class", "public", true, null, "contract")]
    [InlineData("class", "protected", true, null, "infrastructure")]
    [InlineData("class", "public", false, null, "implementation")]
    [InlineData("class", "package", false, "com.acme.Base", "implementation")]
    [InlineData("class", "public", false, "java.lang.RuntimeException", "model")]
    [InlineData("class", "public", false, "java.lang.Exception", "model")]
    [InlineData("enum", "public", false, null, "model")]
    [InlineData("annotation", "public", true, null, "model")]
    public void The_role_follows_kind_access_abstraction_and_direct_superclass(string kind, string access, bool isAbstract, string? super, string role) =>
        Assert.Equal(role, JavaGraphWriter.TypeRole(Type(kind, access, isAbstract, super)));
}
