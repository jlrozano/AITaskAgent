using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AITaskAgent.CSharp;

/// <summary>How a <see cref="CSharpUnitCompiler"/> compiles and where it keeps what it emitted.</summary>
public sealed record CSharpCompileOptions
{
    /// <summary>Folder where emitted assemblies are cached by hash (<c>&lt;hash&gt;.dll</c>, <c>.pdb</c>, <c>.json</c>); null keeps them in memory only.</summary>
    public string? CacheDirectory { get; init; }

    /// <summary>Assemblies (files) the code may use besides the allowlist: what a host that resolves NuGet packages for a package adds.</summary>
    public IReadOnlyList<string> AdditionalReferences { get; init; } = [];

    /// <summary>Folders where the native libraries of <see cref="AdditionalReferences"/> are (probed by the unit's load context); the host decides which runtime identifier applies.</summary>
    public IReadOnlyList<string> NativeLibraryDirectories { get; init; } = [];

    /// <summary>
    /// Tells the unit's load context which assembly names always come from the host (it never loads them again, whatever file a reference
    /// to them has, so their types are the host's). Null = only <c>AITaskAgent.*</c>. The host adds to that by passing its own predicate.
    /// </summary>
    public Func<string, bool>? IsSharedAssembly { get; init; }

    /// <summary>Emitted assemblies kept in memory (the oldest is dropped past this number).</summary>
    public int MaxCachedBuilds { get; init; } = 64;
}

/// <summary>
/// Compiles the unit of a package: the bodies of its <c>CSharpStep</c>s as the static methods of one generated class, next to
/// its shared declarations and the classes of its schemas, in one assembly (see architecture §15). Nothing of the author's code
/// runs while compiling or loading. The emitted assembly is cached by the hash of the sources, in memory and, with
/// <see cref="CSharpCompileOptions.CacheDirectory"/>, on disk; each call returns its own <see cref="CSharpUnit"/>.
/// </summary>
public sealed partial class CSharpUnitCompiler(CSharpCompileOptions? options = null)
{
    private const string Namespace = "Workflow";

    private static readonly ConcurrentDictionary<string, Lazy<Task<Build>>> Memory = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> Order = new();

    private readonly CSharpCompileOptions _options = options ?? new CSharpCompileOptions();

    /// <summary>Forgets every emitted assembly kept in memory (tests use it to exercise the disk cache).</summary>
    internal static void ClearMemoryForTests()
    {
        Memory.Clear();
        Order.Clear();
    }

    private sealed record Build(byte[]? Assembly, byte[]? Symbols, IReadOnlyList<CSharpDiagnostic> Diagnostics, bool FromDisk = false);

