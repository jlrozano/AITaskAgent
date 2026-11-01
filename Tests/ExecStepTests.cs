using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace AITaskAgent.Tests;

/// <summary>
/// <see cref="ExecStep"/>: runs the harmless <c>ExecHelper</c> executable (Tests.ExecHelper, driven by its arguments), never a
/// shell command. Covers stdout/stdin, arguments as literals, exit codes, timeout and cancellation with an orphaned
/// grandchild, the output cap, the minimal environment, YAML, registration by tag and the policy hook.
/// </summary>
public sealed class ExecStepTests : IDisposable
{
    private static readonly string Helper = Path.Combine(
        AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ExecHelper.exe" : "ExecHelper");

    private readonly string _dir = Directory.CreateTempSubdirectory("execstep-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A killed process may still hold the folder for a moment; the OS temp cleaner takes it.
        }
    }

    private static ExecStep Step(params string[] args) => new("Run") { Executable = Helper, Args = args };

    private static StepResult<JToken> Seed(object? value = null)
        => new JsonStepResult(new EmptyStep("Seed"), JToken.FromObject(value ?? new { }));

    private static async Task<IStepResult> RunAsync(
        ExecStep step, object? input = null, IServiceProvider? services = null, CancellationToken ct = default)
    {
        PipelineContext context = services is null ? new PipelineContext() : new PipelineContext(null, services);
        return await new Pipeline().ExecuteAsync("Test", [step], Seed(input), context, cancellationToken: ct);
    }

    private static ExecStepException Failure(IStepResult result)
    {
        Assert.True(result.HasError, "the step was expected to fail");
        return Assert.IsType<ExecStepException>(result.Error!.OriginalException);
    }

