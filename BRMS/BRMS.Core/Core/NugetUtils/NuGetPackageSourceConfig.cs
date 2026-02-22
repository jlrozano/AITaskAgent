namespace BRMS.Core.Core.NugetUtils;

public class NuGetPackageSourceConfig
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
}
