namespace AITaskAgent.Designer.Core;

using AITaskAgent.Core.Abstractions;
using Newtonsoft.Json.Linq;

/// <summary>
/// Internal registration record for step factories.
/// Equivalent to BRMS _ruleFactories value tuple.
/// </summary>
internal record StepRegistration(
Type StepType,
Func<JObject?, IServiceProvider?, IStep> Factory
);
