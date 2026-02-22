namespace BRMS.Core.Attributes;

/// <summary>
/// Atributo para excluir una propiedad del esquema JSON generado para una regla.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class ExcludeFromSchemaAttribute : Attribute
{
}
