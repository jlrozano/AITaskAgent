using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Support.Exec;
using AITaskAgent.Observability.Events;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Runs an external executable or batch file (<c>.exe</c>, <c>.bat</c>, <c>.cmd</c>) and returns the text of its standard
/// output. Declarable in YAML as <c>type: ExecStep</c>. It is not a shell: nothing interprets the command line.
/// </summary>
/// <remarks>
/// <para><b>Command.</b> <see cref="Executable"/> is a literal (no templates; a template there would make the command
/// unknowable to whoever reviews the flow). Each item of <see cref="Args"/> is rendered on its own with the framework
/// templates (<c>{{ input.x }}</c>, <c>{{ context.x }}</c>) and is exactly ONE argument, whatever characters it has
/// (<see cref="ProcessStartInfo.ArgumentList"/>; no quoting to get wrong). A value that starts with <c>-</c> can still be
/// read as an option by the target program: that is argument injection, not shell injection, and cannot be solved here.</para>
/// <para><b>Batch files.</b> On Windows a <c>.bat</c>/<c>.cmd</c> is run as <c>cmd.exe /d /s /c</c> with every argument
/// double-quoted. Inside quotes <c>cmd</c> treats <c>&amp; | &lt; &gt; ^ ( )</c> literally, but it still expands
/// <c>%var%</c> and has no way to escape a <c>"</c>, so an argument (or the executable path) containing <c>"</c>,
/// <c>%</c>, a line break or NUL is refused with <see cref="ExecFailureKind.InvalidConfiguration"/> instead of being
/// escaped. What the batch file itself does with its arguments (<c>call</c>, unquoted <c>%1</c>...) is outside the
/// step's control: do not feed untrusted text to a batch file.</para>
/// <para><b>Environment.</b> By default (<see cref="InheritEnvironment"/> = <c>minimal</c>) the process receives only
/// what it needs to run (<c>PATH</c>, <c>SystemRoot</c>, <c>TEMP</c>... on Windows; <c>PATH</c>, <c>HOME</c>, <c>TMPDIR</c>,
/// <c>LANG</c>, <c>LC_ALL</c> elsewhere) plus <see cref="Env"/> (values are rendered). <c>all</c> inherits the host's
/// whole environment, secrets included. Neither the environment values nor the <see cref="Stdin"/> text are written to
/// events, logs or error messages.</para>
/// <para><b>Result.</b> The text of stdout, exactly as the program wrote it (decoded with <see cref="Encoding"/>; the
/// trailing line break is NOT removed: no step of the framework trims it, so a template that needs it clean uses a
/// filter or a following step). stderr is not part of the result: it is kept for the error message. A <see cref="StepBase.MaxRetries"/>
/// of 1 is the default, and an error always carries an exception, so <c>RetryMiddleware</c> never re-runs a command by itself.</para>
/// <para><b>Errors.</b> An exit code outside <see cref="OkExitCodes"/>, a command the policy denies, a process that cannot
/// start, an invalid property or output over <see cref="MaxOutputChars"/> fail with <see cref="ExecStepException"/> (its
/// <see cref="ExecStepException.Kind"/> says which; for a non-zero exit it carries the code and the last 2000 characters of
/// stderr). A timeout (<see cref="TimeoutMs"/>, or the step timeout of the pipeline, whichever comes first) fails with
/// <see cref="TimeoutException"/> and a cancellation throws <see cref="OperationCanceledException"/>; in both the whole process
/// tree is killed first.</para>
/// <para><b>Killing the tree.</b> On Windows the process is put in its own job object (<c>KILL_ON_JOB_CLOSE</c>), so
/// grandchildren, orphans included, die with it on timeout, cancellation, output limit and also when the step ends
/// normally: a child the command leaves running in the background does not outlive the step. A process spawned in the
/// instant between start and assignment to the job is the only one that can escape. On Linux and macOS only
/// <see cref="Process.Kill(bool)"/> with <c>entireProcessTree</c> is available: it kills the descendants that exist at that
/// moment, so a grandchild already orphaned (its parent exited) survives, and background children survive a normal end.</para>
/// <para><b>Policy.</b> A host can register an <see cref="IExecStepPolicy"/> in DI to deny a command or confine its working
/// directory; without one everything is allowed. The step is not a sandbox: the command runs with the privileges of the
/// host process.</para>
/// <para>The step keeps nothing in its fields between runs, so one instance can serve concurrent runs.</para>
/// </remarks>
[StepTag("ExecStep")]
[Description("Runs an external executable or batch file (.exe, .bat, .cmd), without a shell, and returns the text of its standard output.")]
public sealed class ExecStep : StepBase
{
    /// <summary>Default <see cref="TimeoutMs"/>: one minute.</summary>
    public const int DefaultTimeoutMs = 60_000;

