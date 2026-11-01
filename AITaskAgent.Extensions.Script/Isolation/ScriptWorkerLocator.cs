using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AITaskAgent.Script.Isolation;

/// <summary>Finds the worker next to the host application and builds the command that starts it.</summary>
internal static class ScriptWorkerLocator
{
    /// <summary>Name of the worker assembly (the file the host application must carry).</summary>
    public const string WorkerAssembly = "AITaskAgent.Extensions.Script.Worker";

    /// <summary>Environment variable that overrides the location of <c>AITaskAgent.Extensions.Script.Worker.dll</c>.</summary>
    public const string WorkerPathVariable = "AITASKAGENT_SCRIPT_WORKER";

    public static ProcessStartInfo CreateStartInfo()
    {
        string workerDll = ResolveWorkerDll();

        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        ProcessStartInfo startInfo = new(ResolveDotnet())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(workerDll)!,
        };
        startInfo.ArgumentList.Add(workerDll);
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static string ResolveWorkerDll()
    {
        string? overridden = Environment.GetEnvironmentVariable(WorkerPathVariable);
        string path = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(AppContext.BaseDirectory, WorkerAssembly + ".dll")
            : overridden;

        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"'{path}' does not exist. The application that runs ScriptStep must ship {WorkerAssembly}.dll " +
                $"(and its .runtimeconfig.json and dependencies) next to its own binaries, or point the " +
                $"{WorkerPathVariable} environment variable at it.");
    }

    /// <summary>
    /// The <c>dotnet</c> that runs this process, found from the runtime's own directory
    /// (<c>.../dotnet/shared/Microsoft.NETCore.App/x.y.z/</c>): the worker then runs on the same installation.
    /// Falls back to <c>dotnet</c> on the PATH.
    /// </summary>
    private static string ResolveDotnet()
    {
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        string beside = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", fileName));
        return File.Exists(beside) ? beside : "dotnet";
    }
}
