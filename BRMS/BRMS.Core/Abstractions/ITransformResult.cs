using BRMS.Core.Models;

namespace BRMS.Core.Abstractions;

public interface ITransformResult : IRuleResult
{
    BRMSExecutionContext? OutputContext { get; }

}
