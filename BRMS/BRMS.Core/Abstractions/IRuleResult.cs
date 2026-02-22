using BRMS.Core.Models;

namespace BRMS.Core.Abstractions;

public interface IRuleResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    BRMSExecutionContext Context { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    Exception? Exception { get; set; }
    string? Message { get; }
    string RuleId { get; }
    string PropertyPath { get; }
    ResultLevelEnum ResultLevel { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    IRule Rule { get; }
    bool Success { get; }
}
