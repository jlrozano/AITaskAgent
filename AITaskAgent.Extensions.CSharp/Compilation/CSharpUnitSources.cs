using System.Security.Cryptography;
using System.Text;

namespace AITaskAgent.CSharp;

/// <summary>The body of one <c>CSharpStep</c> and where it lies in the YAML.</summary>
/// <param name="StepId">The step's id (the unit's method for it is named after it).</param>
/// <param name="Code">The text of <c>code</c>: the body of the method.</param>
/// <param name="FirstLine">1-based line of the YAML where the first line of <paramref name="Code"/> is; 0 when unknown (diagnostics then carry no YAML line).</param>
public sealed record CSharpStepSource(string StepId, string Code, int FirstLine = 0);

/// <summary>Everything a package contributes to its unit.</summary>
/// <param name="Steps">The <c>CSharpStep</c>s of the workflow, nested ones included.</param>
/// <param name="Shared">The text of <c>csharp/shared.cs</c>, or null.</param>
/// <param name="Schemas">The package's schemas, name to JSON (each becomes a class).</param>
public sealed record CSharpUnitSources(
    IReadOnlyList<CSharpStepSource> Steps, string? Shared, IReadOnlyDictionary<string, string> Schemas)
{
    /// <summary>The path diagnostics of a step body carry (the YAML the body was written in).</summary>
    public const string WorkflowPath = "workflow.yaml";

    /// <summary>The path of the shared declarations (also the zip entry).</summary>
    public const string SharedPath = "csharp/shared.cs";

    /// <summary>No sources: nothing to compile (schemas alone do not make a unit).</summary>
    public bool IsEmpty => Steps.Count == 0 && string.IsNullOrWhiteSpace(Shared);

    /// <summary>Sources with no schemas.</summary>
    public static CSharpUnitSources Of(IEnumerable<CSharpStepSource> steps, string? shared = null)
        => new([.. steps], shared, new Dictionary<string, string>());

    /// <summary>SHA-256 (lower-case hex) of what the unit is made of; <paramref name="environment"/> is the compiler and reference set.</summary>
    public string Hash(string environment)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + text));

        Add(environment);
        foreach (CSharpStepSource step in Steps.OrderBy(s => s.StepId, StringComparer.Ordinal))
        {
            Add(step.StepId);
            Add(step.FirstLine.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(step.Code);
        }

        Add("shared");
        Add(Shared ?? string.Empty);
        foreach ((string name, string json) in Schemas.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            Add(name);
            Add(json);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

/// <summary>How serious a <see cref="CSharpDiagnostic"/> is.</summary>
public enum CSharpSeverity
{
    /// <summary>The unit does not compile.</summary>
    Error,

    /// <summary>The unit compiles; worth a look.</summary>
    Warning
}

/// <summary>A compiler (or unit) message, located in what the author wrote.</summary>
/// <param name="Id">Compiler id (<c>CS0103</c>) or one of the unit's (<c>WFCS001</c>..).</param>
/// <param name="Severity">Error or warning.</param>
/// <param name="Message">The message.</param>
/// <param name="File"><c>workflow.yaml</c> for a step body, <c>csharp/shared.cs</c>, <c>schema:&lt;name&gt;</c> for generated classes, or null.</param>
/// <param name="Line">1-based line in <paramref name="File"/> (the YAML line for a step body); null when unknown.</param>
/// <param name="Column">1-based column (inside the body's line for a step body).</param>
/// <param name="StepId">The step whose body the message is about.</param>
/// <param name="CodeLine">1-based line inside the step's body.</param>
public sealed record CSharpDiagnostic(
    string Id, CSharpSeverity Severity, string Message, string? File = null, int? Line = null, int? Column = null, string? StepId = null, int? CodeLine = null)
{
    /// <summary>True for errors.</summary>
    public bool IsError => Severity == CSharpSeverity.Error;
}

/// <summary>Where a compiled unit came from.</summary>
public enum UnitCacheSource
{
    /// <summary>Compiled now.</summary>
    None,

    /// <summary>The emitted assembly of the same sources was already in this process.</summary>
    Memory,

    /// <summary>The emitted assembly was in the cache folder (a previous process compiled it).</summary>
    Disk
}

/// <summary>The outcome of <see cref="CSharpUnitCompiler.CompileAsync"/>.</summary>
public sealed class UnitCompilation
{
    internal UnitCompilation(string hash, IReadOnlyList<CSharpDiagnostic> diagnostics, CSharpUnit? unit, UnitCacheSource source, TimeSpan elapsed)
    {
        Hash = hash;
        Diagnostics = diagnostics;
        Unit = unit;
        Source = source;
        Elapsed = elapsed;
    }

    /// <summary>The hash of the sources (the cache key).</summary>
    public string Hash { get; }

    /// <summary>Errors and warnings.</summary>
    public IReadOnlyList<CSharpDiagnostic> Diagnostics { get; }

    /// <summary>The loaded unit; null when there are errors. The caller owns it and disposes it (or hands it to <c>AddCSharpUnit</c>, whose provider does).</summary>
    public CSharpUnit? Unit { get; }

    /// <summary>True when there are no errors.</summary>
    public bool Success => Unit != null;

    /// <summary>Whether the assembly came from a cache.</summary>
    public UnitCacheSource Source { get; }

    /// <summary>How long the call took (compile or load).</summary>
    public TimeSpan Elapsed { get; }
}
