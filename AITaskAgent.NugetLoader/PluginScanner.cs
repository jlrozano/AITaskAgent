using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Hooks;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace AITaskAgent.NugetLoader;

/// <summary>What <see cref="PluginScanner.Scan"/> found, and what is wrong with it.</summary>
public sealed record PluginScanResult(PluginContributions Contributions, IReadOnlyList<LoadProblem> Problems);

/// <summary>
/// Reads what a plugin contributes from the <b>metadata</b> of its own assemblies (<see cref="System.Reflection.Metadata"/>): nothing is loaded
/// into any <see cref="AssemblyLoadContext"/>, no type is created, no attribute constructor runs. That is what lets the contributions be shown
/// before the plugin is approved. Interfaces are followed through base classes and interfaces declared in the plugin's own assemblies, in
/// the dependency files given (metadata again) and, for the host's shared assemblies (<c>AITaskAgent.*</c>…), in the host's copy (host code).
/// </summary>
/// <remarks>
/// Discovered: public, concrete, non-generic <c>IStep</c> (tag = <c>[StepTag]</c>, else <see cref="Type.FullName"/>); public concrete <c>ITool</c>
/// with a public parameterless constructor; <c>IToolSource</c>, <c>ITemplateSource</c> and <c>IPluginConfiguration</c> classes (public, concrete,
/// parameterless constructor; at most one configuration); <c>public static</c> methods with <c>[Hook]</c>, <c>[Validator]</c>,
/// <c>[ResultValidator]</c>; classes with <c>[ResponseType]</c>. The plugin's <c>IExtension</c> class (public, concrete, parameterless constructor) is
/// reported as <see cref="PluginContributions.Extension"/>: exactly one, or the one the manifest names; several is <c>multiple-extensions</c>.
/// What the extension registers is only known by running it (after approval): the lists above are the preview the user approves.
/// </remarks>
public static class PluginScanner
{
    private const string LlmAssembly = "AITaskAgent.Extensions.Llm";
    private const string CoreAssembly = "AITaskAgent.Core";

    private static readonly TypeKey IStepKey = KeyOf(typeof(IStep));
    private static readonly TypeKey IExtensionKey = KeyOf(typeof(IExtension));
    private static readonly TypeKey IConfigurationKey = KeyOf(typeof(IPluginConfiguration));
    private static readonly TypeKey IToolKey = new(LlmAssembly, "AITaskAgent.LLM.Tools.Abstractions.ITool");
    private static readonly TypeKey IToolSourceKey = new(LlmAssembly, "AITaskAgent.LLM.Tools.Abstractions.IToolSource");
    private static readonly TypeKey ITemplateSourceKey = new(CoreAssembly, "AITaskAgent.Support.Template.ITemplateSource");
    private static readonly TypeKey StepTagKey = KeyOf(typeof(StepTagAttribute));
    private static readonly TypeKey HookKey = KeyOf(typeof(HookAttribute));
    private static readonly TypeKey ValidatorKey = KeyOf(typeof(ValidatorAttribute));
    private static readonly TypeKey ResultValidatorKey = KeyOf(typeof(ResultValidatorAttribute));
    private static readonly TypeKey ResponseTypeKey = KeyOf(typeof(ResponseTypeAttribute));

    /// <summary>Scans <paramref name="ownAssemblies"/> (the plugin's own files: its contributions); <paramref name="dependencies"/> are only read to follow base types.</summary>
    /// <param name="ownAssemblies">The plugin's own files.</param>
    /// <param name="dependencies">Files only read to follow base types.</param>
    /// <param name="extensionType">The <c>IExtension</c> class the manifest names (<c>plugin.json</c> <c>extension</c>), or null.</param>
    public static PluginScanResult Scan(IReadOnlyList<string> ownAssemblies, IEnumerable<string>? dependencies = null, string? extensionType = null)
    {
        ArgumentNullException.ThrowIfNull(ownAssemblies);
        using Session session = new(ownAssemblies, dependencies ?? [], extensionType);
        return session.Run();
    }

