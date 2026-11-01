# LLM Pricing Configuration

## Description

The price of each model is configured in `appsettings.json`, inside the `Models` list of each provider (`AITaskAgent:LlmProviders:Providers:<provider>:Models`). `LlmProvider.CalculateCost` uses them to compute the real cost of each call.

## Configuration Properties

Each entry in `Models` (`LlmModelSettings`) accepts:

- **`Name`**: model name; it is compared case-insensitively with the model of the call
- **`InputPricePerMillion`**: Price in USD per million input tokens
- **`OutputPricePerMillion`**: Price in USD per million output tokens
- **`CachedInputPricePerMillion`** (optional): price of input tokens served from the prompt cache (see 2.1)

## Configuration Example

```json
{
  "AITaskAgent": {
    "LlmProviders": {
      "Providers": {
        "Google": {
          "ServiceType": "Gemini",
          "BaseUrl": "https://generativelanguage.googleapis.com/v1beta/openai/",
          "ApiKey": "YOUR_API_KEY",
          "Models": [
            { "Name": "gemini-2.5-flash", "InputPricePerMillion": 0.075, "OutputPricePerMillion": 0.30 }
          ]
        },
        "NVidia": {
          "ServiceType": "OpenAI",
          "BaseUrl": "https://integrate.api.nvidia.com/v1",
          "ApiKey": "YOUR_API_KEY",
          "Models": [
            { "Name": "meta/llama-3.1-8b-instruct", "InputPricePerMillion": 0.20, "OutputPricePerMillion": 0.20 }
          ]
        }
      },
      "Roles": {
        "default": {
          "Provider": "Google",
          "Model": "gemini-2.5-flash",
          "Inference": { "Temperature": 0.5, "MaxTokens": 16000, "JsonSupport": true }
        },
        "Writers": {
          "Provider": "NVidia",
          "Model": "meta/llama-3.1-8b-instruct",
          "Inference": { "Temperature": 0.5, "MaxTokens": 16000, "JsonSupport": false }
        }
      },
      "DefaultRole": "default"
    }
  }
}
```

`ServiceType` selects the `ILlmService` registered under that name (if omitted, the provider key is used); the values in this example are illustrative and must match the `Name` of the services you register.

## How It Works

### 1. Token Information

The LLM automatically provides the following information in every response:

- **`PromptTokens`**: Number of input tokens (your prompt)
- **`CompletionTokens`**: Number of output tokens (LLM response)
- **`TokensUsed`**: Total tokens (input + output)

### 2. Cost Calculation

When the service does not report its own cost, `BaseLlmStep` calls `LlmProvider.CalculateCost`, looking first for the model that returned the response and, if that model has no price, for the requested model (Azure responds with the versioned model name). The formula, without cached tokens, is:

```csharp
var promptCost = promptTokens / 1_000_000m * InputPricePerMillion;
var completionCost = completionTokens / 1_000_000m * OutputPricePerMillion;
var totalCost = promptCost + completionCost;
```

### 2.1 Cached Input Tokens

Each model in `Providers:<p>:Models` also accepts the optional field **`CachedInputPricePerMillion`**: the price per million of the input tokens that the provider serves from its prompt cache (OpenAI/Azure report them in `usage.prompt_tokens_details.cached_tokens` and they arrive as `LlmResult.CachedPromptTokens`). Those tokens are included in `PromptTokens`: `LlmProvider.CalculateCost` charges the cached ones at `CachedInputPricePerMillion` and the rest at `InputPricePerMillion`. If the field is missing, cached tokens are charged at the normal input price (the cost is overestimated, never underestimated).

```json
{ "Name": "gpt-5.4", "InputPricePerMillion": 2.5, "OutputPricePerMillion": 15, "CachedInputPricePerMillion": 0.25 }
```

### 3. Models Without a Price

There are no predefined prices in the code. If the model is not in `Models`, or lacks `InputPricePerMillion` or `OutputPricePerMillion`, the cost is **`null`** (unknown), not $0.00: an unknown cost must not be read as a free call.

## Reference Prices (January 2026)

For guidance only: the code does not use them and they have not been checked against each provider's current rates.

### Google Gemini
- **gemini-2.5-flash**: $0.075 / $0.30 per million tokens
- **gemini-2.0-flash**: $0.10 / $0.40 per million tokens
- **gemini-1.5-pro**: $1.25 / $5.00 per million tokens

### OpenAI
- **gpt-4o**: $5.00 / $15.00 per million tokens
- **gpt-4o-mini**: $0.15 / $0.60 per million tokens
- **gpt-4-turbo**: $10.00 / $30.00 per million tokens

### Anthropic Claude
- **claude-3.5-sonnet**: $3.00 / $15.00 per million tokens
- **claude-3-haiku**: $0.25 / $1.25 per million tokens

### Meta Llama (via NVIDIA)
- **llama-3.1-8b-instruct**: $0.20 / $0.20 per million tokens
- **llama-3.1-70b-instruct**: $0.60 / $0.60 per million tokens

### DeepSeek
- **deepseek-chat**: $0.14 / $0.28 per million tokens
- **deepseek-coder**: $0.14 / $0.28 per million tokens

## Accessing the Cost in Code

The calculated cost is available in:

```csharp
// En LlmResult (lo rellena BaseLlmStep tras la llamada; un ILlmService invocado directamente no lo calcula)
LlmResult result = await llmService.InvokeAsync(request);
decimal? cost = result.CostUsd; // Coste en USD

// En el resultado de un paso LLM (ILlmStepResult / LlmStepResult)
IStepResult result = await step.ExecuteAsync(input, context, 1, null, CancellationToken.None);
if (result is ILlmStepResult llmResult)
{
    decimal? cost = llmResult.CostUsd;
    Console.WriteLine($"Coste de la llamada: ${cost:F6}");
}
```

## Logging

The cost travels in `LlmResponseEvent.CostUsd`; `BaseLlmStep` stores it in `context.Metadata["CostUsd"]` and adds it to the `Metrics.LlmCost` metric.

## Important Notes

1. **Prices are optional**: If you do not configure them, `CostUsd` stays `null`
2. **Update prices regularly**: LLM providers change their prices frequently
3. **Check with your provider**: Prices may vary depending on your plan or region
4. **Prices in USD**: All prices are handled in US dollars
