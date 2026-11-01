namespace AITaskAgent.Script.Protocol;

/// <summary>
/// Limits of one script run. Shared (compiled into both the host and the worker) so each side applies the same
/// numbers: the host rejects out-of-range values early, the worker refuses them again as defence in depth.
/// </summary>
internal static class ScriptLimits
{
    /// <summary>Default V8 heap cap, in MB.</summary>
    public const int DefaultHeapMb = 64;

    /// <summary>Smallest accepted heap cap, in MB.</summary>
    public const int MinHeapMb = 8;

    /// <summary>Hard ceiling of the heap cap, in MB. A YAML pipeline cannot raise it.</summary>
    public const int MaxHeapMbCeiling = 128;

    /// <summary>Default V8 stack cap, in KB.</summary>
    public const int DefaultStackKb = 512;

    /// <summary>Smallest accepted stack cap, in KB.</summary>
    public const int MinStackKb = 64;

    /// <summary>Hard ceiling of the stack cap, in KB. A YAML pipeline cannot raise it.</summary>
    public const int MaxStackKbCeiling = 2048;

    /// <summary>Default number of host function calls per run.</summary>
    public const int DefaultMaxCalls = 100;

    /// <summary>Hard ceiling of host function calls per run. A YAML pipeline cannot raise it.</summary>
    public const int MaxCallsCeiling = 10_000;

    /// <summary>Default budget of bytes (arguments plus replies) exchanged with host functions per run.</summary>
    public const int DefaultMaxCallBytes = 1024 * 1024;

    /// <summary>Hard ceiling of that budget, in bytes. A YAML pipeline cannot raise it.</summary>
    public const int MaxCallBytesCeiling = 8 * 1024 * 1024;

    /// <summary>Most step definitions a script may queue with <c>nextStep</c>.</summary>
    public const int MaxNextSteps = 50;

    /// <summary>Largest total size of the libraries sent with one run, in bytes.</summary>
    public const int MaxModulesBytes = 512 * 1024;

    /// <summary>Longest a <c>load</c> (compiling the bundle and checking its shape) may take, in milliseconds.</summary>
    public const int LoadTimeoutMs = 10_000;

    /// <summary>Memory the worker needs besides the script (CLR, V8 engine, ICU data), in MB.</summary>
    public const int ProcessOverheadMb = 192;

    /// <summary>
    /// V8's own old-space limit is a multiple of the heap cap: the soft cap (an interrupt) must trip before V8
    /// reaches its limit and aborts, which is only possible with headroom above the cap.
    /// </summary>
    public const int V8OldSpaceFactor = 4;

    /// <summary>
    /// Memory limit of the whole worker process: V8 old space, the ArrayBuffer cap (same size as the heap cap)
    /// and the fixed overhead. Enforced by the operating system where it can (Windows job object) and by the
    /// host's watchdog everywhere else.
    /// </summary>
    public static long ProcessMemoryLimitBytes(int heapMb)
        => ((long)heapMb * V8OldSpaceFactor + heapMb + ProcessOverheadMb) * 1024 * 1024;
}

/// <summary>
/// Wire format between host and worker: one JSON object per line over the worker's stdin/stdout, with the
/// message kind in <c>t</c>. Everything is a string or a number, so a line never needs more than JSON escaping.
/// </summary>
/// <remarks>
/// Protocol version <see cref="ScriptProtocol.Version"/>. One worker serves a whole run: it compiles the run's JavaScript
/// once (<c>load</c>) and then runs any number of calls (<c>run</c>), one at a time, until the host closes its stdin or
/// kills it. Worker to host: <c>ready</c> (proto); <c>loaded</c> (ok, errors, error, exports); <c>log</c> (level, text);
/// <c>get</c> (key); <c>set</c> (key, json); <c>call</c> (fn, args); <c>result</c> (ok, json, error, nextSteps, recycle).
/// Host to worker: <c>load</c> (proto, pieces, heapMb, check, exports: compile the bundle of <see cref="ScriptBundle"/>; with
/// <c>check</c> nothing is kept, it is the syntax check; <c>exports</c>, a JSON array of library names, asks for the functions each one
/// exports, answered in <c>loaded.exports</c> as <c>{ lib: { functions } | { error } }</c>); <c>run</c> (proto, call, fn, export, name, input,
/// timeoutMs, heapMb, stackKb, uses, allow, maxCalls, maxCallBytes: call one function of the loaded bundle in a fresh V8 context; <c>call</c> is
/// <c>step</c> (default: <c>fn</c> is a step's function), <c>validate</c> (<c>fn</c> is a library, <c>export</c> the function it exports,
/// <c>input</c> the payload <c>{ result, keys, step? }</c>; the result's <c>json</c> is the envelope <c>{ type, value?, patch }</c>) or <c>hook</c>
/// (the same, with the payload <c>{ fields, writable, keys }</c> and the envelope <c>{ type, patch, fields }</c>, <c>fields</c> being the patch of
/// the writable members)); <c>value</c> (json),
/// the one answer to a <c>get</c>, and (ok, json, error) to a <c>call</c>. <c>recycle</c> in a result means the call broke
/// a limit: the host discards that worker and starts another from the same pieces. The worker is single-threaded with
/// respect to the channel: during a call it only blocks to read when it is waiting for a <c>value</c>.
/// </remarks>
internal static class Msg
{
    public const string Kind = "t";
    public const string Ready = "ready";
    public const string Load = "load";
    public const string Loaded = "loaded";
    public const string Run = "run";
    public const string Log = "log";
    public const string Get = "get";
    public const string Set = "set";
    public const string Value = "value";
    public const string Result = "result";
    public const string Call = "call";
}

/// <summary>Version of the wire format; a host and a worker that disagree refuse to talk.</summary>
internal static class ScriptProtocol
{
    /// <summary>
    /// 3 = one worker per run: the bundle is loaded (compiled) once and each step is a call to its function. 4 = JavaScript validators: a
    /// <c>run</c> may be a <c>validate</c> call, and a <c>load</c> may ask for the functions its libraries export. 5 = JavaScript hooks: a
    /// <c>run</c> may be a <c>hook</c> call.
    /// </summary>
    public const int Version = 5;
}