    /// <summary>
    /// The version an assembly declares, read from metadata: <c>[AssemblyInformationalVersion]</c> without its <c>+metadata</c>, else the assembly
    /// version; null when the file cannot be read.
    /// </summary>
    public static string? AssemblyVersionOf(string path)
    {
        try
        {
            using PEReader pe = new(ImmutableArray.Create(File.ReadAllBytes(path)));
            if (!pe.HasMetadata)
            {
                return null;
            }

            MetadataReader reader = pe.GetMetadataReader();
            AssemblyDefinition assembly = reader.GetAssemblyDefinition();
            foreach (CustomAttributeHandle handle in assembly.GetCustomAttributes())
            {
                CustomAttribute attribute = reader.GetCustomAttribute(handle);
                if (AttributeKey(reader, reader.GetString(assembly.Name), attribute) is { FullName: "System.Reflection.AssemblyInformationalVersionAttribute" }
                    && attribute.DecodeValue(StringTypeProvider.Instance).FixedArguments is [{ Value: string informational }, ..]
                    && informational.Length > 0)
                {
                    return informational.Split('+')[0];
                }
            }

            return assembly.Version.ToString(3);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    // ── Session ──────────────────────────────────────────────────────────────

    private sealed class Session : IDisposable
    {
        private readonly List<Module> _own = [];
        private readonly Dictionary<string, Module?> _modules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _dependencyPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TypeKey, HashSet<TypeKey>> _implemented = [];
        private readonly HashSet<TypeKey> _inProgress = [];
        private readonly List<LoadProblem> _problems = [];
        private readonly IReadOnlyList<string> _ownPaths;
        private readonly string? _extensionType;
        private readonly List<TypeContribution> _extensions = [];
        private readonly List<string> _badExtensions = [];

        public Session(IReadOnlyList<string> own, IEnumerable<string> dependencies, string? extensionType = null)
        {
            _ownPaths = own;
            _extensionType = extensionType;
            foreach (string path in dependencies)
            {
                _dependencyPaths.TryAdd(Path.GetFileNameWithoutExtension(path), path);
            }
        }

        public PluginScanResult Run()
        {
            foreach (string path in _ownPaths)
            {
                try
                {
                    Module module = Module.Open(path);
                    _own.Add(module);
                    _modules[module.AssemblyName] = module;
                }
                catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _problems.Add(new LoadProblem("load-failed", $"'{Path.GetFileName(path)}' is not a .NET assembly that can be read: {ex.Message}"));
                }
            }

            List<StepContribution> steps = [];
            List<SkippedStep> skipped = [];
            List<TypeContribution> tools = [];
            List<TypeContribution> toolSources = [];
            List<TypeContribution> templates = [];
            List<TypeContribution> configurations = [];
            List<HookContribution> hooks = [];
            List<ResponseTypeContribution> responses = [];

            foreach (Module module in _own)
            {
                MetadataReader reader = module.Reader;
                foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
                {
                    TypeDefinition type = reader.GetTypeDefinition(handle);
                    string fullName = module.FullNameOf(handle);
                    if (fullName == "<Module>")
                    {
                        continue;
                    }

                    try
                    {
                        ScanType(module, handle, type, fullName, steps, skipped, tools, toolSources, templates, configurations, hooks, responses);
                    }
                    catch (BadImageFormatException ex)
                    {
                        _problems.Add(new LoadProblem("type-load-failed", $"Type '{fullName}' of '{module.FileName}' could not be read: {ex.Message}"));
                    }
                }
            }

            if (configurations.Count > 1)
            {
                _problems.Add(new LoadProblem("multiple-configurations",
                    $"A plugin can declare one IPluginConfiguration; this one has {configurations.Count}: {string.Join(", ", configurations.Select(c => c.TypeName))}."));
            }

            TypeContribution? extension = ChooseExtension();

            Duplicates(steps.Select(s => (s.Tag, s.TypeName)), "Step tag");
            Duplicates(hooks.Select(h => (h.Name, h.TypeName + "." + h.Method)), "Hook name");
            Duplicates(responses.Select(r => (r.Name, r.TypeName)), "Response type name");

            PluginContributions contributions = new()
            {
                Assemblies = [.. _own.Select(m => m.FileName)],
                Steps = [.. steps.OrderBy(s => s.Tag, StringComparer.Ordinal)],
                Skipped = skipped,
                Tools = tools,
                ToolSources = toolSources,
                Hooks = [.. hooks.OrderBy(h => h.Name, StringComparer.Ordinal)],
                ResponseTypes = [.. responses.OrderBy(r => r.Name, StringComparer.Ordinal)],
                TemplateSources = templates,
                Configuration = configurations.Count == 1 ? configurations[0] : null,
                Extensions = [.. _extensions.Select(e => e.TypeName)],
                Extension = extension
            };
            return new PluginScanResult(contributions, _problems);
        }

        /// <summary>The plugin's entry point among the <c>IExtension</c> classes found; null when it has none (or the problem says why none can be used).</summary>
        private TypeContribution? ChooseExtension()
        {
            if (_extensions.Count == 0)
            {
                if (_extensionType != null)
                {
                    _problems.Add(new LoadProblem("extension-not-found",
                        $"The manifest names the extension '{_extensionType}', which is not a public concrete IExtension with a public parameterless constructor in the plugin's own assemblies."));
                }
                else if (_badExtensions.Count > 0)
                {
                    _problems.Add(new LoadProblem("invalid-extension",
                        $"'{string.Join("', '", _badExtensions)}' implements IExtension but is not public, concrete and non-generic with a public parameterless constructor: the plugin cannot be driven by it."));
                }

                return null;
            }

            foreach (string bad in _badExtensions)
            {
                _problems.Add(new LoadProblem("ignored-extension", $"'{bad}' implements IExtension but cannot be created (not public or no public parameterless constructor): it is ignored."));
            }

            if (_extensionType != null)
            {
                TypeContribution? named = _extensions.FirstOrDefault(e => string.Equals(e.TypeName, _extensionType, StringComparison.Ordinal));
                if (named == null)
                {
                    _problems.Add(new LoadProblem("extension-not-found",
                        $"The manifest names the extension '{_extensionType}', which is not among the plugin's IExtension classes ({string.Join(", ", _extensions.Select(e => e.TypeName))})."));
                }

                return named;
            }

            if (_extensions.Count > 1)
            {
                _problems.Add(new LoadProblem("multiple-extensions",
                    $"A plugin is driven by one IExtension; this one has {_extensions.Count}: {string.Join(", ", _extensions.Select(e => e.TypeName))}. Name the entry point in plugin.json (\"extension\": \"Full.Type.Name\")."));
                return null;
            }

            return _extensions[0];
        }

        private void Duplicates(IEnumerable<(string Name, string Owner)> items, string what)
        {
            foreach (IGrouping<string, (string Name, string Owner)> group in items.GroupBy(i => i.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                _problems.Add(new LoadProblem("collision", $"{what} '{group.Key}' is declared twice in the plugin ({string.Join(" and ", group.Select(g => g.Owner))})."));
            }
        }

        private void ScanType(
            Module module, TypeDefinitionHandle handle, TypeDefinition type, string fullName,
            List<StepContribution> steps, List<SkippedStep> skipped, List<TypeContribution> tools, List<TypeContribution> toolSources,
            List<TypeContribution> templates, List<TypeContribution> configurations, List<HookContribution> hooks,
            List<ResponseTypeContribution> responses)
        {
            MetadataReader reader = module.Reader;
            TypeAttributes attributes = type.Attributes;
            bool isInterface = (attributes & TypeAttributes.Interface) != 0;
            bool isAbstract = (attributes & TypeAttributes.Abstract) != 0;
            bool isStatic = isAbstract && (attributes & TypeAttributes.Sealed) != 0;
            bool isGeneric = type.GetGenericParameters().Count > 0;
            bool isPublic = module.IsPublic(handle);
            MetadataRef reference = new(module.Path, MetadataTokens.GetToken(handle));
            HashSet<TypeKey> implemented = Implemented(new TypeKey(module.AssemblyName, fullName));
            bool concrete = !isInterface && !isAbstract && !isGeneric && isPublic;
            if (implemented.FirstOrDefault(k => k.Assembly == Unresolved) is { FullName: { } missing } && isPublic)
            {
                // At run time this type would not load: a base type or an interface of it is in an assembly nobody ships.
                _problems.Add(new LoadProblem("type-load-failed",
                    $"Type '{fullName}' derives from or implements a type of '{missing}', which neither the plugin nor the host has: it is left out."));
                return;
            }

            if (implemented.Contains(IStepKey))
            {
                // An abstract class, an interface or an open generic is a base of the plugin, not a step that was left out: nothing to report.
                // Only a concrete class that could have been a step and was discarded for a reason the author can act on is skipped.
                if (!isInterface && !isAbstract && !isGeneric && !isPublic)
                {
                    skipped.Add(new SkippedStep(fullName, "not-public", $"'{fullName}' is not public."));
                }
                else if (concrete)
                {
                    string? tag = DeclaredTag(module, type);
                    steps.Add(new StepContribution(tag ?? fullName, fullName, tag != null) { Ref = reference });
                }
            }

            if (implemented.Contains(IExtensionKey) && !isInterface && !isAbstract && !isGeneric)
            {
                if (concrete && HasPublicParameterlessConstructor(reader, type))
                {
                    _extensions.Add(new TypeContribution(fullName) { Ref = reference });
                }
                else
                {
                    _badExtensions.Add(fullName);
                }
            }

            if (implemented.Contains(IToolKey) && concrete && HasPublicParameterlessConstructor(reader, type))
            {
                tools.Add(new TypeContribution(fullName) { Ref = reference });
            }

            Contract(IToolSourceKey, "IToolSource", toolSources);
            Contract(ITemplateSourceKey, "ITemplateSource", templates);
            Contract(IConfigurationKey, "IPluginConfiguration", configurations);

            foreach (CustomAttributeHandle attributeHandle in type.GetCustomAttributes())
            {
                CustomAttribute attribute = reader.GetCustomAttribute(attributeHandle);
                if (AttributeKey(reader, module.AssemblyName, attribute) != ResponseTypeKey)
                {
                    continue;
                }

                if (!isPublic || isInterface || isAbstract || isGeneric || (attributes & TypeAttributes.ClassSemanticsMask) != TypeAttributes.Class || IsValueType(module, type))
                {
                    _problems.Add(new LoadProblem("invalid-response-type", $"A response type must be a public, non-static, non-abstract, non-generic class: '{fullName}' is not; it is ignored."));
                    break;
                }

                string? name = attribute.DecodeValue(StringTypeProvider.Instance).FixedArguments is [{ Value: string given }] && given.Trim().Length > 0 ? given.Trim() : null;
                responses.Add(new ResponseTypeContribution(name ?? reader.GetString(type.Name), fullName) { Ref = reference });
                break;
            }

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                foreach (CustomAttributeHandle attributeHandle in method.GetCustomAttributes())
                {
                    CustomAttribute attribute = reader.GetCustomAttribute(attributeHandle);
                    TypeKey? key = AttributeKey(reader, module.AssemblyName, attribute);
                    HookKind? kind = key == HookKey ? HookKind.Hook : key == ValidatorKey ? HookKind.StepValidator : key == ResultValidatorKey ? HookKind.ResultValidator : null;
                    if (kind == null)
                    {
                        continue;
                    }

                    string methodName = reader.GetString(method.Name);
                    bool publicStatic = (method.Attributes & MethodAttributes.Static) != 0 && (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                    if (!publicStatic || !isPublic || method.GetGenericParameters().Count > 0 || isGeneric)
                    {
                        _problems.Add(new LoadProblem("invalid-hook", $"A hook must be a public static, non-generic method of a public class: '{fullName}.{methodName}' is not; it is ignored."));
                        break;
                    }

                    CustomAttributeValue<string?> value = attribute.DecodeValue(StringTypeProvider.Instance);
                    string? Named(string property) => value.NamedArguments.FirstOrDefault(a => a.Name == property).Value as string;
                    string? given = kind == HookKind.Hook ? Named("Name")?.Trim() : null;
                    string name = string.IsNullOrEmpty(given) ? module.SimpleNameChain(handle) + "." + methodName : given;
                    hooks.Add(new HookContribution(name, kind.Value, fullName, methodName, Named("Description"))
                    {
                        Ref = new MetadataRef(module.Path, MetadataTokens.GetToken(methodHandle))
                    });
                    break;
                }
            }

            void Contract(TypeKey contract, string label, List<TypeContribution> list)
            {
                // An abstract base class of the plugin is not a contribution, only its concrete subclasses are.
                if (!implemented.Contains(contract) || isInterface || isAbstract)
                {
                    return;
                }

                if (!concrete || !HasPublicParameterlessConstructor(reader, type))
                {
                    _problems.Add(new LoadProblem("invalid-contribution",
                        $"'{fullName}' implements {label} but is not a public, concrete, non-generic class with a public parameterless constructor; it is ignored."));
                    return;
                }

                list.Add(new TypeContribution(fullName) { Ref = reference });
            }
        }

        private bool IsValueType(Module module, TypeDefinition type)
            => !type.BaseType.IsNil && module.KeyOf(type.BaseType) is { } baseKey && baseKey.FullName is "System.ValueType" or "System.Enum";

        /// <summary>The interfaces <paramref name="key"/> is or implements (through its bases), as far as they can be read.</summary>
        private HashSet<TypeKey> Implemented(TypeKey key)
        {
            if (_implemented.TryGetValue(key, out HashSet<TypeKey>? known))
            {
                return known;
            }

            if (!_inProgress.Add(key))
            {
                return [];
            }

            HashSet<TypeKey> result = [];
            try
            {
                if (!IsPlatform(key.Assembly) && SharedAssemblySet.HostWins(key.Assembly))
                {
                    _ = AddFromHost(key, result);
                }
                else if (Open(key.Assembly) is { } module && module.Find(key.FullName) is { } handle)
                {
                    TypeDefinition type = module.Reader.GetTypeDefinition(handle);
                    if ((type.Attributes & TypeAttributes.Interface) != 0)
                    {
                        result.Add(key);
                    }

                    foreach (InterfaceImplementationHandle implementation in type.GetInterfaceImplementations())
                    {
                        if (module.KeyOf(module.Reader.GetInterfaceImplementation(implementation).Interface) is { } iface)
                        {
                            result.Add(iface);
                            result.UnionWith(Implemented(iface));
                        }
                    }

                    if (!type.BaseType.IsNil && module.KeyOf(type.BaseType) is { } baseKey)
                    {
                        result.UnionWith(Implemented(baseKey));
                    }
                }
                else if (!IsPlatform(key.Assembly) && !AddFromHost(key, result, loadedOnly: true))
                {
                    // Not shipped with the plugin and not in the host: marked, so the type that needs it is reported instead of silently missed.
                    result.Add(new TypeKey(Unresolved, key.Assembly));
                }
            }
            catch (BadImageFormatException)
            {
                // A dependency that cannot be read: what it implements is unknown.
            }
            finally
            {
                _inProgress.Remove(key);
            }

            _implemented[key] = result;
            return result;
        }

        private Module? Open(string assemblyName)
        {
            if (_modules.TryGetValue(assemblyName, out Module? module))
            {
                return module;
            }

            module = null;
            if (_dependencyPaths.TryGetValue(assemblyName, out string? path) && File.Exists(path))
            {
                try
                {
                    module = Module.Open(path);
                }
                catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    module = null;
                }
            }

            return _modules[assemblyName] = module;
        }

        public void Dispose()
        {
            foreach (Module? module in _modules.Values)
            {
                module?.Dispose();
            }

            foreach (Module module in _own)
            {
                module.Dispose();
            }
        }
    }

    /// <summary>Platform assemblies: none of their types implements a plugin contract, so they are not looked at.</summary>
    private static bool IsPlatform(string assembly)
        => assembly is "mscorlib" or "netstandard" or "System" || assembly.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || assembly.StartsWith("Microsoft.NETCore.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Marker assembly of a <see cref="TypeKey"/> that stands for «a type of an assembly that cannot be found» (its name in <c>FullName</c>).</summary>
    private const string Unresolved = "?";

    /// <summary>The interfaces of a host type (host code, already part of the process or of its shared set); false when the host does not have the type.</summary>
    private static bool AddFromHost(TypeKey key, HashSet<TypeKey> result, bool loadedOnly = false)
    {
        Assembly? assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, key.Assembly, StringComparison.OrdinalIgnoreCase));
        if (assembly == null && !loadedOnly)
        {
            try
            {
                assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(key.Assembly));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                return false;
            }
        }

