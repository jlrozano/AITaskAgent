
using NJsonSchema;


namespace BRMS.Core.Abstractions;

/// <summary>
/// Interfaz base que deben implementar los paquetes NuGet que contengan reglas, validadores o normalizadores.
/// Los paquetes deben implementar una clase con un constructor sin parámetros.
/// Cuando se cargue el paquete, se buscará una clase que implemente esta interfaz,
/// se creará una instancia y se ejecutará el método Register.
/// </summary>
public interface IPlugin
{

    /// <summary>
    /// Registra todas las reglas, validadores y normalizadores del plugin en el sistema.
    /// Este método será llamado automáticamente durante la carga del plugin.
    /// </summary>
    Task Register();

    /// <summary>
    /// Desregistra todas las reglas, validadores y normalizadores del plugin del sistema.
    /// Este método será llamado automáticamente durante la descarga del plugin.
    /// </summary>
    Task Unregister();

    /// <summary>
    /// Devuelve la clave dentro del archivo de configuracion BRMS donde se configurará este plugin
    /// y el modelo o estructura que tiene. O null si no necesita configuración.
    /// </summary>
    JsonSchema? ConfigurationModel();
}
