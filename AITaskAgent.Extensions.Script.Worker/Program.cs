using AITaskAgent.Script.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Text;

namespace AITaskAgent.Script.Worker;

/// <summary>
/// Entry point of the script worker: serves one run. It compiles the run's JavaScript once (<c>load</c>), runs the calls
/// the host sends (<c>run</c>), one at a time, and exits when the host closes its stdin. The host starts it, caps its
/// memory, and kills it on timeout, cancellation or a broken limit; see <c>AITaskAgent.Extensions.Script</c>.
/// </summary>
internal static class Program
{
    private const int NoRunExitCode = 2;
    private const int HostGoneExitCode = 3;

    private static int Main(string[] args)
    {
        StartParentWatchdog(args);

        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        using StreamReader input = new(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        using StreamWriter output = new(Console.OpenStandardOutput(), utf8, bufferSize: 1 << 16) { AutoFlush = true, NewLine = "\n" };
        WorkerChannel channel = new(input, output);

        channel.Send(Msg.Ready, ("proto", ScriptProtocol.Version));

        ScriptRunner.LoadedBundle? loaded = null;
        try
        {
            while (channel.Read() is { } message)
            {
                string? kind = message.Value<string>(Msg.Kind);
                if (kind is not (Msg.Load or Msg.Run))
                {
                    return NoRunExitCode;
                }

                int proto = message.Value<int?>("proto") ?? 0;
                if (proto != ScriptProtocol.Version)
                {
                    string mismatch = $"the host speaks script protocol {proto}, this worker speaks {ScriptProtocol.Version}.";
                    if (kind == Msg.Load)
                    {
                        channel.Send(Msg.Loaded, ("ok", false), ("errors", null), ("error", mismatch));
                    }
                    else
                    {
                        channel.Send(Msg.Result, ("ok", false), ("json", null), ("error", mismatch), ("recycle", true));
                    }

                    return 0;
                }

                if (kind == Msg.Load)
                {
                    loaded?.Dispose();
                    loaded = null;
                    (ScriptRunner.LoadedBundle? bundle, JArray report, string? error, JObject? exports) = ScriptRunner.Load(
                        message.Value<string>("pieces") ?? "[]",
                        message.Value<int?>("heapMb") ?? ScriptLimits.DefaultHeapMb,
                        message.Value<bool?>("check") ?? false,
                        message.Value<string>("exports"));
                    loaded = bundle;
                    channel.Send(
                        Msg.Loaded,
                        ("ok", error == null && report.All(e => e["error"] == null)),
                        ("errors", report.ToString(Formatting.None)),
                        ("error", error),
                        ("exports", exports?.ToString(Formatting.None)));
                    continue;
                }

                RunOutcome outcome = loaded == null
                    ? new RunOutcome(null, "no scripts are loaded in this worker.", Recycle: true)
                    : loaded.Run(
                        new RunRequest(
                            message.Value<string>("name") ?? "script",
                            message.Value<string>("fn") ?? "",
                            message.Value<string>("input") ?? "null",
                            message.Value<int>("timeoutMs"),
                            message.Value<int>("heapMb"),
                            message.Value<int>("stackKb"),
                            message.Value<string>("uses") ?? "[]",
                            message.Value<string>("allow") ?? "[]",
                            message.Value<int?>("maxCalls") ?? ScriptLimits.DefaultMaxCalls,
                            message.Value<int?>("maxCallBytes") ?? ScriptLimits.DefaultMaxCallBytes,
                            message.Value<string>("call") ?? ScriptRunner.StepCall,
                            message.Value<string>("export")),
                        channel);

                channel.Send(
                    Msg.Result,
                    ("ok", outcome.Error == null),
                    ("json", outcome.Json),
                    ("error", outcome.Error),
                    ("nextSteps", outcome.NextStepsJson),
                    ("recycle", outcome.Recycle));
            }
        }
        finally
        {
            loaded?.Dispose();
        }

        return 0;
    }

    /// <summary>
    /// A worker whose host died must not outlive it (on Windows the job object also guarantees it; this covers
    /// every platform): watch the parent process and exit when it is gone.
    /// </summary>
    private static void StartParentWatchdog(string[] args)
    {
        int index = Array.IndexOf(args, "--parent");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int parentId))
        {
            return;
        }

        Thread watchdog = new(() =>
        {
            try
            {
                using Process parent = Process.GetProcessById(parentId);
                parent.WaitForExit();
            }
            catch (ArgumentException)
            {
                // The parent is already gone.
            }

            Environment.Exit(HostGoneExitCode);
        })
        { IsBackground = true };
        watchdog.Start();
    }
}
