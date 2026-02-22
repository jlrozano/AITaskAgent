

using NJsonSchema;

namespace BRMS.Core.Abstractions;

/// <summary>
/// Contrato para transformadores de datos
/// </summary>
public interface IDataTransform : IRule
{
    // JsonSchema InputModel { get; } // NO NECESARIO. Viene en el contexto de ejecución
    JsonSchema OutputType { get; set; }
}
