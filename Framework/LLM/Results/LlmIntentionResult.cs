using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;

namespace AITaskAgent.LLM.Results
{
    public class LlmIntentionResult(IStep step) : LlmStepResult<Intention>(step), IRouterResult
    {
        private readonly IStep _step = step;
        string IRouterResult.SelectedOption => Value?.SelectedOption ?? string.Empty;

        public override async Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
        {
            (bool isValid, string? error) = await base.ValidateAsync(context);
            if (!isValid)
            {
                return (false, error);
            }

            if (Value is Intention intencion)
            {
                if (_step is IRouterOptions options && !options.Options.Any(o => intencion.SelectedOption.Equals(o, StringComparison.OrdinalIgnoreCase)))
                {
                    string validOptions = string.Join(", ", options.Options);
                    return (false, $"Selected option '{intencion.SelectedOption}' is not valid. Valid options are: {validOptions}.");
                }
                return (true, null);
            }
            else
            {
                return (false, $"Expected an Intention result but got {Value?.GetType().Name ?? "null"}.");
            }
        }
    }

}