    /// <summary>Default <see cref="MaxOutputChars"/>, per stream.</summary>
    public const int DefaultMaxOutputChars = 1_000_000;

    /// <summary>Value of <see cref="InheritEnvironment"/> that passes only the minimal environment (the default).</summary>
    public const string InheritMinimal = "minimal";

    /// <summary>Value of <see cref="InheritEnvironment"/> that passes the host's whole environment.</summary>
    public const string InheritAll = "all";

    private const int StderrTailChars = 2000;
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    private static readonly string[] MinimalWindows =
        ["PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "TEMP", "TMP"];

    private static readonly string[] MinimalUnix = ["PATH", "HOME", "TMPDIR", "LANG", "LC_ALL"];

    private string _inheritEnvironment = InheritMinimal;

    /// <summary>Creates the step; the registry sets <see cref="StepBase.Name"/> from the YAML <c>id</c>.</summary>
    public ExecStep() : this("ExecStep")
    {
    }

    /// <summary>Creates the step with an explicit name (programmatic use).</summary>
    public ExecStep(string name) : base(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        // A failing command is deterministic, and may have side effects: it is not re-run on its own.
        MaxRetries = 1;
    }

    /// <summary>Executable or batch file to run; a literal (templates are refused). Looked up in <c>PATH</c> when it has no directory.</summary>
    [Description("Executable or batch file (.exe, .bat, .cmd). Literal: no templates.")]
    public string Executable { get; set; } = "";

    /// <summary>Arguments, one item per argument; each item is rendered with the templates on its own.</summary>
    [Description("Arguments: a list, each item rendered with {{ input.x }}/{{ context.x }} and passed as ONE argument.")]
    public string[]? Args { get; set; }

    /// <summary>Working directory (rendered with the templates). Null or empty means the host process's.</summary>
    [Description("Working directory; templates allowed. Default: the host process's.")]
    public string? WorkingDirectory { get; set; }

    /// <summary>Environment variables to set (values are rendered with the templates), on top of <see cref="InheritEnvironment"/>.</summary>
    [Description("Environment variables to set (values accept templates).")]
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>
    /// <c>minimal</c> (default): only the variables a process needs to run, plus <see cref="Env"/>. <c>all</c>: the host's
    /// whole environment.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not <c>minimal</c> or <c>all</c>.</exception>
    [Description("minimal (default): only PATH, SystemRoot, TEMP and the like, plus env. all: the host's whole environment.")]
    public string InheritEnvironment
    {
        get => _inheritEnvironment;
        set => _inheritEnvironment = value?.Trim().ToLowerInvariant() switch
        {
            InheritMinimal => InheritMinimal,
            InheritAll => InheritAll,
            _ => throw new ArgumentException("'inheritEnvironment' must be 'minimal' or 'all'.", nameof(InheritEnvironment))
        };
    }

    /// <summary>Text written to the process's stdin, which is then closed (template). Null: stdin is closed at once.</summary>
    [Description("Text sent to stdin (template); stdin is closed afterwards. Never logged.")]
    public string? Stdin { get; set; }

    /// <summary>Wall-clock limit of the process in milliseconds (default <see cref="DefaultTimeoutMs"/>). Must be positive.</summary>
    [Description("Wall-clock limit in milliseconds (default 60000). The step timeout of the pipeline also applies.")]
    public int TimeoutMs { get; set; } = DefaultTimeoutMs;

    /// <summary>Exit codes that count as success (default <c>[0]</c>).</summary>
    [Description("Exit codes that count as success (default [0]).")]
    public int[] OkExitCodes { get; set; } = [0];

    /// <summary>Most characters accepted from stdout, and from stderr, each (default <see cref="DefaultMaxOutputChars"/>). Going over fails the step.</summary>
    [Description("Most characters accepted from stdout and from stderr, each (default 1000000). Going over fails the step.")]
    public int MaxOutputChars { get; set; } = DefaultMaxOutputChars;

    /// <summary>Encoding of stdin, stdout and stderr (default <c>utf-8</c>); any name <see cref="System.Text.Encoding.GetEncoding(string)"/> knows.</summary>
    [Description("Encoding of stdin/stdout/stderr (default utf-8), e.g. windows-1252 or ibm850.")]
    public string Encoding { get; set; } = "utf-8";

    /// <inheritdoc/>
    protected override StepStartedEvent EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context)
    {
        // Only literals: the rendered arguments, the environment and stdin may carry secrets.
        Dictionary<string, object> data = new(baseEvent.AdditionalData ?? []) { ["executable"] = Executable };
        return base.EnrichStartedEvent(baseEvent with { AdditionalData = data }, input, context);
    }