    /// <summary>Compiles <paramref name="sources"/> (or finds the compiled assembly) and loads it.</summary>
    public async Task<UnitCompilation> CompileAsync(CSharpUnitSources sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Stopwatch watch = Stopwatch.StartNew();
        if (sources.IsEmpty)
        {
            return new UnitCompilation(string.Empty, [], CSharpUnit.Empty, UnitCacheSource.None, watch.Elapsed);
        }

        string environment = UnitReferences.Id + "|" + string.Join(';', _options.AdditionalReferences.Select(ReferenceId)) + "|" + string.Join(';', _options.NativeLibraryDirectories);
        string hash = sources.Hash(environment);

        UnitCacheSource source = UnitCacheSource.Memory;
        Lazy<Task<Build>> entry = Memory.GetOrAdd(hash, _ =>
        {
            source = UnitCacheSource.None;
            return new Lazy<Task<Build>>(() => BuildAsync(sources, hash, cancellationToken));
        });

        Build build;
        try
        {
            build = await entry.Value;
        }
        catch
        {
            Memory.TryRemove(hash, out _);
            throw;
        }

        if (source == UnitCacheSource.None)
        {
            Remember(hash);
        }

        if (source == UnitCacheSource.None && build.FromDisk)
        {
            source = UnitCacheSource.Disk;
        }

        if (build.Assembly == null || build.Symbols == null)
        {
            return new UnitCompilation(hash, build.Diagnostics, null, source, watch.Elapsed);
        }

        try
        {
            CSharpUnit unit = CSharpUnit.Load(hash, build.Assembly, build.Symbols, _options.AdditionalReferences, _options.NativeLibraryDirectories, _options.IsSharedAssembly);
            return new UnitCompilation(hash, build.Diagnostics, unit, source, watch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            List<CSharpDiagnostic> failed = [.. build.Diagnostics, new CSharpDiagnostic("WFCS007", CSharpSeverity.Error, $"The compiled unit could not be loaded: {ex.Message}")];
            return new UnitCompilation(hash, failed, null, source, watch.Elapsed);
        }
    }

    private static string ReferenceId(string path)
    {
        FileInfo info = new(path);
        return $"{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private void Remember(string hash)
    {
        Order.Enqueue(hash);
        while (Order.Count > Math.Max(1, _options.MaxCachedBuilds) && Order.TryDequeue(out string? old))
        {
            Memory.TryRemove(old, out _);
        }
    }

    private async Task<Build> BuildAsync(CSharpUnitSources sources, string hash, CancellationToken cancellationToken)
    {
        if (ReadDisk(hash) is { } cached)
        {
            return cached;
        }

        Build built = await Task.Run(() => CompileCoreAsync(sources, hash, cancellationToken), cancellationToken);
        if (built.Assembly != null && built.Symbols != null)
        {
            WriteDisk(hash, built);
        }

        return built;
    }

    // ── Compilation ──────────────────────────────────────────────────────────

    /// <summary>What <see cref="CreateCompilationAsync"/> built: the compilation and where each part of the sources is in it.</summary>
    private sealed record UnitTrees(
        CSharpCompilation Compilation, IReadOnlyList<SchemaClass> Classes, IReadOnlyList<CSharpDiagnostic> SchemaWarnings,
        SyntaxTree? Shared, SyntaxTree StepsTree, List<StepRegion> Regions);

    /// <summary>
    /// The compilation of a unit (usings, schema classes, shared file, generated steps, references): one place, so what is emitted and what
    /// the completion of the editor binds against cannot drift. <paramref name="forEditor"/> keeps a body's trailing empty lines and an empty
    /// shared file, so a caret there still has a position.
    /// </summary>
    private async Task<UnitTrees> CreateCompilationAsync(CSharpUnitSources sources, string assemblyName, bool forEditor = false)
    {
        (IReadOnlyList<SchemaClass> classes, IReadOnlyList<CSharpDiagnostic> schemaWarnings) = await SchemaClasses.GenerateAsync(sources.Schemas);

        CSharpParseOptions parse = new(LanguageVersion.Latest);
        List<SyntaxTree> trees = [];
        trees.Add(Parse(Usings(classes), "generated/Usings.g.cs", parse));
        foreach (SchemaClass schema in classes)
        {
            trees.Add(Parse("#nullable disable\n" + schema.Code, $"generated/schema.{schema.SchemaName}.cs", parse));
        }

        SyntaxTree? shared = null;
        if (forEditor ? sources.Shared != null : !string.IsNullOrWhiteSpace(sources.Shared))
        {
            shared = Parse(sources.Shared!, CSharpUnitSources.SharedPath, parse);
            trees.Add(shared);
        }

        (string stepsText, List<StepRegion> regions) = GenerateSteps(sources.Steps, trimBodies: !forEditor);
        SyntaxTree stepsTree = Parse(stepsText, "generated/Steps.g.cs", parse);
        trees.Add(stepsTree);

        IEnumerable<MetadataReference> references = _options.AdditionalReferences.Count == 0
            ? UnitReferences.Default
            : [.. UnitReferences.Default, .. UnitReferences.FromFiles(_options.AdditionalReferences)];

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true));
        return new UnitTrees(compilation, classes, schemaWarnings, shared, stepsTree, regions);
    }

