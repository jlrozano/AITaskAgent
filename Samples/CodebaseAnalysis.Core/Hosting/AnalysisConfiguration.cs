using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>
/// Carga de <c>appsettings.analysis.json</c>: se sustituyen las referencias <c>${VARIABLE}</c> por
/// variables de entorno y después se aplican, por este orden, variables de entorno, user secrets y
/// línea de comandos.
/// </summary>
public static class AnalysisConfiguration
{
    public const string ConfigFileName = "appsettings.analysis.json";

    /// <summary>Compartido por la CLI y cualquier otro host, para que usen la misma clave guardada.</summary>
    public const string UserSecretsId = "3e60cd17-5eb3-409f-b631-3ab8497c947d";

    /// <summary>
    /// Solo el directorio del ejecutable: buscar también en el directorio actual permitía que un
    /// appsettings suelto se colara o que se editara uno que nunca llegaba a cargarse.
    /// </summary>
    public static string DefaultConfigFilePath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

    /// <summary>Variable de entorno del proceso, del usuario o de la máquina, por ese orden.</summary>
    public static string? GetEnvVar(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

    /// <summary>Sustituye cada <c>${NOMBRE}</c> por su variable; si no existe, la deja literal.</summary>
    public static string ExpandEnvironmentVariables(string json, Func<string, string?>? getEnvVar = null)
    {
        getEnvVar ??= GetEnvVar;
        return Regex.Replace(json, @"\${(\w+)}", m => getEnvVar(m.Groups[1].Value) ?? m.Value);
    }

    /// <summary>
    /// Añade al builder el JSON expandido, las variables de entorno, los user secrets y la línea de
    /// comandos, en ese orden.
    /// </summary>
    /// <exception cref="FileNotFoundException">Si no existe <paramref name="configFilePath"/>.</exception>
    public static IConfigurationBuilder AddAnalysisConfiguration(
        this IConfigurationBuilder builder,
        string configFilePath,
        string[] args,
        Func<string, string?>? getEnvVar = null,
        Action<string>? log = null)
    {
        if (!File.Exists(configFilePath))
        {
            throw new FileNotFoundException($"No se encontró la configuración '{configFilePath}'.", configFilePath);
        }

        log?.Invoke($"📄 Cargando configuración desde: {configFilePath}");

        var expandedJson = ExpandEnvironmentVariables(File.ReadAllText(configFilePath), getEnvVar);

        // El stream no se libera: el proveedor JSON lo lee al construir y no vuelve a leerlo.
        return builder
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(expandedJson)))
            .AddEnvironmentVariables()
            .AddUserSecrets(UserSecretsId)
            .AddCommandLine(args);
    }

    /// <inheritdoc cref="AddAnalysisConfiguration"/>
    public static IConfigurationRoot Build(
        string configFilePath,
        string[] args,
        Func<string, string?>? getEnvVar = null,
        Action<string>? log = null) =>
        new ConfigurationBuilder().AddAnalysisConfiguration(configFilePath, args, getEnvVar, log).Build();
}
