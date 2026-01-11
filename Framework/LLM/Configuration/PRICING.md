# Configuración de Precios de LLM

## Descripción

A partir de ahora, puedes configurar los precios de cada modelo LLM directamente en el archivo `appsettings.json`. Esto permite calcular el coste real de las llamadas al LLM basándose en los precios específicos de cada proveedor.

## Propiedades de Configuración

Se han añadido dos nuevas propiedades opcionales a cada profile de LLM:

- **`InputTokenPricePerMillion`**: Precio en USD por millón de tokens de entrada
- **`OutputTokenPricePerMillion`**: Precio en USD por millón de tokens de salida

## Ejemplo de Configuración

```json
{
  "AITaskAgent": {
    "LlmProviders": {
      "Providers": {
        "IntentionAnalyzer": {
          "Provider": "Google",
          "BaseUrl": "https://generativelanguage.googleapis.com/v1beta/openai/",
          "ApiKey": "YOUR_API_KEY",
          "Model": "gemini-2.5-flash",
          "Temperature": 0.5,
          "MaxTokens": 16000,
          "JsonCapability": "JsonSchema",
          "InputTokenPricePerMillion": 0.075,
          "OutputTokenPricePerMillion": 0.30
        },
        "Writers": {
          "Provider": "NVidea",
          "BaseUrl": "https://integrate.api.nvidia.com/v1",
          "ApiKey": "YOUR_API_KEY",
          "Model": "meta/llama-3.1-8b-instruct",
          "Temperature": 0.5,
          "MaxTokens": 16000,
          "JsonCapability": "None",
          "InputTokenPricePerMillion": 0.20,
          "OutputTokenPricePerMillion": 0.20
        }
      },
      "DefaultProvider": "IntentionAnalyzer"
    }
  }
}
```

## Cómo Funciona

### 1. Información de Tokens

El LLM proporciona automáticamente la siguiente información en cada respuesta:

- **`PromptTokens`**: Número de tokens de entrada (tu prompt)
- **`CompletionTokens`**: Número de tokens de salida (respuesta del LLM)
- **`TokensUsed`**: Total de tokens (entrada + salida)

### 2. Cálculo de Coste

El coste se calcula automáticamente usando la siguiente fórmula:

```csharp
var promptCost = promptTokens / 1_000_000m * InputTokenPricePerMillion;
var completionCost = completionTokens / 1_000_000m * OutputTokenPricePerMillion;
var totalCost = promptCost + completionCost;
```

### 3. Fallback a Precios Hardcoded

Si **NO** configuras los precios en el profile, el sistema usará precios predefinidos para modelos conocidos de OpenAI:

| Modelo | Input ($/1K tokens) | Output ($/1K tokens) |
|--------|---------------------|----------------------|
| gpt-4 | $0.03 | $0.06 |
| gpt-4-turbo | $0.01 | $0.03 |
| gpt-3.5-turbo | $0.0005 | $0.0015 |
| gpt-4o | $0.005 | $0.015 |
| gpt-4o-mini | $0.00015 | $0.0006 |

Si el modelo no está en la tabla y no hay precios configurados, el coste será **$0.00**.

## Precios de Referencia (Enero 2026)

### Google Gemini
- **gemini-2.5-flash**: $0.075 / $0.30 por millón de tokens
- **gemini-2.0-flash**: $0.10 / $0.40 por millón de tokens
- **gemini-1.5-pro**: $1.25 / $5.00 por millón de tokens

### OpenAI
- **gpt-4o**: $5.00 / $15.00 por millón de tokens
- **gpt-4o-mini**: $0.15 / $0.60 por millón de tokens
- **gpt-4-turbo**: $10.00 / $30.00 por millón de tokens

### Anthropic Claude
- **claude-3.5-sonnet**: $3.00 / $15.00 por millón de tokens
- **claude-3-haiku**: $0.25 / $1.25 por millón de tokens

### Meta Llama (via NVIDIA)
- **llama-3.1-8b-instruct**: $0.20 / $0.20 por millón de tokens
- **llama-3.1-70b-instruct**: $0.60 / $0.60 por millón de tokens

### DeepSeek
- **deepseek-chat**: $0.14 / $0.28 por millón de tokens
- **deepseek-coder**: $0.14 / $0.28 por millón de tokens

## Acceso al Coste en el Código

El coste calculado está disponible en:

```csharp
// En LlmResponse
LlmResponse response = await llmService.InvokeAsync(request);
decimal? cost = response.CostUsd; // Coste en USD

// En LlmStepResult
var result = await step.ExecuteAsync(input, context);
if (result is LlmStepResult llmResult)
{
    decimal? cost = llmResult.CostUsd;
    Console.WriteLine($"Coste de la llamada: ${cost:F6}");
}
```

## Logging

El coste también se registra automáticamente en los logs:

```
[Information] OpenAI response: 1234 tokens, $0.0012, finish: stop
```

Y en las métricas de telemetría bajo la clave `CostUsd` en los metadatos del contexto.

## Notas Importantes

1. **Los precios son opcionales**: Si no los configuras, el sistema intentará usar precios predefinidos o devolverá $0.00
2. **Actualiza los precios regularmente**: Los proveedores de LLM cambian sus precios frecuentemente
3. **Verifica con tu proveedor**: Los precios pueden variar según tu plan o región
4. **Precios en USD**: Todos los precios se manejan en dólares estadounidenses