    private static async Task<int> WaitForPidFileAsync(string path)
    {
        for (int i = 0; i < 200; i++)
        {
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path), out int pid))
            {
                return pid;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("the helper never wrote its grandchild's pid");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.WaitForExit(0);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task AssertDeadAsync(int pid)
    {
        for (int i = 0; i < 100 && IsAlive(pid); i++)
        {
            await Task.Delay(50);
        }

        Assert.False(IsAlive(pid), $"process {pid} is still running");
    }

    // ── Result and arguments ─────────────────────────────────────────────

    [Fact]
    public async Task The_result_is_the_text_of_stdout_untrimmed()
    {
        IStepResult result = await RunAsync(Step("out", "hello\n"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.IsType<StepResult<string>>(result);
        Assert.Equal("hello\n", result.Value);
    }

    [Fact]
    public async Task Stdin_is_rendered_written_and_closed()
    {
        ExecStep step = Step("stdin");
        step.Stdin = "héllo {{ input.name }}\nline2";

        IStepResult result = await RunAsync(step, new { name = "mundo" });

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("héllo mundo\nline2", result.Value);
    }

    [Fact]
    public async Task Each_argument_is_one_literal_argument_and_templates_render_item_by_item()
    {
        string[] literals = ["a b", "x&y|z;`$(id)>f", "\"quoted\"", "-flag", "", "100%", "{{ input.v }}", "{{ input.v }}!"];
        IStepResult result = await RunAsync(Step(["args", .. literals]), new { v = "p q&r \"s\"" });

        Assert.False(result.HasError, result.Error?.Message);
        string[] expected = ["a b", "x&y|z;`$(id)>f", "\"quoted\"", "-flag", "", "100%", "p q&r \"s\"", "p q&r \"s\"!"];
        Assert.Equal(string.Concat(expected.Select(a => $"<{a}>\n")), result.Value);
    }

    [Fact]
    public async Task The_executable_is_a_literal_and_refuses_templates()
    {
        ExecStep step = new("Run") { Executable = "{{ input.exe }}" };

        ExecStepException ex = Failure(await RunAsync(step, new { exe = Helper }));

        Assert.Equal(ExecFailureKind.InvalidConfiguration, ex.Kind);
    }

    [Fact]
    public async Task A_missing_executable_is_a_start_failure()
    {
        ExecStep step = new("Run") { Executable = Path.Combine(_dir, "does-not-exist.exe") };

        ExecStepException ex = Failure(await RunAsync(step));

        Assert.Equal(ExecFailureKind.StartFailed, ex.Kind);
    }

    [Fact]
    public async Task The_working_directory_is_rendered_and_used()
    {
        ExecStep step = Step("cwd");
        step.WorkingDirectory = "{{ input.dir }}";

        IStepResult result = await RunAsync(step, new { dir = _dir });

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(Path.TrimEndingDirectorySeparator(_dir), Path.TrimEndingDirectorySeparator((string)result.Value!), ignoreCase: OperatingSystem.IsWindows());
    }

    // ── Exit codes ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_unaccepted_exit_code_fails_with_a_typed_error_carrying_code_and_stderr()
    {
        IStepResult result = await RunAsync(Step("err", "boom: bad thing", "3"));

        ExecStepException ex = Failure(result);
        Assert.Equal(ExecFailureKind.NonZeroExit, ex.Kind);
        Assert.Equal(3, ex.ExitCode);
        Assert.Contains("boom: bad thing", ex.StderrTail);
        Assert.Contains("code 3", result.Error!.Message);
        Assert.Contains("boom: bad thing", result.Error.Message);
    }

    [Fact]
    public async Task Only_the_tail_of_stderr_goes_into_the_error()
    {
        ExecStep step = Step("bigerr", "50000");
        step.OkExitCodes = [7];

        IStepResult result = await RunAsync(step);

        ExecStepException ex = Failure(result);
        Assert.Equal(0, ex.ExitCode);
        Assert.Equal(2000, ex.StderrTail!.Length);
        Assert.True(result.Error!.Message.Length < 3000);
    }

    [Fact]
    public async Task OkExitCodes_widens_what_counts_as_success()
    {
        ExecStep step = Step("exit", "3");
        step.OkExitCodes = [0, 3];

        Assert.False((await RunAsync(step)).HasError);

        step.OkExitCodes = [0];
        Assert.Equal(3, Failure(await RunAsync(step)).ExitCode);
    }

    [Fact]
    public async Task Stdin_env_values_and_secrets_never_reach_the_error_message()
    {
        ExecStep step = Step("err", "failed", "2");
        step.Stdin = "STDIN-SECRET-1";
        step.Env = new() { ["EXECSTEP_SECRET"] = "ENV-SECRET-2" };

        IStepResult result = await RunAsync(step);

        Assert.True(result.HasError);
        Assert.DoesNotContain("STDIN-SECRET-1", result.Error!.Message);
        Assert.DoesNotContain("ENV-SECRET-2", result.Error.Message);
    }

    // ── Timeout and cancellation ─────────────────────────────────────────

    [Fact]
    public async Task The_own_timeout_kills_the_tree_and_reports_a_timeout()
    {
        string pidFile = Path.Combine(_dir, "grandchild.pid");
        ExecStep step = Step("spawn", pidFile);
        step.TimeoutMs = 1500;

        Stopwatch watch = Stopwatch.StartNew();
        IStepResult result = await RunAsync(step);
        watch.Stop();

        Assert.True(result.HasError);
        Assert.IsType<TimeoutException>(result.Error!.OriginalException);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        await AssertDeadAsync(await WaitForPidFileAsync(pidFile));
    }

    [Fact]
    public async Task The_step_timeout_of_the_framework_also_kills_the_tree()
    {
        string pidFile = Path.Combine(_dir, "grandchild.pid");
        ExecStep step = Step("spawn", pidFile);
        step.Timeout = TimeSpan.FromMilliseconds(1500);

        IStepResult result = await RunAsync(step);

        Assert.True(result.HasError);
        Assert.IsType<TimeoutException>(result.Error!.OriginalException);
        await AssertDeadAsync(await WaitForPidFileAsync(pidFile));
    }

    [Fact]
    public async Task Cancellation_kills_the_tree_and_is_reported_as_cancelled()
    {
        string pidFile = Path.Combine(_dir, "grandchild.pid");
        using CancellationTokenSource cts = new();
        Task<IStepResult> run = RunAsync(Step("spawn", pidFile), ct: cts.Token);

        int grandchild = await WaitForPidFileAsync(pidFile);
        Assert.True(IsAlive(grandchild));
        await cts.CancelAsync();

        try
        {
            IStepResult result = await run;
            Assert.True(result.HasError);
            Assert.IsAssignableFrom<OperationCanceledException>(result.Error!.OriginalException);
        }
        catch (OperationCanceledException)
        {
            // The pipeline may surface the caller's cancellation as the exception itself.
        }

        await AssertDeadAsync(grandchild);
    }

    [Fact]
    public async Task On_Windows_an_orphaned_grandchild_dies_when_the_step_ends()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Elsewhere only Kill(entireProcessTree) exists: an orphan survives (documented limitation).
        }

        string pidFile = Path.Combine(_dir, "orphan.pid");

        IStepResult result = await RunAsync(Step("spawn-exit", pidFile));

        Assert.False(result.HasError, result.Error?.Message);
        await AssertDeadAsync(await WaitForPidFileAsync(pidFile));
    }

    // ── Output cap ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("big", "stdout")]
    [InlineData("bigerr", "stderr")]
    public async Task Output_over_the_cap_fails_with_a_clear_error(string command, string stream)
    {
        ExecStep step = Step(command, "5000");
        step.MaxOutputChars = 1000;

        IStepResult result = await RunAsync(step);

        ExecStepException ex = Failure(result);
        Assert.Equal(ExecFailureKind.OutputLimit, ex.Kind);
        Assert.Contains(stream, result.Error!.Message);
        Assert.Contains("maxOutputChars", result.Error.Message);
    }

    [Fact]
    public async Task Output_exactly_at_the_cap_is_accepted()
    {
        ExecStep step = Step("big", "1000");
        step.MaxOutputChars = 1000;

        IStepResult result = await RunAsync(step);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(1000, ((string)result.Value!).Length);
    }

    // ── Environment ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_default_environment_is_minimal_and_all_inherits_everything()
    {
        const string name = "EXECSTEP_TEST_HOST_VARIABLE";
        Environment.SetEnvironmentVariable(name, "present");
        try
        {
            IStepResult minimal = await RunAsync(Step("envnames"));
            Assert.False(minimal.HasError, minimal.Error?.Message);
            string[] names = ((string)minimal.Value!).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.DoesNotContain(name, names, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("PATH", names, StringComparer.OrdinalIgnoreCase);
            Assert.True(names.Length < 25, $"minimal passed {names.Length} variables");

            ExecStep all = Step("envnames");
            all.InheritEnvironment = "all";
            IStepResult full = await RunAsync(all);
            Assert.Contains(name, ((string)full.Value!).Split('\n'), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task Env_values_are_rendered_and_set()
    {
        ExecStep step = Step("env", "EXECSTEP_MINE");
        step.Env = new() { ["EXECSTEP_MINE"] = "v={{ input.v }}" };

        IStepResult result = await RunAsync(step, new { v = "1 2" });

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("v=1 2", result.Value);
    }

    [Fact]
    public void An_unknown_inheritEnvironment_is_rejected_when_set()
    {
        ExecStep step = new("Run");

        Assert.Throws<ArgumentException>(() => step.InheritEnvironment = "some");
        step.InheritEnvironment = "ALL";
        Assert.Equal("all", step.InheritEnvironment);
        Assert.Equal("minimal", new ExecStep().InheritEnvironment);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cap")]
    [InlineData("empty")]
    [InlineData("encoding")]
    public async Task Invalid_properties_fail_before_anything_runs(string which)
    {
        ExecStep step = Step("out", "x");
        switch (which)
        {
            case "timeout": step.TimeoutMs = 0; break;
            case "cap": step.MaxOutputChars = 0; break;
            case "empty": step.Executable = " "; break;
            case "encoding": step.Encoding = "no-such-encoding"; break;
        }

        Assert.Equal(ExecFailureKind.InvalidConfiguration, Failure(await RunAsync(step)).Kind);
    }

    [Fact]
    public async Task A_legacy_code_page_encoding_is_accepted()
    {
        ExecStep step = Step("out", "abc");
        step.Encoding = "windows-1252";

        IStepResult result = await RunAsync(step);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("abc", result.Value);
    }

    // ── Batch files (Windows) ────────────────────────────────────────────

    [Fact]
    public async Task On_Windows_a_batch_file_gets_metacharacters_as_literals_and_refuses_unquotable_ones()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string bat = Path.Combine(_dir, "echo arg.cmd");
        await File.WriteAllTextAsync(bat, "@echo off\r\necho \"%~1\" \"%~2\"\r\n");
        ExecStep step = new("Run") { Executable = bat, Args = ["a&b|c>d", "x y"] };

        IStepResult result = await RunAsync(step);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("\"a&b|c>d\" \"x y\"", ((string)result.Value!).Trim());

        foreach (string bad in new[] { "100%", "say \"hi\"", "a\nb" })
        {
            step.Args = [bad];
            Assert.Equal(ExecFailureKind.InvalidConfiguration, Failure(await RunAsync(step)).Kind);
        }
    }

    // ── YAML and registration ────────────────────────────────────────────

    [Fact]
    public async Task It_round_trips_through_YAML_v2_with_its_properties()
    {
        YamlTestKit kit = new();
        string helper = Helper.Replace("'", "''");
        var pipeline = kit.Factory.CreateFromYaml($$$"""
            name: exec
            steps:
              - id: run
                type: ExecStep
                executable: '{{{helper}}}'
                args: ["args", "{{ input.who }}", "a b"]
                env:
                  EXECSTEP_Y: "1"
                inheritEnvironment: minimal
                timeoutMs: 20000
                okExitCodes: [0, 2]
                maxOutputChars: 5000
                encoding: utf-8
            """);
        var context = YamlTestKit.NewContext();

        IStepResult result = await pipeline.ExecuteAsync(new JObject { ["who"] = "Ana" }, context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("<Ana>\n<a b>\n", result.Value);
    }

    [Fact]
    public async Task A_failing_command_in_YAML_is_an_ExecStepException()
    {
        YamlTestKit kit = new();
        string helper = Helper.Replace("'", "''");
        var pipeline = kit.Factory.CreateFromYaml($$$"""
            name: exec
            steps:
              - id: run
                type: ExecStep
                executable: '{{{helper}}}'
                args: [exit, "5"]
            """);

        IStepResult result = await pipeline.ExecuteAsync(new JObject(), YamlTestKit.NewContext());

        Assert.Equal(5, Failure(result).ExitCode);
    }

    [Fact]
    public void The_core_scan_registers_it_under_its_tag_with_its_parameters()
    {
        StepRegistry registry = new();
        registry.AddFromAssembly(typeof(StepRegistry).Assembly);

        Assert.IsType<ExecStep>(registry.Create("ExecStep"));
        var properties = registry.Describe("ExecStep").Parameters!.Properties;
        foreach (string expected in new[] { "executable", "args", "workingDirectory", "env", "inheritEnvironment", "stdin", "timeoutMs", "okExitCodes", "maxOutputChars", "encoding" })
        {
            Assert.Contains(properties.Keys, k => string.Equals(k, expected, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Policy ───────────────────────────────────────────────────────────

    private sealed class RecordingPolicy(Func<ExecRequest, ExecStepPolicyDecision> decide) : IExecStepPolicy
    {
        public List<ExecRequest> Requests { get; } = [];

        public Task<ExecStepPolicyDecision> EvaluateAsync(ExecRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(decide(request));
        }
    }

    private static IServiceProvider Services(IExecStepPolicy policy)
        => new ServiceCollection().AddSingleton(policy).BuildServiceProvider();

    [Fact]
    public async Task A_policy_that_denies_stops_the_command_before_it_starts()
    {
        string pidFile = Path.Combine(_dir, "must-not-exist.pid");
        RecordingPolicy policy = new(_ => ExecStepPolicyDecision.Deny("not in this package"));

        IStepResult result = await RunAsync(Step("spawn", pidFile), services: Services(policy));

        ExecStepException ex = Failure(result);
        Assert.Equal(ExecFailureKind.PolicyDenied, ex.Kind);
        Assert.Contains("not in this package", result.Error!.Message);
        Assert.False(File.Exists(pidFile));
        Assert.Single(policy.Requests);
    }

    [Fact]
    public async Task The_policy_sees_the_rendered_command_but_not_env_values_or_stdin()
    {
        RecordingPolicy policy = new(_ => ExecStepPolicyDecision.Allow());
        ExecStep step = Step("out", "{{ input.v }}");
        step.Env = new() { ["EXECSTEP_P"] = "ENV-SECRET" };
        step.Stdin = "STDIN-SECRET";

        IStepResult result = await RunAsync(step, new { v = "seen" }, Services(policy));

        Assert.False(result.HasError, result.Error?.Message);
        ExecRequest request = Assert.Single(policy.Requests);
        Assert.Equal("Run", request.StepName);
        Assert.Equal(Helper, request.Executable);
        Assert.Equal(["out", "seen"], request.Arguments);
        Assert.Equal(["EXECSTEP_P"], request.EnvironmentNames);
        Assert.False(request.InheritsFullEnvironment);
        Assert.DoesNotContain("SECRET", request.ToString());
    }

    [Fact]
    public async Task A_policy_can_confine_the_working_directory()
    {
        RecordingPolicy policy = new(_ => ExecStepPolicyDecision.AllowIn(_dir));
        ExecStep step = Step("cwd");
        step.WorkingDirectory = Path.GetTempPath();

        IStepResult result = await RunAsync(step, services: Services(policy));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(Path.TrimEndingDirectorySeparator(_dir), Path.TrimEndingDirectorySeparator((string)result.Value!), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task A_policy_that_throws_denies()
    {
        RecordingPolicy policy = new(_ => throw new InvalidOperationException("policy bug"));

        IStepResult result = await RunAsync(Step("out", "x"), services: Services(policy));

        Assert.Equal(ExecFailureKind.PolicyDenied, Failure(result).Kind);
    }
}
