using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;

namespace AITaskAgent.LLM.Results
{
    public class LlmIntentionResult(IStep step) : LlmStepResult<Intention>(step), IRouterResult
    {
        private readonly IStep _step = step;
        string IRouterResult.SelectedOption => Value?.SelectedOption ?? string.Empty;

        public override Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
        {
            (bool isValid, string? error) = base.ValidateAsync(context).Result;
            if (!isValid)
            {
                return Task.FromResult((false, error));
            }

            if (Value is Intention intencion)
            {
                if (_step is IRouterOptions options && !options.Options.Any(o => intencion.SelectedOption.Equals(o, StringComparison.OrdinalIgnoreCase)))
                {
                    string validOptions = string.Join(", ", options.Options);
                    return Task.FromResult((false, (string?)$"Selected option '{intencion.SelectedOption}' is not valid. Valid options are: {validOptions}."));
                }
                return Task.FromResult((true, (string?)null));
            }
            else
            {
                return Task.FromResult((false, (string?)$"Expected an Intention result but got {Value?.GetType().Name ?? "null"}."));
            }
        }
    }

}
