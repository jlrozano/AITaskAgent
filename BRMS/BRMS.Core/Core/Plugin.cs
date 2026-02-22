using BRMS.Core.Abstractions;
using BRMS.Core.Extensions;
using NJsonSchema;



namespace BRMS.Core.Core;


public abstract class Plugin : IPlugin
{
    protected virtual JsonSchema? ConfigurationModel()
    {
        return null;
    }

    protected abstract Task Register();

    protected abstract Task Unregister();

    JsonSchema? IPlugin.ConfigurationModel()
    {
        return ConfigurationModel();
    }

    Task IPlugin.Register()
    {
        return Register();
    }

    Task IPlugin.Unregister()
    {
        return Unregister();
    }
}
public abstract class Plugin<ConfigurationModel>(IBRMSConfigurationProvider configProvider, string key) : IPlugin where ConfigurationModel : class, new()
{

    private ConfigurationModel? _configuration;


    protected async Task<ConfigurationModel?> GetConfiguration()
    {
        return _configuration ??= (await configProvider?.GetObjectAsync<ConfigurationModel>(key)!) ?? new ConfigurationModel();
    }

    JsonSchema? IPlugin.ConfigurationModel()
    {

        return typeof(ConfigurationModel).ToJSchema();

    }

    async Task IPlugin.Register()
    {
        await Register(await GetConfiguration());
    }

    protected abstract Task Register(ConfigurationModel? config);

    protected abstract Task UnRegister(ConfigurationModel? config);
    ///// <summary>
    ///// Desregistra las reglas, validadores y normalizadores del plugin durante la descarga del plugin
    ///// </summary>
    async Task IPlugin.Unregister()
    {
        await UnRegister(await GetConfiguration());
    }

}
