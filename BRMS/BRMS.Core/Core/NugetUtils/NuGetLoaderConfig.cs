namespace BRMS.Core.Core.NugetUtils;

public class NuGetLoaderConfig
{
    private List<NuGetPluginConfig> _plugins = [];
    public List<NuGetPackageSourceConfig> PackageSources { get; set; } = [];
    public string GlobalPackagesFolder { get; set; } = "%USERPROFILE%\\.nuget\\packages";
    public string DefaultTargetFramework { get; set; } = "net9.0";
    public NuGetCacheSettings CacheSettings { get; set; } = new();
    public List<NuGetPluginConfig> Plugins { get => _plugins; set => _plugins = value ?? []; }

}
