using Microsoft.Extensions.Configuration;

namespace AITaskAgent.Tests;

/// <summary>
/// Checks that every sample's appsettings can actually be bound by the framework.
///
/// A stale configuration compiles perfectly and only fails when the sample is run, which is exactly
/// the failure the build cannot catch: two samples were shipping configuration written for an
/// earlier shape of the LLM section and would have thrown on startup.
/// </summary>
/// <remarks>
/// The BaseUrl assertion below was briefly dropped on the reasoning that neither backend crashes
/// without one at the raw HTTP-client level. That verification was incomplete: it looked at
/// OpenAILlmService/GeminiLlmService directly, not at how a properly wired sample actually reaches
/// them. Every sample that goes through the framework's real config path -
/// AITaskAgent.Configuration.ServiceCollectionExtensions.ConfigureLlmEnvironmentOptions, reached via
/// AddAITaskAgent() + IOptions&lt;LlmEnvironment&gt; - throws there if BaseUrl is missing, by design
/// ("has no BaseUrl configured"). Every sample using that path already supplies one, including for
/// Gemini providers (the real generativelanguage.googleapis.com endpoint, even though the SDK would
/// default to it anyway) - so the assertion holds in practice today, and only fails for a sample
/// whose config drifted from that convention. Restored.
/// </remarks>
public class SampleConfigurationTests
{
    /// <summary>Walks up from the test binaries to the repository root.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AITaskAgent.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>
    /// Samples whose appsettings the framework binder never sees: they bind their own local types.
    /// </summary>
    private static readonly HashSet<string> BindsItsOwnConfig = new(StringComparer.OrdinalIgnoreCase)
    {
        "GeminiTestApp"
    };

    public static TheoryData<string> SampleConfigs()
    {
        var data = new TheoryData<string>();
        var samplesRoot = Path.Combine(RepoRoot(), "Samples");

        // Only the checked-in file of each sample. Build output holds stale copies of exactly the
        // configuration this test is meant to police.
        foreach (var dir in Directory.GetDirectories(samplesRoot))
        {
            var file = Path.Combine(dir, "appsettings.json");
            if (File.Exists(file) && !BindsItsOwnConfig.Contains(Path.GetFileName(dir)))
            {
                data.Add(file);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SampleConfigs))]
    public void Every_role_names_a_configured_provider_and_a_model(string path)
    {
        // Read through IConfiguration rather than a JSON parser: it tolerates the comments some of
        // these files carry, exactly as the samples themselves do.
        var section = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build()
            .GetSection("AITaskAgent:LlmProviders");

        if (!section.Exists())
        {
            return;
        }

        var providers = section.GetSection("Providers").GetChildren().Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roles = section.GetSection("Roles").GetChildren().ToList();
        var sample = Path.GetFileName(Path.GetDirectoryName(path));

        Assert.True(roles.Count > 0, $"{sample}: LlmProviders is configured but declares no roles.");

        foreach (var role in roles)
        {
            var provider = role["Provider"];
            Assert.False(string.IsNullOrWhiteSpace(provider), $"{sample}: role '{role.Key}' declares no Provider.");
            Assert.True(providers.Contains(provider!), $"{sample}: role '{role.Key}' points at unknown provider '{provider}'.");
            Assert.False(string.IsNullOrWhiteSpace(role["Model"]), $"{sample}: role '{role.Key}' declares no Model.");
        }

        var defaultRole = section["DefaultRole"];
        if (!string.IsNullOrWhiteSpace(defaultRole))
        {
            Assert.True(roles.Any(r => string.Equals(r.Key, defaultRole, StringComparison.OrdinalIgnoreCase)),
                $"{sample}: DefaultRole '{defaultRole}' is not among the configured roles.");
        }

        foreach (var provider in section.GetSection("Providers").GetChildren())
        {
            Assert.False(string.IsNullOrWhiteSpace(provider["BaseUrl"]),
                $"{sample}: provider '{provider.Key}' declares no BaseUrl.");
        }

        foreach (var fallback in section.GetSection("FallbackProviders").GetChildren())
        {
            var provider = fallback["Provider"];
            Assert.True(providers.Contains(provider ?? ""),
                $"{sample}: fallback points at unknown provider '{provider}'.");
            Assert.False(string.IsNullOrWhiteSpace(fallback["Model"]),
                $"{sample}: fallback for '{provider}' declares no Model.");
        }
    }
}
