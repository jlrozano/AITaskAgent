namespace AITaskAgent.Core.Steps;

/// <summary>Why an <see cref="ExecStep"/> failed. Carried by <see cref="ExecStepException.Kind"/>.</summary>
public enum ExecFailureKind
{
    /// <summary>The step's properties are not valid (empty executable, bad encoding, a batch argument that cannot be quoted safely...).</summary>
    InvalidConfiguration,

    /// <summary>The policy (<see cref="IExecStepPolicy"/>) denied the command.</summary>
    PolicyDenied,

    /// <summary>The operating system could not start the process (not found, access denied...).</summary>
    StartFailed,

    /// <summary>The process ended with an exit code that is not in <c>okExitCodes</c>.</summary>
    NonZeroExit,

    /// <summary>stdout or stderr went over <c>maxOutputChars</c>; the process tree was killed.</summary>
    OutputLimit
}

/// <summary>
/// Typed failure of an <see cref="ExecStep"/>. The step returns it as the <c>OriginalException</c> of its error, so a host
/// that classifies errors by exception type sees <c>ExecStepException</c> (and <c>RetryMiddleware</c> does not retry:
/// the same command would fail the same way). A timeout is reported as <see cref="TimeoutException"/> and a cancellation
/// as <see cref="OperationCanceledException"/>, not as this type.
/// </summary>
/// <remarks>The message never contains environment variables or stdin; it may carry the tail of stderr.</remarks>
public sealed class ExecStepException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ExecStepException(ExecFailureKind kind, string message, int? exitCode = null, string? stderrTail = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        ExitCode = exitCode;
        StderrTail = stderrTail;
    }

    /// <summary>What went wrong.</summary>
    public ExecFailureKind Kind { get; }

    /// <summary>Exit code of the process, for <see cref="ExecFailureKind.NonZeroExit"/>.</summary>
    public int? ExitCode { get; }

    /// <summary>Last characters of the process's stderr (already part of the message), when it ran.</summary>
    public string? StderrTail { get; }
}

/// <summary>
/// The command an <see cref="ExecStep"/> is about to run, as shown to an <see cref="IExecStepPolicy"/>. It holds what
/// a policy needs to decide; it deliberately does not hold the stdin text or the values of the environment variables.
/// </summary>
/// <param name="StepName">Name (<c>id</c>) of the step.</param>
/// <param name="Executable">The executable as written in the step.</param>
/// <param name="Arguments">The rendered arguments, one item per argument.</param>
/// <param name="WorkingDirectory">The rendered working directory, or null for the host process's.</param>
/// <param name="EnvironmentNames">Names (not values) of the variables the step sets.</param>
/// <param name="InheritsFullEnvironment">True when the step uses <c>inheritEnvironment: all</c>.</param>
/// <param name="IsBatch">True for a <c>.bat</c>/<c>.cmd</c> file on Windows (run through <c>cmd.exe</c>).</param>
public sealed record ExecRequest(
    string StepName,
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyList<string> EnvironmentNames,
    bool InheritsFullEnvironment,
    bool IsBatch);

/// <summary>Answer of an <see cref="IExecStepPolicy"/>.</summary>
public sealed record ExecStepPolicyDecision
{
    private ExecStepPolicyDecision()
    {
    }

    /// <summary>False when the command must not run.</summary>
    public bool Allowed { get; private init; }

    /// <summary>Why it was denied (shown in the error).</summary>
    public string? Reason { get; private init; }

    /// <summary>When set, the command runs in this directory instead of the requested one (confinement).</summary>
    public string? WorkingDirectory { get; private init; }

    /// <summary>Lets the command run as requested.</summary>
    public static ExecStepPolicyDecision Allow() => new() { Allowed = true };

    /// <summary>Lets the command run, but in <paramref name="workingDirectory"/>.</summary>
    public static ExecStepPolicyDecision AllowIn(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        return new() { Allowed = true, WorkingDirectory = workingDirectory };
    }

    /// <summary>Stops the command; the step fails with <see cref="ExecFailureKind.PolicyDenied"/>.</summary>
    public static ExecStepPolicyDecision Deny(string reason) => new() { Allowed = false, Reason = reason };
}

/// <summary>
/// Optional hook a host registers in DI to decide whether an <see cref="ExecStep"/> may run a command, or to confine
/// its working directory. <see cref="ExecStep"/> resolves it from <c>context.ServiceProvider</c> on every execution;
/// with nothing registered every command is allowed.
/// </summary>
public interface IExecStepPolicy
{
    /// <summary>Called after the arguments are rendered and before the process starts.</summary>
    Task<ExecStepPolicyDecision> EvaluateAsync(ExecRequest request, CancellationToken cancellationToken);
}
