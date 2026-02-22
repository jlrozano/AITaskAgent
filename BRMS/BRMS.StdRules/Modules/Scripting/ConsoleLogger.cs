// BRMS.StdRules, Version=1.0.1.0, Culture=neutral, PublicKeyToken=null
// BRMS.StdRules.JsScript.ConsoleLogger
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BRMS.StdRules.Modules.Scripting;

public class ConsoleLogger(IList<ConsoleMessage> messages)
{
    private readonly IList<ConsoleMessage> _messages = messages ?? throw new ArgumentNullException(nameof(messages));

    public void log(params object[] args)
    {
        string message = FormatArguments(args);
        _messages.Add(new ConsoleMessage
        {
            Level = LogLevel.Information,
            Message = message
        });
        Console.WriteLine("[log] " + message);
    }

    public void warn(params object[] args)
    {
        string message = FormatArguments(args);
        _messages.Add(new ConsoleMessage
        {
            Level = LogLevel.Warning,
            Message = message
        });
        Console.WriteLine("[warn] " + message);
    }

    public void error(params object[] args)
    {
        string message = FormatArguments(args);
        _messages.Add(new ConsoleMessage
        {
            Level = LogLevel.Error,
            Message = message
        });
        Console.WriteLine("[error] " + message);
    }

    private static string FormatArguments(object[] args)
    {
        string[] formattedArgs = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            object arg = args[i];
            if (arg == null)
            {
                formattedArgs[i] = "null";
            }
            else if (arg is string)
            {
                formattedArgs[i] = $"\"{arg}\"";
            }
            else if (arg is int or double or bool or decimal)
            {
                formattedArgs[i] = arg.ToString() ?? "null";
            }
            else if (arg is DynamicObjectWrapper wrapper)
            {
                try
                {
                    formattedArgs[i] = JsonConvert.SerializeObject(wrapper.GetJObject(), Formatting.Indented);
                }
                catch
                {
                    formattedArgs[i] = wrapper.ToString();
                }
            }
            else
            {
                try
                {
                    formattedArgs[i] = JsonConvert.SerializeObject(arg, Formatting.Indented);
                }
                catch
                {
                    formattedArgs[i] = arg.ToString() ?? "null";
                }
            }
        }
        return string.Join("\r\n", formattedArgs);
    }
}
