using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System.Text;

namespace AITaskAgent.Script.Hosting;

/// <summary>
/// The host functions that ship with the extension: <c>config.get</c>, <c>file.readText</c>, <c>file.exists</c>. They are listed
/// in every catalog but a script reaches one only when the host grants it (see <see cref="ScriptHostOptions"/>).
/// </summary>
public static class BuiltInHostFunctions
{
    /// <summary>The built-in host functions (listed in every catalog; granted only by the host).</summary>
    public static IReadOnlyList<IScriptHostFunction> Create() =>
    [
        new DelegateHostFunction(
            "config.get",
            (args, ctx, _) => Task.FromResult<JToken?>(ConfigGet(args, Options(ctx))),
            "Reads a read-only configuration value (text) the host exposes to scripts; null when it is not set.",
            TextArgument("Name of the value.")),
        new DelegateHostFunction(
            "file.readText",
            (args, ctx, ct) => ReadText(args, Options(ctx), ct),
            "Reads a UTF-8 text file under the folders the host allows (no roots configured = always an error); at most 1 MiB.",
            TextArgument("Path of the file (absolute, or relative to the first allowed folder).")),
        new DelegateHostFunction(
            "file.exists",
            (args, ctx, _) => Task.FromResult<JToken?>(Exists(args, Options(ctx))),
            "Tells whether a file exists under the folders the host allows (outside them: false).",
            TextArgument("Path of the file (absolute, or relative to the first allowed folder).")),
    ];

    private static ScriptHostOptions Options(ScriptCallContext ctx)
        => ctx.Services.GetService<ScriptHostOptions>() ?? new ScriptHostOptions();

    private static JObject TextArgument(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    private static JToken? ConfigGet(JToken args, ScriptHostOptions options)
    {
        string name = Text(args, "config.get");
        return options.Config.TryGetValue(name, out string? value) ? new JValue(value) : null;
    }

    private static async Task<JToken?> ReadText(JToken args, ScriptHostOptions options, CancellationToken ct)
    {
        string path = Resolve(Text(args, "file.readText"), options, mustExist: true)
            ?? throw new InvalidOperationException("the file is outside the folders the host allows scripts to read, or does not exist.");

        FileInfo info = new(path);
        if (info.Length > options.MaxFileBytes)
        {
            throw new InvalidOperationException($"the file is {info.Length} bytes; the limit is {options.MaxFileBytes}.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("the file is not valid UTF-8 text.");
        }
    }

    private static JToken Exists(JToken args, ScriptHostOptions options)
        => Resolve(Text(args, "file.exists"), options, mustExist: true) != null;

    private static string Text(JToken args, string function)
        => args.Type == JTokenType.String && args.Value<string>() is { Length: > 0 } text
            ? text
            : throw new InvalidOperationException($"'{function}' takes one non-empty text argument.");

    /// <summary>
    /// Canonical full path of <paramref name="requested"/> when it lies under one of the allowed roots, else null. Refuses
    /// device paths, UNC paths, paths with a reparse point (symlink, junction) anywhere under the root, and the root escape by <c>..</c>.
    /// </summary>
    internal static string? Resolve(string requested, ScriptHostOptions options, bool mustExist)
    {
        if (options.FileRoots.Count == 0 || requested.Contains('\0') || requested.StartsWith(@"\\", StringComparison.Ordinal)
            || requested.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (string configured in options.FileRoots)
        {
            string root;
            try
            {
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            }
            catch (Exception)
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(requested, root + Path.DirectorySeparatorChar);
            }
            catch (Exception)
            {
                return null;
            }

            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            {
                continue;
            }

            if (mustExist && !File.Exists(candidate))
            {
                continue;
            }

            if (HasReparsePoint(root, candidate))
            {
                return null;
            }

            return candidate;
        }

        return null;
    }

    private static bool HasReparsePoint(string root, string candidate)
    {
        string current = candidate;
        while (current.Length > root.Length)
        {
            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return true;
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent == null || parent.Length >= current.Length)
            {
                break;
            }

            current = parent;
        }

        return false;
    }
}
