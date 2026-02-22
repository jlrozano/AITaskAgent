// BRMS.StdRules, Version=1.0.1.0, Culture=neutral, PublicKeyToken=null
// BRMS.StdRules.JsScript.TypeExtensions
using System.Runtime.CompilerServices;

namespace BRMS.StdRules.Modules.Scripting;

public static class TypeExtensions
{
    public static bool IsAnonymousType(this Type type)
    {
        return type.Name.Contains("AnonymousType") && type.GetCustomAttributes(typeof(CompilerGeneratedAttribute), inherit: false).Length != 0;
    }
}
