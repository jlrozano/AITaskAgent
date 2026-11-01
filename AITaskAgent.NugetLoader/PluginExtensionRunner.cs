using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace AITaskAgent.NugetLoader;

/// <summary>
/// What running a plugin's <see cref="IExtension"/> produced: the extension with its declaration and registration
/// (<see cref="PluginExtension"/>), the capture it registered into and the same registration as <see cref="PluginContributions"/> (for the
/// collision checks).
/// </summary>
/// <param name="Extension">The extension and the comparison of <c>Info()</c> with what <c>Register</c> did.</param>
/// <param name="Capture">What <c>Register</c> asked the builder for.</param>
/// <param name="Contributions">Steps, hooks and response types actually registered.</param>
public sealed record PluginExtensionRun(PluginExtension Extension, ExtensionCapture Capture, PluginContributions Contributions);

/// <summary>
/// Runs an <see cref="IExtension"/> the way a plugin host does: <c>Info()</c>, then <c>Register</c> against an <see cref="ExtensionCapture"/>,
/// then the comparison of the two. Used by <see cref="PluginLoader"/> (after approval, inside the plugin's load context) and by hosts that
/// reference a plugin directly.
/// </summary>
public static class PluginExtensionRunner
{
    /// <summary>
    /// Runs <paramref name="extension"/>. Problems are added to <paramref name="problems"/>: <c>info-failed</c> (a warning: the declaration is taken
    /// as empty), <c>info-mismatch</c> (a warning naming the items declared and not registered, or registered and not declared) and
    /// <c>extension-failed</c> (<c>Register</c> threw or registered something twice: null is returned and nothing of the plugin is registered).
    /// </summary>
    /// <param name="extension">The instance.</param>
    /// <param name="plugin">The identity <c>IExtensionBuilder.Plugin</c> reports while it registers.</param>
    /// <param name="reference">The host's step registry, for reads during <c>Register</c>; null = none.</param>
    /// <param name="problems">Where problems go.</param>
    /// <param name="noteChanged">Called with every exception from plugin code (the loader looks for a failed content check); may be null.</param>
    /// <param name="logger">Optional.</param>
    public static PluginExtensionRun? Run(
        IExtension extension, PluginContext plugin, IStepRegistry? reference, List<LoadProblem> problems, Action<Exception>? noteChanged = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(problems);
        string typeName = extension.GetType().FullName ?? extension.GetType().Name;
        ExtensionInfo declared;
        try
        {
            declared = extension.Info() ?? ExtensionInfo.Empty;
        }
        catch (Exception ex)
        {
            noteChanged?.Invoke(ex);
            declared = ExtensionInfo.Empty;
            problems.Add(new LoadProblem("info-failed", $"Info() of '{typeName}' threw and its declaration is taken as empty: {ex.GetBaseException().Message}"));
        }

        ExtensionCapture capture = new(reference, plugin);
        ExtensionInfo registered;
        try
        {
            extension.Register(capture);
            registered = capture.ActualInfo(extension);
            foreach (IGrouping<string, DiscoveredHook> twice in capture.Hooks.GroupBy(h => h.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                throw new InvalidOperationException($"Hook name '{twice.Key}' is registered twice by the extension.");
            }

            foreach (IGrouping<string, (string Name, Type Type)> twice in capture.ResponseTypes.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                throw new InvalidOperationException($"Response type '{twice.Key}' is registered twice by the extension.");
            }
        }
        catch (Exception ex)
        {
            noteChanged?.Invoke(ex);
            problems.Add(new LoadProblem("extension-failed", $"Register of '{typeName}' (plugin '{plugin.Id}') failed and nothing of the plugin is registered: {ex.GetBaseException().Message}"));
            return null;
        }

        (IReadOnlyList<ExtensionEntry> missing, IReadOnlyList<ExtensionEntry> extra) = ExtensionInfo.Difference(declared, registered);
        if (missing.Count > 0 || extra.Count > 0)
        {
            static string Items(IEnumerable<ExtensionEntry> entries) => string.Join(", ", entries.Select(e => $"{e.Kind} '{(e.Name.Length > 0 ? e.Name : e.Type)}'"));
            string message = $"Info() of '{typeName}' does not match what Register registered (the real registration is used)."
                + (missing.Count > 0 ? $" Declared and not registered: {Items(missing)}." : string.Empty)
                + (extra.Count > 0 ? $" Registered and not declared: {Items(extra)}." : string.Empty);
            problems.Add(new LoadProblem("info-mismatch", message));
            logger?.LogWarning("Plugin {Id}: {Message}", plugin.Id, message);
        }

        PluginContributions contributions = new()
        {
            Steps = [.. capture.CapturedSteps.Select(s => new StepContribution(s.Tag, s.Type.FullName ?? s.Type.Name,
                s.Type.GetCustomAttribute<StepTagAttribute>(inherit: false)?.TagName == s.Tag))],
            Hooks = [.. capture.Hooks.Select(h => new HookContribution(h.Name, h.Kind, h.Method.DeclaringType?.FullName ?? string.Empty, h.Method.Name, h.Description))],
            ResponseTypes = [.. capture.ResponseTypes.Select(r => new ResponseTypeContribution(r.Name, r.Type.FullName ?? r.Type.Name))],
        };
        PluginExtension result = new(
            extension, typeName, declared, registered, missing, extra,
            [.. capture.ToolInstances], [.. capture.ToolSources], [.. capture.TemplateSources], capture.Services);
        return new PluginExtensionRun(result, capture, contributions);
    }
}