    /// <inheritdoc/>
    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(input, context, cancellationToken);
        }
        catch (ExecStepException ex)
        {
            Logger.LogWarning("ExecStep '{Step}' failed ({Kind}): {Message}", Name, ex.Kind, ex.Message);
            return CreateErrorResult(ex.Message, ex);
        }
        catch (TimeoutException ex)
        {
            Logger.LogWarning("ExecStep '{Step}' timed out: {Message}", Name, ex.Message);
            return CreateErrorResult(ex.Message, ex);
        }
    }

    private async Task<IStepResult> RunAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken)
    {
        string who = $"ExecStep '{Name}'";
        System.Text.Encoding encoding = ResolveEncoding(who);
        Validate(who);

        cancellationToken.ThrowIfCancellationRequested();

        bool isBatch = OperatingSystem.IsWindows() && IsBatchFile(Executable);
        string[] args = [.. (Args ?? []).Select(a => TemplateExpression.Render(a ?? "", input, context))];
        string? workingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory)
            ? null
            : TemplateExpression.Render(WorkingDirectory, input, context);
        Dictionary<string, string> env = (Env ?? []).ToDictionary(
            kv => kv.Key, kv => TemplateExpression.Render(kv.Value ?? "", input, context), StringComparer.Ordinal);
        string? stdin = Stdin is null ? null : TemplateExpression.Render(Stdin, input, context);

        if (isBatch)
        {
            CheckBatchQuoting(who, Executable, args);
        }

        // Host policy: may deny or confine the working directory. A policy that fails denies.
        if (context.ServiceProvider.GetService(typeof(IExecStepPolicy)) is IExecStepPolicy policy)
        {
            ExecRequest request = new(Name, Executable, args, workingDirectory, [.. env.Keys],
                InheritEnvironment == InheritAll, isBatch);
            ExecStepPolicyDecision decision;
            try
            {
                decision = await policy.EvaluateAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ExecStepException(ExecFailureKind.PolicyDenied, $"{who}: the execution policy failed ({ex.GetType().Name}); the command was not run.", inner: ex);
            }

            if (!decision.Allowed)
            {
                throw new ExecStepException(ExecFailureKind.PolicyDenied,
                    $"{who}: the execution policy denied '{Executable}'{(string.IsNullOrWhiteSpace(decision.Reason) ? "." : ": " + decision.Reason)}");
            }

            workingDirectory = decision.WorkingDirectory ?? workingDirectory;
        }

        if (workingDirectory != null && !Directory.Exists(workingDirectory))
        {
            throw new ExecStepException(ExecFailureKind.StartFailed, $"{who}: the working directory '{workingDirectory}' does not exist.");
        }

        ProcessStartInfo startInfo = BuildStartInfo(isBatch, args, workingDirectory, env, encoding);

        Stopwatch stopwatch = Stopwatch.StartNew();
        ProcessOutcome outcome = await ExecuteProcessAsync(startInfo, stdin, who, cancellationToken);
        stopwatch.Stop();

        if (outcome.TimedOut)
        {
            throw new TimeoutException($"{who} exceeded its limit of {TimeoutMs} ms; the process tree was killed.");
        }

        if (outcome.LimitStream != null)
        {
            throw new ExecStepException(ExecFailureKind.OutputLimit,
                $"{who}: {outcome.LimitStream} went over maxOutputChars ({MaxOutputChars}); the process tree was killed.");
        }

        Logger.LogInformation("ExecStep '{Step}' ran '{Executable}' ({ArgCount} args): exit code {ExitCode} in {Ms} ms, {Chars} chars on stdout",
            Name, Executable, args.Length, outcome.ExitCode, stopwatch.ElapsedMilliseconds, outcome.Stdout.Length);

        if (!OkExitCodes.Contains(outcome.ExitCode))
        {
            string tail = outcome.Stderr.Length > StderrTailChars ? outcome.Stderr[^StderrTailChars..] : outcome.Stderr;
            string message = $"{who}: '{Executable}' exited with code {outcome.ExitCode} (accepted: {string.Join(", ", OkExitCodes)})."
                + (tail.Length == 0 ? " stderr was empty." : $" stderr (last {tail.Length} chars):{Environment.NewLine}{tail}");
            throw new ExecStepException(ExecFailureKind.NonZeroExit, message, outcome.ExitCode, tail);
        }

        return new StepResult<string>(this, outcome.Stdout);
    }

    private async Task<ProcessOutcome> ExecuteProcessAsync(
        ProcessStartInfo startInfo, string? stdin, string who, CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = startInfo };
        ProcessJob? job = null;
        try
        {
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                throw new ExecStepException(ExecFailureKind.StartFailed, $"{who}: could not start '{Executable}': {ex.Message}", inner: ex);
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    job = ProcessJob.Create();
                    job.Assign(process);
                }
                catch (Exception ex)
                {
                    // Not fatal: the tree kill below still works, only orphans could survive.
                    Logger.LogWarning(ex, "ExecStep '{Step}': the process could not be put in a job object", Name);
                    job?.Dispose();
                    job = null;
                }
            }

            using CancellationTokenSource timeoutCts = new(TimeoutMs);
            using CancellationTokenSource limitCts = new();
            using CancellationTokenSource pumpCts = new();
            using CancellationTokenSource waitCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, limitCts.Token);

            StringBuilder stdout = new();
            StringBuilder stderr = new();
            string? limitStream = null;

            async Task PumpAsync(StreamReader reader, StringBuilder sink, string streamName)
            {
                char[] buffer = new char[8192];
                try
                {
                    int read;
                    while ((read = await reader.ReadAsync(buffer.AsMemory(), pumpCts.Token)) > 0)
                    {
                        if (sink.Length + read > MaxOutputChars)
                        {
                            Interlocked.CompareExchange(ref limitStream, streamName, null);
                            limitCts.Cancel();
                            return;
                        }

                        sink.Append(buffer, 0, read);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The pipe was closed under us, or we gave up waiting: keep what was read.
                }
            }

            async Task FeedStdinAsync()
            {
                try
                {
                    if (stdin != null)
                    {
                        await process.StandardInput.WriteAsync(stdin.AsMemory(), pumpCts.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The process did not read its stdin (or ended): not an error by itself.
                }
                finally
                {
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        // Already closed.
                    }
                }
            }

            Task outTask = PumpAsync(process.StandardOutput, stdout, "stdout");
            Task errTask = PumpAsync(process.StandardError, stderr, "stderr");
            Task inTask = FeedStdinAsync();

            try
            {
                await process.WaitForExitAsync(waitCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Outer cancellation, own timeout or output limit: classified below.
            }

            bool exited = process.HasExited;
            if (!exited)
            {
                await KillTreeAsync(process, job);
            }
            else
            {
                // Whatever the command left running dies with it (Windows; elsewhere nothing to do).
                job?.Terminate();
            }

            // The pipes close when every writer is gone; give the readers a short grace, then stop waiting.
            try
            {
                await Task.WhenAll(outTask, errTask, inTask).WaitAsync(DrainGrace);
            }
            catch (TimeoutException)
            {
                await pumpCts.CancelAsync();
                try
                {
                    await Task.WhenAll(outTask, errTask, inTask).WaitAsync(DrainGrace);
                }
                catch (TimeoutException)
                {
                    Logger.LogWarning("ExecStep '{Step}': the output pipes did not close; a descendant still holds them", Name);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException($"{who} was cancelled; the process tree was killed.", cancellationToken);
            }

            if (limitStream != null)
            {
                return new ProcessOutcome(0, "", "", TimedOut: false, limitStream);
            }

            if (!exited)
            {
                return new ProcessOutcome(0, "", "", TimedOut: true, null);
            }

            return new ProcessOutcome(process.ExitCode, stdout.ToString(), stderr.ToString(), false, null);
        }
        finally
        {
            // KILL_ON_JOB_CLOSE: closing the handle is the last guarantee.
            job?.Dispose();
        }
    }

    private static async Task KillTreeAsync(Process process, ProcessJob? job)
    {
        try
        {
            job?.Terminate();
        }
        catch (Exception)
        {
            // Fall through to the process-tree kill.
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone, or not killable: nothing more to do.
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(KillGrace);
        }
        catch (TimeoutException)
        {
            // Left to the job object / the operating system.
        }
    }

    private ProcessStartInfo BuildStartInfo(
        bool isBatch, string[] args, string? workingDirectory, Dictionary<string, string> env, System.Text.Encoding encoding)
    {
        ProcessStartInfo startInfo = new()
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            StandardInputEncoding = encoding is UTF8Encoding ? new UTF8Encoding(false) : encoding
        };

        if (workingDirectory != null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (isBatch)
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe";
            // /s strips the outer pair of quotes; every argument is quoted and CheckBatchQuoting made sure none can break out.
            startInfo.Arguments = "/d /s /c \"" + string.Join(' ', new[] { Executable }.Concat(args).Select(a => "\"" + a + "\"")) + "\"";
        }
        else
        {
            startInfo.FileName = Executable;
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        if (InheritEnvironment == InheritMinimal)
        {
            Dictionary<string, string?> inherited = [];
            foreach (string name in OperatingSystem.IsWindows() ? MinimalWindows : MinimalUnix)
            {
                inherited[name] = Environment.GetEnvironmentVariable(name);
            }

            startInfo.Environment.Clear();
            foreach ((string name, string? value) in inherited)
            {
                if (value != null)
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        foreach ((string name, string value) in env)
        {
            startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    private void Validate(string who)
    {
        if (string.IsNullOrWhiteSpace(Executable))
        {
            throw new ExecStepException(ExecFailureKind.InvalidConfiguration, $"{who}: the 'executable' property is empty.");
        }

        if (Executable.Contains("{{", StringComparison.Ordinal))
        {
            throw new ExecStepException(ExecFailureKind.InvalidConfiguration,
                $"{who}: 'executable' is a literal and does not accept templates; pass variable parts in 'args'.");
        }

        if (TimeoutMs <= 0)
        {
            throw new ExecStepException(ExecFailureKind.InvalidConfiguration, $"{who}: 'timeoutMs' must be positive (was {TimeoutMs}).");
        }

        if (MaxOutputChars <= 0)
        {
            throw new ExecStepException(ExecFailureKind.InvalidConfiguration, $"{who}: 'maxOutputChars' must be positive (was {MaxOutputChars}).");
        }

        if (OkExitCodes is null or { Length: 0 })
        {
            throw new ExecStepException(ExecFailureKind.InvalidConfiguration, $"{who}: 'okExitCodes' must list at least one code.");
        }

        if (Env != null && Env.Keys.Any(k => string.IsNullOrWhiteSpace(k) || k.Contains('=') || k.Contains('\0')))
        {
            throw new ExecStepException(ExecFailureKind.InvalidConfiguration, $"{who}: 'env' has an empty name, or one with '=' or NUL.");
        }
    }

    private System.Text.Encoding ResolveEncoding(string who)
    {
        try
        {
            // Code pages such as ibm850 or windows-1252 need the provider on .NET Core.
            System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(string.IsNullOrWhiteSpace(Encoding) ? "utf-8" : Encoding);
        }
        catch (ArgumentException ex)
        {
            throw new ExecStepException(ExecFailureKind.InvalidConfiguration, $"{who}: the encoding '{Encoding}' is not known.", inner: ex);
        }
    }

    private static bool IsBatchFile(string executable)
    {
        string extension = Path.GetExtension(executable.Trim());
        return extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckBatchQuoting(string who, string executable, string[] args)
    {
        static bool Unsafe(string text) => text.IndexOfAny(['"', '%', '\r', '\n', '\0']) >= 0;

        // The executable is a literal, but it is checked too: a '%' in a path would be expanded by cmd.
        if (Unsafe(executable))
        {
            throw new ExecStepException(ExecFailureKind.InvalidConfiguration,
                $"{who}: the executable path contains a double quote, a percent sign, a line break or NUL, which cmd.exe cannot receive safely.");
        }

        // Unsafe arguments are reported by position, never by value: they may come from the context.
        for (int i = 0; i < args.Length; i++)
        {
            if (Unsafe(args[i]))
            {
                throw new ExecStepException(ExecFailureKind.InvalidConfiguration,
                    $"{who}: argument {i + 1} contains a double quote, a percent sign, a line break or NUL, which cmd.exe cannot receive safely in a batch file call; the command was not run.");
            }
        }
    }

    private sealed record ProcessOutcome(int ExitCode, string Stdout, string Stderr, bool TimedOut, string? LimitStream);
}