    private async Task<Build> CompileCoreAsync(CSharpUnitSources sources, string hash, CancellationToken cancellationToken)
    {
        UnitTrees trees = await CreateCompilationAsync(sources, "Workflow_" + hash[..12]);
        using MemoryStream assembly = new();
        using MemoryStream symbols = new();
        EmitResult emitted = trees.Compilation.Emit(assembly, symbols, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb), cancellationToken: cancellationToken);
        List<CSharpDiagnostic> ordered = Collect(trees, emitted, sources);
        bool failed = !emitted.Success || ordered.Any(d => d.IsError);
        return failed
            ? new Build(null, null, ordered)
            : new Build(assembly.ToArray(), symbols.ToArray(), ordered);
    }

    /// <summary>
    /// Checks <paramref name="sources"/> exactly as <see cref="CompileAsync"/> would compile them (same references, usings, schema classes and
    /// unit rules) and returns only the diagnostics: the assembly is emitted into memory and dropped, never loaded, never cached (in memory or on
    /// disk), so nothing of the author's code runs and nothing is written. What the designer's «Comprobar» button uses for text that is not saved.
    /// Additive (2026-10-24).
    /// </summary>
    public async Task<IReadOnlyList<CSharpDiagnostic>> CheckAsync(CSharpUnitSources sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.IsEmpty)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        UnitTrees trees = await CreateCompilationAsync(sources, "Workflow_check");
        return await Task.Run(() =>
        {
            using MemoryStream assembly = new();
            EmitResult emitted = trees.Compilation.Emit(assembly, cancellationToken: cancellationToken);
            return (IReadOnlyList<CSharpDiagnostic>)Collect(trees, emitted, sources);
        }, cancellationToken);
    }

    /// <summary>The diagnostics of an emit plus the unit's own rules, in the order <see cref="UnitCompilation.Diagnostics"/> gives them.</summary>
    private static List<CSharpDiagnostic> Collect(UnitTrees trees, EmitResult emitted, CSharpUnitSources sources)
    {
        List<CSharpDiagnostic> diagnostics = [.. trees.SchemaWarnings];
        foreach (Diagnostic diagnostic in emitted.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && !d.IsSuppressed))
        {
            if (ToDiagnostic(diagnostic, trees.StepsTree, trees.Regions, sources.Steps) is { } converted)
            {
                diagnostics.Add(converted);
            }
        }

        if (trees.Shared != null)
        {
            CheckShared(trees.Compilation, trees.Shared, trees.Classes, sources.Schemas.Keys, diagnostics);
        }

        return [.. diagnostics
            .OrderByDescending(d => d.IsError)
            .ThenBy(d => d.File, StringComparer.Ordinal)
            .ThenBy(d => d.Line ?? 0)
            .ThenBy(d => d.Id, StringComparer.Ordinal)];
    }

    private static SyntaxTree Parse(string text, string path, CSharpParseOptions options)
        => CSharpSyntaxTree.ParseText(SourceText.From(text, Encoding.UTF8), options, path);

    private static string Usings(IReadOnlyList<SchemaClass> classes)
    {
        StringBuilder text = new();
        foreach (string ns in new[]
        {
            "System", "System.Collections.Generic", "System.Linq", "System.Threading", "System.Threading.Tasks",
            "Newtonsoft.Json", "Newtonsoft.Json.Linq",
            "AITaskAgent.Core.Abstractions", "AITaskAgent.Core.Models", "AITaskAgent.Core.Hooks", "AITaskAgent.CSharp", Namespace,
        })
        {
            text.Append("global using ").Append(ns).Append(";\n");
        }

        foreach (SchemaClass schema in classes)
        {
            text.Append("global using ").Append(schema.ClassName).Append(" = global::").Append(schema.Namespace).Append('.').Append(schema.ClassName).Append(";\n");
        }

        // `Workflow` must exist even when the author declares nothing in it.
        text.Append("namespace Workflow { internal static class WorkflowNamespaceAnchor { } }\n");
        return text.ToString();
    }

    // ── Generated steps ──────────────────────────────────────────────────────

    /// <summary>Where the method of one step lies in the generated text (1-based generated lines).</summary>
    private sealed record StepRegion(string StepId, int FirstLine, int MethodStart, int BodyStart, int BodyEnd, int MethodEnd);

    private static (string Text, List<StepRegion> Regions) GenerateSteps(IReadOnlyList<CSharpStepSource> steps, bool trimBodies = true)
    {
        List<string> lines =
        [
            "// <auto-generated/>",
            "#pragma warning disable CS1998",
            "#nullable enable",
            $"namespace {Namespace};",
            "public static partial class Steps",
            "{",
        ];
        List<StepRegion> regions = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (CSharpStepSource step in steps)
        {
            string method = MethodNameOf(step.StepId, names);
            string normalized = step.Code.Replace("\r\n", "\n", StringComparison.Ordinal);
            string[] code = (trimBodies ? normalized.TrimEnd('\n') : normalized).Split('\n');
            if (trimBodies && code.Length == 1 && code[0].Length == 0)
            {
                code = [];
            }

            lines.Add($"    [global::AITaskAgent.CSharp.StepMethod({SymbolDisplay.FormatLiteral(step.StepId, quote: true)})]");
            int methodStart = lines.Count;
            lines.Add($"    public static async global::System.Threading.Tasks.Task<global::AITaskAgent.Core.Abstractions.IStepResult> {method}(global::AITaskAgent.CSharp.StepCall call)");
            lines.Add("    {");
            lines.Add($"#line {Math.Max(1, step.FirstLine)} \"{CSharpUnitSources.WorkflowPath}\"");
            int bodyStart = lines.Count + 1;
            lines.AddRange(code);
            int bodyEnd = lines.Count;
            lines.Add("#line default");
            lines.Add("    }");
            regions.Add(new StepRegion(step.StepId, step.FirstLine, methodStart, bodyStart, bodyEnd, lines.Count));
        }

        lines.Add("}");
        return (string.Join('\n', lines) + "\n", regions);
    }

    /// <summary>The step id as a method name: non letters and digits become <c>_</c>, a digit first gets a <c>_</c>, a clash a number, a keyword an <c>@</c>.</summary>
    internal static string MethodNameOf(string stepId, HashSet<string> taken)
    {
        StringBuilder name = new();
        foreach (char c in stepId)
        {
            name.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        string baseName = name.Length == 0 ? "_" : name.ToString();
        if (char.IsDigit(baseName[0]))
        {
            baseName = "_" + baseName;
        }

        string candidate = baseName;
        for (int i = 2; !taken.Add(candidate); i++)
        {
            candidate = baseName + "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return SyntaxFacts.GetKeywordKind(candidate) != SyntaxKind.None ? "@" + candidate : candidate;
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    private static CSharpDiagnostic? ToDiagnostic(Diagnostic diagnostic, SyntaxTree stepsTree, List<StepRegion> regions, IReadOnlyList<CSharpStepSource> steps)
    {
        CSharpSeverity severity = diagnostic.Severity == DiagnosticSeverity.Error ? CSharpSeverity.Error : CSharpSeverity.Warning;
        string message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        Location location = diagnostic.Location;
        if (!location.IsInSource)
        {
            return new CSharpDiagnostic(diagnostic.Id, severity, message);
        }

        string path = location.SourceTree?.FilePath ?? string.Empty;
        FileLinePositionSpan raw = location.GetLineSpan();

        if (path.StartsWith("generated/schema.", StringComparison.Ordinal))
        {
            // Warnings inside generated classes are noise for the author; an error there is a real problem with the schema.
            return severity == CSharpSeverity.Error
                ? new CSharpDiagnostic(diagnostic.Id, severity, message, "schema:" + path["generated/schema.".Length..^".cs".Length])
                : null;
        }

        if (path == CSharpUnitSources.SharedPath)
        {
            return new CSharpDiagnostic(diagnostic.Id, severity, message, path, raw.StartLinePosition.Line + 1, raw.StartLinePosition.Character + 1);
        }

        if (location.SourceTree == stepsTree)
        {
            int generated = raw.StartLinePosition.Line + 1;
            StepRegion? region = regions.FirstOrDefault(r => generated >= r.MethodStart && generated <= r.MethodEnd);
            if (region == null)
            {
                return new CSharpDiagnostic(diagnostic.Id, severity, message);
            }

            CSharpStepSource step = steps.First(s => s.StepId == region.StepId);
            FileLinePositionSpan mapped = location.GetMappedLineSpan();
            bool inBody = generated >= region.BodyStart && generated <= region.BodyEnd && mapped.HasMappedPath;
            if (inBody)
            {
                int codeLine = generated - region.BodyStart + 1;
                return new CSharpDiagnostic(
                    diagnostic.Id, severity, message, CSharpUnitSources.WorkflowPath,
                    step.FirstLine > 0 ? mapped.StartLinePosition.Line + 1 : null,
                    mapped.StartLinePosition.Character + 1, step.StepId, codeLine);
            }

            // Outside the body: a missing return is reported at the closing brace of the generated method.
            return new CSharpDiagnostic(
                diagnostic.Id, severity, $"{message} (in the method generated for the step: does every path of the code return an IStepResult?)",
                CSharpUnitSources.WorkflowPath, step.FirstLine > 0 ? step.FirstLine : null, null, step.StepId);
        }

        return new CSharpDiagnostic(diagnostic.Id, severity, message, path, raw.StartLinePosition.Line + 1, raw.StartLinePosition.Character + 1);
    }

    // ── Checks on the shared declarations (symbols; nothing is loaded) ───────

    private const string HookAttributeName = "AITaskAgent.Core.Hooks.HookAttribute";
    private const string ValidatorAttributeName = "AITaskAgent.Core.Hooks.ValidatorAttribute";
    private const string ResultValidatorAttributeName = "AITaskAgent.Core.Hooks.ResultValidatorAttribute";
    private const string ResponseTypeAttributeName = "AITaskAgent.Core.Hooks.ResponseTypeAttribute";

    private static void CheckShared(
        CSharpCompilation compilation, SyntaxTree shared, IReadOnlyList<SchemaClass> classes, IEnumerable<string> schemaNames, List<CSharpDiagnostic> diagnostics)
    {
        SemanticModel model = compilation.GetSemanticModel(shared);
        HashSet<string> classNames = new(classes.Select(c => c.ClassName), StringComparer.Ordinal);
        HashSet<string> schemas = new(schemaNames, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> responseNames = new(StringComparer.OrdinalIgnoreCase);

        CSharpDiagnostic At(string id, string message, SyntaxToken token)
        {
            FileLinePositionSpan span = token.GetLocation().GetLineSpan();
            return new CSharpDiagnostic(id, CSharpSeverity.Error, message, CSharpUnitSources.SharedPath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        }

        foreach (BaseTypeDeclarationSyntax declaration in shared.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type)
            {
                continue;
            }

            if (type.ContainingType == null && classNames.Contains(type.Name))
            {
                diagnostics.Add(At(
                    "WFCS002",
                    $"The type '{type.Name}' is defined here and also generated from the schema of the same name (schemas/*.json): a name defined both ways is not allowed. Rename the class or the schema.",
                    declaration.Identifier));
            }

            AttributeData? response = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ResponseTypeAttributeName);
            if (response == null)
            {
                continue;
            }

            string name = response.ConstructorArguments.Length > 0 && response.ConstructorArguments[0].Value is string given && given.Length > 0 ? given : type.Name;
            if (schemas.Contains(name))
            {
                diagnostics.Add(At(
                    "WFCS005",
                    $"Response type '{name}' is defined both as the class '{type.Name}' and as the schema schemas/{name}.json: a name defined both ways is not allowed.",
                    declaration.Identifier));
            }
            else if (responseNames.TryGetValue(name, out string? first))
            {
                diagnostics.Add(At("WFCS006", $"Response type '{name}' is declared twice ('{first}' and '{type.Name}').", declaration.Identifier));
            }
            else
            {
                responseNames[name] = type.Name;
            }

            if (!IsPublicChain(type) || type.IsStatic || type.IsAbstract || type.TypeKind != TypeKind.Class)
            {
                diagnostics.Add(At("WFCS001", $"A response type must be a public, non-static, non-abstract class: '{type.Name}' is not.", declaration.Identifier));
            }
        }

        foreach (MethodDeclarationSyntax declaration in shared.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(declaration) is not IMethodSymbol method)
            {
                continue;
            }

            foreach (AttributeData attribute in method.GetAttributes())
            {
                string? attributeName = attribute.AttributeClass?.ToDisplayString();
                if (attributeName is not (HookAttributeName or ValidatorAttributeName or ResultValidatorAttributeName))
                {
                    continue;
                }

                string kind = attributeName == HookAttributeName ? "hook" : attributeName == ValidatorAttributeName ? "validator" : "result validator";
                if (!method.IsStatic || method.DeclaredAccessibility != Accessibility.Public || !IsPublicChain(method.ContainingType))
                {
                    diagnostics.Add(At("WFCS001", $"A {kind} must be a public static method of a public class: '{method.Name}' is not.", declaration.Identifier));
                    continue;
                }

                if (attributeName != HookAttributeName && !IsValidatorShape(method, attributeName == ValidatorAttributeName ? "AITaskAgent.Core.Hooks.ValidateArgs" : "AITaskAgent.Core.Hooks.ResultValidateArgs"))
                {
                    string args = attributeName == ValidatorAttributeName ? "ValidateArgs" : "ResultValidateArgs";
                    diagnostics.Add(At("WFCS001", $"A {kind} must have the signature Task<(bool IsValid, string? Error)> {method.Name}({args} args).", declaration.Identifier));
                }
            }
        }
    }

    private static bool IsPublicChain(INamedTypeSymbol? type)
    {
        for (INamedTypeSymbol? current = type; current != null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidatorShape(IMethodSymbol method, string argsType)
        => method.Parameters.Length == 1
           && method.Parameters[0].Type.ToDisplayString() == argsType
           && method.ReturnType is INamedTypeSymbol { Name: "Task", TypeArguments: [ITypeSymbol result] } task
           && task.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
           && result is INamedTypeSymbol { IsTupleType: true, TupleElements: [{ Type.SpecialType: SpecialType.System_Boolean }, { Type.SpecialType: SpecialType.System_String }] };

    // ── Disk cache ───────────────────────────────────────────────────────────

    private Build? ReadDisk(string hash)
    {
        if (_options.CacheDirectory is not { } folder)
        {
            return null;
        }

        string stem = Path.Combine(folder, hash);
        try
        {
            if (File.Exists(stem + ".dll") && File.Exists(stem + ".pdb") && File.Exists(stem + ".json"))
            {
                List<CSharpDiagnostic>? diagnostics = JsonSerializer.Deserialize<List<CSharpDiagnostic>>(File.ReadAllText(stem + ".json"));
                if (diagnostics != null)
                {
                    return new Build(File.ReadAllBytes(stem + ".dll"), File.ReadAllBytes(stem + ".pdb"), diagnostics, FromDisk: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken cache entry is compiled again.
        }

        return null;
    }

    private void WriteDisk(string hash, Build build)
    {
        if (_options.CacheDirectory is not { } folder)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            string stem = Path.Combine(folder, hash);
            // The .json goes last: it is what makes an entry complete.
            WriteAtomic(stem + ".dll", build.Assembly!);
            WriteAtomic(stem + ".pdb", build.Symbols!);
            WriteAtomic(stem + ".json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(build.Diagnostics)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimization.
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
