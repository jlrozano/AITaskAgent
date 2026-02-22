using BRMS.Core.Abstractions;
using BRMS.Core.Models;

namespace BRMS.StdRules.Modules.Scripting;

public class ScriptNormalizerResult(INormalizer rule,
        BRMSExecutionContext context,
        IEnumerable<ConsoleMessage> console,
        string? errorMessage = null,

        bool? hasChanges = null) :
    NormalizerResult(rule, context, errorMessage, hasChanges)
{
    public IEnumerable<ConsoleMessage> Console => console;
}
