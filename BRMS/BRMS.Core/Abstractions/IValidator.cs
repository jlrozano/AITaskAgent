namespace BRMS.Core.Abstractions;


/// <summary>
/// Interfaz especializada para reglas de validación que no modifican datos, solo verifican
/// </summary>
public interface IValidator : IRule<IRuleResult>;
