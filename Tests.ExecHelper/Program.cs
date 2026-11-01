using System.Diagnostics;
using System.Text;

// Harmless helper process for the ExecStep tests. Driven by its arguments; it touches nothing outside its own output
// (and the pid file it is told to write).
//   args            one line per argument, as "<value>"
//   stdin           copies stdin to stdout
//   exit N          exits with code N
//   err TEXT [N]    writes TEXT to stderr, exits with N (default 0)
//   out TEXT        writes TEXT (no newline) to stdout
//   sleep MS        sleeps
//   spawn PIDFILE   starts a grandchild that sleeps for ten minutes, writes its pid to PIDFILE, then sleeps too
//   spawn-exit PIDFILE  same, but this process exits at once (the grandchild is orphaned)
//   big N           writes N 'x' characters to stdout
//   bigerr N        writes N 'x' characters to stderr
//   env NAME        prints the value of the variable, or "<unset>"
//   envnames        prints the names of all variables, sorted, one per line
//   cwd             prints the working directory
//   echo-batch      (unused marker)
Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

string command = args.Length > 0 ? args[0] : "";
switch (command)
{
    case "args":
        foreach (string a in args.Skip(1))
        {
            Console.Out.Write($"<{a}>\n");
        }
        return 0;

    case "stdin":
        Console.Out.Write(Console.In.ReadToEnd());
        return 0;

    case "exit":
        return int.Parse(args[1]);

    case "err":
        Console.Error.Write(args[1]);
        return args.Length > 2 ? int.Parse(args[2]) : 0;

    case "out":
        Console.Out.Write(args[1]);
        return 0;

    case "sleep":
        Thread.Sleep(int.Parse(args[1]));
        return 0;

    case "grandchild":
        Thread.Sleep(600_000);
        return 0;

    case "spawn":
    case "spawn-exit":
    {
        ProcessStartInfo psi = new(Environment.ProcessPath!) { UseShellExecute = false };
        psi.ArgumentList.Add("grandchild");
        using Process child = Process.Start(psi)!;
        File.WriteAllText(args[1], child.Id.ToString());
        if (command == "spawn")
        {
            Thread.Sleep(600_000);
        }

        return 0;
    }

    case "big":
        Console.Out.Write(new string('x', int.Parse(args[1])));
        return 0;

    case "bigerr":
        Console.Error.Write(new string('x', int.Parse(args[1])));
        return 0;

    case "env":
        Console.Out.Write(Environment.GetEnvironmentVariable(args[1]) ?? "<unset>");
        return 0;

    case "envnames":
        foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.OrdinalIgnoreCase))
        {
            Console.Out.Write(name + "\n");
        }
        return 0;

    case "cwd":
        Console.Out.Write(Directory.GetCurrentDirectory());
        return 0;

    default:
        Console.Error.Write("unknown command");
        return 99;
}
