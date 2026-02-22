// BRMS.StdRules, Version=1.0.1.0, Culture=neutral, PublicKeyToken=null
// BRMS.StdRules.JsScript.ScriptProgramBuilder
using Microsoft.ClearScript.V8;

namespace BRMS.StdRules.Modules.Scripting;

public static class ScriptProgramBuilder
{
    private static readonly V8Runtime _runtime = new();

    private static readonly V8Script? _compiledScript;

    private static readonly Dictionary<string, Func<object>> _hostedObjects = [];

    public static StdRulesConfiguration EngineConfiguration { get; private set; } = new StdRulesConfiguration();

    //public static void Configure(StdRulesConfiguration engineConfig)
    //{
    //    var code = new StringBuilder();
    //    EngineConfiguration = engineConfig;
    //    if (!string.IsNullOrEmpty(engineConfig.LibPath) && Directory.Exists(engineConfig.LibPath))
    //    {
    //        string[] files = Directory.GetFiles(engineConfig.LibPath, "*.js", SearchOption.AllDirectories);
    //        foreach (string file in files)
    //        {
    //            _ = code.AppendLine(File.ReadAllText(file));
    //        }
    //        if (code.Length > 0)
    //        {
    //            _compiledScript = _runtime.CreateScriptEngine().Compile(code.ToString());
    //        }
    //    }
    //}


    public static void AddHostedObject(string name, Func<object> factory)
    {
        _hostedObjects[name] = factory;
    }

    public static V8ScriptEngine CreateEngine()
    {

        V8ScriptEngine engine = _runtime.CreateScriptEngine();
        foreach (KeyValuePair<string, Func<object>> obj in _hostedObjects)
        {
            engine.AddHostObject(obj.Key, obj.Value);
        }
        if (_compiledScript != null)
        {
            engine.Execute(_compiledScript);
        }
        return engine;
    }
}