        if (assembly?.GetType(key.FullName, throwOnError: false) is not { } type)
        {
            return false;
        }

        if (type.IsInterface)
        {
            result.Add(key);
        }

        foreach (Type iface in type.GetInterfaces())
        {
            result.Add(KeyOf(iface));
        }

        return true;
    }

    private static TypeKey KeyOf(Type type)
    {
        Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        return new TypeKey(definition.Assembly.GetName().Name ?? string.Empty, definition.FullName ?? definition.Name);
    }

    private static TypeKey? AttributeKey(MetadataReader reader, string ownAssembly, CustomAttribute attribute)
    {
        EntityHandle parent = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default
        };
        return parent.IsNil ? null : new Module.Keys(reader, ownAssembly).KeyOf(parent);
    }

    private static string? DeclaredTag(Module module, TypeDefinition type)
    {
        foreach (CustomAttributeHandle handle in type.GetCustomAttributes())
        {
            CustomAttribute attribute = module.Reader.GetCustomAttribute(handle);
            if (AttributeKey(module.Reader, module.AssemblyName, attribute) == StepTagKey
                && attribute.DecodeValue(StringTypeProvider.Instance).FixedArguments is [{ Value: string tag }]
                && !string.IsNullOrWhiteSpace(tag))
            {
                return tag;
            }
        }

        return null;
    }

    private static bool HasPublicParameterlessConstructor(MetadataReader reader, TypeDefinition type)
    {
        foreach (MethodDefinitionHandle handle in type.GetMethods())
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            if (reader.GetString(method.Name) != ".ctor" || (method.Attributes & MethodAttributes.Static) != 0
                || (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
            {
                continue;
            }

            BlobReader blob = reader.GetBlobReader(method.Signature);
            SignatureHeader header = blob.ReadSignatureHeader();
            if (header.IsGeneric)
            {
                blob.ReadCompressedInteger();
            }

            if (blob.ReadCompressedInteger() == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A type by assembly simple name and full name (nested types with <c>+</c>, generic definitions with their arity).</summary>
    private readonly record struct TypeKey(string Assembly, string FullName);

    /// <summary>One assembly read from bytes (no file is kept open, nothing is loaded).</summary>
    private sealed class Module : IDisposable
    {
        private readonly PEReader _pe;
        private Dictionary<string, TypeDefinitionHandle>? _types;

        private Module(string path, PEReader pe)
        {
            Path = path;
            _pe = pe;
            Reader = pe.GetMetadataReader();
            AssemblyName = Reader.GetString(Reader.GetAssemblyDefinition().Name);
        }

        public string Path { get; }

        public string FileName => System.IO.Path.GetFileName(Path);

        public MetadataReader Reader { get; }

        public string AssemblyName { get; }

        public static Module Open(string path)
        {
            PEReader pe = new(ImmutableArray.Create(File.ReadAllBytes(path)));
            if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly)
            {
                pe.Dispose();
                throw new BadImageFormatException("The file has no .NET assembly metadata.");
            }

            return new Module(path, pe);
        }

        public TypeDefinitionHandle? Find(string fullName)
        {
            _types ??= Reader.TypeDefinitions.ToDictionary(FullNameOf, h => h, StringComparer.Ordinal);
            return _types.TryGetValue(fullName, out TypeDefinitionHandle handle) ? handle : null;
        }

        public string FullNameOf(TypeDefinitionHandle handle)
        {
            TypeDefinition type = Reader.GetTypeDefinition(handle);
            string name = Reader.GetString(type.Name);
            TypeDefinitionHandle declaring = type.GetDeclaringType();
            if (!declaring.IsNil)
            {
                return FullNameOf(declaring) + "+" + name;
            }

            string ns = Reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        /// <summary><c>Outer.Inner</c>: the simple names of the type and its declaring types (the hook naming rule; no namespace).</summary>
        public string SimpleNameChain(TypeDefinitionHandle handle)
        {
            TypeDefinition type = Reader.GetTypeDefinition(handle);
            string name = Reader.GetString(type.Name);
            TypeDefinitionHandle declaring = type.GetDeclaringType();
            return declaring.IsNil ? name : SimpleNameChain(declaring) + "." + name;
        }

        public bool IsPublic(TypeDefinitionHandle handle)
        {
            TypeDefinition type = Reader.GetTypeDefinition(handle);
            TypeAttributes visibility = type.Attributes & TypeAttributes.VisibilityMask;
            TypeDefinitionHandle declaring = type.GetDeclaringType();
            return declaring.IsNil ? visibility == TypeAttributes.Public : visibility == TypeAttributes.NestedPublic && IsPublic(declaring);
        }

        public TypeKey? KeyOf(EntityHandle handle) => new Keys(Reader, AssemblyName).KeyOf(handle);

        public void Dispose() => _pe.Dispose();

        /// <summary>Turns a type handle of a reader into a <see cref="TypeKey"/>.</summary>
        public readonly struct Keys(MetadataReader reader, string ownAssembly)
        {
            public TypeKey? KeyOf(EntityHandle handle)
            {
                switch (handle.Kind)
                {
                    case HandleKind.TypeDefinition:
                        return new TypeKey(ownAssembly, DefinitionName((TypeDefinitionHandle)handle));
                    case HandleKind.TypeReference:
                        return ReferenceKey((TypeReferenceHandle)handle);
                    case HandleKind.TypeSpecification:
                        TypeSpecification specification = reader.GetTypeSpecification((TypeSpecificationHandle)handle);
                        EntityHandle? generic = specification.DecodeSignature(GenericDefinitionProvider.Instance, null);
                        return generic is { } definition && definition.Kind != HandleKind.TypeSpecification ? KeyOf(definition) : null;
                    default:
                        return null;
                }
            }

            private string DefinitionName(TypeDefinitionHandle handle)
            {
                TypeDefinition type = reader.GetTypeDefinition(handle);
                string name = reader.GetString(type.Name);
                TypeDefinitionHandle declaring = type.GetDeclaringType();
                if (!declaring.IsNil)
                {
                    return DefinitionName(declaring) + "+" + name;
                }

                string ns = reader.GetString(type.Namespace);
                return ns.Length == 0 ? name : ns + "." + name;
            }

            private TypeKey? ReferenceKey(TypeReferenceHandle handle)
            {
                TypeReference reference = reader.GetTypeReference(handle);
                string name = reader.GetString(reference.Name);
                EntityHandle scope = reference.ResolutionScope;
                switch (scope.Kind)
                {
                    case HandleKind.TypeReference:
                        return ReferenceKey((TypeReferenceHandle)scope) is { } outer ? outer with { FullName = outer.FullName + "+" + name } : null;
                    case HandleKind.AssemblyReference:
                        AssemblyReference assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
                        return new TypeKey(reader.GetString(assembly.Name), Qualified(reader.GetString(reference.Namespace), name));
                    default:
                        // Module (or nil, meaning this module): a type of this assembly.
                        return new TypeKey(ownAssembly, Qualified(reader.GetString(reference.Namespace), name));
                }
            }

            private static string Qualified(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
        }
    }

    /// <summary>Decodes a type specification to the handle of its generic definition (null for anything else).</summary>
    private sealed class GenericDefinitionProvider : ISignatureTypeProvider<EntityHandle?, object?>
    {
        public static GenericDefinitionProvider Instance { get; } = new();

        public EntityHandle? GetGenericInstantiation(EntityHandle? genericType, ImmutableArray<EntityHandle?> typeArguments) => genericType;

        public EntityHandle? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => handle;

        public EntityHandle? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => handle;

        public EntityHandle? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => null;

        public EntityHandle? GetArrayType(EntityHandle? elementType, ArrayShape shape) => null;

        public EntityHandle? GetByReferenceType(EntityHandle? elementType) => null;

        public EntityHandle? GetPointerType(EntityHandle? elementType) => null;

        public EntityHandle? GetSZArrayType(EntityHandle? elementType) => null;

        public EntityHandle? GetPinnedType(EntityHandle? elementType) => null;

        public EntityHandle? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;

        public EntityHandle? GetFunctionPointerType(MethodSignature<EntityHandle?> signature) => null;

        public EntityHandle? GetGenericMethodParameter(object? genericContext, int index) => null;

        public EntityHandle? GetGenericTypeParameter(object? genericContext, int index) => null;

        public EntityHandle? GetModifiedType(EntityHandle? modifier, EntityHandle? unmodifiedType, bool isRequired) => unmodifiedType;
    }

    /// <summary>Decodes attribute blobs whose arguments are strings (the only ones read: <c>[StepTag]</c>, <c>[Hook]</c>, <c>[ResponseType]</c>, informational version).</summary>
    private sealed class StringTypeProvider : ICustomAttributeTypeProvider<string?>
    {
        public static StringTypeProvider Instance { get; } = new();

        public string? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string? GetSystemType() => "System.Type";

        public string? GetSZArrayType(string? elementType) => elementType + "[]";

        public string? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);

        public string? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);

        public string? GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string? type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(string? type) => type == "System.Type";
    }
}
