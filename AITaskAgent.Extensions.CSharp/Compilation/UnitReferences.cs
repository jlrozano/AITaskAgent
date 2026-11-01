using Microsoft.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace AITaskAgent.CSharp;

/// <summary>
/// The assemblies the author's code compiles against: an allowlist of the running runtime's assemblies (the implementation
/// assemblies, as a runtime without an SDK has no reference pack) and of the framework's. Not the whole runtime: no
/// <c>System.Diagnostics.Process</c>, <c>System.Net.*</c> or <c>System.Console</c>. That stops honest mistakes, not malice
/// (reflection reaches anything the process can).
/// </summary>
internal static class UnitReferences
{
    private static readonly string[] Allowed =
    [
        // The runtime's contract and the collections, text, threading and component model every body uses.
        "System.Private.CoreLib", "System.Runtime", "netstandard", "mscorlib",
        "System.Collections", "System.Collections.Concurrent", "System.Collections.Immutable", "System.Collections.NonGeneric",
        "System.Linq", "System.Linq.Expressions", "System.Memory", "System.ObjectModel",
        "System.Text.RegularExpressions", "System.Text.Json", "System.Text.Encoding.Extensions", "System.Text.Encodings.Web",
        "System.Threading", "System.Threading.Tasks", "System.Threading.Tasks.Parallel",
        "System.ComponentModel", "System.ComponentModel.Primitives", "System.ComponentModel.Annotations", "System.ComponentModel.TypeConverter",
        "System.Globalization", "System.Runtime.Extensions", "System.Runtime.InteropServices", "System.Runtime.Numerics", "System.Numerics.Vectors",
        "System.IO", "System.IO.FileSystem", "System.Diagnostics.Debug", "System.Diagnostics.DiagnosticSource", "System.Diagnostics.Tools",
        "System.Reflection", "System.Resources.ResourceManager", "System.Security.Cryptography", "System.Net.Primitives",
        // What the framework's types show in their signatures.
        "AITaskAgent.Abstractions", "AITaskAgent.Core", "AITaskAgent.Extensions.CSharp",
        "Newtonsoft.Json", "NJsonSchema",
        "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Configuration.Abstractions", "Microsoft.Extensions.Primitives",
    ];

    private static readonly Lazy<(IReadOnlyList<MetadataReference> References, string Id)> Cached = new(Build);

    /// <summary>The references of every unit.</summary>
    public static IReadOnlyList<MetadataReference> Default => Cached.Value.References;

    /// <summary>Identifies the reference set and the compiler (part of the cache key: another runtime or framework build recompiles).</summary>
    public static string Id => Cached.Value.Id;

    // A file read once per size and write time: the editor's completion builds a compilation per keystroke.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MetadataReference> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>References for extra assemblies (files), used by hosts that resolve NuGet packages for the unit.</summary>
    public static IEnumerable<MetadataReference> FromFiles(IEnumerable<string> paths) => paths.Select(p =>
    {
        FileInfo info = new(p);
        return Files.GetOrAdd($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", _ => Reference(p));
    });

    private static (IReadOnlyList<MetadataReference>, string) Build()
    {
        HashSet<string> allowed = new(Allowed, StringComparer.OrdinalIgnoreCase);
        List<string> files = [];
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string platform)
        {
            files.AddRange(platform.Split(Path.PathSeparator).Where(p => allowed.Contains(Path.GetFileNameWithoutExtension(p))));
        }

        // Assemblies of the framework that the trusted list might not name (a host that loads them by path).
        foreach (Type anchor in new[] { typeof(AITaskAgent.Core.Abstractions.IStep), typeof(AITaskAgent.Core.Steps.StepBase), typeof(CSharpStep), typeof(Newtonsoft.Json.JsonConvert), typeof(NJsonSchema.JsonSchema) })
        {
            string location = anchor.Assembly.Location;
            if (location.Length > 0 && !files.Contains(location, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(location);
            }
        }

        files = [.. files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

        // The key: compiler version + each reference's name, size and write time (cheap, and changes with the runtime or the build); «csu2» since the hook attributes moved to AITaskAgent.Abstractions (2026-10-21).
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("csu2|" + typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.GetName().Version + "|" + Environment.Version));
        foreach (string file in files)
        {
            FileInfo info = new(file);
            hash.AppendData(Encoding.UTF8.GetBytes($"{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks};"));
        }

        return ([.. files.Select(Reference)], Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>
    /// A reference to <paramref name="path"/>, with its XML documentation when the file ships one beside it (<c>Name.xml</c>): the editor's
    /// completion shows the summaries (the framework's assemblies generate theirs). Reading it is lazy; a broken file shows no docs.
    /// </summary>
    private static MetadataReference Reference(string path)
    {
        string xml = Path.ChangeExtension(path, ".xml");
        return File.Exists(xml)
            ? MetadataReference.CreateFromFile(path, documentation: new XmlDocs(xml))
            : MetadataReference.CreateFromFile(path);
    }

    /// <summary>The members of a compiler-generated XML documentation file, read on first use (Roslyn's own provider lives in Workspaces).</summary>
    private sealed class XmlDocs(string path) : DocumentationProvider
    {
        private readonly Lazy<Dictionary<string, string>> _members = new(() =>
        {
            Dictionary<string, string> members = new(StringComparer.Ordinal);
            try
            {
                foreach (System.Xml.Linq.XElement member in System.Xml.Linq.XDocument.Load(path).Descendants("member"))
                {
                    if (member.Attribute("name")?.Value is { } name)
                    {
                        members[name] = member.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // No docs: completion still works.
            }

            return members;
        });

        protected override string? GetDocumentationForSymbol(string documentationMemberID, System.Globalization.CultureInfo preferredCulture, CancellationToken cancellationToken = default)
            => _members.Value.GetValueOrDefault(documentationMemberID);

        public override bool Equals(object? obj) => obj is XmlDocs other && string.Equals(other._path, _path, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(_path);

        private readonly string _path = path;
    }
}
