namespace BRMS.Core.Abstractions;

/// <summary>
/// Interfaz para proveedores de configuraciones 
/// </summary>

public interface IBRMSConfigurationProvider
{
    // Para tipos de valor
    Task<(bool exists, T value)> GetValueAsync<T>(string key) where T : struct;

    Task<T?> GetObjectAsync<T>(string key) where T : class;
}

