
using BRMS.Core.Models;

namespace BRMS.StdRules.Http.Models;

internal class NormalizedRequest
{
    public required BRMSExecutionContext Context { get; set; }
    public required string PropertyPath { get; set; }
    public bool MustNotifyChange { get; set; }
}
